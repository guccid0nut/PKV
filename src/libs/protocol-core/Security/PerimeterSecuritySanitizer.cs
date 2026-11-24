// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Text;
using System.Text.RegularExpressions;

namespace ASPIRE.Common.Security;

/// <summary>
///     Provides centralized perimeter sanitization and validation filters to shield the native
///     K2 game engine (C++) from malformed payloads, buffer overruns, and font rendering crashes.
/// </summary>
public static partial class PerimeterSecuritySanitizer
{
    public const int MaxGuideNameLength = 50;
    public const int MaxGuideIntroLength = 500;
    public const int MaxGuideContentLength = 4000;
    public const int MaxItemsPerCategory = 16;
    public const int MaxAbilityQueueLevels = 25;
    public const int MinNicknameLength = 3;
    public const int MaxNicknameLength = 15;
    public const int MaxChatMessageLength = 250;
    public const int MaxChannelNameLength = 32;

    [GeneratedRegex(@"\^([0-9]{3}|#[0-9a-fA-F]{6}|[a-zA-Z0-9\*])")]
    private static partial Regex CaretColorRegex();

    [GeneratedRegex(@"<[^>]*>")]
    private static partial Regex HtmlTagRegex();

    [GeneratedRegex(@"^[A-Za-z0-9_]{1,64}$")]
    private static partial Regex IdentifierRegex();

    [GeneratedRegex(@"^[a-zA-Z0-9_]{3,15}$")]
    private static partial Regex NicknameRegex();

    /// <summary>
    ///     Sanitizes free-form text in hero guides by stripping caret color codes, HTML tags,
    ///     control characters, and enforcing maximum string lengths.
    /// </summary>
    public static string SanitizeGuideText(string? raw, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(raw)) return string.Empty;

        string cleaned = CaretColorRegex().Replace(raw, string.Empty);
        cleaned = HtmlTagRegex().Replace(cleaned, string.Empty).Trim();
        cleaned = SanitizeControlCharacters(cleaned);

        return cleaned.Length > maxLength ? cleaned[..maxLength] : cleaned;
    }

    /// <summary>
    ///     Sanitizes and bounds a pipe- or comma-delimited item list for hero guides.
    ///     Enforces identifier syntax and bounds the list to a maximum item count (default 16).
    ///     Outputs canonical pipe-delimited string for the native K2 engine.
    /// </summary>
    public static string SanitizeItemList(string? rawList, int maxItems = MaxItemsPerCategory)
    {
        if (string.IsNullOrWhiteSpace(rawList)) return string.Empty;

        string[] tokens = rawList
            .Replace('[', ' ')
            .Replace(']', ' ')
            .Replace('"', ' ')
            .Split([',', '|'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        List<string> sanitized = new(Math.Min(tokens.Length, maxItems));

        foreach (string token in tokens)
        {
            if (sanitized.Count >= maxItems) break;

            if (IdentifierRegex().IsMatch(token))
            {
                sanitized.Add(token);
            }
        }

        return string.Join('|', sanitized);
    }

    /// <summary>
    ///     Sanitizes and bounds a pipe- or comma-delimited ability queue for hero guides.
    ///     Enforces identifier syntax and bounds the queue to standard hero levels (default 25).
    ///     Outputs canonical pipe-delimited string for the native K2 engine.
    /// </summary>
    public static string SanitizeAbilityQueue(string? rawQueue, int maxAbilities = MaxAbilityQueueLevels)
    {
        if (string.IsNullOrWhiteSpace(rawQueue)) return string.Empty;

        string[] tokens = rawQueue
            .Replace('[', ' ')
            .Replace(']', ' ')
            .Replace('"', ' ')
            .Split([',', '|'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        List<string> sanitized = new(Math.Min(tokens.Length, maxAbilities));

        foreach (string token in tokens)
        {
            if (sanitized.Count >= maxAbilities) break;

            if (IdentifierRegex().IsMatch(token))
            {
                sanitized.Add(token);
            }
        }

        return string.Join('|', sanitized);
    }

    /// <summary>
    ///     Validates whether an account nickname satisfies the strict perimeter character set:
    ///     3 to 15 characters, alphanumeric and underscore only.
    ///     Guards against reserved system prefixes (__kongor*).
    /// </summary>
    public static bool IsValidNickname(string? nickname)
    {
        if (string.IsNullOrWhiteSpace(nickname)) return false;
        if (nickname.StartsWith("__kongor", StringComparison.OrdinalIgnoreCase)) return false;
        return NicknameRegex().IsMatch(nickname);
    }

    /// <summary>
    ///     Strips non-printable ASCII control characters (0x00 to 0x1F, excluding \t, \r, \n).
    /// </summary>
    public static string SanitizeControlCharacters(string input)
    {
        if (string.IsNullOrEmpty(input)) return string.Empty;

        bool needsClean = false;
        for (int i = 0; i < input.Length; i++)
        {
            char c = input[i];
            if (c < 32 && c is not '\t' and not '\r' and not '\n')
            {
                needsClean = true;
                break;
            }
        }

        if (!needsClean) return input;

        StringBuilder sb = new(input.Length);
        foreach (char c in input)
        {
            if (c < 32 && c is not '\t' and not '\r' and not '\n')
                continue;
            sb.Append(c);
        }

        return sb.ToString();
    }

    /// <summary>
    ///     Comprehensive perimeter string sanitizer:
    ///     1. Strips non-printable ASCII control codes and null terminators.
    ///     2. Strips Unicode bidirectional overrides (\u202A-\u202E, \u2066-\u2069).
    ///     3. Normalizes or drops unpaired UTF-16 surrogates (\uD800-\uDFFF).
    ///     4. Truncates to the designated maximum length.
    /// </summary>
    public static string SanitizePerimeterString(string? input, int maxLength = int.MaxValue)
    {
        if (string.IsNullOrEmpty(input)) return string.Empty;

        bool needsClean = input.Length > maxLength;
        if (!needsClean)
        {
            for (int i = 0; i < input.Length; i++)
            {
                char c = input[i];
                if ((c < 32 && c is not '\t' and not '\r' and not '\n') ||
                    (c is >= '\u202A' and <= '\u202E' or >= '\u2066' and <= '\u2069') ||
                    char.IsSurrogate(c))
                {
                    needsClean = true;
                    break;
                }
            }
        }

        if (!needsClean) return input;

        StringBuilder sb = new(Math.Min(input.Length, Math.Min(maxLength, 1024)));

        for (int i = 0; i < input.Length && sb.Length < maxLength; i++)
        {
            char c = input[i];

            // 1. Strip null and non-printable ASCII control codes (except tab, CR, LF)
            if (c < 32 && c is not '\t' and not '\r' and not '\n')
                continue;

            // 2. Strip Unicode bidirectional text-direction overrides
            if (c is >= '\u202A' and <= '\u202E' or >= '\u2066' and <= '\u2069')
                continue;

            // 3. Check for valid surrogate pairs
            if (char.IsSurrogate(c))
            {
                if (char.IsHighSurrogate(c) && i + 1 < input.Length && char.IsLowSurrogate(input[i + 1]))
                {
                    if (sb.Length + 2 <= maxLength)
                    {
                        sb.Append(c);
                        sb.Append(input[++i]);
                    }
                    else
                    {
                        break;
                    }
                }
                // Discard lone/unpaired surrogates to protect native font renderer
                continue;
            }

            sb.Append(c);
        }

        return sb.ToString();
    }

    /// <summary>
    ///     Sanitizes and validates a chat channel name, preventing directory traversal,
    ///     path injection, and oversized tag buffers.
    /// </summary>
    public static string SanitizeChannelName(string? rawName)
    {
        if (string.IsNullOrWhiteSpace(rawName)) return string.Empty;

        string trimmed = rawName.Trim();
        if (trimmed.Length > MaxChannelNameLength) trimmed = trimmed[..MaxChannelNameLength];

        if (trimmed.Contains("..", StringComparison.Ordinal) ||
            trimmed.Contains('/') ||
            trimmed.Contains('\\') ||
            trimmed.Contains('<') ||
            trimmed.Contains('>') ||
            trimmed.Contains('"') ||
            trimmed.Contains('\''))
        {
            return string.Empty;
        }

        return SanitizeControlCharacters(trimmed);
    }
}
