using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;

namespace Uplink2.Runtime;

public partial class WorldRuntime
{
    /// <summary>Parses IPv4 CIDR plan (/8,/16,/24 only) into allocator-friendly structure.</summary>
    private static AddressPlan ParseAddressPlan(string rawAddressPlan, string context)
    {
        if (string.IsNullOrWhiteSpace(rawAddressPlan))
        {
            throw new InvalidDataException($"{context} is required.");
        }

        var segments = rawAddressPlan.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length != 2)
        {
            throw new InvalidDataException($"{context} must be in CIDR form (example: 10.0.0.0/24).");
        }

        if (!IPAddress.TryParse(segments[0], out var parsedIp) || parsedIp.AddressFamily != AddressFamily.InterNetwork)
        {
            throw new InvalidDataException($"{context} must be a valid IPv4 network address.");
        }

        if (!int.TryParse(segments[1], out var prefixLength) || (prefixLength != 8 && prefixLength != 16 && prefixLength != 24))
        {
            throw new InvalidDataException($"{context} prefix must be one of: 8, 16, 24.");
        }

        var networkAddress = ConvertIpToUInt32(parsedIp);
        var hostMask = prefixLength switch
        {
            8 => 0x00FFFFFFu,
            16 => 0x0000FFFFu,
            24 => 0x000000FFu,
            _ => 0u,
        };

        if ((networkAddress & hostMask) != 0)
        {
            throw new InvalidDataException($"{context} must use network boundary address for /{prefixLength}.");
        }

        return new AddressPlan(networkAddress, prefixLength, rawAddressPlan.Trim());
    }

    /// <summary>Converts IPv4 address to uint32.</summary>
    private static uint ConvertIpToUInt32(IPAddress ipAddress)
    {
        var bytes = ipAddress.GetAddressBytes();
        return ((uint)bytes[0] << 24) |
               ((uint)bytes[1] << 16) |
               ((uint)bytes[2] << 8) |
               bytes[3];
    }

    /// <summary>Converts uint32 IPv4 representation into dotted-quad string.</summary>
    private static string ConvertUInt32ToIp(uint value)
    {
        return $"{(value >> 24) & 255}.{(value >> 16) & 255}.{(value >> 8) & 255}.{value & 255}";
    }

    private readonly struct AddressPlan
    {
        public AddressPlan(uint networkAddress, int prefixLength, string rawText)
        {
            NetworkAddress = networkAddress;
            PrefixLength = prefixLength;
            RawText = rawText;
            HostBytes = (32 - prefixLength) / 8;
            HostMask = prefixLength switch
            {
                8 => 0x00FFFFFFu,
                16 => 0x0000FFFFu,
                24 => 0x000000FFu,
                _ => 0u,
            };
            MaxHostValue = HostMask;
        }

        public uint NetworkAddress { get; }

        public int PrefixLength { get; }

        public int HostBytes { get; }

        public uint HostMask { get; }

        public uint MaxHostValue { get; }

        public string RawText { get; }

        public string ComposeIp(uint hostValue)
        {
            var value = (NetworkAddress & ~HostMask) | (hostValue & HostMask);
            return ConvertUInt32ToIp(value);
        }
    }

    private sealed class AddressAllocator
    {
        private readonly AddressPlan addressPlan;
        private readonly HashSet<string> globalUsedIps;
        private readonly HashSet<uint> assignedHostValues = new();
        private uint nextHostCandidate;

        public AddressAllocator(AddressPlan addressPlan, HashSet<string> globalUsedIps)
        {
            this.addressPlan = addressPlan;
            this.globalUsedIps = globalUsedIps;

            nextHostCandidate = Math.Min(addressPlan.MaxHostValue, DefaultHostStart);
            if (nextHostCandidate < 1)
            {
                nextHostCandidate = 1;
            }
        }

        public string AllocateFixed(IReadOnlyList<int> hostSuffix, string context)
        {
            if (hostSuffix.Count != addressPlan.HostBytes)
            {
                throw new InvalidDataException(
                    $"{context} hostSuffix length must be {addressPlan.HostBytes} for '{addressPlan.RawText}'.");
            }

            var hostValue = 0u;
            foreach (var suffixPart in hostSuffix)
            {
                if (suffixPart < 0 || suffixPart > 255)
                {
                    throw new InvalidDataException($"{context} hostSuffix values must be between 0 and 255.");
                }

                hostValue = (hostValue << 8) | (uint)suffixPart;
            }

            return ReserveHost(hostValue, context, requireNonReserved: true);
        }

        public string AllocateNext(string context)
        {
            if (TryAllocateInRange(nextHostCandidate, addressPlan.MaxHostValue, out var ip))
            {
                return ip;
            }

            if (nextHostCandidate > 1 && TryAllocateInRange(1, nextHostCandidate - 1, out ip))
            {
                return ip;
            }

            throw new InvalidDataException(
                $"No available addresses left in '{addressPlan.RawText}' while allocating '{context}'.");
        }

        private bool TryAllocateInRange(uint startHostValue, uint endHostValue, out string ip)
        {
            for (var hostValue = startHostValue; hostValue <= endHostValue; hostValue++)
            {
                if (IsReservedHost(hostValue) || assignedHostValues.Contains(hostValue))
                {
                    continue;
                }

                var candidateIp = addressPlan.ComposeIp(hostValue);
                if (globalUsedIps.Contains(candidateIp))
                {
                    continue;
                }

                assignedHostValues.Add(hostValue);
                globalUsedIps.Add(candidateIp);
                nextHostCandidate = hostValue < uint.MaxValue ? hostValue + 1 : hostValue;
                ip = candidateIp;
                return true;
            }

            ip = string.Empty;
            return false;
        }

        private string ReserveHost(uint hostValue, string context, bool requireNonReserved)
        {
            if (hostValue > addressPlan.MaxHostValue)
            {
                throw new InvalidDataException($"{context} hostSuffix is out of range for '{addressPlan.RawText}'.");
            }

            if (requireNonReserved && IsReservedHost(hostValue))
            {
                throw new InvalidDataException(
                    $"{context} hostSuffix resolves to reserved network/broadcast address for '{addressPlan.RawText}'.");
            }

            if (!assignedHostValues.Add(hostValue))
            {
                throw new InvalidDataException($"{context} produced duplicate host address in '{addressPlan.RawText}'.");
            }

            var ip = addressPlan.ComposeIp(hostValue);
            if (!globalUsedIps.Add(ip))
            {
                assignedHostValues.Remove(hostValue);
                throw new InvalidDataException($"{context} produced duplicate IP '{ip}' in world scope.");
            }

            if (hostValue >= nextHostCandidate && hostValue < uint.MaxValue)
            {
                nextHostCandidate = hostValue + 1;
            }

            return ip;
        }

        private bool IsReservedHost(uint hostValue)
        {
            return hostValue == 0 || hostValue == addressPlan.MaxHostValue;
        }
    }

    private sealed class SpawnInterfaceSeed
    {
        public string NetId { get; init; } = string.Empty;

        public string Ip { get; init; } = string.Empty;

        public bool InitiallyExposed { get; init; }
    }
}
