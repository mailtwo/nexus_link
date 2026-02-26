using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
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

internal sealed class HelpCommandHandler : VfsCommandHandlerBase
{
    private const string HelpPageResourcePath = "res://scenario_content/resources/text/help_page.txt";

    public override string Command => "help";

    public override SystemCallResult Execute(SystemCallExecutionContext context, IReadOnlyList<string> arguments)
    {
        _ = context;
        if (arguments.Count != 0)
        {
            return SystemCallResultFactory.Usage("help");
        }

        string absolutePath;
        try
        {
            absolutePath = Godot.ProjectSettings.GlobalizePath(HelpPageResourcePath);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or NotSupportedException)
        {
            return SystemCallResultFactory.Failure(SystemCallErrorCode.InternalError, ex.Message);
        }

        if (!File.Exists(absolutePath))
        {
            return SystemCallResultFactory.NotFound(HelpPageResourcePath);
        }

        string content;
        try
        {
            content = File.ReadAllText(absolutePath, Encoding.UTF8);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return SystemCallResultFactory.Failure(SystemCallErrorCode.InternalError, ex.Message);
        }

        if (string.IsNullOrEmpty(content))
        {
            return SystemCallResultFactory.Success();
        }

        var normalizedContent = content
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n');
        return SystemCallResultFactory.Success(lines: normalizedContent.Split('\n'));
    }
}

internal sealed class EchoCommandHandler : VfsCommandHandlerBase
{
    public override string Command => "echo";

    public override SystemCallResult Execute(SystemCallExecutionContext context, IReadOnlyList<string> arguments)
    {
        _ = context;
        if (arguments.Count == 0)
        {
            return SystemCallResultFactory.Success(lines: new[] { string.Empty });
        }

        return SystemCallResultFactory.Success(lines: new[] { string.Join(' ', arguments) });
    }
}

internal sealed class ClearCommandHandler : VfsCommandHandlerBase
{
    private const string UsageText = "clear";

    public override string Command => "clear";

    public override SystemCallResult Execute(SystemCallExecutionContext context, IReadOnlyList<string> arguments)
    {
        _ = context;
        if (arguments.Count != 0)
        {
            return SystemCallResultFactory.Usage(UsageText);
        }

        return SystemCallResultFactory.Success(
            data: new TerminalContextTransition
            {
                ClearTerminalBeforeOutput = true,
                NextCwd = string.Empty,
            });
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
    private const int HexLineWidth = 100;
    private const int MaxHexChars = 20000;
    private const string TextDisplayMode = "text";
    private const string HexDisplayMode = "hex";

    public override string Command => "edit";

    public override SystemCallResult Execute(SystemCallExecutionContext context, IReadOnlyList<string> arguments)
    {
        if (arguments.Count != 1)
        {
            return SystemCallResultFactory.Usage("edit <path>");
        }

        var targetPath = NormalizePath(context, arguments[0]);
        if (!context.Server.DiskOverlay.TryResolveEntry(targetPath, out var entry))
        {
            if (!RequireWrite(context, Command, out var writePermissionResult))
            {
                return writePermissionResult;
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

            return SystemCallResultFactory.Success(
                data: new EditorOpenTransition
                {
                    TargetPath = targetPath,
                    Content = string.Empty,
                    ReadOnly = false,
                    DisplayMode = TextDisplayMode,
                    PathExists = false,
                });
        }

        if (!RequireRead(context, Command, out var readPermissionResult))
        {
            return readPermissionResult;
        }

        if (entry.EntryKind != VfsEntryKind.File)
        {
            return SystemCallResultFactory.NotFile(targetPath);
        }

        if (entry.FileKind != VfsFileKind.Text)
        {
            var worldSeed = context.World is null ? 0 : context.World.WorldSeed;
            return SystemCallResultFactory.Success(
                data: new EditorOpenTransition
                {
                    TargetPath = targetPath,
                    Content = BuildPseudoHexView(worldSeed, entry),
                    ReadOnly = true,
                    DisplayMode = HexDisplayMode,
                    PathExists = true,
                });
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
                ReadOnly = !context.User.Privilege.Write,
                DisplayMode = TextDisplayMode,
                PathExists = true,
            });
    }

    private static string BuildPseudoHexView(int worldSeed, VfsEntryMeta entry)
    {
        var proportionalChars = entry.Size >= long.MaxValue / 2 ? long.MaxValue : entry.Size * 2L;
        var boundedChars = Math.Min(proportionalChars, MaxHexChars);
        if (boundedChars <= 0)
        {
            return string.Empty;
        }

        var targetCharCount = (int)(((boundedChars + HexLineWidth - 1) / HexLineWidth) * HexLineWidth);
        if (targetCharCount > MaxHexChars)
        {
            targetCharCount = MaxHexChars;
        }

        var hexChars = new StringBuilder(targetCharCount);
        var seedText = $"{worldSeed}|{entry.ContentId}|{entry.Size}";
        var seedBytes = SHA256.HashData(Encoding.UTF8.GetBytes(seedText));
        var hashInput = new byte[seedBytes.Length + sizeof(int)];
        Buffer.BlockCopy(seedBytes, 0, hashInput, 0, seedBytes.Length);
        var counter = 0;

        while (hexChars.Length < targetCharCount)
        {
            var counterBytes = BitConverter.GetBytes(counter++);
            if (!BitConverter.IsLittleEndian)
            {
                Array.Reverse(counterBytes);
            }

            Buffer.BlockCopy(counterBytes, 0, hashInput, seedBytes.Length, sizeof(int));
            var chunk = SHA256.HashData(hashInput);
            AppendLowerHex(chunk, hexChars, targetCharCount);
        }

        var lineCount = targetCharCount / HexLineWidth;
        var lines = new string[lineCount];
        for (var index = 0; index < lineCount; index++)
        {
            var offset = index * HexLineWidth;
            lines[index] = hexChars.ToString(offset, HexLineWidth);
        }

        return string.Join('\n', lines);
    }

    private static void AppendLowerHex(byte[] bytes, StringBuilder builder, int maxChars)
    {
        const string alphabet = "0123456789abcdef";
        foreach (var value in bytes)
        {
            if (builder.Length >= maxChars)
            {
                break;
            }

            builder.Append(alphabet[(value >> 4) & 0x0F]);
            if (builder.Length >= maxChars)
            {
                break;
            }

            builder.Append(alphabet[value & 0x0F]);
        }
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

internal sealed class RmdirCommandHandler : VfsCommandHandlerBase
{
    private const string UsageText = "rmdir <dir>";

    public override string Command => "rmdir";

    public override SystemCallResult Execute(SystemCallExecutionContext context, IReadOnlyList<string> arguments)
    {
        if (arguments.Count != 1 ||
            string.IsNullOrWhiteSpace(arguments[0]) ||
            arguments[0].StartsWith("-", StringComparison.Ordinal))
        {
            return SystemCallResultFactory.Usage(UsageText);
        }

        if (!RequireWrite(context, Command, out var permissionResult))
        {
            return permissionResult;
        }

        var targetPath = NormalizePath(context, arguments[0]);
        if (targetPath == "/")
        {
            return SystemCallResultFactory.Failure(SystemCallErrorCode.InvalidArgs, "rmdir cannot remove root directory.");
        }

        if (!context.Server.DiskOverlay.TryResolveEntry(targetPath, out var entry))
        {
            return SystemCallResultFactory.NotFound(targetPath);
        }

        if (entry.EntryKind != VfsEntryKind.Dir)
        {
            return SystemCallResultFactory.NotDirectory(targetPath);
        }

        if (string.Equals(targetPath, context.Cwd, StringComparison.Ordinal) ||
            context.Cwd.StartsWith(targetPath + "/", StringComparison.Ordinal))
        {
            return SystemCallResultFactory.Failure(
                SystemCallErrorCode.InvalidArgs,
                "rmdir cannot remove current working directory or its ancestor.");
        }

        if (context.Server.DiskOverlay.ListChildren(targetPath).Count != 0)
        {
            return SystemCallResultFactory.NotEmpty(targetPath);
        }

        try
        {
            context.Server.DiskOverlay.AddTombstone(targetPath);
        }
        catch (InvalidOperationException ex)
        {
            return SystemCallResultFactory.Failure(SystemCallErrorCode.InvalidArgs, ex.Message);
        }

        return SystemCallResultFactory.Success();
    }
}

internal sealed class CpCommandHandler : VfsCommandHandlerBase
{
    private const string UsageText = "cp [(-r|-R|--recursive)] <src> <dst>";

    public override string Command => "cp";

    public override SystemCallResult Execute(SystemCallExecutionContext context, IReadOnlyList<string> arguments)
    {
        if (!RequireRead(context, Command, out var readPermissionResult))
        {
            return readPermissionResult;
        }

        if (!RequireWrite(context, Command, out var writePermissionResult))
        {
            return writePermissionResult;
        }

        if (!CopyMoveCommandHelper.TryParseArguments(arguments, UsageText, out var parsed, out var parseFailure))
        {
            return parseFailure;
        }

        if (!CopyMoveCommandHelper.TryResolveCopyMoveTarget(
                context,
                parsed.SourcePathInput,
                parsed.DestinationPathInput,
                out var sourcePath,
                out var sourceEntry,
                out var destinationPath,
                out var resolveFailure))
        {
            return resolveFailure;
        }

        if (string.Equals(sourcePath, destinationPath, StringComparison.Ordinal))
        {
            return SystemCallResultFactory.Failure(SystemCallErrorCode.InvalidArgs, "cp source and destination are identical.");
        }

        if (CopyMoveCommandHelper.IsDescendantPath(destinationPath, sourcePath))
        {
            return SystemCallResultFactory.Failure(SystemCallErrorCode.InvalidArgs, "cp destination cannot be inside source.");
        }

        if (sourceEntry.EntryKind == VfsEntryKind.Dir && !parsed.Recursive)
        {
            return SystemCallResultFactory.Usage(UsageText);
        }

        return CopyMoveCommandHelper.TryCopyEntry(
                context.Server.DiskOverlay,
                sourcePath,
                sourceEntry,
                destinationPath,
                out var copyFailure)
            ? SystemCallResultFactory.Success()
            : copyFailure;
    }
}

internal sealed class MvCommandHandler : VfsCommandHandlerBase
{
    private const string UsageText = "mv [(-r|-R|--recursive)] <src> <dst>";

    public override string Command => "mv";

    public override SystemCallResult Execute(SystemCallExecutionContext context, IReadOnlyList<string> arguments)
    {
        if (!RequireRead(context, Command, out var readPermissionResult))
        {
            return readPermissionResult;
        }

        if (!RequireWrite(context, Command, out var writePermissionResult))
        {
            return writePermissionResult;
        }

        if (!CopyMoveCommandHelper.TryParseArguments(arguments, UsageText, out var parsed, out var parseFailure))
        {
            return parseFailure;
        }

        if (!CopyMoveCommandHelper.TryResolveCopyMoveTarget(
                context,
                parsed.SourcePathInput,
                parsed.DestinationPathInput,
                out var sourcePath,
                out var sourceEntry,
                out var destinationPath,
                out var resolveFailure))
        {
            return resolveFailure;
        }

        if (string.Equals(sourcePath, destinationPath, StringComparison.Ordinal))
        {
            return SystemCallResultFactory.Success();
        }

        if (CopyMoveCommandHelper.IsDescendantPath(destinationPath, sourcePath))
        {
            return SystemCallResultFactory.Failure(SystemCallErrorCode.InvalidArgs, "mv destination cannot be inside source.");
        }

        if (!CopyMoveCommandHelper.TryCopyEntry(
                context.Server.DiskOverlay,
                sourcePath,
                sourceEntry,
                destinationPath,
                out var copyFailure))
        {
            return copyFailure;
        }

        return CopyMoveCommandHelper.TryTombstoneSubtree(context.Server.DiskOverlay, sourcePath, out var deleteFailure)
            ? SystemCallResultFactory.Success()
            : deleteFailure;
    }
}

internal readonly record struct ParsedCopyMoveArguments(bool Recursive, string SourcePathInput, string DestinationPathInput);

internal static class CopyMoveCommandHelper
{
    private static readonly HashSet<string> RecursiveOptionTokens = new(StringComparer.Ordinal)
    {
        "-r",
        "-R",
        "--recursive",
    };

    internal static bool TryParseArguments(
        IReadOnlyList<string> arguments,
        string usageText,
        out ParsedCopyMoveArguments parsed,
        out SystemCallResult failure)
    {
        parsed = default;
        failure = SystemCallResultFactory.Success();

        if (arguments.Count < 2)
        {
            failure = SystemCallResultFactory.Usage(usageText);
            return false;
        }

        var recursive = false;
        var positional = new List<string>(capacity: 2);
        foreach (var token in arguments)
        {
            if (RecursiveOptionTokens.Contains(token))
            {
                recursive = true;
                continue;
            }

            if (token.StartsWith("-", StringComparison.Ordinal))
            {
                failure = SystemCallResultFactory.Usage(usageText);
                return false;
            }

            positional.Add(token);
        }

        if (positional.Count != 2)
        {
            failure = SystemCallResultFactory.Usage(usageText);
            return false;
        }

        parsed = new ParsedCopyMoveArguments(recursive, positional[0], positional[1]);
        return true;
    }

    internal static bool TryResolveCopyMoveTarget(
        SystemCallExecutionContext context,
        string sourcePathInput,
        string destinationPathInput,
        out string sourcePath,
        out VfsEntryMeta sourceEntry,
        out string destinationPath,
        out SystemCallResult failure)
    {
        sourcePath = BaseFileSystem.NormalizePath(context.Cwd, sourcePathInput);
        sourceEntry = null!;
        destinationPath = string.Empty;
        failure = SystemCallResultFactory.Success();

        if (sourcePath == "/")
        {
            failure = SystemCallResultFactory.Failure(SystemCallErrorCode.InvalidArgs, "root path cannot be used as source.");
            return false;
        }

        if (!context.Server.DiskOverlay.TryResolveEntry(sourcePath, out sourceEntry))
        {
            failure = SystemCallResultFactory.NotFound(sourcePath);
            return false;
        }

        var destinationCandidate = BaseFileSystem.NormalizePath(context.Cwd, destinationPathInput);
        if (context.Server.DiskOverlay.TryResolveEntry(destinationCandidate, out var destinationCandidateEntry) &&
            destinationCandidateEntry.EntryKind == VfsEntryKind.Dir)
        {
            destinationPath = JoinPath(destinationCandidate, GetName(sourcePath));
            return true;
        }

        destinationPath = destinationCandidate;
        return true;
    }

    internal static bool TryCopyEntry(
        OverlayFileSystem diskOverlay,
        string sourcePath,
        VfsEntryMeta sourceEntry,
        string destinationPath,
        out SystemCallResult failure)
    {
        if (sourceEntry.EntryKind == VfsEntryKind.File)
        {
            return TryCopyFile(
                diskOverlay,
                sourcePath,
                sourceEntry,
                destinationPath,
                out failure);
        }

        return TryCopyDirectoryTree(
            diskOverlay,
            sourcePath,
            destinationPath,
            out failure);
    }

    internal static bool TryTombstoneSubtree(OverlayFileSystem diskOverlay, string sourcePath, out SystemCallResult failure)
    {
        failure = SystemCallResultFactory.Success();
        if (!diskOverlay.TryResolveEntry(sourcePath, out var sourceEntry))
        {
            failure = SystemCallResultFactory.NotFound(sourcePath);
            return false;
        }

        var targets = new List<string> { sourcePath };
        if (sourceEntry.EntryKind == VfsEntryKind.Dir)
        {
            var queue = new Queue<string>();
            queue.Enqueue(sourcePath);
            while (queue.Count > 0)
            {
                var currentDirPath = queue.Dequeue();
                foreach (var childName in diskOverlay.ListChildren(currentDirPath))
                {
                    var childPath = JoinPath(currentDirPath, childName);
                    targets.Add(childPath);
                    if (diskOverlay.TryResolveEntry(childPath, out var childEntry) &&
                        childEntry.EntryKind == VfsEntryKind.Dir)
                    {
                        queue.Enqueue(childPath);
                    }
                }
            }
        }

        targets.Sort(static (left, right) =>
        {
            var byLength = right.Length.CompareTo(left.Length);
            return byLength != 0
                ? byLength
                : StringComparer.Ordinal.Compare(right, left);
        });

        foreach (var path in targets)
        {
            try
            {
                diskOverlay.AddTombstone(path);
            }
            catch (InvalidOperationException ex)
            {
                failure = SystemCallResultFactory.Failure(SystemCallErrorCode.InvalidArgs, ex.Message);
                return false;
            }
        }

        return true;
    }

    internal static bool IsDescendantPath(string candidatePath, string ancestorPath)
    {
        if (string.Equals(candidatePath, ancestorPath, StringComparison.Ordinal))
        {
            return false;
        }

        return candidatePath.StartsWith(ancestorPath + "/", StringComparison.Ordinal);
    }

    private static bool TryCopyDirectoryTree(
        OverlayFileSystem diskOverlay,
        string sourceDirectoryPath,
        string destinationDirectoryPath,
        out SystemCallResult failure)
    {
        failure = SystemCallResultFactory.Success();
        if (!TryEnsureDestinationDirectory(diskOverlay, destinationDirectoryPath, out failure))
        {
            return false;
        }

        var queue = new Queue<(string SourceDirPath, string DestinationDirPath)>();
        queue.Enqueue((sourceDirectoryPath, destinationDirectoryPath));
        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            foreach (var childName in diskOverlay.ListChildren(current.SourceDirPath))
            {
                var sourceChildPath = JoinPath(current.SourceDirPath, childName);
                if (!diskOverlay.TryResolveEntry(sourceChildPath, out var sourceChildEntry))
                {
                    continue;
                }

                var destinationChildPath = JoinPath(current.DestinationDirPath, childName);
                if (sourceChildEntry.EntryKind == VfsEntryKind.Dir)
                {
                    if (!TryEnsureDestinationDirectory(diskOverlay, destinationChildPath, out failure))
                    {
                        return false;
                    }

                    queue.Enqueue((sourceChildPath, destinationChildPath));
                    continue;
                }

                if (!TryCopyFile(
                        diskOverlay,
                        sourceChildPath,
                        sourceChildEntry,
                        destinationChildPath,
                        out failure))
                {
                    return false;
                }
            }
        }

        return true;
    }

    private static bool TryCopyFile(
        OverlayFileSystem diskOverlay,
        string sourceFilePath,
        VfsEntryMeta sourceFileEntry,
        string destinationFilePath,
        out SystemCallResult failure)
    {
        failure = SystemCallResultFactory.Success();
        if (sourceFileEntry.EntryKind != VfsEntryKind.File)
        {
            failure = SystemCallResultFactory.NotFile(sourceFilePath);
            return false;
        }

        if (diskOverlay.TryResolveEntry(destinationFilePath, out var destinationEntry) &&
            destinationEntry.EntryKind != VfsEntryKind.File)
        {
            failure = SystemCallResultFactory.IsDirectory(destinationFilePath);
            return false;
        }

        var destinationParentPath = GetParentPath(destinationFilePath);
        if (!diskOverlay.TryResolveEntry(destinationParentPath, out var destinationParentEntry))
        {
            failure = SystemCallResultFactory.NotFound(destinationParentPath);
            return false;
        }

        if (destinationParentEntry.EntryKind != VfsEntryKind.Dir)
        {
            failure = SystemCallResultFactory.NotDirectory(destinationParentPath);
            return false;
        }

        if (!diskOverlay.TryReadFileText(sourceFilePath, out var sourceContent))
        {
            failure = SystemCallResultFactory.NotFile(sourceFilePath);
            return false;
        }

        try
        {
            diskOverlay.WriteFile(
                destinationFilePath,
                sourceContent,
                cwd: "/",
                fileKind: sourceFileEntry.FileKind ?? VfsFileKind.Text,
                size: sourceFileEntry.Size);
        }
        catch (InvalidOperationException ex)
        {
            failure = SystemCallResultFactory.Failure(SystemCallErrorCode.InvalidArgs, ex.Message);
            return false;
        }

        return true;
    }

    private static bool TryEnsureDestinationDirectory(OverlayFileSystem diskOverlay, string destinationDirectoryPath, out SystemCallResult failure)
    {
        failure = SystemCallResultFactory.Success();
        if (diskOverlay.TryResolveEntry(destinationDirectoryPath, out var existingEntry))
        {
            if (existingEntry.EntryKind != VfsEntryKind.Dir)
            {
                failure = SystemCallResultFactory.NotDirectory(destinationDirectoryPath);
                return false;
            }

            return true;
        }

        var destinationParentPath = GetParentPath(destinationDirectoryPath);
        if (!diskOverlay.TryResolveEntry(destinationParentPath, out var parentEntry))
        {
            failure = SystemCallResultFactory.NotFound(destinationParentPath);
            return false;
        }

        if (parentEntry.EntryKind != VfsEntryKind.Dir)
        {
            failure = SystemCallResultFactory.NotDirectory(destinationParentPath);
            return false;
        }

        try
        {
            diskOverlay.AddDirectory(destinationDirectoryPath, cwd: "/");
        }
        catch (InvalidOperationException ex)
        {
            failure = SystemCallResultFactory.Failure(SystemCallErrorCode.InvalidArgs, ex.Message);
            return false;
        }

        return true;
    }

    private static string JoinPath(string directoryPath, string childName)
    {
        return directoryPath == "/"
            ? "/" + childName
            : directoryPath + "/" + childName;
    }

    private static string GetParentPath(string normalizedPath)
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

    private static string GetName(string normalizedPath)
    {
        if (normalizedPath == "/")
        {
            return string.Empty;
        }

        var trimmed = normalizedPath.TrimEnd('/');
        if (trimmed.Length == 0)
        {
            return string.Empty;
        }

        var separatorIndex = trimmed.LastIndexOf('/');
        return separatorIndex < 0
            ? trimmed
            : trimmed[(separatorIndex + 1)..];
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

internal enum MiniScriptSshExecutionMode
{
    RealWorld = 0,
    SandboxValidated,
}

internal sealed class MiniScriptExecutionOptions
{
    internal CancellationToken CancellationToken { get; init; } = CancellationToken.None;

    internal Action<string> StandardOutputLineSink { get; init; }

    internal Action<string> StandardErrorLineSink { get; init; }

    internal MiniScriptSshExecutionMode SshMode { get; init; } = MiniScriptSshExecutionMode.RealWorld;

    internal bool CaptureOutputLines { get; init; } = true;

    internal IReadOnlyList<string> ScriptArguments { get; init; } = Array.Empty<string>();

    internal double MaxIntrinsicCallsPerSecond { get; init; } = 100000;
}

internal readonly record struct MiniScriptExecutionResult(SystemCallResult Result, bool WasCancelled);

internal static class MiniScriptExecutionRunner
{
    private const double TimeSliceSeconds = 0.01;

    internal static SystemCallResult ExecuteScript(
        string scriptSource,
        SystemCallExecutionContext executionContext = null,
        IReadOnlyList<string> scriptArguments = null)
    {
        return ExecuteScriptWithOptions(
            scriptSource,
            executionContext,
            new MiniScriptExecutionOptions
            {
                ScriptArguments = scriptArguments ?? Array.Empty<string>(),
            }).Result;
    }

    internal static MiniScriptExecutionResult ExecuteScriptWithOptions(
        string scriptSource,
        SystemCallExecutionContext executionContext = null,
        MiniScriptExecutionOptions options = null)
    {
        if (options == null)
        {
            options = new MiniScriptExecutionOptions();
        }
        var standardOutput = new ScriptOutputCollector(
            options.StandardOutputLineSink,
            options.CaptureOutputLines);
        var fatalStderrLineCount = 0;
        var errorOutput = new ScriptOutputCollector(
            line =>
            {
                if (!options.CaptureOutputLines && !IsNonFatalMiniScriptStderrLine(line))
                {
                    fatalStderrLineCount++;
                }

                options.StandardErrorLineSink?.Invoke(line);
            },
            options.CaptureOutputLines);

        try
        {
            var interpreter = new Interpreter(scriptSource, standardOutput.Append, errorOutput.Append)
            {
                implicitOutput = standardOutput.Append,
            };

            MiniScriptCryptoIntrinsics.InjectCryptoModule(interpreter);
            MiniScriptSshIntrinsics.InjectSshModule(interpreter, executionContext, options.SshMode);
            MiniScriptTermIntrinsics.InjectTermModule(interpreter, executionContext);
            ArgsIntrinsics.InjectArgs(interpreter, options.ScriptArguments);
            MiniScriptIntrinsicRateLimiter.ConfigureInterpreter(interpreter, options.MaxIntrinsicCallsPerSecond);
            while (!interpreter.done)
            {
                if (options.CancellationToken.IsCancellationRequested)
                {
                    interpreter.Stop();
                    return new MiniScriptExecutionResult(SystemCallResultFactory.Success(), true);
                }

                interpreter.RunUntilDone(TimeSliceSeconds, returnEarly: false);

                if (options.CancellationToken.IsCancellationRequested)
                {
                    interpreter.Stop();
                    return new MiniScriptExecutionResult(SystemCallResultFactory.Success(), true);
                }
            }
        }
        catch (OperationCanceledException)
        {
            return new MiniScriptExecutionResult(SystemCallResultFactory.Success(), true);
        }
        catch (Exception ex)
        {
            return new MiniScriptExecutionResult(
                SystemCallResultFactory.Failure(
                    SystemCallErrorCode.InternalError,
                    "miniscript execution failed: " + ex.Message),
                false);
        }

        var stdoutLines = standardOutput.ToLines();
        if (!options.CaptureOutputLines)
        {
            return fatalStderrLineCount == 0
                ? new MiniScriptExecutionResult(SystemCallResultFactory.Success(), false)
                : new MiniScriptExecutionResult(
                    new SystemCallResult
                    {
                        Ok = false,
                        Code = SystemCallErrorCode.InternalError,
                        Lines = Array.Empty<string>(),
                    },
                    false);
        }

        var stderrLines = errorOutput.ToLines();
        if (stderrLines.Count == 0)
        {
            return new MiniScriptExecutionResult(SystemCallResultFactory.Success(lines: stdoutLines), false);
        }

        var nonFatalStderrLines = new List<string>();
        var fatalStderrLines = new List<string>();
        foreach (var line in stderrLines)
        {
            if (IsNonFatalMiniScriptStderrLine(line))
            {
                nonFatalStderrLines.Add(line);
                continue;
            }

            fatalStderrLines.Add(line);
        }

        var lines = new List<string>(stdoutLines.Count + nonFatalStderrLines.Count + fatalStderrLines.Count);
        lines.AddRange(stdoutLines);
        lines.AddRange(nonFatalStderrLines);
        if (fatalStderrLines.Count == 0)
        {
            return new MiniScriptExecutionResult(SystemCallResultFactory.Success(lines: lines), false);
        }

        foreach (var line in fatalStderrLines)
        {
            lines.Add(NormalizeFatalMiniScriptStderrLine(line));
        }

        return new MiniScriptExecutionResult(
            new SystemCallResult
            {
                Ok = false,
                Code = SystemCallErrorCode.InternalError,
                Lines = lines,
            },
            false);
    }

    private static bool IsNonFatalMiniScriptStderrLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return false;
        }

        return line.StartsWith("warn:", StringComparison.OrdinalIgnoreCase) ||
               line.StartsWith("error:", StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeFatalMiniScriptStderrLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return "error: miniscript execution failed.";
        }

        return line.StartsWith("error:", StringComparison.OrdinalIgnoreCase) ? line : "error: " + line;
    }

    private sealed class ScriptOutputCollector
    {
        private readonly List<string> lines = new();
        private readonly StringBuilder pending = new();
        private readonly Action<string> lineSink;
        private readonly bool captureLines;

        internal ScriptOutputCollector(Action<string> lineSink, bool captureLines)
        {
            this.lineSink = lineSink;
            this.captureLines = captureLines;
        }

        internal int LineCount { get; private set; }

        internal void Append(string text, bool addLineBreak)
        {
            if (!string.IsNullOrEmpty(text))
            {
                pending.Append(text);
            }

            if (addLineBreak)
            {
                FlushPendingLine();
            }
        }

        internal IReadOnlyList<string> ToLines()
        {
            if (pending.Length > 0)
            {
                FlushPendingLine();
            }

            return captureLines ? lines : Array.Empty<string>();
        }

        private void FlushPendingLine()
        {
            var line = pending.ToString();
            pending.Clear();
            LineCount++;
            if (captureLines)
            {
                lines.Add(line);
            }

            if (lineSink != null)
            {
                lineSink(line);
            }
        }
    }
}

internal sealed class RmCommandHandler : VfsCommandHandlerBase
{
    private const string UsageText = "rm [(-r|-R|--recursive)] <path>";

    public override string Command => "rm";

    public override SystemCallResult Execute(SystemCallExecutionContext context, IReadOnlyList<string> arguments)
    {
        if (!RequireWrite(context, Command, out var permissionResult))
        {
            return permissionResult;
        }

        if (!TryParseArguments(arguments, out var recursive, out var pathInput))
        {
            return SystemCallResultFactory.Usage(UsageText);
        }

        var targetPath = NormalizePath(context, pathInput);
        if (targetPath == "/")
        {
            return SystemCallResultFactory.Failure(SystemCallErrorCode.InvalidArgs, "rm cannot remove root directory.");
        }

        if (!context.Server.DiskOverlay.TryResolveEntry(targetPath, out var entry))
        {
            return SystemCallResultFactory.NotFound(targetPath);
        }

        if (!recursive && entry.EntryKind != VfsEntryKind.File)
        {
            return SystemCallResultFactory.NotFile(targetPath);
        }

        if (recursive &&
            (string.Equals(targetPath, context.Cwd, StringComparison.Ordinal) ||
             context.Cwd.StartsWith(targetPath + "/", StringComparison.Ordinal)))
        {
            return SystemCallResultFactory.Failure(
                SystemCallErrorCode.InvalidArgs,
                "rm cannot remove current working directory or its ancestor.");
        }

        if (!recursive || entry.EntryKind == VfsEntryKind.File)
        {
            context.Server.DiskOverlay.AddTombstone(targetPath);
            return SystemCallResultFactory.Success();
        }

        var targets = EnumerateLiveSubtree(context.Server.DiskOverlay, targetPath);
        targets.Sort(static (left, right) =>
        {
            var byLength = right.Length.CompareTo(left.Length);
            return byLength != 0
                ? byLength
                : StringComparer.Ordinal.Compare(right, left);
        });

        foreach (var path in targets)
        {
            try
            {
                context.Server.DiskOverlay.AddTombstone(path);
            }
            catch (InvalidOperationException ex)
            {
                return SystemCallResultFactory.Failure(SystemCallErrorCode.InvalidArgs, ex.Message);
            }
        }

        return SystemCallResultFactory.Success();
    }

    private static bool TryParseArguments(IReadOnlyList<string> arguments, out bool recursive, out string pathInput)
    {
        recursive = false;
        pathInput = string.Empty;
        if (arguments.Count == 1)
        {
            pathInput = arguments[0];
            return !string.IsNullOrWhiteSpace(pathInput);
        }

        if (arguments.Count != 2)
        {
            return false;
        }

        if (!IsRecursiveOption(arguments[0]))
        {
            return false;
        }

        recursive = true;
        pathInput = arguments[1];
        return !string.IsNullOrWhiteSpace(pathInput);
    }

    private static bool IsRecursiveOption(string token)
    {
        return string.Equals(token, "-r", StringComparison.Ordinal) ||
               string.Equals(token, "-R", StringComparison.Ordinal) ||
               string.Equals(token, "--recursive", StringComparison.Ordinal);
    }

    private static List<string> EnumerateLiveSubtree(OverlayFileSystem diskOverlay, string rootPath)
    {
        var targets = new List<string> { rootPath };
        var queue = new Queue<string>();
        queue.Enqueue(rootPath);

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            foreach (var childName in diskOverlay.ListChildren(current))
            {
                var childPath = current == "/" ? "/" + childName : current + "/" + childName;
                if (!diskOverlay.TryResolveEntry(childPath, out var childEntry))
                {
                    continue;
                }

                targets.Add(childPath);
                if (childEntry.EntryKind == VfsEntryKind.Dir)
                {
                    queue.Enqueue(childPath);
                }
            }
        }

        return targets;
    }
}
