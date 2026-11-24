// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace Kongor.Core.Diagnostics;

/// <summary>
/// Reason why <see cref="CustomTableEncodingAndPrefixAuditor.DecodeBounded"/> stopped decoding
/// (<c>encoding_file.cpp:485-626</c>).
/// </summary>
public enum DecodeStopReason
{
    Completed = 0,
    MaxCharsReached = 1,
    ControlCharStopped = 2,
    StopTokenReached = 3,
    IncompleteSequence = 4,
    UnmappedByte = 5
}

/// <summary>
/// Options controlling bounded custom-table decoding (<c>encoding_file.cpp:DecodeOptions</c>).
/// </summary>
public sealed record DecodeTableOptions(
    int MaxChars = int.MaxValue,
    bool StopOnUnmappedByte = false,
    bool StopOnControlCharacter = false,
    string? FallbackReplacement = "?",
    string? StopToken = null);

/// <summary>
/// Result of bounded custom-table decoding.
/// </summary>
public sealed record DecodeTableResult(
    string DecodedText,
    int BytesConsumed,
    int CharsProduced,
    DecodeStopReason StopReason);

/// <summary>
/// Represents an ambiguous prefix collision between two variable-length byte sequences in a custom encoding table.
/// </summary>
public sealed record PrefixAmbiguityConflict(
    byte[] PrefixBytes,
    byte[] LongerBytes,
    string PrefixGlyph,
    string LongerGlyph);

/// <summary>
/// Phase 13 (#160): Longest-Match Bucketed <c>.tbl</c> Custom Encoding Codec, Path-Traversal Sandbox
/// &amp; O(N log N) Lexicographic Prefix-Free Ambiguity Auditor (<c>encoding_file.cpp:145-811</c> port).
/// </summary>
public sealed class CustomTableEncodingAndPrefixAuditor
{
    private sealed class ByteSequenceEqualityComparer : IEqualityComparer<byte[]>
    {
        public static readonly ByteSequenceEqualityComparer Instance = new ByteSequenceEqualityComparer();

        public bool Equals(byte[]? x, byte[]? y)
        {
            if (ReferenceEquals(x, y)) return true;
            if (x == null || y == null || x.Length != y.Length) return false;
            return x.AsSpan().SequenceEqual(y);
        }

        public int GetHashCode(byte[] obj)
        {
            HashCode hc = new HashCode();
            hc.AddBytes(obj);
            return hc.ToHashCode();
        }
    }

    // Reverse-length ordered buckets: key is byte-sequence length, sorted descending during lookup
    private readonly SortedDictionary<int, Dictionary<byte[], string>> _lengthBuckets =
        new SortedDictionary<int, Dictionary<byte[], string>>(Comparer<int>.Create(static (int a, int b) => b.CompareTo(a)));

    private int _entryCount;
    private int _maxKeyLength;

    public int EntryCount => _entryCount;
    public int MaxSequenceByteLength => _maxKeyLength;

    /// <summary>
    /// Adds a raw byte-sequence to glyph mapping into the reverse-length bucketed table.
    /// </summary>
    public void AddMapping(ReadOnlySpan<byte> sequence, string glyph)
    {
        if (sequence.Length == 0)
        {
            throw new ArgumentException("Sequence must contain at least 1 byte.", nameof(sequence));
        }

        ArgumentNullException.ThrowIfNull(glyph);

        int len = sequence.Length;
        if (!_lengthBuckets.TryGetValue(len, out Dictionary<byte[], string>? bucket))
        {
            bucket = new Dictionary<byte[], string>(ByteSequenceEqualityComparer.Instance);
            _lengthBuckets[len] = bucket;
        }

        byte[] key = sequence.ToArray();
        if (!bucket.ContainsKey(key))
        {
            _entryCount++;
        }

        bucket[key] = glyph;
        if (len > _maxKeyLength)
        {
            _maxKeyLength = len;
        }
    }

    /// <summary>
    /// Parses a Thingy <c>.tbl</c> content string (<c>XX=glyph</c>, <c>XXXX=glyph</c>, comments <c>#</c>,
    /// and cycle-checked <c>-include path</c> directives within <paramref name="allowedIncludeRoot"/>,
    /// <c>encoding_file.cpp:628-723</c>).
    /// </summary>
    public static CustomTableEncodingAndPrefixAuditor ParseTableContent(
        string content,
        string? allowedIncludeRoot = null,
        Func<string, string?>? includeResolver = null)
    {
        ArgumentNullException.ThrowIfNull(content);
        CustomTableEncodingAndPrefixAuditor table = new CustomTableEncodingAndPrefixAuditor();
        HashSet<string> visitedIncludes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        table.ParseContentInternal(content, allowedIncludeRoot, includeResolver, visitedIncludes);
        return table;
    }

    private void ParseContentInternal(
        string content,
        string? allowedIncludeRoot,
        Func<string, string?>? includeResolver,
        HashSet<string> visitedIncludes)
    {
        using StringReader reader = new StringReader(content);
        string? rawLine;
        while ((rawLine = reader.ReadLine()) != null)
        {
            string trimmed = rawLine.TrimStart();
            if (trimmed.Length == 0 || trimmed.StartsWith('#') || trimmed.StartsWith("//", StringComparison.Ordinal))
            {
                continue;
            }

            if (trimmed.StartsWith("-include ", StringComparison.OrdinalIgnoreCase))
            {
                string includeTarget = trimmed.Substring("-include ".Length).Trim().Trim('"');
                if (includeTarget.Length == 0)
                {
                    continue;
                }

                if (allowedIncludeRoot != null)
                {
                    string candidateFull = Path.GetFullPath(Path.Combine(allowedIncludeRoot, includeTarget));
                    if (!IsWithinAllowedDirectory(candidateFull, allowedIncludeRoot))
                    {
                        throw new InvalidOperationException($"Blocked path traversal in .tbl -include directive: '{includeTarget}'.");
                    }

                    if (!visitedIncludes.Add(candidateFull))
                    {
                        throw new InvalidOperationException($"Detected cyclic .tbl -include directive for '{includeTarget}'.");
                    }

                    string? includedText = includeResolver != null
                        ? includeResolver(candidateFull)
                        : (File.Exists(candidateFull) ? File.ReadAllText(candidateFull) : null);

                    if (includedText != null)
                    {
                        ParseContentInternal(includedText, allowedIncludeRoot, includeResolver, visitedIncludes);
                    }
                }
                else
                {
                    if (!visitedIncludes.Add(includeTarget))
                    {
                        throw new InvalidOperationException($"Detected cyclic .tbl -include directive for '{includeTarget}'.");
                    }

                    string? includedText = includeResolver?.Invoke(includeTarget);
                    if (includedText != null)
                    {
                        ParseContentInternal(includedText, null, includeResolver, visitedIncludes);
                    }
                }

                continue;
            }

            // Find first '=' separator in "HEX=glyph" (note that glyph itself may be '=')
            int eqIndex = rawLine.IndexOf('=');
            if (eqIndex <= 0)
            {
                continue;
            }

            string hexPart = rawLine.Substring(0, eqIndex).Trim();
            string glyphPart = rawLine.Substring(eqIndex + 1);
            if (hexPart.Length == 0 || (hexPart.Length % 2) != 0)
            {
                continue;
            }

            byte[] seq = new byte[hexPart.Length / 2];
            bool validHex = true;
            for (int i = 0; i < seq.Length; i++)
            {
                if (!byte.TryParse(hexPart.AsSpan(i * 2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out seq[i]))
                {
                    validHex = false;
                    break;
                }
            }

            if (!validHex)
            {
                continue;
            }

            if (glyphPart.Equals(@"\n", StringComparison.Ordinal))
            {
                glyphPart = "\n";
            }
            else if (glyphPart.Equals(@"\r", StringComparison.Ordinal))
            {
                glyphPart = "\r";
            }
            else if (glyphPart.Equals(@"\0", StringComparison.Ordinal))
            {
                glyphPart = "\0";
            }

            AddMapping(seq, glyphPart);
        }
    }

    /// <summary>
    /// Canonical path-containment sandbox (<c>encoding_file.cpp:145-167</c>) verifying that
    /// <paramref name="candidatePath"/> resolves strictly inside <paramref name="allowedRootDirectory"/>.
    /// </summary>
    public static bool IsWithinAllowedDirectory(string candidatePath, string allowedRootDirectory)
    {
        if (string.IsNullOrWhiteSpace(candidatePath) || string.IsNullOrWhiteSpace(allowedRootDirectory))
        {
            return false;
        }

        string fullCandidate = Path.GetFullPath(candidatePath)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string fullRoot = Path.GetFullPath(allowedRootDirectory)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        if (fullCandidate.Equals(fullRoot, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        string rootWithSlash = fullRoot + Path.DirectorySeparatorChar;
        return fullCandidate.StartsWith(rootWithSlash, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Decodes <paramref name="input"/> using longest-match reverse-length bucketed lookup
    /// (<c>encoding_file.cpp:485-626</c>).
    /// </summary>
    public DecodeTableResult DecodeBounded(ReadOnlySpan<byte> input, DecodeTableOptions? options = null)
    {
        DecodeTableOptions opts = options ?? new DecodeTableOptions();
        StringBuilder sb = new StringBuilder(Math.Min(input.Length, 4096));
        int offset = 0;
        int charsProduced = 0;

        while (offset < input.Length)
        {
            if (charsProduced >= opts.MaxChars)
            {
                return new DecodeTableResult(sb.ToString(), offset, charsProduced, DecodeStopReason.MaxCharsReached);
            }

            int remaining = input.Length - offset;
            string? matchedGlyph = null;
            int matchedByteLen = 0;
            bool hadLongerPrefixCandidate = false;

            foreach (KeyValuePair<int, Dictionary<byte[], string>> kvp in _lengthBuckets)
            {
                int bucketLen = kvp.Key;
                if (bucketLen > remaining)
                {
                    // Check if the remaining tail is a prefix of any entry in this longer bucket
                    foreach (KeyValuePair<byte[], string> entry in kvp.Value)
                    {
                        if (entry.Key.AsSpan(0, remaining).SequenceEqual(input.Slice(offset, remaining)))
                        {
                            hadLongerPrefixCandidate = true;
                            break;
                        }
                    }

                    continue;
                }

                byte[] sliceKey = input.Slice(offset, bucketLen).ToArray();
                if (kvp.Value.TryGetValue(sliceKey, out string? glyph))
                {
                    matchedGlyph = glyph;
                    matchedByteLen = bucketLen;
                    break;
                }
            }

            if (matchedGlyph != null)
            {
                if (opts.StopToken != null && matchedGlyph.Equals(opts.StopToken, StringComparison.Ordinal))
                {
                    offset += matchedByteLen;
                    return new DecodeTableResult(sb.ToString(), offset, charsProduced, DecodeStopReason.StopTokenReached);
                }

                if (opts.StopOnControlCharacter && matchedGlyph.Length == 1 && char.IsControl(matchedGlyph[0]) && matchedGlyph[0] != '\n' && matchedGlyph[0] != '\r' && matchedGlyph[0] != '\t')
                {
                    return new DecodeTableResult(sb.ToString(), offset, charsProduced, DecodeStopReason.ControlCharStopped);
                }

                sb.Append(matchedGlyph);
                offset += matchedByteLen;
                charsProduced++;
            }
            else
            {
                if (hadLongerPrefixCandidate)
                {
                    return new DecodeTableResult(sb.ToString(), offset, charsProduced, DecodeStopReason.IncompleteSequence);
                }

                if (opts.StopOnUnmappedByte)
                {
                    return new DecodeTableResult(sb.ToString(), offset, charsProduced, DecodeStopReason.UnmappedByte);
                }

                if (opts.FallbackReplacement != null)
                {
                    sb.Append(opts.FallbackReplacement);
                    charsProduced++;
                }

                offset++;
            }
        }

        return new DecodeTableResult(sb.ToString(), offset, charsProduced, DecodeStopReason.Completed);
    }

    /// <summary>
    /// Verifies in O(N log N) time whether any byte sequence in the table is a strict prefix of another
    /// sequence (<c>encoding_file.cpp:765-811</c>). Sorts all keys lexicographically and checks adjacent pairs.
    /// </summary>
    public bool HasAmbiguousPrefixSequences(out PrefixAmbiguityConflict? firstConflict)
    {
        firstConflict = null;
        if (_entryCount <= 1)
        {
            return false;
        }

        List<(byte[] Key, string Glyph)> allEntries = new List<(byte[] Key, string Glyph)>(_entryCount);
        foreach (KeyValuePair<int, Dictionary<byte[], string>> bucket in _lengthBuckets)
        {
            foreach (KeyValuePair<byte[], string> entry in bucket.Value)
            {
                allEntries.Add((entry.Key, entry.Value));
            }
        }

        allEntries.Sort(static ((byte[] Key, string Glyph) a, (byte[] Key, string Glyph) b) =>
        {
            int minLen = Math.Min(a.Key.Length, b.Key.Length);
            for (int i = 0; i < minLen; i++)
            {
                int cmp = a.Key[i].CompareTo(b.Key[i]);
                if (cmp != 0)
                {
                    return cmp;
                }
            }

            return a.Key.Length.CompareTo(b.Key.Length);
        });

        for (int i = 0; i + 1 < allEntries.Count; i++)
        {
            byte[] shorter = allEntries[i].Key;
            byte[] next = allEntries[i + 1].Key;

            if (shorter.Length < next.Length &&
                next.AsSpan(0, shorter.Length).SequenceEqual(shorter))
            {
                firstConflict = new PrefixAmbiguityConflict(
                    PrefixBytes: shorter,
                    LongerBytes: next,
                    PrefixGlyph: allEntries[i].Glyph,
                    LongerGlyph: allEntries[i + 1].Glyph);
                return true;
            }
        }

        return false;
    }
}
