// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

namespace NEXUS.Contracts.Admin;

/// <summary>
///     Request payload to update a user's global role.
/// </summary>
public sealed class SetUserRoleRequest
{
    public string RoleName { get; set; } = string.Empty;
}
