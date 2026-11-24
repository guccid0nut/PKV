// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2022-2026 [K]ONGOR and Contributors
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV) -- Upstream lineage: https://github.com/Project-KONGOR-Open-Source

namespace MERRICK.DatabaseContext.Constants;

public static class UserRoleClaims
{
    public static readonly List<Claim> Administrator =
        [new(Claims.UserRole, UserRoles.Administrator, ClaimValueTypes.String)];

    public static readonly List<Claim> User = [new(Claims.UserRole, UserRoles.User, ClaimValueTypes.String)];
}
