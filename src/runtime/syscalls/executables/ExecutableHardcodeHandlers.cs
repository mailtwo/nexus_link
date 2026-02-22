using Uplink2.Vfs;

#nullable enable

namespace Uplink2.Runtime.Syscalls;

/// <summary>No-op hardcoded executable handler.</summary>
internal sealed class NoopExecutableHardcodeHandler : IExecutableHardcodeHandler
{
    public string ExecutableId => "noop";

    public SystemCallResult Execute(ExecutableHardcodeInvocation invocation)
    {
        return SystemCallResultFactory.Success();
    }
}

/// <summary>MiniScript hardcoded executable handler (`miniscript &lt;script&gt;`).</summary>
internal sealed class MiniScriptExecutableHardcodeHandler : IExecutableHardcodeHandler
{
    public string ExecutableId => "miniscript";

    public SystemCallResult Execute(ExecutableHardcodeInvocation invocation)
    {
        var context = invocation.Context;
        if (invocation.Arguments.Count != 1)
        {
            return SystemCallResultFactory.Usage("miniscript <script>");
        }

        var scriptPath = BaseFileSystem.NormalizePath(context.Cwd, invocation.Arguments[0]);
        if (!context.Server.DiskOverlay.TryResolveEntry(scriptPath, out var scriptEntry))
        {
            return SystemCallResultFactory.NotFound(scriptPath);
        }

        if (scriptEntry.EntryKind != VfsEntryKind.File)
        {
            return SystemCallResultFactory.NotFile(scriptPath);
        }

        if (scriptEntry.FileKind != VfsFileKind.Text)
        {
            return SystemCallResultFactory.Failure(
                SystemCallErrorCode.InvalidArgs,
                "miniscript source must be text: " + scriptPath);
        }

        if (!context.Server.DiskOverlay.TryReadFileText(scriptPath, out var scriptSource))
        {
            return SystemCallResultFactory.NotFile(scriptPath);
        }

        return MiniScriptExecutionRunner.ExecuteScript(scriptSource, context);
    }
}
