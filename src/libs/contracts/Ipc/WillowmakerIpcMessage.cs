// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Buffers;
using System.Buffers.Binary;
using System.Text;
using System.Text.Json;

namespace NEXUS.Contracts.Ipc;

/// <summary>
///     Represents a 12-byte framed binary message for the ProjectKongor IPC protocol.
///     Frame structure: [Magic (2B: 0x574D)][OpCode (2B)][Length (4B)][RequestId (4B)][Payload (NB)].
/// </summary>
public sealed class WillowmakerIpcMessage
{
    public const ushort HeaderMagic = 0x574D; // 'W', 'M'
    public const int HeaderSize = 12; // Magic (2) + OpCode (2) + Length (4) + RequestId (4)
    public const int LegacyHeaderSize = 8; // Legacy Magic (2) + OpCode (2) + Length (4)
    public const int MaxPayloadSize = 16 * 1024 * 1024; // 16 MB max frame size

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public IpcOpCode OpCode { get; init; }
    public uint RequestId { get; init; }
    public byte[] Payload { get; init; } = [];

    public string PayloadString => Payload.Length == 0 ? string.Empty : Encoding.UTF8.GetString(Payload);

    public static WillowmakerIpcMessage CreateString(IpcOpCode opCode, string text, uint requestId = 0)
    {
        byte[] bytes = string.IsNullOrEmpty(text) ? [] : Encoding.UTF8.GetBytes(text);
        return new WillowmakerIpcMessage
        {
            OpCode = opCode,
            RequestId = requestId,
            Payload = bytes
        };
    }

    [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage("Trimming", "IL2026:Members annotated with 'RequiresUnreferencedCodeAttribute' require dynamic access otherwise can break functionality when trimming application code", Justification = "Fallback generic JSON serialization")]
    [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage("AOT", "IL3050:Calling members annotated with 'RequiresDynamicCodeAttribute' may break functionality when AOT compiling.", Justification = "Fallback generic JSON serialization")]
    public static WillowmakerIpcMessage CreateJson<T>(IpcOpCode opCode, T data, uint requestId = 0)
    {
        ArgumentNullException.ThrowIfNull(data);
        byte[] jsonBytes = JsonSerializer.SerializeToUtf8Bytes(data, JsonOpts);
        return new WillowmakerIpcMessage
        {
            OpCode = opCode,
            RequestId = requestId,
            Payload = jsonBytes
        };
    }

    public static WillowmakerIpcMessage CreateJson<T>(IpcOpCode opCode, T data, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> jsonTypeInfo, uint requestId = 0)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(jsonTypeInfo);
        byte[] jsonBytes = JsonSerializer.SerializeToUtf8Bytes(data, jsonTypeInfo);
        return new WillowmakerIpcMessage
        {
            OpCode = opCode,
            RequestId = requestId,
            Payload = jsonBytes
        };
    }

    [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage("Trimming", "IL2026:Members annotated with 'RequiresUnreferencedCodeAttribute' require dynamic access otherwise can break functionality when trimming application code", Justification = "Fallback generic JSON serialization")]
    [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage("AOT", "IL3050:Calling members annotated with 'RequiresDynamicCodeAttribute' may break functionality when AOT compiling.", Justification = "Fallback generic JSON serialization")]
    public T? ReadJson<T>()
    {
        if (Payload.Length == 0) return default;

        return JsonSerializer.Deserialize<T>(Payload, JsonOpts);
    }

    public T? ReadJson<T>(System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> jsonTypeInfo)
    {
        ArgumentNullException.ThrowIfNull(jsonTypeInfo);
        if (Payload.Length == 0) return default;

        return JsonSerializer.Deserialize(Payload, jsonTypeInfo);
    }

    public static WillowmakerIpcMessage CreateBytes(IpcOpCode opCode, ReadOnlySpan<byte> bytes, uint requestId = 0)
    {
        return new WillowmakerIpcMessage
        {
            OpCode = opCode,
            RequestId = requestId,
            Payload = bytes.ToArray()
        };
    }

    public static void EncodeFrame(IpcOpCode opCode, uint requestId, ReadOnlySpan<byte> payload, IBufferWriter<byte> writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        int payloadLength = payload.Length;
        if (payloadLength > MaxPayloadSize)
            throw new InvalidOperationException(
                $"Payload size {payloadLength} exceeds maximum limit of {MaxPayloadSize} bytes.");

        Span<byte> span = writer.GetSpan(HeaderSize + payloadLength);
        BinaryPrimitives.WriteUInt16LittleEndian(span[..2], HeaderMagic);
        BinaryPrimitives.WriteUInt16LittleEndian(span.Slice(2, 2), (ushort)opCode);
        BinaryPrimitives.WriteInt32LittleEndian(span.Slice(4, 4), payloadLength);
        BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(8, 4), requestId);

        if (payloadLength > 0)
        {
            payload.CopyTo(span[HeaderSize..]);
        }

        writer.Advance(HeaderSize + payloadLength);
    }

    public static byte[] Serialize(WillowmakerIpcMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);

        int payloadLength = message.Payload.Length;
        if (payloadLength > MaxPayloadSize)
            throw new InvalidOperationException(
                $"Payload size {payloadLength} exceeds maximum limit of {MaxPayloadSize} bytes.");

        byte[] buffer = new byte[HeaderSize + payloadLength];
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(0, 2), HeaderMagic);
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(2, 2), (ushort)message.OpCode);
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(4, 4), payloadLength);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(8, 4), message.RequestId);

        if (payloadLength > 0) message.Payload.CopyTo(buffer.AsSpan(HeaderSize));

        return buffer;
    }

    public static bool TryDeserialize(ReadOnlySpan<byte> buffer, out WillowmakerIpcMessage? message,
        out int bytesConsumed)
    {
        message = null;
        bytesConsumed = 0;

        if (buffer.Length < HeaderSize) return false;

        ushort magic = BinaryPrimitives.ReadUInt16LittleEndian(buffer[..2]);
        if (magic != HeaderMagic)
            throw new InvalidDataException($"Invalid IPC magic header: 0x{magic:X4}. Expected 0x{HeaderMagic:X4}.");

        IpcOpCode opCode = (IpcOpCode)BinaryPrimitives.ReadUInt16LittleEndian(buffer.Slice(2, 2));
        int payloadLength = BinaryPrimitives.ReadInt32LittleEndian(buffer.Slice(4, 4));

        if (payloadLength is < 0 or > MaxPayloadSize)
            throw new InvalidDataException($"Invalid IPC payload length: {payloadLength} bytes.");

        if (buffer.Length < HeaderSize + payloadLength) return false; // Incomplete packet, wait for more data

        uint requestId = BinaryPrimitives.ReadUInt32LittleEndian(buffer.Slice(8, 4));
        byte[] payload = payloadLength == 0 ? [] : buffer.Slice(HeaderSize, payloadLength).ToArray();
        bytesConsumed = HeaderSize + payloadLength;

        message = new WillowmakerIpcMessage
        {
            OpCode = opCode,
            RequestId = requestId,
            Payload = payload
        };

        return true;
    }

    public static bool TryDeserializeLegacy(ReadOnlySpan<byte> buffer, out WillowmakerIpcMessage? message,
        out int bytesConsumed)
    {
        message = null;
        bytesConsumed = 0;

        if (buffer.Length < LegacyHeaderSize) return false;

        ushort magic = BinaryPrimitives.ReadUInt16LittleEndian(buffer[..2]);
        if (magic != HeaderMagic)
            throw new InvalidDataException($"Invalid IPC magic header: 0x{magic:X4}. Expected 0x{HeaderMagic:X4}.");

        IpcOpCode opCode = (IpcOpCode)BinaryPrimitives.ReadUInt16LittleEndian(buffer.Slice(2, 2));
        int payloadLength = BinaryPrimitives.ReadInt32LittleEndian(buffer.Slice(4, 4));

        if (payloadLength is < 0 or > MaxPayloadSize)
            throw new InvalidDataException($"Invalid IPC payload length: {payloadLength} bytes.");

        if (buffer.Length < LegacyHeaderSize + payloadLength) return false;

        byte[] payload = payloadLength == 0 ? [] : buffer.Slice(LegacyHeaderSize, payloadLength).ToArray();
        bytesConsumed = LegacyHeaderSize + payloadLength;

        message = new WillowmakerIpcMessage
        {
            OpCode = opCode,
            RequestId = 0,
            Payload = payload
        };

        return true;
    }

    public static bool TryDeserializeLine(ReadOnlySpan<byte> buffer, out WillowmakerIpcMessage? message,
        out int bytesConsumed)
    {
        message = null;
        bytesConsumed = 0;

        int newlineIndex = buffer.IndexOf((byte)'\n');
        if (newlineIndex < 0)
        {
            if (buffer.Length > MaxPayloadSize)
                throw new InvalidDataException($"Line exceeds maximum payload size of {MaxPayloadSize} bytes.");
            return false;
        }

        if (newlineIndex > MaxPayloadSize)
            throw new InvalidDataException($"Line exceeds maximum payload size of {MaxPayloadSize} bytes.");

        ReadOnlySpan<byte> lineSpan = buffer[..newlineIndex];
        if (!lineSpan.IsEmpty && lineSpan[^1] == (byte)'\r')
            lineSpan = lineSpan[..^1];

        string text = Encoding.UTF8.GetString(lineSpan).Trim();
        bytesConsumed = newlineIndex + 1;

        message = CreateString(IpcOpCode.ExecuteCommand, text);
        return true;
    }

    public static bool TryDeserializeAny(ReadOnlySpan<byte> buffer, out WillowmakerIpcMessage? message,
        out int bytesConsumed)
    {
        message = null;
        bytesConsumed = 0;

        if (buffer.IsEmpty) return false;

        // 1. Binary frames starting with HeaderMagic (0x574D)
        if (buffer.Length >= 2 && BinaryPrimitives.ReadUInt16LittleEndian(buffer[..2]) == HeaderMagic)
        {
            if (buffer.Length < LegacyHeaderSize) return false;

            int payloadLength = BinaryPrimitives.ReadInt32LittleEndian(buffer.Slice(4, 4));
            if (payloadLength is < 0 or > MaxPayloadSize)
                throw new InvalidDataException($"Invalid IPC payload length: {payloadLength} bytes.");

            int legacyFrameSize = LegacyHeaderSize + payloadLength;
            int modernFrameSize = HeaderSize + payloadLength;

            bool hasLegacyNextMagic = buffer.Length >= legacyFrameSize + 2 &&
                                      BinaryPrimitives.ReadUInt16LittleEndian(buffer.Slice(legacyFrameSize, 2)) == HeaderMagic;
            bool hasModernNextMagic = buffer.Length >= modernFrameSize + 2 &&
                                      BinaryPrimitives.ReadUInt16LittleEndian(buffer.Slice(modernFrameSize, 2)) == HeaderMagic;
            bool hasLegacyNextLine = buffer.Length > legacyFrameSize &&
                                     buffer.Slice(legacyFrameSize).IndexOf((byte)'\n') >= 0;

            if (hasLegacyNextMagic && !hasModernNextMagic)
            {
                if (TryDeserializeLegacy(buffer, out message, out bytesConsumed))
                    return true;
            }
            else if (hasLegacyNextLine && !hasModernNextMagic)
            {
                if (TryDeserializeLegacy(buffer, out message, out bytesConsumed))
                    return true;
            }
            else if (hasModernNextMagic && !hasLegacyNextMagic)
            {
                if (TryDeserialize(buffer, out message, out bytesConsumed))
                    return true;
            }
            else
            {
                if (buffer.Length >= modernFrameSize)
                {
                    if (TryDeserialize(buffer, out message, out bytesConsumed))
                        return true;
                }
                else if (buffer.Length >= legacyFrameSize)
                {
                    if (TryDeserializeLegacy(buffer, out message, out bytesConsumed))
                        return true;
                }
            }

            return false; // Incomplete binary frame, wait for more bytes
        }

        // 2. Text-based newline-delimited command framing
        if (TryDeserializeLine(buffer, out message, out bytesConsumed))
            return true;

        if (buffer.Length > MaxPayloadSize)
            throw new InvalidDataException($"Incoming IPC stream ({buffer.Length} bytes) exceeded maximum frame capacity without valid delimiter.");

        return false;
    }
}
