using Godot;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Uplink2.Blueprint;
using Uplink2.Runtime.Events;

#nullable enable

namespace Uplink2.Runtime;

public partial class WorldRuntime
{
    private readonly EventQueue eventQueue = new();
    private readonly HashSet<string> firedHandlerIds = new(StringComparer.Ordinal);
    private readonly Queue<TerminalEventLine> terminalEventLines = new();
    private readonly object terminalEventLinesSync = new();
    private readonly Dictionary<string, HashSet<string>> initiallyExposedNodesByNet = new(StringComparer.Ordinal);
    private readonly Dictionary<int, long> scheduledProcessEndAtById = new();
    private readonly EventIndex eventIndex = new();
    private readonly ProcessScheduler processScheduler = new();

    private EventDispatcher? eventDispatcher;
    private GuardEvaluator? guardEvaluator;
    private ActionExecutor? actionExecutor;
    private long worldTickIndex;
    private long eventSeq;

    /// <summary>Scenario-level mutable key/value flags written by setFlag actions.</summary>
    public Dictionary<string, object> ScenarioFlags { get; } = new(StringComparer.Ordinal);

    /// <inheritdoc/>
    public override void _PhysicsProcess(double delta)
    {
        DrainIntrinsicQueueRequests();
        WorldTick();
    }

    /// <inheritdoc/>
    public override void _Process(double delta)
    {
        DrainIntrinsicQueueRequests();
    }

    /// <summary>Emits a privilege-acquire gameplay event when a new privilege is granted to a user.</summary>
    public void EmitPrivilegeAcquire(
        string nodeId,
        string userKey,
        string privilege,
        string? via = null,
        IEnumerable<string>? unlockedNetIds = null,
        bool emitWhenAlreadyGranted = false)
    {
        EnsureEventRuntimeServices();
        if (string.IsNullOrWhiteSpace(nodeId) ||
            string.IsNullOrWhiteSpace(userKey) ||
            string.IsNullOrWhiteSpace(privilege))
        {
            WarnRuntimeIssue(
                $"EmitPrivilegeAcquire ignored because at least one required argument is empty. nodeId='{nodeId}', userKey='{userKey}', privilege='{privilege}'.");
            return;
        }

        if (!ServerList.TryGetValue(nodeId, out var server))
        {
            WarnRuntimeIssue($"EmitPrivilegeAcquire ignored because nodeId '{nodeId}' does not exist.");
            return;
        }

        if (!server.Users.TryGetValue(userKey, out var user))
        {
            WarnRuntimeIssue($"EmitPrivilegeAcquire ignored because userKey '{userKey}' does not exist on node '{nodeId}'.");
            return;
        }

        var normalizedPrivilege = privilege.Trim().ToLowerInvariant();
        if (!TryGrantPrivilege(user, normalizedPrivilege, out var granted))
        {
            WarnRuntimeIssue($"EmitPrivilegeAcquire ignored because privilege '{privilege}' is not supported.");
            return;
        }

        if (!granted)
        {
            if (!emitWhenAlreadyGranted)
            {
                return;
            }
        }

        EnqueueGameEvent(
            EventRuntimeConstants.PrivilegeAcquireEventType,
            new PrivilegeAcquireDto(
                nodeId.Trim(),
                userKey.Trim(),
                normalizedPrivilege,
                GetCurrentWorldTimeMs(),
                via,
                unlockedNetIds?.Where(static value => !string.IsNullOrWhiteSpace(value)).Select(static value => value.Trim()).ToList()));
    }

    /// <summary>Emits a file-acquire gameplay event after normalizing the file name to basename.</summary>
    public void EmitFileAcquire(
        string fromNodeId,
        string userKey,
        string fileName,
        string? remotePath = null,
        string? localPath = null,
        int? sizeBytes = null,
        string? contentId = null,
        string? transferMethod = null)
    {
        EnsureEventRuntimeServices();
        var normalizedFileName = Path.GetFileName(fileName ?? string.Empty);
        if (string.IsNullOrWhiteSpace(normalizedFileName))
        {
            WarnRuntimeIssue(
                $"EmitFileAcquire ignored because normalized fileName is empty. fromNodeId='{fromNodeId}', userKey='{userKey}', fileName='{fileName}'.");
            return;
        }

        EnqueueGameEvent(
            EventRuntimeConstants.FileAcquireEventType,
            new FileAcquireDto(
                fromNodeId?.Trim() ?? string.Empty,
                userKey?.Trim() ?? string.Empty,
                normalizedFileName,
                GetCurrentWorldTimeMs(),
                remotePath,
                localPath,
                sizeBytes,
                contentId,
                transferMethod));
    }

    /// <summary>Drains queued terminal lines matching current context, including broadcast lines with empty node/user targets.</summary>
    public Godot.Collections.Array<string> DrainTerminalEventLines(string nodeId, string userId)
    {
        var drainedLines = new Godot.Collections.Array<string>();
        lock (terminalEventLinesSync)
        {
            if (terminalEventLines.Count == 0)
            {
                return drainedLines;
            }

            var normalizedNodeId = nodeId?.Trim() ?? string.Empty;
            var normalizedUserId = userId?.Trim() ?? string.Empty;
            var retained = new Queue<TerminalEventLine>();
            while (terminalEventLines.Count > 0)
            {
                var line = terminalEventLines.Dequeue();
                var lineNodeId = line.NodeId?.Trim() ?? string.Empty;
                var lineUserKey = line.UserKey?.Trim() ?? string.Empty;
                var matchNode = string.IsNullOrEmpty(normalizedNodeId) ||
                                string.IsNullOrEmpty(lineNodeId) ||
                                string.Equals(lineNodeId, normalizedNodeId, StringComparison.Ordinal);
                var lineUserId = ResolveUserIdForTerminalEventLine(line);
                var matchUser = string.IsNullOrEmpty(normalizedUserId) ||
                                string.IsNullOrEmpty(lineUserKey) ||
                                string.Equals(lineUserId, normalizedUserId, StringComparison.Ordinal);
                if (matchNode && matchUser)
                {
                    drainedLines.Add(line.Text);
                    continue;
                }

                retained.Enqueue(line);
            }

            while (retained.Count > 0)
            {
                terminalEventLines.Enqueue(retained.Dequeue());
            }
        }

        return drainedLines;
    }

    private string ResolveUserIdForTerminalEventLine(TerminalEventLine line)
    {
        if (TryGetServer(line.NodeId, out var server) &&
            server.Users.TryGetValue(line.UserKey, out var user) &&
            !string.IsNullOrWhiteSpace(user.UserId))
        {
            return user.UserId;
        }

        return line.UserKey ?? string.Empty;
    }

    internal void QueueTerminalEventLine(TerminalEventLine line)
    {
        lock (terminalEventLinesSync)
        {
            terminalEventLines.Enqueue(line);
        }
    }

    internal void InitializeEventRuntime(ScenarioBlueprint scenario)
    {
        InitializeEventRuntime(new[] { scenario });
    }

    internal void InitializeEventRuntime(IEnumerable<ScenarioBlueprint> scenarios)
    {
        EnsureEventRuntimeServices();
        eventQueue.Clear();
        firedHandlerIds.Clear();
        lock (terminalEventLinesSync)
        {
            terminalEventLines.Clear();
        }
        ScenarioFlags.Clear();
        eventIndex.Clear();

        foreach (var scenario in scenarios)
        {
            foreach (var eventPair in scenario.Events.OrderBy(static pair => pair.Key, StringComparer.Ordinal))
            {
                var descriptor = BuildEventHandlerDescriptor(scenario, eventPair.Key, eventPair.Value);
                eventIndex.Add(descriptor);
            }
        }

        processScheduler.RebuildFrom(ProcessList);
        scheduledProcessEndAtById.Clear();
        foreach (var processPair in ProcessList)
        {
            if (processPair.Value.State == ProcessState.Running)
            {
                scheduledProcessEndAtById[processPair.Key] = processPair.Value.EndAt;
            }
        }
    }

    internal void ResetEventRuntimeState()
    {
        worldTickIndex = 0;
        eventSeq = 0;
        eventQueue.Clear();
        firedHandlerIds.Clear();
        lock (terminalEventLinesSync)
        {
            terminalEventLines.Clear();
        }
        ScenarioFlags.Clear();
        eventIndex.Clear();
        processScheduler.Clear();
        scheduledProcessEndAtById.Clear();
        initiallyExposedNodesByNet.Clear();
    }

    private void WorldTick()
    {
        EnsureEventRuntimeServices();
        worldTickIndex++;
        var nowMs = GetCurrentWorldTimeMs();
        ProcessDueProcesses(nowMs);
        eventDispatcher?.Drain(eventQueue, ScenarioFlags);
    }

    private void ProcessDueProcesses(long nowMs)
    {
        SyncProcessSchedulerWithProcessList();
        var dueProcessIds = processScheduler.PopDue(nowMs, ProcessList);
        foreach (var processId in dueProcessIds)
        {
            if (!ProcessList.TryGetValue(processId, out var process) || process.State != ProcessState.Running)
            {
                continue;
            }

            process.State = ProcessState.Finished;
            scheduledProcessEndAtById.Remove(processId);
            if (ServerList.TryGetValue(process.HostNodeId, out var hostServer))
            {
                hostServer.RemoveProcess(processId);
            }

            var effectApplied = true;
            string? effectSkipReason = null;
            if (hostServer is not null &&
                hostServer.Status == ServerStatus.Offline &&
                (hostServer.Reason == ServerReason.Disabled || hostServer.Reason == ServerReason.Crashed))
            {
                effectApplied = false;
                effectSkipReason = "server status reason blocks process-complete side effects.";
            }

            EnqueueGameEvent(
                EventRuntimeConstants.ProcessFinishedEventType,
                new ProcessFinishedDto(
                    processId,
                    process.HostNodeId,
                    process.UserKey,
                    process.Name,
                    process.Path,
                    process.ProcessType.ToString(),
                    new Dictionary<string, object>(process.ProcessArgs, StringComparer.Ordinal),
                    process.EndAt,
                    nowMs,
                    effectApplied,
                    effectSkipReason),
                nowMs);
        }
    }

    private void SyncProcessSchedulerWithProcessList()
    {
        var removedProcessIds = scheduledProcessEndAtById.Keys
            .Where(processId => !ProcessList.TryGetValue(processId, out var process) || process.State != ProcessState.Running)
            .ToArray();
        foreach (var removedProcessId in removedProcessIds)
        {
            scheduledProcessEndAtById.Remove(removedProcessId);
        }

        foreach (var processPair in ProcessList)
        {
            if (processPair.Value.State != ProcessState.Running)
            {
                continue;
            }

            if (scheduledProcessEndAtById.TryGetValue(processPair.Key, out var scheduledEndAt) &&
                scheduledEndAt == processPair.Value.EndAt)
            {
                continue;
            }

            processScheduler.ScheduleOrUpdate(processPair.Key, processPair.Value.EndAt);
            scheduledProcessEndAtById[processPair.Key] = processPair.Value.EndAt;
        }
    }

    private EventHandlerDescriptor BuildEventHandlerDescriptor(
        ScenarioBlueprint scenario,
        string eventId,
        EventBlueprint eventBlueprint)
    {
        var normalizedNodeId = NormalizeConditionValue(
            eventBlueprint.ConditionArgs.TryGetValue("nodeId", out var nodeId) ? nodeId : null);
        var normalizedUserKey = NormalizeConditionValue(
            eventBlueprint.ConditionArgs.TryGetValue("userKey", out var userKey) ? userKey : null);
        var normalizedPrivilege = NormalizeConditionValue(
            eventBlueprint.ConditionArgs.TryGetValue("privilege", out var privilege) ? privilege : null);
        var normalizedFileName = NormalizeConditionValue(
            eventBlueprint.ConditionArgs.TryGetValue("fileName", out var fileName) ? fileName : null,
            normalizeAsFileName: true);

        CompiledGuard? guard = null;
        if (!string.IsNullOrWhiteSpace(eventBlueprint.GuardContent))
        {
            var resolvedGuard = ResolveGuardContent(scenario, eventId, eventBlueprint.GuardContent);
            guard = guardEvaluator!.Compile(
                scenario.ScenarioId,
                eventId,
                resolvedGuard.SourceKind,
                resolvedGuard.SourceId,
                resolvedGuard.SourceBody);
        }

        return new EventHandlerDescriptor(
            scenario.ScenarioId,
            eventId,
            eventBlueprint.ConditionType,
            normalizedNodeId,
            normalizedUserKey,
            normalizedPrivilege,
            normalizedFileName,
            guard,
            eventBlueprint.Actions.ToArray());
    }

    private (GuardSourceKind SourceKind, string SourceId, string SourceBody) ResolveGuardContent(
        ScenarioBlueprint scenario,
        string eventId,
        string guardContent)
    {
        if (guardContent.StartsWith("script-", StringComparison.Ordinal))
        {
            var body = guardContent["script-".Length..];
            if (body.StartsWith("\r\n", StringComparison.Ordinal))
            {
                body = body[2..];
            }
            else if (body.StartsWith("\n", StringComparison.Ordinal) ||
                     body.StartsWith("\r", StringComparison.Ordinal))
            {
                body = body[1..];
            }

            return (GuardSourceKind.Inline, eventId, body);
        }

        if (guardContent.StartsWith("id-", StringComparison.Ordinal))
        {
            var scriptId = guardContent["id-".Length..].Trim();
            if (!scenario.Scripts.TryGetValue(scriptId, out var sourceBody))
            {
                throw new InvalidDataException(
                    $"guardContent references missing script id. scenarioId='{scenario.ScenarioId}', eventId='{eventId}', scriptId='{scriptId}'.");
            }

            return (GuardSourceKind.ScriptId, scriptId, sourceBody);
        }

        if (guardContent.StartsWith("path-", StringComparison.Ordinal))
        {
            var relativePath = guardContent["path-".Length..].Trim();
            if (Path.IsPathRooted(relativePath))
            {
                throw new InvalidDataException(
                    $"guardContent path must be project-root relative. scenarioId='{scenario.ScenarioId}', eventId='{eventId}', path='{relativePath}'.");
            }

            var resourcePath = NormalizeProjectRelativeResourcePath(relativePath);
            if (!Godot.FileAccess.FileExists(resourcePath))
            {
                throw new FileNotFoundException(
                    $"guardContent path file was not found. scenarioId='{scenario.ScenarioId}', eventId='{eventId}', path='{relativePath}'.",
                    resourcePath);
            }

            var sourceBody = ReadAllTextFromPath(resourcePath);
            return (GuardSourceKind.Path, relativePath, sourceBody);
        }

        throw new InvalidDataException(
            $"guardContent has unsupported prefix. scenarioId='{scenario.ScenarioId}', eventId='{eventId}', guardContent='{guardContent}'.");
    }

    private static string NormalizeConditionValue(object? rawValue, bool normalizeAsFileName = false)
    {
        if (rawValue is not string stringValue || string.IsNullOrWhiteSpace(stringValue))
        {
            return EventRuntimeConstants.Any;
        }

        var value = stringValue.Trim();
        if (normalizeAsFileName)
        {
            value = Path.GetFileName(value);
        }

        return string.IsNullOrWhiteSpace(value) ? EventRuntimeConstants.Any : value;
    }

    private void ApplySystemEventHooks(GameEvent gameEvent)
    {
        if (!string.Equals(gameEvent.EventType, EventRuntimeConstants.PrivilegeAcquireEventType, StringComparison.Ordinal) ||
            gameEvent.Payload is not PrivilegeAcquireDto privilegePayload ||
            !string.Equals(privilegePayload.Privilege, "execute", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var newlyVisibleNets = new HashSet<string>(StringComparer.Ordinal);
        if (privilegePayload.UnlockedNetIds is not null && privilegePayload.UnlockedNetIds.Count > 0)
        {
            foreach (var unlockedNetId in privilegePayload.UnlockedNetIds)
            {
                if (string.IsNullOrWhiteSpace(unlockedNetId))
                {
                    continue;
                }

                var normalizedNetId = unlockedNetId.Trim();
                if (!VisibleNets.Contains(normalizedNetId))
                {
                    newlyVisibleNets.Add(normalizedNetId);
                }
            }
        }
        else if (ServerList.TryGetValue(privilegePayload.NodeId, out var sourceServer))
        {
            foreach (var netId in sourceServer.SubnetMembership)
            {
                if (!VisibleNets.Contains(netId))
                {
                    newlyVisibleNets.Add(netId);
                }
            }
        }

        foreach (var netId in newlyVisibleNets)
        {
            VisibleNets.Add(netId);
            if (!KnownNodesByNet.TryGetValue(netId, out var knownNodes))
            {
                knownNodes = new HashSet<string>(StringComparer.Ordinal);
                KnownNodesByNet[netId] = knownNodes;
            }

            if (initiallyExposedNodesByNet.TryGetValue(netId, out var seededNodes))
            {
                knownNodes.UnionWith(seededNodes);
            }
        }

        RefreshServerExposureFromKnownNodes();
    }

    private void RefreshServerExposureFromKnownNodes()
    {
        foreach (var server in ServerList.Values)
        {
            foreach (var netId in server.SubnetMembership)
            {
                var isKnown = KnownNodesByNet.TryGetValue(netId, out var knownNodes) && knownNodes.Contains(server.NodeId);
                server.SetExposure(netId, isKnown);
            }
        }
    }

    private void EnsureEventRuntimeServices()
    {
        guardEvaluator ??= new GuardEvaluator(WarnRuntimeIssue);
        actionExecutor ??= new ActionExecutor(this, WarnRuntimeIssue);
        eventDispatcher ??= new EventDispatcher(eventIndex, firedHandlerIds, guardEvaluator, actionExecutor, ApplySystemEventHooks);
    }

    private void EnqueueGameEvent(string eventType, object payload, long? explicitTimeMs = null)
    {
        var gameEvent = new GameEvent(
            eventType,
            explicitTimeMs ?? GetCurrentWorldTimeMs(),
            ++eventSeq,
            payload);
        eventQueue.Enqueue(gameEvent);
    }

    private long GetCurrentWorldTimeMs()
    {
        var ticksPerSecond = physicsTicksPerSecond > 0 ? physicsTicksPerSecond : 60;
        return (worldTickIndex * 1000L) / ticksPerSecond;
    }

    internal (long WorldTickIndex, long EventSeq) CaptureEventRuntimeClockForSave()
    {
        return (worldTickIndex, eventSeq);
    }

    internal IReadOnlyList<string> CaptureFiredHandlerIdsForSave()
    {
        return firedHandlerIds.OrderBy(static value => value, StringComparer.Ordinal).ToArray();
    }

    internal void ApplyEventRuntimeStateForLoad(
        long loadedWorldTickIndex,
        long loadedEventSeq,
        IEnumerable<string> loadedFiredHandlerIds)
    {
        worldTickIndex = Math.Max(0, loadedWorldTickIndex);
        eventSeq = Math.Max(0, loadedEventSeq);
        eventQueue.Clear();
        firedHandlerIds.Clear();

        foreach (var firedHandlerId in loadedFiredHandlerIds)
        {
            if (string.IsNullOrWhiteSpace(firedHandlerId))
            {
                continue;
            }

            firedHandlerIds.Add(firedHandlerId.Trim());
        }

        lock (terminalEventLinesSync)
        {
            terminalEventLines.Clear();
        }
    }

    internal void RebuildProcessSchedulerForLoad()
    {
        processScheduler.RebuildFrom(ProcessList);
        scheduledProcessEndAtById.Clear();
        foreach (var processPair in ProcessList)
        {
            if (processPair.Value.State == ProcessState.Running)
            {
                scheduledProcessEndAtById[processPair.Key] = processPair.Value.EndAt;
            }
        }
    }

    private static bool TryGrantPrivilege(UserConfig user, string privilege, out bool granted)
    {
        granted = false;
        if (string.Equals(privilege, "read", StringComparison.Ordinal))
        {
            if (!user.Privilege.Read)
            {
                user.Privilege.Read = true;
                granted = true;
            }

            return true;
        }

        if (string.Equals(privilege, "write", StringComparison.Ordinal))
        {
            if (!user.Privilege.Write)
            {
                user.Privilege.Write = true;
                granted = true;
            }

            return true;
        }

        if (string.Equals(privilege, "execute", StringComparison.Ordinal))
        {
            if (!user.Privilege.Execute)
            {
                user.Privilege.Execute = true;
                granted = true;
            }

            return true;
        }

        return false;
    }

    private static void WarnRuntimeIssue(string message)
    {
        GD.PushWarning(message);
    }

    internal void CaptureInitiallyExposedNode(string netId, string nodeId)
    {
        if (string.IsNullOrWhiteSpace(netId) || string.IsNullOrWhiteSpace(nodeId))
        {
            return;
        }

        if (!initiallyExposedNodesByNet.TryGetValue(netId, out var nodeIds))
        {
            nodeIds = new HashSet<string>(StringComparer.Ordinal);
            initiallyExposedNodesByNet[netId] = nodeIds;
        }

        nodeIds.Add(nodeId);
    }
}
