using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

#nullable enable

namespace Uplink2.Runtime.Workspace;

/// <summary>Builds stored workspace-state models from defaults or live runtime snapshots.</summary>
internal static class WorkspaceStoredStateFactory
{
    /// <summary>Creates the alpha default stored workspace state.</summary>
    internal static WorkspaceStoredState CreateDefaultStoredState(bool includeWorldMapTrace)
    {
        var slots = new Dictionary<DockSlot, WorkspaceStoredDockSlotState>
        {
            [DockSlot.Left] = new(
                DockSlot.Left,
                new ReadOnlyCollection<WorkspacePaneKind>(new List<WorkspacePaneKind>
                {
                    WorkspacePaneKind.Terminal,
                }),
                WorkspacePaneKind.Terminal),
            [DockSlot.RightTop] = new(
                DockSlot.RightTop,
                includeWorldMapTrace
                    ? new ReadOnlyCollection<WorkspacePaneKind>(new List<WorkspacePaneKind>
                    {
                        WorkspacePaneKind.WorldMapTrace,
                    })
                    : Array.Empty<WorkspacePaneKind>(),
                includeWorldMapTrace ? WorkspacePaneKind.WorldMapTrace : null),
            [DockSlot.RightBottom] = new(
                DockSlot.RightBottom,
                Array.Empty<WorkspacePaneKind>(),
                null),
        };

        return new WorkspaceStoredState(
            WorkspaceMode.Docked,
            null,
            WorkspaceStateMachine.DefaultLeftRatio,
            WorkspaceStateMachine.DefaultRightTopRatio,
            new ReadOnlyDictionary<DockSlot, WorkspaceStoredDockSlotState>(slots),
            new ReadOnlyCollection<WorkspacePaneKind>(new List<WorkspacePaneKind>
            {
                WorkspacePaneKind.Terminal,
            }),
            new ReadOnlyDictionary<WorkspacePaneKind, WorkspacePaneStateTable>(
                new Dictionary<WorkspacePaneKind, WorkspacePaneStateTable>()));
    }

    /// <summary>Creates a stored workspace state from the current canonical runtime snapshot.</summary>
    internal static WorkspaceStoredState CreateFromSnapshot(
        WorkspaceStateSnapshot snapshot,
        IReadOnlyDictionary<WorkspacePaneKind, WorkspacePaneStateTable> paneStateByKind)
    {
        if (snapshot is null)
        {
            throw new ArgumentNullException(nameof(snapshot));
        }

        if (paneStateByKind is null)
        {
            throw new ArgumentNullException(nameof(paneStateByKind));
        }

        var storedSlots = new Dictionary<DockSlot, WorkspaceStoredDockSlotState>(snapshot.Slots.Count);
        foreach (var pair in snapshot.Slots)
        {
            storedSlots[pair.Key] = new WorkspaceStoredDockSlotState(
                pair.Key,
                new ReadOnlyCollection<WorkspacePaneKind>(new List<WorkspacePaneKind>(pair.Value.DockStack)),
                pair.Value.ActivePane);
        }

        return new WorkspaceStoredState(
            snapshot.Mode,
            snapshot.MaximizedPane,
            snapshot.LeftRatio,
            snapshot.RightTopRatio,
            new ReadOnlyDictionary<DockSlot, WorkspaceStoredDockSlotState>(storedSlots),
            new ReadOnlyCollection<WorkspacePaneKind>(new List<WorkspacePaneKind>(snapshot.PinnedSet)),
            new ReadOnlyDictionary<WorkspacePaneKind, WorkspacePaneStateTable>(
                new Dictionary<WorkspacePaneKind, WorkspacePaneStateTable>(paneStateByKind)));
    }
}
