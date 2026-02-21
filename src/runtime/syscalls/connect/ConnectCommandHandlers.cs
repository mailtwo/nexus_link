using System;
using System.Collections.Generic;
using System.Globalization;

#nullable enable

namespace Uplink2.Runtime.Syscalls;

internal sealed class ConnectCommandHandler : ISystemCallHandler
{
    private const string UsageText = "connect [(-p|--port) <port>] <host|ip> <user> <password>";

    public string Command => "connect";

    public SystemCallResult Execute(SystemCallExecutionContext context, IReadOnlyList<string> arguments)
    {
        if (!TryParseArguments(arguments, out var parsed, out var parseResult))
        {
            return parseResult!;
        }

        if (!context.World.TryResolveServerByHostOrIp(parsed.HostOrIp, out var targetServer))
        {
            return SystemCallResultFactory.Failure(SystemCallErrorCode.NotFound, $"host not found: {parsed.HostOrIp}");
        }

        if (targetServer.Status != ServerStatus.Online)
        {
            return SystemCallResultFactory.Failure(SystemCallErrorCode.NotFound, $"server offline: {targetServer.NodeId}");
        }

        if (!targetServer.Ports.TryGetValue(parsed.Port, out var targetPort) ||
            targetPort.PortType == PortType.None)
        {
            return SystemCallResultFactory.Failure(SystemCallErrorCode.NotFound, $"port not available: {parsed.Port}");
        }

        if (targetPort.PortType != PortType.Ssh)
        {
            return SystemCallResultFactory.Failure(SystemCallErrorCode.InvalidArgs, $"port {parsed.Port} is not ssh.");
        }

        if (!IsPortExposureAllowed(context.Server, targetServer, targetPort.Exposure))
        {
            return SystemCallResultFactory.Failure(SystemCallErrorCode.PermissionDenied, $"port exposure denied: {parsed.Port}");
        }

        if (!targetServer.Users.TryGetValue(parsed.UserKey, out var targetUser))
        {
            return SystemCallResultFactory.Failure(SystemCallErrorCode.NotFound, $"user not found: {parsed.UserKey}");
        }

        if (!IsAuthenticationSuccessful(targetUser, parsed.Password, out var authFailureResult))
        {
            return authFailureResult!;
        }

        var terminalSessionId = context.World.NormalizeTerminalSessionId(context.TerminalSessionId);
        var sessionId = context.World.AllocateTerminalRemoteSessionId();
        var remoteIp = context.World.ResolveRemoteIpForSession(context.Server, targetServer);
        targetServer.UpsertSession(sessionId, new SessionConfig
        {
            UserKey = parsed.UserKey,
            RemoteIp = remoteIp,
            Cwd = "/",
        });

        context.World.PushTerminalConnectionFrame(
            terminalSessionId,
            context.NodeId,
            context.UserKey,
            context.Cwd,
            context.World.ResolvePromptUser(context.Server, context.UserKey),
            context.World.ResolvePromptHost(context.Server),
            targetServer.NodeId,
            sessionId);

        var transition = new TerminalContextTransition
        {
            NextNodeId = targetServer.NodeId,
            NextUserKey = parsed.UserKey,
            NextPromptUser = context.World.ResolvePromptUser(targetServer, parsed.UserKey),
            NextPromptHost = context.World.ResolvePromptHost(targetServer),
            NextCwd = "/",
        };

        return SystemCallResultFactory.Success(nextCwd: transition.NextCwd, data: transition);
    }

    private static bool TryParseArguments(
        IReadOnlyList<string> arguments,
        out ParsedConnectArguments parsed,
        out SystemCallResult? result)
    {
        parsed = default;
        result = null;

        if (arguments.Count == 0)
        {
            result = SystemCallResultFactory.Usage(UsageText);
            return false;
        }

        var index = 0;
        var port = 22;
        var first = arguments[0];
        if (string.Equals(first, "-p", StringComparison.Ordinal) ||
            string.Equals(first, "--port", StringComparison.Ordinal))
        {
            if (arguments.Count < 2)
            {
                result = SystemCallResultFactory.Usage(UsageText);
                return false;
            }

            if (!TryParsePort(arguments[1], out port))
            {
                result = SystemCallResultFactory.Failure(SystemCallErrorCode.InvalidArgs, $"invalid port: {arguments[1]}");
                return false;
            }

            index = 2;
        }
        else if (first.StartsWith("-", StringComparison.Ordinal))
        {
            result = SystemCallResultFactory.Usage(UsageText);
            return false;
        }

        if (arguments.Count - index != 3)
        {
            result = SystemCallResultFactory.Usage(UsageText);
            return false;
        }

        var hostOrIp = arguments[index].Trim();
        var userKey = arguments[index + 1].Trim();
        var password = arguments[index + 2];
        if (string.IsNullOrWhiteSpace(hostOrIp))
        {
            result = SystemCallResultFactory.Failure(SystemCallErrorCode.InvalidArgs, "host or ip is required.");
            return false;
        }

        if (string.IsNullOrWhiteSpace(userKey))
        {
            result = SystemCallResultFactory.Failure(SystemCallErrorCode.InvalidArgs, "user is required.");
            return false;
        }

        parsed = new ParsedConnectArguments(hostOrIp, userKey, password, port);
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

    private static bool IsPortExposureAllowed(ServerNodeRuntime source, ServerNodeRuntime target, PortExposure exposure)
    {
        return exposure switch
        {
            PortExposure.Public => true,
            PortExposure.Lan => source.SubnetMembership.Overlaps(target.SubnetMembership),
            PortExposure.Localhost => string.Equals(source.NodeId, target.NodeId, StringComparison.Ordinal),
            _ => false,
        };
    }

    private static bool IsAuthenticationSuccessful(
        UserConfig targetUser,
        string password,
        out SystemCallResult? failureResult)
    {
        failureResult = null;

        if (targetUser.AuthMode == AuthMode.None)
        {
            return true;
        }

        if (targetUser.AuthMode == AuthMode.Static)
        {
            if (string.Equals(targetUser.UserPasswd, password, StringComparison.Ordinal))
            {
                return true;
            }

            failureResult = SystemCallResultFactory.Failure(SystemCallErrorCode.PermissionDenied, "authentication failed.");
            return false;
        }

        failureResult = SystemCallResultFactory.Failure(
            SystemCallErrorCode.PermissionDenied,
            $"authentication mode not supported: {targetUser.AuthMode}.");
        return false;
    }

    private readonly record struct ParsedConnectArguments(string HostOrIp, string UserKey, string Password, int Port);
}

internal sealed class DisconnectCommandHandler : ISystemCallHandler
{
    public string Command => "disconnect";

    public SystemCallResult Execute(SystemCallExecutionContext context, IReadOnlyList<string> arguments)
    {
        if (arguments.Count != 0)
        {
            return SystemCallResultFactory.Usage("disconnect");
        }

        var terminalSessionId = context.World.NormalizeTerminalSessionId(context.TerminalSessionId);
        if (!context.World.TryPopTerminalConnectionFrame(terminalSessionId, out var frame))
        {
            return SystemCallResultFactory.Failure(SystemCallErrorCode.InvalidArgs, "not connected.");
        }

        context.World.RemoveTerminalRemoteSession(frame.SessionNodeId, frame.SessionId);

        var transition = new TerminalContextTransition
        {
            NextNodeId = frame.PreviousNodeId,
            NextUserKey = frame.PreviousUserKey,
            NextPromptUser = frame.PreviousPromptUser,
            NextPromptHost = frame.PreviousPromptHost,
            NextCwd = frame.PreviousCwd,
        };

        return SystemCallResultFactory.Success(nextCwd: transition.NextCwd, data: transition);
    }
}
