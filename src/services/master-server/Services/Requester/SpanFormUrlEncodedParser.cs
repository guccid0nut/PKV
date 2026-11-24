// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

namespace KONGOR.MasterServer.Services.Requester;

/// <summary>
///     High-performance zero-allocation parser for extracting keys and values directly from ReadOnlySpan&lt;char&gt; query
///     strings and form urlencoded payloads.
/// </summary>
public static class SpanFormUrlEncodedParser
{
    /// <summary>
    ///     Attempts to extract a parameter value by key from a URL-encoded span without heap allocations.
    /// </summary>
    /// <param name="source">The input URL-encoded string or query string as a character span.</param>
    /// <param name="key">The target parameter key to search for.</param>
    /// <param name="value">The extracted parameter value span if found.</param>
    /// <returns>True if the parameter was found; otherwise false.</returns>
    public static bool TryExtractParameter(ReadOnlySpan<char> source, ReadOnlySpan<char> key,
        out ReadOnlySpan<char> value)
    {
        if (source.IsEmpty)
        {
            value = default;
            return false;
        }

        if (source.StartsWith("?")) source = source[1..];

        while (!source.IsEmpty)
        {
            int nextAmp = source.IndexOf('&');
            ReadOnlySpan<char> pair = nextAmp >= 0 ? source[..nextAmp] : source;
            source = nextAmp >= 0 ? source[(nextAmp + 1)..] : default;

            int equalIdx = pair.IndexOf('=');
            if (equalIdx >= 0)
            {
                ReadOnlySpan<char> currentKey = pair[..equalIdx];
                if (currentKey.Equals(key, StringComparison.OrdinalIgnoreCase))
                {
                    value = pair[(equalIdx + 1)..];
                    return true;
                }
            }
        }

        value = default;
        return false;
    }
}
