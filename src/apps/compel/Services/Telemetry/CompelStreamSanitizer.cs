// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2022-2026 [K]ONGOR and Contributors
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors

using System.Text;
using ASPIRE.Common.Utilities;

namespace COMPEL.Services.Telemetry;

/// <summary>
/// Sanitizes dedicated-server stdout/stderr and console log lines before parsing or dispatch.
/// Strips UTF-16LE null bytes, ANSI/VT CSI/OSC/incomplete escape sequences, and splits terminal-overwrite
/// carriage-return (<c>\r</c>) and cursor-reposition segments so concatenated console fragments never corrupt
/// parsed MatchIDs or session cookies.
/// </summary>
internal static class CompelStreamSanitizer
{
    /// <summary>
    /// Sanitizes a raw stdout/stderr/log chunk into clean, trimmed single-line segments.
    /// </summary>
    public static IReadOnlyList<string> SanitizeLines(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return [];

        string normalized = StripNonTextEscapesAndControlChars(raw);
        if (string.IsNullOrWhiteSpace(normalized))
            return [];

        string ansiStripped = SimdAnsiStripper.StripAnsi(normalized);
        if (string.IsNullOrWhiteSpace(ansiStripped))
            return [];

        string[] parts = ansiStripped.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0)
            return [];

        List<string> result = new(parts.Length);
        foreach (string part in parts)
        {
            string cleaned = TrimAndStripControlChars(part);
            if (!string.IsNullOrEmpty(cleaned))
            {
                result.Add(cleaned);
            }
        }

        return result;
    }

    /// <summary>
    /// Strips UTF-16LE null bytes, OSC sequences (<c>ESC ] ... BEL/ST</c>), converts cursor-reposition/line-erase
    /// CSI sequences (<c>G/H/f/J/K</c>) into newline segment breaks so terminal overwrites do not concatenate
    /// unrelated digits/tokens, and drops incomplete trailing <c>ESC</c> fragments.
    /// </summary>
    private static string StripNonTextEscapesAndControlChars(string raw)
    {
        // Fast path: no NUL, no ESC, no CRLF edge handling needed -> still normalize below only if ESC/NUL present.
        if (raw.IndexOf('\0') < 0 && raw.IndexOf('\x1B') < 0)
        {
            return raw.Replace("\r\n", "\n", StringComparison.Ordinal);
        }

        StringBuilder sb = new(raw.Length);
        int i = 0;
        while (i < raw.Length)
        {
            char c = raw[i];
            if (c == '\0')
            {
                i++;
                continue;
            }

            if (c == '\r' && i + 1 < raw.Length && raw[i + 1] == '\n')
            {
                sb.Append('\n');
                i += 2;
                continue;
            }

            if (c == '\x1B')
            {
                if (i + 1 >= raw.Length)
                {
                    // Trailing incomplete ESC
                    break;
                }

                char next = raw[i + 1];
                if (next == ']')
                {
                    // OSC sequence: ESC ] ... (BEL \x07 or ST ESC \ or end of line)
                    int j = i + 2;
                    while (j < raw.Length)
                    {
                        if (raw[j] == '\x07')
                        {
                            j++;
                            break;
                        }
                        if (raw[j] == '\x1B' && j + 1 < raw.Length && raw[j + 1] == '\\')
                        {
                            j += 2;
                            break;
                        }
                        if (raw[j] == '\r' || raw[j] == '\n')
                        {
                            break;
                        }
                        j++;
                    }
                    i = j;
                    continue;
                }

                if (next == '[')
                {
                    // Check CSI final byte to distinguish cursor-reposition/line-erase (G/H/f/J/K) from SGR/etc.
                    int j = i + 2;
                    while (j < raw.Length && raw[j] is >= '\x30' and <= '\x3F') j++;
                    while (j < raw.Length && raw[j] is >= '\x20' and <= '\x2F') j++;

                    if (j >= raw.Length || raw[j] == '\r' || raw[j] == '\n')
                    {
                        // Incomplete CSI before EOL/EOF — drop the CSI fragment cleanly
                        i = j;
                        continue;
                    }

                    char finalByte = raw[j];
                    if (finalByte is 'G' or 'H' or 'f' or 'J' or 'K')
                    {
                        // Terminal line overwrite / clear / cursor-reposition acts as a segment boundary
                        sb.Append('\n');
                        i = j + 1;
                        continue;
                    }

                    // Let SimdAnsiStripper handle complete SGR / other CSI sequences
                    sb.Append(raw, i, (j + 1) - i);
                    i = j + 1;
                    continue;
                }

                // Other complete 2-byte ESC sequences pass through to SimdAnsiStripper; stray controls get dropped later
                sb.Append(c);
                sb.Append(next);
                i += 2;
                continue;
            }

            sb.Append(c);
            i++;
        }

        return sb.ToString();
    }

    private static string TrimAndStripControlChars(string input)
    {
        if (string.IsNullOrEmpty(input))
            return string.Empty;

        bool hasControl = false;
        foreach (char c in input)
        {
            if (c < ' ' && c != '\t')
            {
                hasControl = true;
                break;
            }
        }

        if (!hasControl)
            return input.Trim();

        StringBuilder sb = new(input.Length);
        foreach (char c in input)
        {
            if (c >= ' ' || c == '\t')
            {
                sb.Append(c);
            }
        }

        return sb.ToString().Trim();
    }
}
