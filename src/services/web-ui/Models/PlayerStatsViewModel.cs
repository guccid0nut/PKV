// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using DAWNBRINGER.WebPortal.UI.Controllers;
using MERRICK.DatabaseContext.Entities.Game;
using NEXUS.Contracts.Clients;

namespace DAWNBRINGER.WebPortal.UI.Models;

public class PlayerStatsViewModel
{
    public string AccountName { get; set; } = "Player";
    public string AvatarUrl { get; set; } = "/images/default-avatar.png";
    public string? DiscordUsername { get; set; }
    public string? DiscordId { get; set; }
    public int TotalMatches { get; set; }
    public int MatchmakingMatches { get; set; }
    public int PublicMatches { get; set; }
    public int MidWarsMatches { get; set; }
    public int SkillRating { get; set; } = 1500;
    public double WinRate { get; set; }
    public double Kdr { get; set; }
    public List<HeroPerformanceDto> TopHeroes { get; set; } = [];
    public string PlayerStatus { get; set; } = "Active";
    public string PlayerStatusReason { get; set; } = string.Empty;
    public DateTimeOffset? PlayerStatusExpiration { get; set; }
    public List<PlayerReport> DisciplinaryReports { get; set; } = [];
    public bool IsRoleModel { get; set; } = true;
    public bool IsStaff { get; set; }
    public bool IsOwner { get; set; }
    public HostApplicationDto? HostApplication { get; set; }
    public bool HasHostAccount { get; set; }
    public bool HasLinkedHostAccounts { get; set; }
    public bool HasPendingHostApplication { get; set; }
    public string HostUsername { get; set; } = string.Empty;
    public int GamesHosted { get; set; }
    public int TotalPlayedAcrossSubaccounts { get; set; }
    public double HostingRatio { get; set; }
    public double CommunityContributionPercentage { get; set; }
    public string? HostBadgeTier { get; set; }
    public bool IsHost { get; set; }
    public bool IsStreamer { get; set; }
    public string? TwitchUsername { get; set; }
    public string? TwitchChannelUrl { get; set; }
    public string? EquippedBorder { get; set; }
    public string? EquippedTitle { get; set; }
}
