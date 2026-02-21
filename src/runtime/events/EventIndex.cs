using System;
using System.Collections.Generic;
using System.IO;
using Uplink2.Blueprint;

#nullable enable

namespace Uplink2.Runtime.Events;

internal sealed class EventIndex
{
    private readonly Dictionary<string, Dictionary<string, Dictionary<string, List<EventHandlerDescriptor>>>> privilegeIndex =
        new(StringComparer.Ordinal);

    private readonly Dictionary<string, Dictionary<string, List<EventHandlerDescriptor>>> fileIndex =
        new(StringComparer.Ordinal);

    internal void Clear()
    {
        privilegeIndex.Clear();
        fileIndex.Clear();
    }

    internal void Add(EventHandlerDescriptor descriptor)
    {
        if (descriptor.ConditionType == BlueprintConditionType.PrivilegeAcquire)
        {
            AddPrivilegeDescriptor(descriptor);
            return;
        }

        if (descriptor.ConditionType == BlueprintConditionType.FileAcquire)
        {
            AddFileDescriptor(descriptor);
        }
    }

    internal IReadOnlyList<EventHandlerDescriptor> Query(GameEvent gameEvent)
    {
        if (string.Equals(gameEvent.EventType, EventRuntimeConstants.PrivilegeAcquireEventType, StringComparison.Ordinal) &&
            gameEvent.Payload is PrivilegeAcquireDto privilegePayload)
        {
            return QueryPrivilege(privilegePayload);
        }

        if (string.Equals(gameEvent.EventType, EventRuntimeConstants.FileAcquireEventType, StringComparison.Ordinal) &&
            gameEvent.Payload is FileAcquireDto filePayload)
        {
            return QueryFile(filePayload);
        }

        return Array.Empty<EventHandlerDescriptor>();
    }

    private void AddPrivilegeDescriptor(EventHandlerDescriptor descriptor)
    {
        if (!privilegeIndex.TryGetValue(descriptor.PrivilegeKey, out var nodeMap))
        {
            nodeMap = new Dictionary<string, Dictionary<string, List<EventHandlerDescriptor>>>(StringComparer.Ordinal);
            privilegeIndex[descriptor.PrivilegeKey] = nodeMap;
        }

        if (!nodeMap.TryGetValue(descriptor.NodeIdKey, out var userMap))
        {
            userMap = new Dictionary<string, List<EventHandlerDescriptor>>(StringComparer.Ordinal);
            nodeMap[descriptor.NodeIdKey] = userMap;
        }

        if (!userMap.TryGetValue(descriptor.UserKey, out var handlers))
        {
            handlers = new List<EventHandlerDescriptor>();
            userMap[descriptor.UserKey] = handlers;
        }

        handlers.Add(descriptor);
    }

    private void AddFileDescriptor(EventHandlerDescriptor descriptor)
    {
        if (!fileIndex.TryGetValue(descriptor.FileNameKey, out var nodeMap))
        {
            nodeMap = new Dictionary<string, List<EventHandlerDescriptor>>(StringComparer.Ordinal);
            fileIndex[descriptor.FileNameKey] = nodeMap;
        }

        if (!nodeMap.TryGetValue(descriptor.NodeIdKey, out var handlers))
        {
            handlers = new List<EventHandlerDescriptor>();
            nodeMap[descriptor.NodeIdKey] = handlers;
        }

        handlers.Add(descriptor);
    }

    private IReadOnlyList<EventHandlerDescriptor> QueryPrivilege(PrivilegeAcquireDto payload)
    {
        var results = new List<EventHandlerDescriptor>();
        var seenEventIds = new HashSet<string>(StringComparer.Ordinal);

        foreach (var privilegeKey in ExpandActualAndAny(NormalizeKey(payload.Privilege)))
        {
            if (!privilegeIndex.TryGetValue(privilegeKey, out var nodeMap))
            {
                continue;
            }

            foreach (var nodeIdKey in ExpandActualAndAny(NormalizeKey(payload.NodeId)))
            {
                if (!nodeMap.TryGetValue(nodeIdKey, out var userMap))
                {
                    continue;
                }

                foreach (var userKey in ExpandActualAndAny(NormalizeKey(payload.UserKey)))
                {
                    if (!userMap.TryGetValue(userKey, out var handlers))
                    {
                        continue;
                    }

                    AppendUnique(results, seenEventIds, handlers);
                }
            }
        }

        return results;
    }

    private IReadOnlyList<EventHandlerDescriptor> QueryFile(FileAcquireDto payload)
    {
        var results = new List<EventHandlerDescriptor>();
        var seenEventIds = new HashSet<string>(StringComparer.Ordinal);
        var fileName = NormalizeKey(Path.GetFileName(payload.FileName ?? string.Empty));

        foreach (var fileNameKey in ExpandActualAndAny(fileName))
        {
            if (!fileIndex.TryGetValue(fileNameKey, out var nodeMap))
            {
                continue;
            }

            foreach (var nodeIdKey in ExpandActualAndAny(NormalizeKey(payload.FromNodeId)))
            {
                if (!nodeMap.TryGetValue(nodeIdKey, out var handlers))
                {
                    continue;
                }

                AppendUnique(results, seenEventIds, handlers);
            }
        }

        return results;
    }

    private static void AppendUnique(
        List<EventHandlerDescriptor> destination,
        HashSet<string> seenEventIds,
        IEnumerable<EventHandlerDescriptor> source)
    {
        foreach (var descriptor in source)
        {
            if (!seenEventIds.Add(descriptor.EventId))
            {
                continue;
            }

            destination.Add(descriptor);
        }
    }

    private static IEnumerable<string> ExpandActualAndAny(string key)
    {
        yield return key;
        if (!string.Equals(key, EventRuntimeConstants.Any, StringComparison.Ordinal))
        {
            yield return EventRuntimeConstants.Any;
        }
    }

    private static string NormalizeKey(string? key)
    {
        return string.IsNullOrWhiteSpace(key) ? EventRuntimeConstants.Any : key.Trim();
    }

}
