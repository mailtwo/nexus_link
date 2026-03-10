using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using Uplink2.Runtime.Workspace.Ui;
using Xunit;

#nullable enable

namespace Uplink2.Tests;

/// <summary>Unit tests for workspace pane-state bridge normalization.</summary>
[Trait("Speed", "fast")]
public sealed class WorkspacePaneStateBridgeTest
{
    /// <summary>Ensures dictionary-like generic enumerables are accepted as pane-state maps.</summary>
    [Fact]
    public void TryNormalizeStateMap_AcceptsGenericDictionaryLikeEnumerable()
    {
        var rawState = new FakeDictionaryLikeState([
            new KeyValuePair<object, object?>("schema", 1L),
            new KeyValuePair<object, object?>("active_tab", "map"),
            new KeyValuePair<object, object?>("flags", new List<object?> { true, "hot" }),
        ]);

        var normalized = InvokeTryNormalizeStateMap(rawState, out var succeeded);

        Assert.True(succeeded);
        Assert.Equal(1L, normalized["schema"]);
        Assert.Equal("map", Assert.IsType<string>(normalized["active_tab"]));
        var flags = Assert.IsType<List<object?>>(normalized["flags"]);
        Assert.True(Assert.IsType<bool>(flags[0]));
        Assert.Equal("hot", Assert.IsType<string>(flags[1]));
    }

    /// <summary>Ensures empty dictionary-like generic enumerables are treated as valid empty state.</summary>
    [Fact]
    public void TryNormalizeStateMap_AcceptsEmptyGenericDictionaryLikeEnumerable()
    {
        var normalized = InvokeTryNormalizeStateMap(new FakeDictionaryLikeState([]), out var succeeded);

        Assert.True(succeeded);
        Assert.Empty(normalized);
    }

    private static Dictionary<string, object?> InvokeTryNormalizeStateMap(object rawState, out bool succeeded)
    {
        var method = typeof(WorkspacePaneStateBridge).GetMethod(
            "TryNormalizeStateMap",
            BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("TryNormalizeStateMap method not found.");

        var arguments = new object?[] { rawState, null };
        succeeded = (bool)(method.Invoke(null, arguments)
            ?? throw new InvalidOperationException("TryNormalizeStateMap returned null."));
        return Assert.IsType<Dictionary<string, object?>>(arguments[1]);
    }

    private sealed class FakeDictionaryLikeState : IEnumerable<KeyValuePair<object, object?>>
    {
        private readonly IReadOnlyList<KeyValuePair<object, object?>> entries;

        public FakeDictionaryLikeState(IReadOnlyList<KeyValuePair<object, object?>> entries)
        {
            this.entries = entries;
        }

        public IEnumerator<KeyValuePair<object, object?>> GetEnumerator() => entries.GetEnumerator();

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
