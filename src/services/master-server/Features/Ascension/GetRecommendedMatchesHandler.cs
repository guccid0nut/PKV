// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Net;
using System.Text;
using System.Text.Json;
using ASPIRE.Common.Enumerations.Statistics;
using ASPIRE.Common.Models.ServerManagement;
using MediatR;
using MERRICK.DatabaseContext.Entities.Core;
using Microsoft.Extensions.Logging;
using NEXUS.Contracts.CQRS.Ascension;
using NEXUS.Contracts.Data;
using KONGOR.MasterServer.Extensions.Cache;
using KONGOR.MasterServer.Serialization;
using StackExchange.Redis;

namespace KONGOR.MasterServer.Features.Ascension;

/// <summary>
///     MediatR query handler for retrieving active live matches sorted descending by average MMR (<see cref="GetRecommendedMatchesQuery"/>).
/// </summary>
public sealed class GetRecommendedMatchesHandler(
    IConnectionMultiplexer redis,
    IAccountRepository? accountRepository = null,
    ILogger<GetRecommendedMatchesHandler>? logger = null,
    TimeProvider? timeProvider = null)
    : IRequestHandler<GetRecommendedMatchesQuery, RecommendedMatchesResultDto>
{
    private readonly IDatabase _distributedCache = redis.GetDatabase();
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    public async Task<RecommendedMatchesResultDto> Handle(
        GetRecommendedMatchesQuery request,
        CancellationToken cancellationToken)
    {
        logger?.LogInformation(
            "[{Timestamp}] [GetRecommendedMatchesHandler] Fetching active live matches pool",
            _timeProvider.GetUtcNow());

        List<MatchStartData> activeMatches = [];
        EndPoint[] endPoints = redis.GetEndPoints();
        foreach (EndPoint endPoint in endPoints)
        {
            try
            {
                IServer server = redis.GetServer(endPoint);
                if (endPoints.Length > 1 && (!server.IsConnected || server.IsReplica)) continue;

                foreach (RedisKey key in server.Keys(pattern: "MATCH-START-DATA:*"))
                {
                    RedisValue cachedValue = await _distributedCache.StringGetAsync(key);
                    if (!cachedValue.IsNullOrEmpty)
                    {
                        byte[] bytes = (byte[]?)cachedValue ?? Encoding.UTF8.GetBytes(cachedValue.ToString());
                        MatchStartData? matchData = JsonSerializer.Deserialize(bytes, MasterServerJsonContext.Default.MatchStartData);
                        if (matchData is not null)
                        {
                            activeMatches.Add(matchData);
                        }
                    }
                }
                break;
            }
            catch (Exception ex)
            {
                logger?.LogWarning(ex, "[GetRecommendedMatchesHandler] Failed scanning MATCH-START-DATA keys from Redis on endpoint {EndPoint}", endPoint);
            }
        }

        List<(int AvgMmr, object MatchObj)> matchPool = [];

        foreach (MatchStartData match in activeMatches)
        {
            int ruleType = 1;
            string mapLower = match.Map?.ToLowerInvariant() ?? "caldavar";
            if (mapLower.Contains("midwars") || string.Equals(match.MatchMode, "mw", StringComparison.OrdinalIgnoreCase))
            {
                ruleType = 3;
            }
            else if (match.Tier > 1 || match.League > 0)
            {
                ruleType = match.IsCasual ? 6 : 5;
            }
            else if (match.IsCasual)
            {
                ruleType = 2;
            }
            else
            {
                ruleType = 1;
            }

            List<string> connectedPlayers = await _distributedCache.GetMatchConnectedPlayers(match.MatchID);
            List<(int AccountId, string Nickname, int Team, string HeroName, int Mmr)> playerItems = [];

            if (connectedPlayers.Count > 0 && accountRepository is not null)
            {
                IReadOnlyList<Account> accounts = await accountRepository.GetAccountsByNamesAsync(connectedPlayers, cancellationToken);
                Dictionary<string, Account> accMap = new(StringComparer.OrdinalIgnoreCase);
                foreach (Account a in accounts)
                {
                    accMap.TryAdd(a.Name, a);
                }

                for (int i = 0; i < connectedPlayers.Count; i++)
                {
                    string pName = connectedPlayers[i];
                    accMap.TryGetValue(pName, out Account? acc);
                    int accId = acc?.ID ?? (i + 1);
                    int mmr = (int)Math.Round(
                        acc?.Statistics.FirstOrDefault(s => s.Type == AccountStatisticsType.Matchmaking)?.SkillRating
                        ?? acc?.Statistics.FirstOrDefault(s => s.Type == AccountStatisticsType.Public)?.SkillRating
                        ?? 1500.0);
                    int team = (i < 5) ? 1 : 2;
                    playerItems.Add((accId, pName, team, "", mmr));
                }
            }
            else if (connectedPlayers.Count > 0)
            {
                for (int i = 0; i < connectedPlayers.Count; i++)
                {
                    int team = (i < 5) ? 1 : 2;
                    playerItems.Add((i + 1, connectedPlayers[i], team, "", 1500));
                }
            }

            if (playerItems.Count == 0)
            {
                string host = !string.IsNullOrWhiteSpace(match.HostAccountName) ? match.HostAccountName : "HostPlayer";
                playerItems.Add((1, host, 1, "", 1500));
            }

            int avgMmr = (int)Math.Round(playerItems.Average(p => p.Mmr));
            int maxMmr = playerItems.Max(p => p.Mmr);
            bool starAssigned = false;

            var accountList = playerItems.Select(p =>
            {
                bool isStar = false;
                if (!starAssigned && p.Mmr == maxMmr)
                {
                    isStar = true;
                    starAssigned = true;
                }

                return new
                {
                    account_id = p.AccountId,
                    nickname = p.Nickname,
                    team = p.Team,
                    hero_name = p.HeroName,
                    is_star = isStar,
                    mmr = p.Mmr
                };
            }).ToList();

            int elapsedSecs = (int)Math.Max(0, (_timeProvider.GetUtcNow() - match.TimestampStarted).TotalSeconds);
            string cleanMap = mapLower.Contains("midwars") ? "midwars" : "caldavar";

            matchPool.Add((avgMmr, new
            {
                match_id = match.MatchID,
                elapsed_secs = elapsedSecs,
                map = cleanMap,
                rule_type = ruleType,
                avg_mmr = avgMmr,
                casual = match.IsCasual ? 1 : 0,
                match_mode = 0,
                f_view_num = "0",
                account_list = accountList
            }));
        }

        var sortedPool = matchPool
            .OrderByDescending(m => m.AvgMmr)
            .Select(m => m.MatchObj)
            .ToList();

        var responseData = new
        {
            error_code = 100,
            success = true,
            recommended_pool = sortedPool
        };

        return new RecommendedMatchesResultDto(true, 100, sortedPool, responseData);
    }
}
