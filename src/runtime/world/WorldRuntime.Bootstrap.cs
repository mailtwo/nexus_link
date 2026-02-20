using Godot;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Uplink2.Blueprint;
using Uplink2.Vfs;

namespace Uplink2.Runtime;

public partial class WorldRuntime
{
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

    /// <summary>Builds a fresh runtime world from blueprint YAML files.</summary>
    private void BuildInitialWorldFromBlueprint()
    {
        ResetRuntimeState();

        BlueprintCatalog = LoadBlueprintCatalog();
        var scenario = ResolveStartupScenario(BlueprintCatalog);
        ActiveScenarioId = scenario.ScenarioId;

        var spawnByNodeId = BuildSpawnIndex(scenario);
        var assignedInterfacesByNodeId = AllocateScenarioInterfaces(scenario, spawnByNodeId);
        var serversByNodeId = BuildServerRuntimes(scenario, spawnByNodeId, assignedInterfacesByNodeId);

        BuildLanNeighborCache(serversByNodeId, scenario, assignedInterfacesByNodeId);
        InitializeVisibilityState(serversByNodeId, assignedInterfacesByNodeId);

        foreach (var server in serversByNodeId.Values.OrderBy(static server => server.NodeId, StringComparer.Ordinal))
        {
            RegisterServer(server);
        }

        PlayerWorkstationServer = ResolvePlayerWorkstation(serversByNodeId.Values);
        GD.Print($"WorldRuntime initialized scenario '{ActiveScenarioId}' ({ServerList.Count} servers).");
    }

    /// <summary>Loads dictionary password pool from configured resource file.</summary>
    private void LoadDictionaryPasswordPool()
    {
        var dictionaryFile = string.IsNullOrWhiteSpace(DictionaryPasswordFile)
            ? DefaultDictionaryPasswordFile
            : DictionaryPasswordFile.Trim();
        var absolutePath = ProjectSettings.GlobalizePath(dictionaryFile);
        if (!File.Exists(absolutePath))
        {
            throw new FileNotFoundException($"Dictionary password file not found: {dictionaryFile}", absolutePath);
        }

        var loadedPool = File.ReadLines(absolutePath, Encoding.UTF8)
            .Select(static line => line.Trim())
            .Where(static line => !string.IsNullOrWhiteSpace(line))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        if (loadedPool.Length == 0)
        {
            throw new InvalidDataException(
                $"Dictionary password file '{dictionaryFile}' must contain at least one non-empty line.");
        }

        dictionaryPasswordPool = loadedPool;
    }

    /// <summary>Loads all blueprint YAML documents from configured directory.</summary>
    private BlueprintCatalog LoadBlueprintCatalog()
    {
        var directory = string.IsNullOrWhiteSpace(BlueprintDirectory)
            ? DefaultBlueprintDirectory
            : BlueprintDirectory.Trim();
        var absoluteDirectory = ProjectSettings.GlobalizePath(directory);
        var yamlReader = new BlueprintYamlReader();
        return yamlReader.ReadDirectory(absoluteDirectory, "*.yaml", SearchOption.TopDirectoryOnly);
    }

    /// <summary>Resolves startup scenario from explicit scenario id or configured campaign.</summary>
    private ScenarioBlueprint ResolveStartupScenario(BlueprintCatalog catalog)
    {
        if (!string.IsNullOrWhiteSpace(StartupScenarioId))
        {
            if (catalog.Scenarios.TryGetValue(StartupScenarioId, out var scenario))
            {
                return scenario;
            }

            throw new InvalidDataException($"Startup scenario '{StartupScenarioId}' was not found in loaded blueprints.");
        }

        if (!string.IsNullOrWhiteSpace(StartupCampaignId))
        {
            if (catalog.Campaigns.TryGetValue(StartupCampaignId, out var campaign))
            {
                foreach (var scenarioId in campaign.Scenarios)
                {
                    if (catalog.Scenarios.TryGetValue(scenarioId, out var campaignScenario))
                    {
                        return campaignScenario;
                    }
                }

                GD.PushWarning($"Campaign '{StartupCampaignId}' does not contain a resolvable scenario. Falling back to first scenario.");
            }
            else
            {
                GD.PushWarning($"Campaign '{StartupCampaignId}' was not found. Falling back to first scenario.");
            }
        }

        if (catalog.Scenarios.Count == 0)
        {
            throw new InvalidDataException("No scenarios were loaded from blueprint YAML files.");
        }

        var firstScenarioId = catalog.Scenarios.Keys.OrderBy(static key => key, StringComparer.Ordinal).First();
        return catalog.Scenarios[firstScenarioId];
    }

    /// <summary>Builds node-id index from scenario server spawns.</summary>
    private static Dictionary<string, ServerSpawnBlueprint> BuildSpawnIndex(ScenarioBlueprint scenario)
    {
        var spawnByNodeId = new Dictionary<string, ServerSpawnBlueprint>(StringComparer.Ordinal);
        foreach (var spawn in scenario.Servers)
        {
            if (!spawnByNodeId.TryAdd(spawn.NodeId, spawn))
            {
                throw new InvalidDataException($"Duplicate nodeId '{spawn.NodeId}' in scenario '{scenario.ScenarioId}'.");
            }
        }

        return spawnByNodeId;
    }

    /// <summary>Resets mutable world containers before rebuilding runtime from blueprint.</summary>
    private void ResetRuntimeState()
    {
        ServerList.Clear();
        IpIndex.Clear();
        ProcessList.Clear();
        VisibleNets.Clear();
        KnownNodesByNet.Clear();
        ActiveScenarioId = string.Empty;
        nextProcessId = 1;
    }

}
