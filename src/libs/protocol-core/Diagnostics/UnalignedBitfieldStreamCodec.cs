// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System;

namespace Kongor.Core.Diagnostics;

/// <summary>
/// Represents a dual <c>(ByteOffset, BitOffset)</c> bit-stream cursor supporting forward (<c>TopToBottom</c>)
/// and reversed (<c>BottomToTop</c>) bit advancement (<c>PatternLanguage/evaluator.cpp:57-83</c>).
/// </summary>
internal struct BitfieldCursor
{
    public int ByteOffset;
    public int BitOffset;

    public BitfieldCursor(int byteOffset, int bitOffset = 0)
    {
        ByteOffset = byteOffset + (bitOffset >> 3);
        BitOffset = bitOffset & 7;
    }

    /// <summary>
    /// Advances the cursor by <paramref name="bitCount"/> bits and returns the starting
    /// <c>(ByteOffset, BitOffset)</c> before advancement.
    /// </summary>
    public (int ByteOffset, int BitOffset) GetAndAdvance(int bitCount, bool reversed = false)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(bitCount);

        if (!reversed)
        {
            int startByte = ByteOffset;
            int startBit = BitOffset;

            int totalBits = (ByteOffset * 8) + BitOffset + bitCount;
            ByteOffset = totalBits / 8;
            BitOffset = totalBits % 8;

            return (startByte, startBit);
        }
        else
        {
            int totalBits = (ByteOffset * 8) + BitOffset - bitCount;
            if (totalBits < 0)
            {
                throw new InvalidOperationException("BitfieldCursor underflowed below bit offset 0 during reversed advancement.");
            }

            ByteOffset = totalBits / 8;
            BitOffset = totalBits % 8;
            return (ByteOffset, BitOffset);
        }
    }
}

/// <summary>
/// Phase 13 (#159): PatternLanguage Unaligned Bit-Stream Reader &amp; Non-Destructive Read-Modify-Write
/// Bit-Field Writer with Endian &amp; Bit-Order Reversal (<c>PatternLanguage/evaluator.cpp:57-146</c> port).
/// Uses 128-bit (<see cref="UInt128"/>) spanning windows so up to 64-bit fields starting at any unaligned
/// bit offset <c>0..7</c> can be read or patched without clobbering adjacent bits.
/// </summary>
internal static class UnalignedBitfieldStreamCodec
{
    /// <summary>
    /// Reads <paramref name="bitCount"/> bits (1..64) starting at <c>(byteOffset, bitOffset)</c> from <paramref name="buffer"/>.
    /// </summary>
    public static ulong ReadBits(
        ReadOnlySpan<byte> buffer,
        int byteOffset,
        int bitOffset,
        int bitCount,
        bool littleEndian = true,
        bool bitReversed = false)
    {
        if (bitCount < 1 || bitCount > 64)
        {
            throw new ArgumentOutOfRangeException(nameof(bitCount), "Bit count must be in [1, 64].");
        }

        if (byteOffset < 0 || bitOffset < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(byteOffset), "Byte and bit offsets must be non-negative.");
        }

        byteOffset += bitOffset / 8;
        bitOffset %= 8;

        int touchedBytes = (bitOffset + bitCount + 7) / 8;
        if (byteOffset + touchedBytes > buffer.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(byteOffset), $"Bitfield read of {bitCount} bits at ({byteOffset}, {bitOffset}) exceeds buffer length {buffer.Length}.");
        }

        UInt128 window = ReadWindow128(buffer.Slice(byteOffset, touchedBytes), littleEndian, bitReversed);
        UInt128 mask = bitCount == 64
            ? (UInt128)ulong.MaxValue
            : ((UInt128.One << bitCount) - UInt128.One);

        UInt128 extracted = (window >> bitOffset) & mask;
        return (ulong)extracted;
    }

    /// <summary>
    /// Reads a signed bitfield of <paramref name="bitCount"/> bits (1..64) and sign-extends the top bit.
    /// </summary>
    public static long ReadSignedBits(
        ReadOnlySpan<byte> buffer,
        int byteOffset,
        int bitOffset,
        int bitCount,
        bool littleEndian = true,
        bool bitReversed = false)
    {
        ulong raw = ReadBits(buffer, byteOffset, bitOffset, bitCount, littleEndian, bitReversed);
        return BinaryCustomFloatAndLeb128Codec.SignExtend(raw, bitCount);
    }

    /// <summary>
    /// Non-destructively writes the lowest <paramref name="bitCount"/> bits of <paramref name="value"/> into
    /// <paramref name="buffer"/> at <c>(byteOffset, bitOffset)</c>, preserving all surrounding bits in the
    /// leading and trailing bytes (<c>evaluator.cpp:121-146</c>).
    /// </summary>
    public static void WriteBits(
        Span<byte> buffer,
        int byteOffset,
        int bitOffset,
        int bitCount,
        ulong value,
        bool littleEndian = true,
        bool bitReversed = false)
    {
        if (bitCount < 1 || bitCount > 64)
        {
            throw new ArgumentOutOfRangeException(nameof(bitCount), "Bit count must be in [1, 64].");
        }

        if (byteOffset < 0 || bitOffset < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(byteOffset), "Byte and bit offsets must be non-negative.");
        }

        byteOffset += bitOffset / 8;
        bitOffset %= 8;

        int touchedBytes = (bitOffset + bitCount + 7) / 8;
        if (byteOffset + touchedBytes > buffer.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(byteOffset), $"Bitfield write of {bitCount} bits at ({byteOffset}, {bitOffset}) exceeds buffer length {buffer.Length}.");
        }

        Span<byte> targetSlice = buffer.Slice(byteOffset, touchedBytes);
        UInt128 window = ReadWindow128(targetSlice, littleEndian, bitReversed);

        UInt128 fieldMask = bitCount == 64
            ? (UInt128)ulong.MaxValue
            : ((UInt128.One << bitCount) - UInt128.One);
        UInt128 shiftedMask = fieldMask << bitOffset;
        UInt128 shiftedValue = ((UInt128)value & fieldMask) << bitOffset;

        window = (window & ~shiftedMask) | shiftedValue;
        WriteWindow128(targetSlice, window, littleEndian, bitReversed);
    }

    private static UInt128 ReadWindow128(ReadOnlySpan<byte> slice, bool littleEndian, bool bitReversed)
    {
        UInt128 acc = UInt128.Zero;
        int len = slice.Length;

        for (int i = 0; i < len; i++)
        {
            int srcIdx = littleEndian ? i : (len - 1 - i);
            byte b = slice[srcIdx];
            if (bitReversed)
            {
                b = ReverseByteBits(b);
            }

            acc |= (UInt128)b << (i * 8);
        }

        return acc;
    }

    private static void WriteWindow128(Span<byte> slice, UInt128 window, bool littleEndian, bool bitReversed)
    {
        int len = slice.Length;
        for (int i = 0; i < len; i++)
        {
            byte b = (byte)((window >> (i * 8)) & 0xFF);
            if (bitReversed)
            {
                b = ReverseByteBits(b);
            }

            int dstIdx = littleEndian ? i : (len - 1 - i);
            slice[dstIdx] = b;
        }
    }

    public static byte ReverseByteBits(byte x)
    {
        x = (byte)(((x & 0xF0) >> 4) | ((x & 0x0F) << 4));
        x = (byte)(((x & 0xCC) >> 2) | ((x & 0x33) << 2));
        x = (byte)(((x & 0xAA) >> 1) | ((x & 0x55) << 1));
        return x;
    }
}
