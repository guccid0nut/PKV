// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

namespace NEXUS.Contracts.Security;

/// <summary>
///     Provides security token and cookie masking helpers for safe structured logging.
/// </summary>
public static class SecurityMasking
{
    /// <summary>
    ///     Masks a sensitive token or session cookie to a safe prefix (default 8 chars) followed by '...'.
    ///     Returns '&lt;none&gt;' for null, empty, or whitespace-only inputs.
    /// </summary>
    /// <param name="token">The sensitive token or cookie string.</param>
    /// <param name="prefixLength">The maximum number of prefix characters to expose (default 8).</param>
    /// <returns>A safe masked token representation suitable for logging.</returns>
    public static string MaskToken(string? token, int prefixLength = 8)
    {
        if (string.IsNullOrWhiteSpace(token)) return "<none>";

        int len = Math.Min(token.Length, Math.Max(0, prefixLength));
        return $"{token[..len]}...";
    }
}
