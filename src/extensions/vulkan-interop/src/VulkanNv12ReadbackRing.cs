// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ============================================================================
// File: VulkanNv12ReadbackRing.cs
// System: NEXUS.VulkanInterop
// Policy: Strict Explicit Typing (NO 'var')
// Purpose: 6-slot async NV12 staging ring and CUTLASS NumericArrayConverter-inspired
//          SIMD Q16 BT.709 (full-range & studio-range) RGBA8->NV12 pipeline (#95, #126).
// ============================================================================

using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using ASPIRE.Common.Memory;

namespace ReplayVulkanRenderer;

public sealed class VulkanNv12ReadbackRing : IDisposable
{
    public const int SlotCount = 6;

    // Full-range BT.709 Q16 coefficients (Y in [0..255], UV in [0..255], U and V rows sum to exactly 0)
    private const int FullYR = 13933;
    private const int FullYG = 46871;
    private const int FullYB = 4732;
    private const int FullUR = -7509;
    private const int FullUG = -25259;
    private const int FullUB = 32768;
    private const int FullVR = 32768;
    private const int FullVG = -29763;
    private const int FullVB = -3005;

    // Studio limited-range BT.709 Q16 coefficients (Y in [16..235], UV in [16..240], U and V rows sum to exactly 0)
    private const int StudioYR = 11966;
    private const int StudioYG = 40254;
    private const int StudioYB = 4064;
    private const int StudioUR = -6596;
    private const int StudioUG = -22189;
    private const int StudioUB = 28785;
    private const int StudioVR = 28785;
    private const int StudioVG = -26145;
    private const int StudioVB = -2640;

    private static readonly FastDivmod s_slotDivmod = new((uint)SlotCount);

    private readonly uint _width;
    private readonly uint _height;
    private readonly byte[][] _stagingSlots;
    private readonly long[] _slotFrameNumbers;
    private readonly TensorLayout2D _yPlaneLayout;
    private readonly TensorLayout2D _uvPlaneLayout;
    private int _writeCursor;
    private int _readCursor;
    private int _inFlightCount;
    private bool _disposed;

    public VulkanNv12ReadbackRing(uint width, uint height)
    {
        if (width == 0 || height == 0 || (width % 2) != 0 || (height % 2) != 0)
        {
            throw new ArgumentException($"NV12 dimensions ({width}x{height}) must be positive and even.");
        }

        _width = width;
        _height = height;
        (_yPlaneLayout, _uvPlaneLayout) = GetPlaneLayouts(width, height);
        int slotBytes = ComputeNv12ByteLength(width, height);
        _stagingSlots = new byte[SlotCount][];
        _slotFrameNumbers = new long[SlotCount];

        for (int i = 0; i < SlotCount; i++)
        {
            _stagingSlots[i] = new byte[slotBytes];
            _slotFrameNumbers[i] = -1L;
        }
    }

    public static FastDivmod SlotDivmod => s_slotDivmod;
    public uint Width => _width;
    public uint Height => _height;
    public int InFlightCount => _inFlightCount;
    public int SlotByteLength => ComputeNv12ByteLength(_width, _height);
    public TensorLayout2D YPlaneLayout => _yPlaneLayout;
    public TensorLayout2D UvPlaneLayout => _uvPlaneLayout;

    /// <summary>
    ///     Returns the CuTe 2D plane layouts for the luma (Y) plane and interleaved chroma (UV) plane (#133).
    /// </summary>
    public static (TensorLayout2D YPlaneLayout, TensorLayout2D UvPlaneLayout) GetPlaneLayouts(uint width, uint height)
    {
        int w = checked((int)width);
        int h = checked((int)height);
        TensorLayout2D yLayout = TensorLayout2D.CreateRowMajor(w, h, w, 0);
        TensorLayout2D uvLayout = TensorLayout2D.CreateRowMajor(w, h / 2, w, w * h);
        return (yLayout, uvLayout);
    }

    /// <summary>
    ///     Returns the Q16 fixed-point BT.709 coefficients for full-range or studio limited-range NV12 conversion (#126).
    ///     Both U and V coefficient triples sum to exactly 0 so neutral gray (R == G == B) maps to exact 128 without bias.
    /// </summary>
    public static (int Y_R, int Y_G, int Y_B, int U_R, int U_G, int U_B, int V_R, int V_G, int V_B) GetBt709Q16Coefficients(bool fullRange = true)
    {
        if (fullRange)
        {
            return (FullYR, FullYG, FullYB, FullUR, FullUG, FullUB, FullVR, FullVG, FullVB);
        }

        return (StudioYR, StudioYG, StudioYB, StudioUR, StudioUG, StudioUB, StudioVR, StudioVG, StudioVB);
    }

    public static int ComputeRgbaByteLength(uint width, uint height)
    {
        return checked((int)(width * height * 4U));
    }

    public static int ComputeNv12ByteLength(uint width, uint height)
    {
        return checked((int)((width * height * 3U) / 2U));
    }

    public static double ComputePcieBandwidthSavingsRatio(uint width, uint height)
    {
        int rgbaBytes = ComputeRgbaByteLength(width, height);
        int nv12Bytes = ComputeNv12ByteLength(width, height);
        return 1.0 - ((double)nv12Bytes / rgbaBytes);
    }

    public int EnqueueRgbaFrame(ReadOnlySpan<byte> rgbaPixels, long frameNumber)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        int expectedRgba = ComputeRgbaByteLength(_width, _height);
        if (rgbaPixels.Length < expectedRgba)
        {
            throw new ArgumentException($"RGBA buffer length ({rgbaPixels.Length}) is smaller than required ({expectedRgba}).");
        }

        if (_inFlightCount >= SlotCount)
        {
            // Advance read cursor when ring wraps via FastDivmod (#129) so oldest slot is recycled deterministically
            _readCursor = (int)s_slotDivmod.Mod((uint)(_readCursor + 1));
            _inFlightCount = SlotCount - 1;
        }

        int slotIndex = _writeCursor;
        ConvertRgbaToNv12Bt709(rgbaPixels, _stagingSlots[slotIndex], _width, _height, fullRange: true);
        _slotFrameNumbers[slotIndex] = frameNumber;

        _writeCursor = (int)s_slotDivmod.Mod((uint)(_writeCursor + 1));
        _inFlightCount++;
        return slotIndex;
    }

    public bool TryDequeueNv12Frame(out ReadOnlySpan<byte> nv12Payload, out long frameNumber, out int slotIndex)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_inFlightCount <= 0)
        {
            nv12Payload = ReadOnlySpan<byte>.Empty;
            frameNumber = -1L;
            slotIndex = -1;
            return false;
        }

        slotIndex = _readCursor;
        nv12Payload = _stagingSlots[slotIndex];
        frameNumber = _slotFrameNumbers[slotIndex];

        _readCursor = (int)s_slotDivmod.Mod((uint)(_readCursor + 1));
        _inFlightCount--;
        return true;
    }

    public static void ConvertRgbaToNv12Bt709FullRange(
        ReadOnlySpan<byte> rgba,
        Span<byte> nv12Out,
        uint width,
        uint height)
    {
        ConvertRgbaToNv12Bt709(rgba, nv12Out, width, height, fullRange: true);
    }

    /// <summary>
    ///     CUTLASS NumericArrayConverter-style vectorized Q16 fixed-point RGBA8-&gt;NV12 converter (#126).
    ///     Processes 4x2 pixel blocks (8 RGBA pixels per step) using AVX2 256-bit integer lanes when supported,
    ///     with a Q16 scalar tail loop for widths where <c>width % 4 == 2</c> (e.g. 1366x768).
    /// </summary>
    public static void ConvertRgbaToNv12Bt709(
        ReadOnlySpan<byte> rgba,
        Span<byte> nv12Out,
        uint width,
        uint height,
        bool fullRange = true)
    {
        int w = (int)width;
        int h = (int)height;
        if (w <= 0 || h <= 0 || (w % 2) != 0 || (h % 2) != 0)
        {
            throw new ArgumentException($"NV12 dimensions ({width}x{height}) must be positive and even.");
        }

        int expectedRgba = ComputeRgbaByteLength(width, height);
        if (rgba.Length < expectedRgba)
        {
            throw new ArgumentException($"Input RGBA span ({rgba.Length}) is smaller than required ({expectedRgba}).");
        }

        (TensorLayout2D yLayout, TensorLayout2D uvLayout) = GetPlaneLayouts(width, height);
        int yPlaneSize = yLayout.Size;
        int requiredNv12 = yPlaneSize + uvLayout.Size;

        if (nv12Out.Length < requiredNv12)
        {
            throw new ArgumentException($"Output NV12 span ({nv12Out.Length}) is smaller than required ({requiredNv12}).");
        }

        (int yR, int yG, int yB, int uR, int uG, int uB, int vR, int vG, int vB) = GetBt709Q16Coefficients(fullRange);
        int yBias = fullRange ? 32768 : (32768 + (16 << 16));
        int yMin = fullRange ? 0 : 16;
        int yMax = fullRange ? 255 : 235;
        int uvMin = fullRange ? 0 : 16;
        int uvMax = fullRange ? 255 : 240;

        Span<byte> yPlane = nv12Out.Slice(yLayout.BaseOffset, yPlaneSize);
        Span<byte> uvPlane = nv12Out.Slice(uvLayout.BaseOffset, uvLayout.Size);

        bool useAvx2 = Avx2.IsSupported && w >= 4;
        Vector256<int> vecYR = useAvx2 ? Vector256.Create(yR) : default;
        Vector256<int> vecYG = useAvx2 ? Vector256.Create(yG) : default;
        Vector256<int> vecYB = useAvx2 ? Vector256.Create(yB) : default;
        Vector256<int> vecYBias = useAvx2 ? Vector256.Create(yBias) : default;
        Vector256<int> vecYMin = useAvx2 ? Vector256.Create(yMin) : default;
        Vector256<int> vecYMax = useAvx2 ? Vector256.Create(yMax) : default;

        for (int y = 0; y < h; y += 2)
        {
            int row0 = yLayout.GetOffsetUnchecked(0, y);
            int row1 = yLayout.GetOffsetUnchecked(0, y + 1);
            int uvRow = (y / 2) * w;
            int x = 0;

            if (useAvx2)
            {
                int simdLimit = w - 3;
                for (; x < simdLimit; x += 4)
                {
                    int idx00 = (row0 + x) * 4;
                    int idx01 = (row1 + x) * 4;

                    int r00 = rgba[idx00];
                    int g00 = rgba[idx00 + 1];
                    int b00 = rgba[idx00 + 2];

                    int r10 = rgba[idx00 + 4];
                    int g10 = rgba[idx00 + 5];
                    int b10 = rgba[idx00 + 6];

                    int r20 = rgba[idx00 + 8];
                    int g20 = rgba[idx00 + 9];
                    int b20 = rgba[idx00 + 10];

                    int r30 = rgba[idx00 + 12];
                    int g30 = rgba[idx00 + 13];
                    int b30 = rgba[idx00 + 14];

                    int r01 = rgba[idx01];
                    int g01 = rgba[idx01 + 1];
                    int b01 = rgba[idx01 + 2];

                    int r11 = rgba[idx01 + 4];
                    int g11 = rgba[idx01 + 5];
                    int b11 = rgba[idx01 + 6];

                    int r21 = rgba[idx01 + 8];
                    int g21 = rgba[idx01 + 9];
                    int b21 = rgba[idx01 + 10];

                    int r31 = rgba[idx01 + 12];
                    int g31 = rgba[idx01 + 13];
                    int b31 = rgba[idx01 + 14];

                    Vector256<int> vecR = Vector256.Create(r00, r10, r20, r30, r01, r11, r21, r31);
                    Vector256<int> vecG = Vector256.Create(g00, g10, g20, g30, g01, g11, g21, g31);
                    Vector256<int> vecB = Vector256.Create(b00, b10, b20, b30, b01, b11, b21, b31);

                    Vector256<int> yAccum = Avx2.Add(
                        Avx2.Add(Avx2.MultiplyLow(vecR, vecYR), Avx2.MultiplyLow(vecG, vecYG)),
                        Avx2.Add(Avx2.MultiplyLow(vecB, vecYB), vecYBias));
                    Vector256<int> yShifted = Avx2.ShiftRightArithmetic(yAccum, 16);
                    Vector256<int> yClamped = Avx2.Min(Avx2.Max(yShifted, vecYMin), vecYMax);

                    yPlane[row0 + x] = (byte)yClamped.GetElement(0);
                    yPlane[row0 + x + 1] = (byte)yClamped.GetElement(1);
                    yPlane[row0 + x + 2] = (byte)yClamped.GetElement(2);
                    yPlane[row0 + x + 3] = (byte)yClamped.GetElement(3);

                    yPlane[row1 + x] = (byte)yClamped.GetElement(4);
                    yPlane[row1 + x + 1] = (byte)yClamped.GetElement(5);
                    yPlane[row1 + x + 2] = (byte)yClamped.GetElement(6);
                    yPlane[row1 + x + 3] = (byte)yClamped.GetElement(7);

                    int sumR0 = r00 + r10 + r01 + r11;
                    int sumG0 = g00 + g10 + g01 + g11;
                    int sumB0 = b00 + b10 + b01 + b11;

                    int sumR1 = r20 + r30 + r21 + r31;
                    int sumG1 = g20 + g30 + g21 + g31;
                    int sumB1 = b20 + b30 + b21 + b31;

                    int u0 = (((uR * sumR0) + (uG * sumG0) + (uB * sumB0) + 131072) >> 18) + 128;
                    int v0 = (((vR * sumR0) + (vG * sumG0) + (vB * sumB0) + 131072) >> 18) + 128;
                    int u1 = (((uR * sumR1) + (uG * sumG1) + (uB * sumB1) + 131072) >> 18) + 128;
                    int v1 = (((vR * sumR1) + (vG * sumG1) + (vB * sumB1) + 131072) >> 18) + 128;

                    uvPlane[uvRow + x] = (byte)Math.Clamp(u0, uvMin, uvMax);
                    uvPlane[uvRow + x + 1] = (byte)Math.Clamp(v0, uvMin, uvMax);
                    uvPlane[uvRow + x + 2] = (byte)Math.Clamp(u1, uvMin, uvMax);
                    uvPlane[uvRow + x + 3] = (byte)Math.Clamp(v1, uvMin, uvMax);
                }
            }

            // Scalar Q16 tail loop for remaining 2x2 macroblocks (or non-AVX2 fallback)
            for (; x < w; x += 2)
            {
                int idx00 = (row0 + x) * 4;
                int idx10 = (row0 + x + 1) * 4;
                int idx01 = (row1 + x) * 4;
                int idx11 = (row1 + x + 1) * 4;

                int r00 = rgba[idx00];
                int g00 = rgba[idx00 + 1];
                int b00 = rgba[idx00 + 2];

                int r10 = rgba[idx10];
                int g10 = rgba[idx10 + 1];
                int b10 = rgba[idx10 + 2];

                int r01 = rgba[idx01];
                int g01 = rgba[idx01 + 1];
                int b01 = rgba[idx01 + 2];

                int r11 = rgba[idx11];
                int g11 = rgba[idx11 + 1];
                int b11 = rgba[idx11 + 2];

                int y00 = ((yR * r00) + (yG * g00) + (yB * b00) + yBias) >> 16;
                int y10 = ((yR * r10) + (yG * g10) + (yB * b10) + yBias) >> 16;
                int y01 = ((yR * r01) + (yG * g01) + (yB * b01) + yBias) >> 16;
                int y11 = ((yR * r11) + (yG * g11) + (yB * b11) + yBias) >> 16;

                yPlane[row0 + x] = (byte)Math.Clamp(y00, yMin, yMax);
                yPlane[row0 + x + 1] = (byte)Math.Clamp(y10, yMin, yMax);
                yPlane[row1 + x] = (byte)Math.Clamp(y01, yMin, yMax);
                yPlane[row1 + x + 1] = (byte)Math.Clamp(y11, yMin, yMax);

                int sumR = r00 + r10 + r01 + r11;
                int sumG = g00 + g10 + g01 + g11;
                int sumB = b00 + b10 + b01 + b11;

                int u = (((uR * sumR) + (uG * sumG) + (uB * sumB) + 131072) >> 18) + 128;
                int v = (((vR * sumR) + (vG * sumG) + (vB * sumB) + 131072) >> 18) + 128;

                uvPlane[uvRow + x] = (byte)Math.Clamp(u, uvMin, uvMax);
                uvPlane[uvRow + x + 1] = (byte)Math.Clamp(v, uvMin, uvMax);
            }
        }
    }

    /// <summary>
    ///     Scalar floating-point BT.709 reference implementation retained for parity verification against the Q16 SIMD converter (#126).
    /// </summary>
    public static void ConvertRgbaToNv12Bt709ScalarFloatReference(
        ReadOnlySpan<byte> rgba,
        Span<byte> nv12Out,
        uint width,
        uint height,
        bool fullRange = true)
    {
        int w = (int)width;
        int h = (int)height;
        int yPlaneSize = w * h;
        int requiredNv12 = yPlaneSize + (yPlaneSize / 2);

        if (nv12Out.Length < requiredNv12)
        {
            throw new ArgumentException($"Output NV12 span ({nv12Out.Length}) is smaller than required ({requiredNv12}).");
        }

        Span<byte> yPlane = nv12Out.Slice(0, yPlaneSize);
        Span<byte> uvPlane = nv12Out.Slice(yPlaneSize, yPlaneSize / 2);

        for (int y = 0; y < h; y += 2)
        {
            int row0 = y * w;
            int row1 = (y + 1) * w;
            int uvRow = (y / 2) * w;

            for (int x = 0; x < w; x += 2)
            {
                int idx00 = (row0 + x) * 4;
                int idx10 = (row0 + x + 1) * 4;
                int idx01 = (row1 + x) * 4;
                int idx11 = (row1 + x + 1) * 4;

                float r00 = rgba[idx00];
                float g00 = rgba[idx00 + 1];
                float b00 = rgba[idx00 + 2];

                float r10 = rgba[idx10];
                float g10 = rgba[idx10 + 1];
                float b10 = rgba[idx10 + 2];

                float r01 = rgba[idx01];
                float g01 = rgba[idx01 + 1];
                float b01 = rgba[idx01 + 2];

                float r11 = rgba[idx11];
                float g11 = rgba[idx11 + 1];
                float b11 = rgba[idx11 + 2];

                float avgR = 0.25f * (r00 + r10 + r01 + r11);
                float avgG = 0.25f * (g00 + g10 + g01 + g11);
                float avgB = 0.25f * (b00 + b10 + b01 + b11);

                if (fullRange)
                {
                    yPlane[row0 + x] = ClampToByte((0.2126f * r00) + (0.7152f * g00) + (0.0722f * b00), 0, 255);
                    yPlane[row0 + x + 1] = ClampToByte((0.2126f * r10) + (0.7152f * g10) + (0.0722f * b10), 0, 255);
                    yPlane[row1 + x] = ClampToByte((0.2126f * r01) + (0.7152f * g01) + (0.0722f * b01), 0, 255);
                    yPlane[row1 + x + 1] = ClampToByte((0.2126f * r11) + (0.7152f * g11) + (0.0722f * b11), 0, 255);

                    uvPlane[uvRow + x] = ClampToByte((-0.114572f * avgR) - (0.385428f * avgG) + (0.500000f * avgB) + 128.0f, 0, 255);
                    uvPlane[uvRow + x + 1] = ClampToByte((0.500000f * avgR) - (0.454153f * avgG) - (0.045847f * avgB) + 128.0f, 0, 255);
                }
                else
                {
                    yPlane[row0 + x] = ClampToByte((0.182586f * r00) + (0.614231f * g00) + (0.062007f * b00) + 16.0f, 16, 235);
                    yPlane[row0 + x + 1] = ClampToByte((0.182586f * r10) + (0.614231f * g10) + (0.062007f * b10) + 16.0f, 16, 235);
                    yPlane[row1 + x] = ClampToByte((0.182586f * r01) + (0.614231f * g01) + (0.062007f * b01) + 16.0f, 16, 235);
                    yPlane[row1 + x + 1] = ClampToByte((0.182586f * r11) + (0.614231f * g11) + (0.062007f * b11) + 16.0f, 16, 235);

                    uvPlane[uvRow + x] = ClampToByte((-0.100644f * avgR) - (0.338572f * avgG) + (0.439216f * avgB) + 128.0f, 16, 240);
                    uvPlane[uvRow + x + 1] = ClampToByte((0.439216f * avgR) - (0.398942f * avgG) - (0.040274f * avgB) + 128.0f, 16, 240);
                }
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static byte ClampToByte(float value, int minVal = 0, int maxVal = 255)
    {
        int rounded = (int)(value + 0.5f);
        return (byte)Math.Clamp(rounded, minVal, maxVal);
    }

    public void Dispose()
    {
        _disposed = true;
        _inFlightCount = 0;
    }
}
