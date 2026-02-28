using Godot;
using System;
using System.Collections.Generic;

#nullable enable

namespace Uplink2.Runtime.Windowing;

/// <summary>High-level windowing mode for the multi-window contract.</summary>
public enum WindowingMode
{
    /// <summary>Native OS windows mode.</summary>
    NativeOs = 0,

    /// <summary>Virtual desktop embedded windows mode.</summary>
    VirtualDesktop = 1,
}

/// <summary>Logical role identifier for core gameplay windows.</summary>
public enum CoreWindowKind
{
    /// <summary>Terminal gameplay window.</summary>
    Terminal = 0,

    /// <summary>GUI main gameplay window.</summary>
    GuiMain = 1,
}

internal enum WindowKind
{
    SshLogin = 0,
    WorldMapTrace = 1,
    NetworkTopology = 2,
    FileTransferQueue = 3,
    WebViewer = 4,
    ProcessList = 5,
    CodeEditor = 6,
}

internal enum WindowFocusMode
{
    Exclusive = 0,
    Passthrough = 1,
}

internal readonly record struct WindowCapability(
    bool SingleInstance,
    bool Resizable,
    bool AutoFocus,
    WindowFocusMode FocusMode,
    bool Volatile,
    bool Minimizable);

internal static class WindowCapabilityRegistry
{
    private static readonly IReadOnlyDictionary<WindowKind, WindowCapability> Capabilities =
        new Dictionary<WindowKind, WindowCapability>
        {
            [WindowKind.SshLogin] = new(
                SingleInstance: true,
                Resizable: false,
                AutoFocus: false,
                FocusMode: WindowFocusMode.Passthrough,
                Volatile: true,
                Minimizable: false),
            [WindowKind.FileTransferQueue] = new(
                SingleInstance: true,
                Resizable: false,
                AutoFocus: false,
                FocusMode: WindowFocusMode.Passthrough,
                Volatile: true,
                Minimizable: false),
            [WindowKind.WebViewer] = new(
                SingleInstance: true,
                Resizable: true,
                AutoFocus: true,
                FocusMode: WindowFocusMode.Exclusive,
                Volatile: false,
                Minimizable: true),
            [WindowKind.CodeEditor] = new(
                SingleInstance: true,
                Resizable: true,
                AutoFocus: true,
                FocusMode: WindowFocusMode.Exclusive,
                Volatile: false,
                Minimizable: true),
            [WindowKind.WorldMapTrace] = new(
                SingleInstance: true,
                Resizable: true,
                AutoFocus: true,
                FocusMode: WindowFocusMode.Exclusive,
                Volatile: false,
                Minimizable: false),
            [WindowKind.NetworkTopology] = new(
                SingleInstance: true,
                Resizable: true,
                AutoFocus: true,
                FocusMode: WindowFocusMode.Exclusive,
                Volatile: false,
                Minimizable: false),
            [WindowKind.ProcessList] = new(
                SingleInstance: true,
                Resizable: true,
                AutoFocus: true,
                FocusMode: WindowFocusMode.Exclusive,
                Volatile: false,
                Minimizable: false),
        };

    internal static WindowCapability Get(WindowKind kind)
    {
        if (Capabilities.TryGetValue(kind, out var capability))
        {
            return capability;
        }

        return new WindowCapability(
            SingleInstance: true,
            Resizable: false,
            AutoFocus: true,
            FocusMode: WindowFocusMode.Exclusive,
            Volatile: false,
            Minimizable: false);
    }
}

/// <summary>Abstracts platform-level window calls for policy logic.</summary>
public interface IPlatformWindowAdapter
{
    /// <summary>Returns the engine main window id.</summary>
    int GetMainWindowId();

    /// <summary>Returns the current display mode of a window.</summary>
    DisplayServer.WindowMode GetWindowMode(int windowId);

    /// <summary>Sets the display mode of a window.</summary>
    void SetWindowMode(int windowId, DisplayServer.WindowMode mode);

    /// <summary>Sets a window flag value on a window.</summary>
    void SetWindowFlag(DisplayServer.WindowFlags flag, bool enabled, int windowId);
}

/// <summary>Autoload window manager that enforces phase-1 native window policy.</summary>
public sealed partial class WindowManager : Node
{
    private const bool EnableSubWindowMinimizeInAlpha = false;
    private const long SshLoginAutoCloseDelayMs = 3000;
    private static readonly Vector2I SshLoginDefaultSize = new(520, 240);
    private static readonly Vector2I SshLoginDefaultPosition = new(120, 80);

    private WindowManagerController? controller;
    private readonly SshLoginVolatilePolicy sshLoginVolatilePolicy = new(SshLoginAutoCloseDelayMs);
    private Window? sshLoginWindow;
    private Label? sshLoginHostLabel;
    private LineEdit? sshLoginUserField;
    private LineEdit? sshLoginPasswordField;
    private Uplink2.Runtime.WorldRuntime.SshLoginAttempt? lastSshLoginAttempt;
    private bool sshLoginWindowRequestedVisible;
    private readonly WindowReactivationPolicy sshLoginReactivationPolicy = new(8);

    /// <inheritdoc/>
    public override void _Ready()
    {
        EnforceNativeSubwindowEmbedding();
        _ = EnsureController();
    }

    /// <inheritdoc/>
    public override void _Process(double delta)
    {
        var activeController = EnsureController();
        activeController.Tick();
        PumpSshLoginAttempts();
        TickSshLoginWindow(activeController.GetWindowingMode());
    }

    /// <summary>Requests a windowing mode change.</summary>
    public bool SetWindowingMode(WindowingMode mode)
    {
        return EnsureController().SetWindowingMode(mode);
    }

    /// <summary>Returns current windowing mode.</summary>
    public WindowingMode GetWindowingMode()
    {
        return EnsureController().GetWindowingMode();
    }

    /// <summary>Requests primary core-window promotion.</summary>
    public bool SetPrimaryCoreWindow(CoreWindowKind kind)
    {
        return EnsureController().SetPrimaryCoreWindow(kind);
    }

    /// <summary>Returns current primary core window kind.</summary>
    public CoreWindowKind GetPrimaryCoreWindow()
    {
        return EnsureController().GetPrimaryCoreWindow();
    }

    /// <summary>Requests a display mode change for the primary core window.</summary>
    public bool SetMainWindowDisplayMode(DisplayServer.WindowMode mode)
    {
        return EnsureController().SetMainWindowDisplayMode(mode);
    }

    private WindowManagerController EnsureController()
    {
        if (controller is null)
        {
            controller = new WindowManagerController(
                new GodotPlatformWindowAdapter(),
                static message => GD.PushWarning(message));
            controller.Initialize();
        }

        return controller;
    }

    private void EnforceNativeSubwindowEmbedding()
    {
        // Native OS mode uses real top-level windows, so embedding must stay disabled.
        ProjectSettings.SetSetting("display/window/subwindows/embed_subwindows", false);
        var rootWindow = GetTree().Root;
        rootWindow.GuiEmbedSubwindows = false;
    }

    private void PumpSshLoginAttempts()
    {
        var runtime = Uplink2.Runtime.WorldRuntime.Instance;
        if (runtime is null)
        {
            return;
        }

        if (!runtime.TryDrainLatestSshLoginAttempt(out var latestAttempt))
        {
            return;
        }

        EnsureSshLoginWindowCreated();
        lastSshLoginAttempt = latestAttempt;
        sshLoginWindowRequestedVisible = true;
        var nowMs = GetMonotonicTimeMs();
        ApplySshLoginAttempt(latestAttempt);
        if (!IsMainWindowMinimized())
        {
            var forceVisibilityEdge = sshLoginReactivationPolicy.ConsumeForceVisibilityEdge();
            if (!sshLoginWindow!.Visible || forceVisibilityEdge)
            {
                ShowSshLoginWindow(forceVisibilityEdge);
            }
        }
        sshLoginVolatilePolicy.NotifyUpdate(nowMs);
    }

    private void TickSshLoginWindow(WindowingMode mode)
    {
        if (sshLoginWindow is null)
        {
            sshLoginVolatilePolicy.Clear();
            sshLoginWindowRequestedVisible = false;
            sshLoginReactivationPolicy.Reset();
            return;
        }

        var nowMs = GetMonotonicTimeMs();
        var decision = sshLoginReactivationPolicy.Tick(
            requestedVisible: sshLoginWindowRequestedVisible,
            subWindowVisible: sshLoginWindow.Visible,
            mainWindowMinimized: IsMainWindowMinimized(),
            mainWindowFocused: IsMainWindowFocused(),
            subWindowFocused: IsSshLoginWindowFocused());
        if (decision.PauseTimer)
        {
            sshLoginVolatilePolicy.Pause(nowMs);
        }

        if (decision.StopTick)
        {
            return;
        }

        if (decision.ShouldShow)
        {
            ShowSshLoginWindow(decision.ForceVisibilityEdge);
        }

        if (!sshLoginWindow.Visible)
        {
            if (!sshLoginWindowRequestedVisible)
            {
                sshLoginVolatilePolicy.Clear();
                return;
            }

            sshLoginVolatilePolicy.Pause(nowMs);
            ShowSshLoginWindow(forceVisibilityEdge: false);
            if (!sshLoginWindow.Visible)
            {
                return;
            }
        }

        if (!sshLoginWindowRequestedVisible)
        {
            sshLoginVolatilePolicy.Clear();
            sshLoginReactivationPolicy.Reset();
            return;
        }

        if (mode != WindowingMode.NativeOs)
        {
            sshLoginVolatilePolicy.Pause(nowMs);
            return;
        }

        sshLoginVolatilePolicy.Resume(nowMs);
        var pointerInsideWindow = IsPointerInsideWindow(sshLoginWindow);
        var anyMouseButtonPressed = IsAnyMouseButtonPressed();
        if (!sshLoginVolatilePolicy.ShouldClose(nowMs, anyMouseButtonPressed, pointerInsideWindow))
        {
            return;
        }

        HideSshLoginWindow(resetPolicy: true, reason: "volatile_timeout");
    }

    private void EnsureSshLoginWindowCreated()
    {
        if (sshLoginWindow is not null)
        {
            return;
        }

        var sshLoginCapability = WindowCapabilityRegistry.Get(WindowKind.SshLogin);
        var window = new Window
        {
            Name = "SshLoginWindow",
            Title = "SSH Login",
            Borderless = false,
            Unresizable = !sshLoginCapability.Resizable,
            Unfocusable = sshLoginCapability.FocusMode == WindowFocusMode.Passthrough,
            Transient = true,
            Visible = false,
            Size = SshLoginDefaultSize,
            Position = SshLoginDefaultPosition,
        };
        window.Connect("close_requested", Callable.From(HandleSshLoginCloseRequested));

        var margin = new MarginContainer
        {
            Name = "RootMargin",
            OffsetLeft = 16,
            OffsetTop = 14,
            OffsetRight = -16,
            OffsetBottom = -14,
            AnchorRight = 1.0f,
            AnchorBottom = 1.0f,
        };
        window.AddChild(margin);

        var content = new VBoxContainer
        {
            Name = "Content",
            AnchorRight = 1.0f,
            AnchorBottom = 1.0f,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            SizeFlagsVertical = Control.SizeFlags.ExpandFill,
        };
        content.AddThemeConstantOverride("separation", 12);
        margin.AddChild(content);

        var hostLabel = new Label
        {
            Name = "HostLabel",
            Text = "<unknown>",
            HorizontalAlignment = HorizontalAlignment.Center,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
        };
        hostLabel.AddThemeFontSizeOverride("font_size", 24);
        content.AddChild(hostLabel);

        var userRow = new HBoxContainer
        {
            Name = "UserRow",
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
        };
        userRow.AddThemeConstantOverride("separation", 8);
        content.AddChild(userRow);

        var userLabel = new Label
        {
            Name = "UserTitle",
            Text = "User:",
            CustomMinimumSize = new Vector2(64, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        userRow.AddChild(userLabel);

        var userField = new LineEdit
        {
            Name = "UserField",
            Editable = false,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
        };
        userRow.AddChild(userField);

        var passwordRow = new HBoxContainer
        {
            Name = "PasswordRow",
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
        };
        passwordRow.AddThemeConstantOverride("separation", 8);
        content.AddChild(passwordRow);

        var passwordLabel = new Label
        {
            Name = "PasswordTitle",
            Text = "Passwd:",
            CustomMinimumSize = new Vector2(64, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        passwordRow.AddChild(passwordLabel);

        var passwordField = new LineEdit
        {
            Name = "PasswordField",
            Editable = false,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
        };
        passwordRow.AddChild(passwordField);

        AddChild(window);
        sshLoginWindow = window;
        sshLoginHostLabel = hostLabel;
        sshLoginUserField = userField;
        sshLoginPasswordField = passwordField;
        if (lastSshLoginAttempt.HasValue)
        {
            ApplySshLoginAttempt(lastSshLoginAttempt.Value);
        }
    }

    private void ApplySshLoginAttempt(Uplink2.Runtime.WorldRuntime.SshLoginAttempt attempt)
    {
        if (sshLoginHostLabel is null || sshLoginUserField is null || sshLoginPasswordField is null)
        {
            return;
        }

        var hostText = string.IsNullOrWhiteSpace(attempt.HostOrIp) ? "<unknown>" : attempt.HostOrIp;
        var userText = string.IsNullOrWhiteSpace(attempt.UserId) ? "<unknown>" : attempt.UserId;
        sshLoginHostLabel.Text = hostText;
        sshLoginUserField.Text = userText;
        sshLoginPasswordField.Text = attempt.Password;
    }

    private void ShowSshLoginWindow(bool forceVisibilityEdge)
    {
        if (sshLoginWindow is null)
        {
            return;
        }

        if (sshLoginWindow.Mode == Window.ModeEnum.Minimized)
        {
            sshLoginWindow.Mode = Window.ModeEnum.Windowed;
        }

        if (sshLoginWindow.Visible && !forceVisibilityEdge)
        {
            return;
        }

        if (sshLoginWindow.Visible && forceVisibilityEdge)
        {
            // Force a visibility edge for cases where OS restored state keeps a visible window hidden.
            sshLoginWindow.Hide();
        }

        sshLoginWindow.Show();
    }

    private void HideSshLoginWindow(bool resetPolicy, string reason)
    {
        sshLoginWindowRequestedVisible = false;
        sshLoginReactivationPolicy.Reset();
        if (sshLoginWindow is not null)
        {
            sshLoginWindow.Hide();
        }

        if (resetPolicy)
        {
            sshLoginVolatilePolicy.Clear();
        }
    }

    private void HandleSshLoginCloseRequested()
    {
        HideSshLoginWindow(resetPolicy: true, reason: "close_requested");
    }

    private static long GetMonotonicTimeMs()
    {
        return unchecked((long)Time.GetTicksMsec());
    }

    private static bool IsAnyMouseButtonPressed()
    {
        return Input.IsMouseButtonPressed(MouseButton.Left) ||
               Input.IsMouseButtonPressed(MouseButton.Right) ||
               Input.IsMouseButtonPressed(MouseButton.Middle) ||
               Input.IsMouseButtonPressed(MouseButton.Xbutton1) ||
               Input.IsMouseButtonPressed(MouseButton.Xbutton2);
    }

    private bool IsMainWindowMinimized()
    {
        var rootWindow = GetTree().Root;
        if (rootWindow.Mode == Window.ModeEnum.Minimized)
        {
            return true;
        }

        var mainWindowMode = EnsureController().GetMainWindowDisplayMode();
        return mainWindowMode == DisplayServer.WindowMode.Minimized;
    }

    private bool IsMainWindowFocused()
    {
        var rootWindow = GetTree().Root;
        return rootWindow.HasFocus();
    }

    private bool IsSshLoginWindowFocused()
    {
        return sshLoginWindow is not null && sshLoginWindow.HasFocus();
    }

    private static bool IsEffectiveSubWindowMinimizable(WindowKind kind)
    {
        return EnableSubWindowMinimizeInAlpha && WindowCapabilityRegistry.Get(kind).Minimizable;
    }

    private static bool IsPointerInsideWindow(Window window)
    {
        var windowPosition = window.Position;
        var windowSize = window.Size;
        if (windowSize.X <= 0 || windowSize.Y <= 0)
        {
            return false;
        }

        var mousePosition = DisplayServer.MouseGetPosition();
        var mouseX = (int)mousePosition.X;
        var mouseY = (int)mousePosition.Y;
        var minX = windowPosition.X;
        var minY = windowPosition.Y;
        var maxX = minX + windowSize.X;
        var maxY = minY + windowSize.Y;
        return mouseX >= minX && mouseX < maxX &&
               mouseY >= minY && mouseY < maxY;
    }
}

internal sealed class GodotPlatformWindowAdapter : IPlatformWindowAdapter
{
    internal static readonly int InvalidWindowId = checked((int)DisplayServer.InvalidWindowId);

    public int GetMainWindowId()
    {
        return checked((int)DisplayServer.MainWindowId);
    }

    public DisplayServer.WindowMode GetWindowMode(int windowId)
    {
        return DisplayServer.WindowGetMode(windowId);
    }

    public void SetWindowMode(int windowId, DisplayServer.WindowMode mode)
    {
        DisplayServer.WindowSetMode(mode, windowId);
    }

    public void SetWindowFlag(DisplayServer.WindowFlags flag, bool enabled, int windowId)
    {
        DisplayServer.WindowSetFlag(flag, enabled, windowId);
    }
}

internal sealed class WindowManagerController
{
    private readonly IPlatformWindowAdapter platformWindowAdapter;
    private readonly Action<string> warningLogger;
    private readonly Dictionary<CoreWindowKind, int> coreWindowIds = new();
    private bool initialized;
    private WindowingMode windowingMode = WindowingMode.NativeOs;
    private CoreWindowKind primaryCoreWindow = CoreWindowKind.Terminal;
    private DisplayServer.WindowMode lastObservedMainWindowMode = DisplayServer.WindowMode.Windowed;

    internal WindowManagerController(
        IPlatformWindowAdapter platformWindowAdapter,
        Action<string>? warningLogger = null)
    {
        this.platformWindowAdapter = platformWindowAdapter ?? throw new ArgumentNullException(nameof(platformWindowAdapter));
        this.warningLogger = warningLogger ?? (_ => { });
    }

    internal void Initialize()
    {
        if (initialized)
        {
            return;
        }

        initialized = true;
        windowingMode = WindowingMode.NativeOs;
        RegisterCoreWindow(CoreWindowKind.Terminal, platformWindowAdapter.GetMainWindowId());
        primaryCoreWindow = CoreWindowKind.Terminal;
        EnforceNativeMainWindowStyle(ResolvePrimaryWindowId());
        RecoverMainWindowFromFullscreenIfNeeded("initialization");
    }

    internal void Tick()
    {
        if (!initialized || windowingMode != WindowingMode.NativeOs)
        {
            return;
        }

        RecoverMainWindowFromFullscreenIfNeeded("runtime fullscreen guard");
    }

    internal bool SetWindowingMode(WindowingMode mode)
    {
        Initialize();
        if (mode == windowingMode)
        {
            return true;
        }

        if (mode == WindowingMode.VirtualDesktop)
        {
            warningLogger("WindowManager: VIRTUAL_DESKTOP mode is recognized but not implemented in phase 1.");
            return false;
        }

        windowingMode = WindowingMode.NativeOs;
        EnforceNativeMainWindowStyle(ResolvePrimaryWindowId());
        RecoverMainWindowFromFullscreenIfNeeded("native mode apply");
        return true;
    }

    internal WindowingMode GetWindowingMode()
    {
        Initialize();
        return windowingMode;
    }

    internal bool SetPrimaryCoreWindow(CoreWindowKind kind)
    {
        Initialize();
        if (!coreWindowIds.ContainsKey(kind))
        {
            warningLogger($"WindowManager: cannot promote '{kind}' because the core window is not registered.");
            return false;
        }

        primaryCoreWindow = kind;
        return true;
    }

    internal CoreWindowKind GetPrimaryCoreWindow()
    {
        Initialize();
        return primaryCoreWindow;
    }

    internal bool SetMainWindowDisplayMode(DisplayServer.WindowMode mode)
    {
        Initialize();
        if (windowingMode != WindowingMode.NativeOs)
        {
            warningLogger($"WindowManager: display mode request '{mode}' rejected because current mode is '{windowingMode}'.");
            return false;
        }

        if (!IsAllowedNativeDisplayMode(mode))
        {
            warningLogger($"WindowManager: display mode '{mode}' requires virtual desktop transition and is blocked in phase 1.");
            RecoverMainWindowFromFullscreenIfNeeded("blocked display mode request");
            return false;
        }

        var mainWindowId = ResolvePrimaryWindowId();
        platformWindowAdapter.SetWindowMode(mainWindowId, mode);
        EnforceNativeMainWindowStyle(mainWindowId);
        lastObservedMainWindowMode = platformWindowAdapter.GetWindowMode(mainWindowId);
        return true;
    }

    internal DisplayServer.WindowMode GetMainWindowDisplayMode()
    {
        Initialize();
        return platformWindowAdapter.GetWindowMode(ResolvePrimaryWindowId());
    }

    private static bool IsAllowedNativeDisplayMode(DisplayServer.WindowMode mode)
    {
        return mode == DisplayServer.WindowMode.Windowed ||
               mode == DisplayServer.WindowMode.Maximized;
    }

    private static bool IsFullscreenMode(DisplayServer.WindowMode mode)
    {
        return mode == DisplayServer.WindowMode.Fullscreen ||
               mode == DisplayServer.WindowMode.ExclusiveFullscreen;
    }

    private int ResolvePrimaryWindowId()
    {
        if (coreWindowIds.TryGetValue(primaryCoreWindow, out var primaryWindowId))
        {
            return primaryWindowId;
        }

        if (coreWindowIds.TryGetValue(CoreWindowKind.Terminal, out var terminalWindowId))
        {
            primaryCoreWindow = CoreWindowKind.Terminal;
            return terminalWindowId;
        }

        var mainWindowId = platformWindowAdapter.GetMainWindowId();
        RegisterCoreWindow(CoreWindowKind.Terminal, mainWindowId);
        primaryCoreWindow = CoreWindowKind.Terminal;
        return mainWindowId;
    }

    private void RegisterCoreWindow(CoreWindowKind kind, int windowId)
    {
        if (windowId == GodotPlatformWindowAdapter.InvalidWindowId)
        {
            warningLogger($"WindowManager: registration ignored for '{kind}' because window id is invalid.");
            return;
        }

        coreWindowIds[kind] = windowId;
    }

    private void EnforceNativeMainWindowStyle(int windowId)
    {
        platformWindowAdapter.SetWindowFlag(DisplayServer.WindowFlags.Borderless, false, windowId);
    }

    private void RecoverMainWindowFromFullscreenIfNeeded(string reason)
    {
        var mainWindowId = ResolvePrimaryWindowId();
        var currentMode = platformWindowAdapter.GetWindowMode(mainWindowId);
        if (!IsFullscreenMode(currentMode))
        {
            lastObservedMainWindowMode = currentMode;
            return;
        }

        if (lastObservedMainWindowMode != currentMode)
        {
            warningLogger($"WindowManager: fullscreen mode '{currentMode}' detected during '{reason}', forcing maximized native mode.");
        }

        platformWindowAdapter.SetWindowMode(mainWindowId, DisplayServer.WindowMode.Maximized);
        EnforceNativeMainWindowStyle(mainWindowId);
        lastObservedMainWindowMode = platformWindowAdapter.GetWindowMode(mainWindowId);
    }
}

internal readonly record struct WindowReactivationDecision(
    bool PauseTimer,
    bool StopTick,
    bool ShouldShow,
    bool ForceVisibilityEdge);

internal sealed class WindowReactivationPolicy
{
    private readonly int retryFrameBudget;
    private bool windowWasMinimized;
    private bool windowWasInactive;
    private int pendingRetryFrames;
    private bool forceVisibilityEdgeOnNextShow;

    internal WindowReactivationPolicy(int retryFrameBudget = 8)
    {
        this.retryFrameBudget = Math.Max(1, retryFrameBudget);
    }

    internal WindowReactivationDecision Tick(
        bool requestedVisible,
        bool subWindowVisible,
        bool mainWindowMinimized,
        bool mainWindowFocused,
        bool subWindowFocused)
    {
        if (mainWindowMinimized)
        {
            windowWasMinimized = true;
            return new(
                PauseTimer: requestedVisible,
                StopTick: true,
                ShouldShow: false,
                ForceVisibilityEdge: false);
        }

        if (!mainWindowFocused && !subWindowFocused)
        {
            windowWasInactive = true;
            return new(
                PauseTimer: requestedVisible,
                StopTick: true,
                ShouldShow: false,
                ForceVisibilityEdge: false);
        }

        if (windowWasMinimized)
        {
            windowWasMinimized = false;
            windowWasInactive = false;
            if (requestedVisible)
            {
                pendingRetryFrames = Math.Max(pendingRetryFrames, retryFrameBudget);
                forceVisibilityEdgeOnNextShow = true;
            }
        }

        if (windowWasInactive)
        {
            windowWasInactive = false;
            if (requestedVisible)
            {
                pendingRetryFrames = Math.Max(pendingRetryFrames, retryFrameBudget);
                forceVisibilityEdgeOnNextShow = true;
            }
        }

        if (pendingRetryFrames > 0 && requestedVisible)
        {
            pendingRetryFrames--;
            var forceVisibilityEdge = ConsumeForceVisibilityEdge();
            if (!subWindowVisible || forceVisibilityEdge)
            {
                return new(
                    PauseTimer: false,
                    StopTick: false,
                    ShouldShow: true,
                    ForceVisibilityEdge: forceVisibilityEdge);
            }
        }

        return new(
            PauseTimer: false,
            StopTick: false,
            ShouldShow: false,
            ForceVisibilityEdge: false);
    }

    internal bool ConsumeForceVisibilityEdge()
    {
        var forceVisibilityEdge = forceVisibilityEdgeOnNextShow;
        forceVisibilityEdgeOnNextShow = false;
        return forceVisibilityEdge;
    }

    internal void Reset()
    {
        windowWasMinimized = false;
        windowWasInactive = false;
        pendingRetryFrames = 0;
        forceVisibilityEdgeOnNextShow = false;
    }
}

internal sealed class SshLoginVolatilePolicy
{
    private readonly long closeDelayMs;
    private long deadlineMs = -1;
    private bool isPaused;
    private long pausedAtMs;
    private bool deferredByMouseHold;

    internal SshLoginVolatilePolicy(long closeDelayMs = 3000)
    {
        this.closeDelayMs = Math.Max(1, closeDelayMs);
    }

    internal void NotifyUpdate(long nowMs)
    {
        deadlineMs = nowMs + closeDelayMs;
        deferredByMouseHold = false;
        isPaused = false;
        pausedAtMs = 0;
    }

    internal void Pause(long nowMs)
    {
        if (deadlineMs < 0 || isPaused)
        {
            return;
        }

        isPaused = true;
        pausedAtMs = nowMs;
    }

    internal void Resume(long nowMs)
    {
        if (!isPaused)
        {
            return;
        }

        isPaused = false;
        if (deadlineMs < 0)
        {
            pausedAtMs = 0;
            return;
        }

        var pausedDuration = Math.Max(0, nowMs - pausedAtMs);
        deadlineMs += pausedDuration;
        pausedAtMs = 0;
    }

    internal bool ShouldClose(long nowMs, bool isMouseButtonDown, bool isPointerInsideWindow)
    {
        if (deadlineMs < 0 || isPaused || nowMs < deadlineMs)
        {
            return false;
        }

        if (isMouseButtonDown && isPointerInsideWindow)
        {
            deferredByMouseHold = true;
            return false;
        }

        if (!deferredByMouseHold)
        {
            return true;
        }

        if (!isMouseButtonDown && !isPointerInsideWindow)
        {
            return true;
        }

        return false;
    }

    internal void Clear()
    {
        deadlineMs = -1;
        deferredByMouseHold = false;
        isPaused = false;
        pausedAtMs = 0;
    }
}
