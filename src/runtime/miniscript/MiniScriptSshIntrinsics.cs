using Miniscript;
using System;
using System.Collections.Generic;
using Uplink2.Runtime.Syscalls;
using Uplink2.Vfs;

#nullable enable

namespace Uplink2.Runtime.MiniScript;

/// <summary>Registers and injects project-specific SSH intrinsics into MiniScript interpreters.</summary>
internal static class MiniScriptSshIntrinsics
{
    private const string SshConnectIntrinsicName = "uplink_ssh_connect";
    private const string SshDisconnectIntrinsicName = "uplink_ssh_disconnect";
    private const string FtpGetIntrinsicName = "uplink_ftp_get";
    private const string FtpPutIntrinsicName = "uplink_ftp_put";
    private const int DefaultFtpPort = 21;
    private const string KindKey = "kind";
    private const string SessionKind = "sshSession";
    private const string RouteKind = "sshRoute";
    private const string RouteKey = "route";
    private const string RouteVersionKey = "version";
    private const string RouteSessionsKey = "sessions";
    private const string RoutePrefixRoutesKey = "prefixRoutes";
    private const string RouteLastSessionKey = "lastSession";
    private const string RouteHopCountKey = "hopCount";
    private const string OptsSessionKey = "session";
    private const string SessionNodeIdKey = "sessionNodeId";
    private const string SessionIdKey = "sessionId";
    private const int RouteVersion = 1;
    private const int MaxRouteHops = 8;

    private static readonly object registrationSync = new();
    private static bool isRegistered;

    /// <summary>Ensures custom SSH intrinsics are registered exactly once per process.</summary>
    internal static void EnsureRegistered()
    {
        lock (registrationSync)
        {
            if (isRegistered)
            {
                return;
            }

            RegisterSshConnectIntrinsic();
            RegisterSshDisconnectIntrinsic();
            RegisterFtpGetIntrinsic();
            RegisterFtpPutIntrinsic();
            isRegistered = true;
        }
    }

    /// <summary>Injects SSH module globals into a compiled interpreter instance.</summary>
    internal static void InjectSshModule(
        Interpreter interpreter,
        SystemCallExecutionContext? executionContext,
        MiniScriptSshExecutionMode mode = MiniScriptSshExecutionMode.RealWorld)
    {
        if (interpreter is null)
        {
            throw new ArgumentNullException(nameof(interpreter));
        }

        EnsureRegistered();
        interpreter.Compile();
        if (interpreter.vm is null)
        {
            return;
        }

        var moduleState = new SshModuleState(executionContext, mode);
        var sshModule = new ValMap
        {
            userData = moduleState,
        };
        sshModule["connect"] = Intrinsic.GetByName(SshConnectIntrinsicName).GetFunc();
        sshModule["disconnect"] = Intrinsic.GetByName(SshDisconnectIntrinsicName).GetFunc();
        interpreter.SetGlobalValue("ssh", sshModule);

        var ftpModule = new ValMap
        {
            userData = moduleState,
        };
        ftpModule["get"] = Intrinsic.GetByName(FtpGetIntrinsicName).GetFunc();
        ftpModule["put"] = Intrinsic.GetByName(FtpPutIntrinsicName).GetFunc();
        interpreter.SetGlobalValue("ftp", ftpModule);
    }

    private static void RegisterSshConnectIntrinsic()
    {
        if (Intrinsic.GetByName(SshConnectIntrinsicName) is not null)
        {
            return;
        }

        var intrinsic = Intrinsic.Create(SshConnectIntrinsicName);
        intrinsic.AddParam("hostOrIp");
        intrinsic.AddParam("user");
        intrinsic.AddParam("password");
        intrinsic.AddParam("port", 22);
        intrinsic.AddParam("opts");
        intrinsic.code = (context, partialResult) =>
        {
            if (!TryGetExecutionState(context, out var state) || state.ExecutionContext is null)
            {
                return new Intrinsic.Result(
                    CreateConnectFailureMap(
                        SystemCallErrorCode.InternalError,
                        "ssh.connect is unavailable in this execution context."));
            }

            var executionContext = state.ExecutionContext;
            var hostOrIp = context.GetLocalString("hostOrIp", string.Empty) ?? string.Empty;
            var userId = context.GetLocalString("user", string.Empty) ?? string.Empty;
            var password = context.GetLocalString("password", string.Empty) ?? string.Empty;
            if (!TryParseConnectArguments(context, out var port, out var optsMap, out var parseError))
            {
                return new Intrinsic.Result(
                    CreateConnectFailureMap(
                        SystemCallErrorCode.InvalidArgs,
                        parseError));
            }

            if (!TryResolveConnectSource(
                    state,
                    executionContext,
                    optsMap,
                    out var sourceServer,
                    out var parentSessions,
                    out var routeRequested,
                    out var sourceError))
            {
                return new Intrinsic.Result(
                    CreateConnectFailureMap(
                        SystemCallErrorCode.InvalidArgs,
                        sourceError));
            }

            if (routeRequested && parentSessions.Count + 1 > MaxRouteHops)
            {
                return new Intrinsic.Result(
                    CreateConnectFailureMap(
                        SystemCallErrorCode.InvalidArgs,
                        $"route.hopCount exceeds max hops ({MaxRouteHops})."));
            }

            if (state.Mode == MiniScriptSshExecutionMode.RealWorld)
            {
                if (!executionContext.World.TryOpenSshSession(
                        sourceServer,
                        hostOrIp,
                        userId,
                        password,
                        port,
                        via: "ssh.connect",
                        out var openResult,
                        out var failureResult))
                {
                    return new Intrinsic.Result(CreateConnectFailureMap(failureResult.Code, ExtractErrorText(failureResult)));
                }

                var session = CreateSessionMap(openResult);
                var route = routeRequested
                    ? CreateRouteMap(AppendRouteSession(parentSessions, session))
                    : null;
                return new Intrinsic.Result(CreateConnectSuccessMap(session, route));
            }

            if (!executionContext.World.TryValidateSshSessionOpen(
                    sourceServer,
                    hostOrIp,
                    userId,
                    password,
                    port,
                    out var validated,
                    out var sandboxFailure))
            {
                return new Intrinsic.Result(CreateConnectFailureMap(sandboxFailure.Code, ExtractErrorText(sandboxFailure)));
            }

            var sandboxSessionId = state.RegisterSandboxSession(
                validated.TargetNodeId,
                validated.TargetUserKey,
                validated.TargetUserId,
                validated.HostOrIp,
                validated.RemoteIp,
                "/");
            var sandboxOpenResult = new WorldRuntime.SshSessionOpenResult
            {
                TargetServer = validated.TargetServer,
                TargetNodeId = validated.TargetNodeId,
                TargetUserKey = validated.TargetUserKey,
                TargetUserId = validated.TargetUserId,
                SessionId = sandboxSessionId,
                RemoteIp = validated.RemoteIp,
                HostOrIp = validated.HostOrIp,
            };
            var sandboxSession = CreateSessionMap(sandboxOpenResult);
            var sandboxRoute = routeRequested
                ? CreateRouteMap(AppendRouteSession(parentSessions, sandboxSession))
                : null;
            return new Intrinsic.Result(CreateConnectSuccessMap(sandboxSession, sandboxRoute));
        };
    }

    private static void RegisterSshDisconnectIntrinsic()
    {
        if (Intrinsic.GetByName(SshDisconnectIntrinsicName) is not null)
        {
            return;
        }

        var intrinsic = Intrinsic.Create(SshDisconnectIntrinsicName);
        intrinsic.AddParam("session");
        intrinsic.code = (context, partialResult) =>
        {
            if (!TryGetExecutionState(context, out var state) || state.ExecutionContext is null)
            {
                return new Intrinsic.Result(
                    CreateDisconnectFailureMap(
                        SystemCallErrorCode.InternalError,
                        "ssh.disconnect is unavailable in this execution context."));
            }

            var executionContext = state.ExecutionContext;
            var session = context.GetLocal("session");
            if (session is not ValMap targetMap)
            {
                return new Intrinsic.Result(
                    CreateDisconnectFailureMap(
                        SystemCallErrorCode.InvalidArgs,
                        "session or route object is required."));
            }

            if (!TryReadKind(targetMap, out var kind, out var kindError, "target"))
            {
                return new Intrinsic.Result(CreateDisconnectFailureMap(SystemCallErrorCode.InvalidArgs, kindError));
            }

            if (string.Equals(kind, SessionKind, StringComparison.Ordinal))
            {
                if (!TryReadSessionIdentity(targetMap, out var sessionNodeId, out var sessionId, out var readError))
                {
                    return new Intrinsic.Result(CreateDisconnectFailureMap(SystemCallErrorCode.InvalidArgs, readError));
                }

                var disconnected = state.Mode == MiniScriptSshExecutionMode.RealWorld
                    ? executionContext.World.TryRemoveRemoteSession(sessionNodeId, sessionId)
                    : state.TryRemoveSandboxSession(sessionNodeId, sessionId);
                var summary = CreateDisconnectSummaryMap(
                    requested: 1,
                    closed: disconnected ? 1 : 0,
                    alreadyClosed: disconnected ? 0 : 1,
                    invalid: 0);
                return new Intrinsic.Result(CreateDisconnectSuccessMap(disconnected, summary));
            }

            if (!string.Equals(kind, RouteKind, StringComparison.Ordinal))
            {
                return new Intrinsic.Result(
                    CreateDisconnectFailureMap(
                        SystemCallErrorCode.InvalidArgs,
                        "target.kind must be sshSession or sshRoute."));
            }

            if (!TryReadRouteSessionMaps(targetMap, out var routeSessions, out var routeError))
            {
                return new Intrinsic.Result(CreateDisconnectFailureMap(SystemCallErrorCode.InvalidArgs, routeError));
            }

            var requested = 0;
            var closed = 0;
            var alreadyClosed = 0;
            var invalid = 0;
            var processedKeys = new HashSet<string>(StringComparer.Ordinal);
            for (var index = routeSessions.Count - 1; index >= 0; index--)
            {
                var routeSession = routeSessions[index];
                if (!TryReadSessionIdentity(routeSession, out var sessionNodeId, out var sessionId, out _))
                {
                    invalid++;
                    continue;
                }

                var key = sessionNodeId + ":" + sessionId;
                if (!processedKeys.Add(key))
                {
                    continue;
                }

                requested++;
                var removed = state.Mode == MiniScriptSshExecutionMode.RealWorld
                    ? executionContext.World.TryRemoveRemoteSession(sessionNodeId, sessionId)
                    : state.TryRemoveSandboxSession(sessionNodeId, sessionId);
                if (removed)
                {
                    closed++;
                }
                else
                {
                    alreadyClosed++;
                }
            }

            var routeSummary = CreateDisconnectSummaryMap(requested, closed, alreadyClosed, invalid);
            return new Intrinsic.Result(CreateDisconnectSuccessMap(closed > 0, routeSummary));
        };
    }

    private static void RegisterFtpGetIntrinsic()
    {
        if (Intrinsic.GetByName(FtpGetIntrinsicName) is not null)
        {
            return;
        }

        var intrinsic = Intrinsic.Create(FtpGetIntrinsicName);
        intrinsic.AddParam("sessionOrRoute");
        intrinsic.AddParam("remotePath");
        intrinsic.AddParam("localPath");
        intrinsic.AddParam("opts");
        intrinsic.code = (context, partialResult) =>
        {
            if (!TryGetExecutionState(context, out var state) || state.ExecutionContext is null)
            {
                return new Intrinsic.Result(
                    CreateFtpFailureMap(
                        SystemCallErrorCode.InternalError,
                        "ftp.get is unavailable in this execution context."));
            }

            if (context.GetLocal("sessionOrRoute") is not ValMap sessionOrRouteMap)
            {
                return new Intrinsic.Result(
                    CreateFtpFailureMap(
                        SystemCallErrorCode.InvalidArgs,
                        "sessionOrRoute object is required."));
            }

            if (!TryParseFtpArguments(
                    context,
                    "remotePath",
                    "localPath",
                    out var remotePathInput,
                    out var localPathInput,
                    out var port,
                    out var parseError))
            {
                return new Intrinsic.Result(CreateFtpFailureMap(SystemCallErrorCode.InvalidArgs, parseError));
            }

            var executionContext = state.ExecutionContext;
            if (!TryResolveFtpEndpoints(
                    state,
                    executionContext,
                    sessionOrRouteMap,
                    out var firstEndpoint,
                    out var lastEndpoint,
                    out var endpointError))
            {
                return new Intrinsic.Result(CreateFtpFailureMap(SystemCallErrorCode.InvalidArgs, endpointError));
            }

            if (!executionContext.World.TryValidatePortAccess(
                    firstEndpoint.Server,
                    lastEndpoint.Server,
                    port,
                    PortType.Ftp,
                    out var portFailure))
            {
                return new Intrinsic.Result(CreateFtpFailureMap(portFailure.Code, ExtractErrorText(portFailure)));
            }

            if (!TryGetFtpEndpointUser(firstEndpoint, out var firstUser, out var firstUserError))
            {
                return new Intrinsic.Result(CreateFtpFailureMap(SystemCallErrorCode.InvalidArgs, firstUserError));
            }

            if (!TryGetFtpEndpointUser(lastEndpoint, out var lastUser, out var lastUserError))
            {
                return new Intrinsic.Result(CreateFtpFailureMap(SystemCallErrorCode.InvalidArgs, lastUserError));
            }

            if (!lastUser.Privilege.Read ||
                !firstUser.Privilege.Write)
            {
                return new Intrinsic.Result(
                    CreateFtpFailureMap(
                        SystemCallErrorCode.PermissionDenied,
                        "permission denied: ftp get"));
            }

            var remoteSourcePath = BaseFileSystem.NormalizePath(lastEndpoint.Cwd, remotePathInput);
            if (!TryReadSourceFile(lastEndpoint.Server, remoteSourcePath, out var sourceEntry, out var sourceContent, out var sourceFailure))
            {
                return new Intrinsic.Result(CreateFtpFailureMap(sourceFailure.Code, ExtractErrorText(sourceFailure)));
            }

            var sourceFileName = GetFileName(remoteSourcePath);
            if (string.IsNullOrWhiteSpace(sourceFileName))
            {
                return new Intrinsic.Result(
                    CreateFtpFailureMap(
                        SystemCallErrorCode.InvalidArgs,
                        $"invalid source path: {remotePathInput}"));
            }

            if (!TryResolveDestinationPath(
                    firstEndpoint.Server,
                    firstEndpoint.Cwd,
                    localPathInput,
                    sourceFileName,
                    out var localDestinationPath,
                    out var destinationFailure))
            {
                return new Intrinsic.Result(
                    CreateFtpFailureMap(
                        destinationFailure!.Code,
                        ExtractErrorText(destinationFailure)));
            }

            if (state.Mode == MiniScriptSshExecutionMode.RealWorld)
            {
                if (!TryWriteDestinationFile(firstEndpoint.Server, localDestinationPath, sourceContent, sourceEntry, out var writeFailure))
                {
                    return new Intrinsic.Result(
                        CreateFtpFailureMap(
                            writeFailure!.Code,
                            ExtractErrorText(writeFailure)));
                }

                executionContext.World.EmitFileAcquire(
                    fromNodeId: lastEndpoint.NodeId,
                    userKey: firstEndpoint.UserKey,
                    fileName: sourceFileName,
                    remotePath: remoteSourcePath,
                    localPath: localDestinationPath,
                    sizeBytes: ToOptionalInt(sourceEntry.Size),
                    contentId: sourceEntry.ContentId,
                    transferMethod: "ftp");
            }

            return new Intrinsic.Result(CreateFtpSuccessMap(localDestinationPath, ToOptionalInt(sourceEntry.Size)));
        };
    }

    private static void RegisterFtpPutIntrinsic()
    {
        if (Intrinsic.GetByName(FtpPutIntrinsicName) is not null)
        {
            return;
        }

        var intrinsic = Intrinsic.Create(FtpPutIntrinsicName);
        intrinsic.AddParam("sessionOrRoute");
        intrinsic.AddParam("localPath");
        intrinsic.AddParam("remotePath");
        intrinsic.AddParam("opts");
        intrinsic.code = (context, partialResult) =>
        {
            if (!TryGetExecutionState(context, out var state) || state.ExecutionContext is null)
            {
                return new Intrinsic.Result(
                    CreateFtpFailureMap(
                        SystemCallErrorCode.InternalError,
                        "ftp.put is unavailable in this execution context."));
            }

            if (context.GetLocal("sessionOrRoute") is not ValMap sessionOrRouteMap)
            {
                return new Intrinsic.Result(
                    CreateFtpFailureMap(
                        SystemCallErrorCode.InvalidArgs,
                        "sessionOrRoute object is required."));
            }

            if (!TryParseFtpArguments(
                    context,
                    "localPath",
                    "remotePath",
                    out var localPathInput,
                    out var remotePathInput,
                    out var port,
                    out var parseError))
            {
                return new Intrinsic.Result(CreateFtpFailureMap(SystemCallErrorCode.InvalidArgs, parseError));
            }

            var executionContext = state.ExecutionContext;
            if (!TryResolveFtpEndpoints(
                    state,
                    executionContext,
                    sessionOrRouteMap,
                    out var firstEndpoint,
                    out var lastEndpoint,
                    out var endpointError))
            {
                return new Intrinsic.Result(CreateFtpFailureMap(SystemCallErrorCode.InvalidArgs, endpointError));
            }

            if (!executionContext.World.TryValidatePortAccess(
                    firstEndpoint.Server,
                    lastEndpoint.Server,
                    port,
                    PortType.Ftp,
                    out var portFailure))
            {
                return new Intrinsic.Result(CreateFtpFailureMap(portFailure.Code, ExtractErrorText(portFailure)));
            }

            if (!TryGetFtpEndpointUser(firstEndpoint, out var firstUser, out var firstUserError))
            {
                return new Intrinsic.Result(CreateFtpFailureMap(SystemCallErrorCode.InvalidArgs, firstUserError));
            }

            if (!TryGetFtpEndpointUser(lastEndpoint, out var lastUser, out var lastUserError))
            {
                return new Intrinsic.Result(CreateFtpFailureMap(SystemCallErrorCode.InvalidArgs, lastUserError));
            }

            if (!firstUser.Privilege.Read ||
                !lastUser.Privilege.Write)
            {
                return new Intrinsic.Result(
                    CreateFtpFailureMap(
                        SystemCallErrorCode.PermissionDenied,
                        "permission denied: ftp put"));
            }

            var localSourcePath = BaseFileSystem.NormalizePath(firstEndpoint.Cwd, localPathInput);
            if (!TryReadSourceFile(firstEndpoint.Server, localSourcePath, out var sourceEntry, out var sourceContent, out var sourceFailure))
            {
                return new Intrinsic.Result(CreateFtpFailureMap(sourceFailure.Code, ExtractErrorText(sourceFailure)));
            }

            var sourceFileName = GetFileName(localSourcePath);
            if (string.IsNullOrWhiteSpace(sourceFileName))
            {
                return new Intrinsic.Result(
                    CreateFtpFailureMap(
                        SystemCallErrorCode.InvalidArgs,
                        $"invalid source path: {localPathInput}"));
            }

            if (!TryResolveDestinationPath(
                    lastEndpoint.Server,
                    lastEndpoint.Cwd,
                    remotePathInput,
                    sourceFileName,
                    out var remoteDestinationPath,
                    out var destinationFailure))
            {
                return new Intrinsic.Result(
                    CreateFtpFailureMap(
                        destinationFailure!.Code,
                        ExtractErrorText(destinationFailure)));
            }

            if (state.Mode == MiniScriptSshExecutionMode.RealWorld)
            {
                if (!TryWriteDestinationFile(lastEndpoint.Server, remoteDestinationPath, sourceContent, sourceEntry, out var writeFailure))
                {
                    return new Intrinsic.Result(
                        CreateFtpFailureMap(
                            writeFailure!.Code,
                            ExtractErrorText(writeFailure)));
                }
            }

            return new Intrinsic.Result(CreateFtpSuccessMap(remoteDestinationPath, ToOptionalInt(sourceEntry.Size)));
        };
    }

    private static bool TryParseFtpArguments(
        TAC.Context context,
        string requiredPathKey,
        string optionalPathKey,
        out string requiredPath,
        out string? optionalPath,
        out int port,
        out string error)
    {
        requiredPath = string.Empty;
        optionalPath = null;
        port = DefaultFtpPort;
        error = string.Empty;

        var rawRequiredPath = context.GetLocal(requiredPathKey);
        if (rawRequiredPath is null)
        {
            error = "path is required.";
            return false;
        }

        requiredPath = rawRequiredPath.ToString().Trim();
        if (string.IsNullOrWhiteSpace(requiredPath))
        {
            error = "path is required.";
            return false;
        }

        var rawOptionalPath = context.GetLocal(optionalPathKey);
        var rawOpts = context.GetLocal("opts");
        ValMap? optsMap = null;
        if (rawOptionalPath is ValMap optsFromOptionalPath)
        {
            if (rawOpts is not null)
            {
                error = $"opts must be omitted when {optionalPathKey} argument is already a map.";
                return false;
            }

            optsMap = optsFromOptionalPath;
        }
        else
        {
            if (rawOptionalPath is not null)
            {
                optionalPath = rawOptionalPath.ToString().Trim();
                if (string.IsNullOrWhiteSpace(optionalPath))
                {
                    error = "path is required.";
                    return false;
                }
            }

            if (rawOpts is not null)
            {
                if (rawOpts is not ValMap parsedOpts)
                {
                    error = "opts must be a map.";
                    return false;
                }

                optsMap = parsedOpts;
            }
        }

        if (!TryParseFtpOpts(optsMap, out port, out error))
        {
            return false;
        }

        return true;
    }

    private static bool TryParseFtpOpts(
        ValMap? optsMap,
        out int port,
        out string error)
    {
        port = DefaultFtpPort;
        error = string.Empty;
        if (optsMap is null)
        {
            return true;
        }

        // TODO: Support overwrite/maxBytes once FTP intrinsic transfer policy is finalized.
        foreach (var key in optsMap.Keys)
        {
            var keyText = key?.ToString().Trim() ?? string.Empty;
            if (!string.Equals(keyText, "port", StringComparison.Ordinal))
            {
                error = $"unsupported opts key: {keyText}";
                return false;
            }
        }

        if (!optsMap.TryGetValue("port", out var rawPort))
        {
            return true;
        }

        return TryReadPort(rawPort, out port, out error);
    }

    private static bool TryResolveFtpEndpoints(
        SshModuleState state,
        SystemCallExecutionContext executionContext,
        ValMap sessionOrRouteMap,
        out FtpEndpoint firstEndpoint,
        out FtpEndpoint lastEndpoint,
        out string error)
    {
        firstEndpoint = default;
        lastEndpoint = default;
        error = string.Empty;
        if (!TryReadKind(sessionOrRouteMap, out var kind, out var kindError, "sessionOrRoute"))
        {
            error = kindError;
            return false;
        }

        if (string.Equals(kind, SessionKind, StringComparison.Ordinal))
        {
            if (!TryResolveCanonicalSessionMap(
                    state,
                    executionContext,
                    sessionOrRouteMap,
                    out var canonicalSession,
                    out var canonicalError))
            {
                error = canonicalError;
                return false;
            }

            if (!TryResolveExecutionContextFtpEndpoint(executionContext, out firstEndpoint, out var firstError))
            {
                error = firstError;
                return false;
            }

            if (!TryResolveSessionFtpEndpoint(state, executionContext, canonicalSession, out lastEndpoint, out var lastError))
            {
                error = lastError;
                return false;
            }

            return true;
        }

        if (!string.Equals(kind, RouteKind, StringComparison.Ordinal))
        {
            error = "sessionOrRoute.kind must be sshSession or sshRoute.";
            return false;
        }

        if (!TryReadRouteSessionMaps(sessionOrRouteMap, out var routeSessions, out var routeError))
        {
            error = routeError;
            return false;
        }

        var canonicalRouteSessions = new List<ValMap>(routeSessions.Count);
        foreach (var routeSession in routeSessions)
        {
            if (!TryResolveCanonicalSessionMap(state, executionContext, routeSession, out var canonicalSession, out var canonicalError))
            {
                error = canonicalError;
                return false;
            }

            canonicalRouteSessions.Add(canonicalSession);
        }

        if (!TryResolveSessionFtpEndpoint(
                state,
                executionContext,
                canonicalRouteSessions[0],
                out firstEndpoint,
                out var routeFirstError))
        {
            error = routeFirstError;
            return false;
        }

        if (!TryResolveSessionFtpEndpoint(
                state,
                executionContext,
                canonicalRouteSessions[canonicalRouteSessions.Count - 1],
                out lastEndpoint,
                out var routeLastError))
        {
            error = routeLastError;
            return false;
        }

        return true;
    }

    private static bool TryResolveExecutionContextFtpEndpoint(
        SystemCallExecutionContext executionContext,
        out FtpEndpoint endpoint,
        out string error)
    {
        endpoint = default;
        error = string.Empty;
        var userKey = executionContext.UserKey?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(userKey))
        {
            error = "execution context user is invalid.";
            return false;
        }

        if (!executionContext.Server.Users.ContainsKey(userKey))
        {
            error = $"execution context user not found: {userKey}.";
            return false;
        }

        endpoint = new FtpEndpoint(
            executionContext.Server,
            executionContext.Server.NodeId,
            userKey,
            BaseFileSystem.NormalizePath("/", executionContext.Cwd));
        return true;
    }

    private static bool TryResolveSessionFtpEndpoint(
        SshModuleState state,
        SystemCallExecutionContext executionContext,
        ValMap sessionMap,
        out FtpEndpoint endpoint,
        out string error)
    {
        endpoint = default;
        error = string.Empty;
        if (!TryReadSessionIdentity(sessionMap, out var sessionNodeId, out var sessionId, out var readError))
        {
            error = readError;
            return false;
        }

        if (state.Mode == MiniScriptSshExecutionMode.RealWorld)
        {
            if (!executionContext.World.TryResolveRemoteSession(sessionNodeId, sessionId, out var sessionServer, out var sessionConfig))
            {
                error = $"session not found: {sessionNodeId}/{sessionId}.";
                return false;
            }

            var userKey = sessionConfig.UserKey?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(userKey))
            {
                error = $"session user is invalid: {sessionNodeId}/{sessionId}.";
                return false;
            }

            if (!sessionServer.Users.ContainsKey(userKey))
            {
                error = $"session user not found: {sessionNodeId}/{userKey}.";
                return false;
            }

            endpoint = new FtpEndpoint(
                sessionServer,
                sessionNodeId,
                userKey,
                BaseFileSystem.NormalizePath("/", sessionConfig.Cwd));
            return true;
        }

        if (!state.TryResolveSandboxSession(sessionNodeId, sessionId, out var sandboxSession))
        {
            error = $"session not found: {sessionNodeId}/{sessionId}.";
            return false;
        }

        if (!executionContext.World.TryGetServer(sessionNodeId, out var sandboxServer))
        {
            error = $"server not found: {sessionNodeId}.";
            return false;
        }

        var sandboxUserKey = sandboxSession.UserKey?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(sandboxUserKey) &&
            !executionContext.World.TryResolveUserKeyByUserId(sandboxServer, sandboxSession.UserId, out sandboxUserKey))
        {
            error = $"session user not found: {sessionNodeId}/{sandboxSession.UserId}.";
            return false;
        }

        if (!sandboxServer.Users.ContainsKey(sandboxUserKey))
        {
            error = $"session user not found: {sessionNodeId}/{sandboxUserKey}.";
            return false;
        }

        endpoint = new FtpEndpoint(
            sandboxServer,
            sessionNodeId,
            sandboxUserKey,
            BaseFileSystem.NormalizePath("/", sandboxSession.Cwd));
        return true;
    }

    private static bool TryGetFtpEndpointUser(
        FtpEndpoint endpoint,
        out UserConfig user,
        out string error)
    {
        user = null!;
        error = string.Empty;
        if (!endpoint.Server.Users.TryGetValue(endpoint.UserKey, out var endpointUser) ||
            endpointUser is null)
        {
            error = $"user not found: {endpoint.NodeId}/{endpoint.UserKey}.";
            return false;
        }

        user = endpointUser;
        return true;
    }

    private static bool TryReadSourceFile(
        ServerNodeRuntime sourceServer,
        string sourcePath,
        out VfsEntryMeta sourceEntry,
        out string sourceContent,
        out SystemCallResult failure)
    {
        sourceEntry = null!;
        sourceContent = string.Empty;
        failure = SystemCallResultFactory.Success();

        if (!sourceServer.DiskOverlay.TryResolveEntry(sourcePath, out var entry))
        {
            failure = SystemCallResultFactory.NotFound(sourcePath);
            return false;
        }

        if (entry.EntryKind != VfsEntryKind.File)
        {
            failure = SystemCallResultFactory.NotFile(sourcePath);
            return false;
        }

        if (!sourceServer.DiskOverlay.TryReadFileText(sourcePath, out sourceContent))
        {
            failure = SystemCallResultFactory.NotFound(sourcePath);
            return false;
        }

        sourceEntry = entry;
        return true;
    }

    private static bool TryResolveDestinationPath(
        ServerNodeRuntime destinationServer,
        string destinationCwd,
        string? destinationPathInput,
        string sourceFileName,
        out string destinationPath,
        out SystemCallResult? failure)
    {
        destinationPath = string.Empty;
        failure = null;

        var normalizedInput = destinationPathInput?.Trim() ?? string.Empty;
        destinationPath = string.IsNullOrWhiteSpace(normalizedInput)
            ? BaseFileSystem.NormalizePath(destinationCwd, sourceFileName)
            : BaseFileSystem.NormalizePath(destinationCwd, normalizedInput);

        if (destinationServer.DiskOverlay.TryResolveEntry(destinationPath, out var existingEntry) &&
            existingEntry.EntryKind == VfsEntryKind.Dir)
        {
            destinationPath = JoinPath(destinationPath, sourceFileName);
        }

        return TryValidateDestinationParent(destinationServer, destinationPath, out failure);
    }

    private static bool TryValidateDestinationParent(
        ServerNodeRuntime destinationServer,
        string destinationPath,
        out SystemCallResult? failure)
    {
        failure = null;
        var parentPath = GetParentPath(destinationPath);
        if (!destinationServer.DiskOverlay.TryResolveEntry(parentPath, out var parentEntry))
        {
            failure = SystemCallResultFactory.NotFound(parentPath);
            return false;
        }

        if (parentEntry.EntryKind != VfsEntryKind.Dir)
        {
            failure = SystemCallResultFactory.NotDirectory(parentPath);
            return false;
        }

        if (destinationServer.DiskOverlay.TryResolveEntry(destinationPath, out var existingEntry) &&
            existingEntry.EntryKind != VfsEntryKind.File)
        {
            failure = SystemCallResultFactory.NotFile(destinationPath);
            return false;
        }

        return true;
    }

    private static bool TryWriteDestinationFile(
        ServerNodeRuntime destinationServer,
        string destinationPath,
        string content,
        VfsEntryMeta sourceEntry,
        out SystemCallResult? failure)
    {
        failure = null;
        try
        {
            destinationServer.DiskOverlay.WriteFile(
                destinationPath,
                content,
                fileKind: sourceEntry.FileKind ?? VfsFileKind.Text,
                size: sourceEntry.Size);
            return true;
        }
        catch (InvalidOperationException ex)
        {
            failure = SystemCallResultFactory.Failure(SystemCallErrorCode.InvalidArgs, ex.Message);
            return false;
        }
    }

    private static string JoinPath(string parentPath, string childName)
    {
        return parentPath == "/"
            ? "/" + childName
            : parentPath + "/" + childName;
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

    private static string GetFileName(string normalizedPath)
    {
        var trimmed = (normalizedPath ?? string.Empty).TrimEnd('/');
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            return string.Empty;
        }

        var index = trimmed.LastIndexOf('/');
        if (index < 0 || index == trimmed.Length - 1)
        {
            return trimmed;
        }

        return trimmed[(index + 1)..];
    }

    private static int? ToOptionalInt(long value)
    {
        return value is < int.MinValue or > int.MaxValue
            ? null
            : (int)value;
    }

    private static bool TryGetExecutionState(TAC.Context context, out SshModuleState state)
    {
        state = null!;
        if (context.self is not ValMap selfMap ||
            selfMap.userData is not SshModuleState sshState)
        {
            return false;
        }

        state = sshState;
        return true;
    }

    private static bool TryParseConnectArguments(
        TAC.Context context,
        out int port,
        out ValMap? optsMap,
        out string error)
    {
        port = 22;
        optsMap = null;
        error = string.Empty;

        var rawPort = context.GetLocal("port");
        var rawOpts = context.GetLocal("opts");
        if (rawPort is ValMap optsFromPort)
        {
            if (rawOpts is not null)
            {
                error = "opts must be omitted when port argument is already a map.";
                return false;
            }

            optsMap = optsFromPort;
            return true;
        }

        if (!TryReadPort(rawPort, out port, out error))
        {
            return false;
        }

        if (rawOpts is null)
        {
            return true;
        }

        if (rawOpts is not ValMap parsedOpts)
        {
            error = "opts must be a map.";
            return false;
        }

        optsMap = parsedOpts;
        return true;
    }

    private static bool TryReadPort(Value rawPort, out int port, out string error)
    {
        port = 22;
        error = string.Empty;
        if (rawPort is null)
        {
            error = "invalid port: null";
            return false;
        }

        try
        {
            port = rawPort.IntValue();
        }
        catch (Exception)
        {
            error = $"invalid port: {rawPort}";
            return false;
        }

        if (port is < 1 or > 65535)
        {
            error = $"invalid port: {port}";
            return false;
        }

        return true;
    }

    private static bool TryResolveConnectSource(
        SshModuleState state,
        SystemCallExecutionContext executionContext,
        ValMap? optsMap,
        out ServerNodeRuntime sourceServer,
        out List<ValMap> parentSessions,
        out bool routeRequested,
        out string error)
    {
        sourceServer = executionContext.Server;
        parentSessions = new List<ValMap>();
        routeRequested = false;
        error = string.Empty;
        if (optsMap is null)
        {
            return true;
        }

        if (!optsMap.TryGetValue(OptsSessionKey, out var sessionOrRouteValue))
        {
            return true;
        }

        routeRequested = true;
        if (sessionOrRouteValue is not ValMap sessionOrRouteMap)
        {
            error = "opts.session must be sshSession or sshRoute.";
            return false;
        }

        if (!TryReadKind(sessionOrRouteMap, out var kind, out var kindError, "opts.session"))
        {
            error = kindError;
            return false;
        }

        if (string.Equals(kind, SessionKind, StringComparison.Ordinal))
        {
            if (!TryResolveCanonicalSessionMap(state, executionContext, sessionOrRouteMap, out var canonicalSession, out var sessionError))
            {
                error = sessionError;
                return false;
            }

            parentSessions.Add(canonicalSession);
        }
        else if (string.Equals(kind, RouteKind, StringComparison.Ordinal))
        {
            if (!TryReadRouteSessionMaps(sessionOrRouteMap, out var routeSessions, out var routeError))
            {
                error = routeError;
                return false;
            }

            foreach (var routeSession in routeSessions)
            {
                if (!TryResolveCanonicalSessionMap(state, executionContext, routeSession, out var canonicalSession, out var sessionError))
                {
                    error = sessionError;
                    return false;
                }

                parentSessions.Add(canonicalSession);
            }
        }
        else
        {
            error = "opts.session.kind must be sshSession or sshRoute.";
            return false;
        }

        if (parentSessions.Count == 0)
        {
            error = "opts.session must include at least one session.";
            return false;
        }

        if (!TryReadSessionIdentity(parentSessions[parentSessions.Count - 1], out var sourceNodeId, out _, out _))
        {
            error = "opts.session is invalid.";
            return false;
        }

        if (!executionContext.World.TryGetServer(sourceNodeId, out sourceServer))
        {
            error = $"source session node not found: {sourceNodeId}.";
            return false;
        }

        return true;
    }

    private static bool TryResolveCanonicalSessionMap(
        SshModuleState state,
        SystemCallExecutionContext executionContext,
        ValMap inputSessionMap,
        out ValMap canonicalSession,
        out string error)
    {
        canonicalSession = null!;
        error = string.Empty;
        if (!TryReadSessionIdentity(inputSessionMap, out var sessionNodeId, out var sessionId, out var readError))
        {
            error = readError;
            return false;
        }

        if (state.Mode == MiniScriptSshExecutionMode.RealWorld)
        {
            if (!executionContext.World.TryResolveRemoteSession(
                    sessionNodeId,
                    sessionId,
                    out var sessionServer,
                    out var sessionConfig))
            {
                error = $"session not found: {sessionNodeId}/{sessionId}.";
                return false;
            }

            var userId = executionContext.World.ResolvePromptUser(sessionServer, sessionConfig.UserKey);
            var hostOrIp = TryReadOptionalString(inputSessionMap, "hostOrIp", out var hostHint) &&
                           !string.IsNullOrWhiteSpace(hostHint)
                ? hostHint!
                : (string.IsNullOrWhiteSpace(sessionConfig.RemoteIp) ? sessionNodeId : sessionConfig.RemoteIp);
            var remoteIp = string.IsNullOrWhiteSpace(sessionConfig.RemoteIp) ? "127.0.0.1" : sessionConfig.RemoteIp;
            canonicalSession = CreateSessionMap(sessionNodeId, sessionId, userId, hostOrIp, remoteIp);
            return true;
        }

        if (!state.TryResolveSandboxSession(sessionNodeId, sessionId, out var sandboxSession))
        {
            error = $"session not found: {sessionNodeId}/{sessionId}.";
            return false;
        }

        canonicalSession = CreateSessionMap(
            sandboxSession.SessionNodeId,
            sandboxSession.SessionId,
            sandboxSession.UserId,
            sandboxSession.HostOrIp,
            sandboxSession.RemoteIp);
        return true;
    }

    private static bool TryReadOptionalString(
        ValMap map,
        string key,
        out string? value)
    {
        value = null;
        if (!map.TryGetValue(key, out var raw) || raw is null)
        {
            return false;
        }

        value = raw.ToString().Trim();
        return true;
    }

    private static bool TryReadRouteSessionMaps(
        ValMap routeMap,
        out List<ValMap> sessionMaps,
        out string error)
    {
        sessionMaps = new List<ValMap>();
        error = string.Empty;
        if (!TryReadKind(routeMap, out var kind, out var kindError, "route"))
        {
            error = kindError;
            return false;
        }

        if (!string.Equals(kind, RouteKind, StringComparison.Ordinal))
        {
            error = "route.kind must be sshRoute.";
            return false;
        }

        if (!TryReadRequiredInt(routeMap, RouteVersionKey, out var version, out error, "route"))
        {
            return false;
        }

        if (version != RouteVersion)
        {
            error = $"route.version must be {RouteVersion}.";
            return false;
        }

        if (!routeMap.TryGetValue(RouteSessionsKey, out var routeSessionsValue) ||
            routeSessionsValue is not ValList routeSessionsList)
        {
            error = "route.sessions is required.";
            return false;
        }

        if (routeSessionsList.values.Count == 0)
        {
            error = "route.sessions must include at least one session.";
            return false;
        }

        if (routeSessionsList.values.Count > MaxRouteHops)
        {
            error = $"route.hopCount exceeds max hops ({MaxRouteHops}).";
            return false;
        }

        for (var index = 0; index < routeSessionsList.values.Count; index++)
        {
            if (routeSessionsList.values[index] is not ValMap routeSessionMap)
            {
                error = $"route.sessions[{index}] must be a session map.";
                return false;
            }

            if (!TryReadSessionIdentity(routeSessionMap, out _, out _, out var routeSessionError))
            {
                error = $"route.sessions[{index}]: {routeSessionError}";
                return false;
            }

            sessionMaps.Add(routeSessionMap);
        }

        if (!TryReadRequiredInt(routeMap, RouteHopCountKey, out var hopCount, out error, "route"))
        {
            return false;
        }

        if (hopCount != sessionMaps.Count)
        {
            error = "route.hopCount must match route.sessions length.";
            return false;
        }

        if (!routeMap.TryGetValue(RouteLastSessionKey, out var lastSessionValue) ||
            lastSessionValue is not ValMap lastSessionMap)
        {
            error = "route.lastSession is required.";
            return false;
        }

        if (!TryReadSessionIdentity(lastSessionMap, out var lastNodeId, out var lastSessionId, out var lastError))
        {
            error = "route.lastSession: " + lastError;
            return false;
        }

        if (!TryReadSessionIdentity(
                sessionMaps[sessionMaps.Count - 1],
                out var expectedLastNodeId,
                out var expectedLastSessionId,
                out _))
        {
            error = "route.sessions is invalid.";
            return false;
        }

        if (!string.Equals(lastNodeId, expectedLastNodeId, StringComparison.Ordinal) ||
            lastSessionId != expectedLastSessionId)
        {
            error = "route.lastSession must match the final element of route.sessions.";
            return false;
        }

        if (!routeMap.TryGetValue(RoutePrefixRoutesKey, out var prefixRoutesValue) ||
            prefixRoutesValue is not ValList prefixRoutesList)
        {
            error = "route.prefixRoutes is required.";
            return false;
        }

        var expectedPrefixCount = Math.Max(0, sessionMaps.Count - 1);
        if (prefixRoutesList.values.Count != expectedPrefixCount)
        {
            error = "route.prefixRoutes length must be route.hopCount - 1.";
            return false;
        }

        for (var index = 0; index < prefixRoutesList.values.Count; index++)
        {
            if (prefixRoutesList.values[index] is not ValMap prefixRouteMap)
            {
                error = $"route.prefixRoutes[{index}] must be a route map.";
                return false;
            }

            if (!TryValidatePrefixRoute(prefixRouteMap, sessionMaps, index + 1, out var prefixError))
            {
                error = $"route.prefixRoutes[{index}]: {prefixError}";
                return false;
            }
        }

        return true;
    }

    private static bool TryValidatePrefixRoute(
        ValMap prefixRouteMap,
        IReadOnlyList<ValMap> fullRouteSessions,
        int expectedHopCount,
        out string error)
    {
        error = string.Empty;
        if (!TryReadKind(prefixRouteMap, out var kind, out var kindError, "prefixRoute"))
        {
            error = kindError;
            return false;
        }

        if (!string.Equals(kind, RouteKind, StringComparison.Ordinal))
        {
            error = "prefixRoute.kind must be sshRoute.";
            return false;
        }

        if (!TryReadRequiredInt(prefixRouteMap, RouteVersionKey, out var version, out error, "prefixRoute"))
        {
            return false;
        }

        if (version != RouteVersion)
        {
            error = $"prefixRoute.version must be {RouteVersion}.";
            return false;
        }

        if (!prefixRouteMap.TryGetValue(RouteSessionsKey, out var prefixSessionsValue) ||
            prefixSessionsValue is not ValList prefixSessionsList)
        {
            error = "prefixRoute.sessions is required.";
            return false;
        }

        if (prefixSessionsList.values.Count != expectedHopCount)
        {
            error = "prefixRoute.sessions length is invalid.";
            return false;
        }

        for (var index = 0; index < prefixSessionsList.values.Count; index++)
        {
            if (prefixSessionsList.values[index] is not ValMap prefixSessionMap)
            {
                error = $"prefixRoute.sessions[{index}] must be a session map.";
                return false;
            }

            if (!TryReadSessionIdentity(prefixSessionMap, out _, out _, out var prefixSessionError))
            {
                error = $"prefixRoute.sessions[{index}]: {prefixSessionError}";
                return false;
            }

            if (!AreEqualSessionIdentity(prefixSessionMap, fullRouteSessions[index]))
            {
                error = $"prefixRoute.sessions[{index}] does not match route.sessions[{index}].";
                return false;
            }
        }

        if (!TryReadRequiredInt(prefixRouteMap, RouteHopCountKey, out var hopCount, out error, "prefixRoute"))
        {
            return false;
        }

        if (hopCount != expectedHopCount)
        {
            error = "prefixRoute.hopCount is invalid.";
            return false;
        }

        if (!prefixRouteMap.TryGetValue(RouteLastSessionKey, out var prefixLastSessionValue) ||
            prefixLastSessionValue is not ValMap prefixLastSessionMap)
        {
            error = "prefixRoute.lastSession is required.";
            return false;
        }

        if (!AreEqualSessionIdentity(prefixLastSessionMap, fullRouteSessions[expectedHopCount - 1]))
        {
            error = "prefixRoute.lastSession is invalid.";
            return false;
        }

        if (!prefixRouteMap.TryGetValue(RoutePrefixRoutesKey, out var nestedPrefixValue) ||
            nestedPrefixValue is not ValList nestedPrefixList)
        {
            error = "prefixRoute.prefixRoutes is required.";
            return false;
        }

        if (nestedPrefixList.values.Count != 0)
        {
            error = "prefixRoute.prefixRoutes must be empty.";
            return false;
        }

        return true;
    }

    private static bool AreEqualSessionIdentity(ValMap leftSession, ValMap rightSession)
    {
        if (!TryReadSessionIdentity(leftSession, out var leftNodeId, out var leftSessionId, out _) ||
            !TryReadSessionIdentity(rightSession, out var rightNodeId, out var rightSessionId, out _))
        {
            return false;
        }

        return string.Equals(leftNodeId, rightNodeId, StringComparison.Ordinal) &&
               leftSessionId == rightSessionId;
    }

    private static bool TryReadRequiredInt(
        ValMap map,
        string key,
        out int value,
        out string error,
        string scope)
    {
        value = 0;
        error = string.Empty;
        if (!map.TryGetValue(key, out var rawValue) ||
            rawValue is null)
        {
            error = $"{scope}.{key} is required.";
            return false;
        }

        try
        {
            value = rawValue.IntValue();
        }
        catch (Exception)
        {
            error = $"{scope}.{key} must be an integer.";
            return false;
        }

        return true;
    }

    private static bool TryReadKind(
        ValMap map,
        out string kind,
        out string error,
        string scope)
    {
        kind = string.Empty;
        error = string.Empty;
        if (!map.TryGetValue(KindKey, out var kindValue) ||
            kindValue is null)
        {
            error = $"{scope}.kind is required.";
            return false;
        }

        kind = kindValue.ToString().Trim();
        if (string.IsNullOrWhiteSpace(kind))
        {
            error = $"{scope}.kind is required.";
            return false;
        }

        return true;
    }

    private static bool TryReadSessionIdentity(
        ValMap sessionMap,
        out string sessionNodeId,
        out int sessionId,
        out string error)
    {
        sessionNodeId = string.Empty;
        sessionId = 0;
        error = string.Empty;

        if (!TryReadKind(sessionMap, out var kind, out var kindError, "session"))
        {
            error = kindError;
            return false;
        }

        if (!string.Equals(kind, SessionKind, StringComparison.Ordinal))
        {
            error = "session.kind must be sshSession.";
            return false;
        }

        if (!sessionMap.TryGetValue(SessionNodeIdKey, out var sessionNodeIdValue) ||
            sessionNodeIdValue is null)
        {
            error = "session.sessionNodeId is required.";
            return false;
        }

        sessionNodeId = sessionNodeIdValue.ToString().Trim();
        if (string.IsNullOrWhiteSpace(sessionNodeId))
        {
            error = "session.sessionNodeId is required.";
            return false;
        }

        if (!sessionMap.TryGetValue(SessionIdKey, out var sessionIdValue) ||
            sessionIdValue is null)
        {
            error = "session.sessionId is required.";
            return false;
        }

        try
        {
            sessionId = sessionIdValue.IntValue();
        }
        catch (Exception)
        {
            error = "session.sessionId must be a positive integer.";
            return false;
        }

        if (sessionId < 1)
        {
            error = "session.sessionId must be a positive integer.";
            return false;
        }

        return true;
    }

    private static List<ValMap> AppendRouteSession(IReadOnlyList<ValMap> parentSessions, ValMap currentSession)
    {
        var routeSessions = new List<ValMap>(parentSessions.Count + 1);
        for (var index = 0; index < parentSessions.Count; index++)
        {
            routeSessions.Add(parentSessions[index]);
        }

        routeSessions.Add(currentSession);
        return routeSessions;
    }

    private static ValMap CreateSessionMap(WorldRuntime.SshSessionOpenResult openResult)
    {
        return CreateSessionMap(
            openResult.TargetNodeId,
            openResult.SessionId,
            openResult.TargetUserId,
            openResult.HostOrIp,
            openResult.RemoteIp);
    }

    private static ValMap CreateSessionMap(
        string sessionNodeId,
        int sessionId,
        string userId,
        string hostOrIp,
        string remoteIp)
    {
        var normalizedHostOrIp = hostOrIp?.Trim() ?? string.Empty;
        var normalizedRemoteIp = remoteIp?.Trim() ?? string.Empty;
        return new ValMap
        {
            [KindKey] = new ValString(SessionKind),
            [SessionIdKey] = new ValNumber(sessionId),
            [SessionNodeIdKey] = new ValString(sessionNodeId ?? string.Empty),
            ["userId"] = new ValString(userId ?? string.Empty),
            ["hostOrIp"] = new ValString(string.IsNullOrWhiteSpace(normalizedHostOrIp) ? normalizedRemoteIp : normalizedHostOrIp),
            ["remoteIp"] = new ValString(string.IsNullOrWhiteSpace(normalizedRemoteIp) ? "127.0.0.1" : normalizedRemoteIp),
        };
    }

    private static ValMap CreateRouteMap(IReadOnlyList<ValMap> sessions)
    {
        var routeSessions = new ValList();
        for (var index = 0; index < sessions.Count; index++)
        {
            routeSessions.values.Add(sessions[index]);
        }

        var prefixRoutes = new ValList();
        for (var hopCount = 1; hopCount < sessions.Count; hopCount++)
        {
            var prefixSessions = new ValList();
            for (var index = 0; index < hopCount; index++)
            {
                prefixSessions.values.Add(sessions[index]);
            }

            var prefixRoute = new ValMap
            {
                [KindKey] = new ValString(RouteKind),
                [RouteVersionKey] = new ValNumber(RouteVersion),
                [RouteSessionsKey] = prefixSessions,
                [RoutePrefixRoutesKey] = new ValList(),
                [RouteLastSessionKey] = sessions[hopCount - 1],
                [RouteHopCountKey] = new ValNumber(hopCount),
            };
            prefixRoutes.values.Add(prefixRoute);
        }

        return new ValMap
        {
            [KindKey] = new ValString(RouteKind),
            [RouteVersionKey] = new ValNumber(RouteVersion),
            [RouteSessionsKey] = routeSessions,
            [RoutePrefixRoutesKey] = prefixRoutes,
            [RouteLastSessionKey] = sessions[sessions.Count - 1],
            [RouteHopCountKey] = new ValNumber(sessions.Count),
        };
    }

    private static ValMap CreateConnectSuccessMap(ValMap session, ValMap? route)
    {
        var result = new ValMap
        {
            ["ok"] = ValNumber.one,
            ["code"] = new ValString(SystemCallErrorCode.None.ToString()),
            ["err"] = ValNull.instance,
            ["session"] = session,
        };

        result[RouteKey] = route ?? (Value)null!;
        return result;
    }

    private static ValMap CreateConnectFailureMap(SystemCallErrorCode code, string err)
    {
        var result = new ValMap
        {
            ["ok"] = ValNumber.zero,
            ["code"] = new ValString(code.ToString()),
            ["err"] = new ValString(err),
        };

        // MiniScript compares null by raw value-null checks, so session must be literal null.
        result["session"] = (Value)null!;
        result[RouteKey] = (Value)null!;
        return result;
    }

    private static ValMap CreateDisconnectSuccessMap(bool disconnected, ValMap summary)
    {
        return new ValMap
        {
            ["ok"] = ValNumber.one,
            ["code"] = new ValString(SystemCallErrorCode.None.ToString()),
            ["err"] = ValNull.instance,
            ["disconnected"] = disconnected ? ValNumber.one : ValNumber.zero,
            ["summary"] = summary,
        };
    }

    private static ValMap CreateDisconnectFailureMap(SystemCallErrorCode code, string err)
    {
        return new ValMap
        {
            ["ok"] = ValNumber.zero,
            ["code"] = new ValString(code.ToString()),
            ["err"] = new ValString(err),
            ["disconnected"] = ValNumber.zero,
            ["summary"] = CreateDisconnectSummaryMap(0, 0, 0, 0),
        };
    }

    private static ValMap CreateDisconnectSummaryMap(int requested, int closed, int alreadyClosed, int invalid)
    {
        return new ValMap
        {
            ["requested"] = new ValNumber(requested),
            ["closed"] = new ValNumber(closed),
            ["alreadyClosed"] = new ValNumber(alreadyClosed),
            ["invalid"] = new ValNumber(invalid),
        };
    }

    private static ValMap CreateFtpSuccessMap(string savedTo, int? bytes)
    {
        var result = new ValMap
        {
            ["ok"] = ValNumber.one,
            ["code"] = new ValString(SystemCallErrorCode.None.ToString()),
            ["err"] = ValNull.instance,
            ["savedTo"] = new ValString(savedTo ?? string.Empty),
        };
        result["bytes"] = bytes.HasValue ? new ValNumber(bytes.Value) : (Value)null!;
        return result;
    }

    private static ValMap CreateFtpFailureMap(SystemCallErrorCode code, string err)
    {
        return new ValMap
        {
            ["ok"] = ValNumber.zero,
            ["code"] = new ValString(code.ToString()),
            ["err"] = new ValString(err),
        };
    }

    private static string ExtractErrorText(SystemCallResult result)
    {
        if (result.Lines.Count == 0)
        {
            return "unknown error.";
        }

        var first = result.Lines[0] ?? string.Empty;
        const string prefix = "error:";
        if (first.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return first[prefix.Length..].Trim();
        }

        return first.Trim();
    }

    private readonly record struct FtpEndpoint(
        ServerNodeRuntime Server,
        string NodeId,
        string UserKey,
        string Cwd);

    private sealed class SshModuleState
    {
        private readonly Dictionary<int, SandboxSessionSnapshot> sandboxSessionsById = new();
        private int nextSandboxSessionId = 1;

        internal SshModuleState(SystemCallExecutionContext? executionContext, MiniScriptSshExecutionMode mode)
        {
            ExecutionContext = executionContext;
            Mode = mode;
        }

        internal SystemCallExecutionContext? ExecutionContext { get; }

        internal MiniScriptSshExecutionMode Mode { get; }

        internal int RegisterSandboxSession(
            string sessionNodeId,
            string userKey,
            string userId,
            string hostOrIp,
            string remoteIp,
            string cwd)
        {
            var sessionId = nextSandboxSessionId++;
            sandboxSessionsById[sessionId] = new SandboxSessionSnapshot(
                sessionNodeId,
                sessionId,
                userKey,
                userId,
                hostOrIp,
                remoteIp,
                cwd);
            return sessionId;
        }

        internal bool TryRemoveSandboxSession(string sessionNodeId, int sessionId)
        {
            if (!TryResolveSandboxSession(sessionNodeId, sessionId, out _))
            {
                return false;
            }

            sandboxSessionsById.Remove(sessionId);
            return true;
        }

        internal bool TryResolveSandboxSession(
            string sessionNodeId,
            int sessionId,
            out SandboxSessionSnapshot sandboxSession)
        {
            sandboxSession = default;
            if (!sandboxSessionsById.TryGetValue(sessionId, out var storedSession))
            {
                return false;
            }

            if (!string.Equals(storedSession.SessionNodeId, sessionNodeId, StringComparison.Ordinal))
            {
                return false;
            }

            sandboxSession = storedSession;
            return true;
        }
    }

    private readonly record struct SandboxSessionSnapshot(
        string SessionNodeId,
        int SessionId,
        string UserKey,
        string UserId,
        string HostOrIp,
        string RemoteIp,
        string Cwd);
}
