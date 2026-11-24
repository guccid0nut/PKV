// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Text;

namespace Kongor.Core.Services.Native;

/// <summary>
/// Phase 12 (#152: imhex-utf8-utf16-and-ranged-value-scanner):
/// RFC-3629 strict UTF-8 &amp; UTF-16LE (wchar_t) string extractor (rejecting overlong 0xE0 sequences
/// and 0xED UTF-16 surrogates) and ranged numeric value scanner (locating i32/u32/f32/f64 values
/// within [minValue, maxValue]) adapted from ImHex view_find.cpp.
/// </summary>
public enum ExtractedStringEncoding : byte
{
    AsciiOrUtf8 = 0,
    Utf16LittleEndian = 1
}

public sealed record ExtractedBinaryString(
    long ByteOffset,
    int ByteLength,
    ExtractedStringEncoding Encoding,
    string Value);

public sealed record RangedNumericMatch(
    long ByteOffset,
    string ValueType,
    double NumericValue,
    ulong RawBits);

public static class BinaryStringAndNumericScanner
{
    /// <summary>
    /// Extracts valid printable ASCII / strict RFC-3629 UTF-8 strings and optionally UTF-16LE
    /// wide strings (wchar_t) of at least minCharacters length from a binary buffer.
    /// </summary>
    public static IReadOnlyList<ExtractedBinaryString> ExtractStrings(
        ReadOnlySpan<byte> buffer,
        int minCharacters = 4,
        bool includeUtf8 = true,
        bool includeUtf16Le = true,
        int maxResults = 2048)
    {
        if (minCharacters < 1)
        {
            minCharacters = 1;
        }

        List<ExtractedBinaryString> results = new List<ExtractedBinaryString>();
        if (buffer.IsEmpty)
        {
            return results;
        }

        if (includeUtf8)
        {
            ExtractUtf8Strings(buffer, minCharacters, maxResults, results);
        }

        if (includeUtf16Le && results.Count < maxResults)
        {
            ExtractUtf16LeStrings(buffer, minCharacters, maxResults, results);
        }

        return results;
    }

    /// <summary>
    /// Validates a single UTF-8 codepoint sequence at buffer[offset..] per strict RFC-3629 rules
    /// (explicitly rejecting 0xE0 overlong 3-byte sequences and 0xED UTF-16 surrogate halves U+D800..U+DFFF).
    /// </summary>
    public static bool TryConsumeValidUtf8Codepoint(
        ReadOnlySpan<byte> buffer,
        int offset,
        out int bytesConsumed,
        out uint codepoint)
    {
        bytesConsumed = 0;
        codepoint = 0;

        if ((uint)offset >= (uint)buffer.Length)
        {
            return false;
        }

        byte b0 = buffer[offset];
        if (b0 < 0x80)
        {
            // Printable ASCII + tab
            if ((b0 >= 0x20 && b0 <= 0x7E) || b0 == 0x09)
            {
                bytesConsumed = 1;
                codepoint = b0;
                return true;
            }
            return false;
        }

        // 2-byte UTF-8: 0xC2..0xDF, followed by 0x80..0xBF (0xC0..0xC1 are overlong ASCII)
        if (b0 >= 0xC2 && b0 <= 0xDF)
        {
            if (offset + 1 >= buffer.Length)
            {
                return false;
            }

            byte b1 = buffer[offset + 1];
            if ((b1 & 0xC0) != 0x80)
            {
                return false;
            }

            bytesConsumed = 2;
            codepoint = (uint)(((b0 & 0x1F) << 6) | (b1 & 0x3F));
            return true;
        }

        // 3-byte UTF-8: 0xE0..0xEF
        if (b0 >= 0xE0 && b0 <= 0xEF)
        {
            if (offset + 2 >= buffer.Length)
            {
                return false;
            }

            byte b1 = buffer[offset + 1];
            byte b2 = buffer[offset + 2];
            if ((b1 & 0xC0) != 0x80 || (b2 & 0xC0) != 0x80)
            {
                return false;
            }

            // Reject 0xE0 overlong sequences (b1 must be >= 0xA0)
            if (b0 == 0xE0 && b1 < 0xA0)
            {
                return false;
            }

            // Reject 0xED UTF-16 surrogates U+D800..U+DFFF (b1 must be < 0xA0)
            if (b0 == 0xED && b1 >= 0xA0)
            {
                return false;
            }

            uint cp = (uint)(((b0 & 0x0F) << 12) | ((b1 & 0x3F) << 6) | (b2 & 0x3F));
            if (cp == 0xFFFE || cp == 0xFFFF)
            {
                return false;
            }

            bytesConsumed = 3;
            codepoint = cp;
            return true;
        }

        // 4-byte UTF-8: 0xF0..0xF4
        if (b0 >= 0xF0 && b0 <= 0xF4)
        {
            if (offset + 3 >= buffer.Length)
            {
                return false;
            }

            byte b1 = buffer[offset + 1];
            byte b2 = buffer[offset + 2];
            byte b3 = buffer[offset + 3];
            if ((b1 & 0xC0) != 0x80 || (b2 & 0xC0) != 0x80 || (b3 & 0xC0) != 0x80)
            {
                return false;
            }

            // Reject 0xF0 overlong sequences (b1 must be >= 0x90)
            if (b0 == 0xF0 && b1 < 0x90)
            {
                return false;
            }

            // Reject > U+10FFFF (when b0 == 0xF4, b1 must be < 0x90)
            if (b0 == 0xF4 && b1 >= 0x90)
            {
                return false;
            }

            bytesConsumed = 4;
            codepoint = (uint)(((b0 & 0x07) << 18) | ((b1 & 0x3F) << 12) | ((b2 & 0x3F) << 6) | (b3 & 0x3F));
            return true;
        }

        return false;
    }

    private static void ExtractUtf8Strings(
        ReadOnlySpan<byte> buffer,
        int minCharacters,
        int maxResults,
        List<ExtractedBinaryString> results)
    {
        int i = 0;
        while (i < buffer.Length && results.Count < maxResults)
        {
            int startOffset = i;
            int charCount = 0;

            while (i < buffer.Length &&
                   TryConsumeValidUtf8Codepoint(buffer, i, out int step, out uint _))
            {
                i += step;
                charCount++;
            }

            if (charCount >= minCharacters)
            {
                int byteLen = i - startOffset;
                string decoded = Encoding.UTF8.GetString(buffer.Slice(startOffset, byteLen));
                results.Add(new ExtractedBinaryString(
                    startOffset,
                    byteLen,
                    ExtractedStringEncoding.AsciiOrUtf8,
                    decoded));
            }

            if (i == startOffset)
            {
                i++;
            }
        }
    }

    private static void ExtractUtf16LeStrings(
        ReadOnlySpan<byte> buffer,
        int minCharacters,
        int maxResults,
        List<ExtractedBinaryString> results)
    {
        // Scan both even and odd alignments so unaligned UTF-16LE strings inside packed structs are found
        for (int alignment = 0; alignment < 2 && results.Count < maxResults; alignment++)
        {
            int i = alignment;
            while (i + 1 < buffer.Length && results.Count < maxResults)
            {
                int startOffset = i;
                int charCount = 0;
                bool hasAsciiRangeWideChar = false;

                while (i + 1 < buffer.Length)
                {
                    ushort ch = BinaryPrimitives.ReadUInt16LittleEndian(buffer.Slice(i, 2));
                    if ((ch >= 0x0020 && ch <= 0x007E) || ch == 0x0009)
                    {
                        hasAsciiRangeWideChar = true;
                        charCount++;
                        i += 2;
                    }
                    else
                    {
                        break;
                    }
                }

                if (charCount >= minCharacters && hasAsciiRangeWideChar)
                {
                    int byteLen = i - startOffset;
                    string decoded = Encoding.Unicode.GetString(buffer.Slice(startOffset, byteLen));
                    results.Add(new ExtractedBinaryString(
                        startOffset,
                        byteLen,
                        ExtractedStringEncoding.Utf16LittleEndian,
                        decoded));
                }

                if (i == startOffset)
                {
                    i += 2;
                }
            }
        }
    }

    /// <summary>
    /// Scans a binary buffer for 32-bit floats (f32) within [minValue, maxValue] at the given byte alignment.
    /// Rejects NaN and subnormal noise automatically.
    /// </summary>
    public static IReadOnlyList<RangedNumericMatch> ScanFloat32InRange(
        ReadOnlySpan<byte> buffer,
        float minValue,
        float maxValue,
        int alignment = 4,
        int maxResults = 1024)
    {
        if (alignment < 1)
        {
            alignment = 1;
        }

        List<RangedNumericMatch> matches = new List<RangedNumericMatch>();
        for (int offset = 0; offset + 4 <= buffer.Length && matches.Count < maxResults; offset += alignment)
        {
            uint raw = BinaryPrimitives.ReadUInt32LittleEndian(buffer.Slice(offset, 4));
            float val = BitConverter.UInt32BitsToSingle(raw);
            if (!float.IsFinite(val) || (val != 0.0f && MathF.Abs(val) < 1e-30f))
            {
                continue;
            }

            if (val >= minValue && val <= maxValue)
            {
                matches.Add(new RangedNumericMatch(offset, "f32", val, raw));
            }
        }

        return matches;
    }

    /// <summary>
    /// Scans a binary buffer for signed 32-bit integers (i32) within [minValue, maxValue] at the given byte alignment.
    /// </summary>
    public static IReadOnlyList<RangedNumericMatch> ScanInt32InRange(
        ReadOnlySpan<byte> buffer,
        int minValue,
        int maxValue,
        int alignment = 4,
        int maxResults = 1024)
    {
        if (alignment < 1)
        {
            alignment = 1;
        }

        List<RangedNumericMatch> matches = new List<RangedNumericMatch>();
        for (int offset = 0; offset + 4 <= buffer.Length && matches.Count < maxResults; offset += alignment)
        {
            int val = BinaryPrimitives.ReadInt32LittleEndian(buffer.Slice(offset, 4));
            if (val >= minValue && val <= maxValue)
            {
                matches.Add(new RangedNumericMatch(offset, "i32", val, (ulong)(uint)val));
            }
        }

        return matches;
    }
}
