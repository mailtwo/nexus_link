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
    }
}
