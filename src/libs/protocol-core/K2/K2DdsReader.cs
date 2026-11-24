// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.IO.Compression;

namespace ASPIRE.Common.K2;

public static class K2DdsReader
{
    public static (byte[] Rgba, int Width, int Height) DecodeDds(Stream ddsStream)
    {
        using BinaryReader reader = new(ddsStream, Encoding.ASCII, true);

        byte[] magicBytes = reader.ReadBytes(4);
        string magic = Encoding.ASCII.GetString(magicBytes);
        if (magic != "DDS ") throw new InvalidDataException($"Invalid DDS header: expected 'DDS ', got '{magic}'");

        int headerSize = reader.ReadInt32();
        int flags = reader.ReadInt32();
        int height = reader.ReadInt32();
        int width = reader.ReadInt32();
        int pitchOrLinearSize = reader.ReadInt32();
        int depth = reader.ReadInt32();
        int mipMapCount = reader.ReadInt32();
        byte[] reserved1 = reader.ReadBytes(44);
        int pfSize = reader.ReadInt32();
        int pfFlags = reader.ReadInt32();
        string fourCC = Encoding.ASCII.GetString(reader.ReadBytes(4));

        // Skip remainder of 128-byte DDS header
        ddsStream.Seek(128, SeekOrigin.Begin);

        if (width <= 0 || height <= 0 || width > 8192 || height > 8192)
            throw new InvalidDataException($"Invalid DDS dimensions: {width}x{height}");

        byte[] rgba = new byte[width * height * 4];

        if (fourCC == "DXT1")
        {
            DecodeDxt1(reader, rgba, width, height);
        }
        else if (fourCC == "DXT3")
        {
            DecodeDxt3(reader, rgba, width, height);
        }
        else if (fourCC == "DXT5")
        {
            DecodeDxt5(reader, rgba, width, height);
        }
        else
        {
            // Fallback uncompressed or read directly
            int totalBytes = width * height * 4;
            byte[] raw = reader.ReadBytes(Math.Min(totalBytes, (int)(ddsStream.Length - ddsStream.Position)));
            Array.Copy(raw, rgba, raw.Length);
        }

        return (rgba, width, height);
    }

    public static (byte[] Rgba, int Width, int Height) DecodeRxgbNormal(Stream ddsStream)
    {
        (byte[] rawRgba, int width, int height) = DecodeDds(ddsStream);
        byte[] normalRgba = new byte[rawRgba.Length];

        for (int i = 0; i < rawRgba.Length; i += 4)
        {
            byte rx = rawRgba[i + 3]; // In RxGB DXT5, X-normal is in the Alpha channel
            byte gy = rawRgba[i + 1]; // Y-normal is in the Green channel

            float nx = rx / 255.0f * 2.0f - 1.0f;
            float ny = gy / 255.0f * 2.0f - 1.0f;
            float nzSq = 1.0f - nx * nx - ny * ny;
            float nz = nzSq > 0.0f ? MathF.Sqrt(nzSq) : 0.0f;

            byte bz = (byte)Math.Clamp((int)((nz * 0.5f + 0.5f) * 255.0f), 0, 255);

            normalRgba[i + 0] = rx;
            normalRgba[i + 1] = gy;
            normalRgba[i + 2] = bz;
            normalRgba[i + 3] = 255;
        }

        return (normalRgba, width, height);
    }

    public static (byte[] Rgba, int Width, int Height) ConvertSpecularToPbr(byte[] specRgba, int width, int height)
    {
        byte[] pbrRgba = new byte[specRgba.Length];

        for (int i = 0; i < specRgba.Length; i += 4)
        {
            byte r = specRgba[i + 0];
            byte g = specRgba[i + 1];
            byte b = specRgba[i + 2];

            float specIntensity = (r * 0.299f + g * 0.587f + b * 0.114f) / 255.0f;
            float roughness = Math.Clamp(1.0f - specIntensity * 0.85f, 0.10f, 0.95f);
            float metalness = specIntensity > 0.40f
                ? Math.Clamp((specIntensity - 0.35f) * 1.4f, 0.0f, 0.90f)
                : 0.0f;

            // glTF PBR standard: R = (unused), G = Roughness, B = Metallic, A = 255
            pbrRgba[i + 0] = 255;
            pbrRgba[i + 1] = (byte)Math.Clamp((int)(roughness * 255.0f), 0, 255);
            pbrRgba[i + 2] = (byte)Math.Clamp((int)(metalness * 255.0f), 0, 255);
            pbrRgba[i + 3] = 255;
        }

        return (pbrRgba, width, height);
    }

    public static (byte[] Rgba, int Width, int Height) DecodeTga(Stream tgaStream)
    {
        using BinaryReader reader = new(tgaStream, Encoding.ASCII, true);

        byte idLength = reader.ReadByte();
        byte colorMapType = reader.ReadByte();
        byte imageType = reader.ReadByte();
        short colorMapOrigin = reader.ReadInt16();
        short colorMapLength = reader.ReadInt16();
        byte colorMapEntrySize = reader.ReadByte();
        short xOrigin = reader.ReadInt16();
        short yOrigin = reader.ReadInt16();
        int width = reader.ReadInt16();
        int height = reader.ReadInt16();
        byte pixelDepth = reader.ReadByte();
        byte imageDescriptor = reader.ReadByte();

        if (idLength > 0) reader.ReadBytes(idLength);

        int bytesPerPixel = pixelDepth / 8;
        if (bytesPerPixel is < 3 or > 4)
            throw new NotSupportedException($"Unsupported TGA pixel depth: {pixelDepth} bpp");

        bool isRle = imageType is 10 or 9;
        bool isUncompressed = imageType is 2 or 1;

        if (!isRle && !isUncompressed) throw new NotSupportedException($"Unsupported TGA image type: {imageType}");

        int totalPixels = width * height;
        byte[] rawPixels = new byte[totalPixels * bytesPerPixel];

        if (isUncompressed)
        {
            reader.ReadExactly(rawPixels);
        }
        else
        {
            int pixelIndex = 0;
            while (pixelIndex < totalPixels)
            {
                byte packetHeader = reader.ReadByte();
                int count = (packetHeader & 0x7F) + 1;

                if ((packetHeader & 0x80) != 0)
                {
                    byte[] pixel = reader.ReadBytes(bytesPerPixel);
                    for (int i = 0; i < count && pixelIndex < totalPixels; i++)
                    {
                        Array.Copy(pixel, 0, rawPixels, pixelIndex * bytesPerPixel, bytesPerPixel);
                        pixelIndex++;
                    }
                }
                else
                {
                    int bytesToRead = count * bytesPerPixel;
                    byte[] block = reader.ReadBytes(bytesToRead);
                    Array.Copy(block, 0, rawPixels, pixelIndex * bytesPerPixel, block.Length);
                    pixelIndex += count;
                }
            }
        }

        byte[] rgba = new byte[totalPixels * 4];
        bool flipVertical = (imageDescriptor & 0x20) == 0;

        for (int y = 0; y < height; y++)
        {
            int srcY = flipVertical ? height - 1 - y : y;
            for (int x = 0; x < width; x++)
            {
                int srcIdx = (srcY * width + x) * bytesPerPixel;
                int dstIdx = (y * width + x) * 4;

                byte b = rawPixels[srcIdx];
                byte g = rawPixels[srcIdx + 1];
                byte r = rawPixels[srcIdx + 2];
                byte a = bytesPerPixel == 4 ? rawPixels[srcIdx + 3] : (byte)255;

                rgba[dstIdx] = r;
                rgba[dstIdx + 1] = g;
                rgba[dstIdx + 2] = b;
                rgba[dstIdx + 3] = a;
            }
        }

        return (rgba, width, height);
    }

    public static byte[] EncodeRgbaToPng(byte[] rgba, int width, int height, bool flipVertical = false)
    {
        using MemoryStream ms = new();
        using BinaryWriter bw = new(ms);

        // PNG signature
        bw.Write([137, 80, 78, 71, 13, 10, 26, 10]);

        // IHDR Chunk
        using (MemoryStream ihdrStream = new())
        {
            using (BinaryWriter ihdrWriter = new(ihdrStream))
            {
                ihdrWriter.Write(SwapBytes((uint)width));
                ihdrWriter.Write(SwapBytes((uint)height));
                ihdrWriter.Write((byte)8); // 8 bits per channel
                ihdrWriter.Write((byte)6); // RGBA
                ihdrWriter.Write((byte)0); // Deflate
                ihdrWriter.Write((byte)0); // Filter
                ihdrWriter.Write((byte)0); // Interlace
            }

            WriteChunk(bw, "IHDR", ihdrStream.ToArray());
        }

        // IDAT Chunk
        byte[] scanlines = new byte[height * (1 + width * 4)];
        int rowBytes = width * 4;
        int dstOffset = 0;
        for (int y = 0; y < height; y++)
        {
            int srcY = flipVertical ? height - 1 - y : y;
            int srcOffset = srcY * rowBytes;

            scanlines[dstOffset++] = 0; // Filter byte: None
            Buffer.BlockCopy(rgba, srcOffset, scanlines, dstOffset, rowBytes);
            dstOffset += rowBytes;
        }

        using (MemoryStream compMs = new())
        {
            using (ZLibStream zlib = new(compMs, CompressionLevel.Optimal, true))
            {
                zlib.Write(scanlines, 0, scanlines.Length);
            }

            WriteChunk(bw, "IDAT", compMs.ToArray());
        }

        // IEND Chunk
        WriteChunk(bw, "IEND", []);

        return ms.ToArray();
    }

    private static void DecodeDxt1(BinaryReader reader, byte[] rgba, int width, int height)
    {
        for (int by = 0; by < height; by += 4)
        for (int bx = 0; bx < width; bx += 4)
        {
            ushort c0 = reader.ReadUInt16();
            ushort c1 = reader.ReadUInt16();
            uint cBits = reader.ReadUInt32();

            RgbColor color0 = UnpackRgb565(c0);
            RgbColor color1 = UnpackRgb565(c1);

            RgbColor[] colors = new RgbColor[4];
            colors[0] = color0;
            colors[1] = color1;
            if (c0 > c1)
            {
                colors[2] = new RgbColor((byte)((2 * color0.R + color1.R) / 3),
                    (byte)((2 * color0.G + color1.G) / 3), (byte)((2 * color0.B + color1.B) / 3));
                colors[3] = new RgbColor((byte)((color0.R + 2 * color1.R) / 3),
                    (byte)((color0.G + 2 * color1.G) / 3), (byte)((color0.B + 2 * color1.B) / 3));
            }
            else
            {
                colors[2] = new RgbColor((byte)((color0.R + color1.R) / 2), (byte)((color0.G + color1.G) / 2),
                    (byte)((color0.B + color1.B) / 2));
                colors[3] = new RgbColor(0, 0, 0);
            }

            for (int py = 0; py < 4; py++)
            for (int px = 0; px < 4; px++)
            {
                int x = bx + px;
                int y = by + py;
                if (x < width && y < height)
                {
                    int pixIdx = py * 4 + px;
                    int cIdx = (int)((cBits >> (pixIdx * 2)) & 3);
                    RgbColor c = colors[cIdx];
                    byte alpha = c0 <= c1 && cIdx == 3 ? (byte)0 : (byte)255;

                    int dstIdx = (y * width + x) * 4;
                    rgba[dstIdx + 0] = c.R;
                    rgba[dstIdx + 1] = c.G;
                    rgba[dstIdx + 2] = c.B;
                    rgba[dstIdx + 3] = alpha;
                }
            }
        }
    }

    private static void DecodeDxt3(BinaryReader reader, byte[] rgba, int width, int height)
    {
        for (int by = 0; by < height; by += 4)
        for (int bx = 0; bx < width; bx += 4)
        {
            ulong alphaBits = reader.ReadUInt64();
            ushort c0 = reader.ReadUInt16();
            ushort c1 = reader.ReadUInt16();
            uint cBits = reader.ReadUInt32();

            RgbColor color0 = UnpackRgb565(c0);
            RgbColor color1 = UnpackRgb565(c1);

            RgbColor[] colors =
            [
                color0,
                color1,
                new((byte)((2 * color0.R + color1.R) / 3), (byte)((2 * color0.G + color1.G) / 3),
                    (byte)((2 * color0.B + color1.B) / 3)),
                new((byte)((color0.R + 2 * color1.R) / 3), (byte)((color0.G + 2 * color1.G) / 3),
                    (byte)((color0.B + 2 * color1.B) / 3))
            ];

            for (int py = 0; py < 4; py++)
            for (int px = 0; px < 4; px++)
            {
                int x = bx + px;
                int y = by + py;
                if (x < width && y < height)
                {
                    int pixIdx = py * 4 + px;
                    int a4 = (int)((alphaBits >> (pixIdx * 4)) & 0xF);
                    byte alpha = (byte)((a4 << 4) | a4);

                    int cIdx = (int)((cBits >> (pixIdx * 2)) & 3);
                    RgbColor c = colors[cIdx];

                    int dstIdx = (y * width + x) * 4;
                    rgba[dstIdx + 0] = c.R;
                    rgba[dstIdx + 1] = c.G;
                    rgba[dstIdx + 2] = c.B;
                    rgba[dstIdx + 3] = alpha;
                }
            }
        }
    }

    private static void DecodeDxt5(BinaryReader reader, byte[] rgba, int width, int height)
    {
        for (int by = 0; by < height; by += 4)
        for (int bx = 0; bx < width; bx += 4)
        {
            byte a0 = reader.ReadByte();
            byte a1 = reader.ReadByte();
            byte[] aBytes = reader.ReadBytes(6);
            ulong a64 = 0;
            for (int i = 0; i < 6; i++) a64 |= (ulong)aBytes[i] << (i * 8);

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

            ushort c0 = reader.ReadUInt16();
            ushort c1 = reader.ReadUInt16();
            uint cBits = reader.ReadUInt32();

            RgbColor color0 = UnpackRgb565(c0);
            RgbColor color1 = UnpackRgb565(c1);

            RgbColor[] colors =
            [
                color0,
                color1,
                new((byte)((2 * color0.R + color1.R) / 3), (byte)((2 * color0.G + color1.G) / 3),
                    (byte)((2 * color0.B + color1.B) / 3)),
                new((byte)((color0.R + 2 * color1.R) / 3), (byte)((color0.G + 2 * color1.G) / 3),
                    (byte)((color0.B + 2 * color1.B) / 3))
            ];

            for (int py = 0; py < 4; py++)
            for (int px = 0; px < 4; px++)
            {
                int x = bx + px;
                int y = by + py;
                if (x < width && y < height)
                {
                    int pixIdx = py * 4 + px;
                    int aIdx = (int)((a64 >> (pixIdx * 3)) & 7);
                    int cIdx = (int)((cBits >> (pixIdx * 2)) & 3);

                    byte alpha = alphas[aIdx];
                    RgbColor c = colors[cIdx];

                    int dstIdx = (y * width + x) * 4;
                    rgba[dstIdx + 0] = c.R;
                    rgba[dstIdx + 1] = c.G;
                    rgba[dstIdx + 2] = c.B;
                    rgba[dstIdx + 3] = alpha;
                }
            }
        }
    }

    private static RgbColor UnpackRgb565(ushort c)
    {
        byte r = (byte)(((c >> 11) & 0x1F) * 255 / 31);
        byte g = (byte)(((c >> 5) & 0x3F) * 255 / 63);
        byte b = (byte)((c & 0x1F) * 255 / 31);
        return new RgbColor(r, g, b);
    }

    private static void WriteChunk(BinaryWriter bw, string type, byte[] data)
    {
        bw.Write(SwapBytes((uint)data.Length));
        byte[] typeBytes = Encoding.ASCII.GetBytes(type);
        bw.Write(typeBytes);
        if (data.Length > 0) bw.Write(data);

        uint crc = Crc32(typeBytes, data);
        bw.Write(SwapBytes(crc));
    }

    private static uint SwapBytes(uint x)
    {
        return ((x & 0x000000FF) << 24) |
               ((x & 0x0000FF00) << 8) |
               ((x & 0x00FF0000) >> 8) |
               ((x & 0xFF000000) >> 24);
    }

    private static uint Crc32(byte[] type, byte[] data)
    {
        uint crc = 0xFFFFFFFF;
        UpdateCrc(ref crc, type);
        UpdateCrc(ref crc, data);
        return ~crc;
    }

    private static void UpdateCrc(ref uint crc, byte[] bytes)
    {
        for (int i = 0; i < bytes.Length; i++)
        {
            crc ^= bytes[i];
            for (int j = 0; j < 8; j++) crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320 : crc >> 1;
        }
    }

    private readonly struct RgbColor(byte r, byte g, byte b)
    {
        public byte R { get; } = r;
        public byte G { get; } = g;
        public byte B { get; } = b;
    }
}
