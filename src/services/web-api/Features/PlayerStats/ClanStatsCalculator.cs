// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using ASPIRE.Common.Enumerations.Statistics;
using MERRICK.DatabaseContext.Entities.Core;
using MERRICK.DatabaseContext.Entities.Statistics;
using NEXUS.Contracts.Domain;
using NEXUS.Contracts.DTOs;

namespace ZORGATH.WebPortal.API.Features.PlayerStats;

/// <summary>
///     Canonical clan and member statistics calculator shared across the Clans Directory
///     and Clan Detail handlers. Keeps MMR precedence aligned with Player Stats while
///     computing accurate career win rates from <see cref="AccountStatistics"/>.
/// </summary>
internal static class ClanStatsCalculator
{
    /// <summary>
    ///     Minimum number of ranked members required for a clan to qualify for competitive
    ///     Top 3 Podium placement and primary MMR / Win Rate standings.
    /// </summary>
    public const int MinRankedMembersForCompetitiveStanding = 3;

    internal readonly record struct ResolvedMemberStats(
        double? SkillRating,
        string RankName,
        int MatchesPlayed,
        int MatchesWon,
        double WinRate
    );

    public static bool IsActiveClanAccount(Account? account) =>
        account is not null &&
        account.Type is not (AccountType.Disabled or AccountType.ServerHost or AccountType.Guest);

    public static ResolvedMemberStats ResolveMemberStats(Account member)
    {
        List<AccountStatistics> stats = member.Statistics ?? [];

        AccountStatistics? rankedStats = stats.FirstOrDefault(s => s.Type == AccountStatisticsType.Matchmaking);
        AccountStatistics? publicStats = stats.FirstOrDefault(s =>
            s.Type is AccountStatisticsType.Public or AccountStatisticsType.Player);

        // Canonical MMR precedence matching HomeController.Stats.cs:
        // Matchmaking (> 0) -> Public/Player (> 0) -> null (Unranked)
        double? skillRating = rankedStats is { SkillRating: > 0 }
            ? Math.Round(rankedStats.SkillRating, 1)
            : publicStats is { SkillRating: > 0 }
                ? Math.Round(publicStats.SkillRating, 1)
                : null;

        int totalMatches = 0;
        int totalWins = 0;
        foreach (AccountStatistics row in stats)
        {
            if (row.Type is not (AccountStatisticsType.Matchmaking or AccountStatisticsType.Public or AccountStatisticsType.Player))
            {
                continue;
            }

            int rowMatches = Math.Max(row.MatchesPlayed, row.MatchesWon + row.MatchesLost);
            int rowWins = Math.Clamp(row.MatchesWon, 0, rowMatches);
            totalMatches += rowMatches;
            totalWins += rowWins;
        }

        double winRate = totalMatches > 0
            ? Math.Clamp(Math.Round((double)totalWins / totalMatches * 100.0, 1), 0.0, 100.0)
            : 0.0;

        string rankName = skillRating.HasValue
            ? FormatRankName(RankExtensions.GetRank(skillRating.Value))
            : "Unranked";

        return new ResolvedMemberStats(skillRating, rankName, totalMatches, totalWins, winRate);
    }

    public static (string Label, int SortOrder) ComputeActivityBucket(DateTimeOffset lastActive, DateTimeOffset nowUtc)
    {
        TimeSpan age = nowUtc - lastActive;
        if (age <= TimeSpan.FromHours(24))
        {
            return ("Active today", 0);
        }

        if (age <= TimeSpan.FromDays(7))
        {
            return ("Active this week", 1);
        }

        if (age <= TimeSpan.FromDays(30))
        {
            return ("Active this month", 2);
        }

        return ("Inactive 30d+", 3);
    }

    public static string ResolveAvatarUrl(Account member)
    {
        if (!string.IsNullOrEmpty(member.User?.DiscordId) && !string.IsNullOrEmpty(member.User?.DiscordAvatar))
        {
            return member.User.DiscordAvatar.StartsWith("a_", StringComparison.OrdinalIgnoreCase)
                ? $"https://cdn.discordapp.com/avatars/{member.User.DiscordId}/{member.User.DiscordAvatar}.gif"
                : $"https://cdn.discordapp.com/avatars/{member.User.DiscordId}/{member.User.DiscordAvatar}.png?size=256";
        }

        if (!string.IsNullOrEmpty(member.User?.DiscordId))
        {
            ulong discordId = ulong.TryParse(member.User.DiscordId, out ulong parsed) ? parsed : 0;
            return $"https://cdn.discordapp.com/embed/avatars/{(discordId >> 22) % 6}.png";
        }

        return $"/avatars/by-name/{Uri.EscapeDataString(member.Name)}";
    }

    public static ClanMemberDto ToClanMemberDto(Account member, DateTimeOffset nowUtc)
    {
        ResolvedMemberStats stats = ResolveMemberStats(member);
        (string activityLabel, int activityOrder) = ComputeActivityBucket(member.TimestampLastActive, nowUtc);

        return new ClanMemberDto(
            AccountId: member.ID,
            Name: member.Name,
            Tier: member.ClanTier,
            TimestampJoinedClan: member.TimestampJoinedClan,
            ActivityBucket: activityLabel,
            ActivitySortOrder: activityOrder,
            SkillRating: stats.SkillRating,
            RankName: stats.RankName,
            MatchesPlayed: stats.MatchesPlayed,
            MatchesWon: stats.MatchesWon,
            WinRate: stats.WinRate,
            AvatarUrl: ResolveAvatarUrl(member)
        );
    }

    public static ClanSummaryDto BuildClanSummary(
        Clan clan,
        IReadOnlyList<Account> activeMembers,
        DateTimeOffset nowUtc)
    {
        double mmrSum = 0.0;
        int rankedCount = 0;
        int combinedMatches = 0;
        int combinedWins = 0;
        int officerCount = 0;
        string? leaderName = null;
        DateTimeOffset mostRecentActivity = clan.TimestampCreated;
        bool hasMemberActivity = false;

        foreach (Account member in activeMembers)
        {
            if (member.ClanTier == ClanTier.Leader && leaderName is null)
            {
                leaderName = member.Name;
            }
            else if (member.ClanTier == ClanTier.Officer)
            {
                officerCount++;
            }

            if (!hasMemberActivity || member.TimestampLastActive > mostRecentActivity)
            {
                mostRecentActivity = member.TimestampLastActive;
                hasMemberActivity = true;
            }

            ResolvedMemberStats stats = ResolveMemberStats(member);
            if (stats.SkillRating.HasValue)
            {
                mmrSum += stats.SkillRating.Value;
                rankedCount++;
            }

            combinedMatches += stats.MatchesPlayed;
            combinedWins += stats.MatchesWon;
        }

        double? averageMmr = rankedCount > 0 ? Math.Round(mmrSum / rankedCount, 1) : null;
        bool isCompetitiveQualified = rankedCount >= MinRankedMembersForCompetitiveStanding;
        double combinedWinRate = combinedMatches > 0
            ? Math.Clamp(Math.Round((double)combinedWins / combinedMatches * 100.0, 1), 0.0, 100.0)
            : 0.0;
        (string activityBucket, _) = ComputeActivityBucket(mostRecentActivity, nowUtc);

        return new ClanSummaryDto(
            Id: clan.ID,
            Name: clan.Name,
            Tag: clan.Tag,
            TimestampCreated: clan.TimestampCreated,
            MemberCount: activeMembers.Count,
            OfficerCount: officerCount,
            LeaderName: leaderName,
            AverageMmr: averageMmr,
            RankedMemberCount: rankedCount,
            IsCompetitiveQualified: isCompetitiveQualified,
            CombinedWinRate: combinedWinRate,
            CombinedMemberMatches: combinedMatches,
            CombinedMemberWins: combinedWins,
            ActivityBucket: activityBucket
        );
    }

    public static string FormatRankName(Rank rank) => rank switch
    {
        Rank.IMMORTAL => "Immortal",
        Rank.LEGENDARY_1 => "Legendary I",
        Rank.LEGENDARY_2 => "Legendary II",
        Rank.DIAMOND_1 => "Diamond I",
        Rank.DIAMOND_2 => "Diamond II",
        Rank.DIAMOND_3 => "Diamond III",
        Rank.GOLD_1 => "Gold I",
        Rank.GOLD_2 => "Gold II",
        Rank.GOLD_3 => "Gold III",
        Rank.GOLD_4 => "Gold IV",
        Rank.SILVER_1 => "Silver I",
        Rank.SILVER_2 => "Silver II",
        Rank.SILVER_3 => "Silver III",
        Rank.SILVER_4 => "Silver IV",
        Rank.SILVER_5 => "Silver V",
        Rank.BRONZE_1 => "Bronze I",
        Rank.BRONZE_2 => "Bronze II",
        Rank.BRONZE_3 => "Bronze III",
        Rank.BRONZE_4 => "Bronze IV",
        Rank.BRONZE_5 => "Bronze V",
        _ => "Unranked"
    };
}
