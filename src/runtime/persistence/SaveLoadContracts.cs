namespace Uplink2.Runtime;

/// <summary>Error categories for save/load operations.</summary>
public enum SaveLoadErrorCode
{
    None = 0,
    InvalidArgs,
    IoError,
    FormatError,
    UnsupportedVersion,
    IntegrityCheckFailed,
    MissingRequiredChunk,
    ScenarioRestoreFailed,
    StateApplyFailed,
    UnsupportedValueType,
}

/// <summary>Structured result payload for save/load engine APIs.</summary>
public sealed class SaveLoadResult
{
    /// <summary>True when save/load operation succeeded.</summary>
    public bool Ok { get; init; }

    /// <summary>Structured error code for diagnostics and flow control.</summary>
    public SaveLoadErrorCode Code { get; init; }

    /// <summary>Human-readable status or error message.</summary>
    public string Message { get; init; } = string.Empty;

    /// <summary>Resolved absolute save path used by the operation.</summary>
    public string SavePath { get; init; } = string.Empty;
}
