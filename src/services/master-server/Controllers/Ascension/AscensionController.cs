// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Net;
using System.Text;
using System.Text.Json;
using ASPIRE.Common.Enumerations.Statistics;
using ASPIRE.Common.Models.ServerManagement;
using KONGOR.MasterServer.Controllers.News;
using KONGOR.MasterServer.Extensions.Cache;
using KONGOR.MasterServer.Serialization;
using MediatR;
using MERRICK.DatabaseContext.Entities.Core;
using NEXUS.Contracts.CQRS.Ascension;
using NEXUS.Contracts.Data;
using NEXUS.Contracts.Events;
using StackExchange.Redis;

namespace KONGOR.MasterServer.Controllers.Ascension;

[ApiController]
[Route(TextConstant.EmptyString + "index.php")]
public class AscensionController(
    IConnectionMultiplexer redis,
    IAccountRepository? accountRepository = null,
    ILogger<AscensionController>? logger = null,
    IMediator? mediator = null) : ControllerBase
{
    private readonly IDatabase _distributedCache = redis.GetDatabase();
    private readonly IMediator? _mediator = mediator;

    /// <summary>
    ///     Routes ascension API requests based on the "r" query parameter.
    ///     This endpoint handles all client.sea.heroesofnewerth.com requests.
    /// </summary>
    [HttpGet("/", Name = "Ascension Root")]
    [HttpGet("", Name = "Ascension Index")]
    [HttpHead("/", Name = "Ascension Root Head")]
    [HttpHead("", Name = "Ascension Index Head")]
    [HttpPost("/", Name = "Ascension Root Post")]
    [HttpPost("", Name = "Ascension Index Post")]
    public async Task<IActionResult> RouteAscensionRequest([FromQuery(Name = "r")] string? route,
        CancellationToken cancellationToken = default)
    {
        string? effectiveRoute = route;
        if (string.IsNullOrEmpty(effectiveRoute) && Request.HasFormContentType &&
            Request.Form.TryGetValue("r", out var formRoute))
        {
            effectiveRoute = formRoute.ToString();
        }

        if (string.IsNullOrEmpty(effectiveRoute)) return Ok("NEXUS Master Server Active");

        string normalizedRoute = effectiveRoute.TrimStart('/');

        if (string.Equals(normalizedRoute, "news/index", StringComparison.OrdinalIgnoreCase))
            return new ContentResult
            {
                Content = NewsController.GenerateNewsHtml(),
                ContentType = "text/html; charset=utf-8",
                StatusCode = 200
            };

        if (string.Equals(normalizedRoute, "api/match/checkmatch", StringComparison.OrdinalIgnoreCase))
            return await CheckMatch(cancellationToken);

        if (string.Equals(normalizedRoute, "api/match/getrecommendedmatches", StringComparison.OrdinalIgnoreCase))
            return await GetRecommendedMatches(cancellationToken);

        if (string.Equals(normalizedRoute, "api/MasterServer/SpectateMatch", StringComparison.OrdinalIgnoreCase))
            return await SpectateMatch(cancellationToken);

        throw new NotImplementedException(
            $"Unsupported Ascension Controller Query String Parameter: r={effectiveRoute}");
    }

    /// <summary>
    ///     Checks if a match is a season match before it starts.
    ///     The server uses this information to determine whether statistics should be recorded or not for the match.
    /// </summary>
    private async Task<IActionResult> CheckMatch(CancellationToken cancellationToken = default)
    {
        if (!int.TryParse(Request.Query["match_id"], out int matchID))
            return BadRequest(new { error_code = 400, message = "Missing or invalid required parameter 'match_id'" });

        if (_mediator is not null)
        {
            CheckMatchResultDto result = await _mediator.Send(new CheckMatchQuery(matchID), cancellationToken);
            return Ok(result.ResponseData);
        }

        return await CheckMatchAsync(matchID, cancellationToken);
    }

    private async Task<IActionResult> CheckMatchAsync(int matchID, CancellationToken cancellationToken = default)
    {
        MatchStartData? matchStartData = await _distributedCache.GetMatchStartData(matchID);

        bool isSeasonMatch = matchStartData is not null;

        return Ok(new
        {
            error_code = 100,
            data = new
            {
                is_season_match = isSeasonMatch,
                comment = Array.Empty<string>(),
                referee = Array.Empty<string>()
            }
        });
    }

    /// <summary>
    ///     Returns active live matches sorted descending by average MMR,
    ///     including player rosters, team assignments, and star player designation.
    /// </summary>
    private async Task<IActionResult> GetRecommendedMatches(CancellationToken cancellationToken = default)
    {
        if (_mediator is not null)
        {
            RecommendedMatchesResultDto result = await _mediator.Send(new GetRecommendedMatchesQuery(), cancellationToken);
            return Ok(result.ResponseData);
        }

        IAccountRepository? accountRepo = accountRepository ?? HttpContext?.RequestServices?.GetService<IAccountRepository>();

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
                logger?.LogWarning(ex, "[AscensionController] Failed scanning MATCH-START-DATA keys from Redis on endpoint {EndPoint}", endPoint);
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

            if (connectedPlayers.Count > 0 && accountRepo is not null)
            {
                IReadOnlyList<Account> accounts = await accountRepo.GetAccountsByNamesAsync(connectedPlayers, cancellationToken);
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

            int elapsedSecs = (int)Math.Max(0, (TimeProvider.System.GetUtcNow() - match.TimestampStarted).TotalSeconds);
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

        return Ok(new
        {
            error_code = 100,
            success = true,
            recommended_pool = sortedPool
        });
    }

    /// <summary>
    ///     Extracts match and spectator info, publishes Redis event on ChatServer:HydratePlayerStatus,
    ///     and returns the star player nickname for direct client spectating.
    /// </summary>
    private async Task<IActionResult> SpectateMatch(CancellationToken cancellationToken = default)
    {
        IAccountRepository? accountRepo = accountRepository ?? HttpContext?.RequestServices?.GetService<IAccountRepository>();

        string? matchIdStr = Request.Query["match_id"].ToString();
        if (string.IsNullOrEmpty(matchIdStr) && Request.HasFormContentType)
        {
            matchIdStr = Request.Form["match_id"].ToString();
        }

        if (!int.TryParse(matchIdStr?.Trim(), out int matchId))
        {
            return BadRequest(new { error_code = 400, message = "Missing or invalid required parameter 'match_id'" });
        }

        int spectatorAccountId = 0;
        string? accountIdStr = Request.Query["account_id"].ToString();
        if (string.IsNullOrEmpty(accountIdStr) && Request.HasFormContentType)
        {
            accountIdStr = Request.Form["account_id"].ToString();
        }
        if (int.TryParse(accountIdStr?.Trim(), out int directAccId) && directAccId > 0)
        {
            spectatorAccountId = directAccId;
        }

        string? rawCookie = Request.Query["hongameclientcookie"].ToString();
        if (string.IsNullOrWhiteSpace(rawCookie))
        {
            rawCookie = Request.Query["cookie"].ToString();
        }
        if (string.IsNullOrWhiteSpace(rawCookie) && Request.HasFormContentType)
        {
            rawCookie = Request.Form["hongameclientcookie"].ToString();
            if (string.IsNullOrWhiteSpace(rawCookie))
            {
                rawCookie = Request.Form["cookie"].ToString();
            }
        }

        string cookie = string.IsNullOrWhiteSpace(rawCookie) ? "" : rawCookie.Split('|')[0].Trim();

        if (_mediator is not null)
        {
            SpectateMatchResultDto result = await _mediator.Send(
                new SpectateMatchCommand(matchId, spectatorAccountId, rawCookie),
                cancellationToken);
            return Ok(result.ResponseData);
        }

        string? spectatorName = null;
        if (!string.IsNullOrEmpty(cookie))
        {
            spectatorName = await _distributedCache.GetAccountNameForSessionCookie(cookie);
            if (spectatorAccountId == 0 && !string.IsNullOrEmpty(spectatorName) && accountRepo is not null)
            {
                Account? spectatorAcc = await accountRepo.GetByNicknameAsync(spectatorName, cancellationToken);
                if (spectatorAcc is not null)
                {
                    spectatorAccountId = spectatorAcc.ID;
                }
            }
        }

        MatchStartData? match = await _distributedCache.GetMatchStartData(matchId);
        string serverAddress = "127.0.0.1:11235";
        string matchName = match?.MatchName ?? $"Match {matchId}";

        if (match is not null)
        {
            MatchServer? matchServer = await _distributedCache.GetMatchServerByID(match.ServerID);
            if (matchServer is not null && !string.IsNullOrWhiteSpace(matchServer.IPAddress))
            {
                serverAddress = $"{matchServer.IPAddress}:{matchServer.Port}";
            }
        }

        List<string> connectedPlayers = await _distributedCache.GetMatchConnectedPlayers(matchId);
        string starPlayerName = "";
        int starPlayerAccountId = 0;

        if (connectedPlayers.Count > 0 && accountRepo is not null)
        {
            IReadOnlyList<Account> accounts = await accountRepo.GetAccountsByNamesAsync(connectedPlayers, cancellationToken);
            if (accounts.Count > 0)
            {
                Account starAcc = accounts.OrderByDescending(a =>
                    a.Statistics.FirstOrDefault(s => s.Type == AccountStatisticsType.Matchmaking)?.SkillRating
                    ?? a.Statistics.FirstOrDefault(s => s.Type == AccountStatisticsType.Public)?.SkillRating
                    ?? 1500).First();
                starPlayerName = starAcc.Name;
                starPlayerAccountId = starAcc.ID;
            }
            else
            {
                starPlayerName = connectedPlayers[0];
            }
        }
        else if (connectedPlayers.Count > 0)
        {
            starPlayerName = connectedPlayers[0];
        }

        if (string.IsNullOrEmpty(starPlayerName))
        {
            starPlayerName = match?.HostAccountName ?? "HostPlayer";
        }

        if (starPlayerAccountId == 0 && !string.IsNullOrEmpty(starPlayerName) && accountRepo is not null)
        {
            Account? acc = await accountRepo.GetByNicknameAsync(starPlayerName, cancellationToken);
            if (acc is not null) starPlayerAccountId = acc.ID;
        }

        // Publish Redis event on ChatServer:HydratePlayerStatus
        PlayerStatusHydrationEvent hydrationEvent = new(
            spectatorAccountId,
            starPlayerAccountId,
            matchId,
            serverAddress,
            matchName,
            starPlayerName,
            spectatorName);

        try
        {
            ISubscriber subscriber = redis.GetSubscriber();
            await subscriber.PublishAsync(
                RedisChannel.Literal("ChatServer:HydratePlayerStatus"),
                JsonSerializer.Serialize(hydrationEvent, MasterServerJsonContext.Default.PlayerStatusHydrationEvent));
            logger?.LogInformation(
                "[AscensionController] Published ChatServer:HydratePlayerStatus for Spectator {SpectatorId} ({SpectatorName}), StarPlayer {StarName} ({StarId}), Match {MatchId} at {ServerAddress}",
                spectatorAccountId, spectatorName ?? "Unknown", starPlayerName, starPlayerAccountId, matchId, serverAddress);
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "[AscensionController] Failed to publish ChatServer:HydratePlayerStatus to Redis");
        }

        return Ok(new
        {
            result = 0,
            star_player = starPlayerName,
            match_id = matchId,
            spectate_dir = serverAddress,
            match_version = match?.Version ?? "4.10.1.0"
        });
    }
}

