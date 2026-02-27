using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using Uplink2.Runtime;
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
        var header = CreateSaveHeader(formatMajor: 1, formatMinor: 0, flags: 0x3u);
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
        Assert.Equal((ushort)0, (ushort)GetPropertyValue(parsedHeader, "FormatMinor"));
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
        var header = CreateSaveHeader(formatMajor: 1, formatMinor: 0, flags: 0x3u);
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
}
