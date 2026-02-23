using System;
using System.Collections.Generic;
using System.Globalization;
using Uplink2.Vfs;

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
        var motdLines = context.World.ResolveMotdLinesForLogin(openResult.TargetServer, openResult.TargetUserKey);

        return SystemCallResultFactory.Success(lines: motdLines, nextCwd: transition.NextCwd, data: transition);
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
            NextPromptUser = previousUserId,
            NextPromptHost = frame.PreviousPromptHost,
            NextCwd = frame.PreviousCwd,
        };

        return SystemCallResultFactory.Success(nextCwd: transition.NextCwd, data: transition);
    }
}

internal sealed class FtpCommandHandler : ISystemCallHandler
{
    private const int DefaultPort = 21;
    private const string UsageText = "ftp [(-p|--port) <port>] <get|put> <pathA> [<pathB>]";
    private const string MissingConnectionMessage = "ftp requires an active ssh connection.";
    private const string InvalidContextMessage = "ftp local workstation context is invalid.";

    public string Command => "ftp";

    public SystemCallResult Execute(SystemCallExecutionContext context, IReadOnlyList<string> arguments)
    {
        if (!TryParseArguments(arguments, out var parsed, out var parseResult))
        {
            return parseResult!;
        }

        var terminalSessionId = context.World.NormalizeTerminalSessionId(context.TerminalSessionId);
        if (!context.World.TryResolveActiveRemoteSessionAccount(
                terminalSessionId,
                out var remoteSessionServer,
                out var topFrame,
                out _,
                out var remoteSessionUserKey))
        {
            return SystemCallResultFactory.Failure(SystemCallErrorCode.InvalidArgs, MissingConnectionMessage);
        }

        if (!context.World.TryGetTerminalConnectionOriginFrame(terminalSessionId, out var originFrame))
        {
            return SystemCallResultFactory.Failure(SystemCallErrorCode.InvalidArgs, MissingConnectionMessage);
        }

        if (!string.Equals(context.Server.NodeId, topFrame.SessionNodeId, StringComparison.Ordinal))
        {
            return SystemCallResultFactory.Failure(SystemCallErrorCode.InvalidArgs, MissingConnectionMessage);
        }

        if (!context.World.TryGetServer(originFrame.PreviousNodeId, out var localServer) ||
            !localServer.Users.TryGetValue(originFrame.PreviousUserKey, out var localUser))
        {
            return SystemCallResultFactory.Failure(SystemCallErrorCode.InternalError, InvalidContextMessage);
        }

        if (!remoteSessionServer.Users.TryGetValue(remoteSessionUserKey, out var remoteSessionUser))
        {
            return SystemCallResultFactory.Failure(SystemCallErrorCode.InternalError, MissingConnectionMessage);
        }

        if (!context.World.TryValidatePortAccess(context.Server, context.Server, parsed.Port, PortType.Ftp, out var portFailure))
        {
            return portFailure;
        }

        return parsed.Mode switch
        {
            FtpMode.Get => ExecuteGet(
                context,
                remoteSessionUser,
                localServer,
                localUser,
                originFrame.PreviousUserKey,
                originFrame.PreviousCwd,
                parsed),
            FtpMode.Put => ExecutePut(
                context,
                remoteSessionUser,
                localServer,
                localUser,
                originFrame.PreviousCwd,
                parsed),
            _ => SystemCallResultFactory.Usage(UsageText),
        };
    }

    private static SystemCallResult ExecuteGet(
        SystemCallExecutionContext context,
        UserConfig remoteSessionUser,
        ServerNodeRuntime localServer,
        UserConfig localUser,
        string localUserKey,
        string localCwd,
        ParsedFtpArguments parsed)
    {
        if (!remoteSessionUser.Privilege.Read)
        {
            return SystemCallResultFactory.PermissionDenied("ftp get");
        }

        if (!localUser.Privilege.Write)
        {
            return SystemCallResultFactory.PermissionDenied("ftp get");
        }

        var remoteSourcePath = BaseFileSystem.NormalizePath(context.Cwd, parsed.PathA);
        if (!TryReadSourceFile(context.Server, remoteSourcePath, out var sourceEntry, out var sourceContent, out var sourceFailure))
        {
            return sourceFailure;
        }

        var sourceFileName = GetFileName(remoteSourcePath);
        if (string.IsNullOrWhiteSpace(sourceFileName))
        {
            return SystemCallResultFactory.Failure(SystemCallErrorCode.InvalidArgs, $"invalid source path: {parsed.PathA}");
        }

        if (!TryResolveDestinationPath(
                localServer,
                localCwd,
                parsed.PathB,
                sourceFileName,
                out var localDestinationPath,
                out var destinationFailure))
        {
            return destinationFailure!;
        }

        if (!TryWriteDestinationFile(localServer, localDestinationPath, sourceContent, sourceEntry, out var writeFailure))
        {
            return writeFailure!;
        }

        context.World.EmitFileAcquire(
            fromNodeId: context.Server.NodeId,
            userKey: localUserKey,
            fileName: sourceFileName,
            remotePath: remoteSourcePath,
            localPath: localDestinationPath,
            sizeBytes: ToOptionalInt(sourceEntry.Size),
            contentId: sourceEntry.ContentId,
            transferMethod: "ftp");

        return SystemCallResultFactory.Success(lines: new[] { $"ftp get: {remoteSourcePath} -> {localDestinationPath}" });
    }

    private static SystemCallResult ExecutePut(
        SystemCallExecutionContext context,
        UserConfig remoteSessionUser,
        ServerNodeRuntime localServer,
        UserConfig localUser,
        string localCwd,
        ParsedFtpArguments parsed)
    {
        if (!remoteSessionUser.Privilege.Write)
        {
            return SystemCallResultFactory.PermissionDenied("ftp put");
        }

        if (!localUser.Privilege.Read)
        {
            return SystemCallResultFactory.PermissionDenied("ftp put");
        }

        var localSourcePath = BaseFileSystem.NormalizePath(localCwd, parsed.PathA);
        if (!TryReadSourceFile(localServer, localSourcePath, out var sourceEntry, out var sourceContent, out var sourceFailure))
        {
            return sourceFailure;
        }

        var sourceFileName = GetFileName(localSourcePath);
        if (string.IsNullOrWhiteSpace(sourceFileName))
        {
            return SystemCallResultFactory.Failure(SystemCallErrorCode.InvalidArgs, $"invalid source path: {parsed.PathA}");
        }

        if (!TryResolveDestinationPath(
                context.Server,
                context.Cwd,
                parsed.PathB,
                sourceFileName,
                out var remoteDestinationPath,
                out var destinationFailure))
        {
            return destinationFailure!;
        }

        if (!TryWriteDestinationFile(context.Server, remoteDestinationPath, sourceContent, sourceEntry, out var writeFailure))
        {
            return writeFailure!;
        }

        return SystemCallResultFactory.Success(lines: new[] { $"ftp put: {localSourcePath} -> {remoteDestinationPath}" });
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

        if (!TryValidateDestinationParent(destinationServer, destinationPath, out failure))
        {
            return false;
        }

        return true;
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

    private static string GetParentPath(string normalizedPath)
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

    private static int? ToOptionalInt(long value)
    {
        return value is < int.MinValue or > int.MaxValue
            ? null
            : (int)value;
    }

    private static bool TryParseArguments(
        IReadOnlyList<string> arguments,
        out ParsedFtpArguments parsed,
        out SystemCallResult? failure)
    {
        parsed = default;
        failure = null;

        if (arguments.Count == 0)
        {
            failure = SystemCallResultFactory.Usage(UsageText);
            return false;
        }

        var index = 0;
        var port = DefaultPort;
        var first = arguments[0];
        if (string.Equals(first, "-p", StringComparison.Ordinal) ||
            string.Equals(first, "--port", StringComparison.Ordinal))
        {
            if (arguments.Count < 2)
            {
                failure = SystemCallResultFactory.Usage(UsageText);
                return false;
            }

            if (!TryParsePort(arguments[1], out port))
            {
                failure = SystemCallResultFactory.Failure(SystemCallErrorCode.InvalidArgs, $"invalid port: {arguments[1]}");
                return false;
            }

            index = 2;
        }
        else if (first.StartsWith("-", StringComparison.Ordinal))
        {
            failure = SystemCallResultFactory.Usage(UsageText);
            return false;
        }

        if (arguments.Count - index < 2 || arguments.Count - index > 3)
        {
            failure = SystemCallResultFactory.Usage(UsageText);
            return false;
        }

        if (!TryParseMode(arguments[index], out var mode))
        {
            failure = SystemCallResultFactory.Usage(UsageText);
            return false;
        }

        var pathA = arguments[index + 1]?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(pathA))
        {
            failure = SystemCallResultFactory.Failure(SystemCallErrorCode.InvalidArgs, "path is required.");
            return false;
        }

        var pathB = arguments.Count - index == 3
            ? arguments[index + 2]
            : null;
        if (pathB is not null && string.IsNullOrWhiteSpace(pathB))
        {
            failure = SystemCallResultFactory.Failure(SystemCallErrorCode.InvalidArgs, "path is required.");
            return false;
        }

        parsed = new ParsedFtpArguments(mode, pathA, pathB, port);
        return true;
    }

    private static bool TryParseMode(string token, out FtpMode mode)
    {
        if (string.Equals(token, "get", StringComparison.Ordinal))
        {
            mode = FtpMode.Get;
            return true;
        }

        if (string.Equals(token, "put", StringComparison.Ordinal))
        {
            mode = FtpMode.Put;
            return true;
        }

        mode = default;
        return false;
    }

    private static bool TryParsePort(string value, out int port)
    {
        if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out port))
        {
            return false;
        }

        return port is >= 1 and <= 65535;
    }

    private enum FtpMode
    {
        Get,
        Put,
    }

    private readonly record struct ParsedFtpArguments(FtpMode Mode, string PathA, string? PathB, int Port);
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
    private const string NetIdNotFoundMessagePrefix = "scan: interface not found: ";
    private const string InvalidNetIdMessage = "scan: netId must not be empty.";
    private const string NoNeighborMessage =
        "No adjacent servers found (this server is not connected to a subnet or is the player workstation).";

    public string Command => "scan";

    public SystemCallResult Execute(SystemCallExecutionContext context, IReadOnlyList<string> arguments)
    {
        if (arguments.Count > 1)
        {
            return SystemCallResultFactory.Usage("scan [netId]");
        }

        var requestedNetId = string.Empty;
        if (arguments.Count == 1)
        {
            requestedNetId = arguments[0].Trim();
            if (string.IsNullOrWhiteSpace(requestedNetId))
            {
                return SystemCallResultFactory.Failure(SystemCallErrorCode.InvalidArgs, InvalidNetIdMessage);
            }
        }

        if (!context.User.Privilege.Execute)
        {
            return SystemCallResultFactory.Failure(SystemCallErrorCode.PermissionDenied, PermissionDeniedMessage);
        }

        if (IsPlayerWorkstation(context))
        {
            return SystemCallResultFactory.Success(lines: new[] { NoNeighborMessage });
        }

        var scannedInterfaces = CollectScannableInterfaces(context.Server, requestedNetId);
        if (scannedInterfaces.Count == 0)
        {
            if (!string.IsNullOrWhiteSpace(requestedNetId))
            {
                return SystemCallResultFactory.Failure(
                    SystemCallErrorCode.NotFound,
                    NetIdNotFoundMessagePrefix + requestedNetId);
            }

            return SystemCallResultFactory.Success(lines: new[] { NoNeighborMessage });
        }

        var lines = new List<string>(scannedInterfaces.Count * 2);
        foreach (var scannedInterface in scannedInterfaces)
        {
            var neighborRows = CollectNeighborDisplayRows(context, scannedInterface.NetId);
            lines.Add($"[interface {scannedInterface.NetId} {scannedInterface.LocalIp}] -");
            if (neighborRows.Count == 0)
            {
                lines.Add("    (none)");
                continue;
            }

            var neighborsLine = new List<string>(neighborRows.Count);
            foreach (var neighbor in neighborRows)
            {
                neighborsLine.Add(neighbor.Display);
            }

            lines.Add("    " + string.Join(", ", neighborsLine));
        }

        return SystemCallResultFactory.Success(lines: lines);
    }

    private static bool IsPlayerWorkstation(SystemCallExecutionContext context)
    {
        var workstation = context.World.PlayerWorkstationServer;
        return workstation is not null &&
               string.Equals(workstation.NodeId, context.Server.NodeId, StringComparison.Ordinal);
    }

    private static List<ScannableInterface> CollectScannableInterfaces(ServerNodeRuntime server, string requestedNetId)
    {
        var interfaces = new List<ScannableInterface>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var iface in server.Interfaces)
        {
            if (string.IsNullOrWhiteSpace(iface.NetId) ||
                string.IsNullOrWhiteSpace(iface.Ip) ||
                string.Equals(iface.NetId, InternetNetId, StringComparison.Ordinal))
            {
                continue;
            }

            if (!string.IsNullOrWhiteSpace(requestedNetId) &&
                !string.Equals(iface.NetId, requestedNetId, StringComparison.Ordinal))
            {
                continue;
            }

            var dedupeKey = iface.NetId + "\n" + iface.Ip;
            if (!seen.Add(dedupeKey))
            {
                continue;
            }

            interfaces.Add(new ScannableInterface(iface.NetId, iface.Ip));
        }

        interfaces.Sort(static (left, right) =>
        {
            var byNetId = StringComparer.Ordinal.Compare(left.NetId, right.NetId);
            if (byNetId != 0)
            {
                return byNetId;
            }

            return StringComparer.Ordinal.Compare(left.LocalIp, right.LocalIp);
        });
        return interfaces;
    }

    private static List<ScanNeighborRow> CollectNeighborDisplayRows(SystemCallExecutionContext context, string netId)
    {
        var neighborRows = new List<ScanNeighborRow>();
        var seenNeighborIps = new HashSet<string>(StringComparer.Ordinal);
        foreach (var neighborNodeId in context.Server.LanNeighbors)
        {
            if (!context.World.TryGetServer(neighborNodeId, out var neighborServer))
            {
                continue;
            }

            if (!TryGetInterfaceIp(neighborServer, netId, out var neighborIp) ||
                string.IsNullOrWhiteSpace(neighborIp) ||
                !seenNeighborIps.Add(neighborIp))
            {
                continue;
            }

            var display = ResolveServerDisplayName(neighborServer, neighborIp);
            neighborRows.Add(new ScanNeighborRow(display, neighborIp));
        }

        neighborRows.Sort(static (left, right) =>
        {
            var byDisplay = StringComparer.Ordinal.Compare(left.Display, right.Display);
            if (byDisplay != 0)
            {
                return byDisplay;
            }

            return StringComparer.Ordinal.Compare(left.Ip, right.Ip);
        });
        return neighborRows;
    }

    private static string ResolveServerDisplayName(ServerNodeRuntime server, string fallbackIp)
    {
        var hostname = server.Name?.Trim() ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(hostname))
        {
            return hostname;
        }

        if (!string.IsNullOrWhiteSpace(fallbackIp))
        {
            return fallbackIp;
        }

        if (!string.IsNullOrWhiteSpace(server.PrimaryIp))
        {
            return server.PrimaryIp;
        }

        return server.NodeId;
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

    private readonly record struct ScannableInterface(string NetId, string LocalIp);
    private readonly record struct ScanNeighborRow(string Display, string Ip);
}
