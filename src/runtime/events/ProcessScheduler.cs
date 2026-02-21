using System.Collections.Generic;

#nullable enable

namespace Uplink2.Runtime.Events;

internal sealed class ProcessScheduler
{
    private readonly PriorityQueue<ProcessHeapItem, long> minHeap = new();
    private readonly Dictionary<int, long> revisions = new();

    internal void Clear()
    {
        minHeap.Clear();
        revisions.Clear();
    }

    internal void ScheduleOrUpdate(int processId, long endAtMs)
    {
        var nextRevision = revisions.GetValueOrDefault(processId) + 1;
        revisions[processId] = nextRevision;
        minHeap.Enqueue(new ProcessHeapItem(processId, endAtMs, nextRevision), endAtMs);
    }

    internal void RebuildFrom(IReadOnlyDictionary<int, ProcessStruct> processList)
    {
        Clear();
        foreach (var processPair in processList)
        {
            if (processPair.Value.State != ProcessState.Running)
            {
                continue;
            }

            ScheduleOrUpdate(processPair.Key, processPair.Value.EndAt);
        }
    }

    internal IReadOnlyList<int> PopDue(long nowMs, IReadOnlyDictionary<int, ProcessStruct> processList)
    {
        var dueProcessIds = new List<int>();
        while (minHeap.Count > 0)
        {
            minHeap.TryPeek(out var heapItem, out var priority);
            if (priority > nowMs)
            {
                break;
            }

            minHeap.Dequeue();
            if (heapItem is null)
            {
                continue;
            }

            if (!revisions.TryGetValue(heapItem.ProcessId, out var latestRevision) ||
                latestRevision != heapItem.Revision)
            {
                continue;
            }

            if (!processList.TryGetValue(heapItem.ProcessId, out var process) ||
                process.State != ProcessState.Running ||
                process.EndAt != heapItem.ScheduledEndAtMs)
            {
                revisions.Remove(heapItem.ProcessId);
                continue;
            }

            if (process.EndAt > nowMs)
            {
                continue;
            }

            revisions.Remove(heapItem.ProcessId);
            dueProcessIds.Add(heapItem.ProcessId);
        }

        return dueProcessIds;
    }

    private sealed class ProcessHeapItem
    {
        internal ProcessHeapItem(int processId, long scheduledEndAtMs, long revision)
        {
            ProcessId = processId;
            ScheduledEndAtMs = scheduledEndAtMs;
            Revision = revision;
        }

        internal int ProcessId { get; }

        internal long ScheduledEndAtMs { get; }

        internal long Revision { get; }
    }
}
