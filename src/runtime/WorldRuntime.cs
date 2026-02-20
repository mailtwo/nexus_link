using Godot;
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

    /// <summary>Global server registry keyed by IP.</summary>
    public Dictionary<string, ServerNodeRuntime> ServerList { get; } = new();

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

        BaseFileSystem.AddFile("/system.bin", "uplink2-base-system");
        BaseFileSystem.AddFile("/etc/motd", "Welcome to Uplink2 runtime.");
        BaseFileSystem.AddFile("/bin/help.ms", "print \"help: ls, cd, cat, vim\"");
        BaseFileSystem.AddFile("/bin/ls.ms", "print \"ls (base stub)\"");
        BaseFileSystem.AddFile("/home/player/.profile", "export TERM=uplink2");
    }

    /// <summary>Creates initial world state with one workstation server.</summary>
    private void BuildInitialServerRuntime()
    {
        var workstationIp = "10.0.0.10";
        var workstation = new ServerNodeRuntime(
            name: "player-workstation",
            role: ServerRole.Terminal,
            ip: workstationIp,
            baseFileSystem: BaseFileSystem,
            blobStore: BlobStore);

        workstation.Users["player"] = new UserConfig
        {
            UserPasswd = "player",
            AuthMode = AuthMode.Static,
            Privilege = PrivilegeConfig.FullAccess(),
        };
        workstation.Users["system"] = new UserConfig
        {
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

        ServerList[workstation.Ip] = workstation;
        PlayerWorkstationServer = workstation;
    }

    /// <summary>Allocates the next unique process id.</summary>
    public int AllocateProcessId()
    {
        return nextProcessId++;
    }

    /// <summary>Returns a server by IP if found.</summary>
    public bool TryGetServer(string ip, out ServerNodeRuntime server)
    {
        return ServerList.TryGetValue(ip, out server);
    }
}
