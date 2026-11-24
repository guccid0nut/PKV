// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

namespace KONGOR.MasterServer.Services.Mastery;

public interface IMasteryRewardService
{
    List<HeroMasteryInfo> GenerateMasteryInfo(List<FavHeroDTO> heroes, IHeroDefinitionService heroDefinitions);

    string GenerateMasteryInfoString(Account? account, IHeroDefinitionService heroDefinitions,
        List<FavHeroDTO>? topHeroes = null);

    List<MasteryRewardTier> GenerateMasteryRewards(Account? account = null);
    string GenerateMasteryRewardsString(Account? account = null);
    List<MasteryRewardTier> GenerateMasteryRewardTiers(Account account);

    int CalculateMatchMasteryExperience(Account? account, string heroIdentifier, int heroLevel, bool isWin,
        bool isMvp, int durationSeconds = 1800,
        AccountStatisticsType matchType = AccountStatisticsType.Matchmaking);
}
