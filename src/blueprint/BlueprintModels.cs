using System;
using System.Collections.Generic;

#nullable enable

namespace Uplink2.Blueprint;

/// <summary>Top-level in-memory container for loaded blueprint data.</summary>
public sealed class BlueprintCatalog
{
    /// <summary>Server specs keyed by specId.</summary>
    public Dictionary<string, ServerSpecBlueprint> ServerSpecs { get; } = new(StringComparer.Ordinal);

    /// <summary>Scenarios keyed by scenarioId.</summary>
    public Dictionary<string, ScenarioBlueprint> Scenarios { get; } = new(StringComparer.Ordinal);

    /// <summary>Campaigns keyed by campaignId.</summary>
    public Dictionary<string, CampaignBlueprint> Campaigns { get; } = new(StringComparer.Ordinal);
}

/// <summary>Server online/offline state at world start.</summary>
public enum BlueprintServerStatus
{
    Online,
    Offline,
}

/// <summary>Reason for current server status.</summary>
public enum BlueprintServerReason
{
    Ok,
    Reboot,
    Disabled,
    Crashed,
}

/// <summary>Server role declared in scenario spawn.</summary>
public enum BlueprintServerRole
{
    Terminal,
    OtpGenerator,
    Mainframe,
    Tracer,
    Gateway,
}

/// <summary>User authentication mode in blueprints.</summary>
public enum BlueprintAuthMode
{
    None,
    Static,
    Otp,
    Other,
}

/// <summary>Daemon type keys used by blueprint daemons map.</summary>
public enum BlueprintDaemonType
{
    Otp,
    Firewall,
    ConnectionRateLimiter,
}

/// <summary>Port protocol type (`None` means unassigned).</summary>
public enum BlueprintPortType
{
    None,
    Ssh,
    Ftp,
    Http,
    Sql,
}

/// <summary>Port exposure scope.</summary>
public enum BlueprintPortExposure
{
    Public,
    Lan,
    Localhost,
}

/// <summary>Overlay entry kind.</summary>
public enum BlueprintEntryKind
{
    File,
    Dir,
}

/// <summary>Overlay file kind for file entries.</summary>
public enum BlueprintFileKind
{
    Text,
    Binary,
    Image,
    ExecutableScript,
    ExecutableHardcode,
}

/// <summary>Event trigger category.</summary>
public enum BlueprintConditionType
{
    PrivilegeAcquire,
    FileAcquire,
}

/// <summary>Event action category.</summary>
public enum BlueprintActionType
{
    Print,
    SetFlag,
}

/// <summary>Subnet hub type.</summary>
public enum BlueprintHubType
{
    Switch,
}

/// <summary>Reusable server recipe blueprint.</summary>
public sealed class ServerSpecBlueprint
{
    /// <summary>Unique server spec id.</summary>
    public string SpecId { get; set; } = string.Empty;

    /// <summary>Initial server status.</summary>
    public BlueprintServerStatus InitialStatus { get; set; } = BlueprintServerStatus.Online;

    /// <summary>Initial server reason.</summary>
    public BlueprintServerReason InitialReason { get; set; } = BlueprintServerReason.Ok;

    /// <summary>Default hostname.</summary>
    public string Hostname { get; set; } = string.Empty;

    /// <summary>User blueprints keyed by userKey.</summary>
    public Dictionary<string, UserBlueprint> Users { get; } = new(StringComparer.Ordinal);

    /// <summary>Port blueprints keyed by port number.</summary>
    public Dictionary<int, PortBlueprint> Ports { get; } = new();

    /// <summary>Daemon blueprints keyed by daemon type.</summary>
    public Dictionary<BlueprintDaemonType, DaemonBlueprint> Daemons { get; } = new();

    /// <summary>Default disk overlay entries and tombstones.</summary>
    public DiskOverlayBlueprint DiskOverlay { get; set; } = new();

    /// <summary>Per-server log buffer capacity.</summary>
    public int LogCapacity { get; set; } = 256;
}

/// <summary>User account blueprint.</summary>
public sealed class UserBlueprint
{
    /// <summary>Display/login user id text.</summary>
    public string UserId { get; set; } = string.Empty;

    /// <summary>Password text (empty when omitted by content).</summary>
    public string Passwd { get; set; } = string.Empty;

    /// <summary>Authentication mode.</summary>
    public BlueprintAuthMode AuthMode { get; set; } = BlueprintAuthMode.None;

    /// <summary>Read/write/execute privilege flags.</summary>
    public PrivilegeBlueprint Privilege { get; set; } = new();
}

/// <summary>Privilege flags for an account blueprint.</summary>
public sealed class PrivilegeBlueprint
{
    /// <summary>Read privilege.</summary>
    public bool Read { get; set; }

    /// <summary>Write privilege.</summary>
    public bool Write { get; set; }

    /// <summary>Execute privilege.</summary>
    public bool Execute { get; set; }
}

/// <summary>Port configuration blueprint.</summary>
public sealed class PortBlueprint
{
    /// <summary>Protocol type.</summary>
    public BlueprintPortType PortType { get; set; } = BlueprintPortType.Ssh;

    /// <summary>Optional service behavior id.</summary>
    public string ServiceId { get; set; } = string.Empty;

    /// <summary>Exposure scope.</summary>
    public BlueprintPortExposure Exposure { get; set; } = BlueprintPortExposure.Public;
}

/// <summary>Daemon configuration blueprint with typed args in key/value form.</summary>
public sealed class DaemonBlueprint
{
    /// <summary>Daemon type key.</summary>
    public BlueprintDaemonType DaemonType { get; set; } = BlueprintDaemonType.Otp;

    /// <summary>Daemon arguments by key (contract is daemon-type specific).</summary>
    public Dictionary<string, object> DaemonArgs { get; } = new(StringComparer.Ordinal);
}

/// <summary>Overlay disk data used by server spec defaults.</summary>
public sealed class DiskOverlayBlueprint
{
    /// <summary>Overlay entries keyed by absolute path.</summary>
    public Dictionary<string, BlueprintEntryMeta> OverlayEntries { get; } = new(StringComparer.Ordinal);

    /// <summary>Paths hidden from base by tombstone.</summary>
    public HashSet<string> Tombstones { get; } = new(StringComparer.Ordinal);
}

/// <summary>Overlay entry metadata used in blueprint disk layers.</summary>
public sealed class BlueprintEntryMeta
{
    /// <summary>Entry kind (file or dir).</summary>
    public BlueprintEntryKind EntryKind { get; set; } = BlueprintEntryKind.File;

    /// <summary>File kind for file entries.</summary>
    public BlueprintFileKind FileKind { get; set; } = BlueprintFileKind.Text;

    /// <summary>Blob content id or resource path reference for file content.</summary>
    public string ContentId { get; set; } = string.Empty;

    /// <summary>Optional gameplay-visible file size hint in bytes.</summary>
    public int? Size { get; set; }

    /// <summary>Actual UTF-8 payload byte size computed from resolved content.</summary>
    public int RealSize { get; set; }

    /// <summary>Optional owner text metadata.</summary>
    public string Owner { get; set; } = string.Empty;

    /// <summary>Optional permission text metadata.</summary>
    public string Perms { get; set; } = string.Empty;

    /// <summary>Optional modification time metadata in unix ms.</summary>
    public long MtimeMs { get; set; }
}

/// <summary>Scenario unit blueprint.</summary>
public sealed class ScenarioBlueprint
{
    /// <summary>Unique scenario id.</summary>
    public string ScenarioId { get; set; } = string.Empty;

    /// <summary>Servers spawned for this scenario.</summary>
    public List<ServerSpawnBlueprint> Servers { get; } = new();

    /// <summary>Subnet topology keyed by subnet id.</summary>
    public Dictionary<string, SubnetBlueprint> SubnetTopology { get; } = new(StringComparer.Ordinal);

    /// <summary>Events keyed by event id.</summary>
    public Dictionary<string, EventBlueprint> Events { get; } = new(StringComparer.Ordinal);

    /// <summary>Reusable MiniScript guard bodies keyed by script id.</summary>
    public Dictionary<string, string> Scripts { get; } = new(StringComparer.Ordinal);
}

/// <summary>Single spawned server declaration in a scenario.</summary>
public sealed class ServerSpawnBlueprint
{
    /// <summary>Unique node id in world scope.</summary>
    public string NodeId { get; set; } = string.Empty;

    /// <summary>Referenced server spec id.</summary>
    public string SpecId { get; set; } = string.Empty;

    /// <summary>Optional hostname override (empty means not overridden).</summary>
    public string HostnameOverride { get; set; } = string.Empty;

    /// <summary>True when initial status override is specified.</summary>
    public bool HasInitialStatusOverride { get; set; }

    /// <summary>Initial status override value.</summary>
    public BlueprintServerStatus InitialStatusOverride { get; set; } = BlueprintServerStatus.Online;

    /// <summary>True when initial reason override is specified.</summary>
    public bool HasInitialReasonOverride { get; set; }

    /// <summary>Initial reason override value.</summary>
    public BlueprintServerReason InitialReasonOverride { get; set; } = BlueprintServerReason.Ok;

    /// <summary>Spawn-time server role.</summary>
    public BlueprintServerRole Role { get; set; } = BlueprintServerRole.Terminal;

    /// <summary>Scenario-provided informational lines.</summary>
    public List<string> Info { get; } = new();

    /// <summary>Disk overlay overrides keyed by path.</summary>
    public DiskOverlayOverrideBlueprint DiskOverlayOverrides { get; set; } = new();

    /// <summary>Port overrides keyed by port number.</summary>
    public Dictionary<int, PortOverrideBlueprint> PortOverrides { get; } = new();

    /// <summary>Daemon overrides keyed by daemon type.</summary>
    public Dictionary<BlueprintDaemonType, DaemonOverrideBlueprint> DaemonOverrides { get; } = new();

    /// <summary>Declared network interfaces for this server.</summary>
    public List<InterfaceBlueprint> Interfaces { get; } = new();
}

/// <summary>Path-level overlay overrides for scenario spawn.</summary>
public sealed class DiskOverlayOverrideBlueprint
{
    /// <summary>Entry overrides keyed by path.</summary>
    public Dictionary<string, EntryOverrideBlueprint> OverlayEntries { get; } = new(StringComparer.Ordinal);

    /// <summary>Tombstones added by this spawn overlay.</summary>
    public HashSet<string> Tombstones { get; } = new(StringComparer.Ordinal);
}

/// <summary>Overlay entry replacement/removal descriptor.</summary>
public sealed class EntryOverrideBlueprint
{
    /// <summary>True when this path should be removed.</summary>
    public bool Remove { get; set; }

    /// <summary>Replacement entry metadata when Remove is false.</summary>
    public BlueprintEntryMeta Entry { get; set; } = new();
}

/// <summary>Port replacement/removal descriptor.</summary>
public sealed class PortOverrideBlueprint
{
    /// <summary>True when this port should be removed.</summary>
    public bool Remove { get; set; }

    /// <summary>Replacement port config when Remove is false.</summary>
    public PortBlueprint Port { get; set; } = new();
}

/// <summary>Daemon replacement/removal descriptor.</summary>
public sealed class DaemonOverrideBlueprint
{
    /// <summary>True when this daemon should be removed.</summary>
    public bool Remove { get; set; }

    /// <summary>Replacement daemon config when Remove is false.</summary>
    public DaemonBlueprint Daemon { get; set; } = new();
}

/// <summary>Interface declaration for scenario server spawn.</summary>
public sealed class InterfaceBlueprint
{
    /// <summary>Network id (internet/easy_subnet/...)</summary>
    public string NetId { get; set; } = string.Empty;

    /// <summary>True when host suffix is explicitly provided.</summary>
    public bool HasHostSuffix { get; set; }

    /// <summary>Host suffix bytes used for fixed IP assignment.</summary>
    public List<int> HostSuffix { get; } = new();

    /// <summary>Initial known/exposed flag for this interface network.</summary>
    public bool InitiallyExposed { get; set; }
}

/// <summary>Subnet topology blueprint.</summary>
public sealed class SubnetBlueprint
{
    /// <summary>CIDR address plan string.</summary>
    public string AddressPlan { get; set; } = string.Empty;

    /// <summary>Hub declarations keyed by hub id.</summary>
    public Dictionary<string, HubBlueprint> Hubs { get; } = new(StringComparer.Ordinal);

    /// <summary>Explicit node-to-node links.</summary>
    public List<SubnetLinkBlueprint> Links { get; } = new();
}

/// <summary>Hub declaration within subnet topology.</summary>
public sealed class HubBlueprint
{
    /// <summary>Hub behavior type.</summary>
    public BlueprintHubType Type { get; set; } = BlueprintHubType.Switch;

    /// <summary>Member node ids connected by this hub.</summary>
    public List<string> Members { get; } = new();
}

/// <summary>Explicit adjacency edge between two nodes.</summary>
public sealed class SubnetLinkBlueprint
{
    /// <summary>Source node id.</summary>
    public string A { get; set; } = string.Empty;

    /// <summary>Destination node id.</summary>
    public string B { get; set; } = string.Empty;
}

/// <summary>Event declaration blueprint.</summary>
public sealed class EventBlueprint
{
    /// <summary>Condition category for trigger evaluation.</summary>
    public BlueprintConditionType ConditionType { get; set; } = BlueprintConditionType.PrivilegeAcquire;

    /// <summary>Condition payload by key.</summary>
    public Dictionary<string, object> ConditionArgs { get; } = new(StringComparer.Ordinal);

    /// <summary>Optional guard source descriptor (`script-`, `id-`, or `path-`).</summary>
    public string GuardContent { get; set; } = string.Empty;

    /// <summary>Actions executed when condition is met.</summary>
    public List<ActionBlueprint> Actions { get; } = new();
}

/// <summary>Action declaration blueprint.</summary>
public sealed class ActionBlueprint
{
    /// <summary>Action category.</summary>
    public BlueprintActionType ActionType { get; set; } = BlueprintActionType.Print;

    /// <summary>Action payload by key.</summary>
    public Dictionary<string, object> ActionArgs { get; } = new(StringComparer.Ordinal);
}

/// <summary>Campaign grouping blueprint.</summary>
public sealed class CampaignBlueprint
{
    /// <summary>Unique campaign id.</summary>
    public string CampaignId { get; set; } = string.Empty;

    /// <summary>Child campaign ids.</summary>
    public List<string> ChildCampaigns { get; } = new();

    /// <summary>Included scenario ids.</summary>
    public List<string> Scenarios { get; } = new();
}
