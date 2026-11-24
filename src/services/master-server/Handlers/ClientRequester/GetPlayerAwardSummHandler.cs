// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using KONGOR.MasterServer.Extensions;
using KONGOR.MasterServer.Logging;
using KONGOR.MasterServer.Services.Security;
using MERRICK.DatabaseContext.Extensions;
using NEXUS.Contracts.Data;
using NEXUS.Contracts.Security;

namespace KONGOR.MasterServer.Handlers.ClientRequester;

public class GetPlayerAwardSummHandler(
    IMatchHistoryRepository matchHistoryRepository,
    IAccountRepository accountRepository,
    IPlayerStatisticsService statisticsService,
    ISessionContextResolver sessionResolver,
    ILogger<GetPlayerAwardSummHandler> logger) : IClientRequestHandler
{
    public string FunctionName => "get_player_award_summ";

    public async Task<IActionResult> HandleRequestAsync(HttpContext context,
        CancellationToken cancellationToken = default)
    {
        string cookie = context.Request.GetCookie() ?? string.Empty;
        string nickname = context.Request.HasFormContentType
            ? context.Request.Form["nickname"].ToString()
            : (context.Request.Query["nickname"].ToString() ?? string.Empty);

        logger.LogProcessingRequest(FunctionName, SecurityMasking.MaskToken(cookie), true);

        // Account Resolution logic
        Account? account = null;
        if (!string.IsNullOrEmpty(nickname))
            account = await accountRepository.GetByNicknameAsync(nickname, cancellationToken);

        account ??= await sessionResolver.ResolveAccountAsync(context, cancellationToken);

        if (account == null)
        {
            if (logger.IsEnabled(LogLevel.Warning))
                logger.LogWarning("Account not found for nickname: {Nickname}", nickname);

            return new PhpResult(new Dictionary<object, object>());
        }

        PlayerStatisticsAggregatedDTO
            aggregatedStats = await statisticsService.GetAggregatedStatisticsAsync(account.ID, cancellationToken: cancellationToken);

        string cleanName = AccountExtensions.SeparateClanTagFromAccountName(account.Name).AccountName;

        // Fetch all player records for this account across matches
        IReadOnlyList<PlayerStatistics> playerMatchStats = await matchHistoryRepository
            .GetPlayerStatisticsForAwardSummaryAsync(account.ID, account.Name, cleanName, cancellationToken);

        List<int> matchIds = [.. playerMatchStats.Select(p => p.MatchID).Distinct()];

        // Fetch all participants in these matches to compute accurate per-match winner badges
        IReadOnlyList<PlayerStatistics> allMatchPlayers = matchIds.Count > 0
            ? await matchHistoryRepository.GetPlayerStatisticsByMatchIdsAsync(matchIds, cancellationToken)
            : [];

        ILookup<int, PlayerStatistics> playersByMatch = allMatchPlayers.ToLookup(p => p.MatchID);

        IReadOnlyList<MatchStatistics> matchStatsList = matchIds.Count > 0
            ? await matchHistoryRepository.GetMatchesByIdsAsync(matchIds, cancellationToken)
            : [];

        Dictionary<int, MatchStatistics> matchDict = matchStatsList.ToDictionary(m => m.MatchID);

        int mvpCount = 0;
        int mostKillsCount = 0;
        int mostAssistsCount = 0;
        int leastDeathsCount = 0;
        int mostBuildingDamageCount = 0;
        int mostHeroDamageCount = 0;
        int mostCreepsCount = 0;
        int mostWardsCount = 0;

        foreach (PlayerStatistics playerStat in playerMatchStats)
        {
            int matchId = playerStat.MatchID;
            List<PlayerStatistics> matchParticipants = [.. playersByMatch[matchId]];
            matchDict.TryGetValue(matchId, out MatchStatistics? matchRecord);

            // MVP check
            if (playerStat.MVP > 0 || (matchRecord != null && matchRecord.MVPAccountID == account.ID)) mvpCount++;

            if (matchParticipants.Count > 0)
            {
                // Most Kills in match
                int maxKills = matchRecord?.AwardMostKills ?? matchParticipants.Max(p => p.HeroKills);
                if (playerStat.HeroKills > 0 && playerStat.HeroKills >= maxKills) mostKillsCount++;

                // Most Assists in match
                int maxAssists = matchRecord?.AwardMostAssists ?? matchParticipants.Max(p => p.HeroAssists);
                if (playerStat.HeroAssists > 0 && playerStat.HeroAssists >= maxAssists) mostAssistsCount++;

                // Least Deaths in match
                int minDeaths = matchRecord?.AwardLeastDeaths ?? matchParticipants.Min(p => p.HeroDeaths);
                if (playerStat.HeroDeaths <= minDeaths) leastDeathsCount++;

                // Most Building Damage in match
                int maxBldg = matchRecord?.AwardMostBuildingDamage ?? matchParticipants.Max(p => p.BuildingDamage);
                if (playerStat.BuildingDamage > 0 && playerStat.BuildingDamage >= maxBldg) mostBuildingDamageCount++;

                // Most Hero Damage in match
                int maxHeroDmg = matchRecord?.AwardMostHeroDamageDealt ?? matchParticipants.Max(p => p.HeroDamage);
                if (playerStat.HeroDamage > 0 && playerStat.HeroDamage >= maxHeroDmg) mostHeroDamageCount++;

                // Highest Creep Score in match
                int maxCreeps = matchRecord?.AwardHighestCreepScore ??
                                matchParticipants.Max(p => p.TeamCreepKills + p.NeutralCreepKills);
                int playerCreeps = playerStat.TeamCreepKills + playerStat.NeutralCreepKills;
                if (playerCreeps > 0 && playerCreeps >= maxCreeps) mostCreepsCount++;

                // Most Wards placed/killed
                int maxWards = matchRecord?.AwardMostWardsKilled ?? matchParticipants.Max(p => p.WardsPlaced);
                if (playerStat.WardsPlaced > 0 && playerStat.WardsPlaced >= maxWards) mostWardsCount++;
            }
        }

        // Totals & Streaks
        int annihilationCount = playerMatchStats.Count > 0
            ? playerMatchStats.Sum(ps => ps.Annihilation)
            : aggregatedStats.Annihilations;

        int quadKillCount = playerMatchStats.Count > 0
            ? playerMatchStats.Sum(ps => ps.QuadKill)
            : aggregatedStats.MostQuadKills;

        int smackdownCount = playerMatchStats.Count > 0
            ? playerMatchStats.Sum(ps => ps.Smackdown)
            : aggregatedStats.Smackdowns;

        int killStreakCount = playerMatchStats.Count > 0
            ? playerMatchStats
                .Select(x => x.KillStreak15 > 0 ? 15 :
                    x.KillStreak10 > 0 ? 10 :
                    x.KillStreak09 > 0 ? 9 :
                    x.KillStreak08 > 0 ? 8 :
                    x.KillStreak07 > 0 ? 7 :
                    x.KillStreak06 > 0 ? 6 :
                    x.KillStreak05 > 0 ? 5 :
                    x.KillStreak04 > 0 ? 4 :
                    x.KillStreak03 > 0 ? 3 : 0)
                .DefaultIfEmpty(0)
                .Max()
            : aggregatedStats.HighestKillStreak;

        // Fallback to AccountStatistics if no matches exist
        if (playerMatchStats.Count == 0)
        {
            AccountStatistics? stats = await matchHistoryRepository.GetAccountStatisticsAsync(account.ID, cancellationToken)
                                       ?? account.Statistics.FirstOrDefault(s => s.Type == AccountStatisticsType.Matchmaking)
                                       ?? account.Statistics.FirstOrDefault();
            if (stats != null)
            {
                annihilationCount = stats.AwardMostAnnihilations ?? annihilationCount;
                quadKillCount = stats.AwardMostQuadKills ?? quadKillCount;
                smackdownCount = stats.AwardMostSmackdowns ?? smackdownCount;
                mostKillsCount = stats.AwardMostKills ?? mostKillsCount;
                mostAssistsCount = stats.AwardMostAssists ?? mostAssistsCount;
                leastDeathsCount = stats.AwardLeastDeaths ?? leastDeathsCount;
                mostBuildingDamageCount = stats.AwardMostBuildingDamage ?? mostBuildingDamageCount;
                mostHeroDamageCount = stats.AwardMostHeroDamageDealt ?? mostHeroDamageCount;
                mostCreepsCount = stats.AwardMostCreepKills ?? mostCreepsCount;
                mostWardsCount = stats.AwardMostWardsKilled ?? mostWardsCount;
            }
        }

        if (logger.IsEnabled(LogLevel.Information))
            logger.LogInformation(
                "[Awards] Calculated for {Nickname} (ID: {AccountID}): MVP={MVP}, Annihilations={Annihilations}, QuadKills={QuadKills}, BestStreak={Streak}, Smackdowns={Smackdowns}, MostKills={MostKills}, MostAssists={MostAssists}, LeastDeaths={LeastDeaths}, MostBldg={Bldg}, MostHeroDmg={HeroDmg}, MostCreeps={Creeps}",
                account.Name, account.ID, mvpCount, annihilationCount, quadKillCount, killStreakCount, smackdownCount,
                mostKillsCount, mostAssistsCount, leastDeathsCount, mostBuildingDamageCount, mostHeroDamageCount,
                mostCreepsCount);

        Dictionary<object, object> response = new()
        {
            // Canonical Form Keys (resultparam0 to resultparam12 from player_stats_v2.package)
            { "awd_mann", annihilationCount.ToString() },
            { "awd_mqk", quadKillCount.ToString() },
            { "awd_lgks", killStreakCount.ToString() },
            { "awd_msd", smackdownCount.ToString() },
            { "awd_mkill", mostKillsCount.ToString() },
            { "awd_masst", mostAssistsCount.ToString() },
            { "awd_ledth", leastDeathsCount.ToString() },
            { "awd_mbdmg", mostBuildingDamageCount.ToString() },
            { "awd_mwk", mostWardsCount.ToString() },
            { "awd_mhdd", mostHeroDamageCount.ToString() },
            { "awd_hcs", mostCreepsCount.ToString() },
            { "account_id", account.ID.ToString() },
            { "mvp", mvpCount.ToString() },

            // Positional integer and string index keys
            { 1, annihilationCount.ToString() },
            { 2, quadKillCount.ToString() },
            { 3, killStreakCount.ToString() },
            { 4, smackdownCount.ToString() },
            { 5, mostKillsCount.ToString() },
            { 6, mostAssistsCount.ToString() },
            { 7, leastDeathsCount.ToString() },
            { 8, mostBuildingDamageCount.ToString() },
            { 9, mostWardsCount.ToString() },
            { 10, mostHeroDamageCount.ToString() },
            { 11, mostCreepsCount.ToString() },
            { 12, account.ID.ToString() },
            { 13, mvpCount.ToString() },
            { "1", annihilationCount.ToString() },
            { "2", quadKillCount.ToString() },
            { "3", killStreakCount.ToString() },
            { "4", smackdownCount.ToString() },
            { "5", mostKillsCount.ToString() },
            { "6", mostAssistsCount.ToString() },
            { "7", leastDeathsCount.ToString() },
            { "8", mostBuildingDamageCount.ToString() },
            { "9", mostWardsCount.ToString() },
            { "10", mostHeroDamageCount.ToString() },
            { "11", mostCreepsCount.ToString() },
            { "12", account.ID.ToString() },
            { "13", mvpCount.ToString() },

            // Named and legacy fallback keys
            { "annihilation", annihilationCount.ToString() },
            { "quadkill", quadKillCount.ToString() },
            { "ks15", killStreakCount.ToString() },
            { "lgks", killStreakCount.ToString() },
            { "smackdown", smackdownCount.ToString() },
            { "mkill", mostKillsCount.ToString() },
            { "most_kills", mostKillsCount.ToString() },
            { "awd_mast", mostAssistsCount.ToString() },
            { "most_assists", mostAssistsCount.ToString() },
            { "awd_ldth", leastDeathsCount.ToString() },
            { "least_deaths", leastDeathsCount.ToString() },
            { "most_building_damage", mostBuildingDamageCount.ToString() },
            { "most_wards", mostWardsCount.ToString() },
            { "most_hero_damage", mostHeroDamageCount.ToString() },
            { "hcs", mostCreepsCount.ToString() },
            { "most_creeps", mostCreepsCount.ToString() },
            { "mvp_score", mvpCount.ToString() },
            { "vested_threshold", 5 },
            { 0, true }
        };

        return new PhpResult(response);
    }
}
