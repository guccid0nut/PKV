// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Globalization;
using ASPIRE.Common.Caching;
using ASPIRE.Common.Serialization.PHP;
using KONGOR.MasterServer.Extensions;
using KONGOR.MasterServer.Features.Matches.SubmitMatchStats;
using KONGOR.MasterServer.Helpers.Stats;
using KONGOR.MasterServer.Services.Quests;
using KONGOR.MasterServer.Services.Store;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using NEXUS.Contracts.Security;
using MatchPlayerInventory = KONGOR.MasterServer.Models.RequestResponse.Stats.MatchPlayerInventory;

namespace KONGOR.MasterServer.Handlers.ClientRequester;

public partial class MatchStatsHandler(
    IAccountRepository accountRepository,
    IMatchHistoryRepository matchHistoryRepository,
    IDatabase distributedCache,
    IHeroDefinitionService heroDefinitionService,
    ILogger<MatchStatsHandler> logger,
    INexusHybridCache? hybridCache = null,
    IMemoryCache? memoryCache = null,
    IServiceScopeFactory? serviceScopeFactory = null,
    IConfiguration? configuration = null,
    IWarEffortService? warEffortService = null) : IClientRequestHandler
{
    private IAccountRepository AccountRepository { get; } = accountRepository;
    private IMatchHistoryRepository MatchHistoryRepository { get; } = matchHistoryRepository;
    private IDatabase DistributedCache { get; } = distributedCache;
    private IHeroDefinitionService HeroDefinitionService { get; } = heroDefinitionService;
    private ILogger Logger { get; } = logger;
    private INexusHybridCache? HybridCache { get; } = hybridCache;
    private IMemoryCache? MemoryCache { get; } = memoryCache;
    private IConfiguration? Configuration { get; } = configuration;

    public async Task<IActionResult> HandleRequestAsync(HttpContext context,
        CancellationToken cancellationToken = default)
    {
        HttpRequest Request = context.Request;
        string? cookie = ClientRequestHelper.GetCookie(Request);
        string? matchIDString = Request.GetFormOrQueryValue("match_id");

        LogRequestReceived(matchIDString ?? "NULL", SecurityMasking.MaskToken(cookie));

        if (matchIDString is null)
        {
            LogMissingMatchId();
            return new BadRequestObjectResult(@"Missing Value For Form Parameter ""match_id""");
        }

        if (!int.TryParse(matchIDString, NumberStyles.Integer, CultureInfo.InvariantCulture, out int matchID))
        {
            LogInvalidMatchId(matchIDString);
            return new BadRequestObjectResult("Invalid Match ID");
        }

        string? accountName = context.Items["SessionAccountName"] as string;
        if (accountName is null && !string.IsNullOrWhiteSpace(cookie) &&
            !cookie.Equals("NULL", StringComparison.OrdinalIgnoreCase))
            accountName = await DistributedCache.GetAccountNameForSessionCookie(cookie);

        Account? account = null;
        if (accountName is not null)
        {
            LogFetchingRequester(accountName);
            account = await AccountRepository.GetByNicknameAsync(accountName, cancellationToken);
        }

        int accountId = account?.ID ?? 0;

        bool awaitedIngestion = await MatchSubmissionIdempotency.WaitForCompletionAsync(
            DistributedCache,
            matchID,
            TimeSpan.FromSeconds(4.0),
            cancellationToken: cancellationToken);
        if (awaitedIngestion && Logger.IsEnabled(LogLevel.Information))
        {
            Logger.LogInformation("[MatchStats] Awaited in-flight stats ingestion for Match ID: {MatchID}", matchID);
        }

        if (HybridCache != null)
        {
            string cacheKey = $"match:stats:{matchID}:{accountId}";
            bool factoryReturnedNull = false;
            string? cachedPayload = await HybridCache.GetOrCreateAsync(
                cacheKey,
                async token =>
                {
                    MatchCoreData? core;
                    if (serviceScopeFactory != null)
                    {
                        await using AsyncServiceScope scope = serviceScopeFactory.CreateAsyncScope();
                        IMatchHistoryRepository scopedHistoryRepo = scope.ServiceProvider.GetRequiredService<IMatchHistoryRepository>();
                        IAccountRepository scopedAccountRepo = scope.ServiceProvider.GetRequiredService<IAccountRepository>();
                        core = await GetMatchCoreDataAsync(scopedHistoryRepo, scopedAccountRepo, matchID, token);
                    }
                    else
                    {
                        core = await GetMatchCoreDataAsync(MatchHistoryRepository, AccountRepository, matchID, token);
                    }
                    if (core == null)
                    {
                        factoryReturnedNull = true;
                        return null;
                    }

                    bool resolvedUnlockAll = UpgradesResolutionService.GetUnlockAllSetting(
                        Configuration ?? context.RequestServices?.GetService<IConfiguration>());
                    MatchStatsResponse response = BuildMatchStatsResponse(core, account, resolvedUnlockAll);
                    if (account is not null)
                    {
                        IWarEffortService? scopedQuestSvc = null;
                        if (serviceScopeFactory != null)
                        {
                            await using AsyncServiceScope qScope = serviceScopeFactory.CreateAsyncScope();
                            scopedQuestSvc = qScope.ServiceProvider.GetService<IWarEffortService>();
                            if (scopedQuestSvc is not null && scopedQuestSvc.IsEnabled)
                            {
                                try
                                {
                                    response.QuestSystem = await scopedQuestSvc
                                        .BuildClientPayloadAsync(account.ID, includeOnlyNativeTriggers: false, token)
                                        .ConfigureAwait(false);
                                }
                                catch (Exception qe)
                                {
                                    Logger.LogWarning(qe, "[WAR_EFFORT] Failed to populate MatchStats quest_system for AccountId={AccountId}", account.ID);
                                }
                            }
                        }
                        else if (warEffortService is not null && warEffortService.IsEnabled)
                        {
                            try
                            {
                                response.QuestSystem = await warEffortService
                                    .BuildClientPayloadAsync(account.ID, includeOnlyNativeTriggers: false, token)
                                    .ConfigureAwait(false);
                            }
                            catch (Exception qe)
                            {
                                Logger.LogWarning(qe, "[WAR_EFFORT] Failed to populate MatchStats quest_system for AccountId={AccountId}", account.ID);
                            }
                        }
                    }
                    return PhpSerializer.Serialize(response);
                },
                [$"match:{matchID}", $"account:{accountId}"],
                TimeSpan.FromMinutes(5),
                TimeSpan.FromHours(24),
                allowStaleOnError: true,
                cancellationToken);

            if (cachedPayload != null)
            {
                LogReturningStats(matchID);
                return new PhpResult(cachedPayload, contentType: "text/plain; charset=utf-8");
            }

            // Never leave a null/not-found entry cached in L1/L2 so retries succeed as soon as ingestion commits.
            await HybridCache.RemoveAsync(cacheKey, cancellationToken);

            if (factoryReturnedNull)
            {
                LogStatsNotFound(matchID);
                bool unlockAll = UpgradesResolutionService.GetUnlockAllSetting(
                    Configuration ?? context.RequestServices?.GetService<IConfiguration>());
                HashSet<string> safeOwned = UpgradesResolutionService.ExpandOwnedItems(
                    account?.User?.InventoryItems?.Select(i => i.ItemCode),
                    unlockAllItems: unlockAll);
                MatchStatsResponse safeResponse = CreateSafeResponse(account, safeOwned);
                return new PhpResult(safeResponse, contentType: "text/plain; charset=utf-8");
            }
        }

        MatchCoreData? coreData = await GetMatchCoreDataAsync(MatchHistoryRepository, AccountRepository, matchID, cancellationToken);
        bool explicitUnlockAll = UpgradesResolutionService.GetUnlockAllSetting(
            Configuration ?? context.RequestServices?.GetService<IConfiguration>());
        if (coreData is null)
        {
            LogStatsNotFound(matchID);
            HashSet<string> safeOwned = UpgradesResolutionService.ExpandOwnedItems(
                account?.User?.InventoryItems?.Select(i => i.ItemCode),
                unlockAllItems: explicitUnlockAll);
            MatchStatsResponse safeResponse = CreateSafeResponse(account, safeOwned);
            return new PhpResult(safeResponse, contentType: "text/plain; charset=utf-8");
        }

        MatchStatsResponse finalResponse = BuildMatchStatsResponse(coreData, account, explicitUnlockAll);
        if (account is not null && warEffortService is not null && warEffortService.IsEnabled)
        {
            try
            {
                finalResponse.QuestSystem = await warEffortService
                    .BuildClientPayloadAsync(account.ID, includeOnlyNativeTriggers: false, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception qe)
            {
                Logger.LogWarning(qe, "[WAR_EFFORT] Failed to populate MatchStats quest_system for AccountId={AccountId}", account.ID);
            }
        }
        LogReturningStats(matchID);
        return new PhpResult(finalResponse);
    }

    private sealed record MatchCoreData(
        MatchStatistics MatchStatistics,
        List<PlayerStatistics> AllPlayerStatistics,
        MatchStartData MatchStartData,
        Dictionary<int, MatchPlayerStatistics> MatchPlayerStatistics,
        Dictionary<string, MatchPlayerInventory> MatchPlayerInventories);

    private Task<MatchCoreData?> GetMatchCoreDataAsync(int matchID, CancellationToken cancellationToken) =>
        GetMatchCoreDataAsync(MatchHistoryRepository, AccountRepository, matchID, cancellationToken);

    private async Task<MatchCoreData?> GetMatchCoreDataAsync(
        IMatchHistoryRepository historyRepo,
        IAccountRepository accountRepo,
        int matchID,
        CancellationToken cancellationToken)
    {
        string coreCacheKey = $"match:core:{matchID}";
        if (MemoryCache != null && MemoryCache.TryGetValue(coreCacheKey, out MatchCoreData? cachedCore) && cachedCore != null)
        {
            return cachedCore;
        }

        MatchCoreData? core = await BuildMatchCoreDataAsync(historyRepo, accountRepo, matchID, cancellationToken);
        if (core != null && MemoryCache != null)
        {
            MemoryCache.Set(coreCacheKey, core, TimeSpan.FromHours(24));
        }
        return core;
    }

    private Task<MatchCoreData?> BuildMatchCoreDataAsync(int matchID, CancellationToken cancellationToken) =>
        BuildMatchCoreDataAsync(MatchHistoryRepository, AccountRepository, matchID, cancellationToken);

    private async Task<MatchCoreData?> BuildMatchCoreDataAsync(
        IMatchHistoryRepository historyRepo,
        IAccountRepository accountRepo,
        int matchID,
        CancellationToken cancellationToken)
    {
        LogFetchingStatistics(matchID);
        MatchStatistics? matchStatistics =
            await historyRepo.GetMatchByIdAsync(matchID, cancellationToken);

        if (matchStatistics is null) return null;

        LogFetchingPlayerStats(matchID);
        List<PlayerStatistics> allPlayerStatistics =
        [
            .. await historyRepo.GetPlayerStatisticsForMatchAsync(matchStatistics.MatchID, cancellationToken)
        ];

        LogFetchingMatchStartData();
        MatchStartData? matchStartData = await DistributedCache.GetMatchStartData(matchStatistics.MatchID);

        string resolvedServerName = !string.IsNullOrEmpty(matchStatistics.HostAccountName) &&
                                    matchStatistics.HostAccountName != "Unknown"
            ? matchStatistics.HostAccountName
            : !string.IsNullOrEmpty(matchStatistics.FileName) && matchStatistics.FileName != "Unknown"
                ? matchStatistics.FileName
                : matchStatistics.ServerID > 0
                    ? $"NEXUS Match Server #{matchStatistics.ServerID}"
                    : "NEXUS Local Server";

        matchStartData ??= new MatchStartData
        {
            MatchID = matchStatistics.MatchID,
            ServerID = matchStatistics.ServerID,
            MatchName = matchStatistics.FileName,
            HostAccountName = matchStatistics.HostAccountName,
            Map = matchStatistics.Map,
            MatchMode = matchStatistics.GameMode,
            Version = matchStatistics.Version,
            IsCasual = matchStatistics.GameMode == "casual",
            MatchType = (byte)(matchStatistics.GameMode.Contains("rank") ? 1 : 0),
            Options = MatchOptions.None,
            ServerName = resolvedServerName
        };

        Dictionary<int, MatchPlayerStatistics> matchPlayerStatistics = [];
        Dictionary<string, MatchPlayerInventory> matchPlayerInventories = [];

        List<int> playerAccountIDs = [.. allPlayerStatistics.Select(s => s.AccountID).Distinct()];

        LogFetchingBulkStats(playerAccountIDs.Count);
        List<AccountStatistics> allAccountStatistics =
            [.. await historyRepo.GetAccountStatisticsByIdsAsync(playerAccountIDs, cancellationToken)];

        List<Account> playerAccounts =
            [.. await accountRepo.GetAccountsByIdsAsync(playerAccountIDs, cancellationToken)];

        Role dummyRole = new() { ID = 1, Name = "User" };

        foreach (PlayerStatistics stats in allPlayerStatistics)
        {
            Account playerAccount = playerAccounts.FirstOrDefault(a => a.ID == stats.AccountID) ?? new Account
            {
                ID = stats.AccountID,
                Name = stats.AccountName,
                IsMain = true,
                User = new User
                {
                    ID = stats.AccountID,
                    EmailAddress = $"{stats.AccountName.ToLowerInvariant()}@kongor.net",
                    Role = dummyRole,
                    SRPPasswordSalt = "00",
                    SRPPasswordHash = "00"
                }
            };

            AccountStatistics? accountStats =
                allAccountStatistics.FirstOrDefault(s => s.AccountID == stats.AccountID);

            AccountStatistics publicStats =
                allAccountStatistics.FirstOrDefault(s =>
                    s.AccountID == stats.AccountID && s.Type == AccountStatisticsType.Public)
                ?? accountStats ?? new AccountStatistics
                    { Account = playerAccount, Type = AccountStatisticsType.Public };
            AccountStatistics mmStats = allAccountStatistics.FirstOrDefault(s =>
                                            s.AccountID == stats.AccountID &&
                                            s.Type == AccountStatisticsType.Matchmaking)
                                        ?? accountStats ?? new AccountStatistics
                                            { Account = playerAccount, Type = AccountStatisticsType.Matchmaking };
            AccountStatistics currentStats = mmStats;

            string rawHeroIdentifier = !string.IsNullOrWhiteSpace(stats.HeroIdentifier)
                ? stats.HeroIdentifier
                : stats.HeroProductID > 0
                    ? HeroDefinitionService.GetHeroIdentifier(stats.HeroProductID.Value)
                    : "Hero_Legionnaire";
            string baseHeroIdentifier = HeroDefinitionService.CleanHeroIdentifier(rawHeroIdentifier);
            int resolvedHeroProductId = stats.HeroProductID > 0
                ? (int)stats.HeroProductID.Value
                : (int)HeroDefinitionService.GetBaseHeroId(baseHeroIdentifier);

            string altAvatarName = !string.IsNullOrWhiteSpace(stats.AlternativeAvatarName)
                ? stats.AlternativeAvatarName
                : rawHeroIdentifier.Contains('.')
                    ? rawHeroIdentifier
                    : "";

            if (string.IsNullOrWhiteSpace(altAvatarName) && playerAccount.User?.InventoryItems != null)
            {
                string? ownedAlt = playerAccount.User.InventoryItems
                    .Where(item => item.ItemCode.StartsWith($"aa.{baseHeroIdentifier}.", StringComparison.OrdinalIgnoreCase) ||
                                   item.ItemCode.StartsWith($"{baseHeroIdentifier}.", StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(item => item.ItemCode)
                    .Select(item => item.ItemCode)
                    .FirstOrDefault();

                if (!string.IsNullOrWhiteSpace(ownedAlt))
                {
                    altAvatarName = ownedAlt;
                }
            }

            if (altAvatarName.StartsWith("aa.", StringComparison.OrdinalIgnoreCase))
                altAvatarName = altAvatarName[3..];

            List<string> inv = stats.Inventory ?? [];
            string encodedPayload = $"{baseHeroIdentifier}:{altAvatarName}|{string.Join(",", inv)}";

            bool playerWon = stats.Win == 1;
            bool playerMvp = stats.MVP == 1;
            int durationSec = matchStatistics.TimePlayed;
            int playerMatchExp = Math.Max(20, (int)(durationSec / 60.0 * (playerWon ? 4.0 : 2.0))) + (playerMvp ? 25 : 0);
            int playerBonusExp = Math.Max(5, (int)(playerMatchExp * 0.1));
            int playerMaxLevelCount = playerAccount.Mastery?.HeroesAtMaximumMasteryCount() ?? 0;
            int playerMaxLevelAddon = playerMaxLevelCount * 20;
            int playerTotalEarnedExp = playerMatchExp + playerBonusExp + playerMaxLevelAddon;
            int playerCumulativeExp = playerAccount.Mastery?.GetHeroExperienceByHeroIdentifier(baseHeroIdentifier) ?? 0;
            int playerOriginalExp = playerCumulativeExp >= playerTotalEarnedExp
                ? playerCumulativeExp - playerTotalEarnedExp
                : playerCumulativeExp;

            matchPlayerStatistics[stats.AccountID] = new MatchPlayerStatistics(matchStartData, playerAccount, stats,
                currentStats, publicStats, mmStats)
            {
                HeroProductID = resolvedHeroProductId.ToString(CultureInfo.InvariantCulture),
                HeroIdentifier = baseHeroIdentifier,
                AlternativeAvatarName = encodedPayload,
                MasteryExperienceOriginal = playerOriginalExp,
                MasteryExperienceMatch = playerMatchExp,
                MasteryExperienceBonus = playerBonusExp,
                MasteryMaxLevelHeroCount = playerMaxLevelCount,
                MasteryMaxLevelAddon = playerMaxLevelAddon,
                MasteryExperienceToBoost = playerTotalEarnedExp * 2
            };

            matchPlayerInventories[stats.AccountID.ToString()] = new MatchPlayerInventory
            {
                AccountID = stats.AccountID,
                MatchID = stats.MatchID,
                Slot1 = inv.Count > 0 ? inv[0] : "",
                Slot2 = inv.Count > 1 ? inv[1] : "",
                Slot3 = inv.Count > 2 ? inv[2] : "",
                Slot4 = inv.Count > 3 ? inv[3] : "",
                Slot5 = inv.Count > 4 ? inv[4] : "",
                Slot6 = inv.Count > 5 ? inv[5] : ""
            };
        }

        return new MatchCoreData(
            matchStatistics,
            allPlayerStatistics,
            matchStartData,
            matchPlayerStatistics,
            matchPlayerInventories);
    }

    private MatchStatsResponse BuildMatchStatsResponse(MatchCoreData core, Account? account, bool unlockAllItems = true)
    {
        MatchSummary matchSummary = new(core.MatchStatistics, core.AllPlayerStatistics, core.MatchStartData)
        {
            SelectedUpgrades = account != null ? string.Join("|", account.SelectedStoreItemCodes) : ""
        };

        PlayerStatistics? targetPlayer = null;
        if (account != null) targetPlayer = core.AllPlayerStatistics.FirstOrDefault(s => s.AccountID == account.ID);

        targetPlayer ??= core.AllPlayerStatistics.FirstOrDefault();

        string rawTargetHeroIdentifier = !string.IsNullOrWhiteSpace(targetPlayer?.HeroIdentifier)
            ? targetPlayer.HeroIdentifier
            : targetPlayer?.HeroProductID > 0
                ? HeroDefinitionService.GetHeroIdentifier(targetPlayer.HeroProductID.Value)
                : "Hero_Legionnaire";
        string heroIdentifier = HeroDefinitionService.CleanHeroIdentifier(rawTargetHeroIdentifier);
        int heroProductID = targetPlayer?.HeroProductID > 0
            ? (int)targetPlayer.HeroProductID.Value
            : (int)HeroDefinitionService.GetBaseHeroId(heroIdentifier);

        // Fetch Real Mastery Data from DB if available
        int currentExp = 0;
        int maxLevelHeroesCount = 0;

        if (account?.Mastery != null)
        {
            currentExp = account.Mastery.GetHeroExperienceByHeroIdentifier(heroIdentifier);
            maxLevelHeroesCount = account.Mastery.HeroesAtMaximumMasteryCount();
        }
        else if (targetPlayer != null && core.MatchPlayerStatistics.TryGetValue(targetPlayer.AccountID, out MatchPlayerStatistics? targetStats))
        {
            currentExp = targetStats.MasteryExperienceOriginal;
            maxLevelHeroesCount = targetStats.MasteryMaxLevelHeroCount;
        }

        // Compute Base Match Mastery XP based on actual game duration and outcome
        bool isWin = targetPlayer?.Win == 1;
        bool isMvp = targetPlayer?.MVP == 1;
        int durationSec = core.MatchStatistics.TimePlayed;

        // Base exp: ~4 exp per min for win, ~2 exp per min for loss + MVP bonus
        int baseExp = Math.Max(20, (int)(durationSec / 60.0 * (isWin ? 4.0 : 2.0)));
        if (isMvp) baseExp += 25;

        // Map/event bonuses
        int bonusExp = Math.Max(5, (int)(baseExp * 0.1)); // 10% bonus base

        int boostCount = MasteryConsumables.GetTotalMasteryBoostsOwned(account?.User, isSuper: false);
        int superBoostCount = MasteryConsumables.GetTotalMasteryBoostsOwned(account?.User, isSuper: true);
        bool matchAlreadyBoosted = MasteryConsumables.HasClaimedMatchBoost(account?.User, core.MatchStatistics.MatchID);

        string masteryAltAvatar = !string.IsNullOrWhiteSpace(targetPlayer?.AlternativeAvatarName)
            ? targetPlayer.AlternativeAvatarName
            : rawTargetHeroIdentifier.Contains('.')
                ? rawTargetHeroIdentifier
                : "";

        if (string.IsNullOrWhiteSpace(masteryAltAvatar) && account?.User?.InventoryItems != null)
        {
            string? ownedAlt = account.User.InventoryItems
                .Where(item => item.ItemCode.StartsWith($"aa.{heroIdentifier}.", StringComparison.OrdinalIgnoreCase) ||
                               item.ItemCode.StartsWith($"{heroIdentifier}.", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(item => item.ItemCode)
                .Select(item => item.ItemCode)
                .FirstOrDefault();

            if (!string.IsNullOrWhiteSpace(ownedAlt))
            {
                masteryAltAvatar = ownedAlt;
            }
        }

        if (masteryAltAvatar.StartsWith("aa.", StringComparison.OrdinalIgnoreCase))
            masteryAltAvatar = masteryAltAvatar[3..];

        int maxHeroesBonus = maxLevelHeroesCount * 20;
        int totalEarnedMasteryExp = baseExp + bonusExp + maxHeroesBonus;
        int originalMasteryExp = currentExp >= totalEarnedMasteryExp
            ? currentExp - totalEarnedMasteryExp
            : currentExp;

        MatchMastery matchMastery = new()
        {
            HeroIdentifier = heroIdentifier,
            AlternativeAvatarName = masteryAltAvatar,
            HeroProductID = heroProductID,
            CurrentMasteryExperience = originalMasteryExp,
            MatchMasteryExperience = baseExp,
            MasteryExperienceBonus = bonusExp,
            MasteryExperienceBoost = 0,
            MasteryExperienceSuperBoost = 0,
            MasteryExperienceMaximumLevelHeroesCount = maxLevelHeroesCount,
            MasteryExperienceHeroesBonus = maxHeroesBonus,
            MasteryExperienceToBoost = totalEarnedMasteryExp * 2,
            MasteryExperienceEventBonus = 0,
            MasteryExperienceCanBoost = !matchAlreadyBoosted && boostCount > 0,
            MasteryExperienceCanSuperBoost = !matchAlreadyBoosted && superBoostCount > 0,
            MasteryExperienceBoostProductIdentifier = 3609,
            MasteryExperienceSuperBoostProductIdentifier = 4605,
            MasteryExperienceBoostGoldCoin = 40,
            MasteryExperienceBoostSilverCoin = 200,
            MasteryExperienceBoostProductCount = boostCount,
            MasteryExperienceSuperBoostProductCount = superBoostCount,
            MasteryMatchID = core.MatchStatistics.MatchID,
            MasteryMaxLevelAddon = maxHeroesBonus
        };

        Dictionary<int, MatchPlayerStatistics> refreshedMatchPlayerStats = new(core.MatchPlayerStatistics);
        if (account != null &&
            refreshedMatchPlayerStats.TryGetValue(account.ID, out MatchPlayerStatistics? requesterPlayerStats))
        {
            refreshedMatchPlayerStats[account.ID] =
                requesterPlayerStats.CloneWithMasteryBoostState(account, core.MatchStatistics.MatchID);
        }

        return new MatchStatsResponse
        {
            GoldCoins = (account?.User?.GoldCoins ?? 0).ToString(CultureInfo.InvariantCulture),
            SilverCoins = (account?.User?.SilverCoins ?? 0).ToString(CultureInfo.InvariantCulture),
            MatchSummary = new Dictionary<int, MatchSummary> { [core.MatchStatistics.MatchID] = matchSummary },
            MatchPlayerStatistics =
                new Dictionary<int, Dictionary<int, MatchPlayerStatistics>>
                {
                    [core.MatchStatistics.MatchID] = refreshedMatchPlayerStats
                },
            MatchPlayerInventories =
                new Dictionary<int, Dictionary<string, MatchPlayerInventory>>
                {
                    [0] = core.MatchPlayerInventories,
                    [core.MatchStatistics.MatchID] = core.MatchPlayerInventories
                },
            MatchMastery = matchMastery,
            DiceTokens = 999999,
            GameTokens = 999999,
            SeasonLevel = 100,
            CreepLevel = 100,
            OwnedStoreItems = UpgradesResolutionService.ExpandOwnedItems(
                account?.User?.InventoryItems?.Select(i => i.ItemCode),
                unlockAllItems: unlockAllItems).ToList(),
            OwnedStoreItemsData = [],
            SelectedStoreItems = account?.SelectedStoreItemCodes ?? [],
            CustomIconSlotID = "0",
            ServerTimestamp =
                Convert.ToInt32(Math.Min(TimeProvider.System.GetUtcNow().ToUnixTimeSeconds(),
                    Convert.ToInt64(int.MaxValue))),
            QuestSystem = new Dictionary<string, QuestSystem> { ["error"] = new() },
            SeasonSystem = new SeasonSystem(),
            CampaignReward = new CampaignReward()
        };
    }

    private static MatchStatsResponse CreateSafeResponse(Account? account, HashSet<string> safeOwned)
    {
        return new MatchStatsResponse
        {
            GoldCoins = (account?.User?.GoldCoins ?? 0).ToString(CultureInfo.InvariantCulture),
            SilverCoins = (account?.User?.SilverCoins ?? 0).ToString(CultureInfo.InvariantCulture),
            MatchSummary = [],
            MatchPlayerStatistics = [],
            MatchPlayerInventories = [],
            MatchMastery = new MatchMastery
            {
                HeroIdentifier = "Hero_Legionnaire",
                HeroProductID = 1,
                CurrentMasteryExperience = 0,
                MatchMasteryExperience = 0,
                MasteryExperienceBonus = 0,
                MasteryExperienceBoost = 0,
                MasteryExperienceSuperBoost = 0,
                MasteryExperienceMaximumLevelHeroesCount = 0,
                MasteryExperienceHeroesBonus = 0,
                MasteryExperienceToBoost = 0,
                MasteryExperienceBoostProductCount = 0,
                MasteryExperienceSuperBoostProductCount = 0
            },
            OwnedStoreItems = safeOwned.ToList(),
            SelectedStoreItems = account?.SelectedStoreItemCodes ?? [],
            OwnedStoreItemsData = [],
            CustomIconSlotID = "0",
            CampaignReward = new CampaignReward()
        };
    }

    [LoggerMessage(Level = LogLevel.Information,
        Message = "[MatchStats] Request Received for: MatchID: {MatchID}, Cookie: {CookiePrefix}")]
    private partial void LogRequestReceived(string matchId, string cookiePrefix);

    [LoggerMessage(Level = LogLevel.Warning, Message = "[MatchStats] Missing Form Parameter: match_id")]
    private partial void LogMissingMatchId();

    [LoggerMessage(Level = LogLevel.Warning, Message = "[MatchStats] Invalid Match ID: {MatchID}")]
    private partial void LogInvalidMatchId(string matchId);

    [LoggerMessage(Level = LogLevel.Information, Message = "[MatchStats] Fetching Statistics for Match ID: {MatchID}")]
    private partial void LogFetchingStatistics(int matchId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "[MatchStats] Statistics Not Found For Match ID: {MatchID}")]
    private partial void LogStatsNotFound(int matchId);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "[MatchStats] Fetching Player Statistics for Match ID: {MatchID}")]
    private partial void LogFetchingPlayerStats(int matchId);

    [LoggerMessage(Level = LogLevel.Information, Message = "[MatchStats] Fetching Requester Account: {AccountName}")]
    private partial void LogFetchingRequester(string accountName);

    [LoggerMessage(Level = LogLevel.Information, Message = "[MatchStats] Fetching Match Start Data")]
    private partial void LogFetchingMatchStartData();

    [LoggerMessage(Level = LogLevel.Information,
        Message = "[MatchStats] Fetching Bulk Account Statistics for {PlayerCount} Players")]
    private partial void LogFetchingBulkStats(int playerCount);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "[MatchStats] Returning Match Statistics for Match ID: {MatchID}")]
    private partial void LogReturningStats(int matchId);
}
