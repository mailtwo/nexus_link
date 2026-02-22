using Miniscript;
using System;
using System.Collections.Generic;
using System.Text;
using Uplink2.Runtime.Syscalls;

#nullable enable

namespace Uplink2.Runtime.MiniScript;

/// <summary>Registers and injects project-specific SSH intrinsics into MiniScript interpreters.</summary>
internal static partial class MiniScriptSshIntrinsics
{
    private const string SshConnectIntrinsicName = "uplink_ssh_connect";
    private const string SshDisconnectIntrinsicName = "uplink_ssh_disconnect";
    private const string SshExecIntrinsicName = "uplink_ssh_exec";
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
            RegisterSshExecIntrinsic();
            RegisterFtpGetIntrinsic();
            RegisterFtpPutIntrinsic();
            RegisterFsListIntrinsic();
            RegisterFsReadIntrinsic();
            RegisterFsWriteIntrinsic();
            RegisterFsDeleteIntrinsic();
            RegisterFsStatIntrinsic();
            RegisterNetScanIntrinsic();
            RegisterNetPortsIntrinsic();
            RegisterNetBannerIntrinsic();
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
        sshModule["exec"] = Intrinsic.GetByName(SshExecIntrinsicName).GetFunc();
        interpreter.SetGlobalValue("ssh", sshModule);
        InjectFtpModule(interpreter, moduleState);
        InjectFsModule(interpreter, moduleState);
        InjectNetModule(interpreter, moduleState);
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

    private static void RegisterSshExecIntrinsic()
    {
        if (Intrinsic.GetByName(SshExecIntrinsicName) is not null)
        {
            return;
        }

        var intrinsic = Intrinsic.Create(SshExecIntrinsicName);
        intrinsic.AddParam("sessionOrRoute");
        intrinsic.AddParam("cmd");
        intrinsic.AddParam("opts");
        intrinsic.code = (context, partialResult) =>
        {
            if (!TryGetExecutionState(context, out var state) || state.ExecutionContext is null)
            {
                return new Intrinsic.Result(
                    CreateExecFailureMap(
                        SystemCallErrorCode.InternalError,
                        "ssh.exec is unavailable in this execution context."));
            }

            if (!TryParseExecArguments(
                    context,
                    out var sessionOrRouteMap,
                    out var commandLine,
                    out var maxBytes,
                    out var parseError))
            {
                return new Intrinsic.Result(CreateExecFailureMap(SystemCallErrorCode.InvalidArgs, parseError));
            }

            var executionContext = state.ExecutionContext;
            if (!TryResolveExecEndpoint(
                    state,
                    executionContext,
                    sessionOrRouteMap,
                    out var endpoint,
                    out var endpointUserId,
                    out var endpointError))
            {
                return new Intrinsic.Result(CreateExecFailureMap(SystemCallErrorCode.InvalidArgs, endpointError));
            }

            var temporaryTerminalSessionId = executionContext.World.AllocateTerminalSessionId();
            SystemCallResult commandResult;
            try
            {
                commandResult = executionContext.World.ExecuteSystemCall(new SystemCallRequest
                {
                    NodeId = endpoint.NodeId,
                    UserId = endpointUserId,
                    Cwd = endpoint.Cwd,
                    CommandLine = commandLine,
                    TerminalSessionId = temporaryTerminalSessionId,
                });
            }
            finally
            {
                executionContext.World.CleanupTerminalSessionConnections(temporaryTerminalSessionId);
            }

            var stdout = JoinResultLines(commandResult);
            if (maxBytes.HasValue)
            {
                var stdoutBytes = Encoding.UTF8.GetByteCount(stdout);
                if (stdoutBytes > maxBytes.Value)
                {
                    return new Intrinsic.Result(
                        CreateExecFailureMap(
                            SystemCallErrorCode.TooLarge,
                            $"stdout exceeds opts.maxBytes (max={maxBytes.Value}, actual={stdoutBytes})."));
                }
            }

            return new Intrinsic.Result(commandResult.Ok
                ? CreateExecSuccessMap(commandResult.Code, stdout)
                : CreateExecFailureMap(commandResult.Code, ExtractErrorText(commandResult), stdout));
        };
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

    private static bool TryParseExecArguments(
        TAC.Context context,
        out ValMap sessionOrRouteMap,
        out string commandLine,
        out int? maxBytes,
        out string error)
    {
        sessionOrRouteMap = null!;
        commandLine = string.Empty;
        maxBytes = null;
        error = string.Empty;

        if (context.GetLocal("sessionOrRoute") is not ValMap parsedSessionOrRouteMap)
        {
            error = "sessionOrRoute object is required.";
            return false;
        }

        if (!TryReadKind(parsedSessionOrRouteMap, out var kind, out var kindError, "sessionOrRoute"))
        {
            error = kindError;
            return false;
        }

        if (!string.Equals(kind, SessionKind, StringComparison.Ordinal) &&
            !string.Equals(kind, RouteKind, StringComparison.Ordinal))
        {
            error = "sessionOrRoute.kind must be sshSession or sshRoute.";
            return false;
        }

        sessionOrRouteMap = parsedSessionOrRouteMap;
        var rawCommand = context.GetLocal("cmd");
        if (rawCommand is null || rawCommand is ValMap)
        {
            error = "cmd is required.";
            return false;
        }

        commandLine = rawCommand.ToString().Trim();
        if (string.IsNullOrWhiteSpace(commandLine))
        {
            error = "cmd is required.";
            return false;
        }

        ValMap? optsMap = null;
        var rawOpts = context.GetLocal("opts");
        if (rawOpts is null)
        {
            return true;
        }

        if (rawOpts is not ValMap parsedOptsMap)
        {
            error = "opts must be a map.";
            return false;
        }

        optsMap = parsedOptsMap;
        return TryParseExecOpts(optsMap, out maxBytes, out error);
    }

    private static bool TryParseExecOpts(
        ValMap? optsMap,
        out int? maxBytes,
        out string error)
    {
        maxBytes = null;
        error = string.Empty;
        if (optsMap is null)
        {
            return true;
        }

        foreach (var key in optsMap.Keys)
        {
            var keyText = key?.ToString().Trim() ?? string.Empty;
            if (!string.Equals(keyText, "maxBytes", StringComparison.Ordinal))
            {
                error = $"unsupported opts key: {keyText}";
                return false;
            }
        }

        if (!optsMap.TryGetValue("maxBytes", out var rawMaxBytes))
        {
            return true;
        }

        if (rawMaxBytes is null)
        {
            error = "opts.maxBytes must be a non-negative integer.";
            return false;
        }

        try
        {
            var parsedMaxBytes = rawMaxBytes.IntValue();
            if (parsedMaxBytes < 0)
            {
                error = "opts.maxBytes must be a non-negative integer.";
                return false;
            }

            maxBytes = parsedMaxBytes;
            return true;
        }
        catch (Exception)
        {
            error = "opts.maxBytes must be a non-negative integer.";
            return false;
        }
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

    private static bool TryResolveExecEndpoint(
        SshModuleState state,
        SystemCallExecutionContext executionContext,
        ValMap sessionOrRouteMap,
        out FtpEndpoint endpoint,
        out string endpointUserId,
        out string error)
    {
        endpoint = default;
        endpointUserId = string.Empty;
        error = string.Empty;
        if (!TryReadKind(sessionOrRouteMap, out var kind, out var kindError, "sessionOrRoute"))
        {
            error = kindError;
            return false;
        }

        ValMap endpointSessionMap;
        if (string.Equals(kind, SessionKind, StringComparison.Ordinal))
        {
            if (!TryResolveCanonicalSessionMap(state, executionContext, sessionOrRouteMap, out endpointSessionMap, out var sessionError))
            {
                error = sessionError;
                return false;
            }
        }
        else if (string.Equals(kind, RouteKind, StringComparison.Ordinal))
        {
            if (!TryReadRouteSessionMaps(sessionOrRouteMap, out var routeSessions, out var routeError))
            {
                error = routeError;
                return false;
            }

            var canonicalRouteSessions = new List<ValMap>(routeSessions.Count);
            foreach (var routeSession in routeSessions)
            {
                if (!TryResolveCanonicalSessionMap(state, executionContext, routeSession, out var canonicalSession, out var sessionError))
                {
                    error = sessionError;
                    return false;
                }

                canonicalRouteSessions.Add(canonicalSession);
            }

            endpointSessionMap = canonicalRouteSessions[canonicalRouteSessions.Count - 1];
        }
        else
        {
            error = "sessionOrRoute.kind must be sshSession or sshRoute.";
            return false;
        }

        if (!TryResolveSessionFtpEndpoint(state, executionContext, endpointSessionMap, out endpoint, out var endpointError))
        {
            error = endpointError;
            return false;
        }

        endpointUserId = executionContext.World.ResolvePromptUser(endpoint.Server, endpoint.UserKey);
        if (string.IsNullOrWhiteSpace(endpointUserId))
        {
            error = $"user not found: {endpoint.NodeId}/{endpoint.UserKey}.";
            return false;
        }

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

    private static ValMap CreateExecSuccessMap(SystemCallErrorCode code, string stdout)
    {
        return new ValMap
        {
            ["ok"] = ValNumber.one,
            ["code"] = new ValString(code.ToString()),
            ["err"] = ValNull.instance,
            ["stdout"] = new ValString(stdout ?? string.Empty),
            ["exitCode"] = ValNumber.zero,
        };
    }

    private static ValMap CreateExecFailureMap(SystemCallErrorCode code, string err, string stdout = "")
    {
        return new ValMap
        {
            ["ok"] = ValNumber.zero,
            ["code"] = new ValString(code.ToString()),
            ["err"] = new ValString(err),
            ["stdout"] = new ValString(stdout ?? string.Empty),
            ["exitCode"] = ValNumber.one,
        };
    }

    private static string JoinResultLines(SystemCallResult result)
    {
        if (result.Lines.Count == 0)
        {
            return string.Empty;
        }

        return string.Join("\n", result.Lines);
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

