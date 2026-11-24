// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using KONGOR.MasterServer.Services.Economy;
using KONGOR.MasterServer.Services.Mastery;
using MERRICK.DatabaseContext.Entities.Core;
using MERRICK.DatabaseContext.Entities.Statistics;
using MERRICK.DatabaseContext.Persistence;
using Microsoft.Extensions.Logging;
using NEXUS.Contracts.Domain;
using NEXUS.Contracts.Economy;
using NEXUS.Contracts.Services;
using MasteryEntity = MERRICK.DatabaseContext.Entities.Statistics.Mastery;

namespace KONGOR.MasterServer.Services.Matches;

public interface IHeroMasteryProgressionService
{
    Task ProcessProgressionAndRewardsAsync(
        IngestedMatchContext context,
        CancellationToken cancellationToken = default);
}

public sealed class HeroMasteryProgressionService(
    MerrickContext merrickContext,
    IEconomyService economyService,
    IMasteryRewardService masteryRewardService,
    ILogger<HeroMasteryProgressionService> logger) : IHeroMasteryProgressionService
{
    public async Task ProcessProgressionAndRewardsAsync(
        IngestedMatchContext context,
        CancellationToken cancellationToken = default)
    {
        foreach (var entry in context.ProcessedPlayerEntries)
        {
            var account = entry.Account;
            var playerStatistics = entry.PlayerStats;
            int groupSize = entry.GroupSize;
            bool isWin = entry.IsWin;
            bool isMvp = entry.IsMvp;

            // 1. Delegate Match Rewards & Ledger recording to IEconomyService
            int goldEarned = 0;
            int silverEarned = 0;
            int plinkoEarned = 0;

            bool isCustomNonTmmShortLobby =
                context.MatchType is AccountStatisticsType.Public or AccountStatisticsType.Cooperative &&
                context.DurationSeconds < 300;

            if (!context.IsUnrankedListenServer && !isCustomNonTmmShortLobby && account.User is not null && account.Type != AccountType.Guest)
            {
                MatchRewardRequestDTO rewardRequest = new(
                    account.User.ID,
                    account.ID,
                    account.Name,
                    context.MatchID,
                    groupSize,
                    isWin,
                    isMvp,
                    context.DurationSeconds);

                MatchRewardResultDTO rewardResult = await economyService.AwardMatchRewardsAsync(
                    rewardRequest,
                    cancellationToken);

                if (rewardResult.Success)
                {
                    goldEarned = rewardResult.CurrenciesEarned.GoldDelta;
                    silverEarned = rewardResult.CurrenciesEarned.SilverDelta;
                    plinkoEarned = rewardResult.CurrenciesEarned.PlinkoDelta;
                }
                else if (logger.IsEnabled(LogLevel.Warning))
                {
                    logger.LogWarning(
                        "[HeroMasteryProgressionService] Failed to award match rewards to User #{UserId} (Account '{AccountName}'): {Error}",
                        account.User.ID, account.Name, rewardResult.ErrorMessage);
                }
            }
            else if (context.IsUnrankedListenServer && logger.IsEnabled(LogLevel.Information))
            {
                logger.LogInformation(
                    "[HeroMasteryProgressionService] Skipping currency/ticket rewards for Account '{AccountName}' (Match #{MatchId} is an Unranked P2P Listen Server match).",
                    account.Name, context.MatchID);
            }

            // 2. Calculate Hero Mastery XP
            if (account.Mastery is null)
            {
                account.Mastery = new MasteryEntity
                {
                    Account = account,
                    AccountID = account.ID,
                    HeroExperiences = []
                };
                await merrickContext.Masteries.AddAsync(account.Mastery, cancellationToken);
            }

            string rawHero = !string.IsNullOrEmpty(playerStatistics.HeroIdentifier)
                ? playerStatistics.HeroIdentifier
                : entry.HeroKey;
            bool isCustomMapUnit =
                ASPIRE.Common.Constants.CustomHeroCatalog.IsCustomMapUnit(entry.HeroKey) ||
                ASPIRE.Common.Constants.CustomHeroCatalog.IsCustomMapUnit(rawHero);

            string heroIdentifier = HeroDefinitionService.CleanHeroIdentifier(rawHero);
            int heroLevel = playerStatistics.HeroLevel > 0 ? playerStatistics.HeroLevel : 1;
            int earnedMasteryXp = masteryRewardService.CalculateMatchMasteryExperience(
                account,
                heroIdentifier,
                heroLevel,
                isWin,
                isMvp,
                context.DurationSeconds,
                context.MatchType);

            if (!isCustomMapUnit)
            {
                int currentHeroXp = account.Mastery.GetHeroExperienceByHeroIdentifier(heroIdentifier);
                account.Mastery.SetHeroExperienceByHeroIdentifier(heroIdentifier, currentHeroXp + earnedMasteryXp);
            }
            else if (logger.IsEnabled(LogLevel.Debug))
            {
                logger.LogDebug(
                    "[HeroMasteryProgressionService] Skipping HeroMasteryExperience row for non-catalog custom map unit '{RawHero}' in Match #{MatchId}.",
                    rawHero, context.MatchID);
            }

            if (account.User is not null)
            {
                account.User.TotalExperience += earnedMasteryXp;
                account.User.TotalLevel = account.Mastery.TotalMasteryLevel();
            }

            if (logger.IsEnabled(LogLevel.Information))
            {
                if (isCustomMapUnit)
                {
                    logger.LogInformation(
                        "[HeroMasteryProgressionService] Credited rewards for {AccountName}: Gold +{Gold}, Silver +{Silver}, Plinko +{Plinko}, Custom Unit '{Hero}' Account XP +{MasteryXp} (Hero Mastery skipped)",
                        account.Name, goldEarned, silverEarned, plinkoEarned, heroIdentifier, earnedMasteryXp);
                }
                else
                {
                    logger.LogInformation(
                        "[HeroMasteryProgressionService] Credited rewards for {AccountName}: Gold +{Gold}, Silver +{Silver}, Plinko +{Plinko}, Hero '{Hero}' Mastery XP +{MasteryXp}",
                        account.Name, goldEarned, silverEarned, plinkoEarned, heroIdentifier, earnedMasteryXp);
                }
            }
        }
    }
}
