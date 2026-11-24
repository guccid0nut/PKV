// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System;
using System.Linq;
using System.Security.Claims;

namespace DAWNBRINGER.WebPortal.UI.Infrastructure;

/// <summary>
/// Authoritative helper for verifying staff access and policies across the web portal.
/// Used for securing sensitive administrative tools including developer telemetry and autonomous healing.
/// </summary>
public static class StaffAuthorizationHelper
{
    public const string PolicyName = "StaffOnly";

    /// <summary>
    /// Checks whether the specified user principal has authoritative staff privileges.
    /// </summary>
    public static bool IsStaff(ClaimsPrincipal? user)
    {
        if (user?.Identity?.IsAuthenticated != true)
        {
            return false;
        }

        // Direct claim set during Discord/Account authentication enrichment
        if (user.HasClaim("is_staff", "true"))
        {
            return true;
        }

        // Standard role checks
        if (user.IsInRole("Staff") ||
            user.IsInRole("STAFF") ||
            user.IsInRole("Admin") ||
            user.IsInRole("ADMIN") ||
            user.IsInRole("Administrator") ||
            user.IsInRole("ADMINISTRATOR") ||
            user.IsInRole("GameMaster") ||
            user.IsInRole("GAMEMASTER") ||
            user.IsInRole("MatchModerator") ||
            user.IsInRole("MATCHMODERATOR"))
        {
            return true;
        }

        // Check custom or raw claims collection (including AccountType enum/string claims)
        if (user.Claims.Any(c =>
                c.Type.Equals("AccountType", StringComparison.OrdinalIgnoreCase) &&
                (c.Value.Equals("Staff", StringComparison.OrdinalIgnoreCase) ||
                 c.Value.Equals("GameMaster", StringComparison.OrdinalIgnoreCase) ||
                 c.Value.Equals("MatchModerator", StringComparison.OrdinalIgnoreCase) ||
                 c.Value == "5" ||
                 c.Value == "6" ||
                 c.Value == "7")))
        {
            return true;
        }

        return user.Claims.Any(c => 
            (c.Type == ClaimTypes.Role || c.Type == "role") &&
            (c.Value.Equals("Staff", StringComparison.OrdinalIgnoreCase) ||
             c.Value.Equals("Admin", StringComparison.OrdinalIgnoreCase) ||
             c.Value.Equals("Administrator", StringComparison.OrdinalIgnoreCase) ||
             c.Value.Equals("GameMaster", StringComparison.OrdinalIgnoreCase) ||
             c.Value.Equals("MatchModerator", StringComparison.OrdinalIgnoreCase)));
    }
}
