using System;
using System.Collections.Generic;
using Uplink2.Vfs;

namespace Uplink2.Runtime;

/// <summary>Server role type in runtime world.</summary>
public enum ServerRole
{
    Terminal,
    OtpGenerator,
    Mainframe,
    Tracer,
    Gateway,
}

/// <summary>Logical online state for a server.</summary>
public enum ServerStatus
{
    Online,
    Offline,
}

/// <summary>Detailed reason for current server status.</summary>
public enum ServerReason
{
    Ok,
    Reboot,
    Disabled,
    Crashed,
}

/// <summary>Process lifecycle state.</summary>
public enum ProcessState
{
    Running,
    Finished,
    Canceled,
}

/// <summary>Process completion behavior type.</summary>
public enum ProcessType
{
    Generic,
    Booting,
    FtpSend,
    FileWrite,
}

/// <summary>Authentication mode for an account.</summary>
public enum AuthMode
{
    None,
    Static,
    Otp,
    Other,
}

/// <summary>Service protocol type for a port.</summary>
public enum PortType
{
    Ssh,
    Ftp,
    Http,
    Sql,
}

/// <summary>Exposure scope for a port.</summary>
public enum PortExposure
{
    Public,
    Lan,
    Localhost,
}

/// <summary>Daemon type used for gameplay rules/hints.</summary>
public enum DaemonType
{
    Otp,
    Firewall,
    ConnectionRateLimiter,
}

/// <summary>Log action category.</summary>
public enum LogActionType
{
    Login,
    Logout,
    Read,
    Write,
    Execute,
}

/// <summary>Privilege flags for account actions.</summary>
public sealed class PrivilegeConfig
{
    /// <summary>Permission to read files/logs.</summary>
    public bool Read { get; set; }

    /// <summary>Permission to write files/logs.</summary>
    public bool Write { get; set; }

    /// <summary>Permission to execute programs/server actions.</summary>
    public bool Execute { get; set; }

    /// <summary>Creates a full-access privilege config.</summary>
    public static PrivilegeConfig FullAccess()
    {
        return new PrivilegeConfig { Read = true, Write = true, Execute = true };
    }

    /// <summary>Creates an empty privilege config.</summary>
    public static PrivilegeConfig None()
    {
        return new PrivilegeConfig { Read = false, Write = false, Execute = false };
    }
}

/// <summary>User account runtime configuration keyed by userKey.</summary>
public sealed class UserConfig
{
    /// <summary>Display/login id text for this account.</summary>
    public string UserId { get; set; } = string.Empty;

    /// <summary>Account password (empty when unset).</summary>
    public string UserPasswd { get; set; } = string.Empty;

    /// <summary>Authentication mode for this account.</summary>
    public AuthMode AuthMode { get; set; } = AuthMode.None;

    /// <summary>Privilege configuration.</summary>
    public PrivilegeConfig Privilege { get; set; } = PrivilegeConfig.None();

    /// <summary>Additional info/hint lines for this user.</summary>
    public List<string> Info { get; } = new();
}

/// <summary>Live session metadata on a server.</summary>
public sealed class SessionConfig
{
    /// <summary>Authenticated user key.</summary>
    public string UserKey { get; set; } = string.Empty;

    /// <summary>Remote source IP.</summary>
    public string RemoteIp { get; set; } = "127.0.0.1";

    /// <summary>Current working directory.</summary>
    public string Cwd { get; set; } = "/";
}

/// <summary>Port service runtime configuration.</summary>
public sealed class PortConfig
{
    /// <summary>Service protocol type.</summary>
    public PortType PortType { get; set; } = PortType.Ssh;

    /// <summary>Optional service behavior id.</summary>
    public string ServiceId { get; set; } = string.Empty;

    /// <summary>Exposure scope for the service.</summary>
    public PortExposure Exposure { get; set; } = PortExposure.Public;
}

/// <summary>Daemon runtime configuration.</summary>
public sealed class DaemonStruct
{
    /// <summary>Daemon type key.</summary>
    public DaemonType DaemonType { get; set; }

    /// <summary>Arbitrary daemon arguments keyed by daemon contract.</summary>
    public Dictionary<string, object> DaemonArgs { get; } = new(StringComparer.Ordinal);
}

/// <summary>Server interface runtime record.</summary>
public sealed class InterfaceRuntime
{
    /// <summary>Subnet/network id (e.g. internet, medium_subnet).</summary>
    public string NetId { get; set; } = string.Empty;

    /// <summary>IP address bound to this network id.</summary>
    public string Ip { get; set; } = string.Empty;
}

/// <summary>Global process record stored in processList.</summary>
public sealed class ProcessStruct
{
    /// <summary>Display/debug name.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Host server node id where this process runs.</summary>
    public string HostNodeId { get; set; } = string.Empty;

    /// <summary>User key that launched this process.</summary>
    public string UserKey { get; set; } = "system";

    /// <summary>Current process lifecycle state.</summary>
    public ProcessState State { get; set; } = ProcessState.Running;

    /// <summary>Program path associated with this process.</summary>
    public string Path { get; set; } = string.Empty;

    /// <summary>Completion behavior type.</summary>
    public ProcessType ProcessType { get; set; } = ProcessType.Generic;

    /// <summary>Completion behavior arguments.</summary>
    public Dictionary<string, object> ProcessArgs { get; } = new(StringComparer.Ordinal);

    /// <summary>Completion time as unix milliseconds.</summary>
    public long EndAt { get; set; }
}

/// <summary>Runtime log record for gameplay and forensics.</summary>
public sealed class LogStruct
{
    /// <summary>Sequential log id.</summary>
    public int Id { get; set; }

    /// <summary>Unix time in milliseconds.</summary>
    public long Time { get; set; }

    /// <summary>Action subject display user id text.</summary>
    public string User { get; set; } = string.Empty;

    /// <summary>Remote source IP.</summary>
    public string RemoteIp { get; set; } = "127.0.0.1";

    /// <summary>Action category.</summary>
    public LogActionType ActionType { get; set; } = LogActionType.Read;

    /// <summary>Human-readable action text.</summary>
    public string Action { get; set; } = string.Empty;

    /// <summary>True if this log was modified after creation.</summary>
    public bool Dirty { get; private set; }

    /// <summary>Original snapshot captured on first mutation.</summary>
    public LogStruct? Origin { get; private set; }

    /// <summary>Marks this log dirty and updates action text.</summary>
    public void MarkDirty(string updatedAction)
    {
        if (!Dirty)
        {
            Origin = Copy();
            Dirty = true;
        }

        Action = updatedAction;
    }

    /// <summary>Creates a shallow copy for origin snapshotting.</summary>
    public LogStruct Copy()
    {
        return new LogStruct
        {
            Id = Id,
            Time = Time,
            User = User,
            RemoteIp = RemoteIp,
            ActionType = ActionType,
            Action = Action,
            Dirty = Dirty,
            Origin = Origin,
        };
    }
}

/// <summary>Runtime state container for a single server node.</summary>
public sealed class ServerNodeRuntime
{
    /// <summary>Unique node id key.</summary>
    public string NodeId { get; }

    /// <summary>Display name.</summary>
    public string Name { get; set; }

    /// <summary>Server role type.</summary>
    public ServerRole Role { get; set; }

    /// <summary>Logical online/offline state.</summary>
    public ServerStatus Status { get; private set; }

    /// <summary>Detailed reason for current status.</summary>
    public ServerReason Reason { get; private set; }

    /// <summary>Primary player-visible IP (internet interface preferred).</summary>
    public string? PrimaryIp { get; private set; }

    /// <summary>Attached network interfaces.</summary>
    public List<InterfaceRuntime> Interfaces { get; } = new();

    /// <summary>Set of netIds this server belongs to.</summary>
    public HashSet<string> SubnetMembership { get; } = new(StringComparer.Ordinal);

    /// <summary>Current exposure state by netId.</summary>
    public Dictionary<string, bool> IsExposedByNet { get; } = new(StringComparer.Ordinal);

    /// <summary>User accounts indexed by userKey.</summary>
    public Dictionary<string, UserConfig> Users { get; } = new(StringComparer.Ordinal);

    /// <summary>Active sessions indexed by session id.</summary>
    public Dictionary<int, SessionConfig> Sessions { get; } = new();

    /// <summary>Neighboring node ids in the current topology.</summary>
    public List<string> LanNeighbors { get; } = new();

    /// <summary>Port configuration indexed by port number.</summary>
    public Dictionary<int, PortConfig> Ports { get; } = new();

    /// <summary>Server-local disk overlay.</summary>
    public OverlayFileSystem DiskOverlay { get; }

    /// <summary>Owned process ids currently associated with this server.</summary>
    public HashSet<int> Process { get; } = new();

    /// <summary>Daemon configurations indexed by daemon type.</summary>
    public Dictionary<DaemonType, DaemonStruct> Daemons { get; } = new();

    /// <summary>Maximum number of logs kept in ring buffer.</summary>
    public int LogCapacity { get; set; } = 256;

    // Internal ring buffer storage.
    private readonly Queue<LogStruct> logs = new();

    /// <summary>Read-only snapshot of logs in insertion order.</summary>
    public IReadOnlyCollection<LogStruct> Logs => logs.ToArray();

    /// <summary>Creates a server runtime with empty state collections.</summary>
    public ServerNodeRuntime(string nodeId, string name, ServerRole role, BaseFileSystem baseFileSystem, BlobStore blobStore)
    {
        if (string.IsNullOrWhiteSpace(nodeId))
        {
            throw new ArgumentException("Node id cannot be empty.", nameof(nodeId));
        }

        NodeId = nodeId;
        Name = name;
        Role = role;
        Status = ServerStatus.Online;
        Reason = ServerReason.Ok;
        DiskOverlay = new OverlayFileSystem(baseFileSystem, blobStore);
    }

    /// <summary>Replaces interfaces and rebuilds derived network caches.</summary>
    public void SetInterfaces(IEnumerable<InterfaceRuntime> interfaces)
    {
        if (interfaces is null)
        {
            throw new ArgumentNullException(nameof(interfaces));
        }

        Interfaces.Clear();
        foreach (var iface in interfaces)
        {
            if (iface is null || string.IsNullOrWhiteSpace(iface.NetId) || string.IsNullOrWhiteSpace(iface.Ip))
            {
                continue;
            }

            Interfaces.Add(new InterfaceRuntime
            {
                NetId = iface.NetId,
                Ip = iface.Ip,
            });
        }

        RebuildNetworkCaches();
    }

    /// <summary>Rebuilds primaryIp, subnet membership, and exposure keys from interfaces.</summary>
    public void RebuildNetworkCaches()
    {
        PrimaryIp = null;
        SubnetMembership.Clear();

        var nextExposure = new Dictionary<string, bool>(StringComparer.Ordinal);

        foreach (var iface in Interfaces)
        {
            if (PrimaryIp is null && string.Equals(iface.NetId, "internet", StringComparison.Ordinal))
            {
                PrimaryIp = iface.Ip;
            }

            SubnetMembership.Add(iface.NetId);
            nextExposure[iface.NetId] = IsExposedByNet.GetValueOrDefault(iface.NetId);
        }

        IsExposedByNet.Clear();
        foreach (var pair in nextExposure)
        {
            IsExposedByNet[pair.Key] = pair.Value;
        }
    }

    /// <summary>Sets current exposure state for a netId.</summary>
    public void SetExposure(string netId, bool isExposed)
    {
        if (string.IsNullOrWhiteSpace(netId))
        {
            throw new ArgumentException("Net id cannot be empty.", nameof(netId));
        }

        IsExposedByNet[netId] = isExposed;
    }

    /// <summary>Sets this server to online/OK invariant.</summary>
    public void SetOnline()
    {
        Status = ServerStatus.Online;
        Reason = ServerReason.Ok;
    }

    /// <summary>Sets this server to offline with explicit non-OK reason.</summary>
    public void SetOffline(ServerReason reason)
    {
        if (reason == ServerReason.Ok)
        {
            throw new InvalidOperationException("Offline state cannot use OK reason.");
        }

        Status = ServerStatus.Offline;
        Reason = reason;
    }

    /// <summary>Adds or replaces an active session by id.</summary>
    public void UpsertSession(int sessionId, SessionConfig session)
    {
        Sessions[sessionId] = session;
    }

    /// <summary>Removes an active session by id.</summary>
    public void RemoveSession(int sessionId)
    {
        Sessions.Remove(sessionId);
    }

    /// <summary>Clears all active sessions.</summary>
    public void ClearSessions()
    {
        Sessions.Clear();
    }

    /// <summary>Adds a process ownership id.</summary>
    public void AddProcess(int processId)
    {
        Process.Add(processId);
    }

    /// <summary>Removes a process ownership id.</summary>
    public void RemoveProcess(int processId)
    {
        Process.Remove(processId);
    }

    /// <summary>Clears all process ownership ids.</summary>
    public void ClearProcesses()
    {
        Process.Clear();
    }

    /// <summary>Appends a log using ring-buffer eviction.</summary>
    public void AppendLog(LogStruct log)
    {
        if (LogCapacity < 1)
        {
            LogCapacity = 1;
        }

        while (logs.Count >= LogCapacity)
        {
            logs.Dequeue();
        }

        logs.Enqueue(log);
    }
}
