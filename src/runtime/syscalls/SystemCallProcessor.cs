using Godot;
using System;
using System.Collections.Generic;
using System.Linq;
using Uplink2.Vfs;

#nullable enable

namespace Uplink2.Runtime.Syscalls;

internal sealed class SystemCallProcessor
{
    private static readonly string[] ProgramPathDirectories =
    {
        "/opt/bin",
    };

    private readonly WorldRuntime world;
    private readonly SystemCallRegistry registry = new();
    private readonly Dictionary<string, Func<SystemCallExecutionContext, IReadOnlyList<string>, SystemCallResult>> hardcodedExecutableHandlers =
        new(StringComparer.Ordinal)
        {
            ["noop"] = static (_, _) => SystemCallResultFactory.Success(),
            ["miniscript"] = ExecuteMiniScriptProgram,
        };

    internal SystemCallProcessor(WorldRuntime world, IEnumerable<ISystemCallModule> modules)
    {
        this.world = world ?? throw new ArgumentNullException(nameof(world));
        if (modules is null)
        {
            throw new ArgumentNullException(nameof(modules));
        }

        foreach (var module in modules)
        {
            module.Register(registry);
        }
    }

    internal SystemCallResult Execute(SystemCallRequest request)
    {
        if (request is null)
        {
            throw new ArgumentNullException(nameof(request));
        }

        if (!SystemCallParser.TryParse(request.CommandLine, out var command, out var arguments, out var parseError))
        {
            return SystemCallResultFactory.Failure(SystemCallErrorCode.InvalidArgs, parseError);
        }

        var contextResult = TryCreateContext(request, out var context);
        if (!contextResult.Ok)
        {
            return contextResult;
        }

        if (!registry.TryGetHandler(command, out var handler))
        {
            if (TryExecuteProgram(context!, command, arguments, out var programResult))
            {
                return programResult;
            }

            return SystemCallResultFactory.Failure(SystemCallErrorCode.UnknownCommand, $"unknown command: {command}");
        }

        try
        {
            return handler.Execute(context!, arguments);
        }
        catch (Exception ex)
        {
            GD.PushError($"System call '{command}' failed: {ex}");
            return SystemCallResultFactory.Failure(SystemCallErrorCode.InternalError, "internal error.");
        }
    }

    private SystemCallResult TryCreateContext(SystemCallRequest request, out SystemCallExecutionContext? context)
    {
        context = null;

        if (string.IsNullOrWhiteSpace(request.NodeId))
        {
            return SystemCallResultFactory.Failure(SystemCallErrorCode.InvalidArgs, "nodeId is required.");
        }

        if (!world.TryGetServer(request.NodeId.Trim(), out var server))
        {
            return SystemCallResultFactory.Failure(SystemCallErrorCode.NotFound, $"server not found: {request.NodeId.Trim()}");
        }

        if (string.IsNullOrWhiteSpace(request.UserKey))
        {
            return SystemCallResultFactory.Failure(SystemCallErrorCode.InvalidArgs, "userKey is required.");
        }

        var userKey = request.UserKey.Trim();
        if (!server.Users.TryGetValue(userKey, out var user))
        {
            return SystemCallResultFactory.Failure(SystemCallErrorCode.NotFound, $"user not found: {userKey}");
        }

        var normalizedCwd = BaseFileSystem.NormalizePath("/", request.Cwd);
        if (!server.DiskOverlay.TryResolveEntry(normalizedCwd, out var cwdEntry))
        {
            return SystemCallResultFactory.NotFound(normalizedCwd);
        }

        if (cwdEntry.EntryKind != VfsEntryKind.Dir)
        {
            return SystemCallResultFactory.NotDirectory(normalizedCwd);
        }

        context = new SystemCallExecutionContext(
            world,
            server,
            user,
            request.NodeId.Trim(),
            userKey,
            normalizedCwd);
        return SystemCallResultFactory.Success();
    }

    private bool TryExecuteProgram(
        SystemCallExecutionContext context,
        string command,
        IReadOnlyList<string> arguments,
        out SystemCallResult result)
    {
        result = SystemCallResultFactory.Success();

        if (!TryResolveProgramPath(context, command, out var resolvedProgramPath, out var resolvedProgramEntry))
        {
            return false;
        }

        if (!context.User.Privilege.Read || !context.User.Privilege.Execute)
        {
            result = SystemCallResultFactory.PermissionDenied(command);
            return true;
        }

        if (resolvedProgramEntry!.FileKind == VfsFileKind.ExecutableScript)
        {
            return TryExecuteScriptProgram(context, resolvedProgramPath, out result);
        }

        if (resolvedProgramEntry.FileKind == VfsFileKind.ExecutableHardcode)
        {
            return TryExecuteHardcodedProgram(context, command, resolvedProgramPath, arguments, out result);
        }

        return false;
    }

    private static bool TryResolveProgramPath(
        SystemCallExecutionContext context,
        string command,
        out string resolvedProgramPath,
        out VfsEntryMeta? resolvedProgramEntry)
    {
        resolvedProgramPath = string.Empty;
        resolvedProgramEntry = null;

        if (string.IsNullOrWhiteSpace(command))
        {
            return false;
        }

        var candidatePaths = new List<string>();
        if (command.Contains("/", StringComparison.Ordinal))
        {
            candidatePaths.Add(BaseFileSystem.NormalizePath(context.Cwd, command));
        }
        else
        {
            candidatePaths.Add(BaseFileSystem.NormalizePath(context.Cwd, command));
            foreach (var searchDir in ProgramPathDirectories)
            {
                var joined = searchDir.EndsWith("/", StringComparison.Ordinal)
                    ? searchDir + command
                    : searchDir + "/" + command;
                candidatePaths.Add(BaseFileSystem.NormalizePath("/", joined));
            }
        }

        foreach (var candidatePath in candidatePaths.Distinct(StringComparer.Ordinal))
        {
            if (!context.Server.DiskOverlay.TryResolveEntry(candidatePath, out var entry))
            {
                continue;
            }

            if (entry.EntryKind != VfsEntryKind.File || !entry.IsDirectExecutable())
            {
                continue;
            }

            resolvedProgramPath = candidatePath;
            resolvedProgramEntry = entry;
            return true;
        }

        return false;
    }

    private static bool TryExecuteScriptProgram(
        SystemCallExecutionContext context,
        string resolvedProgramPath,
        out SystemCallResult result)
    {
        result = SystemCallResultFactory.Success();

        if (!context.Server.DiskOverlay.TryReadFileText(resolvedProgramPath, out var scriptSource))
        {
            result = SystemCallResultFactory.NotFile(resolvedProgramPath);
            return true;
        }

        result = MiniScriptExecutionRunner.ExecuteScript(scriptSource);
        return true;
    }

    private bool TryExecuteHardcodedProgram(
        SystemCallExecutionContext context,
        string command,
        string resolvedProgramPath,
        IReadOnlyList<string> arguments,
        out SystemCallResult result)
    {
        result = SystemCallResultFactory.Success();

        if (!context.Server.DiskOverlay.TryReadFileText(resolvedProgramPath, out var executablePayload))
        {
            return false;
        }

        var executableId = executablePayload.Trim();
        if (string.IsNullOrWhiteSpace(executableId))
        {
            WarnUnknownExecutableId(context, command, resolvedProgramPath, executableId);
            result = SystemCallResultFactory.Failure(SystemCallErrorCode.UnknownCommand, $"unknown command: {command}");
            return true;
        }

        if (!hardcodedExecutableHandlers.TryGetValue(executableId, out var handler))
        {
            WarnUnknownExecutableId(context, command, resolvedProgramPath, executableId);
            result = SystemCallResultFactory.Failure(SystemCallErrorCode.UnknownCommand, $"unknown command: {command}");
            return true;
        }

        try
        {
            result = handler(context, arguments);
            return true;
        }
        catch (Exception ex)
        {
            GD.PushError($"Hardcoded executable '{executableId}' failed: {ex}");
            result = SystemCallResultFactory.Failure(SystemCallErrorCode.InternalError, "internal error.");
            return true;
        }
    }

    private void WarnUnknownExecutableId(
        SystemCallExecutionContext context,
        string command,
        string resolvedProgramPath,
        string executableId)
    {
        if (!world.DebugOption)
        {
            return;
        }

        var printableExecutableId = string.IsNullOrWhiteSpace(executableId) ? "<empty>" : executableId;
        GD.PushWarning(
            $"Unknown executableId mapping. command='{command}', resolvedProgramPath='{resolvedProgramPath}', executableId='{printableExecutableId}', nodeId='{context.NodeId}', userKey='{context.UserKey}', cwd='{context.Cwd}'.");
    }

    private static SystemCallResult ExecuteMiniScriptProgram(
        SystemCallExecutionContext context,
        IReadOnlyList<string> arguments)
    {
        if (arguments.Count != 1)
        {
            return SystemCallResultFactory.Usage("miniscript <script>");
        }

        var scriptPath = BaseFileSystem.NormalizePath(context.Cwd, arguments[0]);
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

        return MiniScriptExecutionRunner.ExecuteScript(scriptSource);
    }
}
