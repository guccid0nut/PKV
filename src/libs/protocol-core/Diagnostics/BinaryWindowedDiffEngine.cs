// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System;
using System.Collections.Generic;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Kongor.Core.Diagnostics;

/// <summary>
/// Phase 12 (#151: imhex-windowed-myers-binary-diff):
/// Windowed Myers O(ND) edit-graph binary diff engine (with the upstream slice-offset bug fixed so
/// multi-window offsets are globally accurate), coalesced linear byte diff, and directional
/// next-differing-byte searcher adapted from ImHex diffing_algorithms.cpp and differing_byte_searcher.cpp.
/// </summary>
public enum BinaryDiffSpanType : byte
{
    Identical = 0,
    Modified = 1,
    Inserted = 2,
    Deleted = 3
}

public sealed record BinaryDiffSpan(
    BinaryDiffSpanType Type,
    long OffsetA,
    long OffsetB,
    int Length);

public sealed record BinaryDiffSummary(
    long LengthA,
    long LengthB,
    long FirstDifferenceOffset,
    int TotalDiffSpans,
    long ModifiedBytes,
    long InsertedBytes,
    long DeletedBytes,
    IReadOnlyList<BinaryDiffSpan> Spans);

public static class BinaryWindowedDiffEngine
{
    private static readonly ILogger s_logger = NullLogger.Instance;

    /// <summary>
    /// Finds the next or previous offset where streamA and streamB differ, starting from startOffset.
    /// Returns -1 if all remaining bytes in that direction are identical.
    /// Adapted from ImHex findNextDifferingByte() in differing_byte_searcher.cpp.
    /// </summary>
    public static long FindNextDifferingByte(
        ReadOnlySpan<byte> a,
        ReadOnlySpan<byte> b,
        long startOffset,
        bool forward = true)
    {
        long maxLen = Math.Max(a.Length, b.Length);
        long minLen = Math.Min(a.Length, b.Length);
        if (maxLen == 0)
        {
            return -1;
        }

        if (forward)
        {
            long start = Math.Max(0L, startOffset);
            for (long i = start; i < maxLen; i++)
            {
                if (i >= minLen)
                {
                    return i;
                }

                if (a[(int)i] != b[(int)i])
                {
                    return i;
                }
            }
            return -1;
        }
        else
        {
            long start = Math.Min(maxLen - 1L, startOffset);
            for (long i = start; i >= 0; i--)
            {
                if (i >= minLen)
                {
                    return i;
                }

                if (a[(int)i] != b[(int)i])
                {
                    return i;
                }
            }
            return -1;
        }
    }

    /// <summary>
    /// Fast linear O(N) byte-for-byte comparison that coalesces contiguous Modified, Inserted,
    /// or Deleted byte runs into compact spans.
    /// </summary>
    public static BinaryDiffSummary ComputeLinearDiff(
        ReadOnlySpan<byte> a,
        ReadOnlySpan<byte> b,
        int maxSpans = 4096)
    {
        List<BinaryDiffSpan> spans = new List<BinaryDiffSpan>();
        int minLen = Math.Min(a.Length, b.Length);
        long firstDiff = -1;
        long modifiedBytes = 0;
        long insertedBytes = 0;
        long deletedBytes = 0;

        int i = 0;
        while (i < minLen)
        {
            if (a[i] == b[i])
            {
                i++;
                continue;
            }

            if (firstDiff < 0)
            {
                firstDiff = i;
            }

            int runStart = i;
            while (i < minLen && a[i] != b[i])
            {
                i++;
            }

            int runLen = i - runStart;
            modifiedBytes += runLen;
            if (spans.Count < maxSpans)
            {
                spans.Add(new BinaryDiffSpan(BinaryDiffSpanType.Modified, runStart, runStart, runLen));
            }
        }

        if (b.Length > minLen)
        {
            int insLen = b.Length - minLen;
            insertedBytes += insLen;
            if (firstDiff < 0)
            {
                firstDiff = minLen;
            }

            if (spans.Count < maxSpans)
            {
                spans.Add(new BinaryDiffSpan(BinaryDiffSpanType.Inserted, minLen, minLen, insLen));
            }
        }
        else if (a.Length > minLen)
        {
            int delLen = a.Length - minLen;
            deletedBytes += delLen;
            if (firstDiff < 0)
            {
                firstDiff = minLen;
            }

            if (spans.Count < maxSpans)
            {
                spans.Add(new BinaryDiffSpan(BinaryDiffSpanType.Deleted, minLen, minLen, delLen));
            }
        }

        return new BinaryDiffSummary(
            a.Length,
            b.Length,
            firstDiff,
            spans.Count,
            modifiedBytes,
            insertedBytes,
            deletedBytes,
            spans);
    }

    /// <summary>
    /// Windowed Myers O(ND) shortest-edit-script binary diff that detects shifted insertions and
    /// deletions within each window (default 2048 bytes) while adding global baseOffsetA / baseOffsetB
    /// to every emitted span (fixing ImHex's upstream window-relative offset bug).
    /// </summary>
    public static BinaryDiffSummary ComputeWindowedMyersDiff(
        ReadOnlySpan<byte> a,
        ReadOnlySpan<byte> b,
        int windowSize = 2048,
        int maxSpans = 4096,
        ILogger? logger = null)
    {
        ILogger activeLogger = logger ?? s_logger;
        if (windowSize <= 0)
        {
            windowSize = 2048;
        }

        List<BinaryDiffSpan> spans = new List<BinaryDiffSpan>();
        long firstDiff = FindNextDifferingByte(a, b, 0, forward: true);
        if (firstDiff < 0)
        {
            return new BinaryDiffSummary(a.Length, b.Length, -1, 0, 0, 0, 0, spans);
        }

        int offsetA = 0;
        int offsetB = 0;
        long modifiedBytes = 0;
        long insertedBytes = 0;
        long deletedBytes = 0;

        while ((offsetA < a.Length || offsetB < b.Length) && spans.Count < maxSpans)
        {
            // Skip identical common prefix before running Myers on the window
            while (offsetA < a.Length && offsetB < b.Length && a[offsetA] == b[offsetB])
            {
                offsetA++;
                offsetB++;
            }

            if (offsetA >= a.Length && offsetB >= b.Length)
            {
                break;
            }

            int lenA = Math.Min(windowSize, a.Length - offsetA);
            int lenB = Math.Min(windowSize, b.Length - offsetB);
            ReadOnlySpan<byte> winA = a.Slice(offsetA, lenA);
            ReadOnlySpan<byte> winB = b.Slice(offsetB, lenB);
            bool hasMoreA = (offsetA + lenA) < a.Length;
            bool hasMoreB = (offsetB + lenB) < b.Length;

            ComputeMyersWindowSpans(
                winA,
                winB,
                offsetA,
                offsetB,
                hasMoreA,
                hasMoreB,
                spans,
                maxSpans,
                ref modifiedBytes,
                ref insertedBytes,
                ref deletedBytes,
                out int consumedA,
                out int consumedB);

            offsetA += Math.Max(1, consumedA);
            offsetB += Math.Max(1, consumedB);
        }

        activeLogger.LogDebug(
            "[WindowedMyersDiff] lenA={LenA} lenB={LenB} firstDiff={FirstDiff} spans={Spans} ins={Ins} del={Del} mod={Mod}",
            a.Length,
            b.Length,
            firstDiff,
            spans.Count,
            insertedBytes,
            deletedBytes,
            modifiedBytes);

        return new BinaryDiffSummary(
            a.Length,
            b.Length,
            firstDiff,
            spans.Count,
            modifiedBytes,
            insertedBytes,
            deletedBytes,
            spans);
    }

    private static void ComputeMyersWindowSpans(
        ReadOnlySpan<byte> winA,
        ReadOnlySpan<byte> winB,
        long baseOffsetA,
        long baseOffsetB,
        bool hasMoreA,
        bool hasMoreB,
        List<BinaryDiffSpan> outputSpans,
        int maxSpans,
        ref long modifiedBytes,
        ref long insertedBytes,
        ref long deletedBytes,
        out int consumedA,
        out int consumedB)
    {
        int n = winA.Length;
        int m = winB.Length;
        consumedA = n;
        consumedB = m;

        if (n == 0 && m == 0)
        {
            return;
        }

        if (n == 0)
        {
            insertedBytes += m;
            if (outputSpans.Count < maxSpans)
            {
                outputSpans.Add(new BinaryDiffSpan(BinaryDiffSpanType.Inserted, baseOffsetA, baseOffsetB, m));
            }
            return;
        }

        if (m == 0)
        {
            deletedBytes += n;
            if (outputSpans.Count < maxSpans)
            {
                outputSpans.Add(new BinaryDiffSpan(BinaryDiffSpanType.Deleted, baseOffsetA, baseOffsetB, n));
            }
            return;
        }

        int maxD = n + m;
        int vOffset = maxD;
        int vLen = (2 * maxD) + 1;
        int[] v = new int[vLen];
        List<int[]> trace = new List<int[]>();

        int finalD = maxD;
        for (int d = 0; d <= maxD; d++)
        {
            int[] snapshot = new int[vLen];
            Array.Copy(v, snapshot, vLen);
            trace.Add(snapshot);

            bool reachedEnd = false;
            for (int k = -d; k <= d; k += 2)
            {
                int x;
                if (k == -d || (k != d && v[vOffset + k - 1] < v[vOffset + k + 1]))
                {
                    x = v[vOffset + k + 1];
                }
                else
                {
                    x = v[vOffset + k - 1] + 1;
                }

                int y = x - k;
                while (x < n && y < m && winA[x] == winB[y])
                {
                    x++;
                    y++;
                }

                v[vOffset + k] = x;
                if (x >= n && y >= m)
                {
                    finalD = d;
                    reachedEnd = true;
                    break;
                }
            }

            if (reachedEnd)
            {
                break;
            }
        }

        // Backtrack to recover edit steps: +1 = Insert (from B), -1 = Delete (from A), 0 = Match
        List<(BinaryDiffSpanType Type, int PosA, int PosB)> edits = new List<(BinaryDiffSpanType, int, int)>();
        int curX = n;
        int curY = m;

        for (int d = finalD; d > 0; d--)
        {
            int[] prevV = trace[d];
            int k = curX - curY;
            int prevK;
            if (k == -d || (k != d && prevV[vOffset + k - 1] < prevV[vOffset + k + 1]))
            {
                prevK = k + 1;
            }
            else
            {
                prevK = k - 1;
            }

            int prevX = prevV[vOffset + prevK];
            int prevY = prevX - prevK;

            while (curX > prevX && curY > prevY)
            {
                curX--;
                curY--;
            }

            if (curX == prevX)
            {
                curY--;
                edits.Add((BinaryDiffSpanType.Inserted, curX, curY));
            }
            else
            {
                curX--;
                edits.Add((BinaryDiffSpanType.Deleted, curX, curY));
            }
        }

        edits.Reverse();

        // Trim trailing boundary-truncation edits when one side reached the end of the window
        // while the other stream still has more bytes beyond this window.
        while (edits.Count > 0)
        {
            (BinaryDiffSpanType lastType, int lastPosA, int lastPosB) = edits[edits.Count - 1];
            if (lastType == BinaryDiffSpanType.Deleted && lastPosB == m && hasMoreB && consumedA > 0)
            {
                consumedA = lastPosA;
                edits.RemoveAt(edits.Count - 1);
            }
            else if (lastType == BinaryDiffSpanType.Inserted && lastPosA == n && hasMoreA && consumedB > 0)
            {
                consumedB = lastPosB;
                edits.RemoveAt(edits.Count - 1);
            }
            else
            {
                break;
            }
        }

        // Coalesce contiguous Insert/Delete edits into spans with global baseOffsetA / baseOffsetB
        int idx = 0;
        while (idx < edits.Count && outputSpans.Count < maxSpans)
        {
            (BinaryDiffSpanType type, int posA, int posB) = edits[idx];
            int runLen = 1;
            idx++;

            while (idx < edits.Count && edits[idx].Type == type)
            {
                int expectedA = type == BinaryDiffSpanType.Deleted ? posA + runLen : posA;
                int expectedB = type == BinaryDiffSpanType.Inserted ? posB + runLen : posB;
                if (edits[idx].PosA != expectedA || edits[idx].PosB != expectedB)
                {
                    break;
                }
                runLen++;
                idx++;
            }

            if (type == BinaryDiffSpanType.Inserted)
            {
                insertedBytes += runLen;
            }
            else if (type == BinaryDiffSpanType.Deleted)
            {
                deletedBytes += runLen;
            }
            else
            {
                modifiedBytes += runLen;
            }

            outputSpans.Add(new BinaryDiffSpan(
                type,
                baseOffsetA + posA,
                baseOffsetB + posB,
                runLen));
        }
    }
}
