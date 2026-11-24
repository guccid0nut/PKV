// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ============================================================================
// File: XorFecMath.cs
// System: Kongor Core Netcode - SIMD-Accelerated XOR Accumulator
// Policy: Strict Explicit Typing (NO 'var')
// ============================================================================

using System.Numerics;

namespace ASPIRE.Common.Netcode;

/// <summary>
/// High-performance, SIMD-accelerated byte-span XOR accumulation engine.
/// Utilizes Vector&lt;byte&gt; to process 32 or 64 bytes per instruction with scalar trailing fallback.
/// Zero heap allocations.
/// </summary>
public static class XorFecMath
{
    public static void AccumulateXor(Span<byte> accumulator, ReadOnlySpan<byte> source)
    {
        int length = Math.Min(accumulator.Length, source.Length);
        if (length == 0) return;

        int vectorSize = Vector<byte>.Count;
        int i = 0;

        if (Vector.IsHardwareAccelerated && length >= vectorSize)
        {
            int vectorLimit = length - vectorSize;
            while (i <= vectorLimit)
            {
                Vector<byte> vAcc = new(accumulator.Slice(i, vectorSize));
                Vector<byte> vSrc = new(source.Slice(i, vectorSize));
                (vAcc ^ vSrc).CopyTo(accumulator.Slice(i, vectorSize));
                i += vectorSize;
            }
        }

        while (i < length)
        {
            accumulator[i] ^= source[i];
            i++;
        }
    }
}
