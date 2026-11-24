// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System;
using System.Collections.Generic;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Kongor.Core.Diagnostics;

/// <summary>
/// Phase 12 (#147: imhex-shannon-entropy-and-digram-matrix):
/// Stratified sqrt(N)-sampled Shannon block entropy analyzer, encrypted/compressed/corrupt
/// anomaly region detector, 256x256 sequential byte-transition digram matrix, and byte-distribution
/// classifier adapted from ImHex diagrams.hpp and data_information_sections.cpp.
/// </summary>
public sealed record EntropyAnomalySpan(
    int StartBlockIndex,
    int EndBlockIndex,
    long StartByteOffset,
    long EndByteOffset,
    double PeakEntropy,
    double MeanEntropy,
    string Classification);

public sealed record ByteDistributionProfile(
    long TotalBytes,
    long PrintableAsciiCount,
    long ZeroByteCount,
    long FFByteCount,
    long ControlByteCount,
    long HighBitByteCount,
    int UniqueByteValues,
    double OverallEntropy,
    double AverageBlockEntropy,
    double HighestBlockEntropy,
    long HighestEntropyBlockOffset,
    double LowestBlockEntropy,
    long LowestEntropyBlockOffset,
    string DominantClassification,
    IReadOnlyList<double> SampledBlockEntropies,
    IReadOnlyList<EntropyAnomalySpan> HighEntropySpans);

public sealed record DigramMatrixResult(
    long TotalTransitions,
    int NonZeroPairCount,
    byte PeakFirstByte,
    byte PeakSecondByte,
    long PeakPairCount,
    double LogScaledDensityScore,
    long[] Counts256x256);

public static class BinaryEntropyAndDigramAnalyzer
{
    private static readonly ILogger s_logger = NullLogger.Instance;

    /// <summary>
    /// Computes normalized Shannon entropy H in [0.0, 1.0] (bits per byte / 8.0) for a single span.
    /// </summary>
    public static double ComputeNormalizedShannonEntropy(ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty)
        {
            return 0.0;
        }

        Span<long> counts = stackalloc long[256];
        counts.Clear();

        for (int i = 0; i < data.Length; i++)
        {
            counts[data[i]]++;
        }

        double length = data.Length;
        double entropy = 0.0;

        for (int b = 0; b < 256; b++)
        {
            long c = counts[b];
            if (c <= 0)
            {
                continue;
            }

            double p = c / length;
            entropy -= p * Math.Log2(p);
        }

        // Normalize by log2(256) == 8.0 so H is in [0.0, 1.0]
        double normalized = entropy / 8.0;
        if (normalized < 0.0)
        {
            return 0.0;
        }

        if (normalized > 1.0)
        {
            return 1.0;
        }

        return normalized;
    }

    /// <summary>
    /// Analyzes block-level Shannon entropy with ImHex's stratified sqrt(N) block size heuristic
    /// (clamped to [minBlockSize, maxBlockSize]) and detects contiguous high-entropy encrypted/compressed
    /// regions and abrupt entropy drops (corrupted/zeroed pages).
    /// </summary>
    public static ByteDistributionProfile AnalyzeEntropyAndDistribution(
        ReadOnlySpan<byte> data,
        int requestedBlockSize = 0,
        double highEntropyThreshold = 0.85,
        ILogger? logger = null)
    {
        ILogger activeLogger = logger ?? s_logger;
        if (data.IsEmpty)
        {
            return new ByteDistributionProfile(
                0, 0, 0, 0, 0, 0, 0,
                0.0, 0.0, 0.0, 0, 0.0, 0,
                "Empty",
                Array.Empty<double>(),
                Array.Empty<EntropyAnomalySpan>());
        }

        // ImHex stratified block sizing: ceil(sqrt(size)), clamped to [64, 65536] unless explicitly specified
        int blockSize = requestedBlockSize > 0
            ? requestedBlockSize
            : Math.Clamp((int)Math.Ceiling(Math.Sqrt(data.Length)), 64, 65536);

        Span<long> globalCounts = stackalloc long[256];
        globalCounts.Clear();

        long printableAscii = 0;
        long zeroBytes = 0;
        long ffBytes = 0;
        long controlBytes = 0;
        long highBitBytes = 0;

        for (int i = 0; i < data.Length; i++)
        {
            byte v = data[i];
            globalCounts[v]++;
            if (v == 0x00)
            {
                zeroBytes++;
            }
            else if (v == 0xFF)
            {
                ffBytes++;
            }

            if (v >= 0x20 && v <= 0x7E)
            {
                printableAscii++;
            }
            else if (v == 0x09 || v == 0x0A || v == 0x0D)
            {
                printableAscii++;
            }
            else if (v < 0x20 || v == 0x7F)
            {
                controlBytes++;
            }
            else
            {
                highBitBytes++;
            }
        }

        int uniqueBytes = 0;
        double totalLength = data.Length;
        double overallEntropy = 0.0;
        for (int b = 0; b < 256; b++)
        {
            long c = globalCounts[b];
            if (c > 0)
            {
                uniqueBytes++;
                double p = c / totalLength;
                overallEntropy -= p * Math.Log2(p);
            }
        }
        overallEntropy = Math.Clamp(overallEntropy / 8.0, 0.0, 1.0);

        int numBlocks = (data.Length + blockSize - 1) / blockSize;
        List<double> blockEntropies = new List<double>(numBlocks);
        List<EntropyAnomalySpan> anomalySpans = new List<EntropyAnomalySpan>();

        double sumBlockEntropy = 0.0;
        double highestEntropy = -1.0;
        long highestOffset = 0;
        double lowestEntropy = 2.0;
        long lowestOffset = 0;

        int activeSpanStartBlock = -1;
        double activeSpanSum = 0.0;
        double activeSpanPeak = 0.0;

        for (int blockIdx = 0; blockIdx < numBlocks; blockIdx++)
        {
            int offset = blockIdx * blockSize;
            int len = Math.Min(blockSize, data.Length - offset);
            ReadOnlySpan<byte> slice = data.Slice(offset, len);
            double h = ComputeNormalizedShannonEntropy(slice);
            blockEntropies.Add(h);
            sumBlockEntropy += h;

            if (h > highestEntropy)
            {
                highestEntropy = h;
                highestOffset = offset;
            }

            if (h < lowestEntropy)
            {
                lowestEntropy = h;
                lowestOffset = offset;
            }

            if (h >= highEntropyThreshold)
            {
                if (activeSpanStartBlock < 0)
                {
                    activeSpanStartBlock = blockIdx;
                    activeSpanSum = h;
                    activeSpanPeak = h;
                }
                else
                {
                    activeSpanSum += h;
                    if (h > activeSpanPeak)
                    {
                        activeSpanPeak = h;
                    }
                }
            }
            else if (activeSpanStartBlock >= 0)
            {
                int spanCount = blockIdx - activeSpanStartBlock;
                double meanH = activeSpanSum / spanCount;
                string spanClass = meanH >= 0.96 ? "EncryptedOrRandom" : "CompressedPayload";
                anomalySpans.Add(new EntropyAnomalySpan(
                    activeSpanStartBlock,
                    blockIdx - 1,
                    (long)activeSpanStartBlock * blockSize,
                    Math.Min((long)blockIdx * blockSize, data.Length),
                    activeSpanPeak,
                    meanH,
                    spanClass));
                activeSpanStartBlock = -1;
            }
        }

        if (activeSpanStartBlock >= 0)
        {
            int spanCount = numBlocks - activeSpanStartBlock;
            double meanH = activeSpanSum / spanCount;
            string spanClass = meanH >= 0.96 ? "EncryptedOrRandom" : "CompressedPayload";
            anomalySpans.Add(new EntropyAnomalySpan(
                activeSpanStartBlock,
                numBlocks - 1,
                (long)activeSpanStartBlock * blockSize,
                data.Length,
                activeSpanPeak,
                meanH,
                spanClass));
        }

        double avgBlockEntropy = numBlocks > 0 ? sumBlockEntropy / numBlocks : 0.0;
        string dominantClass = ClassifyBuffer(
            data.Length,
            printableAscii,
            zeroBytes,
            overallEntropy,
            avgBlockEntropy);

        activeLogger.LogDebug(
            "[EntropyAnalyzer] bytes={TotalBytes} blockSize={BlockSize} blocks={Blocks} overallH={OverallH:F4} avgBlockH={AvgH:F4} class={Class} anomalies={Anomalies}",
            data.Length,
            blockSize,
            numBlocks,
            overallEntropy,
            avgBlockEntropy,
            dominantClass,
            anomalySpans.Count);

        return new ByteDistributionProfile(
            data.Length,
            printableAscii,
            zeroBytes,
            ffBytes,
            controlBytes,
            highBitBytes,
            uniqueBytes,
            overallEntropy,
            avgBlockEntropy,
            highestEntropy < 0 ? 0.0 : highestEntropy,
            highestOffset,
            lowestEntropy > 1.0 ? 0.0 : lowestEntropy,
            lowestOffset,
            dominantClass,
            blockEntropies,
            anomalySpans);
    }

    /// <summary>
    /// Computes the 256x256 sequential byte-transition digram matrix (buffer[i], buffer[i+1]).
    /// </summary>
    public static DigramMatrixResult ComputeDigramMatrix(ReadOnlySpan<byte> data, int maxSampleTransitions = 1_048_576)
    {
        long[] matrix = new long[256 * 256];
        if (data.Length < 2)
        {
            return new DigramMatrixResult(0, 0, 0, 0, 0, 0.0, matrix);
        }

        int totalTransitions = data.Length - 1;
        int stride = totalTransitions > maxSampleTransitions && maxSampleTransitions > 0
            ? Math.Max(1, totalTransitions / maxSampleTransitions)
            : 1;

        long sampledTransitions = 0;
        for (int i = 0; i < data.Length - 1; i += stride)
        {
            byte first = data[i];
            byte second = data[i + 1];
            matrix[(first * 256) + second]++;
            sampledTransitions++;
        }

        int nonZeroPairs = 0;
        byte peakFirst = 0;
        byte peakSecond = 0;
        long peakCount = 0;
        double logSum = 0.0;

        for (int idx = 0; idx < matrix.Length; idx++)
        {
            long count = matrix[idx];
            if (count <= 0)
            {
                continue;
            }

            nonZeroPairs++;
            logSum += Math.Log(1.0 + count);
            if (count > peakCount)
            {
                peakCount = count;
                peakFirst = (byte)(idx >> 8);
                peakSecond = (byte)(idx & 0xFF);
            }
        }

        double maxPossibleLog = 65536.0 * Math.Log(1.0 + Math.Max(1, sampledTransitions));
        double densityScore = maxPossibleLog > 0.0 ? Math.Clamp(logSum / maxPossibleLog, 0.0, 1.0) : 0.0;

        return new DigramMatrixResult(
            sampledTransitions,
            nonZeroPairs,
            peakFirst,
            peakSecond,
            peakCount,
            densityScore,
            matrix);
    }

    private static string ClassifyBuffer(
        long totalBytes,
        long printableAscii,
        long zeroBytes,
        double overallEntropy,
        double avgBlockEntropy)
    {
        if (totalBytes == 0)
        {
            return "Empty";
        }

        double zeroRatio = (double)zeroBytes / totalBytes;
        double printableRatio = (double)printableAscii / totalBytes;

        if (zeroRatio >= 0.90)
        {
            return "SparseOrZeroed";
        }

        if (printableRatio >= 0.85)
        {
            return "PlaintextOrXmlLua";
        }

        if (overallEntropy >= 0.95 && avgBlockEntropy >= 0.92)
        {
            return "EncryptedOrHighEntropyCompressed";
        }

        if (overallEntropy >= 0.82)
        {
            return "CompressedArchiveOrTexture";
        }

        if (overallEntropy >= 0.45 && zeroRatio >= 0.10)
        {
            return "StructuredNativeBinaryOrModel";
        }

        return "StructuredBinary";
    }
}
