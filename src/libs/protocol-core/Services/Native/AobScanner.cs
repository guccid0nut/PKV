// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ============================================================================
// File: AobScanner.cs
// System: Kongor Protocol Core - Native C++ Modernization & Engine Hardening
// Subsystem: Dynamic AOB (Array of Bytes) Memory & Binary Pattern Scanner
// ============================================================================

using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;

namespace Kongor.Core.Services.Native;

/// <summary>
///     Parsed representation of an Array of Bytes (AOB) pattern with support for full-byte wildcards,
///     ImHex-style half-byte nibble masks (<c>4?</c>, <c>?8</c>), inline ASCII strings (<c>"text"</c>),
///     and endian-typed integer literals (<c>u16le:1000</c>, <c>u32le:0x1234</c>, <c>u64be:42</c>) (#145).
/// </summary>
public sealed class AobPattern
{
    public byte[] Pattern { get; }
    public bool[] Mask { get; }
    public byte[] BitMasks { get; }
    public int Length => Pattern.Length;
    public int FirstExactIndex { get; }
    public int FirstFullyExactIndex { get; }
    public int FirstNibbleMaskedIndex { get; }

    public AobPattern(byte[] pattern, bool[] mask)
        : this(pattern, mask, CreateBitMasksFromBoolMask(mask))
    {
    }

    public AobPattern(byte[] pattern, byte[] bitMasks)
        : this(pattern, CreateBoolMaskFromBitMasks(bitMasks), bitMasks)
    {
    }

    private AobPattern(byte[] pattern, bool[] mask, byte[] bitMasks)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        ArgumentNullException.ThrowIfNull(mask);
        ArgumentNullException.ThrowIfNull(bitMasks);

        if (pattern.Length != mask.Length || pattern.Length != bitMasks.Length)
        {
            throw new ArgumentException("Pattern, mask, and bitMasks must have identical length.");
        }

        Pattern = new byte[pattern.Length];
        Mask = new bool[mask.Length];
        BitMasks = new byte[bitMasks.Length];

        int firstFullyExact = -1;
        int firstNonZeroMask = -1;

        for (int i = 0; i < pattern.Length; i++)
        {
            byte bm = bitMasks[i];
            Pattern[i] = (byte)(pattern[i] & bm);
            Mask[i] = bm == 0xFF;
            BitMasks[i] = bm;

            if (firstFullyExact == -1 && bm == 0xFF)
            {
                firstFullyExact = i;
            }

            if (firstNonZeroMask == -1 && bm != 0x00)
            {
                firstNonZeroMask = i;
            }
        }

        FirstFullyExactIndex = firstFullyExact;
        FirstExactIndex = firstFullyExact;
        FirstNibbleMaskedIndex = firstNonZeroMask;
    }

    private static byte[] CreateBitMasksFromBoolMask(bool[] mask)
    {
        ArgumentNullException.ThrowIfNull(mask);
        byte[] bitMasks = new byte[mask.Length];
        for (int i = 0; i < mask.Length; i++)
        {
            bitMasks[i] = mask[i] ? (byte)0xFF : (byte)0x00;
        }
        return bitMasks;
    }

    private static bool[] CreateBoolMaskFromBitMasks(byte[] bitMasks)
    {
        ArgumentNullException.ThrowIfNull(bitMasks);
        bool[] mask = new bool[bitMasks.Length];
        for (int i = 0; i < bitMasks.Length; i++)
        {
            mask[i] = bitMasks[i] == 0xFF;
        }
        return mask;
    }

    /// <summary>
    ///     Parses a signature string containing hex bytes, full wildcards (<c>??</c>, <c>?</c>, <c>xx</c>),
    ///     half-byte nibble wildcards (<c>4?</c>, <c>?8</c>), inline quoted ASCII strings (<c>"CConsole"</c>),
    ///     and typed endian integer literals (<c>u32le:1000</c>, <c>u16be:0x1234</c>) (#145).
    /// </summary>
    public static AobPattern Parse(string signature)
    {
        if (string.IsNullOrWhiteSpace(signature))
        {
            throw new ArgumentException("Signature string cannot be empty.", nameof(signature));
        }

        List<string> tokens = TokenizeSignature(signature);
        if (tokens.Count == 0)
        {
            throw new ArgumentException("Signature string produced zero tokens.", nameof(signature));
        }

        List<byte> patternBytes = new List<byte>(tokens.Count);
        List<byte> bitMasks = new List<byte>(tokens.Count);

        for (int i = 0; i < tokens.Count; i++)
        {
            string token = tokens[i];

            // 1. Inline quoted ASCII string literal: "..."
            if (token.Length >= 2 && token[0] == '"' && token[token.Length - 1] == '"')
            {
                string inner = token.Substring(1, token.Length - 2);
                if (inner.Length == 0)
                {
                    throw CreateParseException(token, i, signature);
                }

                for (int cIdx = 0; cIdx < inner.Length; cIdx++)
                {
                    patternBytes.Add((byte)inner[cIdx]);
                    bitMasks.Add(0xFF);
                }
                continue;
            }

            // 2. Typed integer literal: u8:..., u32le:..., or u32le(...)
            int colonIdx = token.IndexOf(':');
            int parenIdx = token.IndexOf('(');
            if (colonIdx > 0)
            {
                AppendTypedLiteralToken(token, colonIdx, i, signature, patternBytes, bitMasks);
                continue;
            }
            if (parenIdx > 0 && token.EndsWith(')'))
            {
                AppendTypedLiteralToken(token.Substring(0, token.Length - 1), parenIdx, i, signature, patternBytes, bitMasks);
                continue;
            }

            // 3. Full-byte wildcard (?, ??, xx, XX)
            if (token == "?" || token == "??" || token.Equals("xx", StringComparison.OrdinalIgnoreCase))
            {
                patternBytes.Add(0x00);
                bitMasks.Add(0x00);
                continue;
            }

            // 4. Half-byte nibble wildcard (4? or ?8)
            if (token.Length == 2 && (token[0] == '?' || token[1] == '?'))
            {
                if (token[0] == '?')
                {
                    int loNibble = TryParseHexDigit(token[1]);
                    if (loNibble < 0)
                    {
                        throw CreateParseException(token, i, signature);
                    }
                    patternBytes.Add((byte)loNibble);
                    bitMasks.Add(0x0F);
                }
                else
                {
                    int hiNibble = TryParseHexDigit(token[0]);
                    if (hiNibble < 0)
                    {
                        throw CreateParseException(token, i, signature);
                    }
                    patternBytes.Add((byte)(hiNibble << 4));
                    bitMasks.Add(0xF0);
                }
                continue;
            }

            // 5. Standard exact hex byte (e.g. 48, 89, 5C)
            if (!byte.TryParse(token, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte exactByte))
            {
                throw CreateParseException(token, i, signature);
            }

            patternBytes.Add(exactByte);
            bitMasks.Add(0xFF);
        }

        return new AobPattern(patternBytes.ToArray(), bitMasks.ToArray());
    }

    /// <summary>
    ///     Creates an exact (non-wildcard) pattern from a raw byte array.
    /// </summary>
    public static AobPattern FromBytes(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        byte[] bitMasks = new byte[bytes.Length];
        Array.Fill(bitMasks, (byte)0xFF);
        return new AobPattern((byte[])bytes.Clone(), bitMasks);
    }

    private static List<string> TokenizeSignature(string signature)
    {
        List<string> tokens = new List<string>();
        StringBuilder sb = new StringBuilder();
        bool inQuotes = false;

        for (int i = 0; i < signature.Length; i++)
        {
            char c = signature[i];
            if (inQuotes)
            {
                if (c == '\\' && i + 1 < signature.Length)
                {
                    char next = signature[++i];
                    char unescaped = next switch
                    {
                        'n' => '\n',
                        'r' => '\r',
                        't' => '\t',
                        '0' => '\0',
                        '"' => '"',
                        '\\' => '\\',
                        _ => next
                    };
                    sb.Append(unescaped);
                }
                else if (c == '"')
                {
                    sb.Append('"');
                    inQuotes = false;
                    tokens.Add(sb.ToString());
                    sb.Clear();
                }
                else
                {
                    sb.Append(c);
                }
            }
            else
            {
                if (c == '"')
                {
                    if (sb.Length > 0)
                    {
                        tokens.Add(sb.ToString());
                        sb.Clear();
                    }
                    sb.Append('"');
                    inQuotes = true;
                }
                else if (char.IsWhiteSpace(c))
                {
                    if (sb.Length > 0)
                    {
                        tokens.Add(sb.ToString());
                        sb.Clear();
                    }
                }
                else
                {
                    sb.Append(c);
                }
            }
        }

        if (inQuotes)
        {
            throw new FormatException($"Unterminated quoted string literal in AOB signature '{signature}'.");
        }

        if (sb.Length > 0)
        {
            tokens.Add(sb.ToString());
        }

        return tokens;
    }

    private static void AppendTypedLiteralToken(
        string token,
        int colonIdx,
        int tokenIndex,
        string fullSig,
        List<byte> patternBytes,
        List<byte> bitMasks)
    {
        string typeSpec = token.Substring(0, colonIdx).ToLowerInvariant();
        string valText = token.Substring(colonIdx + 1);
        if (string.IsNullOrWhiteSpace(valText))
        {
            throw CreateParseException(token, tokenIndex, fullSig);
        }

        bool isHex = valText.StartsWith("0x", StringComparison.OrdinalIgnoreCase);
        string cleanVal = isHex ? valText.Substring(2) : valText;
        NumberStyles numStyle = isHex ? NumberStyles.HexNumber : NumberStyles.Integer;

        Span<byte> scratch = stackalloc byte[8];
        int byteCount;

        switch (typeSpec)
        {
            case "u8":
                if (!byte.TryParse(cleanVal, numStyle, CultureInfo.InvariantCulture, out byte u8))
                    throw CreateParseException(token, tokenIndex, fullSig);
                scratch[0] = u8;
                byteCount = 1;
                break;

            case "i8":
                if (!sbyte.TryParse(cleanVal, numStyle, CultureInfo.InvariantCulture, out sbyte i8))
                    throw CreateParseException(token, tokenIndex, fullSig);
                scratch[0] = (byte)i8;
                byteCount = 1;
                break;

            case "u16le":
            case "u16":
                if (!ushort.TryParse(cleanVal, numStyle, CultureInfo.InvariantCulture, out ushort u16Le))
                    throw CreateParseException(token, tokenIndex, fullSig);
                BinaryPrimitives.WriteUInt16LittleEndian(scratch, u16Le);
                byteCount = 2;
                break;

            case "u16be":
                if (!ushort.TryParse(cleanVal, numStyle, CultureInfo.InvariantCulture, out ushort u16Be))
                    throw CreateParseException(token, tokenIndex, fullSig);
                BinaryPrimitives.WriteUInt16BigEndian(scratch, u16Be);
                byteCount = 2;
                break;

            case "i16le":
            case "i16":
                if (!short.TryParse(cleanVal, numStyle, CultureInfo.InvariantCulture, out short i16Le))
                    throw CreateParseException(token, tokenIndex, fullSig);
                BinaryPrimitives.WriteInt16LittleEndian(scratch, i16Le);
                byteCount = 2;
                break;

            case "i16be":
                if (!short.TryParse(cleanVal, numStyle, CultureInfo.InvariantCulture, out short i16Be))
                    throw CreateParseException(token, tokenIndex, fullSig);
                BinaryPrimitives.WriteInt16BigEndian(scratch, i16Be);
                byteCount = 2;
                break;

            case "u32le":
            case "u32":
                if (!uint.TryParse(cleanVal, numStyle, CultureInfo.InvariantCulture, out uint u32Le))
                    throw CreateParseException(token, tokenIndex, fullSig);
                BinaryPrimitives.WriteUInt32LittleEndian(scratch, u32Le);
                byteCount = 4;
                break;

            case "u32be":
                if (!uint.TryParse(cleanVal, numStyle, CultureInfo.InvariantCulture, out uint u32Be))
                    throw CreateParseException(token, tokenIndex, fullSig);
                BinaryPrimitives.WriteUInt32BigEndian(scratch, u32Be);
                byteCount = 4;
                break;

            case "i32le":
            case "i32":
                if (!int.TryParse(cleanVal, numStyle, CultureInfo.InvariantCulture, out int i32Le))
                    throw CreateParseException(token, tokenIndex, fullSig);
                BinaryPrimitives.WriteInt32LittleEndian(scratch, i32Le);
                byteCount = 4;
                break;

            case "i32be":
                if (!int.TryParse(cleanVal, numStyle, CultureInfo.InvariantCulture, out int i32Be))
                    throw CreateParseException(token, tokenIndex, fullSig);
                BinaryPrimitives.WriteInt32BigEndian(scratch, i32Be);
                byteCount = 4;
                break;

            case "u64le":
            case "u64":
                if (!ulong.TryParse(cleanVal, numStyle, CultureInfo.InvariantCulture, out ulong u64Le))
                    throw CreateParseException(token, tokenIndex, fullSig);
                BinaryPrimitives.WriteUInt64LittleEndian(scratch, u64Le);
                byteCount = 8;
                break;

            case "u64be":
                if (!ulong.TryParse(cleanVal, numStyle, CultureInfo.InvariantCulture, out ulong u64Be))
                    throw CreateParseException(token, tokenIndex, fullSig);
                BinaryPrimitives.WriteUInt64BigEndian(scratch, u64Be);
                byteCount = 8;
                break;

            case "i64le":
            case "i64":
                if (!long.TryParse(cleanVal, numStyle, CultureInfo.InvariantCulture, out long i64Le))
                    throw CreateParseException(token, tokenIndex, fullSig);
                BinaryPrimitives.WriteInt64LittleEndian(scratch, i64Le);
                byteCount = 8;
                break;

            case "i64be":
                if (!long.TryParse(cleanVal, numStyle, CultureInfo.InvariantCulture, out long i64Be))
                    throw CreateParseException(token, tokenIndex, fullSig);
                BinaryPrimitives.WriteInt64BigEndian(scratch, i64Be);
                byteCount = 8;
                break;

            default:
                throw CreateParseException(token, tokenIndex, fullSig);
        }

        for (int b = 0; b < byteCount; b++)
        {
            patternBytes.Add(scratch[b]);
            bitMasks.Add(0xFF);
        }
    }

    private static int TryParseHexDigit(char c)
    {
        if (c >= '0' && c <= '9') return c - '0';
        if (c >= 'a' && c <= 'f') return c - 'a' + 10;
        if (c >= 'A' && c <= 'F') return c - 'A' + 10;
        return -1;
    }

    private static FormatException CreateParseException(string token, int index, string fullSig) =>
        new FormatException($"Invalid hex or typed token '{token}' at position {index} in signature '{fullSig}'.");
}

/// <summary>
///     Result of an AOB pattern verification or scan operation.
/// </summary>
public readonly record struct AobScanResult(
    bool Found,
    int Offset,
    bool WasFastPath,
    int MatchCount,
    string? ErrorMessage = null
)
{
    public static AobScanResult FastPathSuccess(int offset) =>
        new(Found: true, Offset: offset, WasFastPath: true, MatchCount: 1);

    public static AobScanResult ScanSuccess(int offset) =>
        new(Found: true, Offset: offset, WasFastPath: false, MatchCount: 1);

    public static AobScanResult NotFound() =>
        new(Found: false, Offset: -1, WasFastPath: false, MatchCount: 0, ErrorMessage: "Pattern not found in target buffer.");

    public static AobScanResult Ambiguous(int matchCount) =>
        new(Found: false, Offset: -2, WasFastPath: false, MatchCount: matchCount, ErrorMessage: $"Pattern matched {matchCount} locations; ambiguous match rejected for safety.");
}

/// <summary>
///     High-performance SIMD/Span-accelerated Array of Bytes (AOB) pattern scanner for native binaries and memory buffers.
/// </summary>
public static class AobScanner
{
    /// <summary>
    ///     Finds the first occurrence of the specified AOB pattern within the data span,
    ///     supporting nibble masks (<c>4?</c>, <c>?8</c>) and optional address alignment stride (#145).
    ///     Returns -1 if not found.
    /// </summary>
    public static int FindPattern(ReadOnlySpan<byte> buffer, AobPattern pattern, int startIndex = 0, int alignment = 1)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        if (buffer.IsEmpty || pattern.Length == 0) return -1;
        if (startIndex < 0 || startIndex > buffer.Length - pattern.Length) return -1;

        int stride = Math.Max(1, alignment);
        if (stride > 1)
        {
            int rem = startIndex % stride;
            if (rem != 0)
            {
                startIndex += stride - rem;
                if (startIndex > buffer.Length - pattern.Length)
                {
                    return -1;
                }
            }
        }

        int searchLimit = buffer.Length - pattern.Length;
        ReadOnlySpan<byte> patternBytes = pattern.Pattern;
        ReadOnlySpan<byte> bitMasks = pattern.BitMasks;

        // If pattern is 100% wildcards, return startIndex
        if (pattern.FirstNibbleMaskedIndex == -1)
        {
            return startIndex;
        }

        int firstExact = pattern.FirstFullyExactIndex;

        // Fast SIMD IndexOf anchor path when stride == 1 and at least one byte has BitMasks[i] == 0xFF
        if (stride == 1 && firstExact >= 0)
        {
            byte anchorByte = patternBytes[firstExact];
            int i = startIndex;
            while (i <= searchLimit)
            {
                int relativeFirst = buffer.Slice(i + firstExact).IndexOf(anchorByte);
                if (relativeFirst < 0)
                {
                    return -1;
                }

                int candidate = i + relativeFirst;
                if (candidate > searchLimit)
                {
                    return -1;
                }

                if (IsMatchBitMasked(buffer.Slice(candidate, pattern.Length), patternBytes, bitMasks))
                {
                    return candidate;
                }

                i = candidate + 1;
            }

            return -1;
        }

        // Strided or pure-nibble-mask scan path
        for (int candidate = startIndex; candidate <= searchLimit; candidate += stride)
        {
            if (IsMatchBitMasked(buffer.Slice(candidate, pattern.Length), patternBytes, bitMasks))
            {
                return candidate;
            }
        }

        return -1;
    }

    /// <summary>
    ///     Finds all occurrences of the specified AOB pattern within the data span.
    /// </summary>
    public static List<int> FindAll(ReadOnlySpan<byte> buffer, AobPattern pattern, int alignment = 1)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        List<int> results = new List<int>();
        if (buffer.IsEmpty || pattern.Length == 0 || buffer.Length < pattern.Length)
        {
            return results;
        }

        int stride = Math.Max(1, alignment);
        int offset = 0;

        while (offset <= buffer.Length - pattern.Length)
        {
            int found = FindPattern(buffer, pattern, offset, stride);
            if (found < 0)
            {
                break;
            }

            results.Add(found);
            offset = found + stride;
        }

        return results;
    }

    /// <summary>
    ///     Checks if a fast-path RVA hint matches the expected bytes.
    ///     If the hint misses or is negative, scans the entire buffer and verifies uniqueness.
    /// </summary>
    public static AobScanResult VerifyFastPathOrScan(
        ReadOnlySpan<byte> buffer,
        int rvaHint,
        AobPattern pattern,
        ReadOnlySpan<byte> expectedBytes,
        int offsetFromPattern = 0)
    {
        ArgumentNullException.ThrowIfNull(pattern);

        // 1. Fast Path: Check if bytes at hinted offset match the expected pattern or expected bytes
        if (rvaHint >= 0 && rvaHint + expectedBytes.Length <= buffer.Length)
        {
            if (buffer.Slice(rvaHint, expectedBytes.Length).SequenceEqual(expectedBytes))
            {
                return AobScanResult.FastPathSuccess(rvaHint);
            }
        }

        // 2. Scan Path: Fast path missed, perform full buffer scan
        List<int> matches = FindAll(buffer, pattern);
        if (matches.Count == 0)
        {
            return AobScanResult.NotFound();
        }

        if (matches.Count > 1)
        {
            return AobScanResult.Ambiguous(matches.Count);
        }

        int targetOffset = matches[0] + offsetFromPattern;
        return AobScanResult.ScanSuccess(targetOffset);
    }

    private static bool IsMatchBitMasked(ReadOnlySpan<byte> candidate, ReadOnlySpan<byte> pattern, ReadOnlySpan<byte> bitMasks)
    {
        for (int i = 0; i < pattern.Length; i++)
        {
            byte mask = bitMasks[i];
            if (mask != 0 && (candidate[i] & mask) != pattern[i])
            {
                return false;
            }
        }
        return true;
    }

    public const int DefaultStreamChunkSizeBytes = 10 * 1024 * 1024; // 10 MiB (yara_rule.cpp:135-177)

    /// <summary>
    /// Phase 13 (#162): Scans an arbitrary <see cref="Stream"/> (including &gt; 2 GiB minidumps and <c>.s2z</c> archives)
    /// using a bounded 10 MiB sliding-window buffer (<c>YR_MEMORY_BLOCK_ITERATOR</c> pattern, <c>yara_rule.cpp:135-215</c>)
    /// with <c>pattern.Length - 1</c> boundary overlap and 64-bit <see cref="long"/> offsets.
    /// </summary>
    public static AobStreamScanResult ScanStreamChunked(
        Stream stream,
        AobPattern pattern,
        int chunkSizeBytes = DefaultStreamChunkSizeBytes,
        int alignment = 1,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(pattern);

        if (pattern.Length == 0)
        {
            return new AobStreamScanResult(false, -1L, 0, Array.Empty<long>());
        }

        int stride = Math.Max(1, alignment);
        int overlap = Math.Max(0, pattern.Length - 1);
        int effectiveChunkPayload = Math.Max(pattern.Length * 2, chunkSizeBytes);
        int bufferCapacity = effectiveChunkPayload + overlap;

        byte[] rentedBuffer = ArrayPool<byte>.Shared.Rent(bufferCapacity);
        try
        {
            long streamBaseOffset = 0L;
            int carryOverBytes = 0;
            int chunkCount = 0;
            List<long> allMatches = new List<long>();

            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();

                int bytesRead = 0;
                while (bytesRead < effectiveChunkPayload)
                {
                    int read = stream.Read(rentedBuffer, carryOverBytes + bytesRead, effectiveChunkPayload - bytesRead);
                    if (read <= 0)
                    {
                        break;
                    }

                    bytesRead += read;
                }

                if (bytesRead == 0)
                {
                    break;
                }

                chunkCount++;
                int windowValidLength = carryOverBytes + bytesRead;
                if (windowValidLength >= pattern.Length)
                {
                    ReadOnlySpan<byte> windowSpan = rentedBuffer.AsSpan(0, windowValidLength);
                    int localStart = 0;

                    if (stride > 1)
                    {
                        long globalWindowStart = streamBaseOffset;
                        long rem = globalWindowStart % stride;
                        if (rem != 0)
                        {
                            localStart = (int)(stride - rem);
                        }
                    }

                    int searchEnd = windowValidLength - pattern.Length;
                    while (localStart <= searchEnd)
                    {
                        int foundLocal = FindPattern(windowSpan, pattern, localStart, stride);
                        if (foundLocal < 0)
                        {
                            break;
                        }

                        long globalOffset = streamBaseOffset + foundLocal;
                        if (allMatches.Count == 0 || allMatches[allMatches.Count - 1] != globalOffset)
                        {
                            allMatches.Add(globalOffset);
                        }

                        localStart = foundLocal + stride;
                    }
                }

                if (bytesRead < effectiveChunkPayload)
                {
                    break;
                }

                if (overlap > 0)
                {
                    Buffer.BlockCopy(rentedBuffer, windowValidLength - overlap, rentedBuffer, 0, overlap);
                }

                streamBaseOffset += (windowValidLength - overlap);
                carryOverBytes = overlap;
            }

            if (allMatches.Count == 0)
            {
                return new AobStreamScanResult(false, -1L, chunkCount, allMatches);
            }

            return new AobStreamScanResult(true, allMatches[0], chunkCount, allMatches);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rentedBuffer);
        }
    }
}

/// <summary>
/// Result of a chunked 64-bit stream AOB scan (#162).
/// </summary>
public sealed record AobStreamScanResult(
    bool Found,
    long FirstOffset,
    int ChunksScanned,
    IReadOnlyList<long> AllOffsets);

