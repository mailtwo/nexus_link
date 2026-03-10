using System;
using System.Collections.Generic;
using Uplink2.Runtime.Workspace.Ui;
using Xunit;

#nullable enable

namespace Uplink2.Tests;

/// <summary>Unit tests for world-map pane-local state normalization.</summary>
[Trait("Speed", "fast")]
public sealed class WorldMapTracePaneStateTest
{
    /// <summary>Ensures capture emits the persisted schema and current visible filter state.</summary>
    [Fact]
    public void Capture_ProducesPersistedWorldMapPaneState()
    {
        var captured = WorldMapTracePaneStateCodec.Capture("map", filterHot: false);

        Assert.Equal(1L, Convert.ToInt64(captured["schema"]));
        Assert.Equal("map", Assert.IsType<string>(captured["active_tab"]));
        Assert.False(Assert.IsType<bool>(captured["filter_hot"]));
    }

    /// <summary>Ensures restore normalizes invalid or missing values to phase-one defaults.</summary>
    [Fact]
    public void Restore_InvalidValues_FallBackToDefaultVisibleState()
    {
        var restored = WorldMapTracePaneStateCodec.Restore(new Dictionary<string, object?>
        {
            ["active_tab"] = "nodes",
            ["filter_hot"] = "invalid",
        });

        Assert.Equal("map", restored.ActiveTabId);
        Assert.True(restored.FilterHot);
    }
}
