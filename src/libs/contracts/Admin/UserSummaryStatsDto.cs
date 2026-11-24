// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

namespace NEXUS.Contracts.Admin;

/// <summary>
///     Aggregated summary statistics of all registered users and game accounts for admin dashboards.
/// </summary>
public sealed class UserSummaryStatsDto
{
    public int TotalUsers { get; set; }
    public int TotalAccounts { get; set; }
    public int TotalStaff { get; set; }
    public int TotalHosts { get; set; }
    public bool IsDatabaseConnected { get; set; }
    public string? StatusMessage { get; set; }
}
