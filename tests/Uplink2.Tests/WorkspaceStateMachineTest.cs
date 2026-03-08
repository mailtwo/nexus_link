using System;
using System.Collections.Generic;
using System.Linq;
using Uplink2.Runtime.Workspace;
using Xunit;

#nullable enable

namespace Uplink2.Tests;

/// <summary>Unit tests for the stage-1 NEXUS Shell workspace state machine.</summary>
[Trait("Speed", "fast")]
public sealed class WorkspaceStateMachineTest
{
    /// <summary>Ensures the default bootstrap state is terminal-only with three fixed slots.</summary>
    [Fact]
    public void Constructor_CreatesDefaultTerminalOnlyBootstrapState()
    {
        var stateMachine = new WorkspaceStateMachine();

        var snapshot = stateMachine.GetSnapshot();
        Assert.Equal(WorkspaceMode.Docked, snapshot.Mode);
        Assert.Null(snapshot.MaximizedPane);
        Assert.Equal(0.42f, snapshot.LeftRatio, 3);
        Assert.Equal(0.55f, snapshot.RightTopRatio, 3);
        Assert.Equal(3, snapshot.Slots.Count);
        Assert.Equal(new[] { WorkspacePaneKind.Terminal }, snapshot.Slots[DockSlot.Left].DockStack);
        Assert.Equal(WorkspacePaneKind.Terminal, snapshot.Slots[DockSlot.Left].ActivePane);
        Assert.Empty(snapshot.Slots[DockSlot.RightTop].DockStack);
        Assert.Empty(snapshot.Slots[DockSlot.RightBottom].DockStack);
        Assert.Equal(new[] { WorkspacePaneKind.Terminal }, snapshot.PinnedSet);
        Assert.Equal(WorkspacePaneKind.Terminal, snapshot.FocusedPane);
    }

    /// <summary>Ensures activating a closed pane opens it in its home slot.</summary>
    [Fact]
    public void ActivatePane_OpensClosedPaneInHomeSlotAndMakesItActive()
    {
        var stateMachine = new WorkspaceStateMachine();

        var changed = stateMachine.ActivatePane(WorkspacePaneKind.WorldMapTrace);

        Assert.True(changed);
        var snapshot = stateMachine.GetSnapshot();
        Assert.Equal(new[] { WorkspacePaneKind.WorldMapTrace }, snapshot.Slots[DockSlot.RightTop].DockStack);
        Assert.Equal(WorkspacePaneKind.WorldMapTrace, snapshot.Slots[DockSlot.RightTop].ActivePane);
        Assert.Equal(WorkspacePaneKind.WorldMapTrace, snapshot.FocusedPane);
    }

    /// <summary>Ensures repeated activation does not duplicate resident panes.</summary>
    [Fact]
    public void ActivatePane_RepeatedActivationDoesNotDuplicatePane()
    {
        var stateMachine = new WorkspaceStateMachine();
        _ = stateMachine.ActivatePane(WorkspacePaneKind.WorldMapTrace);

        var changed = stateMachine.ActivatePane(WorkspacePaneKind.WorldMapTrace);
        var snapshot = stateMachine.GetSnapshot();

        Assert.False(changed);
        Assert.Single(snapshot.Slots[DockSlot.RightTop].DockStack);
    }

    /// <summary>Ensures moving a pane changes its slot without duplicating it.</summary>
    [Fact]
    public void MovePane_MovesResidentPaneBetweenSlotsWithoutDuplication()
    {
        var stateMachine = new WorkspaceStateMachine();
        _ = stateMachine.ActivatePane(WorkspacePaneKind.WorldMapTrace);

        var changed = stateMachine.MovePane(WorkspacePaneKind.WorldMapTrace, DockSlot.Left);

        Assert.True(changed);
        var snapshot = stateMachine.GetSnapshot();
        Assert.Empty(snapshot.Slots[DockSlot.RightTop].DockStack);
        Assert.Equal(
            new[] { WorkspacePaneKind.Terminal, WorkspacePaneKind.WorldMapTrace },
            snapshot.Slots[DockSlot.Left].DockStack);
        Assert.Equal(WorkspacePaneKind.WorldMapTrace, snapshot.Slots[DockSlot.Left].ActivePane);
        Assert.Equal(WorkspacePaneKind.WorldMapTrace, snapshot.FocusedPane);
        AssertSingleResidence(snapshot, WorkspacePaneKind.WorldMapTrace);
    }

    /// <summary>Ensures closing an active pane falls back to the next eligible pane in the same stack.</summary>
    [Fact]
    public void ClosePane_RemovesActivePaneAndFallsBackWithinSlot()
    {
        var stateMachine = new WorkspaceStateMachine();
        _ = stateMachine.ActivatePane(WorkspacePaneKind.WebViewer);

        var changed = stateMachine.ClosePane(WorkspacePaneKind.Terminal);

        Assert.True(changed);
        var snapshot = stateMachine.GetSnapshot();
        Assert.Equal(new[] { WorkspacePaneKind.WebViewer }, snapshot.Slots[DockSlot.Left].DockStack);
        Assert.Equal(WorkspacePaneKind.WebViewer, snapshot.Slots[DockSlot.Left].ActivePane);
        Assert.Equal(WorkspacePaneKind.WebViewer, snapshot.FocusedPane);
    }

    /// <summary>Ensures taskbar-style activation preserves maximized context when switching away and back.</summary>
    [Fact]
    public void ActivatePane_PreservesAndRestoresStoredMaximizedPaneContext()
    {
        var stateMachine = new WorkspaceStateMachine();
        _ = stateMachine.ActivatePane(WorkspacePaneKind.WorldMapTrace);
        _ = stateMachine.MaximizePane(WorkspacePaneKind.Terminal);

        var switched = stateMachine.ActivatePane(WorkspacePaneKind.WorldMapTrace);
        var switchedSnapshot = stateMachine.GetSnapshot();
        var restored = stateMachine.ActivatePane(WorkspacePaneKind.Terminal);
        var restoredSnapshot = stateMachine.GetSnapshot();

        Assert.True(switched);
        Assert.Equal(WorkspaceMode.Docked, switchedSnapshot.Mode);
        Assert.Equal(WorkspacePaneKind.Terminal, switchedSnapshot.MaximizedPane);
        Assert.True(restored);
        Assert.Equal(WorkspaceMode.Maximized, restoredSnapshot.Mode);
        Assert.Equal(WorkspacePaneKind.Terminal, restoredSnapshot.MaximizedPane);
        Assert.Equal(WorkspacePaneKind.Terminal, restoredSnapshot.FocusedPane);
    }

    /// <summary>Ensures restoring docked mode clears the stored maximized pane context.</summary>
    [Fact]
    public void RestoreDocked_ClearsStoredMaximizedPane()
    {
        var stateMachine = new WorkspaceStateMachine();
        _ = stateMachine.MaximizePane(WorkspacePaneKind.Terminal);

        var changed = stateMachine.RestoreDocked();

        Assert.True(changed);
        var snapshot = stateMachine.GetSnapshot();
        Assert.Equal(WorkspaceMode.Docked, snapshot.Mode);
        Assert.Null(snapshot.MaximizedPane);
        Assert.Equal(WorkspacePaneKind.Terminal, snapshot.FocusedPane);
    }

    /// <summary>Ensures focus can move between already visible panes without changing layout.</summary>
    [Fact]
    public void FocusPane_ChangesFocusedVisiblePaneWithoutChangingResidency()
    {
        var stateMachine = new WorkspaceStateMachine();
        _ = stateMachine.ActivatePane(WorkspacePaneKind.WorldMapTrace);

        var changed = stateMachine.FocusPane(WorkspacePaneKind.Terminal);

        Assert.True(changed);
        var snapshot = stateMachine.GetSnapshot();
        Assert.Equal(WorkspacePaneKind.Terminal, snapshot.FocusedPane);
        Assert.Equal(new[] { WorkspacePaneKind.WorldMapTrace }, snapshot.Slots[DockSlot.RightTop].DockStack);
    }

    /// <summary>Ensures closing the focused pane clears focus when no visible fallback remains.</summary>
    [Fact]
    public void ClosePane_FocusedPaneWithoutFallback_ClearsFocus()
    {
        var stateMachine = new WorkspaceStateMachine();
        _ = stateMachine.ActivatePane(WorkspacePaneKind.WorldMapTrace);

        var changed = stateMachine.ClosePane(WorkspacePaneKind.WorldMapTrace);

        Assert.True(changed);
        var snapshot = stateMachine.GetSnapshot();
        Assert.Null(snapshot.FocusedPane);
        Assert.Empty(snapshot.Slots[DockSlot.RightTop].DockStack);
    }

    /// <summary>Ensures pinned panes preserve insertion order and re-pin does not reorder existing entries.</summary>
    [Fact]
    public void PinPane_PreservesInsertionOrder_AndUnpinRemovesEntry()
    {
        var stateMachine = new WorkspaceStateMachine();

        Assert.True(stateMachine.PinPane(WorkspacePaneKind.Mail));
        Assert.True(stateMachine.PinPane(WorkspacePaneKind.WorldMapTrace));
        Assert.False(stateMachine.PinPane(WorkspacePaneKind.Mail));
        Assert.True(stateMachine.UnpinPane(WorkspacePaneKind.Mail));

        var snapshot = stateMachine.GetSnapshot();
        Assert.Equal(
            new[] { WorkspacePaneKind.Terminal, WorkspacePaneKind.WorldMapTrace },
            snapshot.PinnedSet);
    }

    /// <summary>Ensures current dock-slot lookup succeeds only for resident panes.</summary>
    [Fact]
    public void TryGetCurrentDockSlot_ReturnsTrueOnlyForResidentPanes()
    {
        var stateMachine = new WorkspaceStateMachine();

        Assert.True(stateMachine.TryGetCurrentDockSlot(WorkspacePaneKind.Terminal, out var terminalSlot));
        Assert.Equal(DockSlot.Left, terminalSlot);
        Assert.False(stateMachine.TryGetCurrentDockSlot(WorkspacePaneKind.Mail, out _));
    }

    /// <summary>Ensures invalid ratio updates are rejected and valid updates are accepted.</summary>
    [Fact]
    public void SetSplitRatios_AcceptsOnlyStrictlyNormalizedFiniteValues()
    {
        var stateMachine = new WorkspaceStateMachine();

        Assert.False(stateMachine.SetSplitRatios(0.0f, 0.55f));
        Assert.False(stateMachine.SetSplitRatios(0.42f, 1.0f));
        Assert.False(stateMachine.SetSplitRatios(float.NaN, 0.55f));
        Assert.True(stateMachine.SetSplitRatios(0.30f, 0.70f));

        var snapshot = stateMachine.GetSnapshot();
        Assert.Equal(0.30f, snapshot.LeftRatio, 3);
        Assert.Equal(0.70f, snapshot.RightTopRatio, 3);
    }

    /// <summary>Ensures representative mutations preserve the documented invariants.</summary>
    [Fact]
    public void RepresentativeMutationSequence_PreservesWorkspaceInvariants()
    {
        var stateMachine = new WorkspaceStateMachine();

        _ = stateMachine.PinPane(WorkspacePaneKind.Mail);
        _ = stateMachine.ActivatePane(WorkspacePaneKind.WorldMapTrace);
        _ = stateMachine.ActivatePane(WorkspacePaneKind.Mail);
        _ = stateMachine.MovePane(WorkspacePaneKind.Mail, DockSlot.Left);
        _ = stateMachine.MaximizePane(WorkspacePaneKind.Mail);
        _ = stateMachine.ActivatePane(WorkspacePaneKind.WorldMapTrace);
        _ = stateMachine.ClosePane(WorkspacePaneKind.WorldMapTrace);
        _ = stateMachine.RestoreDocked();

        var snapshot = stateMachine.GetSnapshot();
        AssertInvariantHolds(snapshot);
    }

    /// <summary>Ensures a sanitized effective snapshot can replace the current state in one operation.</summary>
    [Fact]
    public void ReplaceState_AppliesValidEffectiveSnapshot()
    {
        var stateMachine = new WorkspaceStateMachine();
        var replacement = CreateSnapshot(
            WorkspaceMode.Docked,
            WorkspacePaneKind.WorldMapTrace,
            0.36f,
            0.64f,
            new Dictionary<DockSlot, WorkspaceDockSlotState>
            {
                [DockSlot.Left] = CreateSlotState(
                    DockSlot.Left,
                    [WorkspacePaneKind.Terminal, WorkspacePaneKind.Mail],
                    WorkspacePaneKind.Terminal),
                [DockSlot.RightTop] = CreateSlotState(
                    DockSlot.RightTop,
                    [WorkspacePaneKind.WorldMapTrace],
                    WorkspacePaneKind.WorldMapTrace),
                [DockSlot.RightBottom] = CreateSlotState(
                    DockSlot.RightBottom,
                    [],
                    null),
            },
            [WorkspacePaneKind.Terminal, WorkspacePaneKind.Mail],
            WorkspacePaneKind.Terminal);

        var changed = stateMachine.ReplaceState(replacement);

        Assert.True(changed);
        AssertSnapshotsEqual(replacement, stateMachine.GetSnapshot());
    }

    /// <summary>Ensures invalid effective snapshots are rejected instead of partially applying.</summary>
    [Fact]
    public void ReplaceState_RejectsDuplicateResidentPaneAcrossSlots()
    {
        var stateMachine = new WorkspaceStateMachine();
        var invalid = CreateSnapshot(
            WorkspaceMode.Docked,
            null,
            0.42f,
            0.55f,
            new Dictionary<DockSlot, WorkspaceDockSlotState>
            {
                [DockSlot.Left] = CreateSlotState(
                    DockSlot.Left,
                    [WorkspacePaneKind.Terminal],
                    WorkspacePaneKind.Terminal),
                [DockSlot.RightTop] = CreateSlotState(
                    DockSlot.RightTop,
                    [WorkspacePaneKind.Terminal],
                    WorkspacePaneKind.Terminal),
                [DockSlot.RightBottom] = CreateSlotState(
                    DockSlot.RightBottom,
                    [],
                    null),
            },
            [WorkspacePaneKind.Terminal]);

        var ex = Assert.Throws<InvalidOperationException>(() => stateMachine.ReplaceState(invalid));

        Assert.Contains("appears in more than one slot", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>Ensures non-resident maximized panes are rejected by ReplaceState.</summary>
    [Fact]
    public void ReplaceState_RejectsNonResidentMaximizedPane()
    {
        var stateMachine = new WorkspaceStateMachine();
        var invalid = CreateSnapshot(
            WorkspaceMode.Maximized,
            WorkspacePaneKind.WorldMapTrace,
            0.42f,
            0.55f,
            new Dictionary<DockSlot, WorkspaceDockSlotState>
            {
                [DockSlot.Left] = CreateSlotState(
                    DockSlot.Left,
                    [WorkspacePaneKind.Terminal],
                    WorkspacePaneKind.Terminal),
                [DockSlot.RightTop] = CreateSlotState(
                    DockSlot.RightTop,
                    [],
                    null),
                [DockSlot.RightBottom] = CreateSlotState(
                    DockSlot.RightBottom,
                    [],
                    null),
            },
            [WorkspacePaneKind.Terminal]);

        var ex = Assert.Throws<InvalidOperationException>(() => stateMachine.ReplaceState(invalid));

        Assert.Contains("must be resident", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>Ensures focused panes must remain visible in docked snapshots.</summary>
    [Fact]
    public void ReplaceState_RejectsFocusedPaneThatIsNotVisible()
    {
        var stateMachine = new WorkspaceStateMachine();
        var invalid = CreateSnapshot(
            WorkspaceMode.Docked,
            null,
            0.42f,
            0.55f,
            new Dictionary<DockSlot, WorkspaceDockSlotState>
            {
                [DockSlot.Left] = CreateSlotState(
                    DockSlot.Left,
                    [WorkspacePaneKind.Terminal, WorkspacePaneKind.WebViewer],
                    WorkspacePaneKind.Terminal),
                [DockSlot.RightTop] = CreateSlotState(
                    DockSlot.RightTop,
                    [],
                    null),
                [DockSlot.RightBottom] = CreateSlotState(
                    DockSlot.RightBottom,
                    [],
                    null),
            },
            [WorkspacePaneKind.Terminal],
            WorkspacePaneKind.WebViewer);

        var ex = Assert.Throws<InvalidOperationException>(() => stateMachine.ReplaceState(invalid));

        Assert.Contains("must be visible", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>Ensures the runtime wrapper exposes the ReplaceState entry point for sanitized snapshots.</summary>
    [Fact]
    public void ShellWorkspaceRuntime_ExposesReplaceStateEntryPoint()
    {
        var method = typeof(ShellWorkspaceRuntime).GetMethod(nameof(ShellWorkspaceRuntime.ReplaceState));

        Assert.NotNull(method);
        Assert.Equal(typeof(bool), method!.ReturnType);
        Assert.Single(method.GetParameters());
        Assert.Equal(typeof(WorkspaceStateSnapshot), method.GetParameters()[0].ParameterType);
    }

    private static void AssertSingleResidence(WorkspaceStateSnapshot snapshot, WorkspacePaneKind kind)
    {
        var occurrences = snapshot.Slots.Values.Sum(slot => slot.DockStack.Count(candidate => candidate == kind));
        Assert.Equal(1, occurrences);
    }

    private static void AssertInvariantHolds(WorkspaceStateSnapshot snapshot)
    {
        var seen = new HashSet<WorkspacePaneKind>();
        foreach (var pair in snapshot.Slots)
        {
            var stack = pair.Value.DockStack;
            foreach (var pane in stack)
            {
                Assert.True(seen.Add(pane), $"Pane '{pane}' should not appear in more than one slot.");
            }

            if (pair.Value.ActivePane.HasValue)
            {
                Assert.Contains(pair.Value.ActivePane.Value, stack);
            }
        }

        if (snapshot.Mode == WorkspaceMode.Maximized)
        {
            Assert.NotNull(snapshot.MaximizedPane);
        }

        if (snapshot.MaximizedPane.HasValue)
        {
            Assert.Contains(snapshot.MaximizedPane.Value, seen);
        }

        if (snapshot.FocusedPane.HasValue)
        {
            Assert.Contains(snapshot.FocusedPane.Value, seen);
            if (snapshot.Mode == WorkspaceMode.Maximized)
            {
                Assert.Equal(snapshot.MaximizedPane, snapshot.FocusedPane);
            }
            else
            {
                var visible = false;
                foreach (var slot in snapshot.Slots.Values)
                {
                    WorkspacePaneKind? displayed = null;
                    if (slot.ActivePane.HasValue && slot.ActivePane != snapshot.MaximizedPane)
                    {
                        displayed = slot.ActivePane.Value;
                    }
                    else
                    {
                        foreach (var pane in slot.DockStack)
                        {
                            if (pane == snapshot.MaximizedPane)
                            {
                                continue;
                            }

                            displayed = pane;
                            break;
                        }
                    }

                    if (displayed == snapshot.FocusedPane)
                    {
                        visible = true;
                        break;
                    }
                }

                Assert.True(visible, $"Focused pane '{snapshot.FocusedPane}' should be visible in docked mode.");
            }
        }
    }

    private static WorkspaceDockSlotState CreateSlotState(
        DockSlot slot,
        IReadOnlyList<WorkspacePaneKind> stack,
        WorkspacePaneKind? activePane)
    {
        return new WorkspaceDockSlotState(slot, stack, activePane);
    }

    private static WorkspaceStateSnapshot CreateSnapshot(
        WorkspaceMode mode,
        WorkspacePaneKind? maximizedPane,
        float leftRatio,
        float rightTopRatio,
        IReadOnlyDictionary<DockSlot, WorkspaceDockSlotState> slots,
        IReadOnlyList<WorkspacePaneKind> pinnedSet,
        WorkspacePaneKind? focusedPane = null)
    {
        return new WorkspaceStateSnapshot(mode, maximizedPane, leftRatio, rightTopRatio, slots, pinnedSet, focusedPane);
    }

    private static void AssertSnapshotsEqual(WorkspaceStateSnapshot expected, WorkspaceStateSnapshot actual)
    {
        Assert.Equal(expected.Mode, actual.Mode);
        Assert.Equal(expected.MaximizedPane, actual.MaximizedPane);
        Assert.Equal(expected.LeftRatio, actual.LeftRatio, 3);
        Assert.Equal(expected.RightTopRatio, actual.RightTopRatio, 3);
        Assert.Equal(expected.FocusedPane, actual.FocusedPane);
        Assert.Equal(expected.PinnedSet, actual.PinnedSet);

        foreach (var slot in expected.Slots.Keys.OrderBy(static value => value))
        {
            Assert.Equal(expected.Slots[slot].ActivePane, actual.Slots[slot].ActivePane);
            Assert.Equal(expected.Slots[slot].DockStack, actual.Slots[slot].DockStack);
        }
    }
}
