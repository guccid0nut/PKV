// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using ASPIRE.Common.DTOs;

namespace DAWNBRINGER.WebPortal.UI.Models;

public class AccountSettingsViewModel
{
    public int UserId { get; set; }
    public string MainAccountName { get; set; } = string.Empty;
    public string EmailAddress { get; set; } = string.Empty;
    public string? DiscordUsername { get; set; }
    public string? DiscordId { get; set; }
    public string? DiscordAvatar { get; set; }
    public string? TwitchUsername { get; set; }
    public string? TwitchId { get; set; }
    public bool HasTwitchLinked => !string.IsNullOrWhiteSpace(TwitchUsername);
    public bool IsStreamer { get; set; }
    public string RoleName { get; set; } = "User";
    public string AccountTypeName { get; set; } = "Normal";
    public int GoldCoins { get; set; }
    public int SilverCoins { get; set; }
    public int PlinkoTickets { get; set; }
    public List<AccountItemDto> LinkedAccounts { get; set; } = [];
    public DateTimeOffset TimestampCreated { get; set; }
    public bool HasInGamePasswordSet { get; set; }
    public string? StatusMessage { get; set; }
    public bool IsSuccess { get; set; }

    public StreamerApplicationDto? StreamerApp { get; set; }
    public HostApplicationDto? HostApp { get; set; }
    public bool HasPendingStreamerApplication => StreamerApp is { Status: StreamerApplicationStatus.Pending };
    public bool HasPendingHostApplication => HostApp is { Status: HostApplicationStatus.Pending };
    public bool HasApprovedHostAccount => HostApp is { Status: HostApplicationStatus.Approved };
    public bool HasLinkedHostAccount => HasApprovedHostAccount || LinkedAccounts.Any(a => a.Type == "ServerHost");

    public string HostUsername => HostApp?.HostUsername ??
                                  LinkedAccounts.FirstOrDefault(a => a.Type == "ServerHost")?.Name ??
                                  $"{MainAccountName}_host";

    public int ApprovedHostCount { get; set; }

    public ApplyHostRequestDto ApplyHostModel { get; set; } = new();
    public ApplyStreamerRequestDto ApplyStreamerModel { get; set; } = new();

    public int TotalCompletedGames { get; set; }
    public int MaxAllowedActiveCodes { get; set; }
    public int ActiveUnusedCodes { get; set; }
    public int RemainingInviteQuota { get; set; }
    public bool CanGenerateInvite { get; set; }
    public List<UserInviteDto> MyInvites { get; set; } = [];
    public bool IsInviteOnlyModeActive { get; set; }
}

public class AccountItemDto
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public bool IsMain { get; set; }
    public string Type { get; set; } = string.Empty;
    public DateTimeOffset TimestampCreated { get; set; }
}

public class ChangePasswordViewModel
{
    public string? CurrentPassword { get; set; }
    public string NewPassword { get; set; } = string.Empty;
    public string ConfirmPassword { get; set; } = string.Empty;
}
