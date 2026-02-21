using System;
using System.Collections.Generic;

#nullable enable

namespace Uplink2.Runtime.Events;

internal sealed class EventDispatcher
{
    private readonly EventIndex eventIndex;
    private readonly HashSet<string> firedHandlerIds;
    private readonly GuardEvaluator guardEvaluator;
    private readonly ActionExecutor actionExecutor;
    private readonly Action<GameEvent> preDispatchHook;

    internal EventDispatcher(
        EventIndex eventIndex,
        HashSet<string> firedHandlerIds,
        GuardEvaluator guardEvaluator,
        ActionExecutor actionExecutor,
        Action<GameEvent> preDispatchHook)
    {
        this.eventIndex = eventIndex ?? throw new ArgumentNullException(nameof(eventIndex));
        this.firedHandlerIds = firedHandlerIds ?? throw new ArgumentNullException(nameof(firedHandlerIds));
        this.guardEvaluator = guardEvaluator ?? throw new ArgumentNullException(nameof(guardEvaluator));
        this.actionExecutor = actionExecutor ?? throw new ArgumentNullException(nameof(actionExecutor));
        this.preDispatchHook = preDispatchHook ?? throw new ArgumentNullException(nameof(preDispatchHook));
    }

    internal void Drain(EventQueue eventQueue, IReadOnlyDictionary<string, object> scenarioFlags)
    {
        var tickBudget = new GuardBudgetTracker(EventRuntimeConstants.GuardTickBudgetSeconds);
        while (eventQueue.TryDequeue(out var gameEvent))
        {
            if (tickBudget.IsExhausted)
            {
                eventQueue.DeferFront(gameEvent);
                return;
            }

            preDispatchHook(gameEvent);
            if (!ShouldDispatchScenarioHandlers(gameEvent.EventType))
            {
                continue;
            }

            var candidates = eventIndex.Query(gameEvent);
            foreach (var descriptor in candidates)
            {
                if (firedHandlerIds.Contains(descriptor.EventId))
                {
                    continue;
                }

                if (tickBudget.IsExhausted)
                {
                    eventQueue.DeferFront(gameEvent);
                    return;
                }

                var guardResult = guardEvaluator.Evaluate(descriptor, gameEvent, scenarioFlags, tickBudget);
                if (guardResult.BudgetExceeded)
                {
                    eventQueue.DeferFront(gameEvent);
                    return;
                }

                if (!guardResult.Passed)
                {
                    continue;
                }

                actionExecutor.ExecuteAll(descriptor, gameEvent);
                firedHandlerIds.Add(descriptor.EventId);
            }
        }
    }

    private static bool ShouldDispatchScenarioHandlers(string eventType)
    {
        return string.Equals(eventType, EventRuntimeConstants.PrivilegeAcquireEventType, StringComparison.Ordinal) ||
               string.Equals(eventType, EventRuntimeConstants.FileAcquireEventType, StringComparison.Ordinal);
    }
}
