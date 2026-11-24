// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using MediatR;
using MERRICK.DatabaseContext;
using MERRICK.DatabaseContext.Entities.Core;
using MERRICK.DatabaseContext.Entities.Statistics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NEXUS.Contracts.Clients;
using NEXUS.Contracts.CQRS.PlayerStats;

namespace ZORGATH.WebPortal.API.Features.PlayerStats;

/// <summary>
///     MediatR query handler for <see cref="GetLeaderboardQuery"/>.
///     Executes optimized multi-aggregation queries against player statistics and accounts.
/// </summary>
public sealed class GetLeaderboardHandler(
    MerrickContext db,
    ILogger<GetLeaderboardHandler> logger)
    : IRequestHandler<GetLeaderboardQuery, LeaderboardResultDto>
{
    public async Task<LeaderboardResultDto> Handle(
        GetLeaderboardQuery request,
        CancellationToken cancellationToken)
    {
        logger.LogInformation(
            "[GetLeaderboardHandler] Query: page={Page}, pageSize={PageSize}, search='{Search}', mode='{Mode}', map='{Map}', sort='{Sort}'",
            request.Page, request.PageSize, request.Search, request.Mode, request.Map, request.Sort);

        IQueryable<Account> query = db.Accounts
            .Include(a => a.User)
            .Include(a => a.Statistics)
            .Where(a => a.Type != AccountType.ServerHost && a.Type != AccountType.Disabled && a.Type != AccountType.Guest)
            .AsNoTracking();

        if (!string.IsNullOrWhiteSpace(request.Search))
        {
            string s = request.Search.Trim();
            query = query.Where(a => EF.Functions.ILike(a.Name, $"%{s}%"));
        }

        int totalCount = await query.CountAsync(cancellationToken);
        int totalPages = (int)Math.Ceiling(totalCount / (double)request.PageSize);

        List<Account> allMatching = await query.ToListAsync(cancellationToken);
        List<int> allMatchingIds = [.. allMatching.Select(a => a.ID)];

        IQueryable<PlayerStatistics> psQuery = db.PlayerStatistics
            .Where(ps => allMatchingIds.Contains(ps.AccountID));

        if (!string.IsNullOrWhiteSpace(request.Map) && !request.Map.Equals("all", StringComparison.OrdinalIgnoreCase))
        {
            string cleanMap = request.Map.Trim().ToLowerInvariant();
            if (cleanMap.EndsWith("_ranked")) cleanMap = cleanMap[..^7];
            else if (cleanMap.EndsWith("_public")) cleanMap = cleanMap[..^7];

            psQuery = psQuery.Where(ps => ps.Match != null && ps.Match.Map.ToLower().Contains(cleanMap));
        }

        var aggStatsRaw = await psQuery
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
            .ToListAsync(cancellationToken);

        Dictionary<int, PlayerStatsAggregateDto> aggMap = aggStatsRaw
            .Select(a => new PlayerStatsAggregateDto(
                a.MatchesPlayed,
                a.HeroKills,
                a.HeroDeaths,
                a.HeroAssists,
                a.TeamCreepKills,
                a.NeutralCreepKills,
                a.WardsPlaced,
                a.Experience,
                a.Gold,
                a.HeroDamage,
                a.BuildingDamage,
                0,
                a.AccountId
            ))
            .ToDictionary(a => a.AccountId, a => a);

        var topHeroes = await psQuery
            .GroupBy(ps => new { ps.AccountID, ps.HeroIdentifier })
            .Select(g => new { g.Key.AccountID, g.Key.HeroIdentifier, Count = g.Count() })
            .ToListAsync(cancellationToken);

        Dictionary<int, string?> topHeroMap = topHeroes
            .GroupBy(x => x.AccountID)
            .ToDictionary(
                g => g.Key,
                g => g.OrderByDescending(x => x.Count).Select(x => x.HeroIdentifier).FirstOrDefault()
            );

        var accountWinsLosses = await psQuery
            .GroupBy(ps => ps.AccountID)
            .Select(g => new
            {
                AccountId = g.Key,
                Wins = g.Sum(ps => ps.Win),
                Losses = g.Sum(ps => ps.Loss),
                Total = g.Count()
            })
            .ToListAsync(cancellationToken);

        Dictionary<int, (int Wins, int Losses, int Total)> winLossMap =
            accountWinsLosses.ToDictionary(x => x.AccountId, x => (x.Wins, x.Losses, x.Total));

        AccountStatisticsType targetStatType = (request.Mode ?? "").ToLowerInvariant() switch
        {
            "public" => AccountStatisticsType.Public,
            "casual" => AccountStatisticsType.MatchmakingCasual,
            "midwars" => AccountStatisticsType.MidWars,
            "riftwars" => AccountStatisticsType.RiftWars,
            _ => AccountStatisticsType.Matchmaking
        };

        var scoredAccounts = allMatching.Select(a =>
        {
            AccountStatistics? rStats = a.Statistics.FirstOrDefault(s => s.Type == targetStatType)
                                        ?? a.Statistics.FirstOrDefault(s => s.Type == AccountStatisticsType.Matchmaking)
                                        ?? a.Statistics.FirstOrDefault(s => s.Type == AccountStatisticsType.Public)
                                        ?? a.Statistics.FirstOrDefault();

            winLossMap.TryGetValue(a.ID, out (int Wins, int Losses, int Total) wl);
            int wins = rStats != null && (rStats.MatchesWon > 0 || rStats.MatchesLost > 0)
                ? rStats.MatchesWon
                : wl.Wins;
            int losses = rStats != null && (rStats.MatchesWon > 0 || rStats.MatchesLost > 0)
                ? rStats.MatchesLost
                : wl.Losses;
            int total = wins + losses > 0
                ? wins + losses
                : rStats?.MatchesPlayed > 0
                    ? rStats.MatchesPlayed
                    : wl.Total > 0
                        ? wl.Total
                        : aggMap.TryGetValue(a.ID, out PlayerStatsAggregateDto? aggItem)
                            ? aggItem.MatchesPlayed
                            : 0;
            double winRate = total > 0 ? Math.Round((double)wins / total * 100.0, 1) : 0.0;
            int rating = (int)Math.Round(rStats?.SkillRating ?? 1500.0 + wins * 5.0 - losses * 5.0, 0);

            aggMap.TryGetValue(a.ID, out PlayerStatsAggregateDto? ag);
            int kills = ag?.HeroKills ?? 0;
            int deaths = ag?.Deaths ?? 0;
            int assists = ag?.HeroAssists ?? 0;
            double kdr = deaths > 0 ? Math.Round((double)kills / deaths, 2) : kills;
            topHeroMap.TryGetValue(a.ID, out string? hero);

            return new
            {
                Account = a,
                Rating = rating,
                Wins = wins,
                Losses = losses,
                TotalMatches = total,
                WinRate = winRate,
                Kills = kills,
                Deaths = deaths,
                Assists = assists,
                Kdr = kdr,
                TopHero = hero
            };
        });

        string cleanSort = (request.Sort ?? "winrate").Trim().ToLowerInvariant();
        var sorted = cleanSort switch
        {
            "matches" or "games" => scoredAccounts
                .OrderByDescending(x => x.TotalMatches > 0 ? 1 : 0)
                .ThenByDescending(x => x.TotalMatches)
                .ThenByDescending(x => x.WinRate)
                .ThenByDescending(x => x.Rating),
            "mmr" or "rating" => scoredAccounts
                .OrderByDescending(x => x.TotalMatches > 0 ? 1 : 0)
                .ThenByDescending(x => x.Rating)
                .ThenByDescending(x => x.WinRate)
                .ThenByDescending(x => x.TotalMatches),
            _ => scoredAccounts
                .OrderByDescending(x => x.TotalMatches > 0 ? 1 : 0)
                .ThenByDescending(x => x.WinRate)
                .ThenByDescending(x => x.Rating)
                .ThenByDescending(x => x.TotalMatches)
        };

        var paged = sorted.Skip((Math.Max(1, request.Page) - 1) * request.PageSize).Take(request.PageSize).ToList();

        List<LeaderboardEntryDto> entries = [];
        int startRank = (Math.Max(1, request.Page) - 1) * request.PageSize + 1;
        for (int i = 0; i < paged.Count; i++)
        {
            var p = paged[i];
            entries.Add(new LeaderboardEntryDto(
                startRank + i,
                p.Account.ID,
                p.Account.Name,
                p.Rating,
                p.Wins,
                p.Losses,
                p.WinRate,
                p.Kills,
                p.Deaths,
                p.Assists,
                p.Kdr,
                p.TopHero
            ));
        }

        logger.LogInformation(
            "[GetLeaderboardHandler] Returning {Count} entries (total: {TotalCount})",
            entries.Count, totalCount);

        return new LeaderboardResultDto(entries, totalCount, Math.Max(1, request.Page), Math.Max(1, totalPages));
    }
}
