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

    /// <summary>Injects terminal module globals into a compiled interpreter instance.</summary>
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

            if (!TryParseExecArguments(context, out var commandLine, out var maxBytes, out var parseError))
            {
                return new Intrinsic.Result(CreateExecFailureMap(SystemCallErrorCode.InvalidArgs, parseError));
            }

            var executionContext = state.ExecutionContext;
            var temporaryTerminalSessionId = executionContext.World.AllocateTerminalSessionId();
            SystemCallResult commandResult;
            try
            {
                commandResult = executionContext.World.ExecuteSystemCall(new SystemCallRequest
                {
                    NodeId = executionContext.NodeId,
                    UserId = executionContext.User.UserId,
                    Cwd = executionContext.Cwd,
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
                ? CreateExecSuccessMap(stdout)
                : CreateExecFailureMap(commandResult.Code, ExtractErrorText(commandResult), stdout));
        };
    }

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
        out string error)
    {
        commandLine = string.Empty;
        maxBytes = null;
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

        return TryParseExecOpts(optsMap, out maxBytes, out error);
    }

    private static bool TryParseExecOpts(
        ValMap optsMap,
        out int? maxBytes,
        out string error)
    {
        maxBytes = null;
        error = string.Empty;

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
            ["code"] = new ValString(SystemCallErrorCode.None.ToString()),
            ["err"] = ValNull.instance,
        };
    }

    private static ValMap CreateResultFailureMap(SystemCallErrorCode code, string err)
    {
        return new ValMap
        {
            ["ok"] = ValNumber.zero,
            ["code"] = new ValString(code.ToString()),
            ["err"] = new ValString(err),
        };
    }

    private static ValMap CreateExecSuccessMap(string stdout)
    {
        return new ValMap
        {
            ["ok"] = ValNumber.one,
            ["code"] = new ValString(SystemCallErrorCode.None.ToString()),
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

    private sealed class TermModuleState
    {
        internal TermModuleState(SystemCallExecutionContext? executionContext)
        {
            ExecutionContext = executionContext;
        }

        internal SystemCallExecutionContext? ExecutionContext { get; }
    }
}
