using Godot;
using System;
using System.Collections.Generic;
using System.Linq;

#nullable enable

namespace Uplink2.Runtime.Workspace.Ui;

/// <summary>Scene root that renders the stage-3A NEXUS Shell workspace from <see cref="ShellWorkspaceRuntime"/> state.</summary>
public partial class ShellWorkspaceRoot : Control
{
    private readonly WorkspaceLayoutDefinition layoutDefinition = WorkspaceLayoutDefinition.CreateAlpha();
    private readonly Dictionary<DockSlot, WorkspaceSlotView> slotViews = new();
    private readonly Dictionary<WorkspaceSplitRatioBinding, SplitContainer> splitContainers = new();

    private global::Uplink2.Runtime.WorldRuntime? runtimeRoot;
    private ShellWorkspaceRuntime? workspaceRuntime;
    private PaneContentFactory? paneContentFactory;
    private WorkspaceStateSnapshot? currentSnapshot;
    private WorkspaceDisplayModel? currentDisplayModel;
    private WorkspaceConstraintSnapshot? currentConstraintSnapshot;
    private Control? preShellHost;
    private Control? preShellContentHost;
    private Control? dockedHost;
    private Control? maximizedHost;
    private Control? taskbarHost;
    private Control? feedbackLayer;
    private PopupMenu? taskbarContextMenu;
    private Label? maximizedTitleLabel;
    private Button? maximizedRestoreButton;
    private Control? maximizedContentHost;
    private Button? taskbarStartPlaceholderButton;
    private HBoxContainer? taskbarButtonsStrip;
    private readonly Dictionary<WorkspacePaneKind, Button> taskbarButtons = new();
    private readonly Dictionary<DockSlot, WorkspaceSlotChromeMetrics> slotChromeMetrics = new();
    private WorkspacePaneKind? taskbarContextPaneKind;
    private Rect2? taskbarContextButtonRect;
    private WorkspaceSplitRatioBinding? activeDraggedSplitBinding;
    private bool isApplyingSplitLayout;

    private const int TaskbarContextMenuPinId = 1;
    private const int TaskbarMinHeight = 44;
    private const float TaskbarFeedbackHoldSeconds = 0.5f;
    private const float TaskbarFeedbackFadeSeconds = 0.5f;
    private const float SplitRatioSyncEpsilon = 0.0005f;

    /// <inheritdoc/>
    public override void _Ready()
    {
        runtimeRoot = global::Uplink2.Runtime.WorldRuntime.Instance;
        preShellHost = GetNode<Control>("MainLayout/WorkspaceArea/PreShellHost");
        dockedHost = GetNode<Control>("MainLayout/WorkspaceArea/DockedHost");
        maximizedHost = GetNode<Control>("MainLayout/WorkspaceArea/MaximizedHost");
        taskbarHost = GetNode<Control>("MainLayout/TaskbarHost");
        feedbackLayer = GetNode<Control>("FeedbackLayer");
        taskbarContextMenu = GetNode<PopupMenu>("TaskbarContextMenu");
        paneContentFactory = new PaneContentFactory();
        workspaceRuntime = ShellWorkspaceRuntime.Instance;
        if (workspaceRuntime is null)
        {
            GD.PushError("ShellWorkspaceRoot: '/root/ShellWorkspaceRuntime' not found.");
            return;
        }

        BuildPreShellChrome();
        BuildDockedChrome();
        BuildMaximizedChrome();
        BuildTaskbarChrome();
        ConfigureTaskbarContextMenu();

        workspaceRuntime.ResetToDefaultState();
        workspaceRuntime.StateChanged += OnWorkspaceStateChanged;
        if (runtimeRoot is not null)
        {
            runtimeRoot.NexusShellOpenStateChanged += OnNexusShellOpenStateChanged;
        }

        if (runtimeRoot?.IsNexusShellOpen == true)
        {
            ApplyDevelopmentBootstrapOverride();
        }

        ApplyCurrentWorkspaceState();
    }

    /// <inheritdoc/>
    public override void _ExitTree()
    {
        if (runtimeRoot is not null)
        {
            runtimeRoot.NexusShellOpenStateChanged -= OnNexusShellOpenStateChanged;
        }

        if (workspaceRuntime is not null)
        {
            workspaceRuntime.StateChanged -= OnWorkspaceStateChanged;
        }

        if (taskbarContextMenu is not null)
        {
            taskbarContextMenu.IdPressed -= OnTaskbarContextMenuIdPressed;
        }

        paneContentFactory?.DisposeCachedContent();
        paneContentFactory = null;
        runtimeRoot = null;
    }

    /// <inheritdoc/>
    public override void _Notification(int what)
    {
        if (what == NotificationResized && currentSnapshot is not null)
        {
            CallDeferred(MethodName.ApplyCurrentSplitRatios);
        }
    }

    /// <inheritdoc/>
    public override void _Input(InputEvent @event)
    {
        if (@event is not InputEventMouseButton mouseButton ||
            !mouseButton.Pressed ||
            mouseButton.ButtonIndex != MouseButton.Left ||
            workspaceRuntime is null ||
            currentDisplayModel is null)
        {
            return;
        }

        if (currentDisplayModel.Mode == WorkspaceMode.Maximized &&
            currentDisplayModel.MaximizedPane is not null &&
            maximizedContentHost is not null &&
            IsPointerInsideControl(maximizedContentHost, mouseButton.Position))
        {
            workspaceRuntime.FocusPane(currentDisplayModel.MaximizedPane.PaneKind);
            return;
        }

        foreach (var pair in currentDisplayModel.DockedSlots)
        {
            if (!pair.Value.DisplayedPane.HasValue)
            {
                continue;
            }

            var view = slotViews[pair.Key];
            if (IsPointerInsideControl(view.ContentHost, mouseButton.Position))
            {
                workspaceRuntime.FocusPane(pair.Value.DisplayedPane.Value);
                return;
            }
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
        slotChromeMetrics.Clear();
        ClearDisposableChildren(dockedHost);

        var layoutRoot = BuildDockedLayoutNode(layoutDefinition.Root);
        PrepareFillControl(layoutRoot);
        dockedHost.AddChild(layoutRoot);
        foreach (var pair in slotViews)
        {
            slotChromeMetrics[pair.Key] = pair.Value.MeasureChromeMetrics();
        }
    }

    private void BuildPreShellChrome()
    {
        if (preShellHost is null)
        {
            return;
        }

        ClearDisposableChildren(preShellHost);

        var frame = new PanelContainer
        {
            Name = "PreShellFrame",
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            SizeFlagsVertical = Control.SizeFlags.ExpandFill,
        };
        frame.AddThemeStyleboxOverride("panel", CreateFrameStyleBox());
        PrepareFillControl(frame);
        preShellHost.AddChild(frame);

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
        frame.AddChild(margin);

        preShellContentHost = new Control
        {
            Name = "PreShellContentHost",
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            SizeFlagsVertical = Control.SizeFlags.ExpandFill,
        };
        PrepareFillControl(preShellContentHost);
        margin.AddChild(preShellContentHost);
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
            ClipText = true,
        };
        header.AddChild(maximizedTitleLabel);

        maximizedRestoreButton = new Button
        {
            Name = "RestoreButton",
            Text = "Restore",
            TooltipText = "Return to docked layout",
            SizeFlagsHorizontal = Control.SizeFlags.ShrinkEnd,
            CustomMinimumSize = new Vector2(86.0f, 0.0f),
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

    private void BuildTaskbarChrome()
    {
        if (taskbarHost is null)
        {
            return;
        }

        taskbarButtons.Clear();
        ClearDisposableChildren(taskbarHost);

        var frame = new PanelContainer
        {
            Name = "TaskbarFrame",
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            CustomMinimumSize = new Vector2(0.0f, TaskbarMinHeight),
        };
        frame.AddThemeStyleboxOverride("panel", CreateTaskbarFrameStyleBox());
        PrepareFillControl(frame);
        taskbarHost.AddChild(frame);

        var margin = new MarginContainer
        {
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            SizeFlagsVertical = Control.SizeFlags.ExpandFill,
        };
        PrepareFillControl(margin);
        margin.AddThemeConstantOverride("margin_left", 8);
        margin.AddThemeConstantOverride("margin_top", 6);
        margin.AddThemeConstantOverride("margin_right", 8);
        margin.AddThemeConstantOverride("margin_bottom", 6);
        frame.AddChild(margin);

        var row = new HBoxContainer
        {
            Name = "TaskbarRow",
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            SizeFlagsVertical = Control.SizeFlags.ExpandFill,
        };
        PrepareFillControl(row);
        row.AddThemeConstantOverride("separation", 8);
        margin.AddChild(row);

        taskbarStartPlaceholderButton = new Button
        {
            Name = "TaskbarStartPlaceholder",
            Text = "Start",
            Disabled = true,
            TooltipText = "Start menu not implemented yet.",
            CustomMinimumSize = new Vector2(86.0f, 30.0f),
            SizeFlagsHorizontal = Control.SizeFlags.ShrinkBegin,
        };
        ApplyStartPlaceholderStyle(taskbarStartPlaceholderButton);
        row.AddChild(taskbarStartPlaceholderButton);

        taskbarButtonsStrip = new HBoxContainer
        {
            Name = "TaskbarButtons",
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            SizeFlagsVertical = Control.SizeFlags.ExpandFill,
        };
        taskbarButtonsStrip.AddThemeConstantOverride("separation", 6);
        row.AddChild(taskbarButtonsStrip);
    }

    private void ConfigureTaskbarContextMenu()
    {
        if (taskbarContextMenu is null)
        {
            return;
        }

        taskbarContextMenu.IdPressed += OnTaskbarContextMenuIdPressed;
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
        split.DragStarted += () => OnSplitDragStarted(splitNode.RatioBinding);
        split.DragEnded += OnSplitDragEnded;
        split.Resized += () => OnSplitResized(splitNode.RatioBinding);
        var dragAreaControls = split.GetDragAreaControls();
        if (dragAreaControls.Count > 0)
        {
            dragAreaControls[0].GuiInput += @event => OnSplitDragAreaGuiInput(splitNode.RatioBinding, @event);
        }
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
        currentConstraintSnapshot = WorkspaceConstraintResolver.Resolve(
            currentDisplayModel,
            layoutDefinition,
            slotChromeMetrics);

        ApplyDisplayModel(currentDisplayModel);
    }

    private void ApplyDisplayModel(WorkspaceDisplayModel displayModel)
    {
        if (dockedHost is null || maximizedHost is null || paneContentFactory is null)
        {
            return;
        }

        var isShellOpen = runtimeRoot?.IsNexusShellOpen == true;
        if (preShellHost is not null)
        {
            preShellHost.Visible = !isShellOpen;
        }

        dockedHost.Visible = isShellOpen && displayModel.Mode == WorkspaceMode.Docked;
        maximizedHost.Visible = isShellOpen && displayModel.Mode == WorkspaceMode.Maximized;
        if (taskbarHost is not null)
        {
            taskbarHost.Visible = isShellOpen;
        }

        foreach (var pair in displayModel.DockedSlots)
        {
            ApplySlotDisplayModel(slotViews[pair.Key], pair.Value);
        }

        ApplySlotMinimumSizes(displayModel);
        ApplyMaximizedDisplayModel(displayModel.MaximizedPane);
        if (isShellOpen)
        {
            RebuildTaskbar(displayModel);
        }
        else if (taskbarButtonsStrip is not null)
        {
            taskbarButtons.Clear();
            ClearDisposableChildren(taskbarButtonsStrip);
        }

        ApplyPreShellDisplay(isShellOpen);
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
        ApplyConstraintStateToVisibleContent(slotModel.DisplayedPane.Value, content);
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
                ClipText = true,
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
        ApplyConstraintStateToVisibleContent(maximizedModel.PaneKind, content);
    }

    private void RebuildTaskbar(WorkspaceDisplayModel displayModel)
    {
        if (taskbarButtonsStrip is null)
        {
            return;
        }

        taskbarButtons.Clear();
        ClearDisposableChildren(taskbarButtonsStrip);
        foreach (var item in displayModel.TaskbarItems)
        {
            var button = new Button
            {
                Name = item.PaneKind + "TaskbarButton",
                Text = PaneContentFactory.GetPaneTitle(item.PaneKind),
                TooltipText = PaneContentFactory.GetPaneTitle(item.PaneKind),
                CustomMinimumSize = new Vector2(110.0f, 30.0f),
                SizeFlagsHorizontal = Control.SizeFlags.ShrinkBegin,
                MouseDefaultCursorShape = CursorShape.PointingHand,
            };
            ApplyTaskbarButtonStyle(button, item.VisualState, item.IsPinned);
            button.Pressed += () => OnTaskbarItemPressed(item);
            button.GuiInput += @event => OnTaskbarButtonGuiInput(item, button, @event);
            taskbarButtonsStrip.AddChild(button);
            taskbarButtons[item.PaneKind] = button;
        }
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

    private void OnTaskbarItemPressed(WorkspaceTaskbarItemDisplayModel item)
    {
        if (workspaceRuntime is null)
        {
            return;
        }

        switch (item.VisualState)
        {
            case WorkspaceTaskbarItemVisualState.Focused:
                return;
            case WorkspaceTaskbarItemVisualState.VisibleUnfocused:
                workspaceRuntime.FocusPane(item.PaneKind);
                return;
            case WorkspaceTaskbarItemVisualState.OpenHidden:
            case WorkspaceTaskbarItemVisualState.PinnedClosed:
                workspaceRuntime.ActivatePane(item.PaneKind);
                return;
            default:
                throw new InvalidOperationException($"Unknown taskbar visual state '{item.VisualState}'.");
        }
    }

    private void OnTaskbarButtonGuiInput(
        WorkspaceTaskbarItemDisplayModel item,
        Button button,
        InputEvent @event)
    {
        if (@event is not InputEventMouseButton mouseButton ||
            !mouseButton.Pressed ||
            mouseButton.ButtonIndex != MouseButton.Right)
        {
            return;
        }

        taskbarContextPaneKind = item.PaneKind;
        taskbarContextButtonRect = ResolveControlRectInRoot(button);
        ShowTaskbarContextMenu(item);
        AcceptEvent();
    }

    private void ShowTaskbarContextMenu(WorkspaceTaskbarItemDisplayModel item)
    {
        if (taskbarContextMenu is null)
        {
            return;
        }

        taskbarContextMenu.Clear();
        taskbarContextMenu.AddItem(item.IsPinned ? "Unpin" : "Pin", TaskbarContextMenuPinId);
        var popupPosition = GetScreenTransform() * GetLocalMousePosition();
        taskbarContextMenu.Position = new Vector2I(
            Mathf.RoundToInt(popupPosition.X),
            Mathf.RoundToInt(popupPosition.Y));
        taskbarContextMenu.ResetSize();
        taskbarContextMenu.Popup();
    }

    private void OnTaskbarContextMenuIdPressed(long id)
    {
        if (workspaceRuntime is null ||
            taskbarContextPaneKind is null ||
            taskbarContextButtonRect is null ||
            currentDisplayModel is null)
        {
            return;
        }

        if (id != TaskbarContextMenuPinId)
        {
            return;
        }

        var paneKind = taskbarContextPaneKind.Value;
        var isPinned = false;
        foreach (var item in currentDisplayModel.TaskbarItems)
        {
            if (item.PaneKind != paneKind)
            {
                continue;
            }

            isPinned = item.IsPinned;
            break;
        }

        var changed = isPinned
            ? workspaceRuntime.UnpinPane(paneKind)
            : workspaceRuntime.PinPane(paneKind);
        if (changed)
        {
            ShowTaskbarPinFeedback(taskbarContextButtonRect.Value, becamePinned: !isPinned);
        }

        taskbarContextPaneKind = null;
        taskbarContextButtonRect = null;
    }

    private void ShowTaskbarPinFeedback(Rect2 buttonRect, bool becamePinned)
    {
        if (feedbackLayer is null)
        {
            return;
        }

        var badge = new PanelContainer
        {
            Name = "TaskbarPinFeedback",
            MouseFilter = Control.MouseFilterEnum.Ignore,
            CustomMinimumSize = new Vector2(54.0f, 20.0f),
        };
        badge.AddThemeStyleboxOverride("panel", CreateTaskbarFeedbackStyleBox(becamePinned));

        var label = new Label
        {
            Text = becamePinned ? "PIN" : "UNPIN",
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        label.AddThemeFontSizeOverride("font_size", 10);
        label.AddThemeColorOverride("font_color", new Color(0.91f, 0.96f, 1.0f, 1.0f));
        label.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        badge.AddChild(label);

        var feedbackPosition = buttonRect.Position + new Vector2(buttonRect.Size.X - 42.0f, -8.0f);
        badge.Position = feedbackPosition;
        feedbackLayer.AddChild(badge);

        var tween = CreateTween();
        tween.TweenInterval(TaskbarFeedbackHoldSeconds);
        tween.TweenProperty(badge, "modulate:a", 0.0f, TaskbarFeedbackFadeSeconds);
        tween.Finished += () => badge.QueueFree();
    }

    private Rect2 ResolveControlRectInRoot(Control control)
    {
        var localTopLeft = control.GlobalPosition - GlobalPosition;
        return new Rect2(localTopLeft, control.Size);
    }

    private static bool IsPointerInsideControl(Control control, Vector2 pointerPosition)
    {
        var rect = new Rect2(control.GlobalPosition, control.Size);
        return rect.HasPoint(pointerPosition);
    }

    private void ApplyCurrentSplitRatios()
    {
        if (currentSnapshot is null || isApplyingSplitLayout)
        {
            return;
        }

        isApplyingSplitLayout = true;
        try
        {
            if (splitContainers.TryGetValue(WorkspaceSplitRatioBinding.LeftColumn, out var leftSplit))
            {
                leftSplit.SplitOffsets = [ResolveOffsetFromRatio(WorkspaceSplitRatioBinding.LeftColumn, currentSnapshot.LeftRatio)];
                ClampSplitOffset(WorkspaceSplitRatioBinding.LeftColumn);
            }

            if (splitContainers.TryGetValue(WorkspaceSplitRatioBinding.RightTop, out var rightSplit))
            {
                rightSplit.SplitOffsets = [ResolveOffsetFromRatio(WorkspaceSplitRatioBinding.RightTop, currentSnapshot.RightTopRatio)];
                ClampSplitOffset(WorkspaceSplitRatioBinding.RightTop);
            }
        }
        finally
        {
            isApplyingSplitLayout = false;
        }
    }

    private void OnSplitDragEnded()
    {
        activeDraggedSplitBinding = null;
        if (workspaceRuntime is null || currentSnapshot is null)
        {
            return;
        }

        var nextLeftRatio = currentSnapshot.LeftRatio;
        var nextRightTopRatio = currentSnapshot.RightTopRatio;

        if (TryReadCurrentSplitRatio(WorkspaceSplitRatioBinding.LeftColumn, out var leftRatio))
        {
            nextLeftRatio = leftRatio;
        }

        if (TryReadCurrentSplitRatio(WorkspaceSplitRatioBinding.RightTop, out var rightTopRatio))
        {
            nextRightTopRatio = rightTopRatio;
        }

        _ = workspaceRuntime.SetSplitRatios(nextLeftRatio, nextRightTopRatio);
    }

    private void OnSplitDragStarted(WorkspaceSplitRatioBinding binding)
    {
        activeDraggedSplitBinding = binding;
        ClampSplitOffset(binding);
    }

    private void OnSplitDragAreaGuiInput(WorkspaceSplitRatioBinding binding, InputEvent @event)
    {
        if (activeDraggedSplitBinding != binding)
        {
            return;
        }

        if (@event is not InputEventMouseMotion motion || (motion.ButtonMask & MouseButtonMask.Left) == 0)
        {
            return;
        }

        ClampSplitOffset(binding);
    }

    private void OnSplitResized(WorkspaceSplitRatioBinding binding)
    {
        if (isApplyingSplitLayout)
        {
            return;
        }

        if (!ClampSplitOffset(binding) || activeDraggedSplitBinding.HasValue)
        {
            return;
        }

        SyncClampedRatiosBackToRuntimeIfNeeded();
    }

    private bool TryReadCurrentSplitRatio(WorkspaceSplitRatioBinding binding, out float ratio)
    {
        ratio = 0.0f;
        if (!splitContainers.TryGetValue(binding, out var split))
        {
            return false;
        }

        var axisSize = ResolveSplitAxisAvailableSize(split, binding);
        if (axisSize <= 0.0f)
        {
            return false;
        }

        if (split.GetChildCount() >= 1 && split.GetChild(0) is Control firstChild)
        {
            var firstSize = binding == WorkspaceSplitRatioBinding.LeftColumn
                ? firstChild.Size.X
                : firstChild.Size.Y;
            ratio = Mathf.Clamp(firstSize / axisSize, 0.001f, 0.999f);
            return true;
        }

        var splitOffsets = split.SplitOffsets;
        if (!splitOffsets.Any())
        {
            return false;
        }

        ratio = Mathf.Clamp(splitOffsets[0] / axisSize, 0.001f, 0.999f);
        return true;
    }

    private void ApplyPreShellDisplay(bool isShellOpen)
    {
        if (preShellContentHost is null || paneContentFactory is null)
        {
            return;
        }

        if (isShellOpen)
        {
            MountContent(preShellContentHost, null, null);
            return;
        }

        var terminalContent = paneContentFactory.GetContent(
            WorkspacePaneKind.Terminal,
            WorkspaceRenderedContentKind.Implemented);
        MountContent(preShellContentHost, terminalContent, null);
    }

    private void OnWorkspaceStateChanged()
    {
        ApplyCurrentWorkspaceState();
    }

    private void OnNexusShellOpenStateChanged(bool isOpen)
    {
        ApplyCurrentWorkspaceState();
        if (!isOpen || paneContentFactory is null)
        {
            return;
        }

        var terminalContent = paneContentFactory.GetContent(
            WorkspacePaneKind.Terminal,
            WorkspaceRenderedContentKind.Implemented);
        if (terminalContent.HasMethod("focus_terminal_input"))
        {
            terminalContent.CallDeferred("focus_terminal_input");
        }
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

    private void ApplyDevelopmentBootstrapOverride()
    {
        if (workspaceRuntime is null || runtimeRoot is null)
        {
            return;
        }

        if (!runtimeRoot.IsNexusShellOpen || runtimeRoot.DebugOption != true)
        {
            return;
        }

        _ = workspaceRuntime.ActivatePane(WorkspacePaneKind.WorldMapTrace);
        _ = workspaceRuntime.FocusPane(WorkspacePaneKind.Terminal);
    }

    private int ResolveOffsetFromRatio(WorkspaceSplitRatioBinding binding, float ratio)
    {
        if (!splitContainers.TryGetValue(binding, out var split))
        {
            return 0;
        }

        var axisSize = ResolveSplitAxisAvailableSize(split, binding);
        if (axisSize <= 0.0f)
        {
            return 0;
        }

        var desiredFirstSize = axisSize * Mathf.Clamp(ratio, 0.001f, 0.999f);
        return ResolveOffsetFromDesiredFirstSize(split, binding, desiredFirstSize);
    }

    private bool ClampSplitOffset(WorkspaceSplitRatioBinding binding)
    {
        if (!splitContainers.TryGetValue(binding, out var split) ||
            currentConstraintSnapshot is null ||
            !currentConstraintSnapshot.SplitClampRules.TryGetValue(binding, out var clampRule))
        {
            return false;
        }

        var splitOffsets = split.SplitOffsets;
        if (!splitOffsets.Any())
        {
            return false;
        }

        var axisSize = ResolveSplitAxisAvailableSize(split, binding);
        if (axisSize <= 0.0f)
        {
            return false;
        }

        if (!TryGetCurrentFirstChildAxisSize(split, binding, out var currentFirstSize))
        {
            return false;
        }

        var clampedFirstSize = ClampFirstSizeToRule(currentFirstSize, axisSize, clampRule);
        if (Mathf.IsEqualApprox(clampedFirstSize, currentFirstSize))
        {
            return false;
        }

        var clampedOffset = ResolveOffsetFromDesiredFirstSize(split, binding, clampedFirstSize);
        if (clampedOffset == splitOffsets[0])
        {
            return false;
        }

        split.SplitOffsets = [clampedOffset];
        return true;
    }

    private float ClampFirstSizeToRule(
        float requestedFirstSize,
        float axisSize,
        WorkspaceSplitClampRule clampRule)
    {
        var firstMin = Mathf.Max(0.0f, clampRule.FirstBranchMinSizePx);
        var secondMin = Mathf.Max(0.0f, clampRule.SecondBranchMinSizePx);
        var available = Mathf.Max(0.0f, axisSize);
        if (firstMin + secondMin > available && available > 0.0f)
        {
            var totalRequested = firstMin + secondMin;
            var preferredFirstRatio = totalRequested <= 0.0f ? 0.5f : firstMin / totalRequested;
            return available * preferredFirstRatio;
        }

        var minFirstSize = firstMin;
        var maxFirstSize = Mathf.Max(0.0f, available - secondMin);
        return Mathf.Clamp(requestedFirstSize, minFirstSize, maxFirstSize);
    }

    private int ResolveOffsetFromDesiredFirstSize(
        SplitContainer split,
        WorkspaceSplitRatioBinding binding,
        float desiredFirstSize)
    {
        var splitOffsets = split.SplitOffsets;
        var currentOffset = splitOffsets.Any() ? splitOffsets[0] : 0;
        if (!TryGetCurrentFirstChildAxisSize(split, binding, out var currentFirstSize))
        {
            return Mathf.RoundToInt(desiredFirstSize);
        }

        var defaultFirstSize = currentFirstSize - currentOffset;
        return Mathf.RoundToInt(defaultFirstSize < 0.0f
            ? desiredFirstSize
            : desiredFirstSize - defaultFirstSize);
    }

    private static bool TryGetCurrentFirstChildAxisSize(
        SplitContainer split,
        WorkspaceSplitRatioBinding binding,
        out float firstSize)
    {
        firstSize = 0.0f;
        if (split.GetChildCount() < 1 || split.GetChild(0) is not Control firstChild)
        {
            return false;
        }

        firstSize = binding == WorkspaceSplitRatioBinding.LeftColumn
            ? firstChild.Size.X
            : firstChild.Size.Y;
        return true;
    }

    private float ResolveSplitAxisAvailableSize(SplitContainer split, WorkspaceSplitRatioBinding binding)
    {
        if (split.GetChildCount() >= 2 &&
            split.GetChild(0) is Control firstChild &&
            split.GetChild(1) is Control secondChild)
        {
            return binding == WorkspaceSplitRatioBinding.LeftColumn
                ? firstChild.Size.X + secondChild.Size.X
                : firstChild.Size.Y + secondChild.Size.Y;
        }

        return binding == WorkspaceSplitRatioBinding.LeftColumn
            ? split.Size.X
            : split.Size.Y;
    }

    private void SyncClampedRatiosBackToRuntimeIfNeeded()
    {
        if (workspaceRuntime is null || currentSnapshot is null || activeDraggedSplitBinding.HasValue || isApplyingSplitLayout)
        {
            return;
        }

        var nextLeftRatio = currentSnapshot.LeftRatio;
        var nextRightTopRatio = currentSnapshot.RightTopRatio;
        var changed = false;

        if (TryReadCurrentSplitRatio(WorkspaceSplitRatioBinding.LeftColumn, out var leftRatio) &&
            !Mathf.IsEqualApprox(leftRatio, currentSnapshot.LeftRatio))
        {
            if (Mathf.Abs(leftRatio - currentSnapshot.LeftRatio) > SplitRatioSyncEpsilon)
            {
                nextLeftRatio = leftRatio;
                changed = true;
            }
        }

        if (TryReadCurrentSplitRatio(WorkspaceSplitRatioBinding.RightTop, out var rightTopRatio) &&
            !Mathf.IsEqualApprox(rightTopRatio, currentSnapshot.RightTopRatio))
        {
            if (Mathf.Abs(rightTopRatio - currentSnapshot.RightTopRatio) > SplitRatioSyncEpsilon)
            {
                nextRightTopRatio = rightTopRatio;
                changed = true;
            }
        }

        if (changed)
        {
            _ = workspaceRuntime.SetSplitRatios(nextLeftRatio, nextRightTopRatio);
        }
    }

    private void ApplyConstraintStateToVisibleContent(WorkspacePaneKind paneKind, Control content)
    {
        if (content is not IWorkspaceConstraintAwarePaneContent constraintAwareContent ||
            currentConstraintSnapshot is null ||
            !currentConstraintSnapshot.VisiblePaneStates.TryGetValue(paneKind, out var constraintState))
        {
            return;
        }

        constraintAwareContent.ApplyConstraintState(constraintState);
    }

    private void ApplySlotMinimumSizes(WorkspaceDisplayModel displayModel)
    {
        foreach (var pair in slotViews)
        {
            var minimumSize = Vector2.Zero;
            if (displayModel.Mode == WorkspaceMode.Docked &&
                currentConstraintSnapshot is not null &&
                displayModel.DockedSlots.TryGetValue(pair.Key, out var slotModel) &&
                slotModel.DisplayedPane.HasValue &&
                currentConstraintSnapshot.VisiblePaneStates.TryGetValue(slotModel.DisplayedPane.Value, out var constraintState) &&
                slotChromeMetrics.TryGetValue(pair.Key, out var chromeMetrics))
            {
                var minWidth = constraintState.HorizontalResolvePolicy == WorkspaceConstraintResolvePolicy.Clamp
                    ? constraintState.MinUsableWidthPx + chromeMetrics.ExtraWidthPx
                    : 0.0f;
                var minHeight = constraintState.VerticalResolvePolicy == WorkspaceConstraintResolvePolicy.Clamp
                    ? constraintState.MinUsableHeightPx + chromeMetrics.ExtraHeightPx
                    : 0.0f;
                minimumSize = new Vector2(minWidth, minHeight);
            }

            pair.Value.Root.CustomMinimumSize = minimumSize;
        }
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

    private static StyleBoxFlat CreateTaskbarFrameStyleBox()
    {
        return new StyleBoxFlat
        {
            BgColor = new Color(0.04f, 0.06f, 0.08f, 1.0f),
            BorderColor = new Color(0.12f, 0.18f, 0.22f, 1.0f),
            BorderWidthTop = 1,
            CornerRadiusTopLeft = 6,
            CornerRadiusTopRight = 6,
        };
    }

    private static StyleBoxFlat CreateTaskbarButtonStyleBox(
        Color background,
        Color border)
    {
        return new StyleBoxFlat
        {
            BgColor = background,
            BorderColor = border,
            BorderWidthLeft = 1,
            BorderWidthTop = 1,
            BorderWidthRight = 1,
            BorderWidthBottom = 1,
            CornerRadiusTopLeft = 4,
            CornerRadiusTopRight = 4,
            CornerRadiusBottomLeft = 4,
            CornerRadiusBottomRight = 4,
            ContentMarginLeft = 10,
            ContentMarginTop = 4,
            ContentMarginRight = 10,
            ContentMarginBottom = 4,
        };
    }

    private static StyleBoxFlat CreateTaskbarFeedbackStyleBox(bool becamePinned)
    {
        return new StyleBoxFlat
        {
            BgColor = becamePinned
                ? new Color(0.09f, 0.24f, 0.16f, 0.96f)
                : new Color(0.24f, 0.12f, 0.12f, 0.96f),
            BorderColor = becamePinned
                ? new Color(0.28f, 0.64f, 0.42f, 1.0f)
                : new Color(0.70f, 0.38f, 0.38f, 1.0f),
            BorderWidthLeft = 1,
            BorderWidthTop = 1,
            BorderWidthRight = 1,
            BorderWidthBottom = 1,
            CornerRadiusTopLeft = 4,
            CornerRadiusTopRight = 4,
            CornerRadiusBottomLeft = 4,
            CornerRadiusBottomRight = 4,
            ContentMarginLeft = 6,
            ContentMarginTop = 2,
            ContentMarginRight = 6,
            ContentMarginBottom = 2,
        };
    }

    private static void ApplyStartPlaceholderStyle(Button button)
    {
        button.AddThemeColorOverride("font_color", new Color(0.56f, 0.62f, 0.68f, 1.0f));
        button.AddThemeStyleboxOverride(
            "normal",
            CreateTaskbarButtonStyleBox(
                new Color(0.05f, 0.07f, 0.09f, 1.0f),
                new Color(0.12f, 0.18f, 0.22f, 1.0f)));
        button.AddThemeStyleboxOverride(
            "disabled",
            CreateTaskbarButtonStyleBox(
                new Color(0.05f, 0.07f, 0.09f, 1.0f),
                new Color(0.12f, 0.18f, 0.22f, 1.0f)));
    }

    private static void ApplyTaskbarButtonStyle(
        Button button,
        WorkspaceTaskbarItemVisualState visualState,
        bool isPinned)
    {
        var background = visualState switch
        {
            WorkspaceTaskbarItemVisualState.Focused => new Color(0.12f, 0.22f, 0.18f, 1.0f),
            WorkspaceTaskbarItemVisualState.VisibleUnfocused => new Color(0.06f, 0.08f, 0.10f, 1.0f),
            WorkspaceTaskbarItemVisualState.OpenHidden => new Color(0.06f, 0.08f, 0.10f, 1.0f),
            WorkspaceTaskbarItemVisualState.PinnedClosed => new Color(0.04f, 0.05f, 0.06f, 1.0f),
            _ => throw new InvalidOperationException($"Unknown taskbar visual state '{visualState}'."),
        };
        var border = visualState switch
        {
            WorkspaceTaskbarItemVisualState.Focused => new Color(0.35f, 0.86f, 0.58f, 1.0f),
            WorkspaceTaskbarItemVisualState.VisibleUnfocused => new Color(0.32f, 0.38f, 0.42f, 1.0f),
            WorkspaceTaskbarItemVisualState.OpenHidden => new Color(0.32f, 0.38f, 0.42f, 1.0f),
            WorkspaceTaskbarItemVisualState.PinnedClosed => new Color(0.22f, 0.26f, 0.30f, 1.0f),
            _ => throw new InvalidOperationException($"Unknown taskbar visual state '{visualState}'."),
        };
        var fontColor = visualState switch
        {
            WorkspaceTaskbarItemVisualState.Focused => new Color(0.92f, 0.98f, 0.96f, 1.0f),
            WorkspaceTaskbarItemVisualState.VisibleUnfocused => new Color(0.88f, 0.92f, 0.95f, 1.0f),
            WorkspaceTaskbarItemVisualState.OpenHidden => new Color(0.88f, 0.92f, 0.95f, 1.0f),
            WorkspaceTaskbarItemVisualState.PinnedClosed => new Color(0.55f, 0.60f, 0.65f, 1.0f),
            _ => throw new InvalidOperationException($"Unknown taskbar visual state '{visualState}'."),
        };

        button.AddThemeColorOverride("font_color", fontColor);
        button.AddThemeColorOverride("font_hover_color", fontColor);
        button.AddThemeColorOverride("font_pressed_color", fontColor);
        button.AddThemeColorOverride("font_focus_color", fontColor);
        button.AddThemeStyleboxOverride("normal", CreateTaskbarButtonStyleBox(background, border));
        button.AddThemeStyleboxOverride("hover", CreateTaskbarButtonStyleBox(background.Lightened(0.08f), border));
        button.AddThemeStyleboxOverride("pressed", CreateTaskbarButtonStyleBox(background.Darkened(0.08f), border));
        button.AddThemeStyleboxOverride("focus", CreateTaskbarButtonStyleBox(background, border));

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
                ClipText = true,
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
                CustomMinimumSize = new Vector2(86.0f, 0.0f),
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

        internal WorkspaceSlotChromeMetrics MeasureChromeMetrics()
        {
            const float rootMarginHorizontal = 20.0f;
            const float rootMarginVertical = 20.0f;
            const float bodyMarginHorizontal = 16.0f;
            const float bodyMarginVertical = 16.0f;
            const float layoutSeparation = 8.0f;
            const float headerHeight = 32.0f;
            const float frameBorderHorizontal = 2.0f;
            const float frameBorderVertical = 2.0f;
            const float bodyBorderHorizontal = 2.0f;
            const float bodyBorderVertical = 2.0f;

            return new WorkspaceSlotChromeMetrics(
                ExtraWidthPx: rootMarginHorizontal + bodyMarginHorizontal + frameBorderHorizontal + bodyBorderHorizontal,
                ExtraHeightPx: rootMarginVertical + bodyMarginVertical + layoutSeparation + headerHeight + frameBorderVertical + bodyBorderVertical);
        }
    }
}
