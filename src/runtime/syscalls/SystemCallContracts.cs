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
    /// <summary>Network exposure/policy denied the requested access.</summary>
    NetDenied,
    NotFound,
    /// <summary>Requested port is unassigned or unavailable.</summary>
    PortClosed,
    NotFile,
    NotDirectory,
    Conflict,
    InternalError,
    AlreadyExists,
    IsDirectory,
    NotTextFile,
    TooLarge,
}

/// <summary>Request payload for executing one terminal system call.</summary>
public sealed class SystemCallRequest
{
    /// <summary>Target server node id.</summary>
    public string NodeId { get; set; } = string.Empty;

    /// <summary>Executing account id text on the target server (user-facing identifier).</summary>
    public string UserId { get; set; } = string.Empty;

    /// <summary>Current working directory of the caller.</summary>
    public string Cwd { get; set; } = "/";

    /// <summary>Raw command line entered by the user.</summary>
    public string CommandLine { get; set; } = string.Empty;

    /// <summary>Terminal session id used to track connect/disconnect stack state.</summary>
    public string TerminalSessionId { get; set; } = string.Empty;
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

    internal static SystemCallResult AlreadyExists(string path)
    {
        return Failure(SystemCallErrorCode.AlreadyExists, "already exists: " + path);
    }

    internal static SystemCallResult IsDirectory(string path)
    {
        return Failure(SystemCallErrorCode.IsDirectory, "is a directory: " + path);
    }

    internal static SystemCallResult NotTextFile(string path)
    {
        return Failure(SystemCallErrorCode.NotTextFile, "not a text file: " + path);
    }

    internal static SystemCallResult TooLarge(string path, long maxBytes, long actualBytes)
    {
        return Failure(SystemCallErrorCode.TooLarge, $"too large: {path} (max={maxBytes}, actual={actualBytes})");
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
        string cwd,
        string terminalSessionId)
    {
        World = world;
        Server = server;
        User = user;
        NodeId = nodeId;
        UserKey = userKey;
        Cwd = cwd;
        TerminalSessionId = terminalSessionId;
    }

    internal WorldRuntime World { get; }

    internal ServerNodeRuntime Server { get; }

    internal UserConfig User { get; }

    internal string NodeId { get; }

    internal string UserKey { get; }

    internal string Cwd { get; }

    internal string TerminalSessionId { get; }
}

/// <summary>Prepared MiniScript launch payload for asynchronous terminal execution.</summary>
internal readonly record struct MiniScriptProgramLaunch(
    SystemCallExecutionContext Context,
    string ScriptSource,
    string ProgramPath,
    string Command,
    string CommandLine,
    IReadOnlyList<string> ScriptArguments);

/// <summary>Context transition payload returned by system calls that switch terminal target.</summary>
internal sealed class TerminalContextTransition
{
    internal string NextNodeId { get; init; } = string.Empty;

    internal string NextUserId { get; init; } = string.Empty;

    internal string NextPromptUser { get; init; } = string.Empty;

    internal string NextPromptHost { get; init; } = string.Empty;

    internal string NextCwd { get; init; } = "/";
}

/// <summary>Editor-open payload returned by system calls that enter text editor mode.</summary>
internal sealed class EditorOpenTransition
{
    internal string TargetPath { get; init; } = string.Empty;

    internal string Content { get; init; } = string.Empty;

    internal bool ReadOnly { get; init; }

    internal string DisplayMode { get; init; } = "text";

    internal bool PathExists { get; init; }
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
