// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using KONGOR.MasterServer.Extensions;
using KONGOR.MasterServer.Services.Mastery;
using KONGOR.MasterServer.Services.Stats;

namespace KONGOR.MasterServer.Services.Requester;

public static class ClientRequestHelper
{
    public static string? GetCookie(HttpRequest request)
    {
        return request.GetCookie();
    }

    public static ShowSimpleStatsResponse CreateShowSimpleStatsResponse(
        Account account,
        PlayerStatisticsAggregatedDTO stats,
        int currentSeason,
        IHeroDefinitionService heroDefinitions,
        bool? unlockAllItems = null)
    {
        return unlockAllItems.HasValue
            ? PlayerProfileStatsBuilder.Instance.CreateShowSimpleStatsResponse(
                account, stats, currentSeason, heroDefinitions, unlockAllItems.Value)
            : PlayerProfileStatsBuilder.Instance.CreateShowSimpleStatsResponse(
                account, stats, currentSeason, heroDefinitions);
    }

    public static List<HeroMasteryInfo> GenerateMasteryInfo(
        List<FavHeroDTO> heroes,
        IHeroDefinitionService heroDefinitions)
    {
        return MasteryRewardService.Instance.GenerateMasteryInfo(heroes, heroDefinitions);
    }

    public static List<MasteryRewardTier> GenerateMasteryRewards(Account? account = null)
    {
        return MasteryRewardService.Instance.GenerateMasteryRewards(account);
    }

    public static List<MasteryRewardTier> GenerateMasteryRewardTiers(Account account)
    {
        return MasteryRewardService.Instance.GenerateMasteryRewardTiers(account);
    }

    public static string SetCustomIconSlotID(Account account)
    {
        return PlayerProfileStatsBuilder.Instance.SetCustomIconSlotID(account);
    }

    public static Dictionary<string, OwnedStoreItemDTO> SetOwnedStoreItemsData(Account account)
    {
        return PlayerProfileStatsBuilder.Instance.SetOwnedStoreItemsData(account);
    }

    public static object[] CreateLegacyPositionalPlayerStats(ShowSimpleStatsResponse props)
    {
        return LegacyProtocolFormatter.Instance.CreateLegacyPositionalPlayerStats(props);
    }

    public static Dictionary<object, object> CreateHybridSimpleStats(ShowSimpleStatsResponse props,
        string? table = null)
    {
        return LegacyProtocolFormatter.Instance.CreateHybridSimpleStats(props, table);
    }

    public static Dictionary<object, object> ToHashtable(SimpleSeasonStats stats, bool isCasual = false)
    {
        return LegacyProtocolFormatter.Instance.ToHashtable(stats, isCasual);
    }

    public static object ConvertToPhp(object? value)
    {
        return LegacyProtocolFormatter.Instance.ConvertToPhp(value);
    }
}
