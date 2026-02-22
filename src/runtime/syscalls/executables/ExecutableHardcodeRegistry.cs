using System;
using System.Collections.Generic;

#nullable enable

namespace Uplink2.Runtime.Syscalls;

/// <summary>Registry mapping executable id tokens to ExecutableHardcode handlers.</summary>
internal sealed class ExecutableHardcodeRegistry
{
    private readonly Dictionary<string, IExecutableHardcodeHandler> handlers = new(StringComparer.Ordinal);

    /// <summary>Registers one handler for its executable id.</summary>
    internal void Register(IExecutableHardcodeHandler handler)
    {
        if (handler is null)
        {
            throw new ArgumentNullException(nameof(handler));
        }

        if (string.IsNullOrWhiteSpace(handler.ExecutableId))
        {
            throw new ArgumentException("Executable id cannot be empty.", nameof(handler));
        }

        if (!handlers.TryAdd(handler.ExecutableId.Trim(), handler))
        {
            throw new InvalidOperationException(
                $"Duplicate hardcoded executable registration: '{handler.ExecutableId}'.");
        }
    }

    /// <summary>Finds a handler by executable id token.</summary>
    internal bool TryGetHandler(string executableId, out IExecutableHardcodeHandler handler)
    {
        return handlers.TryGetValue(executableId, out handler!);
    }
}
