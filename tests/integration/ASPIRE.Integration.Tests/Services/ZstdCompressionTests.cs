// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Diagnostics;
using ASPIRE.Common.Utilities;
using Xunit.Abstractions;

namespace ASPIRE.Tests.Services;

public class ZstdCompressionTests(ITestOutputHelper? output = null)
{
    [Fact]
    public void IsZstdCompressed_ValidHeader_ReturnsTrue()
    {
        // Arrange - Zstd Frame magic number is 0xFD2FB528 -> bytes in little endian: 0x28, 0xB5, 0x2F, 0xFD
        byte[] validHeader = [0x28, 0xB5, 0x2F, 0xFD, 0x00, 0x01];

        // Act
        bool isZstd = ZstdCompressionExtensions.IsZstdCompressed(validHeader);

        // Assert
        Assert.True(isZstd);
    }

    [Fact]
    public void IsZstdCompressed_InvalidHeader_ReturnsFalse()
    {
        // Arrange - Zip magic number is PK.. -> 0x50, 0x4B, 0x03, 0x04
        byte[] zipHeader = [0x50, 0x4B, 0x03, 0x04];

        // Act
        bool isZstd = ZstdCompressionExtensions.IsZstdCompressed(zipHeader);

        // Assert
        Assert.False(isZstd);
    }

    [Fact]
    public void CompressAndDecompress_SpanBytes_RoundtripsSuccessfully()
    {
        // Arrange
        byte[] original =
            Encoding.UTF8.GetBytes(
                "The quick brown fox jumps over the lazy dog repeatedly to test compression ratios across frames.");

        // Act
        byte[] compressed = original.CompressZstd(3);
        byte[] decompressed = compressed.DecompressZstd();

        // Assert
        Assert.NotNull(compressed);
        Assert.True(compressed.Length > 0);
        Assert.Equal(original, decompressed);
    }

    [Fact]
    public void CompressAndDecompress_EmptyArray_ReturnsEmpty()
    {
        // Arrange
        byte[] empty = [];

        // Act
        byte[] compressed = empty.CompressZstd();
        byte[] decompressed = compressed.DecompressZstd();

        // Assert
        Assert.NotEmpty(compressed);
        Assert.Empty(decompressed);
    }

    [Fact]
    public async Task CompressAndDecompress_AsyncStreams_RoundtripsSuccessfully()
    {
        // Arrange - 1 MB test payload
        byte[] largeData = new byte[1024 * 1024];
        Random.Shared.NextBytes(largeData);

        using MemoryStream inputStream = new(largeData);
        using MemoryStream compressedStream = new();

        // Act
        await inputStream.CompressZstdAsync(compressedStream, 3);
        compressedStream.Position = 0;

        using MemoryStream decompressedStream = new();
        await compressedStream.DecompressZstdAsync(decompressedStream);

        byte[] resultBytes = decompressedStream.ToArray();

        // Assert
        Assert.True(compressedStream.Length > 0);
        Assert.Equal(largeData.Length, resultBytes.Length);
        Assert.Equal(largeData, resultBytes);
    }

    [Fact]
    public void Benchmark_LargeAssetBundle_DecompressionThroughput()
    {
        // Arrange - 10 MB simulated game asset / texture bundle
        byte[] assetBundle = new byte[10 * 1024 * 1024];
        for (int i = 0; i < assetBundle.Length; i++)
            assetBundle[i] = (byte)(i % 251); // Realistic semi-compressible data pattern

        byte[] compressed = assetBundle.CompressZstd(3);

        // Warmup JIT & native allocator
        _ = new byte[] { 1, 2, 3, 4, 5 }.CompressZstd().DecompressZstd();

        // Act - Measure decompression throughput
        Stopwatch stopwatch = Stopwatch.StartNew();
        byte[] decompressed = compressed.DecompressZstd();
        stopwatch.Stop();

        double elapsedSeconds = stopwatch.Elapsed.TotalSeconds;
        double throughputMbPerSec = assetBundle.Length / (1024.0 * 1024.0) / elapsedSeconds;

        output?.WriteLine(
            $"[Zstd Benchmark] Decompressed {assetBundle.Length / (1024 * 1024)} MB in {stopwatch.ElapsedMilliseconds} ms ({throughputMbPerSec:F2} MB/s)!");

        // Assert
        Assert.Equal(assetBundle.Length, decompressed.Length);
        Assert.Equal(assetBundle, decompressed);
        Assert.True(stopwatch.ElapsedMilliseconds < 1500,
            $"Decompression took {stopwatch.ElapsedMilliseconds}ms, expected under 1500ms");
    }
}
