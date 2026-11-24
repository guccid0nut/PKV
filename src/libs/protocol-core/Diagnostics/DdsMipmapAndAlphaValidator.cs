// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System;
using System.Buffers.Binary;
using System.Text;

namespace Kongor.Protocol.Diagnostics;

public sealed record DdsValidationReport(
    bool IsValidHeader,
    string FourCC,
    int Width,
    int Height,
    int Depth,
    int DeclaredMipCount,
    int ValidMipLevelsPresent,
    long ActualFileBytes,
    long ExpectedTotalFileBytes,
    bool IsTruncatedMipmapChain,
    bool HasNonOpaqueAlpha,
    bool IsDeadAlphaOpaqueCandidate,
    long EstimatedVramSavingsBytes,
    string? DiagnosticWarning);

/// <summary>
/// Xash3D-FWGS load_dds.c inspired DDS mipmap chain completeness validator and
/// DXT3/DXT5 dead-alpha (100% opaque BC2/BC3 -> BC1 50% VRAM savings) auditor (#142).
/// </summary>
public static class DdsMipmapAndAlphaValidator
{
    private const uint DdsMagic = 0x20534444; // "DDS "

    public static DdsValidationReport ValidateDdsBuffer(ReadOnlySpan<byte> ddsBytes)
    {
        if (ddsBytes.Length < 128)
        {
            return new DdsValidationReport(
                false, string.Empty, 0, 0, 0, 0, 0,
                ddsBytes.Length, 128, true, false, false, 0,
                $"DDS buffer is truncated ({ddsBytes.Length} < 128 bytes header).");
        }

        uint magic = BinaryPrimitives.ReadUInt32LittleEndian(ddsBytes.Slice(0, 4));
        if (magic != DdsMagic)
        {
            return new DdsValidationReport(
                false, string.Empty, 0, 0, 0, 0, 0,
                ddsBytes.Length, 128, true, false, false, 0,
                $"Invalid DDS magic 0x{magic:X8} (expected 0x20534444 'DDS ').");
        }

        int height = (int)BinaryPrimitives.ReadUInt32LittleEndian(ddsBytes.Slice(12, 4));
        int width  = (int)BinaryPrimitives.ReadUInt32LittleEndian(ddsBytes.Slice(16, 4));
        int depth  = (int)BinaryPrimitives.ReadUInt32LittleEndian(ddsBytes.Slice(24, 4));
        int rawMips = (int)BinaryPrimitives.ReadUInt32LittleEndian(ddsBytes.Slice(28, 4));

        if (depth <= 0)
        {
            depth = 1;
        }
        int declaredMips = rawMips <= 0 ? 1 : Math.Min(rawMips, 16);

        uint pfFlags = BinaryPrimitives.ReadUInt32LittleEndian(ddsBytes.Slice(80, 4));
        string fourCC = Encoding.ASCII.GetString(ddsBytes.Slice(84, 4)).Trim('\0', ' ');
        int rgbBitCount = (int)BinaryPrimitives.ReadUInt32LittleEndian(ddsBytes.Slice(88, 4));
        uint aBitMask = BinaryPrimitives.ReadUInt32LittleEndian(ddsBytes.Slice(104, 4));

        bool isFourCC = (pfFlags & 0x4u) != 0;
        int headerBytes = (isFourCC && string.Equals(fourCC, "DX10", StringComparison.Ordinal)) ? 148 : 128;

        if (width <= 0 || height <= 0 || ddsBytes.Length < headerBytes)
        {
            return new DdsValidationReport(
                false, fourCC, width, height, depth, declaredMips, 0,
                ddsBytes.Length, headerBytes, true, false, false, 0,
                $"Invalid DDS dimensions ({width}x{height}) or truncated DX10 header.");
        }

        int blockSizeBytes = 0;
        if (isFourCC)
        {
            blockSizeBytes = fourCC switch
            {
                "DXT1" or "BC1" or "ATI1" or "BC4U" or "BC4S" => 8,
                "DXT2" or "DXT3" or "DXT4" or "DXT5" or "BC2" or "BC3" or "ATI2" or "BC5U" or "BC5S" or "DX10" => 16,
                _ => 16
            };
        }

        long expectedPayloadBytes = 0;
        long mip0Bytes = 0;
        int validMipLevels = 0;
        int w = width;
        int h = height;
        int d = depth;

        for (int mip = 0; mip < declaredMips; mip++)
        {
            long levelBytes;
            if (isFourCC)
            {
                long blocksX = Math.Max(1, (w + 3) / 4);
                long blocksY = Math.Max(1, (h + 3) / 4);
                long blocksZ = Math.Max(1, d);
                levelBytes = blocksX * blocksY * blocksZ * blockSizeBytes;
            }
            else
            {
                int bytesPerPixel = Math.Max(1, (rgbBitCount > 0 ? rgbBitCount : 32) / 8);
                levelBytes = (long)Math.Max(1, w) * Math.Max(1, h) * Math.Max(1, d) * bytesPerPixel;
            }

            if (mip == 0)
            {
                mip0Bytes = levelBytes;
            }

            expectedPayloadBytes += levelBytes;
            if (headerBytes + expectedPayloadBytes <= ddsBytes.Length)
            {
                validMipLevels = mip + 1;
            }

            w = Math.Max(1, w >> 1);
            h = Math.Max(1, h >> 1);
            d = Math.Max(1, d >> 1);
        }

        long expectedTotalBytes = headerBytes + expectedPayloadBytes;
        bool isTruncated = ddsBytes.Length < expectedTotalBytes;

        bool hasNonOpaqueAlpha = false;
        bool isDeadAlphaCandidate = false;

        int availableMip0Bytes = (int)Math.Min(mip0Bytes, Math.Max(0, ddsBytes.Length - headerBytes));
        ReadOnlySpan<byte> mip0Span = ddsBytes.Slice(headerBytes, availableMip0Bytes);

        if (isFourCC && (string.Equals(fourCC, "DXT3", StringComparison.Ordinal) ||
                         string.Equals(fourCC, "DXT2", StringComparison.Ordinal) ||
                         string.Equals(fourCC, "BC2", StringComparison.Ordinal)))
        {
            hasNonOpaqueAlpha = HasNonOpaqueDxt3Alpha(mip0Span);
            isDeadAlphaCandidate = !isTruncated && availableMip0Bytes >= 16 && !hasNonOpaqueAlpha;
        }
        else if (isFourCC && (string.Equals(fourCC, "DXT5", StringComparison.Ordinal) ||
                              string.Equals(fourCC, "DXT4", StringComparison.Ordinal) ||
                              string.Equals(fourCC, "BC3", StringComparison.Ordinal)))
        {
            hasNonOpaqueAlpha = HasNonOpaqueDxt5Alpha(mip0Span);
            isDeadAlphaCandidate = !isTruncated && availableMip0Bytes >= 16 && !hasNonOpaqueAlpha;
        }
        else if (!isFourCC && rgbBitCount == 32 && aBitMask != 0)
        {
            hasNonOpaqueAlpha = HasNonOpaqueRgba32Alpha(mip0Span);
        }

        long vramSavings = isDeadAlphaCandidate ? (expectedPayloadBytes / 2) : 0;

        string? warning = null;
        if (isTruncated)
        {
            warning = $"TRUNCATED MIPMAP CHAIN: Declared {declaredMips} mip(s) requiring {expectedTotalBytes} bytes, but file has {ddsBytes.Length} bytes ({validMipLevels} complete mip level(s)).";
        }
        else if (isDeadAlphaCandidate)
        {
            warning = $"DEAD ALPHA CHANNEL: {fourCC} texture is 100% opaque across all blocks; re-encoding as DXT1/BC1 saves {vramSavings} bytes (50% VRAM).";
        }

        return new DdsValidationReport(
            true,
            fourCC,
            width,
            height,
            depth,
            declaredMips,
            validMipLevels,
            ddsBytes.Length,
            expectedTotalBytes,
            isTruncated,
            hasNonOpaqueAlpha,
            isDeadAlphaCandidate,
            vramSavings,
            warning);
    }

    private static bool HasNonOpaqueDxt3Alpha(ReadOnlySpan<byte> mip0Span)
    {
        // Each 16-byte DXT3 block starts with 8 bytes (64 bits) of explicit 4-bit alpha values.
        // Fully opaque = 0xFFFFFFFFFFFFFFFF.
        int offset = 0;
        while (offset + 16 <= mip0Span.Length)
        {
            ulong alphaMask = BinaryPrimitives.ReadUInt64LittleEndian(mip0Span.Slice(offset, 8));
            if (alphaMask != ulong.MaxValue)
            {
                return true;
            }
            offset += 16;
        }
        return false;
    }

    private static bool HasNonOpaqueDxt5Alpha(ReadOnlySpan<byte> mip0Span)
    {
        // Each 16-byte DXT5 block starts with alpha0 (1B), alpha1 (1B), and 6 bytes (48 bits) of 3-bit indices.
        int offset = 0;
        while (offset + 16 <= mip0Span.Length)
        {
            byte alpha0 = mip0Span[offset];
            byte alpha1 = mip0Span[offset + 1];

            if (alpha0 < 255 || alpha1 < 255)
            {
                // If both endpoints are 255, every interpolated alpha (except code 6=0 in 5-step mode) is 255.
                // Check the 16 3-bit selectors in bytes [offset+2 .. offset+7].
                ulong packedIndices = 0;
                for (int b = 0; b < 6; b++)
                {
                    packedIndices |= ((ulong)mip0Span[offset + 2 + b]) << (b * 8);
                }

                for (int px = 0; px < 16; px++)
                {
                    int code = (int)((packedIndices >> (px * 3)) & 0x7UL);
                    int resolvedAlpha = ResolveDxt5AlphaSample(alpha0, alpha1, code);
                    if (resolvedAlpha < 255)
                    {
                        return true;
                    }
                }
            }
            else
            {
                // alpha0 == 255 && alpha1 == 255 -> 5-interpolated-alpha mode (alpha0 <= alpha1).
                // Code 6 evaluates to 0 (transparent)! Check if any pixel uses code 6.
                ulong packedIndices = 0;
                for (int b = 0; b < 6; b++)
                {
                    packedIndices |= ((ulong)mip0Span[offset + 2 + b]) << (b * 8);
                }

                for (int px = 0; px < 16; px++)
                {
                    int code = (int)((packedIndices >> (px * 3)) & 0x7UL);
                    if (code == 6)
                    {
                        return true;
                    }
                }
            }

            offset += 16;
        }
        return false;
    }

    private static int ResolveDxt5AlphaSample(byte alpha0, byte alpha1, int code)
    {
        if (code == 0) return alpha0;
        if (code == 1) return alpha1;
        if (alpha0 > alpha1)
        {
            return code switch
            {
                2 => (6 * alpha0 + 1 * alpha1) / 7,
                3 => (5 * alpha0 + 2 * alpha1) / 7,
                4 => (4 * alpha0 + 3 * alpha1) / 7,
                5 => (3 * alpha0 + 4 * alpha1) / 7,
                6 => (2 * alpha0 + 5 * alpha1) / 7,
                _ => (1 * alpha0 + 6 * alpha1) / 7
            };
        }
        else
        {
            return code switch
            {
                2 => (4 * alpha0 + 1 * alpha1) / 5,
                3 => (3 * alpha0 + 2 * alpha1) / 5,
                4 => (2 * alpha0 + 3 * alpha1) / 5,
                5 => (1 * alpha0 + 4 * alpha1) / 5,
                6 => 0,
                _ => 255
            };
        }
    }

    private static bool HasNonOpaqueRgba32Alpha(ReadOnlySpan<byte> mip0Span)
    {
        for (int i = 3; i < mip0Span.Length; i += 4)
        {
            if (mip0Span[i] < 255)
            {
                return true;
            }
        }
        return false;
    }
}
