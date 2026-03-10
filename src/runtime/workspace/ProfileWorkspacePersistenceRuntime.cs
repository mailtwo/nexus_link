using Godot;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Uplink2.Runtime.Workspace.Ui;

#nullable enable

namespace Uplink2.Runtime.Workspace;

/// <summary>Autoload runtime that owns profile/workspace TOML persistence outside gameplay save slots.</summary>
public partial class ProfileWorkspacePersistenceRuntime : Node
{
    private const string ProfileVirtualPath = "user://profile.toml";

    private WorldRuntime? worldRuntime;
    private ShellWorkspaceRuntime? workspaceRuntime;
    private WorkspacePaneStateBridge? paneStateBridge;
    private ProfileState currentProfileState = ProfileState.CreateDefault(includeDevelopmentWorldMapTrace: false);
    private HashSet<WorkspacePaneKind> currentAvailablePaneKinds = new();
    private bool suppressSaves;
    private bool hasPendingSave;

    /// <summary>Gets the current autoload instance.</summary>
    public static ProfileWorkspacePersistenceRuntime? Instance { get; private set; }

    internal event Action? AvailablePaneKindsChanged;

    /// <inheritdoc/>
    public override void _EnterTree()
    {
        Instance = this;
    }

    /// <inheritdoc/>
    public override void _Ready()
    {
        worldRuntime = WorldRuntime.Instance;
        workspaceRuntime = ShellWorkspaceRuntime.Instance;
        if (workspaceRuntime is null)
        {
            GD.PushError("ProfileWorkspacePersistenceRuntime: '/root/ShellWorkspaceRuntime' not found.");
            return;
        }

        workspaceRuntime.StateChanged += OnWorkspaceStateChanged;
        if (worldRuntime is not null)
        {
            worldRuntime.SaveSlotLoaded += OnSaveSlotLoaded;
            worldRuntime.ScenarioFlagsChanged += OnScenarioFlagsChanged;
        }

        LoadProfileOrDefault();
    }

    /// <inheritdoc/>
    public override void _ExitTree()
    {
        if (workspaceRuntime is not null)
        {
            workspaceRuntime.StateChanged -= OnWorkspaceStateChanged;
        }

        if (worldRuntime is not null)
        {
            worldRuntime.SaveSlotLoaded -= OnSaveSlotLoaded;
            worldRuntime.ScenarioFlagsChanged -= OnScenarioFlagsChanged;
        }

        if (paneStateBridge is not null)
        {
            paneStateBridge.PaneStateDirty -= OnPaneStateDirty;
        }

        paneStateBridge = null;
        workspaceRuntime = null;
        worldRuntime = null;
        if (ReferenceEquals(Instance, this))
        {
            Instance = null;
        }
    }

    /// <summary>Loads the profile TOML from disk when present, otherwise applies the default profile state.</summary>
    public void LoadProfileOrDefault()
    {
        var includeDevelopmentWorldMapTrace = ShouldIncludeDevelopmentWorldMapTrace();
        var nextProfileState = ProfileState.CreateDefault(includeDevelopmentWorldMapTrace);

        var resolvedPath = ResolveProfilePath();
        if (File.Exists(resolvedPath))
        {
            try
            {
                var toml = File.ReadAllText(resolvedPath, Encoding.UTF8);
                if (ProfileTomlCodec.TryParse(
                        toml,
                        includeDevelopmentWorldMapTrace,
                        out var parsedProfileState,
                        out var errorMessage))
                {
                    nextProfileState = parsedProfileState;
                }
                else
                {
                    GD.PushWarning($"ProfileWorkspacePersistenceRuntime: failed to parse '{ProfileVirtualPath}'. {errorMessage}");
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
            {
                GD.PushWarning($"ProfileWorkspacePersistenceRuntime: failed to read '{ProfileVirtualPath}'. {ex.Message}");
            }
        }

        currentProfileState = nextProfileState;
        hasPendingSave = false;
        ApplyStoredProfileState(currentProfileState, ResolveAvailablePaneKinds(), clearPendingSave: true);
    }

    /// <summary>Saves the current profile state when the in-memory state is marked dirty.</summary>
    public void SaveProfileIfDirty()
    {
        if (suppressSaves || !hasPendingSave || workspaceRuntime is null)
        {
            return;
        }

        var nextProfileState = BuildCurrentProfileState();
        var toml = ProfileTomlCodec.Serialize(nextProfileState);
        if (!TryWriteProfileToml(toml, out var errorMessage))
        {
            GD.PushWarning($"ProfileWorkspacePersistenceRuntime: failed to write '{ProfileVirtualPath}'. {errorMessage}");
            return;
        }

        currentProfileState = nextProfileState;
        hasPendingSave = false;
    }

    /// <summary>Resets options to the doc-16 default reserved-table state and persists the result immediately.</summary>
    public void ResetOptions()
    {
        currentProfileState = new ProfileState(
            ProfileState.CurrentVersion,
            ProfileOptionsState.CreateDefault(),
            currentProfileState.WorkspaceState);
        MarkDirtyAndSave();
    }

    /// <summary>Resets workspace layout and pane-local state to the alpha default and persists the result immediately.</summary>
    public void ResetWorkspaceLayout()
    {
        currentProfileState = new ProfileState(
            ProfileState.CurrentVersion,
            currentProfileState.Options,
            WorkspaceStoredStateFactory.CreateDefaultStoredState(includeWorldMapTrace: false));
        ApplyStoredProfileState(currentProfileState, ResolveAvailablePaneKinds(), clearPendingSave: true);
        MarkDirtyAndSave();
    }

    /// <summary>Resets both options and workspace state to defaults and persists the result immediately.</summary>
    public void ResetAllProfileState()
    {
        currentProfileState = ProfileState.CreateDefault(includeDevelopmentWorldMapTrace: false);
        ApplyStoredProfileState(currentProfileState, ResolveAvailablePaneKinds(), clearPendingSave: true);
        MarkDirtyAndSave();
    }

    internal IReadOnlySet<WorkspacePaneKind> GetAvailablePaneKinds()
    {
        return currentAvailablePaneKinds;
    }

    internal void RegisterPaneStateBridge(WorkspacePaneStateBridge bridge)
    {
        if (bridge is null)
        {
            throw new ArgumentNullException(nameof(bridge));
        }

        if (ReferenceEquals(paneStateBridge, bridge))
        {
            return;
        }

        if (paneStateBridge is not null)
        {
            paneStateBridge.PaneStateDirty -= OnPaneStateDirty;
        }

        paneStateBridge = bridge;
        paneStateBridge.PaneStateDirty += OnPaneStateDirty;
        paneStateBridge.ReplaceRestorablePaneStateTables(
            FilterRestorablePaneStateTables(currentProfileState.WorkspaceState.PaneStateByKind, currentAvailablePaneKinds),
            currentAvailablePaneKinds);
    }

    internal void UnregisterPaneStateBridge(WorkspacePaneStateBridge bridge)
    {
        if (bridge is null || !ReferenceEquals(paneStateBridge, bridge))
        {
            return;
        }

        paneStateBridge.PaneStateDirty -= OnPaneStateDirty;
        paneStateBridge = null;
    }

    private void OnWorkspaceStateChanged()
    {
        MarkDirtyAndSave();
    }

    private void OnPaneStateDirty()
    {
        MarkDirtyAndSave();
    }

    private void OnSaveSlotLoaded(string savePath)
    {
        _ = savePath;
        ApplyStoredProfileState(currentProfileState, ResolveAvailablePaneKinds(), clearPendingSave: true);
    }

    private void OnScenarioFlagsChanged()
    {
        RefreshAvailabilityAgainstCurrentRuntimeState();
    }

    private ProfileState BuildCurrentProfileState()
    {
        if (workspaceRuntime is null)
        {
            return currentProfileState;
        }

        var snapshot = workspaceRuntime.GetSnapshot();
        var existingAvailablePaneState = FilterRestorablePaneStateTables(
            currentProfileState.WorkspaceState.PaneStateByKind,
            currentAvailablePaneKinds);
        var capturedAvailablePaneState = paneStateBridge is null
            ? new Dictionary<WorkspacePaneKind, WorkspacePaneStateTable>(existingAvailablePaneState)
            : paneStateBridge.CaptureAvailablePaneStateTables(currentAvailablePaneKinds, existingAvailablePaneState);

        var mergedPaneStateByKind = new Dictionary<WorkspacePaneKind, WorkspacePaneStateTable>(
            currentProfileState.WorkspaceState.PaneStateByKind);
        foreach (var paneKind in currentAvailablePaneKinds)
        {
            if (capturedAvailablePaneState.TryGetValue(paneKind, out var paneStateTable))
            {
                mergedPaneStateByKind[paneKind] = paneStateTable;
            }
            else
            {
                mergedPaneStateByKind.Remove(paneKind);
            }
        }

        var storedWorkspaceState = WorkspaceStoredStateFactory.CreateFromSnapshot(snapshot, mergedPaneStateByKind);
        return new ProfileState(ProfileState.CurrentVersion, currentProfileState.Options, storedWorkspaceState);
    }

    private void RefreshAvailabilityAgainstCurrentRuntimeState()
    {
        if (workspaceRuntime is null)
        {
            return;
        }

        var nextAvailablePaneKinds = ResolveAvailablePaneKinds();
        var availabilityChanged = !currentAvailablePaneKinds.SetEquals(nextAvailablePaneKinds);
        if (!availabilityChanged)
        {
            return;
        }

        var currentWorkspaceStoredState = WorkspaceStoredStateFactory.CreateFromSnapshot(
            workspaceRuntime.GetSnapshot(),
            currentProfileState.WorkspaceState.PaneStateByKind);
        ApplyHydrationResult(
            WorkspaceStateHydrator.Hydrate(currentWorkspaceStoredState, nextAvailablePaneKinds),
            nextAvailablePaneKinds,
            clearPendingSave: true);
        MarkDirtyAndSave();
    }

    private void ApplyStoredProfileState(
        ProfileState profileState,
        HashSet<WorkspacePaneKind> availablePaneKinds,
        bool clearPendingSave)
    {
        ApplyHydrationResult(
            WorkspaceStateHydrator.Hydrate(profileState.WorkspaceState, availablePaneKinds),
            availablePaneKinds,
            clearPendingSave);
    }

    private void ApplyHydrationResult(
        WorkspaceHydrationResult hydrationResult,
        HashSet<WorkspacePaneKind> availablePaneKinds,
        bool clearPendingSave)
    {
        if (workspaceRuntime is null)
        {
            return;
        }

        suppressSaves = true;
        try
        {
            var availabilityChanged = !currentAvailablePaneKinds.SetEquals(availablePaneKinds);
            currentAvailablePaneKinds = availablePaneKinds;
            _ = workspaceRuntime.ReplaceState(hydrationResult.EffectiveState);
            paneStateBridge?.ReplaceRestorablePaneStateTables(
                hydrationResult.RestorablePaneStateByKind,
                currentAvailablePaneKinds);
            if (availabilityChanged)
            {
                AvailablePaneKindsChanged?.Invoke();
            }

            if (clearPendingSave)
            {
                hasPendingSave = false;
            }
        }
        finally
        {
            suppressSaves = false;
        }
    }

    private HashSet<WorkspacePaneKind> ResolveAvailablePaneKinds()
    {
        if (worldRuntime is null)
        {
            return new HashSet<WorkspacePaneKind>();
        }

        return WorkspacePaneAvailabilityResolver.Resolve(
            worldRuntime.ScenarioFlags,
            PaneContentFactory.DefaultImplementedPaneKinds);
    }

    private static Dictionary<WorkspacePaneKind, WorkspacePaneStateTable> FilterRestorablePaneStateTables(
        IReadOnlyDictionary<WorkspacePaneKind, WorkspacePaneStateTable> paneStateByKind,
        IReadOnlySet<WorkspacePaneKind> availablePaneKinds)
    {
        var result = new Dictionary<WorkspacePaneKind, WorkspacePaneStateTable>();
        foreach (var pair in paneStateByKind)
        {
            if (!availablePaneKinds.Contains(pair.Key))
            {
                continue;
            }

            result[pair.Key] = pair.Value;
        }

        return result;
    }

    private void MarkDirtyAndSave()
    {
        if (suppressSaves)
        {
            return;
        }

        hasPendingSave = true;
        SaveProfileIfDirty();
    }

    private static string ResolveProfilePath()
    {
        return ProjectSettings.GlobalizePath(ProfileVirtualPath);
    }

    private static bool TryWriteProfileToml(string toml, out string errorMessage)
    {
        errorMessage = string.Empty;
        var resolvedPath = ResolveProfilePath();
        var tempPath = resolvedPath + ".tmp";

        try
        {
            var parentDirectory = Path.GetDirectoryName(resolvedPath);
            if (!string.IsNullOrWhiteSpace(parentDirectory))
            {
                Directory.CreateDirectory(parentDirectory);
            }

            File.WriteAllText(tempPath, toml, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            if (File.Exists(resolvedPath))
            {
                File.Replace(tempPath, resolvedPath, null);
            }
            else
            {
                File.Move(tempPath, resolvedPath);
            }

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            errorMessage = ex.Message;
            try
            {
                if (File.Exists(tempPath))
                {
                    File.Delete(tempPath);
                }
            }
            catch
            {
                // Best-effort cleanup only.
            }

            return false;
        }
    }

    private bool ShouldIncludeDevelopmentWorldMapTrace()
    {
        return worldRuntime?.DebugOption == true && worldRuntime.IsNexusShellOpen;
    }
}
