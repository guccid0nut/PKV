// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System;
using System.IO;
using System.IO.Compression;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Kongor.Core.Diagnostics;

/// <summary>
/// Phase 12 (#154: imhex-bounded-decompression-and-pagination-guard):
/// Bounded stream decompression guard (enforcing a hard ceiling inside the inner read/decode loop
/// against zip-bombs in .s2z/.honreplay archives), partial-output fault-offset tracker for truncated
/// or corrupted streams, and 100 KiB header fallback for oversized assets, adapted from ImHex
/// pl_functions.cpp and data_information_sections.cpp.
/// </summary>
public sealed record BoundedDecompressionResult(
    bool Succeeded,
    bool ExceededOutputCeiling,
    bool TruncatedToHeaderFallback,
    long CompressedBytesConsumed,
    long DecompressedBytesProduced,
    long? FaultInputOffset,
    string StatusMessage,
    byte[] OutputBytes);

public static class BoundedDecompressionPaginator
{
    public const int DefaultMaxDecompressedBytes = 64 * 1024 * 1024; // 64 MiB default per-entry guard
    public const int AbsoluteMaxDecompressedBytes = 1024 * 1024 * 1024; // 1 GiB ImHex hard ceiling
    public const int HeaderFallbackBytes = 100 * 1024; // 100 KiB ImHex header-only fallback

    private static readonly ILogger s_logger = NullLogger.Instance;

    /// <summary>
    /// Decompresses a raw Deflate or GZip stream in 4 KiB chunks while enforcing maxOutputBytes
    /// inside the inner read loop. If the stream is corrupted mid-way or exceeds the output ceiling,
    /// preserves the partial output decoded so far along with the exact compressed input offset where
    /// the fault or ceiling occurred.
    /// </summary>
    public static BoundedDecompressionResult DecompressBounded(
        ReadOnlySpan<byte> compressedData,
        bool isGZip = false,
        int maxOutputBytes = DefaultMaxDecompressedBytes,
        bool fallbackToHeaderOnCeiling = false,
        ILogger? logger = null)
    {
        ILogger activeLogger = logger ?? s_logger;
        if (maxOutputBytes <= 0 || maxOutputBytes > AbsoluteMaxDecompressedBytes)
        {
            maxOutputBytes = DefaultMaxDecompressedBytes;
        }

        if (compressedData.IsEmpty)
        {
            return new BoundedDecompressionResult(
                true,
                false,
                false,
                0,
                0,
                null,
                "EmptyInput",
                Array.Empty<byte>());
        }

        byte[] inputArray = compressedData.ToArray();
        using MemoryStream inputStream = new MemoryStream(inputArray, writable: false);
        using Stream decompressor = isGZip
            ? new GZipStream(inputStream, CompressionMode.Decompress, leaveOpen: true)
            : new DeflateStream(inputStream, CompressionMode.Decompress, leaveOpen: true);

        using MemoryStream outputStream = new MemoryStream(Math.Min(maxOutputBytes, 65536));
        byte[] chunkBuffer = new byte[4096];

        try
        {
            while (true)
            {
                int remainingAllowance = maxOutputBytes - (int)outputStream.Length;
                if (remainingAllowance <= 0)
                {
                    // Probe 1 byte to check if the stream actually has more data beyond maxOutputBytes
                    int probe = decompressor.Read(chunkBuffer, 0, 1);
                    if (probe > 0)
                    {
                        long ceilingOffset = inputStream.Position;
                        byte[] partial = fallbackToHeaderOnCeiling && outputStream.Length > HeaderFallbackBytes
                            ? outputStream.GetBuffer().AsSpan(0, HeaderFallbackBytes).ToArray()
                            : outputStream.ToArray();

                        activeLogger.LogWarning(
                            "[BoundedDecompression] Output ceiling {MaxBytes} bytes exceeded at compressed offset 0x{Offset:X}. TruncatedToHeader={Fallback}",
                            maxOutputBytes,
                            ceilingOffset,
                            fallbackToHeaderOnCeiling);

                        return new BoundedDecompressionResult(
                            Succeeded: false,
                            ExceededOutputCeiling: true,
                            TruncatedToHeaderFallback: fallbackToHeaderOnCeiling,
                            CompressedBytesConsumed: ceilingOffset,
                            DecompressedBytesProduced: partial.Length,
                            FaultInputOffset: ceilingOffset,
                            StatusMessage: $"Decompression exceeded ceiling of {maxOutputBytes} bytes at compressed offset 0x{ceilingOffset:X}.",
                            OutputBytes: partial);
                    }

                    break;
                }

                int toRead = Math.Min(chunkBuffer.Length, remainingAllowance);
                int bytesRead = decompressor.Read(chunkBuffer, 0, toRead);
                if (bytesRead <= 0)
                {
                    break;
                }

                outputStream.Write(chunkBuffer, 0, bytesRead);
            }

            byte[] finalBytes = outputStream.ToArray();
            return new BoundedDecompressionResult(
                Succeeded: true,
                ExceededOutputCeiling: false,
                TruncatedToHeaderFallback: false,
                CompressedBytesConsumed: inputStream.Position,
                DecompressedBytesProduced: finalBytes.Length,
                FaultInputOffset: null,
                StatusMessage: "OK",
                OutputBytes: finalBytes);
        }
        catch (InvalidDataException ex)
        {
            long faultOffset = inputStream.Position;
            byte[] partialBytes = outputStream.ToArray();
            activeLogger.LogWarning(
                ex,
                "[BoundedDecompression] Corrupted stream at compressed offset 0x{Offset:X} after producing {Produced} bytes.",
                faultOffset,
                partialBytes.Length);

            return new BoundedDecompressionResult(
                Succeeded: false,
                ExceededOutputCeiling: false,
                TruncatedToHeaderFallback: false,
                CompressedBytesConsumed: faultOffset,
                DecompressedBytesProduced: partialBytes.Length,
                FaultInputOffset: faultOffset,
                StatusMessage: $"Corrupted stream at compressed offset 0x{faultOffset:X}: {ex.Message}",
                OutputBytes: partialBytes);
        }
    }

    /// <summary>
    /// Slices an oversized payload to its 100 KiB header preview (ImHex data_information_sections.cpp pattern)
    /// when full inspection exceeds the configured threshold.
    /// </summary>
    public static ReadOnlySpan<byte> SliceToHeaderFallbackIfOversized(
        ReadOnlySpan<byte> payload,
        int fullAnalysisMaxBytes,
        out bool wasTruncatedToHeader,
        int headerFallbackBytes = HeaderFallbackBytes)
    {
        if (payload.Length > fullAnalysisMaxBytes && fullAnalysisMaxBytes > 0)
        {
            wasTruncatedToHeader = true;
            int cap = Math.Min(payload.Length, Math.Max(1, headerFallbackBytes));
            return payload.Slice(0, cap);
        }

        wasTruncatedToHeader = false;
        return payload;
    }

    public const int DefaultMaxCompressionRatio = 500;
    public const long DefaultMinDecompressionFloorBytes = 256L * 1024L * 1024L; // 256 MiB floor

    /// <summary>
    /// Computes the dynamic archive decompression byte budget (#174: rea-archive-decompression-ratio-and-budget-guard):
    /// max_bytes = max(compressed_bytes * 500, 256 MB).
    /// Prevents both multi-gigabyte zip-bombs and false-positive rejections on legitimately large K2 archives.
    /// </summary>
    public static long ComputeArtifactDecompressionBudgetBytes(
        long compressedBytes,
        int maxRatio = DefaultMaxCompressionRatio,
        long minFloorBytes = DefaultMinDecompressionFloorBytes)
    {
        long clampedCompressed = Math.Max(0L, compressedBytes);
        long clampedRatio = Math.Max(1, maxRatio);
        long clampedFloor = Math.Max(1L, minFloorBytes);

        long ratioProduct;
        if (clampedCompressed > long.MaxValue / clampedRatio)
        {
            ratioProduct = long.MaxValue;
        }
        else
        {
            ratioProduct = clampedCompressed * clampedRatio;
        }

        return Math.Max(ratioProduct, clampedFloor);
    }

    /// <summary>
    /// Validates an archive entry's declared uncompressed size against the dynamic 500x / 256 MiB decompression budget (#174).
    /// </summary>
    public static ArchiveDecompressionBudgetEvaluation ValidateArchiveEntryDecompressionBudget(
        string entryName,
        long compressedBytes,
        long uncompressedBytes,
        int maxRatio = DefaultMaxCompressionRatio,
        long minFloorBytes = DefaultMinDecompressionFloorBytes,
        Action<string>? logger = null)
    {
        string safeEntryName = string.IsNullOrWhiteSpace(entryName) ? "<unnamed>" : entryName;
        long budgetBytes = ComputeArtifactDecompressionBudgetBytes(compressedBytes, maxRatio, minFloorBytes);
        double ratio = compressedBytes > 0
            ? (double)uncompressedBytes / compressedBytes
            : (uncompressedBytes > 0 ? double.PositiveInfinity : 1.0);

        bool withinBudget = uncompressedBytes >= 0 && uncompressedBytes <= budgetBytes;
        if (!withinBudget)
        {
            string msg =
                $"[REA-DECOMP-BUDGET] Archive entry '{safeEntryName}' rejected: uncompressed={uncompressedBytes} bytes exceeds dynamic budget={budgetBytes} bytes (compressed={compressedBytes}, ratio={ratio:F1}x, maxRatio={maxRatio}x, minFloor={minFloorBytes}). Run scripts/vfs/read_archive_entry.ps1 to inspect the archive entry header.";
            logger?.Invoke(msg);
            return new ArchiveDecompressionBudgetEvaluation(
                IsWithinBudget: false,
                EntryName: safeEntryName,
                CompressedBytes: compressedBytes,
                UncompressedBytes: uncompressedBytes,
                ComputedBudgetBytes: budgetBytes,
                CompressionRatio: ratio,
                DiagnosticMessage: msg);
        }

        string okMsg = $"[REA-DECOMP-BUDGET] OK: '{safeEntryName}' uncompressed={uncompressedBytes} <= budget={budgetBytes} (ratio={ratio:F1}x).";
        return new ArchiveDecompressionBudgetEvaluation(
            IsWithinBudget: true,
            EntryName: safeEntryName,
            CompressedBytes: compressedBytes,
            UncompressedBytes: uncompressedBytes,
            ComputedBudgetBytes: budgetBytes,
            CompressionRatio: ratio,
            DiagnosticMessage: okMsg);
    }
}

public sealed record ArchiveDecompressionBudgetEvaluation(
    bool IsWithinBudget,
    string EntryName,
    long CompressedBytes,
    long UncompressedBytes,
    long ComputedBudgetBytes,
    double CompressionRatio,
    string DiagnosticMessage);
