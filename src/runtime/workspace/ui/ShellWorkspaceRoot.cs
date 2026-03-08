using Godot;
using System;
using System.Collections.Generic;

#nullable enable

namespace Uplink2.Runtime.Workspace.Ui;

/// <summary>Scene root that renders the stage-3A NEXUS Shell workspace from <see cref="ShellWorkspaceRuntime"/> state.</summary>
public partial class ShellWorkspaceRoot : Control
{
    private readonly WorkspaceLayoutDefinition layoutDefinition = WorkspaceLayoutDefinition.CreateAlpha();
    private readonly Dictionary<DockSlot, WorkspaceSlotView> slotViews = new();
    private readonly Dictionary<WorkspaceSplitRatioBinding, SplitContainer> splitContainers = new();

    private ShellWorkspaceRuntime? workspaceRuntime;
    private PaneContentFactory? paneContentFactory;
    private WorkspaceStateSnapshot? currentSnapshot;
    private WorkspaceDisplayModel? currentDisplayModel;
    private Control? dockedHost;
    private Control? maximizedHost;
    private Label? maximizedTitleLabel;
    private Button? maximizedRestoreButton;
    private Control? maximizedContentHost;

    /// <inheritdoc/>
    public override void _Ready()
    {
        dockedHost = GetNode<Control>("DockedHost");
        maximizedHost = GetNode<Control>("MaximizedHost");
        paneContentFactory = new PaneContentFactory();
        workspaceRuntime = ShellWorkspaceRuntime.Instance;
        if (workspaceRuntime is null)
        {
            GD.PushError("ShellWorkspaceRoot: '/root/ShellWorkspaceRuntime' not found.");
            return;
        }

        BuildDockedChrome();
        BuildMaximizedChrome();

        workspaceRuntime.ResetToDefaultState();
        workspaceRuntime.StateChanged += OnWorkspaceStateChanged;

        ApplyCurrentWorkspaceState();
    }

    /// <inheritdoc/>
    public override void _ExitTree()
    {
        if (workspaceRuntime is not null)
        {
            workspaceRuntime.StateChanged -= OnWorkspaceStateChanged;
        }
    }

    /// <inheritdoc/>
    public override void _Notification(int what)
    {
        if (what == NotificationResized && currentSnapshot is not null)
        {
            CallDeferred(MethodName.ApplyCurrentSplitRatios);
        }
    }

    private void BuildDockedChrome()
    {
        if (dockedHost is null)
        {
            return;
        }

        slotViews.Clear();
        splitContainers.Clear();
        ClearDisposableChildren(dockedHost);

        var layoutRoot = BuildDockedLayoutNode(layoutDefinition.Root);
        PrepareFillControl(layoutRoot);
        dockedHost.AddChild(layoutRoot);
    }

    private void BuildMaximizedChrome()
    {
        if (maximizedHost is null)
        {
            return;
        }

        ClearDisposableChildren(maximizedHost);

        var frame = new PanelContainer
        {
            Name = "MaximizedFrame",
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            SizeFlagsVertical = Control.SizeFlags.ExpandFill,
        };
        frame.AddThemeStyleboxOverride("panel", CreateFrameStyleBox());
        PrepareFillControl(frame);
        maximizedHost.AddChild(frame);

        var margin = new MarginContainer
        {
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            SizeFlagsVertical = Control.SizeFlags.ExpandFill,
        };
        PrepareFillControl(margin);
        margin.AddThemeConstantOverride("margin_left", 12);
        margin.AddThemeConstantOverride("margin_top", 12);
        margin.AddThemeConstantOverride("margin_right", 12);
        margin.AddThemeConstantOverride("margin_bottom", 12);
        frame.AddChild(margin);

        var layout = new VBoxContainer
        {
            Name = "MaximizedLayout",
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            SizeFlagsVertical = Control.SizeFlags.ExpandFill,
        };
        PrepareFillControl(layout);
        layout.AddThemeConstantOverride("separation", 8);
        margin.AddChild(layout);

        var header = new HBoxContainer
        {
            Name = "MaximizedHeader",
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
        };
        header.CustomMinimumSize = new Vector2(0.0f, 34.0f);
        header.AddThemeConstantOverride("separation", 8);
        layout.AddChild(header);

        maximizedTitleLabel = new Label
        {
            Name = "MaximizedTitle",
            Text = string.Empty,
            VerticalAlignment = VerticalAlignment.Center,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
        };
        header.AddChild(maximizedTitleLabel);

        maximizedRestoreButton = new Button
        {
            Name = "RestoreButton",
            Text = "Restore",
            TooltipText = "Return to docked layout",
            SizeFlagsHorizontal = Control.SizeFlags.ShrinkEnd,
        };
        maximizedRestoreButton.Pressed += OnRestorePressed;
        header.AddChild(maximizedRestoreButton);

        var bodyFrame = new PanelContainer
        {
            Name = "MaximizedBody",
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            SizeFlagsVertical = Control.SizeFlags.ExpandFill,
        };
        bodyFrame.AddThemeStyleboxOverride("panel", CreateBodyStyleBox());
        layout.AddChild(bodyFrame);

        var bodyMargin = new MarginContainer
        {
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            SizeFlagsVertical = Control.SizeFlags.ExpandFill,
        };
        PrepareFillControl(bodyMargin);
        bodyMargin.AddThemeConstantOverride("margin_left", 10);
        bodyMargin.AddThemeConstantOverride("margin_top", 10);
        bodyMargin.AddThemeConstantOverride("margin_right", 10);
        bodyMargin.AddThemeConstantOverride("margin_bottom", 10);
        bodyFrame.AddChild(bodyMargin);

        maximizedContentHost = new Control
        {
            Name = "MaximizedContentHost",
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            SizeFlagsVertical = Control.SizeFlags.ExpandFill,
        };
        PrepareFillControl(maximizedContentHost);
        bodyMargin.AddChild(maximizedContentHost);
    }

    private Control BuildDockedLayoutNode(WorkspaceLayoutNodeDefinition node)
    {
        if (node is WorkspaceSlotLeafDefinition slotLeaf)
        {
            var slotView = new WorkspaceSlotView(slotLeaf.Slot, OnMaximizeRequested);
            slotViews[slotLeaf.Slot] = slotView;
            return slotView.Root;
        }

        if (node is not WorkspaceSplitNodeDefinition splitNode)
        {
            throw new InvalidOperationException("Unknown workspace layout node.");
        }

        SplitContainer split = splitNode.Axis switch
        {
            WorkspaceSplitAxis.Horizontal => new HSplitContainer(),
            WorkspaceSplitAxis.Vertical => new VSplitContainer(),
            _ => throw new InvalidOperationException("Unknown split axis."),
        };

        split.Name = splitNode.RatioBinding + "Split";
        split.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        split.SizeFlagsVertical = Control.SizeFlags.ExpandFill;
        split.DraggerVisibility = SplitContainer.DraggerVisibilityEnum.Hidden;
        splitContainers[splitNode.RatioBinding] = split;

        var firstChild = BuildDockedLayoutNode(splitNode.FirstChild);
        var secondChild = BuildDockedLayoutNode(splitNode.SecondChild);
        firstChild.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        firstChild.SizeFlagsVertical = Control.SizeFlags.ExpandFill;
        secondChild.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        secondChild.SizeFlagsVertical = Control.SizeFlags.ExpandFill;

        split.AddChild(firstChild);
        split.AddChild(secondChild);
        return split;
    }

    private void ApplyCurrentWorkspaceState()
    {
        if (workspaceRuntime is null || paneContentFactory is null)
        {
            return;
        }

        currentSnapshot = workspaceRuntime.GetSnapshot();
        currentDisplayModel = WorkspaceDisplayModelBuilder.Build(
            currentSnapshot,
            layoutDefinition,
            paneContentFactory.ImplementedPaneKinds);

        ApplyDisplayModel(currentDisplayModel);
    }

    private void ApplyDisplayModel(WorkspaceDisplayModel displayModel)
    {
        if (dockedHost is null || maximizedHost is null || paneContentFactory is null)
        {
            return;
        }

        dockedHost.Visible = displayModel.Mode == WorkspaceMode.Docked;
        maximizedHost.Visible = displayModel.Mode == WorkspaceMode.Maximized;

        foreach (var pair in displayModel.DockedSlots)
        {
            ApplySlotDisplayModel(slotViews[pair.Key], pair.Value);
        }

        ApplyMaximizedDisplayModel(displayModel.MaximizedPane);
        CallDeferred(MethodName.ApplyCurrentSplitRatios);
    }

    private void ApplySlotDisplayModel(WorkspaceSlotView view, WorkspaceSlotDisplayModel slotModel)
    {
        if (paneContentFactory is null)
        {
            return;
        }

        view.TitleLabel.Text = slotModel.DisplayedPane.HasValue
            ? PaneContentFactory.GetPaneTitle(slotModel.DisplayedPane.Value)
            : slotModel.SlotTitle;
        view.MaximizeButton.Visible = slotModel.DisplayedPane.HasValue;

        RebuildSlotTabs(view, slotModel);

        if (!slotModel.DisplayedPane.HasValue)
        {
            MountContent(view.ContentHost, null, CreateEmptySlotContent(slotModel.SlotTitle));
            return;
        }

        var content = paneContentFactory.GetContent(slotModel.DisplayedPane.Value, slotModel.ContentKind);
        MountContent(view.ContentHost, content, null);
    }

    private void RebuildSlotTabs(WorkspaceSlotView view, WorkspaceSlotDisplayModel slotModel)
    {
        ClearDisposableChildren(view.TabStrip);
        view.TabStrip.Visible = slotModel.Tabs.Count > 0;
        if (slotModel.Tabs.Count == 0)
        {
            return;
        }

        foreach (var pane in slotModel.Tabs)
        {
            var button = new Button
            {
                Text = PaneContentFactory.GetPaneTitle(pane),
                Flat = true,
                ToggleMode = true,
                ButtonPressed = slotModel.DisplayedPane == pane,
                TooltipText = PaneContentFactory.GetPaneTitle(pane),
                SizeFlagsHorizontal = Control.SizeFlags.ShrinkCenter,
            };
            if (slotModel.DisplayedPane == pane)
            {
                button.Disabled = true;
            }

            button.Pressed += () => OnTabRequested(pane);
            view.TabStrip.AddChild(button);
        }
    }

    private void ApplyMaximizedDisplayModel(WorkspaceMaximizedDisplayModel? maximizedModel)
    {
        if (maximizedTitleLabel is null || maximizedRestoreButton is null || maximizedContentHost is null || paneContentFactory is null)
        {
            return;
        }

        if (maximizedModel is null)
        {
            maximizedTitleLabel.Text = string.Empty;
            maximizedRestoreButton.Visible = false;
            MountContent(maximizedContentHost, null, CreateEmptyMaximizedContent());
            return;
        }

        maximizedTitleLabel.Text = PaneContentFactory.GetPaneTitle(maximizedModel.PaneKind);
        maximizedRestoreButton.Visible = true;
        var content = paneContentFactory.GetContent(maximizedModel.PaneKind, maximizedModel.ContentKind);
        MountContent(maximizedContentHost, content, null);
    }

    private void MountContent(Control host, Control? reusableContent, Control? ephemeralContent)
    {
        if (paneContentFactory is null)
        {
            return;
        }

        var existingChildren = new List<Node>();
        foreach (var child in host.GetChildren())
        {
            if (child is Node node)
            {
                existingChildren.Add(node);
            }
        }

        foreach (var child in existingChildren)
        {
            var keepReusable = reusableContent is not null && ReferenceEquals(child, reusableContent);
            var keepEphemeral = ephemeralContent is not null && ReferenceEquals(child, ephemeralContent);
            if (keepReusable || keepEphemeral)
            {
                continue;
            }

            host.RemoveChild(child);
            if (!paneContentFactory.IsReusableContent(child))
            {
                child.QueueFree();
            }
        }

        if (reusableContent is not null)
        {
            if (reusableContent.GetParent() is Node currentParent && !ReferenceEquals(currentParent, host))
            {
                currentParent.RemoveChild(reusableContent);
            }

            if (!ReferenceEquals(reusableContent.GetParent(), host))
            {
                host.AddChild(reusableContent);
            }

            PrepareFillControl(reusableContent);
            return;
        }

        if (ephemeralContent is null)
        {
            return;
        }

        if (!ReferenceEquals(ephemeralContent.GetParent(), host))
        {
            host.AddChild(ephemeralContent);
        }

        PrepareFillControl(ephemeralContent);
    }

    private void ApplyCurrentSplitRatios()
    {
        if (currentSnapshot is null)
        {
            return;
        }

        if (splitContainers.TryGetValue(WorkspaceSplitRatioBinding.LeftColumn, out var leftSplit))
        {
            leftSplit.SplitOffsets = [Mathf.RoundToInt(leftSplit.Size.X * currentSnapshot.LeftRatio)];
        }

        if (splitContainers.TryGetValue(WorkspaceSplitRatioBinding.RightTop, out var rightSplit))
        {
            rightSplit.SplitOffsets = [Mathf.RoundToInt(rightSplit.Size.Y * currentSnapshot.RightTopRatio)];
        }
    }

    private void OnWorkspaceStateChanged()
    {
        ApplyCurrentWorkspaceState();
    }

    private void OnRestorePressed()
    {
        workspaceRuntime?.RestoreDocked();
    }

    private void OnMaximizeRequested(DockSlot slot)
    {
        if (workspaceRuntime is null || currentDisplayModel is null)
        {
            return;
        }

        if (!currentDisplayModel.DockedSlots.TryGetValue(slot, out var slotModel) || !slotModel.DisplayedPane.HasValue)
        {
            return;
        }

        workspaceRuntime.MaximizePane(slotModel.DisplayedPane.Value);
    }

    private void OnTabRequested(WorkspacePaneKind paneKind)
    {
        workspaceRuntime?.ActivatePane(paneKind);
    }

    private static void ClearDisposableChildren(Node parent)
    {
        foreach (var child in parent.GetChildren())
        {
            if (child is not Node node)
            {
                continue;
            }

            parent.RemoveChild(node);
            node.QueueFree();
        }
    }

    private static Control CreateEmptySlotContent(string slotTitle)
    {
        var root = new VBoxContainer
        {
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            SizeFlagsVertical = Control.SizeFlags.ExpandFill,
            Alignment = BoxContainer.AlignmentMode.Center,
        };
        PrepareFillControl(root);
        root.AddThemeConstantOverride("separation", 8);

        var title = new Label
        {
            Text = slotTitle,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        root.AddChild(title);

        var message = new Label
        {
            Text = "표시할 pane이 없습니다.",
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        message.AddThemeColorOverride("font_color", new Color(0.62f, 0.69f, 0.74f, 1.0f));
        root.AddChild(message);
        return root;
    }

    private static Control CreateEmptyMaximizedContent()
    {
        var label = new Label
        {
            Text = "표시할 maximized pane이 없습니다.",
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        PrepareFillControl(label);
        label.AddThemeColorOverride("font_color", new Color(0.62f, 0.69f, 0.74f, 1.0f));
        return label;
    }

    private static StyleBoxFlat CreateFrameStyleBox()
    {
        return new StyleBoxFlat
        {
            BgColor = new Color(0.06f, 0.08f, 0.10f, 1.0f),
            BorderColor = new Color(0.18f, 0.26f, 0.22f, 1.0f),
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

    private static StyleBoxFlat CreateBodyStyleBox()
    {
        return new StyleBoxFlat
        {
            BgColor = new Color(0.05f, 0.07f, 0.09f, 1.0f),
            BorderColor = new Color(0.15f, 0.19f, 0.22f, 1.0f),
            BorderWidthLeft = 1,
            BorderWidthTop = 1,
            BorderWidthRight = 1,
            BorderWidthBottom = 1,
            CornerRadiusTopLeft = 4,
            CornerRadiusTopRight = 4,
            CornerRadiusBottomLeft = 4,
            CornerRadiusBottomRight = 4,
        };
    }

    private static void PrepareFillControl(Control control)
    {
        control.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        control.OffsetLeft = 0.0f;
        control.OffsetTop = 0.0f;
        control.OffsetRight = 0.0f;
        control.OffsetBottom = 0.0f;
    }

    private sealed class WorkspaceSlotView
    {
        internal WorkspaceSlotView(
            DockSlot slot,
            Action<DockSlot> maximizeHandler)
        {
            Slot = slot;
            Root = new PanelContainer
            {
                Name = slot + "Slot",
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                SizeFlagsVertical = Control.SizeFlags.ExpandFill,
            };
            Root.AddThemeStyleboxOverride("panel", CreateFrameStyleBox());

            var margin = new MarginContainer
            {
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                SizeFlagsVertical = Control.SizeFlags.ExpandFill,
            };
            PrepareFillControl(margin);
            margin.AddThemeConstantOverride("margin_left", 10);
            margin.AddThemeConstantOverride("margin_top", 10);
            margin.AddThemeConstantOverride("margin_right", 10);
            margin.AddThemeConstantOverride("margin_bottom", 10);
            Root.AddChild(margin);

            var layout = new VBoxContainer
            {
                Name = slot + "Layout",
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                SizeFlagsVertical = Control.SizeFlags.ExpandFill,
            };
            PrepareFillControl(layout);
            layout.AddThemeConstantOverride("separation", 8);
            margin.AddChild(layout);

            var header = new HBoxContainer
            {
                Name = slot + "Header",
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            };
            header.CustomMinimumSize = new Vector2(0.0f, 32.0f);
            header.AddThemeConstantOverride("separation", 8);
            layout.AddChild(header);

            TitleLabel = new Label
            {
                Name = slot + "Title",
                VerticalAlignment = VerticalAlignment.Center,
            };
            TitleLabel.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
            header.AddChild(TitleLabel);

            TabStrip = new HBoxContainer
            {
                Name = slot + "Tabs",
                SizeFlagsHorizontal = Control.SizeFlags.ShrinkEnd,
            };
            TabStrip.AddThemeConstantOverride("separation", 4);
            header.AddChild(TabStrip);

            MaximizeButton = new Button
            {
                Name = slot + "Maximize",
                Text = "Maximize",
                TooltipText = "Maximize this pane",
                SizeFlagsHorizontal = Control.SizeFlags.ShrinkEnd,
            };
            MaximizeButton.Pressed += () => maximizeHandler(slot);
            header.AddChild(MaximizeButton);

            var body = new PanelContainer
            {
                Name = slot + "Body",
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                SizeFlagsVertical = Control.SizeFlags.ExpandFill,
            };
            body.AddThemeStyleboxOverride("panel", CreateBodyStyleBox());
            layout.AddChild(body);

            var bodyMargin = new MarginContainer
            {
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                SizeFlagsVertical = Control.SizeFlags.ExpandFill,
            };
            PrepareFillControl(bodyMargin);
            bodyMargin.AddThemeConstantOverride("margin_left", 8);
            bodyMargin.AddThemeConstantOverride("margin_top", 8);
            bodyMargin.AddThemeConstantOverride("margin_right", 8);
            bodyMargin.AddThemeConstantOverride("margin_bottom", 8);
            body.AddChild(bodyMargin);

            ContentHost = new Control
            {
                Name = slot + "ContentHost",
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                SizeFlagsVertical = Control.SizeFlags.ExpandFill,
            };
            PrepareFillControl(ContentHost);
            bodyMargin.AddChild(ContentHost);
        }

        internal DockSlot Slot { get; }

        internal PanelContainer Root { get; }

        internal Label TitleLabel { get; }

        internal HBoxContainer TabStrip { get; }

        internal Button MaximizeButton { get; }

        internal Control ContentHost { get; }
    }
}
