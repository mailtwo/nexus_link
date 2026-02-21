using Miniscript;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using Uplink2.Runtime.MiniScript;

#nullable enable

namespace Uplink2.Runtime.Events;

internal sealed class GuardEvaluator
{
    private const double GuardRunTimeSliceSeconds = 0.005;
    private const string EventArgVariableName = "__uplink_evt";
    private const string StateArgVariableName = "__uplink_state";
    private const string GuardResultVariableName = "__uplink_guard_result";

    private readonly Action<string> warningSink;

    internal GuardEvaluator(Action<string> warningSink)
    {
        this.warningSink = warningSink ?? throw new ArgumentNullException(nameof(warningSink));
    }

    internal CompiledGuard Compile(
        string scenarioId,
        string eventId,
        GuardSourceKind sourceKind,
        string sourceId,
        string sourceBody)
    {
        var wrappedSource = WrapGuardBody(sourceBody);
        var errorLines = new List<string>();
        var interpreter = new Interpreter(
            wrappedSource,
            static (_, _) => { },
            (text, _) =>
            {
                if (!string.IsNullOrWhiteSpace(text))
                {
                    errorLines.Add(text.Trim());
                }
            });
        MiniScriptCryptoIntrinsics.InjectCryptoModule(interpreter);
        interpreter.Compile();

        if (interpreter.vm is null || errorLines.Count > 0)
        {
            var details = errorLines.Count == 0 ? "unknown compile error" : string.Join(" | ", errorLines);
            throw new InvalidDataException(
                $"Guard compile failed. scenarioId='{scenarioId}', eventId='{eventId}', source='{sourceKind}:{sourceId}', details='{details}'.");
        }

        return new CompiledGuard(sourceKind, sourceId, sourceBody, wrappedSource);
    }

    internal GuardEvaluationResult Evaluate(
        EventHandlerDescriptor descriptor,
        GameEvent gameEvent,
        IReadOnlyDictionary<string, object> scenarioFlags,
        GuardBudgetTracker budget)
    {
        if (descriptor.Guard is null)
        {
            return new GuardEvaluationResult(true, false);
        }

        if (budget.IsExhausted)
        {
            return new GuardEvaluationResult(false, true);
        }

        var errorLines = new List<string>();
        var interpreter = new Interpreter(
            descriptor.Guard.WrappedSource,
            static (_, _) => { },
            (text, _) =>
            {
                if (!string.IsNullOrWhiteSpace(text))
                {
                    errorLines.Add(text.Trim());
                }
            });
        MiniScriptCryptoIntrinsics.InjectCryptoModule(interpreter);

        interpreter.Compile();
        if (interpreter.vm is null || errorLines.Count > 0)
        {
            WarnGuardIssue(descriptor, "compile", errorLines);
            return new GuardEvaluationResult(false, false);
        }

        var eventValue = ConvertToMiniScriptValue(BuildEventPayloadMap(gameEvent));
        var stateMap = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["flags"] = new Dictionary<string, object>(scenarioFlags, StringComparer.Ordinal),
        };
        var stateValue = ConvertToMiniScriptValue(stateMap);

        interpreter.SetGlobalValue(EventArgVariableName, eventValue);
        interpreter.SetGlobalValue(StateArgVariableName, stateValue);

        var stopwatch = Stopwatch.StartNew();
        while (!interpreter.done)
        {
            interpreter.RunUntilDone(GuardRunTimeSliceSeconds, returnEarly: false);
            if (stopwatch.Elapsed.TotalSeconds > EventRuntimeConstants.GuardPerCallBudgetSeconds)
            {
                budget.Consume(stopwatch.Elapsed.TotalSeconds);
                WarnGuardIssue(descriptor, "timeout", Array.Empty<string>());
                return new GuardEvaluationResult(false, false);
            }
        }

        budget.Consume(stopwatch.Elapsed.TotalSeconds);
        if (errorLines.Count > 0)
        {
            WarnGuardIssue(descriptor, "runtime", errorLines);
            return new GuardEvaluationResult(false, false);
        }

        var guardResultValue = interpreter.GetGlobalValue(GuardResultVariableName);
        var guardPassed = guardResultValue is not null && guardResultValue.BoolValue();
        return new GuardEvaluationResult(guardPassed, false);
    }

    private static Dictionary<string, object?> BuildEventPayloadMap(GameEvent gameEvent)
    {
        if (gameEvent.Payload is PrivilegeAcquireDto privilege)
        {
            return new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["nodeId"] = privilege.NodeId,
                ["userKey"] = privilege.UserKey,
                ["privilege"] = privilege.Privilege,
                ["acquiredAtMs"] = privilege.AcquiredAtMs,
                ["via"] = privilege.Via,
                ["unlockedNetIds"] = privilege.UnlockedNetIds,
            };
        }

        if (gameEvent.Payload is FileAcquireDto file)
        {
            return new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["fromNodeId"] = file.FromNodeId,
                ["userKey"] = file.UserKey,
                ["fileName"] = file.FileName,
                ["acquiredAtMs"] = file.AcquiredAtMs,
                ["remotePath"] = file.RemotePath,
                ["localPath"] = file.LocalPath,
                ["sizeBytes"] = file.SizeBytes,
                ["contentId"] = file.ContentId,
                ["transferMethod"] = file.TransferMethod,
            };
        }

        if (gameEvent.Payload is ProcessFinishedDto process)
        {
            return new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["processId"] = process.ProcessId,
                ["hostNodeId"] = process.HostNodeId,
                ["userKey"] = process.UserKey,
                ["name"] = process.Name,
                ["path"] = process.Path,
                ["processType"] = process.ProcessType,
                ["processArgs"] = process.ProcessArgs,
                ["scheduledEndAtMs"] = process.ScheduledEndAtMs,
                ["finishedAtMs"] = process.FinishedAtMs,
                ["effectApplied"] = process.EffectApplied,
                ["effectSkipReason"] = process.EffectSkipReason,
            };
        }

        return new Dictionary<string, object?>(StringComparer.Ordinal);
    }

    private static Value ConvertToMiniScriptValue(object? value)
    {
        if (value is null)
        {
            return null!;
        }

        if (value is Value miniScriptValue)
        {
            return miniScriptValue;
        }

        if (value is string text)
        {
            return new ValString(text);
        }

        if (value is bool boolValue)
        {
            return ValNumber.Truth(boolValue);
        }

        if (value is int intValue)
        {
            return new ValNumber(intValue);
        }

        if (value is long longValue)
        {
            return new ValNumber(longValue);
        }

        if (value is float floatValue)
        {
            return new ValNumber(floatValue);
        }

        if (value is double doubleValue)
        {
            return new ValNumber(doubleValue);
        }

        if (value is decimal decimalValue)
        {
            return new ValNumber(Convert.ToDouble(decimalValue, CultureInfo.InvariantCulture));
        }

        if (value is IDictionary<string, object> typedObjectDictionary)
        {
            var copiedDictionary = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (var pair in typedObjectDictionary)
            {
                copiedDictionary[pair.Key] = pair.Value;
            }

            return ConvertDictionaryToMiniScript(copiedDictionary);
        }

        if (value is IDictionary<string, object?> nullableObjectDictionary)
        {
            return ConvertDictionaryToMiniScript(nullableObjectDictionary);
        }

        if (value is IDictionary rawDictionary)
        {
            var copy = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (DictionaryEntry pair in rawDictionary)
            {
                var key = Convert.ToString(pair.Key, CultureInfo.InvariantCulture) ?? string.Empty;
                copy[key] = pair.Value;
            }

            return ConvertDictionaryToMiniScript(copy);
        }

        if (value is IEnumerable<object?> objectEnumerable)
        {
            return ConvertEnumerableToMiniScript(objectEnumerable);
        }

        if (value is IEnumerable rawEnumerable)
        {
            var values = new List<object?>();
            foreach (var item in rawEnumerable)
            {
                values.Add(item);
            }

            return ConvertEnumerableToMiniScript(values);
        }

        return new ValString(Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty);
    }

    private static Value ConvertDictionaryToMiniScript(IEnumerable<KeyValuePair<string, object?>> dictionary)
    {
        var map = new ValMap();
        foreach (var pair in dictionary)
        {
            map[pair.Key] = ConvertToMiniScriptValue(pair.Value);
        }

        return map;
    }

    private static Value ConvertEnumerableToMiniScript(IEnumerable<object?> enumerable)
    {
        var values = new List<Value>();
        foreach (var item in enumerable)
        {
            values.Add(ConvertToMiniScriptValue(item));
        }

        return new ValList(values);
    }

    private static string WrapGuardBody(string sourceBody)
    {
        var normalizedBody = (sourceBody ?? string.Empty)
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n');

        return
            $"guard = function(evt, state)\n{normalizedBody}\nend function\n{GuardResultVariableName} = guard({EventArgVariableName}, {StateArgVariableName})";
    }

    private void WarnGuardIssue(EventHandlerDescriptor descriptor, string stage, IReadOnlyList<string> errorLines)
    {
        var details = errorLines.Count == 0 ? string.Empty : string.Join(" | ", errorLines);
        warningSink(
            $"Guard {stage} failure. scenarioId='{descriptor.ScenarioId}', eventId='{descriptor.EventId}', conditionType='{descriptor.ConditionType}', source='{descriptor.Guard?.SourceKind}:{descriptor.Guard?.SourceId}', details='{details}'.");
    }
}
