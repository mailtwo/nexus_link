using System;
using System.Collections.Generic;
using Uplink2.Vfs;

namespace Uplink2.Runtime.Syscalls;

internal abstract class VfsCommandHandlerBase : ISystemCallHandler
{
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
        var outputLines = new List<string>(children.Count);
        foreach (var childName in children)
        {
            var childPath = targetPath == "/" ? "/" + childName : targetPath + "/" + childName;
            if (context.Server.DiskOverlay.TryResolveEntry(childPath, out var childEntry) &&
                childEntry.EntryKind == VfsEntryKind.Dir)
            {
                outputLines.Add(childName + "/");
            }
            else
            {
                outputLines.Add(childName);
            }
        }

        return SystemCallResultFactory.Success(lines: outputLines);
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
