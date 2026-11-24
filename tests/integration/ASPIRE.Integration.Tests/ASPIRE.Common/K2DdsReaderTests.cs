// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using ASPIRE.Common.K2;

namespace ASPIRE.Tests.ASPIRE.Common;

public sealed class K2DdsReaderTests
{
    [Fact]
    public void K2DdsReader_DecodeDds_ThrowsOnInvalidHeader()
    {
        byte[] invalidHeader = [.. "NOT_A_DDS_HEADER_DATA"u8];
        using MemoryStream stream = new(invalidHeader);

        Assert.Throws<InvalidDataException>(() => K2DdsReader.DecodeDds(stream));
    }

    [Fact]
    public void DecodeDds_WithValidDxt1HeaderAndBlock_DecodesCorrectDimensionsAndRgba()
    {
        using MemoryStream stream = new();
        using BinaryWriter writer = new(stream);

        // 1. DDS Magic "DDS "
        writer.Write(Encoding.ASCII.GetBytes("DDS "));
        // 2. Header: 124 bytes
        writer.Write(124); // dwSize
        writer.Write(0x1007); // dwFlags (DDSD_CAPS | DDSD_HEIGHT | DDSD_WIDTH | DDSD_PIXELFORMAT)
        writer.Write(4); // dwHeight (4 pixels)
        writer.Write(4); // dwWidth (4 pixels)
        writer.Write(8); // dwPitchOrLinearSize
        writer.Write(0); // dwDepth
        writer.Write(0); // dwMipMapCount
        writer.Write(new byte[44]); // dwReserved1[11]

        // DDS_PIXELFORMAT (32 bytes)
        writer.Write(32); // dwSize
        writer.Write(0x4); // dwFlags (DDPF_FOURCC)
        writer.Write(Encoding.ASCII.GetBytes("DXT1")); // dwFourCC
        writer.Write(0); // dwRGBBitCount
        writer.Write((uint)0); // dwRBitMask
        writer.Write((uint)0); // dwGBitMask
        writer.Write((uint)0); // dwBBitMask
        writer.Write((uint)0); // dwABitMask

        // Remainder of 128-byte header (caps, etc. -> 128 - 4 - 124 = 0)
        while (stream.Position < 128) writer.Write((byte)0);

        // DXT1 block: 8 bytes for a 4x4 block
        // Color 0: Pure Red in RGB565 -> 0xF800
        // Color 1: Pure Green in RGB565 -> 0x07E0
        // Bitmask for all 16 pixels pointing to Color 0 (00_00_00_00)
        writer.Write((ushort)0xF800);
        writer.Write((ushort)0x07E0);
        writer.Write((uint)0x00000000);

        stream.Seek(0, SeekOrigin.Begin);

        (byte[] rgba, int width, int height) = K2DdsReader.DecodeDds(stream);

        Assert.Equal(4, width);
        Assert.Equal(4, height);
        Assert.Equal(4 * 4 * 4, rgba.Length);

        // Verify pixel 0 is Red: R > 200, G == 0, B == 0, A == 255
        Assert.True(rgba[0] > 200);
        Assert.Equal(0, rgba[1]);
        Assert.Equal(0, rgba[2]);
        Assert.Equal(255, rgba[3]);
    }

    [Fact]
    public void DecodeDds_WithValidDxt5HeaderAndBlock_DecodesAlphaAndColor()
    {
        using MemoryStream stream = new();
        using BinaryWriter writer = new(stream);

        // 1. DDS Magic "DDS "
        writer.Write(Encoding.ASCII.GetBytes("DDS "));
        writer.Write(124); // dwSize
        writer.Write(0x1007); // dwFlags
        writer.Write(4); // dwHeight
        writer.Write(4); // dwWidth
        writer.Write(16); // dwPitchOrLinearSize
        writer.Write(0);
        writer.Write(0);
        writer.Write(new byte[44]);

        // PixelFormat
        writer.Write(32);
        writer.Write(0x4); // DDPF_FOURCC
        writer.Write(Encoding.ASCII.GetBytes("DXT5"));
        writer.Write(0);
        writer.Write((uint)0);
        writer.Write((uint)0);
        writer.Write((uint)0);
        writer.Write((uint)0);

        while (stream.Position < 128) writer.Write((byte)0);

        // DXT5 block: 16 bytes (8 bytes alpha block + 8 bytes color block)
        // Alpha block: Alpha0 = 255, Alpha1 = 0, 6 bytes indices (all 00 -> alpha0)
        writer.Write((byte)255);
        writer.Write((byte)0);
        writer.Write((byte)0x00);
        writer.Write((byte)0x00);
        writer.Write((byte)0x00);
        writer.Write((byte)0x00);
        writer.Write((byte)0x00);
        writer.Write((byte)0x00);

        // Color block (DXT1 format):
        // Color 0: Blue in RGB565 -> 0x001F
        // Color 1: Black in RGB565 -> 0x0000
        // All pixels index 0 -> Color 0
        writer.Write((ushort)0x001F);
        writer.Write((ushort)0x0000);
        writer.Write((uint)0x00000000);

        stream.Seek(0, SeekOrigin.Begin);

        (byte[] rgba, int width, int height) = K2DdsReader.DecodeDds(stream);

        Assert.Equal(4, width);
        Assert.Equal(4, height);
        Assert.Equal(64, rgba.Length);

        // Pixel 0: B > 200, R == 0, G == 0, A == 255
        Assert.Equal(0, rgba[0]);
        Assert.Equal(0, rgba[1]);
        Assert.True(rgba[2] > 200);
        Assert.Equal(255, rgba[3]);
    }

    [Fact]
    public void K2DdsReader_DecodeRxgbNormal_CalculatesZNormalAndSwizzlesChannels()
    {
        using MemoryStream stream = new();
        using BinaryWriter writer = new(stream);

        // DDS header for DXT5
        writer.Write(Encoding.ASCII.GetBytes("DDS "));
        writer.Write(124);
        writer.Write(0x1007);
        writer.Write(4);
        writer.Write(4);
        writer.Write(16);
        writer.Write(0);
        writer.Write(0);
        writer.Write(new byte[44]);
        writer.Write(32);
        writer.Write(0x4);
        writer.Write(Encoding.ASCII.GetBytes("DXT5"));
        writer.Write(0);
        writer.Write((uint)0);
        writer.Write((uint)0);
        writer.Write((uint)0);
        writer.Write((uint)0);

        while (stream.Position < 128) writer.Write((byte)0);

        // Alpha = 128 (Normal X = 0.0), Green = 128 (Normal Y = 0.0) -> Normal Z should be ~1.0 (255)
        writer.Write((byte)128); // Alpha0
        writer.Write((byte)0); // Alpha1
        writer.Write(new byte[6]); // Alpha indices (all 0 -> 128)

        // RGB565 with Green = 128 / 255 * 63 = 31 -> 0x03E0
        writer.Write((ushort)0x03E0);
        writer.Write((ushort)0x0000);
        writer.Write((uint)0x00000000);

        stream.Seek(0, SeekOrigin.Begin);

        (byte[] normalRgba, int width, int height) = K2DdsReader.DecodeRxgbNormal(stream);

        Assert.Equal(4, width);
        Assert.Equal(4, height);
        // Verify R (Rx), G (Gy), B (Nz), A (255)
        Assert.Equal(128, normalRgba[0]);
        Assert.True(normalRgba[1] > 100);
        Assert.True(normalRgba[2] > 200); // Z normal reconstructed
        Assert.Equal(255, normalRgba[3]);
    }

    [Fact]
    public void K2DdsReader_ConvertSpecularToPbr_GeneratesPbrMetallicRoughness()
    {
        byte[] specRgba = [255, 255, 255, 255, 0, 0, 0, 255]; // High spec vs zero spec
        (byte[] pbrRgba, int width, int height) = K2DdsReader.ConvertSpecularToPbr(specRgba, 2, 1);

        Assert.Equal(2, width);
        Assert.Equal(1, height);
        Assert.Equal(8, pbrRgba.Length);

        // High spec pixel: Low roughness, higher metallic
        Assert.Equal(255, pbrRgba[0]); // R unused
        Assert.True(pbrRgba[1] < 100); // Roughness (G) is low
        Assert.True(pbrRgba[2] > 100); // Metalness (B) is high
        Assert.Equal(255, pbrRgba[3]);

        // Zero spec pixel: High roughness, 0 metallic
        Assert.Equal(255, pbrRgba[4]);
        Assert.True(pbrRgba[5] > 200); // Roughness is high
        Assert.Equal(0, pbrRgba[6]); // Metalness is 0
        Assert.Equal(255, pbrRgba[7]);
    }

    [Fact]
    public void DecodeTga_WithUncompressedRgb_DecodesCorrectly()
    {
        using MemoryStream stream = new();
        using BinaryWriter writer = new(stream);

        // TGA Header (18 bytes)
        writer.Write((byte)0); // idLength
        writer.Write((byte)0); // colorMapType
        writer.Write((byte)2); // imageType (Uncompressed Truecolor)
        writer.Write((short)0); // colorMapOrigin
        writer.Write((short)0); // colorMapLength
        writer.Write((byte)0); // colorMapEntrySize
        writer.Write((short)0); // xOrigin
        writer.Write((short)0); // yOrigin
        writer.Write((short)2); // width (2 pixels)
        writer.Write((short)2); // height (2 pixels)
        writer.Write((byte)24); // pixelDepth (24-bit BGR)
        writer.Write((byte)0x20); // imageDescriptor (top-to-bottom)

        // 4 pixels BGR:
        // Pixel 0: B=255, G=0, R=0 (Blue)
        // Pixel 1: B=0, G=255, R=0 (Green)
        // Pixel 2: B=0, G=0, R=255 (Red)
        // Pixel 3: B=255, G=255, R=255 (White)
        writer.Write([255, 0, 0]);
        writer.Write([0, 255, 0]);
        writer.Write([0, 0, 255]);
        writer.Write([255, 255, 255]);

        stream.Seek(0, SeekOrigin.Begin);

        (byte[] rgba, int width, int height) = K2DdsReader.DecodeTga(stream);

        Assert.Equal(2, width);
        Assert.Equal(2, height);
        Assert.Equal(16, rgba.Length);

        // Pixel 0 should be RGBA: R=0, G=0, B=255, A=255
        Assert.Equal(0, rgba[0]);
        Assert.Equal(0, rgba[1]);
        Assert.Equal(255, rgba[2]);
        Assert.Equal(255, rgba[3]);

        // Pixel 2 should be RGBA: R=255, G=0, B=0, A=255
        Assert.Equal(255, rgba[8]);
        Assert.Equal(0, rgba[9]);
        Assert.Equal(0, rgba[10]);
        Assert.Equal(255, rgba[11]);
    }

    [Fact]
    public void K2DdsReader_EncodeRgbaToPng_ProducesValidPngHeaderAndChunks()
    {
        byte[] rgba = [255, 0, 0, 255, 0, 255, 0, 255, 0, 0, 255, 255, 255, 255, 255, 255];
        byte[] pngBytes = K2DdsReader.EncodeRgbaToPng(rgba, 2, 2);

        Assert.NotNull(pngBytes);
        Assert.True(pngBytes.Length > 30);

        // Verify PNG magic signature: 0x89, 'P', 'N', 'G', 0x0D, 0x0A, 0x1A, 0x0A
        byte[] pngSignature = [137, 80, 78, 71, 13, 10, 26, 10];
        for (int i = 0; i < pngSignature.Length; i++) Assert.Equal(pngSignature[i], pngBytes[i]);

        string rawString = Encoding.ASCII.GetString(pngBytes);
        Assert.Contains("IHDR", rawString);
        Assert.Contains("IDAT", rawString);
        Assert.Contains("IEND", rawString);
    }
}
