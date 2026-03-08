using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

#nullable enable

namespace Uplink2.Runtime.Workspace;

/// <summary>Represents the current NEXUS Shell workspace presentation mode.</summary>
public enum WorkspaceMode
{
    /// <summary>Renders the three dock slots.</summary>
    Docked = 0,

    /// <summary>Renders a single maximized pane.</summary>
    Maximized = 1,
}

/// <summary>Identifies one of the fixed alpha dock slots.</summary>
public enum DockSlot
{
    /// <summary>The main left column slot.</summary>
    Left = 0,

    /// <summary>The upper slot in the right column.</summary>
    RightTop = 1,

    /// <summary>The lower slot in the right column.</summary>
    RightBottom = 2,
}

/// <summary>Identifies a stage-1 shell pane kind without reusing legacy windowing enums.</summary>
public enum WorkspacePaneKind
{
    /// <summary>The terminal pane.</summary>
    Terminal = 0,

    /// <summary>The embedded web viewer pane.</summary>
    WebViewer = 1,

    /// <summary>The code editor pane.</summary>
    CodeEditor = 2,

    /// <summary>The world map trace pane.</summary>
    WorldMapTrace = 3,

    /// <summary>The mail pane.</summary>
    Mail = 4,

    /// <summary>The mission panel pane.</summary>
    MissionPanel = 5,
}

/// <summary>Immutable snapshot of a single dock slot.</summary>
public sealed class WorkspaceDockSlotState
{
    /// <summary>Initializes a new immutable dock-slot snapshot.</summary>
    /// <param name="slot">The slot identifier.</param>
    /// <param name="dockStack">Resident pane stack for the slot.</param>
    /// <param name="activePane">Currently active pane for the slot, if any.</param>
    public WorkspaceDockSlotState(
        DockSlot slot,
        IReadOnlyList<WorkspacePaneKind> dockStack,
        WorkspacePaneKind? activePane)
    {
        Slot = slot;
        DockStack = dockStack ?? throw new ArgumentNullException(nameof(dockStack));
        ActivePane = activePane;
    }

    /// <summary>Gets the slot identifier.</summary>
    public DockSlot Slot { get; }

    /// <summary>Gets the resident pane stack for the slot.</summary>
    public IReadOnlyList<WorkspacePaneKind> DockStack { get; }

    /// <summary>Gets the active pane for the slot, if any.</summary>
    public WorkspacePaneKind? ActivePane { get; }
}

/// <summary>Immutable snapshot of the minimal stage-1 workspace runtime state.</summary>
public sealed class WorkspaceStateSnapshot
{
    /// <summary>Initializes a new immutable workspace snapshot.</summary>
    /// <param name="mode">Current workspace mode.</param>
    /// <param name="maximizedPane">Stored maximized pane context, if any.</param>
    /// <param name="leftRatio">Normalized left-column ratio.</param>
    /// <param name="rightTopRatio">Normalized right-top ratio.</param>
    /// <param name="slots">Per-slot state snapshots.</param>
    /// <param name="pinnedSet">Pinned pane kinds.</param>
    public WorkspaceStateSnapshot(
        WorkspaceMode mode,
        WorkspacePaneKind? maximizedPane,
        float leftRatio,
        float rightTopRatio,
        IReadOnlyDictionary<DockSlot, WorkspaceDockSlotState> slots,
        IReadOnlyCollection<WorkspacePaneKind> pinnedSet)
    {
        Mode = mode;
        MaximizedPane = maximizedPane;
        LeftRatio = leftRatio;
        RightTopRatio = rightTopRatio;
        Slots = slots ?? throw new ArgumentNullException(nameof(slots));
        PinnedSet = pinnedSet ?? throw new ArgumentNullException(nameof(pinnedSet));
    }

    /// <summary>Gets the current workspace mode.</summary>
    public WorkspaceMode Mode { get; }

    /// <summary>Gets the stored maximized pane context, if any.</summary>
    public WorkspacePaneKind? MaximizedPane { get; }

    /// <summary>Gets the normalized left-column ratio.</summary>
    public float LeftRatio { get; }

    /// <summary>Gets the normalized right-top ratio.</summary>
    public float RightTopRatio { get; }

    /// <summary>Gets the immutable per-slot state snapshots.</summary>
    public IReadOnlyDictionary<DockSlot, WorkspaceDockSlotState> Slots { get; }

    /// <summary>Gets the immutable pinned pane kinds.</summary>
    public IReadOnlyCollection<WorkspacePaneKind> PinnedSet { get; }
}

/// <summary>Opaque pane-local state table that is passed through hydrate without interpretation.</summary>
public sealed class WorkspacePaneStateTable
{
    /// <summary>Initializes a new opaque pane-state table.</summary>
    /// <param name="entries">Opaque pane-local entries.</param>
    public WorkspacePaneStateTable(IReadOnlyDictionary<string, object?> entries)
    {
        if (entries is null)
        {
            throw new ArgumentNullException(nameof(entries));
        }

        Entries = new ReadOnlyDictionary<string, object?>(
            new Dictionary<string, object?>(entries, StringComparer.Ordinal));
    }

    /// <summary>Gets the opaque pane-local entries.</summary>
    public IReadOnlyDictionary<string, object?> Entries { get; }

    /// <summary>Gets the number of entries in the table.</summary>
    public int Count => Entries.Count;

    /// <summary>Tries to look up an opaque pane-local entry.</summary>
    public bool TryGetValue(string key, out object? value)
    {
        return Entries.TryGetValue(key, out value);
    }
}

/// <summary>Stored-state representation of a single dock slot prior to sanitize/hydrate.</summary>
public sealed class WorkspaceStoredDockSlotState
{
    /// <summary>Initializes a new stored dock-slot state.</summary>
    /// <param name="slot">The stored slot identifier.</param>
    /// <param name="dockStack">The stored pane stack for the slot.</param>
    /// <param name="activePane">The stored active pane for the slot, if any.</param>
    public WorkspaceStoredDockSlotState(
        DockSlot slot,
        IReadOnlyList<WorkspacePaneKind> dockStack,
        WorkspacePaneKind? activePane)
    {
        Slot = slot;
        DockStack = dockStack ?? throw new ArgumentNullException(nameof(dockStack));
        ActivePane = activePane;
    }

    /// <summary>Gets the slot identifier.</summary>
    public DockSlot Slot { get; }

    /// <summary>Gets the stored pane stack for the slot.</summary>
    public IReadOnlyList<WorkspacePaneKind> DockStack { get; }

    /// <summary>Gets the stored active pane for the slot, if any.</summary>
    public WorkspacePaneKind? ActivePane { get; }
}

/// <summary>Stored workspace state used as input to sanitize/hydrate.</summary>
public sealed class WorkspaceStoredState
{
    /// <summary>Initializes a new stored workspace state.</summary>
    /// <param name="mode">The stored workspace mode.</param>
    /// <param name="maximizedPane">The stored maximized pane context, if any.</param>
    /// <param name="leftRatio">The stored left-column ratio.</param>
    /// <param name="rightTopRatio">The stored right-top ratio.</param>
    /// <param name="slots">The stored slot states, which may omit slots.</param>
    /// <param name="pinnedSet">The stored pinned pane kinds.</param>
    /// <param name="paneStateByKind">The stored opaque pane-local state tables.</param>
    public WorkspaceStoredState(
        WorkspaceMode mode,
        WorkspacePaneKind? maximizedPane,
        float leftRatio,
        float rightTopRatio,
        IReadOnlyDictionary<DockSlot, WorkspaceStoredDockSlotState> slots,
        IReadOnlyCollection<WorkspacePaneKind> pinnedSet,
        IReadOnlyDictionary<WorkspacePaneKind, WorkspacePaneStateTable> paneStateByKind)
    {
        Mode = mode;
        MaximizedPane = maximizedPane;
        LeftRatio = leftRatio;
        RightTopRatio = rightTopRatio;
        Slots = slots ?? throw new ArgumentNullException(nameof(slots));
        PinnedSet = pinnedSet ?? throw new ArgumentNullException(nameof(pinnedSet));
        PaneStateByKind = paneStateByKind ?? throw new ArgumentNullException(nameof(paneStateByKind));
    }

    /// <summary>Gets the stored workspace mode.</summary>
    public WorkspaceMode Mode { get; }

    /// <summary>Gets the stored maximized pane context, if any.</summary>
    public WorkspacePaneKind? MaximizedPane { get; }

    /// <summary>Gets the stored left-column ratio.</summary>
    public float LeftRatio { get; }

    /// <summary>Gets the stored right-top ratio.</summary>
    public float RightTopRatio { get; }

    /// <summary>Gets the stored slot states.</summary>
    public IReadOnlyDictionary<DockSlot, WorkspaceStoredDockSlotState> Slots { get; }

    /// <summary>Gets the stored pinned pane kinds.</summary>
    public IReadOnlyCollection<WorkspacePaneKind> PinnedSet { get; }

    /// <summary>Gets the stored opaque pane-local state tables.</summary>
    public IReadOnlyDictionary<WorkspacePaneKind, WorkspacePaneStateTable> PaneStateByKind { get; }
}

/// <summary>Result of converting stored workspace state into effective runtime state.</summary>
public sealed class WorkspaceHydrationResult
{
    /// <summary>Initializes a new workspace hydration result.</summary>
    /// <param name="effectiveState">The sanitize/hydrate output snapshot.</param>
    /// <param name="restorablePaneStateByKind">Opaque pane-local state tables for available panes only.</param>
    public WorkspaceHydrationResult(
        WorkspaceStateSnapshot effectiveState,
        IReadOnlyDictionary<WorkspacePaneKind, WorkspacePaneStateTable> restorablePaneStateByKind)
    {
        EffectiveState = effectiveState ?? throw new ArgumentNullException(nameof(effectiveState));
        RestorablePaneStateByKind = restorablePaneStateByKind
            ?? throw new ArgumentNullException(nameof(restorablePaneStateByKind));
    }

    /// <summary>Gets the sanitize/hydrate output snapshot.</summary>
    public WorkspaceStateSnapshot EffectiveState { get; }

    /// <summary>Gets opaque pane-local state tables that remain restorable after availability filtering.</summary>
    public IReadOnlyDictionary<WorkspacePaneKind, WorkspacePaneStateTable> RestorablePaneStateByKind { get; }
}
