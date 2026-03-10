using System;
using System.Collections.Generic;

#nullable enable

namespace Uplink2.Runtime.Workspace.Ui;

/// <summary>Normalizes persisted world-map pane-local state without depending on live Godot controls.</summary>
internal static class WorldMapTracePaneStateCodec
{
    private const long Schema = 1;
    private const string DefaultTabId = "map";

    internal static IReadOnlyDictionary<string, object?> Capture(string activeTabId, bool filterHot)
    {
        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["schema"] = Schema,
            ["active_tab"] = NormalizeVisibleTabId(activeTabId),
            ["filter_hot"] = filterHot,
        };
    }

    internal static WorldMapTracePaneStoredState Restore(IReadOnlyDictionary<string, object?> state)
    {
        if (state is null)
        {
            throw new ArgumentNullException(nameof(state));
        }

        var activeTabId = DefaultTabId;
        if (state.TryGetValue("active_tab", out var rawTabId) &&
            rawTabId is string tabId &&
            !string.IsNullOrWhiteSpace(tabId))
        {
            activeTabId = NormalizeVisibleTabId(tabId);
        }

        var filterHot = true;
        if (state.TryGetValue("filter_hot", out var rawFilterHot) && rawFilterHot is bool hotToggle)
        {
            filterHot = hotToggle;
        }

        return new WorldMapTracePaneStoredState(activeTabId, filterHot);
    }

    private static string NormalizeVisibleTabId(string? tabId)
    {
        return string.Equals(tabId?.Trim(), DefaultTabId, StringComparison.Ordinal)
            ? DefaultTabId
            : DefaultTabId;
    }
}

/// <summary>Immutable normalized pane-local persistence payload for the world-map trace pane.</summary>
internal readonly record struct WorldMapTracePaneStoredState(
    string ActiveTabId,
    bool FilterHot);
