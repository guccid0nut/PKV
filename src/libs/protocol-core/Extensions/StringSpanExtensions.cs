// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

namespace ASPIRE.Common.Extensions;

/// <summary>
///     High-performance ReadOnlySpan&lt;char&gt; string manipulation extensions to prevent intermediate string
///     allocations.
/// </summary>
public static class StringSpanExtensions
{
    /// <summary>
    ///     Extracts the prefix portion of a string up to the specified delimiter as a ReadOnlySpan&lt;char&gt; without array
    ///     allocations.
    /// </summary>
    public static ReadOnlySpan<char> GetPrefixSpan(this string? input, char delimiter)
    {
        if (string.IsNullOrEmpty(input)) return [];

        ReadOnlySpan<char> span = input.AsSpan();
        int index = span.IndexOf(delimiter);
        return index >= 0 ? span[..index] : span;
    }

    /// <summary>
    ///     Extracts the prefix portion of a string up to the specified delimiter as a new string without string.Split array
    ///     allocations.
    /// </summary>
    public static string GetPrefixString(this string? input, char delimiter)
    {
        if (string.IsNullOrEmpty(input)) return string.Empty;

        ReadOnlySpan<char> span = input.AsSpan();
        int index = span.IndexOf(delimiter);
        return index >= 0 ? new string(span[..index]) : input;
    }
}
