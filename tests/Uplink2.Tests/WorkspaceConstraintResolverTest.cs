using System.Collections.Generic;
using Uplink2.Runtime.Workspace;
using Uplink2.Runtime.Workspace.Ui;
using Xunit;

#nullable enable

namespace Uplink2.Tests;

/// <summary>Unit tests for shell pane-constraint aggregation and split clamp resolution.</summary>
[Trait("Speed", "fast")]
public sealed class WorkspaceConstraintResolverTest
{
    private static readonly IReadOnlySet<WorkspacePaneKind> ImplementedPaneKinds =
        PaneContentFactory.DefaultImplementedPaneKinds;

    private static readonly IReadOnlyDictionary<DockSlot, WorkspaceSlotChromeMetrics> SlotChromeMetrics =
        new Dictionary<DockSlot, WorkspaceSlotChromeMetrics>
        {
            [DockSlot.Left] = new WorkspaceSlotChromeMetrics(40.0f, 80.0f),
            [DockSlot.RightTop] = new WorkspaceSlotChromeMetrics(40.0f, 80.0f),
            [DockSlot.RightBottom] = new WorkspaceSlotChromeMetrics(40.0f, 80.0f),
        };

    /// <summary>Ensures the terminal constraint matches the alpha contract.</summary>
    [Fact]
    public void Registry_Terminal_ReturnsCanonicalConstraint()
    {
        var found = WorkspacePaneConstraintRegistry.TryGetConstraint(WorkspacePaneKind.Terminal, out var constraint);

        Assert.True(found);
        Assert.Equal(150.0f, constraint.MinUsableWidthPx, 3);
        Assert.Equal(100.0f, constraint.MinUsableHeightPx, 3);
        Assert.Equal(WorkspaceConstraintResolvePolicy.Clamp, constraint.HorizontalResolvePolicy);
        Assert.Equal(WorkspaceConstraintResolvePolicy.Scroll, constraint.VerticalResolvePolicy);
    }

    /// <summary>Ensures the world-map constraint derives threshold size from the reference texture.</summary>
    [Fact]
    public void Registry_WorldMapTrace_UsesReferenceTextureDerivedThreshold()
    {
        var found = WorkspacePaneConstraintRegistry.TryGetConstraint(WorkspacePaneKind.WorldMapTrace, out var constraint);

        Assert.True(found);
        Assert.Equal(205.0f, constraint.MinUsableWidthPx, 3);
        Assert.Equal(103.0f, constraint.MinUsableHeightPx, 3);
        Assert.Equal(WorkspaceConstraintResolvePolicy.Clamp, constraint.HorizontalResolvePolicy);
        Assert.Equal(WorkspaceConstraintResolvePolicy.Clamp, constraint.VerticalResolvePolicy);
    }

    /// <summary>Ensures horizontal split floors use only clamp panes and aggregate each branch by max.</summary>
    [Fact]
    public void Resolve_LeftRightHorizontalFloor_UsesClampParticipantsOnly()
    {
        var displayModel = BuildDisplayModel(CreateSnapshot(
            WorkspaceMode.Docked,
            null,
            new Dictionary<DockSlot, WorkspaceDockSlotState>
            {
                [DockSlot.Left] = CreateSlotState(
                    DockSlot.Left,
                    [WorkspacePaneKind.Terminal],
                    WorkspacePaneKind.Terminal),
                [DockSlot.RightTop] = CreateSlotState(
                    DockSlot.RightTop,
                    [WorkspacePaneKind.WorldMapTrace],
                    WorkspacePaneKind.WorldMapTrace),
                [DockSlot.RightBottom] = CreateSlotState(DockSlot.RightBottom, [], null),
            }));

        var snapshot = WorkspaceConstraintResolver.Resolve(displayModel, WorkspaceLayoutDefinition.CreateAlpha(), SlotChromeMetrics);

        var leftRightRule = snapshot.SplitClampRules[WorkspaceSplitRatioBinding.LeftColumn];
        Assert.Equal(WorkspaceSplitAxis.Horizontal, leftRightRule.Axis);
        Assert.Equal(190.0f, leftRightRule.FirstBranchMinSizePx, 3);
        Assert.Equal(245.0f, leftRightRule.SecondBranchMinSizePx, 3);
    }

    /// <summary>Ensures vertical split floors use the world-map clamp minimum on the top branch.</summary>
    [Fact]
    public void Resolve_RightTopVerticalFloor_UsesClampHeight()
    {
        var displayModel = BuildDisplayModel(CreateSnapshot(
            WorkspaceMode.Docked,
            null,
            new Dictionary<DockSlot, WorkspaceDockSlotState>
            {
                [DockSlot.Left] = CreateSlotState(
                    DockSlot.Left,
                    [WorkspacePaneKind.Terminal],
                    WorkspacePaneKind.Terminal),
                [DockSlot.RightTop] = CreateSlotState(
                    DockSlot.RightTop,
                    [WorkspacePaneKind.WorldMapTrace],
                    WorkspacePaneKind.WorldMapTrace),
                [DockSlot.RightBottom] = CreateSlotState(DockSlot.RightBottom, [], null),
            }));

        var snapshot = WorkspaceConstraintResolver.Resolve(displayModel, WorkspaceLayoutDefinition.CreateAlpha(), SlotChromeMetrics);

        var rightVerticalRule = snapshot.SplitClampRules[WorkspaceSplitRatioBinding.RightTop];
        Assert.Equal(WorkspaceSplitAxis.Vertical, rightVerticalRule.Axis);
        Assert.Equal(183.0f, rightVerticalRule.FirstBranchMinSizePx, 3);
        Assert.Equal(0.0f, rightVerticalRule.SecondBranchMinSizePx, 3);
    }

    /// <summary>Ensures a world-map pane contributes a horizontal clamp floor even when the opposite branch is empty.</summary>
    [Fact]
    public void Resolve_WorldMapHorizontalClamp_ProducesSplitFloor()
    {
        var displayModel = BuildDisplayModel(CreateSnapshot(
            WorkspaceMode.Docked,
            null,
            new Dictionary<DockSlot, WorkspaceDockSlotState>
            {
                [DockSlot.Left] = CreateSlotState(DockSlot.Left, [], null),
                [DockSlot.RightTop] = CreateSlotState(
                    DockSlot.RightTop,
                    [WorkspacePaneKind.WorldMapTrace],
                    WorkspacePaneKind.WorldMapTrace),
                [DockSlot.RightBottom] = CreateSlotState(DockSlot.RightBottom, [], null),
            }));

        var snapshot = WorkspaceConstraintResolver.Resolve(displayModel, WorkspaceLayoutDefinition.CreateAlpha(), SlotChromeMetrics);

        var leftRightRule = snapshot.SplitClampRules[WorkspaceSplitRatioBinding.LeftColumn];
        Assert.Equal(0.0f, leftRightRule.FirstBranchMinSizePx, 3);
        Assert.Equal(245.0f, leftRightRule.SecondBranchMinSizePx, 3);
    }

    /// <summary>Ensures constraint evaluation follows DisplayedPane fallback rather than the stored active pane.</summary>
    [Fact]
    public void Resolve_UsesDisplayedPaneFallback_WhenStoredActivePaneIsExcluded()
    {
        var displayModel = BuildDisplayModel(CreateSnapshot(
            WorkspaceMode.Docked,
            WorkspacePaneKind.Terminal,
            new Dictionary<DockSlot, WorkspaceDockSlotState>
            {
                [DockSlot.Left] = CreateSlotState(
                    DockSlot.Left,
                    [WorkspacePaneKind.Terminal, WorkspacePaneKind.WebViewer],
                    WorkspacePaneKind.Terminal),
                [DockSlot.RightTop] = CreateSlotState(DockSlot.RightTop, [], null),
                [DockSlot.RightBottom] = CreateSlotState(DockSlot.RightBottom, [], null),
            }));

        var snapshot = WorkspaceConstraintResolver.Resolve(displayModel, WorkspaceLayoutDefinition.CreateAlpha(), SlotChromeMetrics);

        Assert.Equal(WorkspacePaneKind.WebViewer, displayModel.DockedSlots[DockSlot.Left].DisplayedPane);
        var leftRightRule = snapshot.SplitClampRules[WorkspaceSplitRatioBinding.LeftColumn];
        Assert.Equal(0.0f, leftRightRule.FirstBranchMinSizePx, 3);
    }

    private static WorkspaceDisplayModel BuildDisplayModel(WorkspaceStateSnapshot snapshot)
    {
        return WorkspaceDisplayModelBuilder.Build(
            snapshot,
            WorkspaceLayoutDefinition.CreateAlpha(),
            ImplementedPaneKinds,
            ImplementedPaneKinds);
    }

    private static WorkspaceStateSnapshot CreateSnapshot(
        WorkspaceMode mode,
        WorkspacePaneKind? maximizedPane,
        IReadOnlyDictionary<DockSlot, WorkspaceDockSlotState> slots)
    {
        return new WorkspaceStateSnapshot(
            mode,
            maximizedPane,
            WorkspaceStateMachine.DefaultLeftRatio,
            WorkspaceStateMachine.DefaultRightTopRatio,
            slots,
            [WorkspacePaneKind.Terminal],
            WorkspacePaneKind.Terminal);
    }

    private static WorkspaceDockSlotState CreateSlotState(
        DockSlot slot,
        IReadOnlyList<WorkspacePaneKind> stack,
        WorkspacePaneKind? activePane)
    {
        return new WorkspaceDockSlotState(slot, stack, activePane);
    }
}
