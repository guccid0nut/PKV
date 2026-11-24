// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ============================================================================
// File: VoiceDatagramPacket.cs
// System: ProjectKongor - Opus Voice Subsystem & Datagram Transport
// Policy: Strict Explicit Typing (NO 'var') - Zero Allocation Binary Serialization
// ============================================================================

using System;
using System.Buffers.Binary;

namespace Kongor.Core.Netcode.Voice;

/// <summary>
///     Identifies the routing scope and intended recipients for a voice datagram.
/// </summary>
public enum VoiceChannelType : byte
{
    /// <summary>Private voice channel between members of a queued or active lobby party.</summary>
    Party = 0,

    /// <summary>Full 5-player team match voice channel.</summary>
    Team = 1,

    /// <summary>Proximity / lane-partner voice channel (lane allies only).</summary>
    Lane = 2,

    /// <summary>Mentor-to-pupil direct communication channel.</summary>
    Mentor = 3
}

/// <summary>
///     Bit flags for voice datagram metadata.
/// </summary>
[Flags]
public enum VoicePacketFlags : byte
{
    None = 0,

    /// <summary>Marks the final frame of an utterance (microphone released / silence transition).</summary>
    EndOfTransmission = 1 << 0,

    /// <summary>Indicates packet loss concealment was requested or predicted.</summary>
    PacketLossConcealment = 1 << 1
}

/// <summary>
///     High-performance 16-byte binary header + variable Opus compressed payload datagram.
///     Header layout (16 bytes):
///     [0] Magic (0x56 'V')
///     [1] ChannelType (1B)
///     [2] Flags (1B)
///     [3] Reserved (1B)
///     [4..7] ChannelId (4B uint, little-endian)
///     [8..11] SenderAccountId (4B uint, little-endian)
///     [12..13] Sequence (2B ushort, little-endian)
///     [14..15] PayloadLength (2B ushort, little-endian)
///     [16..N] Opus compressed audio payload
/// </summary>
public readonly struct VoiceDatagramPacket : IEquatable<VoiceDatagramPacket>
{
    public const byte PacketMagic = 0x56; // ASCII 'V'
    public const int HeaderSize = 16;
    public const int MaxPayloadSize = 1276; // Max recommended MTU payload for single Opus frame

    public byte Magic { get; }
    public VoiceChannelType ChannelType { get; }
    public VoicePacketFlags Flags { get; }
    public uint ChannelId { get; }
    public uint SenderAccountId { get; }
    public ushort Sequence { get; }
    public ReadOnlyMemory<byte> Payload { get; }

    public VoiceDatagramPacket(
        VoiceChannelType channelType,
        uint channelId,
        uint senderAccountId,
        ushort sequence,
        ReadOnlyMemory<byte> payload,
        VoicePacketFlags flags = VoicePacketFlags.None)
    {
        Magic = PacketMagic;
        ChannelType = channelType;
        Flags = flags;
        ChannelId = channelId;
        SenderAccountId = senderAccountId;
        Sequence = sequence;
        Payload = payload;
    }

    private VoiceDatagramPacket(
        byte magic,
        VoiceChannelType channelType,
        VoicePacketFlags flags,
        uint channelId,
        uint senderAccountId,
        ushort sequence,
        ReadOnlyMemory<byte> payload)
    {
        Magic = magic;
        ChannelType = channelType;
        Flags = flags;
        ChannelId = channelId;
        SenderAccountId = senderAccountId;
        Sequence = sequence;
        Payload = payload;
    }

    /// <summary>
    ///     Writes the packet into the destination span. Destination must have at least HeaderSize + Payload.Length bytes.
    /// </summary>
    public int WriteTo(Span<byte> destination)
    {
        if (destination.Length < HeaderSize + Payload.Length)
        {
            throw new ArgumentException(
                $"Destination span of length {destination.Length} is too small for packet of total length {HeaderSize + Payload.Length}.",
                nameof(destination));
        }

        destination[0] = Magic;
        destination[1] = (byte)ChannelType;
        destination[2] = (byte)Flags;
        destination[3] = 0; // Reserved

        BinaryPrimitives.WriteUInt32LittleEndian(destination[4..8], ChannelId);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[8..12], SenderAccountId);
        BinaryPrimitives.WriteUInt16LittleEndian(destination[12..14], Sequence);
        BinaryPrimitives.WriteUInt16LittleEndian(destination[14..16], (ushort)Payload.Length);

        Payload.Span.CopyTo(destination[HeaderSize..]);
        return HeaderSize + Payload.Length;
    }

    /// <summary>
    ///     Serializes the packet into a newly allocated byte array.
    /// </summary>
    public byte[] ToByteArray()
    {
        byte[] buffer = new byte[HeaderSize + Payload.Length];
        WriteTo(buffer);
        return buffer;
    }

    /// <summary>
    ///     Attempts to parse a voice packet from a raw memory buffer without allocating heap memory for the payload.
    /// </summary>
    public static bool TryParse(ReadOnlyMemory<byte> buffer, out VoiceDatagramPacket packet)
    {
        packet = default;
        if (buffer.Length < HeaderSize)
        {
            return false;
        }

        ReadOnlySpan<byte> span = buffer.Span;
        if (span[0] != PacketMagic)
        {
            return false;
        }

        VoiceChannelType channelType = (VoiceChannelType)span[1];
        VoicePacketFlags flags = (VoicePacketFlags)span[2];
        uint channelId = BinaryPrimitives.ReadUInt32LittleEndian(span[4..8]);
        uint senderAccountId = BinaryPrimitives.ReadUInt32LittleEndian(span[8..12]);
        ushort sequence = BinaryPrimitives.ReadUInt16LittleEndian(span[12..14]);
        ushort payloadLength = BinaryPrimitives.ReadUInt16LittleEndian(span[14..16]);

        if (buffer.Length < HeaderSize + payloadLength)
        {
            return false;
        }

        ReadOnlyMemory<byte> payload = buffer.Slice(HeaderSize, payloadLength);
        packet = new VoiceDatagramPacket(
            PacketMagic,
            channelType,
            flags,
            channelId,
            senderAccountId,
            sequence,
            payload);

        return true;
    }

    public bool Equals(VoiceDatagramPacket other)
    {
        return Magic == other.Magic &&
               ChannelType == other.ChannelType &&
               Flags == other.Flags &&
               ChannelId == other.ChannelId &&
               SenderAccountId == other.SenderAccountId &&
               Sequence == other.Sequence &&
               Payload.Span.SequenceEqual(other.Payload.Span);
    }

    public override bool Equals(object? obj)
    {
        return obj is VoiceDatagramPacket other && Equals(other);
    }

    public static bool operator ==(VoiceDatagramPacket left, VoiceDatagramPacket right)
    {
        return left.Equals(right);
    }

    public static bool operator !=(VoiceDatagramPacket left, VoiceDatagramPacket right)
    {
        return !left.Equals(right);
    }

    public override int GetHashCode()
    {
        HashCode hash = new HashCode();
        hash.Add(Magic);
        hash.Add((byte)ChannelType);
        hash.Add((byte)Flags);
        hash.Add(ChannelId);
        hash.Add(SenderAccountId);
        hash.Add(Sequence);
        hash.Add(Payload.Length);
        return hash.ToHashCode();
    }
}
