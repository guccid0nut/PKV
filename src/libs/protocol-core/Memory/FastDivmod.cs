// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace ASPIRE.Common.Memory;

/// <summary>
///     CUTLASS-inspired hardware-division-free 32-bit unsigned integer divider and modulo operator (<c>mulhi + shr</c>) (#129).
///     Replaces 20-to-40-cycle hardware <c>DIV</c>/<c>IDIV</c> instructions with a precomputed fixed-point reciprocal
///     multiplier and shift, guaranteeing 100% exact <c>(Quotient, Remainder)</c> across all <c>uint</c> dividends <c>[0..uint.MaxValue]</c>
///     and all <c>uint</c> divisors <c>[1..uint.MaxValue]</c>.
///     Policy: Strict Explicit Typing (NO 'var').
/// </summary>
public readonly record struct FastDivmod
{
    public uint Divisor { get; }
    public uint Multiplier { get; }
    public int ShiftRight { get; }
    public bool IsPowerOfTwo { get; }
    public bool NeedsAddShift { get; }

    public FastDivmod(uint divisor)
    {
        if (divisor == 0U)
        {
            throw new DivideByZeroException(
                "FastDivmod divisor cannot be zero. Pass a positive divisor >= 1 or run dotnet test --filter CutlassWave3OptimizationTests.");
        }

        Divisor = divisor;

        if ((divisor & (divisor - 1U)) == 0U)
        {
            IsPowerOfTwo = true;
            Multiplier = 1U;
            ShiftRight = BitOperations.Log2(divisor);
            NeedsAddShift = false;
            return;
        }

        IsPowerOfTwo = false;
        int floorLog2 = BitOperations.Log2(divisor);
        int ceilLog2 = floorLog2 + 1;

        for (int s = floorLog2; s <= ceilLog2 && s < 32; s++)
        {
            ulong twoToP = 1UL << (32 + s);
            ulong m = (twoToP + divisor - 1UL) / divisor;
            ulong rem = (m * divisor) - twoToP;

            if (m <= uint.MaxValue && (rem == 0UL || ((twoToP - 1UL) / rem) >= uint.MaxValue))
            {
                Multiplier = (uint)m;
                ShiftRight = s;
                NeedsAddShift = false;
                return;
            }
        }

        ulong mFull = ceilLog2 < 32
            ? (((1UL << (32 + ceilLog2)) + divisor - 1UL) / divisor)
            : ((ulong.MaxValue / divisor) + 1UL);

        Multiplier = (uint)(mFull & uint.MaxValue);
        ShiftRight = ceilLog2;
        NeedsAddShift = true;
    }

    /// <summary>
    ///     Computes <c>dividend / Divisor</c> without hardware division instructions.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public uint Div(uint dividend)
    {
        if (IsPowerOfTwo)
        {
            return dividend >> ShiftRight;
        }

        uint t = (uint)(((ulong)dividend * Multiplier) >> 32);
        if (NeedsAddShift)
        {
            return (t + ((dividend - t) >> 1)) >> (ShiftRight - 1);
        }

        return t >> ShiftRight;
    }

    /// <summary>
    ///     Computes <c>dividend % Divisor</c> without hardware division instructions.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public uint Mod(uint dividend)
    {
        if (IsPowerOfTwo)
        {
            return dividend & (Divisor - 1U);
        }

        uint q = Div(dividend);
        return dividend - (q * Divisor);
    }

    /// <summary>
    ///     Computes both <c>(Quotient, Remainder)</c> of <c>dividend / Divisor</c> in a single pass.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public (uint Quotient, uint Remainder) DivMod(uint dividend)
    {
        uint q = Div(dividend);
        uint r = dividend - (q * Divisor);
        return (q, r);
    }

    /// <summary>
    ///     Computes both quotient and <paramref name="remainder"/> of <c>dividend / Divisor</c> in a single pass.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public uint DivMod(uint dividend, out uint remainder)
    {
        uint q = Div(dividend);
        remainder = dividend - (q * Divisor);
        return q;
    }

    /// <summary>
    ///     Signed convenience overload for non-negative ring-buffer cursor indices (<c>index &gt;= 0</c>).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public (int Quotient, int Remainder) DivModNonNegative(int dividend)
    {
        if (dividend < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(dividend), dividend, "Dividend must be non-negative.");
        }

        (uint q, uint r) = DivMod((uint)dividend);
        return ((int)q, (int)r);
    }

    /// <summary>
    ///     Signed convenience overload returning quotient and outputting <paramref name="remainder"/> for non-negative indices.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int DivModNonNegative(int dividend, out int remainder)
    {
        if (dividend < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(dividend), dividend, "Dividend must be non-negative.");
        }

        uint q = DivMod((uint)dividend, out uint r);
        remainder = (int)r;
        return (int)q;
    }
}

