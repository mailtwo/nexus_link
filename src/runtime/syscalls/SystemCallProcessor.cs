using Godot;
using System;
using System.Collections.Generic;
using System.Linq;
using Uplink2.Vfs;

#nullable enable

namespace Uplink2.Runtime.Syscalls;

internal sealed class SystemCallProcessor
{
    private const string ExecutableHardcodePrefix = "exec:";

    private static readonly string[] ProgramPathDirectories =
    {
        "/opt/bin",
    };

    private readonly WorldRuntime world;
    private readonly SystemCallRegistry registry = new();
    private readonly ExecutableHardcodeRegistry hardcodedExecutableRegistry = new();

    internal SystemCallProcessor(WorldRuntime world, IEnumerable<ISystemCallModule> modules)
    {
        this.world = world ?? throw new ArgumentNullException(nameof(world));
        if (modules is null)
        {
            throw new ArgumentNullException(nameof(modules));
        }

        hardcodedExecutableRegistry.Register(new NoopExecutableHardcodeHandler());
        hardcodedExecutableRegistry.Register(new MiniScriptExecutableHardcodeHandler());

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

    internal bool TryPrepareTerminalProgramExecution(
        SystemCallRequest request,
        out MiniScriptProgramLaunch? launch,
        out SystemCallResult? immediateResult)
    {
        launch = null;
        immediateResult = null;

        if (request is null)
        {
            throw new ArgumentNullException(nameof(request));
        }

        if (!SystemCallParser.TryParse(request.CommandLine, out var command, out var arguments, out _))
        {
            return false;
        }

        var contextResult = TryCreateContext(request, out var context);
        if (!contextResult.Ok)
        {
            return false;
        }

        if (registry.TryGetHandler(command, out _))
        {
            if (!string.Equals(command, "DEBUG_miniscript", StringComparison.Ordinal))
            {
                return false;
            }

            return TryPrepareDebugMiniScriptLaunch(
                context!,
                request.CommandLine ?? string.Empty,
                command,
                arguments,
                out launch,
                out immediateResult);
        }

        if (!TryResolveProgramPath(context!, command, out var resolvedProgramPath, out var resolvedProgramEntry))
        {
            return false;
        }

        if (!context!.User.Privilege.Read || !context.User.Privilege.Execute)
        {
            immediateResult = SystemCallResultFactory.PermissionDenied(command);
            return true;
        }

        if (resolvedProgramEntry!.FileKind == VfsFileKind.ExecutableScript)
        {
            return TryPrepareExecutableScriptLaunch(
                context,
                command,
                request.CommandLine ?? string.Empty,
                resolvedProgramPath,
                arguments,
                out launch,
                out immediateResult);
        }

        if (resolvedProgramEntry.FileKind != VfsFileKind.ExecutableHardcode)
        {
            return false;
        }

        if (!context.Server.DiskOverlay.TryReadFileText(resolvedProgramPath, out var executablePayload))
        {
            return false;
        }

        if (!TryParseExecutableHardcodePayload(executablePayload, out var executableId) ||
            !string.Equals(executableId, "miniscript", StringComparison.Ordinal))
        {
            return false;
        }

        return TryPrepareMiniScriptSourceLaunch(
            context,
            command,
            request.CommandLine ?? string.Empty,
            resolvedProgramPath,
            arguments,
            out launch,
            out immediateResult);
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

        if (string.IsNullOrWhiteSpace(request.UserId))
        {
            return SystemCallResultFactory.Failure(SystemCallErrorCode.InvalidArgs, "userId is required.");
        }

        var userId = request.UserId.Trim();
        if (!world.TryResolveUserByUserId(server, userId, out var userKey, out var user))
        {
            return SystemCallResultFactory.Failure(SystemCallErrorCode.NotFound, $"user not found: {userId}");
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
            normalizedCwd,
            request.TerminalSessionId?.Trim() ?? string.Empty);
        return SystemCallResultFactory.Success();
    }

    private static bool TryPrepareExecutableScriptLaunch(
        SystemCallExecutionContext context,
        string command,
        string commandLine,
        string resolvedProgramPath,
        IReadOnlyList<string> arguments,
        out MiniScriptProgramLaunch? launch,
        out SystemCallResult? immediateResult)
    {
        launch = null;
        if (!context.Server.DiskOverlay.TryReadFileText(resolvedProgramPath, out var scriptSource))
        {
            immediateResult = SystemCallResultFactory.NotFile(resolvedProgramPath);
            return true;
        }

        launch = new MiniScriptProgramLaunch(
            context,
            scriptSource,
            resolvedProgramPath,
            command,
            commandLine,
            arguments.ToArray());
        immediateResult = SystemCallResultFactory.Success();
        return true;
    }

    private static bool TryPrepareDebugMiniScriptLaunch(
        SystemCallExecutionContext context,
        string commandLine,
        string command,
        IReadOnlyList<string> arguments,
        out MiniScriptProgramLaunch? launch,
        out SystemCallResult? immediateResult)
    {
        launch = null;
        if (!context.User.Privilege.Read)
        {
            immediateResult = SystemCallResultFactory.PermissionDenied(command);
            return true;
        }

        if (!context.User.Privilege.Execute)
        {
            immediateResult = SystemCallResultFactory.PermissionDenied(command);
            return true;
        }

        if (arguments.Count != 1)
        {
            immediateResult = SystemCallResultFactory.Usage("DEBUG_miniscript <script>");
            return true;
        }

        var scriptPath = BaseFileSystem.NormalizePath(context.Cwd, arguments[0]);
        if (!context.Server.DiskOverlay.TryResolveEntry(scriptPath, out var scriptEntry))
        {
            immediateResult = SystemCallResultFactory.NotFound(scriptPath);
            return true;
        }

        if (scriptEntry.EntryKind != VfsEntryKind.File)
        {
            immediateResult = SystemCallResultFactory.NotFile(scriptPath);
            return true;
        }

        if (scriptEntry.FileKind != VfsFileKind.Text && scriptEntry.FileKind != VfsFileKind.ExecutableScript)
        {
            immediateResult = SystemCallResultFactory.Failure(
                SystemCallErrorCode.InvalidArgs,
                "DEBUG_miniscript source must be text or executable script: " + scriptPath);
            return true;
        }

        if (!context.Server.DiskOverlay.TryReadFileText(scriptPath, out var scriptSource))
        {
            immediateResult = SystemCallResultFactory.NotFile(scriptPath);
            return true;
        }

        launch = new MiniScriptProgramLaunch(
            context,
            scriptSource,
            scriptPath,
            command,
            commandLine,
            Array.Empty<string>());
        immediateResult = SystemCallResultFactory.Success();
        return true;
    }

    private static bool TryPrepareMiniScriptSourceLaunch(
        SystemCallExecutionContext context,
        string command,
        string commandLine,
        string resolvedProgramPath,
        IReadOnlyList<string> arguments,
        out MiniScriptProgramLaunch? launch,
        out SystemCallResult? immediateResult)
    {
        launch = null;
        if (arguments.Count < 1)
        {
            immediateResult = SystemCallResultFactory.Usage("miniscript <script>");
            return true;
        }

        var scriptPath = BaseFileSystem.NormalizePath(context.Cwd, arguments[0]);
        if (!context.Server.DiskOverlay.TryResolveEntry(scriptPath, out var scriptEntry))
        {
            immediateResult = SystemCallResultFactory.NotFound(scriptPath);
            return true;
        }

        if (scriptEntry.EntryKind != VfsEntryKind.File)
        {
            immediateResult = SystemCallResultFactory.NotFile(scriptPath);
            return true;
        }

        if (scriptEntry.FileKind != VfsFileKind.Text)
        {
            immediateResult = SystemCallResultFactory.Failure(
                SystemCallErrorCode.InvalidArgs,
                "miniscript source must be text: " + scriptPath);
            return true;
        }

        if (!context.Server.DiskOverlay.TryReadFileText(scriptPath, out var scriptSource))
        {
            immediateResult = SystemCallResultFactory.NotFile(scriptPath);
            return true;
        }

        var scriptArguments = arguments.Skip(1).ToArray();
        launch = new MiniScriptProgramLaunch(
            context,
            scriptSource,
            resolvedProgramPath,
            command,
            commandLine,
            scriptArguments);
        immediateResult = SystemCallResultFactory.Success();
        return true;
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
            return TryExecuteScriptProgram(context, resolvedProgramPath, arguments, out result);
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
        IReadOnlyList<string> arguments,
        out SystemCallResult result)
    {
        result = SystemCallResultFactory.Success();

        if (!context.Server.DiskOverlay.TryReadFileText(resolvedProgramPath, out var scriptSource))
        {
            result = SystemCallResultFactory.NotFile(resolvedProgramPath);
            return true;
        }

        result = MiniScriptExecutionRunner.ExecuteScript(scriptSource, context, arguments);
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

        var rawContentId = executablePayload;
        if (!TryParseExecutableHardcodePayload(rawContentId, out var executableId))
        {
            WarnUnknownExecutableId(context, command, resolvedProgramPath, rawContentId, executableId);
            result = SystemCallResultFactory.Failure(SystemCallErrorCode.UnknownCommand, $"unknown command: {command}");
            return true;
        }

        if (!hardcodedExecutableRegistry.TryGetHandler(executableId, out var handler))
        {
            WarnUnknownExecutableId(context, command, resolvedProgramPath, rawContentId, executableId);
            result = SystemCallResultFactory.Failure(SystemCallErrorCode.UnknownCommand, $"unknown command: {command}");
            return true;
        }

        try
        {
            result = handler.Execute(new ExecutableHardcodeInvocation(
                context,
                command,
                resolvedProgramPath,
                rawContentId,
                executableId,
                arguments));
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
        string rawContentId,
        string executableId)
    {
        if (!world.DebugOption)
        {
            return;
        }

        var printableExecutableId = string.IsNullOrWhiteSpace(executableId) ? "<empty>" : executableId;
        var printableRawContentId = string.IsNullOrWhiteSpace(rawContentId) ? "<empty>" : rawContentId;
        GD.PushWarning(
            $"Unknown executableId mapping. command='{command}', resolvedProgramPath='{resolvedProgramPath}', rawContentId='{printableRawContentId}', executableId='{printableExecutableId}', nodeId='{context.NodeId}', userKey='{context.UserKey}', cwd='{context.Cwd}'.");
    }

    private static bool TryParseExecutableHardcodePayload(string rawContentId, out string executableId)
    {
        executableId = string.Empty;
        var trimmed = rawContentId?.Trim() ?? string.Empty;
        if (!trimmed.StartsWith(ExecutableHardcodePrefix, StringComparison.Ordinal))
        {
            return false;
        }

        var parsed = trimmed[ExecutableHardcodePrefix.Length..];
        if (string.IsNullOrWhiteSpace(parsed))
        {
            return false;
        }

        executableId = parsed;
        return true;
    }
}
