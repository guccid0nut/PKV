// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Text;
using Kongor.Core.Diagnostics;

namespace Kongor.Core.Services.Native;

public sealed record OverlayPatchRecord(
    long Offset,
    byte[] Data);

public sealed record IpsDecodeResult(
    bool Success,
    bool IsIps32,
    IReadOnlyList<OverlayPatchRecord> Records,
    int TotalPatchedBytes,
    string? ErrorMessage = null);

/// <summary>
/// Non-destructive in-memory sparse byte overlay over a read-only binary buffer, paired with
/// an IPS / IPS32 binary patch encoder & decoder with fixes for upstream ImHex patches.cpp
/// ("EOF" 0x454F46 / "EEOF" 0x45454F46 offset collision and >64KB chunk splitting) (#146).
/// </summary>
public sealed class VirtualByteOverlay
{
    private readonly ReadOnlyMemory<byte> _baseBuffer;
    private readonly SortedDictionary<long, byte> _modifiedBytes = new SortedDictionary<long, byte>();

    public int BaseLength => _baseBuffer.Length;
    public int ModifiedByteCount => _modifiedBytes.Count;

    public VirtualByteOverlay(ReadOnlyMemory<byte> baseBuffer)
    {
        _baseBuffer = baseBuffer;
    }

    public void ApplyOverlay(long offset, ReadOnlySpan<byte> patchBytes)
    {
        if (offset < 0 || offset + patchBytes.Length > _baseBuffer.Length)
        {
            throw new ArgumentOutOfRangeException(
                nameof(offset),
                $"Overlay range [{offset}..{offset + patchBytes.Length}) exceeds base buffer length ({_baseBuffer.Length}).");
        }

        ReadOnlySpan<byte> baseSpan = _baseBuffer.Span;
        for (int i = 0; i < patchBytes.Length; i++)
        {
            long pos = offset + i;
            byte newVal = patchBytes[i];
            if (baseSpan[(int)pos] == newVal)
            {
                _modifiedBytes.Remove(pos);
            }
            else
            {
                _modifiedBytes[pos] = newVal;
            }
        }
    }

    public void RevertRange(long offset, int length)
    {
        if (length <= 0) return;
        long end = offset + length;
        List<long> toRemove = new List<long>();
        foreach (KeyValuePair<long, byte> kvp in _modifiedBytes)
        {
            if (kvp.Key >= offset && kvp.Key < end)
            {
                toRemove.Add(kvp.Key);
            }
            else if (kvp.Key >= end)
            {
                break;
            }
        }

        for (int i = 0; i < toRemove.Count; i++)
        {
            _modifiedBytes.Remove(toRemove[i]);
        }
    }

    public void ClearAll()
    {
        _modifiedBytes.Clear();
    }

    public void ReadEffectiveSpan(long offset, Span<byte> destination)
    {
        if (offset < 0 || offset + destination.Length > _baseBuffer.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(offset));
        }

        _baseBuffer.Span.Slice((int)offset, destination.Length).CopyTo(destination);
        if (_modifiedBytes.Count == 0)
        {
            return;
        }

        long end = offset + destination.Length;
        foreach (KeyValuePair<long, byte> kvp in _modifiedBytes)
        {
            if (kvp.Key < offset) continue;
            if (kvp.Key >= end) break;
            destination[(int)(kvp.Key - offset)] = kvp.Value;
        }
    }

    public byte[] ToEffectiveArray()
    {
        byte[] output = new byte[_baseBuffer.Length];
        ReadEffectiveSpan(0, output);
        return output;
    }

    public IReadOnlyList<OverlayPatchRecord> GetCoalescedRuns()
    {
        List<OverlayPatchRecord> runs = new List<OverlayPatchRecord>();
        if (_modifiedBytes.Count == 0)
        {
            return runs;
        }

        long runStart = -1;
        long prevOffset = -1;
        List<byte> currentBytes = new List<byte>();

        foreach (KeyValuePair<long, byte> kvp in _modifiedBytes)
        {
            if (runStart == -1)
            {
                runStart = kvp.Key;
                prevOffset = kvp.Key;
                currentBytes.Add(kvp.Value);
            }
            else if (kvp.Key == prevOffset + 1 && currentBytes.Count < 0xFFFF)
            {
                prevOffset = kvp.Key;
                currentBytes.Add(kvp.Value);
            }
            else
            {
                runs.Add(new OverlayPatchRecord(runStart, currentBytes.ToArray()));
                currentBytes.Clear();
                runStart = kvp.Key;
                prevOffset = kvp.Key;
                currentBytes.Add(kvp.Value);
            }
        }

        if (currentBytes.Count > 0)
        {
            runs.Add(new OverlayPatchRecord(runStart, currentBytes.ToArray()));
        }

        return runs;
    }

    public byte[] ExportIpsPatch(bool useIps32 = false)
    {
        return IpsPatchCodec.Encode(GetCoalescedRuns(), useIps32, _baseBuffer.Span);
    }

    public IpsDecodeResult ApplyIpsPatch(ReadOnlySpan<byte> ipsPatchBytes)
    {
        IpsDecodeResult decoded = IpsPatchCodec.Decode(ipsPatchBytes);
        if (!decoded.Success)
        {
            return decoded;
        }

        for (int i = 0; i < decoded.Records.Count; i++)
        {
            OverlayPatchRecord rec = decoded.Records[i];
            ApplyOverlay(rec.Offset, rec.Data);
        }

        return decoded;
    }

    /// <summary>
    /// Phase 13 (#155): Builds a flat augmented interval tree over the coalesced patch runs
    /// for O(log N + K) range overlap queries.
    /// </summary>
    public FlatAugmentedIntervalTree<OverlayPatchRecord> BuildPatchIntervalTree()
    {
        FlatAugmentedIntervalTree<OverlayPatchRecord> tree = new FlatAugmentedIntervalTree<OverlayPatchRecord>();
        IReadOnlyList<OverlayPatchRecord> runs = GetCoalescedRuns();
        for (int i = 0; i < runs.Count; i++)
        {
            OverlayPatchRecord rec = runs[i];
            if (rec.Offset < 0 || rec.Data.Length == 0)
            {
                continue;
            }

            ulong start = (ulong)rec.Offset;
            ulong end = start + (ulong)(rec.Data.Length - 1);
            tree.Insert(start, end, rec);
        }

        tree.ComputeTree();
        return tree;
    }

    public List<IntervalEntry<OverlayPatchRecord>> QueryOverlappingPatches(ulong startOffset, ulong endOffset)
    {
        FlatAugmentedIntervalTree<OverlayPatchRecord> tree = BuildPatchIntervalTree();
        return tree.QueryOverlapping(startOffset, endOffset);
    }
}

public static class IpsPatchCodec
{
    private const long IpsMaxOffset = 0xFFFFFFL;      // 16 MiB - 1
    private const long Ips32MaxOffset = 0xFFFFFFFFL;  // 4 GiB - 1
    public const long IpsEofCollisionOffset = 0x454F46L;     // ASCII "EOF"
    public const long Ips32EeofCollisionOffset = 0x45454F46L; // ASCII "EEOF"

    public static byte[] Encode(
        IReadOnlyList<OverlayPatchRecord> records,
        bool useIps32 = false,
        ReadOnlySpan<byte> optionalBaseBuffer = default)
    {
        ArgumentNullException.ThrowIfNull(records);

        for (int i = 0; i < records.Count; i++)
        {
            if (!useIps32 && records[i].Offset > IpsMaxOffset)
            {
                useIps32 = true;
                break;
            }
        }

        List<byte> outBytes = new List<byte>(256);
        byte[] headerMagic = Encoding.ASCII.GetBytes(useIps32 ? "IPS32" : "PATCH");
        outBytes.AddRange(headerMagic);

        for (int i = 0; i < records.Count; i++)
        {
            OverlayPatchRecord rec = records[i];
            long offset = rec.Offset;
            ReadOnlySpan<byte> data = rec.Data;

            if (offset < 0 || (!useIps32 && offset > IpsMaxOffset) || (useIps32 && offset > Ips32MaxOffset))
            {
                throw new ArgumentOutOfRangeException(nameof(records), $"Record offset 0x{offset:X} is out of range.");
            }

            int pos = 0;
            while (pos < data.Length)
            {
                int chunkLen = Math.Min(0xFFFF, data.Length - pos);
                long chunkOffset = offset + pos;
                ReadOnlySpan<byte> slice = data.Slice(pos, chunkLen);

                // Upstream ImHex bug fix: avoid writing an offset equal to ASCII "EOF" (0x454F46) in IPS
                // or "EEOF" (0x45454F46) in IPS32!
                long forbiddenOffset = useIps32 ? Ips32EeofCollisionOffset : IpsEofCollisionOffset;
                if (chunkOffset == forbiddenOffset)
                {
                    if (slice.Length > 1)
                    {
                        // Emit 1-byte chunk at forbiddenOffset? No, even a 1-byte chunk at forbiddenOffset starts with "EOF"!
                        // Instead, if chunkOffset > 0 and optionalBaseBuffer is provided, back up by 1 byte.
                        // Otherwise, if no base buffer is available, we can still encode at forbiddenOffset ONLY IF
                        // the decoder checks that "EOF" is at the end of stream, OR we prepend base byte when available.
                        if (chunkOffset > 0 && !optionalBaseBuffer.IsEmpty && (chunkOffset - 1) < optionalBaseBuffer.Length && chunkLen < 0xFFFF)
                        {
                            byte[] shifted = new byte[chunkLen + 1];
                            shifted[0] = optionalBaseBuffer[(int)(chunkOffset - 1)];
                            slice.CopyTo(shifted.AsSpan(1));
                            WriteChunk(outBytes, chunkOffset - 1, shifted, useIps32);
                            pos += chunkLen;
                            continue;
                        }
                    }
                }

                WriteChunk(outBytes, chunkOffset, slice, useIps32);
                pos += chunkLen;
            }
        }

        byte[] trailerMagic = Encoding.ASCII.GetBytes(useIps32 ? "EEOF" : "EOF");
        outBytes.AddRange(trailerMagic);
        return outBytes.ToArray();
    }

    private static void WriteChunk(List<byte> outBytes, long offset, ReadOnlySpan<byte> slice, bool useIps32)
    {
        if (slice.IsEmpty) return;

        if (useIps32)
        {
            outBytes.Add((byte)((offset >> 24) & 0xFF));
            outBytes.Add((byte)((offset >> 16) & 0xFF));
            outBytes.Add((byte)((offset >> 8) & 0xFF));
            outBytes.Add((byte)(offset & 0xFF));
        }
        else
        {
            outBytes.Add((byte)((offset >> 16) & 0xFF));
            outBytes.Add((byte)((offset >> 8) & 0xFF));
            outBytes.Add((byte)(offset & 0xFF));
        }

        // Check if the entire slice is a single repeated byte and long enough (> 8 bytes) to benefit from RLE
        bool isUniformRle = slice.Length >= 8;
        byte firstByte = slice[0];
        if (isUniformRle)
        {
            for (int k = 1; k < slice.Length; k++)
            {
                if (slice[k] != firstByte)
                {
                    isUniformRle = false;
                    break;
                }
            }
        }

        if (isUniformRle)
        {
            // RLE chunk: size = 0x0000, rleCount = u16be, value = 1 byte
            outBytes.Add(0x00);
            outBytes.Add(0x00);
            outBytes.Add((byte)((slice.Length >> 8) & 0xFF));
            outBytes.Add((byte)(slice.Length & 0xFF));
            outBytes.Add(firstByte);
        }
        else
        {
            outBytes.Add((byte)((slice.Length >> 8) & 0xFF));
            outBytes.Add((byte)(slice.Length & 0xFF));
            for (int k = 0; k < slice.Length; k++)
            {
                outBytes.Add(slice[k]);
            }
        }
    }

    public static IpsDecodeResult Decode(ReadOnlySpan<byte> patchBytes)
    {
        if (patchBytes.Length < 8)
        {
            return new IpsDecodeResult(false, false, Array.Empty<OverlayPatchRecord>(), 0, "IPS buffer is smaller than minimum header + trailer (8 bytes).");
        }

        string magic = Encoding.ASCII.GetString(patchBytes.Slice(0, 5));
        bool isIps32;
        if (string.Equals(magic, "PATCH", StringComparison.Ordinal))
        {
            isIps32 = false;
        }
        else if (string.Equals(magic, "IPS32", StringComparison.Ordinal))
        {
            isIps32 = true;
        }
        else
        {
            return new IpsDecodeResult(false, false, Array.Empty<OverlayPatchRecord>(), 0, $"Invalid IPS header magic '{magic}' (expected 'PATCH' or 'IPS32').");
        }

        int offsetWidth = isIps32 ? 4 : 3;
        List<OverlayPatchRecord> records = new List<OverlayPatchRecord>();
        int totalPatched = 0;
        int cursor = 5;

        while (cursor < patchBytes.Length)
        {
            // Check for trailer ("EOF" for IPS, "EEOF" for IPS32).
            // Upstream ImHex bug fix: only treat "EOF" / "EEOF" as trailer when it is at the end of the buffer
            // (or followed by a 3-byte Lunar IPS truncation size at the very end), so a valid record at offset 0x454F46
            // with payload bytes after it is never mistaken for EOF!
            if (!isIps32 && cursor + 3 <= patchBytes.Length &&
                patchBytes[cursor] == (byte)'E' && patchBytes[cursor + 1] == (byte)'O' && patchBytes[cursor + 2] == (byte)'F' &&
                (cursor + 3 == patchBytes.Length || cursor + 6 == patchBytes.Length))
            {
                return new IpsDecodeResult(true, false, records, totalPatched);
            }

            if (isIps32 && cursor + 4 <= patchBytes.Length &&
                patchBytes[cursor] == (byte)'E' && patchBytes[cursor + 1] == (byte)'E' &&
                patchBytes[cursor + 2] == (byte)'O' && patchBytes[cursor + 3] == (byte)'F' &&
                (cursor + 4 == patchBytes.Length || cursor + 8 == patchBytes.Length))
            {
                return new IpsDecodeResult(true, true, records, totalPatched);
            }

            if (cursor + offsetWidth + 2 > patchBytes.Length)
            {
                return new IpsDecodeResult(false, isIps32, records, totalPatched, $"Truncated IPS record header at byte offset {cursor}.");
            }

            long recordOffset;
            if (isIps32)
            {
                recordOffset = BinaryPrimitives.ReadUInt32BigEndian(patchBytes.Slice(cursor, 4));
                cursor += 4;
            }
            else
            {
                recordOffset = ((long)patchBytes[cursor] << 16) | ((long)patchBytes[cursor + 1] << 8) | patchBytes[cursor + 2];
                cursor += 3;
            }

            ushort size = BinaryPrimitives.ReadUInt16BigEndian(patchBytes.Slice(cursor, 2));
            cursor += 2;

            if (size == 0)
            {
                // RLE record: 2-byte big-endian count + 1-byte fill value
                if (cursor + 3 > patchBytes.Length)
                {
                    return new IpsDecodeResult(false, isIps32, records, totalPatched, $"Truncated IPS RLE header at byte offset {cursor}.");
                }

                ushort rleCount = BinaryPrimitives.ReadUInt16BigEndian(patchBytes.Slice(cursor, 2));
                byte rleVal = patchBytes[cursor + 2];
                cursor += 3;

                byte[] expanded = new byte[rleCount];
                Array.Fill(expanded, rleVal);
                records.Add(new OverlayPatchRecord(recordOffset, expanded));
                totalPatched += rleCount;
            }
            else
            {
                if (cursor + size > patchBytes.Length)
                {
                    return new IpsDecodeResult(false, isIps32, records, totalPatched, $"Truncated IPS data payload at offset {cursor} (expected {size} bytes).");
                }

                byte[] payload = patchBytes.Slice(cursor, size).ToArray();
                cursor += size;
                records.Add(new OverlayPatchRecord(recordOffset, payload));
                totalPatched += size;
            }
        }

        return new IpsDecodeResult(false, isIps32, records, totalPatched, "Missing 'EOF'/'EEOF' trailer at end of IPS stream.");
    }

    // ========================================================================
    // (#169) Closed-Interval Hook & Byte-Patch Overlap Guard (OBL-HOOK-NO-OVERLAP)
    // ========================================================================
    public readonly record struct PatchIntervalDescriptor(
        string Group,
        string Name,
        ulong StartAddress,
        int PatchLengthBytes);

    public sealed record PatchIntervalCollisionReport(
        bool HasCollision,
        string? FirstGroup,
        string? FirstName,
        ulong FirstStartAddress,
        ulong FirstEndAddress,
        string? SecondGroup,
        string? SecondName,
        ulong SecondStartAddress,
        ulong SecondEndAddress,
        string? DiagnosticMessage);

    // satisfies: OBL-HOOK-NO-OVERLAP
    public static PatchIntervalCollisionReport ValidateNonOverlappingPatchIntervals(
        IReadOnlyList<PatchIntervalDescriptor> intervals)
    {
        if (intervals == null || intervals.Count <= 1)
        {
            return new PatchIntervalCollisionReport(false, null, null, 0UL, 0UL, null, null, 0UL, 0UL, null);
        }

        for (int i = 0; i < intervals.Count; i++)
        {
            PatchIntervalDescriptor first = intervals[i];
            if (first.PatchLengthBytes <= 0)
            {
                continue;
            }

            ulong thisStart = first.StartAddress;
            ulong thisEnd = first.StartAddress + (ulong)first.PatchLengthBytes - 1UL;

            for (int j = i + 1; j < intervals.Count; j++)
            {
                PatchIntervalDescriptor second = intervals[j];
                if (second.PatchLengthBytes <= 0)
                {
                    continue;
                }

                // Allow idempotent re-registration of the exact same hook site
                if (first.StartAddress == second.StartAddress &&
                    first.PatchLengthBytes == second.PatchLengthBytes &&
                    string.Equals(first.Group, second.Group, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(first.Name, second.Name, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                ulong otherStart = second.StartAddress;
                ulong otherEnd = second.StartAddress + (ulong)second.PatchLengthBytes - 1UL;

                // Closed-interval overlap condition: [thisStart, thisEnd] intersects [otherStart, otherEnd]
                if (thisStart <= otherEnd && thisEnd >= otherStart)
                {
                    string msg = $"[OBL-HOOK-NO-OVERLAP] COLLISION: '{second.Group}::{second.Name}' [0x{otherStart:X}..0x{otherEnd:X}] ({second.PatchLengthBytes} bytes) overlaps '{first.Group}::{first.Name}' [0x{thisStart:X}..0x{thisEnd:X}] ({first.PatchLengthBytes} bytes).";
                    return new PatchIntervalCollisionReport(
                        true,
                        first.Group,
                        first.Name,
                        thisStart,
                        thisEnd,
                        second.Group,
                        second.Name,
                        otherStart,
                        otherEnd,
                        msg);
                }
            }
        }

        return new PatchIntervalCollisionReport(false, null, null, 0UL, 0UL, null, null, 0UL, 0UL, null);
    }
}
