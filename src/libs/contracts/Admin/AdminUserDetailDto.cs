// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

namespace NEXUS.Contracts.Admin;

/// <summary>
///     Detailed administrative view of a user profile and all associated HoN game sub-accounts.
/// </summary>
public sealed class AdminUserDetailDto
{
    public int Id { get; set; }
    public string EmailAddress { get; set; } = string.Empty;
    public string? DiscordId { get; set; }
    public string? DiscordUsername { get; set; }
    public string? DiscordAvatar { get; set; }
    public string RoleName { get; set; } = string.Empty;
    public bool IsStaff { get; set; }
    public int GoldCoins { get; set; }
    public int SilverCoins { get; set; }
    public int PlinkoTickets { get; set; }
    public int TotalLevel { get; set; }
    public int TotalExperience { get; set; }
    public DateTimeOffset TimestampCreated { get; set; }
    public DateTimeOffset TimestampLastActive { get; set; }
    public List<AdminAccountItemDto> Accounts { get; set; } = [];

    public string DisplayName => !string.IsNullOrWhiteSpace(DiscordUsername)
        ? $"@{DiscordUsername}"
        : MainAccount != null
            ? MainAccount.Name
            : $"User #{Id}";

    public AdminAccountItemDto? MainAccount =>
        Accounts.Find(a => a.IsMain) ?? (Accounts.Count > 0 ? Accounts[0] : null);

    public List<AdminAccountItemDto> HostAccounts => Accounts.FindAll(a => a.Type == AccountType.ServerHost);

    public List<AdminAccountItemDto> SubAccounts =>
        Accounts.FindAll(a => !a.IsMain && a.Type != AccountType.ServerHost);

    public bool HasHostAccounts => HostAccounts.Count > 0;
    public bool HasSubAccounts => SubAccounts.Count > 0;
}
