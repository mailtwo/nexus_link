using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Uplink2.Blueprint;
using Uplink2.Vfs;

namespace Uplink2.Runtime;

public partial class WorldRuntime
{
    /// <summary>Maps blueprint server role to runtime role enum.</summary>
    private static ServerRole ConvertRole(BlueprintServerRole role)
    {
        return role switch
        {
            BlueprintServerRole.Terminal => ServerRole.Terminal,
            BlueprintServerRole.OtpGenerator => ServerRole.OtpGenerator,
            BlueprintServerRole.Mainframe => ServerRole.Mainframe,
            BlueprintServerRole.Tracer => ServerRole.Tracer,
            BlueprintServerRole.Gateway => ServerRole.Gateway,
            _ => throw new InvalidDataException($"Unsupported server role: {role}."),
        };
    }

    /// <summary>Maps blueprint server reason to runtime reason enum.</summary>
    private static ServerReason ConvertReason(BlueprintServerReason reason)
    {
        return reason switch
        {
            BlueprintServerReason.Ok => ServerReason.Ok,
            BlueprintServerReason.Reboot => ServerReason.Reboot,
            BlueprintServerReason.Disabled => ServerReason.Disabled,
            BlueprintServerReason.Crashed => ServerReason.Crashed,
            _ => throw new InvalidDataException($"Unsupported server reason: {reason}."),
        };
    }

    /// <summary>Maps blueprint auth mode to runtime auth mode enum.</summary>
    private static AuthMode ConvertAuthMode(BlueprintAuthMode mode)
    {
        return mode switch
        {
            BlueprintAuthMode.None => AuthMode.None,
            BlueprintAuthMode.Static => AuthMode.Static,
            BlueprintAuthMode.Otp => AuthMode.Otp,
            BlueprintAuthMode.Other => AuthMode.Other,
            _ => throw new InvalidDataException($"Unsupported auth mode: {mode}."),
        };
    }

    /// <summary>Maps blueprint port config to runtime port config.</summary>
    private static PortConfig ConvertPortConfig(PortBlueprint port)
    {
        return new PortConfig
        {
            PortType = port.PortType switch
            {
                BlueprintPortType.Ssh => PortType.Ssh,
                BlueprintPortType.Ftp => PortType.Ftp,
                BlueprintPortType.Http => PortType.Http,
                BlueprintPortType.Sql => PortType.Sql,
                _ => throw new InvalidDataException($"Unsupported port type: {port.PortType}."),
            },
            ServiceId = port.ServiceId ?? string.Empty,
            Exposure = port.Exposure switch
            {
                BlueprintPortExposure.Public => PortExposure.Public,
                BlueprintPortExposure.Lan => PortExposure.Lan,
                BlueprintPortExposure.Localhost => PortExposure.Localhost,
                _ => throw new InvalidDataException($"Unsupported port exposure: {port.Exposure}."),
            },
        };
    }

    /// <summary>Maps blueprint daemon type to runtime daemon type enum.</summary>
    private static DaemonType ConvertDaemonType(BlueprintDaemonType daemonType)
    {
        return daemonType switch
        {
            BlueprintDaemonType.Otp => DaemonType.Otp,
            BlueprintDaemonType.Firewall => DaemonType.Firewall,
            BlueprintDaemonType.ConnectionRateLimiter => DaemonType.ConnectionRateLimiter,
            _ => throw new InvalidDataException($"Unsupported daemon type: {daemonType}."),
        };
    }

    /// <summary>Converts blueprint daemon to runtime daemon structure with cloned args.</summary>
    private static DaemonStruct ConvertDaemonStruct(BlueprintDaemonType daemonType, DaemonBlueprint daemonBlueprint)
    {
        var daemon = new DaemonStruct
        {
            DaemonType = ConvertDaemonType(daemonType),
        };

        foreach (var argPair in daemonBlueprint.DaemonArgs)
        {
            daemon.DaemonArgs[argPair.Key] = CloneValue(argPair.Value);
        }

        return daemon;
    }

    /// <summary>Converts blueprint file kind to runtime file kind enum.</summary>
    private static VfsFileKind ConvertFileKind(BlueprintFileKind fileKind)
    {
        return fileKind switch
        {
            BlueprintFileKind.Text => VfsFileKind.Text,
            BlueprintFileKind.Binary => VfsFileKind.Binary,
            BlueprintFileKind.Image => VfsFileKind.Image,
            BlueprintFileKind.Executable => VfsFileKind.Executable,
            _ => throw new InvalidDataException($"Unsupported file kind: {fileKind}."),
        };
    }

    /// <summary>Resolves final server display name using spawn override when present.</summary>
    private static string ResolveServerName(ServerSpecBlueprint spec, ServerSpawnBlueprint spawn)
    {
        var name = string.IsNullOrWhiteSpace(spawn.HostnameOverride) ? spec.Hostname : spawn.HostnameOverride;
        return string.IsNullOrWhiteSpace(name) ? spawn.NodeId : name;
    }

    /// <summary>Resolves AUTO userId policy into deterministic runtime user id.</summary>
    private static string ResolveUserId(string source, string nodeId, string userKey)
    {
        if (!TryReadAutoPolicy(source, out var policy))
        {
            return source ?? string.Empty;
        }

        if (string.Equals(policy, "user", StringComparison.OrdinalIgnoreCase))
        {
            return userKey;
        }

        return $"{userKey}_{CreateDeterministicToken($"{nodeId}:{userKey}:userid:{policy}", 4, LowercaseAlphaNumericAlphabet)}";
    }

    /// <summary>Resolves AUTO password policy into deterministic runtime password.</summary>
    private static string ResolvePassword(string source, string nodeId, string userKey)
    {
        if (!TryReadAutoPolicy(source, out var policy))
        {
            return source ?? string.Empty;
        }

        if (string.Equals(policy, "dictionary", StringComparison.OrdinalIgnoreCase))
        {
            if (dictionaryPasswordPool.Length == 0)
            {
                throw new InvalidOperationException(
                    "Dictionary password pool is empty. Ensure LoadDictionaryPasswordPool runs before world initialization.");
            }

            var index = CreateDeterministicIndex($"{nodeId}:{userKey}:dictionary", dictionaryPasswordPool.Length);
            return dictionaryPasswordPool[index];
        }

        if (TryReadBase64LengthPolicy(policy, out var length))
        {
            return CreateDeterministicToken($"{nodeId}:{userKey}:base64:{policy}", length, Base64Alphabet);
        }

        return CreateDeterministicToken($"{nodeId}:{userKey}:fallback", 12, Base64Alphabet);
    }

    /// <summary>Parses AUTO:&lt;policy&gt; token.</summary>
    private static bool TryReadAutoPolicy(string source, out string policy)
    {
        if (!string.IsNullOrWhiteSpace(source) && source.StartsWith("AUTO:", StringComparison.OrdinalIgnoreCase))
        {
            policy = source[5..].Trim();
            return !string.IsNullOrWhiteSpace(policy);
        }

        policy = string.Empty;
        return false;
    }

    /// <summary>Parses cN_base64 policy form and extracts requested length.</summary>
    private static bool TryReadBase64LengthPolicy(string policy, out int length)
    {
        if (policy.StartsWith("c", StringComparison.OrdinalIgnoreCase) &&
            policy.EndsWith("_base64", StringComparison.OrdinalIgnoreCase))
        {
            var numericPart = policy[1..^7];
            if (int.TryParse(numericPart, out length) && length > 0)
            {
                return true;
            }
        }

        length = 0;
        return false;
    }

    /// <summary>Creates deterministic token text from arbitrary seed using fixed alphabet.</summary>
    private static string CreateDeterministicToken(string seed, int length, string alphabet)
    {
        if (length <= 0)
        {
            return string.Empty;
        }

        var chars = new char[length];
        var state = SHA256.HashData(Encoding.UTF8.GetBytes(seed));
        var stateOffset = 0;
        for (var index = 0; index < chars.Length; index++)
        {
            if (stateOffset >= state.Length)
            {
                state = SHA256.HashData(state);
                stateOffset = 0;
            }

            chars[index] = alphabet[state[stateOffset] % alphabet.Length];
            stateOffset++;
        }

        return new string(chars);
    }

    /// <summary>Creates deterministic [0, maxExclusive) integer from seed.</summary>
    private static int CreateDeterministicIndex(string seed, int maxExclusive)
    {
        if (maxExclusive <= 1)
        {
            return 0;
        }

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(seed));
        var value = ((uint)hash[0] << 24) | ((uint)hash[1] << 16) | ((uint)hash[2] << 8) | hash[3];
        return (int)(value % (uint)maxExclusive);
    }

    /// <summary>Returns path parent segment for normalized absolute VFS path.</summary>
    private static string GetParentPath(string normalizedPath)
    {
        if (normalizedPath == "/")
        {
            return "/";
        }

        var index = normalizedPath.LastIndexOf('/');
        if (index <= 0)
        {
            return "/";
        }

        return normalizedPath[..index];
    }

    /// <summary>Clones daemon arg values recursively to avoid cross-server aliasing.</summary>
    private static object CloneValue(object value)
    {
        return value switch
        {
            Dictionary<string, object> map => map.ToDictionary(
                static pair => pair.Key,
                static pair => CloneValue(pair.Value),
                StringComparer.Ordinal),
            List<object> list => list.Select(CloneValue).ToList(),
            _ => value,
        };
    }
}
