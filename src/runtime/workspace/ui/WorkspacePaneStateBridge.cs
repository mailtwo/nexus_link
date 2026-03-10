using Godot;
using System;
using System.Collections;
using System.Collections.Generic;
using Uplink2.Runtime.Persistence;

#nullable enable

namespace Uplink2.Runtime.Workspace.Ui;

/// <summary>Bridges pane-local persistence hooks across C# and GDScript pane implementations.</summary>
internal sealed class WorkspacePaneStateBridge : IDisposable
{
    private const string GdSignalName = "workspace_pane_state_changed";
    private const string GdCaptureMethodName = "capture_workspace_pane_state";
    private const string GdRestoreMethodName = "restore_workspace_pane_state";

    private static readonly WorkspacePaneStateTable EmptyPaneStateTable =
        new(new Dictionary<string, object?>());

    private readonly Dictionary<WorkspacePaneKind, PaneAttachment> attachments = new();
    private readonly Dictionary<WorkspacePaneKind, WorkspacePaneStateTable> pendingRestoreByKind = new();

    internal event Action? PaneStateDirty;

    /// <inheritdoc/>
    public void Dispose()
    {
        foreach (var attachment in attachments.Values)
        {
            DisconnectAttachment(attachment);
        }

        attachments.Clear();
        pendingRestoreByKind.Clear();
    }

    internal void AttachPane(WorkspacePaneKind paneKind, Control node)
    {
        if (node is null)
        {
            throw new ArgumentNullException(nameof(node));
        }

        if (attachments.TryGetValue(paneKind, out var existingAttachment))
        {
            if (!ReferenceEquals(existingAttachment.Node, node))
            {
                DisconnectAttachment(existingAttachment);
                attachments.Remove(paneKind);
            }
            else
            {
                TryApplyPendingPaneState(paneKind, existingAttachment.Node);
                return;
            }
        }

        var attachment = CreateAttachment(node);
        attachments[paneKind] = attachment;
        TryApplyPendingPaneState(paneKind, node);
    }

    internal void ReplaceRestorablePaneStateTables(
        IReadOnlyDictionary<WorkspacePaneKind, WorkspacePaneStateTable> paneStateByKind,
        IReadOnlySet<WorkspacePaneKind> availablePaneKinds)
    {
        pendingRestoreByKind.Clear();
        foreach (var paneKind in availablePaneKinds)
        {
            pendingRestoreByKind[paneKind] = paneStateByKind.TryGetValue(paneKind, out var table)
                ? table
                : EmptyPaneStateTable;
        }

        foreach (var pair in attachments)
        {
            TryApplyPendingPaneState(pair.Key, pair.Value.Node);
        }
    }

    internal Dictionary<WorkspacePaneKind, WorkspacePaneStateTable> CaptureAvailablePaneStateTables(
        IReadOnlySet<WorkspacePaneKind> availablePaneKinds,
        IReadOnlyDictionary<WorkspacePaneKind, WorkspacePaneStateTable> existingAvailablePaneStateByKind)
    {
        var result = new Dictionary<WorkspacePaneKind, WorkspacePaneStateTable>();
        foreach (var pair in existingAvailablePaneStateByKind)
        {
            if (!availablePaneKinds.Contains(pair.Key))
            {
                continue;
            }

            result[pair.Key] = pair.Value;
        }

        foreach (var paneKind in availablePaneKinds)
        {
            if (!attachments.TryGetValue(paneKind, out var attachment))
            {
                continue;
            }

            if (!TryCapturePaneState(attachment.Node, out var stateEntries, out var hasState))
            {
                continue;
            }

            if (!hasState || stateEntries.Count == 0)
            {
                result.Remove(paneKind);
                continue;
            }

            result[paneKind] = new WorkspacePaneStateTable(stateEntries);
        }

        return result;
    }

    private PaneAttachment CreateAttachment(Control node)
    {
        if (node is IWorkspacePaneStateParticipant participant)
        {
            Action handler = OnPaneStateDirty;
            participant.WorkspacePaneStateChanged += handler;
            return new PaneAttachment(node, participant, handler, null);
        }

        if (node.HasSignal(GdSignalName))
        {
            var callable = Callable.From(OnPaneStateDirty);
            if (!node.IsConnected(GdSignalName, callable))
            {
                node.Connect(GdSignalName, callable);
            }

            return new PaneAttachment(node, null, null, callable);
        }

        return new PaneAttachment(node, null, null, null);
    }

    private void DisconnectAttachment(PaneAttachment attachment)
    {
        if (attachment.Participant is not null && attachment.CSharpHandler is not null)
        {
            attachment.Participant.WorkspacePaneStateChanged -= attachment.CSharpHandler;
        }

        if (attachment.GdSignalCallable.HasValue &&
            attachment.Node.HasSignal(GdSignalName) &&
            attachment.Node.IsConnected(GdSignalName, attachment.GdSignalCallable.Value))
        {
            attachment.Node.Disconnect(GdSignalName, attachment.GdSignalCallable.Value);
        }
    }

    private void TryApplyPendingPaneState(WorkspacePaneKind paneKind, Control node)
    {
        if (!pendingRestoreByKind.TryGetValue(paneKind, out var table))
        {
            return;
        }

        if (!TryRestorePaneState(node, table.Entries))
        {
            GD.PushWarning($"WorkspacePaneStateBridge: failed to restore pane-local state for '{paneKind}'.");
        }

        pendingRestoreByKind.Remove(paneKind);
    }

    private static bool TryCapturePaneState(
        Control node,
        out Dictionary<string, object?> stateEntries,
        out bool hasState)
    {
        stateEntries = new Dictionary<string, object?>(StringComparer.Ordinal);
        hasState = false;

        try
        {
            object? rawState = null;
            if (node is IWorkspacePaneStateParticipant participant)
            {
                rawState = participant.CaptureWorkspacePaneState();
            }
            else if (node.HasMethod(GdCaptureMethodName))
            {
                rawState = node.Call(GdCaptureMethodName);
            }
            else
            {
                return true;
            }

            if (!TryNormalizeStateMap(rawState, out stateEntries))
            {
                GD.PushWarning($"WorkspacePaneStateBridge: pane '{node.Name}' returned an invalid pane-state payload.");
                return false;
            }

            hasState = true;
            return true;
        }
        catch (Exception ex)
        {
            GD.PushWarning($"WorkspacePaneStateBridge: failed to capture pane-local state from '{node.Name}'. {ex.Message}");
            return false;
        }
    }

    private static bool TryRestorePaneState(Control node, IReadOnlyDictionary<string, object?> stateEntries)
    {
        try
        {
            if (node is IWorkspacePaneStateParticipant participant)
            {
                participant.RestoreWorkspacePaneState(stateEntries);
                return true;
            }

            if (!node.HasMethod(GdRestoreMethodName))
            {
                return true;
            }

            node.Call(GdRestoreMethodName, ConvertToGodotVariant(stateEntries));
            return true;
        }
        catch (Exception ex)
        {
            GD.PushWarning($"WorkspacePaneStateBridge: failed to restore pane-local state to '{node.Name}'. {ex.Message}");
            return false;
        }
    }

    private static bool TryNormalizeStateMap(object? rawState, out Dictionary<string, object?> normalizedState)
    {
        normalizedState = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (rawState is null)
        {
            return true;
        }

        if (rawState is IReadOnlyDictionary<string, object?> readOnlyDictionary)
        {
            foreach (var pair in readOnlyDictionary)
            {
                if (!TryNormalizeStateValue(pair.Value, out var normalizedValue))
                {
                    return false;
                }

                normalizedState[pair.Key] = normalizedValue;
            }

            return true;
        }

        if (rawState is not IDictionary dictionary)
        {
            return false;
        }

        foreach (DictionaryEntry entry in dictionary)
        {
            var key = entry.Key?.ToString();
            if (string.IsNullOrWhiteSpace(key))
            {
                return false;
            }

            if (!TryNormalizeStateValue(entry.Value, out var normalizedValue))
            {
                return false;
            }

            normalizedState[key] = normalizedValue;
        }

        return true;
    }

    private static bool TryNormalizeStateValue(object? rawValue, out object? normalizedValue)
    {
        normalizedValue = null;
        if (!SaveValueConverter.TryToDto(rawValue, out var dto, out _))
        {
            return false;
        }

        return SaveValueConverter.TryFromDto(dto, out normalizedValue, out _);
    }

    private static Variant ConvertToGodotVariant(object? value)
    {
        return value switch
        {
            null => default,
            bool boolValue => boolValue,
            int intValue => intValue,
            long longValue => longValue,
            float floatValue => floatValue,
            double doubleValue => doubleValue,
            string stringValue => stringValue,
            IEnumerable<KeyValuePair<string, object?>> nullableDictionary => ConvertNullableDictionary(nullableDictionary),
            IDictionary dictionary => ConvertUntypedDictionary(dictionary),
            IEnumerable enumerable when value is not string => ConvertEnumerable(enumerable),
            _ => default,
        };
    }

    private static Godot.Collections.Dictionary ConvertNullableDictionary(IEnumerable<KeyValuePair<string, object?>> dictionary)
    {
        var result = new Godot.Collections.Dictionary();
        foreach (var pair in dictionary)
        {
            result[pair.Key] = ConvertToGodotVariant(pair.Value);
        }

        return result;
    }

    private static Godot.Collections.Dictionary ConvertObjectDictionary(IDictionary<string, object> dictionary)
    {
        var result = new Godot.Collections.Dictionary();
        foreach (var pair in dictionary)
        {
            result[pair.Key] = ConvertToGodotVariant(pair.Value);
        }

        return result;
    }

    private static Godot.Collections.Dictionary ConvertUntypedDictionary(IDictionary dictionary)
    {
        var result = new Godot.Collections.Dictionary();
        foreach (DictionaryEntry entry in dictionary)
        {
            var key = entry.Key?.ToString();
            if (string.IsNullOrWhiteSpace(key))
            {
                continue;
            }

            result[key] = ConvertToGodotVariant(entry.Value);
        }

        return result;
    }

    private static Godot.Collections.Array ConvertEnumerable(IEnumerable enumerable)
    {
        var result = new Godot.Collections.Array();
        foreach (var item in enumerable)
        {
            result.Add(ConvertToGodotVariant(item));
        }

        return result;
    }

    private void OnPaneStateDirty()
    {
        PaneStateDirty?.Invoke();
    }

    private sealed record PaneAttachment(
        Control Node,
        IWorkspacePaneStateParticipant? Participant,
        Action? CSharpHandler,
        Callable? GdSignalCallable);
}
