using Godot;

#nullable enable

namespace Uplink2.Runtime.Workspace;

/// <summary>Autoload wrapper that exposes the shell workspace state machine to the rest of the game.</summary>
public partial class ShellWorkspaceRuntime : Node
{
    private readonly WorkspaceStateMachine stateMachine = new();

    /// <summary>Raised when the workspace runtime state changes.</summary>
    [Signal]
    public delegate void StateChangedEventHandler();

    /// <summary>Gets the current autoload instance.</summary>
    public static ShellWorkspaceRuntime? Instance { get; private set; }

    /// <inheritdoc/>
    public override void _EnterTree()
    {
        Instance = this;
    }

    /// <inheritdoc/>
    public override void _ExitTree()
    {
        if (ReferenceEquals(Instance, this))
        {
            Instance = null;
        }
    }

    /// <summary>Returns an immutable snapshot of the current workspace state.</summary>
    public WorkspaceStateSnapshot GetSnapshot()
    {
        return stateMachine.GetSnapshot();
    }

    /// <summary>Resets the workspace to the stage-1 bootstrap state.</summary>
    public bool ResetToDefaultState()
    {
        return EmitChangedIfNeeded(stateMachine.ResetToDefaultState());
    }

    /// <summary>Replaces the current runtime state with a fully sanitized effective workspace snapshot.</summary>
    public bool ReplaceState(WorkspaceStateSnapshot snapshot)
    {
        return EmitChangedIfNeeded(stateMachine.ReplaceState(snapshot));
    }

    /// <summary>Activates a pane, opening it when needed.</summary>
    public bool ActivatePane(WorkspacePaneKind kind)
    {
        return EmitChangedIfNeeded(stateMachine.ActivatePane(kind));
    }

    /// <summary>Focuses a currently visible pane without changing residency or layout.</summary>
    public bool FocusPane(WorkspacePaneKind kind)
    {
        return EmitChangedIfNeeded(stateMachine.FocusPane(kind));
    }

    /// <summary>Closes a resident pane.</summary>
    public bool ClosePane(WorkspacePaneKind kind)
    {
        return EmitChangedIfNeeded(stateMachine.ClosePane(kind));
    }

    /// <summary>Moves a resident pane to another dock slot.</summary>
    public bool MovePane(WorkspacePaneKind kind, DockSlot targetSlot)
    {
        return EmitChangedIfNeeded(stateMachine.MovePane(kind, targetSlot));
    }

    /// <summary>Adds a pane kind to the pinned set.</summary>
    public bool PinPane(WorkspacePaneKind kind)
    {
        return EmitChangedIfNeeded(stateMachine.PinPane(kind));
    }

    /// <summary>Removes a pane kind from the pinned set.</summary>
    public bool UnpinPane(WorkspacePaneKind kind)
    {
        return EmitChangedIfNeeded(stateMachine.UnpinPane(kind));
    }

    /// <summary>Stores and enters a maximized pane context for a resident pane.</summary>
    public bool MaximizePane(WorkspacePaneKind kind)
    {
        return EmitChangedIfNeeded(stateMachine.MaximizePane(kind));
    }

    /// <summary>Clears the stored maximized pane context and returns to docked mode.</summary>
    public bool RestoreDocked()
    {
        return EmitChangedIfNeeded(stateMachine.RestoreDocked());
    }

    /// <summary>Updates the normalized split ratios.</summary>
    public bool SetSplitRatios(float leftRatio, float rightTopRatio)
    {
        return EmitChangedIfNeeded(stateMachine.SetSplitRatios(leftRatio, rightTopRatio));
    }

    /// <summary>Tries to resolve the current dock slot of a resident pane.</summary>
    public bool TryGetCurrentDockSlot(WorkspacePaneKind kind, out DockSlot slot)
    {
        return stateMachine.TryGetCurrentDockSlot(kind, out slot);
    }

    private bool EmitChangedIfNeeded(bool changed)
    {
        if (changed)
        {
            EmitSignal(SignalName.StateChanged);
        }

        return changed;
    }
}
