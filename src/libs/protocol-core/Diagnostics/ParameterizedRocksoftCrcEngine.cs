// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System;

namespace Kongor.Core.Diagnostics;

/// <summary>
/// Phase 13 (#161): Generalized Parameterized Rocksoft™ Model CRC Engine (<c>Crc&lt;8|16|32|64&gt;</c>)
/// &amp; Branchless 3-Stage Bit Reflector (<c>crypto.cpp:51-162</c> &amp; <c>hashes.cpp:53-110</c> port).
/// </summary>
public sealed class ParameterizedRocksoftCrcEngine
{
    private readonly int _bitWidth;
    private readonly ulong _polynomial;
    private readonly ulong _initialValue;
    private readonly ulong _xorOut;
    private readonly bool _reflectInput;
    private readonly bool _reflectOutput;
    private readonly ulong _widthMask;
    private readonly ulong[] _table;

    public static ParameterizedRocksoftCrcEngine Crc8Standard { get; } = new ParameterizedRocksoftCrcEngine(
        bitWidth: 8,
        polynomial: 0x07UL,
        initialValue: 0x00UL,
        xorOut: 0x00UL,
        reflectInput: false,
        reflectOutput: false);

    public static ParameterizedRocksoftCrcEngine Crc16CcittFalse { get; } = new ParameterizedRocksoftCrcEngine(
        bitWidth: 16,
        polynomial: 0x1021UL,
        initialValue: 0xFFFFUL,
        xorOut: 0x0000UL,
        reflectInput: false,
        reflectOutput: false);

    public static ParameterizedRocksoftCrcEngine Crc16Arc { get; } = new ParameterizedRocksoftCrcEngine(
        bitWidth: 16,
        polynomial: 0x8005UL,
        initialValue: 0x0000UL,
        xorOut: 0x0000UL,
        reflectInput: true,
        reflectOutput: true);

    public static ParameterizedRocksoftCrcEngine Crc32Ieee { get; } = new ParameterizedRocksoftCrcEngine(
        bitWidth: 32,
        polynomial: 0x04C11DB7UL,
        initialValue: 0xFFFFFFFFUL,
        xorOut: 0xFFFFFFFFUL,
        reflectInput: true,
        reflectOutput: true);

    public static ParameterizedRocksoftCrcEngine Crc32Castagnoli { get; } = new ParameterizedRocksoftCrcEngine(
        bitWidth: 32,
        polynomial: 0x1EDC6F41UL,
        initialValue: 0xFFFFFFFFUL,
        xorOut: 0xFFFFFFFFUL,
        reflectInput: true,
        reflectOutput: true);

    public static ParameterizedRocksoftCrcEngine Crc64Xz { get; } = new ParameterizedRocksoftCrcEngine(
        bitWidth: 64,
        polynomial: 0x42F0E1EBA9EA3693UL,
        initialValue: 0xFFFFFFFFFFFFFFFFUL,
        xorOut: 0xFFFFFFFFFFFFFFFFUL,
        reflectInput: true,
        reflectOutput: true);

    public ParameterizedRocksoftCrcEngine(
        int bitWidth,
        ulong polynomial,
        ulong initialValue,
        ulong xorOut,
        bool reflectInput,
        bool reflectOutput)
    {
        if (bitWidth != 8 && bitWidth != 16 && bitWidth != 32 && bitWidth != 64)
        {
            throw new ArgumentOutOfRangeException(nameof(bitWidth), "CRC bitWidth must be 8, 16, 32, or 64.");
        }

        _bitWidth = bitWidth;
        _widthMask = bitWidth == 64 ? ulong.MaxValue : ((1UL << bitWidth) - 1UL);
        _polynomial = polynomial & _widthMask;
        _initialValue = initialValue & _widthMask;
        _xorOut = xorOut & _widthMask;
        _reflectInput = reflectInput;
        _reflectOutput = reflectOutput;
        _table = BuildLookupTable(_bitWidth, _polynomial, _widthMask);
    }

    public int BitWidth => _bitWidth;
    public ulong Polynomial => _polynomial;
    public ulong InitialValue => _initialValue;
    public ulong XorOut => _xorOut;
    public bool ReflectInput => _reflectInput;
    public bool ReflectOutput => _reflectOutput;

    /// <summary>
    /// Branchless 3-stage 8-bit reflection (<c>crypto.cpp:51-57</c>).
    /// </summary>
    public static byte ReflectByte(byte value)
    {
        value = (byte)(((value & 0xF0) >> 4) | ((value & 0x0F) << 4));
        value = (byte)(((value & 0xCC) >> 2) | ((value & 0x33) << 2));
        value = (byte)(((value & 0xAA) >> 1) | ((value & 0x55) << 1));
        return value;
    }

    /// <summary>
    /// Reflects the lowest <paramref name="bitWidth"/> bits of <paramref name="value"/> (<c>crypto.cpp:59-68</c>).
    /// </summary>
    public static ulong ReflectBits(ulong value, int bitWidth)
    {
        ulong result = 0UL;
        for (int i = 0; i < bitWidth; i++)
        {
            result <<= 1;
            if ((value & 1UL) != 0UL)
            {
                result |= 1UL;
            }

            value >>= 1;
        }

        return result;
    }

    private static ulong[] BuildLookupTable(int bitWidth, ulong polynomial, ulong widthMask)
    {
        ulong[] table = new ulong[256];
        ulong topBit = 1UL << (bitWidth - 1);
        int shift = bitWidth - 8;

        for (int dividend = 0; dividend < 256; dividend++)
        {
            ulong remainder = (ulong)dividend << shift;
            for (int bit = 0; bit < 8; bit++)
            {
                if ((remainder & topBit) != 0UL)
                {
                    remainder = ((remainder << 1) ^ polynomial) & widthMask;
                }
                else
                {
                    remainder = (remainder << 1) & widthMask;
                }
            }

            table[dividend] = remainder;
        }

        return table;
    }

    /// <summary>
    /// Computes the Rocksoft™ model CRC checksum over <paramref name="data"/> (<c>crypto.cpp:102-138</c>).
    /// </summary>
    public ulong Compute(ReadOnlySpan<byte> data)
    {
        ulong crc = _initialValue;
        int topByteShift = _bitWidth - 8;

        for (int i = 0; i < data.Length; i++)
        {
            byte curByte = _reflectInput ? ReflectByte(data[i]) : data[i];
            byte tableIdx = (byte)(((crc >> topByteShift) ^ curByte) & 0xFFUL);
            if (_bitWidth == 8)
            {
                crc = _table[tableIdx] & _widthMask;
            }
            else
            {
                crc = ((crc << 8) ^ _table[tableIdx]) & _widthMask;
            }
        }

        if (_reflectOutput)
        {
            crc = ReflectBits(crc, _bitWidth);
        }

        return (crc ^ _xorOut) & _widthMask;
    }

    public uint ComputeUInt32(ReadOnlySpan<byte> data)
    {
        return (uint)Compute(data);
    }

    public ushort ComputeUInt16(ReadOnlySpan<byte> data)
    {
        return (ushort)Compute(data);
    }

    public byte ComputeByte(ReadOnlySpan<byte> data)
    {
        return (byte)Compute(data);
    }
}
