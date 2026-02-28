using Godot;
using System;
using System.Collections.Generic;
using Uplink2.Blueprint;
using Uplink2.Runtime.Syscalls;
using Uplink2.Vfs;

namespace Uplink2.Runtime;

/// <summary>Global runtime root (register as Autoload).</summary>
public partial class WorldRuntime : Node
{
    private enum InitializationStage
    {
        NotStarted = 0,
        SystemInitializing,
        WorldBuilding,
        Ready,
    }

    private const string InternetNetId = "internet";
    private const string DefaultBlueprintDirectory = "res://scenario_content/campaigns";
    private const string DefaultStartupCampaignId = "gameCampaign";
    private const string DefaultStartupServerNodeId = "startScenario/myWorkstation";
    private const string DefaultMotdFile = "res://scenario_content/resources/text/default_motd.txt";
    private const string DefaultDictionaryPasswordFile = "res://scenario_content/resources/text/leaked_password.txt";
    private const string FallbackDefaultUserId = "player";
    private const string DefaultInternetAddressPlan = "10.255.0.0/16";
    private const uint DefaultHostStart = 10;
    private const string Base64Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/";
    private const string NumSpecialAlphabet = "0123456789!@#$%^&*()";
    private const string LowercaseAlphaNumericAlphabet = "abcdefghijklmnopqrstuvwxyz0123456789";
    private static string[] dictionaryPasswordPool = Array.Empty<string>();

    /// <summary>Guards all mutable world state accessed from both the main thread and worker threads.</summary>
    private readonly object _worldStateLock = new object();

    /// <summary>Blueprint YAML directory used when creating a fresh world.</summary>
    [Export]
    public string BlueprintDirectory { get; set; } = DefaultBlueprintDirectory;

    /// <summary>Campaign id used to choose the default startup scenario.</summary>
    [Export]
    public string StartupCampaignId { get; set; } = DefaultStartupCampaignId;

    /// <summary>Optional explicit startup scenario id (overrides campaign selection).</summary>
    [Export]
    public string StartupScenarioId { get; set; } = string.Empty;

    /// <summary>Node id used to select the initial player workstation server.</summary>
    [Export]
    public string StartupServerNodeId { get; set; } = DefaultStartupServerNodeId;

    /// <summary>Password dictionary source file used by AUTO:dictionary policy.</summary>
    [Export]
    public string DictionaryPasswordFile { get; set; } = DefaultDictionaryPasswordFile;

    /// <summary>Default public userId text used by AUTO:user policy.</summary>
    [Export]
    public string DefaultUserId { get; set; } = FallbackDefaultUserId;

    /// <summary>Enables debug-only runtime features such as DEBUG_* system calls.</summary>
    [Export]
    public bool DebugOption { get; set; }

    /// <summary>Base64-encoded HMAC key used for save/load integrity verification.</summary>
    [Export]
    public string SaveHmacKeyBase64 { get; set; } = "2CYcE8vXmr5koA2cXr2i2Rx5eHa5arQpW//cS2kWJFg=";

    /// <summary>Enables prototype-only terminal save/load commands (`save`, `load`).</summary>
    [Export]
    public bool EnablePrototypeSaveLoadSystemCalls { get; set; } = true;

    /// <summary>Per-world stable seed used for deterministic runtime generation/rendering.</summary>
    public int WorldSeed
    {
        get
        {
            EnsureWorldSeedReadable();
            return worldSeed;
        }

        set => worldSeed = value;
    }

    /// <summary>Global runtime instance.</summary>
    public static WorldRuntime Instance { get; private set; }

    /// <summary>Shared blob store for file payloads.</summary>
    public BlobStore BlobStore { get; private set; } = null!;

    /// <summary>Shared immutable base filesystem image.</summary>
    public BaseFileSystem BaseFileSystem { get; private set; } = null!;

    /// <summary>Loaded blueprint catalog used for world creation.</summary>
    public BlueprintCatalog BlueprintCatalog { get; private set; } = new();

    /// <summary>Scenario id used to instantiate the current runtime world.</summary>
    public string ActiveScenarioId { get; private set; } = string.Empty;

    /// <summary>Global server registry keyed by node id.</summary>
    public Dictionary<string, ServerNodeRuntime> ServerList { get; } = new(StringComparer.Ordinal);

    /// <summary>IP to node id reverse index.</summary>
    public Dictionary<string, string> IpIndex { get; } = new(StringComparer.Ordinal);

    /// <summary>Global process registry keyed by process id.</summary>
    public Dictionary<int, ProcessStruct> ProcessList { get; } = new();

    /// <summary>Visible subnet ids for current player exploration state.</summary>
    public HashSet<string> VisibleNets { get; } = new(StringComparer.Ordinal);

    /// <summary>Known node ids grouped by net id.</summary>
    public Dictionary<string, HashSet<string>> KnownNodesByNet { get; } = new(StringComparer.Ordinal);

    /// <summary>Initial player workstation server instance.</summary>
    public ServerNodeRuntime PlayerWorkstationServer { get; private set; } = null!;

    // System-call processor for command dispatch.
    private SystemCallProcessor systemCallProcessor = null!;

    // Runtime allocators/state.
    private InitializationStage initializationStage = InitializationStage.NotStarted;
    [Export]
    // Serialized backing field keeps inspector editing without invoking guarded getter before initialization.
    private int worldSeed;
    private int nextProcessId = 1;
    private int physicsTicksPerSecond = 60;

    /// <inheritdoc/>
    public override void _EnterTree()
    {
        Instance = this;
    }

    /// <summary>Initializes game systems, then builds initial world runtime from blueprints.</summary>
    public override void _Ready()
    {
        CaptureWorldRuntimeThread();
        physicsTicksPerSecond = Math.Max(1, Engine.PhysicsTicksPerSecond);
        initializationStage = InitializationStage.SystemInitializing;

        BlobStore = new BlobStore();
        BaseFileSystem = new BaseFileSystem(BlobStore);
        BuildBaseOsImage();
        LoadDictionaryPasswordPool();
        InitializeSystemCalls();
        BuildInitialWorldFromBlueprint();
        ValidateWorldSeedForWorldBuild();
        initializationStage = InitializationStage.Ready;
    }

    /// <summary>Validates deterministic world-seed input before initial world build starts.</summary>
    /// <exception cref="InvalidOperationException">Thrown when worldSeed is missing (zero).</exception>
    private void ValidateWorldSeedForWorldBuild()
    {
        if (worldSeed == 0)
        {
            throw new InvalidOperationException("WorldSeed must be non-zero after world build initialization.");
        }
    }

    private void EnsureWorldSeedReadable()
    {
        if (initializationStage != InitializationStage.Ready)
        {
            throw new InvalidOperationException(
                $"WorldSeed cannot be read during initialization stage '{initializationStage}'. WorldSeed is readable only after world initialization is complete.");
        }
    }
}
