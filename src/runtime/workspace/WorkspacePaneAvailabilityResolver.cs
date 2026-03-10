using System;
using System.Collections.Generic;
using Uplink2.Runtime.Workspace.Ui;

#nullable enable

namespace Uplink2.Runtime.Workspace;

/// <summary>Resolves currently available shell panes from scenario flags and renderer capability.</summary>
internal static class WorkspacePaneAvailabilityResolver
{
    /// <summary>Gets the stable scenario-flag key for a pane entitlement.</summary>
    internal static string GetFlagKey(WorkspacePaneKind paneKind)
    {
        return "unlock.pane." + WorkspaceKindTextCodec.ToAvailabilityFlagSegment(paneKind);
    }

    /// <summary>Resolves currently available pane kinds.</summary>
    internal static HashSet<WorkspacePaneKind> Resolve(
        IReadOnlyDictionary<string, object> scenarioFlags,
        IReadOnlySet<WorkspacePaneKind> implementedPaneKinds,
        bool enableDebugOverride = false)
    {
        if (scenarioFlags is null)
        {
            throw new ArgumentNullException(nameof(scenarioFlags));
        }

        if (implementedPaneKinds is null)
        {
            throw new ArgumentNullException(nameof(implementedPaneKinds));
        }

        if (enableDebugOverride)
        {
            return new HashSet<WorkspacePaneKind>(implementedPaneKinds);
        }

        var available = new HashSet<WorkspacePaneKind>();
        foreach (var paneKind in PaneContentFactory.DefaultImplementedPaneKinds)
        {
            if (!implementedPaneKinds.Contains(paneKind))
            {
                continue;
            }

            if (scenarioFlags.TryGetValue(GetFlagKey(paneKind), out var rawValue) &&
                rawValue is bool enabled &&
                enabled)
            {
                available.Add(paneKind);
            }
        }

        return available;
    }
}
