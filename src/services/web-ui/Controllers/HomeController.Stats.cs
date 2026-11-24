// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Security.Claims;
using ASPIRE.Common.Utilities;
using DAWNBRINGER.WebPortal.UI.Models;
using DAWNBRINGER.WebPortal.UI.Services;
using MERRICK.DatabaseContext.Entities.Game;
using Microsoft.AspNetCore.Mvc;

namespace DAWNBRINGER.WebPortal.UI.Controllers;

public class LeaderboardEntryDto
{
    public int Rank { get; set; }
    public int AccountId { get; set; }
    public required string AccountName { get; set; }
    public string? DiscordUsername { get; set; }
    public required string AvatarUrl { get; set; }
    public int TotalMatches { get; set; }
    public int Wins { get; set; }
    public int Losses { get; set; }
    public double WinRate { get; set; }
    public int Mmr { get; set; }
    public required string TopHeroName { get; set; }
    public required string TopHeroIcon { get; set; }
}

public partial class HomeController
{
    [HttpGet("Home/Stats")]
    [HttpGet("Home/Stats/{accountName}")]
    [HttpGet("stats")]
    [HttpGet("stats/{accountName}")]
    [HttpGet("analytics")]
    [HttpGet("player/{accountName}")]
    public async Task<IActionResult> Stats(
        string? accountName = null,
        [FromQuery(Name = "accountName")] string? queryAccountName = null,
        [FromQuery(Name = "target")] string? target = null,
        CancellationToken cancellationToken = default)
    {
        bool isStaff = User?.Identity?.IsAuthenticated == true && (User.IsInRole("Administrator") ||
                                                                   User.IsInRole("Staff") ||
                                                                   (User.Claims != null &&
                                                                    User.HasClaim("is_staff", "true")));
        ViewData["IsStaff"] = isStaff;

        string? candidate = !string.IsNullOrWhiteSpace(accountName)
            ? accountName
            : (!string.IsNullOrWhiteSpace(queryAccountName)
                ? queryAccountName
                : target);

        if (string.IsNullOrWhiteSpace(candidate) && HttpContext?.Request?.Query != null)
        {
            if (HttpContext.Request.Query.TryGetValue("accountName", out var qAcc) && !string.IsNullOrWhiteSpace(qAcc))
                candidate = qAcc.ToString();
            else if (HttpContext.Request.Query.TryGetValue("target", out var qTarget) && !string.IsNullOrWhiteSpace(qTarget))
                candidate = qTarget.ToString();
        }

        bool hasExplicitTarget = !string.IsNullOrWhiteSpace(candidate);
        string? effectiveAccountName = hasExplicitTarget ? candidate!.Trim() : null;

        if (string.IsNullOrWhiteSpace(effectiveAccountName) && User?.Identity?.IsAuthenticated == true)
        {
            effectiveAccountName = User.FindFirst("ign")?.Value
                                   ?? User.FindFirst("AccountName")?.Value
                                   ?? User.FindFirst(ClaimTypes.Name)?.Value
                                   ?? User.FindFirst("discord_username")?.Value
                                   ?? User.Identity?.Name
                                   ?? User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        }

        if (string.IsNullOrWhiteSpace(effectiveAccountName))
        {
            ViewData["AccountName"] = "Guest";
            ViewData["AvatarUrl"] = "/images/default-avatar.png";
            ViewData["TotalMatches"] = 0;
            ViewData["SkillRating"] = 1500;
            ViewData["WinRate"] = 0.0;
            ViewData["KDR"] = 0.0;
            ViewData["TopHeroes"] = new List<HeroPerformanceDto>();
            ViewData["PlayerStatus"] = "Active";
            ViewData["PlayerStatusReason"] = string.Empty;
            ViewData["PlayerStatusExpiration"] = null;
            ViewData["DisciplinaryReports"] = new List<PlayerReport>();
            ViewData["DisciplinaryHistory"] = new List<PlayerReport>();
            ViewData["IsRoleModel"] = true;

            PlayerStatsViewModel guestViewModel = new()
            {
                AccountName = "Guest",
                AvatarUrl = "/images/default-avatar.png",
                TotalMatches = 0,
                SkillRating = 1500,
                WinRate = 0.0,
                Kdr = 0.0,
                TopHeroes = [],
                PlayerStatus = "Active",
                PlayerStatusReason = string.Empty,
                PlayerStatusExpiration = null,
                DisciplinaryReports = [],
                IsRoleModel = true,
                IsStaff = isStaff
            };
            return View(guestViewModel);
        }

        PlayerProfileSummaryDto? summary = await _playerStatsClient.GetPlayerProfileAsync(effectiveAccountName, cancellationToken);
        if (!hasExplicitTarget && (summary == null || summary.Account == null) && User?.Identity?.IsAuthenticated == true)
        {
            string? discordId = User.FindFirst("discord_id")?.Value
                                ?? User.FindFirst(ClaimTypes.NameIdentifier)?.Value
                                ?? User.FindFirst("urn:discord:id")?.Value;
            if (!string.IsNullOrEmpty(discordId))
            {
                UserDto? linkedUser = await _userApiClient.GetUserByDiscordIdAsync(discordId, cancellationToken);
                string? fallbackAccountName = linkedUser?.Accounts?.FirstOrDefault(a => a.IsMain)?.Name
                                              ?? linkedUser?.Accounts?.FirstOrDefault()?.Name;
                if (!string.IsNullOrEmpty(fallbackAccountName) &&
                    !string.Equals(fallbackAccountName, effectiveAccountName, StringComparison.OrdinalIgnoreCase))
                {
                    PlayerProfileSummaryDto? linkedSummary =
                        await _playerStatsClient.GetPlayerProfileAsync(fallbackAccountName, cancellationToken);
                    if (linkedSummary?.Account != null)
                    {
                        summary = linkedSummary;
                        effectiveAccountName = fallbackAccountName;
                    }
                }
            }
        }

        if (summary == null || summary.Account == null)
        {
            _logger.LogWarning("[HomeController.Stats] Player profile not found for '{AccountName}'. Rendering placeholder.", effectiveAccountName);
            ViewData["AccountName"] = effectiveAccountName;
            ViewData["AvatarUrl"] = $"/avatars/by-name/{Uri.EscapeDataString(effectiveAccountName)}";
            ViewData["TotalMatches"] = 0;
            ViewData["SkillRating"] = 1500;
            ViewData["WinRate"] = 0.0;
            ViewData["KDR"] = 0.0;
            ViewData["TopHeroes"] = new List<HeroPerformanceDto>();
            ViewData["PlayerStatus"] = "Active";
            ViewData["PlayerStatusReason"] = string.Empty;
            ViewData["PlayerStatusExpiration"] = null;
            ViewData["DisciplinaryReports"] = new List<PlayerReport>();
            ViewData["DisciplinaryHistory"] = new List<PlayerReport>();
            ViewData["IsRoleModel"] = true;

            PlayerStatsViewModel notFoundViewModel = new()
            {
                AccountName = effectiveAccountName,
                AvatarUrl = $"/avatars/by-name/{Uri.EscapeDataString(effectiveAccountName)}",
                TotalMatches = 0,
                SkillRating = 1500,
                WinRate = 0.0,
                Kdr = 0.0,
                TopHeroes = [],
                PlayerStatus = "Active",
                PlayerStatusReason = string.Empty,
                PlayerStatusExpiration = null,
                DisciplinaryReports = [],
                IsRoleModel = true,
                IsStaff = isStaff
            };
            return View(notFoundViewModel);
        }

        AccountDto account = summary.Account;
        UserDto user = summary.User ?? new UserDto { EmailAddress = $"{account.Name}@projectkongor.local" };
        int statsMatches = Math.Max(
            (summary.RankedStats?.MatchesPlayed ?? 0) + (summary.PublicStats?.MatchesPlayed ?? 0),
            (summary.RankedStats?.MatchesWon ?? 0) + (summary.RankedStats?.MatchesLost ?? 0) +
            (summary.PublicStats?.MatchesWon ?? 0) + (summary.PublicStats?.MatchesLost ?? 0));
        int statsWins = (summary.RankedStats?.MatchesWon ?? 0) + (summary.PublicStats?.MatchesWon ?? 0);
        int aggMatches = summary.AggregatedStats?.MatchesPlayed ?? 0;

        int totalMatches;
        int wins;
        if (aggMatches > 0)
        {
            totalMatches = aggMatches;
            int aggWins = summary.TopHeroes.Count > 0
                ? summary.TopHeroes.Sum(h => h.Wins)
                : Math.Min(statsWins, totalMatches);
            wins = Math.Clamp(aggWins, 0, totalMatches);
        }
        else
        {
            totalMatches = statsMatches;
            wins = Math.Clamp(statsWins, 0, totalMatches);
        }

        double winRate = totalMatches > 0
            ? Math.Clamp(Math.Round((double)wins / totalMatches * 100.0, 1), 0.0, 100.0)
            : 0.0;

        double? skillRating = (summary.RankedStats != null && summary.RankedStats.SkillRating > 0)
            ? summary.RankedStats.SkillRating
            : (summary.PublicStats != null && summary.PublicStats.SkillRating > 0
                ? summary.PublicStats.SkillRating
                : null);
        int mmr = (int)Math.Round(skillRating ?? 1500.0, 0);

        _logger.LogInformation("[HomeController.Stats] Loaded profile for '{AccountName}' (#{AccountId}): Matches={Matches}, Wins={Wins}, WinRate={WinRate}%, MMR={MMR}",
            account.Name, account.ID, totalMatches, wins, winRate, mmr);

        List<HeroPerformanceDto> topHeroes =
        [
            .. summary.TopHeroes.Select(h => new HeroPerformanceDto
            {
                HeroCode = Heroes.ResolveHeroDisplayName(h.HeroIdentifier),
                HeroIdentifier = h.HeroIdentifier,
                MatchesPlayed = h.MatchesPlayed,
                WinRate = h.MatchesPlayed > 0 ? Math.Round((double)h.Wins / h.MatchesPlayed * 100, 1) : 0.0
            })
        ];

        int totalKills = summary.AggregatedStats?.HeroKills ?? 0;
        int totalDeaths = summary.AggregatedStats?.Deaths ?? 0;
        double kdr = totalDeaths > 0 ? Math.Round((double)totalKills / totalDeaths, 2) : totalKills;

        string avatarUrl = (!string.IsNullOrEmpty(user.DiscordId) && !string.IsNullOrEmpty(user.DiscordAvatar))
            ? (user.DiscordAvatar.StartsWith("a_", StringComparison.OrdinalIgnoreCase)
                ? $"https://cdn.discordapp.com/avatars/{user.DiscordId}/{user.DiscordAvatar}.gif"
                : $"https://cdn.discordapp.com/avatars/{user.DiscordId}/{user.DiscordAvatar}.png?size=256")
            : "/images/default-avatar.png";

        bool isUserBanned = account.IsBanned || account.Type == AccountType.Disabled || (user.Accounts != null &&
            user.Accounts.Any(a => a.IsBanned || a.Type == AccountType.Disabled));
        bool isUserSuspended = account.IsSuspended || (user.Accounts != null && user.Accounts.Any(a => a.IsSuspended));
        bool isUserMuted = account.IsMuted || (user.Accounts != null && user.Accounts.Any(a => a.IsMuted));
        string playerStatus =
            isUserBanned ? "Banned" : (isUserSuspended ? "Suspended" : (isUserMuted ? "Muted" : "Active"));

        bool hasLinkedHost = summary.ApprovedHostApp != null || account.Type == AccountType.ServerHost ||
                             (summary.HostTelemetry != null && summary.HostTelemetry.HasLinkedHostAccounts);
        HostTelemetryResultDto? hostTelemetry = summary.HostTelemetry ??
                                                (hasLinkedHost
                                                    ? await _userApiClient.GetHostTelemetryAsync(account.Name,
                                                        cancellationToken)
                                                    : null);

        ViewData["HasLinkedHostAccounts"] = hasLinkedHost;
        ViewData["HasHostAccount"] = hasLinkedHost;
        ViewData["HasPendingHostApplication"] = false;
        ViewData["IsHost"] = hasLinkedHost;
        ViewData["HostApplication"] = summary.ApprovedHostApp;
        ViewData["GamesHosted"] = hostTelemetry?.GamesHosted ?? 0;
        ViewData["TotalPlayedAcrossSubaccounts"] = hostTelemetry?.TotalPlayedAcrossLinkedAccounts ?? totalMatches;
        ViewData["HostingRatio"] = hostTelemetry?.HostingRatio ?? 0.0;
        ViewData["CommunityContributionPercentage"] = hostTelemetry?.CommunityContributionPercentage ?? 0.0;
        ViewData["HostUsername"] = hasLinkedHost
            ? (hostTelemetry?.HostUsername ?? summary.ApprovedHostApp?.HostUsername ?? $"{account.Name}_host")
            : null;
        ViewData["HostBadgeTier"] = hasLinkedHost ? (hostTelemetry?.HostBadgeTier ?? "Community Host") : null;

        bool isOwner = User?.Identity?.IsAuthenticated == true &&
                       (string.Equals(User.Identity.Name, account.Name, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(User.FindFirst("ign")?.Value, account.Name, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(User.FindFirst(ClaimTypes.Name)?.Value, account.Name, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(User.FindFirst("discord_username")?.Value, user.DiscordUsername, StringComparison.OrdinalIgnoreCase) ||
                        (user.DiscordId != null && (
                            string.Equals(User.FindFirst("discord_id")?.Value, user.DiscordId, StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(User.FindFirst(ClaimTypes.NameIdentifier)?.Value, user.DiscordId, StringComparison.OrdinalIgnoreCase))));
        ViewData["IsOwner"] = isOwner;

        string? equippedBorder = summary.EquippedRewards?.FirstOrDefault(r => string.Equals(r.RewardType, "Border", StringComparison.OrdinalIgnoreCase))?.RewardCode;
        string? equippedTitle = summary.EquippedRewards?.FirstOrDefault(r => string.Equals(r.RewardType, "Title", StringComparison.OrdinalIgnoreCase))?.RewardCode;
        ViewData["EquippedBorder"] = equippedBorder;
        ViewData["EquippedTitle"] = equippedTitle;

        bool isStreamer = summary.ApprovedStreamerApp != null || account.Type == AccountType.Streamer ||
                          (user.TwitchId != null && (account.Type == AccountType.Staff ||
                                                     account.Type == AccountType.GameMaster ||
                                                     account.Type == AccountType.MatchModerator ||
                                                     account.Type == AccountType.MatchCaster ||
                                                     account.Type == AccountType.VIP ||
                                                     account.Type == AccountType.Streamer));
        string? twitchUsername = summary.ApprovedStreamerApp?.TwitchUsername ?? user.TwitchUsername;
        string? twitchChannelUrl = summary.ApprovedStreamerApp?.TwitchChannelUrl ??
                                   (!string.IsNullOrEmpty(twitchUsername)
                                       ? $"https://twitch.tv/{twitchUsername}"
                                       : null);

        ViewData["IsStreamer"] = isStreamer;
        ViewData["StreamerChannelUrl"] = twitchChannelUrl;
        ViewData["TwitchUsername"] = twitchUsername;
        ViewData["TwitchChannelUrl"] = twitchChannelUrl;

        if (isStreamer && _twitchStreamService != null && !string.IsNullOrEmpty(twitchUsername))
        {
            string? twitchId = summary.ApprovedStreamerApp?.TwitchId ?? user.TwitchId;
            TwitchStreamInfo? streamInfo =
                await _twitchStreamService.GetStreamStatusAsync(twitchUsername, twitchId, cancellationToken);
            if (streamInfo != null)
            {
                ViewData["IsTwitchLive"] = streamInfo.IsLive;
                ViewData["TwitchStreamTitle"] = streamInfo.StreamTitle;
                ViewData["TwitchGameName"] = streamInfo.GameName;
                ViewData["TwitchViewerCount"] = streamInfo.ViewerCount;
                ViewData["TwitchThumbnailUrl"] = streamInfo.ThumbnailUrl;
            }
        }

        bool isRoleModel = (playerStatus != "Banned" && playerStatus != "Suspended") &&
                           (summary.RecentReports.Count == 0 || summary.RecentReports.All(r =>
                               r.Status.Equals("APPEALED", StringComparison.OrdinalIgnoreCase) ||
                               r.Status.Equals("DISMISSED", StringComparison.OrdinalIgnoreCase)));
        ViewData["IsRoleModel"] = isRoleModel;

        List<PlayerReport> reportEntities =
        [
            .. summary.RecentReports.Select(r => new PlayerReport
            {
                ID = r.ID,
                ReporterAccountID = r.ReporterAccountID,
                ReporterAccountName = r.ReporterAccountName ?? string.Empty,
                ReportedAccountID = r.ReportedAccountID,
                ReportedAccountName = r.ReportedAccountName ?? string.Empty,
                MatchID = r.MatchID,
                Reason = r.Reason ?? string.Empty,
                Description = r.Description,
                Status = r.Status ?? "PENDING",
                TimestampCreated = r.TimestampCreated,
                TimestampReviewed = r.TimestampReviewed,
                ReviewedByStaffName = r.ReviewedByStaffName,
                PunishmentType = r.PunishmentType,
                PunishmentDurationHours = r.PunishmentDurationHours,
                PunishmentExpiration = r.PunishmentExpiration,
                StaffNotes = r.StaffNotes
            })
        ];
        ViewData["DisciplinaryReports"] = reportEntities;
        ViewData["DisciplinaryHistory"] = reportEntities;

        string playerStatusReason = (isUserBanned
            ? (account.BanReason ?? user.Accounts?.FirstOrDefault(a => a.IsBanned)?.BanReason)
            : (isUserSuspended
                ? (account.SuspensionReason ?? user.Accounts?.FirstOrDefault(a => a.IsSuspended)?.SuspensionReason)
                : (isUserMuted
                    ? (account.MuteReason ?? user.Accounts?.FirstOrDefault(a => a.IsMuted)?.MuteReason)
                    : string.Empty))) ?? string.Empty;
        DateTimeOffset? playerStatusExpiration = isUserBanned
            ? account.BanExpiration
            : (isUserSuspended ? account.SuspensionExpiration : (isUserMuted ? account.MuteExpiration : null));

        ViewData["Account"] = account;
        ViewData["AccountName"] = account.Name;
        ViewData["AvatarUrl"] = avatarUrl;
        ViewData["DiscordUsername"] = user.DiscordUsername;
        ViewData["DiscordId"] = isStaff ? user.DiscordId : null;
        ViewData["TotalMatches"] = totalMatches;
        ViewData["SkillRating"] = mmr;
        ViewData["WinRate"] = winRate;
        ViewData["KDR"] = kdr;
        ViewData["TopHeroes"] = topHeroes;
        ViewData["PlayerStatus"] = playerStatus;
        ViewData["PlayerStatusReason"] = playerStatusReason;
        ViewData["PlayerStatusExpiration"] = playerStatusExpiration;

        string hostUsername = hasLinkedHost
            ? (hostTelemetry?.HostUsername ?? summary.ApprovedHostApp?.HostUsername ?? $"{account.Name}_host")
            : string.Empty;
        string? hostBadgeTier = hasLinkedHost ? (hostTelemetry?.HostBadgeTier ?? "Community Host") : null;
        int gamesHosted = hostTelemetry?.GamesHosted ?? 0;
        int totalPlayedAcrossSubaccounts = hostTelemetry?.TotalPlayedAcrossLinkedAccounts ?? totalMatches;
        double hostingRatio = hostTelemetry?.HostingRatio ?? 0.0;
        double communityContribution = hostTelemetry?.CommunityContributionPercentage ?? 0.0;

        PlayerStatsViewModel viewModel = new()
        {
            AccountName = account.Name,
            AvatarUrl = avatarUrl,
            DiscordUsername = user.DiscordUsername,
            DiscordId = isStaff ? user.DiscordId : null,
            TotalMatches = totalMatches,
            MatchmakingMatches = summary.RankedStats?.MatchesPlayed ?? 0,
            PublicMatches = summary.PublicStats?.MatchesPlayed ?? 0,
            MidWarsMatches = 0,
            SkillRating = mmr,
            WinRate = winRate,
            Kdr = kdr,
            TopHeroes = topHeroes,
            PlayerStatus = playerStatus,
            PlayerStatusReason = playerStatusReason,
            PlayerStatusExpiration = playerStatusExpiration,
            DisciplinaryReports = reportEntities,
            IsRoleModel = isRoleModel,
            IsStaff = isStaff,
            IsOwner = isOwner,
            HostApplication = summary.ApprovedHostApp,
            HasHostAccount = hasLinkedHost,
            HasLinkedHostAccounts = hasLinkedHost,
            HasPendingHostApplication = false,
            HostUsername = hostUsername,
            GamesHosted = gamesHosted,
            TotalPlayedAcrossSubaccounts = totalPlayedAcrossSubaccounts,
            HostingRatio = hostingRatio,
            CommunityContributionPercentage = communityContribution,
            HostBadgeTier = hostBadgeTier,
            IsHost = hasLinkedHost,
            IsStreamer = isStreamer,
            TwitchUsername = twitchUsername,
            TwitchChannelUrl = twitchChannelUrl,
            EquippedBorder = equippedBorder,
            EquippedTitle = equippedTitle
        };

        return View(viewModel);
    }

    [HttpGet("Home/Leaderboard")]
    [HttpGet("leaderboard")]
    public async Task<IActionResult> Leaderboard(
        string? search = null,
        string? filter = null,
        string? region = null,
        int minGames = 0,
        string sort = "winrate",
        string? mode = null,
        string? map = null,
        int minMatches = 0,
        CancellationToken cancellationToken = default)
    {
        string activeMode = !string.IsNullOrEmpty(mode) ? mode : (!string.IsNullOrEmpty(filter) ? filter : "all");
        string activeMap = !string.IsNullOrEmpty(map) ? map : (!string.IsNullOrEmpty(region) ? region : "all");
        int activeMinMatches = minMatches > 0 ? minMatches : minGames;

        ViewData["SearchQuery"] = search ?? "";
        ViewData["CurrentMode"] = activeMode;
        ViewData["CurrentMap"] = activeMap;
        ViewData["CurrentSort"] = sort;
        ViewData["MinMatches"] = activeMinMatches;

        if (activeMode == "hosts" || sort == "hosts" || sort == "hosted" || filter == "hosts")
        {
            IReadOnlyList<HostLeaderboardEntryDto> hostEntries =
                await _playerStatsClient.GetHostLeaderboardAsync(cancellationToken);
            int totalCommunityHostedMatches = await _playerStatsClient.GetTotalMatchesCountAsync(cancellationToken);
            ViewData["IsHostLeaderboard"] = true;
            ViewData["TotalCommunityHostedMatches"] = totalCommunityHostedMatches;
            ViewData["HostLeaderboardEntries"] = hostEntries.Select(h => new HostLeaderboardEntryDto
            {
                Rank = h.Rank,
                AccountId = h.AccountId,
                AccountName = h.AccountName,
                HostAccountName = h.HostAccountName,
                AvatarUrl = $"/avatars/by-name/{Uri.EscapeDataString(h.AccountName)}",
                GamesHosted = h.GamesHosted,
                GamesPlayed = h.GamesPlayed,
                HostingRatio = h.HostingRatio,
                CommunityContributionPercentage = h.CommunityContributionPercentage,
                HostBadgeTier = h.HostBadgeTier,
                Location = h.Location,
                Instances = h.Instances
            }).ToList();

            return View("Leaderboard", new List<LeaderboardEntryDto>());
        }

        _logger.LogInformation("[HomeController.Leaderboard] Loading leaderboard: mode='{Mode}', map='{Map}', search='{Search}', sort='{Sort}'",
            activeMode, activeMap, search, sort);

        LeaderboardResultDto result =
            await _playerStatsClient.GetLeaderboardAsync(1, 100, search, sort, activeMode, activeMap, cancellationToken);
        int totalMatchesTracked = await _playerStatsClient.GetTotalMatchesCountAsync(cancellationToken);

        List<LeaderboardEntryDto> entries =
        [
            .. result.Entries.Select(e => new LeaderboardEntryDto
            {
                Rank = e.Rank,
                AccountId = e.AccountId,
                AccountName = e.AccountName,
                AvatarUrl = $"/avatars/by-name/{Uri.EscapeDataString(e.AccountName)}",
                TotalMatches = e.Wins + e.Losses,
                Wins = e.Wins,
                Losses = e.Losses,
                WinRate = e.WinRate,
                Mmr = e.Rating,
                TopHeroName = !string.IsNullOrEmpty(e.FavoriteHero)
                    ? Heroes.ResolveHeroDisplayName(e.FavoriteHero)
                    : "Bubbles",
                TopHeroIcon = HeroAndMapAssetResolver.ResolveHeroIconUrl(e.FavoriteHero, "bubbles")
            })
        ];

        if (activeMinMatches > 0)
        {
            entries = [.. entries.Where(e => e.TotalMatches >= activeMinMatches)];
        }

        ViewData["IsHostLeaderboard"] = false;
        ViewData["HostLeaderboardEntries"] = new List<HostLeaderboardEntryDto>();
        ViewData["TotalCommunityHostedMatches"] = 0;
        ViewData["TotalMatchesTracked"] =
            totalMatchesTracked > 0 ? totalMatchesTracked : entries.Sum(e => e.TotalMatches);
        ViewData["TotalRankedPlayers"] = result.TotalCount;
        return View("Leaderboard", entries);
    }
}
