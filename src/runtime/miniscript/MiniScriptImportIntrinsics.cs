using Miniscript;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using Uplink2.Runtime.Syscalls;
using Uplink2.Vfs;

#nullable enable

namespace Uplink2.Runtime.MiniScript;

/// <summary>MiniScript 인터프리터에 전역 <c>import</c> intrinsic을 등록/주입합니다.</summary>
internal static class MiniScriptImportIntrinsics
{
    private const string ImportIntrinsicName = "uplink_import";
    private const string ImportGlobalName = "import";
    private const string ImportResultStorageName = "__uplink_import_result";
    private const string StandardLibraryResourceRoot = "res://scenario_content/resources/text/stdlib";
    private const string StandardLibraryCanonicalRoot = "/standard_library";
    private static readonly object registrationSync = new();
    private static bool isRegistered;

    /// <summary>Resolves stdlib resource root path to an absolute path (test-overridable).</summary>
    internal static Func<string, string> ResolveAbsoluteStdlibPath { get; set; } = DefaultResolveAbsoluteStdlibPath;

    /// <summary>Ensures custom import intrinsic is registered exactly once per process.</summary>
    internal static void EnsureRegistered()
    {
        lock (registrationSync)
        {
            if (isRegistered)
            {
                return;
            }

            RegisterImportIntrinsic();
            isRegistered = true;
        }
    }

    /// <summary>인터프리터에 전역 <c>import</c> 함수를 주입합니다.</summary>
    /// <remarks>
    /// <para><b>MiniScript</b></para>
    /// <para><c>m = import(name, alias=null)</c></para>
    /// <para><b>Parameters</b></para>
    /// <list type="bullet">
    /// <item><description><c>interpreter</c>: 전역 intrinsic을 주입할 대상 인터프리터입니다.</description></item>
    /// <item><description><c>runtimeState</c>: import 캐시/순환 감지/실행 컨텍스트를 공유하는 런타임 상태입니다.</description></item>
    /// </list>
    /// <para><b>Returns</b></para>
    /// <list type="bullet">
    /// <item><description>반환값은 없으며, 인터프리터 전역에 <c>import</c> 함수를 등록합니다.</description></item>
    /// </list>
    /// <para><b>Note</b></para>
    /// <list type="bullet">
    /// <item><description>호출 전에 intrinsic 등록을 보장하며, interpreter.hostData에 import 런타임 상태를 연결합니다.</description></item>
    /// <item><description>이미 컴파일된 인터프리터라도 안전하게 재주입할 수 있도록 compile 상태를 확인합니다.</description></item>
    /// </list>
    /// <para><b>See</b>: <see href="/api/import.html#import">Manual</see>.</para>
    /// </remarks>
    /// <param name="interpreter">전역 <c>import</c> 함수를 주입할 인터프리터입니다.</param>
    /// <param name="runtimeState">중첩 import 호출에서 공유할 실행 상태입니다.</param>
    internal static void InjectImportIntrinsic(Interpreter interpreter, ImportRuntimeState runtimeState)
    {
        if (interpreter is null)
        {
            throw new ArgumentNullException(nameof(interpreter));
        }

        if (runtimeState is null)
        {
            throw new ArgumentNullException(nameof(runtimeState));
        }

        EnsureRegistered();
        interpreter.Compile();
        if (interpreter.vm is null)
        {
            return;
        }

        interpreter.hostData = runtimeState;
        interpreter.SetGlobalValue(ImportGlobalName, Intrinsic.GetByName(ImportIntrinsicName).GetFunc());
    }

    /// <summary><c>import</c> intrinsic(<c>uplink_import</c>)을 프로세스 전역에 1회 등록합니다.</summary>
    /// <remarks>
    /// <para><b>MiniScript</b></para>
    /// <para><c>m = import(name, alias=null)</c></para>
    /// <para><b>Parameters</b></para>
    /// <list type="bullet">
    /// <item><description><c>name</c>: 로드할 모듈 이름/경로입니다.</description></item>
    /// <item><description><c>alias</c>(선택): 바인딩할 변수명입니다. 생략 시 해결된 파일명 stem을 사용합니다.</description></item>
    /// </list>
    /// <para><b>Returns</b></para>
    /// <list type="bullet">
    /// <item><description>성공: 모듈 값(<c>return</c> 값 또는 기본 <c>locals</c> map) 반환</description></item>
    /// <item><description>실패: ResultMap 대신 runtime error를 발생시키며 메시지에 <c>ERR_*</c> 코드 토큰이 포함됩니다.</description></item>
    /// </list>
    /// <para><b>Note</b></para>
    /// <list type="bullet">
    /// <item><description>탐색 순서는 현재 스크립트 기준 상대경로 -&gt; 표준 라이브러리(<c>res://scenario_content/resources/text/stdlib</c>)입니다.</description></item>
    /// <item><description>라이브러리 계약: 파일 최상단 연속 <c>//</c> 주석 블록에 <c>@name</c>이 반드시 있어야 합니다(<c>ERR_NOT_A_LIBRARY</c>).</description></item>
    /// <item><description>캐시는 <c>serverId:canonicalPath</c> 키를 사용하며, 로딩 중 재진입은 <c>ERR_IMPORT_CYCLE</c>로 즉시 실패합니다.</description></item>
    /// </list>
    /// <para><b>See</b>: <see href="/api/import.html#import">Manual</see>.</para>
    /// </remarks>
    private static void RegisterImportIntrinsic()
    {
        if (Intrinsic.GetByName(ImportIntrinsicName) is not null)
        {
            return;
        }

        var intrinsic = Intrinsic.Create(ImportIntrinsicName);
        intrinsic.AddParam("name");
        intrinsic.AddParam("alias");
        intrinsic.code = (context, partialResult) =>
        {
            _ = partialResult;
            if (!TryGetExecutionState(context, out var state))
            {
                throw CreateImportRuntimeError(
                    SystemCallErrorCode.InternalError,
                    "import runtime state is not available.");
            }

            if (!TryParseImportArguments(context, out var importName, out var aliasName, out var parseError))
            {
                throw CreateImportRuntimeError(SystemCallErrorCode.InvalidArgs, parseError);
            }

            try
            {
                var module = ResolveLoadModule(context, state, importName, out var defaultBindingName);
                var bindingName = string.IsNullOrWhiteSpace(aliasName)
                    ? defaultBindingName
                    : aliasName!;
                BindModuleToCallerScope(context, bindingName, module);
                return new Intrinsic.Result(module);
            }
            catch (RuntimeException)
            {
                throw;
            }
            catch (MiniscriptException)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw CreateImportRuntimeError(SystemCallErrorCode.InternalError, ex.Message);
            }
        };
    }

    private static Value ResolveLoadModule(
        TAC.Context callerContext,
        ImportRuntimeState state,
        string importName,
        out string defaultBindingName)
    {
        var attempts = new List<string>();
        var resolved = ResolveModuleSource(state, importName, attempts);
        defaultBindingName = resolved.DefaultBindingName;
        var cacheKey = state.BuildCacheKey(resolved.CanonicalPath);
        if (state.ModuleCache.TryGetValue(cacheKey, out var cachedModule))
        {
            return cachedModule;
        }

        if (!state.LoadingSet.Add(cacheKey))
        {
            throw CreateImportRuntimeError(
                SystemCallErrorCode.ImportCycle,
                $"circular import detected for '{resolved.CanonicalPath}'.");
        }

        try
        {
            EnsureLibraryContract(resolved.SourceText, resolved.CanonicalPath);
            var module = ExecuteResolvedModule(callerContext, state, resolved);
            state.ModuleCache[cacheKey] = module;
            return module;
        }
        finally
        {
            state.LoadingSet.Remove(cacheKey);
        }
    }

    private static ImportResolvedModule ResolveModuleSource(
        ImportRuntimeState state,
        string importName,
        List<string> attempts)
    {
        ValidateImportName(importName);
        if (state.ExecutionContext is null)
        {
            throw CreateImportRuntimeError(
                SystemCallErrorCode.InternalError,
                "import is unavailable because execution context is missing.");
        }

        if (TryResolveRelativeModule(state, importName, attempts, out var relativeModule))
        {
            return relativeModule;
        }

        if (TryResolveStandardLibraryModule(state, importName, attempts, out var stdlibModule))
        {
            return stdlibModule;
        }

        var joinedAttempts = attempts.Count == 0
            ? "<none>"
            : string.Join(", ", attempts);
        throw CreateImportRuntimeError(
            SystemCallErrorCode.NotFound,
            $"module '{importName}' not found. searched: {joinedAttempts}");
    }

    private static bool TryResolveRelativeModule(
        ImportRuntimeState state,
        string importName,
        List<string> attempts,
        out ImportResolvedModule resolvedModule)
    {
        resolvedModule = default;
        var resolvedCandidate = default(ImportResolvedModule);
        var scriptPath = state.CurrentScriptPath;
        if (string.IsNullOrWhiteSpace(scriptPath))
        {
            throw CreateImportRuntimeError(
                SystemCallErrorCode.InternalError,
                "current script path is unavailable for relative import resolution.");
        }

        var normalizedScriptPath = BaseFileSystem.NormalizePath("/", scriptPath);
        var scriptDirectory = GetParentPath(normalizedScriptPath);
        if (!state.ExecutionContext!.World.TryRunViaIntrinsicQueue(
                () =>
                {
                    foreach (var candidate in EnumerateImportCandidates(importName))
                    {
                        var normalizedCandidatePath = BaseFileSystem.NormalizePath(scriptDirectory, candidate);
                        attempts.Add(normalizedCandidatePath);
                        if (!state.ExecutionContext.Server.DiskOverlay.TryResolveEntry(normalizedCandidatePath, out var entry) ||
                            entry.EntryKind != VfsEntryKind.File)
                        {
                            continue;
                        }

                        if (entry.FileKind != VfsFileKind.Text && entry.FileKind != VfsFileKind.ExecutableScript)
                        {
                            continue;
                        }

                        if (!state.ExecutionContext.Server.DiskOverlay.TryReadFileText(normalizedCandidatePath, out var sourceText))
                        {
                            throw CreateImportRuntimeError(
                                SystemCallErrorCode.InternalError,
                                $"failed to read module source: {normalizedCandidatePath}");
                        }

                        resolvedCandidate = new ImportResolvedModule(
                            normalizedCandidatePath,
                            sourceText,
                            GetFileStem(normalizedCandidatePath));
                        return true;
                    }

                    return false;
                },
                out var found,
                out var queueError))
        {
            throw CreateImportRuntimeError(SystemCallErrorCode.InternalError, queueError);
        }

        if (found)
        {
            resolvedModule = resolvedCandidate;
        }

        return found;
    }

    private static bool TryResolveStandardLibraryModule(
        ImportRuntimeState state,
        string importName,
        List<string> attempts,
        out ImportResolvedModule resolvedModule)
    {
        resolvedModule = default;
        var index = state.GetOrBuildStandardLibraryIndex();
        if (index.IsEmpty)
        {
            return false;
        }

        if (IsStdlibPathLike(importName))
        {
            foreach (var candidate in EnumerateImportCandidates(importName))
            {
                var normalizedCandidatePath = BaseFileSystem.NormalizePath(StandardLibraryCanonicalRoot, candidate);
                if (!IsUnderCanonicalRoot(normalizedCandidatePath, StandardLibraryCanonicalRoot))
                {
                    throw CreateImportRuntimeError(
                        SystemCallErrorCode.InvalidArgs,
                        $"import path escapes standard library root: {importName}");
                }

                attempts.Add(normalizedCandidatePath);
                if (string.Equals(normalizedCandidatePath, StandardLibraryCanonicalRoot, StringComparison.Ordinal))
                {
                    continue;
                }

                var relativePath = normalizedCandidatePath[(StandardLibraryCanonicalRoot.Length + 1)..];
                if (!index.TryGetByRelativePath(relativePath, out var entry))
                {
                    continue;
                }

                resolvedModule = ResolveStandardLibraryEntry(entry);
                return true;
            }

            return false;
        }

        var stem = GetFileStem(importName);
        var canonicalProbe = $"{StandardLibraryCanonicalRoot}/**/{stem}.ms";
        attempts.Add(canonicalProbe);
        var matches = index.GetByStem(stem);
        if (matches.Count == 0)
        {
            return false;
        }

        if (matches.Count > 1)
        {
            var candidates = matches
                .Select(static entry => entry.CanonicalPath)
                .OrderBy(static path => path, StringComparer.Ordinal)
                .ToArray();
            throw CreateImportRuntimeError(
                SystemCallErrorCode.ImportAmbiguous,
                $"ambiguous standard library module '{importName}'. candidates: {string.Join(", ", candidates)}");
        }

        resolvedModule = ResolveStandardLibraryEntry(matches[0]);
        return true;
    }

    private static ImportResolvedModule ResolveStandardLibraryEntry(StandardLibraryEntry entry)
    {
        string sourceText;
        try
        {
            sourceText = ReadAllTextFromAnyPath(entry.AbsolutePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            throw CreateImportRuntimeError(
                SystemCallErrorCode.InternalError,
                $"failed to read standard library module '{entry.CanonicalPath}': {ex.Message}");
        }

        return new ImportResolvedModule(entry.CanonicalPath, sourceText, entry.Stem);
    }

    private static Value ExecuteResolvedModule(
        TAC.Context callerContext,
        ImportRuntimeState state,
        ImportResolvedModule resolvedModule)
    {
        var parentInterpreter = callerContext.interpreter;
        var callerVm = callerContext.vm ?? parentInterpreter?.vm;
        var standardOutput = parentInterpreter?.standardOutput ?? callerContext.vm?.standardOutput;
        var errorOutput = parentInterpreter?.errorOutput ?? standardOutput;
        var childInterpreter = new Interpreter(string.Empty, standardOutput, errorOutput)
        {
            implicitOutput = parentInterpreter?.implicitOutput,
        };

        state.PushScriptPath(resolvedModule.CanonicalPath);
        try
        {
            childInterpreter.Compile();
            if (childInterpreter.vm is null)
            {
                throw CreateImportRuntimeError(
                    SystemCallErrorCode.InternalError,
                    $"failed to initialize interpreter for module '{resolvedModule.CanonicalPath}'.");
            }

            SeedChildTypeMapsFromCaller(callerVm, childInterpreter.vm);
            MiniScriptCryptoIntrinsics.InjectCryptoModule(childInterpreter);
            MiniScriptSshIntrinsics.InjectSshModule(childInterpreter, state.ExecutionContext, state.SshMode);
            MiniScriptTermIntrinsics.InjectTermModule(childInterpreter, state.ExecutionContext);
            ArgsIntrinsics.InjectArgs(childInterpreter, Array.Empty<string>());
            InjectImportIntrinsic(childInterpreter, state);
            MiniScriptIntrinsicRateLimiter.ConfigureInterpreter(childInterpreter, state.MaxIntrinsicCallsPerSecond);

            var parser = new Parser
            {
                errorContext = resolvedModule.CanonicalPath,
            };
            parser.Parse(resolvedModule.SourceText);
            var importFunction = parser.CreateImport();
            childInterpreter.vm.ManuallyPushCall(
                new ValFunction(importFunction),
                new ValVar(ImportResultStorageName),
                null);

            while (!childInterpreter.vm.done)
            {
                if (state.CancellationToken.IsCancellationRequested)
                {
                    throw new OperationCanceledException();
                }

                childInterpreter.vm.Step();
            }

            MergeChildTypeMapsToCaller(callerVm, childInterpreter.vm);
            return childInterpreter.GetGlobalValue(ImportResultStorageName);
        }
        finally
        {
            state.PopScriptPath();
        }
    }

    private static void SeedChildTypeMapsFromCaller(TAC.Machine? callerVm, TAC.Machine childVm)
    {
        if (callerVm?.listType is not null)
        {
            childVm.listType = CloneTypeMapShallow(callerVm.listType);
        }

        if (callerVm?.mapType is not null)
        {
            childVm.mapType = CloneTypeMapShallow(callerVm.mapType);
        }

        if (callerVm?.stringType is not null)
        {
            childVm.stringType = CloneTypeMapShallow(callerVm.stringType);
        }
    }

    private static void MergeChildTypeMapsToCaller(TAC.Machine? callerVm, TAC.Machine childVm)
    {
        if (callerVm is null)
        {
            return;
        }

        callerVm.listType = MergeTypeMapEntries(callerVm.listType, childVm.listType);
        callerVm.mapType = MergeTypeMapEntries(callerVm.mapType, childVm.mapType);
        callerVm.stringType = MergeTypeMapEntries(callerVm.stringType, childVm.stringType);
    }

    private static ValMap CloneTypeMapShallow(ValMap source)
    {
        var clone = new ValMap
        {
            assignOverride = source.assignOverride,
            evalOverride = source.evalOverride,
            userData = source.userData,
        };

        foreach (var pair in source.map)
        {
            clone.map[pair.Key] = pair.Value;
        }

        return clone;
    }

    private static ValMap MergeTypeMapEntries(ValMap? target, ValMap? source)
    {
        if (source is null)
        {
            return target ?? new ValMap();
        }

        if (target is null)
        {
            return CloneTypeMapShallow(source);
        }

        foreach (var pair in source.map)
        {
            target.map[pair.Key] = pair.Value;
        }

        return target;
    }

    private static void EnsureLibraryContract(string sourceText, string canonicalPath)
    {
        if (!HasTopLevelLibraryNameDocstring(sourceText))
        {
            throw CreateImportRuntimeError(
                SystemCallErrorCode.NotALibrary,
                $"missing required top-of-file @name docstring: {canonicalPath}");
        }
    }

    private static bool HasTopLevelLibraryNameDocstring(string sourceText)
    {
        if (string.IsNullOrEmpty(sourceText) || !sourceText.StartsWith("//", StringComparison.Ordinal))
        {
            return false;
        }

        var lines = sourceText
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split('\n');
        var hasName = false;
        foreach (var line in lines)
        {
            if (!line.StartsWith("//", StringComparison.Ordinal))
            {
                break;
            }

            var body = line[2..].TrimStart();
            if (!body.StartsWith("@name", StringComparison.Ordinal))
            {
                continue;
            }

            var tail = body[5..].Trim();
            if (!string.IsNullOrWhiteSpace(tail))
            {
                hasName = true;
                break;
            }
        }

        return hasName;
    }

    private static void BindModuleToCallerScope(TAC.Context context, string bindingName, Value moduleValue)
    {
        var targetContext = context.parent ?? context;
        targetContext.SetVar(bindingName, moduleValue);
    }

    private static bool TryGetExecutionState(TAC.Context context, out ImportRuntimeState state)
    {
        state = null!;
        if (context.interpreter?.hostData is not ImportRuntimeState importState)
        {
            return false;
        }

        state = importState;
        return true;
    }

    private static bool TryParseImportArguments(
        TAC.Context context,
        out string importName,
        out string? aliasName,
        out string error)
    {
        importName = string.Empty;
        aliasName = null;
        error = string.Empty;

        if (context.GetLocal("name") is not ValString rawName)
        {
            error = "name must be a string.";
            return false;
        }

        importName = rawName.value?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(importName))
        {
            error = "name is required.";
            return false;
        }

        var rawAlias = context.GetLocal("alias");
        if (rawAlias is null || rawAlias is ValNull)
        {
            return true;
        }

        if (rawAlias is not ValString aliasString)
        {
            error = "alias must be a string.";
            return false;
        }

        aliasName = aliasString.value?.Trim();
        if (string.IsNullOrWhiteSpace(aliasName))
        {
            error = "alias must not be empty.";
            return false;
        }

        return true;
    }

    private static void ValidateImportName(string importName)
    {
        if (string.IsNullOrWhiteSpace(importName))
        {
            throw CreateImportRuntimeError(SystemCallErrorCode.InvalidArgs, "name is required.");
        }

        var trimmed = importName.Trim();
        if (trimmed.Contains('\\') ||
            trimmed.StartsWith("/", StringComparison.Ordinal) ||
            trimmed.StartsWith("res://", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("user://", StringComparison.OrdinalIgnoreCase) ||
            IsDriveLetterPath(trimmed))
        {
            throw CreateImportRuntimeError(
                SystemCallErrorCode.InvalidArgs,
                $"host/absolute paths are not allowed: {trimmed}");
        }
    }

    private static bool IsDriveLetterPath(string value)
    {
        return value.Length >= 2 &&
               char.IsLetter(value[0]) &&
               value[1] == ':';
    }

    private static bool IsGodotVirtualPath(string path)
    {
        return path.StartsWith("res://", StringComparison.OrdinalIgnoreCase) ||
               path.StartsWith("user://", StringComparison.OrdinalIgnoreCase);
    }

    private static IEnumerable<string> EnumerateGodotVirtualFiles(string rootPath, string extensionWithDot)
    {
        var normalizedRoot = rootPath.TrimEnd('/');
        var pendingDirectories = new Stack<string>();
        pendingDirectories.Push(normalizedRoot);
        var files = new List<string>();

        while (pendingDirectories.Count > 0)
        {
            var currentDirectory = pendingDirectories.Pop();
            using var dir = Godot.DirAccess.Open(currentDirectory);
            if (dir is null)
            {
                continue;
            }

            dir.ListDirBegin();
            while (true)
            {
                var entryName = dir.GetNext();
                if (string.IsNullOrEmpty(entryName))
                {
                    break;
                }

                if (entryName is "." or "..")
                {
                    continue;
                }

                var entryPath = currentDirectory + "/" + entryName;
                if (dir.CurrentIsDir())
                {
                    pendingDirectories.Push(entryPath);
                    continue;
                }

                if (entryName.EndsWith(extensionWithDot, StringComparison.OrdinalIgnoreCase))
                {
                    files.Add(entryPath);
                }
            }

            dir.ListDirEnd();
        }

        return files;
    }

    private static string ReadAllTextFromAnyPath(string path)
    {
        if (!IsGodotVirtualPath(path))
        {
            return File.ReadAllText(path, Encoding.UTF8);
        }

        using var file = Godot.FileAccess.Open(path, Godot.FileAccess.ModeFlags.Read);
        if (file is null)
        {
            throw new IOException($"Failed to open '{path}' with Godot FileAccess.");
        }

        return file.GetAsText();
    }

    private static string DefaultResolveAbsoluteStdlibPath(string resourcePath)
    {
        var trimmedPath = resourcePath?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(trimmedPath))
        {
            return string.Empty;
        }

        if (trimmedPath.StartsWith("res://", StringComparison.OrdinalIgnoreCase) ||
            trimmedPath.StartsWith("user://", StringComparison.OrdinalIgnoreCase))
        {
            return trimmedPath;
        }

        if (!trimmedPath.StartsWith("res://", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                return Path.GetFullPath(trimmedPath);
            }
            catch (Exception)
            {
                return trimmedPath;
            }
        }

        return trimmedPath;
    }

    private static string? TryFindProjectRoot(string startPath)
    {
        if (string.IsNullOrWhiteSpace(startPath))
        {
            return null;
        }

        DirectoryInfo? directory;
        try
        {
            directory = Directory.Exists(startPath)
                ? new DirectoryInfo(startPath)
                : new FileInfo(startPath).Directory;
        }
        catch (Exception)
        {
            return null;
        }

        while (directory is not null)
        {
            var markerPath = Path.Combine(directory.FullName, "project.godot");
            if (File.Exists(markerPath))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        return null;
    }

    private static IEnumerable<string> EnumerateImportCandidates(string importName)
    {
        if (importName.EndsWith(".ms", StringComparison.OrdinalIgnoreCase))
        {
            yield return importName;
            yield break;
        }

        yield return importName + ".ms";
        yield return importName;
    }

    private static bool IsStdlibPathLike(string importName)
    {
        return importName.IndexOf('/', StringComparison.Ordinal) >= 0 ||
               importName.StartsWith(".", StringComparison.Ordinal);
    }

    private static string GetParentPath(string normalizedPath)
    {
        if (normalizedPath == "/")
        {
            return "/";
        }

        var index = normalizedPath.LastIndexOf('/');
        return index <= 0 ? "/" : normalizedPath[..index];
    }

    private static string GetFileStem(string normalizedPath)
    {
        var fileName = normalizedPath;
        var separatorIndex = fileName.LastIndexOf('/');
        if (separatorIndex >= 0)
        {
            fileName = fileName[(separatorIndex + 1)..];
        }

        var extensionIndex = fileName.LastIndexOf('.');
        return extensionIndex <= 0
            ? fileName
            : fileName[..extensionIndex];
    }

    private static bool IsUnderCanonicalRoot(string canonicalPath, string canonicalRoot)
    {
        return string.Equals(canonicalPath, canonicalRoot, StringComparison.Ordinal) ||
               canonicalPath.StartsWith(canonicalRoot + "/", StringComparison.Ordinal);
    }

    private static RuntimeException CreateImportRuntimeError(SystemCallErrorCode code, string message)
    {
        var normalizedMessage = string.IsNullOrWhiteSpace(message) ? "import failed." : message.Trim();
        return new RuntimeException($"{SystemCallErrorCodeTokenMapper.ToApiToken(code)}: {normalizedMessage}");
    }

    internal sealed class ImportRuntimeState
    {
        private readonly Stack<string> scriptPathStack = new();
        private StandardLibraryIndex? standardLibraryIndex;

        internal ImportRuntimeState(
            SystemCallExecutionContext? executionContext,
            string? currentScriptPath,
            MiniScriptSshExecutionMode sshMode,
            double maxIntrinsicCallsPerSecond,
            CancellationToken cancellationToken)
        {
            ExecutionContext = executionContext;
            SshMode = sshMode;
            MaxIntrinsicCallsPerSecond = maxIntrinsicCallsPerSecond;
            CancellationToken = cancellationToken;
            if (!string.IsNullOrWhiteSpace(currentScriptPath))
            {
                scriptPathStack.Push(BaseFileSystem.NormalizePath("/", currentScriptPath));
            }
        }

        internal SystemCallExecutionContext? ExecutionContext { get; }

        internal MiniScriptSshExecutionMode SshMode { get; }

        internal double MaxIntrinsicCallsPerSecond { get; }

        internal CancellationToken CancellationToken { get; }

        internal Dictionary<string, Value> ModuleCache { get; } = new(StringComparer.Ordinal);

        internal HashSet<string> LoadingSet { get; } = new(StringComparer.Ordinal);

        internal string? CurrentScriptPath => scriptPathStack.Count == 0 ? null : scriptPathStack.Peek();

        internal string BuildCacheKey(string canonicalPath)
        {
            var serverId = ExecutionContext?.NodeId?.Trim();
            if (string.IsNullOrWhiteSpace(serverId))
            {
                serverId = "<no-server>";
            }

            return serverId + ":" + canonicalPath;
        }

        internal void PushScriptPath(string scriptPath)
        {
            scriptPathStack.Push(BaseFileSystem.NormalizePath("/", scriptPath));
        }

        internal void PopScriptPath()
        {
            if (scriptPathStack.Count > 0)
            {
                scriptPathStack.Pop();
            }
        }

        internal StandardLibraryIndex GetOrBuildStandardLibraryIndex()
        {
            if (standardLibraryIndex is not null)
            {
                return standardLibraryIndex;
            }

            var resolvedRootPath = ResolveAbsoluteStdlibPath(StandardLibraryResourceRoot);
            if (string.IsNullOrWhiteSpace(resolvedRootPath))
            {
                standardLibraryIndex = StandardLibraryIndex.Empty;
                return standardLibraryIndex;
            }

            var entries = new List<StandardLibraryEntry>();
            IEnumerable<string> libraryFiles;
            if (IsGodotVirtualPath(resolvedRootPath))
            {
                if (Godot.DirAccess.Open(resolvedRootPath) is null)
                {
                    standardLibraryIndex = StandardLibraryIndex.Empty;
                    return standardLibraryIndex;
                }

                libraryFiles = EnumerateGodotVirtualFiles(resolvedRootPath, ".ms");
            }
            else
            {
                if (!Directory.Exists(resolvedRootPath))
                {
                    standardLibraryIndex = StandardLibraryIndex.Empty;
                    return standardLibraryIndex;
                }

                libraryFiles = Directory.GetFiles(resolvedRootPath, "*.ms", SearchOption.AllDirectories);
            }

            foreach (var absoluteFilePath in libraryFiles)
            {
                string relativePath;
                if (IsGodotVirtualPath(resolvedRootPath))
                {
                    relativePath = absoluteFilePath.StartsWith(resolvedRootPath + "/", StringComparison.Ordinal)
                        ? absoluteFilePath[(resolvedRootPath.Length + 1)..]
                        : absoluteFilePath;
                }
                else
                {
                    relativePath = Path.GetRelativePath(resolvedRootPath, absoluteFilePath)
                        .Replace('\\', '/');
                }

                relativePath = relativePath.Trim();
                if (string.IsNullOrWhiteSpace(relativePath))
                {
                    continue;
                }

                var canonicalPath = BaseFileSystem.NormalizePath(StandardLibraryCanonicalRoot, relativePath);
                if (!IsUnderCanonicalRoot(canonicalPath, StandardLibraryCanonicalRoot) ||
                    string.Equals(canonicalPath, StandardLibraryCanonicalRoot, StringComparison.Ordinal))
                {
                    continue;
                }

                var canonicalRelativePath = canonicalPath[(StandardLibraryCanonicalRoot.Length + 1)..];
                var stem = GetFileStem(canonicalPath);
                entries.Add(new StandardLibraryEntry(canonicalRelativePath, canonicalPath, absoluteFilePath, stem));
            }

            standardLibraryIndex = StandardLibraryIndex.Create(entries);
            return standardLibraryIndex;
        }
    }

    private readonly record struct ImportResolvedModule(
        string CanonicalPath,
        string SourceText,
        string DefaultBindingName);

    internal readonly record struct StandardLibraryEntry(
        string RelativePath,
        string CanonicalPath,
        string AbsolutePath,
        string Stem);

    internal sealed class StandardLibraryIndex
    {
        private static readonly IReadOnlyList<StandardLibraryEntry> EmptyEntries = Array.Empty<StandardLibraryEntry>();
        private readonly Dictionary<string, StandardLibraryEntry> byRelativePath;
        private readonly Dictionary<string, List<StandardLibraryEntry>> byStem;

        private StandardLibraryIndex(
            Dictionary<string, StandardLibraryEntry> byRelativePath,
            Dictionary<string, List<StandardLibraryEntry>> byStem)
        {
            this.byRelativePath = byRelativePath;
            this.byStem = byStem;
        }

        internal static StandardLibraryIndex Empty { get; } = new(
            new Dictionary<string, StandardLibraryEntry>(StringComparer.Ordinal),
            new Dictionary<string, List<StandardLibraryEntry>>(StringComparer.Ordinal));

        internal bool IsEmpty => byRelativePath.Count == 0;

        internal static StandardLibraryIndex Create(IEnumerable<StandardLibraryEntry> entries)
        {
            var relativeMap = new Dictionary<string, StandardLibraryEntry>(StringComparer.Ordinal);
            var stemMap = new Dictionary<string, List<StandardLibraryEntry>>(StringComparer.Ordinal);
            foreach (var entry in entries)
            {
                relativeMap[entry.RelativePath] = entry;
                if (!stemMap.TryGetValue(entry.Stem, out var list))
                {
                    list = new List<StandardLibraryEntry>();
                    stemMap[entry.Stem] = list;
                }

                list.Add(entry);
            }

            foreach (var list in stemMap.Values)
            {
                list.Sort(static (left, right) => StringComparer.Ordinal.Compare(left.CanonicalPath, right.CanonicalPath));
            }

            return new StandardLibraryIndex(relativeMap, stemMap);
        }

        internal bool TryGetByRelativePath(string relativePath, out StandardLibraryEntry entry)
        {
            return byRelativePath.TryGetValue(relativePath, out entry);
        }

        internal IReadOnlyList<StandardLibraryEntry> GetByStem(string stem)
        {
            return byStem.TryGetValue(stem, out var entries)
                ? entries
                : EmptyEntries;
        }
    }
}
