using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using Uplink2.Runtime;
using Uplink2.Vfs;
using Xunit;

#nullable enable

namespace Uplink2.Tests;

/// <summary>Tests for save/load persistence container and value conversion helpers.</summary>
[Trait("Speed", "fast")]
public sealed class PersistenceCodecTest
{
    /// <summary>Ensures container write/read preserves header fields and payload bytes.</summary>
    [Fact]
    public void SaveContainerCodec_RoundTrip_PreservesChunkPayload()
    {
        var header = CreateSaveHeader(formatMajor: 1, formatMinor: 1, flags: 0x3u);
        var chunk = CreateChunkRecord(0x4242u, 1, Encoding.UTF8.GetBytes("hello"));
        var chunks = CreateChunkList(chunk);
        var hmacKey = Encoding.UTF8.GetBytes("0123456789abcdef0123456789abcdef");

        var fileBytes = BuildContainer(header, chunks, hmacKey);
        var parseOk = TryParseContainer(fileBytes, hmacKey, out var parsedContainer, out var errorCode, out _);

        Assert.True(parseOk);
        Assert.Equal(SaveLoadErrorCode.None, errorCode);
        Assert.NotNull(parsedContainer);

        var parsedHeader = GetPropertyValue(parsedContainer!, "Header");
        Assert.Equal((ushort)1, (ushort)GetPropertyValue(parsedHeader, "FormatMajor"));
        Assert.Equal((ushort)1, (ushort)GetPropertyValue(parsedHeader, "FormatMinor"));
        Assert.Equal(0x3u, (uint)GetPropertyValue(parsedHeader, "Flags"));
        Assert.Equal(1u, (uint)GetPropertyValue(parsedHeader, "ChunkCount"));

        var parsedChunks = (IEnumerable)GetPropertyValue(parsedContainer!, "Chunks");
        object? firstChunk = null;
        foreach (var chunkEntry in parsedChunks)
        {
            firstChunk = chunkEntry;
            break;
        }

        Assert.NotNull(firstChunk);
        Assert.Equal(0x4242u, (uint)GetPropertyValue(firstChunk!, "ChunkId"));
        Assert.Equal((ushort)1, (ushort)GetPropertyValue(firstChunk!, "ChunkVersion"));
        Assert.Equal("hello", Encoding.UTF8.GetString((byte[])GetPropertyValue(firstChunk!, "PayloadBytes")));
    }

    /// <summary>Ensures HMAC verification fails when one byte in signed data is tampered.</summary>
    [Fact]
    public void SaveContainerCodec_TamperedBytes_FailsIntegrityCheck()
    {
        var header = CreateSaveHeader(formatMajor: 1, formatMinor: 1, flags: 0x3u);
        var chunk = CreateChunkRecord(0x4242u, 1, Encoding.UTF8.GetBytes("hello"));
        var chunks = CreateChunkList(chunk);
        var hmacKey = Encoding.UTF8.GetBytes("0123456789abcdef0123456789abcdef");
        var fileBytes = BuildContainer(header, chunks, hmacKey);

        var tamperIndex = Math.Max(0, fileBytes.Length - 33);
        fileBytes[tamperIndex] ^= 0x01;

        var parseOk = TryParseContainer(fileBytes, hmacKey, out _, out var errorCode, out _);

        Assert.False(parseOk);
        Assert.Equal(SaveLoadErrorCode.IntegrityCheckFailed, errorCode);
    }

    /// <summary>Ensures world-load validation rejects legacy minor format saves.</summary>
    [Fact]
    public void TryValidateContainerVersion_Fails_WhenFormatMinorIsLegacy()
    {
        var world = CreateUninitializedWorldRuntime();
        var header = CreateSaveHeader(formatMajor: 1, formatMinor: 0, flags: 0x3u);

        var valid = InvokeTryValidateContainerVersion(world, header, out var failure);

        Assert.False(valid);
        Assert.Equal(SaveLoadErrorCode.UnsupportedVersion, failure.Code);
        Assert.Contains("minor", failure.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Ensures icon persistence extension does not change save major/minor or schema version constants.</summary>
    [Fact]
    public void SaveContainerConstants_IconExtension_DoesNotBumpVersion()
    {
        var constantsType = RequireRuntimeType("Uplink2.Runtime.Persistence.SaveContainerConstants");
        var formatMinorField = constantsType.GetField("FormatMinor", BindingFlags.Static | BindingFlags.NonPublic);
        var schemaVersionField = constantsType.GetField("SaveSchemaVersion", BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(formatMinorField);
        Assert.NotNull(schemaVersionField);

        var formatMinor = (ushort)formatMinorField!.GetRawConstantValue()!;
        var schemaVersion = (string)schemaVersionField!.GetRawConstantValue()!;

        Assert.Equal((ushort)1, formatMinor);
        Assert.Equal("0.2", schemaVersion);
    }

    /// <summary>Ensures unsupported runtime value types are rejected by SaveValueConverter.</summary>
    [Fact]
    public void SaveValueConverter_UnsupportedRuntimeType_ReturnsFalse()
    {
        var converterType = RequireRuntimeType("Uplink2.Runtime.Persistence.SaveValueConverter");
        var method = converterType.GetMethod(
            "TryToDto",
            BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(method);

        object?[] args = { DateTime.UtcNow, null, string.Empty };
        var converted = (bool)method!.Invoke(null, args)!;

        Assert.False(converted);
        Assert.Contains("unsupported runtime value type", (string)args[2]!);
    }

    /// <summary>Ensures log restore fails when sourceNodeId is missing.</summary>
    [Fact]
    public void TryBuildLogStruct_Fails_WhenSourceNodeIdIsMissing()
    {
        var snapshot = CreateInternalInstance("Uplink2.Runtime.Persistence.LogSnapshotDto");
        SetPropertyValue(snapshot, "Id", 17);
        SetPropertyValue(snapshot, "Time", 1234L);
        SetPropertyValue(snapshot, "User", "guest");
        SetPropertyValue(snapshot, "SourceNodeId", " ");
        SetPropertyValue(snapshot, "RemoteIp", "10.0.1.20");
        SetPropertyValue(snapshot, "ActionType", (int)LogActionType.Read);
        SetPropertyValue(snapshot, "Action", "cat /etc/passwd");
        SetPropertyValue(snapshot, "Dirty", false);
        SetPropertyValue(snapshot, "Origin", null);

        var restored = TryBuildLogStruct(snapshot, out var log, out var error);

        Assert.False(restored);
        Assert.Null(log);
        Assert.Contains("sourceNodeId", error, StringComparison.Ordinal);
    }

    /// <summary>Ensures log snapshot capture/restore roundtrip keeps sourceNodeId.</summary>
    [Fact]
    public void LogSnapshot_RoundTrip_PreservesSourceNodeId()
    {
        var source = new LogStruct
        {
            Id = 9,
            Time = 7777L,
            User = "guest",
            SourceNodeId = "node-1",
            RemoteIp = "10.0.1.20",
            ActionType = LogActionType.Execute,
            Action = "miniscript /scripts/probe.ms",
        };
        var snapshot = CaptureLogSnapshot(source);

        var restored = TryBuildLogStruct(snapshot, out var log, out var error);

        Assert.True(restored, error);
        Assert.NotNull(log);
        Assert.Equal(source.SourceNodeId, log!.SourceNodeId);
        Assert.Equal(source.RemoteIp, log.RemoteIp);
        Assert.Equal(source.Action, log.Action);
    }

    /// <summary>Ensures server-state apply fails when required location payload is missing.</summary>
    [Fact]
    public void TryApplyServerState_Fails_WhenLocationSnapshotIsMissing()
    {
        using var scope = TempDirScope.Create();
        var regionPath = scope.WriteFile(
            "regions.yaml",
            """
            regions:
              Asia:
                boxes:
                  - [10.0, 20.0, 20.0, 30.0]
              Unknown:
                boxes:
                  - [-90.0, -180.0, 90.0, 180.0]
            """);

        var world = CreateUninitializedWorldRuntime(regionPath);
        var server = CreateServer("node-1");
        var serverState = CreateInternalInstance("Uplink2.Runtime.Persistence.ServerStateChunkDto");
        SetPropertyValue(serverState, "NodeId", "node-1");
        SetPropertyValue(serverState, "Status", (int)ServerStatus.Online);
        SetPropertyValue(serverState, "Reason", (int)ServerReason.Ok);
        SetPropertyValue(serverState, "Location", null);

        var applied = InvokeTryApplyServerState(world, server, serverState, out var errorMessage);

        Assert.False(applied);
        Assert.Contains("location", errorMessage, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Ensures save snapshot keeps region/lat/lng and load recomputes displayName from coordinates.</summary>
    [Fact]
    public void ServerLocationSnapshot_Apply_RecomputesDisplayNameFromCoordinates()
    {
        using var scope = TempDirScope.Create();
        var regionPath = scope.WriteFile(
            "regions.yaml",
            """
            regions:
              Asia:
                boxes:
                  - [30.0, 120.0, 45.0, 135.0]
              Korea:
                boxes:
                  - [33.0, 124.5, 38.6, 130.9]
              Unknown:
                boxes:
                  - [-90.0, -180.0, 90.0, 180.0]
            """);

        var world = CreateUninitializedWorldRuntime(regionPath);
        var server = CreateServer("node-1");
        server.Location = new RuntimeLocationInfo
        {
            RegionId = "Asia",
            DisplayName = "Asia",
            Lat = 35.0,
            Lng = 125.0,
        };

        var captured = InvokeTryCaptureServerState(world, server, out var serverState, out var failure);
        Assert.True(captured, failure.Message);

        var locationSnapshot = GetPropertyValue(serverState!, "Location");
        Assert.NotNull(locationSnapshot);
        SetPropertyValue(locationSnapshot, "RegionId", "Asia");
        SetPropertyValue(locationSnapshot, "Lat", 37.55d);
        SetPropertyValue(locationSnapshot, "Lng", 126.99d);

        var applied = InvokeTryApplyServerState(world, server, serverState!, out var applyError);
        Assert.True(applied, applyError);
        Assert.Equal("Asia", server.Location.RegionId);
        Assert.Equal("Korea", server.Location.DisplayName);
        Assert.Equal(37.55d, server.Location.Lat, 9);
        Assert.Equal(126.99d, server.Location.Lng, 9);
    }

    /// <summary>Ensures server-state capture snapshots icon payload values.</summary>
    [Fact]
    public void TryCaptureServerState_CapturesIconSnapshot()
    {
        var world = CreateUninitializedWorldRuntime();
        var server = CreateServer("node-1");
        server.Icon = new RuntimeServerIconInfo
        {
            IconType = ServerIconType.Triangle,
            HaloType = ServerHaloType.Yellow,
        };

        var captured = InvokeTryCaptureServerState(world, server, out var serverState, out var failure);
        Assert.True(captured, failure.Message);

        var iconSnapshot = GetPropertyValue(serverState!, "Icon");
        Assert.NotNull(iconSnapshot);
        Assert.Equal((int)ServerIconType.Triangle, (int)GetPropertyValue(iconSnapshot, "IconType"));
        Assert.Equal((int)ServerHaloType.Yellow, (int)GetPropertyValue(iconSnapshot, "HaloType"));
    }

    /// <summary>Ensures load falls back to default icon values when icon snapshot is missing.</summary>
    [Fact]
    public void TryApplyServerState_UsesDefaultIcon_WhenIconSnapshotIsMissing()
    {
        using var scope = TempDirScope.Create();
        var regionPath = scope.WriteFile(
            "regions.yaml",
            """
            regions:
              Asia:
                boxes:
                  - [10.0, 20.0, 20.0, 30.0]
              Unknown:
                boxes:
                  - [-90.0, -180.0, 90.0, 180.0]
            """);

        var world = CreateUninitializedWorldRuntime(regionPath);
        var server = CreateServer("node-1");
        server.Icon = new RuntimeServerIconInfo
        {
            IconType = ServerIconType.Square,
            HaloType = ServerHaloType.Yellow,
        };

        var locationSnapshot = CreateInternalInstance("Uplink2.Runtime.Persistence.ServerLocationSnapshotDto");
        SetPropertyValue(locationSnapshot, "RegionId", "Asia");
        SetPropertyValue(locationSnapshot, "Lat", 15.0d);
        SetPropertyValue(locationSnapshot, "Lng", 25.0d);

        var serverState = CreateInternalInstance("Uplink2.Runtime.Persistence.ServerStateChunkDto");
        SetPropertyValue(serverState, "NodeId", "node-1");
        SetPropertyValue(serverState, "Status", (int)ServerStatus.Online);
        SetPropertyValue(serverState, "Reason", (int)ServerReason.Ok);
        SetPropertyValue(serverState, "Location", locationSnapshot);
        SetPropertyValue(serverState, "Icon", null);

        var applied = InvokeTryApplyServerState(world, server, serverState, out var errorMessage);
        Assert.True(applied, errorMessage);
        Assert.Equal(ServerIconType.Circle, server.Icon.IconType);
        Assert.Equal(ServerHaloType.None, server.Icon.HaloType);
    }

    /// <summary>Ensures load applies icon snapshot values when enums are valid.</summary>
    [Fact]
    public void TryApplyServerState_AppliesIcon_WhenSnapshotIsValid()
    {
        using var scope = TempDirScope.Create();
        var regionPath = scope.WriteFile(
            "regions.yaml",
            """
            regions:
              Asia:
                boxes:
                  - [10.0, 20.0, 20.0, 30.0]
              Unknown:
                boxes:
                  - [-90.0, -180.0, 90.0, 180.0]
            """);

        var world = CreateUninitializedWorldRuntime(regionPath);
        var server = CreateServer("node-1");

        var locationSnapshot = CreateInternalInstance("Uplink2.Runtime.Persistence.ServerLocationSnapshotDto");
        SetPropertyValue(locationSnapshot, "RegionId", "Asia");
        SetPropertyValue(locationSnapshot, "Lat", 15.0d);
        SetPropertyValue(locationSnapshot, "Lng", 25.0d);

        var iconSnapshot = CreateInternalInstance("Uplink2.Runtime.Persistence.ServerIconSnapshotDto");
        SetPropertyValue(iconSnapshot, "IconType", (int)ServerIconType.Triangle);
        SetPropertyValue(iconSnapshot, "HaloType", (int)ServerHaloType.Yellow);

        var serverState = CreateInternalInstance("Uplink2.Runtime.Persistence.ServerStateChunkDto");
        SetPropertyValue(serverState, "NodeId", "node-1");
        SetPropertyValue(serverState, "Status", (int)ServerStatus.Online);
        SetPropertyValue(serverState, "Reason", (int)ServerReason.Ok);
        SetPropertyValue(serverState, "Location", locationSnapshot);
        SetPropertyValue(serverState, "Icon", iconSnapshot);

        var applied = InvokeTryApplyServerState(world, server, serverState, out var errorMessage);
        Assert.True(applied, errorMessage);
        Assert.Equal(ServerIconType.Triangle, server.Icon.IconType);
        Assert.Equal(ServerHaloType.Yellow, server.Icon.HaloType);
    }

    /// <summary>Ensures load rejects icon snapshots with out-of-range enum values.</summary>
    [Fact]
    public void TryApplyServerState_Fails_WhenIconSnapshotContainsInvalidEnum()
    {
        using var scope = TempDirScope.Create();
        var regionPath = scope.WriteFile(
            "regions.yaml",
            """
            regions:
              Asia:
                boxes:
                  - [10.0, 20.0, 20.0, 30.0]
              Unknown:
                boxes:
                  - [-90.0, -180.0, 90.0, 180.0]
            """);

        var world = CreateUninitializedWorldRuntime(regionPath);
        var server = CreateServer("node-1");

        var locationSnapshot = CreateInternalInstance("Uplink2.Runtime.Persistence.ServerLocationSnapshotDto");
        SetPropertyValue(locationSnapshot, "RegionId", "Asia");
        SetPropertyValue(locationSnapshot, "Lat", 15.0d);
        SetPropertyValue(locationSnapshot, "Lng", 25.0d);

        var iconSnapshot = CreateInternalInstance("Uplink2.Runtime.Persistence.ServerIconSnapshotDto");
        SetPropertyValue(iconSnapshot, "IconType", 999);
        SetPropertyValue(iconSnapshot, "HaloType", (int)ServerHaloType.None);

        var serverState = CreateInternalInstance("Uplink2.Runtime.Persistence.ServerStateChunkDto");
        SetPropertyValue(serverState, "NodeId", "node-1");
        SetPropertyValue(serverState, "Status", (int)ServerStatus.Online);
        SetPropertyValue(serverState, "Reason", (int)ServerReason.Ok);
        SetPropertyValue(serverState, "Location", locationSnapshot);
        SetPropertyValue(serverState, "Icon", iconSnapshot);

        var applied = InvokeTryApplyServerState(world, server, serverState, out var errorMessage);
        Assert.False(applied);
        Assert.Contains("invalid icon type value", errorMessage, StringComparison.Ordinal);
    }

    private static object CreateSaveHeader(ushort formatMajor, ushort formatMinor, uint flags)
    {
        var header = CreateInternalInstance("Uplink2.Runtime.Persistence.SaveFileHeader");
        SetPropertyValue(header, "FormatMajor", formatMajor);
        SetPropertyValue(header, "FormatMinor", formatMinor);
        SetPropertyValue(header, "Flags", flags);
        SetPropertyValue(header, "ChunkCount", 1u);
        return header;
    }

    private static object CreateChunkRecord(uint chunkId, ushort chunkVersion, byte[] payload)
    {
        var chunk = CreateInternalInstance("Uplink2.Runtime.Persistence.SaveChunkRecord");
        SetPropertyValue(chunk, "ChunkId", chunkId);
        SetPropertyValue(chunk, "ChunkVersion", chunkVersion);
        SetPropertyValue(chunk, "PayloadBytes", payload);
        return chunk;
    }

    private static object CreateChunkList(params object[] chunks)
    {
        var chunkType = RequireRuntimeType("Uplink2.Runtime.Persistence.SaveChunkRecord");
        var listType = typeof(List<>).MakeGenericType(chunkType);
        var list = Activator.CreateInstance(listType);
        Assert.NotNull(list);

        var addMethod = listType.GetMethod("Add", BindingFlags.Instance | BindingFlags.Public);
        Assert.NotNull(addMethod);

        foreach (var chunk in chunks)
        {
            addMethod!.Invoke(list, new[] { chunk });
        }

        return list!;
    }

    private static byte[] BuildContainer(object header, object chunks, byte[] hmacKey)
    {
        var codecType = RequireRuntimeType("Uplink2.Runtime.Persistence.SaveContainerCodec");
        var buildMethod = codecType.GetMethod(
            "BuildContainer",
            BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(buildMethod);

        var result = buildMethod!.Invoke(null, new[] { header, chunks, hmacKey });
        Assert.NotNull(result);
        return (byte[])result!;
    }

    private static bool TryParseContainer(
        byte[] fileBytes,
        byte[] hmacKey,
        out object? parsedContainer,
        out SaveLoadErrorCode errorCode,
        out string errorMessage)
    {
        var codecType = RequireRuntimeType("Uplink2.Runtime.Persistence.SaveContainerCodec");
        var parseMethod = codecType.GetMethod(
            "TryParseContainer",
            BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(parseMethod);

        object?[] args = { fileBytes, hmacKey, null, SaveLoadErrorCode.None, string.Empty };
        var parseOk = (bool)parseMethod!.Invoke(null, args)!;

        parsedContainer = args[2];
        errorCode = (SaveLoadErrorCode)args[3]!;
        errorMessage = (string)args[4]!;
        return parseOk;
    }

    private static object CreateInternalInstance(string fullTypeName)
    {
        var type = RequireRuntimeType(fullTypeName);
        var instance = Activator.CreateInstance(type, nonPublic: true);
        Assert.NotNull(instance);
        return instance!;
    }

    private static void SetPropertyValue(object target, string propertyName, object? value)
    {
        var property = target.GetType().GetProperty(
            propertyName,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        Assert.NotNull(property);
        property!.SetValue(target, value);
    }

    private static object GetPropertyValue(object target, string propertyName)
    {
        var property = target.GetType().GetProperty(
            propertyName,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        Assert.NotNull(property);
        return property!.GetValue(target)!;
    }

    private static Type RequireRuntimeType(string fullTypeName)
    {
        var runtimeType = typeof(WorldRuntime).Assembly.GetType(fullTypeName);
        Assert.NotNull(runtimeType);
        return runtimeType!;
    }

    private static WorldRuntime CreateUninitializedWorldRuntime(string? regionDataFile = null)
    {
        var world = (WorldRuntime)RuntimeHelpers.GetUninitializedObject(typeof(WorldRuntime));
        SetPrivateField(world, "_worldStateLock", new object());
        if (!string.IsNullOrWhiteSpace(regionDataFile))
        {
            world.RegionDataFile = regionDataFile!;
        }

        return world;
    }

    private static ServerNodeRuntime CreateServer(string nodeId)
    {
        var blobStore = new BlobStore();
        var baseFileSystem = new BaseFileSystem(blobStore);
        return new ServerNodeRuntime(nodeId, nodeId, ServerRole.Terminal, baseFileSystem, blobStore);
    }

    private static bool InvokeTryValidateContainerVersion(WorldRuntime world, object header, out SaveLoadResult failure)
    {
        var method = typeof(WorldRuntime).GetMethod(
            "TryValidateContainerVersion",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);
        object?[] args = { header, null };
        var result = (bool)method!.Invoke(world, args)!;
        Assert.IsType<SaveLoadResult>(args[1]);
        failure = (SaveLoadResult)args[1]!;
        return result;
    }

    private static bool InvokeTryCaptureServerState(
        WorldRuntime world,
        ServerNodeRuntime server,
        out object? serverState,
        out SaveLoadResult failure)
    {
        var method = typeof(WorldRuntime).GetMethod(
            "TryCaptureServerState",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);
        object?[] args = { server, null, null };
        var result = (bool)method!.Invoke(world, args)!;
        serverState = args[1];
        Assert.IsType<SaveLoadResult>(args[2]);
        failure = (SaveLoadResult)args[2]!;
        return result;
    }

    private static bool InvokeTryApplyServerState(
        WorldRuntime world,
        ServerNodeRuntime server,
        object serverState,
        out string errorMessage)
    {
        var method = typeof(WorldRuntime).GetMethod(
            "TryApplyServerState",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);
        object?[] args = { server, serverState, string.Empty };
        var result = (bool)method!.Invoke(world, args)!;
        errorMessage = (string)args[2]!;
        return result;
    }

    private static void SetPrivateField(object target, string fieldName, object value)
    {
        var field = target.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        field!.SetValue(target, value);
    }

    private static bool TryBuildLogStruct(object snapshot, out LogStruct? log, out string errorMessage)
    {
        var buildMethod = typeof(WorldRuntime).GetMethod(
            "TryBuildLogStruct",
            BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(buildMethod);

        object?[] args = { snapshot, null, string.Empty, 0 };
        var restored = (bool)buildMethod!.Invoke(null, args)!;

        log = args[1] as LogStruct;
        errorMessage = (string)args[2]!;
        return restored;
    }

    private static object CaptureLogSnapshot(LogStruct log)
    {
        var captureMethod = typeof(WorldRuntime).GetMethod(
            "CaptureLogSnapshot",
            BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(captureMethod);

        var snapshot = captureMethod!.Invoke(null, new object?[] { log, new HashSet<LogStruct>() });
        Assert.NotNull(snapshot);
        return snapshot!;
    }

    private sealed class TempDirScope : IDisposable
    {
        private TempDirScope(string path)
        {
            Path = path;
        }

        public string Path { get; }

        public static TempDirScope Create()
        {
            var path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "uplink2-persistence-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path);
            return new TempDirScope(path);
        }

        public string WriteFile(string fileName, string content)
        {
            var fullPath = System.IO.Path.Combine(Path, fileName);
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(fullPath)!);
            File.WriteAllText(fullPath, content, Encoding.UTF8);
            return fullPath;
        }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Path))
                {
                    Directory.Delete(Path, recursive: true);
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
