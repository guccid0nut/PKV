// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ============================================================================
// File: FecPacketHeader.cs
// System: Kongor Core Netcode - Zero-Allocation XOR Forward Error Correction Header
// Policy: Strict Explicit Typing (NO 'var')
// ============================================================================

using System.Buffers.Binary;

namespace ASPIRE.Common.Netcode;

/// <summary>
/// Compact binary header for XOR Forward Error Correction (FEC) datagram envelopes.
/// Base data packets: 8 bytes.
/// Parity packets: 8 bytes base + (BlockSize * 2) bytes Length-Map for exact payload truncation.
/// </summary>
public readonly struct FecPacketHeader
{
    public const byte FecMagic = 0xFE;
    public const byte FecVersion = 0x01;
    public const int DataHeaderSize = 8;
    public const byte DefaultBlockSize = 4;

    public byte Magic { get; }
    public byte Version { get; }
    public ushort GroupId { get; }
    public byte PacketIndex { get; }
    public byte BlockSize { get; }
    public ushort PayloadLength { get; }

    public bool IsParity => PacketIndex == BlockSize;
    public bool IsValid => Magic == FecMagic && Version == FecVersion && BlockSize > 0 && PacketIndex <= BlockSize;

    public FecPacketHeader(ushort groupId, byte packetIndex, byte blockSize, ushort payloadLength)
    {
        Magic = FecMagic;
        Version = FecVersion;
        GroupId = groupId;
        PacketIndex = packetIndex;
        BlockSize = blockSize;
        PayloadLength = payloadLength;
    }

    public static bool IsFecPacket(ReadOnlySpan<byte> buffer)
    {
        return buffer.Length >= DataHeaderSize && buffer[0] == FecMagic && buffer[1] == FecVersion;
    }

    public bool TryWriteDataHeader(Span<byte> destination, out int bytesWritten)
    {
        if (destination.Length < DataHeaderSize)
        {
            bytesWritten = 0;
            return false;
        }

        destination[0] = Magic;
        destination[1] = Version;
        BinaryPrimitives.WriteUInt16LittleEndian(destination.Slice(2, 2), GroupId);
        destination[4] = PacketIndex;
        destination[5] = BlockSize;
        BinaryPrimitives.WriteUInt16LittleEndian(destination.Slice(6, 2), PayloadLength);
        bytesWritten = DataHeaderSize;
        return true;
    }

    public bool TryWriteParityHeader(Span<byte> destination, ReadOnlySpan<ushort> lengths, out int bytesWritten)
    {
        int totalHeaderSize = DataHeaderSize + (BlockSize * 2);
        if (destination.Length < totalHeaderSize || lengths.Length < BlockSize)
        {
            bytesWritten = 0;
            return false;
        }

        destination[0] = Magic;
        destination[1] = Version;
        BinaryPrimitives.WriteUInt16LittleEndian(destination.Slice(2, 2), GroupId);
        destination[4] = PacketIndex;
        destination[5] = BlockSize;
        BinaryPrimitives.WriteUInt16LittleEndian(destination.Slice(6, 2), PayloadLength);

        for (int i = 0; i < BlockSize; i++)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(destination.Slice(DataHeaderSize + (i * 2), 2), lengths[i]);
        }

        bytesWritten = totalHeaderSize;
        return true;
    }

    public static bool TryParse(
        ReadOnlySpan<byte> source,
        out FecPacketHeader header,
        Span<ushort> outLengths,
        out int headerSize)
    {
        if (source.Length < DataHeaderSize || source[0] != FecMagic || source[1] != FecVersion)
        {
            header = default;
            headerSize = 0;
            return false;
        }

        ushort groupId = BinaryPrimitives.ReadUInt16LittleEndian(source.Slice(2, 2));
        byte packetIndex = source[4];
        byte blockSize = source[5];
        ushort payloadLength = BinaryPrimitives.ReadUInt16LittleEndian(source.Slice(6, 2));

        if (blockSize == 0 || packetIndex > blockSize)
        {
            header = default;
            headerSize = 0;
            return false;
        }

        header = new FecPacketHeader(groupId, packetIndex, blockSize, payloadLength);

        if (header.IsParity)
        {
            int requiredHeaderSize = DataHeaderSize + (blockSize * 2);
            if (source.Length < requiredHeaderSize)
            {
                headerSize = 0;
                return false;
            }

            int countToRead = Math.Min(outLengths.Length, (int)blockSize);
            for (int i = 0; i < countToRead; i++)
            {
                outLengths[i] = BinaryPrimitives.ReadUInt16LittleEndian(source.Slice(DataHeaderSize + (i * 2), 2));
            }

            headerSize = requiredHeaderSize;
            return true;
        }

        headerSize = DataHeaderSize;
        return true;
    }
}
