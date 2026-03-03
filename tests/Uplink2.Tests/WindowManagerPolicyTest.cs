using Godot;
using System;
using System.Collections.Generic;
using System.Reflection;
using Uplink2.Runtime;
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

    /// <summary>Ensures node collection includes internet-known nodes plus workstation and excludes non-internet known sets.</summary>
    [Fact]
    public void CollectWorldMapTraceNodeIds_UsesInternetKnownPlusWorkstationOnly()
    {
        var method = typeof(WindowManager).GetMethod(
            "CollectWorldMapTraceNodeIds",
            BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(method);

        var knownByNet = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal)
        {
            ["internet"] = new HashSet<string>(StringComparer.Ordinal) { "node-b", "node-a", "node-a" },
            ["corp_lan"] = new HashSet<string>(StringComparer.Ordinal) { "node-c" },
        };

        var result = method!.Invoke(null, new object[] { knownByNet, "node-ws" });
        Assert.NotNull(result);
        var nodeIds = Assert.IsType<List<string>>(result);
        Assert.Equal(new[] { "node-a", "node-b", "node-ws" }, nodeIds);
    }

    /// <summary>Ensures equirectangular world-map projection maps key coordinates to viewport edges/center.</summary>
    [Fact]
    public void ProjectWorldMapLocation_MapsCoordinatesToViewport()
    {
        var method = typeof(WindowManager).GetMethod(
            "ProjectWorldMapLocation",
            BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(method);

        var viewport = new Vector2(512, 256);
        var northWest = (Vector2)method!.Invoke(null, new object[] { 90d, -180d, viewport })!;
        var center = (Vector2)method.Invoke(null, new object[] { 0d, 0d, viewport })!;
        var southEast = (Vector2)method.Invoke(null, new object[] { -90d, 180d, viewport })!;

        Assert.Equal(0f, northWest.X, 3);
        Assert.Equal(0f, northWest.Y, 3);
        Assert.Equal(256f, center.X, 3);
        Assert.Equal(128f, center.Y, 3);
        Assert.Equal(512f, southEast.X, 3);
        Assert.Equal(256f, southEast.Y, 3);
    }

    /// <summary>Ensures fill color priority and outline mode mapping follow WORLD_MAP_TRACE icon contract.</summary>
    [Fact]
    public void ResolveWorldMapNodeStyle_AppliesFillPriorityAndOutlineModes()
    {
        var fillMethod = typeof(WindowManager).GetMethod(
            "ResolveWorldMapNodeFillColor",
            BindingFlags.Static | BindingFlags.NonPublic);
        var outlineMethod = typeof(WindowManager).GetMethod(
            "ResolveWorldMapNodeOutlineMode",
            BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(fillMethod);
        Assert.NotNull(outlineMethod);

        var offlineFill = (Color)fillMethod!.Invoke(null, new object[] { true, true })!;
        var workstationFill = (Color)fillMethod.Invoke(null, new object[] { false, true })!;
        var onlineFill = (Color)fillMethod.Invoke(null, new object[] { false, false })!;

        Assert.Equal(0.5f, offlineFill.R, 3);
        Assert.Equal(0.5f, offlineFill.G, 3);
        Assert.Equal(0.5f, offlineFill.B, 3);
        Assert.Equal(0.18f, workstationFill.R, 3);
        Assert.Equal(0.9f, workstationFill.G, 3);
        Assert.Equal(1f, onlineFill.R, 3);
        Assert.Equal(1f, onlineFill.G, 3);
        Assert.Equal(1f, onlineFill.B, 3);

        var rebootOutline = outlineMethod!.Invoke(null, new object[] { ServerReason.Reboot });
        var disabledOutline = outlineMethod.Invoke(null, new object[] { ServerReason.Disabled });
        var crashedOutline = outlineMethod.Invoke(null, new object[] { ServerReason.Crashed });
        var okOutline = outlineMethod.Invoke(null, new object[] { ServerReason.Ok });

        Assert.Equal("PulseRed", rebootOutline?.ToString());
        Assert.Equal("SolidRed", disabledOutline?.ToString());
        Assert.Equal("SolidRed", crashedOutline?.ToString());
        Assert.Equal("None", okOutline?.ToString());
    }

    /// <summary>Ensures SSH render builder filters non-visible endpoints and dedupes directed edges.</summary>
    [Fact]
    public void BuildWorldMapSshRenderStates_FiltersVisibleEndpoints_DedupesDirectedEdges_AndAnchorsToMarkers()
    {
        var windowManagerType = typeof(WindowManager);
        var snapshotType = windowManagerType.Assembly.GetType("Uplink2.Runtime.WorldRuntime+ActiveSshSessionEdgeSnapshot");
        Assert.NotNull(snapshotType);
        var buildMethod = windowManagerType.GetMethod(
            "BuildWorldMapSshRenderStates",
            BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(buildMethod);

        var sshEdgeSnapshots = CreateTypedList(
            snapshotType!,
            CreateSshEdgeSnapshot(snapshotType!, "node-a", "node-b", 1),
            CreateSshEdgeSnapshot(snapshotType!, "node-a", "node-b", 2),
            CreateSshEdgeSnapshot(snapshotType!, "node-a", "node-c", 3),
            CreateSshEdgeSnapshot(snapshotType!, "node-b", "node-a", 4));
        var projectedPositions = new Dictionary<string, Vector2>(StringComparer.Ordinal)
        {
            ["node-a"] = new Vector2(0f, 0f),
            ["node-b"] = new Vector2(30f, 0f),
        };
        var args = new object?[] { sshEdgeSnapshots, projectedPositions, null, null, null };
        _ = buildMethod!.Invoke(null, args);

        Assert.NotNull(args[2]);
        Assert.NotNull(args[3]);
        Assert.NotNull(args[4]);
        var lines = ToObjectList(args[2]!);
        var startArcs = ToObjectList(args[3]!);
        var targetMarkers = ToObjectList(args[4]!);

        Assert.Equal(2, lines.Count);
        Assert.Empty(startArcs);
        Assert.Empty(targetMarkers);

        var hasForwardLine = false;
        var hasReverseLine = false;
        foreach (var line in lines)
        {
            var start = GetPropertyValue<Vector2>(line, "Start");
            var end = GetPropertyValue<Vector2>(line, "End");
            if (Math.Abs(start.X - 0f) <= 0.001f && Math.Abs(end.X - 30f) <= 0.001f)
            {
                hasForwardLine = true;
            }

            if (Math.Abs(start.X - 30f) <= 0.001f && Math.Abs(end.X - 0f) <= 0.001f)
            {
                hasReverseLine = true;
            }
        }

        Assert.True(hasForwardLine);
        Assert.True(hasReverseLine);
    }

    /// <summary>Ensures only chain boundaries use SSH markers while middle hops connect center-to-center.</summary>
    [Fact]
    public void BuildWorldMapSshRenderStates_UsesMarkersOnlyAtChainBoundaries()
    {
        var windowManagerType = typeof(WindowManager);
        var snapshotType = windowManagerType.Assembly.GetType("Uplink2.Runtime.WorldRuntime+ActiveSshSessionEdgeSnapshot");
        Assert.NotNull(snapshotType);
        var buildMethod = windowManagerType.GetMethod(
            "BuildWorldMapSshRenderStates",
            BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(buildMethod);

        var sshEdgeSnapshots = CreateTypedList(
            snapshotType!,
            CreateSshEdgeSnapshot(snapshotType!, "node-a", "node-b", 1),
            CreateSshEdgeSnapshot(snapshotType!, "node-b", "node-c", 2),
            CreateSshEdgeSnapshot(snapshotType!, "node-c", "node-d", 3));
        var projectedPositions = new Dictionary<string, Vector2>(StringComparer.Ordinal)
        {
            ["node-a"] = new Vector2(0f, 0f),
            ["node-b"] = new Vector2(30f, 0f),
            ["node-c"] = new Vector2(60f, 0f),
            ["node-d"] = new Vector2(90f, 0f),
        };
        var args = new object?[] { sshEdgeSnapshots, projectedPositions, null, null, null };
        _ = buildMethod!.Invoke(null, args);

        var lines = ToObjectList(args[2]!);
        var startArcs = ToObjectList(args[3]!);
        var targetMarkers = ToObjectList(args[4]!);
        Assert.Equal(3, lines.Count);
        Assert.Equal(2, startArcs.Count);
        Assert.Single(targetMarkers);

        var hasFirstSegment = false;
        var hasMiddleSegment = false;
        var hasLastSegment = false;
        foreach (var line in lines)
        {
            var start = GetPropertyValue<Vector2>(line, "Start");
            var end = GetPropertyValue<Vector2>(line, "End");
            if (Math.Abs(start.X - 7.25f) <= 0.001f && Math.Abs(end.X - 30f) <= 0.001f)
            {
                hasFirstSegment = true;
            }

            if (Math.Abs(start.X - 30f) <= 0.001f && Math.Abs(end.X - 60f) <= 0.001f)
            {
                hasMiddleSegment = true;
            }

            if (Math.Abs(start.X - 60f) <= 0.001f && Math.Abs(end.X - 74f) <= 0.001f)
            {
                hasLastSegment = true;
            }
        }

        Assert.True(hasFirstSegment);
        Assert.True(hasMiddleSegment);
        Assert.True(hasLastSegment);
    }

    /// <summary>Ensures source arc coverage merges into a full ring when angle ranges cover 360 degrees.</summary>
    [Fact]
    public void BuildWorldMapSshRenderStates_ConvertsFullCoverageToRingArc()
    {
        var windowManagerType = typeof(WindowManager);
        var snapshotType = windowManagerType.Assembly.GetType("Uplink2.Runtime.WorldRuntime+ActiveSshSessionEdgeSnapshot");
        Assert.NotNull(snapshotType);
        var buildMethod = windowManagerType.GetMethod(
            "BuildWorldMapSshRenderStates",
            BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(buildMethod);

        var sshEdgeSnapshots = CreateTypedList(
            snapshotType!,
            CreateSshEdgeSnapshot(snapshotType!, "source", "n0", 1),
            CreateSshEdgeSnapshot(snapshotType!, "source", "n72", 2),
            CreateSshEdgeSnapshot(snapshotType!, "source", "n144", 3),
            CreateSshEdgeSnapshot(snapshotType!, "source", "n216", 4),
            CreateSshEdgeSnapshot(snapshotType!, "source", "n288", 5));
        var projectedPositions = new Dictionary<string, Vector2>(StringComparer.Ordinal)
        {
            ["source"] = Vector2.Zero,
            ["n0"] = new Vector2(100f, 0f),
            ["n72"] = new Vector2(30.9017f, 95.1057f),
            ["n144"] = new Vector2(-80.9017f, 58.7785f),
            ["n216"] = new Vector2(-80.9017f, -58.7785f),
            ["n288"] = new Vector2(30.9017f, -95.1057f),
        };
        var args = new object?[] { sshEdgeSnapshots, projectedPositions, null, null, null };
        _ = buildMethod!.Invoke(null, args);

        Assert.NotNull(args[3]);
        var startArcs = ToObjectList(args[3]!);
        Assert.Single(startArcs);
        Assert.True(GetPropertyValue<bool>(startArcs[0], "IsFullRing"));
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

    private static object CreateSshEdgeSnapshot(Type snapshotType, string sourceNodeId, string targetNodeId, int sessionId)
    {
        var snapshot = Activator.CreateInstance(snapshotType);
        Assert.NotNull(snapshot);
        SetPropertyValue(snapshot!, "SourceNodeId", sourceNodeId);
        SetPropertyValue(snapshot!, "TargetNodeId", targetNodeId);
        SetPropertyValue(snapshot!, "SessionId", sessionId);
        return snapshot!;
    }

    private static object CreateTypedList(Type itemType, params object[] items)
    {
        var listType = typeof(List<>).MakeGenericType(itemType);
        var list = Activator.CreateInstance(listType);
        Assert.NotNull(list);
        var addMethod = listType.GetMethod("Add");
        Assert.NotNull(addMethod);
        foreach (var item in items)
        {
            addMethod!.Invoke(list, new[] { item });
        }

        return list!;
    }

    private static List<object> ToObjectList(object enumerable)
    {
        Assert.IsAssignableFrom<System.Collections.IEnumerable>(enumerable);
        var result = new List<object>();
        foreach (var item in (System.Collections.IEnumerable)enumerable)
        {
            if (item is not null)
            {
                result.Add(item);
            }
        }

        return result;
    }

    private static void SetPropertyValue(object target, string propertyName, object? value)
    {
        var property = target.GetType().GetProperty(
            propertyName,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        Assert.NotNull(property);
        property!.SetValue(target, value);
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
