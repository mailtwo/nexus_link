using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

#nullable enable

namespace Uplink2.Runtime.Workspace;

/// <summary>Converts stored workspace preferences into an effective runtime snapshot.</summary>
public static class WorkspaceStateHydrator
{
    private static readonly DockSlot[] SlotOrder =
    {
        DockSlot.Left,
        DockSlot.RightTop,
        DockSlot.RightBottom,
    };

    /// <summary>Sanitizes stored state against current availability and produces the effective runtime state.</summary>
    /// <param name="storedState">Stored workspace preferences to sanitize.</param>
    /// <param name="availablePaneKinds">Pane kinds currently available in the loaded world/save context.</param>
    /// <returns>The effective workspace snapshot plus restorable pane-local state tables.</returns>
    public static WorkspaceHydrationResult Hydrate(
        WorkspaceStoredState storedState,
        IReadOnlySet<WorkspacePaneKind> availablePaneKinds)
    {
        if (storedState is null)
        {
            throw new ArgumentNullException(nameof(storedState));
        }

        if (availablePaneKinds is null)
        {
            throw new ArgumentNullException(nameof(availablePaneKinds));
        }

        var residentPanes = new HashSet<WorkspacePaneKind>();
        var effectiveSlots = new Dictionary<DockSlot, WorkspaceDockSlotState>(SlotOrder.Length);

        foreach (var slot in SlotOrder)
        {
            storedState.Slots.TryGetValue(slot, out var storedSlotState);
            var storedStack = storedSlotState?.DockStack ?? Array.Empty<WorkspacePaneKind>();
            var sanitizedStack = SanitizeStack(storedStack, availablePaneKinds, residentPanes);
            var activePane = ResolveActivePane(storedSlotState?.ActivePane, sanitizedStack);

            effectiveSlots.Add(
                slot,
                new WorkspaceDockSlotState(
                    slot,
                    Array.AsReadOnly(sanitizedStack.ToArray()),
                    activePane));
        }

        var effectiveMaximizedPane = ResolveMaximizedPane(storedState.MaximizedPane, availablePaneKinds, residentPanes);
        var effectiveMode = ResolveMode(storedState.Mode, effectiveMaximizedPane);
        var effectivePinnedSet = SanitizePinnedSet(storedState.PinnedSet);
        var restorablePaneStateByKind = FilterRestorablePaneStateTables(storedState.PaneStateByKind, availablePaneKinds);

        var effectiveState = new WorkspaceStateSnapshot(
            effectiveMode,
            effectiveMaximizedPane,
            NormalizeRatio(storedState.LeftRatio, WorkspaceStateMachine.DefaultLeftRatio),
            NormalizeRatio(storedState.RightTopRatio, WorkspaceStateMachine.DefaultRightTopRatio),
            new ReadOnlyDictionary<DockSlot, WorkspaceDockSlotState>(effectiveSlots),
            Array.AsReadOnly(effectivePinnedSet),
            focusedPane: null);

        return new WorkspaceHydrationResult(
            effectiveState,
            new ReadOnlyDictionary<WorkspacePaneKind, WorkspacePaneStateTable>(restorablePaneStateByKind));
    }

    private static List<WorkspacePaneKind> SanitizeStack(
        IReadOnlyList<WorkspacePaneKind> storedStack,
        IReadOnlySet<WorkspacePaneKind> availablePaneKinds,
        HashSet<WorkspacePaneKind> residentPanes)
    {
        var sanitized = new List<WorkspacePaneKind>(storedStack.Count);
        foreach (var pane in storedStack)
        {
            if (!availablePaneKinds.Contains(pane))
            {
                continue;
            }

            if (!residentPanes.Add(pane))
            {
                continue;
            }

            sanitized.Add(pane);
        }

        return sanitized;
    }

    private static WorkspacePaneKind? ResolveActivePane(
        WorkspacePaneKind? storedActivePane,
        IReadOnlyList<WorkspacePaneKind> sanitizedStack)
    {
        if (storedActivePane.HasValue && sanitizedStack.Contains(storedActivePane.Value))
        {
            return storedActivePane.Value;
        }

        return sanitizedStack.Count > 0 ? sanitizedStack[0] : null;
    }

    private static WorkspacePaneKind? ResolveMaximizedPane(
        WorkspacePaneKind? storedMaximizedPane,
        IReadOnlySet<WorkspacePaneKind> availablePaneKinds,
        IReadOnlySet<WorkspacePaneKind> residentPanes)
    {
        if (!storedMaximizedPane.HasValue)
        {
            return null;
        }

        var pane = storedMaximizedPane.Value;
        if (!availablePaneKinds.Contains(pane) || !residentPanes.Contains(pane))
        {
            return null;
        }

        return pane;
    }

    private static WorkspaceMode ResolveMode(
        WorkspaceMode storedMode,
        WorkspacePaneKind? effectiveMaximizedPane)
    {
        if (storedMode == WorkspaceMode.Maximized && effectiveMaximizedPane.HasValue)
        {
            return WorkspaceMode.Maximized;
        }

        return WorkspaceMode.Docked;
    }

    private static WorkspacePaneKind[] SanitizePinnedSet(IReadOnlyList<WorkspacePaneKind> storedPinnedSet)
    {
        var seen = new HashSet<WorkspacePaneKind>();
        var ordered = new List<WorkspacePaneKind>(storedPinnedSet.Count);
        foreach (var pane in storedPinnedSet)
        {
            if (!seen.Add(pane))
            {
                continue;
            }

            ordered.Add(pane);
        }

        return ordered.ToArray();
    }

    private static Dictionary<WorkspacePaneKind, WorkspacePaneStateTable> FilterRestorablePaneStateTables(
        IReadOnlyDictionary<WorkspacePaneKind, WorkspacePaneStateTable> storedPaneStateByKind,
        IReadOnlySet<WorkspacePaneKind> availablePaneKinds)
    {
        var result = new Dictionary<WorkspacePaneKind, WorkspacePaneStateTable>();
        foreach (var pair in storedPaneStateByKind)
        {
            if (!availablePaneKinds.Contains(pair.Key))
            {
                continue;
            }

            result[pair.Key] = pair.Value;
        }

        return result;
    }

    private static float NormalizeRatio(float value, float fallback)
    {
        return float.IsFinite(value) && value > 0.0f && value < 1.0f
            ? value
            : fallback;
    }
}
