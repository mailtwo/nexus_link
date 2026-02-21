using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Miniscript;
using Uplink2.Runtime.MiniScript;
using Uplink2.Vfs;

namespace Uplink2.Runtime.Syscalls;

internal abstract class VfsCommandHandlerBase : ISystemCallHandler
{
    private const string ExecutableReadErrorPrefix = "error: cannot read executable file: ";

    public abstract string Command { get; }

    public abstract SystemCallResult Execute(SystemCallExecutionContext context, IReadOnlyList<string> arguments);

    protected static bool RequireRead(SystemCallExecutionContext context, string command, out SystemCallResult result)
    {
        if (context.User.Privilege.Read)
        {
            result = SystemCallResultFactory.Success();
            return true;
        }

        result = SystemCallResultFactory.PermissionDenied(command);
        return false;
    }

    protected static bool RequireWrite(SystemCallExecutionContext context, string command, out SystemCallResult result)
    {
        if (context.User.Privilege.Write)
        {
            result = SystemCallResultFactory.Success();
            return true;
        }

        result = SystemCallResultFactory.PermissionDenied(command);
        return false;
    }

    protected static bool RequireExecute(SystemCallExecutionContext context, string command, out SystemCallResult result)
    {
        if (context.User.Privilege.Execute)
        {
            result = SystemCallResultFactory.Success();
            return true;
        }

        result = SystemCallResultFactory.PermissionDenied(command);
        return false;
    }

    protected static string NormalizePath(SystemCallExecutionContext context, string inputPath)
    {
        return BaseFileSystem.NormalizePath(context.Cwd, inputPath);
    }

    protected static string GetParentPath(string normalizedPath)
    {
        if (normalizedPath == "/")
        {
            return "/";
        }

        var index = normalizedPath.LastIndexOf('/');
        if (index <= 0)
        {
            return "/";
        }

        return normalizedPath[..index];
    }

    protected static bool IsEditorReadableFile(VfsEntryMeta entry)
    {
        return !entry.IsBinaryLikeExecutable();
    }

    protected static SystemCallResult ExecutableReadDenied(string path)
    {
        return SystemCallResultFactory.Failure(SystemCallErrorCode.InvalidArgs, ExecutableReadErrorPrefix + path);
    }
}

internal sealed class PwdCommandHandler : VfsCommandHandlerBase
{
    public override string Command => "pwd";

    public override SystemCallResult Execute(SystemCallExecutionContext context, IReadOnlyList<string> arguments)
    {
        if (arguments.Count != 0)
        {
            return SystemCallResultFactory.Usage("pwd");
        }

        return SystemCallResultFactory.Success(lines: new[] { context.Cwd });
    }
}

internal sealed class LsCommandHandler : VfsCommandHandlerBase
{
    public override string Command => "ls";

    public override SystemCallResult Execute(SystemCallExecutionContext context, IReadOnlyList<string> arguments)
    {
        if (arguments.Count > 1)
        {
            return SystemCallResultFactory.Usage("ls [path]");
        }

        if (!RequireRead(context, Command, out var permissionResult))
        {
            return permissionResult;
        }

        var targetPath = arguments.Count == 0
            ? context.Cwd
            : NormalizePath(context, arguments[0]);
        if (!context.Server.DiskOverlay.TryResolveEntry(targetPath, out var targetEntry))
        {
            return SystemCallResultFactory.NotFound(targetPath);
        }

        if (targetEntry.EntryKind != VfsEntryKind.Dir)
        {
            return SystemCallResultFactory.NotDirectory(targetPath);
        }

        var children = context.Server.DiskOverlay.ListChildren(targetPath);
        var displayEntries = new List<string>(children.Count);
        foreach (var childName in children)
        {
            var childPath = targetPath == "/" ? "/" + childName : targetPath + "/" + childName;
            if (context.Server.DiskOverlay.TryResolveEntry(childPath, out var childEntry) &&
                childEntry.EntryKind == VfsEntryKind.Dir)
            {
                displayEntries.Add(childName + "/");
            }
            else
            {
                displayEntries.Add(childName);
            }
        }

        return SystemCallResultFactory.Success(lines: FormatLsOutput(displayEntries));
    }

    private static IReadOnlyList<string> FormatLsOutput(IReadOnlyList<string> entries)
    {
        if (entries.Count == 0)
        {
            return Array.Empty<string>();
        }

        var maxWidth = entries.Max(static entry => entry.Length);
        var columnWidth = Math.Max(2, maxWidth + 2);
        const int terminalWidth = 96;
        var columns = Math.Max(1, terminalWidth / columnWidth);

        if (columns == 1)
        {
            return new List<string>(entries);
        }

        var lines = new List<string>((entries.Count + columns - 1) / columns);
        for (var offset = 0; offset < entries.Count; offset += columns)
        {
            var count = Math.Min(columns, entries.Count - offset);
            var builder = new StringBuilder(columnWidth * count);
            for (var index = 0; index < count; index++)
            {
                var entry = entries[offset + index];
                if (index < count - 1)
                {
                    builder.Append(entry.PadRight(columnWidth));
                }
                else
                {
                    builder.Append(entry);
                }
            }

            lines.Add(builder.ToString().TrimEnd());
        }

        return lines;
    }
}

internal sealed class CdCommandHandler : VfsCommandHandlerBase
{
    public override string Command => "cd";

    public override SystemCallResult Execute(SystemCallExecutionContext context, IReadOnlyList<string> arguments)
    {
        if (arguments.Count != 1)
        {
            return SystemCallResultFactory.Usage("cd <path>");
        }

        if (!RequireRead(context, Command, out var permissionResult))
        {
            return permissionResult;
        }

        var targetPath = NormalizePath(context, arguments[0]);
        if (!context.Server.DiskOverlay.TryResolveEntry(targetPath, out var targetEntry))
        {
            return SystemCallResultFactory.NotFound(targetPath);
        }

        if (targetEntry.EntryKind != VfsEntryKind.Dir)
        {
            return SystemCallResultFactory.NotDirectory(targetPath);
        }

        return SystemCallResultFactory.Success(nextCwd: targetPath);
    }
}

internal sealed class CatCommandHandler : VfsCommandHandlerBase
{
    public override string Command => "cat";

    public override SystemCallResult Execute(SystemCallExecutionContext context, IReadOnlyList<string> arguments)
    {
        if (arguments.Count != 1)
        {
            return SystemCallResultFactory.Usage("cat <file>");
        }

        if (!RequireRead(context, Command, out var permissionResult))
        {
            return permissionResult;
        }

        var targetPath = NormalizePath(context, arguments[0]);
        if (!context.Server.DiskOverlay.TryResolveEntry(targetPath, out var entry))
        {
            return SystemCallResultFactory.NotFound(targetPath);
        }

        if (entry.EntryKind != VfsEntryKind.File)
        {
            return SystemCallResultFactory.NotFile(targetPath);
        }

        if (!IsEditorReadableFile(entry))
        {
            return ExecutableReadDenied(targetPath);
        }

        if (!context.Server.DiskOverlay.TryReadFileText(targetPath, out var content))
        {
            return SystemCallResultFactory.NotFile(targetPath);
        }

        if (string.IsNullOrEmpty(content))
        {
            return SystemCallResultFactory.Success();
        }

        var normalizedContent = content
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n');
        var outputLines = normalizedContent.Split('\n');
        return SystemCallResultFactory.Success(lines: outputLines);
    }
}

internal sealed class EditCommandHandler : VfsCommandHandlerBase
{
    public override string Command => "edit";

    public override SystemCallResult Execute(SystemCallExecutionContext context, IReadOnlyList<string> arguments)
    {
        if (arguments.Count != 1)
        {
            return SystemCallResultFactory.Usage("edit <file>");
        }

        if (!RequireRead(context, Command, out var permissionResult))
        {
            return permissionResult;
        }

        var targetPath = NormalizePath(context, arguments[0]);
        if (!context.Server.DiskOverlay.TryResolveEntry(targetPath, out var entry))
        {
            return SystemCallResultFactory.NotFound(targetPath);
        }

        if (entry.EntryKind != VfsEntryKind.File)
        {
            return SystemCallResultFactory.NotFile(targetPath);
        }

        if (!IsEditorReadableFile(entry))
        {
            return ExecutableReadDenied(targetPath);
        }

        if (!context.Server.DiskOverlay.TryReadFileText(targetPath, out var content))
        {
            return SystemCallResultFactory.NotFile(targetPath);
        }

        return SystemCallResultFactory.Success(
            data: new EditorOpenTransition
            {
                TargetPath = targetPath,
                Content = content,
            });
    }
}

internal sealed class MkdirCommandHandler : VfsCommandHandlerBase
{
    public override string Command => "mkdir";

    public override SystemCallResult Execute(SystemCallExecutionContext context, IReadOnlyList<string> arguments)
    {
        if (arguments.Count != 1)
        {
            return SystemCallResultFactory.Usage("mkdir <dir>");
        }

        if (!RequireWrite(context, Command, out var permissionResult))
        {
            return permissionResult;
        }

        var targetPath = NormalizePath(context, arguments[0]);
        if (targetPath == "/")
        {
            return SystemCallResultFactory.Success();
        }

        if (context.Server.DiskOverlay.TryResolveEntry(targetPath, out var existingEntry))
        {
            return existingEntry.EntryKind == VfsEntryKind.Dir
                ? SystemCallResultFactory.Success()
                : SystemCallResultFactory.Conflict(targetPath);
        }

        var parentPath = GetParentPath(targetPath);
        if (!context.Server.DiskOverlay.TryResolveEntry(parentPath, out var parentEntry))
        {
            return SystemCallResultFactory.NotFound(parentPath);
        }

        if (parentEntry.EntryKind != VfsEntryKind.Dir)
        {
            return SystemCallResultFactory.NotDirectory(parentPath);
        }

        context.Server.DiskOverlay.AddDirectory(targetPath);
        return SystemCallResultFactory.Success();
    }
}

internal sealed class DebugMiniScriptCommandHandler : VfsCommandHandlerBase
{
    public override string Command => "DEBUG_miniscript";

    public override SystemCallResult Execute(SystemCallExecutionContext context, IReadOnlyList<string> arguments)
    {
        if (arguments.Count != 1)
        {
            return SystemCallResultFactory.Usage("DEBUG_miniscript <script>");
        }

        if (!RequireRead(context, Command, out var readPermissionResult))
        {
            return readPermissionResult;
        }

        if (!RequireExecute(context, Command, out var executePermissionResult))
        {
            return executePermissionResult;
        }

        var scriptPath = NormalizePath(context, arguments[0]);
        if (!context.Server.DiskOverlay.TryResolveEntry(scriptPath, out var scriptEntry))
        {
            return SystemCallResultFactory.NotFound(scriptPath);
        }

        if (scriptEntry.EntryKind != VfsEntryKind.File)
        {
            return SystemCallResultFactory.NotFile(scriptPath);
        }

        if (scriptEntry.FileKind != VfsFileKind.Text && scriptEntry.FileKind != VfsFileKind.ExecutableScript)
        {
            return SystemCallResultFactory.Failure(
                SystemCallErrorCode.InvalidArgs,
                "DEBUG_miniscript source must be text or executable script: " + scriptPath);
        }

        if (!context.Server.DiskOverlay.TryReadFileText(scriptPath, out var scriptSource))
        {
            return SystemCallResultFactory.NotFile(scriptPath);
        }

        return MiniScriptExecutionRunner.ExecuteScript(scriptSource, context);
    }
}

internal static class MiniScriptExecutionRunner
{
    private const double TimeSliceSeconds = 0.01;
    private const double MaxRuntimeSeconds = 2.0;

    internal static SystemCallResult ExecuteScript(
        string scriptSource,
        SystemCallExecutionContext executionContext = null)
    {
        var standardOutput = new ScriptOutputCollector();
        var errorOutput = new ScriptOutputCollector();

        try
        {
            var interpreter = new Interpreter(scriptSource, standardOutput.Append, errorOutput.Append)
            {
                implicitOutput = standardOutput.Append,
            };

            MiniScriptCryptoIntrinsics.InjectCryptoModule(interpreter);
            MiniScriptSshIntrinsics.InjectSshModule(interpreter, executionContext);
            var deadlineUtc = DateTime.UtcNow.AddSeconds(MaxRuntimeSeconds);
            while (!interpreter.done)
            {
                interpreter.RunUntilDone(TimeSliceSeconds, returnEarly: false);
                if (DateTime.UtcNow >= deadlineUtc)
                {
                    return SystemCallResultFactory.Failure(
                        SystemCallErrorCode.InternalError,
                        "miniscript execution timed out.");
                }
            }
        }
        catch (Exception ex)
        {
            return SystemCallResultFactory.Failure(
                SystemCallErrorCode.InternalError,
                "miniscript execution failed: " + ex.Message);
        }

        var stdoutLines = standardOutput.ToLines();
        var stderrLines = errorOutput.ToLines();
        if (stderrLines.Count == 0)
        {
            return SystemCallResultFactory.Success(lines: stdoutLines);
        }

        var lines = new List<string>(stdoutLines.Count + stderrLines.Count);
        lines.AddRange(stdoutLines);
        foreach (var line in stderrLines)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                lines.Add("error: miniscript execution failed.");
                continue;
            }

            lines.Add(line.StartsWith("error:", StringComparison.OrdinalIgnoreCase) ? line : "error: " + line);
        }

        return new SystemCallResult
        {
            Ok = false,
            Code = SystemCallErrorCode.InternalError,
            Lines = lines,
        };
    }

    private sealed class ScriptOutputCollector
    {
        private readonly List<string> lines = new();
        private readonly StringBuilder pending = new();

        internal void Append(string text, bool addLineBreak)
        {
            if (!string.IsNullOrEmpty(text))
            {
                pending.Append(text);
            }

            if (addLineBreak)
            {
                lines.Add(pending.ToString());
                pending.Clear();
            }
        }

        internal IReadOnlyList<string> ToLines()
        {
            if (pending.Length > 0)
            {
                lines.Add(pending.ToString());
                pending.Clear();
            }

            return lines;
        }
    }
}

internal sealed class RmCommandHandler : VfsCommandHandlerBase
{
    public override string Command => "rm";

    public override SystemCallResult Execute(SystemCallExecutionContext context, IReadOnlyList<string> arguments)
    {
        if (arguments.Count != 1)
        {
            return SystemCallResultFactory.Usage("rm <file>");
        }

        if (!RequireWrite(context, Command, out var permissionResult))
        {
            return permissionResult;
        }

        var targetPath = NormalizePath(context, arguments[0]);
        if (targetPath == "/")
        {
            return SystemCallResultFactory.Failure(SystemCallErrorCode.InvalidArgs, "rm cannot remove root directory.");
        }

        if (!context.Server.DiskOverlay.TryResolveEntry(targetPath, out var entry))
        {
            return SystemCallResultFactory.NotFound(targetPath);
        }

        if (entry.EntryKind != VfsEntryKind.File)
        {
            return SystemCallResultFactory.NotFile(targetPath);
        }

        context.Server.DiskOverlay.AddTombstone(targetPath);
        return SystemCallResultFactory.Success();
    }
}
