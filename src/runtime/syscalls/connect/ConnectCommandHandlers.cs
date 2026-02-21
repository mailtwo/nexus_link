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

        if (!context.World.TryOpenSshSession(
                context.Server,
                parsed.HostOrIp,
                parsed.UserId,
                parsed.Password,
                parsed.Port,
                via: "connect",
                out var openResult,
                out var failureResult))
        {
            return failureResult;
        }

        var terminalSessionId = context.World.NormalizeTerminalSessionId(context.TerminalSessionId);

        context.World.PushTerminalConnectionFrame(
            terminalSessionId,
            context.NodeId,
            context.UserKey,
            context.Cwd,
            context.World.ResolvePromptUser(context.Server, context.UserKey),
            context.World.ResolvePromptHost(context.Server),
            openResult.TargetNodeId,
            openResult.SessionId);

        var transition = new TerminalContextTransition
        {
            NextNodeId = openResult.TargetNodeId,
            NextUserId = openResult.TargetUserId,
            NextPromptUser = openResult.TargetUserId,
            NextPromptHost = context.World.ResolvePromptHost(openResult.TargetServer),
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
        var userId = arguments[index + 1].Trim();
        var password = arguments[index + 2];
        if (string.IsNullOrWhiteSpace(hostOrIp))
        {
            result = SystemCallResultFactory.Failure(SystemCallErrorCode.InvalidArgs, "host or ip is required.");
            return false;
        }

        if (string.IsNullOrWhiteSpace(userId))
        {
            result = SystemCallResultFactory.Failure(SystemCallErrorCode.InvalidArgs, "user is required.");
            return false;
        }

        parsed = new ParsedConnectArguments(hostOrIp, userId, password, port);
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

    private readonly record struct ParsedConnectArguments(string HostOrIp, string UserId, string Password, int Port);
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

        var previousUserId = frame.PreviousPromptUser;
        if (context.World.TryGetServer(frame.PreviousNodeId, out var previousServer))
        {
            previousUserId = context.World.ResolvePromptUser(previousServer, frame.PreviousUserKey);
        }

        var transition = new TerminalContextTransition
        {
            NextNodeId = frame.PreviousNodeId,
            NextUserId = previousUserId,
            NextPromptUser = frame.PreviousPromptUser,
            NextPromptHost = frame.PreviousPromptHost,
            NextCwd = frame.PreviousCwd,
        };

        return SystemCallResultFactory.Success(nextCwd: transition.NextCwd, data: transition);
    }
}

internal sealed class KnownCommandHandler : ISystemCallHandler
{
    private const string InternetNetId = "internet";
    private const string EmptyKnownMessage = "No known public IPs found.";

    public string Command => "known";

    public SystemCallResult Execute(SystemCallExecutionContext context, IReadOnlyList<string> arguments)
    {
        if (arguments.Count != 0)
        {
            return SystemCallResultFactory.Usage("known");
        }

        var knownNodesByNet = context.World.KnownNodesByNet;
        if (knownNodesByNet is null ||
            !knownNodesByNet.TryGetValue(InternetNetId, out var knownNodeIds) ||
            knownNodeIds.Count == 0)
        {
            return SystemCallResultFactory.Success(lines: new[] { EmptyKnownMessage });
        }

        var rows = new List<KnownHostRow>(knownNodeIds.Count);
        foreach (var nodeId in knownNodeIds)
        {
            if (!context.World.TryGetServer(nodeId, out var server))
            {
                continue;
            }

            if (!TryGetInterfaceIp(server, InternetNetId, out var publicIp))
            {
                continue;
            }

            var hostname = context.World.ResolvePromptHost(server);
            rows.Add(new KnownHostRow(hostname, publicIp));
        }

        if (rows.Count == 0)
        {
            return SystemCallResultFactory.Success(lines: new[] { EmptyKnownMessage });
        }

        rows.Sort(static (a, b) =>
        {
            var ipCompare = StringComparer.Ordinal.Compare(a.Ip, b.Ip);
            return ipCompare != 0 ? ipCompare : StringComparer.Ordinal.Compare(a.Hostname, b.Hostname);
        });

        var hostnameWidth = "HOSTNAME".Length;
        foreach (var row in rows)
        {
            if (row.Hostname.Length > hostnameWidth)
            {
                hostnameWidth = row.Hostname.Length;
            }
        }

        var lines = new List<string>(rows.Count + 1)
        {
            $"{"HOSTNAME".PadRight(hostnameWidth)}  IP",
        };

        foreach (var row in rows)
        {
            lines.Add($"{row.Hostname.PadRight(hostnameWidth)}  {row.Ip}");
        }

        return SystemCallResultFactory.Success(lines: lines);
    }

    private static bool TryGetInterfaceIp(ServerNodeRuntime server, string netId, out string ip)
    {
        foreach (var iface in server.Interfaces)
        {
            if (!string.Equals(iface.NetId, netId, StringComparison.Ordinal) ||
                string.IsNullOrWhiteSpace(iface.Ip))
            {
                continue;
            }

            ip = iface.Ip;
            return true;
        }

        ip = string.Empty;
        return false;
    }

    private readonly record struct KnownHostRow(string Hostname, string Ip);
}

internal sealed class ScanCommandHandler : ISystemCallHandler
{
    private const string InternetNetId = "internet";
    private const string PermissionDeniedMessage = "scan: permission denied";
    private const string NoNeighborMessage =
        "No adjacent servers found (this server is not connected to a subnet or is the player workstation).";

    public string Command => "scan";

    public SystemCallResult Execute(SystemCallExecutionContext context, IReadOnlyList<string> arguments)
    {
        if (arguments.Count != 0)
        {
            return SystemCallResultFactory.Usage("scan");
        }

        if (!context.User.Privilege.Execute)
        {
            return SystemCallResultFactory.Failure(SystemCallErrorCode.PermissionDenied, PermissionDeniedMessage);
        }

        if (IsPlayerWorkstation(context) || !HasNonInternetInterface(context.Server))
        {
            return SystemCallResultFactory.Success(lines: new[] { NoNeighborMessage });
        }

        var neighborIps = new List<string>();
        var seenNeighborIps = new HashSet<string>(StringComparer.Ordinal);
        foreach (var neighborNodeId in context.Server.LanNeighbors)
        {
            if (!context.World.TryGetServer(neighborNodeId, out var neighborServer))
            {
                continue;
            }

            var neighborIp = ResolveNeighborIp(context.Server, neighborServer);
            if (string.IsNullOrWhiteSpace(neighborIp) || !seenNeighborIps.Add(neighborIp))
            {
                continue;
            }

            neighborIps.Add(neighborIp);
        }

        if (neighborIps.Count == 0)
        {
            return SystemCallResultFactory.Success(lines: new[] { NoNeighborMessage });
        }

        neighborIps.Sort(StringComparer.Ordinal);

        var currentIp = ResolveCurrentSubnetIp(context.Server);
        if (string.IsNullOrWhiteSpace(currentIp))
        {
            currentIp = context.Server.PrimaryIp ?? string.Empty;
        }

        if (string.IsNullOrWhiteSpace(currentIp))
        {
            return SystemCallResultFactory.Success(lines: neighborIps);
        }

        var prefix = currentIp + " - ";
        var lines = new List<string>(neighborIps.Count)
        {
            prefix + neighborIps[0],
        };

        if (neighborIps.Count > 1)
        {
            var indent = new string(' ', prefix.Length);
            for (var index = 1; index < neighborIps.Count; index++)
            {
                lines.Add(indent + neighborIps[index]);
            }
        }

        return SystemCallResultFactory.Success(lines: lines);
    }

    private static bool IsPlayerWorkstation(SystemCallExecutionContext context)
    {
        var workstation = context.World.PlayerWorkstationServer;
        return workstation is not null &&
               string.Equals(workstation.NodeId, context.Server.NodeId, StringComparison.Ordinal);
    }

    private static bool HasNonInternetInterface(ServerNodeRuntime server)
    {
        foreach (var iface in server.Interfaces)
        {
            if (!string.IsNullOrWhiteSpace(iface.Ip) &&
                !string.Equals(iface.NetId, InternetNetId, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static string ResolveCurrentSubnetIp(ServerNodeRuntime server)
    {
        foreach (var iface in server.Interfaces)
        {
            if (!string.IsNullOrWhiteSpace(iface.Ip) &&
                !string.Equals(iface.NetId, InternetNetId, StringComparison.Ordinal))
            {
                return iface.Ip;
            }
        }

        return string.Empty;
    }

    private static string ResolveNeighborIp(ServerNodeRuntime source, ServerNodeRuntime neighbor)
    {
        foreach (var sourceInterface in source.Interfaces)
        {
            if (string.Equals(sourceInterface.NetId, InternetNetId, StringComparison.Ordinal))
            {
                continue;
            }

            if (TryGetInterfaceIp(neighbor, sourceInterface.NetId, out var neighborIp))
            {
                return neighborIp;
            }
        }

        foreach (var sourceInterface in source.Interfaces)
        {
            if (TryGetInterfaceIp(neighbor, sourceInterface.NetId, out var neighborIp))
            {
                return neighborIp;
            }
        }

        foreach (var neighborInterface in neighbor.Interfaces)
        {
            if (!string.IsNullOrWhiteSpace(neighborInterface.Ip) &&
                !string.Equals(neighborInterface.NetId, InternetNetId, StringComparison.Ordinal))
            {
                return neighborInterface.Ip;
            }
        }

        if (!string.IsNullOrWhiteSpace(neighbor.PrimaryIp))
        {
            return neighbor.PrimaryIp;
        }

        foreach (var neighborInterface in neighbor.Interfaces)
        {
            if (!string.IsNullOrWhiteSpace(neighborInterface.Ip))
            {
                return neighborInterface.Ip;
            }
        }

        return string.Empty;
    }

    private static bool TryGetInterfaceIp(ServerNodeRuntime server, string netId, out string ip)
    {
        foreach (var iface in server.Interfaces)
        {
            if (!string.Equals(iface.NetId, netId, StringComparison.Ordinal) ||
                string.IsNullOrWhiteSpace(iface.Ip))
            {
                continue;
            }

            ip = iface.Ip;
            return true;
        }

        ip = string.Empty;
        return false;
    }
}
