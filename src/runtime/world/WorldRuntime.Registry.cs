using System;

namespace Uplink2.Runtime;

public partial class WorldRuntime
{
    /// <summary>Registers a server and refreshes ip index entries.</summary>
    public void RegisterServer(ServerNodeRuntime server)
    {
        if (server is null)
        {
            throw new ArgumentNullException(nameof(server));
        }

        if (ServerList.ContainsKey(server.NodeId))
        {
            throw new InvalidOperationException($"Duplicate node id: {server.NodeId}");
        }

        ServerList[server.NodeId] = server;

        foreach (var iface in server.Interfaces)
        {
            if (IpIndex.TryGetValue(iface.Ip, out var existingNodeId) &&
                !string.Equals(existingNodeId, server.NodeId, StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"Duplicate IP mapping: {iface.Ip}");
            }

            IpIndex[iface.Ip] = server.NodeId;
        }
    }

    /// <summary>Allocates the next unique process id.</summary>
    public int AllocateProcessId()
    {
        return nextProcessId++;
    }

    /// <summary>Returns a server by node id if found.</summary>
    public bool TryGetServer(string nodeId, out ServerNodeRuntime server)
    {
        return ServerList.TryGetValue(nodeId, out server);
    }

    /// <summary>Returns a server by IP if found.</summary>
    public bool TryGetServerByIp(string ip, out ServerNodeRuntime server)
    {
        server = null;
        if (!IpIndex.TryGetValue(ip, out var nodeId))
        {
            return false;
        }

        return ServerList.TryGetValue(nodeId, out server);
    }

    /// <summary>Resolves node id from IP if present.</summary>
    public bool TryResolveNodeIdByIp(string ip, out string nodeId)
    {
        return IpIndex.TryGetValue(ip, out nodeId);
    }
}
