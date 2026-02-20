using Godot;
using System;
using System.Collections.Generic;
using Uplink2.Vfs;

#nullable enable

namespace Uplink2.Runtime.Syscalls;

internal sealed class SystemCallProcessor
{
    private readonly WorldRuntime world;
    private readonly SystemCallRegistry registry = new();

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

        if (!registry.TryGetHandler(command, out var handler))
        {
            return SystemCallResultFactory.Failure(SystemCallErrorCode.UnknownCommand, $"unknown command: {command}");
        }

        var contextResult = TryCreateContext(request, out var context);
        if (!contextResult.Ok)
        {
            return contextResult;
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
}
