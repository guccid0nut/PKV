// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using MediatR;
using MERRICK.DatabaseContext;
using MERRICK.DatabaseContext.Entities.Core;
using MERRICK.DatabaseContext.Entities.Game;
using MERRICK.DatabaseContext.Entities.Statistics;
using MERRICK.DatabaseContext.Extensions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NEXUS.Contracts.Clients;
using NEXUS.Contracts.CQRS.PlayerStats;
using NEXUS.Contracts.Data;

namespace ZORGATH.WebPortal.API.Features.PlayerStats;

/// <summary>
///     MediatR query handler for <see cref="GetPlayerProfileQuery"/>.
///     Executes read-optimized, un-tracked database queries to assemble the complete player profile.
/// </summary>
public sealed class GetPlayerProfileHandler(
    MerrickContext db,
    ILogger<GetPlayerProfileHandler> logger)
    : IRequestHandler<GetPlayerProfileQuery, PlayerProfileSummaryDto?>
{
    public async Task<PlayerProfileSummaryDto?> Handle(
        GetPlayerProfileQuery request,
        CancellationToken cancellationToken)
    {
        string cleanIgn = request.InGameName.Trim();
        logger.LogInformation("[GetPlayerProfileHandler] Assembling profile for '{IGN}'", cleanIgn);

        Account? account = await db.Accounts
            .Include(a => a.User)
            .Include(a => a.Clan)
            .Include(a => a.Statistics)
            .Include(a => a.Mastery).ThenInclude(m => m!.HeroExperiences)
            .AsNoTracking()
            .FirstOrDefaultAsync(
                a => a.Name == cleanIgn || a.Name.ToLower() == cleanIgn.ToLower(),
                cancellationToken);

        if (account is null)
        {
            logger.LogWarning("[GetPlayerProfileHandler] Player '{IGN}' not found in database", cleanIgn);
            return null;
        }

        User user = account.User ?? new User
        {
            EmailAddress = $"{cleanIgn}@projectkongor.local",
            SRPPasswordSalt = string.Empty,
            SRPPasswordHash = string.Empty,
            GoldCoins = 0,
            SilverCoins = 0,
            PlinkoTickets = 0
        };

        AccountStatistics? rankedStats =
            account.Statistics.FirstOrDefault(s => s.Type == AccountStatisticsType.Matchmaking);
        AccountStatistics? publicStats =
            account.Statistics.FirstOrDefault(s => s.Type == AccountStatisticsType.Public || s.Type == AccountStatisticsType.Player);

        var aggRaw = await db.PlayerStatistics
            .Where(ps => ps.AccountID == account.ID)
            .GroupBy(ps => ps.AccountID)
            .Select(g => new
            {
                MatchesPlayed = g.Count(),
                HeroKills = g.Sum(ps => (int)ps.HeroKills),
                HeroDeaths = g.Sum(ps => (int)ps.HeroDeaths),
                HeroAssists = g.Sum(ps => (int)ps.HeroAssists),
                TeamCreepKills = g.Sum(ps => (int)ps.TeamCreepKills),
                NeutralCreepKills = g.Sum(ps => (int)ps.NeutralCreepKills),
                WardsPlaced = g.Sum(ps => (int)ps.WardsPlaced),
                Experience = g.Sum(ps => (int)ps.Experience),
                Gold = g.Sum(ps => (int)ps.Gold),
                HeroDamage = g.Sum(ps => (int)ps.HeroDamage),
                BuildingDamage = g.Sum(ps => (int)ps.BuildingDamage),
                AccountId = g.Key
            })
            .FirstOrDefaultAsync(cancellationToken);

        PlayerStatsAggregateDto? agg = aggRaw != null
            ? new PlayerStatsAggregateDto(
                aggRaw.MatchesPlayed,
                aggRaw.HeroKills,
                aggRaw.HeroDeaths,
                aggRaw.HeroAssists,
                aggRaw.TeamCreepKills,
                aggRaw.NeutralCreepKills,
                aggRaw.WardsPlaced,
                aggRaw.Experience,
                aggRaw.Gold,
                aggRaw.HeroDamage,
                aggRaw.BuildingDamage,
                0,
                aggRaw.AccountId
            )
            : null;

        var topHeroesRaw = await db.PlayerStatistics
            .Where(ps => ps.AccountID == account.ID)
            .GroupBy(ps => ps.HeroIdentifier)
            .Select(g => new
            {
                HeroIdentifier = g.Key,
                MatchesPlayed = g.Count(),
                HeroKills = g.Sum(ps => (int)ps.HeroKills),
                HeroDeaths = g.Sum(ps => (int)ps.HeroDeaths),
                HeroAssists = g.Sum(ps => (int)ps.HeroAssists),
                Wins = g.Sum(ps => (int)ps.Win)
            })
            .OrderByDescending(h => h.MatchesPlayed)
            .Take(5)
            .ToListAsync(cancellationToken);

        List<PlayerHeroCountDto> topHeroes = [.. topHeroesRaw
            .Select(h => new PlayerHeroCountDto(
                h.HeroIdentifier,
                h.MatchesPlayed,
                h.HeroKills,
                h.HeroDeaths,
                h.HeroAssists,
                h.Wins
            ))];

        List<int> recentMatchIds = await db.PlayerStatistics
            .Where(ps => ps.AccountID == account.ID)
            .GroupBy(ps => ps.MatchID)
            .OrderByDescending(g => g.Max(ps => ps.ID))
            .Select(g => g.Key)
            .Take(10)
            .ToListAsync(cancellationToken);

        List<MatchStatistics> recentMatches = recentMatchIds.Count > 0
            ? await db.MatchStatistics
                .AsNoTracking()
                .Where(m => recentMatchIds.Contains(m.MatchID))
                .OrderByDescending(m => m.TimestampRecorded)
                .ToListAsync(cancellationToken)
            : [];

        List<PlayerReport> recentReports = await db.PlayerReports
            .AsNoTracking()
            .Where(r => r.ReportedAccountID == account.ID || r.ReportedAccountName == account.Name)
            .OrderByDescending(r => r.TimestampCreated)
            .Take(5)
            .ToListAsync(cancellationToken);

        StreamerApplication? streamerApp = await db.StreamerApplications
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.UserID == user.ID && s.Status == StreamerApplicationStatus.Approved, cancellationToken);

        HostApplication? hostApp = await db.HostApplications
            .AsNoTracking()
            .FirstOrDefaultAsync(
                h => (h.UserID == user.ID || h.MainAccountID == account.ID || h.HostAccountID == account.ID) &&
                     h.Status == HostApplicationStatus.Approved, cancellationToken);

        List<UserWebReward> equippedRewards = await db.UserWebRewards
            .AsNoTracking()
            .Where(r => r.UserID == user.ID && r.IsEquipped)
            .ToListAsync(cancellationToken);

        HostTelemetryResultDto hostTelemetry = new(
            hostApp != null,
            false,
            hostApp != null ? [hostApp.HostUsername] : [],
            [account.ID],
            [account.Name],
            0,
            agg?.MatchesPlayed ?? 0,
            0.0,
            0.0,
            null,
            hostApp?.ToDto(),
            hostApp?.HostUsername ?? $"{account.Name}_host"
        );

        PlayerProfileSummaryDto summary = new(
            account.ToDto(),
            user.ToDto(),
            rankedStats?.ToDto(),
            publicStats?.ToDto(),
            agg,
            topHeroes,
            [.. recentMatches.Select(m => m.ToDto())],
            [.. recentReports.Select(r => r.ToDto())],
            streamerApp?.ToDto(),
            hostApp?.ToDto(),
            hostTelemetry,
            [.. equippedRewards.Select(r => r.ToDto())]
        );

        return summary;
    }
}
