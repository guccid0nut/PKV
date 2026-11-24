// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System;

namespace Kongor.Core.Services.Native;

public enum FastDivVariant
{
    PowerOfTwoShift,
    MulHiShift,
    MulHiAddShift,
    PreShiftMulHiShift
}

public sealed record FastDivSynthesisResult(
    int BitWidth,
    ulong Divisor,
    FastDivVariant Variant,
    ulong Multiplier,
    int PreShift,
    int PostShift,
    bool IsVerifiedExact,
    string DisassemblyFormula);

public sealed record FastDivDecodeResult(
    bool Success,
    int BitWidth,
    FastDivVariant Variant,
    ulong Multiplier,
    int PreShift,
    int PostShift,
    ulong RecoveredDivisor,
    bool IsVerifiedExact,
    string CExpression,
    string Explanation);

/// <summary>
/// Compiler fast-division invariant multiplier &lt;-&gt; divisor decoder and synthesizer modeled after
/// ImHex multiplication_decoder.cpp and Granlund-Montgomery ("Division by Invariant Integers using Multiplication"),
/// supporting 8, 16, 32, and 64-bit unsigned operands across MulHiShift, MulHiAddShift, and PreShiftMulHiShift (#150).
/// </summary>
public static class InvariantMultiplierDivisorDecoder
{
    /// <summary>
    /// Synthesizes the canonical Granlund-Montgomery unsigned fast-division parameters for a divisor d.
    /// </summary>
    public static FastDivSynthesisResult SynthesizeUnsigned(ulong divisor, int bitWidth = 32)
    {
        if (bitWidth is not (8 or 16 or 32 or 64))
        {
            throw new ArgumentOutOfRangeException(nameof(bitWidth), "Supported bit widths are 8, 16, 32, or 64.");
        }
        if (divisor == 0)
        {
            throw new DivideByZeroException("Divisor cannot be zero.");
        }

        // Power-of-two fast path: n / 2^k == n >> k
        if ((divisor & (divisor - 1)) == 0)
        {
            int shift = CountTrailingZeros(divisor);
            return new FastDivSynthesisResult(
                bitWidth,
                divisor,
                FastDivVariant.PowerOfTwoShift,
                1UL,
                0,
                shift,
                true,
                $"n >> {shift}");
        }

        // Compute floor(log2(d)) and ceil(log2(d))
        int floorLog2 = 63 - System.Numerics.BitOperations.LeadingZeroCount(divisor);
        int l = floorLog2 + 1; // ceil(log2(d)) for non-power-of-two

        UInt128 one = UInt128.One;
        UInt128 twoToW = one << bitWidth;
        UInt128 maxInput = twoToW - one;

        // Try standard MulHiShift across postShift s in [0 .. l]
        for (int s = 0; s <= l; s++)
        {
            UInt128 mCandidate = ((one << (bitWidth + s)) + divisor - 1) / divisor;
            if (mCandidate < twoToW)
            {
                ulong mult = (ulong)mCandidate;
                if (VerifyUnsignedDivisorExact(divisor, bitWidth, FastDivVariant.MulHiShift, mult, 0, s))
                {
                    return new FastDivSynthesisResult(
                        bitWidth,
                        divisor,
                        FastDivVariant.MulHiShift,
                        mult,
                        0,
                        s,
                        true,
                        $"mulhi_u{bitWidth}(n, 0x{mult:X}) >> {s}");
                }
            }
        }

        // If divisor is even (d = 2^a * d_odd), try PreShiftMulHiShift
        int trailingZeros = CountTrailingZeros(divisor);
        if (trailingZeros > 0)
        {
            ulong oddDiv = divisor >> trailingZeros;
            int oddFloorLog2 = 63 - System.Numerics.BitOperations.LeadingZeroCount(oddDiv);
            int oddL = oddFloorLog2 + 1;
            UInt128 mOdd = ((one << (bitWidth + oddL - trailingZeros)) + oddDiv - 1) / oddDiv;
            if (mOdd < twoToW)
            {
                ulong mult = (ulong)mOdd;
                int postShift = oddL - trailingZeros;
                if (postShift >= 0 && VerifyUnsignedDivisorExact(divisor, bitWidth, FastDivVariant.PreShiftMulHiShift, mult, trailingZeros, postShift))
                {
                    return new FastDivSynthesisResult(
                        bitWidth,
                        divisor,
                        FastDivVariant.PreShiftMulHiShift,
                        mult,
                        trailingZeros,
                        postShift,
                        true,
                        $"mulhi_u{bitWidth}(n >> {trailingZeros}, 0x{mult:X}) >> {postShift}");
                }
            }
        }

        // Fallback: Granlund-Montgomery N+1-bit overflow-compensated MulHiAddShift
        UInt128 mFull = ((one << (bitWidth + l)) + divisor - 1) / divisor;
        ulong mLow = (ulong)(mFull - twoToW);
        bool exactAdd = VerifyUnsignedDivisorExact(divisor, bitWidth, FastDivVariant.MulHiAddShift, mLow, 0, l);

        return new FastDivSynthesisResult(
            bitWidth,
            divisor,
            FastDivVariant.MulHiAddShift,
            mLow,
            0,
            l,
            exactAdd,
            $"t = mulhi_u{bitWidth}(n, 0x{mLow:X}); (t + ((n - t) >> 1)) >> {l - 1}");
    }

    /// <summary>
    /// Decodes a compiler-emitted magic multiplier and shift sequence back into the exact integer divisor d,
    /// verifying exactness across boundary values (#150).
    /// </summary>
    public static FastDivDecodeResult DecodeUnsigned(
        ulong multiplier,
        int postShift,
        int bitWidth = 32,
        FastDivVariant variant = FastDivVariant.MulHiShift,
        int preShift = 0)
    {
        if (bitWidth is not (8 or 16 or 32 or 64))
        {
            return new FastDivDecodeResult(false, bitWidth, variant, multiplier, preShift, postShift, 0, false, string.Empty, "Unsupported bitWidth.");
        }

        if (variant == FastDivVariant.PowerOfTwoShift)
        {
            ulong pow2Div = 1UL << postShift;
            return new FastDivDecodeResult(
                true, bitWidth, variant, 1UL, 0, postShift, pow2Div, true,
                $"n / {pow2Div}u",
                $"Exact power-of-two shift division by {pow2Div}.");
        }

        if (multiplier == 0)
        {
            return new FastDivDecodeResult(false, bitWidth, variant, multiplier, preShift, postShift, 0, false, string.Empty, "Multiplier cannot be zero.");
        }

        UInt128 one = UInt128.One;
        UInt128 effectiveMultiplier;
        int totalShift;

        if (variant == FastDivVariant.MulHiAddShift)
        {
            // In MulHiAddShift, the second shift in `(t + ((n - t) >> 1)) >> s2` has s2 = l - 1,
            // where postShift can be passed either as l (total exponent above W) or s2 + 1.
            effectiveMultiplier = (one << bitWidth) + multiplier;
            totalShift = bitWidth + postShift;
        }
        else if (variant == FastDivVariant.PreShiftMulHiShift)
        {
            effectiveMultiplier = multiplier;
            totalShift = bitWidth + preShift + postShift;
        }
        else
        {
            effectiveMultiplier = multiplier;
            totalShift = bitWidth + postShift;
        }

        if (totalShift <= 0 || totalShift >= 128)
        {
            return new FastDivDecodeResult(false, bitWidth, variant, multiplier, preShift, postShift, 0, false, string.Empty, "Total shift out of range.");
        }

        UInt128 numerator = one << totalShift;
        UInt128 approxDiv = numerator / effectiveMultiplier;

        // Check candidate divisors around approxDiv: { approxDiv, approxDiv + 1, approxDiv - 1 }
        ulong baseCandidate = (ulong)approxDiv;
        ulong[] candidates = new ulong[]
        {
            baseCandidate + 1UL,
            baseCandidate,
            baseCandidate > 1UL ? baseCandidate - 1UL : 1UL
        };

        for (int i = 0; i < candidates.Length; i++)
        {
            ulong cand = candidates[i];
            if (cand == 0) continue;

            if (VerifyUnsignedDivisorExact(cand, bitWidth, variant, multiplier, preShift, postShift))
            {
                return new FastDivDecodeResult(
                    true,
                    bitWidth,
                    variant,
                    multiplier,
                    preShift,
                    postShift,
                    cand,
                    true,
                    $"n / {cand}u",
                    $"Verified exact {bitWidth}-bit unsigned division by {cand} via {variant} (M=0x{multiplier:X}, pre={preShift}, post={postShift}).");
            }
        }

        // If MulHiAddShift was passed s2 = l - 1 directly instead of l, also test postShift + 1
        if (variant == FastDivVariant.MulHiAddShift && totalShift + 1 < 128)
        {
            UInt128 numPlusOne = one << (totalShift + 1);
            ulong candBase2 = (ulong)(numPlusOne / effectiveMultiplier);
            ulong[] candidates2 = new ulong[] { candBase2 + 1UL, candBase2 };
            for (int i = 0; i < candidates2.Length; i++)
            {
                ulong cand = candidates2[i];
                if (cand == 0) continue;
                if (VerifyUnsignedDivisorExact(cand, bitWidth, variant, multiplier, preShift, postShift + 1))
                {
                    return new FastDivDecodeResult(
                        true,
                        bitWidth,
                        variant,
                        multiplier,
                        preShift,
                        postShift + 1,
                        cand,
                        true,
                        $"n / {cand}u",
                        $"Verified exact {bitWidth}-bit unsigned division by {cand} via {variant} (M=0x{multiplier:X}, l={postShift + 1}).");
                }
            }
        }

        ulong bestEstimate = baseCandidate + 1UL;
        return new FastDivDecodeResult(
            false,
            bitWidth,
            variant,
            multiplier,
            preShift,
            postShift,
            bestEstimate,
            false,
            $"n / {bestEstimate}u /* approx */",
            $"Approximate divisor {bestEstimate} failed boundary exactness proof.");
    }

    public static bool VerifyUnsignedDivisorExact(
        ulong divisor,
        int bitWidth,
        FastDivVariant variant,
        ulong multiplier,
        int preShift,
        int postShift)
    {
        if (divisor == 0) return false;
        ulong maxVal = bitWidth == 64 ? ulong.MaxValue : ((1UL << bitWidth) - 1UL);

        // Critical Granlund-Montgomery boundary points:
        // 0, 1, d-1, d, d+1, 2d-1, 2d, maxVal, maxVal - 1, and largest multiple of d <= maxVal (and one below it)
        ulong maxMultiple = (maxVal / divisor) * divisor;
        ulong midMultiple = ((maxVal / 2UL) / divisor) * divisor;

        ReadOnlySpan<ulong> probes = stackalloc ulong[]
        {
            0UL,
            1UL,
            divisor - 1UL,
            divisor,
            divisor < maxVal ? divisor + 1UL : maxVal,
            divisor <= maxVal / 2UL ? (2UL * divisor) - 1UL : maxVal,
            divisor <= maxVal / 2UL ? (2UL * divisor) : maxVal,
            midMultiple > 0 ? midMultiple - 1UL : 0UL,
            midMultiple,
            midMultiple < maxVal ? midMultiple + 1UL : maxVal,
            maxMultiple > 0 ? maxMultiple - 1UL : 0UL,
            maxMultiple,
            maxVal - 1UL,
            maxVal
        };

        for (int i = 0; i < probes.Length; i++)
        {
            ulong n = probes[i] & maxVal;
            ulong expectedQuotient = n / divisor;
            ulong actualQuotient = EvaluateFastDivUnsigned(n, bitWidth, variant, multiplier, preShift, postShift);
            if (actualQuotient != expectedQuotient)
            {
                return false;
            }
        }

        return true;
    }

    public static ulong EvaluateFastDivUnsigned(
        ulong n,
        int bitWidth,
        FastDivVariant variant,
        ulong multiplier,
        int preShift,
        int postShift)
    {
        ulong maxMask = bitWidth == 64 ? ulong.MaxValue : ((1UL << bitWidth) - 1UL);
        ulong maskedN = n & maxMask;

        switch (variant)
        {
            case FastDivVariant.PowerOfTwoShift:
                return maskedN >> postShift;

            case FastDivVariant.MulHiShift:
            {
                ulong hi = ComputeMulHi(maskedN, multiplier, bitWidth);
                return hi >> postShift;
            }

            case FastDivVariant.PreShiftMulHiShift:
            {
                ulong shiftedN = maskedN >> preShift;
                ulong hi = ComputeMulHi(shiftedN, multiplier, bitWidth);
                return hi >> postShift;
            }

            case FastDivVariant.MulHiAddShift:
            {
                ulong t = ComputeMulHi(maskedN, multiplier, bitWidth);
                ulong diff = (maskedN - t) & maxMask;
                ulong sum = (t + (diff >> 1)) & maxMask;
                int finalShift = Math.Max(0, postShift - 1);
                return sum >> finalShift;
            }

            default:
                return 0UL;
        }
    }

    private static ulong ComputeMulHi(ulong a, ulong b, int bitWidth)
    {
        UInt128 prod = (UInt128)a * (UInt128)b;
        return (ulong)(prod >> bitWidth);
    }

    private static int CountTrailingZeros(ulong value)
    {
        return System.Numerics.BitOperations.TrailingZeroCount(value);
    }
}
