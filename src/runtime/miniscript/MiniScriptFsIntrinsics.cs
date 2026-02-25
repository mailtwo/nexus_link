using Miniscript;
using System;
using System.Collections.Generic;
using Uplink2.Runtime.Syscalls;
using Uplink2.Vfs;

#nullable enable

namespace Uplink2.Runtime.MiniScript;

internal static partial class MiniScriptSshIntrinsics
{
    private const string FsListIntrinsicName = "uplink_fs_list";
    private const string FsReadIntrinsicName = "uplink_fs_read";
    private const string FsWriteIntrinsicName = "uplink_fs_write";
    private const string FsDeleteIntrinsicName = "uplink_fs_delete";
    private const string FsStatIntrinsicName = "uplink_fs_stat";

    /// <summary>인터프리터에 fs 모듈 전역 API를 주입합니다.</summary>
    /// <remarks>
    /// MiniScript: <c>fs.list([sessionOrRoute], path)</c>, <c>fs.read([sessionOrRoute], path, opts?)</c>, <c>fs.write([sessionOrRoute], path, text, opts?)</c>, <c>fs.delete([sessionOrRoute], path)</c>, <c>fs.stat([sessionOrRoute], path)</c>.
    /// 각 API는 공통 ResultMap(<c>ok/code/err/cost/trace</c>) 규약을 따르며 payload는 함수별 최상위 필드로 반환됩니다.
    /// session/route 입력 시 endpoint는 항상 <c>route.lastSession</c> 또는 지정 session 기준으로 해석됩니다.
    /// See: <see href="/docfx_api_document/api/fs.md#module-fs">Manual</see>.
    /// </remarks>
    /// <param name="interpreter">fs 모듈 전역을 주입할 대상 인터프리터입니다.</param>
    /// <param name="moduleState">session/route 해석과 실행 컨텍스트를 포함한 모듈 상태입니다.</param>
    private static void InjectFsModule(Interpreter interpreter, SshModuleState moduleState)
    {
        var fsModule = new ValMap
        {
            userData = moduleState,
        };
        fsModule["list"] = Intrinsic.GetByName(FsListIntrinsicName).GetFunc();
        fsModule["read"] = Intrinsic.GetByName(FsReadIntrinsicName).GetFunc();
        fsModule["write"] = Intrinsic.GetByName(FsWriteIntrinsicName).GetFunc();
        fsModule["delete"] = Intrinsic.GetByName(FsDeleteIntrinsicName).GetFunc();
        fsModule["stat"] = Intrinsic.GetByName(FsStatIntrinsicName).GetFunc();
        interpreter.SetGlobalValue("fs", fsModule);
    }

    /// <summary><c>fs.list</c> intrinsic을 등록합니다.</summary>
    /// <remarks>
    /// MiniScript: <c>r = fs.list([sessionOrRoute], path)</c>.
    /// ResultMap(<c>ok/code/err/cost/trace</c>)에 payload(<c>entries</c>)를 반환합니다.
    /// 디렉터리 read 권한을 검사하고 결과는 <c>{ name, entryKind }</c> 목록으로 제공됩니다.
    /// See: <see href="/docfx_api_document/api/fs.md#fslist">Manual</see>.
    /// </remarks>
    private static void RegisterFsListIntrinsic()
    {
        if (Intrinsic.GetByName(FsListIntrinsicName) is not null)
        {
            return;
        }

        var intrinsic = Intrinsic.Create(FsListIntrinsicName);
        intrinsic.AddParam("arg1");
        intrinsic.AddParam("arg2");
        intrinsic.code = (context, partialResult) =>
        {
            if (!TryGetExecutionState(context, out var state) || state.ExecutionContext is null)
            {
                return new Intrinsic.Result(
                    CreateFsFailureMap(
                        SystemCallErrorCode.InternalError,
                        "fs.list is unavailable in this execution context."));
            }

            if (!TryParseFsUnaryArguments(context, out var sessionOrRouteMap, out var pathInput, out var parseError))
            {
                return new Intrinsic.Result(CreateFsFailureMap(SystemCallErrorCode.InvalidArgs, parseError));
            }

            var executionContext = state.ExecutionContext;
            if (!TryResolveFsEndpoint(
                    state,
                    executionContext,
                    sessionOrRouteMap,
                    out var endpoint,
                    out var endpointUser,
                    out var endpointError))
            {
                return new Intrinsic.Result(CreateFsFailureMap(SystemCallErrorCode.InvalidArgs, endpointError));
            }

            if (!endpointUser.Privilege.Read)
            {
                return new Intrinsic.Result(CreateFsFailureMap(SystemCallErrorCode.PermissionDenied, "permission denied: fs.list"));
            }

            var targetPath = BaseFileSystem.NormalizePath(endpoint.Cwd, pathInput);
            if (!endpoint.Server.DiskOverlay.TryResolveEntry(targetPath, out var entry))
            {
                return new Intrinsic.Result(CreateFsFailureMap(SystemCallResultFactory.NotFound(targetPath)));
            }

            if (entry.EntryKind != VfsEntryKind.Dir)
            {
                return new Intrinsic.Result(CreateFsFailureMap(SystemCallResultFactory.NotDirectory(targetPath)));
            }

            var children = endpoint.Server.DiskOverlay.ListChildren(targetPath);
            var entries = new ValList();
            foreach (var childName in children)
            {
                var childPath = targetPath == "/" ? "/" + childName : targetPath + "/" + childName;
                var childKind = endpoint.Server.DiskOverlay.TryResolveEntry(childPath, out var childEntry) &&
                                childEntry.EntryKind == VfsEntryKind.Dir
                    ? "Dir"
                    : "File";
                entries.values.Add(new ValMap
                {
                    ["name"] = new ValString(childName),
                    ["entryKind"] = new ValString(childKind),
                });
            }

            var result = CreateFsSuccessMap();
            result["entries"] = entries;
            return new Intrinsic.Result(result);
        };
    }

    /// <summary><c>fs.read</c> intrinsic을 등록합니다.</summary>
    /// <remarks>
    /// MiniScript: <c>r = fs.read([sessionOrRoute], path, opts?)</c>.
    /// ResultMap(<c>ok/code/err/cost/trace</c>)에 payload(<c>text</c>)를 반환합니다.
    /// 텍스트 파일/최대 바이트 상한 규칙을 검사하며 위반 시 <c>ERR_NOT_TEXT_FILE</c> 또는 <c>ERR_TOO_LARGE</c>를 반환합니다.
    /// See: <see href="/docfx_api_document/api/fs.md#fsread">Manual</see>.
    /// </remarks>
    private static void RegisterFsReadIntrinsic()
    {
        if (Intrinsic.GetByName(FsReadIntrinsicName) is not null)
        {
            return;
        }

        var intrinsic = Intrinsic.Create(FsReadIntrinsicName);
        intrinsic.AddParam("arg1");
        intrinsic.AddParam("arg2");
        intrinsic.AddParam("opts");
        intrinsic.code = (context, partialResult) =>
        {
            if (!TryGetExecutionState(context, out var state) || state.ExecutionContext is null)
            {
                return new Intrinsic.Result(
                    CreateFsFailureMap(
                        SystemCallErrorCode.InternalError,
                        "fs.read is unavailable in this execution context."));
            }

            if (!TryParseFsReadArguments(context, out var sessionOrRouteMap, out var pathInput, out var maxBytes, out var parseError))
            {
                return new Intrinsic.Result(CreateFsFailureMap(SystemCallErrorCode.InvalidArgs, parseError));
            }

            var executionContext = state.ExecutionContext;
            if (!TryResolveFsEndpoint(
                    state,
                    executionContext,
                    sessionOrRouteMap,
                    out var endpoint,
                    out var endpointUser,
                    out var endpointError))
            {
                return new Intrinsic.Result(CreateFsFailureMap(SystemCallErrorCode.InvalidArgs, endpointError));
            }

            if (!endpointUser.Privilege.Read)
            {
                return new Intrinsic.Result(CreateFsFailureMap(SystemCallErrorCode.PermissionDenied, "permission denied: fs.read"));
            }

            var targetPath = BaseFileSystem.NormalizePath(endpoint.Cwd, pathInput);
            if (!endpoint.Server.DiskOverlay.TryResolveEntry(targetPath, out var entry))
            {
                return new Intrinsic.Result(CreateFsFailureMap(SystemCallResultFactory.NotFound(targetPath)));
            }

            if (entry.EntryKind != VfsEntryKind.File)
            {
                return new Intrinsic.Result(CreateFsFailureMap(SystemCallResultFactory.IsDirectory(targetPath)));
            }

            if (entry.FileKind != VfsFileKind.Text)
            {
                return new Intrinsic.Result(CreateFsFailureMap(SystemCallResultFactory.NotTextFile(targetPath)));
            }

            if (maxBytes.HasValue && entry.Size > maxBytes.Value)
            {
                return new Intrinsic.Result(CreateFsFailureMap(SystemCallResultFactory.TooLarge(targetPath, maxBytes.Value, entry.Size)));
            }

            if (!endpoint.Server.DiskOverlay.TryReadFileText(targetPath, out var text))
            {
                return new Intrinsic.Result(CreateFsFailureMap(SystemCallResultFactory.NotFound(targetPath)));
            }

            var result = CreateFsSuccessMap();
            result["text"] = new ValString(text);
            return new Intrinsic.Result(result);
        };
    }

    /// <summary><c>fs.write</c> intrinsic을 등록합니다.</summary>
    /// <remarks>
    /// MiniScript: <c>r = fs.write([sessionOrRoute], path, text, opts?)</c>.
    /// ResultMap(<c>ok/code/err/cost/trace</c>)에 payload(<c>written/path</c>)를 반환합니다.
    /// overwrite/createParents 규칙을 적용하며 성공 시 파일 획득 이벤트(<c>transferMethod="fs.write"</c>)를 발생시킬 수 있습니다.
    /// See: <see href="/docfx_api_document/api/fs.md#fswrite">Manual</see>.
    /// </remarks>
    private static void RegisterFsWriteIntrinsic()
    {
        if (Intrinsic.GetByName(FsWriteIntrinsicName) is not null)
        {
            return;
        }

        var intrinsic = Intrinsic.Create(FsWriteIntrinsicName);
        intrinsic.AddParam("arg1");
        intrinsic.AddParam("arg2");
        intrinsic.AddParam("arg3");
        intrinsic.AddParam("opts");
        intrinsic.code = (context, partialResult) =>
        {
            if (!TryGetExecutionState(context, out var state) || state.ExecutionContext is null)
            {
                return new Intrinsic.Result(
                    CreateFsFailureMap(
                        SystemCallErrorCode.InternalError,
                        "fs.write is unavailable in this execution context."));
            }

            if (!TryParseFsWriteArguments(
                    context,
                    out var sessionOrRouteMap,
                    out var pathInput,
                    out var text,
                    out var overwrite,
                    out var createParents,
                    out var parseError))
            {
                return new Intrinsic.Result(CreateFsFailureMap(SystemCallErrorCode.InvalidArgs, parseError));
            }

            var executionContext = state.ExecutionContext;
            if (!TryResolveFsEndpoint(
                    state,
                    executionContext,
                    sessionOrRouteMap,
                    out var endpoint,
                    out var endpointUser,
                    out var endpointError))
            {
                return new Intrinsic.Result(CreateFsFailureMap(SystemCallErrorCode.InvalidArgs, endpointError));
            }

            if (!endpointUser.Privilege.Write)
            {
                return new Intrinsic.Result(CreateFsFailureMap(SystemCallErrorCode.PermissionDenied, "permission denied: fs.write"));
            }

            var targetPath = BaseFileSystem.NormalizePath(endpoint.Cwd, pathInput);
            if (endpoint.Server.DiskOverlay.TryResolveEntry(targetPath, out var existingEntry))
            {
                if (existingEntry.EntryKind == VfsEntryKind.Dir)
                {
                    return new Intrinsic.Result(CreateFsFailureMap(SystemCallResultFactory.IsDirectory(targetPath)));
                }

                if (!overwrite)
                {
                    return new Intrinsic.Result(CreateFsFailureMap(SystemCallResultFactory.AlreadyExists(targetPath)));
                }
            }

            var applyChanges = state.Mode == MiniScriptSshExecutionMode.RealWorld;
            if (!TryEnsureFsParentDirectories(
                    endpoint.Server,
                    targetPath,
                    createParents,
                    applyChanges,
                    out var parentFailure))
            {
                return new Intrinsic.Result(CreateFsFailureMap(parentFailure));
            }

            if (applyChanges)
            {
                try
                {
                    endpoint.Server.DiskOverlay.WriteFile(targetPath, text, cwd: "/", fileKind: VfsFileKind.Text);
                    var hasResolvedWrittenEntry = endpoint.Server.DiskOverlay.TryResolveEntry(targetPath, out var writtenEntry);
                    executionContext.World.EmitFileAcquire(
                        fromNodeId: endpoint.NodeId,
                        userKey: endpoint.UserKey,
                        fileName: targetPath,
                        remotePath: null,
                        localPath: targetPath,
                        sizeBytes: hasResolvedWrittenEntry ? ToOptionalInt(writtenEntry.Size) : null,
                        contentId: hasResolvedWrittenEntry ? writtenEntry.ContentId : null,
                        transferMethod: "fs.write");
                }
                catch (InvalidOperationException ex)
                {
                    return new Intrinsic.Result(CreateFsFailureMap(SystemCallErrorCode.InvalidArgs, ex.Message));
                }
            }

            var result = CreateFsSuccessMap();
            result["written"] = new ValNumber(text.Length);
            result["path"] = new ValString(targetPath);
            return new Intrinsic.Result(result);
        };
    }

    /// <summary><c>fs.delete</c> intrinsic을 등록합니다.</summary>
    /// <remarks>
    /// MiniScript: <c>r = fs.delete([sessionOrRoute], path)</c>.
    /// ResultMap(<c>ok/code/err/cost/trace</c>)에 payload(<c>deleted</c>)를 반환합니다.
    /// 루트 삭제 금지, 비어 있지 않은 디렉터리 삭제 제한, 권한 검사 규칙을 적용합니다.
    /// See: <see href="/docfx_api_document/api/fs.md#fsdelete">Manual</see>.
    /// </remarks>
    private static void RegisterFsDeleteIntrinsic()
    {
        if (Intrinsic.GetByName(FsDeleteIntrinsicName) is not null)
        {
            return;
        }

        var intrinsic = Intrinsic.Create(FsDeleteIntrinsicName);
        intrinsic.AddParam("arg1");
        intrinsic.AddParam("arg2");
        intrinsic.code = (context, partialResult) =>
        {
            if (!TryGetExecutionState(context, out var state) || state.ExecutionContext is null)
            {
                return new Intrinsic.Result(
                    CreateFsFailureMap(
                        SystemCallErrorCode.InternalError,
                        "fs.delete is unavailable in this execution context."));
            }

            if (!TryParseFsUnaryArguments(context, out var sessionOrRouteMap, out var pathInput, out var parseError))
            {
                return new Intrinsic.Result(CreateFsFailureMap(SystemCallErrorCode.InvalidArgs, parseError));
            }

            var executionContext = state.ExecutionContext;
            if (!TryResolveFsEndpoint(
                    state,
                    executionContext,
                    sessionOrRouteMap,
                    out var endpoint,
                    out var endpointUser,
                    out var endpointError))
            {
                return new Intrinsic.Result(CreateFsFailureMap(SystemCallErrorCode.InvalidArgs, endpointError));
            }

            if (!endpointUser.Privilege.Write)
            {
                return new Intrinsic.Result(CreateFsFailureMap(SystemCallErrorCode.PermissionDenied, "permission denied: fs.delete"));
            }

            var targetPath = BaseFileSystem.NormalizePath(endpoint.Cwd, pathInput);
            if (targetPath == "/")
            {
                return new Intrinsic.Result(
                    CreateFsFailureMap(
                        SystemCallErrorCode.InvalidArgs,
                        "fs.delete cannot remove root directory."));
            }

            if (!endpoint.Server.DiskOverlay.TryResolveEntry(targetPath, out var entry))
            {
                return new Intrinsic.Result(CreateFsFailureMap(SystemCallResultFactory.NotFound(targetPath)));
            }

            if (entry.EntryKind == VfsEntryKind.Dir && endpoint.Server.DiskOverlay.ListChildren(targetPath).Count != 0)
            {
                return new Intrinsic.Result(
                    CreateFsFailureMap(
                        SystemCallErrorCode.NotDirectory,
                        "directory not empty: " + targetPath));
            }

            if (state.Mode == MiniScriptSshExecutionMode.RealWorld)
            {
                try
                {
                    endpoint.Server.DiskOverlay.AddTombstone(targetPath);
                }
                catch (InvalidOperationException ex)
                {
                    return new Intrinsic.Result(CreateFsFailureMap(SystemCallErrorCode.InvalidArgs, ex.Message));
                }
            }

            var result = CreateFsSuccessMap();
            result["deleted"] = ValNumber.one;
            return new Intrinsic.Result(result);
        };
    }

    /// <summary><c>fs.stat</c> intrinsic을 등록합니다.</summary>
    /// <remarks>
    /// MiniScript: <c>r = fs.stat([sessionOrRoute], path)</c>.
    /// ResultMap(<c>ok/code/err/cost/trace</c>)에 payload(<c>entryKind/fileKind/size</c>)를 반환합니다.
    /// 파일/디렉터리 구분에 따라 메타 필드 노출이 달라지며 read 권한이 필요합니다.
    /// See: <see href="/docfx_api_document/api/fs.md#fsstat">Manual</see>.
    /// </remarks>
    private static void RegisterFsStatIntrinsic()
    {
        if (Intrinsic.GetByName(FsStatIntrinsicName) is not null)
        {
            return;
        }

        var intrinsic = Intrinsic.Create(FsStatIntrinsicName);
        intrinsic.AddParam("arg1");
        intrinsic.AddParam("arg2");
        intrinsic.code = (context, partialResult) =>
        {
            if (!TryGetExecutionState(context, out var state) || state.ExecutionContext is null)
            {
                return new Intrinsic.Result(
                    CreateFsFailureMap(
                        SystemCallErrorCode.InternalError,
                        "fs.stat is unavailable in this execution context."));
            }

            if (!TryParseFsUnaryArguments(context, out var sessionOrRouteMap, out var pathInput, out var parseError))
            {
                return new Intrinsic.Result(CreateFsFailureMap(SystemCallErrorCode.InvalidArgs, parseError));
            }

            var executionContext = state.ExecutionContext;
            if (!TryResolveFsEndpoint(
                    state,
                    executionContext,
                    sessionOrRouteMap,
                    out var endpoint,
                    out var endpointUser,
                    out var endpointError))
            {
                return new Intrinsic.Result(CreateFsFailureMap(SystemCallErrorCode.InvalidArgs, endpointError));
            }

            if (!endpointUser.Privilege.Read)
            {
                return new Intrinsic.Result(CreateFsFailureMap(SystemCallErrorCode.PermissionDenied, "permission denied: fs.stat"));
            }

            var targetPath = BaseFileSystem.NormalizePath(endpoint.Cwd, pathInput);
            if (!endpoint.Server.DiskOverlay.TryResolveEntry(targetPath, out var entry))
            {
                return new Intrinsic.Result(CreateFsFailureMap(SystemCallResultFactory.NotFound(targetPath)));
            }

            var result = CreateFsSuccessMap();
            result["entryKind"] = new ValString(entry.EntryKind == VfsEntryKind.Dir ? "Dir" : "File");
            if (entry.EntryKind == VfsEntryKind.File)
            {
                result["fileKind"] = new ValString((entry.FileKind ?? VfsFileKind.Text).ToString());
                result["size"] = new ValNumber(entry.Size);
            }
            else
            {
                result["fileKind"] = (Value)null!;
                result["size"] = (Value)null!;
            }

            return new Intrinsic.Result(result);
        };
    }

    private static bool TryParseFsUnaryArguments(
        TAC.Context context,
        out ValMap? sessionOrRouteMap,
        out string pathInput,
        out string error)
    {
        sessionOrRouteMap = null;
        pathInput = string.Empty;
        error = string.Empty;

        var rawArg1 = context.GetLocal("arg1");
        var rawArg2 = context.GetLocal("arg2");
        if (!TryParseFsSessionOrRouteArgument(rawArg1, out sessionOrRouteMap, out var hasSessionOrRoute, out error))
        {
            return false;
        }

        if (hasSessionOrRoute)
        {
            if (rawArg2 is null || rawArg2 is ValMap)
            {
                error = "path is required.";
                return false;
            }

            return TryReadFsPath(rawArg2, out pathInput, out error);
        }

        if (rawArg2 is not null)
        {
            error = "too many arguments.";
            return false;
        }

        return TryReadFsPath(rawArg1, out pathInput, out error);
    }

    private static bool TryParseFsReadArguments(
        TAC.Context context,
        out ValMap? sessionOrRouteMap,
        out string pathInput,
        out int? maxBytes,
        out string error)
    {
        sessionOrRouteMap = null;
        pathInput = string.Empty;
        maxBytes = null;
        error = string.Empty;

        var rawArg1 = context.GetLocal("arg1");
        var rawArg2 = context.GetLocal("arg2");
        var rawOpts = context.GetLocal("opts");
        if (!TryParseFsSessionOrRouteArgument(rawArg1, out sessionOrRouteMap, out var hasSessionOrRoute, out error))
        {
            return false;
        }

        ValMap? optsMap;
        if (hasSessionOrRoute)
        {
            if (rawArg2 is null || rawArg2 is ValMap)
            {
                error = "path is required.";
                return false;
            }

            if (!TryReadFsPath(rawArg2, out pathInput, out error))
            {
                return false;
            }

            if (rawOpts is null)
            {
                optsMap = null;
            }
            else if (rawOpts is ValMap parsedOpts)
            {
                optsMap = parsedOpts;
            }
            else
            {
                error = "opts must be a map.";
                return false;
            }
        }
        else
        {
            if (!TryReadFsPath(rawArg1, out pathInput, out error))
            {
                return false;
            }

            if (rawOpts is not null)
            {
                error = "opts must be passed as the second argument when session is omitted.";
                return false;
            }

            if (rawArg2 is null)
            {
                optsMap = null;
            }
            else if (rawArg2 is ValMap parsedOpts)
            {
                optsMap = parsedOpts;
            }
            else
            {
                error = "opts must be a map.";
                return false;
            }
        }

        return TryParseFsReadOpts(optsMap, out maxBytes, out error);
    }

    private static bool TryParseFsWriteArguments(
        TAC.Context context,
        out ValMap? sessionOrRouteMap,
        out string pathInput,
        out string text,
        out bool overwrite,
        out bool createParents,
        out string error)
    {
        sessionOrRouteMap = null;
        pathInput = string.Empty;
        text = string.Empty;
        overwrite = false;
        createParents = false;
        error = string.Empty;

        var rawArg1 = context.GetLocal("arg1");
        var rawArg2 = context.GetLocal("arg2");
        var rawArg3 = context.GetLocal("arg3");
        var rawOpts = context.GetLocal("opts");
        if (!TryParseFsSessionOrRouteArgument(rawArg1, out sessionOrRouteMap, out var hasSessionOrRoute, out error))
        {
            return false;
        }

        Value rawPath;
        Value rawText;
        ValMap? optsMap = null;
        if (hasSessionOrRoute)
        {
            rawPath = rawArg2;
            rawText = rawArg3;
            if (rawOpts is null)
            {
                optsMap = null;
            }
            else if (rawOpts is ValMap parsedOpts)
            {
                optsMap = parsedOpts;
            }
            else
            {
                error = "opts must be a map.";
                return false;
            }
        }
        else
        {
            rawPath = rawArg1;
            rawText = rawArg2;
            if (rawOpts is not null)
            {
                error = "opts must be passed as the third argument when session is omitted.";
                return false;
            }

            if (rawArg3 is null)
            {
                optsMap = null;
            }
            else if (rawArg3 is ValMap parsedOpts)
            {
                optsMap = parsedOpts;
            }
            else
            {
                error = "opts must be a map.";
                return false;
            }
        }

        if (rawPath is null || rawPath is ValMap)
        {
            error = "path is required.";
            return false;
        }

        if (!TryReadFsPath(rawPath, out pathInput, out error))
        {
            return false;
        }

        if (rawText is null || rawText is ValMap)
        {
            error = "text is required.";
            return false;
        }

        text = rawText.ToString();
        return TryParseFsWriteOpts(optsMap, out overwrite, out createParents, out error);
    }

    private static bool TryParseFsSessionOrRouteArgument(
        Value rawArg,
        out ValMap? sessionOrRouteMap,
        out bool hasSessionOrRoute,
        out string error)
    {
        sessionOrRouteMap = null;
        hasSessionOrRoute = false;
        error = string.Empty;
        if (rawArg is not ValMap map)
        {
            return true;
        }

        if (!TryReadKind(map, out var kind, out var kindError, "sessionOrRoute"))
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

        hasSessionOrRoute = true;
        sessionOrRouteMap = map;
        return true;
    }

    private static bool TryReadFsRouteLastSessionMap(
        ValMap routeMap,
        out ValMap lastSessionMap,
        out string error)
    {
        lastSessionMap = null!;
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

        if (!routeMap.TryGetValue(RouteLastSessionKey, out var lastSessionValue) ||
            lastSessionValue is not ValMap parsedLastSessionMap)
        {
            error = "route.lastSession is required.";
            return false;
        }

        if (!TryReadSessionIdentity(parsedLastSessionMap, out _, out _, out var lastSessionError))
        {
            error = "route.lastSession: " + lastSessionError;
            return false;
        }

        lastSessionMap = parsedLastSessionMap;
        return true;
    }

    private static bool TryReadFsPath(Value rawPath, out string path, out string error)
    {
        path = string.Empty;
        error = string.Empty;
        if (rawPath is null)
        {
            error = "path is required.";
            return false;
        }

        path = rawPath.ToString().Trim();
        if (string.IsNullOrWhiteSpace(path))
        {
            error = "path is required.";
            return false;
        }

        return true;
    }

    private static bool TryParseFsReadOpts(ValMap? optsMap, out int? maxBytes, out string error)
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
            var parsed = rawMaxBytes.IntValue();
            if (parsed < 0)
            {
                error = "opts.maxBytes must be a non-negative integer.";
                return false;
            }

            maxBytes = parsed;
            return true;
        }
        catch (Exception)
        {
            error = "opts.maxBytes must be a non-negative integer.";
            return false;
        }
    }

    private static bool TryParseFsWriteOpts(
        ValMap? optsMap,
        out bool overwrite,
        out bool createParents,
        out string error)
    {
        overwrite = false;
        createParents = false;
        error = string.Empty;
        if (optsMap is null)
        {
            return true;
        }

        foreach (var key in optsMap.Keys)
        {
            var keyText = key?.ToString().Trim() ?? string.Empty;
            if (!string.Equals(keyText, "overwrite", StringComparison.Ordinal) &&
                !string.Equals(keyText, "createParents", StringComparison.Ordinal))
            {
                error = $"unsupported opts key: {keyText}";
                return false;
            }
        }

        if (optsMap.TryGetValue("overwrite", out var rawOverwrite))
        {
            if (rawOverwrite is null)
            {
                error = "opts.overwrite must be boolean-like.";
                return false;
            }

            try
            {
                overwrite = rawOverwrite.BoolValue();
            }
            catch (Exception)
            {
                error = "opts.overwrite must be boolean-like.";
                return false;
            }
        }

        if (optsMap.TryGetValue("createParents", out var rawCreateParents))
        {
            if (rawCreateParents is null)
            {
                error = "opts.createParents must be boolean-like.";
                return false;
            }

            try
            {
                createParents = rawCreateParents.BoolValue();
            }
            catch (Exception)
            {
                error = "opts.createParents must be boolean-like.";
                return false;
            }
        }

        return true;
    }

    private static bool TryResolveFsEndpoint(
        SshModuleState state,
        SystemCallExecutionContext executionContext,
        ValMap? sessionOrRouteMap,
        out FtpEndpoint endpoint,
        out UserConfig endpointUser,
        out string error)
    {
        endpoint = default;
        endpointUser = null!;
        error = string.Empty;
        if (sessionOrRouteMap is null)
        {
            if (!TryResolveExecutionContextFtpEndpoint(executionContext, out endpoint, out var executionContextError))
            {
                error = executionContextError;
                return false;
            }
        }
        else
        {
            if (!TryReadKind(sessionOrRouteMap, out var kind, out var kindError, "sessionOrRoute"))
            {
                error = kindError;
                return false;
            }

            ValMap sessionMap;
            if (string.Equals(kind, SessionKind, StringComparison.Ordinal))
            {
                sessionMap = sessionOrRouteMap;
            }
            else if (string.Equals(kind, RouteKind, StringComparison.Ordinal))
            {
                if (!TryReadFsRouteLastSessionMap(sessionOrRouteMap, out sessionMap, out var lastSessionError))
                {
                    error = lastSessionError;
                    return false;
                }
            }
            else
            {
                error = "sessionOrRoute.kind must be sshSession or sshRoute.";
                return false;
            }

            if (!TryResolveCanonicalSessionMap(state, executionContext, sessionMap, out var canonicalSession, out var sessionError))
            {
                error = sessionError;
                return false;
            }

            if (!TryResolveSessionFtpEndpoint(state, executionContext, canonicalSession, out endpoint, out var endpointError))
            {
                error = endpointError;
                return false;
            }
        }

        if (!TryGetFtpEndpointUser(endpoint, out endpointUser, out var userError))
        {
            error = userError;
            return false;
        }

        return true;
    }

    private static bool TryEnsureFsParentDirectories(
        ServerNodeRuntime server,
        string targetPath,
        bool createParents,
        bool applyChanges,
        out SystemCallResult failure)
    {
        failure = SystemCallResultFactory.Success();
        var parentPath = GetParentPath(targetPath);
        if (parentPath == "/")
        {
            return true;
        }

        var current = "/";
        var segments = parentPath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        foreach (var segment in segments)
        {
            var next = current == "/" ? "/" + segment : current + "/" + segment;
            if (!server.DiskOverlay.TryResolveEntry(next, out var entry))
            {
                if (!createParents)
                {
                    failure = SystemCallResultFactory.NotFound(next);
                    return false;
                }

                if (applyChanges)
                {
                    try
                    {
                        server.DiskOverlay.AddDirectory(next);
                    }
                    catch (InvalidOperationException ex)
                    {
                        failure = SystemCallResultFactory.Failure(SystemCallErrorCode.InvalidArgs, ex.Message);
                        return false;
                    }
                }

                current = next;
                continue;
            }

            if (entry.EntryKind != VfsEntryKind.Dir)
            {
                failure = SystemCallResultFactory.NotDirectory(next);
                return false;
            }

            current = next;
        }

        return true;
    }

    private static ValMap CreateFsSuccessMap()
    {
        return new ValMap
        {
            ["ok"] = ValNumber.one,
            ["code"] = new ValString(SystemCallErrorCodeTokenMapper.ToApiToken(SystemCallErrorCode.None)),
            ["err"] = ValNull.instance,
        };
    }

    private static ValMap CreateFsFailureMap(SystemCallResult failure)
    {
        return CreateFsFailureMap(failure.Code, ExtractErrorText(failure));
    }

    private static ValMap CreateFsFailureMap(SystemCallErrorCode code, string err)
    {
        return new ValMap
        {
            ["ok"] = ValNumber.zero,
            ["code"] = new ValString(SystemCallErrorCodeTokenMapper.ToApiToken(code)),
            ["err"] = new ValString(err),
        };
    }
}
