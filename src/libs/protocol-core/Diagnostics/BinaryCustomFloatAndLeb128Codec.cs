// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System;
using System.Buffers.Binary;

namespace Kongor.Core.Diagnostics;

/// <summary>
/// Phase 12 (#149: imhex-custom-float-and-leb128-codec):
/// Arbitrary custom floating-point decoder (customFloatToFloat32&lt;E,M&gt;), fixed-point (fpM.N)
/// decoder, bitfield extractor &amp; sign-extender, and bidirectional ULEB128 / SLEB128 variable-length
/// integer codec adapted from ImHex utils.hpp and data_inspector.cpp.
/// </summary>
public static class BinaryCustomFloatAndLeb128Codec
{
    /// <summary>
    /// Converts an arbitrary custom floating-point bit pattern (with configurable exponentBits,
    /// mantissaBits, and optional sign bit) into an IEEE-754 single-precision float.
    /// Supports GPU/K2 formats such as BFloat16 (E=8, M=7), FP16 (E=5, M=10), FP24 (E=7, M=16),
    /// R11G11B10_FLOAT (E=5, M=6 / E=5, M=5, hasSign=false).
    /// </summary>
    public static float CustomFloatToFloat32(ulong rawBits, int exponentBits, int mantissaBits, bool hasSign = true)
    {
        if (exponentBits <= 0 || exponentBits > 8)
        {
            throw new ArgumentOutOfRangeException(nameof(exponentBits), "exponentBits must be in [1, 8].");
        }

        if (mantissaBits <= 0 || mantissaBits > 23)
        {
            throw new ArgumentOutOfRangeException(nameof(mantissaBits), "mantissaBits must be in [1, 23].");
        }

        ulong mantissaMask = (1UL << mantissaBits) - 1UL;
        ulong exponentMask = (1UL << exponentBits) - 1UL;

        uint mantissa = (uint)(rawBits & mantissaMask);
        uint exponent = (uint)((rawBits >> mantissaBits) & exponentMask);
        uint sign = hasSign ? (uint)((rawBits >> (exponentBits + mantissaBits)) & 1UL) : 0U;

        int customBias = (1 << (exponentBits - 1)) - 1;
        const int ieee754Bias = 127;
        int mantissaShift = 23 - mantissaBits;

        uint outSign = sign << 31;
        uint outExponent;
        uint outMantissa;

        if (exponent == exponentMask)
        {
            // Infinity or NaN
            outExponent = 0xFFU;
            outMantissa = mantissa != 0 ? (mantissa << mantissaShift) | 0x400000U : 0U;
        }
        else if (exponent == 0)
        {
            if (mantissa == 0)
            {
                // Signed zero
                outExponent = 0U;
                outMantissa = 0U;
            }
            else
            {
                // Subnormal: normalize by shifting mantissa until the leading 1 reaches bit mantissaBits
                int expVal = 1 - customBias;
                uint normMantissa = mantissa;
                while ((normMantissa & (1U << mantissaBits)) == 0U)
                {
                    normMantissa <<= 1;
                    expVal--;
                }

                normMantissa &= (uint)mantissaMask;
                int biasedIeee = expVal + ieee754Bias;
                if (biasedIeee <= 0)
                {
                    outExponent = 0U;
                    outMantissa = 0U;
                }
                else
                {
                    outExponent = (uint)biasedIeee;
                    outMantissa = normMantissa << mantissaShift;
                }
            }
        }
        else
        {
            int biasedIeee = ((int)exponent - customBias) + ieee754Bias;
            if (biasedIeee <= 0)
            {
                outExponent = 0U;
                outMantissa = 0U;
            }
            else if (biasedIeee >= 0xFF)
            {
                outExponent = 0xFFU;
                outMantissa = 0U;
            }
            else
            {
                outExponent = (uint)biasedIeee;
                outMantissa = mantissa << mantissaShift;
            }
        }

        uint ieeeBits = outSign | (outExponent << 23) | (outMantissa & 0x7FFFFFU);
        return BitConverter.UInt32BitsToSingle(ieeeBits);
    }

    /// <summary>
    /// Decodes a signed or unsigned fixed-point integer with the specified number of fractional bits (e.g. Q16.16 -> fractionalBits = 16).
    /// </summary>
    public static double DecodeFixedPoint(long rawValue, int fractionalBits)
    {
        if (fractionalBits < 0 || fractionalBits > 62)
        {
            throw new ArgumentOutOfRangeException(nameof(fractionalBits), "fractionalBits must be in [0, 62].");
        }

        double scale = 1UL << fractionalBits;
        return rawValue / scale;
    }

    /// <summary>
    /// Extracts a bitfield of width [1..64] starting at bitOffset from a 64-bit unsigned value.
    /// </summary>
    public static ulong ExtractBits(ulong value, int bitOffset, int bitWidth)
    {
        if (bitOffset < 0 || bitOffset >= 64)
        {
            throw new ArgumentOutOfRangeException(nameof(bitOffset), "bitOffset must be in [0, 63].");
        }

        if (bitWidth <= 0 || bitOffset + bitWidth > 64)
        {
            throw new ArgumentOutOfRangeException(nameof(bitWidth), "bitWidth must be in [1, 64 - bitOffset].");
        }

        if (bitWidth == 64)
        {
            return value;
        }

        ulong mask = (1UL << bitWidth) - 1UL;
        return (value >> bitOffset) & mask;
    }

    /// <summary>
    /// Sign-extends an extracted bitfield of width bitWidth [1..64] to a signed 64-bit integer.
    /// </summary>
    public static long SignExtend(ulong value, int bitWidth)
    {
        if (bitWidth <= 0 || bitWidth > 64)
        {
            throw new ArgumentOutOfRangeException(nameof(bitWidth), "bitWidth must be in [1, 64].");
        }

        if (bitWidth == 64)
        {
            return (long)value;
        }

        int shift = 64 - bitWidth;
        return ((long)(value << shift)) >> shift;
    }

    /// <summary>
    /// Encodes an unsigned 64-bit integer into Unsigned LEB128 (ULEB128) variable-length bytes.
    /// Returns the number of bytes written (1..10).
    /// </summary>
    public static int EncodeUleb128(ulong value, Span<byte> destination)
    {
        int index = 0;
        do
        {
            if (index >= destination.Length)
            {
                throw new ArgumentException("Destination span is too small for ULEB128 encoding.", nameof(destination));
            }

            byte byteVal = (byte)(value & 0x7FUL);
            value >>= 7;
            if (value != 0UL)
            {
                byteVal |= 0x80;
            }

            destination[index++] = byteVal;
        }
        while (value != 0UL);

        return index;
    }

    /// <summary>
    /// Decodes an Unsigned LEB128 (ULEB128) integer from a byte span.
    /// </summary>
    public static bool TryDecodeUleb128(ReadOnlySpan<byte> source, out ulong value, out int bytesConsumed)
    {
        value = 0UL;
        bytesConsumed = 0;
        int shift = 0;

        for (int i = 0; i < source.Length && i < 10; i++)
        {
            byte b = source[i];
            bytesConsumed++;

            if (i == 9 && (b & 0xFE) != 0)
            {
                // Overflow beyond 64 bits
                return false;
            }

            value |= (ulong)(b & 0x7F) << shift;
            if ((b & 0x80) == 0)
            {
                return true;
            }

            shift += 7;
        }

        return false;
    }

    /// <summary>
    /// Encodes a signed 64-bit integer into Signed LEB128 (SLEB128) variable-length bytes.
    /// Returns the number of bytes written (1..10).
    /// </summary>
    public static int EncodeSleb128(long value, Span<byte> destination)
    {
        int index = 0;
        bool more = true;

        while (more)
        {
            if (index >= destination.Length)
            {
                throw new ArgumentException("Destination span is too small for SLEB128 encoding.", nameof(destination));
            }

            byte byteVal = (byte)(value & 0x7FL);
            value >>= 7;

            bool signBitSet = (byteVal & 0x40) != 0;
            if ((value == 0L && !signBitSet) || (value == -1L && signBitSet))
            {
                more = false;
            }
            else
            {
                byteVal |= 0x80;
            }

            destination[index++] = byteVal;
        }

        return index;
    }

    /// <summary>
    /// Decodes a Signed LEB128 (SLEB128) integer from a byte span.
    /// </summary>
    public static bool TryDecodeSleb128(ReadOnlySpan<byte> source, out long value, out int bytesConsumed)
    {
        value = 0L;
        bytesConsumed = 0;
        int shift = 0;
        byte lastByte = 0;

        for (int i = 0; i < source.Length && i < 10; i++)
        {
            lastByte = source[i];
            bytesConsumed++;

            value |= (long)(lastByte & 0x7F) << shift;
            shift += 7;

            if ((lastByte & 0x80) == 0)
            {
                if (shift < 64 && (lastByte & 0x40) != 0)
                {
                    value |= -(1L << shift);
                }

                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Phase 13 (#158): Inverse custom float quantizer (<c>ieee_decoder.cpp:349-471</c>) converting an IEEE-754
    /// <see cref="double"/> into an arbitrary <c>(1 sign, exponentBits, mantissaBits)</c> bit representation
    /// using round-to-nearest-even, fixing upstream ImHex's mantissa rounding carry-overflow bug (<c>L427</c>)
    /// and preserving signaling vs. quiet NaN payload bits (<c>L374</c>).
    /// </summary>
    public static ulong FloatToCustomBits(double value, int exponentBits, int mantissaBits)
    {
        if (exponentBits < 2 || exponentBits > 15)
        {
            throw new ArgumentOutOfRangeException(nameof(exponentBits), "Exponent bits must be in [2, 15].");
        }

        if (mantissaBits < 1 || mantissaBits > 52)
        {
            throw new ArgumentOutOfRangeException(nameof(mantissaBits), "Mantissa bits must be in [1, 52].");
        }

        ulong rawDouble = BitConverter.DoubleToUInt64Bits(value);
        ulong signBit = (rawDouble >> 63) & 1UL;
        int signShift = exponentBits + mantissaBits;
        ulong signMask = signBit << signShift;

        long maxExponent = (1L << exponentBits) - 1L;
        long targetBias = (1L << (exponentBits - 1)) - 1L;
        ulong mantissaLimit = 1UL << mantissaBits;

        if (double.IsNaN(value))
        {
            ulong isQuietNan = (rawDouble >> 51) & 1UL;
            int payloadShift = Math.Max(0, 52 - mantissaBits);
            ulong nanPayload = (rawDouble >> payloadShift) & (mantissaLimit - 1UL);
            ulong quietBitMask = 1UL << (mantissaBits - 1);

            if (isQuietNan != 0UL)
            {
                nanPayload |= quietBitMask;
            }
            else
            {
                nanPayload &= ~quietBitMask;
                if (nanPayload == 0UL)
                {
                    nanPayload = 1UL; // Ensure signaling NaN has non-zero mantissa so it does not become Infinity
                }
            }

            return signMask | ((ulong)maxExponent << mantissaBits) | nanPayload;
        }

        if (double.IsInfinity(value))
        {
            return signMask | ((ulong)maxExponent << mantissaBits);
        }

        ulong doubleExp = (rawDouble >> 52) & 0x7FFUL;
        ulong doubleFrac = rawDouble & ((1UL << 52) - 1UL);

        if (doubleExp == 0UL && doubleFrac == 0UL)
        {
            return signMask;
        }

        long unbiasedExp;
        ulong sig53;
        if (doubleExp == 0UL)
        {
            // Normalize double subnormal
            unbiasedExp = -1022L;
            sig53 = doubleFrac;
            while ((sig53 & (1UL << 52)) == 0UL)
            {
                sig53 <<= 1;
                unbiasedExp--;
            }
        }
        else
        {
            unbiasedExp = (long)doubleExp - 1023L;
            sig53 = (1UL << 52) | doubleFrac;
        }

        long targetExp = unbiasedExp + targetBias;
        if (targetExp >= maxExponent)
        {
            // Overflow to signed Infinity
            return signMask | ((ulong)maxExponent << mantissaBits);
        }

        if (targetExp > 0L)
        {
            ulong frac52 = sig53 & ((1UL << 52) - 1UL);
            ulong mantissaField;
            if (mantissaBits >= 52)
            {
                mantissaField = frac52 << (mantissaBits - 52);
            }
            else
            {
                int shift = 52 - mantissaBits;
                mantissaField = frac52 >> shift;
                ulong roundBit = (frac52 >> (shift - 1)) & 1UL;
                ulong stickyMask = shift > 1 ? ((1UL << (shift - 1)) - 1UL) : 0UL;
                bool stickyBit = (frac52 & stickyMask) != 0UL;

                if (roundBit != 0UL && (stickyBit || (mantissaField & 1UL) != 0UL))
                {
                    mantissaField++;
                    // Fix for upstream ImHex ieee_decoder.cpp:427: carry into exponent on mantissa overflow
                    if (mantissaField >= mantissaLimit)
                    {
                        mantissaField = 0UL;
                        targetExp++;
                        if (targetExp >= maxExponent)
                        {
                            return signMask | ((ulong)maxExponent << mantissaBits);
                        }
                    }
                }
            }

            return signMask | ((ulong)targetExp << mantissaBits) | mantissaField;
        }

        // Subnormal or underflow in target custom float
        int subnormalShift = (52 - mantissaBits) + (int)(1L - targetExp);
        if (subnormalShift > 53 || subnormalShift <= 0)
        {
            return signMask;
        }

        ulong subMantissa = sig53 >> subnormalShift;
        ulong subRoundBit = (sig53 >> (subnormalShift - 1)) & 1UL;
        ulong subStickyMask = subnormalShift > 1 ? ((1UL << (subnormalShift - 1)) - 1UL) : 0UL;
        bool subStickyBit = (sig53 & subStickyMask) != 0UL;

        if (subRoundBit != 0UL && (subStickyBit || (subMantissa & 1UL) != 0UL))
        {
            subMantissa++;
            if (subMantissa >= mantissaLimit)
            {
                // Promoted from max subnormal to smallest normal (biased exponent = 1, mantissa = 0)
                return signMask | (1UL << mantissaBits);
            }
        }

        return signMask | subMantissa;
    }

    /// <summary>
    /// Reads an unsigned 24-bit little-endian integer (<c>u24</c>, <c>data_inspector.cpp:213-220</c>).
    /// </summary>
    public static uint ReadUInt24LittleEndian(ReadOnlySpan<byte> source)
    {
        if (source.Length < 3)
        {
            throw new ArgumentException("Source span must contain at least 3 bytes for u24.", nameof(source));
        }

        return (uint)(source[0] | (source[1] << 8) | (source[2] << 16));
    }

    /// <summary>
    /// Reads a signed 24-bit little-endian integer (<c>i24</c>) with sign extension from bit 23.
    /// </summary>
    public static int ReadInt24LittleEndian(ReadOnlySpan<byte> source)
    {
        uint raw = ReadUInt24LittleEndian(source);
        return (int)SignExtend(raw, 24);
    }

    /// <summary>
    /// Reads an unsigned 48-bit little-endian integer (<c>u48</c>, <c>data_inspector.cpp:227-242</c>).
    /// </summary>
    public static ulong ReadUInt48LittleEndian(ReadOnlySpan<byte> source)
    {
        if (source.Length < 6)
        {
            throw new ArgumentException("Source span must contain at least 6 bytes for u48.", nameof(source));
        }

        return (ulong)source[0]
               | ((ulong)source[1] << 8)
               | ((ulong)source[2] << 16)
               | ((ulong)source[3] << 24)
               | ((ulong)source[4] << 32)
               | ((ulong)source[5] << 40);
    }

    /// <summary>
    /// Reads a signed 48-bit little-endian integer (<c>i48</c>) with sign extension from bit 47.
    /// </summary>
    public static long ReadInt48LittleEndian(ReadOnlySpan<byte> source)
    {
        ulong raw = ReadUInt48LittleEndian(source);
        return SignExtend(raw, 48);
    }

    /// <summary>
    /// Decodes and validates a 16-bit MS-DOS Date and 16-bit MS-DOS Time pair (<c>data_inspector.cpp:813-848</c>)
    /// as used in <c>.s2z</c> ZIP local and central directory headers.
    /// </summary>
    public static bool TryDecodeDosDateTime(ushort dosDate, ushort dosTime, out DateTime result)
    {
        result = default;

        int day = dosDate & 0x1F;
        int month = (dosDate >> 5) & 0x0F;
        int year = ((dosDate >> 9) & 0x7F) + 1980;

        int second = (dosTime & 0x1F) * 2;
        int minute = (dosTime >> 5) & 0x3F;
        int hour = (dosTime >> 11) & 0x1F;

        if (month < 1 || month > 12 || day < 1 || day > DateTime.DaysInMonth(year, month))
        {
            return false;
        }

        if (hour > 23 || minute > 59 || second > 59)
        {
            return false;
        }

        result = new DateTime(year, month, day, hour, minute, second, DateTimeKind.Utc);
        return true;
    }

    /// <summary>
    /// Validates whether a 16-byte span represents a structurally conformant RFC-4122 GUID
    /// (<c>data_inspector.cpp:850-896</c>), extracting its version (1..5) and variant field.
    /// </summary>
    public static bool IsValidRfc4122Guid(ReadOnlySpan<byte> bytes16, out int version, out byte variantHighBits)
    {
        version = 0;
        variantHighBits = 0;

        if (bytes16.Length < 16)
        {
            return false;
        }

        // Mixed-endian Microsoft/RFC-4122 layout:
        // byte[7] upper nibble holds version (1..5) in little-endian Data3, or byte[6] in big-endian RFC layout.
        // We check the standard Microsoft GUID layout (where Data3 is little-endian uint16 at bytes 6..7, so version is high nibble of byte 7).
        version = (bytes16[7] >> 4) & 0x0F;
        variantHighBits = (byte)((bytes16[8] >> 6) & 0x03);

        // RFC-4122 variant requires bits 7..6 of byte[8] to be 0b10 (value 2) and version in 1..5 (or 6..8 for RFC-9562)
        return variantHighBits == 0b10 && version >= 1 && version <= 8;
    }
}

