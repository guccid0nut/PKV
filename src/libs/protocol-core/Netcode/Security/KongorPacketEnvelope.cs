// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ============================================================================
// File: KongorPacketEnvelope.cs
// System: Kongor Core Netcode - Modern Authenticated Transport Envelope
// Policy: Strict Explicit Typing (NO 'var')
// ============================================================================

using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace ASPIRE.Common.Netcode;

/// <summary>
///     Blittable 21-byte wire header for modern authenticated transport datagrams.
///     Contains sequence tracking, sub-tick microsecond delta, and bidirectional ACK echo for BBR pacing.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct KongorPacketHeader : IEquatable<KongorPacketHeader>
{
    public const byte ExpectedMagic = 0x80;

    public byte Magic;
    public uint SessionId;
    public uint PacketSequence;
    public uint SubTickDeltaUs;
    public uint AckSequence;
    public uint AckTimestampEchoUs;

    public KongorPacketHeader(
        uint sessionId,
        uint packetSequence,
        uint subTickDeltaUs,
        uint ackSequence = 0,
        uint ackTimestampEchoUs = 0)
    {
        Magic = ExpectedMagic;
        SessionId = sessionId;
        PacketSequence = packetSequence;
        SubTickDeltaUs = subTickDeltaUs;
        AckSequence = ackSequence;
        AckTimestampEchoUs = ackTimestampEchoUs;
    }

    public readonly bool Equals(KongorPacketHeader other)
    {
        return Magic == other.Magic &&
               SessionId == other.SessionId &&
               PacketSequence == other.PacketSequence &&
               SubTickDeltaUs == other.SubTickDeltaUs &&
               AckSequence == other.AckSequence &&
               AckTimestampEchoUs == other.AckTimestampEchoUs;
    }

    public override readonly bool Equals(object? obj) => obj is KongorPacketHeader other && Equals(other);

    public override readonly int GetHashCode()
    {
        HashCode hash = new();
        hash.Add(Magic);
        hash.Add(SessionId);
        hash.Add(PacketSequence);
        hash.Add(SubTickDeltaUs);
        hash.Add(AckSequence);
        hash.Add(AckTimestampEchoUs);
        return hash.ToHashCode();
    }

    public static bool operator ==(KongorPacketHeader left, KongorPacketHeader right) => left.Equals(right);
    public static bool operator !=(KongorPacketHeader left, KongorPacketHeader right) => !left.Equals(right);
}

/// <summary>
///     High-performance zero-allocation binary codec for modern transport envelopes.
///     Wire format:
///     [0..20]  : 21-byte KongorPacketHeader (Little Endian, Authenticated as AAD)
///     [21..N-16]: Ciphertext payload
///     [N-16..N]: 16-byte Poly1305 MAC tag
/// </summary>
// shortcut: 21-byte authenticated envelope protocol tested in isolation but inactive for retail K2 UDP traffic, enable in client launcher or native guard proxy when end-to-end encrypted packet envelopes are rolled out
public static class KongorPacketEnvelope
{
    public const byte MagicByte = KongorPacketHeader.ExpectedMagic;
    public const int HeaderSize = 21;
    public const int TagSize = 16;
    public const int TotalOverhead = HeaderSize + TagSize; // 37 bytes

    /// <summary>
    ///     Checks whether the datagram contains a modern envelope flag (bit 7 set) and satisfies minimum length.
    /// </summary>
    public static bool IsModernEnvelope(ReadOnlySpan<byte> datagram)
    {
        return datagram.Length >= TotalOverhead && datagram[0] == MagicByte;
    }

    /// <summary>
    ///     Writes the 21-byte header into the target buffer in strict little-endian format.
    /// </summary>
    public static bool TryWriteHeader(Span<byte> destination, in KongorPacketHeader header)
    {
        if (destination.Length < HeaderSize)
        {
            return false;
        }

        destination[0] = header.Magic;
        BinaryPrimitives.WriteUInt32LittleEndian(destination[1..5], header.SessionId);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[5..9], header.PacketSequence);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[9..13], header.SubTickDeltaUs);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[13..17], header.AckSequence);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[17..21], header.AckTimestampEchoUs);

        return true;
    }

    /// <summary>
    ///     Reads the 21-byte header from the buffer in little-endian format.
    /// </summary>
    public static bool TryReadHeader(ReadOnlySpan<byte> source, out KongorPacketHeader header)
    {
        if (source.Length < HeaderSize || source[0] != MagicByte)
        {
            header = default;
            return false;
        }

        byte magic = source[0];
        uint sessionId = BinaryPrimitives.ReadUInt32LittleEndian(source[1..5]);
        uint packetSequence = BinaryPrimitives.ReadUInt32LittleEndian(source[5..9]);
        uint subTickDeltaUs = BinaryPrimitives.ReadUInt32LittleEndian(source[9..13]);
        uint ackSequence = BinaryPrimitives.ReadUInt32LittleEndian(source[13..17]);
        uint ackTimestampEchoUs = BinaryPrimitives.ReadUInt32LittleEndian(source[17..21]);

        header = new KongorPacketHeader
        {
            Magic = magic,
            SessionId = sessionId,
            PacketSequence = packetSequence,
            SubTickDeltaUs = subTickDeltaUs,
            AckSequence = ackSequence,
            AckTimestampEchoUs = ackTimestampEchoUs
        };

        return true;
    }

    /// <summary>
    ///     Wraps header, ciphertext, and tag into an authenticated datagram.
    /// </summary>
    public static bool TryWrap(
        Span<byte> destination,
        in KongorPacketHeader header,
        ReadOnlySpan<byte> ciphertext,
        ReadOnlySpan<byte> tag,
        out int bytesWritten)
    {
        bytesWritten = 0;
        int requiredLength = HeaderSize + ciphertext.Length + TagSize;

        if (tag.Length != TagSize || destination.Length < requiredLength)
        {
            return false;
        }

        if (!TryWriteHeader(destination[..HeaderSize], in header))
        {
            return false;
        }

        ciphertext.CopyTo(destination[HeaderSize..(HeaderSize + ciphertext.Length)]);
        tag.CopyTo(destination[(HeaderSize + ciphertext.Length)..requiredLength]);

        bytesWritten = requiredLength;
        return true;
    }

    /// <summary>
    ///     Unwraps an authenticated datagram into its header, ciphertext slice, and Poly1305 tag slice.
    /// </summary>
    public static bool TryUnwrap(
        ReadOnlySpan<byte> datagram,
        out KongorPacketHeader header,
        out ReadOnlySpan<byte> ciphertext,
        out ReadOnlySpan<byte> tag)
    {
        if (!IsModernEnvelope(datagram))
        {
            header = default;
            ciphertext = ReadOnlySpan<byte>.Empty;
            tag = ReadOnlySpan<byte>.Empty;
            return false;
        }

        if (!TryReadHeader(datagram[..HeaderSize], out header))
        {
            ciphertext = ReadOnlySpan<byte>.Empty;
            tag = ReadOnlySpan<byte>.Empty;
            return false;
        }

        int payloadLen = datagram.Length - TotalOverhead;
        ciphertext = datagram.Slice(HeaderSize, payloadLen);
        tag = datagram.Slice(HeaderSize + payloadLen, TagSize);

        return true;
    }
}
