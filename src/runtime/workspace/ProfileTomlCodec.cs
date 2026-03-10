using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Tomlyn;
using Tomlyn.Model;
using Tomlyn.Parsing;
using Uplink2.Runtime.Persistence;

#nullable enable

namespace Uplink2.Runtime.Workspace;

/// <summary>Parses and serializes the profile/workspace TOML document defined by doc 16.</summary>
internal static class ProfileTomlCodec
{
    /// <summary>Parses profile TOML text into an immutable profile model.</summary>
    internal static bool TryParse(
        string toml,
        bool includeDevelopmentWorldMapTrace,
        out ProfileState profileState,
        out string errorMessage)
    {
        profileState = ProfileState.CreateDefault(includeDevelopmentWorldMapTrace);
        errorMessage = string.Empty;

        var document = SyntaxParser.Parse(toml ?? string.Empty, "profile.toml", validate: true);
        if (document.HasErrors)
        {
            errorMessage = string.Join(
                "; ",
                document.Diagnostics.Select(static diagnostic => diagnostic.ToString()));
            return false;
        }

        var rootTable = TomlSerializer.Deserialize<TomlTable>(toml ?? string.Empty) ?? new TomlTable();
        var version = ReadVersion(rootTable);
        if (version != ProfileState.CurrentVersion)
        {
            errorMessage = $"unsupported profile TOML version: {version}.";
            return false;
        }

        profileState = new ProfileState(
            version,
            ParseOptionsState(rootTable),
            ParseWorkspaceStoredState(rootTable, includeDevelopmentWorldMapTrace));
        return true;
    }

    /// <summary>Serializes the immutable profile model to TOML text.</summary>
    internal static string Serialize(ProfileState profileState)
    {
        if (profileState is null)
        {
            throw new ArgumentNullException(nameof(profileState));
        }

        var root = new TomlTable
        {
            ["meta"] = new TomlTable
            {
                ["version"] = profileState.Version,
            },
            ["options"] = SerializeOptionsState(profileState.Options),
            ["workspace"] = SerializeWorkspaceState(profileState.WorkspaceState),
        };

        return TomlSerializer.Serialize(root);
    }

    private static int ReadVersion(IDictionary<string, object> rootTable)
    {
        if (!TryGetTable(rootTable, "meta", out var metaTable))
        {
            return 0;
        }

        return ReadInt32(metaTable, "version", 0);
    }

    private static ProfileOptionsState ParseOptionsState(IDictionary<string, object> rootTable)
    {
        if (!TryGetTable(rootTable, "options", out var optionsTable))
        {
            return ProfileOptionsState.CreateDefault();
        }

        return new ProfileOptionsState(
            ParseOpaqueCategoryTable(optionsTable, "audio"),
            ParseOpaqueCategoryTable(optionsTable, "display"),
            ParseOpaqueCategoryTable(optionsTable, "input"),
            ParseOpaqueCategoryTable(optionsTable, "accessibility"),
            ParseOpaqueCategoryTable(optionsTable, "ui"));
    }

    private static WorkspaceStoredState ParseWorkspaceStoredState(
        IDictionary<string, object> rootTable,
        bool includeDevelopmentWorldMapTrace)
    {
        var fallback = WorkspaceStoredStateFactory.CreateDefaultStoredState(includeDevelopmentWorldMapTrace);
        if (!TryGetTable(rootTable, "workspace", out var workspaceTable))
        {
            return fallback;
        }

        var mode = WorkspaceKindTextCodec.TryParseModeTomlToken(
            ReadString(workspaceTable, "mode"),
            out var parsedMode)
            ? parsedMode
            : WorkspaceMode.Docked;
        var maximizedPane = WorkspaceKindTextCodec.TryParsePaneTomlToken(
            ReadString(workspaceTable, "maximized_pane"),
            out var parsedMaximizedPane)
            ? parsedMaximizedPane
            : (WorkspacePaneKind?)null;

        var leftRatio = WorkspaceStateMachine.DefaultLeftRatio;
        var rightTopRatio = WorkspaceStateMachine.DefaultRightTopRatio;
        if (TryGetTable(workspaceTable, "split", out var splitTable))
        {
            leftRatio = ReadSingle(splitTable, "left_ratio", WorkspaceStateMachine.DefaultLeftRatio);
            rightTopRatio = ReadSingle(splitTable, "right_top_ratio", WorkspaceStateMachine.DefaultRightTopRatio);
        }

        var pinnedSet = new List<WorkspacePaneKind>();
        if (TryGetTable(workspaceTable, "pins", out var pinsTable) &&
            TryGetArray(pinsTable, "kinds", out var kindsArray))
        {
            pinnedSet.AddRange(ParsePaneKindArray(kindsArray));
        }
        else
        {
            pinnedSet.AddRange(fallback.PinnedSet);
        }

        var slots = new Dictionary<DockSlot, WorkspaceStoredDockSlotState>();
        if (TryGetTable(workspaceTable, "slots", out var slotsTable))
        {
            foreach (var entry in slotsTable)
            {
                var slotToken = entry.Key;
                if (!WorkspaceKindTextCodec.TryParseDockSlotTomlToken(slotToken, out var slot) ||
                    entry.Value is not IDictionary<string, object> slotTable)
                {
                    continue;
                }

                var stack = TryGetArray(slotTable, "stack", out var stackArray)
                    ? ParsePaneKindArray(stackArray)
                    : new List<WorkspacePaneKind>();
                var activePane = WorkspaceKindTextCodec.TryParsePaneTomlToken(
                    ReadString(slotTable, "active"),
                    out var parsedActivePane)
                    ? parsedActivePane
                    : (WorkspacePaneKind?)null;
                slots[slot] = new WorkspaceStoredDockSlotState(slot, stack, activePane);
            }
        }

        var paneStateByKind = new Dictionary<WorkspacePaneKind, WorkspacePaneStateTable>();
        if (TryGetTable(workspaceTable, "pane_state", out var paneStateTable))
        {
            foreach (var entry in paneStateTable)
            {
                var paneToken = entry.Key;
                if (!WorkspaceKindTextCodec.TryParsePaneTomlToken(paneToken, out var paneKind) ||
                    entry.Value is not IDictionary<string, object> stateTable)
                {
                    continue;
                }

                paneStateByKind[paneKind] = new WorkspacePaneStateTable(ParseOpaqueTable(stateTable));
            }
        }

        return new WorkspaceStoredState(
            mode,
            maximizedPane,
            leftRatio,
            rightTopRatio,
            slots,
            pinnedSet,
            paneStateByKind);
    }

    private static TomlTable SerializeOptionsState(ProfileOptionsState optionsState)
    {
        return new TomlTable
        {
            ["audio"] = CopyTable(optionsState.Audio),
            ["display"] = CopyTable(optionsState.Display),
            ["input"] = CopyTable(optionsState.Input),
            ["accessibility"] = CopyTable(optionsState.Accessibility),
            ["ui"] = CopyTable(optionsState.Ui),
        };
    }

    private static TomlTable SerializeWorkspaceState(WorkspaceStoredState workspaceState)
    {
        var workspaceTable = new TomlTable
        {
            ["mode"] = WorkspaceKindTextCodec.ToTomlToken(workspaceState.Mode),
            ["maximized_pane"] = workspaceState.MaximizedPane.HasValue
                ? WorkspaceKindTextCodec.ToTomlToken(workspaceState.MaximizedPane.Value)
                : string.Empty,
            ["split"] = new TomlTable
            {
                ["left_ratio"] = workspaceState.LeftRatio,
                ["right_top_ratio"] = workspaceState.RightTopRatio,
            },
            ["pins"] = new TomlTable
            {
                ["kinds"] = ToTomlArray(workspaceState.PinnedSet.Select(WorkspaceKindTextCodec.ToTomlToken)),
            },
        };

        var slotsTable = new TomlTable();
        foreach (var slot in new[] { DockSlot.Left, DockSlot.RightTop, DockSlot.RightBottom })
        {
            workspaceState.Slots.TryGetValue(slot, out var slotState);
            slotState ??= new WorkspaceStoredDockSlotState(slot, Array.Empty<WorkspacePaneKind>(), null);
            slotsTable[WorkspaceKindTextCodec.ToTomlToken(slot)] = new TomlTable
            {
                ["stack"] = ToTomlArray(slotState.DockStack.Select(WorkspaceKindTextCodec.ToTomlToken)),
                ["active"] = slotState.ActivePane.HasValue
                    ? WorkspaceKindTextCodec.ToTomlToken(slotState.ActivePane.Value)
                    : string.Empty,
            };
        }

        workspaceTable["slots"] = slotsTable;

        var paneStateTable = new TomlTable();
        foreach (var pair in workspaceState.PaneStateByKind.OrderBy(static pair => pair.Key))
        {
            if (pair.Value.Count == 0)
            {
                continue;
            }

            paneStateTable[WorkspaceKindTextCodec.ToTomlToken(pair.Key)] = CopyTable(pair.Value.Entries);
        }

        if (paneStateTable.Count > 0)
        {
            workspaceTable["pane_state"] = paneStateTable;
        }

        return workspaceTable;
    }

    private static TomlArray ToTomlArray(IEnumerable<string> values)
    {
        var array = new TomlArray();
        foreach (var value in values)
        {
            array.Add(value);
        }

        return array;
    }

    private static Dictionary<string, object?> ParseOpaqueCategoryTable(IDictionary<string, object> parentTable, string key)
    {
        return TryGetTable(parentTable, key, out var childTable)
            ? ParseOpaqueTable(childTable)
            : new Dictionary<string, object?>(StringComparer.Ordinal);
    }

    private static Dictionary<string, object?> ParseOpaqueTable(IDictionary<string, object> table)
    {
        var result = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var entry in table)
        {
            if (string.IsNullOrWhiteSpace(entry.Key))
            {
                continue;
            }

            if (!TryNormalizeOpaqueValue(entry.Value, out var normalized))
            {
                continue;
            }

            result[entry.Key] = normalized;
        }

        return result;
    }

    private static bool TryNormalizeOpaqueValue(object? rawValue, out object? normalizedValue)
    {
        normalizedValue = null;
        if (!SaveValueConverter.TryToDto(rawValue, out var dto, out _))
        {
            return false;
        }

        return SaveValueConverter.TryFromDto(dto, out normalizedValue, out _);
    }

    private static List<WorkspacePaneKind> ParsePaneKindArray(IEnumerable values)
    {
        var result = new List<WorkspacePaneKind>();
        foreach (var value in values)
        {
            if (value is not string token ||
                !WorkspaceKindTextCodec.TryParsePaneTomlToken(token, out var paneKind))
            {
                continue;
            }

            result.Add(paneKind);
        }

        return result;
    }

    private static TomlTable CopyTable(IEnumerable<KeyValuePair<string, object?>> values)
    {
        var result = new TomlTable();
        foreach (var pair in values)
        {
            result[pair.Key] = CopyValue(pair.Value);
        }

        return result;
    }

    private static object? CopyValue(object? value)
    {
        return value switch
        {
            null => null,
            IEnumerable<KeyValuePair<string, object?>> nullableDictionary => CopyTable(nullableDictionary),
            IDictionary dictionary => CopyDictionary(dictionary),
            IEnumerable enumerable when value is not string => CopyList(enumerable),
            _ => value,
        };
    }

    private static TomlTable CopyDictionary(IDictionary dictionary)
    {
        var result = new TomlTable();
        foreach (DictionaryEntry entry in dictionary)
        {
            var key = Convert.ToString(entry.Key, CultureInfo.InvariantCulture);
            if (string.IsNullOrWhiteSpace(key))
            {
                continue;
            }

            result[key] = CopyValue(entry.Value);
        }

        return result;
    }

    private static TomlArray CopyList(IEnumerable enumerable)
    {
        var result = new TomlArray();
        foreach (var item in enumerable)
        {
            result.Add(CopyValue(item));
        }

        return result;
    }

    private static bool TryGetTable(
        IDictionary<string, object> table,
        string key,
        out IDictionary<string, object> childTable)
    {
        childTable = null!;
        if (!table.TryGetValue(key, out var value))
        {
            return false;
        }

        if (value is not IDictionary<string, object> dictionary)
        {
            return false;
        }

        childTable = dictionary;
        return true;
    }

    private static bool TryGetArray(IDictionary<string, object> table, string key, out IEnumerable values)
    {
        values = null!;
        if (!table.TryGetValue(key, out var value))
        {
            return false;
        }

        if (value is string || value is not IEnumerable enumerable)
        {
            return false;
        }

        values = enumerable;
        return true;
    }

    private static string? ReadString(IDictionary<string, object> table, string key)
    {
        return table.TryGetValue(key, out var value) ? value as string : null;
    }

    private static int ReadInt32(IDictionary<string, object> table, string key, int fallback)
    {
        if (!table.TryGetValue(key, out var value))
        {
            return fallback;
        }

        return value switch
        {
            int intValue => intValue,
            long longValue when longValue >= int.MinValue && longValue <= int.MaxValue => (int)longValue,
            double doubleValue when doubleValue >= int.MinValue && doubleValue <= int.MaxValue => (int)doubleValue,
            _ => fallback,
        };
    }

    private static float ReadSingle(IDictionary<string, object> table, string key, float fallback)
    {
        if (!table.TryGetValue(key, out var value))
        {
            return fallback;
        }

        return value switch
        {
            float floatValue => floatValue,
            double doubleValue => (float)doubleValue,
            int intValue => intValue,
            long longValue => longValue,
            _ => fallback,
        };
    }
}
