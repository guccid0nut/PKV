// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Globalization;
using KONGOR.MasterServer.Configuration.Store;
using KONGOR.MasterServer.Helpers.Stats;
using KONGOR.MasterServer.Models.RequestResponse.Upgrades;
using KONGOR.MasterServer.Services.Security;
using KONGOR.MasterServer.Services.Store;
using MERRICK.DatabaseContext.Extensions;
using NEXUS.Contracts.Constants;
using NEXUS.Contracts.Data;

namespace KONGOR.MasterServer.Handlers.ClientRequester;

public partial class UpgradesHandler(
    ISessionContextResolver sessionResolver,
    IPlayerStatisticsService statisticsService,
    IHeroDefinitionService heroDefinitions,
    IOptions<OperationalConfiguration> operationalConfiguration,
    ILogger<UpgradesHandler> logger,
    IUpgradesResolutionService upgradesResolver,
    IStoreRepository? storeRepository = null) : IClientRequestHandler
{
    private ISessionContextResolver SessionResolver { get; } = sessionResolver;
    private IPlayerStatisticsService StatisticsService { get; } = statisticsService;
    private IHeroDefinitionService HeroDefinitions { get; } = heroDefinitions;
    private OperationalConfiguration OperationalConfiguration { get; } = operationalConfiguration.Value;
    private ILogger Logger { get; } = logger;
    private IUpgradesResolutionService UpgradesResolver { get; } = upgradesResolver;
    private IStoreRepository? StoreRepository { get; } = storeRepository;

    private static StoreItemsConfiguration StoreItems => JSONConfiguration.StoreItemsConfiguration;

    public async Task<IActionResult> HandleRequestAsync(HttpContext context,
        CancellationToken cancellationToken = default)
    {
        HttpRequest Request = context.Request;
        string? functionName = Request.Query["f"].FirstOrDefault()
                               ?? (Request.HasFormContentType && Request.Form.ContainsKey("f")
                                   ? Request.Form["f"].FirstOrDefault()
                                   : null);

        if (functionName == "debug_upgrades_diff")
            return await HandleDebugUpgradesDiff(context, Request, cancellationToken);

        return await HandleGetUpgrades(context, Request, cancellationToken);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "[Upgrades] Missing cookie.")]
    private partial void LogMissingCookie();

    [LoggerMessage(Level = LogLevel.Information,
        Message = "[Upgrades] HandleGetUpgrades Called. Cookie: '{CookiePrefix}'")]
    private partial void LogHandleGetUpgrades(string cookiePrefix);

    [LoggerMessage(Level = LogLevel.Information, Message = "[Upgrades] Validating cookie via DistributedCache...")]
    private partial void LogValidatingCookie();

    [LoggerMessage(Level = LogLevel.Information,
        Message = "[Upgrades] Validation Result: IsValid={IsValid}, Account={Account}")]
    private partial void LogValidationResult(bool isValid, string? account);

    [LoggerMessage(Level = LogLevel.Warning, Message = "[Upgrades] Validation Failed for cookie '{CookiePrefix}'")]
    private partial void LogValidationFailed(string cookiePrefix);

    [LoggerMessage(Level = LogLevel.Error, Message = "[Upgrades] Account '{AccountName}' not found in DB.")]
    private partial void LogAccountNotFound(string accountName);

    [LoggerMessage(Level = LogLevel.Information, Message = "[Upgrades] Response Length: {Length}")]
    private partial void LogResponseLength(int length);

    [LoggerMessage(Level = LogLevel.Error, Message = "[Upgrades] CRITICAL FAILURE in HandleGetUpgrades")]
    private partial void LogCriticalFailure(Exception ex);

    [LoggerMessage(Level = LogLevel.Information, Message = "[Upgrades] Response Payload: \n{Payload}")]
    private partial void LogResponsePayload(string payload);

    private async Task<IActionResult> HandleGetUpgrades(HttpContext context, HttpRequest Request,
        CancellationToken cancellationToken = default)
    {
        try
        {
            Account? account = await SessionResolver.ResolveAccountAsync(context, cancellationToken);
            if (account is null) return new UnauthorizedObjectResult("No Session Found Or Account Not Found");

            // Optimized Statistics Retrieval
            PlayerStatisticsAggregatedDTO stats =
                await StatisticsService.GetAggregatedStatisticsAsync(account.ID, cancellationToken: cancellationToken);
            ShowSimpleStatsResponse fullStats = ClientRequestHelper.CreateShowSimpleStatsResponse(account, stats,
                int.Parse(OperationalConfiguration.CurrentSeason), HeroDefinitions);

            double rnkRating = 1500.0 + stats.RankedRatingChange;
            double csRating = 1500.0 + stats.CasualRatingChange;

            AccountFieldStatsDTO accountFieldStats = new()
            {
                AccountID = account.ID.ToString(),
                Level = fullStats.Level,
                LevelExp = fullStats.LevelExperience.ToString(),
                PublicSkillRating = "1500.000",
                RankedRating = rnkRating.ToString("F3", CultureInfo.InvariantCulture),
                CasualRating = csRating.ToString("F3", CultureInfo.InvariantCulture),
                TotalGamesPlayed = stats.TotalMatches,
                TotalDiscos = stats.Disconnected,
                RankedGamesPlayed = stats.RankedMatches,
                RankedDiscos = stats.RankedDiscos,
                CasualGamesPlayed = stats.CasualMatches,
                CasualDiscos = stats.CasualDiscos,
                MidGamesPlayed = 0,
                MidDiscos = 0,
                RiftGamesPlayed = 0,
                RiftDiscos = 0,
                CampaignGamesPlayed = 0,
                CampaignDiscos = 0,
                CampaignCasualGamesPlayed = 0,
                CampaignCasualDisconnections = 0,
                TrialGamesPlayed = 0,
                Standing = 3,
                IsNew = 0
            };

            Dictionary<int, AccountFieldStatsDTO> fieldStats = new() { { account.ID, accountFieldStats } };

            HashSet<string> expandedOwned = await UpgradesResolver.ResolveExpandedOwnedItemsAsync(account, cancellationToken);

            Dictionary<string, OneOf<StoreItemData, StoreItemDiscountCoupon>> myUpgradesInfo = [];
            foreach (string item in expandedOwned)
            {
                if (item.StartsWith("ma.", StringComparison.Ordinal)) continue;

                // Coupon logic temporarily disabled per user request
                if (item.StartsWith("cp.", StringComparison.Ordinal)) continue;

                myUpgradesInfo[item] = new StoreItemData();
            }

            GetUpgradesResponseDTO responseDto = new()
            {
                FieldStats = fieldStats,
                MyUpgrades = [.. expandedOwned],
                MyUpgradesInfo = myUpgradesInfo,
                SelectedUpgrades = account.SelectedStoreItemCodes,
                Points = account.User.GoldCoins,
                MMPoints = account.User.SilverCoins,
                GameTokens = fullStats.GameTokens,
                Standing = 3,
                SeasonLevel = fullStats.SeasonLevel,
                CreepLevel = 0,
                Timestamp = Convert.ToInt32(Math.Min(TimeProvider.System.GetUtcNow().ToUnixTimeSeconds(),
                    Convert.ToInt64(int.MaxValue)))
            };

            return new PhpResult(responseDto);
        }
        catch (Exception ex)
        {
            LogCriticalFailure(ex);
            return new StatusCodeResult(500);
        }
    }

    private async Task<IActionResult> HandleDebugUpgradesDiff(HttpContext context, HttpRequest Request,
        CancellationToken cancellationToken = default)
    {
        try
        {
            Account? account = await SessionResolver.ResolveAccountAsync(context, cancellationToken);
            if (account is null) return new UnauthorizedObjectResult("Session Or Account Not Found");

            StringBuilder report = new();
            report.AppendLine($"--- Payload Comparison Report for {account.Name} ---");

            PlayerStatisticsAggregatedDTO stats =
                await StatisticsService.GetAggregatedStatisticsAsync(account.ID, cancellationToken: cancellationToken);
            ShowSimpleStatsResponse fullStats = ClientRequestHelper.CreateShowSimpleStatsResponse(account, stats,
                int.Parse(OperationalConfiguration.CurrentSeason), HeroDefinitions);

            Dictionary<string, object> initStatsReference = new()
            {
                { "points", account.User.SilverCoins },
                { "mmpoints", account.User.GoldCoins },
                { "standing", "3" },
                { "0", true }
            };

            Dictionary<string, object> fieldStats = new()
            {
                ["nickname"] = account.GetNameWithClanTag(),
                ["account_id"] = fullStats.ID
            };
            List<string> ownedCodes = account.User.InventoryItems?.Select(i => i.ItemCode).Distinct().ToList() ?? [];

            Dictionary<string, object> myUpgradesInfo = ownedCodes
                .ToDictionary(item => item, _ => (object)new Dictionary<string, string>
                {
                    { "data", "" }, { "end_time", "0" }, { "start_time", "0" }
                });

            Dictionary<string, object> upgradesFix = new()
            {
                ["field_stats"] = fieldStats,
                ["my_upgrades_info"] = myUpgradesInfo,
                ["points"] = account.User.GoldCoins.ToString(CultureInfo.InvariantCulture),
                ["mmpoints"] = account.User.SilverCoins.ToString(CultureInfo.InvariantCulture),
                ["game_tokens"] = fullStats.GameTokens,
                ["standing"] = "3",
                ["0"] = true
            };

            report.AppendLine("\n[Critical Key Analysis]");
            report.AppendLine($"InitStats (Working): Key '0' is STRING. Value: {initStatsReference["0"]}");
            report.AppendLine($"Upgrades (Proposed): Key '0' is STRING. Value: {upgradesFix["0"]}");

            report.AppendLine("\n[Serialization Preview]");
            report.AppendLine(
                $"InitStats '0' -> {PhpSerialization.Serialize(initStatsReference["0"])} (inside dict with string keys)");

            Dictionary<string, bool> d1 = new() { ["0"] = true };
            Dictionary<int, bool> d2 = new() { [0] = true };

            report.AppendLine($"Dict<string,bool> {{[\"0\"]=true}} -> {PhpSerialization.Serialize(d1)}");
            report.AppendLine($"Dict<int,bool> {{[0]=true}}       -> {PhpSerialization.Serialize(d2)}");

            report.AppendLine("\n[Conclusion]");
            report.AppendLine(
                "If InitStats works with s:1:\"0\";b:1;, then Upgrades MUST match it unless the client parser expects different types for different packets.");
            report.AppendLine("Current Fix sets it to i:0;b:1; (Int Key). Verify if this is d sired.");

            return new OkObjectResult(report.ToString());
        }
        catch (Exception ex)
        {
            return new ObjectResult(ex.Message) { StatusCode = 500 };
        }
    }
}
