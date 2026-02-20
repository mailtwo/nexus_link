using System;
using System.Collections.Generic;
using Uplink2.Runtime;

#nullable enable

namespace Uplink2.Runtime.Syscalls;

/// <summary>Error code for system-call execution results.</summary>
public enum SystemCallErrorCode
{
    None,
    UnknownCommand,
    InvalidArgs,
    PermissionDenied,
    NotFound,
    NotFile,
    NotDirectory,
    Conflict,
    InternalError,
}

/// <summary>Request payload for executing one terminal system call.</summary>
public sealed class SystemCallRequest
{
    /// <summary>Target server node id.</summary>
    public string NodeId { get; set; } = string.Empty;

    /// <summary>Executing account key on the target server.</summary>
    public string UserKey { get; set; } = string.Empty;

    /// <summary>Current working directory of the caller.</summary>
    public string Cwd { get; set; } = "/";

    /// <summary>Raw command line entered by the user.</summary>
    public string CommandLine { get; set; } = string.Empty;
}

/// <summary>Execution result returned by the system-call processor.</summary>
public sealed class SystemCallResult
{
    /// <summary>True when command execution succeeded.</summary>
    public bool Ok { get; init; }

    /// <summary>Structured result code for branching and diagnostics.</summary>
    public SystemCallErrorCode Code { get; init; }

    /// <summary>Lines to print to terminal in order.</summary>
    public IReadOnlyList<string> Lines { get; init; } = Array.Empty<string>();

    /// <summary>Updated working directory when command changes cwd.</summary>
    public string? NextCwd { get; init; }

    /// <summary>Optional structured payload for future UI/meta use.</summary>
    public object? Data { get; init; }

    /// <summary>Creates a successful result payload.</summary>
    public static SystemCallResult Success(IEnumerable<string>? lines = null, string? nextCwd = null, object? data = null)
    {
        return new SystemCallResult
        {
            Ok = true,
            Code = SystemCallErrorCode.None,
            Lines = lines is null ? Array.Empty<string>() : new List<string>(lines),
            NextCwd = nextCwd,
            Data = data,
        };
    }

    /// <summary>Creates a failed result payload with one terminal error line.</summary>
    public static SystemCallResult Failure(SystemCallErrorCode code, string message)
    {
        var normalizedMessage = string.IsNullOrWhiteSpace(message) ? "unknown error." : message.Trim();
        return new SystemCallResult
        {
            Ok = false,
            Code = code,
            Lines = new[] { "error: " + normalizedMessage },
        };
    }
}

internal static class SystemCallResultFactory
{
    internal static SystemCallResult Success(IEnumerable<string>? lines = null, string? nextCwd = null, object? data = null)
    {
        return SystemCallResult.Success(lines, nextCwd, data);
    }

    internal static SystemCallResult Failure(SystemCallErrorCode code, string message)
    {
        return SystemCallResult.Failure(code, message);
    }

    internal static SystemCallResult Usage(string usage)
    {
        return Failure(SystemCallErrorCode.InvalidArgs, "usage: " + usage);
    }

    internal static SystemCallResult PermissionDenied(string command)
    {
        return Failure(SystemCallErrorCode.PermissionDenied, "permission denied: " + command);
    }

    internal static SystemCallResult NotFound(string path)
    {
        return Failure(SystemCallErrorCode.NotFound, "no such file or directory: " + path);
    }

    internal static SystemCallResult NotDirectory(string path)
    {
        return Failure(SystemCallErrorCode.NotDirectory, "not a directory: " + path);
    }

    internal static SystemCallResult NotFile(string path)
    {
        return Failure(SystemCallErrorCode.NotFile, "not a file: " + path);
    }

    internal static SystemCallResult Conflict(string path)
    {
        return Failure(SystemCallErrorCode.Conflict, "path conflict: " + path);
    }
}

internal sealed class SystemCallExecutionContext
{
    internal SystemCallExecutionContext(
        WorldRuntime world,
        ServerNodeRuntime server,
        UserConfig user,
        string nodeId,
        string userKey,
        string cwd)
    {
        World = world;
        Server = server;
        User = user;
        NodeId = nodeId;
        UserKey = userKey;
        Cwd = cwd;
    }

    internal WorldRuntime World { get; }

    internal ServerNodeRuntime Server { get; }

    internal UserConfig User { get; }

    internal string NodeId { get; }

    internal string UserKey { get; }

    internal string Cwd { get; }
}

internal interface ISystemCallHandler
{
    string Command { get; }

    SystemCallResult Execute(SystemCallExecutionContext context, IReadOnlyList<string> arguments);
}

internal interface ISystemCallModule
{
    void Register(SystemCallRegistry registry);
}
