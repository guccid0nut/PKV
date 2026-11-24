// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Globalization;
using ASPIRE.Common.Caching;
using ASPIRE.Common.Serialization.PHP;
using KONGOR.MasterServer.Extensions;
using KONGOR.MasterServer.Features.Matches.GetMatchHistory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NEXUS.Contracts.Data;

namespace KONGOR.MasterServer.Handlers.ClientRequester;

public partial class MatchHistoryHandler(
    IMatchHistoryRepository matchHistoryRepository,
    IAccountRepository accountRepository,
    IDatabase distributedCache,
    IHeroDefinitionService heroDefinitionService,
    ILogger<MatchHistoryHandler> logger,
    IGetMatchHistoryHandler? getMatchHistoryHandler = null,
    INexusHybridCache? hybridCache = null,
    IServiceScopeFactory? serviceScopeFactory = null) : IClientRequestHandler
{
    private IMatchHistoryRepository MatchHistoryRepository { get; } = matchHistoryRepository;
    private IAccountRepository AccountRepository { get; } = accountRepository;
    private IDatabase DistributedCache { get; } = distributedCache;
    private IHeroDefinitionService HeroDefinitionService { get; } = heroDefinitionService;
    private ILogger Logger { get; } = logger;
    private IGetMatchHistoryHandler GetMatchHistoryHandler { get; } =
        getMatchHistoryHandler ?? new GetMatchHistoryHandler(
            matchHistoryRepository,
            accountRepository,
            NullLogger<GetMatchHistoryHandler>.Instance);
    private INexusHybridCache? HybridCache { get; } = hybridCache;

    public async Task<IActionResult> HandleRequestAsync(HttpContext context,
        CancellationToken cancellationToken = default)
    {
        HttpRequest Request = context.Request;
        string? functionName = Request.GetQueryOrFormValue("f");

        // Dispatch based on function name or analyze internal logic
        // "grab_last_matches", "grab_last_matches_from_nick" -> HandleGrabLastMatches
        // "match_history_overview" -> HandleMatchHistoryOverview

        if (functionName == "match_history_overview")
            return await HandleMatchHistoryOverview(context, Request, cancellationToken);

        return await HandleGrabLastMatches(context, Request, cancellationToken);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "[MatchHistory] GrabLastMatches for '{AccountName}'")]
    private partial void LogGrabLastMatches(string accountName);

    [LoggerMessage(Level = LogLevel.Warning, Message = "[MatchHistory] Session/Account Not Found")]
    private partial void LogSessionNotFound();

    [LoggerMessage(Level = LogLevel.Warning, Message = "[MatchHistory] Account Not Found in DB")]
    private partial void LogAccountNotFound();

    [LoggerMessage(Level = LogLevel.Information, Message = "[MatchHistory] Overview for '{Nickname}' (Table: {Table})")]
    private partial void LogHistoryOverview(string nickname, string table);

    [LoggerMessage(Level = LogLevel.Information, Message = "[MatchHistory] Returning {Count} entries.")]
    private partial void LogReturningEntries(int count);

    private static string? GetRequestValue(HttpRequest request, string key)
    {
        if (request.QueryString.HasValue &&
            SpanFormUrlEncodedParser.TryExtractParameter(request.QueryString.Value.AsSpan(), key,
                out ReadOnlySpan<char> querySpan))
            return querySpan.ToString();

        if (request.HasFormContentType && request.Form.ContainsKey(key)) return request.Form[key].FirstOrDefault();

        return request.Query.ContainsKey(key) ? request.Query[key].FirstOrDefault() : null;
    }

    private async Task<IActionResult> HandleGrabLastMatches(HttpContext context, HttpRequest Request,
        CancellationToken cancellationToken = default)
    {
        string? accountName = GetRequestValue(Request, "nickname");

        // Logan (2025-02-14): Simple Sanitization (Strip |B64)
        if (accountName?.EndsWith("|B64", StringComparison.OrdinalIgnoreCase) == true) accountName = accountName[..^4];

        if (string.IsNullOrEmpty(accountName))
        {
            string? cookie = ClientRequestHelper.GetCookie(Request);
            accountName = context.Items["SessionAccountName"] as string
                          ?? await DistributedCache.GetAccountNameForSessionCookie(cookie ?? "NULL");
        }

        LogGrabLastMatches(accountName ?? "NULL");

        if (accountName is null)
        {
            LogSessionNotFound();
            return new UnauthorizedObjectResult("Session Not Found");
        }

        if (HybridCache != null)
        {
            string normalizedNick = accountName.Trim().ToLowerInvariant();
            string cacheKey = $"match:history:recent:{normalizedNick}";
            string? cachedPayload = await HybridCache.GetOrCreateAsync(
                cacheKey,
                async token =>
                {
                    GetMatchHistoryQuery query = new(null, accountName, 100);
                    GetMatchHistoryResponse result;
                    if (serviceScopeFactory != null)
                    {
                        await using AsyncServiceScope scope = serviceScopeFactory.CreateAsyncScope();
                        IMatchHistoryRepository scopedHistoryRepo = scope.ServiceProvider.GetRequiredService<IMatchHistoryRepository>();
                        IAccountRepository scopedAccountRepo = scope.ServiceProvider.GetRequiredService<IAccountRepository>();
                        IGetMatchHistoryHandler handler = scope.ServiceProvider.GetService<IGetMatchHistoryHandler>()
                            ?? new GetMatchHistoryHandler(scopedHistoryRepo, scopedAccountRepo, NullLogger<GetMatchHistoryHandler>.Instance);
                        result = await handler.HandleAsync(query, token);
                    }
                    else
                    {
                        result = await GetMatchHistoryHandler.HandleAsync(query, token);
                    }
                    if (!result.Success || result.Data == null) return null;
                    return PhpSerializer.Serialize(result.Data);
                },
                [$"account:nick:{normalizedNick}"],
                TimeSpan.FromSeconds(30),
                TimeSpan.FromMinutes(5),
                allowStaleOnError: true,
                cancellationToken);

            if (cachedPayload != null)
            {
                return new PhpResult(cachedPayload, contentType: "text/plain; charset=utf-8");
            }
        }

        GetMatchHistoryQuery query = new(null, accountName, 100);
        GetMatchHistoryResponse result = await GetMatchHistoryHandler.HandleAsync(query, cancellationToken);

        if (!result.Success)
        {
            if (result.StatusCode == 404)
            {
                LogAccountNotFound();
                return new NotFoundObjectResult("Account Not Found");
            }
            return new ObjectResult(result.ErrorMessage) { StatusCode = result.StatusCode };
        }

        return new PhpResult(result.Data!, contentType: "text/plain; charset=utf-8");
    }

    private async Task<IActionResult> HandleMatchHistoryOverview(HttpContext context, HttpRequest Request,
        CancellationToken cancellationToken = default)
    {
        string? nickname = GetRequestValue(Request, "nickname");
        string? accountIdStr = GetRequestValue(Request, "account_id");
        string? table = GetRequestValue(Request, "table");

        // Logan (2025-02-14): Simple Sanitization (Strip |B64)
        if (nickname?.EndsWith("|B64", StringComparison.OrdinalIgnoreCase) == true) nickname = nickname[..^4];

        if (string.IsNullOrEmpty(nickname) && !string.IsNullOrEmpty(accountIdStr) && int.TryParse(accountIdStr,
                NumberStyles.Integer, CultureInfo.InvariantCulture, out int accId))
        {
            Account? acc = await AccountRepository.GetByIdAsync(accId, cancellationToken);
            nickname = acc?.Name;
        }

        if (string.IsNullOrEmpty(nickname))
        {
            string? cookie = ClientRequestHelper.GetCookie(Request);
            nickname = context.Items["SessionAccountName"] as string
                       ?? await DistributedCache.GetAccountNameForSessionCookie(cookie ?? "NULL");
        }

        LogHistoryOverview(nickname ?? "NULL", table ?? "NULL");

        if (string.IsNullOrEmpty(nickname))
        {
            MatchHistoryOverviewResponse emptyResponse = new() { Entries = [] };
            return new PhpResult(emptyResponse, contentType: "text/plain; charset=utf-8");
        }

        Account? account = await AccountRepository.GetByNicknameAsync(nickname, cancellationToken);

        if (account is null)
        {
            MatchHistoryOverviewResponse emptyResponse = new() { Entries = [] };
            return new PhpResult(emptyResponse, contentType: "text/plain; charset=utf-8");
        }

        string? currentSeason = GetRequestValue(Request, "current_season") ?? GetRequestValue(Request, "curseason");
        string cleanTable = string.IsNullOrWhiteSpace(table) ? "all" : table.Trim().ToLowerInvariant();
        string cleanSeason = string.IsNullOrWhiteSpace(currentSeason) ? "current" : currentSeason.Trim().ToLowerInvariant();

        if (HybridCache != null)
        {
            string cacheKey = $"match:history:overview:{account.ID}:{cleanTable}:{cleanSeason}";
            string? cachedPayload = await HybridCache.GetOrCreateAsync(
                cacheKey,
                async token =>
                {
                    IReadOnlyList<PlayerMatchRecordDTO> historyData;
                    if (serviceScopeFactory != null)
                    {
                        await using AsyncServiceScope scope = serviceScopeFactory.CreateAsyncScope();
                        IMatchHistoryRepository repo = scope.ServiceProvider.GetRequiredService<IMatchHistoryRepository>();
                        historyData = await repo.GetPlayerMatchHistoryOverviewAsync(
                            account.ID, nickname, table, currentSeason, 100, token);
                    }
                    else
                    {
                        historyData = await MatchHistoryRepository.GetPlayerMatchHistoryOverviewAsync(
                            account.ID, nickname, table, currentSeason, 100, token);
                    }

                    List<MatchHistoryOverviewEntry> entries = BuildOverviewEntries(historyData);
                    MatchHistoryOverviewResponse response = new() { Entries = entries };
                    return PhpSerializer.Serialize(response);
                },
                [$"account:{account.ID}", $"account:nick:{account.Name.Trim().ToLowerInvariant()}"],
                TimeSpan.FromSeconds(30),
                TimeSpan.FromMinutes(5),
                allowStaleOnError: true,
                cancellationToken);

            if (cachedPayload != null)
            {
                return new PhpResult(cachedPayload, contentType: "text/plain; charset=utf-8");
            }
        }

        IReadOnlyList<PlayerMatchRecordDTO> uncachedHistoryData = await MatchHistoryRepository.GetPlayerMatchHistoryOverviewAsync(
            account.ID, nickname, table, currentSeason, 100, cancellationToken);

        List<MatchHistoryOverviewEntry> overviewEntries = BuildOverviewEntries(uncachedHistoryData);

        MatchHistoryOverviewResponse uncachedResponse = new() { Entries = overviewEntries };
        LogReturningEntries(overviewEntries.Count);
        return new PhpResult(uncachedResponse, contentType: "text/plain; charset=utf-8");
    }

    private List<MatchHistoryOverviewEntry> BuildOverviewEntries(IReadOnlyList<PlayerMatchRecordDTO> historyData)
    {
        List<MatchHistoryOverviewEntry> entries = [];

        foreach (PlayerMatchRecordDTO match in historyData)
        {
            string map = string.IsNullOrEmpty(match.Map) ? "unknown" : match.Map;
            string date = match.TimestampRecorded.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
            int duration = match.TimePlayed;

            uint heroProductId = match.HeroProductID ?? match.AlternativeAvatarProductID ?? 0;
            string altAvatarName = match.AlternativeAvatarName ?? "";

            string heroIdentifierString = !string.IsNullOrWhiteSpace(match.HeroIdentifier)
                ? HeroDefinitionService.CleanHeroIdentifier(match.HeroIdentifier)
                : string.Empty;

            uint baseHeroId = 0;
            if (!string.IsNullOrEmpty(heroIdentifierString))
                baseHeroId = HeroDefinitionService.GetBaseHeroId(heroIdentifierString);

            if (baseHeroId == 0) baseHeroId = HeroDefinitionService.GetBaseHeroId(heroProductId);

            if (baseHeroId == 0 && !string.IsNullOrEmpty(altAvatarName))
                baseHeroId = HeroDefinitionService.GetBaseHeroId(altAvatarName);

            if (baseHeroId == 0 && heroProductId > 0) baseHeroId = heroProductId;

            if (string.IsNullOrEmpty(heroIdentifierString) || heroIdentifierString == "Hero_Legionnaire")
            {
                heroIdentifierString = HeroDefinitionService.GetHeroIdentifier(baseHeroId);
                if (string.IsNullOrEmpty(heroIdentifierString) || heroIdentifierString == "Hero_Legionnaire")
                {
                    if (!string.IsNullOrEmpty(altAvatarName))
                    {
                        heroIdentifierString = altAvatarName;
                    }
                    else
                    {
                        string prodIdent = HeroDefinitionService.GetHeroIdentifier(heroProductId);
                        if (!string.IsNullOrEmpty(prodIdent)) heroIdentifierString = prodIdent;
                    }
                }
            }

            heroIdentifierString = HeroDefinitionService.CleanHeroIdentifier(heroIdentifierString);

            if (baseHeroId is >= 1000 or 0)
            {
                uint reResolved = HeroDefinitionService.GetBaseHeroId(heroIdentifierString);
                if (reResolved is > 0 and < 1000) baseHeroId = reResolved;
            }

            entries.Add(new MatchHistoryOverviewEntry
            {
                MatchID = match.MatchID.ToString(CultureInfo.InvariantCulture),
                Wins = match.Win.ToString(CultureInfo.InvariantCulture),
                Team = match.Team.ToString(CultureInfo.InvariantCulture),
                HeroKills = match.HeroKills.ToString(CultureInfo.InvariantCulture),
                Deaths = match.HeroDeaths.ToString(CultureInfo.InvariantCulture),
                HeroAssists = match.HeroAssists.ToString(CultureInfo.InvariantCulture),
                HeroID = baseHeroId.ToString(CultureInfo.InvariantCulture),
                SecondsPlayed = duration.ToString(CultureInfo.InvariantCulture),
                Map = map,
                MatchDatetime = date,
                HeroClientName = heroIdentifierString
            });
        }

        return entries;
    }
}
