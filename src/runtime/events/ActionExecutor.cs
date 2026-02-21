using System;
using Uplink2.Blueprint;

#nullable enable

namespace Uplink2.Runtime.Events;

internal sealed class ActionExecutor
{
    private readonly WorldRuntime world;
    private readonly Action<string> warningSink;

    internal ActionExecutor(WorldRuntime world, Action<string> warningSink)
    {
        this.world = world ?? throw new ArgumentNullException(nameof(world));
        this.warningSink = warningSink ?? throw new ArgumentNullException(nameof(warningSink));
    }

    internal void ExecuteAll(EventHandlerDescriptor descriptor, GameEvent gameEvent)
    {
        foreach (var action in descriptor.Actions)
        {
            try
            {
                ExecuteSingle(action, descriptor, gameEvent);
            }
            catch (Exception ex)
            {
                warningSink(
                    $"Action execution failed. scenarioId='{descriptor.ScenarioId}', eventId='{descriptor.EventId}', actionType='{action.ActionType}', details='{ex.Message}'.");
            }
        }
    }

    private void ExecuteSingle(ActionBlueprint action, EventHandlerDescriptor descriptor, GameEvent gameEvent)
    {
        if (action.ActionType == BlueprintActionType.Print)
        {
            ExecutePrintAction(action, descriptor, gameEvent);
            return;
        }

        if (action.ActionType == BlueprintActionType.SetFlag)
        {
            ExecuteSetFlagAction(action, descriptor);
            return;
        }

        warningSink(
            $"Unsupported actionType '{action.ActionType}'. scenarioId='{descriptor.ScenarioId}', eventId='{descriptor.EventId}'.");
    }

    private void ExecutePrintAction(ActionBlueprint action, EventHandlerDescriptor descriptor, GameEvent gameEvent)
    {
        if (!action.ActionArgs.TryGetValue("text", out var textValue) || textValue is not string text)
        {
            warningSink(
                $"print action requires string actionArgs.text. scenarioId='{descriptor.ScenarioId}', eventId='{descriptor.EventId}'.");
            return;
        }

        ResolveEventTarget(gameEvent, out var nodeId, out var userKey);
        world.QueueTerminalEventLine(new TerminalEventLine(nodeId, userKey, text));
    }

    private void ExecuteSetFlagAction(ActionBlueprint action, EventHandlerDescriptor descriptor)
    {
        if (!action.ActionArgs.TryGetValue("key", out var keyValue) || keyValue is not string key || string.IsNullOrWhiteSpace(key))
        {
            warningSink(
                $"setFlag action requires non-empty string actionArgs.key. scenarioId='{descriptor.ScenarioId}', eventId='{descriptor.EventId}'.");
            return;
        }

        if (!action.ActionArgs.TryGetValue("value", out var value))
        {
            warningSink(
                $"setFlag action requires actionArgs.value. scenarioId='{descriptor.ScenarioId}', eventId='{descriptor.EventId}'.");
            return;
        }

        world.ScenarioFlags[key] = value;
    }

    private void ResolveEventTarget(GameEvent gameEvent, out string nodeId, out string userKey)
    {
        nodeId = world.PlayerWorkstationServer?.NodeId ?? string.Empty;
        userKey = "player";

        if (gameEvent.Payload is PrivilegeAcquireDto privilege)
        {
            nodeId = privilege.NodeId;
            userKey = privilege.UserKey;
            return;
        }

        if (gameEvent.Payload is FileAcquireDto file)
        {
            nodeId = file.FromNodeId;
            userKey = file.UserKey;
            return;
        }

        if (gameEvent.Payload is ProcessFinishedDto process)
        {
            nodeId = process.HostNodeId;
            userKey = process.UserKey;
        }
    }
}
