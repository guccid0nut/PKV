// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ============================================================================
// File: DdsDecoder.cs
// System: WILLOWMAKER Launcher - Direct DDS Texture Decoder
// Policy: Strict Explicit Typing (NO 'var') - DXT1 & DXT5 to BMP Bytes
// ============================================================================

using System.Text;

namespace WILLOWMAKER.Core.Services;

public static class DdsDecoder
{
    public static bool IsValidDdsHeader(ReadOnlySpan<byte> ddsBytes)
    {
        if (ddsBytes.Length < 4) return false;

        return ddsBytes[0] == (byte)'D' && ddsBytes[1] == (byte)'D' && ddsBytes[2] == (byte)'S' &&
               ddsBytes[3] == (byte)' ';
    }

    public static byte[]? DecodeDdsStreamToBmpBytes(Stream ddsStream)
    {
        using MemoryStream ms = new();
        ddsStream.CopyTo(ms);
        byte[] ddsBytes = ms.ToArray();

        if (ddsBytes.Length < 128) return null;

        if (ddsBytes[0] != (byte)'D' || ddsBytes[1] != (byte)'D' || ddsBytes[2] != (byte)'S' ||
            ddsBytes[3] != (byte)' ') return null;

        int height = (int)BitConverter.ToUInt32(ddsBytes, 12);
        int width = (int)BitConverter.ToUInt32(ddsBytes, 16);
        string fourCC = Encoding.ASCII.GetString(ddsBytes, 84, 4);

        byte[] bgraPixels = new byte[width * height * 4];
        int dataOffset = 128;

        if (fourCC == "DXT1")
            DecodeDxt1ToBgra(ddsBytes, dataOffset, width, height, bgraPixels);
        else if (fourCC == "DXT5")
            DecodeDxt5ToBgra(ddsBytes, dataOffset, width, height, bgraPixels);
        else
            return null;

        // Construct 54-byte uncompressed BMP header (Top-Down BGRA32)
        byte[] bmpBytes = new byte[54 + bgraPixels.Length];
        bmpBytes[0] = (byte)'B';
        bmpBytes[1] = (byte)'M';

        uint fileSize = (uint)bmpBytes.Length;
        BitConverter.GetBytes(fileSize).CopyTo(bmpBytes, 2);
        BitConverter.GetBytes(54).CopyTo(bmpBytes, 10);
        BitConverter.GetBytes(40).CopyTo(bmpBytes, 14);
        BitConverter.GetBytes(width).CopyTo(bmpBytes, 18);
        BitConverter.GetBytes(-height).CopyTo(bmpBytes, 22); // Negative height = Top-Down
        BitConverter.GetBytes((ushort)1).CopyTo(bmpBytes, 26);
        BitConverter.GetBytes((ushort)32).CopyTo(bmpBytes, 28);
        BitConverter.GetBytes((uint)bgraPixels.Length).CopyTo(bmpBytes, 34);

        Buffer.BlockCopy(bgraPixels, 0, bmpBytes, 54, bgraPixels.Length);
        return bmpBytes;
    }

    private static void DecodeDxt1ToBgra(byte[] data, int offset, int width, int height, byte[] bgraOutput)
    {
        int blockCountX = (width + 3) / 4;
        int blockCountY = (height + 3) / 4;
        int bytesPerBlock = 8;
        int blockOffset = offset;

        for (int by = 0; by < blockCountY; by++)
        for (int bx = 0; bx < blockCountX; bx++)
        {
            if (blockOffset + bytesPerBlock > data.Length)
                return;

            ushort c0 = BitConverter.ToUInt16(data, blockOffset);
            ushort c1 = BitConverter.ToUInt16(data, blockOffset + 2);
            uint lookup = BitConverter.ToUInt32(data, blockOffset + 4);

            byte r0 = (byte)(((c0 >> 11) & 0x1F) * 255 / 31);
            byte g0 = (byte)(((c0 >> 5) & 0x3F) * 255 / 63);
            byte b0 = (byte)((c0 & 0x1F) * 255 / 31);

            byte r1 = (byte)(((c1 >> 11) & 0x1F) * 255 / 31);
            byte g1 = (byte)(((c1 >> 5) & 0x3F) * 255 / 63);
            byte b1 = (byte)((c1 & 0x1F) * 255 / 31);

            byte[] r = new byte[4];
            byte[] g = new byte[4];
            byte[] b = new byte[4];
            byte[] a = new byte[4];

            r[0] = r0;
            g[0] = g0;
            b[0] = b0;
            a[0] = 255;
            r[1] = r1;
            g[1] = g1;
            b[1] = b1;
            a[1] = 255;

            if (c0 > c1)
            {
                r[2] = (byte)((2 * r0 + r1) / 3);
                g[2] = (byte)((2 * g0 + g1) / 3);
                b[2] = (byte)((2 * b0 + b1) / 3);
                a[2] = 255;
                r[3] = (byte)((r0 + 2 * r1) / 3);
                g[3] = (byte)((g0 + 2 * g1) / 3);
                b[3] = (byte)((b0 + 2 * b1) / 3);
                a[3] = 255;
            }
            else
            {
                r[2] = (byte)((r0 + r1) / 2);
                g[2] = (byte)((g0 + g1) / 2);
                b[2] = (byte)((b0 + b1) / 2);
                a[2] = 255;
                r[3] = 0;
                g[3] = 0;
                b[3] = 0;
                a[3] = 0;
            }

            for (int py = 0; py < 4; py++)
            for (int px = 0; px < 4; px++)
            {
                int x = bx * 4 + px;
                int y = by * 4 + py;

                if (x < width && y < height)
                {
                    int code = (int)((lookup >> (2 * (py * 4 + px))) & 0x03);
                    int outIdx = (y * width + x) * 4;

                    bgraOutput[outIdx + 0] = b[code];
                    bgraOutput[outIdx + 1] = g[code];
                    bgraOutput[outIdx + 2] = r[code];
                    bgraOutput[outIdx + 3] = a[code];
                }
            }

            blockOffset += bytesPerBlock;
        }
    }

    private static void DecodeDxt5ToBgra(byte[] data, int offset, int width, int height, byte[] bgraOutput)
    {
        int blockCountX = (width + 3) / 4;
        int blockCountY = (height + 3) / 4;
        int bytesPerBlock = 16;
        int blockOffset = offset;

        for (int by = 0; by < blockCountY; by++)
        for (int bx = 0; bx < blockCountX; bx++)
        {
            if (blockOffset + bytesPerBlock > data.Length)
                return;

            byte a0 = data[blockOffset];
            byte a1 = data[blockOffset + 1];

            byte[] alphas = new byte[8];
            alphas[0] = a0;
            alphas[1] = a1;

            if (a0 > a1)
            {
                alphas[2] = (byte)((6 * a0 + 1 * a1) / 7);
                alphas[3] = (byte)((5 * a0 + 2 * a1) / 7);
                alphas[4] = (byte)((4 * a0 + 3 * a1) / 7);
                alphas[5] = (byte)((3 * a0 + 4 * a1) / 7);
                alphas[6] = (byte)((2 * a0 + 5 * a1) / 7);
                alphas[7] = (byte)((1 * a0 + 6 * a1) / 7);
            }
            else
            {
                alphas[2] = (byte)((4 * a0 + 1 * a1) / 5);
                alphas[3] = (byte)((3 * a0 + 2 * a1) / 5);
                alphas[4] = (byte)((2 * a0 + 3 * a1) / 5);
                alphas[5] = (byte)((1 * a0 + 4 * a1) / 5);
                alphas[6] = 0;
                alphas[7] = 255;
            }

            ulong alphaBits = data[blockOffset + 2] |
                              ((ulong)data[blockOffset + 3] << 8) |
                              ((ulong)data[blockOffset + 4] << 16) |
                              ((ulong)data[blockOffset + 5] << 24) |
                              ((ulong)data[blockOffset + 6] << 32) |
                              ((ulong)data[blockOffset + 7] << 40);

            int colorOffset = blockOffset + 8;
            ushort c0 = BitConverter.ToUInt16(data, colorOffset);
            ushort c1 = BitConverter.ToUInt16(data, colorOffset + 2);
            uint lookup = BitConverter.ToUInt32(data, colorOffset + 4);

            byte r0 = (byte)(((c0 >> 11) & 0x1F) * 255 / 31);
            byte g0 = (byte)(((c0 >> 5) & 0x3F) * 255 / 63);
            byte b0 = (byte)((c0 & 0x1F) * 255 / 31);

            byte r1 = (byte)(((c1 >> 11) & 0x1F) * 255 / 31);
            byte g1 = (byte)(((c1 >> 5) & 0x3F) * 255 / 63);
            byte b1 = (byte)((c1 & 0x1F) * 255 / 31);

            byte[] r = new byte[4];
            byte[] g = new byte[4];
            byte[] b = new byte[4];

            r[0] = r0;
            g[0] = g0;
            b[0] = b0;
            r[1] = r1;
            g[1] = g1;
            b[1] = b1;
            r[2] = (byte)((2 * r0 + r1) / 3);
            g[2] = (byte)((2 * g0 + g1) / 3);
            b[2] = (byte)((2 * b0 + b1) / 3);
            r[3] = (byte)((r0 + 2 * r1) / 3);
            g[3] = (byte)((g0 + 2 * g1) / 3);
            b[3] = (byte)((b0 + 2 * b1) / 3);

            for (int py = 0; py < 4; py++)
            for (int px = 0; px < 4; px++)
            {
                int x = bx * 4 + px;
                int y = by * 4 + py;

                if (x < width && y < height)
                {
                    int pixelIndex = py * 4 + px;
                    int alphaCode = (int)((alphaBits >> (pixelIndex * 3)) & 0x07);
                    int colorCode = (int)((lookup >> (2 * pixelIndex)) & 0x03);

                    int outIdx = (y * width + x) * 4;

                    bgraOutput[outIdx + 0] = b[colorCode];
                    bgraOutput[outIdx + 1] = g[colorCode];
                    bgraOutput[outIdx + 2] = r[colorCode];
                    bgraOutput[outIdx + 3] = alphas[alphaCode];
                }
            }

            blockOffset += bytesPerBlock;
        }
    }
}
