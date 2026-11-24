// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System;
using System.Collections.Generic;

namespace ASPIRE.Common.Diagnostics;

/// <summary>
///     Xash3D-FWGS (<c>public/crtlib.c</c> <c>COM_CheckNastyPath</c>, <c>matchpattern_with_separator</c>,
///     and <c>engine/common/cmd.c</c> <c>Cbuf_ExecuteCommandsFromBuffer</c>) VFS path traversal firewall,
///     wildcard glob matcher, and quote-balanced command buffer sanitizer (#135, #137, #144).
/// </summary>
internal static class VfsPathFirewall
{
    public const int MaxCmdLineLength = 2048;

    /// <summary>
    ///     Validates a virtual or relative archive path against directory traversal (<c>..</c>),
    ///     drive/NTFS stream colons (<c>:</c>), dot segments (<c>/./</c>), control characters,
    ///     trailing dots/spaces, and Windows reserved DOS device names (<c>CON</c>, <c>PRN</c>,
    ///     <c>AUX</c>, <c>NUL</c>, <c>COM1..9</c>, <c>LPT1..9</c>) (#144).
    /// </summary>
    /// <returns><c>true</c> if the path is safe; <c>false</c> if the path is nasty/unsafe.</returns>
    public static bool CheckNastyPath(ReadOnlySpan<char> path, bool allowLeadingSlash = false)
    {
        if (path.IsEmpty || path.Length > 1024)
        {
            return false;
        }

        if (!allowLeadingSlash && (path[0] == '/' || path[0] == '\\'))
        {
            return false;
        }

        for (int i = 0; i < path.Length; i++)
        {
            char c = path[i];
            // Reject ASCII control characters, drive/stream colons, and wildcard characters in literal paths
            if (c < 0x20 || c == 0x7F || c == ':' || c == '*' || c == '?' || c == '"' || c == '<' || c == '>' || c == '|')
            {
                return false;
            }
        }

        // Inspect each slash-delimited path component
        int segStart = 0;
        while (segStart <= path.Length)
        {
            int segEnd = segStart;
            while (segEnd < path.Length && path[segEnd] != '/' && path[segEnd] != '\\')
            {
                segEnd++;
            }

            int segLen = segEnd - segStart;
            if (segLen == 0)
            {
                // Reject empty segments except an optional allowed leading slash
                if (!(segStart == 0 && allowLeadingSlash && path.Length > 1))
                {
                    return false;
                }
            }
            else
            {
                ReadOnlySpan<char> segment = path.Slice(segStart, segLen);

                // Reject "." and ".." segments
                if ((segLen == 1 && segment[0] == '.') ||
                    (segLen == 2 && segment[0] == '.' && segment[1] == '.'))
                {
                    return false;
                }

                // Windows silently strips trailing dots/spaces when resolving files, enabling spoofing
                char lastChar = segment[segLen - 1];
                if (lastChar == '.' || lastChar == ' ')
                {
                    return false;
                }

                // Extract stem before first '.' to check reserved DOS device names (e.g. "CON.txt", "NUL.dds")
                int dotIndex = segment.IndexOf('.');
                ReadOnlySpan<char> stem = dotIndex >= 0 ? segment.Slice(0, dotIndex) : segment;
                if (IsReservedDosDeviceName(stem))
                {
                    return false;
                }
            }

            segStart = segEnd + 1;
        }

        return true;
    }

    /// <summary>
    ///     Checks whether a filename stem matches a reserved Windows DOS device name
    ///     (<c>CON</c>, <c>PRN</c>, <c>AUX</c>, <c>NUL</c>, <c>COM1..9</c>, <c>LPT1..9</c>).
    /// </summary>
    public static bool IsReservedDosDeviceName(ReadOnlySpan<char> stem)
    {
        if (stem.Length == 3)
        {
            if (stem.Equals("CON".AsSpan(), StringComparison.OrdinalIgnoreCase) ||
                stem.Equals("PRN".AsSpan(), StringComparison.OrdinalIgnoreCase) ||
                stem.Equals("AUX".AsSpan(), StringComparison.OrdinalIgnoreCase) ||
                stem.Equals("NUL".AsSpan(), StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        else if (stem.Length == 4)
        {
            char digit = stem[3];
            if (digit >= '1' && digit <= '9')
            {
                ReadOnlySpan<char> prefix = stem.Slice(0, 3);
                if (prefix.Equals("COM".AsSpan(), StringComparison.OrdinalIgnoreCase) ||
                    prefix.Equals("LPT".AsSpan(), StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    ///     Non-recursive linear-time wildcard glob matcher (<c>*</c> and <c>?</c>) with slash normalization,
    ///     modeled on Xash3D <c>matchpattern_with_separator</c> (#135, #144).
    /// </summary>
    public static bool MatchWildcard(ReadOnlySpan<char> input, ReadOnlySpan<char> pattern, bool caseInsensitive = true)
    {
        int i = 0;
        int p = 0;
        int starIdx = -1;
        int matchIdx = 0;

        while (i < input.Length)
        {
            if (p < pattern.Length && (pattern[p] == '?' || CharsEqualNormalized(input[i], pattern[p], caseInsensitive)))
            {
                i++;
                p++;
            }
            else if (p < pattern.Length && pattern[p] == '*')
            {
                starIdx = p;
                matchIdx = i;
                p++;
            }
            else if (starIdx != -1)
            {
                p = starIdx + 1;
                matchIdx++;
                i = matchIdx;
            }
            else
            {
                return false;
            }
        }

        while (p < pattern.Length && pattern[p] == '*')
        {
            p++;
        }

        return p == pattern.Length;
    }

    /// <summary>
    ///     Quote-balanced (<c>"</c> with <c>\"</c> and <c>\\</c> escape handling + <c>//</c> comment stripping outside quotes)
    ///     command buffer splitter and privilege firewall modeled on Xash3D <c>Cbuf_ExecuteCommandsFromBuffer</c>
    ///     and <c>Cmd_ShouldAllowCommand</c> (#137).
    /// </summary>
    public static IReadOnlyList<string> SplitAndFilterCommandBuffer(
        ReadOnlySpan<char> buffer,
        bool isPrivileged,
        out int blockedCount,
        out bool wasTruncated)
    {
        blockedCount = 0;
        wasTruncated = false;

        List<string> allowedCommands = new List<string>();
        if (buffer.IsEmpty)
        {
            return allowedCommands;
        }

        int lineStart = 0;
        bool inQuotes = false;

        for (int i = 0; i <= buffer.Length; i++)
        {
            bool atEnd = i == buffer.Length;
            bool isDelimiter = false;
            int nextStart = i + 1;

            if (!atEnd)
            {
                char c = buffer[i];

                // Handle escapes inside quotes (\" and \\)
                if (inQuotes && c == '\\' && (i + 1) < buffer.Length && (buffer[i + 1] == '"' || buffer[i + 1] == '\\'))
                {
                    i++;
                    continue;
                }

                if (c == '"')
                {
                    inQuotes = !inQuotes;
                    continue;
                }

                // Handle // line comments outside quotes
                if (!inQuotes && c == '/' && (i + 1) < buffer.Length && buffer[i + 1] == '/')
                {
                    int commentEnd = i + 2;
                    while (commentEnd < buffer.Length && buffer[commentEnd] != '\n' && buffer[commentEnd] != '\r')
                    {
                        commentEnd++;
                    }

                    EmitCommandSegment(buffer.Slice(lineStart, i - lineStart), isPrivileged, allowedCommands, ref blockedCount, ref wasTruncated);
                    i = commentEnd;
                    lineStart = commentEnd + 1;
                    inQuotes = false;
                    continue;
                }

                if (!inQuotes && (c == ';' || c == '\n' || c == '\r'))
                {
                    isDelimiter = true;
                }
            }

            if (atEnd || isDelimiter)
            {
                ReadOnlySpan<char> rawSegment = buffer.Slice(lineStart, i - lineStart);
                EmitCommandSegment(rawSegment, isPrivileged, allowedCommands, ref blockedCount, ref wasTruncated);
                lineStart = nextStart;
                if (!atEnd && (buffer[i] == '\n' || buffer[i] == '\r'))
                {
                    inQuotes = false;
                }
            }
        }

        return allowedCommands;
    }

    /// <summary>
    ///     Determines whether a command token is permitted for the given privilege level (#137).
    /// </summary>
    public static bool ShouldAllowCommand(ReadOnlySpan<char> commandLine, bool isPrivileged)
    {
        ReadOnlySpan<char> trimmed = commandLine.Trim();
        if (trimmed.IsEmpty)
        {
            return false;
        }

        if (isPrivileged)
        {
            return true;
        }

        int tokenEnd = 0;
        while (tokenEnd < trimmed.Length && trimmed[tokenEnd] != ' ' && trimmed[tokenEnd] != '\t')
        {
            tokenEnd++;
        }

        ReadOnlySpan<char> firstToken = trimmed.Slice(0, tokenEnd);

        // Unprivileged callers cannot invoke internal '@' hooks, '__kongor_*' internal callbacks, or 'kongor_*' admin commands
        if (firstToken[0] == '@' ||
            firstToken.StartsWith("__kongor_".AsSpan(), StringComparison.OrdinalIgnoreCase) ||
            firstToken.StartsWith("kongor_".AsSpan(), StringComparison.OrdinalIgnoreCase) ||
            firstToken.Equals("quit".AsSpan(), StringComparison.OrdinalIgnoreCase) ||
            firstToken.Equals("crash".AsSpan(), StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return true;
    }

    private static void EmitCommandSegment(
        ReadOnlySpan<char> segment,
        bool isPrivileged,
        List<string> allowedCommands,
        ref int blockedCount,
        ref bool wasTruncated)
    {
        ReadOnlySpan<char> trimmed = segment.Trim();
        if (trimmed.IsEmpty)
        {
            return;
        }

        if (trimmed.Length > MaxCmdLineLength)
        {
            trimmed = trimmed.Slice(0, MaxCmdLineLength);
            wasTruncated = true;
        }

        if (!ShouldAllowCommand(trimmed, isPrivileged))
        {
            blockedCount++;
            return;
        }

        allowedCommands.Add(trimmed.ToString());
    }

    private static bool CharsEqualNormalized(char a, char b, bool caseInsensitive)
    {
        char na = a == '\\' ? '/' : a;
        char nb = b == '\\' ? '/' : b;
        if (caseInsensitive)
        {
            na = char.ToLowerInvariant(na);
            nb = char.ToLowerInvariant(nb);
        }

        return na == nb;
    }
}
