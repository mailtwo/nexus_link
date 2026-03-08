using Godot;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

#nullable enable

namespace Uplink2.Runtime.Workspace.Ui;

/// <summary>Creates and caches concrete pane content nodes for the current renderer slice.</summary>
internal sealed class PaneContentFactory
{
    private const string TerminalPaneScenePath = "res://scenes/TerminalPane.tscn";
    private static readonly IReadOnlySet<WorkspacePaneKind> defaultImplementedPaneKinds =
        new ReadOnlySet<WorkspacePaneKind>(
            new HashSet<WorkspacePaneKind>
            {
                WorkspacePaneKind.Terminal,
                WorkspacePaneKind.WorldMapTrace,
            });

    private readonly PackedScene terminalPaneScene;
    private readonly Dictionary<WorkspacePaneKind, Control> cachedContentByKind = new();

    /// <summary>Initializes a new pane content factory.</summary>
    internal PaneContentFactory()
    {
        terminalPaneScene = GD.Load<PackedScene>(TerminalPaneScenePath)
            ?? throw new InvalidOperationException($"Failed to load terminal pane scene '{TerminalPaneScenePath}'.");

        ImplementedPaneKinds = defaultImplementedPaneKinds;
    }

    /// <summary>Gets the stable implemented-pane set for the current renderer slice.</summary>
    internal static IReadOnlySet<WorkspacePaneKind> DefaultImplementedPaneKinds => defaultImplementedPaneKinds;

    /// <summary>Gets pane kinds with concrete renderer implementations in the current slice.</summary>
    internal IReadOnlySet<WorkspacePaneKind> ImplementedPaneKinds { get; }

    /// <summary>Gets a reusable content node for the requested pane and renderer kind.</summary>
    internal Control GetContent(WorkspacePaneKind paneKind, WorkspaceRenderedContentKind contentKind)
    {
        return contentKind switch
        {
            WorkspaceRenderedContentKind.Implemented => GetOrCreateImplementedContent(paneKind),
            WorkspaceRenderedContentKind.Placeholder => GetOrCreatePlaceholderContent(paneKind),
            _ => throw new InvalidOperationException("Empty slots do not have reusable pane content."),
        };
    }

    /// <summary>Returns whether a node is managed by the reusable content cache.</summary>
    internal bool IsReusableContent(Node node)
    {
        if (node is null)
        {
            return false;
        }

        foreach (var cachedNode in cachedContentByKind.Values)
        {
            if (ReferenceEquals(cachedNode, node))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Immediately frees all cached reusable pane content.</summary>
    internal void DisposeCachedContent()
    {
        foreach (var cachedNode in cachedContentByKind.Values.ToArray())
        {
            if (!GodotObject.IsInstanceValid(cachedNode))
            {
                continue;
            }

            if (cachedNode.GetParent() is Node parent)
            {
                parent.RemoveChild(cachedNode);
            }

            cachedNode.Free();
        }

        cachedContentByKind.Clear();
    }

    /// <summary>Converts a pane kind to a stable display title.</summary>
    internal static string GetPaneTitle(WorkspacePaneKind paneKind)
    {
        return paneKind switch
        {
            WorkspacePaneKind.Terminal => "Terminal",
            WorkspacePaneKind.WebViewer => "Web Viewer",
            WorkspacePaneKind.CodeEditor => "Code Editor",
            WorkspacePaneKind.WorldMapTrace => "World Map Trace",
            WorkspacePaneKind.Mail => "Mail",
            WorkspacePaneKind.MissionPanel => "Mission Panel",
            _ => paneKind.ToString(),
        };
    }

    /// <summary>Converts a slot identifier to a stable display title.</summary>
    internal static string GetSlotTitle(DockSlot slot)
    {
        return slot switch
        {
            DockSlot.Left => "LEFT",
            DockSlot.RightTop => "RIGHT_TOP",
            DockSlot.RightBottom => "RIGHT_BOTTOM",
            _ => slot.ToString().ToUpperInvariant(),
        };
    }

    private Control GetOrCreateImplementedContent(WorkspacePaneKind paneKind)
    {
        if (cachedContentByKind.TryGetValue(paneKind, out var cached))
        {
            return cached;
        }

        Control created = paneKind switch
        {
            WorkspacePaneKind.Terminal => CreateTerminalPane(),
            WorkspacePaneKind.WorldMapTrace => CreateWorldMapTracePane(),
            _ => throw new InvalidOperationException($"No concrete renderer is implemented for pane '{paneKind}'."),
        };

        cachedContentByKind[paneKind] = created;
        return created;
    }

    private Control GetOrCreatePlaceholderContent(WorkspacePaneKind paneKind)
    {
        if (cachedContentByKind.TryGetValue(paneKind, out var cached))
        {
            return cached;
        }

        var created = CreatePlaceholderPane(paneKind);
        cachedContentByKind[paneKind] = created;
        return created;
    }

    private Control CreateTerminalPane()
    {
        var node = terminalPaneScene.Instantiate();
        if (node is not Control control)
        {
            node.QueueFree();
            throw new InvalidOperationException("Terminal pane scene root must inherit Control.");
        }

        control.Name = "TerminalPaneContent";
        PrepareContentNode(control);
        return control;
    }

    private static Control CreateWorldMapTracePane()
    {
        var control = new WorldMapTracePane
        {
            Name = "WorldMapTracePaneContent",
        };
        PrepareContentNode(control);
        return control;
    }

    private static Control CreatePlaceholderPane(WorkspacePaneKind paneKind)
    {
        var root = new PanelContainer
        {
            Name = paneKind + "PlaceholderContent",
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            SizeFlagsVertical = Control.SizeFlags.ExpandFill,
        };
        PrepareContentNode(root);
        root.AddThemeStyleboxOverride("panel", CreatePlaceholderStyleBox());

        var margin = new MarginContainer
        {
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            SizeFlagsVertical = Control.SizeFlags.ExpandFill,
        };
        PrepareContentNode(margin);
        margin.AddThemeConstantOverride("margin_left", 16);
        margin.AddThemeConstantOverride("margin_top", 16);
        margin.AddThemeConstantOverride("margin_right", 16);
        margin.AddThemeConstantOverride("margin_bottom", 16);
        root.AddChild(margin);

        var layout = new VBoxContainer
        {
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            SizeFlagsVertical = Control.SizeFlags.ExpandFill,
            Alignment = BoxContainer.AlignmentMode.Center,
        };
        PrepareContentNode(layout);
        layout.AddThemeConstantOverride("separation", 8);
        margin.AddChild(layout);

        var title = new Label
        {
            Text = GetPaneTitle(paneKind),
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        layout.AddChild(title);

        var message = new Label
        {
            Text = "이 pane renderer는 아직 구현되지 않았습니다.",
            HorizontalAlignment = HorizontalAlignment.Center,
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
        };
        message.AddThemeColorOverride("font_color", new Color(0.72f, 0.77f, 0.80f, 1.0f));
        layout.AddChild(message);

        return root;
    }

    private static StyleBoxFlat CreatePlaceholderStyleBox()
    {
        return new StyleBoxFlat
        {
            BgColor = new Color(0.07f, 0.09f, 0.11f, 1.0f),
            BorderColor = new Color(0.23f, 0.29f, 0.34f, 1.0f),
            BorderWidthLeft = 1,
            BorderWidthTop = 1,
            BorderWidthRight = 1,
            BorderWidthBottom = 1,
            CornerRadiusTopLeft = 6,
            CornerRadiusTopRight = 6,
            CornerRadiusBottomLeft = 6,
            CornerRadiusBottomRight = 6,
        };
    }

    private static void PrepareContentNode(Control control)
    {
        control.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        control.OffsetLeft = 0.0f;
        control.OffsetTop = 0.0f;
        control.OffsetRight = 0.0f;
        control.OffsetBottom = 0.0f;
        control.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        control.SizeFlagsVertical = Control.SizeFlags.ExpandFill;
    }
}
