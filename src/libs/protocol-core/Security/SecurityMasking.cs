// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

namespace ASPIRE.Common.Security;

/// <summary>
///     Forwarding helper for SecurityMasking in the ASPIRE.Common namespace.
/// </summary>
public static class SecurityMasking
{
    /// <summary>
    ///     Masks a sensitive token or session cookie to a safe prefix (default 8 chars) followed by '...'.
    /// </summary>
    public static string MaskToken(string? token, int prefixLength = 8)
    {
        return NEXUS.Contracts.Security.SecurityMasking.MaskToken(token, prefixLength);
    }
}
