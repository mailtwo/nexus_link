using System.Collections.Generic;

#nullable enable

namespace Uplink2.Runtime.Syscalls;

/// <summary>Invocation payload for one ExecutableHardcode dispatch.</summary>
internal readonly record struct ExecutableHardcodeInvocation(
    SystemCallExecutionContext Context,
    string Command,
    string ResolvedProgramPath,
    string RawContentId,
    string ExecutableId,
    IReadOnlyList<string> Arguments);

/// <summary>ExecutableHardcode command handler contract keyed by executable id.</summary>
internal interface IExecutableHardcodeHandler
{
    /// <summary>Executable id token handled by this instance (without prefix).</summary>
    string ExecutableId { get; }

    /// <summary>Executes one hardcoded program invocation.</summary>
    SystemCallResult Execute(ExecutableHardcodeInvocation invocation);
}
