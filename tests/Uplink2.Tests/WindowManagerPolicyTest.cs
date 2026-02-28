using Godot;
using System;
using System.Collections.Generic;
using System.Reflection;
using Uplink2.Runtime.Windowing;
using Xunit;

#nullable enable

namespace Uplink2.Tests;

/// <summary>Policy tests for phase-1 native window manager behavior.</summary>
[Trait("Speed", "fast")]
public sealed class WindowManagerPolicyTest
{
    /// <summary>Ensures initialization keeps NativeOs mode with Terminal as primary.</summary>
    [Fact]
    public void Initialize_DefaultsToNativeOsAndTerminalPrimary()
    {
        var adapter = new FakePlatformWindowAdapter();
        var controller = CreateController(adapter, _ => { });

        InvokeVoid(controller, "Initialize");
        var mode = InvokeMethod<WindowingMode>(controller, "GetWindowingMode");
        var primary = InvokeMethod<CoreWindowKind>(controller, "GetPrimaryCoreWindow");

        Assert.Equal(WindowingMode.NativeOs, mode);
        Assert.Equal(CoreWindowKind.Terminal, primary);
        Assert.False(adapter.BorderlessFlagByWindowId.TryGetValue(adapter.MainWindowId, out var isBorderless) && isBorderless);
    }

    /// <summary>Ensures VirtualDesktop request is rejected and mode remains NativeOs.</summary>
    [Fact]
    public void SetWindowingMode_RejectsVirtualDesktopInPhase1()
    {
        var warnings = new List<string>();
        var adapter = new FakePlatformWindowAdapter();
        var controller = CreateController(adapter, warnings.Add);

        var changed = InvokeMethod<bool>(controller, "SetWindowingMode", WindowingMode.VirtualDesktop);
        var mode = InvokeMethod<WindowingMode>(controller, "GetWindowingMode");

        Assert.False(changed);
        Assert.Equal(WindowingMode.NativeOs, mode);
        Assert.Contains(warnings, static value => value.Contains("VIRTUAL_DESKTOP", StringComparison.Ordinal));
    }

    /// <summary>Ensures only Windowed/Maximized display modes are accepted in NativeOs.</summary>
    [Fact]
    public void SetMainWindowDisplayMode_AllowsWindowedAndMaximized_RejectsFullscreenFamily()
    {
        var adapter = new FakePlatformWindowAdapter();
        var controller = CreateController(adapter, _ => { });

        var windowed = InvokeMethod<bool>(controller, "SetMainWindowDisplayMode", DisplayServer.WindowMode.Windowed);
        var maximized = InvokeMethod<bool>(controller, "SetMainWindowDisplayMode", DisplayServer.WindowMode.Maximized);
        var fullscreen = InvokeMethod<bool>(controller, "SetMainWindowDisplayMode", DisplayServer.WindowMode.Fullscreen);
        var exclusive = InvokeMethod<bool>(controller, "SetMainWindowDisplayMode", DisplayServer.WindowMode.ExclusiveFullscreen);

        Assert.True(windowed);
        Assert.True(maximized);
        Assert.False(fullscreen);
        Assert.False(exclusive);
        Assert.Equal(DisplayServer.WindowMode.Maximized, adapter.WindowModeByWindowId[adapter.MainWindowId]);
    }

    /// <summary>Ensures GuiMain promotion fails while Terminal promotion succeeds.</summary>
    [Fact]
    public void SetPrimaryCoreWindow_FailsForUnregisteredGuiMain_AndSucceedsForTerminal()
    {
        var adapter = new FakePlatformWindowAdapter();
        var controller = CreateController(adapter, _ => { });

        InvokeVoid(controller, "Initialize");
        var guiMainResult = InvokeMethod<bool>(controller, "SetPrimaryCoreWindow", CoreWindowKind.GuiMain);
        var terminalResult = InvokeMethod<bool>(controller, "SetPrimaryCoreWindow", CoreWindowKind.Terminal);
        var primary = InvokeMethod<CoreWindowKind>(controller, "GetPrimaryCoreWindow");

        Assert.False(guiMainResult);
        Assert.True(terminalResult);
        Assert.Equal(CoreWindowKind.Terminal, primary);
    }

    /// <summary>Ensures runtime tick recovers forced fullscreen back to Maximized in NativeOs.</summary>
    [Fact]
    public void Tick_RecoversForcedFullscreenToMaximized()
    {
        var adapter = new FakePlatformWindowAdapter();
        var controller = CreateController(adapter, _ => { });
        InvokeVoid(controller, "Initialize");
        adapter.WindowModeByWindowId[adapter.MainWindowId] = DisplayServer.WindowMode.Fullscreen;

        InvokeVoid(controller, "Tick");

        Assert.Equal(DisplayServer.WindowMode.Maximized, adapter.WindowModeByWindowId[adapter.MainWindowId]);
    }

    /// <summary>Ensures volatile policy closes once deadline reaches last-update plus 3 seconds.</summary>
    [Fact]
    public void SshLoginVolatilePolicy_ClosesAfterDeadline()
    {
        var policy = CreateSshLoginVolatilePolicy();
        InvokeVoid(policy, "NotifyUpdate", 1_000L);

        Assert.False(InvokeMethod<bool>(policy, "ShouldClose", 3_999L, false, false));
        Assert.True(InvokeMethod<bool>(policy, "ShouldClose", 4_000L, false, false));
    }

    /// <summary>Ensures volatile policy resets deadline when a new update arrives.</summary>
    [Fact]
    public void SshLoginVolatilePolicy_UpdateResetsDeadline()
    {
        var policy = CreateSshLoginVolatilePolicy();
        InvokeVoid(policy, "NotifyUpdate", 1_000L);
        InvokeVoid(policy, "NotifyUpdate", 2_500L);

        Assert.False(InvokeMethod<bool>(policy, "ShouldClose", 5_499L, false, false));
        Assert.True(InvokeMethod<bool>(policy, "ShouldClose", 5_500L, false, false));
    }

    /// <summary>Ensures volatile policy defers close while mouse is held inside, then closes after release and leave.</summary>
    [Fact]
    public void SshLoginVolatilePolicy_DefersOnMouseHoldInsideWindow()
    {
        var policy = CreateSshLoginVolatilePolicy();
        InvokeVoid(policy, "NotifyUpdate", 1_000L);

        Assert.False(InvokeMethod<bool>(policy, "ShouldClose", 4_500L, true, true));
        Assert.False(InvokeMethod<bool>(policy, "ShouldClose", 4_800L, true, false));
        Assert.False(InvokeMethod<bool>(policy, "ShouldClose", 5_000L, false, true));
        Assert.True(InvokeMethod<bool>(policy, "ShouldClose", 5_100L, false, false));
    }

    /// <summary>Ensures paused interval is excluded from volatile countdown.</summary>
    [Fact]
    public void SshLoginVolatilePolicy_PauseResumeExcludesElapsedTime()
    {
        var policy = CreateSshLoginVolatilePolicy();
        InvokeVoid(policy, "NotifyUpdate", 1_000L);
        InvokeVoid(policy, "Pause", 2_000L);
        InvokeVoid(policy, "Resume", 6_000L);

        Assert.False(InvokeMethod<bool>(policy, "ShouldClose", 7_999L, false, false));
        Assert.True(InvokeMethod<bool>(policy, "ShouldClose", 8_000L, false, false));
    }

    /// <summary>Ensures window capability registry exposes planned minimizable targets for future window kinds.</summary>
    [Fact]
    public void WindowCapabilityRegistry_ReportsExpectedMinimizableFlags()
    {
        var kindType = RequireRuntimeType("Uplink2.Runtime.Windowing.WindowKind");
        var registryType = RequireRuntimeType("Uplink2.Runtime.Windowing.WindowCapabilityRegistry");
        var getMethod = registryType.GetMethod("Get", BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(getMethod);

        var sshCapability = getMethod!.Invoke(null, [Enum.Parse(kindType, "SshLogin")]);
        var ftpCapability = getMethod.Invoke(null, [Enum.Parse(kindType, "FileTransferQueue")]);
        var webViewerCapability = getMethod.Invoke(null, [Enum.Parse(kindType, "WebViewer")]);
        var codeEditorCapability = getMethod.Invoke(null, [Enum.Parse(kindType, "CodeEditor")]);

        Assert.NotNull(sshCapability);
        Assert.NotNull(ftpCapability);
        Assert.NotNull(webViewerCapability);
        Assert.NotNull(codeEditorCapability);

        Assert.False(GetPropertyValue<bool>(sshCapability!, "Minimizable"));
        Assert.False(GetPropertyValue<bool>(ftpCapability!, "Minimizable"));
        Assert.True(GetPropertyValue<bool>(webViewerCapability!, "Minimizable"));
        Assert.True(GetPropertyValue<bool>(codeEditorCapability!, "Minimizable"));
    }

    /// <summary>Ensures alpha minimizable gate keeps sub-window minimize disabled even for minimizable capabilities.</summary>
    [Fact]
    public void IsEffectiveSubWindowMinimizable_StaysDisabledInAlpha()
    {
        var kindType = RequireRuntimeType("Uplink2.Runtime.Windowing.WindowKind");
        var method = typeof(WindowManager).GetMethod(
            "IsEffectiveSubWindowMinimizable",
            BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(method);

        var webViewer = method!.Invoke(null, [Enum.Parse(kindType, "WebViewer")]);
        var codeEditor = method.Invoke(null, [Enum.Parse(kindType, "CodeEditor")]);
        var sshLogin = method.Invoke(null, [Enum.Parse(kindType, "SshLogin")]);

        Assert.NotNull(webViewer);
        Assert.NotNull(codeEditor);
        Assert.NotNull(sshLogin);

        Assert.False((bool)webViewer!);
        Assert.False((bool)codeEditor!);
        Assert.False((bool)sshLogin!);
    }

    /// <summary>Ensures reactivation policy requests timer pause while main window is minimized or inactive.</summary>
    [Fact]
    public void WindowReactivationPolicy_PausesWhenMainWindowIsInactive()
    {
        var policy = CreateWindowReactivationPolicy();
        var minimizedDecision = InvokeMethod<object>(
            policy,
            "Tick",
            true,
            true,
            true,
            false,
            false);
        var inactiveDecision = InvokeMethod<object>(
            policy,
            "Tick",
            true,
            true,
            false,
            false,
            false);

        Assert.True(GetPropertyValue<bool>(minimizedDecision, "PauseTimer"));
        Assert.True(GetPropertyValue<bool>(minimizedDecision, "StopTick"));
        Assert.True(GetPropertyValue<bool>(inactiveDecision, "PauseTimer"));
        Assert.True(GetPropertyValue<bool>(inactiveDecision, "StopTick"));
    }

    /// <summary>Ensures reactivation policy emits one forced edge show request after focus is restored.</summary>
    [Fact]
    public void WindowReactivationPolicy_RestoreRequestsForcedShowEdge()
    {
        var policy = CreateWindowReactivationPolicy();
        _ = InvokeMethod<object>(
            policy,
            "Tick",
            true,
            true,
            false,
            false,
            false);

        var restoredDecision = InvokeMethod<object>(
            policy,
            "Tick",
            true,
            true,
            false,
            true,
            false);

        Assert.False(GetPropertyValue<bool>(restoredDecision, "PauseTimer"));
        Assert.False(GetPropertyValue<bool>(restoredDecision, "StopTick"));
        Assert.True(GetPropertyValue<bool>(restoredDecision, "ShouldShow"));
        Assert.True(GetPropertyValue<bool>(restoredDecision, "ForceVisibilityEdge"));
    }

    /// <summary>Ensures reactivation policy does not request restore show when visibility was not requested.</summary>
    [Fact]
    public void WindowReactivationPolicy_DoesNotShowWhenRequestedVisibleIsFalse()
    {
        var policy = CreateWindowReactivationPolicy();
        _ = InvokeMethod<object>(
            policy,
            "Tick",
            true,
            true,
            true,
            false,
            false);

        var restoredWithoutRequest = InvokeMethod<object>(
            policy,
            "Tick",
            false,
            true,
            false,
            true,
            false);

        Assert.False(GetPropertyValue<bool>(restoredWithoutRequest, "PauseTimer"));
        Assert.False(GetPropertyValue<bool>(restoredWithoutRequest, "StopTick"));
        Assert.False(GetPropertyValue<bool>(restoredWithoutRequest, "ShouldShow"));
        Assert.False(GetPropertyValue<bool>(restoredWithoutRequest, "ForceVisibilityEdge"));
    }

    private static object CreateController(FakePlatformWindowAdapter adapter, Action<string> warningLogger)
    {
        var controllerType = typeof(WindowManager).Assembly.GetType("Uplink2.Runtime.Windowing.WindowManagerController");
        Assert.NotNull(controllerType);

        var controller = Activator.CreateInstance(
            controllerType!,
            BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null,
            args: [adapter, warningLogger],
            culture: null);
        Assert.NotNull(controller);
        return controller!;
    }

    private static object CreateSshLoginVolatilePolicy()
    {
        var policyType = typeof(WindowManager).Assembly.GetType("Uplink2.Runtime.Windowing.SshLoginVolatilePolicy");
        Assert.NotNull(policyType);

        var policy = Activator.CreateInstance(
            policyType!,
            BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null,
            args: [3000L],
            culture: null);
        Assert.NotNull(policy);
        return policy!;
    }

    private static object CreateWindowReactivationPolicy()
    {
        var policyType = typeof(WindowManager).Assembly.GetType("Uplink2.Runtime.Windowing.WindowReactivationPolicy");
        Assert.NotNull(policyType);

        var policy = Activator.CreateInstance(
            policyType!,
            BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null,
            args: [8],
            culture: null);
        Assert.NotNull(policy);
        return policy!;
    }

    private static Type RequireRuntimeType(string typeName)
    {
        var type = typeof(WindowManager).Assembly.GetType(typeName);
        Assert.NotNull(type);
        return type!;
    }

    private static T GetPropertyValue<T>(object target, string propertyName)
    {
        var property = target.GetType().GetProperty(
            propertyName,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        Assert.NotNull(property);
        var value = property!.GetValue(target);
        Assert.NotNull(value);
        return (T)value!;
    }

    private static T InvokeMethod<T>(object target, string methodName, params object[] args)
    {
        var method = target.GetType().GetMethod(
            methodName,
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);

        var value = method!.Invoke(target, args);
        Assert.NotNull(value);
        return (T)value!;
    }

    private static void InvokeVoid(object target, string methodName, params object[] args)
    {
        var method = target.GetType().GetMethod(
            methodName,
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);
        method!.Invoke(target, args);
    }

    private sealed class FakePlatformWindowAdapter : IPlatformWindowAdapter
    {
        internal Dictionary<int, bool> BorderlessFlagByWindowId { get; } = new();

        internal Dictionary<int, DisplayServer.WindowMode> WindowModeByWindowId { get; } = new()
        {
            [1] = DisplayServer.WindowMode.Windowed,
        };

        public int MainWindowId => 1;

        public int GetMainWindowId()
        {
            return MainWindowId;
        }

        public DisplayServer.WindowMode GetWindowMode(int windowId)
        {
            return WindowModeByWindowId.TryGetValue(windowId, out var mode)
                ? mode
                : DisplayServer.WindowMode.Windowed;
        }

        public void SetWindowMode(int windowId, DisplayServer.WindowMode mode)
        {
            WindowModeByWindowId[windowId] = mode;
        }

        public void SetWindowFlag(DisplayServer.WindowFlags flag, bool enabled, int windowId)
        {
            if (flag == DisplayServer.WindowFlags.Borderless)
            {
                BorderlessFlagByWindowId[windowId] = enabled;
            }
        }
    }
}
