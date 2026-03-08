using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

#nullable enable

namespace Uplink2.Runtime.Workspace.Ui;

/// <summary>Classifies how one pane should be rendered by the current shell renderer slice.</summary>
internal enum WorkspaceRenderedContentKind
{
    /// <summary>No pane is displayed and the slot should render an empty state.</summary>
    Empty = 0,

    /// <summary>The pane has a concrete renderer implementation in this slice.</summary>
    Implemented = 1,

    /// <summary>The pane exists in workspace state but only a placeholder renderer is available.</summary>
    Placeholder = 2,
}

/// <summary>Classifies how a taskbar button should be rendered.</summary>
internal enum WorkspaceTaskbarItemVisualState
{
    /// <summary>The pane is focused and visible.</summary>
    Focused = 0,

    /// <summary>The pane is visible but not focused.</summary>
    VisibleUnfocused = 1,

    /// <summary>The pane is resident but currently hidden.</summary>
    OpenHidden = 2,

    /// <summary>The pane is pinned but currently closed.</summary>
    PinnedClosed = 3,
}

/// <summary>Immutable display model for one taskbar item.</summary>
internal sealed class WorkspaceTaskbarItemDisplayModel
{
    /// <summary>Initializes a new taskbar item display model.</summary>
    internal WorkspaceTaskbarItemDisplayModel(
        WorkspacePaneKind paneKind,
        WorkspaceTaskbarItemVisualState visualState,
        bool isPinned)
    {
        PaneKind = paneKind;
        VisualState = visualState;
        IsPinned = isPinned;
    }

    /// <summary>Gets the pane kind represented by the taskbar item.</summary>
    internal WorkspacePaneKind PaneKind { get; }

    /// <summary>Gets the taskbar visual state.</summary>
    internal WorkspaceTaskbarItemVisualState VisualState { get; }

    /// <summary>Gets whether the item is pinned.</summary>
    internal bool IsPinned { get; }
}

/// <summary>Immutable display model for one dock slot in docked mode.</summary>
internal sealed class WorkspaceSlotDisplayModel
{
    /// <summary>Initializes a new slot display model.</summary>
    internal WorkspaceSlotDisplayModel(
        DockSlot slot,
        string slotTitle,
        IReadOnlyList<WorkspacePaneKind> tabs,
        WorkspacePaneKind? displayedPane,
        WorkspaceRenderedContentKind contentKind)
    {
        Slot = slot;
        SlotTitle = slotTitle ?? throw new ArgumentNullException(nameof(slotTitle));
        Tabs = tabs ?? throw new ArgumentNullException(nameof(tabs));
        DisplayedPane = displayedPane;
        ContentKind = contentKind;
    }

    /// <summary>Gets the slot identifier.</summary>
    internal DockSlot Slot { get; }

    /// <summary>Gets the title used when the slot is empty.</summary>
    internal string SlotTitle { get; }

    /// <summary>Gets the tab kinds displayed in the header.</summary>
    internal IReadOnlyList<WorkspacePaneKind> Tabs { get; }

    /// <summary>Gets the pane currently displayed in the slot, if any.</summary>
    internal WorkspacePaneKind? DisplayedPane { get; }

    /// <summary>Gets the content renderer kind used for the slot body.</summary>
    internal WorkspaceRenderedContentKind ContentKind { get; }
}

/// <summary>Immutable display model for maximized mode.</summary>
internal sealed class WorkspaceMaximizedDisplayModel
{
    /// <summary>Initializes a new maximized display model.</summary>
    internal WorkspaceMaximizedDisplayModel(
        WorkspacePaneKind paneKind,
        WorkspaceRenderedContentKind contentKind)
    {
        PaneKind = paneKind;
        ContentKind = contentKind;
    }

    /// <summary>Gets the maximized pane kind.</summary>
    internal WorkspacePaneKind PaneKind { get; }

    /// <summary>Gets the renderer kind used for the maximized body.</summary>
    internal WorkspaceRenderedContentKind ContentKind { get; }
}

/// <summary>Immutable display model consumed by the shell renderer root.</summary>
internal sealed class WorkspaceDisplayModel
{
    /// <summary>Initializes a new shell display model.</summary>
    internal WorkspaceDisplayModel(
        WorkspaceMode mode,
        WorkspaceLayoutDefinition layoutDefinition,
        IReadOnlyDictionary<DockSlot, WorkspaceSlotDisplayModel> dockedSlots,
        WorkspaceMaximizedDisplayModel? maximizedPane,
        IReadOnlyList<WorkspaceTaskbarItemDisplayModel> taskbarItems)
    {
        Mode = mode;
        LayoutDefinition = layoutDefinition ?? throw new ArgumentNullException(nameof(layoutDefinition));
        DockedSlots = dockedSlots ?? throw new ArgumentNullException(nameof(dockedSlots));
        MaximizedPane = maximizedPane;
        TaskbarItems = taskbarItems ?? throw new ArgumentNullException(nameof(taskbarItems));
    }

    /// <summary>Gets the current workspace mode.</summary>
    internal WorkspaceMode Mode { get; }

    /// <summary>Gets the layout definition used to render the workspace.</summary>
    internal WorkspaceLayoutDefinition LayoutDefinition { get; }

    /// <summary>Gets the docked slot display models keyed by slot.</summary>
    internal IReadOnlyDictionary<DockSlot, WorkspaceSlotDisplayModel> DockedSlots { get; }

    /// <summary>Gets the maximized pane display model, if any.</summary>
    internal WorkspaceMaximizedDisplayModel? MaximizedPane { get; }

    /// <summary>Gets the ordered taskbar item display models.</summary>
    internal IReadOnlyList<WorkspaceTaskbarItemDisplayModel> TaskbarItems { get; }
}

/// <summary>Builds the renderer-facing shell display model from the canonical workspace snapshot.</summary>
internal static class WorkspaceDisplayModelBuilder
{
    /// <summary>Creates the renderer display model for the current workspace snapshot.</summary>
    /// <param name="snapshot">Canonical workspace snapshot.</param>
    /// <param name="layoutDefinition">Layout definition used by the renderer.</param>
    /// <param name="implementedPaneKinds">Pane kinds with concrete renderers in the current slice.</param>
    /// <returns>The renderer-facing display model.</returns>
    internal static WorkspaceDisplayModel Build(
        WorkspaceStateSnapshot snapshot,
        WorkspaceLayoutDefinition layoutDefinition,
        IReadOnlySet<WorkspacePaneKind> implementedPaneKinds)
    {
        if (snapshot is null)
        {
            throw new ArgumentNullException(nameof(snapshot));
        }

        if (layoutDefinition is null)
        {
            throw new ArgumentNullException(nameof(layoutDefinition));
        }

        if (implementedPaneKinds is null)
        {
            throw new ArgumentNullException(nameof(implementedPaneKinds));
        }

        var displayedPaneBySlot = new Dictionary<DockSlot, WorkspacePaneKind?>(layoutDefinition.SlotOrder.Count);
        var visiblePaneKinds = new HashSet<WorkspacePaneKind>();
        foreach (var slot in layoutDefinition.SlotOrder)
        {
            var slotState = snapshot.Slots[slot];
            var displayedPane = ResolveDisplayedPane(snapshot, slotState);
            displayedPaneBySlot[slot] = displayedPane;
            if (displayedPane.HasValue)
            {
                visiblePaneKinds.Add(displayedPane.Value);
            }
        }

        var dockedSlots = new Dictionary<DockSlot, WorkspaceSlotDisplayModel>(layoutDefinition.SlotOrder.Count);
        foreach (var slot in layoutDefinition.SlotOrder)
        {
            var slotState = snapshot.Slots[slot];
            var displayedPane = displayedPaneBySlot[slot];
            IReadOnlyList<WorkspacePaneKind> tabs = displayedPane.HasValue
                ? Array.AsReadOnly(slotState.DockStack.ToArray())
                : Array.Empty<WorkspacePaneKind>();
            var contentKind = ResolveContentKind(displayedPane, implementedPaneKinds);

            dockedSlots[slot] = new WorkspaceSlotDisplayModel(
                slot,
                PaneContentFactory.GetSlotTitle(slot),
                tabs,
                displayedPane,
                contentKind);
        }

        WorkspaceMaximizedDisplayModel? maximizedPane = null;
        if (snapshot.Mode == WorkspaceMode.Maximized)
        {
            if (!snapshot.MaximizedPane.HasValue)
            {
                throw new InvalidOperationException("Renderer cannot build maximized mode without a maximized pane.");
            }

            maximizedPane = new WorkspaceMaximizedDisplayModel(
                snapshot.MaximizedPane.Value,
                ResolveContentKind(snapshot.MaximizedPane, implementedPaneKinds));
            visiblePaneKinds.Clear();
            visiblePaneKinds.Add(snapshot.MaximizedPane.Value);
        }

        var taskbarItems = BuildTaskbarItems(snapshot, layoutDefinition.SlotOrder, visiblePaneKinds);

        return new WorkspaceDisplayModel(
            snapshot.Mode,
            layoutDefinition,
            new ReadOnlyDictionary<DockSlot, WorkspaceSlotDisplayModel>(dockedSlots),
            maximizedPane,
            Array.AsReadOnly(taskbarItems.ToArray()));
    }

    private static WorkspacePaneKind? ResolveDisplayedPane(
        WorkspaceStateSnapshot snapshot,
        WorkspaceDockSlotState slotState)
    {
        if (slotState.ActivePane.HasValue && IsDockedDisplayEligible(snapshot, slotState.ActivePane.Value))
        {
            return slotState.ActivePane.Value;
        }

        foreach (var pane in slotState.DockStack)
        {
            if (IsDockedDisplayEligible(snapshot, pane))
            {
                return pane;
            }
        }

        return null;
    }

    private static bool IsDockedDisplayEligible(WorkspaceStateSnapshot snapshot, WorkspacePaneKind pane)
    {
        if (snapshot.Mode != WorkspaceMode.Docked)
        {
            return false;
        }

        return snapshot.MaximizedPane != pane;
    }

    private static WorkspaceRenderedContentKind ResolveContentKind(
        WorkspacePaneKind? displayedPane,
        IReadOnlySet<WorkspacePaneKind> implementedPaneKinds)
    {
        if (!displayedPane.HasValue)
        {
            return WorkspaceRenderedContentKind.Empty;
        }

        return implementedPaneKinds.Contains(displayedPane.Value)
            ? WorkspaceRenderedContentKind.Implemented
            : WorkspaceRenderedContentKind.Placeholder;
    }

    private static List<WorkspaceTaskbarItemDisplayModel> BuildTaskbarItems(
        WorkspaceStateSnapshot snapshot,
        IReadOnlyList<DockSlot> slotOrder,
        IReadOnlySet<WorkspacePaneKind> visiblePaneKinds)
    {
        var items = new List<WorkspaceTaskbarItemDisplayModel>();
        var residentPaneKinds = new HashSet<WorkspacePaneKind>();
        var emittedPaneKinds = new HashSet<WorkspacePaneKind>();

        foreach (var slot in slotOrder)
        {
            foreach (var pane in snapshot.Slots[slot].DockStack)
            {
                residentPaneKinds.Add(pane);
            }
        }

        foreach (var pane in snapshot.PinnedSet)
        {
            items.Add(CreateTaskbarItem(snapshot, pane, residentPaneKinds, visiblePaneKinds));
            emittedPaneKinds.Add(pane);
        }

        foreach (var slot in slotOrder)
        {
            foreach (var pane in snapshot.Slots[slot].DockStack)
            {
                if (!emittedPaneKinds.Add(pane))
                {
                    continue;
                }

                items.Add(CreateTaskbarItem(snapshot, pane, residentPaneKinds, visiblePaneKinds));
            }
        }

        return items;
    }

    private static WorkspaceTaskbarItemDisplayModel CreateTaskbarItem(
        WorkspaceStateSnapshot snapshot,
        WorkspacePaneKind pane,
        IReadOnlySet<WorkspacePaneKind> residentPaneKinds,
        IReadOnlySet<WorkspacePaneKind> visiblePaneKinds)
    {
        var isPinned = snapshot.PinnedSet.Contains(pane);
        var isResident = residentPaneKinds.Contains(pane);
        var isVisible = visiblePaneKinds.Contains(pane);
        var isFocused = snapshot.FocusedPane == pane && isVisible;

        var visualState = isFocused
            ? WorkspaceTaskbarItemVisualState.Focused
            : isVisible
                ? WorkspaceTaskbarItemVisualState.VisibleUnfocused
                : isResident
                    ? WorkspaceTaskbarItemVisualState.OpenHidden
                    : WorkspaceTaskbarItemVisualState.PinnedClosed;

        return new WorkspaceTaskbarItemDisplayModel(pane, visualState, isPinned);
    }
}
