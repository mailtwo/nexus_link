using Godot;
using System;
using System.Collections.Generic;
using System.Linq;
using Uplink2.Runtime.Syscalls;
using Uplink2.Vfs;

#nullable enable

namespace Uplink2.Runtime;

public partial class WorldRuntime
{
    private const string DefaultTerminalSessionKey = "default";
    private const string MotdPath = "/etc/motd";

    private Dictionary<string, Stack<TerminalConnectionFrame>>? terminalConnectionFramesBySessionId =
        new(StringComparer.Ordinal);

    private int nextTerminalSessionSerial = 1;
    private int nextTerminalRemoteSessionId = 1;

    /// <summary>Initializes system-call modules and command dispatch processor.</summary>
    private void InitializeSystemCalls()
    {
        ISystemCallModule[] modules =
        {
            new VfsSystemCallModule(enableDebugCommands: DebugOption),
            new ConnectSystemCallModule(),
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

        var responsePayload = BuildTerminalCommandResponsePayload(result);
        var lines = new Godot.Collections.Array<string>();
        foreach (var line in (IReadOnlyList<string>)responsePayload["lines"])
        {
            lines.Add(line);
        }

        var response = new Godot.Collections.Dictionary
        {
            ["ok"] = (bool)responsePayload["ok"],
            ["code"] = (string)responsePayload["code"],
            ["lines"] = lines,
            ["nextCwd"] = (string)responsePayload["nextCwd"],
            ["nextNodeId"] = (string)responsePayload["nextNodeId"],
            ["nextUserId"] = (string)responsePayload["nextUserId"],
            ["nextPromptUser"] = (string)responsePayload["nextPromptUser"],
            ["nextPromptHost"] = (string)responsePayload["nextPromptHost"],
            ["openEditor"] = (bool)responsePayload["openEditor"],
            ["editorPath"] = (string)responsePayload["editorPath"],
            ["editorContent"] = (string)responsePayload["editorContent"],
            ["editorReadOnly"] = (bool)responsePayload["editorReadOnly"],
            ["editorDisplayMode"] = (string)responsePayload["editorDisplayMode"],
            ["editorPathExists"] = (bool)responsePayload["editorPathExists"],
        };

        return response;
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
        if (!TryCreateEditorSaveContext(nodeId, userId, cwd, out var server, out var user, out var normalizedCwd, out var contextFailure))
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
            ["code"] = result.Code.ToString(),
            ["lines"] = lines,
            ["nextCwd"] = result.NextCwd ?? string.Empty,
            ["nextNodeId"] = string.Empty,
            ["nextUserId"] = string.Empty,
            ["nextPromptUser"] = string.Empty,
            ["nextPromptHost"] = string.Empty,
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
        if (string.IsNullOrWhiteSpace((string)payload["nextCwd"]) &&
            !string.IsNullOrWhiteSpace(transition.NextCwd))
        {
            payload["nextCwd"] = transition.NextCwd;
        }

        return payload;
    }

    private static Godot.Collections.Dictionary BuildEditorSaveResponse(SystemCallResult result, string savedPath)
    {
        var lines = new Godot.Collections.Array<string>();
        foreach (var line in result.Lines)
        {
            lines.Add(line ?? string.Empty);
        }

        return new Godot.Collections.Dictionary
        {
            ["ok"] = result.Ok,
            ["code"] = result.Code.ToString(),
            ["lines"] = lines,
            ["savedPath"] = savedPath,
        };
    }

    private bool TryCreateEditorSaveContext(
        string nodeId,
        string userId,
        string cwd,
        out ServerNodeRuntime server,
        out UserConfig user,
        out string normalizedCwd,
        out SystemCallResult? failure)
    {
        server = null!;
        user = null!;
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

        if (!TryResolveUserByUserId(server, normalizedUserId, out _, out user))
        {
            failure = SystemCallResultFactory.Failure(SystemCallErrorCode.NotFound, $"user not found: {normalizedUserId}");
            return false;
        }

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

        if (!targetServer.Ports.TryGetValue(port, out var targetPort) ||
            targetPort.PortType == PortType.None)
        {
            failureResult = SystemCallResultFactory.Failure(SystemCallErrorCode.NotFound, $"port not available: {port}");
            return false;
        }

        if (targetPort.PortType != PortType.Ssh)
        {
            failureResult = SystemCallResultFactory.Failure(SystemCallErrorCode.InvalidArgs, $"port {port} is not ssh.");
            return false;
        }

        if (!IsPortExposureAllowed(sourceServer, targetServer, targetPort.Exposure))
        {
            failureResult = SystemCallResultFactory.Failure(SystemCallErrorCode.PermissionDenied, $"port exposure denied: {port}");
            return false;
        }

        if (!TryResolveUserByUserId(targetServer, userId, out var targetUserKey, out var targetUser))
        {
            failureResult = SystemCallResultFactory.Failure(SystemCallErrorCode.NotFound, $"user not found: {userId}");
            return false;
        }

        if (!IsAuthenticationSuccessful(targetUser, password, out failureResult))
        {
            return false;
        }

        var sessionId = AllocateTerminalRemoteSessionId();
        var remoteIp = ResolveRemoteIpForSession(sourceServer, targetServer);
        targetServer.UpsertSession(sessionId, new SessionConfig
        {
            UserKey = targetUserKey,
            RemoteIp = remoteIp,
            Cwd = "/",
        });

        EmitPrivilegeAcquireForLogin(targetServer.NodeId, targetUserKey, via);
        openResult = new SshSessionOpenResult
        {
            TargetServer = targetServer,
            TargetNodeId = targetServer.NodeId,
            TargetUserKey = targetUserKey,
            TargetUserId = ResolvePromptUser(targetServer, targetUserKey),
            SessionId = sessionId,
            RemoteIp = remoteIp,
            HostOrIp = normalizedHostOrIp,
        };
        return true;
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

        failureResult = SystemCallResultFactory.Failure(
            SystemCallErrorCode.PermissionDenied,
            $"authentication mode not supported: {targetUser.AuthMode}.");
        return false;
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
        terminalConnectionFramesBySessionId!.Clear();
        nextTerminalSessionSerial = 1;
        nextTerminalRemoteSessionId = 1;
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
}
