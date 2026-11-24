// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// TAO (Together As One) MCP & Kongor CLI -- Created by guccidonut (https://github.com/guccidonut/PKV)

using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using ASPIRE.Common.Memory;
using Kongor.Cli.Mcp.Handlers;
using Kongor.Cli.Mcp.Imaging;
using ReplayVulkanRenderer;
using Xunit;

namespace Kongor.Mcp.Tests;

/// <summary>
///     Verification suite for Wave v3 Phase 9 (CUTLASS Epilogue &amp; SIMD Converters: #133, #125, #126, #127, #128).
///     Policy: Strict Explicit Typing (NO 'var').
/// </summary>
public sealed class CutlassWave3OptimizationTests
{
    [Fact]
    public void TensorLayout2D_LogicalToLinear_SliceTile_Transpose_And_Cosize_PreserveBijectionAndRejectOutOfBounds()
    {
        // Protects: CuTe 2D layout & stride algebra coordinate-to-offset bijection, tile slicing, transposition, and bounds safety (#133).
        // Fails when: Row/column stride arithmetic, sub-tile base offsets, or out-of-bounds guards regress.
        // Seam: TensorLayout2D.CreateRowMajor / LogicalToLinear / SliceTile / Transpose / RingRowOffset.
        TensorLayout2D layout = TensorLayout2D.CreateRowMajor(16, 8, rowPitch: 20, baseOffset: 4);
        Assert.Equal(128, layout.Size);
        Assert.Equal(4 + 15 + (7 * 20) + 1, layout.Cosize);
        Assert.Equal(4 + (5 * 1) + (3 * 20), layout.LogicalToLinear(5, 3));
        Assert.Equal(layout.LogicalToLinear(5, 3), layout.GetOffset(5, 3));

        TensorLayout2D subTile = layout.SliceTile(4, 2, 6, 4);
        Assert.Equal(6, subTile.Width);
        Assert.Equal(4, subTile.Height);
        Assert.Equal(layout.LogicalToLinear(4, 2), subTile.LogicalToLinear(0, 0));
        Assert.Equal(layout.LogicalToLinear(7, 5), subTile.LogicalToLinear(3, 3));

        TensorLayout2D transposed = layout.Transpose();
        Assert.Equal(8, transposed.Width);
        Assert.Equal(16, transposed.Height);
        Assert.Equal(layout.LogicalToLinear(5, 3), transposed.LogicalToLinear(3, 5));

        TensorLayout2D ringLayout = TensorLayout2D.CreateRowMajor(10, 8);
        Assert.Equal(ringLayout.LogicalToLinear(3, 2), ringLayout.RingRowOffset(3, 10, 8));

        // Negative controls: out-of-bounds coordinates and tiles must throw ArgumentOutOfRangeException
        Assert.Throws<ArgumentOutOfRangeException>(() => layout.LogicalToLinear(-1, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => layout.LogicalToLinear(16, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => layout.LogicalToLinear(0, 8));
        Assert.Throws<ArgumentOutOfRangeException>(() => layout.SliceTile(12, 0, 6, 4));
    }

    [Fact]
    public void TensorLayout2D_SwizzleXor_Eliminates32BankSharedMemoryConflicts()
    {
        // Protects: CuTe Swizzle<B, M, S> XOR shared-memory bank permutation (#133, #127).
        // Fails when: SwizzleXor produces bank collisions across a 32-bank row/column access pattern.
        // Seam: TensorLayout2D.SwizzleXor / CountRowBankConflicts / IsBankConflictFreeForRow.
        TensorLayout2D colMajor32 = TensorLayout2D.CreateColumnMajor(32, 32, colPitch: 32);

        // Without swizzling (swizzleBits = 0), accessing a row across 32 columns with StrideX = 32 hits bank 0 on all 32 lanes (31 conflicts)
        int unswizzledConflicts = colMajor32.CountRowBankConflicts(row: 1, swizzleBits: 0, swizzleBase: 0, swizzleShift: 0, numBanks: 32);
        Assert.Equal(31, unswizzledConflicts);
        Assert.False(colMajor32.IsBankConflictFreeForRow(row: 1, swizzleBits: 0, swizzleBase: 0, swizzleShift: 0, numBanks: 32));

        // With Swizzle<5, 0, 5> (XORing column index bits [9:5] into [4:0]), all 32 columns map to 32 distinct banks (0 conflicts)
        for (int row = 0; row < 32; row++)
        {
            Assert.True(colMajor32.IsBankConflictFreeForRow(row, swizzleBits: 5, swizzleBase: 0, swizzleShift: 5, numBanks: 32));
        }
    }

    [Fact]
    public void UiVisualDiffHandler_DualThermalAndDimLuts_MatchExactMathematicalFormulaForAll256Inputs()
    {
        // Protects: Precomputed 256-entry s_dimLut and s_thermalRampLut in UiVisualDiffHandler (#125).
        // Fails when: Any LUT entry diverges from v = sqrt(d/255)*765 or dim = (byte)(lum * 0.25).
        // Seam: UiVisualDiffHandler.ComputeThermalHeatmapPixel / GetDimLut / GetThermalRampLut.
        ReadOnlySpan<Rgba32> dimLut = UiVisualDiffHandler.GetDimLut();
        ReadOnlySpan<Rgba32> thermalLut = UiVisualDiffHandler.GetThermalRampLut();
        Assert.Equal(256, dimLut.Length);
        Assert.Equal(256, thermalLut.Length);

        for (int lum = 0; lum < 256; lum++)
        {
            byte expectedDim = (byte)(lum * 0.25);
            Rgba32 expectedZeroDelta = new Rgba32(expectedDim, expectedDim, expectedDim, 255);
            Assert.Equal(expectedZeroDelta, UiVisualDiffHandler.ComputeThermalHeatmapPixel(0, (byte)lum));
            Assert.Equal(expectedZeroDelta, dimLut[lum]);
        }

        for (int d = 1; d < 256; d++)
        {
            double v = Math.Sqrt(d / 255.0) * 765.0;
            byte r = (byte)Math.Clamp((int)Math.Round(v), 0, 255);
            byte g = (byte)Math.Clamp((int)Math.Round(v - 255.0), 0, 255);
            byte b = (byte)Math.Clamp((int)Math.Round(v - 510.0), 0, 255);
            Rgba32 expected = new Rgba32(r, g, b, 255);

            Assert.Equal(expected, UiVisualDiffHandler.ComputeThermalHeatmapPixel(d, 128));
            Assert.Equal(expected, thermalLut[d]);
        }
    }

    [Fact]
    public void UiVisualDiffHandler_SinglePass8RowSatRing_DetectsBottomRowEpilogueTailDrainHotspotAndAllocatesZeroLohBytes()
    {
        // Protects: Single-pass 8-row pooled integer SAT ring buffer, epilogue tail drain for bottom 3 rows, and 0-LOH allocation (#125).
        // Fails when: Epilogue tail drain is omitted (missing hotspots on y in [height-3..height-1]) or LOH arrays are allocated.
        // Seam: UiVisualDiffHandler.ComputeDiffFromImages.
        List<string> logs = new List<string>();
        UiVisualDiffHandler handler = new UiVisualDiffHandler(logger: msg => logs.Add(msg));

        // 1. Verify Epilogue Tail Drain on a 32x3 strip (where y < radius for all rows, so ONLY the Epilogue Tail Drain executes)
        using Image<Rgba32> baseStrip = new Image<Rgba32>(32, 3, new Rgba32(40, 40, 40, 255));
        using Image<Rgba32> candStrip = new Image<Rgba32>(32, 3, new Rgba32(40, 40, 40, 255));
        candStrip[28, 2] = new Rgba32(255, 255, 255, 255);

        UiVisualDiffHandler.VisualDiffResult stripResult = handler.ComputeDiffFromImages(
            baseStrip,
            candStrip,
            masks: null,
            customOutputPath: "memory://null");

        Assert.Equal(1, stripResult.DifferentPixelsCount);
        Assert.True(stripResult.LocalMax7x7 > 0.01, $"Expected LocalMax7x7 > 0.01 from epilogue tail drain on 32x3 strip, got {stripResult.LocalMax7x7}");

        // 2. Verify Epilogue Tail Drain on the bottom 3 rows [21..23] of a 32x24 frame when row 20 is masked
        const int smallW = 32;
        const int smallH = 24;
        using Image<Rgba32> baseSmall = new Image<Rgba32>(smallW, smallH, new Rgba32(40, 40, 40, 255));
        using Image<Rgba32> candSmall = new Image<Rgba32>(smallW, smallH, new Rgba32(40, 40, 40, 255));

        for (int dy = -2; dy <= 0; dy++)
        {
            for (int dx = -1; dx <= 1; dx++)
            {
                candSmall[28 + dx, (smallH - 1) + dy] = new Rgba32(255, 255, 255, 255);
            }
        }

        List<MaskRegion> row20Mask = new List<MaskRegion>
        {
            new MaskRegion(0, 20, smallW, 1)
        };
        UiVisualDiffHandler.VisualDiffResult tailResult = handler.ComputeDiffFromImages(
            baseSmall,
            candSmall,
            masks: row20Mask,
            customOutputPath: "memory://null");

        Assert.Equal(9, tailResult.DifferentPixelsCount);
        Assert.True(tailResult.LocalMax7x7 > 0.10, $"Expected LocalMax7x7 > 0.10 for bottom-row anomaly, got {tailResult.LocalMax7x7}");
        Assert.True(tailResult.HotspotY >= smallH - 3, $"Expected HotspotY in bottom 3 tail-drain rows [21..23], got {tailResult.HotspotY}");
        Assert.InRange(tailResult.HotspotX, 25, 31);
        Assert.Contains(logs, l => l.Contains("[CUTLASS-EVT-DIFF] mode=single_pass_ring8", StringComparison.Ordinal));

        // 3. Verify masking with 8-row pooled maskRing suppresses masked anomaly
        List<MaskRegion> masks = new List<MaskRegion>
        {
            new MaskRegion(25, smallH - 4, 6, 4)
        };
        UiVisualDiffHandler.VisualDiffResult maskedResult = handler.ComputeDiffFromImages(
            baseSmall,
            candSmall,
            masks: masks,
            customOutputPath: "memory://null");

        Assert.Equal(24, maskedResult.MaskedPixels);
        Assert.Equal(0, maskedResult.DifferentPixelsCount);
        Assert.Equal(0.0, maskedResult.LocalMax7x7, 6);

        // 4. Verify 0 Large Object Heap (LOH >= 85,000 bytes) allocation on 1920x1080 full HD frames
        using Image<Rgba32> hdBase = new Image<Rgba32>(1920, 1080, new Rgba32(25, 35, 45, 255));
        using Image<Rgba32> hdCand = new Image<Rgba32>(1920, 1080, new Rgba32(25, 35, 45, 255));
        hdCand[960, 540] = new Rgba32(240, 100, 50, 255);

        // Warm up ArrayPool bucket once
        _ = handler.ComputeDiffFromImages(hdBase, hdCand, masks: null, customOutputPath: "memory://null");

        long beforeBytes = GC.GetAllocatedBytesForCurrentThread();
        UiVisualDiffHandler.VisualDiffResult hdResult = handler.ComputeDiffFromImages(
            hdBase,
            hdCand,
            masks: null,
            customOutputPath: "memory://null");
        long allocatedBytes = GC.GetAllocatedBytesForCurrentThread() - beforeBytes;

        Assert.Equal(1, hdResult.DifferentPixelsCount);
        // Legacy implementation allocated 16,613,608 + 2,073,600 = 18,687,208 bytes on the LOH per call.
        // Single-pass ring8 allocates < 85,000 bytes (zero LOH allocations).
        Assert.True(allocatedBytes < 85_000L, $"Expected < 85,000 bytes allocated (0 LOH), but allocated {allocatedBytes} bytes.");
    }

    [Fact]
    public void VulkanNv12ReadbackRing_Bt709Q16Coefficients_HaveExactZeroChromaSumAndMapNeutralGraysTo128()
    {
        // Protects: Zero-sum Q16 fixed-point BT.709 chroma coefficients for both full-range and studio limited-range (#126).
        // Fails when: U or V coefficients do not sum to 0, causing neutral gray (R == G == B) to drift away from 128.
        // Seam: VulkanNv12ReadbackRing.GetBt709Q16Coefficients / ConvertRgbaToNv12Bt709.
        (int fullYR, int fullYG, int fullYB, int fullUR, int fullUG, int fullUB, int fullVR, int fullVG, int fullVB) =
            VulkanNv12ReadbackRing.GetBt709Q16Coefficients(fullRange: true);
        Assert.Equal(65536, fullYR + fullYG + fullYB);
        Assert.Equal(0, fullUR + fullUG + fullUB);
        Assert.Equal(0, fullVR + fullVG + fullVB);

        (int studioYR, int studioYG, int studioYB, int studioUR, int studioUG, int studioUB, int studioVR, int studioVG, int studioVB) =
            VulkanNv12ReadbackRing.GetBt709Q16Coefficients(fullRange: false);
        Assert.Equal(56284, studioYR + studioYG + studioYB);
        Assert.Equal(0, studioUR + studioUG + studioUB);
        Assert.Equal(0, studioVR + studioVG + studioVB);

        // Verify all 256 neutral gray levels (R == G == B == k) produce exact U = 128, V = 128
        const uint width = 512;
        const uint height = 2;
        byte[] grayRgba = new byte[VulkanNv12ReadbackRing.ComputeRgbaByteLength(width, height)];
        for (int k = 0; k < 256; k++)
        {
            byte gray = (byte)k;
            int xBase = k * 2;
            for (int y = 0; y < 2; y++)
            {
                for (int dx = 0; dx < 2; dx++)
                {
                    int idx = ((y * (int)width) + xBase + dx) * 4;
                    grayRgba[idx] = gray;
                    grayRgba[idx + 1] = gray;
                    grayRgba[idx + 2] = gray;
                    grayRgba[idx + 3] = 255;
                }
            }
        }

        byte[] nv12Full = new byte[VulkanNv12ReadbackRing.ComputeNv12ByteLength(width, height)];
        byte[] nv12Studio = new byte[VulkanNv12ReadbackRing.ComputeNv12ByteLength(width, height)];

        VulkanNv12ReadbackRing.ConvertRgbaToNv12Bt709(grayRgba, nv12Full, width, height, fullRange: true);
        VulkanNv12ReadbackRing.ConvertRgbaToNv12Bt709(grayRgba, nv12Studio, width, height, fullRange: false);

        int uvOffset = (int)(width * height);
        for (int i = 0; i < (int)width; i++)
        {
            Assert.Equal(128, nv12Full[uvOffset + i]);
            Assert.Equal(128, nv12Studio[uvOffset + i]);
        }
    }

    [Theory]
    [InlineData(64U, 16U, true)]
    [InlineData(64U, 16U, false)]
    [InlineData(66U, 6U, true)]
    [InlineData(66U, 6U, false)]
    public void VulkanNv12ReadbackRing_SimdQ16Converter_MatchesScalarFloatReferenceWithin1Lsb(
        uint width,
        uint height,
        bool fullRange)
    {
        // Protects: AVX2 Vector256 Q16 RGBA8->NV12 converter and non-multiple-of-4 scalar tail loop (e.g. width=66 where 66 % 4 == 2) (#126).
        // Fails when: Any Y, U, or V byte diverges from the floating-point BT.709 reference by > 1 LSB.
        // Seam: VulkanNv12ReadbackRing.ConvertRgbaToNv12Bt709 vs ConvertRgbaToNv12Bt709ScalarFloatReference.
        byte[] rgba = new byte[VulkanNv12ReadbackRing.ComputeRgbaByteLength(width, height)];
        Random rng = new Random(1337 + (int)width + (fullRange ? 1 : 0));
        rng.NextBytes(rgba);

        byte[] simdNv12 = new byte[VulkanNv12ReadbackRing.ComputeNv12ByteLength(width, height)];
        byte[] refNv12 = new byte[VulkanNv12ReadbackRing.ComputeNv12ByteLength(width, height)];

        VulkanNv12ReadbackRing.ConvertRgbaToNv12Bt709(rgba, simdNv12, width, height, fullRange);
        VulkanNv12ReadbackRing.ConvertRgbaToNv12Bt709ScalarFloatReference(rgba, refNv12, width, height, fullRange);

        for (int i = 0; i < simdNv12.Length; i++)
        {
            int delta = Math.Abs(simdNv12[i] - refNv12[i]);
            Assert.True(delta <= 1, $"Byte index {i} diverged by {delta} LSB (simd={simdNv12[i]}, ref={refNv12[i]}, fullRange={fullRange}).");
        }
    }

    [Fact]
    public void RgbaToNv12ComputeShader_ContainsCuteSwizzleSharedTileFullRangePushConstantAndHalfwordAlignmentGuard()
    {
        // Protects: GPU compute shader CuTe Swizzle<3,2,3> shared-memory staging, fullRange push constant, and non-multiple-of-4 width alignment (#127).
        // Fails when: rgba_to_nv12.comp lacks shared tile swizzling, fullRange toggle, or corrupts odd rows when width % 4 == 2.
        // Seam: src/extensions/vulkan-interop/shaders/rgba_to_nv12.comp.
        DirectoryInfo? dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "src", "extensions", "vulkan-interop", "shaders", "rgba_to_nv12.comp")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        string shaderPath = Path.Combine(dir!.FullName, "src", "extensions", "vulkan-interop", "shaders", "rgba_to_nv12.comp");
        Assert.True(File.Exists(shaderPath), $"Shader file missing at {shaderPath}.");

        string shaderSource = File.ReadAllText(shaderPath);
        Assert.Contains("uint fullRange;", shaderSource, StringComparison.Ordinal);
        Assert.Contains("shared vec3 s_rgbaTile[32][64];", shaderSource, StringComparison.Ordinal);
        Assert.Contains("uint cute_swizzle_col(uint col, uint row)", shaderSource, StringComparison.Ordinal);
        Assert.Contains("void store_nv12_halfword(uint byteOffset, uint packed16)", shaderSource, StringComparison.Ordinal);
        Assert.Contains("void store_nv12_quad(uint byteOffset, uint packed32)", shaderSource, StringComparison.Ordinal);
        Assert.Contains("bool hasRightPair = (blockX + 2u) < pc.width;", shaderSource, StringComparison.Ordinal);

        // Verify the shader's CuTe swizzle formula col ^ ((row & 7u) << 3u) produces a bijection across all 64 columns in each row
        for (uint row = 0; row < 32; row++)
        {
            ulong seenCols = 0UL;
            for (uint col = 0; col < 64; col++)
            {
                uint swizzled = col ^ ((row & 7U) << 3);
                Assert.True(swizzled < 64U);
                seenCols |= 1UL << (int)swizzled;
            }

            Assert.Equal(ulong.MaxValue, seenCols);
        }
    }

    [Fact]
    public void SkeletalAnimationEngine_ComposeTrsMatrixDirect_And_FusedLbsSkinning_MatchUnfusedReferenceWithin1eMinus5()
    {
        // Protects: Direct 18-op TRS matrix composition and single-matrix fused Linear Blend Skinning (single + batch) (#128).
        // Fails when: ComposeTrsMatrixDirect or DeformVertexSkeletalLbs / DeformVertexBatchSkeletalLbs drifts from unfused Matrix4x4 reference by > 1e-5f.
        // Seam: SkeletalAnimationEngine.ComposeTrsMatrixDirect / DeformVertexSkeletalLbs / DeformVertexBatchSkeletalLbs.
        Vector3 scale = new Vector3(1.25f, 0.85f, 1.10f);
        Quaternion rotation = Quaternion.Normalize(new Quaternion(0.35f, -0.45f, 0.25f, 0.78f));
        Vector3 translation = new Vector3(12.5f, -4.2f, 88.0f);

        Matrix4x4 directTrs = SkeletalAnimationEngine.ComposeTrsMatrixDirect(scale, rotation, translation);
        Matrix4x4 unfusedTrs = Matrix4x4.CreateScale(scale) *
                               Matrix4x4.CreateFromQuaternion(rotation) *
                               Matrix4x4.CreateTranslation(translation);

        AssertMatrixNearEqual(unfusedTrs, directTrs, 1e-5f);

        SkeletalPose pose = new SkeletalPose(8);
        for (int b = 0; b < 8; b++)
        {
            Vector3 bScale = new Vector3(1.0f + (0.05f * b), 1.0f - (0.03f * b), 1.0f + (0.02f * b));
            Quaternion bRot = Quaternion.Normalize(new Quaternion(0.1f * b, -0.15f * b, 0.2f * b, 1.0f));
            Vector3 bTrans = new Vector3(b * 2.0f, -b * 1.5f, b * 0.75f);
            pose.BoneTransforms[b] = SkeletalAnimationEngine.ComposeTrsMatrixDirect(bScale, bRot, bTrans);
        }

        Vector3[] restPositions = new Vector3[]
        {
            new Vector3(1.5f, 2.0f, -3.25f),
            new Vector3(-4.0f, 5.5f, 0.5f),
            new Vector3(0.25f, -1.75f, 6.0f),
            new Vector3(3.1f, 0.0f, -2.2f)
        };
        Vector4[] jointIndices = new Vector4[]
        {
            new Vector4(0, 1, 2, 3),
            new Vector4(0, 1, 2, 3), // Identical to previous vertex to exercise the 1-slot register cache
            new Vector4(2, 4, 5, 7),
            new Vector4(1, 3, 6, 7)
        };
        Vector4[] jointWeights = new Vector4[]
        {
            new Vector4(0.4f, 0.3f, 0.2f, 0.1f),
            new Vector4(0.4f, 0.3f, 0.2f, 0.1f), // Identical to previous vertex to exercise the 1-slot register cache
            new Vector4(0.5f, 0.25f, 0.15f, 0.10f),
            new Vector4(0.7f, 0.2f, 0.1f, 0.0f)
        };

        Vector3[] batchOutput = new Vector3[restPositions.Length];
        SkeletalAnimationEngine.DeformVertexBatchSkeletalLbs(
            restPositions,
            jointIndices,
            jointWeights,
            pose,
            batchOutput);

        for (int i = 0; i < restPositions.Length; i++)
        {
            Vector3 singleFused = SkeletalAnimationEngine.DeformVertexSkeletalLbs(
                restPositions[i],
                jointIndices[i],
                jointWeights[i],
                pose);

            // Compute unfused 4-transform reference
            Vector4 idx = jointIndices[i];
            Vector4 w = jointWeights[i];
            float totalW = w.X + w.Y + w.Z + w.W;
            Vector3 unfusedRef =
                (Vector3.Transform(restPositions[i], pose.BoneTransforms[(int)idx.X]) * (w.X / totalW)) +
                (Vector3.Transform(restPositions[i], pose.BoneTransforms[(int)idx.Y]) * (w.Y / totalW)) +
                (Vector3.Transform(restPositions[i], pose.BoneTransforms[(int)idx.Z]) * (w.Z / totalW)) +
                (Vector3.Transform(restPositions[i], pose.BoneTransforms[(int)idx.W]) * (w.W / totalW));

            Assert.True(Vector3.Distance(unfusedRef, singleFused) <= 1e-5f,
                $"Vertex {i} singleFused {singleFused} drifted from unfusedRef {unfusedRef}");
            Assert.True(Vector3.Distance(unfusedRef, batchOutput[i]) <= 1e-5f,
                $"Vertex {i} batchOutput {batchOutput[i]} drifted from unfusedRef {unfusedRef}");
        }
    }

    private static void AssertMatrixNearEqual(Matrix4x4 expected, Matrix4x4 actual, float tolerance)
    {
        Assert.True(MathF.Abs(expected.M11 - actual.M11) <= tolerance);
        Assert.True(MathF.Abs(expected.M12 - actual.M12) <= tolerance);
        Assert.True(MathF.Abs(expected.M13 - actual.M13) <= tolerance);
        Assert.True(MathF.Abs(expected.M14 - actual.M14) <= tolerance);

        Assert.True(MathF.Abs(expected.M21 - actual.M21) <= tolerance);
        Assert.True(MathF.Abs(expected.M22 - actual.M22) <= tolerance);
        Assert.True(MathF.Abs(expected.M23 - actual.M23) <= tolerance);
        Assert.True(MathF.Abs(expected.M24 - actual.M24) <= tolerance);

        Assert.True(MathF.Abs(expected.M31 - actual.M31) <= tolerance);
        Assert.True(MathF.Abs(expected.M32 - actual.M32) <= tolerance);
        Assert.True(MathF.Abs(expected.M33 - actual.M33) <= tolerance);
        Assert.True(MathF.Abs(expected.M34 - actual.M34) <= tolerance);

        Assert.True(MathF.Abs(expected.M41 - actual.M41) <= tolerance);
        Assert.True(MathF.Abs(expected.M42 - actual.M42) <= tolerance);
        Assert.True(MathF.Abs(expected.M43 - actual.M43) <= tolerance);
        Assert.True(MathF.Abs(expected.M44 - actual.M44) <= tolerance);
    }

    [Fact]
    public void FastDivmod_DivMod_MatchesHardwareDivisionAndModuloAcrossPrimeEvenPowerOfTwoAndMaxBoundaryDivisors()
    {
        // Protects: Barrett/Granlund-Montgomery FastDivmod strength-reduced integer division & modulo across full 32-bit unsigned domain (#129, #134).
        // Fails when: Any divisor (including 1, powers of two, primes, 641, 1920, 0x80000001u, uint.MaxValue) produces a quotient or remainder != hardware / and %.
        // Seam: ASPIRE.Common.Memory.FastDivmod + VulkanNv12ReadbackRing.SlotDivmod.
        Assert.Throws<DivideByZeroException>(() => new FastDivmod(0U));

        FastDivmod slotDivmod = VulkanNv12ReadbackRing.SlotDivmod;
        Assert.Equal((uint)VulkanNv12ReadbackRing.SlotCount, slotDivmod.Divisor);
        Assert.Throws<ArgumentOutOfRangeException>(() => slotDivmod.DivModNonNegative(-1, out int _));

        uint[] divisors = new uint[]
        {
            1U, 2U, 3U, 4U, 5U, 6U, 7U, 8U, 9U, 10U, 12U, 15U, 16U, 17U, 19U, 24U, 31U, 32U, 33U, 63U, 64U, 65U,
            127U, 128U, 129U, 255U, 256U, 257U, 511U, 512U, 513U, 641U, 997U, 1024U, 1080U, 1920U, 2560U, 3840U,
            65535U, 65536U, 65537U, 0x7FFFFFFFU, 0x80000000U, 0x80000001U, 0xFFFFFFFEU, uint.MaxValue
        };

        uint[] boundaryDividends = new uint[]
        {
            0U, 1U, 2U, 3U, 4U, 5U, 6U, 7U, 8U, 15U, 16U, 17U, 31U, 32U, 33U, 63U, 64U, 65U,
            640U, 641U, 642U, 1919U, 1920U, 1921U, 65535U, 65536U, 65537U,
            0x7FFFFFFEU, 0x7FFFFFFFU, 0x80000000U, 0x80000001U, 0xFFFFFFFEU, uint.MaxValue
        };

        Random rng = new Random(20261018);

        for (int dIdx = 0; dIdx < divisors.Length; dIdx++)
        {
            uint d = divisors[dIdx];
            FastDivmod fast = new FastDivmod(d);
            Assert.Equal(d, fast.Divisor);
            Assert.Equal((d & (d - 1U)) == 0U, fast.IsPowerOfTwo);

            for (int nIdx = 0; nIdx < boundaryDividends.Length; nIdx++)
            {
                uint n = boundaryDividends[nIdx];
                VerifyFastDivmodExact(fast, n, d);
            }

            // Test multiples around k * d - 1, k * d, k * d + 1
            for (uint k = 1U; k <= 128U; k++)
            {
                ulong prod = (ulong)k * d;
                if (prod <= uint.MaxValue)
                {
                    uint p = (uint)prod;
                    if (p > 0U)
                    {
                        VerifyFastDivmodExact(fast, p - 1U, d);
                    }

                    VerifyFastDivmodExact(fast, p, d);
                    if (p < uint.MaxValue)
                    {
                        VerifyFastDivmodExact(fast, p + 1U, d);
                    }
                }
            }

            // Test 256 deterministic pseudo-random 32-bit dividends per divisor
            for (int sample = 0; sample < 256; sample++)
            {
                uint randomDividend = (uint)rng.NextInt64(0L, (long)uint.MaxValue + 1L);
                VerifyFastDivmodExact(fast, randomDividend, d);
            }
        }
    }

    [Fact]
    public void NativeGuardPhase10Sources_EnforcePersistentTileWorkStealingThreadLocalPrewarmerAnd64BoxStackRayTiles()
    {
        // Protects: Phase 10 native C++ CUTLASS-inspired optimizations (#129, #130, #131, #132) against regression to heap-allocating or non-stealing loops.
        // Fails when: kongor_fast_divmod.h, job_pool.cpp, asset_prewarmer.cpp, or simd_ray_bridge.cpp loses its zero-allocation/work-stealing invariants.
        // Seam: native/proxies/native_guard/{kongor_fast_divmod.h,job_pool.cpp,asset_prewarmer.cpp,simd_ray_bridge.cpp}.
        DirectoryInfo? dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "native", "proxies", "native_guard", "job_pool.cpp")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        string nativeGuardDir = Path.Combine(dir!.FullName, "native", "proxies", "native_guard");

        // 1. kongor_fast_divmod.h (#129)
        string fastDivmodHeader = File.ReadAllText(Path.Combine(nativeGuardDir, "kongor_fast_divmod.h"));
        Assert.Contains("struct FastDivmod", fastDivmodHeader, StringComparison.Ordinal);
        Assert.Contains("(static_cast<uint64_t>(dividend) * Multiplier) >> 32u", fastDivmodHeader, StringComparison.Ordinal);
        Assert.Contains("static_assert(FastDivmod(6u).Div(47u) == 7u && FastDivmod(6u).Mod(47u) == 5u", fastDivmodHeader, StringComparison.Ordinal);

        // 2. job_pool.cpp (#130): PersistentTileBatch + atomic NextTile.fetch_add + caller-thread ConsumePersistentTiles + queue reclamation
        string jobPoolSource = File.ReadAllText(Path.Combine(nativeGuardDir, "job_pool.cpp"));
        Assert.Contains("#include \"kongor_fast_divmod.h\"", jobPoolSource, StringComparison.Ordinal);
        Assert.Contains("struct alignas(64) PersistentTileBatch", jobPoolSource, StringComparison.Ordinal);
        Assert.Contains("batch->NextTile.fetch_add(1, std::memory_order_relaxed)", jobPoolSource, StringComparison.Ordinal);
        Assert.Contains("ConsumePersistentTiles(&batch);", jobPoolSource, StringComparison.Ordinal);
        Assert.DoesNotContain("new ParallelChunkContext", jobPoolSource, StringComparison.Ordinal);

        // 3. asset_prewarmer.cpp (#131): thread_local 256 KiB aligned scratch buffer instead of per-file std::vector<char> heap allocations
        string prewarmerSource = File.ReadAllText(Path.Combine(nativeGuardDir, "asset_prewarmer.cpp"));
        Assert.Contains("static thread_local alignas(64) uint8_t t_prewarmScratch[kPrewarmScratchBytes];", prewarmerSource, StringComparison.Ordinal);
        Assert.DoesNotContain("std::vector<char> buffer", prewarmerSource, StringComparison.Ordinal);

        // 4. simd_ray_bridge.cpp (#132): AVX2 PopulateRayPacket8Simd + 64-box stack tile + _mm_prefetch lookahead without std::vector<AABB2D>
        string simdRaySource = File.ReadAllText(Path.Combine(nativeGuardDir, "simd_ray_bridge.cpp"));
        Assert.Contains("PopulateRayPacket8Simd(", simdRaySource, StringComparison.Ordinal);
        Assert.Contains("alignas(32) Kongor::PredictivePathing::AABB2D tileBoxes[kTileBoxes];", simdRaySource, StringComparison.Ordinal);
        Assert.Contains("_mm_prefetch(", simdRaySource, StringComparison.Ordinal);
        Assert.DoesNotContain("std::vector<AABB2D>", simdRaySource, StringComparison.Ordinal);
    }

    private static void VerifyFastDivmodExact(FastDivmod fast, uint n, uint d)
    {
        uint expectedQ = n / d;
        uint expectedR = n % d;

        uint actualQ = fast.DivMod(n, out uint actualR);
        Assert.Equal(expectedQ, actualQ);
        Assert.Equal(expectedR, actualR);
        Assert.Equal(expectedQ, fast.Div(n));
        Assert.Equal(expectedR, fast.Mod(n));
    }
}

