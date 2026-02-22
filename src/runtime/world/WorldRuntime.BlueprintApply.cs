using Godot;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Uplink2.Blueprint;
using Uplink2.Vfs;

namespace Uplink2.Runtime;

public partial class WorldRuntime
{
    /// <summary>Applies initial status/reason state resolved from spec and spawn overrides.</summary>
    private static void ApplyInitialServerState(ServerNodeRuntime server, ServerSpecBlueprint spec, ServerSpawnBlueprint spawn)
    {
        var status = spawn.HasInitialStatusOverride ? spawn.InitialStatusOverride : spec.InitialStatus;
        var reason = spawn.HasInitialReasonOverride ? spawn.InitialReasonOverride : spec.InitialReason;

        if (status == BlueprintServerStatus.Online)
        {
            if (reason != BlueprintServerReason.Ok)
            {
                throw new InvalidDataException(
                    $"Server '{spawn.NodeId}' is online but reason is '{reason}'. Online servers must use reason 'Ok'.");
            }

            server.SetOnline();
            return;
        }

        var offlineReason = ConvertReason(reason);
        if (offlineReason == ServerReason.Ok)
        {
            throw new InvalidDataException(
                $"Server '{spawn.NodeId}' is offline but reason is 'Ok'. Offline servers require a non-OK reason.");
        }

        server.SetOffline(offlineReason);
    }

    /// <summary>Copies users from spec and resolves AUTO id/password policies.</summary>
    private static void ApplyUsers(
        ServerNodeRuntime server,
        IReadOnlyDictionary<string, UserBlueprint> users,
        string nodeId,
        int worldSeed)
    {
        var userIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var userPair in users)
        {
            var userKey = userPair.Key;
            var blueprintUser = userPair.Value;
            var resolvedUserId = ResolveUserId(blueprintUser.UserId, worldSeed, nodeId, userKey);
            var resolvedPassword = ResolvePassword(blueprintUser.Passwd, worldSeed, nodeId, userKey);

            if (string.IsNullOrWhiteSpace(resolvedUserId))
            {
                resolvedUserId = userKey;
            }

            if (!userIds.Add(resolvedUserId))
            {
                throw new InvalidDataException(
                    $"Server '{nodeId}' declares duplicate userId '{resolvedUserId}'. userId must be unique per server.");
            }

            server.Users[userKey] = new UserConfig
            {
                UserId = resolvedUserId,
                UserPasswd = resolvedPassword,
                AuthMode = ConvertAuthMode(blueprintUser.AuthMode),
                Privilege = new PrivilegeConfig
                {
                    Read = blueprintUser.Privilege.Read,
                    Write = blueprintUser.Privilege.Write,
                    Execute = blueprintUser.Privilege.Execute,
                },
            };
        }
    }

    /// <summary>Copies ports from spec and applies spawn-time overlay overrides.</summary>
    private static void ApplyPorts(
        ServerNodeRuntime server,
        IReadOnlyDictionary<int, PortBlueprint> basePorts,
        IReadOnlyDictionary<int, PortOverrideBlueprint> portOverrides)
    {
        SeedDefaultPorts(server);

        foreach (var portPair in basePorts)
        {
            server.Ports[portPair.Key] = ConvertPortConfig(portPair.Value);
        }

        foreach (var overridePair in portOverrides)
        {
            if (overridePair.Value.Remove)
            {
                server.Ports.Remove(overridePair.Key);
                continue;
            }

            server.Ports[overridePair.Key] = ConvertPortConfig(overridePair.Value.Port);
        }
    }

    /// <summary>Seeds every server with default SSH/FTP port entries before blueprint overlays are applied.</summary>
    private static void SeedDefaultPorts(ServerNodeRuntime server)
    {
        server.Ports[22] = new PortConfig
        {
            PortType = PortType.Ssh,
            Exposure = PortExposure.Public,
        };

        server.Ports[21] = new PortConfig
        {
            PortType = PortType.Ftp,
            Exposure = PortExposure.Public,
        };
    }

    /// <summary>Copies daemon configurations and applies spawn-time overlay overrides.</summary>
    private static void ApplyDaemons(
        ServerNodeRuntime server,
        IReadOnlyDictionary<BlueprintDaemonType, DaemonBlueprint> baseDaemons,
        IReadOnlyDictionary<BlueprintDaemonType, DaemonOverrideBlueprint> daemonOverrides)
    {
        foreach (var daemonPair in baseDaemons)
        {
            var daemonType = ConvertDaemonType(daemonPair.Key);
            server.Daemons[daemonType] = ConvertDaemonStruct(daemonPair.Key, daemonPair.Value);
        }

        foreach (var overridePair in daemonOverrides)
        {
            var daemonType = ConvertDaemonType(overridePair.Key);
            if (overridePair.Value.Remove)
            {
                server.Daemons.Remove(daemonType);
                continue;
            }

            server.Daemons[daemonType] = ConvertDaemonStruct(overridePair.Key, overridePair.Value.Daemon);
        }
    }

    /// <summary>Validates OTP user/daemon linkage rule defined by blueprint/runtime schemas.</summary>
    private static void ValidateOtpConsistency(ServerNodeRuntime server)
    {
        var otpUsersExist = server.Users.Values.Any(static user => user.AuthMode == AuthMode.Otp);
        if (!otpUsersExist)
        {
            return;
        }

        if (!server.Daemons.TryGetValue(DaemonType.Otp, out var otpDaemon))
        {
            throw new InvalidDataException(
                $"Server '{server.NodeId}' has OTP-auth users but is missing OTP daemon configuration.");
        }

        if (!otpDaemon.DaemonArgs.TryGetValue("userKey", out var rawUserKey) ||
            rawUserKey is not string otpUserKey ||
            string.IsNullOrWhiteSpace(otpUserKey) ||
            !server.Users.ContainsKey(otpUserKey))
        {
            throw new InvalidDataException(
                $"Server '{server.NodeId}' OTP daemon must declare an existing users[userKey] value.");
        }
    }

    /// <summary>Applies merged disk overlay entries and tombstones to runtime overlay FS.</summary>
    private void ApplyDiskOverlay(
        ServerNodeRuntime server,
        DiskOverlayBlueprint specDiskOverlay,
        DiskOverlayOverrideBlueprint spawnDiskOverlayOverrides)
    {
        var mergedEntries = new Dictionary<string, BlueprintEntryMeta>(specDiskOverlay.OverlayEntries, StringComparer.Ordinal);
        foreach (var overridePair in spawnDiskOverlayOverrides.OverlayEntries)
        {
            if (overridePair.Value.Remove)
            {
                mergedEntries.Remove(overridePair.Key);
            }
            else
            {
                mergedEntries[overridePair.Key] = overridePair.Value.Entry;
            }
        }

        var mergedTombstones = new HashSet<string>(specDiskOverlay.Tombstones, StringComparer.Ordinal);
        mergedTombstones.UnionWith(spawnDiskOverlayOverrides.Tombstones);

        foreach (var entryPair in mergedEntries.OrderBy(
                     static pair => pair.Key.Count(static c => c == '/'),
                     Comparer<int>.Default)
                 .ThenBy(static pair => pair.Key, StringComparer.Ordinal))
        {
            ApplyOverlayEntry(server.DiskOverlay, entryPair.Key, entryPair.Value);
        }

        foreach (var tombstonePath in mergedTombstones.OrderBy(static path => path, StringComparer.Ordinal))
        {
            server.DiskOverlay.AddTombstone(tombstonePath);
        }
    }

    /// <summary>Writes one blueprint overlay entry into runtime overlay FS.</summary>
    private void ApplyOverlayEntry(OverlayFileSystem overlay, string path, BlueprintEntryMeta entry)
    {
        var normalizedPath = BaseFileSystem.NormalizePath("/", path);
        if (normalizedPath == "/")
        {
            throw new InvalidDataException("Disk overlay entry path cannot target root ('/').");
        }

        EnsureOverlayParentDirectories(overlay, normalizedPath);

        if (entry.EntryKind == BlueprintEntryKind.Dir)
        {
            overlay.AddDirectory(normalizedPath);
            return;
        }

        var content = ResolveBlueprintContent(entry.ContentId, entry.FileKind);
        entry.RealSize = Encoding.UTF8.GetByteCount(content);
        overlay.WriteFile(normalizedPath, content, fileKind: ConvertFileKind(entry.FileKind), size: entry.Size);
    }

    /// <summary>Ensures parent directories exist before writing overlay entries.</summary>
    private static void EnsureOverlayParentDirectories(OverlayFileSystem overlay, string normalizedPath)
    {
        var parentPath = GetParentPath(normalizedPath);
        if (parentPath == "/")
        {
            return;
        }

        var current = "/";
        var segments = parentPath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        foreach (var segment in segments)
        {
            var next = current == "/" ? "/" + segment : current + "/" + segment;
            if (!overlay.TryResolveEntry(next, out var entry))
            {
                overlay.AddDirectory(next);
            }
            else if (entry.EntryKind != VfsEntryKind.Dir)
            {
                throw new InvalidDataException($"Overlay parent path '{next}' exists but is not a directory.");
            }

            current = next;
        }
    }

    /// <summary>Resolves blueprint file content string from literal text or res:// path.</summary>
    private static string ResolveBlueprintContent(string contentId, BlueprintFileKind fileKind)
    {
        if (string.IsNullOrWhiteSpace(contentId))
        {
            return string.Empty;
        }

        if (!contentId.StartsWith("res://", StringComparison.Ordinal) &&
            !contentId.StartsWith("user://", StringComparison.Ordinal))
        {
            return contentId;
        }

        var absolutePath = ProjectSettings.GlobalizePath(contentId);
        if (!File.Exists(absolutePath))
        {
            throw new FileNotFoundException($"Blueprint content file not found: {contentId}", absolutePath);
        }

        if (fileKind == BlueprintFileKind.Text ||
            fileKind == BlueprintFileKind.ExecutableScript ||
            fileKind == BlueprintFileKind.ExecutableHardcode)
        {
            return File.ReadAllText(absolutePath, Encoding.UTF8);
        }

        var bytes = File.ReadAllBytes(absolutePath);
        return Convert.ToBase64String(bytes);
    }

    /// <summary>Adds a bidirectional edge into node-based neighbor map.</summary>
    private static void AddNeighborEdge(
        IReadOnlyDictionary<string, HashSet<string>> neighborsByNodeId,
        IReadOnlyCollection<string> subnetMembers,
        string subnetId,
        string nodeA,
        string nodeB,
        string sourceContext)
    {
        if (string.IsNullOrWhiteSpace(nodeA) ||
            string.IsNullOrWhiteSpace(nodeB) ||
            string.Equals(nodeA, nodeB, StringComparison.Ordinal))
        {
            return;
        }

        if (!subnetMembers.Contains(nodeA) || !subnetMembers.Contains(nodeB))
        {
            GD.PushWarning(
                $"Ignoring {sourceContext} edge '{nodeA}' <-> '{nodeB}' in subnet '{subnetId}' because at least one node does not belong to the subnet.");
            return;
        }

        if (!neighborsByNodeId.TryGetValue(nodeA, out var aNeighbors) ||
            !neighborsByNodeId.TryGetValue(nodeB, out var bNeighbors))
        {
            GD.PushWarning(
                $"Ignoring {sourceContext} edge '{nodeA}' <-> '{nodeB}' because at least one node does not exist in current scenario.");
            return;
        }

        aNeighbors.Add(nodeB);
        bNeighbors.Add(nodeA);
    }
}
