using Godot;
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Uplink2.Runtime.Persistence;
using Uplink2.Vfs;

#nullable enable

namespace Uplink2.Runtime;

public partial class WorldRuntime
{
    /// <summary>Saves current runtime world state to a binary container file.</summary>
    public SaveLoadResult SaveGameToFile(string savePath)
    {
        if (!TryResolveSavePath(savePath, out var resolvedSavePath, out var pathError))
        {
            return CreateFailureResult(SaveLoadErrorCode.InvalidArgs, pathError, savePath);
        }

        if (!TryResolveHmacKey(out var hmacKey, out var hmacError))
        {
            return CreateFailureResult(SaveLoadErrorCode.InvalidArgs, hmacError, resolvedSavePath);
        }

        if (!TryCaptureRuntimeSnapshot(out var snapshot, out var captureFailure))
        {
            return WithSavePath(captureFailure, resolvedSavePath);
        }

        var chunks = BuildChunkRecords(snapshot);
        var header = new SaveFileHeader
        {
            FormatMajor = SaveContainerConstants.FormatMajor,
            FormatMinor = SaveContainerConstants.FormatMinor,
            Flags = SaveContainerConstants.RequiredFlags,
            ChunkCount = (uint)chunks.Count,
        };

        byte[] fileBytes;
        try
        {
            fileBytes = SaveContainerCodec.BuildContainer(header, chunks, hmacKey);
        }
        catch (Exception ex)
        {
            return CreateFailureResult(
                SaveLoadErrorCode.StateApplyFailed,
                $"failed to encode save container: {ex.Message}",
                resolvedSavePath);
        }

        try
        {
            var parentDirectory = Path.GetDirectoryName(resolvedSavePath);
            if (!string.IsNullOrWhiteSpace(parentDirectory))
            {
                Directory.CreateDirectory(parentDirectory);
            }

            File.WriteAllBytes(resolvedSavePath, fileBytes);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return CreateFailureResult(SaveLoadErrorCode.IoError, ex.Message, resolvedSavePath);
        }

        return CreateSuccessResult("save completed.", resolvedSavePath);
    }

    /// <summary>Loads runtime world state from a binary container file.</summary>
    public SaveLoadResult LoadGameFromFile(string savePath)
    {
        if (!TryResolveSavePath(savePath, out var resolvedSavePath, out var pathError))
        {
            return CreateFailureResult(SaveLoadErrorCode.InvalidArgs, pathError, savePath);
        }

        if (!TryResolveHmacKey(out var hmacKey, out var hmacError))
        {
            return CreateFailureResult(SaveLoadErrorCode.InvalidArgs, hmacError, resolvedSavePath);
        }

        byte[] fileBytes;
        try
        {
            fileBytes = File.ReadAllBytes(resolvedSavePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return CreateFailureResult(SaveLoadErrorCode.IoError, ex.Message, resolvedSavePath);
        }

        if (!SaveContainerCodec.TryParseContainer(
                fileBytes,
                hmacKey,
                out var parsedContainer,
                out var parseErrorCode,
                out var parseErrorMessage))
        {
            return CreateFailureResult(parseErrorCode, parseErrorMessage, resolvedSavePath);
        }

        if (!TryValidateContainerVersion(parsedContainer.Header, out var versionFailure))
        {
            return WithSavePath(versionFailure, resolvedSavePath);
        }

        if (!TryBuildSnapshotFromContainer(parsedContainer, out var incomingSnapshot, out var snapshotFailure))
        {
            return WithSavePath(snapshotFailure, resolvedSavePath);
        }

        if (!TryCaptureRuntimeSnapshot(out var backupSnapshot, out var backupFailure))
        {
            return WithSavePath(backupFailure, resolvedSavePath);
        }

        SaveLoadResult? applyFailure = null;
        try
        {
            if (!TryRebuildWorldFromSaveMeta(incomingSnapshot.SaveMeta, out var rebuildError))
            {
                applyFailure = CreateFailureResult(
                    SaveLoadErrorCode.ScenarioRestoreFailed,
                    rebuildError,
                    resolvedSavePath);
            }
            else if (!TryApplyRuntimeSnapshot(incomingSnapshot, out var applyError))
            {
                applyFailure = CreateFailureResult(
                    SaveLoadErrorCode.StateApplyFailed,
                    applyError,
                    resolvedSavePath);
            }
        }
        catch (Exception ex)
        {
            applyFailure = CreateFailureResult(
                SaveLoadErrorCode.StateApplyFailed,
                $"unexpected load failure: {ex.Message}",
                resolvedSavePath);
        }

        if (applyFailure is null)
        {
            return CreateSuccessResult("load completed.", resolvedSavePath);
        }

        if (!TryRestoreFromSnapshot(backupSnapshot, out var restoreFailure))
        {
            GD.PushWarning($"Save/load restore fallback failed. {restoreFailure}");
            SafeReinitializeAfterFailedLoad();
        }

        return applyFailure;
    }

    private bool TryValidateContainerVersion(SaveFileHeader header, out SaveLoadResult failure)
    {
        if (header.FormatMajor != SaveContainerConstants.FormatMajor)
        {
            failure = CreateFailureResult(
                SaveLoadErrorCode.UnsupportedVersion,
                $"unsupported save format major version: {header.FormatMajor}.",
                string.Empty);
            return false;
        }

        failure = CreateSuccessResult(string.Empty, string.Empty);
        return true;
    }

    private static List<SaveChunkRecord> BuildChunkRecords(RuntimeSaveSnapshot snapshot)
    {
        var records = new List<SaveChunkRecord>
        {
            new()
            {
                ChunkId = SaveContainerConstants.ChunkIdSaveMeta,
                ChunkVersion = SaveContainerConstants.ChunkVersion1,
                PayloadBytes = SaveContainerCodec.SerializeChunkPayload(snapshot.SaveMeta),
            },
            new()
            {
                ChunkId = SaveContainerConstants.ChunkIdWorldState,
                ChunkVersion = SaveContainerConstants.ChunkVersion1,
                PayloadBytes = SaveContainerCodec.SerializeChunkPayload(snapshot.WorldState),
            },
            new()
            {
                ChunkId = SaveContainerConstants.ChunkIdEventState,
                ChunkVersion = SaveContainerConstants.ChunkVersion1,
                PayloadBytes = SaveContainerCodec.SerializeChunkPayload(snapshot.EventState),
            },
            new()
            {
                ChunkId = SaveContainerConstants.ChunkIdProcessState,
                ChunkVersion = SaveContainerConstants.ChunkVersion1,
                PayloadBytes = SaveContainerCodec.SerializeChunkPayload(snapshot.ProcessState),
            },
        };

        foreach (var serverState in snapshot.ServerStates.OrderBy(static value => value.NodeId, StringComparer.Ordinal))
        {
            records.Add(new SaveChunkRecord
            {
                ChunkId = SaveContainerConstants.ChunkIdServerState,
                ChunkVersion = SaveContainerConstants.ChunkVersion1,
                PayloadBytes = SaveContainerCodec.SerializeChunkPayload(serverState),
            });
        }

        return records;
    }

    private bool TryBuildSnapshotFromContainer(
        ParsedSaveContainer container,
        out RuntimeSaveSnapshot snapshot,
        out SaveLoadResult failure)
    {
        snapshot = new RuntimeSaveSnapshot();
        failure = CreateSuccessResult(string.Empty, string.Empty);

        SaveMetaChunkDto? saveMeta = null;
        WorldStateChunkDto? worldState = null;
        EventStateChunkDto? eventState = null;
        ProcessStateChunkDto? processState = null;
        var serverStates = new List<ServerStateChunkDto>();

        foreach (var chunk in container.Chunks)
        {
            if (chunk.ChunkVersion != SaveContainerConstants.ChunkVersion1)
            {
                var isRequiredChunk = chunk.ChunkId is SaveContainerConstants.ChunkIdSaveMeta
                    or SaveContainerConstants.ChunkIdWorldState
                    or SaveContainerConstants.ChunkIdEventState
                    or SaveContainerConstants.ChunkIdProcessState;
                if (isRequiredChunk)
                {
                    failure = CreateFailureResult(
                        SaveLoadErrorCode.UnsupportedVersion,
                        $"unsupported chunk version. chunkId=0x{chunk.ChunkId:X4}, version={chunk.ChunkVersion}.",
                        string.Empty);
                    return false;
                }

                GD.PushWarning($"Skipping optional chunk with unsupported version. chunkId=0x{chunk.ChunkId:X4}, version={chunk.ChunkVersion}.");
                continue;
            }

            try
            {
                switch (chunk.ChunkId)
                {
                    case SaveContainerConstants.ChunkIdSaveMeta:
                        if (saveMeta is not null)
                        {
                            failure = CreateFailureResult(SaveLoadErrorCode.FormatError, "duplicate SaveMeta chunk.", string.Empty);
                            return false;
                        }

                        saveMeta = SaveContainerCodec.DeserializeChunkPayload<SaveMetaChunkDto>(chunk.PayloadBytes);
                        break;
                    case SaveContainerConstants.ChunkIdWorldState:
                        if (worldState is not null)
                        {
                            failure = CreateFailureResult(SaveLoadErrorCode.FormatError, "duplicate WorldState chunk.", string.Empty);
                            return false;
                        }

                        worldState = SaveContainerCodec.DeserializeChunkPayload<WorldStateChunkDto>(chunk.PayloadBytes);
                        break;
                    case SaveContainerConstants.ChunkIdEventState:
                        if (eventState is not null)
                        {
                            failure = CreateFailureResult(SaveLoadErrorCode.FormatError, "duplicate EventState chunk.", string.Empty);
                            return false;
                        }

                        eventState = SaveContainerCodec.DeserializeChunkPayload<EventStateChunkDto>(chunk.PayloadBytes);
                        break;
                    case SaveContainerConstants.ChunkIdProcessState:
                        if (processState is not null)
                        {
                            failure = CreateFailureResult(SaveLoadErrorCode.FormatError, "duplicate ProcessState chunk.", string.Empty);
                            return false;
                        }

                        processState = SaveContainerCodec.DeserializeChunkPayload<ProcessStateChunkDto>(chunk.PayloadBytes);
                        break;
                    case SaveContainerConstants.ChunkIdServerState:
                        serverStates.Add(SaveContainerCodec.DeserializeChunkPayload<ServerStateChunkDto>(chunk.PayloadBytes));
                        break;
                    default:
                        GD.PushWarning($"Skipping unknown save chunk id: 0x{chunk.ChunkId:X4}.");
                        break;
                }
            }
            catch (Exception ex)
            {
                failure = CreateFailureResult(
                    SaveLoadErrorCode.FormatError,
                    $"failed to deserialize chunk 0x{chunk.ChunkId:X4}: {ex.Message}",
                    string.Empty);
                return false;
            }
        }

        if (saveMeta is null)
        {
            failure = CreateFailureResult(SaveLoadErrorCode.MissingRequiredChunk, "missing SaveMeta chunk.", string.Empty);
            return false;
        }

        if (worldState is null)
        {
            failure = CreateFailureResult(SaveLoadErrorCode.MissingRequiredChunk, "missing WorldState chunk.", string.Empty);
            return false;
        }

        if (eventState is null)
        {
            failure = CreateFailureResult(SaveLoadErrorCode.MissingRequiredChunk, "missing EventState chunk.", string.Empty);
            return false;
        }

        if (processState is null)
        {
            failure = CreateFailureResult(SaveLoadErrorCode.MissingRequiredChunk, "missing ProcessState chunk.", string.Empty);
            return false;
        }

        if (serverStates.Count == 0)
        {
            failure = CreateFailureResult(SaveLoadErrorCode.MissingRequiredChunk, "missing ServerState chunk.", string.Empty);
            return false;
        }

        snapshot = new RuntimeSaveSnapshot
        {
            SaveMeta = saveMeta,
            WorldState = worldState,
            EventState = eventState,
            ProcessState = processState,
            ServerStates = serverStates,
        };
        return true;
    }

    private bool TryCaptureRuntimeSnapshot(out RuntimeSaveSnapshot snapshot, out SaveLoadResult failure)
    {
        snapshot = new RuntimeSaveSnapshot();
        failure = CreateSuccessResult(string.Empty, string.Empty);

        var (worldTickIndexSnapshot, eventSeqSnapshot) = CaptureEventRuntimeClockForSave();
        if (!TryConvertObjectMapToSaveValues(ScenarioFlags, out var scenarioFlags, out var scenarioFlagsError))
        {
            failure = CreateFailureResult(
                SaveLoadErrorCode.UnsupportedValueType,
                $"failed to snapshot scenarioFlags: {scenarioFlagsError}",
                string.Empty);
            return false;
        }

        if (!TryCaptureProcessState(out var processState, out failure))
        {
            return false;
        }

        if (!TryCaptureServerStates(out var serverStates, out failure))
        {
            return false;
        }

        snapshot = new RuntimeSaveSnapshot
        {
            SaveMeta = new SaveMetaChunkDto
            {
                SaveSchemaVersion = SaveContainerConstants.SaveSchemaVersion,
                ActiveScenarioId = ActiveScenarioId ?? string.Empty,
                WorldSeed = worldSeed,
                SavedAtUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            },
            WorldState = new WorldStateChunkDto
            {
                WorldTickIndex = worldTickIndexSnapshot,
                EventSeq = eventSeqSnapshot,
                NextProcessId = nextProcessId,
                VisibleNets = VisibleNets.OrderBy(static value => value, StringComparer.Ordinal).ToList(),
                KnownNodesByNet = KnownNodesByNet
                    .OrderBy(static pair => pair.Key, StringComparer.Ordinal)
                    .ToDictionary(
                        static pair => pair.Key,
                        static pair => pair.Value.OrderBy(static value => value, StringComparer.Ordinal).ToList(),
                        StringComparer.Ordinal),
                ScenarioFlags = scenarioFlags,
            },
            EventState = new EventStateChunkDto
            {
                FiredHandlerIds = CaptureFiredHandlerIdsForSave().ToList(),
            },
            ProcessState = processState,
            ServerStates = serverStates,
        };

        return true;
    }

    private bool TryCaptureProcessState(out ProcessStateChunkDto processState, out SaveLoadResult failure)
    {
        processState = new ProcessStateChunkDto();
        failure = CreateSuccessResult(string.Empty, string.Empty);

        foreach (var processPair in ProcessList.OrderBy(static pair => pair.Key))
        {
            var process = processPair.Value;
            if (!TryConvertObjectMapToSaveValues(process.ProcessArgs, out var processArgs, out var processArgsError))
            {
                failure = CreateFailureResult(
                    SaveLoadErrorCode.UnsupportedValueType,
                    $"failed to snapshot processArgs for processId {processPair.Key}: {processArgsError}",
                    string.Empty);
                return false;
            }

            processState.Processes.Add(new ProcessSnapshotDto
            {
                ProcessId = processPair.Key,
                Name = process.Name ?? string.Empty,
                HostNodeId = process.HostNodeId ?? string.Empty,
                UserKey = process.UserKey ?? string.Empty,
                State = (int)process.State,
                Path = process.Path ?? string.Empty,
                ProcessType = (int)process.ProcessType,
                ProcessArgs = processArgs,
                EndAt = process.EndAt,
            });
        }

        return true;
    }

    private bool TryCaptureServerStates(out List<ServerStateChunkDto> serverStates, out SaveLoadResult failure)
    {
        serverStates = [];
        failure = CreateSuccessResult(string.Empty, string.Empty);

        foreach (var server in ServerList.Values.OrderBy(static value => value.NodeId, StringComparer.Ordinal))
        {
            if (!TryCaptureServerState(server, out var serverState, out failure))
            {
                return false;
            }

            serverStates.Add(serverState);
        }

        return true;
    }

    private bool TryCaptureServerState(
        ServerNodeRuntime server,
        out ServerStateChunkDto serverState,
        out SaveLoadResult failure)
    {
        serverState = new ServerStateChunkDto();
        failure = CreateSuccessResult(string.Empty, string.Empty);

        var users = new Dictionary<string, UserSnapshotDto>(StringComparer.Ordinal);
        foreach (var userPair in server.Users.OrderBy(static pair => pair.Key, StringComparer.Ordinal))
        {
            users[userPair.Key] = new UserSnapshotDto
            {
                UserId = userPair.Value.UserId ?? string.Empty,
                UserPasswd = userPair.Value.UserPasswd ?? string.Empty,
                AuthMode = (int)userPair.Value.AuthMode,
                Privilege = new PrivilegeSnapshotDto
                {
                    Read = userPair.Value.Privilege.Read,
                    Write = userPair.Value.Privilege.Write,
                    Execute = userPair.Value.Privilege.Execute,
                },
                Info = userPair.Value.Info.Where(static value => !string.IsNullOrWhiteSpace(value)).ToList(),
            };
        }

        var entries = new List<OverlayEntrySnapshotDto>();
        foreach (var entryPair in server.DiskOverlay.OverlayEntries.OrderBy(static pair => pair.Key, StringComparer.Ordinal))
        {
            var entry = entryPair.Value;
            var entrySnapshot = new OverlayEntrySnapshotDto
            {
                Path = entryPair.Key,
                EntryKind = (int)entry.EntryKind,
                Size = entry.Size,
                FileKind = entry.FileKind is null ? null : (int)entry.FileKind.Value,
            };

            if (entry.EntryKind == VfsEntryKind.File)
            {
                if (!server.DiskOverlay.TryReadFileText(entryPair.Key, out var content))
                {
                    failure = CreateFailureResult(
                        SaveLoadErrorCode.StateApplyFailed,
                        $"failed to read overlay file content: {entryPair.Key}",
                        string.Empty);
                    return false;
                }

                entrySnapshot.Content = content ?? string.Empty;
            }

            entries.Add(entrySnapshot);
        }

        var logs = new List<LogSnapshotDto>();
        foreach (var log in server.Logs)
        {
            logs.Add(CaptureLogSnapshot(log, new HashSet<LogStruct>()));
        }

        var ports = new Dictionary<int, PortSnapshotDto>();
        foreach (var portPair in server.Ports.OrderBy(static pair => pair.Key))
        {
            ports[portPair.Key] = new PortSnapshotDto
            {
                PortType = (int)portPair.Value.PortType,
                ServiceId = portPair.Value.ServiceId ?? string.Empty,
                Exposure = (int)portPair.Value.Exposure,
            };
        }

        var daemons = new Dictionary<int, DaemonSnapshotDto>();
        foreach (var daemonPair in server.Daemons.OrderBy(static pair => (int)pair.Key))
        {
            if (!TryConvertObjectMapToSaveValues(daemonPair.Value.DaemonArgs, out var daemonArgs, out var daemonArgError))
            {
                failure = CreateFailureResult(
                    SaveLoadErrorCode.UnsupportedValueType,
                    $"failed to snapshot daemonArgs for server '{server.NodeId}', daemon '{daemonPair.Key}': {daemonArgError}",
                    string.Empty);
                return false;
            }

            daemons[(int)daemonPair.Key] = new DaemonSnapshotDto
            {
                DaemonArgs = daemonArgs,
            };
        }

        serverState = new ServerStateChunkDto
        {
            NodeId = server.NodeId,
            Status = (int)server.Status,
            Reason = (int)server.Reason,
            Users = users,
            DiskOverlay = new DiskOverlaySnapshotDto
            {
                Entries = entries,
                Tombstones = server.DiskOverlay.Tombstones
                    .Where(static value => !string.IsNullOrWhiteSpace(value))
                    .OrderBy(static value => value, StringComparer.Ordinal)
                    .ToList(),
            },
            Logs = logs,
            LogCapacity = server.LogCapacity,
            Ports = ports,
            Daemons = daemons,
        };
        return true;
    }

    private static LogSnapshotDto CaptureLogSnapshot(LogStruct log, HashSet<LogStruct> visited)
    {
        var snapshot = new LogSnapshotDto
        {
            Id = log.Id,
            Time = log.Time,
            User = log.User ?? string.Empty,
            RemoteIp = log.RemoteIp ?? "127.0.0.1",
            ActionType = (int)log.ActionType,
            Action = log.Action ?? string.Empty,
            Dirty = log.Dirty,
        };

        if (log.Origin is null)
        {
            return snapshot;
        }

        if (!visited.Add(log.Origin))
        {
            return snapshot;
        }

        snapshot.Origin = CaptureLogSnapshot(log.Origin, visited);
        visited.Remove(log.Origin);
        return snapshot;
    }

    private bool TryApplyRuntimeSnapshot(RuntimeSaveSnapshot snapshot, out string errorMessage)
    {
        errorMessage = string.Empty;
        if (!TryApplyWorldState(snapshot.WorldState, out errorMessage))
        {
            return false;
        }

        if (!TryApplyEventState(snapshot.WorldState, snapshot.EventState, out errorMessage))
        {
            return false;
        }

        if (!TryApplyProcessState(snapshot.ProcessState, snapshot.WorldState.NextProcessId, out errorMessage))
        {
            return false;
        }

        if (!TryApplyServerStates(snapshot.ServerStates, out errorMessage))
        {
            return false;
        }

        RebuildProcessSchedulerForLoad();
        EnsureSessionStateClearedForLoad();
        if (!TryResolveMyWorkstationServer(out var myWorkstationServer))
        {
            errorMessage = "myWorkstation server was not found after load.";
            return false;
        }

        PlayerWorkstationServer = myWorkstationServer;
        return true;
    }

    private bool TryApplyWorldState(WorldStateChunkDto worldState, out string errorMessage)
    {
        errorMessage = string.Empty;

        if (worldState.NextProcessId < 1)
        {
            errorMessage = $"nextProcessId must be positive. value={worldState.NextProcessId}";
            return false;
        }

        if (!TryConvertSaveValuesToObjectMap(worldState.ScenarioFlags, out var scenarioFlags, out errorMessage))
        {
            errorMessage = $"failed to decode scenarioFlags: {errorMessage}";
            return false;
        }

        nextProcessId = worldState.NextProcessId;

        VisibleNets.Clear();
        foreach (var netId in worldState.VisibleNets.Where(static value => !string.IsNullOrWhiteSpace(value)))
        {
            VisibleNets.Add(netId.Trim());
        }

        KnownNodesByNet.Clear();
        foreach (var netPair in worldState.KnownNodesByNet)
        {
            if (string.IsNullOrWhiteSpace(netPair.Key))
            {
                continue;
            }

            var nodeSet = new HashSet<string>(StringComparer.Ordinal);
            foreach (var nodeId in netPair.Value.Where(static value => !string.IsNullOrWhiteSpace(value)))
            {
                nodeSet.Add(nodeId.Trim());
            }

            KnownNodesByNet[netPair.Key] = nodeSet;
        }

        ScenarioFlags.Clear();
        foreach (var flagPair in scenarioFlags)
        {
            ScenarioFlags[flagPair.Key] = flagPair.Value;
        }

        RefreshServerExposureFromKnownNodes();
        return true;
    }

    private bool TryApplyEventState(
        WorldStateChunkDto worldState,
        EventStateChunkDto eventState,
        out string errorMessage)
    {
        errorMessage = string.Empty;
        ApplyEventRuntimeStateForLoad(
            worldState.WorldTickIndex,
            worldState.EventSeq,
            eventState.FiredHandlerIds.Where(static value => !string.IsNullOrWhiteSpace(value)));
        return true;
    }

    private bool TryApplyProcessState(
        ProcessStateChunkDto processState,
        int loadedNextProcessId,
        out string errorMessage)
    {
        errorMessage = string.Empty;
        ProcessList.Clear();
        foreach (var server in ServerList.Values)
        {
            server.ClearProcesses();
        }

        foreach (var processSnapshot in processState.Processes)
        {
            if (processSnapshot.ProcessId < 1)
            {
                errorMessage = $"processId must be positive. value={processSnapshot.ProcessId}.";
                return false;
            }

            if (ProcessList.ContainsKey(processSnapshot.ProcessId))
            {
                errorMessage = $"duplicate processId in save: {processSnapshot.ProcessId}.";
                return false;
            }

            if (!TryResolveEnum(processSnapshot.State, out ProcessState state))
            {
                errorMessage = $"invalid process state value: {processSnapshot.State}.";
                return false;
            }

            if (!TryResolveEnum(processSnapshot.ProcessType, out ProcessType processType))
            {
                errorMessage = $"invalid process type value: {processSnapshot.ProcessType}.";
                return false;
            }

            if (!TryConvertSaveValuesToObjectMap(processSnapshot.ProcessArgs, out var processArgs, out errorMessage))
            {
                errorMessage = $"failed to decode processArgs for processId {processSnapshot.ProcessId}: {errorMessage}";
                return false;
            }

            var process = new ProcessStruct
            {
                Name = processSnapshot.Name ?? string.Empty,
                HostNodeId = processSnapshot.HostNodeId ?? string.Empty,
                UserKey = processSnapshot.UserKey ?? string.Empty,
                State = state,
                Path = processSnapshot.Path ?? string.Empty,
                ProcessType = processType,
                EndAt = processSnapshot.EndAt,
            };

            process.ProcessArgs.Clear();
            foreach (var argPair in processArgs)
            {
                process.ProcessArgs[argPair.Key] = argPair.Value;
            }

            ProcessList[processSnapshot.ProcessId] = process;
        }

        foreach (var processPair in ProcessList)
        {
            if (processPair.Value.State != ProcessState.Running)
            {
                continue;
            }

            if (!ServerList.TryGetValue(processPair.Value.HostNodeId, out var hostServer))
            {
                errorMessage = $"processId {processPair.Key} references missing host node '{processPair.Value.HostNodeId}'.";
                return false;
            }

            hostServer.AddProcess(processPair.Key);
        }

        nextProcessId = Math.Max(loadedNextProcessId, ProcessList.Keys.DefaultIfEmpty(0).Max() + 1);
        return true;
    }

    private bool TryApplyServerStates(IReadOnlyList<ServerStateChunkDto> serverStates, out string errorMessage)
    {
        errorMessage = string.Empty;
        var serverStatesByNodeId = new Dictionary<string, ServerStateChunkDto>(StringComparer.Ordinal);
        foreach (var serverState in serverStates)
        {
            if (string.IsNullOrWhiteSpace(serverState.NodeId))
            {
                errorMessage = "serverState.nodeId cannot be empty.";
                return false;
            }

            if (!serverStatesByNodeId.TryAdd(serverState.NodeId, serverState))
            {
                errorMessage = $"duplicate ServerState chunk for nodeId '{serverState.NodeId}'.";
                return false;
            }
        }

        foreach (var server in ServerList.Values)
        {
            if (!serverStatesByNodeId.ContainsKey(server.NodeId))
            {
                errorMessage = $"server state chunk not found for nodeId '{server.NodeId}'.";
                return false;
            }
        }

        foreach (var serverStatePair in serverStatesByNodeId.OrderBy(static pair => pair.Key, StringComparer.Ordinal))
        {
            if (!ServerList.TryGetValue(serverStatePair.Key, out var server))
            {
                continue;
            }

            if (!TryApplyServerState(server, serverStatePair.Value, out errorMessage))
            {
                return false;
            }
        }

        return true;
    }

    private bool TryApplyServerState(ServerNodeRuntime server, ServerStateChunkDto serverState, out string errorMessage)
    {
        errorMessage = string.Empty;
        if (!TryResolveEnum(serverState.Status, out ServerStatus status))
        {
            errorMessage = $"invalid server status value: {serverState.Status} (nodeId={server.NodeId}).";
            return false;
        }

        if (!TryResolveEnum(serverState.Reason, out ServerReason reason))
        {
            errorMessage = $"invalid server reason value: {serverState.Reason} (nodeId={server.NodeId}).";
            return false;
        }

        if (status == ServerStatus.Online)
        {
            server.SetOnline();
        }
        else
        {
            if (reason == ServerReason.Ok)
            {
                errorMessage = $"offline server cannot use reason=Ok (nodeId={server.NodeId}).";
                return false;
            }

            server.SetOffline(reason);
        }

        if (!TryApplyServerUsers(server, serverState.Users, out errorMessage))
        {
            return false;
        }

        if (!TryApplyServerOverlay(server, serverState.DiskOverlay, out errorMessage))
        {
            return false;
        }

        if (!TryApplyServerLogs(server, serverState, out errorMessage))
        {
            return false;
        }

        if (serverState.Ports is not null &&
            !TryApplyServerPorts(server, serverState.Ports, out errorMessage))
        {
            return false;
        }

        if (serverState.Daemons is not null &&
            !TryApplyServerDaemons(server, serverState.Daemons, out errorMessage))
        {
            return false;
        }

        return true;
    }

    private static bool TryApplyServerUsers(
        ServerNodeRuntime server,
        IReadOnlyDictionary<string, UserSnapshotDto> users,
        out string errorMessage)
    {
        errorMessage = string.Empty;
        server.Users.Clear();
        var resolvedUserIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var userPair in users.OrderBy(static pair => pair.Key, StringComparer.Ordinal))
        {
            if (string.IsNullOrWhiteSpace(userPair.Key))
            {
                errorMessage = $"server '{server.NodeId}' includes empty userKey.";
                return false;
            }

            if (!TryResolveEnum(userPair.Value.AuthMode, out AuthMode authMode))
            {
                errorMessage = $"server '{server.NodeId}', userKey '{userPair.Key}' has invalid authMode value '{userPair.Value.AuthMode}'.";
                return false;
            }

            var userId = string.IsNullOrWhiteSpace(userPair.Value.UserId) ? userPair.Key : userPair.Value.UserId.Trim();
            if (!resolvedUserIds.Add(userId))
            {
                errorMessage = $"server '{server.NodeId}' includes duplicate userId '{userId}'.";
                return false;
            }

            var user = new UserConfig
            {
                UserId = userId,
                UserPasswd = userPair.Value.UserPasswd ?? string.Empty,
                AuthMode = authMode,
                Privilege = new PrivilegeConfig
                {
                    Read = userPair.Value.Privilege.Read,
                    Write = userPair.Value.Privilege.Write,
                    Execute = userPair.Value.Privilege.Execute,
                },
            };

            user.Info.Clear();
            foreach (var infoLine in userPair.Value.Info.Where(static value => !string.IsNullOrWhiteSpace(value)))
            {
                user.Info.Add(infoLine);
            }

            server.Users[userPair.Key] = user;
        }

        return true;
    }

    private static bool TryApplyServerPorts(
        ServerNodeRuntime server,
        IReadOnlyDictionary<int, PortSnapshotDto> ports,
        out string errorMessage)
    {
        errorMessage = string.Empty;
        server.Ports.Clear();
        foreach (var portPair in ports.OrderBy(static pair => pair.Key))
        {
            if (portPair.Key < 0 || portPair.Key > 65535)
            {
                errorMessage = $"server '{server.NodeId}' has invalid port number '{portPair.Key}'.";
                return false;
            }

            if (!TryResolveEnum(portPair.Value.PortType, out PortType portType))
            {
                errorMessage = $"server '{server.NodeId}', port '{portPair.Key}' has invalid portType value '{portPair.Value.PortType}'.";
                return false;
            }

            if (!TryResolveEnum(portPair.Value.Exposure, out PortExposure exposure))
            {
                errorMessage = $"server '{server.NodeId}', port '{portPair.Key}' has invalid exposure value '{portPair.Value.Exposure}'.";
                return false;
            }

            server.Ports[portPair.Key] = new PortConfig
            {
                PortType = portType,
                ServiceId = portPair.Value.ServiceId ?? string.Empty,
                Exposure = exposure,
            };
        }

        return true;
    }

    private static bool TryApplyServerDaemons(
        ServerNodeRuntime server,
        IReadOnlyDictionary<int, DaemonSnapshotDto> daemons,
        out string errorMessage)
    {
        errorMessage = string.Empty;
        server.Daemons.Clear();
        foreach (var daemonPair in daemons.OrderBy(static pair => pair.Key))
        {
            if (!TryResolveEnum(daemonPair.Key, out DaemonType daemonType))
            {
                errorMessage = $"server '{server.NodeId}' has invalid daemonType value '{daemonPair.Key}'.";
                return false;
            }

            if (!TryConvertSaveValuesToObjectMap(daemonPair.Value.DaemonArgs, out var daemonArgs, out errorMessage))
            {
                errorMessage = $"server '{server.NodeId}', daemon '{daemonType}' failed to decode daemonArgs: {errorMessage}";
                return false;
            }

            var daemon = new DaemonStruct
            {
                DaemonType = daemonType,
            };

            daemon.DaemonArgs.Clear();
            foreach (var daemonArgPair in daemonArgs)
            {
                daemon.DaemonArgs[daemonArgPair.Key] = daemonArgPair.Value;
            }

            server.Daemons[daemonType] = daemon;
        }

        return true;
    }

    private static bool TryApplyServerOverlay(
        ServerNodeRuntime server,
        DiskOverlaySnapshotDto overlaySnapshot,
        out string errorMessage)
    {
        errorMessage = string.Empty;
        server.DiskOverlay.ClearOverlayForLoad();

        var entries = overlaySnapshot.Entries
            .OrderBy(static entry => entry.Path.Count(static c => c == '/'))
            .ThenBy(static entry => entry.Path, StringComparer.Ordinal)
            .ToArray();
        foreach (var entry in entries)
        {
            if (string.IsNullOrWhiteSpace(entry.Path))
            {
                errorMessage = $"server '{server.NodeId}' includes overlay entry with empty path.";
                return false;
            }

            var normalizedPath = BaseFileSystem.NormalizePath("/", entry.Path);
            if (!TryResolveEnum(entry.EntryKind, out VfsEntryKind entryKind))
            {
                errorMessage = $"server '{server.NodeId}', path '{normalizedPath}' has invalid entryKind '{entry.EntryKind}'.";
                return false;
            }

            if (!TryEnsureOverlayParentDirectories(server.DiskOverlay, normalizedPath, out errorMessage))
            {
                errorMessage = $"server '{server.NodeId}', path '{normalizedPath}': {errorMessage}";
                return false;
            }

            try
            {
                if (entryKind == VfsEntryKind.Dir)
                {
                    server.DiskOverlay.AddDirectory(normalizedPath, "/");
                    continue;
                }

                if (entry.Size < 0)
                {
                    errorMessage = $"server '{server.NodeId}', path '{normalizedPath}' has negative size '{entry.Size}'.";
                    return false;
                }

                if (entry.FileKind is null || !TryResolveEnum(entry.FileKind.Value, out VfsFileKind fileKind))
                {
                    errorMessage = $"server '{server.NodeId}', path '{normalizedPath}' has invalid fileKind.";
                    return false;
                }

                server.DiskOverlay.WriteFile(
                    normalizedPath,
                    entry.Content ?? string.Empty,
                    cwd: "/",
                    fileKind: fileKind,
                    size: entry.Size);
            }
            catch (Exception ex)
            {
                errorMessage = ex.Message;
                return false;
            }
        }

        foreach (var tombstonePath in overlaySnapshot.Tombstones.OrderBy(static value => value, StringComparer.Ordinal))
        {
            if (string.IsNullOrWhiteSpace(tombstonePath))
            {
                continue;
            }

            try
            {
                server.DiskOverlay.AddTombstone(tombstonePath, "/");
            }
            catch (Exception ex)
            {
                errorMessage = $"server '{server.NodeId}' tombstone '{tombstonePath}' failed: {ex.Message}";
                return false;
            }
        }

        return true;
    }

    private static bool TryEnsureOverlayParentDirectories(OverlayFileSystem overlay, string normalizedPath, out string errorMessage)
    {
        errorMessage = string.Empty;
        if (normalizedPath == "/")
        {
            return true;
        }

        var parentPath = GetParentPath(normalizedPath);
        if (parentPath == "/")
        {
            return true;
        }

        var segments = parentPath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var currentPath = "/";
        foreach (var segment in segments)
        {
            var nextPath = currentPath == "/" ? "/" + segment : currentPath + "/" + segment;
            if (!overlay.TryResolveEntry(nextPath, out var entry))
            {
                overlay.AddDirectory(nextPath, "/");
            }
            else if (entry.EntryKind != VfsEntryKind.Dir)
            {
                errorMessage = $"parent path '{nextPath}' exists but is not a directory.";
                return false;
            }

            currentPath = nextPath;
        }

        return true;
    }

    private static bool TryApplyServerLogs(
        ServerNodeRuntime server,
        ServerStateChunkDto serverState,
        out string errorMessage)
    {
        errorMessage = string.Empty;
        if (serverState.LogCapacity is not null)
        {
            if (serverState.LogCapacity.Value < 1)
            {
                errorMessage = $"server '{server.NodeId}' has invalid logCapacity '{serverState.LogCapacity.Value}'.";
                return false;
            }

            server.LogCapacity = serverState.LogCapacity.Value;
        }

        server.ClearLogsForLoad();
        foreach (var logSnapshot in serverState.Logs)
        {
            if (!TryBuildLogStruct(logSnapshot, out var log, out errorMessage))
            {
                errorMessage = $"server '{server.NodeId}' failed to restore log id '{logSnapshot.Id}': {errorMessage}";
                return false;
            }

            server.AppendLogForLoad(log);
        }

        return true;
    }

    private static bool TryBuildLogStruct(
        LogSnapshotDto snapshot,
        out LogStruct log,
        out string errorMessage,
        int depth = 0)
    {
        errorMessage = string.Empty;
        log = null!;
        if (depth > 16)
        {
            errorMessage = "log origin depth exceeded maximum.";
            return false;
        }

        if (!TryResolveEnum(snapshot.ActionType, out LogActionType actionType))
        {
            errorMessage = $"invalid actionType value '{snapshot.ActionType}'.";
            return false;
        }

        LogStruct? origin = null;
        if (snapshot.Origin is not null)
        {
            if (!TryBuildLogStruct(snapshot.Origin, out var originStruct, out errorMessage, depth + 1))
            {
                return false;
            }

            origin = originStruct;
        }

        log = LogStruct.CreateForLoad(
            snapshot.Id,
            snapshot.Time,
            snapshot.User ?? string.Empty,
            snapshot.RemoteIp ?? "127.0.0.1",
            actionType,
            snapshot.Action ?? string.Empty,
            snapshot.Dirty,
            origin);
        return true;
    }

    private bool TryRebuildWorldFromSaveMeta(SaveMetaChunkDto saveMeta, out string errorMessage)
    {
        errorMessage = string.Empty;

        if (saveMeta.WorldSeed == 0)
        {
            errorMessage = "save worldSeed must be non-zero.";
            return false;
        }

        if (systemCallProcessor is null)
        {
            errorMessage = "system call processor is not initialized.";
            return false;
        }

        if (!TryConfigureStartupSelectionForLoad(saveMeta.ActiveScenarioId, out errorMessage))
        {
            return false;
        }

        worldSeed = saveMeta.WorldSeed;
        try
        {
            BuildInitialWorldFromBlueprint();
            initializationStage = InitializationStage.Ready;
        }
        catch (Exception ex)
        {
            initializationStage = InitializationStage.Ready;
            errorMessage = ex.Message;
            return false;
        }

        return true;
    }

    private bool TryConfigureStartupSelectionForLoad(string activeScenarioId, out string errorMessage)
    {
        errorMessage = string.Empty;
        var normalizedActiveScenarioId = activeScenarioId?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(normalizedActiveScenarioId))
        {
            errorMessage = "activeScenarioId cannot be empty.";
            return false;
        }

        if (normalizedActiveScenarioId.StartsWith(SaveContainerConstants.CampaignPrefix, StringComparison.Ordinal))
        {
            var campaignId = normalizedActiveScenarioId[SaveContainerConstants.CampaignPrefix.Length..].Trim();
            if (string.IsNullOrWhiteSpace(campaignId))
            {
                errorMessage = "campaign activeScenarioId is missing campaign id.";
                return false;
            }

            StartupCampaignId = campaignId;
            StartupScenarioId = string.Empty;
            return true;
        }

        StartupScenarioId = normalizedActiveScenarioId;
        StartupCampaignId = string.Empty;
        return true;
    }

    private bool TryRestoreFromSnapshot(RuntimeSaveSnapshot backupSnapshot, out string errorMessage)
    {
        errorMessage = string.Empty;
        if (!TryRebuildWorldFromSaveMeta(backupSnapshot.SaveMeta, out errorMessage))
        {
            return false;
        }

        if (!TryApplyRuntimeSnapshot(backupSnapshot, out errorMessage))
        {
            return false;
        }

        return true;
    }

    private void SafeReinitializeAfterFailedLoad()
    {
        try
        {
            BuildInitialWorldFromBlueprint();
            initializationStage = InitializationStage.Ready;
        }
        catch (Exception ex)
        {
            GD.PushWarning($"safe runtime reinitialize failed after load error: {ex.Message}");
        }
    }

    private void EnsureSessionStateClearedForLoad()
    {
        ResetTerminalSessionState();
        foreach (var server in ServerList.Values)
        {
            server.ClearSessions();
        }
    }

    private bool TryResolveMyWorkstationServer(out ServerNodeRuntime server)
    {
        if (ServerList.TryGetValue(DefaultStartupServerNodeId, out var configuredDefault))
        {
            server = configuredDefault;
            return true;
        }

        server = ServerList.Values
            .Where(static value =>
                string.Equals(value.NodeId, "myWorkstation", StringComparison.Ordinal) ||
                value.NodeId.EndsWith("/myWorkstation", StringComparison.Ordinal))
            .OrderBy(static value => value.NodeId, StringComparer.Ordinal)
            .FirstOrDefault()!;

        return server is not null;
    }

    private bool TryResolveSavePath(string savePath, out string resolvedSavePath, out string errorMessage)
    {
        resolvedSavePath = string.Empty;
        errorMessage = string.Empty;
        if (string.IsNullOrWhiteSpace(savePath))
        {
            errorMessage = "savePath cannot be empty.";
            return false;
        }

        var trimmedPath = savePath.Trim();
        string basePath;
        if (trimmedPath.StartsWith("res://", StringComparison.Ordinal) ||
            trimmedPath.StartsWith("user://", StringComparison.Ordinal))
        {
            basePath = ProjectSettings.GlobalizePath(trimmedPath);
        }
        else if (Path.IsPathRooted(trimmedPath))
        {
            basePath = trimmedPath;
        }
        else
        {
            var userDirectory = ProjectSettings.GlobalizePath("user://");
            basePath = Path.Combine(userDirectory, trimmedPath);
        }

        try
        {
            resolvedSavePath = Path.GetFullPath(basePath);
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            errorMessage = ex.Message;
            return false;
        }
    }

    private bool TryResolveHmacKey(out byte[] hmacKey, out string errorMessage)
    {
        hmacKey = [];
        errorMessage = string.Empty;
        var rawKey = NormalizeSaveHmacKeyText(SaveHmacKeyBase64);
        if (string.IsNullOrWhiteSpace(rawKey))
        {
            errorMessage = "SaveHmacKeyBase64 must be configured before save/load.";
            return false;
        }

        try
        {
            hmacKey = Convert.FromBase64String(rawKey);
        }
        catch (FormatException ex)
        {
            errorMessage = $"SaveHmacKeyBase64 is not valid base64: {ex.Message}";
            return false;
        }

        if (hmacKey.Length == 0)
        {
            errorMessage = "SaveHmacKeyBase64 resolved to an empty key.";
            return false;
        }

        return true;
    }

    private static string NormalizeSaveHmacKeyText(string? rawKey)
    {
        var normalized = (rawKey ?? string.Empty)
            .Replace("\uFEFF", string.Empty, StringComparison.Ordinal)
            .Replace("\u200B", string.Empty, StringComparison.Ordinal)
            .Replace("\u200C", string.Empty, StringComparison.Ordinal)
            .Replace("\u200D", string.Empty, StringComparison.Ordinal)
            .Trim();

        if (normalized.Length >= 2 && IsMatchingQuotePair(normalized[0], normalized[^1]))
        {
            normalized = normalized[1..^1].Trim();
        }

        return normalized;
    }

    private static bool IsMatchingQuotePair(char first, char last)
    {
        return (first == '"' && last == '"')
            || (first == '\'' && last == '\'')
            || (first == '\u201C' && last == '\u201D')
            || (first == '\u2018' && last == '\u2019')
            || (first == '\uFF02' && last == '\uFF02')
            || (first == '\uFF07' && last == '\uFF07');
    }

    private static bool TryConvertObjectMapToSaveValues(
        IDictionary<string, object> source,
        out Dictionary<string, SaveValueDto> converted,
        out string errorMessage)
    {
        converted = new Dictionary<string, SaveValueDto>(StringComparer.Ordinal);
        errorMessage = string.Empty;
        foreach (var pair in source)
        {
            if (string.IsNullOrWhiteSpace(pair.Key))
            {
                errorMessage = "map key cannot be empty.";
                return false;
            }

            if (!SaveValueConverter.TryToDto(pair.Value, out var convertedValue, out var valueError))
            {
                errorMessage = $"key '{pair.Key}': {valueError}";
                return false;
            }

            converted[pair.Key] = convertedValue;
        }

        return true;
    }

    private static bool TryConvertSaveValuesToObjectMap(
        IDictionary<string, SaveValueDto> source,
        out Dictionary<string, object> converted,
        out string errorMessage)
    {
        converted = new Dictionary<string, object>(StringComparer.Ordinal);
        errorMessage = string.Empty;
        foreach (var pair in source)
        {
            if (string.IsNullOrWhiteSpace(pair.Key))
            {
                errorMessage = "map key cannot be empty.";
                return false;
            }

            if (!SaveValueConverter.TryFromDto(pair.Value, out var convertedValue, out var valueError))
            {
                errorMessage = $"key '{pair.Key}': {valueError}";
                return false;
            }

            converted[pair.Key] = convertedValue!;
        }

        return true;
    }

    private static bool TryResolveEnum<TEnum>(int rawValue, out TEnum value)
        where TEnum : struct, Enum
    {
        if (Enum.IsDefined(typeof(TEnum), rawValue))
        {
            value = (TEnum)Enum.ToObject(typeof(TEnum), rawValue);
            return true;
        }

        value = default;
        return false;
    }

    private static SaveLoadResult CreateSuccessResult(string message, string savePath)
    {
        return new SaveLoadResult
        {
            Ok = true,
            Code = SaveLoadErrorCode.None,
            Message = message ?? string.Empty,
            SavePath = savePath ?? string.Empty,
        };
    }

    private static SaveLoadResult CreateFailureResult(SaveLoadErrorCode code, string message, string savePath)
    {
        return new SaveLoadResult
        {
            Ok = false,
            Code = code,
            Message = message ?? string.Empty,
            SavePath = savePath ?? string.Empty,
        };
    }

    private static SaveLoadResult WithSavePath(SaveLoadResult result, string savePath)
    {
        return new SaveLoadResult
        {
            Ok = result.Ok,
            Code = result.Code,
            Message = result.Message,
            SavePath = savePath ?? string.Empty,
        };
    }
}
