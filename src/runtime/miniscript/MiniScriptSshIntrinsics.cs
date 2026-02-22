using Miniscript;
using System;
using System.Collections.Generic;
using Uplink2.Runtime.Syscalls;

#nullable enable

namespace Uplink2.Runtime.MiniScript;

/// <summary>Registers and injects project-specific SSH intrinsics into MiniScript interpreters.</summary>
internal static class MiniScriptSshIntrinsics
{
    private const string SshConnectIntrinsicName = "uplink_ssh_connect";
    private const string SshDisconnectIntrinsicName = "uplink_ssh_disconnect";
    private const string SessionKind = "sshSession";
    private const string SessionNodeIdKey = "sessionNodeId";
    private const string SessionIdKey = "sessionId";

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

        var sshModule = new ValMap
        {
            userData = new SshModuleState(executionContext, mode),
        };
        sshModule["connect"] = Intrinsic.GetByName(SshConnectIntrinsicName).GetFunc();
        sshModule["disconnect"] = Intrinsic.GetByName(SshDisconnectIntrinsicName).GetFunc();
        interpreter.SetGlobalValue("ssh", sshModule);
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
        intrinsic.code = (context, _) =>
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
            var port = context.GetLocalInt("port", 22);
            if (port is < 1 or > 65535)
            {
                return new Intrinsic.Result(
                    CreateConnectFailureMap(
                        SystemCallErrorCode.InvalidArgs,
                        $"invalid port: {port}"));
            }

            if (state.Mode == MiniScriptSshExecutionMode.RealWorld)
            {
                if (!executionContext.World.TryOpenSshSession(
                        executionContext.Server,
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

                return new Intrinsic.Result(CreateConnectSuccessMap(openResult));
            }

            if (!executionContext.World.TryValidateSshSessionOpen(
                    executionContext.Server,
                    hostOrIp,
                    userId,
                    password,
                    port,
                    out var validated,
                    out var sandboxFailure))
            {
                return new Intrinsic.Result(CreateConnectFailureMap(sandboxFailure.Code, ExtractErrorText(sandboxFailure)));
            }

            var sandboxSessionId = state.RegisterSandboxSession(validated.TargetNodeId);
            return new Intrinsic.Result(CreateConnectSuccessMap(new WorldRuntime.SshSessionOpenResult
            {
                TargetServer = validated.TargetServer,
                TargetNodeId = validated.TargetNodeId,
                TargetUserKey = validated.TargetUserKey,
                TargetUserId = validated.TargetUserId,
                SessionId = sandboxSessionId,
                RemoteIp = validated.RemoteIp,
                HostOrIp = validated.HostOrIp,
            }));
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
        intrinsic.code = (context, _) =>
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
            if (session is not ValMap sessionMap)
            {
                return new Intrinsic.Result(
                    CreateDisconnectFailureMap(
                        SystemCallErrorCode.InvalidArgs,
                        "session object is required."));
            }

            if (!TryReadSessionIdentity(sessionMap, out var sessionNodeId, out var sessionId, out var readError))
            {
                return new Intrinsic.Result(CreateDisconnectFailureMap(SystemCallErrorCode.InvalidArgs, readError));
            }

            var disconnected = state.Mode == MiniScriptSshExecutionMode.RealWorld
                ? executionContext.World.TryRemoveRemoteSession(sessionNodeId, sessionId)
                : state.TryRemoveSandboxSession(sessionNodeId, sessionId);
            return new Intrinsic.Result(CreateDisconnectSuccessMap(disconnected));
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

    private static bool TryReadSessionIdentity(
        ValMap sessionMap,
        out string sessionNodeId,
        out int sessionId,
        out string error)
    {
        sessionNodeId = string.Empty;
        sessionId = 0;
        error = string.Empty;

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

    private static ValMap CreateConnectSuccessMap(WorldRuntime.SshSessionOpenResult openResult)
    {
        var session = new ValMap
        {
            ["kind"] = new ValString(SessionKind),
            [SessionIdKey] = new ValNumber(openResult.SessionId),
            [SessionNodeIdKey] = new ValString(openResult.TargetNodeId),
            ["userId"] = new ValString(openResult.TargetUserId),
            ["hostOrIp"] = new ValString(openResult.HostOrIp),
            ["remoteIp"] = new ValString(openResult.RemoteIp),
        };

        return new ValMap
        {
            ["ok"] = ValNumber.one,
            ["code"] = new ValString(SystemCallErrorCode.None.ToString()),
            ["err"] = ValNull.instance,
            ["session"] = session,
        };
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
        return result;
    }

    private static ValMap CreateDisconnectSuccessMap(bool disconnected)
    {
        return new ValMap
        {
            ["ok"] = ValNumber.one,
            ["code"] = new ValString(SystemCallErrorCode.None.ToString()),
            ["err"] = ValNull.instance,
            ["disconnected"] = disconnected ? ValNumber.one : ValNumber.zero,
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

    private sealed class SshModuleState
    {
        private readonly Dictionary<int, string> sandboxSessionNodeIdById = new();
        private int nextSandboxSessionId = 1;

        internal SshModuleState(SystemCallExecutionContext? executionContext, MiniScriptSshExecutionMode mode)
        {
            ExecutionContext = executionContext;
            Mode = mode;
        }

        internal SystemCallExecutionContext? ExecutionContext { get; }

        internal MiniScriptSshExecutionMode Mode { get; }

        internal int RegisterSandboxSession(string sessionNodeId)
        {
            var sessionId = nextSandboxSessionId++;
            sandboxSessionNodeIdById[sessionId] = sessionNodeId;
            return sessionId;
        }

        internal bool TryRemoveSandboxSession(string sessionNodeId, int sessionId)
        {
            if (!sandboxSessionNodeIdById.TryGetValue(sessionId, out var storedNodeId))
            {
                return false;
            }

            if (!string.Equals(storedNodeId, sessionNodeId, StringComparison.Ordinal))
            {
                return false;
            }

            sandboxSessionNodeIdById.Remove(sessionId);
            return true;
        }
    }
}
