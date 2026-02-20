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
    private const string InternetNetId = "internet";
    private const string DefaultBlueprintDirectory = "res://scenario_content/campaigns/prototype";
    private const string DefaultStartupCampaignId = "prototypeCampaign";
    private const string DefaultDictionaryPasswordFile = "res://scenario_content/resources/text/leaked_password.txt";
    private const string DefaultInternetAddressPlan = "10.255.0.0/16";
    private const uint DefaultHostStart = 10;
    private const string Base64Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/";
    private const string LowercaseAlphaNumericAlphabet = "abcdefghijklmnopqrstuvwxyz0123456789";
    private static string[] dictionaryPasswordPool = Array.Empty<string>();

    /// <summary>Blueprint YAML directory used when creating a fresh world.</summary>
    [Export]
    public string BlueprintDirectory { get; set; } = DefaultBlueprintDirectory;

    /// <summary>Campaign id used to choose the default startup scenario.</summary>
    [Export]
    public string StartupCampaignId { get; set; } = DefaultStartupCampaignId;

    /// <summary>Optional explicit startup scenario id (overrides campaign selection).</summary>
    [Export]
    public string StartupScenarioId { get; set; } = string.Empty;

    /// <summary>Password dictionary source file used by AUTO:dictionary policy.</summary>
    [Export]
    public string DictionaryPasswordFile { get; set; } = DefaultDictionaryPasswordFile;

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
        LoadDictionaryPasswordPool();
        BuildInitialWorldFromBlueprint();
        InitializeSystemCalls();
    }
}
