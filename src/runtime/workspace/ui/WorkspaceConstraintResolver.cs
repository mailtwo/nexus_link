using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using Uplink2.Runtime.Workspace;

#nullable enable

namespace Uplink2.Runtime.Workspace.Ui;

/// <summary>Builds split clamp rules and visible pane constraint state from the current display model.</summary>
internal static class WorkspaceConstraintResolver
{
    /// <summary>Resolves renderer-facing constraint state for the current workspace display model.</summary>
    internal static WorkspaceConstraintSnapshot Resolve(
        WorkspaceDisplayModel displayModel,
        WorkspaceLayoutDefinition layoutDefinition,
        IReadOnlyDictionary<DockSlot, WorkspaceSlotChromeMetrics> slotChromeMetrics)
    {
        if (displayModel is null)
        {
            throw new ArgumentNullException(nameof(displayModel));
        }

        if (layoutDefinition is null)
        {
            throw new ArgumentNullException(nameof(layoutDefinition));
        }

        if (slotChromeMetrics is null)
        {
            throw new ArgumentNullException(nameof(slotChromeMetrics));
        }

        var visiblePaneStates = BuildVisiblePaneStates(displayModel);
        if (displayModel.Mode != WorkspaceMode.Docked)
        {
            return new WorkspaceConstraintSnapshot(
                new ReadOnlyDictionary<WorkspacePaneKind, WorkspacePaneConstraintRenderState>(visiblePaneStates),
                new ReadOnlyDictionary<WorkspaceSplitRatioBinding, WorkspaceSplitClampRule>(
                    new Dictionary<WorkspaceSplitRatioBinding, WorkspaceSplitClampRule>()));
        }

        var splitClampRules = new Dictionary<WorkspaceSplitRatioBinding, WorkspaceSplitClampRule>();
        ResolveSplitClampRules(layoutDefinition.Root, displayModel, slotChromeMetrics, splitClampRules);

        return new WorkspaceConstraintSnapshot(
            new ReadOnlyDictionary<WorkspacePaneKind, WorkspacePaneConstraintRenderState>(visiblePaneStates),
            new ReadOnlyDictionary<WorkspaceSplitRatioBinding, WorkspaceSplitClampRule>(splitClampRules));
    }

    private static Dictionary<WorkspacePaneKind, WorkspacePaneConstraintRenderState> BuildVisiblePaneStates(
        WorkspaceDisplayModel displayModel)
    {
        var visiblePaneStates = new Dictionary<WorkspacePaneKind, WorkspacePaneConstraintRenderState>();
        if (displayModel.Mode == WorkspaceMode.Docked)
        {
            foreach (var slotModel in displayModel.DockedSlots.Values)
            {
                if (!slotModel.DisplayedPane.HasValue)
                {
                    continue;
                }

                AddVisiblePaneState(visiblePaneStates, slotModel.DisplayedPane.Value);
            }

            return visiblePaneStates;
        }

        if (displayModel.MaximizedPane is not null)
        {
            AddVisiblePaneState(visiblePaneStates, displayModel.MaximizedPane.PaneKind);
        }

        return visiblePaneStates;
    }

    private static void AddVisiblePaneState(
        IDictionary<WorkspacePaneKind, WorkspacePaneConstraintRenderState> visiblePaneStates,
        WorkspacePaneKind paneKind)
    {
        if (visiblePaneStates.ContainsKey(paneKind) ||
            !WorkspacePaneConstraintRegistry.TryGetConstraint(paneKind, out var constraint))
        {
            return;
        }

        visiblePaneStates[paneKind] = new WorkspacePaneConstraintRenderState(
            paneKind,
            constraint.MinUsableWidthPx,
            constraint.MinUsableHeightPx,
            constraint.HorizontalResolvePolicy,
            constraint.VerticalResolvePolicy);
    }

    private static void ResolveSplitClampRules(
        WorkspaceLayoutNodeDefinition node,
        WorkspaceDisplayModel displayModel,
        IReadOnlyDictionary<DockSlot, WorkspaceSlotChromeMetrics> slotChromeMetrics,
        IDictionary<WorkspaceSplitRatioBinding, WorkspaceSplitClampRule> splitClampRules)
    {
        if (node is not WorkspaceSplitNodeDefinition splitNode)
        {
            return;
        }

        var firstMinPx = ResolveBranchMinSizePx(
            splitNode.FirstChild,
            splitNode.Axis,
            displayModel,
            slotChromeMetrics);
        var secondMinPx = ResolveBranchMinSizePx(
            splitNode.SecondChild,
            splitNode.Axis,
            displayModel,
            slotChromeMetrics);

        splitClampRules[splitNode.RatioBinding] = new WorkspaceSplitClampRule(
            splitNode.Axis,
            firstMinPx,
            secondMinPx);

        ResolveSplitClampRules(splitNode.FirstChild, displayModel, slotChromeMetrics, splitClampRules);
        ResolveSplitClampRules(splitNode.SecondChild, displayModel, slotChromeMetrics, splitClampRules);
    }

    private static float ResolveBranchMinSizePx(
        WorkspaceLayoutNodeDefinition node,
        WorkspaceSplitAxis axis,
        WorkspaceDisplayModel displayModel,
        IReadOnlyDictionary<DockSlot, WorkspaceSlotChromeMetrics> slotChromeMetrics)
    {
        if (node is WorkspaceSlotLeafDefinition slotLeaf)
        {
            var slotModel = displayModel.DockedSlots[slotLeaf.Slot];
            if (!slotModel.DisplayedPane.HasValue ||
                !WorkspacePaneConstraintRegistry.TryGetConstraint(slotModel.DisplayedPane.Value, out var constraint))
            {
                return 0.0f;
            }

            var resolvePolicy = axis == WorkspaceSplitAxis.Horizontal
                ? constraint.HorizontalResolvePolicy
                : constraint.VerticalResolvePolicy;
            if (resolvePolicy != WorkspaceConstraintResolvePolicy.Clamp)
            {
                return 0.0f;
            }

            var contentMin = axis == WorkspaceSplitAxis.Horizontal
                ? constraint.MinUsableWidthPx
                : constraint.MinUsableHeightPx;
            var chromeMetrics = slotChromeMetrics[slotLeaf.Slot];
            return contentMin + (axis == WorkspaceSplitAxis.Horizontal
                ? chromeMetrics.ExtraWidthPx
                : chromeMetrics.ExtraHeightPx);
        }

        if (node is not WorkspaceSplitNodeDefinition splitNode)
        {
            throw new InvalidOperationException("Unknown workspace layout node.");
        }

        var firstMin = ResolveBranchMinSizePx(splitNode.FirstChild, axis, displayModel, slotChromeMetrics);
        var secondMin = ResolveBranchMinSizePx(splitNode.SecondChild, axis, displayModel, slotChromeMetrics);
        return Math.Max(firstMin, secondMin);
    }
}
