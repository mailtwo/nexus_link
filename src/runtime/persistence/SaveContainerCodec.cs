using MessagePack;
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;

#nullable enable

namespace Uplink2.Runtime.Persistence;

internal static class SaveContainerCodec
{
    private static readonly MessagePackSerializerOptions SerializerOptions = MessagePackSerializerOptions.Standard
        .WithSecurity(MessagePackSecurity.UntrustedData);

    internal static byte[] SerializeChunkPayload<T>(T value)
    {
        return MessagePackSerializer.Serialize(value, SerializerOptions);
    }

    internal static T DeserializeChunkPayload<T>(byte[] payloadBytes)
    {
        return MessagePackSerializer.Deserialize<T>(payloadBytes, SerializerOptions);
    }

    internal static byte[] BuildContainer(
        SaveFileHeader header,
        IReadOnlyList<SaveChunkRecord> chunks,
        byte[] hmacKey)
    {
        ArgumentNullException.ThrowIfNull(header);
        ArgumentNullException.ThrowIfNull(chunks);
        ArgumentNullException.ThrowIfNull(hmacKey);

        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(SaveContainerConstants.Magic);
            writer.Write(header.FormatMajor);
            writer.Write(header.FormatMinor);
            writer.Write(header.Flags);
            writer.Write((uint)chunks.Count);

            foreach (var chunk in chunks)
            {
                var payload = chunk.PayloadBytes ?? [];
                if ((header.Flags & SaveContainerConstants.FlagBrotli) != 0u)
                {
                    payload = CompressWithBrotli(payload);
                }

                writer.Write(chunk.ChunkId);
                writer.Write(chunk.ChunkVersion);
                writer.Write((ushort)0);
                writer.Write((uint)payload.Length);
                writer.Write(payload);
            }
        }

        if ((header.Flags & SaveContainerConstants.FlagHmacSha256) != 0u)
        {
            var dataBytes = stream.ToArray();
            var hmacBytes = ComputeHmac(dataBytes, hmacKey);
            stream.Write(hmacBytes, 0, hmacBytes.Length);
        }

        return stream.ToArray();
    }

    internal static bool TryParseContainer(
        byte[] fileBytes,
        byte[] hmacKey,
        out ParsedSaveContainer container,
        out SaveLoadErrorCode errorCode,
        out string errorMessage)
    {
        container = new ParsedSaveContainer();
        errorCode = SaveLoadErrorCode.FormatError;
        errorMessage = "invalid save file format.";

        if (fileBytes is null || fileBytes.Length < 16)
        {
            errorMessage = "save file is too short.";
            return false;
        }

        if (!TryReadHeader(fileBytes, out var header, out errorMessage))
        {
            return false;
        }

        if ((header.Flags & SaveContainerConstants.RequiredFlags) != SaveContainerConstants.RequiredFlags)
        {
            errorCode = SaveLoadErrorCode.UnsupportedVersion;
            errorMessage = "required compression/HMAC flags are missing.";
            return false;
        }

        var unknownFlags = header.Flags & ~SaveContainerConstants.RequiredFlags;
        if (unknownFlags != 0u)
        {
            errorMessage = $"unsupported save header flags: 0x{unknownFlags:X8}.";
            return false;
        }

        var trailerLength = SaveContainerConstants.HmacSha256Length;
        if (fileBytes.Length < 16 + trailerLength)
        {
            errorMessage = "save file does not contain required HMAC trailer.";
            return false;
        }

        if (!TryVerifyHmac(fileBytes, hmacKey))
        {
            errorCode = SaveLoadErrorCode.IntegrityCheckFailed;
            errorMessage = "save file HMAC verification failed.";
            return false;
        }

        var payloadEnd = fileBytes.Length - trailerLength;
        using var stream = new MemoryStream(fileBytes, 0, payloadEnd, writable: false);
        using var reader = new BinaryReader(stream, System.Text.Encoding.UTF8, leaveOpen: true);
        _ = reader.ReadBytes(4); // magic
        _ = reader.ReadUInt16(); // formatMajor
        _ = reader.ReadUInt16(); // formatMinor
        _ = reader.ReadUInt32(); // flags
        var chunkCount = reader.ReadUInt32();

        var chunks = new List<SaveChunkRecord>((int)chunkCount);
        for (var index = 0; index < chunkCount; index++)
        {
            if (!TryReadChunk(reader, payloadEnd, header.Flags, out var chunk, out errorMessage))
            {
                return false;
            }

            chunks.Add(chunk);
        }

        if (stream.Position != payloadEnd)
        {
            errorMessage = "unexpected trailing bytes before HMAC trailer.";
            return false;
        }

        container = new ParsedSaveContainer
        {
            Header = header,
            Chunks = chunks,
        };
        errorCode = SaveLoadErrorCode.None;
        errorMessage = string.Empty;
        return true;
    }

    private static bool TryReadHeader(
        byte[] fileBytes,
        out SaveFileHeader header,
        out string errorMessage)
    {
        header = new SaveFileHeader();
        errorMessage = string.Empty;

        if (fileBytes.Length < 16)
        {
            errorMessage = "save header is truncated.";
            return false;
        }

        var magic = fileBytes.AsSpan(0, 4);
        if (!magic.SequenceEqual(SaveContainerConstants.Magic))
        {
            errorMessage = "save header magic is invalid.";
            return false;
        }

        var formatMajor = BinaryPrimitives.ReadUInt16LittleEndian(fileBytes.AsSpan(4, 2));
        var formatMinor = BinaryPrimitives.ReadUInt16LittleEndian(fileBytes.AsSpan(6, 2));
        var flags = BinaryPrimitives.ReadUInt32LittleEndian(fileBytes.AsSpan(8, 4));
        var chunkCount = BinaryPrimitives.ReadUInt32LittleEndian(fileBytes.AsSpan(12, 4));

        header = new SaveFileHeader
        {
            FormatMajor = formatMajor,
            FormatMinor = formatMinor,
            Flags = flags,
            ChunkCount = chunkCount,
        };
        return true;
    }

    private static bool TryReadChunk(
        BinaryReader reader,
        long payloadEnd,
        uint flags,
        out SaveChunkRecord chunk,
        out string errorMessage)
    {
        chunk = new SaveChunkRecord();
        errorMessage = string.Empty;

        var remaining = payloadEnd - reader.BaseStream.Position;
        if (remaining < 12)
        {
            errorMessage = "chunk header is truncated.";
            return false;
        }

        var chunkId = reader.ReadUInt32();
        var chunkVersion = reader.ReadUInt16();
        var reserved = reader.ReadUInt16();
        var payloadLength = reader.ReadUInt32();

        if (reserved != 0)
        {
            errorMessage = $"chunk 0x{chunkId:X4} has non-zero reserved field.";
            return false;
        }

        if (payloadLength > int.MaxValue)
        {
            errorMessage = $"chunk 0x{chunkId:X4} payload is too large.";
            return false;
        }

        remaining = payloadEnd - reader.BaseStream.Position;
        if (remaining < payloadLength)
        {
            errorMessage = $"chunk 0x{chunkId:X4} payload is truncated.";
            return false;
        }

        var payload = reader.ReadBytes((int)payloadLength);
        if ((flags & SaveContainerConstants.FlagBrotli) != 0u)
        {
            try
            {
                payload = DecompressWithBrotli(payload);
            }
            catch (Exception ex)
            {
                errorMessage = $"chunk 0x{chunkId:X4} Brotli decompression failed: {ex.Message}";
                return false;
            }
        }

        chunk = new SaveChunkRecord
        {
            ChunkId = chunkId,
            ChunkVersion = chunkVersion,
            PayloadBytes = payload,
        };
        return true;
    }

    private static byte[] CompressWithBrotli(byte[] payload)
    {
        if (payload.Length == 0)
        {
            return payload;
        }

        using var output = new MemoryStream();
        using (var brotli = new BrotliStream(output, CompressionLevel.Optimal, leaveOpen: true))
        {
            brotli.Write(payload, 0, payload.Length);
        }

        return output.ToArray();
    }

    private static byte[] DecompressWithBrotli(byte[] payload)
    {
        if (payload.Length == 0)
        {
            return payload;
        }

        using var input = new MemoryStream(payload);
        using var brotli = new BrotliStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        brotli.CopyTo(output);
        return output.ToArray();
    }

    private static byte[] ComputeHmac(byte[] dataBytes, byte[] hmacKey)
    {
        using var hmac = new HMACSHA256(hmacKey);
        return hmac.ComputeHash(dataBytes);
    }

    private static bool TryVerifyHmac(byte[] fileBytes, byte[] hmacKey)
    {
        if (fileBytes.Length < SaveContainerConstants.HmacSha256Length)
        {
            return false;
        }

        var trailerOffset = fileBytes.Length - SaveContainerConstants.HmacSha256Length;
        var expected = new byte[SaveContainerConstants.HmacSha256Length];
        Buffer.BlockCopy(fileBytes, trailerOffset, expected, 0, expected.Length);

        using var hmac = new HMACSHA256(hmacKey);
        var actual = hmac.ComputeHash(fileBytes, 0, trailerOffset);
        return CryptographicOperations.FixedTimeEquals(expected, actual);
    }
}
