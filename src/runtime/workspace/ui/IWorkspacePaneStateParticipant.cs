using System;
using System.Collections.Generic;

#nullable enable

namespace Uplink2.Runtime.Workspace.Ui;

/// <summary>Provides pane-local persistence hooks for C# workspace pane implementations.</summary>
internal interface IWorkspacePaneStateParticipant
{
    /// <summary>Raised when the pane-local UI state changes and should be persisted.</summary>
    event Action? WorkspacePaneStateChanged;

    /// <summary>Captures the current pane-local state as a persistence-friendly map.</summary>
    IReadOnlyDictionary<string, object?> CaptureWorkspacePaneState();

    /// <summary>Restores the pane-local state from a previously captured persistence map.</summary>
    void RestoreWorkspacePaneState(IReadOnlyDictionary<string, object?> state);
}
