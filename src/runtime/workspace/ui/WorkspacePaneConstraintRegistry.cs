using System;
using Uplink2.Runtime.Workspace;

#nullable enable

namespace Uplink2.Runtime.Workspace.Ui;

/// <summary>Resolves canonical pane constraints owned by the shell workspace contract.</summary>
internal static class WorkspacePaneConstraintRegistry
{
    /// <summary>Tries to resolve the canonical constraint for one pane kind.</summary>
    internal static bool TryGetConstraint(WorkspacePaneKind paneKind, out WorkspacePaneConstraint constraint)
    {
        switch (paneKind)
        {
            case WorkspacePaneKind.Terminal:
                constraint = new WorkspacePaneConstraint(
                    minUsableWidthPx: 150.0f,
                    minUsableHeightPx: 100.0f,
                    horizontalResolvePolicy: WorkspaceConstraintResolvePolicy.Clamp,
                    verticalResolvePolicy: WorkspaceConstraintResolvePolicy.Scroll);
                return true;
            case WorkspacePaneKind.WorldMapTrace:
                var worldMapMinViewport = WorldMapTracePane.ResolveMinUsableViewportSize(
                    WorldMapTracePane.GetReferenceTextureSize());
                constraint = new WorkspacePaneConstraint(
                    minUsableWidthPx: worldMapMinViewport.X,
                    minUsableHeightPx: worldMapMinViewport.Y,
                    horizontalResolvePolicy: WorkspaceConstraintResolvePolicy.Clamp,
                    verticalResolvePolicy: WorkspaceConstraintResolvePolicy.Clamp);
                return true;
            default:
                constraint = null!;
                return false;
        }
    }
}
