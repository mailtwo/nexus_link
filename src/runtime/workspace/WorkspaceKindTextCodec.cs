using System;
using System.Collections.Generic;

#nullable enable

namespace Uplink2.Runtime.Workspace;

/// <summary>Converts workspace enum values to and from stable persistence tokens.</summary>
internal static class WorkspaceKindTextCodec
{
    private static readonly IReadOnlyDictionary<string, WorkspacePaneKind> PaneKindsByTomlToken =
        BuildPaneKindsByTomlToken();

    /// <summary>Converts a pane kind to its canonical TOML token.</summary>
    internal static string ToTomlToken(WorkspacePaneKind kind)
    {
        return ConvertPascalToSeparated(kind.ToString(), '_', uppercase: true);
    }

    /// <summary>Converts a pane kind to its canonical availability-flag segment.</summary>
    internal static string ToAvailabilityFlagSegment(WorkspacePaneKind kind)
    {
        return ConvertPascalToSeparated(kind.ToString(), '_', uppercase: false);
    }

    /// <summary>Converts a dock slot to its canonical TOML token.</summary>
    internal static string ToTomlToken(DockSlot slot)
    {
        return ConvertPascalToSeparated(slot.ToString(), '_', uppercase: true);
    }

    /// <summary>Converts a workspace mode to its canonical TOML token.</summary>
    internal static string ToTomlToken(WorkspaceMode mode)
    {
        return mode switch
        {
            WorkspaceMode.Docked => "DOCKED",
            WorkspaceMode.Maximized => "MAXIMIZED",
            _ => mode.ToString().ToUpperInvariant(),
        };
    }

    /// <summary>Tries to parse a pane-kind TOML token.</summary>
    internal static bool TryParsePaneTomlToken(string? token, out WorkspacePaneKind kind)
    {
        if (!string.IsNullOrWhiteSpace(token) &&
            PaneKindsByTomlToken.TryGetValue(token.Trim(), out kind))
        {
            return true;
        }

        kind = default;
        return false;
    }

    /// <summary>Tries to parse a dock-slot TOML token.</summary>
    internal static bool TryParseDockSlotTomlToken(string? token, out DockSlot slot)
    {
        var trimmed = token?.Trim();
        switch (trimmed)
        {
            case "LEFT":
                slot = DockSlot.Left;
                return true;
            case "RIGHT_TOP":
                slot = DockSlot.RightTop;
                return true;
            case "RIGHT_BOTTOM":
                slot = DockSlot.RightBottom;
                return true;
            default:
                slot = default;
                return false;
        }
    }

    /// <summary>Tries to parse a workspace-mode TOML token.</summary>
    internal static bool TryParseModeTomlToken(string? token, out WorkspaceMode mode)
    {
        var trimmed = token?.Trim();
        switch (trimmed)
        {
            case "DOCKED":
                mode = WorkspaceMode.Docked;
                return true;
            case "MAXIMIZED":
                mode = WorkspaceMode.Maximized;
                return true;
            default:
                mode = default;
                return false;
        }
    }

    private static Dictionary<string, WorkspacePaneKind> BuildPaneKindsByTomlToken()
    {
        var result = new Dictionary<string, WorkspacePaneKind>(StringComparer.Ordinal);
        foreach (WorkspacePaneKind paneKind in Enum.GetValues(typeof(WorkspacePaneKind)))
        {
            result[ToTomlToken(paneKind)] = paneKind;
        }

        return result;
    }

    private static string ConvertPascalToSeparated(string value, char separator, bool uppercase)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var buffer = new System.Text.StringBuilder(value.Length + 8);
        for (var index = 0; index < value.Length; index++)
        {
            var current = value[index];
            if (char.IsUpper(current) && index > 0)
            {
                var previous = value[index - 1];
                var nextIsLower = index + 1 < value.Length && char.IsLower(value[index + 1]);
                if (char.IsLower(previous) || nextIsLower)
                {
                    buffer.Append(separator);
                }
            }

            buffer.Append(uppercase
                ? char.ToUpperInvariant(current)
                : char.ToLowerInvariant(current));
        }

        return buffer.ToString();
    }
}
