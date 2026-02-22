using System;
using System.Collections.Generic;
using System.Linq;

namespace Uplink2.Runtime.Syscalls;

internal sealed class SystemCallRegistry
{
    private readonly Dictionary<string, ISystemCallHandler> handlers = new(StringComparer.OrdinalIgnoreCase);

    internal void Register(ISystemCallHandler handler)
    {
        if (handler is null)
        {
            throw new ArgumentNullException(nameof(handler));
        }

        if (string.IsNullOrWhiteSpace(handler.Command))
        {
            throw new ArgumentException("Handler command cannot be empty.", nameof(handler));
        }

        if (!handlers.TryAdd(handler.Command.Trim(), handler))
        {
            throw new InvalidOperationException($"Duplicate system-call command registration: '{handler.Command}'.");
        }
    }

    internal bool TryGetHandler(string command, out ISystemCallHandler handler)
    {
        return handlers.TryGetValue(command, out handler!);
    }

    internal IReadOnlyList<string> ListCommands()
    {
        return handlers.Keys
            .OrderBy(static command => command, StringComparer.Ordinal)
            .ToArray();
    }
}
