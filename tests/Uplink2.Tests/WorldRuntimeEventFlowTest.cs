using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using Uplink2.Blueprint;
using Uplink2.Runtime;
using Uplink2.Vfs;
using Xunit;

#nullable enable

namespace Uplink2.Tests;

/// <summary>Integration-oriented tests for world-runtime event flow wiring and hook behavior.</summary>
public sealed class WorldRuntimeEventFlowTest
{
    /// <summary>Ensures execute privilege hook reveals newly unlocked net and applies initiallyExposed known-node seeds.</summary>
    [Fact]
    public void ApplySystemEventHooks_ExecutePrivilege_UpdatesVisibility()
    {
        var world = CreateEventWorldStub();
        var serverList = GetAutoProperty<Dictionary<string, ServerNodeRuntime>>(world, "ServerList");
        var visibleNets = GetAutoProperty<HashSet<string>>(world, "VisibleNets");
        var knownNodesByNet = GetAutoProperty<Dictionary<string, HashSet<string>>>(world, "KnownNodesByNet");
        var seededKnownByNet = CreateStringHashSetMap();
        seededKnownByNet["alpha"] = new HashSet<string>(StringComparer.Ordinal) { "n1", "n2" };
        SetField(world, "initiallyExposedNodesByNet", seededKnownByNet);

        var n1 = CreateServer("n1", "10.0.0.1", "alpha");
        var n2 = CreateServer("n2", "10.0.0.2", "alpha");
        serverList["n1"] = n1;
        serverList["n2"] = n2;

        visibleNets.Add("internet");
        knownNodesByNet["internet"] = new HashSet<string>(StringComparer.Ordinal);

        var payload = CreateInternal(
            "Uplink2.Runtime.Events.PrivilegeAcquireDto",
            "n1",
            "guest",
            "execute",
            0L,
            null,
            new List<string> { "alpha" });
        var gameEvent = CreateInternal(
            "Uplink2.Runtime.Events.GameEvent",
            "privilegeAcquire",
            0L,
            1L,
            payload);

        Invoke(world, "ApplySystemEventHooks", gameEvent);

        Assert.Contains("alpha", visibleNets);
        Assert.True(knownNodesByNet.TryGetValue("alpha", out var knownNodes));
        Assert.Contains("n1", knownNodes!);
        Assert.Contains("n2", knownNodes!);
        Assert.True(n1.IsExposedByNet.GetValueOrDefault("alpha"));
        Assert.True(n2.IsExposedByNet.GetValueOrDefault("alpha"));
    }

    /// <summary>Ensures EmitFileAcquire normalizes fileName via Path.GetFileName contract.</summary>
    [Fact]
    public void EmitFileAcquire_UsesPathGetFileName()
    {
        var method = typeof(WorldRuntime).GetMethod(
            "EmitFileAcquire",
            BindingFlags.Instance | BindingFlags.Public);
        Assert.NotNull(method);

        var calledMethods = ExtractCalledMethods((MethodInfo)method!);
        Assert.Contains(
            calledMethods,
            static called => called.DeclaringType == typeof(System.IO.Path) &&
                             called.Name == nameof(System.IO.Path.GetFileName));
    }

    /// <summary>Ensures print lines can be queued through the internal queue bridge used by action executor.</summary>
    [Fact]
    public void QueueTerminalEventLine_AppendsToQueue()
    {
        var world = CreateEventWorldStub();
        var terminalLine = CreateInternal("Uplink2.Runtime.Events.TerminalEventLine", "node-1", "guest", "A");

        Invoke(world, "QueueTerminalEventLine", terminalLine);

        var queue = GetField(world, "terminalEventLines");
        var queueCount = (int)GetProperty(queue, "Count");
        Assert.Equal(1, queueCount);
    }

    /// <summary>Ensures due running processes are finished and processFinished events are queued.</summary>
    [Fact]
    public void ProcessDueProcesses_TransitionsStateAndQueuesEvent()
    {
        var world = CreateEventWorldStub();
        var processList = GetAutoProperty<Dictionary<int, ProcessStruct>>(world, "ProcessList");
        var serverList = GetAutoProperty<Dictionary<string, ServerNodeRuntime>>(world, "ServerList");
        var hostServer = CreateServer("host-1", "10.0.0.1", "internet");
        hostServer.AddProcess(99);
        serverList[hostServer.NodeId] = hostServer;

        processList[99] = new ProcessStruct
        {
            Name = "ftp",
            HostNodeId = hostServer.NodeId,
            UserKey = "guest",
            State = ProcessState.Running,
            EndAt = 10,
            ProcessType = ProcessType.FtpSend,
        };

        Invoke(world, "ProcessDueProcesses", 15L);

        Assert.Equal(ProcessState.Finished, processList[99].State);
        Assert.DoesNotContain(99, hostServer.Process);

        var eventQueue = GetField(world, "eventQueue");
        var queueCount = (int)GetProperty(eventQueue, "Count");
        Assert.Equal(1, queueCount);
    }

    /// <summary>Ensures LoadBlueprintCatalog wires BlueprintYamlReader warning sink through constructor overload.</summary>
    [Fact]
    public void LoadBlueprintCatalog_UsesReaderWarningSinkConstructor()
    {
        var method = typeof(WorldRuntime).GetMethod(
            "LoadBlueprintCatalog",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);

        var calledMethods = ExtractCalledMethods((MethodInfo)method!);
        Assert.Contains(
            calledMethods,
            static called => string.Equals(called.DeclaringType?.FullName, "Uplink2.Blueprint.BlueprintYamlReader", StringComparison.Ordinal) &&
                             string.Equals(called.Name, ".ctor", StringComparison.Ordinal) &&
                             called.GetParameters().Length == 1);
    }

    /// <summary>Ensures startup scenario resolution loads campaign scenarios first, then child campaigns recursively.</summary>
    [Fact]
    public void ResolveStartupScenarios_TraversesCampaignTreeInOrder()
    {
        var world = (WorldRuntime)RuntimeHelpers.GetUninitializedObject(typeof(WorldRuntime));
        SetAutoProperty(world, "StartupScenarioId", string.Empty);
        SetAutoProperty(world, "StartupCampaignId", "gameCampaign");

        var catalog = new BlueprintCatalog();
        catalog.Scenarios["rootA"] = new ScenarioBlueprint
        {
            ScenarioId = "rootA",
        };
        catalog.Scenarios["rootB"] = new ScenarioBlueprint
        {
            ScenarioId = "rootB",
        };
        catalog.Scenarios["childA"] = new ScenarioBlueprint
        {
            ScenarioId = "childA",
        };
        catalog.Scenarios["grandA"] = new ScenarioBlueprint
        {
            ScenarioId = "grandA",
        };

        var rootCampaign = new CampaignBlueprint
        {
            CampaignId = "gameCampaign",
        };
        rootCampaign.Scenarios.Add("rootA");
        rootCampaign.Scenarios.Add("rootB");
        rootCampaign.ChildCampaigns.Add("prototypeCampaign");
        catalog.Campaigns["gameCampaign"] = rootCampaign;

        var childCampaign = new CampaignBlueprint
        {
            CampaignId = "prototypeCampaign",
        };
        childCampaign.Scenarios.Add("childA");
        childCampaign.ChildCampaigns.Add("grandCampaign");
        catalog.Campaigns["prototypeCampaign"] = childCampaign;

        var grandCampaign = new CampaignBlueprint
        {
            CampaignId = "grandCampaign",
        };
        grandCampaign.Scenarios.Add("grandA");
        catalog.Campaigns["grandCampaign"] = grandCampaign;

        var scenarios = (IReadOnlyList<ScenarioBlueprint>)Invoke(world, "ResolveStartupScenarios", catalog);
        Assert.Equal(4, scenarios.Count);
        Assert.Equal("rootA", scenarios[0].ScenarioId);
        Assert.Equal("rootB", scenarios[1].ScenarioId);
        Assert.Equal("childA", scenarios[2].ScenarioId);
        Assert.Equal("grandA", scenarios[3].ScenarioId);
    }

    private static WorldRuntime CreateEventWorldStub()
    {
        var world = (WorldRuntime)RuntimeHelpers.GetUninitializedObject(typeof(WorldRuntime));
        SetAutoProperty(world, "ServerList", new Dictionary<string, ServerNodeRuntime>(StringComparer.Ordinal));
        SetAutoProperty(world, "ProcessList", new Dictionary<int, ProcessStruct>());
        SetAutoProperty(world, "VisibleNets", new HashSet<string>(StringComparer.Ordinal));
        SetAutoProperty(world, "KnownNodesByNet", new Dictionary<string, HashSet<string>>(StringComparer.Ordinal));
        SetAutoProperty(world, "ScenarioFlags", new Dictionary<string, object>(StringComparer.Ordinal));

        SetField(world, "eventQueue", CreateInternal("Uplink2.Runtime.Events.EventQueue"));
        SetField(world, "firedHandlerIds", new HashSet<string>(StringComparer.Ordinal));
        SetField(world, "terminalEventLines", CreateGenericQueue("Uplink2.Runtime.Events.TerminalEventLine"));
        SetField(world, "initiallyExposedNodesByNet", CreateStringHashSetMap());
        SetField(world, "eventIndex", CreateInternal("Uplink2.Runtime.Events.EventIndex"));
        SetField(world, "processScheduler", CreateInternal("Uplink2.Runtime.Events.ProcessScheduler"));
        SetField(world, "scheduledProcessEndAtById", new Dictionary<int, long>());
        return world;
    }

    private static object CreateGenericQueue(string typeName)
    {
        var elementType = RequireRuntimeType(typeName);
        var queueType = typeof(Queue<>).MakeGenericType(elementType);
        var instance = Activator.CreateInstance(queueType);
        Assert.NotNull(instance);
        return instance!;
    }

    private static Dictionary<string, HashSet<string>> CreateStringHashSetMap()
    {
        return new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
    }

    private static ServerNodeRuntime CreateServer(string nodeId, string ip, string netId)
    {
        var blobStore = new BlobStore();
        var baseFileSystem = new BaseFileSystem(blobStore);
        var server = new ServerNodeRuntime(nodeId, nodeId, ServerRole.Terminal, baseFileSystem, blobStore);
        server.Users["guest"] = new UserConfig
        {
            UserId = "guest",
            AuthMode = AuthMode.None,
            Privilege = PrivilegeConfig.FullAccess(),
        };

        server.SetInterfaces(new[]
        {
            new InterfaceRuntime
            {
                NetId = netId,
                Ip = ip,
            },
        });

        return server;
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
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        Assert.NotNull(method);
        return method!.Invoke(target, args)!;
    }

    private static object GetField(object target, string fieldName)
    {
        var field = target.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        return field!.GetValue(target)!;
    }

    private static object GetProperty(object target, string propertyName)
    {
        var property = target.GetType().GetProperty(propertyName, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        Assert.NotNull(property);
        return property!.GetValue(target)!;
    }

    private static T GetAutoProperty<T>(object target, string propertyName)
    {
        var field = target.GetType().GetField(
            $"<{propertyName}>k__BackingField",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        return (T)field!.GetValue(target)!;
    }

    private static void SetAutoProperty(object target, string propertyName, object value)
    {
        var field = target.GetType().GetField(
            $"<{propertyName}>k__BackingField",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        field!.SetValue(target, value);
    }

    private static void SetField(object target, string fieldName, object value)
    {
        var field = target.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        field!.SetValue(target, value);
    }

    private static IReadOnlyList<MethodBase> ExtractCalledMethods(MethodInfo method)
    {
        var ilBytes = method.GetMethodBody()?.GetILAsByteArray();
        if (ilBytes is null || ilBytes.Length == 0)
        {
            return Array.Empty<MethodBase>();
        }

        var methods = new List<MethodBase>();
        for (var index = 0; index <= ilBytes.Length - 5; index++)
        {
            if (ilBytes[index] != 0x28 && ilBytes[index] != 0x6F && ilBytes[index] != 0x73)
            {
                continue;
            }

            var token = BitConverter.ToInt32(ilBytes, index + 1);
            try
            {
                var resolved = method.Module.ResolveMethod(token);
                if (resolved is not null)
                {
                    methods.Add(resolved);
                }
            }
            catch (ArgumentException)
            {
            }
            catch (BadImageFormatException)
            {
            }
        }

        return methods;
    }

    private static Type RequireRuntimeType(string fullTypeName)
    {
        var type = typeof(WorldRuntime).Assembly.GetType(fullTypeName);
        Assert.NotNull(type);
        return type!;
    }
}
