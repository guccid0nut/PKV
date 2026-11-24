// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ReSharper disable LogMessageIsConditional

using System.Diagnostics;
using ASPIRE.Common.Caching;
using ASPIRE.Common.Constants;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace KONGOR.MasterServer.Services;

public interface IPlayerStatisticsService
{
    Task<PlayerStatisticsAggregatedDTO> GetAggregatedStatisticsAsync(int accountId,
        string? filter = null,
        CancellationToken cancellationToken = default);

    Task<List<PlayerMasteryStatDTO>> GetPlayerMasteryStatsAsync(int accountId,
        CancellationToken cancellationToken = default);

    Task InvalidateAggregatedStatisticsAsync(int accountId, CancellationToken cancellationToken = default);

    Task InvalidateAggregatedStatisticsAsync(int accountId, string? accountName, CancellationToken cancellationToken = default);
}

public partial class PlayerStatisticsService(
    MerrickContext databaseContext,
    INexusHybridCache hybridCache,
    ILogger<PlayerStatisticsService> logger,
    IServiceScopeFactory? serviceScopeFactory = null) : IPlayerStatisticsService
{
    private static readonly TimeSpan L1CacheTtl = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan L2CacheTtl = TimeSpan.FromMinutes(5);

    public async Task<PlayerStatisticsAggregatedDTO> GetAggregatedStatisticsAsync(int accountId,
        string? filter = null,
        CancellationToken cancellationToken = default)
    {
        string cleanFilter = string.IsNullOrWhiteSpace(filter) ? "ranked" : filter.Trim().ToLowerInvariant();
        string cacheKey = $"stats:aggregated:{accountId}:{cleanFilter}";
        string accountTag = $"account:{accountId}";

        PlayerStatisticsAggregatedDTO? cached = await hybridCache.GetOrCreateAsync(
            cacheKey,
            async ct =>
            {
                if (serviceScopeFactory != null)
                {
                    await using AsyncServiceScope scope = serviceScopeFactory.CreateAsyncScope();
                    MerrickContext db = scope.ServiceProvider.GetRequiredService<MerrickContext>();
                    return await ComputeAggregatedStatisticsAsync(db, accountId, cleanFilter, ct);
                }
                return await ComputeAggregatedStatisticsAsync(databaseContext, accountId, cleanFilter, ct);
            },
            [accountTag],
            L1CacheTtl,
            L2CacheTtl,
            allowStaleOnError: true,
            cancellationToken).ConfigureAwait(false);

        return cached ?? new PlayerStatisticsAggregatedDTO();
    }

    public Task InvalidateAggregatedStatisticsAsync(int accountId, CancellationToken cancellationToken = default)
        => InvalidateAggregatedStatisticsAsync(accountId, null, cancellationToken);

    public async Task InvalidateAggregatedStatisticsAsync(int accountId, string? accountName, CancellationToken cancellationToken = default)
    {
        string aggregatedKey = $"stats:aggregated:{accountId}";
        string masteryKey = $"stats:mastery:{accountId}";
        string accountTag = $"account:{accountId}";

        try
        {
            await hybridCache.RemoveByTagAsync(accountTag, cancellationToken).ConfigureAwait(false);

            if (!string.IsNullOrWhiteSpace(accountName))
            {
                string nickTag = $"account:nick:{accountName.Trim().ToLowerInvariant()}";
                await hybridCache.RemoveByTagAsync(nickTag, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                try
                {
                    string? name = await databaseContext.Accounts
                        .Where(a => a.ID == accountId)
                        .Select(a => a.Name)
                        .FirstOrDefaultAsync(cancellationToken)
                        .ConfigureAwait(false);

                    if (!string.IsNullOrWhiteSpace(name))
                    {
                        string nickTag = $"account:nick:{name.Trim().ToLowerInvariant()}";
                        await hybridCache.RemoveByTagAsync(nickTag, cancellationToken).ConfigureAwait(false);
                    }
                }
                catch
                {
                    // Ignore DB lookup failure during fallback tag eviction
                }
            }

            await hybridCache.RemoveAsync(aggregatedKey, cancellationToken).ConfigureAwait(false);
            await hybridCache.RemoveAsync(masteryKey, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            if (logger.IsEnabled(LogLevel.Warning))
                logger.LogWarning(ex, "[Stats] HybridCache invalidation failed for Account ID: {AccountID} ({AccountName})", accountId, accountName ?? "unknown");
        }

        if (logger.IsEnabled(LogLevel.Information))
            logger.LogInformation("[Stats] Invalidated cached stats for Account ID: {AccountID} ({AccountName})", accountId, accountName ?? "unknown");
    }

    public async Task<List<PlayerMasteryStatDTO>> GetPlayerMasteryStatsAsync(int accountId,
        CancellationToken cancellationToken = default)
    {
        string cacheKey = $"stats:mastery:{accountId}";
        string accountTag = $"account:{accountId}";

        List<PlayerMasteryStatDTO>? cached = await hybridCache.GetOrCreateAsync(
            cacheKey,
            async ct =>
            {
                if (serviceScopeFactory != null)
                {
                    await using AsyncServiceScope scope = serviceScopeFactory.CreateAsyncScope();
                    MerrickContext db = scope.ServiceProvider.GetRequiredService<MerrickContext>();
                    return await ComputePlayerMasteryStatsAsync(db, accountId, ct);
                }
                return await ComputePlayerMasteryStatsAsync(databaseContext, accountId, ct);
            },
            [accountTag],
            L1CacheTtl,
            L2CacheTtl,
            allowStaleOnError: true,
            cancellationToken).ConfigureAwait(false);

        return cached ?? [];
    }

    private Task<PlayerStatisticsAggregatedDTO> ComputeAggregatedStatisticsAsync(int accountId,
        string filter,
        CancellationToken cancellationToken = default) =>
        ComputeAggregatedStatisticsAsync(databaseContext, accountId, filter, cancellationToken);

    private async Task<PlayerStatisticsAggregatedDTO> ComputeAggregatedStatisticsAsync(MerrickContext db,
        int accountId,
        string filter,
        CancellationToken cancellationToken = default)
    {
        Stopwatch totalSw = Stopwatch.StartNew();
        Stopwatch stageSw = Stopwatch.StartNew();

        string? accName = await db.Accounts.AsNoTracking()
            .Where(a => a.ID == accountId)
            .Select(a => a.Name)
            .FirstOrDefaultAsync(cancellationToken);
        long accountNameMs = stageSw.ElapsedMilliseconds;

        IQueryable<PlayerStatistics> query = db.PlayerStatistics
            .Where(ps => ps.AccountID == accountId || (accName != null && ps.AccountName == accName));

        MatchFilterClassification classification = MapCatalog.ClassifyMatchFilter(filter);
        switch (classification.Kind)
        {
            case MatchFilterKind.RankedFoC:
                query = query.Where(ps => ps.RankedMatch == 1 &&
                    (ps.Match == null || (
                        (ps.Match.Map.ToLower() == "caldavar" || ps.Match.Map.ToLower() == "caldavar_halloween") &&
                        !ps.Match.GameMode.ToLower().Contains("casual") &&
                        !ps.Match.GameMode.ToLower().Contains("quickmatch") &&
                        ps.Match.GameMode.ToLower() != "cm")));
                break;

            case MatchFilterKind.Quickmatch:
                query = query.Where(ps => ps.Match != null &&
                    (ps.Match.GameMode.ToLower().Contains("casual") || ps.Match.Map.ToLower().Contains("casual") ||
                     ps.Match.GameMode.ToLower().Contains("quickmatch") || ps.Match.GameMode.ToLower() == "cm") &&
                    !ps.Match.GameMode.ToLower().Contains("midwars") && !ps.Match.Map.ToLower().Contains("midwars"));
                break;

            case MatchFilterKind.RankedMidWars:
                query = query.Where(ps => ps.RankedMatch == 1 && ps.Match != null &&
                    (ps.Match.GameMode.ToLower().Contains("midwars") || ps.Match.Map.ToLower().Contains("midwars")));
                break;

            case MatchFilterKind.RankedRiftWars:
                query = query.Where(ps => ps.RankedMatch == 1 && ps.Match != null &&
                    (ps.Match.GameMode.ToLower().Contains("riftwars") || ps.Match.Map.ToLower().Contains("riftwars")));
                break;

            case MatchFilterKind.AllPublic:
                query = query.Where(ps => ps.PublicMatch == 1 || ps.RankedMatch == 0);
                break;

            case MatchFilterKind.PublicMap when !string.IsNullOrEmpty(classification.MapKey):
                string key = classification.MapKey;
                if (key == "caldavar")
                {
                    query = query.Where(ps => (ps.PublicMatch == 1 || ps.RankedMatch == 0) &&
                        (ps.Match == null || ((ps.Match.Map.ToLower().Contains("caldavar") || ps.Match.GameMode.ToLower().Contains("caldavar")) &&
                                              !ps.Match.GameMode.ToLower().Contains("midwars") && !ps.Match.Map.ToLower().Contains("midwars") &&
                                              !ps.Match.Map.ToLower().Contains("caldavar_wtf") && !ps.Match.Map.ToLower().Contains("caldavar_10v10"))));
                }
                else if (key == "midwars")
                {
                    query = query.Where(ps => (ps.PublicMatch == 1 || ps.RankedMatch == 0) && ps.Match != null &&
                        (ps.Match.Map.ToLower().Contains("midwars") || ps.Match.GameMode.ToLower().Contains("midwars")) &&
                        !ps.Match.Map.ToLower().Contains("10v10") && !ps.Match.Map.ToLower().Contains("mids_omg"));
                }
                else if (key == "riftwars")
                {
                    query = query.Where(ps => (ps.PublicMatch == 1 || ps.RankedMatch == 0) && ps.Match != null &&
                        (ps.Match.Map.ToLower().Contains("riftwars") || ps.Match.GameMode.ToLower().Contains("riftwars")));
                }
                else if (key == "devowars")
                {
                    query = query.Where(ps => (ps.PublicMatch == 1 || ps.RankedMatch == 0) && ps.Match != null &&
                        (ps.Match.Map.ToLower().Contains("devowars") || ps.Match.Map.ToLower().Contains("devo_wars") ||
                         ps.Match.GameMode.ToLower().Contains("devowars") || ps.Match.GameMode.ToLower().Contains("devo_wars")) &&
                        !ps.Match.Map.ToLower().Contains("truedevo") && !ps.Match.Map.ToLower().Contains("10v10"));
                }
                else if (key == "truedevo")
                {
                    query = query.Where(ps => (ps.PublicMatch == 1 || ps.RankedMatch == 0) && ps.Match != null &&
                        (ps.Match.Map.ToLower().Contains("truedevo") || ps.Match.GameMode.ToLower().Contains("truedevo")) &&
                        !ps.Match.Map.ToLower().Contains("10v10"));
                }
                else if (key == "prophets")
                {
                    query = query.Where(ps => (ps.PublicMatch == 1 || ps.RankedMatch == 0) && ps.Match != null &&
                        (ps.Match.Map.ToLower().Contains("prophet") || ps.Match.GameMode.ToLower().Contains("prophet")));
                }
                else if (key == "strikers")
                {
                    query = query.Where(ps => (ps.PublicMatch == 1 || ps.RankedMatch == 0) && ps.Match != null &&
                        (ps.Match.Map.ToLower().Contains("striker") || ps.Match.Map.ToLower().Contains("soccer") ||
                         ps.Match.GameMode.ToLower().Contains("striker") || ps.Match.GameMode.ToLower().Contains("soccer")));
                }
                else if (key == "grimmscrossing")
                {
                    query = query.Where(ps => (ps.PublicMatch == 1 || ps.RankedMatch == 0) && ps.Match != null &&
                        (ps.Match.Map.ToLower().Contains("grimm") || ps.Match.GameMode.ToLower().Contains("grimm")) &&
                        !ps.Match.Map.ToLower().Contains("grimmhunt"));
                }
                else if (key == "thegrimmhunt")
                {
                    query = query.Where(ps => (ps.PublicMatch == 1 || ps.RankedMatch == 0) && ps.Match != null &&
                        (ps.Match.Map.ToLower().Contains("grimmhunt") || ps.Match.GameMode.ToLower().Contains("grimmhunt")));
                }
                else if (key == "darkwoodvale")
                {
                    query = query.Where(ps => (ps.PublicMatch == 1 || ps.RankedMatch == 0) && ps.Match != null &&
                        (ps.Match.Map.ToLower().Contains("darkwood") || ps.Match.GameMode.ToLower().Contains("darkwood")));
                }
                else if (key == "ctf")
                {
                    query = query.Where(ps => (ps.PublicMatch == 1 || ps.RankedMatch == 0) && ps.Match != null &&
                        (ps.Match.Map.ToLower().Contains("ctf") || ps.Match.Map.ToLower().Contains("capturetheflag") ||
                         ps.Match.GameMode.ToLower().Contains("ctf") || ps.Match.GameMode.ToLower().Contains("capturetheflag")));
                }
                else if (key == "team_deathmatch")
                {
                    query = query.Where(ps => (ps.PublicMatch == 1 || ps.RankedMatch == 0) && ps.Match != null &&
                        (ps.Match.Map.ToLower().Contains("deathmatch") || ps.Match.GameMode.ToLower().Contains("deathmatch")));
                }
                else if (key == "caldavar_wtf")
                {
                    query = query.Where(ps => (ps.PublicMatch == 1 || ps.RankedMatch == 0) && ps.Match != null &&
                        (ps.Match.Map.ToLower().Contains("caldavar_wtf") || ps.Match.GameMode.ToLower().Contains("caldavar_wtf")));
                }
                else
                {
                    query = query.Where(ps => (ps.PublicMatch == 1 || ps.RankedMatch == 0) && ps.Match != null &&
                        (ps.Match.Map.ToLower().Contains(key) || ps.Match.GameMode.ToLower().Contains(key)));
                }
                break;

            case MatchFilterKind.All:
            default:
                break;
        }

        stageSw.Restart();
        PlayerStatisticsAggregatedDTO? aggregated = await query
            .GroupBy(ps => 1)
            .Select(g => new PlayerStatisticsAggregatedDTO
            {
                TotalMatches = g.Count(),
                Smackdowns = g.Sum(x => x.Smackdown),
                Annihilations = g.Sum(x => x.Annihilation),
                Assists = g.Sum(x => x.HeroAssists),
                Kills = g.Sum(x => x.HeroKills),
                Deaths = g.Sum(x => x.HeroDeaths),
                MVP = g.Sum(x => x.MVP),
                Disconnected = g.Sum(x => x.Disconnected),
                Humiliations = g.Sum(x => x.Humiliation),
                Nemesis = g.Sum(x => x.Nemesis),
                Retribution = g.Sum(x => x.Retribution),
                MostKills = g.Max(x => x.HeroKills),
                MostAssists = g.Max(x => x.HeroAssists),
                LeastDeaths = g.Min(x => x.HeroDeaths),
                MostCreepKills = g.Max(x => x.TeamCreepKills + x.NeutralCreepKills),
                MostHeroDamage = g.Max(x => x.HeroDamage),
                MostBuildingDamage = g.Max(x => x.BuildingDamage),
                MostWards = g.Max(x => x.WardsPlaced),
                MostQuadKills = g.Max(x => x.QuadKill),
                HighestKillStreak = g.Max(x =>
                    x.KillStreak15 > 0 ? 15 :
                    x.KillStreak10 > 0 ? 10 :
                    x.KillStreak09 > 0 ? 9 :
                    x.KillStreak08 > 0 ? 8 :
                    x.KillStreak07 > 0 ? 7 :
                    x.KillStreak06 > 0 ? 6 :
                    x.KillStreak05 > 0 ? 5 :
                    x.KillStreak04 > 0 ? 4 :
                    x.KillStreak03 > 0 ? 3 : 0
                ),
                RankedMatches = g.Count(x => x.RankedMatch == 1),
                RankedWins = g.Sum(x => x.RankedMatch == 1 ? x.Win : 0),
                RankedLosses = g.Sum(x => x.RankedMatch == 1 ? x.Loss : 0),
                RankedRatingChange = g.Sum(x => x.RankedMatch == 1 ? x.RankedSkillRatingChange : 0),
                RankedDiscos = g.Sum(x => x.RankedMatch == 1 ? x.Disconnected : 0),
                RankedKills = g.Sum(x => x.RankedMatch == 1 ? x.HeroKills : 0),
                RankedDeaths = g.Sum(x => x.RankedMatch == 1 ? x.HeroDeaths : 0),
                RankedAssists = g.Sum(x => x.RankedMatch == 1 ? x.HeroAssists : 0),
                RankedExp = g.Sum(x => x.RankedMatch == 1 ? (long)x.Experience : 0),
                RankedGold = g.Sum(x => x.RankedMatch == 1 ? (long)x.Gold : 0),
                RankedSeconds = g.Sum(x => x.RankedMatch == 1 ? (long)x.SecondsPlayed : 0),
                RankedDenies = g.Sum(x => x.RankedMatch == 1 ? x.Denies : 0),
                RankedHeroDamage = g.Sum(x => x.RankedMatch == 1 ? x.HeroDamage : 0),
                RankedHeroGold = g.Sum(x => x.RankedMatch == 1 ? x.GoldFromHeroKills : 0),
                RankedGoldLost = g.Sum(x => x.RankedMatch == 1 ? x.GoldLostToDeath : 0),
                RankedSecondsDead = g.Sum(x => x.RankedMatch == 1 ? x.SecondsDead : 0),
                RankedTeamCreepKills = g.Sum(x => x.RankedMatch == 1 ? x.TeamCreepKills : 0),
                RankedTeamCreepDmg = g.Sum(x => x.RankedMatch == 1 ? x.TeamCreepDamage : 0),
                RankedTeamCreepGold = g.Sum(x => x.RankedMatch == 1 ? x.TeamCreepGold : 0),
                RankedTeamCreepExp = g.Sum(x => x.RankedMatch == 1 ? x.TeamCreepExperience : 0),
                RankedNeutralCreepKills = g.Sum(x => x.RankedMatch == 1 ? x.NeutralCreepKills : 0),
                RankedNeutralCreepDmg = g.Sum(x => x.RankedMatch == 1 ? x.NeutralCreepDamage : 0),
                RankedNeutralCreepGold = g.Sum(x => x.RankedMatch == 1 ? x.NeutralCreepGold : 0),
                RankedNeutralCreepExp = g.Sum(x => x.RankedMatch == 1 ? x.NeutralCreepExperience : 0),
                RankedBuildingDmg = g.Sum(x => x.RankedMatch == 1 ? x.BuildingDamage : 0),
                RankedBuildingsRazed = g.Sum(x => x.RankedMatch == 1 ? x.BuildingsRazed : 0),
                RankedBuildingExp = g.Sum(x => x.RankedMatch == 1 ? x.ExperienceFromBuildings : 0),
                RankedBuildingGold = g.Sum(x => x.RankedMatch == 1 ? x.GoldFromBuildings : 0),
                RankedExpDenied = g.Sum(x => x.RankedMatch == 1 ? x.ExperienceDenied : 0),
                RankedGoldSpent = g.Sum(x => x.RankedMatch == 1 ? x.GoldSpent : 0),
                RankedActions = g.Sum(x => x.RankedMatch == 1 ? x.Actions : 0),
                RankedConsumables = g.Sum(x => x.RankedMatch == 1 ? x.ConsumablesPurchased : 0),
                RankedWards = g.Sum(x => x.RankedMatch == 1 ? x.WardsPlaced : 0),
                RankedFirstBloods = g.Sum(x => x.RankedMatch == 1 ? x.FirstBlood : 0),
                RankedDoubleKills = g.Sum(x => x.RankedMatch == 1 ? x.DoubleKill : 0),
                RankedTripleKills = g.Sum(x => x.RankedMatch == 1 ? x.TripleKill : 0),
                RankedQuadKills = g.Sum(x => x.RankedMatch == 1 ? x.QuadKill : 0),
                RankedAnnihilations = g.Sum(x => x.RankedMatch == 1 ? x.Annihilation : 0),
                RankedKS3 = g.Sum(x => x.RankedMatch == 1 ? x.KillStreak03 : 0),
                RankedKS4 = g.Sum(x => x.RankedMatch == 1 ? x.KillStreak04 : 0),
                RankedKS5 = g.Sum(x => x.RankedMatch == 1 ? x.KillStreak05 : 0),
                RankedKS6 = g.Sum(x => x.RankedMatch == 1 ? x.KillStreak06 : 0),
                RankedKS7 = g.Sum(x => x.RankedMatch == 1 ? x.KillStreak07 : 0),
                RankedKS8 = g.Sum(x => x.RankedMatch == 1 ? x.KillStreak08 : 0),
                RankedKS9 = g.Sum(x => x.RankedMatch == 1 ? x.KillStreak09 : 0),
                RankedKS10 = g.Sum(x => x.RankedMatch == 1 ? x.KillStreak10 : 0),
                RankedKS15 = g.Sum(x => x.RankedMatch == 1 ? x.KillStreak15 : 0),
                RankedSmackdowns = g.Sum(x => x.RankedMatch == 1 ? x.Smackdown : 0),
                RankedHumiliations = g.Sum(x => x.RankedMatch == 1 ? x.Humiliation : 0),
                RankedNemesis = g.Sum(x => x.RankedMatch == 1 ? x.Nemesis : 0),
                RankedRetribution = g.Sum(x => x.RankedMatch == 1 ? x.Retribution : 0),
                RankedTimeEarningExp = g.Sum(x => x.RankedMatch == 1 ? x.TimeEarningExperience : 0),
                RankedBuybacks = g.Sum(x => x.RankedMatch == 1 ? x.Buybacks : 0),
                CasualMatches = g.Count(x => x.PublicMatch == 1),
                CasualWins = g.Sum(x => x.PublicMatch == 1 ? x.Win : 0),
                CasualLosses = g.Sum(x => x.PublicMatch == 1 ? x.Loss : 0),
                CasualRatingChange = g.Sum(x => x.PublicMatch == 1 ? x.PublicSkillRatingChange : 0),
                CasualDiscos = g.Sum(x => x.PublicMatch == 1 ? x.Disconnected : 0),
                CasualKills = g.Sum(x => x.PublicMatch == 1 ? x.HeroKills : 0),
                CasualDeaths = g.Sum(x => x.PublicMatch == 1 ? x.HeroDeaths : 0),
                CasualAssists = g.Sum(x => x.PublicMatch == 1 ? x.HeroAssists : 0),
                CasualExp = g.Sum(x => x.PublicMatch == 1 ? (long)x.Experience : 0),
                CasualGold = g.Sum(x => x.PublicMatch == 1 ? (long)x.Gold : 0),
                CasualSeconds = g.Sum(x => x.PublicMatch == 1 ? (long)x.SecondsPlayed : 0),
                CasualDenies = g.Sum(x => x.PublicMatch == 1 ? x.Denies : 0),
                CasualHeroDamage = g.Sum(x => x.PublicMatch == 1 ? x.HeroDamage : 0),
                CasualHeroGold = g.Sum(x => x.PublicMatch == 1 ? x.GoldFromHeroKills : 0),
                CasualGoldLost = g.Sum(x => x.PublicMatch == 1 ? x.GoldLostToDeath : 0),
                CasualSecondsDead = g.Sum(x => x.PublicMatch == 1 ? x.SecondsDead : 0),
                CasualTeamCreepKills = g.Sum(x => x.PublicMatch == 1 ? x.TeamCreepKills : 0),
                CasualTeamCreepDmg = g.Sum(x => x.PublicMatch == 1 ? x.TeamCreepDamage : 0),
                CasualTeamCreepGold = g.Sum(x => x.PublicMatch == 1 ? x.TeamCreepGold : 0),
                CasualTeamCreepExp = g.Sum(x => x.PublicMatch == 1 ? x.TeamCreepExperience : 0),
                CasualNeutralCreepKills = g.Sum(x => x.PublicMatch == 1 ? x.NeutralCreepKills : 0),
                CasualNeutralCreepDmg = g.Sum(x => x.PublicMatch == 1 ? x.NeutralCreepDamage : 0),
                CasualNeutralCreepGold = g.Sum(x => x.PublicMatch == 1 ? x.NeutralCreepGold : 0),
                CasualNeutralCreepExp = g.Sum(x => x.PublicMatch == 1 ? x.NeutralCreepExperience : 0),
                CasualBuildingDmg = g.Sum(x => x.PublicMatch == 1 ? x.BuildingDamage : 0),
                CasualBuildingsRazed = g.Sum(x => x.PublicMatch == 1 ? x.BuildingsRazed : 0),
                CasualBuildingExp = g.Sum(x => x.PublicMatch == 1 ? x.ExperienceFromBuildings : 0),
                CasualBuildingGold = g.Sum(x => x.PublicMatch == 1 ? x.GoldFromBuildings : 0),
                CasualExpDenied = g.Sum(x => x.PublicMatch == 1 ? x.ExperienceDenied : 0),
                CasualGoldSpent = g.Sum(x => x.PublicMatch == 1 ? x.GoldSpent : 0),
                CasualActions = g.Sum(x => x.PublicMatch == 1 ? x.Actions : 0),
                CasualConsumables = g.Sum(x => x.PublicMatch == 1 ? x.ConsumablesPurchased : 0),
                CasualWards = g.Sum(x => x.PublicMatch == 1 ? x.WardsPlaced : 0),
                CasualFirstBloods = g.Sum(x => x.PublicMatch == 1 ? x.FirstBlood : 0),
                CasualDoubleKills = g.Sum(x => x.PublicMatch == 1 ? x.DoubleKill : 0),
                CasualTripleKills = g.Sum(x => x.PublicMatch == 1 ? x.TripleKill : 0),
                CasualQuadKills = g.Sum(x => x.PublicMatch == 1 ? x.QuadKill : 0),
                CasualAnnihilations = g.Sum(x => x.PublicMatch == 1 ? x.Annihilation : 0),
                CasualKS3 = g.Sum(x => x.PublicMatch == 1 ? x.KillStreak03 : 0),
                CasualKS4 = g.Sum(x => x.PublicMatch == 1 ? x.KillStreak04 : 0),
                CasualKS5 = g.Sum(x => x.PublicMatch == 1 ? x.KillStreak05 : 0),
                CasualKS6 = g.Sum(x => x.PublicMatch == 1 ? x.KillStreak06 : 0),
                CasualKS7 = g.Sum(x => x.PublicMatch == 1 ? x.KillStreak07 : 0),
                CasualKS8 = g.Sum(x => x.PublicMatch == 1 ? x.KillStreak08 : 0),
                CasualKS9 = g.Sum(x => x.PublicMatch == 1 ? x.KillStreak09 : 0),
                CasualKS10 = g.Sum(x => x.PublicMatch == 1 ? x.KillStreak10 : 0),
                CasualKS15 = g.Sum(x => x.PublicMatch == 1 ? x.KillStreak15 : 0),
                CasualSmackdowns = g.Sum(x => x.PublicMatch == 1 ? x.Smackdown : 0),
                CasualHumiliations = g.Sum(x => x.PublicMatch == 1 ? x.Humiliation : 0),
                CasualNemesis = g.Sum(x => x.PublicMatch == 1 ? x.Nemesis : 0),
                CasualRetribution = g.Sum(x => x.PublicMatch == 1 ? x.Retribution : 0),
                CasualTimeEarningExp = g.Sum(x => x.PublicMatch == 1 ? x.TimeEarningExperience : 0),
                CasualBuybacks = g.Sum(x => x.PublicMatch == 1 ? x.Buybacks : 0),
                PublicMatches = g.Count(x => x.RankedMatch == 0 && x.PublicMatch == 0),
                PublicWins = g.Sum(x => x.RankedMatch == 0 && x.PublicMatch == 0 ? x.Win : 0),
                PublicLosses = g.Sum(x => x.RankedMatch == 0 && x.PublicMatch == 0 ? x.Loss : 0),
                PublicRatingChange =
                    g.Sum(x => x.RankedMatch == 0 && x.PublicMatch == 0 ? x.PublicSkillRatingChange : 0),
                PublicDiscos = g.Sum(x => x.RankedMatch == 0 && x.PublicMatch == 0 ? x.Disconnected : 0),
                PublicKills = g.Sum(x => x.RankedMatch == 0 && x.PublicMatch == 0 ? x.HeroKills : 0),
                PublicDeaths = g.Sum(x => x.RankedMatch == 0 && x.PublicMatch == 0 ? x.HeroDeaths : 0),
                PublicAssists = g.Sum(x => x.RankedMatch == 0 && x.PublicMatch == 0 ? x.HeroAssists : 0),
                PublicExp = g.Sum(x => x.RankedMatch == 0 && x.PublicMatch == 0 ? (long)x.Experience : 0),
                PublicGold = g.Sum(x => x.RankedMatch == 0 && x.PublicMatch == 0 ? (long)x.Gold : 0),
                PublicSeconds = g.Sum(x => x.RankedMatch == 0 && x.PublicMatch == 0 ? (long)x.SecondsPlayed : 0),
                PublicDenies = g.Sum(x => x.RankedMatch == 0 && x.PublicMatch == 0 ? x.Denies : 0),
                PublicHeroDamage = g.Sum(x => x.RankedMatch == 0 && x.PublicMatch == 0 ? x.HeroDamage : 0),
                PublicHeroGold = g.Sum(x => x.RankedMatch == 0 && x.PublicMatch == 0 ? x.GoldFromHeroKills : 0),
                PublicGoldLost = g.Sum(x => x.RankedMatch == 0 && x.PublicMatch == 0 ? x.GoldLostToDeath : 0),
                PublicSecondsDead = g.Sum(x => x.RankedMatch == 0 && x.PublicMatch == 0 ? x.SecondsDead : 0),
                PublicTeamCreepKills = g.Sum(x => x.RankedMatch == 0 && x.PublicMatch == 0 ? x.TeamCreepKills : 0),
                PublicTeamCreepDmg = g.Sum(x => x.RankedMatch == 0 && x.PublicMatch == 0 ? x.TeamCreepDamage : 0),
                PublicTeamCreepGold = g.Sum(x => x.RankedMatch == 0 && x.PublicMatch == 0 ? x.TeamCreepGold : 0),
                PublicTeamCreepExp = g.Sum(x => x.RankedMatch == 0 && x.PublicMatch == 0 ? x.TeamCreepExperience : 0),
                PublicNeutralCreepKills =
                    g.Sum(x => x.RankedMatch == 0 && x.PublicMatch == 0 ? x.NeutralCreepKills : 0),
                PublicNeutralCreepDmg = g.Sum(x => x.RankedMatch == 0 && x.PublicMatch == 0 ? x.NeutralCreepDamage : 0),
                PublicNeutralCreepGold = g.Sum(x => x.RankedMatch == 0 && x.PublicMatch == 0 ? x.NeutralCreepGold : 0),
                PublicNeutralCreepExp =
                    g.Sum(x => x.RankedMatch == 0 && x.PublicMatch == 0 ? x.NeutralCreepExperience : 0),
                PublicBuildingDmg = g.Sum(x => x.RankedMatch == 0 && x.PublicMatch == 0 ? x.BuildingDamage : 0),
                PublicBuildingsRazed = g.Sum(x => x.RankedMatch == 0 && x.PublicMatch == 0 ? x.BuildingsRazed : 0),
                PublicBuildingExp =
                    g.Sum(x => x.RankedMatch == 0 && x.PublicMatch == 0 ? x.ExperienceFromBuildings : 0),
                PublicBuildingGold = g.Sum(x => x.RankedMatch == 0 && x.PublicMatch == 0 ? x.GoldFromBuildings : 0),
                PublicExpDenied = g.Sum(x => x.RankedMatch == 0 && x.PublicMatch == 0 ? x.ExperienceDenied : 0),
                PublicGoldSpent = g.Sum(x => x.RankedMatch == 0 && x.PublicMatch == 0 ? x.GoldSpent : 0),
                PublicActions = g.Sum(x => x.RankedMatch == 0 && x.PublicMatch == 0 ? x.Actions : 0),
                PublicConsumables = g.Sum(x => x.RankedMatch == 0 && x.PublicMatch == 0 ? x.ConsumablesPurchased : 0),
                PublicWards = g.Sum(x => x.RankedMatch == 0 && x.PublicMatch == 0 ? x.WardsPlaced : 0),
                PublicFirstBloods = g.Sum(x => x.RankedMatch == 0 && x.PublicMatch == 0 ? x.FirstBlood : 0),
                PublicDoubleKills = g.Sum(x => x.RankedMatch == 0 && x.PublicMatch == 0 ? x.DoubleKill : 0),
                PublicTripleKills = g.Sum(x => x.RankedMatch == 0 && x.PublicMatch == 0 ? x.TripleKill : 0),
                PublicQuadKills = g.Sum(x => x.RankedMatch == 0 && x.PublicMatch == 0 ? x.QuadKill : 0),
                PublicAnnihilations = g.Sum(x => x.RankedMatch == 0 && x.PublicMatch == 0 ? x.Annihilation : 0),
                PublicKS3 = g.Sum(x => x.RankedMatch == 0 && x.PublicMatch == 0 ? x.KillStreak03 : 0),
                PublicKS4 = g.Sum(x => x.RankedMatch == 0 && x.PublicMatch == 0 ? x.KillStreak04 : 0),
                PublicKS5 = g.Sum(x => x.RankedMatch == 0 && x.PublicMatch == 0 ? x.KillStreak05 : 0),
                PublicKS6 = g.Sum(x => x.RankedMatch == 0 && x.PublicMatch == 0 ? x.KillStreak06 : 0),
                PublicKS7 = g.Sum(x => x.RankedMatch == 0 && x.PublicMatch == 0 ? x.KillStreak07 : 0),
                PublicKS8 = g.Sum(x => x.RankedMatch == 0 && x.PublicMatch == 0 ? x.KillStreak08 : 0),
                PublicKS9 = g.Sum(x => x.RankedMatch == 0 && x.PublicMatch == 0 ? x.KillStreak09 : 0),
                PublicKS10 = g.Sum(x => x.RankedMatch == 0 && x.PublicMatch == 0 ? x.KillStreak10 : 0),
                PublicKS15 = g.Sum(x => x.RankedMatch == 0 && x.PublicMatch == 0 ? x.KillStreak15 : 0),
                PublicSmackdowns = g.Sum(x => x.RankedMatch == 0 && x.PublicMatch == 0 ? x.Smackdown : 0),
                PublicHumiliations = g.Sum(x => x.RankedMatch == 0 && x.PublicMatch == 0 ? x.Humiliation : 0),
                PublicNemesis = g.Sum(x => x.RankedMatch == 0 && x.PublicMatch == 0 ? x.Nemesis : 0),
                PublicRetribution = g.Sum(x => x.RankedMatch == 0 && x.PublicMatch == 0 ? x.Retribution : 0),
                PublicTimeEarningExp =
                    g.Sum(x => x.RankedMatch == 0 && x.PublicMatch == 0 ? x.TimeEarningExperience : 0),
                PublicBuybacks = g.Sum(x => x.RankedMatch == 0 && x.PublicMatch == 0 ? x.Buybacks : 0)
            })
            .FirstOrDefaultAsync(cancellationToken);

        long groupByMs = stageSw.ElapsedMilliseconds;
        long extendedStatsMs = 0;

        PlayerStatisticsAggregatedDTO result;
        if (aggregated != null)
        {
            result = aggregated;
            stageSw.Restart();
            try
            {
                result.LastMatchDate = await query
                    .Where(x => x.Match != null)
                    .Select(x => (DateTimeOffset?)x.Match!.TimestampRecorded)
                    .MaxAsync(cancellationToken);

                result.TopHeroes = await query
                    .Where(x => x.HeroProductID.HasValue)
                    .GroupBy(x => x.HeroProductID!.Value)
                    .Select(heroGroup => new FavHeroDTO
                    {
                        HeroId = heroGroup.Key,
                        SecondsPlayed = heroGroup.Sum(x => x.SecondsPlayed)
                    })
                    .OrderByDescending(x => x.SecondsPlayed)
                    .Take(10)
                    .ToListAsync(cancellationToken);
            }
            catch
            {
                // Non-fatal if extended match metadata lookup fails
            }
            extendedStatsMs = stageSw.ElapsedMilliseconds;
        }
        else
        {
            if (classification.Kind is MatchFilterKind.RankedFoC or MatchFilterKind.Quickmatch or MatchFilterKind.RankedMidWars or MatchFilterKind.All)
            {
                AccountStatisticsType targetType = classification.Kind switch
                {
                    MatchFilterKind.Quickmatch => AccountStatisticsType.MatchmakingCasual,
                    MatchFilterKind.RankedMidWars => AccountStatisticsType.MidWars,
                    _ => AccountStatisticsType.Matchmaking
                };

                AccountStatistics? accStats = await db.AccountStatistics
                    .AsNoTracking()
                    .Where(s => s.AccountID == accountId && s.Type == targetType)
                    .FirstOrDefaultAsync(cancellationToken);

                if (accStats != null)
                    result = new PlayerStatisticsAggregatedDTO
                    {
                        TotalMatches = accStats.MatchesPlayed,
                        RankedMatches = accStats.MatchesPlayed,
                        RankedWins = accStats.MatchesWon,
                        RankedLosses = accStats.MatchesLost,
                        Disconnected = accStats.MatchesDisconnected,
                        RankedDiscos = accStats.MatchesDisconnected,
                        RankedRatingChange = accStats.SkillRating - 1500.0,
                        Smackdowns = accStats.AwardMostSmackdowns ?? 0,
                        Annihilations = accStats.AwardMostAnnihilations ?? 0,
                        MostQuadKills = accStats.AwardMostQuadKills ?? 0,
                        MostKills = accStats.AwardMostKills ?? 0,
                        MostAssists = accStats.AwardMostAssists ?? 0,
                        LeastDeaths = accStats.AwardLeastDeaths ?? 0,
                        MostBuildingDamage = accStats.AwardMostBuildingDamage ?? 0,
                        MostHeroDamage = accStats.AwardMostHeroDamageDealt ?? 0,
                        MostCreepKills = accStats.AwardMostCreepKills ?? 0,
                        MostWards = accStats.AwardMostWardsKilled ?? 0
                    };
                else
                    result = new PlayerStatisticsAggregatedDTO();
            }
            else
            {
                result = new PlayerStatisticsAggregatedDTO();
            }
        }

        stageSw.Restart();
        List<int> recentMatchIds = await query
            .AsNoTracking()
            .OrderByDescending(ps => ps.MatchID)
            .Take(20)
            .Select(ps => ps.MatchID)
            .ToListAsync(cancellationToken);

        if (recentMatchIds.Count > 0)
        {
            Dictionary<int, DateTimeOffset> matchDates = await db.MatchStatistics
                .AsNoTracking()
                .Where(ms => recentMatchIds.Contains(ms.MatchID))
                .ToDictionaryAsync(ms => ms.MatchID, ms => ms.TimestampRecorded, cancellationToken);

            result.RecentMatchIDs = recentMatchIds;
            result.RecentMatchDates =
            [
                .. recentMatchIds
                    .Select(id =>
                        matchDates.TryGetValue(id, out DateTimeOffset dt) ? dt : TimeProvider.System.GetUtcNow())
            ];
        }
        long recentMatchesMs = stageSw.ElapsedMilliseconds;

        logger.LogInformation(
            "[PlayerStatistics:Timing] AccountId={AccountId} filter={Filter} accountNameMs={AccountNameMs} groupByMs={GroupByMs} extendedStatsMs={ExtendedStatsMs} recentMatchesMs={RecentMatchesMs} totalMs={TotalMs}",
            accountId, filter, accountNameMs, groupByMs, extendedStatsMs, recentMatchesMs, totalSw.ElapsedMilliseconds);

        return result;
    }

    private Task<List<PlayerMasteryStatDTO>> ComputePlayerMasteryStatsAsync(int accountId,
        CancellationToken cancellationToken = default) =>
        ComputePlayerMasteryStatsAsync(databaseContext, accountId, cancellationToken);

    private async Task<List<PlayerMasteryStatDTO>> ComputePlayerMasteryStatsAsync(MerrickContext db,
        int accountId,
        CancellationToken cancellationToken = default)
    {
        Account? acc = await db.Accounts.AsNoTracking()
            .FirstOrDefaultAsync(a => a.ID == accountId, cancellationToken);
        string? accName = acc?.Name;

        List<PlayerMasteryStatDTO> result = await db.PlayerStatistics
            .Where(ps => (ps.AccountID == accountId || (accName != null && ps.AccountName == accName)) &&
                         ps.HeroProductID.HasValue)
            .GroupBy(ps => ps.HeroProductID!.Value)
            .Select(g => new
            {
                HeroId = g.Key,
                SecondsPlayed = g.Sum(x => x.SecondsPlayed),
                Wins = g.Sum(x => x.Win),
                Losses = g.Sum(x => x.Loss)
            })
            .Select(x => new PlayerMasteryStatDTO
            {
                HeroId = x.HeroId,
                Experience = x.SecondsPlayed * 5.0,
                Level = (int)(x.SecondsPlayed * 5.0 / 2000.0) + 1,
                Wins = x.Wins,
                Losses = x.Losses
            })
            .ToListAsync(cancellationToken);

        return result;
    }

    [LoggerMessage(Level = LogLevel.Debug,
        Message = "[Stats] Calculating aggregated stats for Account ID: {AccountID}")]
    private partial void LogCalculatingStats(int accountId);
}
