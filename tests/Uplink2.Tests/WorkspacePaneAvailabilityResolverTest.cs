using System.Collections.Generic;
using Uplink2.Runtime.Workspace;
using Xunit;

#nullable enable

namespace Uplink2.Tests;

/// <summary>Unit tests for workspace pane availability resolution from scenario flags.</summary>
[Trait("Speed", "fast")]
public sealed class WorkspacePaneAvailabilityResolverTest
{
    /// <summary>Ensures availability flag keys follow the agreed lower-snake naming rule.</summary>
    [Fact]
    public void GetFlagKey_UsesLowerSnakeWindowKind()
    {
        Assert.Equal(
            "unlock.pane.world_map_trace",
            WorkspacePaneAvailabilityResolver.GetFlagKey(WorkspacePaneKind.WorldMapTrace));
    }

    /// <summary>Ensures missing flags default to unavailable.</summary>
    [Fact]
    public void Resolve_MissingFlags_DefaultsToUnavailable()
    {
        var availablePaneKinds = WorkspacePaneAvailabilityResolver.Resolve(
            new Dictionary<string, object>(),
            new HashSet<WorkspacePaneKind>
            {
                WorkspacePaneKind.Terminal,
                WorkspacePaneKind.WorldMapTrace,
            });

        Assert.Empty(availablePaneKinds);
    }

    /// <summary>Ensures only implemented panes with explicit true flags become available.</summary>
    [Fact]
    public void Resolve_IntersectsFlagsWithImplementedPaneKinds()
    {
        var availablePaneKinds = WorkspacePaneAvailabilityResolver.Resolve(
            new Dictionary<string, object>
            {
                [WorkspacePaneAvailabilityResolver.GetFlagKey(WorkspacePaneKind.Terminal)] = true,
                [WorkspacePaneAvailabilityResolver.GetFlagKey(WorkspacePaneKind.WorldMapTrace)] = true,
                ["unlock.pane.mail"] = true,
                ["unlock.pane.unknown_future_pane"] = true,
            },
            new HashSet<WorkspacePaneKind>
            {
                WorkspacePaneKind.Terminal,
            });

        Assert.Single(availablePaneKinds);
        Assert.Contains(WorkspacePaneKind.Terminal, availablePaneKinds);
        Assert.DoesNotContain(WorkspacePaneKind.WorldMapTrace, availablePaneKinds);
        Assert.DoesNotContain(WorkspacePaneKind.Mail, availablePaneKinds);
    }

    /// <summary>Ensures debug override exposes every currently implemented pane without unlock flags.</summary>
    [Fact]
    public void Resolve_DebugOverride_MakesAllImplementedPanesAvailable()
    {
        var availablePaneKinds = WorkspacePaneAvailabilityResolver.Resolve(
            new Dictionary<string, object>(),
            new HashSet<WorkspacePaneKind>
            {
                WorkspacePaneKind.Terminal,
                WorkspacePaneKind.WorldMapTrace,
            },
            enableDebugOverride: true);

        Assert.Equal(2, availablePaneKinds.Count);
        Assert.Contains(WorkspacePaneKind.Terminal, availablePaneKinds);
        Assert.Contains(WorkspacePaneKind.WorldMapTrace, availablePaneKinds);
    }
}
