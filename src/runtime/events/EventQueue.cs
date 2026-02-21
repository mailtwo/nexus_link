using System.Collections.Generic;

#nullable enable

namespace Uplink2.Runtime.Events;

internal sealed class EventQueue
{
    private readonly Queue<GameEvent> queue = new();
    private GameEvent? deferredFrontEvent;

    internal int Count => queue.Count + (deferredFrontEvent is null ? 0 : 1);

    internal void Enqueue(GameEvent gameEvent)
    {
        queue.Enqueue(gameEvent);
    }

    internal bool TryDequeue(out GameEvent gameEvent)
    {
        if (deferredFrontEvent is not null)
        {
            gameEvent = deferredFrontEvent;
            deferredFrontEvent = null;
            return true;
        }

        if (queue.Count > 0)
        {
            gameEvent = queue.Dequeue();
            return true;
        }

        gameEvent = null!;
        return false;
    }

    internal void DeferFront(GameEvent gameEvent)
    {
        if (deferredFrontEvent is not null)
        {
            throw new System.InvalidOperationException("EventQueue already contains a deferred front event.");
        }

        deferredFrontEvent = gameEvent;
    }

    internal void Clear()
    {
        queue.Clear();
        deferredFrontEvent = null;
    }
}
