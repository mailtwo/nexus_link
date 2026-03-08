using System;
using System.Collections.Generic;
using Uplink2.Runtime.Workspace;
using Xunit;

#nullable enable

namespace Uplink2.Tests;

/// <summary>Unit tests for workspace stored-state sanitize/hydrate behavior.</summary>
[Trait("Speed", "fast")]
public sealed class WorkspaceStateHydratorTest
{
    /// <summary>Ensures missing slots hydrate to empty fixed slots.</summary>
    [Fact]
    public void Hydrate_FillsMissingSlotsWithEmptyState()
    {
        var storedState = CreateStoredState(
            WorkspaceMode.Docked,
            null,
            0.42f,
            0.55f,
            new Dictionary<DockSlot, WorkspaceStoredDockSlotState>
            {
                [DockSlot.Left] = CreateStoredSlotState(
                    DockSlot.Left,
                    [WorkspacePaneKind.Terminal],
                    WorkspacePaneKind.Terminal),
            },
            [WorkspacePaneKind.Terminal],
            new Dictionary<WorkspacePaneKind, WorkspacePaneStateTable>());

        var result = WorkspaceStateHydrator.Hydrate(
            storedState,
            new HashSet<WorkspacePaneKind> { WorkspacePaneKind.Terminal });

        Assert.Equal(3, result.EffectiveState.Slots.Count);
        Assert.Empty(result.EffectiveState.Slots[DockSlot.RightTop].DockStack);
        Assert.Empty(result.EffectiveState.Slots[DockSlot.RightBottom].DockStack);
        Assert.Null(result.EffectiveState.Slots[DockSlot.RightTop].ActivePane);
        Assert.Null(result.EffectiveState.Slots[DockSlot.RightBottom].ActivePane);
    }

    /// <summary>Ensures unavailable panes are removed and active falls back to the first eligible pane.</summary>
    [Fact]
    public void Hydrate_RemovesUnavailablePanes_AndFallsBackActiveToFirstEligiblePane()
    {
        var storedState = CreateStoredState(
            WorkspaceMode.Docked,
            null,
            0.42f,
            0.55f,
            new Dictionary<DockSlot, WorkspaceStoredDockSlotState>
            {
                [DockSlot.Left] = CreateStoredSlotState(
                    DockSlot.Left,
                    [WorkspacePaneKind.WebViewer, WorkspacePaneKind.Terminal, WorkspacePaneKind.Mail],
                    WorkspacePaneKind.WebViewer),
            },
            [WorkspacePaneKind.Terminal],
            new Dictionary<WorkspacePaneKind, WorkspacePaneStateTable>());

        var result = WorkspaceStateHydrator.Hydrate(
            storedState,
            new HashSet<WorkspacePaneKind> { WorkspacePaneKind.Terminal, WorkspacePaneKind.Mail });

        Assert.Equal(
            new[] { WorkspacePaneKind.Terminal, WorkspacePaneKind.Mail },
            result.EffectiveState.Slots[DockSlot.Left].DockStack);
        Assert.Equal(WorkspacePaneKind.Terminal, result.EffectiveState.Slots[DockSlot.Left].ActivePane);
    }

    /// <summary>Ensures duplicate panes keep the first global occurrence and drop later duplicates.</summary>
    [Fact]
    public void Hydrate_RemovesDuplicatePanes_UsingFirstOccurrenceWins()
    {
        var storedState = CreateStoredState(
            WorkspaceMode.Docked,
            null,
            0.42f,
            0.55f,
            new Dictionary<DockSlot, WorkspaceStoredDockSlotState>
            {
                [DockSlot.Left] = CreateStoredSlotState(
                    DockSlot.Left,
                    [WorkspacePaneKind.WorldMapTrace],
                    WorkspacePaneKind.WorldMapTrace),
                [DockSlot.RightTop] = CreateStoredSlotState(
                    DockSlot.RightTop,
                    [WorkspacePaneKind.WorldMapTrace, WorkspacePaneKind.Mail],
                    WorkspacePaneKind.WorldMapTrace),
            },
            [WorkspacePaneKind.Terminal],
            new Dictionary<WorkspacePaneKind, WorkspacePaneStateTable>());

        var result = WorkspaceStateHydrator.Hydrate(
            storedState,
            new HashSet<WorkspacePaneKind> { WorkspacePaneKind.WorldMapTrace, WorkspacePaneKind.Mail });

        Assert.Equal(
            new[] { WorkspacePaneKind.WorldMapTrace },
            result.EffectiveState.Slots[DockSlot.Left].DockStack);
        Assert.Equal(
            new[] { WorkspacePaneKind.Mail },
            result.EffectiveState.Slots[DockSlot.RightTop].DockStack);
        Assert.Equal(WorkspacePaneKind.Mail, result.EffectiveState.Slots[DockSlot.RightTop].ActivePane);
    }

    /// <summary>Ensures invalid or unavailable maximized panes fall back to docked mode with no maximized context.</summary>
    [Fact]
    public void Hydrate_InvalidMaximizedPane_FallsBackToDockedWithNullMaximizedPane()
    {
        var storedState = CreateStoredState(
            WorkspaceMode.Maximized,
            WorkspacePaneKind.WebViewer,
            0.42f,
            0.55f,
            new Dictionary<DockSlot, WorkspaceStoredDockSlotState>
            {
                [DockSlot.Left] = CreateStoredSlotState(
                    DockSlot.Left,
                    [WorkspacePaneKind.Terminal],
                    WorkspacePaneKind.Terminal),
            },
            [WorkspacePaneKind.Terminal],
            new Dictionary<WorkspacePaneKind, WorkspacePaneStateTable>());

        var result = WorkspaceStateHydrator.Hydrate(
            storedState,
            new HashSet<WorkspacePaneKind> { WorkspacePaneKind.Terminal });

        Assert.Equal(WorkspaceMode.Docked, result.EffectiveState.Mode);
        Assert.Null(result.EffectiveState.MaximizedPane);
    }

    /// <summary>Ensures invalid split ratios fall back to workspace defaults.</summary>
    [Fact]
    public void Hydrate_InvalidRatios_FallBackToWorkspaceDefaults()
    {
        var storedState = CreateStoredState(
            WorkspaceMode.Docked,
            null,
            float.NaN,
            1.20f,
            new Dictionary<DockSlot, WorkspaceStoredDockSlotState>(),
            [WorkspacePaneKind.Terminal],
            new Dictionary<WorkspacePaneKind, WorkspacePaneStateTable>());

        var result = WorkspaceStateHydrator.Hydrate(
            storedState,
            new HashSet<WorkspacePaneKind> { WorkspacePaneKind.Terminal });

        Assert.Equal(WorkspaceStateMachine.DefaultLeftRatio, result.EffectiveState.LeftRatio, 3);
        Assert.Equal(WorkspaceStateMachine.DefaultRightTopRatio, result.EffectiveState.RightTopRatio, 3);
    }

    /// <summary>Ensures unavailable pins stay in the effective preference set while unavailable pane state tables are excluded.</summary>
    [Fact]
    public void Hydrate_LeavesUnavailablePinsInEffectivePinnedSet_ButFiltersUnavailablePaneStateTables()
    {
        var terminalPaneState = CreatePaneStateTable(("tab", "shell"));
        var webPaneState = CreatePaneStateTable(("url", "https://example.invalid"));
        var storedState = CreateStoredState(
            WorkspaceMode.Docked,
            null,
            0.42f,
            0.55f,
            new Dictionary<DockSlot, WorkspaceStoredDockSlotState>
            {
                [DockSlot.Left] = CreateStoredSlotState(
                    DockSlot.Left,
                    [WorkspacePaneKind.Terminal, WorkspacePaneKind.WebViewer],
                    WorkspacePaneKind.Terminal),
            },
            [WorkspacePaneKind.Terminal, WorkspacePaneKind.WebViewer],
            new Dictionary<WorkspacePaneKind, WorkspacePaneStateTable>
            {
                [WorkspacePaneKind.Terminal] = terminalPaneState,
                [WorkspacePaneKind.WebViewer] = webPaneState,
            });

        var result = WorkspaceStateHydrator.Hydrate(
            storedState,
            new HashSet<WorkspacePaneKind> { WorkspacePaneKind.Terminal });

        Assert.Contains(WorkspacePaneKind.WebViewer, result.EffectiveState.PinnedSet);
        Assert.True(result.RestorablePaneStateByKind.ContainsKey(WorkspacePaneKind.Terminal));
        Assert.False(result.RestorablePaneStateByKind.ContainsKey(WorkspacePaneKind.WebViewer));
        Assert.Same(terminalPaneState, result.RestorablePaneStateByKind[WorkspacePaneKind.Terminal]);
    }

    /// <summary>Ensures an invalid stored active pane that is not in the stack falls back to the first eligible pane.</summary>
    [Fact]
    public void Hydrate_StoredActivePaneOutsideStack_FallsBackToFirstEligiblePane()
    {
        var storedState = CreateStoredState(
            WorkspaceMode.Docked,
            null,
            0.42f,
            0.55f,
            new Dictionary<DockSlot, WorkspaceStoredDockSlotState>
            {
                [DockSlot.RightBottom] = CreateStoredSlotState(
                    DockSlot.RightBottom,
                    [WorkspacePaneKind.Mail, WorkspacePaneKind.MissionPanel],
                    WorkspacePaneKind.Terminal),
            },
            [WorkspacePaneKind.Terminal],
            new Dictionary<WorkspacePaneKind, WorkspacePaneStateTable>());

        var result = WorkspaceStateHydrator.Hydrate(
            storedState,
            new HashSet<WorkspacePaneKind> { WorkspacePaneKind.Mail, WorkspacePaneKind.MissionPanel });

        Assert.Equal(WorkspacePaneKind.Mail, result.EffectiveState.Slots[DockSlot.RightBottom].ActivePane);
    }

    private static WorkspaceStoredDockSlotState CreateStoredSlotState(
        DockSlot slot,
        IReadOnlyList<WorkspacePaneKind> stack,
        WorkspacePaneKind? activePane)
    {
        return new WorkspaceStoredDockSlotState(slot, stack, activePane);
    }

    private static WorkspaceStoredState CreateStoredState(
        WorkspaceMode mode,
        WorkspacePaneKind? maximizedPane,
        float leftRatio,
        float rightTopRatio,
        IReadOnlyDictionary<DockSlot, WorkspaceStoredDockSlotState> slots,
        IReadOnlyCollection<WorkspacePaneKind> pinnedSet,
        IReadOnlyDictionary<WorkspacePaneKind, WorkspacePaneStateTable> paneStateByKind)
    {
        return new WorkspaceStoredState(
            mode,
            maximizedPane,
            leftRatio,
            rightTopRatio,
            slots,
            pinnedSet,
            paneStateByKind);
    }

    private static WorkspacePaneStateTable CreatePaneStateTable(params (string Key, object? Value)[] entries)
    {
        var table = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var (key, value) in entries)
        {
            table[key] = value;
        }

        return new WorkspacePaneStateTable(table);
    }
}
