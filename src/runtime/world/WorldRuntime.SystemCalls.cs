using Godot;
using System;
using System.Linq;
using Uplink2.Runtime.Syscalls;

namespace Uplink2.Runtime;

public partial class WorldRuntime
{
    /// <summary>Initializes system-call modules and command dispatch processor.</summary>
    private void InitializeSystemCalls()
    {
        ISystemCallModule[] modules =
        {
            new VfsSystemCallModule(enableDebugCommands: DebugOption),
        };

        systemCallProcessor = new SystemCallProcessor(this, modules);
    }

    /// <summary>Executes a terminal system call through the internal processor; use this public entry point for black-box tests instead of exposing internal handlers.</summary>
    public SystemCallResult ExecuteSystemCall(SystemCallRequest request)
    {
        if (request is null)
        {
            throw new ArgumentNullException(nameof(request));
        }

        if (systemCallProcessor is null)
        {
            return SystemCallResultFactory.Failure(
                SystemCallErrorCode.InternalError,
                "system call processor is not initialized.");
        }

        return systemCallProcessor.Execute(request);
    }

    /// <summary>Returns a default terminal execution context for UI bootstrap.</summary>
    public Godot.Collections.Dictionary GetDefaultTerminalContext(string preferredUserKey = "player")
    {
        var result = new Godot.Collections.Dictionary();
        if (PlayerWorkstationServer is null)
        {
            result["ok"] = false;
            result["error"] = "error: player workstation is not initialized.";
            return result;
        }

        var userKey = preferredUserKey;
        if (string.IsNullOrWhiteSpace(userKey) || !PlayerWorkstationServer.Users.ContainsKey(userKey))
        {
            userKey = PlayerWorkstationServer.Users.Keys
                .OrderBy(static key => key, StringComparer.Ordinal)
                .FirstOrDefault() ?? string.Empty;
        }

        if (string.IsNullOrWhiteSpace(userKey))
        {
            result["ok"] = false;
            result["error"] = "error: no available user on player workstation.";
            return result;
        }

        var promptUser = PlayerWorkstationServer.Users.TryGetValue(userKey, out var userConfig) &&
                         !string.IsNullOrWhiteSpace(userConfig.UserId)
            ? userConfig.UserId
            : userKey;
        var promptHost = string.IsNullOrWhiteSpace(PlayerWorkstationServer.Name)
            ? PlayerWorkstationServer.NodeId
            : PlayerWorkstationServer.Name;

        result["ok"] = true;
        result["nodeId"] = PlayerWorkstationServer.NodeId;
        result["userKey"] = userKey;
        result["cwd"] = "/";
        result["promptUser"] = promptUser;
        result["promptHost"] = promptHost;
        return result;
    }

    /// <summary>Executes one terminal command and returns a GDScript-friendly dictionary payload.</summary>
    public Godot.Collections.Dictionary ExecuteTerminalCommand(
        string nodeId,
        string userKey,
        string cwd,
        string commandLine)
    {
        var result = ExecuteSystemCall(new SystemCallRequest
        {
            NodeId = nodeId ?? string.Empty,
            UserKey = userKey ?? string.Empty,
            Cwd = cwd ?? "/",
            CommandLine = commandLine ?? string.Empty,
        });

        var lines = new Godot.Collections.Array<string>();
        foreach (var line in result.Lines)
        {
            lines.Add(line ?? string.Empty);
        }

        var response = new Godot.Collections.Dictionary
        {
            ["ok"] = result.Ok,
            ["code"] = result.Code.ToString(),
            ["lines"] = lines,
            ["nextCwd"] = result.NextCwd ?? string.Empty,
        };

        return response;
    }
}
