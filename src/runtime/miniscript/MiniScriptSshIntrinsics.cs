using Miniscript;
using System;
using System.Collections.Generic;
using System.Text;
using Uplink2.Runtime.Syscalls;
using Uplink2.Vfs;

#nullable enable

namespace Uplink2.Runtime.MiniScript;

/// <summary>Registers and injects project-specific SSH intrinsics into MiniScript interpreters.</summary>
internal static partial class MiniScriptSshIntrinsics
{
    private const string SshConnectIntrinsicName = "uplink_ssh_connect";
    private const string SshDisconnectIntrinsicName = "uplink_ssh_disconnect";
    private const string SshExecIntrinsicName = "uplink_ssh_exec";
    private const string SshInspectIntrinsicName = "uplink_ssh_inspect";
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
    private const string SessionSourceNodeIdKey = "sourceNodeId";
    private const string SessionSourceUserIdKey = "sourceUserId";
    private const string SessionSourceCwdKey = "sourceCwd";
    private const string ExecutableHardcodePrefix = "exec:";
    private const string InspectExecutableId = "inspect";
    private const string InspectPathDirectory = "/opt/bin";
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
            RegisterSshInspectIntrinsic();
            RegisterFtpGetIntrinsic();
            RegisterFtpPutIntrinsic();
            RegisterFsListIntrinsic();
            RegisterFsReadIntrinsic();
            RegisterFsWriteIntrinsic();
            RegisterFsDeleteIntrinsic();
            RegisterFsStatIntrinsic();
            RegisterNetInterfacesIntrinsic();
            RegisterNetScanIntrinsic();
            RegisterNetPortsIntrinsic();
            RegisterNetBannerIntrinsic();
            isRegistered = true;
        }
    }

    /// <summary>인터프리터에 ssh 모듈과 연계 모듈 전역 API를 주입합니다.</summary>
    /// <remarks>
    /// MiniScript(ssh): <c>ssh.connect(hostOrIp, userId, password, port=22, opts?)</c>, <c>ssh.disconnect(sessionOrRoute)</c>, <c>ssh.exec(sessionOrRoute, cmd, opts?)</c>, <c>ssh.inspect(hostOrIp, userId, port=22, opts?)</c>.
    /// 각 API는 공통 ResultMap(<c>ok/code/err/cost/trace</c>) 규약을 따르며, 본 주입 단계에서 <c>ftp</c>, <c>fs</c>, <c>net</c> 모듈도 함께 연결됩니다.
    /// 실행 모드에 따라 실제 월드 세션 생성 또는 샌드박스 검증 경로를 사용합니다.
    /// See: <see href="/docfx_api_document/api/ssh.md#module-ssh">Manual</see>.
    /// </remarks>
    /// <param name="interpreter">API 전역을 주입할 대상 인터프리터입니다.</param>
    /// <param name="executionContext">네트워크/시스템 호출 실행에 사용할 현재 실행 컨텍스트입니다.</param>
    /// <param name="mode">SSH 실행 모드(실세계 세션 생성 또는 샌드박스 검증)를 지정합니다.</param>
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
        sshModule["inspect"] = Intrinsic.GetByName(SshInspectIntrinsicName).GetFunc();
        interpreter.SetGlobalValue("ssh", sshModule);
        InjectFtpModule(interpreter, moduleState);
        InjectFsModule(interpreter, moduleState);
        InjectNetModule(interpreter, moduleState);
    }

    /// <summary><c>ssh.connect</c> intrinsic을 등록합니다.</summary>
    /// <remarks>
    /// MiniScript: <c>r = ssh.connect(hostOrIp, userId, password, port=22, opts?)</c>.
    /// ResultMap(<c>ok/code/err/cost/trace</c>)에 payload(<c>session</c>, 체인 연결 시 <c>route</c>)를 반환합니다.
    /// 노출 규칙/인증/레이트리밋을 검사하며 성공 시 권한 획득 이벤트를 발생시킵니다.
    /// See: <see href="/docfx_api_document/api/ssh.md#sshconnect">Manual</see>.
    /// </remarks>
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

            if (!TryResolveConnectSessionSourceMetadata(
                    state,
                    executionContext,
                    routeRequested,
                    parentSessions,
                    out var connectSourceMetadata,
                    out var connectSourceError))
            {
                return new Intrinsic.Result(
                    CreateConnectFailureMap(
                        SystemCallErrorCode.InvalidArgs,
                        connectSourceError));
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

                var session = CreateSessionMap(openResult, connectSourceMetadata);
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

            // Sandbox mode validates SSH routing/auth without creating persistent world sessions,
            // but scenario logic still needs privilegeAcquire events on successful login.
            executionContext.World.EmitPrivilegeAcquireForLogin(validated.TargetNodeId, validated.TargetUserKey, "ssh.connect");

            var sandboxSessionId = state.RegisterSandboxSession(
                validated.TargetNodeId,
                validated.TargetUserKey,
                validated.TargetUserId,
                validated.HostOrIp,
                validated.RemoteIp,
                "/",
                connectSourceMetadata.SourceNodeId,
                connectSourceMetadata.SourceUserId,
                connectSourceMetadata.SourceCwd);
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
            var sandboxSession = CreateSessionMap(sandboxOpenResult, connectSourceMetadata);
            var sandboxRoute = routeRequested
                ? CreateRouteMap(AppendRouteSession(parentSessions, sandboxSession))
                : null;
            return new Intrinsic.Result(CreateConnectSuccessMap(sandboxSession, sandboxRoute));
        };
    }

    /// <summary><c>ssh.disconnect</c> intrinsic을 등록합니다.</summary>
    /// <remarks>
    /// MiniScript: <c>r = ssh.disconnect(sessionOrRoute)</c>.
    /// ResultMap(<c>ok/code/err/cost/trace</c>)에 payload(<c>disconnected</c>, <c>summary</c>)를 반환합니다.
    /// route 입력 시 hop을 역순 dedupe 처리하는 best-effort 해제 정책을 따릅니다.
    /// See: <see href="/docfx_api_document/api/ssh.md#sshdisconnect">Manual</see>.
    /// </remarks>
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

    /// <summary><c>ssh.exec</c> intrinsic을 등록합니다.</summary>
    /// <remarks>
    /// MiniScript: <c>r = ssh.exec(sessionOrRoute, cmd, opts?)</c>.
    /// ResultMap(<c>ok/code/err/cost/trace</c>)에 payload(<c>stdout/exitCode/jobId</c>)를 반환합니다.
    /// <c>opts.async=true</c>일 때는 원격 명령 완료가 아니라 비동기 스케줄 결과를 보고합니다.
    /// See: <see href="/docfx_api_document/api/ssh.md#sshexec">Manual</see>.
    /// </remarks>
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
                    out var asyncExecution,
                    out var parseError))
            {
                return new Intrinsic.Result(
                    CreateExecFailureMap(
                        SystemCallErrorCode.InvalidArgs,
                        parseError,
                        asyncExecution: asyncExecution));
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
            var request = new SystemCallRequest
            {
                NodeId = endpoint.NodeId,
                UserId = endpointUserId,
                Cwd = endpoint.Cwd,
                CommandLine = commandLine,
                TerminalSessionId = temporaryTerminalSessionId,
            };
            if (asyncExecution)
            {
                if (!executionContext.World.TryStartAsyncExecJob(request, out var jobId, out var failureResult))
                {
                    executionContext.World.CleanupTerminalSessionConnections(temporaryTerminalSessionId);
                    return new Intrinsic.Result(
                        CreateExecFailureMap(
                            failureResult.Code,
                            ExtractErrorText(failureResult),
                            asyncExecution: true));
                }

                return new Intrinsic.Result(
                    CreateExecSuccessMap(
                        SystemCallErrorCode.None,
                        stdout: string.Empty,
                        asyncExecution: true,
                        jobId: jobId));
            }

            SystemCallResult commandResult;
            try
            {
                commandResult = executionContext.World.ExecuteSystemCall(request);
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
                ? CreateExecSuccessMap(commandResult.Code, stdout, asyncExecution: false, jobId: null)
                : CreateExecFailureMap(
                    commandResult.Code,
                    ExtractErrorText(commandResult),
                    asyncExecution: false,
                    stdout));
        };
    }

    /// <summary><c>ssh.inspect</c> intrinsic을 등록합니다.</summary>
    /// <remarks>
    /// MiniScript: <c>r = ssh.inspect(hostOrIp, userId, port=22, opts?)</c>.
    /// ResultMap(<c>ok/code/err/cost/trace</c>)에 payload(<c>hostOrIp/port/userId/banner/passwdInfo</c>)를 반환합니다.
    /// 실행 전 <c>inspect</c> 공식 프로그램 preflight를 검사하며 실패 시 도구/권한 오류를 반환합니다.
    /// See: <see href="/docfx_api_document/api/ssh.md#sshinspect">Manual</see>.
    /// </remarks>
    private static void RegisterSshInspectIntrinsic()
    {
        if (Intrinsic.GetByName(SshInspectIntrinsicName) is not null)
        {
            return;
        }

        var intrinsic = Intrinsic.Create(SshInspectIntrinsicName);
        intrinsic.AddParam("hostOrIp");
        intrinsic.AddParam("userId");
        intrinsic.AddParam("port", 22);
        intrinsic.AddParam("opts");
        intrinsic.code = (context, partialResult) =>
        {
            if (!TryGetExecutionState(context, out var state) || state.ExecutionContext is null)
            {
                return new Intrinsic.Result(
                    CreateInspectFailureMap(
                        SystemCallErrorCode.InternalError,
                        "ssh.inspect is unavailable in this execution context."));
            }

            if (!TryParseInspectArguments(
                    context,
                    out var hostOrIp,
                    out var userId,
                    out var port,
                    out _))
            {
                return new Intrinsic.Result(
                    CreateInspectFailureMap(
                        SystemCallErrorCode.InvalidArgs,
                        WorldRuntime.ToInspectProbeHumanMessage(SystemCallErrorCode.InvalidArgs)));
            }

            var executionContext = state.ExecutionContext;
            if (!TryValidateInspectToolPreflight(
                    executionContext,
                    out var preflightErrorCode,
                    out var preflightError))
            {
                return new Intrinsic.Result(CreateInspectFailureMap(preflightErrorCode, preflightError));
            }

            if (!executionContext.World.TryRunInspectProbe(
                    executionContext.Server,
                    hostOrIp,
                    userId,
                    port,
                    out var inspectResult,
                    out var failureResult))
            {
                return new Intrinsic.Result(CreateInspectFailureMap(failureResult.Code, ExtractErrorText(failureResult)));
            }

            return new Intrinsic.Result(CreateInspectSuccessMap(inspectResult));
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
        out bool asyncExecution,
        out string error)
    {
        sessionOrRouteMap = null!;
        commandLine = string.Empty;
        maxBytes = null;
        asyncExecution = false;
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
        return TryParseExecOpts(optsMap, out maxBytes, out asyncExecution, out error);
    }

    private static bool TryParseInspectArguments(
        TAC.Context context,
        out string hostOrIp,
        out string userId,
        out int port,
        out string error)
    {
        hostOrIp = string.Empty;
        userId = string.Empty;
        port = 22;
        error = string.Empty;

        var rawHostOrIp = context.GetLocal("hostOrIp");
        if (rawHostOrIp is null || rawHostOrIp is ValMap)
        {
            error = "hostOrIp is required.";
            return false;
        }

        hostOrIp = rawHostOrIp.ToString().Trim();
        if (string.IsNullOrWhiteSpace(hostOrIp))
        {
            error = "hostOrIp is required.";
            return false;
        }

        var rawUserId = context.GetLocal("userId");
        if (rawUserId is null || rawUserId is ValMap)
        {
            error = "userId is required.";
            return false;
        }

        userId = rawUserId.ToString().Trim();
        if (string.IsNullOrWhiteSpace(userId))
        {
            error = "userId is required.";
            return false;
        }

        var rawPort = context.GetLocal("port");
        var rawOpts = context.GetLocal("opts");
        ValMap? optsMap = null;
        if (rawPort is ValMap optsFromPort)
        {
            if (rawOpts is not null)
            {
                error = "opts must be omitted when port argument is already a map.";
                return false;
            }

            optsMap = optsFromPort;
        }
        else
        {
            if (!TryReadPort(rawPort, out port, out error))
            {
                return false;
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

        return TryParseInspectOpts(optsMap, out error);
    }

    private static bool TryParseInspectOpts(ValMap? optsMap, out string error)
    {
        error = string.Empty;
        if (optsMap is null)
        {
            return true;
        }

        foreach (var key in optsMap.Keys)
        {
            var keyText = key?.ToString().Trim() ?? string.Empty;
            error = $"unsupported opts key: {keyText}";
            return false;
        }

        return true;
    }

    private static bool TryValidateInspectToolPreflight(
        SystemCallExecutionContext executionContext,
        out SystemCallErrorCode errorCode,
        out string errorMessage)
    {
        errorCode = SystemCallErrorCode.None;
        errorMessage = string.Empty;
        if (!TryResolveInspectToolExecutable(executionContext, out var inspectToolPath, out var inspectToolEntry))
        {
            errorCode = SystemCallErrorCode.ToolMissing;
            errorMessage = WorldRuntime.ToInspectProbeHumanMessage(errorCode);
            return false;
        }

        if (inspectToolEntry.FileKind != VfsFileKind.ExecutableHardcode)
        {
            errorCode = SystemCallErrorCode.ToolMissing;
            errorMessage = WorldRuntime.ToInspectProbeHumanMessage(errorCode);
            return false;
        }

        if (!executionContext.Server.DiskOverlay.TryReadFileText(inspectToolPath, out var executablePayload) ||
            !TryParseExecutableHardcodePayload(executablePayload, out var executableId) ||
            !string.Equals(executableId, InspectExecutableId, StringComparison.Ordinal))
        {
            errorCode = SystemCallErrorCode.ToolMissing;
            errorMessage = WorldRuntime.ToInspectProbeHumanMessage(errorCode);
            return false;
        }

        if (!executionContext.User.Privilege.Read || !executionContext.User.Privilege.Execute)
        {
            errorCode = SystemCallErrorCode.PermissionDenied;
            errorMessage = WorldRuntime.ToInspectProbeHumanMessage(errorCode);
            return false;
        }

        return true;
    }

    private static bool TryResolveInspectToolExecutable(
        SystemCallExecutionContext executionContext,
        out string resolvedPath,
        out VfsEntryMeta resolvedEntry)
    {
        resolvedPath = string.Empty;
        resolvedEntry = null!;

        var candidatePaths = new[]
        {
            BaseFileSystem.NormalizePath(executionContext.Cwd, InspectExecutableId),
            BaseFileSystem.NormalizePath("/", InspectPathDirectory + "/" + InspectExecutableId),
        };
        var seenPaths = new HashSet<string>(StringComparer.Ordinal);
        foreach (var candidatePath in candidatePaths)
        {
            if (!seenPaths.Add(candidatePath))
            {
                continue;
            }

            if (!executionContext.Server.DiskOverlay.TryResolveEntry(candidatePath, out var entry))
            {
                continue;
            }

            if (entry.EntryKind != VfsEntryKind.File || !entry.IsDirectExecutable())
            {
                continue;
            }

            resolvedPath = candidatePath;
            resolvedEntry = entry;
            return true;
        }

        return false;
    }

    private static bool TryParseExecutableHardcodePayload(string rawContentId, out string executableId)
    {
        executableId = string.Empty;
        var trimmed = rawContentId?.Trim() ?? string.Empty;
        if (!trimmed.StartsWith(ExecutableHardcodePrefix, StringComparison.Ordinal))
        {
            return false;
        }

        var parsedId = trimmed[ExecutableHardcodePrefix.Length..];
        if (string.IsNullOrWhiteSpace(parsedId))
        {
            return false;
        }

        executableId = parsedId;
        return true;
    }

    private static bool TryParseExecOpts(
        ValMap? optsMap,
        out int? maxBytes,
        out bool asyncExecution,
        out string error)
    {
        maxBytes = null;
        asyncExecution = false;
        error = string.Empty;
        if (optsMap is null)
        {
            return true;
        }

        foreach (var key in optsMap.Keys)
        {
            var keyText = key?.ToString().Trim() ?? string.Empty;
            if (!string.Equals(keyText, "maxBytes", StringComparison.Ordinal) &&
                !string.Equals(keyText, "async", StringComparison.Ordinal))
            {
                error = $"unsupported opts key: {keyText}";
                return false;
            }
        }

        if (optsMap.TryGetValue("async", out var rawAsync))
        {
            asyncExecution = true;
            if (!TryParseAsyncExecFlag(rawAsync, out var parsedAsyncExecution))
            {
                error = "opts.async must be 0 or 1.";
                return false;
            }

            asyncExecution = parsedAsyncExecution;
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

    private static bool TryParseAsyncExecFlag(Value rawAsync, out bool asyncExecution)
    {
        asyncExecution = false;
        if (rawAsync is null)
        {
            return false;
        }

        try
        {
            var parsed = rawAsync.IntValue();
            if (parsed == 0)
            {
                asyncExecution = false;
                return true;
            }

            if (parsed == 1)
            {
                asyncExecution = true;
                return true;
            }

            return false;
        }
        catch (Exception)
        {
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

    private static bool TryResolveConnectSessionSourceMetadata(
        SshModuleState state,
        SystemCallExecutionContext executionContext,
        bool routeRequested,
        IReadOnlyList<ValMap> parentSessions,
        out SessionSourceMetadata sourceMetadata,
        out string error)
    {
        sourceMetadata = default;
        error = string.Empty;
        FtpEndpoint sourceEndpoint;
        if (!routeRequested)
        {
            if (!TryResolveExecutionContextFtpEndpoint(executionContext, out sourceEndpoint, out var executionEndpointError))
            {
                error = executionEndpointError;
                return false;
            }
        }
        else
        {
            if (parentSessions.Count == 0)
            {
                error = "opts.session must include at least one session.";
                return false;
            }

            var sourceSession = parentSessions[parentSessions.Count - 1];
            if (!TryResolveSessionFtpEndpoint(state, executionContext, sourceSession, out sourceEndpoint, out var sessionEndpointError))
            {
                error = sessionEndpointError;
                return false;
            }
        }

        var sourceUserId = executionContext.World.ResolvePromptUser(sourceEndpoint.Server, sourceEndpoint.UserKey);
        if (string.IsNullOrWhiteSpace(sourceUserId))
        {
            error = $"source session user not found: {sourceEndpoint.NodeId}/{sourceEndpoint.UserKey}.";
            return false;
        }

        sourceMetadata = new SessionSourceMetadata(
            sourceEndpoint.NodeId,
            sourceUserId,
            sourceEndpoint.Cwd);
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
        if (!TryReadSessionIdentity(
                inputSessionMap,
                out var sessionNodeId,
                out var sessionId,
                out var sourceMetadata,
                out var readError))
        {
            error = readError;
            return false;
        }

        if (!TryResolveSessionSourceFtpEndpoint(executionContext, sourceMetadata, out var canonicalSourceEndpoint, out var sourceError))
        {
            error = sourceError;
            return false;
        }

        var canonicalSourceUserId = executionContext.World.ResolvePromptUser(
            canonicalSourceEndpoint.Server,
            canonicalSourceEndpoint.UserKey);
        if (string.IsNullOrWhiteSpace(canonicalSourceUserId))
        {
            error = $"source session user not found: {canonicalSourceEndpoint.NodeId}/{canonicalSourceEndpoint.UserKey}.";
            return false;
        }

        var canonicalSourceMetadata = new SessionSourceMetadata(
            canonicalSourceEndpoint.NodeId,
            canonicalSourceUserId,
            canonicalSourceEndpoint.Cwd);

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
            canonicalSession = CreateSessionMap(
                sessionNodeId,
                sessionId,
                userId,
                hostOrIp,
                remoteIp,
                canonicalSourceMetadata);
            return true;
        }

        if (!state.TryResolveSandboxSession(sessionNodeId, sessionId, out var sandboxSession))
        {
            error = $"session not found: {sessionNodeId}/{sessionId}.";
            return false;
        }

        var sandboxSourceMetadata = new SessionSourceMetadata(
            sandboxSession.SourceNodeId,
            sandboxSession.SourceUserId,
            sandboxSession.SourceCwd);
        if (!TryResolveSessionSourceFtpEndpoint(executionContext, sandboxSourceMetadata, out var sandboxSourceEndpoint, out var sandboxSourceError))
        {
            error = sandboxSourceError;
            return false;
        }

        var sandboxSourceUserId = executionContext.World.ResolvePromptUser(
            sandboxSourceEndpoint.Server,
            sandboxSourceEndpoint.UserKey);
        if (string.IsNullOrWhiteSpace(sandboxSourceUserId))
        {
            error = $"source session user not found: {sandboxSourceEndpoint.NodeId}/{sandboxSourceEndpoint.UserKey}.";
            return false;
        }

        canonicalSession = CreateSessionMap(
            sandboxSession.SessionNodeId,
            sandboxSession.SessionId,
            sandboxSession.UserId,
            sandboxSession.HostOrIp,
            sandboxSession.RemoteIp,
            new SessionSourceMetadata(
                sandboxSourceEndpoint.NodeId,
                sandboxSourceUserId,
                sandboxSourceEndpoint.Cwd));
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
        if (!TryReadSessionIdentity(
                leftSession,
                out var leftNodeId,
                out var leftSessionId,
                out var leftSourceMetadata,
                out _) ||
            !TryReadSessionIdentity(
                rightSession,
                out var rightNodeId,
                out var rightSessionId,
                out var rightSourceMetadata,
                out _))
        {
            return false;
        }

        return string.Equals(leftNodeId, rightNodeId, StringComparison.Ordinal) &&
               leftSessionId == rightSessionId &&
               string.Equals(leftSourceMetadata.SourceNodeId, rightSourceMetadata.SourceNodeId, StringComparison.Ordinal) &&
               string.Equals(leftSourceMetadata.SourceUserId, rightSourceMetadata.SourceUserId, StringComparison.Ordinal) &&
               string.Equals(leftSourceMetadata.SourceCwd, rightSourceMetadata.SourceCwd, StringComparison.Ordinal);
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
        out SessionSourceMetadata sourceMetadata,
        out string error)
    {
        sessionNodeId = string.Empty;
        sessionId = 0;
        sourceMetadata = default;
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

        if (!TryReadRequiredSessionText(
                sessionMap,
                SessionSourceNodeIdKey,
                out var sourceNodeId,
                out var sourceNodeIdError,
                "session"))
        {
            error = sourceNodeIdError;
            return false;
        }

        if (!TryReadRequiredSessionText(
                sessionMap,
                SessionSourceUserIdKey,
                out var sourceUserId,
                out var sourceUserIdError,
                "session"))
        {
            error = sourceUserIdError;
            return false;
        }

        if (!TryReadRequiredSessionText(
                sessionMap,
                SessionSourceCwdKey,
                out var sourceCwd,
                out var sourceCwdError,
                "session"))
        {
            error = sourceCwdError;
            return false;
        }

        sourceMetadata = new SessionSourceMetadata(
            sourceNodeId,
            sourceUserId,
            BaseFileSystem.NormalizePath("/", sourceCwd));
        return true;
    }

    private static bool TryReadSessionIdentity(
        ValMap sessionMap,
        out string sessionNodeId,
        out int sessionId,
        out string error)
    {
        return TryReadSessionIdentity(
            sessionMap,
            out sessionNodeId,
            out sessionId,
            out _,
            out error);
    }

    private static bool TryReadRequiredSessionText(
        ValMap map,
        string key,
        out string value,
        out string error,
        string scope)
    {
        value = string.Empty;
        error = string.Empty;
        if (!map.TryGetValue(key, out var rawValue) || rawValue is null)
        {
            error = $"{scope}.{key} is required.";
            return false;
        }

        value = rawValue.ToString().Trim();
        if (string.IsNullOrWhiteSpace(value))
        {
            error = $"{scope}.{key} is required.";
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

    private static ValMap CreateSessionMap(
        WorldRuntime.SshSessionOpenResult openResult,
        SessionSourceMetadata sourceMetadata)
    {
        return CreateSessionMap(
            openResult.TargetNodeId,
            openResult.SessionId,
            openResult.TargetUserId,
            openResult.HostOrIp,
            openResult.RemoteIp,
            sourceMetadata);
    }

    private static ValMap CreateSessionMap(
        string sessionNodeId,
        int sessionId,
        string userId,
        string hostOrIp,
        string remoteIp,
        SessionSourceMetadata sourceMetadata)
    {
        var normalizedHostOrIp = hostOrIp?.Trim() ?? string.Empty;
        var normalizedRemoteIp = remoteIp?.Trim() ?? string.Empty;
        return new ValMap
        {
            [KindKey] = new ValString(SessionKind),
            [SessionIdKey] = new ValNumber(sessionId),
            [SessionNodeIdKey] = new ValString(sessionNodeId ?? string.Empty),
            [SessionSourceNodeIdKey] = new ValString(sourceMetadata.SourceNodeId ?? string.Empty),
            [SessionSourceUserIdKey] = new ValString(sourceMetadata.SourceUserId ?? string.Empty),
            [SessionSourceCwdKey] = new ValString(sourceMetadata.SourceCwd ?? "/"),
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
            ["code"] = new ValString(SystemCallErrorCodeTokenMapper.ToApiToken(SystemCallErrorCode.None)),
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
            ["code"] = new ValString(SystemCallErrorCodeTokenMapper.ToApiToken(code)),
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
            ["code"] = new ValString(SystemCallErrorCodeTokenMapper.ToApiToken(SystemCallErrorCode.None)),
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
            ["code"] = new ValString(SystemCallErrorCodeTokenMapper.ToApiToken(code)),
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

    private static ValMap CreateExecSuccessMap(
        SystemCallErrorCode code,
        string stdout,
        bool asyncExecution,
        string? jobId)
    {
        var result = new ValMap
        {
            ["ok"] = ValNumber.one,
            ["code"] = new ValString(SystemCallErrorCodeTokenMapper.ToApiToken(code)),
            ["err"] = ValNull.instance,
        };

        if (asyncExecution)
        {
            result["stdout"] = (Value)null!;
            result["exitCode"] = (Value)null!;
            result["jobId"] = new ValString(jobId ?? string.Empty);
            return result;
        }

        result["stdout"] = new ValString(stdout ?? string.Empty);
        result["exitCode"] = ValNumber.zero;
        result["jobId"] = (Value)null!;
        return result;
    }

    private static ValMap CreateExecFailureMap(
        SystemCallErrorCode code,
        string err,
        bool asyncExecution = false,
        string stdout = "")
    {
        var result = new ValMap
        {
            ["ok"] = ValNumber.zero,
            ["code"] = new ValString(SystemCallErrorCodeTokenMapper.ToApiToken(code)),
            ["err"] = new ValString(err),
        };

        if (asyncExecution)
        {
            result["stdout"] = (Value)null!;
            result["exitCode"] = (Value)null!;
            result["jobId"] = (Value)null!;
            return result;
        }

        result["stdout"] = new ValString(stdout ?? string.Empty);
        result["exitCode"] = ValNumber.one;
        result["jobId"] = (Value)null!;
        return result;
    }

    private static ValMap CreateInspectSuccessMap(WorldRuntime.InspectProbeResult inspectResult)
    {
        var result = new ValMap
        {
            ["ok"] = ValNumber.one,
            ["code"] = new ValString(ToInspectCodeToken(SystemCallErrorCode.None)),
            ["err"] = (Value)null!,
            ["hostOrIp"] = new ValString(inspectResult.HostOrIp ?? string.Empty),
            ["port"] = new ValNumber(inspectResult.Port),
            ["userId"] = new ValString(inspectResult.UserId ?? string.Empty),
            ["passwdInfo"] = CreateInspectPasswdInfoMap(inspectResult.PasswdInfo),
        };
        result["banner"] = string.IsNullOrWhiteSpace(inspectResult.Banner)
            ? (Value)null!
            : new ValString(inspectResult.Banner.Trim());
        return result;
    }

    private static ValMap CreateInspectFailureMap(SystemCallErrorCode code, string err)
    {
        var normalizedMessage = string.IsNullOrWhiteSpace(err)
            ? WorldRuntime.ToInspectProbeHumanMessage(code)
            : err.Trim();
        return new ValMap
        {
            ["ok"] = ValNumber.zero,
            ["code"] = new ValString(ToInspectCodeToken(code)),
            ["err"] = new ValString(normalizedMessage),
        };
    }

    private static ValMap CreateInspectPasswdInfoMap(WorldRuntime.InspectPasswdInfo passwdInfo)
    {
        var normalizedKind = passwdInfo.Kind?.Trim();
        var map = new ValMap
        {
            ["kind"] = new ValString(string.IsNullOrWhiteSpace(normalizedKind) ? "none" : normalizedKind),
        };
        if (passwdInfo.Length.HasValue)
        {
            map["length"] = new ValNumber(passwdInfo.Length.Value);
        }

        if (!string.IsNullOrWhiteSpace(passwdInfo.AlphabetId))
        {
            map["alphabetId"] = new ValString(passwdInfo.AlphabetId.Trim());
        }

        if (!string.IsNullOrWhiteSpace(passwdInfo.Alphabet))
        {
            map["alphabet"] = new ValString(passwdInfo.Alphabet);
        }

        return map;
    }

    private static string ToInspectCodeToken(SystemCallErrorCode code)
    {
        return SystemCallErrorCodeTokenMapper.ToApiToken(code);
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

    private readonly record struct SessionSourceMetadata(
        string SourceNodeId,
        string SourceUserId,
        string SourceCwd);

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
            string cwd,
            string sourceNodeId,
            string sourceUserId,
            string sourceCwd)
        {
            var sessionId = nextSandboxSessionId++;
            sandboxSessionsById[sessionId] = new SandboxSessionSnapshot(
                sessionNodeId,
                sessionId,
                userKey,
                userId,
                hostOrIp,
                remoteIp,
                cwd,
                sourceNodeId,
                sourceUserId,
                sourceCwd);
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
        string Cwd,
        string SourceNodeId,
        string SourceUserId,
        string SourceCwd);
}

