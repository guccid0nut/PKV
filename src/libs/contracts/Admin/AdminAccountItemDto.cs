// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

namespace NEXUS.Contracts.Admin;

/// <summary>
///     Individual game account details returned within an administrative user overview.
/// </summary>
public sealed class AdminAccountItemDto
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public bool IsMain { get; set; }
    public bool IsStaff { get; set; }
    public AccountType Type { get; set; } = AccountType.Normal;
    public int AscensionLevel { get; set; }
    public DateTimeOffset TimestampCreated { get; set; }
    public DateTimeOffset TimestampLastActive { get; set; }

    public int? HostApplicationId { get; set; }
    public string? HostPassword { get; set; }
    public string? AuthToken { get; set; }
    public string? Icon { get; set; }

    public bool IsHost => Type == AccountType.ServerHost;
    public string CategoryTag => IsMain ? "MAIN" : IsHost ? "HOST" : "SUB";
    public string CategoryIcon => IsMain ? "👑" : IsHost ? "🖥️" : "🎮";
}
