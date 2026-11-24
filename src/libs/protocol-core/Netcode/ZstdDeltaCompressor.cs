// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Buffers;
using System.Buffers.Binary;
using System.IO.Compression;
using System.Runtime.CompilerServices;
using ASPIRE.Common.Memory;
using ASPIRE.Common.Utilities;

namespace ASPIRE.Common.Netcode;

/// <summary>
///     High-performance, sub-millisecond Zstandard dictionary compressor and decompressor
///     for UDP entity delta snapshots (RFC 8878 compliant).
/// </summary>
public static class ZstdDeltaCompressor
{
    public const uint ZstdMagicNumber = 0xFD2FB528;
    public const uint NetcodeDictId = 0x484F4E31; // "HON1"
    public const int HeaderSize = 12; // 4B Magic + 4B Dict ID + 4B Uncompressed Length

    /// <summary>
    ///     Checks if the incoming UDP datagram is compressed with the Zstandard Netcode dictionary.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsDeltaCompressed(ReadOnlySpan<byte> packet)
    {
        if (packet.Length < HeaderSize) return false;

        uint magic = BinaryPrimitives.ReadUInt32LittleEndian(packet[0..4]);
        uint dictId = BinaryPrimitives.ReadUInt32LittleEndian(packet[4..8]);

        return magic == ZstdMagicNumber && dictId == NetcodeDictId;
    }

    /// <summary>
    ///     Extracts the expected uncompressed payload size from the delta frame header.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int GetUncompressedLength(ReadOnlySpan<byte> packet)
    {
        if (packet.Length < HeaderSize) return 0;
        return (int)BinaryPrimitives.ReadUInt32LittleEndian(packet[8..12]);
    }

    /// <summary>
    ///     Compresses an entity delta snapshot into the provided destination buffer using dictionary Zstandard.
    ///     Returns total bytes written (Header + Compressed Body).
    /// </summary>
    public static int CompressDelta(ReadOnlySpan<byte> source, Span<byte> destination, int level = 3)
    {
        if (destination.Length < HeaderSize + 16)
        {
            throw new ArgumentException("Destination buffer is too small for compressed delta frame.", nameof(destination));
        }

        // 1. Write Header (12 bytes)
        BinaryPrimitives.WriteUInt32LittleEndian(destination[0..4], ZstdMagicNumber);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[4..8], NetcodeDictId);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[8..12], (uint)source.Length);

        // 2. Fast path for empty payload
        if (source.IsEmpty)
        {
            return HeaderSize;
        }

        // 3. Zero-allocation compression using pooled ZstandardEncoder lease
        ZstdContextPool.EncoderLease lease = ZstdContextPool.RentEncoderLease(level);
        bool isFaulted = true;
        try
        {
            Span<byte> destBody = destination[HeaderSize..];
            OperationStatus status = lease.Encoder.Compress(source, destBody, out _, out int written, isFinalBlock: true);

            if (status == OperationStatus.Done)
            {
                isFaulted = false;
                return HeaderSize + written;
            }

            if (status == OperationStatus.DestinationTooSmall)
            {
                throw new InvalidOperationException("Compressed data exceeded destination buffer capacity.");
            }

            throw new InvalidOperationException($"Zstandard delta compression failed with unexpected status: {status}.");
        }
        finally
        {
            lease.Return(isFaulted);
        }
    }

    /// <summary>
    ///     Convenience allocation overload for non-hot-path compression.
    /// </summary>
    public static byte[] CompressDelta(ReadOnlySpan<byte> source, int level = 3)
    {
        byte[] buffer = ArrayPool<byte>.Shared.Rent(HeaderSize + source.Length + 64);
        try
        {
            int bytesWritten = CompressDelta(source, buffer, level);
            return buffer.AsSpan(0, bytesWritten).ToArray();
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>
    ///     Decompresses a Zstandard dictionary-compressed entity delta packet into destination span.
    /// </summary>
    public static int DecompressDelta(ReadOnlySpan<byte> source, Span<byte> destination)
    {
        if (!TryDecompressDelta(source, destination, out int bytesWritten))
        {
            throw new InvalidDataException("Failed to decompress delta packet. Corrupt or non-Zstd payload.");
        }
        return bytesWritten;
    }

    /// <summary>
    ///     Safely attempts to decompress an entity delta packet into destination span.
    /// </summary>
    public static bool TryDecompressDelta(ReadOnlySpan<byte> source, Span<byte> destination, out int bytesWritten)
    {
        bytesWritten = 0;
        if (source.Length < HeaderSize) return false;

        uint magic = BinaryPrimitives.ReadUInt32LittleEndian(source[0..4]);
        uint dictId = BinaryPrimitives.ReadUInt32LittleEndian(source[4..8]);
        int expectedLen = (int)BinaryPrimitives.ReadUInt32LittleEndian(source[8..12]);

        if (magic != ZstdMagicNumber || dictId != NetcodeDictId) return false;

        if (source.Length == HeaderSize || expectedLen == 0)
        {
            bytesWritten = 0;
            return true;
        }

        if (destination.Length < expectedLen)
        {
            return false;
        }

        ReadOnlySpan<byte> compressedBody = source[HeaderSize..];

        // 1. Primary path: dictionary-primed decompression via pooled ZstandardDecoder lease
        ZstdContextPool.DecoderLease dictLease = ZstdContextPool.RentDecoderLease(useDictionary: true);
        bool dictFaulted = true;
        try
        {
            OperationStatus status = dictLease.Decoder.Decompress(compressedBody, destination, out _, out int written);
            if (status == OperationStatus.Done && written == expectedLen)
            {
                dictFaulted = false;
                bytesWritten = written;
                return true;
            }
        }
        catch
        {
            // Dictionary-primed attempt encountered exception; allow fallback to raw decoder
        }
        finally
        {
            dictLease.Return(dictFaulted);
        }

        // 2. Fallback path: raw non-dictionary decompression via pooled raw decoder lease
        ZstdContextPool.DecoderLease fallbackLease = ZstdContextPool.RentDecoderLease(useDictionary: false);
        bool fallbackFaulted = true;
        try
        {
            OperationStatus fallbackStatus = fallbackLease.Decoder.Decompress(compressedBody, destination, out _, out int written);
            if (fallbackStatus == OperationStatus.Done && written == expectedLen)
            {
                fallbackFaulted = false;
                bytesWritten = written;
                return true;
            }
        }
        catch
        {
            // Both dictionary and raw decompression failed
        }
        finally
        {
            fallbackLease.Return(fallbackFaulted);
        }

        bytesWritten = 0;
        return false;
    }

    /// <summary>
    ///     Convenience allocation overload for decompression.
    /// </summary>
    public static byte[] DecompressDelta(ReadOnlySpan<byte> source)
    {
        int expectedLen = GetUncompressedLength(source);
        if (expectedLen <= 0) return Array.Empty<byte>();

        byte[] output = new byte[expectedLen];
        int written = DecompressDelta(source, output);
        return output.AsSpan(0, written).ToArray();
    }
}
