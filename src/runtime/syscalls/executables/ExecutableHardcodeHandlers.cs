using System;
using System.Collections.Generic;
using System.Globalization;
using Uplink2.Vfs;

#nullable enable

namespace Uplink2.Runtime.Syscalls;

/// <summary>No-op hardcoded executable handler.</summary>
internal sealed class NoopExecutableHardcodeHandler : IExecutableHardcodeHandler
{
    public string ExecutableId => "noop";

    public SystemCallResult Execute(ExecutableHardcodeInvocation invocation)
    {
        return SystemCallResultFactory.Success();
    }
}

/// <summary>MiniScript hardcoded executable handler (`miniscript &lt;script&gt;`).</summary>
internal sealed class MiniScriptExecutableHardcodeHandler : IExecutableHardcodeHandler
{
    public string ExecutableId => "miniscript";

    public SystemCallResult Execute(ExecutableHardcodeInvocation invocation)
    {
        var context = invocation.Context;
        if (invocation.Arguments.Count < 1)
        {
            return SystemCallResultFactory.Usage("miniscript <script>");
        }

        var scriptPath = BaseFileSystem.NormalizePath(context.Cwd, invocation.Arguments[0]);
        if (!context.Server.DiskOverlay.TryResolveEntry(scriptPath, out var scriptEntry))
        {
            return SystemCallResultFactory.NotFound(scriptPath);
        }

        if (scriptEntry.EntryKind != VfsEntryKind.File)
        {
            return SystemCallResultFactory.NotFile(scriptPath);
        }

        if (scriptEntry.FileKind != VfsFileKind.Text)
        {
            return SystemCallResultFactory.Failure(
                SystemCallErrorCode.InvalidArgs,
                "miniscript source must be text: " + scriptPath);
        }

        if (!context.Server.DiskOverlay.TryReadFileText(scriptPath, out var scriptSource))
        {
            return SystemCallResultFactory.NotFile(scriptPath);
        }

        var scriptArguments = new string[invocation.Arguments.Count - 1];
        for (var index = 1; index < invocation.Arguments.Count; index++)
        {
            scriptArguments[index - 1] = invocation.Arguments[index];
        }

        return MiniScriptExecutionRunner.ExecuteScript(scriptSource, context, scriptArguments);
    }
}

/// <summary>Inspect hardcoded executable handler (`inspect [(-p|--port) &lt;port&gt;] &lt;host|ip&gt; [userId]`).</summary>
internal sealed class InspectExecutableHardcodeHandler : IExecutableHardcodeHandler
{
    private const string DefaultInspectUserId = "root";

    public string ExecutableId => "inspect";

    public SystemCallResult Execute(ExecutableHardcodeInvocation invocation)
    {
        if (!TryParseArguments(invocation.Arguments, out var parsed))
        {
            return CreateFailure(SystemCallErrorCode.InvalidArgs);
        }

        if (!invocation.Context.World.TryRunInspectProbe(
                invocation.Context.Server,
                parsed.HostOrIp,
                parsed.UserId,
                parsed.Port,
                out var inspectResult,
                out var failure))
        {
            return CreateFailure(failure.Code);
        }

        return CreateSuccess(inspectResult);
    }

    private static SystemCallResult CreateSuccess(WorldRuntime.InspectProbeResult inspectResult)
    {
        var lines = new List<string>
        {
            "ssh: open",
            "host: " + inspectResult.HostOrIp,
            "port: " + inspectResult.Port.ToString(CultureInfo.InvariantCulture),
            "user: " + inspectResult.UserId,
        };

        if (!string.IsNullOrWhiteSpace(inspectResult.Banner))
        {
            lines.Add("banner: " + inspectResult.Banner.Trim());
        }

        var passwdInfo = inspectResult.PasswdInfo;
        lines.Add("passwd.kind: " + passwdInfo.Kind);
        if (string.Equals(passwdInfo.Kind, "policy", StringComparison.Ordinal))
        {
            lines.Add("passwd.length: " + passwdInfo.Length!.Value.ToString(CultureInfo.InvariantCulture));
            lines.Add("passwd.alphabetId: " + passwdInfo.AlphabetId);
            lines.Add("passwd.alphabet: " + passwdInfo.Alphabet);
        }
        else if (string.Equals(passwdInfo.Kind, "otp", StringComparison.Ordinal))
        {
            lines.Add("passwd.length: " + passwdInfo.Length!.Value.ToString(CultureInfo.InvariantCulture));
            lines.Add("passwd.alphabetId: " + passwdInfo.AlphabetId);
            lines.Add("passwd.alphabet: " + passwdInfo.Alphabet);
        }

        return SystemCallResultFactory.Success(lines);
    }

    private static SystemCallResult CreateFailure(SystemCallErrorCode code)
    {
        var humanMessage = WorldRuntime.ToInspectProbeHumanMessage(code);
        var errorToken = WorldRuntime.ToInspectProbeErrorCodeToken(code);
        return new SystemCallResult
        {
            Ok = false,
            Code = code,
            Lines = new[]
            {
                "error: " + humanMessage,
                "code: " + errorToken,
            },
        };
    }

    private static bool TryParseArguments(IReadOnlyList<string> arguments, out ParsedInspectArguments parsed)
    {
        parsed = default;
        if (arguments.Count == 0)
        {
            return false;
        }

        var index = 0;
        var port = 22;
        var first = arguments[0];
        if (string.Equals(first, "-p", StringComparison.Ordinal) ||
            string.Equals(first, "--port", StringComparison.Ordinal))
        {
            if (arguments.Count < 2 || !TryParsePort(arguments[1], out port))
            {
                return false;
            }

            index = 2;
        }
        else if (first.StartsWith("-", StringComparison.Ordinal))
        {
            return false;
        }

        var remainingCount = arguments.Count - index;
        if (remainingCount is not 1 and not 2)
        {
            return false;
        }

        var hostOrIp = arguments[index].Trim();
        var userId = remainingCount == 2
            ? arguments[index + 1].Trim()
            : DefaultInspectUserId;
        if (string.IsNullOrWhiteSpace(hostOrIp) || string.IsNullOrWhiteSpace(userId))
        {
            return false;
        }

        parsed = new ParsedInspectArguments(hostOrIp, userId, port);
        return true;
    }

    private static bool TryParsePort(string value, out int port)
    {
        if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out port))
        {
            return false;
        }

        return port is >= 1 and <= 65535;
    }

    private readonly record struct ParsedInspectArguments(string HostOrIp, string UserId, int Port);
}
