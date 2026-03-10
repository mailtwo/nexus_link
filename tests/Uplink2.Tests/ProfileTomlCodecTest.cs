using System;
using System.Collections.Generic;
using Uplink2.Runtime.Workspace;
using Xunit;

#nullable enable

namespace Uplink2.Tests;

/// <summary>Unit tests for profile.toml parsing and serialization.</summary>
[Trait("Speed", "fast")]
public sealed class ProfileTomlCodecTest
{
    /// <summary>Ensures profile TOML round-trips workspace and pane-local state.</summary>
    [Fact]
    public void SerializeThenParse_RoundTripsWorkspaceAndPaneState()
    {
        var profileState = new ProfileState(
            ProfileState.CurrentVersion,
            new ProfileOptionsState(
                new Dictionary<string, object?> { ["master_volume"] = 0.75d },
                new Dictionary<string, object?>(),
                new Dictionary<string, object?>(),
                new Dictionary<string, object?>(),
                new Dictionary<string, object?>()),
            new WorkspaceStoredState(
                WorkspaceMode.Docked,
                null,
                0.42f,
                0.58f,
                new Dictionary<DockSlot, WorkspaceStoredDockSlotState>
                {
                    [DockSlot.Left] = new(
                        DockSlot.Left,
                        new[] { WorkspacePaneKind.Terminal },
                        WorkspacePaneKind.Terminal),
                    [DockSlot.RightTop] = new(
                        DockSlot.RightTop,
                        new[] { WorkspacePaneKind.WorldMapTrace },
                        WorkspacePaneKind.WorldMapTrace),
                    [DockSlot.RightBottom] = new(
                        DockSlot.RightBottom,
                        Array.Empty<WorkspacePaneKind>(),
                        null),
                },
                new[] { WorkspacePaneKind.Terminal, WorkspacePaneKind.WorldMapTrace },
                new Dictionary<WorkspacePaneKind, WorkspacePaneStateTable>
                {
                    [WorkspacePaneKind.WorldMapTrace] = new WorkspacePaneStateTable(
                        new Dictionary<string, object?>
                        {
                            ["schema"] = 1L,
                            ["active_tab"] = "map",
                            ["filter_hot"] = false,
                        }),
                }));

        var toml = ProfileTomlCodec.Serialize(profileState);

        Assert.Contains("[workspace.pane_state.WORLD_MAP_TRACE]", toml, StringComparison.Ordinal);
        Assert.True(ProfileTomlCodec.TryParse(toml, false, out var parsedState, out var errorMessage), errorMessage);
        Assert.Equal(ProfileState.CurrentVersion, parsedState.Version);
        Assert.Equal(0.75d, Convert.ToDouble(parsedState.Options.Audio["master_volume"]), 3);
        Assert.Equal(WorkspacePaneKind.WorldMapTrace, parsedState.WorkspaceState.Slots[DockSlot.RightTop].ActivePane);
        Assert.True(parsedState.WorkspaceState.PaneStateByKind.TryGetValue(WorkspacePaneKind.WorldMapTrace, out var paneState));
        Assert.Equal("map", Assert.IsType<string>(paneState.Entries["active_tab"]));
        Assert.False(Assert.IsType<bool>(paneState.Entries["filter_hot"]));
    }

    /// <summary>Ensures unsupported profile TOML versions fail closed and keep the default state.</summary>
    [Fact]
    public void TryParse_InvalidVersion_ReturnsFalseAndKeepsDefaultState()
    {
        const string toml = """
            [meta]
            version = 999
            """;

        Assert.False(ProfileTomlCodec.TryParse(toml, false, out var parsedState, out var errorMessage));
        Assert.Contains("unsupported profile TOML version", errorMessage, StringComparison.Ordinal);
        Assert.Equal(WorkspacePaneKind.Terminal, parsedState.WorkspaceState.Slots[DockSlot.Left].ActivePane);
        Assert.Empty(parsedState.WorkspaceState.Slots[DockSlot.RightTop].DockStack);
    }

    /// <summary>Ensures reserved empty option tables load without forcing concrete option keys.</summary>
    [Fact]
    public void TryParse_EmptyOptionsTables_LoadsSuccessfully()
    {
        const string toml = """
            [meta]
            version = 1

            [options.audio]
            [options.display]
            [options.input]
            [options.accessibility]
            [options.ui]
            """;

        Assert.True(ProfileTomlCodec.TryParse(toml, false, out var parsedState, out var errorMessage), errorMessage);
        Assert.Empty(parsedState.Options.Audio);
        Assert.Empty(parsedState.Options.Display);
        Assert.Empty(parsedState.Options.Input);
        Assert.Empty(parsedState.Options.Accessibility);
        Assert.Empty(parsedState.Options.Ui);
        Assert.Equal(WorkspacePaneKind.Terminal, parsedState.WorkspaceState.Slots[DockSlot.Left].ActivePane);
    }
}
