// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

namespace KONGOR.MasterServer.Services.Stats;

public interface IPlayerProfileStatsBuilder
{
    ShowSimpleStatsResponse CreateShowSimpleStatsResponse(
        Account account,
        PlayerStatisticsAggregatedDTO stats,
        int currentSeason,
        IHeroDefinitionService heroDefinitions);

    ShowSimpleStatsResponse CreateShowSimpleStatsResponse(
        Account account,
        PlayerStatisticsAggregatedDTO stats,
        int currentSeason,
        IHeroDefinitionService heroDefinitions,
        bool unlockAllItems);

    string SetCustomIconSlotID(Account account);

    Dictionary<string, OwnedStoreItemDTO> SetOwnedStoreItemsData(Account account);
}
