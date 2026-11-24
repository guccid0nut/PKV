// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using KONGOR.MasterServer.Services.Store;
using NEXUS.Contracts.Services;
using MasteryEntity = MERRICK.DatabaseContext.Entities.Statistics.Mastery;

namespace KONGOR.MasterServer.Services.Mastery;

public class MasteryRewardService(IStoreCatalogService? catalogService = null) : IMasteryRewardService
{
    public static readonly IMasteryRewardService Instance = new MasteryRewardService();

    private readonly IStoreCatalogService _catalogService = catalogService ?? StoreCatalogService.Instance;

    public List<HeroMasteryInfo> GenerateMasteryInfo(List<FavHeroDTO> heroes, IHeroDefinitionService heroDefinitions)
    {
        return MasteryRewardCalculator.GenerateMasteryInfo(
            heroes,
            Heroes.AllHeroIdentifiers(),
            heroDefinitions.GetHeroIdentifier);
    }

    public string GenerateMasteryInfoString(Account? account, IHeroDefinitionService heroDefinitions,
        List<FavHeroDTO>? topHeroes = null)
    {
        Dictionary<string, int>? heroExperiences = null;
        if (account?.Mastery != null)
        {
            heroExperiences = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (string heroName in Heroes.AllHeroIdentifiers())
                heroExperiences[heroName] = account.Mastery.GetHeroExperienceByHeroIdentifier(heroName);
        }

        return MasteryRewardCalculator.GenerateMasteryInfoString(
            heroExperiences,
            Heroes.AllHeroIdentifiers(),
            heroDefinitions.GetHeroIdentifier,
            topHeroes);
    }

    public List<MasteryRewardTier> GenerateMasteryRewards(Account? account = null)
    {
        IReadOnlyList<MasteryRewardTierDTO> configTiers = _catalogService.GetMasteryRewardTiers();
        return MasteryRewardCalculator.GenerateMasteryRewards(configTiers, account?.User?.InventoryItems?.Select(i => i.ItemCode).ToList());
    }

    public string GenerateMasteryRewardsString(Account? account = null)
    {
        IReadOnlyList<MasteryRewardTierDTO> configTiers = _catalogService.GetMasteryRewardTiers();
        return MasteryRewardCalculator.GenerateMasteryRewardsString(configTiers, account?.User?.InventoryItems?.Select(i => i.ItemCode).ToList());
    }

    public List<MasteryRewardTier> GenerateMasteryRewardTiers(Account account)
    {
        return GenerateMasteryRewards(account);
    }

    public int CalculateMatchMasteryExperience(
        Account? account,
        string heroIdentifier,
        int heroLevel,
        bool isWin,
        bool isMvp,
        int durationSeconds = 1800,
        AccountStatisticsType matchType = AccountStatisticsType.Matchmaking)
    {
        int heroLevelClamped = Math.Clamp(heroLevel, 1, 25);
        int totalHeroes = Heroes.AllHeroIdentifiers().Count;
        if (totalHeroes <= 0) totalHeroes = 139;

        MasteryEntity mastery = account?.Mastery ?? new MasteryEntity
        {
            Account = account ?? new Account { Name = "temp", IsMain = true, User = null! },
            HeroExperiences = []
        };

        int baseExp = mastery.CalculateMatchAndBonusExperience(matchType, heroLevelClamped, totalHeroes);
        return MasteryRewardCalculator.CalculateMatchMasteryExperience(
            heroLevelClamped,
            isWin,
            isMvp,
            durationSeconds,
            totalHeroes,
            baseExp);
    }
}
