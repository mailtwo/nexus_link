using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.CompilerServices;
using Uplink2.Blueprint;
using Uplink2.Runtime;
using Uplink2.Vfs;
using Xunit;

#nullable enable

namespace Uplink2.Tests;

/// <summary>Unit tests for event indexing/dispatch, action execution, guard evaluation, and min-heap scheduling.</summary>
public sealed class EventHandlerTest
{
    /// <summary>Ensures ANY sentinel combinations in EventIndex match both specific and wildcard handlers.</summary>
    [Fact]
    public void EventIndex_Query_PrivilegeAcquire_ResolvesAnyCombinations()
    {
        var eventIndex = CreateInternal("Uplink2.Runtime.Events.EventIndex");
        var any = "__ANY__";

        Invoke(eventIndex, "Add", CreateDescriptor("event_specific", BlueprintConditionType.PrivilegeAcquire, "n1", "u1", "execute", any, null, System.Array.Empty<ActionBlueprint>()));
        Invoke(eventIndex, "Add", CreateDescriptor("event_any_user", BlueprintConditionType.PrivilegeAcquire, "n1", any, "execute", any, null, System.Array.Empty<ActionBlueprint>()));
        Invoke(eventIndex, "Add", CreateDescriptor("event_any_privilege", BlueprintConditionType.PrivilegeAcquire, any, any, any, any, null, System.Array.Empty<ActionBlueprint>()));

        var payload = CreatePrivilegePayload("n1", "u1", "execute");
        var gameEvent = CreateGameEvent("privilegeAcquire", payload, 1);
        var queryResult = Invoke(eventIndex, "Query", gameEvent);

        var matches = ToObjectList(queryResult);
        Assert.Equal(3, matches.Count);
    }

    /// <summary>Ensures once-only handler firing prevents duplicate action execution across repeated matching events.</summary>
    [Fact]
    public void EventDispatcher_OnceOnly_PreventsDuplicateFiring()
    {
        var warnings = new List<string>();
        var world = CreateWorldStub();
        var eventIndex = CreateInternal("Uplink2.Runtime.Events.EventIndex");
        var firedHandlerIds = new HashSet<string>(StringComparer.Ordinal);
        var guardEvaluator = CreateInternal("Uplink2.Runtime.Events.GuardEvaluator", (Action<string>)warnings.Add);
        var actionExecutor = CreateInternal("Uplink2.Runtime.Events.ActionExecutor", world, (Action<string>)warnings.Add);
        var dispatcher = CreateDispatcher(eventIndex, firedHandlerIds, guardEvaluator, actionExecutor);
        var eventQueue = CreateInternal("Uplink2.Runtime.Events.EventQueue");
        var scenarioFlags = new Dictionary<string, object>(StringComparer.Ordinal);

        var descriptor = CreateDescriptor(
            "once_event",
            BlueprintConditionType.PrivilegeAcquire,
            "node-1",
            "guest",
            "execute",
            "__ANY__",
            null,
            new[] { PrintAction("hello-once") });
        Invoke(eventIndex, "Add", descriptor);

        var payload = CreatePrivilegePayload("node-1", "guest", "execute");
        Invoke(eventQueue, "Enqueue", CreateGameEvent("privilegeAcquire", payload, 1));
        Invoke(eventQueue, "Enqueue", CreateGameEvent("privilegeAcquire", payload, 2));

        Invoke(dispatcher, "Drain", eventQueue, scenarioFlags);

        var queuedLines = SnapshotTerminalLines(world);
        Assert.Single(queuedLines);
        Assert.Equal("hello-once", queuedLines[0]);
        Assert.Contains("once_event", firedHandlerIds);
    }

    /// <summary>Ensures guard true/false branching controls whether actions run.</summary>
    [Fact]
    public void EventDispatcher_GuardBranching_RunsActionsOnlyWhenTrue()
    {
        var warnings = new List<string>();
        var world = CreateWorldStub();
        var eventIndex = CreateInternal("Uplink2.Runtime.Events.EventIndex");
        var firedHandlerIds = new HashSet<string>(StringComparer.Ordinal);
        var guardEvaluator = CreateInternal("Uplink2.Runtime.Events.GuardEvaluator", (Action<string>)warnings.Add);
        var actionExecutor = CreateInternal("Uplink2.Runtime.Events.ActionExecutor", world, (Action<string>)warnings.Add);
        var dispatcher = CreateDispatcher(eventIndex, firedHandlerIds, guardEvaluator, actionExecutor);
        var eventQueue = CreateInternal("Uplink2.Runtime.Events.EventQueue");
        var scenarioFlags = new Dictionary<string, object>(StringComparer.Ordinal);

        var guard = Invoke(
            guardEvaluator,
            "Compile",
            "s1",
            "guard_event",
            ParseEnum("Uplink2.Runtime.Events.GuardSourceKind", "Inline"),
            "inline",
            "return evt.privilege == \"execute\"");
        var descriptor = CreateDescriptor(
            "guard_event",
            BlueprintConditionType.PrivilegeAcquire,
            "node-1",
            "guest",
            "__ANY__",
            "__ANY__",
            guard,
            new[] { SetFlagAction("passed", true) });
        Invoke(eventIndex, "Add", descriptor);

        Invoke(eventQueue, "Enqueue", CreateGameEvent("privilegeAcquire", CreatePrivilegePayload("node-1", "guest", "read"), 1));
        Invoke(dispatcher, "Drain", eventQueue, scenarioFlags);
        Assert.False(world.ScenarioFlags.ContainsKey("passed"));

        Invoke(eventQueue, "Enqueue", CreateGameEvent("privilegeAcquire", CreatePrivilegePayload("node-1", "guest", "execute"), 2));
        Invoke(dispatcher, "Drain", eventQueue, scenarioFlags);
        Assert.True(world.ScenarioFlags.ContainsKey("passed"));
    }

    /// <summary>Ensures guard timeout returns false, emits warning, and does not execute actions.</summary>
    [Fact]
    public void EventDispatcher_GuardTimeout_WarnsAndSkipsActions()
    {
        var warnings = new List<string>();
        var world = CreateWorldStub();
        var eventIndex = CreateInternal("Uplink2.Runtime.Events.EventIndex");
        var firedHandlerIds = new HashSet<string>(StringComparer.Ordinal);
        var guardEvaluator = CreateInternal("Uplink2.Runtime.Events.GuardEvaluator", (Action<string>)warnings.Add);
        var actionExecutor = CreateInternal("Uplink2.Runtime.Events.ActionExecutor", world, (Action<string>)warnings.Add);
        var dispatcher = CreateDispatcher(eventIndex, firedHandlerIds, guardEvaluator, actionExecutor);
        var eventQueue = CreateInternal("Uplink2.Runtime.Events.EventQueue");
        var scenarioFlags = new Dictionary<string, object>(StringComparer.Ordinal);

        var guard = Invoke(
            guardEvaluator,
            "Compile",
            "s1",
            "timeout_event",
            ParseEnum("Uplink2.Runtime.Events.GuardSourceKind", "Inline"),
            "inline",
            "while true\nx = 1\nend while");
        var descriptor = CreateDescriptor(
            "timeout_event",
            BlueprintConditionType.PrivilegeAcquire,
            "node-1",
            "guest",
            "__ANY__",
            "__ANY__",
            guard,
            new[] { PrintAction("should-not-print") });
        Invoke(eventIndex, "Add", descriptor);
        Invoke(eventQueue, "Enqueue", CreateGameEvent("privilegeAcquire", CreatePrivilegePayload("node-1", "guest", "execute"), 1));

        Invoke(dispatcher, "Drain", eventQueue, scenarioFlags);

        Assert.Empty(SnapshotTerminalLines(world));
        Assert.Contains(warnings, static warning => warning.Contains("timeout", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Ensures action execution continues when one action fails and preserves print ordering.</summary>
    [Fact]
    public void EventDispatcher_ActionPartialFailure_ContinuesAndPreservesOrder()
    {
        var warnings = new List<string>();
        var world = CreateWorldStub();
        var eventIndex = CreateInternal("Uplink2.Runtime.Events.EventIndex");
        var firedHandlerIds = new HashSet<string>(StringComparer.Ordinal);
        var guardEvaluator = CreateInternal("Uplink2.Runtime.Events.GuardEvaluator", (Action<string>)warnings.Add);
        var actionExecutor = CreateInternal("Uplink2.Runtime.Events.ActionExecutor", world, (Action<string>)warnings.Add);
        var dispatcher = CreateDispatcher(eventIndex, firedHandlerIds, guardEvaluator, actionExecutor);
        var eventQueue = CreateInternal("Uplink2.Runtime.Events.EventQueue");
        var scenarioFlags = new Dictionary<string, object>(StringComparer.Ordinal);

        var descriptor = CreateDescriptor(
            "action_partial_event",
            BlueprintConditionType.PrivilegeAcquire,
            "node-1",
            "guest",
            "execute",
            "__ANY__",
            null,
            new[]
            {
                SetFlagActionWithoutKey(value: 1),
                PrintAction("A"),
                PrintAction("B"),
            });
        Invoke(eventIndex, "Add", descriptor);
        Invoke(eventQueue, "Enqueue", CreateGameEvent("privilegeAcquire", CreatePrivilegePayload("node-1", "guest", "execute"), 1));

        Invoke(dispatcher, "Drain", eventQueue, scenarioFlags);

        var queuedLines = SnapshotTerminalLines(world);
        Assert.Equal(2, queuedLines.Count);
        Assert.Equal("A", queuedLines[0]);
        Assert.Equal("B", queuedLines[1]);
        Assert.Contains(warnings, static warning => warning.Contains("setFlag", StringComparison.Ordinal));
    }

    /// <summary>Ensures ProcessScheduler pops due processes by endAt order and ignores stale update entries.</summary>
    [Fact]
    public void ProcessScheduler_PopDue_RespectsMinHeapOrderAndStaleEntries()
    {
        var scheduler = CreateInternal("Uplink2.Runtime.Events.ProcessScheduler");
        var processList = new Dictionary<int, ProcessStruct>
        {
            [1] = new ProcessStruct { Name = "p1", State = ProcessState.Running, EndAt = 100 },
            [2] = new ProcessStruct { Name = "p2", State = ProcessState.Running, EndAt = 50 },
        };

        Invoke(scheduler, "ScheduleOrUpdate", 1, 100L);
        Invoke(scheduler, "ScheduleOrUpdate", 2, 50L);
        Invoke(scheduler, "ScheduleOrUpdate", 1, 200L);
        processList[1].EndAt = 200;

        var dueAt60 = ToIntList(Invoke(scheduler, "PopDue", 60L, processList));
        Assert.Single(dueAt60);
        Assert.Equal(2, dueAt60[0]);

        var dueAt150 = ToIntList(Invoke(scheduler, "PopDue", 150L, processList));
        Assert.Empty(dueAt150);

        var dueAt250 = ToIntList(Invoke(scheduler, "PopDue", 250L, processList));
        Assert.Single(dueAt250);
        Assert.Equal(1, dueAt250[0]);
    }

    /// <summary>Ensures drain defers remaining events when tick guard budget is exhausted.</summary>
    [Fact]
    public void EventDispatcher_Drain_DefersRemainingEventsWhenBudgetExhausted()
    {
        var warnings = new List<string>();
        var world = CreateWorldStub();
        var eventIndex = CreateInternal("Uplink2.Runtime.Events.EventIndex");
        var firedHandlerIds = new HashSet<string>(StringComparer.Ordinal);
        var guardEvaluator = CreateInternal("Uplink2.Runtime.Events.GuardEvaluator", (Action<string>)warnings.Add);
        var actionExecutor = CreateInternal("Uplink2.Runtime.Events.ActionExecutor", world, (Action<string>)warnings.Add);
        var dispatcher = CreateDispatcher(eventIndex, firedHandlerIds, guardEvaluator, actionExecutor);
        var eventQueue = CreateInternal("Uplink2.Runtime.Events.EventQueue");
        var scenarioFlags = new Dictionary<string, object>(StringComparer.Ordinal);

        var timeoutGuard = Invoke(
            guardEvaluator,
            "Compile",
            "s1",
            "budget_event",
            ParseEnum("Uplink2.Runtime.Events.GuardSourceKind", "Inline"),
            "inline",
            "while true\nx = 1\nend while");
        var descriptor = CreateDescriptor(
            "budget_event",
            BlueprintConditionType.PrivilegeAcquire,
            "node-1",
            "guest",
            "__ANY__",
            "__ANY__",
            timeoutGuard,
            new[] { PrintAction("never") });
        Invoke(eventIndex, "Add", descriptor);

        for (var seq = 0; seq < 6; seq++)
        {
            Invoke(eventQueue, "Enqueue", CreateGameEvent("privilegeAcquire", CreatePrivilegePayload("node-1", "guest", "execute"), seq + 1));
        }

        Invoke(dispatcher, "Drain", eventQueue, scenarioFlags);
        var remainingAfterFirstDrain = (int)GetProperty(eventQueue, "Count");
        Assert.True(remainingAfterFirstDrain > 0);

        Invoke(dispatcher, "Drain", eventQueue, scenarioFlags);
        var remainingAfterSecondDrain = (int)GetProperty(eventQueue, "Count");
        Assert.True(remainingAfterSecondDrain < remainingAfterFirstDrain);
    }

    private static WorldRuntime CreateWorldStub()
    {
        var world = (WorldRuntime)RuntimeHelpers.GetUninitializedObject(typeof(WorldRuntime));
        SetField(world, "<ScenarioFlags>k__BackingField", new Dictionary<string, object>(StringComparer.Ordinal));

        var terminalLineType = RequireRuntimeType("Uplink2.Runtime.Events.TerminalEventLine");
        var queueType = typeof(Queue<>).MakeGenericType(terminalLineType);
        var queueInstance = Activator.CreateInstance(queueType);
        Assert.NotNull(queueInstance);
        SetField(world, "terminalEventLines", queueInstance!);

        var blobStore = new BlobStore();
        var baseFileSystem = new BaseFileSystem(blobStore);
        var server = new ServerNodeRuntime("node-1", "node-1", ServerRole.Terminal, baseFileSystem, blobStore);
        server.Users["guest"] = new UserConfig
        {
            UserId = "guest",
            AuthMode = AuthMode.None,
            Privilege = PrivilegeConfig.FullAccess(),
        };

        SetField(world, "<PlayerWorkstationServer>k__BackingField", server);
        return world;
    }

    private static object CreateDispatcher(
        object eventIndex,
        HashSet<string> firedHandlerIds,
        object guardEvaluator,
        object actionExecutor)
    {
        var gameEventType = RequireRuntimeType("Uplink2.Runtime.Events.GameEvent");
        var preDispatchHookType = typeof(Action<>).MakeGenericType(gameEventType);
        var parameter = Expression.Parameter(gameEventType, "_");
        var preDispatchHook = Expression.Lambda(preDispatchHookType, Expression.Empty(), parameter).Compile();
        return CreateInternal(
            "Uplink2.Runtime.Events.EventDispatcher",
            eventIndex,
            firedHandlerIds,
            guardEvaluator,
            actionExecutor,
            preDispatchHook);
    }

    private static object CreateDescriptor(
        string eventId,
        BlueprintConditionType conditionType,
        string nodeIdKey,
        string userKey,
        string privilegeKey,
        string fileNameKey,
        object? guard,
        IReadOnlyList<ActionBlueprint> actions)
    {
        return CreateInternal(
            "Uplink2.Runtime.Events.EventHandlerDescriptor",
            "scenario",
            eventId,
            conditionType,
            nodeIdKey,
            userKey,
            privilegeKey,
            fileNameKey,
            guard,
            actions);
    }

    private static object CreatePrivilegePayload(string nodeId, string userKey, string privilege)
    {
        return CreateInternal(
            "Uplink2.Runtime.Events.PrivilegeAcquireDto",
            nodeId,
            userKey,
            privilege,
            0L,
            null,
            null);
    }

    private static object CreateGameEvent(string eventType, object payload, long seq)
    {
        return CreateInternal(
            "Uplink2.Runtime.Events.GameEvent",
            eventType,
            0L,
            seq,
            payload);
    }

    private static ActionBlueprint PrintAction(string text)
    {
        var action = new ActionBlueprint
        {
            ActionType = BlueprintActionType.Print,
        };
        action.ActionArgs["text"] = text;
        return action;
    }

    private static ActionBlueprint SetFlagAction(string key, object value)
    {
        var action = new ActionBlueprint
        {
            ActionType = BlueprintActionType.SetFlag,
        };
        action.ActionArgs["key"] = key;
        action.ActionArgs["value"] = value;
        return action;
    }

    private static ActionBlueprint SetFlagActionWithoutKey(object value)
    {
        var action = new ActionBlueprint
        {
            ActionType = BlueprintActionType.SetFlag,
        };
        action.ActionArgs["value"] = value;
        return action;
    }

    private static object ParseEnum(string enumTypeName, string enumValue)
    {
        var enumType = RequireRuntimeType(enumTypeName);
        return Enum.Parse(enumType, enumValue, ignoreCase: false);
    }

    private static object CreateInternal(string fullTypeName, params object?[] args)
    {
        var type = RequireRuntimeType(fullTypeName);
        var instance = Activator.CreateInstance(
            type,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            binder: null,
            args: args,
            culture: null);
        Assert.NotNull(instance);
        return instance!;
    }

    private static object Invoke(object target, string methodName, params object?[] args)
    {
        var method = target.GetType().GetMethod(
            methodName,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        Assert.NotNull(method);
        return method!.Invoke(target, args)!;
    }

    private static object GetProperty(object target, string propertyName)
    {
        var property = target.GetType().GetProperty(
            propertyName,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        Assert.NotNull(property);
        return property!.GetValue(target)!;
    }

    private static void SetField(object target, string fieldName, object value)
    {
        var field = target.GetType().GetField(
            fieldName,
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        field!.SetValue(target, value);
    }

    private static IReadOnlyList<object> ToObjectList(object value)
    {
        var list = new List<object>();
        foreach (var item in (IEnumerable)value)
        {
            if (item is not null)
            {
                list.Add(item);
            }
        }

        return list;
    }

    private static IReadOnlyList<int> ToIntList(object value)
    {
        var list = new List<int>();
        foreach (var item in (IEnumerable)value)
        {
            if (item is int intValue)
            {
                list.Add(intValue);
            }
        }

        return list;
    }

    private static IReadOnlyList<string> SnapshotTerminalLines(WorldRuntime world)
    {
        var queue = GetField(world, "terminalEventLines");
        var lines = new List<string>();
        foreach (var line in (IEnumerable)queue)
        {
            var textProperty = line.GetType().GetProperty(
                "Text",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            Assert.NotNull(textProperty);
            lines.Add((string)textProperty!.GetValue(line)!);
        }

        return lines;
    }

    private static object GetField(object target, string fieldName)
    {
        var field = target.GetType().GetField(
            fieldName,
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        return field!.GetValue(target)!;
    }

    private static Type RequireRuntimeType(string fullTypeName)
    {
        var type = typeof(WorldRuntime).Assembly.GetType(fullTypeName);
        Assert.NotNull(type);
        return type!;
    }
}
