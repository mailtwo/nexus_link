using Godot;
using System;
using System.Collections.Generic;
using Uplink2.Vfs;

namespace Uplink2.Runtime;

/// <summary>Global runtime root (register as Autoload).</summary>
public partial class WorldRuntime : Node
{
    /// <summary>Global runtime instance.</summary>
    public static WorldRuntime Instance { get; private set; }

    /// <summary>Shared blob store for file payloads.</summary>
    public BlobStore BlobStore { get; private set; }

    /// <summary>Shared immutable base filesystem image.</summary>
    public BaseFileSystem BaseFileSystem { get; private set; }

    /// <summary>Global server registry keyed by node id.</summary>
    public Dictionary<string, ServerNodeRuntime> ServerList { get; } = new(StringComparer.Ordinal);

    /// <summary>IP to node id reverse index.</summary>
    public Dictionary<string, string> IpIndex { get; } = new(StringComparer.Ordinal);

    /// <summary>Global process registry keyed by process id.</summary>
    public Dictionary<int, ProcessStruct> ProcessList { get; } = new();

    /// <summary>Initial player workstation server instance.</summary>
    public ServerNodeRuntime PlayerWorkstationServer { get; private set; }

    // Runtime allocators/state.
    private int nextProcessId = 1;

    /// <inheritdoc/>
    public override void _EnterTree()
    {
        Instance = this;
    }

    /// <inheritdoc/>
    public override void _Ready()
    {
        BlobStore = new BlobStore();
        BaseFileSystem = new BaseFileSystem(BlobStore);
        BuildBaseOsImage();
        BuildInitialServerRuntime();
    }

    /// <summary>Seeds the shared base OS image.</summary>
    private void BuildBaseOsImage()
    {
        BaseFileSystem.AddDirectory("/bin");
        BaseFileSystem.AddDirectory("/etc");
        BaseFileSystem.AddDirectory("/home/player");
        BaseFileSystem.AddDirectory("/var/log");

        BaseFileSystem.AddFile("/system.bin", "uplink2-base-system", fileKind: VfsFileKind.Binary);
        BaseFileSystem.AddFile("/etc/motd", "Welcome to Uplink2 runtime.", fileKind: VfsFileKind.Text);
        BaseFileSystem.AddFile("/bin/help.ms", "print \"help: ls, cd, cat, vim\"", fileKind: VfsFileKind.Text);
        BaseFileSystem.AddFile("/bin/ls.ms", "print \"ls (base stub)\"", fileKind: VfsFileKind.Text);
        BaseFileSystem.AddFile("/home/player/.profile", "export TERM=uplink2", fileKind: VfsFileKind.Text);
    }

    /// <summary>Creates initial world state with one workstation server.</summary>
    private void BuildInitialServerRuntime()
    {
        var workstation = new ServerNodeRuntime(
            nodeId: "player_workstation",
            name: "player-workstation",
            role: ServerRole.Terminal,
            baseFileSystem: BaseFileSystem,
            blobStore: BlobStore);

        workstation.SetInterfaces(new[]
        {
            new InterfaceRuntime
            {
                NetId = "internet",
                Ip = "10.0.0.10",
            },
        });
        workstation.SetExposure("internet", true);

        workstation.Users["player"] = new UserConfig
        {
            UserId = "player",
            UserPasswd = "player",
            AuthMode = AuthMode.Static,
            Privilege = PrivilegeConfig.FullAccess(),
        };
        workstation.Users["system"] = new UserConfig
        {
            UserId = "system",
            UserPasswd = string.Empty,
            AuthMode = AuthMode.None,
            Privilege = PrivilegeConfig.FullAccess(),
        };

        workstation.Ports[22] = new PortConfig
        {
            PortType = PortType.Ssh,
            ServiceId = "sshDefault",
            Exposure = PortExposure.Public,
        };

        workstation.DiskOverlay.AddDirectory("/home/player/work");
        workstation.DiskOverlay.WriteFile("/home/player/work/notes.txt", "Welcome operator.");

        workstation.AppendLog(new LogStruct
        {
            Id = 1,
            Time = (long)(Time.GetUnixTimeFromSystem() * 1000.0),
            User = "system",
            RemoteIp = "127.0.0.1",
            ActionType = LogActionType.Execute,
            Action = "runtime bootstrap",
        });

        RegisterServer(workstation);
        PlayerWorkstationServer = workstation;
    }

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
