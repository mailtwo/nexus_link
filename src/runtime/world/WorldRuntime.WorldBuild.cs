using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Uplink2.Blueprint;

namespace Uplink2.Runtime;

public partial class WorldRuntime
{
    /// <summary>Allocates concrete interface IP addresses for each spawned server.</summary>
    private Dictionary<string, List<SpawnInterfaceSeed>> AllocateScenarioInterfaces(
        ScenarioBlueprint scenario,
        IReadOnlyDictionary<string, ServerSpawnBlueprint> spawnByNodeId)
    {
        if (scenario.SubnetTopology.ContainsKey(InternetNetId))
        {
            throw new InvalidDataException($"Scenario '{scenario.ScenarioId}' cannot declare '{InternetNetId}' in subnetTopology.");
        }

        var usedIps = new HashSet<string>(StringComparer.Ordinal);
        var allocators = new Dictionary<string, AddressAllocator>(StringComparer.Ordinal)
        {
            [InternetNetId] = new AddressAllocator(
                ParseAddressPlan(DefaultInternetAddressPlan, $"Scenario.{scenario.ScenarioId}.{InternetNetId}.defaultAddressPlan"),
                usedIps),
        };

        foreach (var subnetPair in scenario.SubnetTopology)
        {
            var context = $"Scenario.{scenario.ScenarioId}.subnetTopology.{subnetPair.Key}.addressPlan";
            allocators[subnetPair.Key] = new AddressAllocator(ParseAddressPlan(subnetPair.Value.AddressPlan, context), usedIps);
        }

        var assignedByNodeId = new Dictionary<string, List<SpawnInterfaceSeed>>(StringComparer.Ordinal);
        foreach (var spawn in scenario.Servers)
        {
            if (!spawnByNodeId.ContainsKey(spawn.NodeId))
            {
                throw new InvalidDataException($"Scenario spawn index missing node '{spawn.NodeId}'.");
            }

            var nodeInterfaces = new List<SpawnInterfaceSeed>();
            var seenNetIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var interfaceBlueprint in spawn.Interfaces)
            {
                if (!seenNetIds.Add(interfaceBlueprint.NetId))
                {
                    throw new InvalidDataException($"Server '{spawn.NodeId}' has duplicate netId '{interfaceBlueprint.NetId}'.");
                }

                if (!allocators.TryGetValue(interfaceBlueprint.NetId, out var allocator))
                {
                    throw new InvalidDataException(
                        $"Server '{spawn.NodeId}' uses netId '{interfaceBlueprint.NetId}', but subnetTopology does not define it.");
                }

                var assignmentContext = $"Scenario.{scenario.ScenarioId}.servers[{spawn.NodeId}].interfaces[{interfaceBlueprint.NetId}]";
                var ip = interfaceBlueprint.HasHostSuffix
                    ? allocator.AllocateFixed(interfaceBlueprint.HostSuffix, assignmentContext)
                    : allocator.AllocateNext(assignmentContext);

                nodeInterfaces.Add(new SpawnInterfaceSeed
                {
                    NetId = interfaceBlueprint.NetId,
                    Ip = ip,
                    InitiallyExposed = interfaceBlueprint.InitiallyExposed,
                });
            }

            assignedByNodeId[spawn.NodeId] = nodeInterfaces;
        }

        return assignedByNodeId;
    }

    /// <summary>Instantiates runtime servers from merged spec + spawn blueprint data.</summary>
    private Dictionary<string, ServerNodeRuntime> BuildServerRuntimes(
        ScenarioBlueprint scenario,
        IReadOnlyDictionary<string, ServerSpawnBlueprint> spawnByNodeId,
        IReadOnlyDictionary<string, List<SpawnInterfaceSeed>> assignedInterfacesByNodeId,
        int worldSeed)
    {
        var serversByNodeId = new Dictionary<string, ServerNodeRuntime>(StringComparer.Ordinal);
        foreach (var spawn in scenario.Servers)
        {
            if (!spawnByNodeId.TryGetValue(spawn.NodeId, out var indexedSpawn))
            {
                throw new InvalidDataException($"Scenario spawn index missing node '{spawn.NodeId}'.");
            }

            if (!BlueprintCatalog.ServerSpecs.TryGetValue(indexedSpawn.SpecId, out var spec))
            {
                throw new InvalidDataException(
                    $"Scenario '{scenario.ScenarioId}' node '{indexedSpawn.NodeId}' references missing spec '{indexedSpawn.SpecId}'.");
            }

            var serverName = ResolveServerName(spec, indexedSpawn);
            var server = new ServerNodeRuntime(
                nodeId: indexedSpawn.NodeId,
                name: serverName,
                role: ConvertRole(indexedSpawn.Role),
                baseFileSystem: BaseFileSystem,
                blobStore: BlobStore);

            ApplyInitialServerState(server, spec, indexedSpawn);
            server.LogCapacity = spec.LogCapacity > 0 ? spec.LogCapacity : server.LogCapacity;
            foreach (var infoLine in indexedSpawn.Info)
            {
                server.Info.Add(infoLine);
            }

            if (!assignedInterfacesByNodeId.TryGetValue(indexedSpawn.NodeId, out var assignedInterfaces))
            {
                throw new InvalidDataException($"Missing allocated interfaces for node '{indexedSpawn.NodeId}'.");
            }

            server.SetInterfaces(assignedInterfaces.Select(static iface => new InterfaceRuntime
            {
                NetId = iface.NetId,
                Ip = iface.Ip,
            }));

            ApplyUsers(server, spec.Users, indexedSpawn.NodeId, worldSeed);
            ApplyPorts(server, spec.Ports, indexedSpawn.PortOverrides);
            ApplyDaemons(server, spec.Daemons, indexedSpawn.DaemonOverrides);
            ValidateOtpConsistency(server);
            ApplyDiskOverlay(server, spec.DiskOverlay, indexedSpawn.DiskOverlayOverrides);

            serversByNodeId[indexedSpawn.NodeId] = server;
        }

        return serversByNodeId;
    }

    /// <summary>Builds server lan-neighbor cache from subnet topology hubs/links.</summary>
    private void BuildLanNeighborCache(
        IReadOnlyDictionary<string, ServerNodeRuntime> serversByNodeId,
        ScenarioBlueprint scenario,
        IReadOnlyDictionary<string, List<SpawnInterfaceSeed>> assignedInterfacesByNodeId)
    {
        var neighborsByNodeId = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var nodeId in serversByNodeId.Keys)
        {
            neighborsByNodeId[nodeId] = new HashSet<string>(StringComparer.Ordinal);
        }

        var memberNodesBySubnet = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var nodePair in assignedInterfacesByNodeId)
        {
            foreach (var iface in nodePair.Value)
            {
                if (!memberNodesBySubnet.TryGetValue(iface.NetId, out var members))
                {
                    members = new HashSet<string>(StringComparer.Ordinal);
                    memberNodesBySubnet[iface.NetId] = members;
                }

                members.Add(nodePair.Key);
            }
        }

        foreach (var subnetPair in scenario.SubnetTopology)
        {
            var subnetId = subnetPair.Key;
            if (!memberNodesBySubnet.TryGetValue(subnetId, out var subnetMembers))
            {
                continue;
            }

            foreach (var hubPair in subnetPair.Value.Hubs)
            {
                var distinctMembers = hubPair.Value.Members
                    .Where(static member => !string.IsNullOrWhiteSpace(member))
                    .Distinct(StringComparer.Ordinal)
                    .ToArray();

                for (var i = 0; i < distinctMembers.Length; i++)
                {
                    for (var j = i + 1; j < distinctMembers.Length; j++)
                    {
                        AddNeighborEdge(
                            neighborsByNodeId,
                            subnetMembers,
                            subnetId,
                            distinctMembers[i],
                            distinctMembers[j],
                            $"hub '{hubPair.Key}'");
                    }
                }
            }

            foreach (var link in subnetPair.Value.Links)
            {
                AddNeighborEdge(
                    neighborsByNodeId,
                    subnetMembers,
                    subnetId,
                    link.A,
                    link.B,
                    "link");
            }
        }

        foreach (var server in serversByNodeId.Values)
        {
            server.LanNeighbors.Clear();
            foreach (var neighborNodeId in neighborsByNodeId[server.NodeId].OrderBy(static nodeId => nodeId, StringComparer.Ordinal))
            {
                server.LanNeighbors.Add(neighborNodeId);
            }
        }
    }

    /// <summary>Initializes VisibleNets/KnownNodes and server exposure cache from interface seeds.</summary>
    private void InitializeVisibilityState(
        IReadOnlyDictionary<string, ServerNodeRuntime> serversByNodeId,
        IReadOnlyDictionary<string, List<SpawnInterfaceSeed>> assignedInterfacesByNodeId)
    {
        VisibleNets.Clear();
        KnownNodesByNet.Clear();
        initiallyExposedNodesByNet.Clear();

        VisibleNets.Add(InternetNetId);
        KnownNodesByNet[InternetNetId] = new HashSet<string>(StringComparer.Ordinal);

        foreach (var nodePair in assignedInterfacesByNodeId)
        {
            foreach (var iface in nodePair.Value)
            {
                if (iface.InitiallyExposed)
                {
                    CaptureInitiallyExposedNode(iface.NetId, nodePair.Key);
                }

                if (!VisibleNets.Contains(iface.NetId) || !iface.InitiallyExposed)
                {
                    continue;
                }

                KnownNodesByNet[iface.NetId].Add(nodePair.Key);
            }
        }

        foreach (var server in serversByNodeId.Values)
        {
            foreach (var netId in server.SubnetMembership)
            {
                var isKnown = KnownNodesByNet.TryGetValue(netId, out var knownNodes) && knownNodes.Contains(server.NodeId);
                server.SetExposure(netId, isKnown);
            }
        }
    }

    /// <summary>Resolves player workstation runtime server for the active scenario.</summary>
    private ServerNodeRuntime ResolvePlayerWorkstation(IReadOnlyDictionary<string, ServerNodeRuntime> serversByNodeId)
    {
        if (serversByNodeId.Count == 0)
        {
            throw new InvalidDataException("No servers were instantiated for the active scenario.");
        }

        var configuredNodeId = StartupServerNodeId?.Trim() ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(configuredNodeId))
        {
            if (serversByNodeId.TryGetValue(configuredNodeId, out var configuredServer))
            {
                return configuredServer;
            }

            throw new InvalidDataException(
                $"Startup server node '{configuredNodeId}' was not found in the active scenario bundle '{ActiveScenarioId}'.");
        }

        var ordered = serversByNodeId.Values.OrderBy(static server => server.NodeId, StringComparer.Ordinal).ToList();
        var internetTerminal = ordered.FirstOrDefault(static server =>
            server.Role == ServerRole.Terminal &&
            server.SubnetMembership.Contains(InternetNetId));
        if (internetTerminal is not null)
        {
            return internetTerminal;
        }

        var internetAny = ordered.FirstOrDefault(static server => server.SubnetMembership.Contains(InternetNetId));
        return internetAny ?? ordered[0];
    }
}
