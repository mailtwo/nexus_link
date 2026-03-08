namespace Uplink2.Runtime.Workspace.Ui;

/// <summary>Applies renderer-level constraint state to pane content that needs local overflow handling.</summary>
internal interface IWorkspaceConstraintAwarePaneContent
{
    /// <summary>Applies the currently active constraint state for the visible pane.</summary>
    void ApplyConstraintState(WorkspacePaneConstraintRenderState state);
}

