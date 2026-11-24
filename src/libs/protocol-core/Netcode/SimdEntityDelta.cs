// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace ASPIRE.Common.Netcode;

/// <summary>
///     High-performance SIMD-accelerated entity delta bitmask and sizing engine.
///     Replaces repetitive branchy HasFlag() calls with vectorized math and precomputed O(1) lookup tables:
///     Tier 0 (AVX-512) -> Tier 1 (AVX2) -> Tier 2 (Vector128) -> Tier 3 (Scalar lookup).
/// </summary>
public static class SimdEntityDelta
{
    public const int BaseEntityOverhead = 7; // EntityId (2) + EntityType (1) + ChangedMask (4)

    public const int CoordinatesSize = 23; // EntityCoordinateDelta.Size
    public const int CombatSize = 36;      // EntityCombatDelta.Size
    public const int AnimationSize = 11;   // EntityAnimationDelta.Size
    public const int InventorySize = 63;   // EntityInventoryDelta.Size
    public const int OrdersSize = 16;      // EntityOrderDelta.Size

    private static readonly byte[] PrecomputedComponentSizes = GeneratePrecomputedComponentSizes();

    private static byte[] GeneratePrecomputedComponentSizes()
    {
        byte[] sizes = new byte[32];
        for (int i = 0; i < 32; i++)
        {
            int size = 0;
            if ((i & (1 << 0)) != 0) size += CoordinatesSize;
            if ((i & (1 << 1)) != 0) size += CombatSize;
            if ((i & (1 << 2)) != 0) size += AnimationSize;
            if ((i & (1 << 3)) != 0) size += InventorySize;
            if ((i & (1 << 4)) != 0) size += OrdersSize;
            sizes[i] = (byte)size;
        }
        return sizes;
    }

    /// <summary>
    ///     Returns true if AVX-512 vector acceleration is available.
    /// </summary>
    public static bool IsAvx512Supported => Avx512F.IsSupported;

    /// <summary>
    ///     Returns true if AVX2 256-bit vector acceleration is available.
    /// </summary>
    public static bool IsAvx2Supported => Avx2.IsSupported;

    /// <summary>
    ///     Returns true if 128-bit vector acceleration (SSE2 / Neon) is available.
    /// </summary>
    public static bool IsVector128Supported => Vector128.IsHardwareAccelerated;

    /// <summary>
    ///     Returns a human-readable identifier of the active SIMD tier.
    /// </summary>
    public static string GetActiveHardwareTier()
    {
        if (IsAvx512Supported) return "AVX-512 (512-bit)";
        if (IsAvx2Supported) return "AVX2 (256-bit)";
        if (IsVector128Supported) return "Vector128 (128-bit)";
        return "Scalar Lookup";
    }

    /// <summary>
    ///     Returns the component payload size for a given delta field mask in O(1) time without branching.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int GetComponentPayloadSize(DeltaFieldMask mask)
    {
        return PrecomputedComponentSizes[(uint)mask & 0x1F];
    }

    /// <summary>
    ///     Returns the total serialized size for a single entity delta (Base 7 bytes + components) in O(1) time.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int GetTotalSerializedSize(DeltaFieldMask mask)
    {
        return BaseEntityOverhead + PrecomputedComponentSizes[(uint)mask & 0x1F];
    }

    /// <summary>
    ///     Computes the total serialized payload size across an entity list with zero-branch lookup.
    /// </summary>
    public static int ComputeTotalSerializedSize(IReadOnlyList<SnapshotEntityDelta> entities)
    {
        int total = 0;
        int count = entities.Count;
        for (int i = 0; i < count; i++)
        {
            total += GetTotalSerializedSize(entities[i].ChangedMask);
        }
        return total;
    }

    /// <summary>
    ///     Computes the total serialized payload size across a span of entity masks using SIMD vectorization.
    /// </summary>
    public static int ComputeTotalSerializedSize(ReadOnlySpan<DeltaFieldMask> masks)
    {
        int total = 0;
        int i = 0;
        int length = masks.Length;

        // Tier 0: AVX-512 (16 masks per instruction)
        if (IsAvx512Supported && length >= 16)
        {
            Vector512<uint> vBaseOverhead = Vector512.Create((uint)BaseEntityOverhead);
            Vector512<uint> vOne = Vector512.Create(1u);
            Vector512<uint> vCoordSize = Vector512.Create((uint)CoordinatesSize);
            Vector512<uint> vCombatSize = Vector512.Create((uint)CombatSize);
            Vector512<uint> vAnimSize = Vector512.Create((uint)AnimationSize);
            Vector512<uint> vInvSize = Vector512.Create((uint)InventorySize);
            Vector512<uint> vOrderSize = Vector512.Create((uint)OrdersSize);

            ref uint maskRef = ref Unsafe.As<DeltaFieldMask, uint>(ref MemoryMarshal.GetReference(masks));

            while (i <= length - 16)
            {
                Vector512<uint> v = Vector512.LoadUnsafe(ref maskRef, (nuint)i);

                Vector512<uint> c0 = (v & vOne) * vCoordSize;
                Vector512<uint> c1 = ((v >> 1) & vOne) * vCombatSize;
                Vector512<uint> c2 = ((v >> 2) & vOne) * vAnimSize;
                Vector512<uint> c3 = ((v >> 3) & vOne) * vInvSize;
                Vector512<uint> c4 = ((v >> 4) & vOne) * vOrderSize;

                Vector512<uint> sum = vBaseOverhead + c0 + c1 + c2 + c3 + c4;
                total += (int)Vector512.Sum(sum);

                i += 16;
            }
        }
        // Tier 1: AVX2 (8 masks per instruction)
        else if (IsAvx2Supported && length >= 8)
        {
            Vector256<uint> vBaseOverhead = Vector256.Create((uint)BaseEntityOverhead);
            Vector256<uint> vOne = Vector256.Create(1u);
            Vector256<uint> vCoordSize = Vector256.Create((uint)CoordinatesSize);
            Vector256<uint> vCombatSize = Vector256.Create((uint)CombatSize);
            Vector256<uint> vAnimSize = Vector256.Create((uint)AnimationSize);
            Vector256<uint> vInvSize = Vector256.Create((uint)InventorySize);
            Vector256<uint> vOrderSize = Vector256.Create((uint)OrdersSize);

            ref uint maskRef = ref Unsafe.As<DeltaFieldMask, uint>(ref MemoryMarshal.GetReference(masks));

            while (i <= length - 8)
            {
                Vector256<uint> v = Vector256.LoadUnsafe(ref maskRef, (nuint)i);

                Vector256<uint> c0 = (v & vOne) * vCoordSize;
                Vector256<uint> c1 = ((v >> 1) & vOne) * vCombatSize;
                Vector256<uint> c2 = ((v >> 2) & vOne) * vAnimSize;
                Vector256<uint> c3 = ((v >> 3) & vOne) * vInvSize;
                Vector256<uint> c4 = ((v >> 4) & vOne) * vOrderSize;

                Vector256<uint> sum = vBaseOverhead + c0 + c1 + c2 + c3 + c4;
                total += (int)Vector256.Sum(sum);

                i += 8;
            }
        }
        // Tier 2: Vector128 (4 masks per instruction)
        else if (IsVector128Supported && length >= 4)
        {
            Vector128<uint> vBaseOverhead = Vector128.Create((uint)BaseEntityOverhead);
            Vector128<uint> vOne = Vector128.Create(1u);
            Vector128<uint> vCoordSize = Vector128.Create((uint)CoordinatesSize);
            Vector128<uint> vCombatSize = Vector128.Create((uint)CombatSize);
            Vector128<uint> vAnimSize = Vector128.Create((uint)AnimationSize);
            Vector128<uint> vInvSize = Vector128.Create((uint)InventorySize);
            Vector128<uint> vOrderSize = Vector128.Create((uint)OrdersSize);

            ref uint maskRef = ref Unsafe.As<DeltaFieldMask, uint>(ref MemoryMarshal.GetReference(masks));

            while (i <= length - 4)
            {
                Vector128<uint> v = Vector128.LoadUnsafe(ref maskRef, (nuint)i);

                Vector128<uint> c0 = (v & vOne) * vCoordSize;
                Vector128<uint> c1 = ((v >> 1) & vOne) * vCombatSize;
                Vector128<uint> c2 = ((v >> 2) & vOne) * vAnimSize;
                Vector128<uint> c3 = ((v >> 3) & vOne) * vInvSize;
                Vector128<uint> c4 = ((v >> 4) & vOne) * vOrderSize;

                Vector128<uint> sum = vBaseOverhead + c0 + c1 + c2 + c3 + c4;
                total += (int)Vector128.Sum(sum);

                i += 4;
            }
        }

        // Tier 3: Scalar tail drain using precomputed table
        while (i < length)
        {
            total += GetTotalSerializedSize(masks[i]);
            i++;
        }

        return total;
    }

    /// <summary>
    ///     Filters indices of entities that have at least one modified component (ChangedMask != None)
    ///     using SIMD vectorization to bypass unchanged entity blocks.
    /// </summary>
    public static int FilterActiveEntities(ReadOnlySpan<DeltaFieldMask> masks, Span<int> activeIndices)
    {
        int written = 0;
        int i = 0;
        int length = masks.Length;

        // Vectorized skip of all-zero entities using Vector256
        if (IsAvx2Supported && length >= 8)
        {
            ref uint maskRef = ref Unsafe.As<DeltaFieldMask, uint>(ref MemoryMarshal.GetReference(masks));

            while (i <= length - 8)
            {
                Vector256<uint> v = Vector256.LoadUnsafe(ref maskRef, (nuint)i);
                Vector256<uint> isZero = Vector256.Equals(v, Vector256<uint>.Zero);
                uint nonZeroBits = ~Vector256.ExtractMostSignificantBits(isZero) & 0xFF;

                while (nonZeroBits != 0)
                {
                    int lane = BitOperations.TrailingZeroCount(nonZeroBits);
                    activeIndices[written++] = i + lane;
                    nonZeroBits &= nonZeroBits - 1; // Clear lowest set bit
                }

                i += 8;
            }
        }

        // Scalar tail drain
        while (i < length)
        {
            if (masks[i] != DeltaFieldMask.None)
            {
                activeIndices[written++] = i;
            }
            i++;
        }

        return written;
    }
}
