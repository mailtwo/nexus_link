using MessagePack;
using System.Collections.Generic;

#nullable enable

namespace Uplink2.Runtime.Persistence;

internal static class SaveContainerConstants
{
    internal const ushort FormatMajor = 1;
    internal const ushort FormatMinor = 0;
    internal const uint FlagBrotli = 1u << 0;
    internal const uint FlagHmacSha256 = 1u << 1;
    internal const uint RequiredFlags = FlagBrotli | FlagHmacSha256;
    internal const uint ChunkIdSaveMeta = 0x0001;
    internal const uint ChunkIdWorldState = 0x0002;
    internal const uint ChunkIdEventState = 0x0003;
    internal const uint ChunkIdProcessState = 0x0004;
    internal const uint ChunkIdServerState = 0x0100;
    internal const ushort ChunkVersion1 = 1;
    internal const string SaveSchemaVersion = "0.1";
    internal const string CampaignPrefix = "campaign:";
    internal const int HmacSha256Length = 32;

    internal static readonly byte[] Magic = { (byte)'U', (byte)'L', (byte)'S', (byte)'1' };
}

internal sealed class SaveFileHeader
{
    internal ushort FormatMajor { get; init; }

    internal ushort FormatMinor { get; init; }

    internal uint Flags { get; init; }

    internal uint ChunkCount { get; init; }
}

internal sealed class SaveChunkRecord
{
    internal uint ChunkId { get; init; }

    internal ushort ChunkVersion { get; init; }

    internal byte[] PayloadBytes { get; init; } = [];
}

internal sealed class ParsedSaveContainer
{
    internal SaveFileHeader Header { get; init; } = new();

    internal IReadOnlyList<SaveChunkRecord> Chunks { get; init; } = [];
}

internal enum SaveValueKind
{
    Null = 0,
    Bool = 1,
    Int = 2,
    Long = 3,
    Double = 4,
    String = 5,
    List = 6,
    Map = 7,
}

[MessagePackObject(AllowPrivate = true)]
internal sealed class SaveValueDto
{
    [Key(0)]
    public SaveValueKind Kind { get; set; }

    [Key(1)]
    public bool BoolValue { get; set; }

    [Key(2)]
    public int IntValue { get; set; }

    [Key(3)]
    public long LongValue { get; set; }

    [Key(4)]
    public double DoubleValue { get; set; }

    [Key(5)]
    public string StringValue { get; set; } = string.Empty;

    [Key(6)]
    public List<SaveValueDto> ListValue { get; set; } = [];

    [Key(7)]
    public Dictionary<string, SaveValueDto> MapValue { get; set; } = [];
}

[MessagePackObject(AllowPrivate = true)]
internal sealed class SaveMetaChunkDto
{
    [Key(0)]
    public string SaveSchemaVersion { get; set; } = string.Empty;

    [Key(1)]
    public string ActiveScenarioId { get; set; } = string.Empty;

    [Key(2)]
    public int WorldSeed { get; set; }

    [Key(3)]
    public long? SavedAtUnixMs { get; set; }
}

[MessagePackObject(AllowPrivate = true)]
internal sealed class WorldStateChunkDto
{
    [Key(0)]
    public long WorldTickIndex { get; set; }

    [Key(1)]
    public long EventSeq { get; set; }

    [Key(2)]
    public int NextProcessId { get; set; }

    [Key(3)]
    public List<string> VisibleNets { get; set; } = [];

    [Key(4)]
    public Dictionary<string, List<string>> KnownNodesByNet { get; set; } = [];

    [Key(5)]
    public Dictionary<string, SaveValueDto> ScenarioFlags { get; set; } = [];
}

[MessagePackObject(AllowPrivate = true)]
internal sealed class EventStateChunkDto
{
    [Key(0)]
    public List<string> FiredHandlerIds { get; set; } = [];
}

[MessagePackObject(AllowPrivate = true)]
internal sealed class ProcessStateChunkDto
{
    [Key(0)]
    public List<ProcessSnapshotDto> Processes { get; set; } = [];
}

[MessagePackObject(AllowPrivate = true)]
internal sealed class ProcessSnapshotDto
{
    [Key(0)]
    public int ProcessId { get; set; }

    [Key(1)]
    public string Name { get; set; } = string.Empty;

    [Key(2)]
    public string HostNodeId { get; set; } = string.Empty;

    [Key(3)]
    public string UserKey { get; set; } = string.Empty;

    [Key(4)]
    public int State { get; set; }

    [Key(5)]
    public string Path { get; set; } = string.Empty;

    [Key(6)]
    public int ProcessType { get; set; }

    [Key(7)]
    public Dictionary<string, SaveValueDto> ProcessArgs { get; set; } = [];

    [Key(8)]
    public long EndAt { get; set; }
}

[MessagePackObject(AllowPrivate = true)]
internal sealed class ServerStateChunkDto
{
    [Key(0)]
    public string NodeId { get; set; } = string.Empty;

    [Key(1)]
    public int Status { get; set; }

    [Key(2)]
    public int Reason { get; set; }

    [Key(3)]
    public Dictionary<string, UserSnapshotDto> Users { get; set; } = [];

    [Key(4)]
    public DiskOverlaySnapshotDto DiskOverlay { get; set; } = new();

    [Key(5)]
    public List<LogSnapshotDto> Logs { get; set; } = [];

    [Key(6)]
    public int? LogCapacity { get; set; }

    [Key(7)]
    public Dictionary<int, PortSnapshotDto>? Ports { get; set; }

    [Key(8)]
    public Dictionary<int, DaemonSnapshotDto>? Daemons { get; set; }
}

[MessagePackObject(AllowPrivate = true)]
internal sealed class UserSnapshotDto
{
    [Key(0)]
    public string UserId { get; set; } = string.Empty;

    [Key(1)]
    public string UserPasswd { get; set; } = string.Empty;

    [Key(2)]
    public int AuthMode { get; set; }

    [Key(3)]
    public PrivilegeSnapshotDto Privilege { get; set; } = new();

    [Key(4)]
    public List<string> Info { get; set; } = [];
}

[MessagePackObject(AllowPrivate = true)]
internal sealed class PrivilegeSnapshotDto
{
    [Key(0)]
    public bool Read { get; set; }

    [Key(1)]
    public bool Write { get; set; }

    [Key(2)]
    public bool Execute { get; set; }
}

[MessagePackObject(AllowPrivate = true)]
internal sealed class DiskOverlaySnapshotDto
{
    [Key(0)]
    public List<OverlayEntrySnapshotDto> Entries { get; set; } = [];

    [Key(1)]
    public List<string> Tombstones { get; set; } = [];
}

[MessagePackObject(AllowPrivate = true)]
internal sealed class OverlayEntrySnapshotDto
{
    [Key(0)]
    public string Path { get; set; } = string.Empty;

    [Key(1)]
    public int EntryKind { get; set; }

    [Key(2)]
    public int? FileKind { get; set; }

    [Key(3)]
    public long Size { get; set; }

    [Key(4)]
    public string Content { get; set; } = string.Empty;
}

[MessagePackObject(AllowPrivate = true)]
internal sealed class LogSnapshotDto
{
    [Key(0)]
    public int Id { get; set; }

    [Key(1)]
    public long Time { get; set; }

    [Key(2)]
    public string User { get; set; } = string.Empty;

    [Key(3)]
    public string RemoteIp { get; set; } = "127.0.0.1";

    [Key(4)]
    public int ActionType { get; set; }

    [Key(5)]
    public string Action { get; set; } = string.Empty;

    [Key(6)]
    public bool Dirty { get; set; }

    [Key(7)]
    public LogSnapshotDto? Origin { get; set; }
}

[MessagePackObject(AllowPrivate = true)]
internal sealed class PortSnapshotDto
{
    [Key(0)]
    public int PortType { get; set; }

    [Key(1)]
    public string ServiceId { get; set; } = string.Empty;

    [Key(2)]
    public int Exposure { get; set; }
}

[MessagePackObject(AllowPrivate = true)]
internal sealed class DaemonSnapshotDto
{
    [Key(0)]
    public Dictionary<string, SaveValueDto> DaemonArgs { get; set; } = [];
}

internal sealed class RuntimeSaveSnapshot
{
    internal SaveMetaChunkDto SaveMeta { get; init; } = new();

    internal WorldStateChunkDto WorldState { get; init; } = new();

    internal EventStateChunkDto EventState { get; init; } = new();

    internal ProcessStateChunkDto ProcessState { get; init; } = new();

    internal List<ServerStateChunkDto> ServerStates { get; init; } = [];
}
