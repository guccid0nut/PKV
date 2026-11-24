// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ReSharper disable LogMessageIsConditional

using System.Diagnostics;
using System.Globalization;
using ASPIRE.Common.Caching;
using ASPIRE.Common.Serialization.PHP;
using KONGOR.MasterServer.Services.Mastery;
using KONGOR.MasterServer.Services.Security;

namespace KONGOR.MasterServer.Handlers.ClientRequester;

public partial class SimpleStatsHandler(
    IAccountRepository accountRepository,
    IMatchHistoryRepository matchHistoryRepository,
    IPlayerStatisticsService statisticsService,
    IHeroDefinitionService heroDefinitions,
    ISessionContextResolver sessionResolver,
    IOptions<OperationalConfiguration> operationalConfiguration,
    ILogger<SimpleStatsHandler> logger,
    INexusHybridCache? hybridCache = null,
    IServiceScopeFactory? serviceScopeFactory = null) : IClientRequestHandler
{
    private IAccountRepository AccountRepository { get; } = accountRepository;
    private IMatchHistoryRepository MatchHistoryRepository { get; } = matchHistoryRepository;
    private IPlayerStatisticsService StatisticsService { get; } = statisticsService;
    private IHeroDefinitionService HeroDefinitions { get; } = heroDefinitions;
    private ISessionContextResolver SessionResolver { get; } = sessionResolver;
    private OperationalConfiguration OperationalConfiguration { get; } = operationalConfiguration.Value;
    private ILogger Logger { get; } = logger;
    private INexusHybridCache? HybridCache { get; } = hybridCache;
    private IServiceScopeFactory? ServiceScopeFactory { get; } = serviceScopeFactory;

    private static readonly TimeSpan L1CacheTtl = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan L2CacheTtl = TimeSpan.FromMinutes(5);

    public async Task<IActionResult> HandleRequestAsync(HttpContext context,
        CancellationToken cancellationToken = default)
    {
        HttpRequest Request = context.Request;
        string? accountName = ExtractValue(Request, "nickname");

        // Simple Sanitization
        // Strip any unexpected suffixes from the nickname to ensure we find the correct account.
        if (accountName?.EndsWith("|B64", StringComparison.OrdinalIgnoreCase) == true)
        {
            accountName = accountName[..^4];
            if (Logger.IsEnabled(LogLevel.Information))
                Logger.LogInformation("[SimpleStats] Sanitized Nickname: '{AccountName}'", accountName);
        }

        LogRequestReceived(accountName ?? "NULL");

        Account? account = null;
        if (string.IsNullOrEmpty(accountName))
        {
            account = await SessionResolver.ResolveAccountAsync(context, cancellationToken);
            if (account != null)
            {
                accountName = account.Name;
                if (Logger.IsEnabled(LogLevel.Information))
                    Logger.LogInformation("[SimpleStats] Account '{AccountName}' resolved from session fallback",
                        accountName);
            }
            else
            {
                LogMissingNickname();
                return new BadRequestObjectResult(@"Missing Value For Form Parameter ""nickname""");
            }
        }

        string normalizedNick = accountName.Trim().ToLowerInvariant();
        string? tableParam = ExtractValue(Request, "table");
        string cleanTable = string.IsNullOrWhiteSpace(tableParam) ? "ranked" : tableParam.Trim().ToLowerInvariant();

        if (HybridCache != null)
        {
            string cacheKey = $"profile:simple_stats:{normalizedNick}:{cleanTable}";
            string[] tags = account != null
                ? [$"account:{account.ID}", $"account:nick:{normalizedNick}"]
                : [$"account:nick:{normalizedNick}"];

            string? cachedPayload = await HybridCache.GetOrCreateAsync(
                cacheKey,
                async ct =>
                {
                    if (ServiceScopeFactory != null)
                    {
                        using IServiceScope scope = ServiceScopeFactory.CreateScope();
                        IAccountRepository scopedAccountRepo = scope.ServiceProvider.GetRequiredService<IAccountRepository>();
                        IMatchHistoryRepository scopedMatchHistoryRepo = scope.ServiceProvider.GetRequiredService<IMatchHistoryRepository>();
                        IHeroDefinitionService scopedHeroDefs = scope.ServiceProvider.GetRequiredService<IHeroDefinitionService>();
                        IPlayerStatisticsService scopedStatsService = scope.ServiceProvider.GetRequiredService<IPlayerStatisticsService>();
                        ISessionContextResolver scopedSessionResolver = scope.ServiceProvider.GetRequiredService<ISessionContextResolver>();

                        return await ComputeAndSerializeStatsCoreAsync(
                            context,
                            accountName,
                            cleanTable,
                            account,
                            scopedAccountRepo,
                            scopedMatchHistoryRepo,
                            scopedHeroDefs,
                            scopedStatsService,
                            scopedSessionResolver,
                            ct);
                    }

                    return await ComputeAndSerializeStatsAsync(context, accountName, cleanTable, account, ct);
                },
                tags,
                L1CacheTtl,
                L2CacheTtl,
                allowStaleOnError: true,
                cancellationToken);

            if (string.IsNullOrEmpty(cachedPayload))
            {
                LogAccountNotFound(accountName);
                return new NotFoundObjectResult($@"Account With Name ""{accountName}"" Was Not Found");
            }

            LogReturningStats(accountName);
            return new PhpResult(cachedPayload);
        }

        string? payload = await ComputeAndSerializeStatsAsync(context, accountName, cleanTable, account, cancellationToken);
        if (string.IsNullOrEmpty(payload))
        {
            LogAccountNotFound(accountName);
            return new NotFoundObjectResult($@"Account With Name ""{accountName}"" Was Not Found");
        }

        LogReturningStats(accountName);
        return new PhpResult(payload);
    }

    private Task<string?> ComputeAndSerializeStatsAsync(
        HttpContext context,
        string accountName,
        string cleanTable,
        Account? preloadedAccount,
        CancellationToken cancellationToken)
    {
        return ComputeAndSerializeStatsCoreAsync(
            context,
            accountName,
            cleanTable,
            preloadedAccount,
            AccountRepository,
            MatchHistoryRepository,
            HeroDefinitions,
            StatisticsService,
            SessionResolver,
            cancellationToken);
    }

    private async Task<string?> ComputeAndSerializeStatsCoreAsync(
        HttpContext context,
        string accountName,
        string cleanTable,
        Account? preloadedAccount,
        IAccountRepository accountRepo,
        IMatchHistoryRepository matchHistoryRepo,
        IHeroDefinitionService heroDefs,
        IPlayerStatisticsService statsService,
        ISessionContextResolver sessionResolver,
        CancellationToken cancellationToken)
    {
        Stopwatch totalSw = Stopwatch.StartNew();
        Stopwatch stageSw = Stopwatch.StartNew();
        long accountLookupMs = 0;
        long masteryQueryMs = 0;
        long masteryFallbackMs = 0;
        long aggregatedStatsMs = 0;

        Account? account = preloadedAccount;
        if (account is null)
        {
            LogFetchingAccount(accountName);
            account = await accountRepo.GetByNicknameAsync(accountName, cancellationToken);
            if (account is null)
            {
                account = await sessionResolver.ResolveAccountAsync(context, cancellationToken);
                if (account != null && Logger.IsEnabled(LogLevel.Information))
                {
                    Logger.LogInformation(
                        "[SimpleStats] Account '{AccountName}' resolved from session fallback after nickname '{LookupNick}' miss",
                        account.Name, accountName);
                }
            }
            accountLookupMs = stageSw.ElapsedMilliseconds;
        }

        if (account is null)
        {
            return null;
        }

        stageSw.Restart();
        try
        {
            account.Mastery = await matchHistoryRepo.GetMasteryByAccountIdAsync(account.ID, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            if (Logger.IsEnabled(LogLevel.Warning))
                Logger.LogWarning(ex, "[SimpleStats] Masteries table query bypassed (table may not exist yet in DB)");
        }
        masteryQueryMs = stageSw.ElapsedMilliseconds;

        cancellationToken.ThrowIfCancellationRequested();

        // Dynamic fallback: if account.Mastery is null or has no hero experience entries, synthesize from player statistics
        if (account.Mastery is null || account.Mastery.HeroExperiences.Count == 0)
        {
            stageSw.Restart();
            try
            {
                IReadOnlyList<PlayerStatistics> playerStats = await matchHistoryRepo
                    .GetAllPlayerStatisticsForAccountAsync(account.ID, account.Name, cancellationToken);
                if (playerStats.Count > 0)
                {
                    Mastery fallbackMastery = new()
                    {
                        AccountID = account.ID,
                        Account = account,
                        HeroExperiences = []
                    };
                    Dictionary<string, int> expMap = new(StringComparer.OrdinalIgnoreCase);
                    foreach (PlayerStatistics ps in playerStats)
                    {
                        string identifier = ps.HeroIdentifier;
                        if (string.IsNullOrEmpty(identifier) && ps.HeroProductID is > 0)
                            identifier = heroDefs.GetHeroIdentifier(ps.HeroProductID.Value);

                        if (!string.IsNullOrEmpty(identifier))
                        {
                            int calcExp = ps.SecondsPlayed * 5;
                            if (calcExp == 0) calcExp = ps.HeroExperience;
                            if (calcExp > 0)
                                expMap[identifier] = expMap.GetValueOrDefault(identifier, 0) + calcExp;
                        }
                    }
                    foreach (var (heroIdent, exp) in expMap)
                    {
                        fallbackMastery.HeroExperiences.Add(new HeroMasteryExperience
                        {
                            HeroIdentifier = heroIdent,
                            Experience = exp
                        });
                    }
                    account.Mastery = fallbackMastery;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                if (Logger.IsEnabled(LogLevel.Warning))
                    Logger.LogWarning(ex, "[SimpleStats] Failed to dynamically synthesize fallback mastery for Account #{AccountId}", account.ID);
            }
            masteryFallbackMs = stageSw.ElapsedMilliseconds;
        }

        cancellationToken.ThrowIfCancellationRequested();

        bool isMasteryTable = string.Equals(cleanTable, "mastery", StringComparison.OrdinalIgnoreCase);
        bool hasMasteryEntries = account.Mastery?.HeroExperiences.Count > 0;

        // Fast-path: cleanTable == "mastery" (sent by lobby_v2.lua PlayerStatsMastery on hero-pick init)
        // only consumes TopHeroes in masteryResponse below and discards all 130 aggregated match columns.
        // Skip the heavy PlayerStatistics GroupBy when mastery entries are already present.
        PlayerStatisticsAggregatedDTO stats;
        if (isMasteryTable && hasMasteryEntries)
        {
            stats = new PlayerStatisticsAggregatedDTO
            {
                TopHeroes = account.Mastery!.HeroExperiences
                    .Select(exp => new FavHeroDTO
                    {
                        HeroId = heroDefs.GetBaseHeroId(exp.HeroIdentifier),
                        SecondsPlayed = exp.Experience > 0 ? (long)(exp.Experience / 5) : 1
                    })
                    .Where(h => h.HeroId > 0)
                    .OrderByDescending(h => h.SecondsPlayed)
                    .ToList()
            };
        }
        else
        {
            LogComputingStats(account.Name);
            stageSw.Restart();
            // Fetch aggregated player stats for the account with mode/map filtering
            stats = await statsService.GetAggregatedStatisticsAsync(account.ID, cleanTable, cancellationToken);
            aggregatedStatsMs = stageSw.ElapsedMilliseconds;

            // If TopHeroes is empty (e.g. HeroProductID was null in historical rows), populate from account mastery
            if (stats.TopHeroes.Count == 0 && hasMasteryEntries)
            {
                stats.TopHeroes = account.Mastery!.HeroExperiences
                    .Select(exp => new FavHeroDTO
                    {
                        HeroId = heroDefs.GetBaseHeroId(exp.HeroIdentifier),
                        SecondsPlayed = exp.Experience > 0 ? (long)(exp.Experience / 5) : 1
                    })
                    .Where(h => h.HeroId > 0)
                    .OrderByDescending(h => h.SecondsPlayed)
                    .ToList();
            }
        }

        if (Logger.IsEnabled(LogLevel.Information))
        {
            Logger.LogInformation(
                "[SimpleStats:Timing] Account={AccountName} (#{AccountId}) table={CleanTable} accountLookupMs={AccountLookupMs} masteryQueryMs={MasteryQueryMs} masteryFallbackMs={MasteryFallbackMs} aggregatedStatsMs={AggregatedStatsMs} skippedAggregatedForMastery={SkippedAggregated} totalMs={TotalMs}",
                account.Name, account.ID, cleanTable, accountLookupMs, masteryQueryMs, masteryFallbackMs, aggregatedStatsMs, isMasteryTable && hasMasteryEntries, totalSw.ElapsedMilliseconds);
        }

        if (isMasteryTable)
        {
            string masteryInfo = MasteryRewardService.Instance.GenerateMasteryInfoString(account, heroDefs, stats.TopHeroes);
            string masteryRewards = MasteryRewardService.Instance.GenerateMasteryRewardsString(account);

            Dictionary<object, object> masteryResponse = new()
            {
                [0] = account.Name,
                [1] = account.Clan?.Name ?? string.Empty,
                [2] = account.ClanTier.ToString(),
                [3] = "3",
                [4] = account.TimestampCreated.ToString("MM/dd/yyyy"),
                [5] = account.TimestampLastActive.ToString("MM/dd/yyyy"),
                [6] = string.Join(";", account.SelectedStoreItemCodes),
                [7] = account.User.TotalLevel.ToString(),
                [8] = account.User.TotalExperience.ToString(),
                [9] = "0",
                [10] = masteryInfo,
                [11] = masteryRewards,
                ["account_id"] = account.ID.ToString(),
                ["nickname"] = account.Name,
                ["name"] = account.Clan?.Name ?? string.Empty,
                ["rank"] = account.ClanTier.ToString(),
                ["standing"] = "3",
                ["create_date"] = account.TimestampCreated.ToString("MM/dd/yyyy"),
                ["last_activity"] = account.TimestampLastActive.ToString("MM/dd/yyyy"),
                ["selected_upgrades"] = account.SelectedStoreItemCodes,
                ["level"] = account.User.TotalLevel,
                ["level_exp"] = account.User.TotalExperience,
                ["mastery_info"] = masteryInfo,
                ["mastery_rewards"] = masteryRewards,
                ["vested_threshold"] = 5
            };

            return PhpSerializer.SerializeToString(masteryResponse);
        }

        ShowSimpleStatsResponse props = ClientRequestHelper.CreateShowSimpleStatsResponse(account, stats,
            int.Parse(OperationalConfiguration.CurrentSeason, CultureInfo.InvariantCulture), heroDefs);

        Dictionary<object, object> hybridResponse = ClientRequestHelper.CreateHybridSimpleStats(props, cleanTable);
        return PhpSerializer.SerializeToString(hybridResponse);
    }

    private static string? ExtractValue(HttpRequest request, string key)
    {
        if (request.HasFormContentType && request.Form.ContainsKey(key)) return request.Form[key].FirstOrDefault();

        if (request.Query.ContainsKey(key)) return request.Query[key].FirstOrDefault();

        if (request.QueryString.HasValue &&
            SpanFormUrlEncodedParser.TryExtractParameter(request.QueryString.Value.AsSpan(), key,
                out ReadOnlySpan<char> span))
            return span.ToString();

        return null;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "[SimpleStats] Request Received for: {AccountName}")]
    private partial void LogRequestReceived(string accountName);

    [LoggerMessage(Level = LogLevel.Warning, Message = "[SimpleStats] Missing Form Parameter: nickname")]
    private partial void LogMissingNickname();

    [LoggerMessage(Level = LogLevel.Information, Message = "[SimpleStats] Fetching Account: {AccountName}")]
    private partial void LogFetchingAccount(string accountName);

    [LoggerMessage(Level = LogLevel.Warning, Message = "[SimpleStats] Account Not Found: {AccountName}")]
    private partial void LogAccountNotFound(string accountName);

    [LoggerMessage(Level = LogLevel.Information, Message = "[SimpleStats] Computing Stats for: {AccountName}")]
    private partial void LogComputingStats(string accountName);

    [LoggerMessage(Level = LogLevel.Information, Message = "[SimpleStats] Returning Stats for: {AccountName}")]
    private partial void LogReturningStats(string accountName);
}
