using Godot;
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Uplink2.Runtime.Events;
using Uplink2.Runtime.Syscalls;
using Uplink2.Vfs;

#nullable enable

namespace Uplink2.Runtime;

public partial class WorldRuntime
{
    private const string DefaultTerminalSessionKey = "default";
    private const string MotdPath = "/etc/motd";
    private const string OtpBase32Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
    private const string InspectNumberAlphabet = "0123456789";
    private const string InspectAlphabetOnlyAlphabet = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ";
    private const string InspectNumberAlphabetWithLetters = "0123456789abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ";
    private const string InspectUnknownAlphabet = "???";
    private const int OtpTokenDigits = 6;
    private const int InspectProbeRateLimitPerSecond = 100000;
    private const string ConnectionRateLimiterBlockedMessage =
        "connectionRateLimiter daemon blocked this connection attempt.";

    private Dictionary<string, Stack<TerminalConnectionFrame>>? terminalConnectionFramesBySessionId =
        new(StringComparer.Ordinal);
    private Dictionary<string, TerminalProgramExecutionState>? terminalProgramExecutionsBySessionId =
        new(StringComparer.Ordinal);
    private Dictionary<string, ConnectionRateLimiterState>? connectionRateLimiterStatesByNodeId =
        new(StringComparer.Ordinal);

    private int nextTerminalSessionSerial = 1;
    private int nextTerminalRemoteSessionId = 1;
    private long inspectProbeRateLimitWindowStartMs;
    private int inspectProbeRateLimitCallsInWindow;
    private readonly object terminalProgramExecutionSync = new();
    private object? connectionRateLimiterSync = new();
    private object? inspectProbeRateLimiterSync = new();

    /// <summary>Initializes system-call modules and command dispatch processor.</summary>
    private void InitializeSystemCalls()
    {
        var modules = new List<ISystemCallModule>
        {
            new VfsSystemCallModule(enableDebugCommands: DebugOption),
            new ConnectSystemCallModule(),
        };
        if (EnablePrototypeSaveLoadSystemCalls)
        {
            modules.Add(new PrototypeSaveLoadSystemCallModule());
        }

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
    public Godot.Collections.Dictionary GetDefaultTerminalContext(string preferredUserId = "player")
    {
        var result = new Godot.Collections.Dictionary();
        var motdLines = new Godot.Collections.Array<string>();
        result["motdLines"] = motdLines;
        if (PlayerWorkstationServer is null)
        {
            result["ok"] = false;
            result["error"] = "error: player workstation is not initialized.";
            return result;
        }

        var userId = preferredUserId?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(userId) ||
            !TryResolveUserKeyByUserId(PlayerWorkstationServer, userId, out var userKey))
        {
            userKey = PlayerWorkstationServer.Users.Keys
                .OrderBy(static key => key, StringComparer.Ordinal)
                .FirstOrDefault() ?? string.Empty;
            userId = ResolvePromptUser(PlayerWorkstationServer, userKey);
        }

        if (string.IsNullOrWhiteSpace(userId))
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
        var terminalSessionId = AllocateTerminalSessionId();
        GetOrCreateTerminalSessionStack(terminalSessionId).Clear();
        foreach (var line in ResolveMotdLinesForLogin(PlayerWorkstationServer, userKey))
        {
            motdLines.Add(line);
        }

        result["ok"] = true;
        result["nodeId"] = PlayerWorkstationServer.NodeId;
        result["userId"] = userId;
        result["cwd"] = "/";
        result["promptUser"] = promptUser;
        result["promptHost"] = promptHost;
        result["terminalSessionId"] = terminalSessionId;
        return result;
    }

    /// <summary>Executes one terminal command and returns a GDScript-friendly dictionary payload.</summary>
    public Godot.Collections.Dictionary ExecuteTerminalCommand(
        string nodeId,
        string userId,
        string cwd,
        string commandLine)
    {
        return ExecuteTerminalCommand(nodeId, userId, cwd, commandLine, string.Empty);
    }

    /// <summary>Executes one terminal command and returns a GDScript-friendly dictionary payload.</summary>
    public Godot.Collections.Dictionary ExecuteTerminalCommand(
        string nodeId,
        string userId,
        string cwd,
        string commandLine,
        string terminalSessionId)
    {
        var result = ExecuteSystemCall(new SystemCallRequest
        {
            NodeId = nodeId ?? string.Empty,
            UserId = userId ?? string.Empty,
            Cwd = cwd ?? "/",
            CommandLine = commandLine ?? string.Empty,
            TerminalSessionId = terminalSessionId ?? string.Empty,
        });

        return BuildGodotTerminalCommandResponse(result);
    }

    /// <summary>Returns terminal command completion candidates for current context.</summary>
    public Godot.Collections.Array<string> GetTerminalCommandCompletions(
        string nodeId,
        string userId,
        string cwd)
    {
        var completions = new Godot.Collections.Array<string>();
        foreach (var command in GetTerminalCommandCompletionsCore(nodeId, userId, cwd))
        {
            completions.Add(command);
        }

        return completions;
    }

    internal IReadOnlyList<string> GetTerminalCommandCompletionsCore(
        string nodeId,
        string userId,
        string cwd)
    {
        if (systemCallProcessor is null)
        {
            return Array.Empty<string>();
        }

        if (!TryCreateEditorSaveContext(
                nodeId,
                userId,
                cwd,
                out var server,
                out var user,
                out var userKey,
                out var normalizedCwd,
                out _))
        {
            return Array.Empty<string>();
        }

        var executionContext = new SystemCallExecutionContext(
            this,
            server,
            user,
            server.NodeId,
            userKey,
            normalizedCwd,
            string.Empty);
        return systemCallProcessor.ListCommandCompletions(executionContext);
    }

    /// <summary>Returns path completion child entries for a normalized directory context.</summary>
    public Godot.Collections.Dictionary GetTerminalPathCompletionEntries(
        string nodeId,
        string userId,
        string cwd,
        string directoryPath)
    {
        var coreResult = GetTerminalPathCompletionEntriesCore(nodeId, userId, cwd, directoryPath);
        var entries = new Godot.Collections.Array();
        var response = new Godot.Collections.Dictionary
        {
            ["ok"] = (bool)coreResult["ok"],
            ["normalizedDirectoryPath"] = (string)coreResult["normalizedDirectoryPath"],
            ["entries"] = entries,
            ["error"] = (string)coreResult["error"],
        };
        var coreEntries = (IReadOnlyList<Dictionary<string, object>>)coreResult["entries"];
        foreach (var coreEntry in coreEntries)
        {
            entries.Add(new Godot.Collections.Dictionary
            {
                ["name"] = (string)coreEntry["name"],
                ["isDirectory"] = (bool)coreEntry["isDirectory"],
            });
        }

        return response;
    }

    internal Dictionary<string, object> GetTerminalPathCompletionEntriesCore(
        string nodeId,
        string userId,
        string cwd,
        string directoryPath)
    {
        var entries = new List<Dictionary<string, object>>();
        var response = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["ok"] = false,
            ["normalizedDirectoryPath"] = string.Empty,
            ["entries"] = entries,
            ["error"] = string.Empty,
        };

        if (!TryCreateEditorSaveContext(
                nodeId,
                userId,
                cwd,
                out var server,
                out var user,
                out _,
                out var normalizedCwd,
                out var contextFailure))
        {
            response["error"] = ResolveCompletionFailureMessage(contextFailure, "failed to create completion context.");
            return response;
        }

        if (!user.Privilege.Read)
        {
            response["error"] = "permission denied: path completion.";
            return response;
        }

        var normalizedDirectoryPath = BaseFileSystem.NormalizePath(normalizedCwd, directoryPath ?? ".");
        response["normalizedDirectoryPath"] = normalizedDirectoryPath;
        if (!server.DiskOverlay.TryResolveEntry(normalizedDirectoryPath, out var directoryEntry))
        {
            response["error"] = $"no such file or directory: {normalizedDirectoryPath}";
            return response;
        }

        if (directoryEntry.EntryKind != VfsEntryKind.Dir)
        {
            response["error"] = $"not a directory: {normalizedDirectoryPath}";
            return response;
        }

        foreach (var childName in server.DiskOverlay.ListChildren(normalizedDirectoryPath))
        {
            var childPath = normalizedDirectoryPath == "/"
                ? "/" + childName
                : normalizedDirectoryPath + "/" + childName;
            var isDirectory = server.DiskOverlay.TryResolveEntry(childPath, out var childEntry) &&
                              childEntry.EntryKind == VfsEntryKind.Dir;
            entries.Add(new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["name"] = childName,
                ["isDirectory"] = isDirectory,
            });
        }

        response["ok"] = true;
        return response;
    }

    /// <summary>Attempts to start asynchronous user program execution for script-like commands.</summary>
    public Godot.Collections.Dictionary TryStartTerminalProgramExecution(
        string nodeId,
        string userId,
        string cwd,
        string commandLine,
        string terminalSessionId)
    {
        var core = TryStartTerminalProgramExecutionCore(nodeId, userId, cwd, commandLine, terminalSessionId);
        var response = new Godot.Collections.Dictionary
        {
            ["handled"] = core.Handled,
            ["started"] = core.Started,
            ["response"] = core.Handled
                ? BuildGodotTerminalCommandResponse(core.Response)
                : new Godot.Collections.Dictionary(),
        };
        return response;
    }

    /// <summary>Returns true while a terminal session has an active asynchronous user program.</summary>
    public bool IsTerminalProgramRunning(string terminalSessionId)
    {
        var normalizedSessionId = NormalizeTerminalSessionId(terminalSessionId);
        lock (terminalProgramExecutionSync)
        {
            EnsureTerminalProgramExecutionStorage();
            return terminalProgramExecutionsBySessionId!.TryGetValue(normalizedSessionId, out var state) &&
                   state.IsRunning;
        }
    }

    /// <summary>Requests interruption of an asynchronous terminal program via cooperative cancellation.</summary>
    public Godot.Collections.Dictionary InterruptTerminalProgramExecution(string terminalSessionId)
    {
        return BuildGodotTerminalCommandResponse(InterruptTerminalProgramExecutionCore(terminalSessionId));
    }

    internal TerminalProgramExecutionStartResult TryStartTerminalProgramExecutionCore(
        string nodeId,
        string userId,
        string cwd,
        string commandLine,
        string terminalSessionId)
    {
        if (systemCallProcessor is null)
        {
            return new TerminalProgramExecutionStartResult
            {
                Handled = true,
                Started = false,
                Response = SystemCallResultFactory.Failure(
                    SystemCallErrorCode.InternalError,
                    "system call processor is not initialized."),
            };
        }

        var request = new SystemCallRequest
        {
            NodeId = nodeId ?? string.Empty,
            UserId = userId ?? string.Empty,
            Cwd = cwd ?? "/",
            CommandLine = commandLine ?? string.Empty,
            TerminalSessionId = terminalSessionId ?? string.Empty,
        };

        if (!systemCallProcessor.TryPrepareTerminalProgramExecution(request, out var launch, out var immediateResult))
        {
            return new TerminalProgramExecutionStartResult
            {
                Handled = false,
                Started = false,
                Response = SystemCallResultFactory.Success(),
            };
        }

        if (launch is null)
        {
            return new TerminalProgramExecutionStartResult
            {
                Handled = true,
                Started = false,
                Response = immediateResult ?? SystemCallResultFactory.Failure(
                    SystemCallErrorCode.InternalError,
                    "failed to prepare program execution."),
            };
        }

        var normalizedSessionId = NormalizeTerminalSessionId(terminalSessionId);
        var state = new TerminalProgramExecutionState(normalizedSessionId, launch.Value);
        lock (terminalProgramExecutionSync)
        {
            EnsureTerminalProgramExecutionStorage();
            if (terminalProgramExecutionsBySessionId!.TryGetValue(normalizedSessionId, out var existingState) &&
                existingState.IsRunning)
            {
                return new TerminalProgramExecutionStartResult
                {
                    Handled = true,
                    Started = false,
                    Response = SystemCallResultFactory.Failure(SystemCallErrorCode.InvalidArgs, "program already running."),
                };
            }

            terminalProgramExecutionsBySessionId[normalizedSessionId] = state;
        }

        state.WorkerTask = Task.Run(() => RunTerminalProgramExecution(state));
        return new TerminalProgramExecutionStartResult
        {
            Handled = true,
            Started = true,
            Response = SystemCallResultFactory.Success(),
        };
    }

    internal SystemCallResult InterruptTerminalProgramExecutionCore(string terminalSessionId)
    {
        var normalizedSessionId = NormalizeTerminalSessionId(terminalSessionId);
        TerminalProgramExecutionState? state;
        lock (terminalProgramExecutionSync)
        {
            EnsureTerminalProgramExecutionStorage();
            if (!terminalProgramExecutionsBySessionId!.TryGetValue(normalizedSessionId, out state) ||
                !state.IsRunning)
            {
                return SystemCallResultFactory.Success();
            }

            state.CancelRequested = true;
            state.CancellationTokenSource.Cancel();
        }

        var stoppedWithinGrace = false;
        try
        {
            stoppedWithinGrace = state.WorkerTask.Wait(TimeSpan.FromMilliseconds(250));
        }
        catch (AggregateException)
        {
            stoppedWithinGrace = true;
        }

        if (stoppedWithinGrace)
        {
            return SystemCallResultFactory.Success(lines: new[] { "program killed by Ctrl+C" });
        }

        lock (terminalProgramExecutionSync)
        {
            state.IsRunning = false;
            state.SuppressOutput = true;
        }

        return SystemCallResultFactory.Success(lines: new[] { "program interrupt failed: unable to stop script thread." });
    }

    /// <summary>Saves editor buffer content to a server-local overlay file path.</summary>
    public Godot.Collections.Dictionary SaveEditorContent(
        string nodeId,
        string userId,
        string cwd,
        string path,
        string content)
    {
        var result = SaveEditorContentInternal(nodeId, userId, cwd, path, content, out var savedPath);
        return BuildEditorSaveResponse(result, savedPath);
    }

    internal SystemCallResult SaveEditorContentInternal(
        string nodeId,
        string userId,
        string cwd,
        string path,
        string content,
        out string savedPath)
    {
        savedPath = string.Empty;
        if (!TryCreateEditorSaveContext(
                nodeId,
                userId,
                cwd,
                out var server,
                out var user,
                out var userKey,
                out var normalizedCwd,
                out var contextFailure))
        {
            return contextFailure!;
        }

        var targetPath = BaseFileSystem.NormalizePath(normalizedCwd, path ?? string.Empty);
        var pathExists = server.DiskOverlay.TryResolveEntry(targetPath, out var entry);
        if (pathExists)
        {
            if (entry!.EntryKind != VfsEntryKind.File)
            {
                return SystemCallResultFactory.NotFile(targetPath);
            }

            if (entry.FileKind != VfsFileKind.Text)
            {
                return SystemCallResultFactory.Failure(SystemCallErrorCode.InvalidArgs, "read-only buffer.");
            }
        }

        if (!user.Privilege.Write)
        {
            return SystemCallResultFactory.PermissionDenied("edit");
        }

        var parentPath = BaseFileSystem.NormalizePath("/", GetEditorParentPath(targetPath));
        if (!server.DiskOverlay.TryResolveEntry(parentPath, out var parentEntry))
        {
            return SystemCallResultFactory.NotFound(parentPath);
        }

        if (parentEntry.EntryKind != VfsEntryKind.Dir)
        {
            return SystemCallResultFactory.NotDirectory(parentPath);
        }

        server.DiskOverlay.WriteFile(targetPath, content ?? string.Empty, cwd: "/", fileKind: VfsFileKind.Text);
        if (!pathExists)
        {
            var hasResolvedSavedEntry = server.DiskOverlay.TryResolveEntry(targetPath, out var savedEntry);
            EmitFileAcquire(
                fromNodeId: server.NodeId,
                userKey: userKey,
                fileName: targetPath,
                remotePath: null,
                localPath: targetPath,
                sizeBytes: hasResolvedSavedEntry ? ToOptionalInt(savedEntry.Size) : null,
                contentId: hasResolvedSavedEntry ? savedEntry.ContentId : null,
                transferMethod: "edit.save");
        }

        savedPath = targetPath;
        return SystemCallResultFactory.Success(lines: new[] { $"saved: {targetPath}" });
    }

    internal static Dictionary<string, object> BuildTerminalCommandResponsePayload(SystemCallResult result)
    {
        var lines = new List<string>();
        foreach (var line in result.Lines)
        {
            lines.Add(line ?? string.Empty);
        }

        var payload = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["ok"] = result.Ok,
            ["code"] = SystemCallErrorCodeTokenMapper.ToApiToken(result.Code),
            ["lines"] = lines,
            ["nextCwd"] = result.NextCwd ?? string.Empty,
            ["nextNodeId"] = string.Empty,
            ["nextUserId"] = string.Empty,
            ["nextPromptUser"] = string.Empty,
            ["nextPromptHost"] = string.Empty,
            ["clearTerminal"] = false,
            ["activateMotdAnchor"] = false,
            ["openEditor"] = false,
            ["editorPath"] = string.Empty,
            ["editorContent"] = string.Empty,
            ["editorReadOnly"] = false,
            ["editorDisplayMode"] = "text",
            ["editorPathExists"] = false,
        };

        if (result.Data is EditorOpenTransition editorOpen)
        {
            payload["openEditor"] = true;
            payload["editorPath"] = editorOpen.TargetPath;
            payload["editorContent"] = editorOpen.Content;
            payload["editorReadOnly"] = editorOpen.ReadOnly;
            payload["editorDisplayMode"] = editorOpen.DisplayMode;
            payload["editorPathExists"] = editorOpen.PathExists;
            return payload;
        }

        if (result.Data is not TerminalContextTransition transition)
        {
            return payload;
        }

        payload["nextNodeId"] = transition.NextNodeId;
        payload["nextUserId"] = transition.NextUserId;
        payload["nextPromptUser"] = transition.NextPromptUser;
        payload["nextPromptHost"] = transition.NextPromptHost;
        payload["clearTerminal"] = transition.ClearTerminalBeforeOutput;
        payload["activateMotdAnchor"] = transition.ActivateMotdAnchor;
        if (string.IsNullOrWhiteSpace((string)payload["nextCwd"]) &&
            !string.IsNullOrWhiteSpace(transition.NextCwd))
        {
            payload["nextCwd"] = transition.NextCwd;
        }

        return payload;
    }

    private static Godot.Collections.Dictionary BuildGodotTerminalCommandResponse(SystemCallResult result)
    {
        var responsePayload = BuildTerminalCommandResponsePayload(result);
        var lines = new Godot.Collections.Array<string>();
        foreach (var line in (IReadOnlyList<string>)responsePayload["lines"])
        {
            lines.Add(line);
        }

        return new Godot.Collections.Dictionary
        {
            ["ok"] = (bool)responsePayload["ok"],
            ["code"] = (string)responsePayload["code"],
            ["lines"] = lines,
            ["nextCwd"] = (string)responsePayload["nextCwd"],
            ["nextNodeId"] = (string)responsePayload["nextNodeId"],
            ["nextUserId"] = (string)responsePayload["nextUserId"],
            ["nextPromptUser"] = (string)responsePayload["nextPromptUser"],
            ["nextPromptHost"] = (string)responsePayload["nextPromptHost"],
            ["clearTerminal"] = (bool)responsePayload["clearTerminal"],
            ["activateMotdAnchor"] = (bool)responsePayload["activateMotdAnchor"],
            ["openEditor"] = (bool)responsePayload["openEditor"],
            ["editorPath"] = (string)responsePayload["editorPath"],
            ["editorContent"] = (string)responsePayload["editorContent"],
            ["editorReadOnly"] = (bool)responsePayload["editorReadOnly"],
            ["editorDisplayMode"] = (string)responsePayload["editorDisplayMode"],
            ["editorPathExists"] = (bool)responsePayload["editorPathExists"],
        };
    }

    internal static Dictionary<string, object> BuildEditorSaveResponsePayload(SystemCallResult result, string savedPath)
    {
        var lines = new List<string>();
        foreach (var line in result.Lines)
        {
            lines.Add(line ?? string.Empty);
        }

        return new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["ok"] = result.Ok,
            ["code"] = SystemCallErrorCodeTokenMapper.ToApiToken(result.Code),
            ["lines"] = lines,
            ["savedPath"] = savedPath ?? string.Empty,
        };
    }

    private static Godot.Collections.Dictionary BuildEditorSaveResponse(SystemCallResult result, string savedPath)
    {
        var responsePayload = BuildEditorSaveResponsePayload(result, savedPath);
        var lines = new Godot.Collections.Array<string>();
        foreach (var line in (IReadOnlyList<string>)responsePayload["lines"])
        {
            lines.Add(line);
        }

        return new Godot.Collections.Dictionary
        {
            ["ok"] = (bool)responsePayload["ok"],
            ["code"] = (string)responsePayload["code"],
            ["lines"] = lines,
            ["savedPath"] = (string)responsePayload["savedPath"],
        };
    }

    private void RunTerminalProgramExecution(TerminalProgramExecutionState state)
    {
        try
        {
            var options = new MiniScriptExecutionOptions
            {
                CancellationToken = state.CancellationTokenSource.Token,
                StandardOutputLineSink = line => TryQueueTerminalProgramOutputLine(state, line),
                StandardErrorLineSink = line => TryQueueTerminalProgramOutputLine(state, NormalizeMiniScriptErrorLine(line)),
                SshMode = MiniScriptSshExecutionMode.SandboxValidated,
                CaptureOutputLines = false,
                ScriptArguments = state.Launch.ScriptArguments,
            };

            var execution = MiniScriptExecutionRunner.ExecuteScriptWithOptions(
                state.Launch.ScriptSource,
                state.Launch.Context,
                options);

            if (!execution.WasCancelled)
            {
                foreach (var line in execution.Result.Lines)
                {
                    TryQueueTerminalProgramOutputLine(state, line);
                }
            }
        }
        catch (Exception ex)
        {
            TryQueueTerminalProgramOutputLine(state, "error: miniscript execution failed: " + ex.Message);
        }
        finally
        {
            lock (terminalProgramExecutionSync)
            {
                if (terminalProgramExecutionsBySessionId is not null &&
                    terminalProgramExecutionsBySessionId.TryGetValue(state.TerminalSessionId, out var registeredState) &&
                    ReferenceEquals(registeredState, state))
                {
                    terminalProgramExecutionsBySessionId.Remove(state.TerminalSessionId);
                }

                state.IsRunning = false;
            }

            state.CancellationTokenSource.Dispose();
        }
    }

    private void TryQueueTerminalProgramOutputLine(TerminalProgramExecutionState state, string line)
    {
        if (line is null)
        {
            return;
        }

        var shouldOutput = true;
        lock (terminalProgramExecutionSync)
        {
            if (state.SuppressOutput)
            {
                shouldOutput = false;
            }
        }

        if (!shouldOutput)
        {
            return;
        }

        QueueTerminalEventLine(new TerminalEventLine(state.NodeId, state.UserKey, line));
    }

    private static string NormalizeMiniScriptErrorLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return "error: miniscript execution failed.";
        }

        return line.StartsWith("warn:", StringComparison.OrdinalIgnoreCase) ||
               line.StartsWith("error:", StringComparison.OrdinalIgnoreCase)
            ? line
            : "error: " + line;
    }

    private static string ResolveCompletionFailureMessage(SystemCallResult? failure, string fallback)
    {
        if (failure is null)
        {
            return fallback;
        }

        foreach (var line in failure.Lines)
        {
            var message = line?.Trim() ?? string.Empty;
            if (message.Length > 0)
            {
                const string errorPrefix = "error: ";
                return message.StartsWith(errorPrefix, StringComparison.OrdinalIgnoreCase)
                    ? message[errorPrefix.Length..]
                    : message;
            }
        }

        return fallback;
    }

    private bool TryCreateEditorSaveContext(
        string nodeId,
        string userId,
        string cwd,
        out ServerNodeRuntime server,
        out UserConfig user,
        out string userKey,
        out string normalizedCwd,
        out SystemCallResult? failure)
    {
        server = null!;
        user = null!;
        userKey = string.Empty;
        normalizedCwd = "/";
        failure = null;

        var normalizedNodeId = nodeId?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(normalizedNodeId))
        {
            failure = SystemCallResultFactory.Failure(SystemCallErrorCode.InvalidArgs, "nodeId is required.");
            return false;
        }

        if (!TryGetServer(normalizedNodeId, out server))
        {
            failure = SystemCallResultFactory.Failure(SystemCallErrorCode.NotFound, $"server not found: {normalizedNodeId}");
            return false;
        }

        var normalizedUserId = userId?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(normalizedUserId))
        {
            failure = SystemCallResultFactory.Failure(SystemCallErrorCode.InvalidArgs, "userId is required.");
            return false;
        }

        if (!TryResolveUserByUserId(server, normalizedUserId, out var resolvedUserKey, out user))
        {
            failure = SystemCallResultFactory.Failure(SystemCallErrorCode.NotFound, $"user not found: {normalizedUserId}");
            return false;
        }

        userKey = resolvedUserKey;
        normalizedCwd = BaseFileSystem.NormalizePath("/", cwd);
        if (!server.DiskOverlay.TryResolveEntry(normalizedCwd, out var cwdEntry))
        {
            failure = SystemCallResultFactory.NotFound(normalizedCwd);
            return false;
        }

        if (cwdEntry.EntryKind != VfsEntryKind.Dir)
        {
            failure = SystemCallResultFactory.NotDirectory(normalizedCwd);
            return false;
        }

        return true;
    }

    private static int? ToOptionalInt(long value)
    {
        return value is < int.MinValue or > int.MaxValue
            ? null
            : (int)value;
    }

    private static string GetEditorParentPath(string normalizedPath)
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

    private void EnsureTerminalProgramExecutionStorage()
    {
        terminalProgramExecutionsBySessionId ??= new Dictionary<string, TerminalProgramExecutionState>(StringComparer.Ordinal);
    }

    internal string NormalizeTerminalSessionId(string? terminalSessionId)
    {
        var normalized = terminalSessionId?.Trim() ?? string.Empty;
        return string.IsNullOrWhiteSpace(normalized) ? DefaultTerminalSessionKey : normalized;
    }

    internal string AllocateTerminalSessionId()
    {
        EnsureTerminalConnectionStorage();

        string terminalSessionId;
        do
        {
            terminalSessionId = $"terminal-{nextTerminalSessionSerial++:D6}";
        }
        while (terminalConnectionFramesBySessionId!.ContainsKey(terminalSessionId));

        terminalConnectionFramesBySessionId[terminalSessionId] = new Stack<TerminalConnectionFrame>();
        return terminalSessionId;
    }

    internal int AllocateTerminalRemoteSessionId()
    {
        EnsureTerminalConnectionStorage();
        return nextTerminalRemoteSessionId++;
    }

    internal void PushTerminalConnectionFrame(
        string terminalSessionId,
        string previousNodeId,
        string previousUserKey,
        string previousCwd,
        string previousPromptUser,
        string previousPromptHost,
        string sessionNodeId,
        int sessionId)
    {
        var stack = GetOrCreateTerminalSessionStack(terminalSessionId);
        stack.Push(new TerminalConnectionFrame
        {
            PreviousNodeId = previousNodeId,
            PreviousUserKey = previousUserKey,
            PreviousCwd = previousCwd,
            PreviousPromptUser = previousPromptUser,
            PreviousPromptHost = previousPromptHost,
            SessionNodeId = sessionNodeId,
            SessionId = sessionId,
        });
    }

    internal bool TryPopTerminalConnectionFrame(string terminalSessionId, out TerminalConnectionFrame frame)
    {
        var stack = GetOrCreateTerminalSessionStack(terminalSessionId);
        if (stack.Count == 0)
        {
            frame = null!;
            return false;
        }

        frame = stack.Pop();
        return true;
    }

    internal bool TryGetTerminalConnectionTopFrame(string terminalSessionId, out TerminalConnectionFrame frame)
    {
        var stack = GetOrCreateTerminalSessionStack(terminalSessionId);
        if (stack.Count == 0)
        {
            frame = null!;
            return false;
        }

        frame = stack.Peek();
        return true;
    }

    internal bool TryGetTerminalConnectionOriginFrame(string terminalSessionId, out TerminalConnectionFrame frame)
    {
        var stack = GetOrCreateTerminalSessionStack(terminalSessionId);
        if (stack.Count == 0)
        {
            frame = null!;
            return false;
        }

        var stackSnapshot = stack.ToArray();
        frame = stackSnapshot[stackSnapshot.Length - 1];
        return true;
    }

    internal void CleanupTerminalSessionConnections(string terminalSessionId)
    {
        var normalizedSessionId = NormalizeTerminalSessionId(terminalSessionId);
        var stack = GetOrCreateTerminalSessionStack(normalizedSessionId);
        while (stack.Count > 0)
        {
            var frame = stack.Pop();
            _ = TryRemoveRemoteSession(frame.SessionNodeId, frame.SessionId);
        }

        terminalConnectionFramesBySessionId?.Remove(normalizedSessionId);
    }

    internal bool TryResolveActiveRemoteSessionAccount(
        string terminalSessionId,
        out ServerNodeRuntime sessionServer,
        out TerminalConnectionFrame topFrame,
        out SessionConfig session,
        out string sessionUserKey)
    {
        sessionServer = null!;
        topFrame = null!;
        session = null!;
        sessionUserKey = string.Empty;

        if (!TryGetTerminalConnectionTopFrame(terminalSessionId, out topFrame) ||
            !TryGetServer(topFrame.SessionNodeId, out sessionServer) ||
            !sessionServer.Sessions.TryGetValue(topFrame.SessionId, out session))
        {
            return false;
        }

        var normalizedUserKey = session.UserKey?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(normalizedUserKey))
        {
            return false;
        }

        sessionUserKey = normalizedUserKey;
        return true;
    }

    internal void RemoveTerminalRemoteSession(string nodeId, int sessionId)
    {
        _ = TryRemoveRemoteSession(nodeId, sessionId);
    }

    internal bool TryRemoveRemoteSession(string nodeId, int sessionId)
    {
        if (sessionId < 1 || string.IsNullOrWhiteSpace(nodeId))
        {
            return false;
        }

        if (!TryGetServer(nodeId, out var server))
        {
            return false;
        }

        if (!server.Sessions.ContainsKey(sessionId))
        {
            return false;
        }

        server.RemoveSession(sessionId);
        return true;
    }

    internal bool TryResolveRemoteSession(
        string nodeId,
        int sessionId,
        out ServerNodeRuntime server,
        out SessionConfig session)
    {
        server = null!;
        session = null!;
        if (sessionId < 1 || string.IsNullOrWhiteSpace(nodeId))
        {
            return false;
        }

        if (!TryGetServer(nodeId, out server) ||
            !server.Sessions.TryGetValue(sessionId, out session))
        {
            return false;
        }

        return true;
    }

    internal string ResolvePromptUser(ServerNodeRuntime server, string userKey)
    {
        if (server.Users.TryGetValue(userKey, out var userConfig) &&
            !string.IsNullOrWhiteSpace(userConfig.UserId))
        {
            return userConfig.UserId;
        }

        return userKey;
    }

    /// <summary>Resolves printable /etc/motd lines for a login target when readable text exists.</summary>
    internal IReadOnlyList<string> ResolveMotdLinesForLogin(ServerNodeRuntime server, string userKey)
    {
        if (server is null ||
            string.IsNullOrWhiteSpace(userKey) ||
            !server.Users.TryGetValue(userKey, out var user) ||
            !user.Privilege.Read)
        {
            return Array.Empty<string>();
        }

        if (!server.DiskOverlay.TryResolveEntry(MotdPath, out var entry) ||
            entry.EntryKind != VfsEntryKind.File ||
            entry.FileKind != VfsFileKind.Text)
        {
            return Array.Empty<string>();
        }

        if (!server.DiskOverlay.TryReadFileText(MotdPath, out var content) ||
            string.IsNullOrEmpty(content))
        {
            return Array.Empty<string>();
        }

        var normalizedContent = content
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n');
        return normalizedContent.Split('\n');
    }

    internal bool TryResolveUserKeyByUserId(ServerNodeRuntime server, string userId, out string userKey)
    {
        userKey = string.Empty;
        if (server is null)
        {
            return false;
        }

        var normalizedUserId = userId?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(normalizedUserId))
        {
            return false;
        }

        foreach (var userPair in server.Users)
        {
            if (!string.Equals(userPair.Value.UserId, normalizedUserId, StringComparison.Ordinal))
            {
                continue;
            }

            userKey = userPair.Key;
            return true;
        }

        return false;
    }

    internal bool TryResolveUserByUserId(
        ServerNodeRuntime server,
        string userId,
        out string userKey,
        out UserConfig user)
    {
        userKey = string.Empty;
        user = null!;
        if (!TryResolveUserKeyByUserId(server, userId, out var resolvedUserKey) ||
            !server.Users.TryGetValue(resolvedUserKey, out var resolvedUser))
        {
            return false;
        }

        userKey = resolvedUserKey;
        user = resolvedUser;
        return true;
    }

    internal bool TryOpenSshSession(
        ServerNodeRuntime sourceServer,
        string hostOrIp,
        string userId,
        string password,
        int port,
        string via,
        out SshSessionOpenResult openResult,
        out SystemCallResult failureResult)
    {
        openResult = null!;
        if (!TryValidateSshSessionOpen(
                sourceServer,
                hostOrIp,
                userId,
                password,
                port,
                out var validated,
                out failureResult))
        {
            return false;
        }

        var sessionId = AllocateTerminalRemoteSessionId();
        validated.TargetServer.UpsertSession(sessionId, new SessionConfig
        {
            UserKey = validated.TargetUserKey,
            RemoteIp = validated.RemoteIp,
            Cwd = "/",
        });

        EmitPrivilegeAcquireForLogin(validated.TargetNodeId, validated.TargetUserKey, via);
        openResult = new SshSessionOpenResult
        {
            TargetServer = validated.TargetServer,
            TargetNodeId = validated.TargetNodeId,
            TargetUserKey = validated.TargetUserKey,
            TargetUserId = validated.TargetUserId,
            SessionId = sessionId,
            RemoteIp = validated.RemoteIp,
            HostOrIp = validated.HostOrIp,
        };
        return true;
    }

    internal bool TryValidateSshSessionOpen(
        ServerNodeRuntime sourceServer,
        string hostOrIp,
        string userId,
        string password,
        int port,
        out SshSessionValidationResult validationResult,
        out SystemCallResult failureResult)
    {
        validationResult = null!;
        failureResult = SystemCallResultFactory.Success();

        var normalizedHostOrIp = hostOrIp?.Trim() ?? string.Empty;
        if (!TryResolveServerByHostOrIp(normalizedHostOrIp, out var targetServer))
        {
            failureResult = SystemCallResultFactory.Failure(SystemCallErrorCode.NotFound, $"host not found: {normalizedHostOrIp}");
            return false;
        }

        if (targetServer.Status != ServerStatus.Online)
        {
            failureResult = SystemCallResultFactory.Failure(SystemCallErrorCode.NotFound, $"server offline: {targetServer.NodeId}");
            return false;
        }

        if (!TryValidatePortAccess(sourceServer, targetServer, port, PortType.Ssh, out failureResult))
        {
            return false;
        }

        var remoteIp = ResolveRemoteIpForSession(sourceServer, targetServer);
        if (!IsConnectionRateLimiterAllowed(targetServer, remoteIp))
        {
            failureResult = SystemCallResultFactory.Failure(
                SystemCallErrorCode.PermissionDenied,
                ConnectionRateLimiterBlockedMessage);
            return false;
        }

        if (!TryResolveUserByUserId(targetServer, userId, out var targetUserKey, out var targetUser))
        {
            failureResult = SystemCallResultFactory.Failure(SystemCallErrorCode.NotFound, $"user not found: {userId}");
            return false;
        }

        if (!IsAuthenticationSuccessful(targetServer, targetUserKey, targetUser, password, out failureResult))
        {
            return false;
        }

        validationResult = new SshSessionValidationResult
        {
            TargetServer = targetServer,
            TargetNodeId = targetServer.NodeId,
            TargetUserKey = targetUserKey,
            TargetUserId = ResolvePromptUser(targetServer, targetUserKey),
            RemoteIp = remoteIp,
            HostOrIp = normalizedHostOrIp,
        };
        return true;
    }

    internal bool TryValidatePortAccess(
        ServerNodeRuntime sourceServer,
        ServerNodeRuntime targetServer,
        int port,
        PortType requiredPortType,
        out SystemCallResult failureResult)
    {
        failureResult = SystemCallResultFactory.Success();

        if (!targetServer.Ports.TryGetValue(port, out var targetPort) ||
            targetPort.PortType == PortType.None)
        {
            failureResult = SystemCallResultFactory.Failure(SystemCallErrorCode.NotFound, $"port not available: {port}");
            return false;
        }

        if (targetPort.PortType != requiredPortType)
        {
            failureResult = SystemCallResultFactory.Failure(
                SystemCallErrorCode.InvalidArgs,
                $"port {port} is not {GetPortTypeToken(requiredPortType)}.");
            return false;
        }

        if (!IsPortExposureAllowed(sourceServer, targetServer, targetPort.Exposure))
        {
            failureResult = SystemCallResultFactory.Failure(SystemCallErrorCode.PermissionDenied, $"port exposure denied: {port}");
            return false;
        }

        return true;
    }

    internal bool TryRunInspectProbe(
        ServerNodeRuntime sourceServer,
        string hostOrIp,
        string userId,
        int port,
        out InspectProbeResult inspectResult,
        out SystemCallResult failureResult)
    {
        inspectResult = null!;
        failureResult = SystemCallResultFactory.Success();

        var normalizedHostOrIp = hostOrIp?.Trim() ?? string.Empty;
        var normalizedUserId = userId?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(normalizedHostOrIp) ||
            string.IsNullOrWhiteSpace(normalizedUserId) ||
            port is < 1 or > 65535)
        {
            failureResult = SystemCallResultFactory.Failure(
                SystemCallErrorCode.InvalidArgs,
                ToInspectProbeHumanMessage(SystemCallErrorCode.InvalidArgs));
            return false;
        }

        if (!TryConsumeInspectProbeRateLimit())
        {
            failureResult = SystemCallResultFactory.Failure(
                SystemCallErrorCode.RateLimited,
                ToInspectProbeHumanMessage(SystemCallErrorCode.RateLimited));
            return false;
        }

        if (!TryResolveServerByHostOrIp(normalizedHostOrIp, out var targetServer) ||
            targetServer.Status != ServerStatus.Online)
        {
            failureResult = SystemCallResultFactory.Failure(
                SystemCallErrorCode.NotFound,
                ToInspectProbeHumanMessage(SystemCallErrorCode.NotFound));
            return false;
        }

        if (!targetServer.Ports.TryGetValue(port, out var targetPort) ||
            targetPort is null ||
            targetPort.PortType == PortType.None ||
            targetPort.PortType != PortType.Ssh)
        {
            failureResult = SystemCallResultFactory.Failure(
                SystemCallErrorCode.PortClosed,
                ToInspectProbeHumanMessage(SystemCallErrorCode.PortClosed));
            return false;
        }

        if (!IsPortExposureAllowed(sourceServer, targetServer, targetPort.Exposure))
        {
            failureResult = SystemCallResultFactory.Failure(
                SystemCallErrorCode.NetDenied,
                ToInspectProbeHumanMessage(SystemCallErrorCode.NetDenied));
            return false;
        }

        if (!TryResolveUserByUserId(targetServer, normalizedUserId, out var targetUserKey, out var targetUser) ||
            !TryBuildInspectPasswdInfo(targetServer, targetUserKey, targetUser, out var passwdInfo))
        {
            failureResult = SystemCallResultFactory.Failure(
                SystemCallErrorCode.AuthFailed,
                ToInspectProbeHumanMessage(SystemCallErrorCode.AuthFailed));
            return false;
        }

        var banner = (targetPort.ServiceId ?? string.Empty).Trim();
        inspectResult = new InspectProbeResult
        {
            HostOrIp = normalizedHostOrIp,
            Port = port,
            UserId = normalizedUserId,
            Banner = string.IsNullOrWhiteSpace(banner) ? null : banner,
            PasswdInfo = passwdInfo,
        };
        return true;
    }

    internal static string ToInspectProbeErrorCodeToken(SystemCallErrorCode code)
    {
        return SystemCallErrorCodeTokenMapper.ToApiToken(code);
    }

    internal static string ToInspectProbeHumanMessage(SystemCallErrorCode code)
    {
        return code switch
        {
            SystemCallErrorCode.InvalidArgs => "invalid args",
            SystemCallErrorCode.NotFound => "host not found",
            SystemCallErrorCode.PortClosed => "port is closed",
            SystemCallErrorCode.NetDenied => "network access denied",
            SystemCallErrorCode.AuthFailed => "authentication failed",
            SystemCallErrorCode.RateLimited => "rate limited",
            SystemCallErrorCode.PermissionDenied => "permission denied",
            SystemCallErrorCode.ToolMissing => "tool missing",
            _ => "internal error",
        };
    }

    private bool TryBuildInspectPasswdInfo(
        ServerNodeRuntime targetServer,
        string targetUserKey,
        UserConfig targetUser,
        out InspectPasswdInfo passwdInfo)
    {
        passwdInfo = new InspectPasswdInfo();
        if (targetUser.AuthMode == AuthMode.None)
        {
            passwdInfo = new InspectPasswdInfo
            {
                Kind = "none",
            };
            return true;
        }

        if (targetUser.AuthMode == AuthMode.Otp)
        {
            passwdInfo = new InspectPasswdInfo
            {
                Kind = "otp",
                Length = OtpTokenDigits,
                AlphabetId = "number",
                Alphabet = InspectNumberAlphabet,
            };
            return true;
        }

        if (targetUser.AuthMode != AuthMode.Static)
        {
            return false;
        }

        return TryBuildInspectStaticPasswdInfo(
            targetServer,
            targetUserKey,
            targetUser.UserPasswd,
            out passwdInfo);
    }

    private bool TryBuildInspectStaticPasswdInfo(
        ServerNodeRuntime targetServer,
        string targetUserKey,
        string? password,
        out InspectPasswdInfo passwdInfo)
    {
        var normalizedPassword = password ?? string.Empty;
        if (IsInspectAutoPolicyMatch(targetServer, targetUserKey, "dictionary", normalizedPassword) ||
            IsInspectAutoPolicyMatch(targetServer, targetUserKey, "dictionaryHard", normalizedPassword))
        {
            passwdInfo = new InspectPasswdInfo
            {
                Kind = "dictionary",
            };
            return true;
        }

        var length = normalizedPassword.Length;
        var numSpecialPolicy = $"c{length}_numspecial";
        if (IsInspectAutoPolicyMatch(targetServer, targetUserKey, numSpecialPolicy, normalizedPassword))
        {
            passwdInfo = CreateInspectPolicyInfo(length, "numspecial", NumSpecialAlphabet);
            return true;
        }

        var base64Policy = $"c{length}_base64";
        if (IsInspectAutoPolicyMatch(targetServer, targetUserKey, base64Policy, normalizedPassword))
        {
            passwdInfo = CreateInspectPolicyInfo(length, "base64", Base64Alphabet);
            return true;
        }

        ClassifyNonAutoStaticAlphabet(normalizedPassword, out var alphabetId, out var alphabet);
        passwdInfo = CreateInspectPolicyInfo(length, alphabetId, alphabet);
        return true;
    }

    private bool IsInspectAutoPolicyMatch(
        ServerNodeRuntime targetServer,
        string targetUserKey,
        string policy,
        string expectedPassword)
    {
        try
        {
            var resolvedPassword = ResolvePassword(
                "AUTO:" + policy,
                worldSeed,
                targetServer.NodeId,
                targetUserKey);
            return string.Equals(resolvedPassword, expectedPassword, StringComparison.Ordinal);
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static void ClassifyNonAutoStaticAlphabet(
        string password,
        out string alphabetId,
        out string alphabet)
    {
        var hasDigits = false;
        var hasLetters = false;
        foreach (var ch in password)
        {
            if (char.IsDigit(ch))
            {
                hasDigits = true;
                continue;
            }

            if (IsAsciiLetter(ch))
            {
                hasLetters = true;
                continue;
            }

            alphabetId = "unknown";
            alphabet = InspectUnknownAlphabet;
            return;
        }

        if (hasDigits && !hasLetters)
        {
            alphabetId = "number";
            alphabet = InspectNumberAlphabet;
            return;
        }

        if (!hasDigits && hasLetters)
        {
            alphabetId = "alphabet";
            alphabet = InspectAlphabetOnlyAlphabet;
            return;
        }

        if (hasDigits && hasLetters)
        {
            alphabetId = "numberalphabet";
            alphabet = InspectNumberAlphabetWithLetters;
            return;
        }

        alphabetId = "unknown";
        alphabet = InspectUnknownAlphabet;
    }

    private static bool IsAsciiLetter(char ch)
    {
        return (ch is >= 'a' and <= 'z') || (ch is >= 'A' and <= 'Z');
    }

    private static InspectPasswdInfo CreateInspectPolicyInfo(
        int length,
        string alphabetId,
        string alphabet)
    {
        return new InspectPasswdInfo
        {
            Kind = "policy",
            Length = length,
            AlphabetId = alphabetId,
            Alphabet = alphabet,
        };
    }

    private static string GetPortTypeToken(PortType portType)
    {
        return portType switch
        {
            PortType.Ssh => "ssh",
            PortType.Ftp => "ftp",
            PortType.Http => "http",
            PortType.Sql => "sql",
            _ => portType.ToString().ToLowerInvariant(),
        };
    }

    private static bool IsPortExposureAllowed(ServerNodeRuntime source, ServerNodeRuntime target, PortExposure exposure)
    {
        return exposure switch
        {
            PortExposure.Public => true,
            PortExposure.Lan => source.SubnetMembership.Overlaps(target.SubnetMembership),
            PortExposure.Localhost => string.Equals(source.NodeId, target.NodeId, StringComparison.Ordinal),
            _ => false,
        };
    }

    private static bool IsAuthenticationSuccessful(
        ServerNodeRuntime targetServer,
        string targetUserKey,
        UserConfig targetUser,
        string password,
        out SystemCallResult failureResult)
    {
        failureResult = SystemCallResultFactory.Success();

        if (targetUser.AuthMode == AuthMode.None)
        {
            return true;
        }

        if (targetUser.AuthMode == AuthMode.Static)
        {
            if (string.Equals(targetUser.UserPasswd, password, StringComparison.Ordinal))
            {
                return true;
            }

            failureResult = SystemCallResultFactory.Failure(SystemCallErrorCode.PermissionDenied, "Permission denied, please try again.");
            return false;
        }

        if (targetUser.AuthMode == AuthMode.Otp)
        {
            if (IsOtpAuthenticationSuccessful(targetServer, targetUserKey, password))
            {
                return true;
            }

            failureResult = SystemCallResultFactory.Failure(SystemCallErrorCode.PermissionDenied, "Permission denied, please try again.");
            return false;
        }

        failureResult = SystemCallResultFactory.Failure(
            SystemCallErrorCode.PermissionDenied,
            $"authentication mode not supported: {targetUser.AuthMode}.");
        return false;
    }

    private static bool IsOtpAuthenticationSuccessful(
        ServerNodeRuntime targetServer,
        string targetUserKey,
        string password)
    {
        if (!TryResolveOtpDaemonSettings(
                targetServer,
                targetUserKey,
                out var otpPairId,
                out var stepMs,
                out var allowedDriftSteps))
        {
            return false;
        }

        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        for (var driftStep = -allowedDriftSteps; driftStep <= allowedDriftSteps; driftStep++)
        {
            var candidateNowMs = nowMs + (stepMs * driftStep);
            var candidateCode = GenerateTotpCode(otpPairId, candidateNowMs, stepMs, OtpTokenDigits);
            if (string.Equals(candidateCode, password, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private bool IsConnectionRateLimiterAllowed(ServerNodeRuntime targetServer, string sourceIp)
    {
        if (!TryResolveConnectionRateLimiterSettings(targetServer, out var settings))
        {
            return true;
        }

        var normalizedSourceIp = string.IsNullOrWhiteSpace(sourceIp)
            ? "127.0.0.1"
            : sourceIp.Trim();
        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        EnsureConnectionRateLimiterStorage();
        var limiterSync = GetConnectionRateLimiterSync();
        lock (limiterSync)
        {
            var state = GetOrCreateConnectionRateLimiterState(targetServer.NodeId);
            return TryConsumeConnectionRateLimiterAttempt(state, normalizedSourceIp, nowMs, settings);
        }
    }

    private bool TryConsumeInspectProbeRateLimit()
    {
        EnsureInspectProbeRateLimiterStorage();
        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var limiterSync = GetInspectProbeRateLimiterSync();
        lock (limiterSync)
        {
            if (inspectProbeRateLimitWindowStartMs <= 0 ||
                nowMs < inspectProbeRateLimitWindowStartMs ||
                nowMs - inspectProbeRateLimitWindowStartMs >= 1000)
            {
                inspectProbeRateLimitWindowStartMs = nowMs;
                inspectProbeRateLimitCallsInWindow = 0;
            }

            if (inspectProbeRateLimitCallsInWindow >= InspectProbeRateLimitPerSecond)
            {
                return false;
            }

            inspectProbeRateLimitCallsInWindow++;
            return true;
        }
    }

    internal void ResetInspectProbeRateLimitState()
    {
        EnsureInspectProbeRateLimiterStorage();
        var limiterSync = GetInspectProbeRateLimiterSync();
        lock (limiterSync)
        {
            inspectProbeRateLimitWindowStartMs = 0;
            inspectProbeRateLimitCallsInWindow = 0;
        }
    }

    private static bool TryResolveConnectionRateLimiterSettings(
        ServerNodeRuntime targetServer,
        out ConnectionRateLimiterSettings settings)
    {
        settings = default;
        if (!targetServer.Daemons.TryGetValue(DaemonType.ConnectionRateLimiter, out var rateLimiterDaemon))
        {
            return false;
        }

        if (!TryReadDaemonPositiveLongArg(rateLimiterDaemon, "monitorMs", out var monitorMs) ||
            !TryReadDaemonPositiveIntArg(rateLimiterDaemon, "threshold", out var threshold) ||
            !TryReadDaemonPositiveLongArg(rateLimiterDaemon, "blockMs", out var blockMs) ||
            !TryReadDaemonPositiveIntArg(rateLimiterDaemon, "rateLimit", out var rateLimit) ||
            !TryReadDaemonPositiveLongArg(rateLimiterDaemon, "recoveryMs", out var recoveryMs))
        {
            return false;
        }

        settings = new ConnectionRateLimiterSettings(monitorMs, threshold, blockMs, rateLimit, recoveryMs);
        return true;
    }

    private static bool TryConsumeConnectionRateLimiterAttempt(
        ConnectionRateLimiterState state,
        string sourceIp,
        long nowMs,
        ConnectionRateLimiterSettings settings)
    {
        if (nowMs < state.OverloadedUntilMs)
        {
            return true;
        }

        if (state.BlockedUntilByIp.TryGetValue(sourceIp, out var blockedUntilMs))
        {
            if (blockedUntilMs > nowMs)
            {
                return false;
            }

            state.BlockedUntilByIp.Remove(sourceIp);
        }

        if (!state.RecentAttemptsByIp.TryGetValue(sourceIp, out var recentAttempts))
        {
            recentAttempts = new Queue<long>();
            state.RecentAttemptsByIp[sourceIp] = recentAttempts;
        }

        var attemptWindowStartMs = nowMs - settings.MonitorMs;
        while (recentAttempts.Count > 0 &&
               recentAttempts.Peek() < attemptWindowStartMs)
        {
            recentAttempts.Dequeue();
        }

        if (recentAttempts.Count + 1 > settings.Threshold)
        {
            state.BlockedUntilByIp[sourceIp] = AddDurationMs(nowMs, settings.BlockMs);
            return false;
        }

        if (state.RateWindowStartMs <= 0 ||
            nowMs < state.RateWindowStartMs ||
            nowMs - state.RateWindowStartMs >= 1000)
        {
            state.RateWindowStartMs = nowMs;
            state.RateCheckedInWindow = 0;
        }

        if (state.RateCheckedInWindow >= settings.RateLimit)
        {
            state.OverloadedUntilMs = AddDurationMs(nowMs, settings.RecoveryMs);
            return true;
        }

        state.RateCheckedInWindow++;
        recentAttempts.Enqueue(nowMs);
        return true;
    }

    private static long AddDurationMs(long startMs, long durationMs)
    {
        if (durationMs <= 0)
        {
            return startMs;
        }

        return startMs > long.MaxValue - durationMs
            ? long.MaxValue
            : startMs + durationMs;
    }

    private static bool TryResolveOtpDaemonSettings(
        ServerNodeRuntime targetServer,
        string targetUserKey,
        out string otpPairId,
        out long stepMs,
        out int allowedDriftSteps)
    {
        otpPairId = string.Empty;
        stepMs = 0;
        allowedDriftSteps = 0;

        if (!targetServer.Daemons.TryGetValue(DaemonType.Otp, out var otpDaemon))
        {
            return false;
        }

        if (!TryReadDaemonStringArg(otpDaemon, "userKey", out var daemonUserKey) ||
            !string.Equals(daemonUserKey, targetUserKey, StringComparison.Ordinal))
        {
            return false;
        }

        if (!TryReadDaemonPositiveLongArg(otpDaemon, "stepMs", out stepMs))
        {
            return false;
        }

        if (!TryReadDaemonNonNegativeIntArg(otpDaemon, "allowedDriftSteps", out allowedDriftSteps))
        {
            return false;
        }

        if (!TryReadDaemonStringArg(otpDaemon, "otpPairId", out otpPairId))
        {
            return false;
        }

        return true;
    }

    private static bool TryReadDaemonStringArg(DaemonStruct daemon, string key, out string value)
    {
        value = string.Empty;
        if (!daemon.DaemonArgs.TryGetValue(key, out var rawValue) ||
            rawValue is not string text)
        {
            return false;
        }

        value = text.Trim();
        return !string.IsNullOrWhiteSpace(value);
    }

    private static bool TryReadDaemonPositiveLongArg(DaemonStruct daemon, string key, out long value)
    {
        value = 0;
        if (!daemon.DaemonArgs.TryGetValue(key, out var rawValue) ||
            !TryConvertObjectToLong(rawValue, out value) ||
            value <= 0)
        {
            return false;
        }

        return true;
    }

    private static bool TryReadDaemonPositiveIntArg(DaemonStruct daemon, string key, out int value)
    {
        value = 0;
        if (!daemon.DaemonArgs.TryGetValue(key, out var rawValue) ||
            !TryConvertObjectToLong(rawValue, out var parsedValue) ||
            parsedValue < 1 ||
            parsedValue > int.MaxValue)
        {
            return false;
        }

        value = (int)parsedValue;
        return true;
    }

    private static bool TryReadDaemonNonNegativeIntArg(DaemonStruct daemon, string key, out int value)
    {
        value = 0;
        if (!daemon.DaemonArgs.TryGetValue(key, out var rawValue) ||
            !TryConvertObjectToLong(rawValue, out var parsedValue) ||
            parsedValue < 0 ||
            parsedValue > int.MaxValue)
        {
            return false;
        }

        value = (int)parsedValue;
        return true;
    }

    private static bool TryConvertObjectToLong(object? rawValue, out long value)
    {
        value = 0;
        switch (rawValue)
        {
            case null:
            case bool:
                return false;
            case byte byteValue:
                value = byteValue;
                return true;
            case sbyte sbyteValue:
                value = sbyteValue;
                return true;
            case short shortValue:
                value = shortValue;
                return true;
            case ushort ushortValue:
                value = ushortValue;
                return true;
            case int intValue:
                value = intValue;
                return true;
            case uint uintValue:
                value = uintValue;
                return true;
            case long longValue:
                value = longValue;
                return true;
            case ulong ulongValue when ulongValue <= long.MaxValue:
                value = (long)ulongValue;
                return true;
            case float floatValue when IsWholeNumber(floatValue) &&
                                        floatValue >= long.MinValue &&
                                        floatValue <= long.MaxValue:
                value = (long)floatValue;
                return true;
            case double doubleValue when IsWholeNumber(doubleValue) &&
                                          doubleValue >= long.MinValue &&
                                          doubleValue <= long.MaxValue:
                value = (long)doubleValue;
                return true;
            case decimal decimalValue when decimalValue == decimal.Truncate(decimalValue) &&
                                           decimalValue >= long.MinValue &&
                                           decimalValue <= long.MaxValue:
                value = (long)decimalValue;
                return true;
            case string stringValue:
                return long.TryParse(
                    stringValue.Trim(),
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out value);
            default:
                return false;
        }
    }

    private static bool IsWholeNumber(float value)
    {
        return !float.IsNaN(value) &&
               !float.IsInfinity(value) &&
               value == MathF.Truncate(value);
    }

    private static bool IsWholeNumber(double value)
    {
        return !double.IsNaN(value) &&
               !double.IsInfinity(value) &&
               value == Math.Truncate(value);
    }

    private static string GenerateTotpCode(string otpPairId, long nowMs, long stepMs, int digits)
    {
        var secretBytes = DecodeBase32Secret(otpPairId);
        if (secretBytes.Length == 0 || digits is < 1 or > 10)
        {
            return string.Empty;
        }

        var counter = (long)Math.Floor(nowMs / (double)stepMs);
        Span<byte> counterBytes = stackalloc byte[8];
        BinaryPrimitives.WriteInt64BigEndian(counterBytes, counter);

        byte[] hash;
        using (var hmac = new HMACSHA1(secretBytes))
        {
            hash = hmac.ComputeHash(counterBytes.ToArray());
        }

        var offset = hash[^1] & 0x0F;
        var binaryCode = ((hash[offset] & 0x7F) << 24) |
                         ((hash[offset + 1] & 0xFF) << 16) |
                         ((hash[offset + 2] & 0xFF) << 8) |
                         (hash[offset + 3] & 0xFF);

        long modulus = 1;
        for (var index = 0; index < digits; index++)
        {
            modulus *= 10;
        }

        var otp = binaryCode % modulus;
        return otp.ToString(CultureInfo.InvariantCulture).PadLeft(digits, '0');
    }

    private static byte[] DecodeBase32Secret(string value)
    {
        var normalized = value
            .Trim()
            .Replace(" ", string.Empty, StringComparison.Ordinal)
            .Replace("-", string.Empty, StringComparison.Ordinal)
            .TrimEnd('=')
            .ToUpperInvariant();
        if (normalized.Length == 0)
        {
            return Array.Empty<byte>();
        }

        var bytes = new List<byte>(normalized.Length * 5 / 8 + 1);
        var bitBuffer = 0;
        var bitsInBuffer = 0;
        foreach (var ch in normalized)
        {
            var charIndex = OtpBase32Alphabet.IndexOf(ch);
            if (charIndex < 0)
            {
                return Array.Empty<byte>();
            }

            bitBuffer = (bitBuffer << 5) | charIndex;
            bitsInBuffer += 5;
            while (bitsInBuffer >= 8)
            {
                bitsInBuffer -= 8;
                bytes.Add((byte)((bitBuffer >> bitsInBuffer) & 0xFF));
            }
        }

        return bytes.ToArray();
    }

    internal void EmitPrivilegeAcquireForLogin(string nodeId, string userKey, string via)
    {
        if (!TryGetServer(nodeId, out var server) ||
            !server.Users.TryGetValue(userKey, out var user))
        {
            return;
        }

        if (user.Privilege.Read)
        {
            EmitPrivilegeAcquire(nodeId, userKey, "read", via: via, emitWhenAlreadyGranted: true);
        }

        if (user.Privilege.Write)
        {
            EmitPrivilegeAcquire(nodeId, userKey, "write", via: via, emitWhenAlreadyGranted: true);
        }

        if (user.Privilege.Execute)
        {
            EmitPrivilegeAcquire(nodeId, userKey, "execute", via: via, emitWhenAlreadyGranted: true);
        }
    }

    internal string ResolvePromptHost(ServerNodeRuntime server)
    {
        return string.IsNullOrWhiteSpace(server.Name)
            ? server.NodeId
            : server.Name;
    }

    internal string ResolveRemoteIpForSession(ServerNodeRuntime source, ServerNodeRuntime target)
    {
        if (string.Equals(source.NodeId, target.NodeId, StringComparison.Ordinal))
        {
            return "127.0.0.1";
        }

        foreach (var iface in source.Interfaces)
        {
            if (target.SubnetMembership.Contains(iface.NetId) && !string.IsNullOrWhiteSpace(iface.Ip))
            {
                return iface.Ip;
            }
        }

        if (!string.IsNullOrWhiteSpace(source.PrimaryIp))
        {
            return source.PrimaryIp;
        }

        foreach (var iface in source.Interfaces)
        {
            if (!string.IsNullOrWhiteSpace(iface.Ip))
            {
                return iface.Ip;
            }
        }

        return "127.0.0.1";
    }

    internal bool TryResolveServerByHostOrIp(string hostOrIp, out ServerNodeRuntime server)
    {
        server = null!;
        var normalizedHost = hostOrIp?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(normalizedHost))
        {
            return false;
        }

        if (TryGetServerByIp(normalizedHost, out server))
        {
            return true;
        }

        if (TryGetServer(normalizedHost, out server))
        {
            return true;
        }

        if (ServerList is null || ServerList.Count == 0)
        {
            return false;
        }

        server = ServerList.Values
            .Where(value => string.Equals(value.Name, normalizedHost, StringComparison.OrdinalIgnoreCase))
            .OrderBy(value => value.NodeId, StringComparer.Ordinal)
            .FirstOrDefault()!;
        return server is not null;
    }

    internal void ResetTerminalSessionState()
    {
        EnsureTerminalConnectionStorage();
        ResetTerminalProgramExecutionState();
        terminalConnectionFramesBySessionId!.Clear();
        nextTerminalSessionSerial = 1;
        nextTerminalRemoteSessionId = 1;
    }

    internal void ResetConnectionRateLimiterState()
    {
        EnsureConnectionRateLimiterStorage();
        var limiterSync = GetConnectionRateLimiterSync();
        lock (limiterSync)
        {
            connectionRateLimiterStatesByNodeId!.Clear();
        }
    }

    private void ResetTerminalProgramExecutionState()
    {
        List<TerminalProgramExecutionState> activeStates;
        lock (terminalProgramExecutionSync)
        {
            EnsureTerminalProgramExecutionStorage();
            activeStates = terminalProgramExecutionsBySessionId!.Values.ToList();
            terminalProgramExecutionsBySessionId.Clear();
        }

        foreach (var state in activeStates)
        {
            state.CancellationTokenSource.Cancel();
        }
    }

    private ConnectionRateLimiterState GetOrCreateConnectionRateLimiterState(string nodeId)
    {
        EnsureConnectionRateLimiterStorage();
        if (!connectionRateLimiterStatesByNodeId!.TryGetValue(nodeId, out var state))
        {
            state = new ConnectionRateLimiterState();
            connectionRateLimiterStatesByNodeId[nodeId] = state;
        }

        return state;
    }

    private object GetConnectionRateLimiterSync()
    {
        var sync = connectionRateLimiterSync;
        if (sync is not null)
        {
            return sync;
        }

        var created = new object();
        var existing = Interlocked.CompareExchange(ref connectionRateLimiterSync, created, null);
        return existing ?? created;
    }

    private object GetInspectProbeRateLimiterSync()
    {
        var sync = inspectProbeRateLimiterSync;
        if (sync is not null)
        {
            return sync;
        }

        var created = new object();
        var existing = Interlocked.CompareExchange(ref inspectProbeRateLimiterSync, created, null);
        return existing ?? created;
    }

    private void EnsureConnectionRateLimiterStorage()
    {
        connectionRateLimiterStatesByNodeId ??= new Dictionary<string, ConnectionRateLimiterState>(StringComparer.Ordinal);
        _ = GetConnectionRateLimiterSync();
    }

    private void EnsureInspectProbeRateLimiterStorage()
    {
        _ = GetInspectProbeRateLimiterSync();
    }

    private void EnsureTerminalConnectionStorage()
    {
        terminalConnectionFramesBySessionId ??= new Dictionary<string, Stack<TerminalConnectionFrame>>(StringComparer.Ordinal);
        if (nextTerminalSessionSerial < 1)
        {
            nextTerminalSessionSerial = 1;
        }

        if (nextTerminalRemoteSessionId < 1)
        {
            nextTerminalRemoteSessionId = 1;
        }
    }

    private Stack<TerminalConnectionFrame> GetOrCreateTerminalSessionStack(string terminalSessionId)
    {
        EnsureTerminalConnectionStorage();
        var normalizedSessionId = NormalizeTerminalSessionId(terminalSessionId);
        if (!terminalConnectionFramesBySessionId!.TryGetValue(normalizedSessionId, out var stack))
        {
            stack = new Stack<TerminalConnectionFrame>();
            terminalConnectionFramesBySessionId[normalizedSessionId] = stack;
        }

        return stack;
    }

    private sealed class TerminalProgramExecutionState
    {
        internal TerminalProgramExecutionState(string terminalSessionId, MiniScriptProgramLaunch launch)
        {
            TerminalSessionId = terminalSessionId;
            Launch = launch;
            NodeId = launch.Context.NodeId;
            UserKey = launch.Context.UserKey;
        }

        internal string TerminalSessionId { get; }

        internal MiniScriptProgramLaunch Launch { get; }

        internal string NodeId { get; }

        internal string UserKey { get; }

        internal CancellationTokenSource CancellationTokenSource { get; } = new();

        internal Task WorkerTask { get; set; } = Task.CompletedTask;

        internal bool IsRunning { get; set; } = true;

        internal bool CancelRequested { get; set; }

        internal bool SuppressOutput { get; set; }
    }

    internal sealed class TerminalProgramExecutionStartResult
    {
        internal bool Handled { get; init; }

        internal bool Started { get; init; }

        internal SystemCallResult Response { get; init; } = SystemCallResultFactory.Success();
    }

    internal sealed class TerminalConnectionFrame
    {
        internal string PreviousNodeId { get; init; } = string.Empty;

        internal string PreviousUserKey { get; init; } = string.Empty;

        internal string PreviousCwd { get; init; } = "/";

        internal string PreviousPromptUser { get; init; } = string.Empty;

        internal string PreviousPromptHost { get; init; } = string.Empty;

        internal string SessionNodeId { get; init; } = string.Empty;

        internal int SessionId { get; init; }
    }

    internal sealed class SshSessionOpenResult
    {
        internal ServerNodeRuntime TargetServer { get; init; } = null!;

        internal string TargetNodeId { get; init; } = string.Empty;

        internal string TargetUserKey { get; init; } = string.Empty;

        internal string TargetUserId { get; init; } = string.Empty;

        internal int SessionId { get; init; }

        internal string RemoteIp { get; init; } = string.Empty;

        internal string HostOrIp { get; init; } = string.Empty;
    }

    internal sealed class SshSessionValidationResult
    {
        internal ServerNodeRuntime TargetServer { get; init; } = null!;

        internal string TargetNodeId { get; init; } = string.Empty;

        internal string TargetUserKey { get; init; } = string.Empty;

        internal string TargetUserId { get; init; } = string.Empty;

        internal string RemoteIp { get; init; } = string.Empty;

        internal string HostOrIp { get; init; } = string.Empty;
    }

    internal sealed class InspectProbeResult
    {
        internal string HostOrIp { get; init; } = string.Empty;

        internal int Port { get; init; }

        internal string UserId { get; init; } = string.Empty;

        internal string? Banner { get; init; }

        internal InspectPasswdInfo PasswdInfo { get; init; } = new();
    }

    internal sealed class InspectPasswdInfo
    {
        internal string Kind { get; init; } = "none";

        internal int? Length { get; init; }

        internal string? AlphabetId { get; init; }

        internal string? Alphabet { get; init; }
    }

    private readonly record struct ConnectionRateLimiterSettings(
        long MonitorMs,
        int Threshold,
        long BlockMs,
        int RateLimit,
        long RecoveryMs);

    private sealed class ConnectionRateLimiterState
    {
        internal Dictionary<string, long> BlockedUntilByIp { get; } = new(StringComparer.Ordinal);

        internal Dictionary<string, Queue<long>> RecentAttemptsByIp { get; } = new(StringComparer.Ordinal);

        internal long RateWindowStartMs { get; set; }

        internal int RateCheckedInWindow { get; set; }

        internal long OverloadedUntilMs { get; set; }
    }
}
