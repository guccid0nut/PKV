// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ============================================================================
// File: SubTickCommandPacket.cs
// System: Kongor Core Netcode - Sub-Tick Binary Envelope Serialization
// Policy: Strict Explicit Typing (NO 'var')
// ============================================================================

using System.Buffers.Binary;

namespace ASPIRE.Common.Netcode;

/// <summary>
///     Sub-tick packet envelope header.
/// </summary>
public readonly record struct SubTickHeader(
    byte Version,
    uint ClientTick,
    byte EventCount,
    ushort Flags);

/// <summary>
///     High-performance, zero-allocation binary codec for Sub-Tick command datagrams.
///     Framing starts with magic 0x53554254 ("SUBT"), allowing the server proxy to identify
///     and extract sub-tick timing without interfering with legacy K2 packets.
/// </summary>
internal static class SubTickCommandPacket
{
    public const uint Magic = 0x54425553; // "SUBT" in Little Endian
    public const byte CurrentVersion = 1;
    public const int HeaderSize = 12;
    public const int MaxEventsPerPacket = 32;

    /// <summary>
    ///     Checks whether the datagram contains a valid Sub-Tick magic header.
    /// </summary>
    public static bool IsSubTickPacket(ReadOnlySpan<byte> datagram)
    {
        if (datagram.Length < HeaderSize)
        {
            return false;
        }

        uint magic = BinaryPrimitives.ReadUInt32LittleEndian(datagram);
        return magic == Magic;
    }

    /// <summary>
    ///     Serializes a batch of sub-tick events into the destination buffer with zero heap allocation.
    /// </summary>
    public static bool TryWrite(
        Span<byte> destination,
        uint clientTick,
        ReadOnlySpan<SubTickInputEvent> events,
        ushort flags,
        out int bytesWritten)
    {
        bytesWritten = 0;
        int requiredBytes = HeaderSize + (events.Length * SubTickInputEvent.BinarySize);

        if (events.Length > MaxEventsPerPacket || destination.Length < requiredBytes)
        {
            return false;
        }

        // Header (12 bytes)
        BinaryPrimitives.WriteUInt32LittleEndian(destination[0..4], Magic);
        destination[4] = CurrentVersion;
        BinaryPrimitives.WriteUInt32LittleEndian(destination[5..9], clientTick);
        destination[9] = (byte)events.Length;
        BinaryPrimitives.WriteUInt16LittleEndian(destination[10..12], flags);

        int offset = HeaderSize;
        for (int i = 0; i < events.Length; i++)
        {
            SubTickInputEvent evt = events[i];
            Span<byte> slice = destination.Slice(offset, SubTickInputEvent.BinarySize);

            BinaryPrimitives.WriteUInt32LittleEndian(slice[0..4], evt.Sequence);
            BinaryPrimitives.WriteUInt32LittleEndian(slice[4..8], evt.BaseTick);
            BinaryPrimitives.WriteSingleLittleEndian(slice[8..12], evt.SubTickOffset);
            BinaryPrimitives.WriteInt64LittleEndian(slice[12..20], evt.HardwareTimestampUs);
            slice[20] = (byte)evt.CommandType;
            slice[21] = evt.Flags;
            BinaryPrimitives.WriteUInt16LittleEndian(slice[22..24], evt.TargetEntityIndex);
            BinaryPrimitives.WriteSingleLittleEndian(slice[24..28], evt.TargetX);
            BinaryPrimitives.WriteSingleLittleEndian(slice[28..32], evt.TargetY);
            BinaryPrimitives.WriteSingleLittleEndian(slice[32..36], evt.TargetZ);

            offset += SubTickInputEvent.BinarySize;
        }

        bytesWritten = requiredBytes;
        return true;
    }

    /// <summary>
    ///     Parses a Sub-Tick datagram from raw bytes into an event buffer with zero heap allocation.
    /// </summary>
    public static bool TryParse(
        ReadOnlySpan<byte> source,
        out SubTickHeader header,
        Span<SubTickInputEvent> eventsOut,
        out int eventsRead)
    {
        header = default;
        eventsRead = 0;

        if (source.Length < HeaderSize)
        {
            return false;
        }

        uint magic = BinaryPrimitives.ReadUInt32LittleEndian(source[0..4]);
        if (magic != Magic)
        {
            return false;
        }

        byte version = source[4];
        if (version != CurrentVersion)
        {
            return false;
        }

        uint clientTick = BinaryPrimitives.ReadUInt32LittleEndian(source[5..9]);
        byte eventCount = source[9];
        ushort flags = BinaryPrimitives.ReadUInt16LittleEndian(source[10..12]);

        header = new SubTickHeader(version, clientTick, eventCount, flags);

        int expectedLength = HeaderSize + (eventCount * SubTickInputEvent.BinarySize);
        if (source.Length < expectedLength || eventsOut.Length < eventCount)
        {
            return false;
        }

        int offset = HeaderSize;
        for (int i = 0; i < eventCount; i++)
        {
            ReadOnlySpan<byte> slice = source.Slice(offset, SubTickInputEvent.BinarySize);

            uint seq = BinaryPrimitives.ReadUInt32LittleEndian(slice[0..4]);
            uint baseTick = BinaryPrimitives.ReadUInt32LittleEndian(slice[4..8]);
            float offsetFrac = BinaryPrimitives.ReadSingleLittleEndian(slice[8..12]);
            long tsUs = BinaryPrimitives.ReadInt64LittleEndian(slice[12..20]);
            SubTickCommandType cmdType = (SubTickCommandType)slice[20];
            byte evtFlags = slice[21];
            ushort targetIndex = BinaryPrimitives.ReadUInt16LittleEndian(slice[22..24]);
            float targetX = BinaryPrimitives.ReadSingleLittleEndian(slice[24..28]);
            float targetY = BinaryPrimitives.ReadSingleLittleEndian(slice[28..32]);
            float targetZ = BinaryPrimitives.ReadSingleLittleEndian(slice[32..36]);

            eventsOut[i] = new SubTickInputEvent(
                seq,
                baseTick,
                offsetFrac,
                tsUs,
                cmdType,
                evtFlags,
                targetIndex,
                targetX,
                targetY,
                targetZ,
                cameraYaw: 0.0f);

            offset += SubTickInputEvent.BinarySize;
        }

        eventsRead = eventCount;
        return true;
    }
}
