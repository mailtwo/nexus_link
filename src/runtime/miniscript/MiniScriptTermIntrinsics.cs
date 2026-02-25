using Miniscript;
using System;
using System.Text;
using Uplink2.Runtime.Syscalls;

#nullable enable

namespace Uplink2.Runtime.MiniScript;

/// <summary>Registers and injects project-specific terminal intrinsics into MiniScript interpreters.</summary>
internal static class MiniScriptTermIntrinsics
{
    private const string TermExecIntrinsicName = "uplink_term_exec";
    private const string TermPrintIntrinsicName = "uplink_term_print";
    private const string TermWarnIntrinsicName = "uplink_term_warn";
    private const string TermErrorIntrinsicName = "uplink_term_error";

    private static readonly object registrationSync = new();
    private static bool isRegistered;

    /// <summary>Ensures custom terminal intrinsics are registered exactly once per process.</summary>
    internal static void EnsureRegistered()
    {
        lock (registrationSync)
        {
            if (isRegistered)
            {
                return;
            }

            RegisterTermExecIntrinsic();
            RegisterTermPrintIntrinsic();
            RegisterTermWarnIntrinsic();
            RegisterTermErrorIntrinsic();
            isRegistered = true;
        }
    }

    /// <summary>인터프리터에 term 모듈 전역 API를 주입합니다.</summary>
    /// <remarks>
    /// MiniScript: <c>term.exec(cmd, opts?)</c>, <c>term.print(text)</c>, <c>term.warn(text)</c>, <c>term.error(text)</c>.
    /// 각 API는 공통 ResultMap(<c>ok/code/err/cost/trace</c>) 규약을 따르며, <c>term.exec</c>는 payload(<c>stdout/exitCode/jobId</c>)를 추가합니다.
    /// <c>term.exec</c>는 실행 컨텍스트가 필요하며 비동기 스케줄/세션 정리 부작용이 있습니다.
    /// See: <see href="/docfx_api_document/api/term.md#module-term">Manual</see>.
    /// </remarks>
    /// <param name="interpreter">term 모듈 전역을 주입할 대상 인터프리터입니다.</param>
    /// <param name="executionContext"><c>term.exec</c>에서 시스템 호출을 수행할 실행 컨텍스트입니다.</param>
    internal static void InjectTermModule(Interpreter interpreter, SystemCallExecutionContext? executionContext)
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

        var moduleState = new TermModuleState(executionContext);
        var termModule = new ValMap
        {
            userData = moduleState,
        };
        termModule["exec"] = Intrinsic.GetByName(TermExecIntrinsicName).GetFunc();
        termModule["print"] = Intrinsic.GetByName(TermPrintIntrinsicName).GetFunc();
        termModule["warn"] = Intrinsic.GetByName(TermWarnIntrinsicName).GetFunc();
        termModule["error"] = Intrinsic.GetByName(TermErrorIntrinsicName).GetFunc();
        interpreter.SetGlobalValue("term", termModule);
    }

    /// <summary><c>term.exec</c> intrinsic을 등록합니다.</summary>
    /// <remarks>
    /// MiniScript: <c>r = term.exec(cmd, opts?)</c>.
    /// ResultMap(<c>ok/code/err/cost/trace</c>)에 payload(<c>stdout/exitCode/jobId</c>)를 함께 반환합니다.
    /// <c>opts.async=true</c>일 때는 명령 완료가 아니라 비동기 작업 스케줄 성공/실패를 보고합니다.
    /// See: <see href="/docfx_api_document/api/term.md#termexec">Manual</see>.
    /// </remarks>
    private static void RegisterTermExecIntrinsic()
    {
        if (Intrinsic.GetByName(TermExecIntrinsicName) is not null)
        {
            return;
        }

        var intrinsic = Intrinsic.Create(TermExecIntrinsicName);
        intrinsic.AddParam("cmd");
        intrinsic.AddParam("opts");
        intrinsic.code = (context, partialResult) =>
        {
            if (!TryGetExecutionState(context, out var state) || state.ExecutionContext is null)
            {
                return new Intrinsic.Result(
                    CreateExecFailureMap(
                        SystemCallErrorCode.InternalError,
                        "term.exec is unavailable in this execution context."));
            }

            if (!TryParseExecArguments(
                    context,
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
            var temporaryTerminalSessionId = executionContext.World.AllocateTerminalSessionId();
            var request = new SystemCallRequest
            {
                NodeId = executionContext.NodeId,
                UserId = executionContext.User.UserId,
                Cwd = executionContext.Cwd,
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
                ? CreateExecSuccessMap(stdout, asyncExecution: false, jobId: null)
                : CreateExecFailureMap(
                    commandResult.Code,
                    ExtractErrorText(commandResult),
                    asyncExecution: false,
                    stdout));
        };
    }

    /// <summary><c>term.print</c> intrinsic을 등록합니다.</summary>
    /// <remarks>
    /// MiniScript: <c>r = term.print(text)</c>.
    /// ResultMap(<c>ok/code/err/cost/trace</c>)를 반환하고 성공 시 payload(<c>printed=1</c>)를 포함합니다.
    /// 호출 시 표준 출력으로 로그 라인을 기록합니다.
    /// See: <see href="/docfx_api_document/api/term.md#termprint">Manual</see>.
    /// </remarks>
    private static void RegisterTermPrintIntrinsic()
    {
        if (Intrinsic.GetByName(TermPrintIntrinsicName) is not null)
        {
            return;
        }

        var intrinsic = Intrinsic.Create(TermPrintIntrinsicName);
        intrinsic.AddParam("text");
        intrinsic.code = (context, partialResult) =>
        {
            if (!TryReadTextArg(context, out var text, out var error))
            {
                return new Intrinsic.Result(CreateResultFailureMap(SystemCallErrorCode.InvalidArgs, error));
            }

            WriteStandardOutput(context, text);
            return new Intrinsic.Result(CreateResultSuccessMap());
        };
    }

    /// <summary><c>term.warn</c> intrinsic을 등록합니다.</summary>
    /// <remarks>
    /// MiniScript: <c>r = term.warn(text)</c>.
    /// ResultMap(<c>ok/code/err/cost/trace</c>)를 반환하고 성공 시 payload(<c>printed=1</c>)를 포함합니다.
    /// 호출 시 <c>warn:</c> 접두사와 함께 stderr 로그를 기록하며 스크립트 실패를 직접 유발하지 않습니다.
    /// See: <see href="/docfx_api_document/api/term.md#termwarn">Manual</see>.
    /// </remarks>
    private static void RegisterTermWarnIntrinsic()
    {
        if (Intrinsic.GetByName(TermWarnIntrinsicName) is not null)
        {
            return;
        }

        var intrinsic = Intrinsic.Create(TermWarnIntrinsicName);
        intrinsic.AddParam("text");
        intrinsic.code = (context, partialResult) =>
        {
            if (!TryReadTextArg(context, out var text, out var error))
            {
                return new Intrinsic.Result(CreateResultFailureMap(SystemCallErrorCode.InvalidArgs, error));
            }

            WriteErrorOutput(context, "warn: " + text);
            return new Intrinsic.Result(CreateResultSuccessMap());
        };
    }

    /// <summary><c>term.error</c> intrinsic을 등록합니다.</summary>
    /// <remarks>
    /// MiniScript: <c>r = term.error(text)</c>.
    /// ResultMap(<c>ok/code/err/cost/trace</c>)를 반환하고 성공 시 payload(<c>printed=1</c>)를 포함합니다.
    /// 호출 시 <c>error:</c> 접두사와 함께 stderr 로그를 기록하며 스크립트 실패 상태는 호출자가 결정합니다.
    /// See: <see href="/docfx_api_document/api/term.md#termerror">Manual</see>.
    /// </remarks>
    private static void RegisterTermErrorIntrinsic()
    {
        if (Intrinsic.GetByName(TermErrorIntrinsicName) is not null)
        {
            return;
        }

        var intrinsic = Intrinsic.Create(TermErrorIntrinsicName);
        intrinsic.AddParam("text");
        intrinsic.code = (context, partialResult) =>
        {
            if (!TryReadTextArg(context, out var text, out var error))
            {
                return new Intrinsic.Result(CreateResultFailureMap(SystemCallErrorCode.InvalidArgs, error));
            }

            WriteErrorOutput(context, "error: " + text);
            return new Intrinsic.Result(CreateResultSuccessMap());
        };
    }

    private static bool TryGetExecutionState(TAC.Context context, out TermModuleState state)
    {
        state = null!;
        if (context.self is not ValMap selfMap ||
            selfMap.userData is not TermModuleState termState)
        {
            return false;
        }

        state = termState;
        return true;
    }

    private static bool TryParseExecArguments(
        TAC.Context context,
        out string commandLine,
        out int? maxBytes,
        out bool asyncExecution,
        out string error)
    {
        commandLine = string.Empty;
        maxBytes = null;
        asyncExecution = false;
        error = string.Empty;

        if (context.GetLocal("cmd") is not ValString rawCommand)
        {
            error = "cmd must be a string.";
            return false;
        }

        commandLine = rawCommand.value?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(commandLine))
        {
            error = "cmd is required.";
            return false;
        }

        var rawOpts = context.GetLocal("opts");
        if (rawOpts is null)
        {
            return true;
        }

        if (rawOpts is not ValMap optsMap)
        {
            error = "opts must be a map.";
            return false;
        }

        return TryParseExecOpts(optsMap, out maxBytes, out asyncExecution, out error);
    }

    private static bool TryParseExecOpts(
        ValMap optsMap,
        out int? maxBytes,
        out bool asyncExecution,
        out string error)
    {
        maxBytes = null;
        asyncExecution = false;
        error = string.Empty;

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

        if (rawMaxBytes is not ValNumber maxBytesNumber ||
            double.IsNaN(maxBytesNumber.value) ||
            double.IsInfinity(maxBytesNumber.value) ||
            maxBytesNumber.value < 0 ||
            maxBytesNumber.value > int.MaxValue ||
            Math.Truncate(maxBytesNumber.value) != maxBytesNumber.value)
        {
            error = "opts.maxBytes must be a non-negative integer.";
            return false;
        }

        maxBytes = (int)maxBytesNumber.value;
        return true;
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

    private static bool TryReadTextArg(TAC.Context context, out string text, out string error)
    {
        text = string.Empty;
        error = string.Empty;
        if (context.GetLocal("text") is not ValString rawText)
        {
            error = "text must be a string.";
            return false;
        }

        text = rawText.value ?? string.Empty;
        return true;
    }

    private static void WriteStandardOutput(TAC.Context context, string line)
    {
        if (context.vm?.standardOutput is not null)
        {
            context.vm.standardOutput(line, true);
            return;
        }

        context.interpreter?.standardOutput?.Invoke(line, true);
    }

    private static void WriteErrorOutput(TAC.Context context, string line)
    {
        if (context.interpreter?.errorOutput is not null)
        {
            context.interpreter.errorOutput(line, true);
            return;
        }

        WriteStandardOutput(context, line);
    }

    private static ValMap CreateResultSuccessMap()
    {
        return new ValMap
        {
            ["ok"] = ValNumber.one,
            ["code"] = new ValString(SystemCallErrorCodeTokenMapper.ToApiToken(SystemCallErrorCode.None)),
            ["err"] = ValNull.instance,
        };
    }

    private static ValMap CreateResultFailureMap(SystemCallErrorCode code, string err)
    {
        return new ValMap
        {
            ["ok"] = ValNumber.zero,
            ["code"] = new ValString(SystemCallErrorCodeTokenMapper.ToApiToken(code)),
            ["err"] = new ValString(err),
        };
    }

    private static ValMap CreateExecSuccessMap(string stdout, bool asyncExecution, string? jobId)
    {
        var result = new ValMap
        {
            ["ok"] = ValNumber.one,
            ["code"] = new ValString(SystemCallErrorCodeTokenMapper.ToApiToken(SystemCallErrorCode.None)),
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

    private sealed class TermModuleState
    {
        internal TermModuleState(SystemCallExecutionContext? executionContext)
        {
            ExecutionContext = executionContext;
        }

        internal SystemCallExecutionContext? ExecutionContext { get; }
    }
}
