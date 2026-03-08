using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using Uplink2.Runtime.Workspace;

#nullable enable

namespace Uplink2.Runtime.Workspace.Ui;

/// <summary>Describes how a pane resolves one axis after reaching its minimum usable size.</summary>
internal enum WorkspaceConstraintResolvePolicy
{
    /// <summary>The shell split must stop shrinking on this axis.</summary>
    Clamp = 0,

    /// <summary>The pane resolves the axis locally with scroll/overflow handling.</summary>
    Scroll = 1,
}

/// <summary>Canonical pane constraint owned by the workspace layout contract.</summary>
internal sealed class WorkspacePaneConstraint
{
    /// <summary>Initializes a new pane constraint definition.</summary>
    internal WorkspacePaneConstraint(
        float minUsableWidthPx,
        float minUsableHeightPx,
        WorkspaceConstraintResolvePolicy horizontalResolvePolicy,
        WorkspaceConstraintResolvePolicy verticalResolvePolicy)
    {
        if (!float.IsFinite(minUsableWidthPx) || minUsableWidthPx < 0.0f)
        {
            throw new ArgumentOutOfRangeException(nameof(minUsableWidthPx));
        }

        if (!float.IsFinite(minUsableHeightPx) || minUsableHeightPx < 0.0f)
        {
            throw new ArgumentOutOfRangeException(nameof(minUsableHeightPx));
        }

        MinUsableWidthPx = minUsableWidthPx;
        MinUsableHeightPx = minUsableHeightPx;
        HorizontalResolvePolicy = horizontalResolvePolicy;
        VerticalResolvePolicy = verticalResolvePolicy;
    }

    /// <summary>Gets the canonical minimum usable width in pixels.</summary>
    internal float MinUsableWidthPx { get; }

    /// <summary>Gets the canonical minimum usable height in pixels.</summary>
    internal float MinUsableHeightPx { get; }

    /// <summary>Gets the horizontal resolve policy.</summary>
    internal WorkspaceConstraintResolvePolicy HorizontalResolvePolicy { get; }

    /// <summary>Gets the vertical resolve policy.</summary>
    internal WorkspaceConstraintResolvePolicy VerticalResolvePolicy { get; }
}

/// <summary>Renderer-facing constraint state for one currently visible pane.</summary>
internal sealed class WorkspacePaneConstraintRenderState
{
    /// <summary>Initializes a new visible-pane constraint render state.</summary>
    internal WorkspacePaneConstraintRenderState(
        WorkspacePaneKind paneKind,
        float minUsableWidthPx,
        float minUsableHeightPx,
        WorkspaceConstraintResolvePolicy horizontalResolvePolicy,
        WorkspaceConstraintResolvePolicy verticalResolvePolicy)
    {
        PaneKind = paneKind;
        MinUsableWidthPx = minUsableWidthPx;
        MinUsableHeightPx = minUsableHeightPx;
        HorizontalResolvePolicy = horizontalResolvePolicy;
        VerticalResolvePolicy = verticalResolvePolicy;
    }

    /// <summary>Gets the pane kind represented by the render state.</summary>
    internal WorkspacePaneKind PaneKind { get; }

    /// <summary>Gets the canonical minimum usable width in pixels.</summary>
    internal float MinUsableWidthPx { get; }

    /// <summary>Gets the canonical minimum usable height in pixels.</summary>
    internal float MinUsableHeightPx { get; }

    /// <summary>Gets the horizontal resolve policy.</summary>
    internal WorkspaceConstraintResolvePolicy HorizontalResolvePolicy { get; }

    /// <summary>Gets the vertical resolve policy.</summary>
    internal WorkspaceConstraintResolvePolicy VerticalResolvePolicy { get; }
}

/// <summary>Measured shell chrome overhead that wraps one slot's pane content area.</summary>
internal readonly record struct WorkspaceSlotChromeMetrics(
    float ExtraWidthPx,
    float ExtraHeightPx);

/// <summary>Clamp rule for one split after branch constraints are aggregated.</summary>
internal sealed class WorkspaceSplitClampRule
{
    /// <summary>Initializes a new split clamp rule.</summary>
    internal WorkspaceSplitClampRule(
        WorkspaceSplitAxis axis,
        float firstBranchMinSizePx,
        float secondBranchMinSizePx)
    {
        Axis = axis;
        FirstBranchMinSizePx = firstBranchMinSizePx;
        SecondBranchMinSizePx = secondBranchMinSizePx;
    }

    /// <summary>Gets the axis affected by this split rule.</summary>
    internal WorkspaceSplitAxis Axis { get; }

    /// <summary>Gets the first-branch minimum size in pixels.</summary>
    internal float FirstBranchMinSizePx { get; }

    /// <summary>Gets the second-branch minimum size in pixels.</summary>
    internal float SecondBranchMinSizePx { get; }
}

/// <summary>Constraint snapshot derived from the current display model.</summary>
internal sealed class WorkspaceConstraintSnapshot
{
    /// <summary>Initializes a new constraint snapshot.</summary>
    internal WorkspaceConstraintSnapshot(
        IReadOnlyDictionary<WorkspacePaneKind, WorkspacePaneConstraintRenderState> visiblePaneStates,
        IReadOnlyDictionary<WorkspaceSplitRatioBinding, WorkspaceSplitClampRule> splitClampRules)
    {
        VisiblePaneStates = visiblePaneStates ?? throw new ArgumentNullException(nameof(visiblePaneStates));
        SplitClampRules = splitClampRules ?? throw new ArgumentNullException(nameof(splitClampRules));
    }

    /// <summary>Gets the constraint render states for currently visible panes.</summary>
    internal IReadOnlyDictionary<WorkspacePaneKind, WorkspacePaneConstraintRenderState> VisiblePaneStates { get; }

    /// <summary>Gets the active split clamp rules keyed by ratio binding.</summary>
    internal IReadOnlyDictionary<WorkspaceSplitRatioBinding, WorkspaceSplitClampRule> SplitClampRules { get; }

    /// <summary>Returns an empty snapshot with no visible pane constraints and no split floors.</summary>
    internal static WorkspaceConstraintSnapshot Empty { get; } =
        new(
            new ReadOnlyDictionary<WorkspacePaneKind, WorkspacePaneConstraintRenderState>(
                new Dictionary<WorkspacePaneKind, WorkspacePaneConstraintRenderState>()),
            new ReadOnlyDictionary<WorkspaceSplitRatioBinding, WorkspaceSplitClampRule>(
                new Dictionary<WorkspaceSplitRatioBinding, WorkspaceSplitClampRule>()));
}

