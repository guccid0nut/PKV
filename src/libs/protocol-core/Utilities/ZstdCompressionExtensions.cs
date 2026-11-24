// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.IO.Compression;

namespace ASPIRE.Common.Utilities;

/// <summary>
///     High-performance Zstandard (Zstd) streaming compression and decompression extensions.
/// </summary>
public static class ZstdCompressionExtensions
{
    public const uint ZstdMagicNumber = 0xFD2FB528;

    /// <summary>
    ///     Checks if the byte header starts with the 4-byte Zstandard frame magic header (0xFD2FB528).
    /// </summary>
    public static bool IsZstdCompressed(ReadOnlySpan<byte> header)
    {
        if (header.Length < 4) return false;

        return BitConverter.ToUInt32(header[..4]) == ZstdMagicNumber;
    }

    /// <summary>
    ///     Compresses the source stream to the destination stream using Zstandard with a specified compression level.
    /// </summary>
    public static async Task CompressZstdAsync(this Stream source, Stream destination, int level = 3,
        CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(destination);

        CompressionLevel compLevel = level switch
        {
            <= 1 => CompressionLevel.Fastest,
            >= 9 => CompressionLevel.SmallestSize,
            _ => CompressionLevel.Optimal
        };

        await using ZstandardStream zstdStream = new(destination, compLevel, leaveOpen: true);
        await source.CopyToAsync(zstdStream, token);
        await zstdStream.FlushAsync(token);
    }

    /// <summary>
    ///     Decompresses the source Zstandard stream to the destination stream.
    /// </summary>
    public static async Task DecompressZstdAsync(this Stream source, Stream destination,
        CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(destination);

        await using ZstandardStream zstdStream = new(source, CompressionMode.Decompress, leaveOpen: true);
        await zstdStream.CopyToAsync(destination, token);
        await destination.FlushAsync(token);
    }

    /// <summary>
    ///     Compresses a byte buffer using Zstandard in-memory.
    /// </summary>
    public static byte[] CompressZstd(this ReadOnlySpan<byte> input, int level = 3)
    {
        CompressionLevel compLevel = level switch
        {
            <= 1 => CompressionLevel.Fastest,
            >= 9 => CompressionLevel.SmallestSize,
            _ => CompressionLevel.Optimal
        };

        using MemoryStream output = new();
        using (ZstandardStream zstd = new(output, compLevel, leaveOpen: true))
        {
            zstd.Write(input);
        }

        return output.ToArray();
    }

    /// <summary>
    ///     Decompresses a Zstandard-compressed byte buffer in-memory.
    /// </summary>
    public static byte[] DecompressZstd(this ReadOnlySpan<byte> input)
    {
        using MemoryStream inputMs = new(input.ToArray());
        using ZstandardStream zstd = new(inputMs, CompressionMode.Decompress);
        using MemoryStream output = new();
        zstd.CopyTo(output);
        return output.ToArray();
    }

    /// <summary>
    ///     Compresses an entity delta snapshot byte buffer using Zstandard Netcode Dictionary.
    /// </summary>
    public static byte[] CompressNetcodeDelta(this ReadOnlySpan<byte> input, int level = 3)
    {
        return Netcode.ZstdDeltaCompressor.CompressDelta(input, level);
    }

    /// <summary>
    ///     Decompresses a Zstandard Netcode Dictionary compressed entity delta buffer.
    /// </summary>
    public static byte[] DecompressNetcodeDelta(this ReadOnlySpan<byte> input)
    {
        return Netcode.ZstdDeltaCompressor.DecompressDelta(input);
    }
}

