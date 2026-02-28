using Miniscript;
using System;
using System.Collections.Generic;
using Uplink2.Runtime.Syscalls;
using Uplink2.Vfs;
using static Uplink2.Runtime.MiniScript.MiniScriptSshIntrinsics;

#nullable enable

namespace Uplink2.Runtime.MiniScript;

internal static class MiniScriptFtpIntrinsics
{
    private const string FtpGetIntrinsicName = "uplink_ftp_get";
    private const string FtpPutIntrinsicName = "uplink_ftp_put";
    private const int DefaultFtpPort = 21;
    private static readonly object registrationSync = new();
    private static bool isRegistered;

    internal static void EnsureRegistered()
    {
        lock (registrationSync)
        {
            if (isRegistered)
            {
                return;
            }

            FtpGet();
            FtpPut();
            isRegistered = true;
        }
    }

    /// <summary>인터프리터에 ftp 모듈 전역 API를 주입합니다.</summary>
    /// <remarks>
    /// MiniScript: <c>ftp.get(sessionOrRoute, remotePath, localPath?, opts?)</c>, <c>ftp.put(sessionOrRoute, localPath, remotePath?, opts?)</c>.
    /// 각 API는 공통 ResultMap(<c>ok/code/err/cost/trace</c>) 규약을 따르며 payload는 <c>savedTo</c>와 선택적 <c>bytes</c>를 반환합니다.
    /// FTP는 SSH session/route를 인증 근거로 사용하고 target FTP 포트 노출 규칙을 통과해야 합니다.
    /// See: <see href="/api/ftp.html#module-ftp">Manual</see>.
    /// </remarks>
    /// <param name="interpreter">ftp 모듈 전역을 주입할 대상 인터프리터입니다.</param>
    /// <param name="moduleState">session/route 해석과 실행 컨텍스트를 포함한 모듈 상태입니다.</param>
    internal static void InjectFtpModule(Interpreter interpreter, MiniScriptSshIntrinsics.SshModuleState moduleState)
    {
        EnsureRegistered();
        var ftpModule = new ValMap
        {
            userData = moduleState,
        };
        ftpModule["get"] = Intrinsic.GetByName(FtpGetIntrinsicName).GetFunc();
        ftpModule["put"] = Intrinsic.GetByName(FtpPutIntrinsicName).GetFunc();
        interpreter.SetGlobalValue("ftp", ftpModule);
    }

    /// <summary><c>ftp.get</c>는 원격 파일을 로컬 경로로 가져오고 저장 위치/크기 결과를 반환합니다.</summary>
    /// <remarks>
    /// <para><b>MiniScript</b></para>
    /// <para><c>r = ftp.get(sessionOrRoute, remotePath, localPath?, opts?)</c></para>
    /// <para><b>Parameters</b></para>
    /// <list type="bullet">
    /// <item><description><c>sessionOrRoute</c>: 전송 endpoint를 지정하는 <c>sshSession</c> 또는 <c>sshRoute</c>입니다.</description></item>
    /// <item><description><c>remotePath</c>: 원격 endpoint에서 읽을 파일 경로입니다.</description></item>
    /// <item><description><c>localPath</c>(선택): 로컬 endpoint에 저장할 경로입니다. 생략 시 원본 파일명으로 저장됩니다.</description></item>
    /// <item><description><c>opts.port</c>(선택): FTP 포트입니다. 기본값은 <c>21</c>입니다.</description></item>
    /// </list>
    /// <para><b>Returns</b></para>
    /// <list type="bullet">
    /// <item><description>성공: <c>{ ok:1, code:"OK", err:null, savedTo:string, bytes:int|null }</c></description></item>
    /// <item><description>실패: <c>{ ok:0, code:"ERR_*", err:string }</c></description></item>
    /// </list>
    /// <para><b>Note</b></para>
    /// <list type="bullet">
    /// <item><description>지원하는 옵션 키는 <c>port</c> 하나뿐입니다. 다른 키는 <c>ERR_INVALID_ARGS</c>를 반환합니다.</description></item>
    /// <item><description><c>sshSession</c> 모드에서는 실행 컨텍스트를 source, session endpoint를 target으로 사용합니다.</description></item>
    /// <item><description><c>sshRoute</c> 모드에서는 <c>firstSession -&gt; lastSession</c> 링크를 사용해 <c>last -&gt; first</c> 방향으로 다운로드합니다.</description></item>
    /// <item><description>성공 시 <c>fileAcquire</c> 이벤트(<c>transferMethod="ftp"</c>)를 1회 발생시킵니다.</description></item>
    /// </list>
    /// <para><b>See</b>: <see href="/api/ftp.html#ftpget">Manual</see>.</para>
    /// </remarks>
    private static void FtpGet()
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
            if (!TryRunWorldAction(
                    state,
                    () =>
                    {
                        if (!TryResolveFtpEndpoints(
                                state,
                                executionContext,
                                sessionOrRouteMap,
                                out var firstEndpoint,
                                out var lastEndpoint,
                                out var endpointError))
                        {
                            return CreateFtpFailureMap(SystemCallErrorCode.InvalidArgs, endpointError);
                        }

                        if (!executionContext.World.TryValidatePortAccess(
                                firstEndpoint.Server,
                                lastEndpoint.Server,
                                port,
                                PortType.Ftp,
                                out var portFailure))
                        {
                            return CreateFtpFailureMap(portFailure.Code, ExtractErrorText(portFailure));
                        }

                        if (!TryGetFtpEndpointUser(firstEndpoint, out var firstUser, out var firstUserError))
                        {
                            return CreateFtpFailureMap(SystemCallErrorCode.InvalidArgs, firstUserError);
                        }

                        if (!TryGetFtpEndpointUser(lastEndpoint, out var lastUser, out var lastUserError))
                        {
                            return CreateFtpFailureMap(SystemCallErrorCode.InvalidArgs, lastUserError);
                        }

                        if (!lastUser.Privilege.Read || !firstUser.Privilege.Write)
                        {
                            return CreateFtpFailureMap(
                                SystemCallErrorCode.PermissionDenied,
                                "permission denied: ftp get");
                        }

                        var remoteSourcePath = BaseFileSystem.NormalizePath(lastEndpoint.Cwd, remotePathInput);
                        if (!TryReadSourceFile(lastEndpoint.Server, remoteSourcePath, out var sourceEntry, out var sourceContent, out var sourceFailure))
                        {
                            return CreateFtpFailureMap(sourceFailure.Code, ExtractErrorText(sourceFailure));
                        }

                        var sourceFileName = GetFileName(remoteSourcePath);
                        if (string.IsNullOrWhiteSpace(sourceFileName))
                        {
                            return CreateFtpFailureMap(
                                SystemCallErrorCode.InvalidArgs,
                                $"invalid source path: {remotePathInput}");
                        }

                        if (!TryResolveDestinationPath(
                                firstEndpoint.Server,
                                firstEndpoint.Cwd,
                                localPathInput,
                                sourceFileName,
                                out var localDestinationPath,
                                out var destinationFailure))
                        {
                            return CreateFtpFailureMap(
                                destinationFailure!.Code,
                                ExtractErrorText(destinationFailure));
                        }

                        if (!TryWriteDestinationFile(firstEndpoint.Server, localDestinationPath, sourceContent, sourceEntry, out var writeFailure))
                        {
                            return CreateFtpFailureMap(
                                writeFailure!.Code,
                                ExtractErrorText(writeFailure));
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

                        return CreateFtpSuccessMap(localDestinationPath, ToOptionalInt(sourceEntry.Size));
                    },
                    out var getResult,
                    out var queueError))
            {
                return new Intrinsic.Result(
                    CreateFtpFailureMap(
                        SystemCallErrorCode.InternalError,
                        queueError));
            }

            return new Intrinsic.Result(getResult);
        };
    }

    /// <summary><c>ftp.put</c>는 로컬 파일을 원격 경로로 업로드하고 저장 위치/크기 결과를 반환합니다.</summary>
    /// <remarks>
    /// <para><b>MiniScript</b></para>
    /// <para><c>r = ftp.put(sessionOrRoute, localPath, remotePath?, opts?)</c></para>
    /// <para><b>Parameters</b></para>
    /// <list type="bullet">
    /// <item><description><c>sessionOrRoute</c>: 전송 endpoint를 지정하는 <c>sshSession</c> 또는 <c>sshRoute</c>입니다.</description></item>
    /// <item><description><c>localPath</c>: 로컬 endpoint에서 읽을 파일 경로입니다.</description></item>
    /// <item><description><c>remotePath</c>(선택): 원격 endpoint에 저장할 경로입니다. 생략 시 원본 파일명으로 저장됩니다.</description></item>
    /// <item><description><c>opts.port</c>(선택): FTP 포트입니다. 기본값은 <c>21</c>입니다.</description></item>
    /// </list>
    /// <para><b>Returns</b></para>
    /// <list type="bullet">
    /// <item><description>성공: <c>{ ok:1, code:"OK", err:null, savedTo:string, bytes:int|null }</c></description></item>
    /// <item><description>실패: <c>{ ok:0, code:"ERR_*", err:string }</c></description></item>
    /// </list>
    /// <para><b>Note</b></para>
    /// <list type="bullet">
    /// <item><description>지원하는 옵션 키는 <c>port</c> 하나뿐입니다. 다른 키는 <c>ERR_INVALID_ARGS</c>를 반환합니다.</description></item>
    /// <item><description><c>sshSession</c> 모드에서는 실행 컨텍스트를 source, session endpoint를 target으로 사용합니다.</description></item>
    /// <item><description><c>sshRoute</c> 모드에서는 <c>firstSession -&gt; lastSession</c> 링크를 사용해 <c>first -&gt; last</c> 방향으로 업로드합니다.</description></item>
    /// <item><description>업로드 성공 시 <c>fileAcquire</c> 이벤트는 발행하지 않습니다.</description></item>
    /// </list>
    /// <para><b>See</b>: <see href="/api/ftp.html#ftpput">Manual</see>.</para>
    /// </remarks>
    private static void FtpPut()
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
            if (!TryRunWorldAction(
                    state,
                    () =>
                    {
                        if (!TryResolveFtpEndpoints(
                                state,
                                executionContext,
                                sessionOrRouteMap,
                                out var firstEndpoint,
                                out var lastEndpoint,
                                out var endpointError))
                        {
                            return CreateFtpFailureMap(SystemCallErrorCode.InvalidArgs, endpointError);
                        }

                        if (!executionContext.World.TryValidatePortAccess(
                                firstEndpoint.Server,
                                lastEndpoint.Server,
                                port,
                                PortType.Ftp,
                                out var portFailure))
                        {
                            return CreateFtpFailureMap(portFailure.Code, ExtractErrorText(portFailure));
                        }

                        if (!TryGetFtpEndpointUser(firstEndpoint, out var firstUser, out var firstUserError))
                        {
                            return CreateFtpFailureMap(SystemCallErrorCode.InvalidArgs, firstUserError);
                        }

                        if (!TryGetFtpEndpointUser(lastEndpoint, out var lastUser, out var lastUserError))
                        {
                            return CreateFtpFailureMap(SystemCallErrorCode.InvalidArgs, lastUserError);
                        }

                        if (!firstUser.Privilege.Read || !lastUser.Privilege.Write)
                        {
                            return CreateFtpFailureMap(
                                SystemCallErrorCode.PermissionDenied,
                                "permission denied: ftp put");
                        }

                        var localSourcePath = BaseFileSystem.NormalizePath(firstEndpoint.Cwd, localPathInput);
                        if (!TryReadSourceFile(firstEndpoint.Server, localSourcePath, out var sourceEntry, out var sourceContent, out var sourceFailure))
                        {
                            return CreateFtpFailureMap(sourceFailure.Code, ExtractErrorText(sourceFailure));
                        }

                        var sourceFileName = GetFileName(localSourcePath);
                        if (string.IsNullOrWhiteSpace(sourceFileName))
                        {
                            return CreateFtpFailureMap(
                                SystemCallErrorCode.InvalidArgs,
                                $"invalid source path: {localPathInput}");
                        }

                        if (!TryResolveDestinationPath(
                                lastEndpoint.Server,
                                lastEndpoint.Cwd,
                                remotePathInput,
                                sourceFileName,
                                out var remoteDestinationPath,
                                out var destinationFailure))
                        {
                            return CreateFtpFailureMap(
                                destinationFailure!.Code,
                                ExtractErrorText(destinationFailure));
                        }

                        if (!TryWriteDestinationFile(lastEndpoint.Server, remoteDestinationPath, sourceContent, sourceEntry, out var writeFailure))
                        {
                            return CreateFtpFailureMap(
                                writeFailure!.Code,
                                ExtractErrorText(writeFailure));
                        }

                        return CreateFtpSuccessMap(remoteDestinationPath, ToOptionalInt(sourceEntry.Size));
                    },
                    out var putResult,
                    out var queueError))
            {
                return new Intrinsic.Result(
                    CreateFtpFailureMap(
                        SystemCallErrorCode.InternalError,
                        queueError));
            }

            return new Intrinsic.Result(putResult);
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
        MiniScriptSshIntrinsics.SshModuleState state,
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

        if (!TryResolveSessionSourceFtpEndpoint(
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

    internal static bool TryResolveExecutionContextFtpEndpoint(
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

    internal static bool TryResolveSessionSourceFtpEndpoint(
        SystemCallExecutionContext executionContext,
        ValMap sessionMap,
        out FtpEndpoint endpoint,
        out string error)
    {
        endpoint = default;
        error = string.Empty;
        if (!TryReadSessionIdentity(
                sessionMap,
                out _,
                out _,
                out var sourceMetadata,
                out var readError))
        {
            error = readError;
            return false;
        }

        return TryResolveSessionSourceFtpEndpoint(
            executionContext,
            sourceMetadata,
            out endpoint,
            out error);
    }

    internal static bool TryResolveSessionSourceFtpEndpoint(
        SystemCallExecutionContext executionContext,
        MiniScriptSshIntrinsics.SessionSourceMetadata sourceMetadata,
        out FtpEndpoint endpoint,
        out string error)
    {
        endpoint = default;
        error = string.Empty;
        var sourceNodeId = sourceMetadata.SourceNodeId?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(sourceNodeId))
        {
            error = "session.sourceNodeId is required.";
            return false;
        }

        if (!executionContext.World.TryGetServer(sourceNodeId, out var sourceServer))
        {
            error = $"source session server not found: {sourceNodeId}.";
            return false;
        }

        var sourceUserId = sourceMetadata.SourceUserId?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(sourceUserId))
        {
            error = "session.sourceUserId is required.";
            return false;
        }

        if (!executionContext.World.TryResolveUserKeyByUserId(sourceServer, sourceUserId, out var sourceUserKey))
        {
            error = $"source session user not found: {sourceNodeId}/{sourceUserId}.";
            return false;
        }

        if (!sourceServer.Users.ContainsKey(sourceUserKey))
        {
            error = $"source session user not found: {sourceNodeId}/{sourceUserId}.";
            return false;
        }

        endpoint = new FtpEndpoint(
            sourceServer,
            sourceNodeId,
            sourceUserKey,
            BaseFileSystem.NormalizePath("/", sourceMetadata.SourceCwd));
        return true;
    }

    internal static bool TryResolveSessionFtpEndpoint(
        MiniScriptSshIntrinsics.SshModuleState state,
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

    internal static bool TryGetFtpEndpointUser(
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

    internal static string GetParentPath(string normalizedPath)
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

    internal static int? ToOptionalInt(long value)
    {
        return value is < int.MinValue or > int.MaxValue
            ? null
            : (int)value;
    }

    private static ValMap CreateFtpSuccessMap(string savedTo, int? bytes)
    {
        var result = new ValMap
        {
            ["ok"] = ValNumber.one,
            ["code"] = new ValString(SystemCallErrorCodeTokenMapper.ToApiToken(SystemCallErrorCode.None)),
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
            ["code"] = new ValString(SystemCallErrorCodeTokenMapper.ToApiToken(code)),
            ["err"] = new ValString(err),
        };
    }

    internal readonly record struct FtpEndpoint(
        ServerNodeRuntime Server,
        string NodeId,
        string UserKey,
        string Cwd);
}

