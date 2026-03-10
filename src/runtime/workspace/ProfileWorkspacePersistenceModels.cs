using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

#nullable enable

namespace Uplink2.Runtime.Workspace;

/// <summary>Immutable profile options container reserved for future concrete option keys.</summary>
internal sealed class ProfileOptionsState
{
    internal ProfileOptionsState(
        IReadOnlyDictionary<string, object?> audio,
        IReadOnlyDictionary<string, object?> display,
        IReadOnlyDictionary<string, object?> input,
        IReadOnlyDictionary<string, object?> accessibility,
        IReadOnlyDictionary<string, object?> ui)
    {
        Audio = CopyCategory(audio);
        Display = CopyCategory(display);
        Input = CopyCategory(input);
        Accessibility = CopyCategory(accessibility);
        Ui = CopyCategory(ui);
    }

    internal IReadOnlyDictionary<string, object?> Audio { get; }

    internal IReadOnlyDictionary<string, object?> Display { get; }

    internal IReadOnlyDictionary<string, object?> Input { get; }

    internal IReadOnlyDictionary<string, object?> Accessibility { get; }

    internal IReadOnlyDictionary<string, object?> Ui { get; }

    internal static ProfileOptionsState CreateDefault()
    {
        return new ProfileOptionsState(
            new Dictionary<string, object?>(),
            new Dictionary<string, object?>(),
            new Dictionary<string, object?>(),
            new Dictionary<string, object?>(),
            new Dictionary<string, object?>());
    }

    private static IReadOnlyDictionary<string, object?> CopyCategory(IReadOnlyDictionary<string, object?> category)
    {
        category ??= new Dictionary<string, object?>();
        return new ReadOnlyDictionary<string, object?>(
            new Dictionary<string, object?>(category, StringComparer.Ordinal));
    }
}

/// <summary>Immutable top-level profile state loaded from or written to profile.toml.</summary>
internal sealed class ProfileState
{
    internal const int CurrentVersion = 1;

    internal ProfileState(
        int version,
        ProfileOptionsState options,
        WorkspaceStoredState workspaceState)
    {
        Version = version;
        Options = options ?? throw new ArgumentNullException(nameof(options));
        WorkspaceState = workspaceState ?? throw new ArgumentNullException(nameof(workspaceState));
    }

    internal int Version { get; }

    internal ProfileOptionsState Options { get; }

    internal WorkspaceStoredState WorkspaceState { get; }

    internal static ProfileState CreateDefault(bool includeDevelopmentWorldMapTrace)
    {
        return new ProfileState(
            CurrentVersion,
            ProfileOptionsState.CreateDefault(),
            WorkspaceStoredStateFactory.CreateDefaultStoredState(includeDevelopmentWorldMapTrace));
    }
}
