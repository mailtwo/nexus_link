using System;
using System.Collections.Generic;
using Uplink2.Blueprint;

#nullable enable

namespace Uplink2.Runtime.Events;

internal static class EventRuntimeConstants
{
    internal const string ProcessFinishedEventType = "processFinished";
    internal const string PrivilegeAcquireEventType = "privilegeAcquire";
    internal const string FileAcquireEventType = "fileAcquire";
    internal const string Any = "__ANY__";
    internal const double GuardPerCallBudgetSeconds = 0.0166;
    internal const double GuardTickBudgetSeconds = 0.05;
}

internal sealed record GameEvent(
    string EventType,
    long TimeMs,
    long Seq,
    object Payload);

internal sealed record ProcessFinishedDto(
    int ProcessId,
    string HostNodeId,
    string UserKey,
    string Name,
    string Path,
    string ProcessType,
    Dictionary<string, object> ProcessArgs,
    long ScheduledEndAtMs,
    long FinishedAtMs,
    bool EffectApplied,
    string? EffectSkipReason);

internal sealed record PrivilegeAcquireDto(
    string NodeId,
    string UserKey,
    string Privilege,
    long AcquiredAtMs,
    string? Via,
    List<string>? UnlockedNetIds);

internal sealed record FileAcquireDto(
    string FromNodeId,
    string UserKey,
    string FileName,
    long AcquiredAtMs,
    string? RemotePath,
    string? LocalPath,
    int? SizeBytes,
    string? ContentId,
    string? TransferMethod);

internal enum GuardSourceKind
{
    Inline,
    ScriptId,
    Path,
}

internal sealed class CompiledGuard
{
    internal CompiledGuard(GuardSourceKind sourceKind, string sourceId, string sourceBody, string wrappedSource)
    {
        SourceKind = sourceKind;
        SourceId = sourceId;
        SourceBody = sourceBody;
        WrappedSource = wrappedSource;
    }

    internal GuardSourceKind SourceKind { get; }

    internal string SourceId { get; }

    internal string SourceBody { get; }

    internal string WrappedSource { get; }
}

internal readonly record struct GuardEvaluationResult(bool Passed, bool BudgetExceeded);

internal sealed class EventHandlerDescriptor
{
    internal EventHandlerDescriptor(
        string scenarioId,
        string eventId,
        BlueprintConditionType conditionType,
        string nodeIdKey,
        string userKey,
        string privilegeKey,
        string fileNameKey,
        CompiledGuard? guard,
        IReadOnlyList<ActionBlueprint> actions)
    {
        ScenarioId = scenarioId;
        EventId = eventId;
        ConditionType = conditionType;
        NodeIdKey = nodeIdKey;
        UserKey = userKey;
        PrivilegeKey = privilegeKey;
        FileNameKey = fileNameKey;
        Guard = guard;
        Actions = actions;
    }

    internal string ScenarioId { get; }

    internal string EventId { get; }

    internal BlueprintConditionType ConditionType { get; }

    internal string NodeIdKey { get; }

    internal string UserKey { get; }

    internal string PrivilegeKey { get; }

    internal string FileNameKey { get; }

    internal CompiledGuard? Guard { get; }

    internal IReadOnlyList<ActionBlueprint> Actions { get; }
}

internal sealed class TerminalEventLine
{
    internal TerminalEventLine(string nodeId, string userKey, string text)
    {
        NodeId = nodeId;
        UserKey = userKey;
        Text = text;
    }

    internal string NodeId { get; }

    internal string UserKey { get; }

    internal string Text { get; }
}

internal sealed class GuardBudgetTracker
{
    private double remainingSeconds;

    internal GuardBudgetTracker(double budgetSeconds)
    {
        remainingSeconds = Math.Max(0.0, budgetSeconds);
    }

    internal bool IsExhausted => remainingSeconds <= 0.0;

    internal void Consume(double elapsedSeconds)
    {
        remainingSeconds -= Math.Max(0.0, elapsedSeconds);
    }
}
