using System.Collections.Generic;
using Uplink2.Runtime.Workspace;
using Uplink2.Runtime.Workspace.Ui;
using Xunit;

#nullable enable

namespace Uplink2.Tests;

/// <summary>Unit tests for the shell renderer display-model builder.</summary>
[Trait("Speed", "fast")]
public sealed class WorkspaceDisplayModelBuilderTest
{
    private static readonly IReadOnlySet<WorkspacePaneKind> ImplementedPaneKinds =
        new HashSet<WorkspacePaneKind>
        {
            WorkspacePaneKind.Terminal,
        };

    /// <summary>Ensures the alpha layout definition keeps the three-slot split tree.</summary>
    [Fact]
    public void CreateAlpha_DefinesStableThreeSlotTree()
    {
        var definition = WorkspaceLayoutDefinition.CreateAlpha();

        Assert.Equal(new[] { DockSlot.Left, DockSlot.RightTop, DockSlot.RightBottom }, definition.SlotOrder);
        var rootSplit = Assert.IsType<WorkspaceSplitNodeDefinition>(definition.Root);
        Assert.Equal(WorkspaceSplitAxis.Horizontal, rootSplit.Axis);
        Assert.Equal(WorkspaceSplitRatioBinding.LeftColumn, rootSplit.RatioBinding);
        Assert.Equal(DockSlot.Left, Assert.IsType<WorkspaceSlotLeafDefinition>(rootSplit.FirstChild).Slot);

        var rightSplit = Assert.IsType<WorkspaceSplitNodeDefinition>(rootSplit.SecondChild);
        Assert.Equal(WorkspaceSplitAxis.Vertical, rightSplit.Axis);
        Assert.Equal(WorkspaceSplitRatioBinding.RightTop, rightSplit.RatioBinding);
        Assert.Equal(DockSlot.RightTop, Assert.IsType<WorkspaceSlotLeafDefinition>(rightSplit.FirstChild).Slot);
        Assert.Equal(DockSlot.RightBottom, Assert.IsType<WorkspaceSlotLeafDefinition>(rightSplit.SecondChild).Slot);
    }

    /// <summary>Ensures bootstrap state renders terminal on the left and empty right slots.</summary>
    [Fact]
    public void Build_BootstrapState_RendersTerminalAndEmptySlots()
    {
        var snapshot = new WorkspaceStateMachine().GetSnapshot();

        var displayModel = WorkspaceDisplayModelBuilder.Build(snapshot, WorkspaceLayoutDefinition.CreateAlpha(), ImplementedPaneKinds);

        Assert.Equal(WorkspaceMode.Docked, displayModel.Mode);
        Assert.Equal(WorkspacePaneKind.Terminal, displayModel.DockedSlots[DockSlot.Left].DisplayedPane);
        Assert.Equal(WorkspaceRenderedContentKind.Implemented, displayModel.DockedSlots[DockSlot.Left].ContentKind);
        Assert.Null(displayModel.DockedSlots[DockSlot.RightTop].DisplayedPane);
        Assert.Equal(WorkspaceRenderedContentKind.Empty, displayModel.DockedSlots[DockSlot.RightTop].ContentKind);
        Assert.Null(displayModel.DockedSlots[DockSlot.RightBottom].DisplayedPane);
        Assert.Equal(WorkspaceRenderedContentKind.Empty, displayModel.DockedSlots[DockSlot.RightBottom].ContentKind);
    }

    /// <summary>Ensures docked mode without maximized context displays the stored active pane.</summary>
    [Fact]
    public void Build_DockedWithoutMaximized_UsesActivePane()
    {
        var snapshot = CreateSnapshot(
            WorkspaceMode.Docked,
            null,
            new Dictionary<DockSlot, WorkspaceDockSlotState>
            {
                [DockSlot.Left] = CreateSlotState(
                    DockSlot.Left,
                    [WorkspacePaneKind.Terminal, WorkspacePaneKind.WebViewer],
                    WorkspacePaneKind.WebViewer),
                [DockSlot.RightTop] = CreateSlotState(DockSlot.RightTop, [], null),
                [DockSlot.RightBottom] = CreateSlotState(DockSlot.RightBottom, [], null),
            });

        var displayModel = WorkspaceDisplayModelBuilder.Build(snapshot, WorkspaceLayoutDefinition.CreateAlpha(), ImplementedPaneKinds);

        Assert.Equal(WorkspacePaneKind.WebViewer, displayModel.DockedSlots[DockSlot.Left].DisplayedPane);
        Assert.Equal(WorkspaceRenderedContentKind.Placeholder, displayModel.DockedSlots[DockSlot.Left].ContentKind);
    }

    /// <summary>Ensures docked mode excludes the stored maximized pane and falls back to empty when no alternative exists.</summary>
    [Fact]
    public void Build_DockedWithStoredMaximizedPane_ExcludesMaximizedPaneFromSlotDisplay()
    {
        var snapshot = CreateSnapshot(
            WorkspaceMode.Docked,
            WorkspacePaneKind.Terminal,
            new Dictionary<DockSlot, WorkspaceDockSlotState>
            {
                [DockSlot.Left] = CreateSlotState(
                    DockSlot.Left,
                    [WorkspacePaneKind.Terminal],
                    WorkspacePaneKind.Terminal),
                [DockSlot.RightTop] = CreateSlotState(DockSlot.RightTop, [], null),
                [DockSlot.RightBottom] = CreateSlotState(DockSlot.RightBottom, [], null),
            });

        var displayModel = WorkspaceDisplayModelBuilder.Build(snapshot, WorkspaceLayoutDefinition.CreateAlpha(), ImplementedPaneKinds);

        Assert.Null(displayModel.DockedSlots[DockSlot.Left].DisplayedPane);
        Assert.Equal(WorkspaceRenderedContentKind.Empty, displayModel.DockedSlots[DockSlot.Left].ContentKind);
        Assert.Empty(displayModel.DockedSlots[DockSlot.Left].Tabs);
    }

    /// <summary>Ensures unsupported panes still render as placeholders rather than empty slots.</summary>
    [Fact]
    public void Build_UnsupportedPane_RendersPlaceholder()
    {
        var snapshot = CreateSnapshot(
            WorkspaceMode.Docked,
            null,
            new Dictionary<DockSlot, WorkspaceDockSlotState>
            {
                [DockSlot.Left] = CreateSlotState(
                    DockSlot.Left,
                    [WorkspacePaneKind.WebViewer],
                    WorkspacePaneKind.WebViewer),
                [DockSlot.RightTop] = CreateSlotState(DockSlot.RightTop, [], null),
                [DockSlot.RightBottom] = CreateSlotState(DockSlot.RightBottom, [], null),
            });

        var displayModel = WorkspaceDisplayModelBuilder.Build(snapshot, WorkspaceLayoutDefinition.CreateAlpha(), ImplementedPaneKinds);

        Assert.Equal(WorkspacePaneKind.WebViewer, displayModel.DockedSlots[DockSlot.Left].DisplayedPane);
        Assert.Equal(WorkspaceRenderedContentKind.Placeholder, displayModel.DockedSlots[DockSlot.Left].ContentKind);
    }

    /// <summary>Ensures mixed implemented and unsupported stacks still choose the correct display pane.</summary>
    [Fact]
    public void Build_MixedStack_UsesFallbackDisplayRulesConsistently()
    {
        var snapshot = CreateSnapshot(
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
            });

        var displayModel = WorkspaceDisplayModelBuilder.Build(snapshot, WorkspaceLayoutDefinition.CreateAlpha(), ImplementedPaneKinds);

        Assert.Equal(WorkspacePaneKind.WebViewer, displayModel.DockedSlots[DockSlot.Left].DisplayedPane);
        Assert.Equal(WorkspaceRenderedContentKind.Placeholder, displayModel.DockedSlots[DockSlot.Left].ContentKind);
        Assert.Equal(new[] { WorkspacePaneKind.Terminal, WorkspacePaneKind.WebViewer }, displayModel.DockedSlots[DockSlot.Left].Tabs);
    }

    /// <summary>Ensures maximized mode produces an implemented renderer model for terminal and placeholders for unsupported panes.</summary>
    [Fact]
    public void Build_MaximizedMode_RendersImplementedAndPlaceholderKinds()
    {
        var terminalSnapshot = CreateSnapshot(
            WorkspaceMode.Maximized,
            WorkspacePaneKind.Terminal,
            new Dictionary<DockSlot, WorkspaceDockSlotState>
            {
                [DockSlot.Left] = CreateSlotState(
                    DockSlot.Left,
                    [WorkspacePaneKind.Terminal],
                    WorkspacePaneKind.Terminal),
                [DockSlot.RightTop] = CreateSlotState(DockSlot.RightTop, [], null),
                [DockSlot.RightBottom] = CreateSlotState(DockSlot.RightBottom, [], null),
            });

        var terminalDisplay = WorkspaceDisplayModelBuilder.Build(
            terminalSnapshot,
            WorkspaceLayoutDefinition.CreateAlpha(),
            ImplementedPaneKinds);

        Assert.NotNull(terminalDisplay.MaximizedPane);
        Assert.Equal(WorkspaceRenderedContentKind.Implemented, terminalDisplay.MaximizedPane!.ContentKind);

        var placeholderSnapshot = CreateSnapshot(
            WorkspaceMode.Maximized,
            WorkspacePaneKind.WorldMapTrace,
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
            });

        var placeholderDisplay = WorkspaceDisplayModelBuilder.Build(
            placeholderSnapshot,
            WorkspaceLayoutDefinition.CreateAlpha(),
            ImplementedPaneKinds);

        Assert.NotNull(placeholderDisplay.MaximizedPane);
        Assert.Equal(WorkspaceRenderedContentKind.Placeholder, placeholderDisplay.MaximizedPane!.ContentKind);
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
            new[] { WorkspacePaneKind.Terminal });
    }

    private static WorkspaceDockSlotState CreateSlotState(
        DockSlot slot,
        IReadOnlyList<WorkspacePaneKind> stack,
        WorkspacePaneKind? activePane)
    {
        return new WorkspaceDockSlotState(slot, stack, activePane);
    }
}
