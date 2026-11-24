// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Globalization;
using KONGOR.MasterServer.Services.Mastery;
using KONGOR.MasterServer.Services.Store;
using MERRICK.DatabaseContext.Extensions;

namespace KONGOR.MasterServer.Services.Stats;

public class PlayerProfileStatsBuilder(
    IMasteryRewardService masteryRewardService,
    IConfiguration? configuration = null) : IPlayerProfileStatsBuilder
{
    public static readonly IPlayerProfileStatsBuilder Instance =
        new PlayerProfileStatsBuilder(MasteryRewardService.Instance);

    public ShowSimpleStatsResponse CreateShowSimpleStatsResponse(
        Account account,
        PlayerStatisticsAggregatedDTO stats,
        int currentSeason,
        IHeroDefinitionService heroDefinitions)
    {
        return CreateShowSimpleStatsResponse(
            account,
            stats,
            currentSeason,
            heroDefinitions,
            UpgradesResolutionService.GetUnlockAllSetting(configuration));
    }

    public ShowSimpleStatsResponse CreateShowSimpleStatsResponse(
        Account account,
        PlayerStatisticsAggregatedDTO stats,
        int currentSeason,
        IHeroDefinitionService heroDefinitions,
        bool unlockAllItems)
    {
        long totalSeconds = stats.RankedSeconds + stats.CasualSeconds;
        if (totalSeconds < 1) totalSeconds = 1;

        List<KeyValuePair<string, int>> top4 =
        [
            new("awd_msd", stats.Smackdowns),
            new("awd_mann", stats.Annihilations),
            new("awd_masst", stats.Assists),
            new("awd_mkill", stats.Kills)
        ];

        top4.Sort((pair1, pair2) => pair2.Value.CompareTo(pair1.Value));
        stats.TopHeroes.Sort((h1, h2) => h2.SecondsPlayed.CompareTo(h1.SecondsPlayed));

        long totalXp = account.User.TotalExperience > 0
            ? account.User.TotalExperience
            : stats.RankedExp + stats.CasualExp;
        int calculatedLevel = (int)Math.Floor(Math.Pow(totalXp / 100.0, 1.0 / 3.0));
        if (calculatedLevel < 1) calculatedLevel = 1;

        if (calculatedLevel > 99) calculatedLevel = 99;

        int avatarsOwned =
            account.User.InventoryItems?.Count(item => item.ItemCode.StartsWith("aa.", StringComparison.OrdinalIgnoreCase)) ?? 0;
        int heroesOwned =
            account.User.InventoryItems?.Count(item => item.ItemCode.StartsWith("h.", StringComparison.OrdinalIgnoreCase)) ?? 0;

        int totalMatchesForAvg = stats.RankedMatches > 0 ? stats.RankedMatches :
            stats.CasualMatches > 0 ? stats.CasualMatches : stats.TotalMatches;
        long totalSecondsForAvg = stats.RankedTimeEarningExp > 0 ? stats.RankedTimeEarningExp :
            stats.CasualTimeEarningExp > 0 ? stats.CasualTimeEarningExp :
            stats.RankedSeconds + stats.CasualSeconds + stats.PublicSeconds;
        long totalExpForAvg = stats.RankedExp > 0 ? stats.RankedExp :
            stats.CasualExp > 0 ? stats.CasualExp : stats.PublicExp;
        long totalKillsForAvg = stats.RankedKills > 0 ? stats.RankedKills :
            stats.CasualKills > 0 ? stats.CasualKills : stats.Kills;
        long totalDeathsForAvg = stats.RankedDeaths > 0 ? stats.RankedDeaths :
            stats.CasualDeaths > 0 ? stats.CasualDeaths : stats.Deaths;
        long totalAssistsForAvg = stats.RankedAssists > 0 ? stats.RankedAssists :
            stats.CasualAssists > 0 ? stats.CasualAssists : stats.Assists;
        int totalDeniesForAvg = stats.RankedDenies > 0 ? stats.RankedDenies :
            stats.CasualDenies > 0 ? stats.CasualDenies : stats.PublicDenies;
        int totalCreepsForAvg = stats.RankedTeamCreepKills > 0 ? stats.RankedTeamCreepKills :
            stats.CasualTeamCreepKills > 0 ? stats.CasualTeamCreepKills : stats.PublicTeamCreepKills;
        int totalNeutralsForAvg = stats.RankedNeutralCreepKills > 0 ? stats.RankedNeutralCreepKills :
            stats.CasualNeutralCreepKills > 0 ? stats.CasualNeutralCreepKills : stats.PublicNeutralCreepKills;
        int totalActionsForAvg = stats.RankedActions > 0 ? stats.RankedActions :
            stats.CasualActions > 0 ? stats.CasualActions : stats.PublicActions;
        int totalWardsForAvg = stats.RankedWards > 0 ? stats.RankedWards :
            stats.CasualWards > 0 ? stats.CasualWards : stats.PublicWards;

        ShowSimpleStatsResponse response = new()
        {
            NameWithClanTag = account.GetNameWithClanTag(),
            ID = account.ID,
            Level = calculatedLevel,
            LevelExperience = (int)totalXp,
            NumberOfAvatarsOwned = avatarsOwned,
            NumberOfHeroesOwned = heroesOwned,
            TotalMatchesPlayed = stats.TotalMatches,
            TotalGamesPlayedLegacy = stats.TotalMatches,
            AccountCreationDate = account.User.TimestampCreated.ToString("MM/dd/yyyy"),
            LastActivityDate =
                stats.LastMatchDate?.ToString("MM/dd/yyyy") ?? account.User.TimestampLastActive.ToString("MM/dd/yyyy"),
            TotalDisconnects = stats.Disconnected,
            CurrentGamesPlayed = stats.RankedMatches > 0 ? stats.RankedMatches : stats.TotalMatches,
            CurrentDisconnects = stats.Disconnected,
            RankedWins = stats.RankedWins,
            RankedLosses = stats.RankedLosses,
            FavHero1 = null!,
            FavHero1Time = 0,
            FavHero2 = null!,
            FavHero2Time = 0,
            FavHero3 = null!,
            FavHero3Time = 0,
            FavHero4 = null!,
            FavHero4Time = 0,
            FavHero5 = null!,
            FavHero5Time = 0,
            FavHero1_2 = null!,
            FavHero2_2 = null!,
            FavHero3_2 = null!,
            FavHero4_2 = null!,
            FavHero5_2 = null!,
            CurrentSeason = currentSeason,
            SeasonLevel = 0,
            CreepLevel = 0,
            SimpleSeasonStats =
                new SimpleSeasonStats
                {
                    RankedMatchesWon = stats.RankedWins,
                    RankedMatchesLost = stats.RankedLosses,
                    WinStreak = 0,
                    InPlacementPhase = 0,
                    RankedRating = (1500.0 + stats.RankedRatingChange).ToString(CultureInfo.InvariantCulture),
                    CurrentRank = ((int)RankExtensions.GetRank(1500.0 + stats.RankedRatingChange)).ToString(),
                    HighestRank = ((int)RankExtensions.GetRank(1500.0 + stats.RankedRatingChange)).ToString(),
                    GenericStats = stats
                },
            SimpleCasualSeasonStats =
                new SimpleSeasonStats
                {
                    RankedMatchesWon = stats.CasualWins,
                    RankedMatchesLost = stats.CasualLosses,
                    WinStreak = 0,
                    InPlacementPhase = 0,
                    RankedRating = (1500.0 + stats.CasualRatingChange).ToString(CultureInfo.InvariantCulture),
                    CurrentRank = ((int)RankExtensions.GetRank(1500.0 + stats.CasualRatingChange)).ToString(),
                    HighestRank = ((int)RankExtensions.GetRank(1500.0 + stats.CasualRatingChange)).ToString(),
                    GenericStats = stats
                },
            SimpleMidWarsSeasonStats =
                new SimpleSeasonStats
                {
                    RankedMatchesWon = stats.CasualWins,
                    RankedMatchesLost = stats.CasualLosses,
                    WinStreak = 0,
                    InPlacementPhase = 0,
                    RankedRating = (1500.0 + stats.CasualRatingChange).ToString(CultureInfo.InvariantCulture),
                    CurrentRank = ((int)RankExtensions.GetRank(1500.0 + stats.CasualRatingChange)).ToString(),
                    HighestRank = ((int)RankExtensions.GetRank(1500.0 + stats.CasualRatingChange)).ToString(),
                    GenericStats = stats
                },
            MVPAwardsCount = stats.MVP.ToString(),
            Top4AwardNames = [.. top4.Select(x => x.Key)],
            Top4AwardCounts = [.. top4.Select(x => x.Value.ToString())],
            CustomIconSlotID = GetCustomIconSlotID(account),
            OwnedStoreItems = UpgradesResolutionService.ExpandOwnedItems(
                account.User.InventoryItems?.Select(i => i.ItemCode),
                unlockAllItems: unlockAllItems).ToList(),
            SelectedStoreItems = GetResolvedSelectedStoreItems(account),
            OwnedStoreItemsData = SetOwnedStoreItemsData(account),
            DiceTokens = "999999",
            GameTokens = 999999,
            ServerTimestamp = TimeProvider.System.GetUtcNow().ToUnixTimeSeconds(),
            HeroKills = totalKillsForAvg.ToString(),
            Deaths = totalDeathsForAvg.ToString(),
            HeroAssists = totalAssistsForAvg.ToString(),
            KDA = $"{totalKillsForAvg}/{totalDeathsForAvg}/{totalAssistsForAvg}",
            AvgGameLength =
                totalMatchesForAvg > 0
                    ? (totalSecondsForAvg / (float)totalMatchesForAvg).ToString("0.0",
                        CultureInfo.InvariantCulture)
                    : "0",
            AvgXPMin =
                totalSecondsForAvg > 0
                    ? (totalExpForAvg / (totalSecondsForAvg / 60.0f)).ToString("0.0",
                        CultureInfo.InvariantCulture)
                    : "0",
            AvgDenies =
                totalMatchesForAvg > 0
                    ? (totalDeniesForAvg / (float)totalMatchesForAvg).ToString("0.0", CultureInfo.InvariantCulture)
                    : "0",
            AvgCreepKills =
                totalMatchesForAvg > 0
                    ? (totalCreepsForAvg / (float)totalMatchesForAvg).ToString("0.0", CultureInfo.InvariantCulture)
                    : "0",
            AvgNeutralKills =
                totalMatchesForAvg > 0
                    ? (totalNeutralsForAvg / (float)totalMatchesForAvg).ToString("0.0", CultureInfo.InvariantCulture)
                    : "0",
            AvgActionsMin =
                totalSecondsForAvg > 0
                    ? (totalActionsForAvg / (totalSecondsForAvg / 60.0f)).ToString("0.0",
                        CultureInfo.InvariantCulture)
                    : "0",
            AvgWardsUsed =
                totalMatchesForAvg > 0
                    ? (totalWardsForAvg / (float)totalMatchesForAvg).ToString("0.0", CultureInfo.InvariantCulture)
                    : "0",
            Gold = (stats.RankedHeroGold > 0 ? stats.RankedHeroGold : stats.CasualHeroGold > 0 ? stats.CasualHeroGold : stats.PublicHeroGold).ToString(),
            Secs = (totalSecondsForAvg > 0 ? totalSecondsForAvg : 1).ToString(CultureInfo.InvariantCulture),
            Exp = totalExpForAvg.ToString(CultureInfo.InvariantCulture),
            Humiliation = (stats.RankedHumiliations > 0 ? stats.RankedHumiliations : stats.CasualHumiliations > 0 ? stats.CasualHumiliations : stats.PublicHumiliations).ToString(),
            Smackdown = (stats.RankedSmackdowns > 0 ? stats.RankedSmackdowns : stats.CasualSmackdowns > 0 ? stats.CasualSmackdowns : stats.PublicSmackdowns).ToString(),
            Nemesis = (stats.RankedNemesis > 0 ? stats.RankedNemesis : stats.CasualNemesis > 0 ? stats.CasualNemesis : stats.PublicNemesis).ToString(),
            Retribution = (stats.RankedRetribution > 0 ? stats.RankedRetribution : stats.CasualRetribution > 0 ? stats.CasualRetribution : stats.PublicRetribution).ToString(),
            Rank = ((int)RankExtensions.GetRank(1500.0 + stats.RankedRatingChange)).ToString(),
            CurrentRankTop = ((int)RankExtensions.GetRank(1500.0 + stats.RankedRatingChange)).ToString(),
            RankedRatingTop = (1500.0 + stats.RankedRatingChange).ToString(CultureInfo.InvariantCulture),
            HighestRankTop = ((int)RankExtensions.GetRank(1500.0 + stats.RankedRatingChange)).ToString(),
            MasteryInfo = masteryRewardService.GenerateMasteryInfoString(account, heroDefinitions, stats.TopHeroes),
            MasteryRewards = masteryRewardService.GenerateMasteryRewardsString(account)
        };

        for (int i = 0; i < 5; i++)
            if (stats.TopHeroes.Count > i)
            {
                FavHeroDTO hero = stats.TopHeroes[i];

                uint baseHeroId = heroDefinitions.GetBaseHeroId(hero.HeroId);
                if (baseHeroId == 0) baseHeroId = hero.HeroId;

                string identifier = heroDefinitions.GetHeroIdentifier(baseHeroId);
                if (string.IsNullOrEmpty(identifier) ||
                    (identifier == "Hero_Legionnaire" && hero.HeroId != 0 && hero.HeroId != 1))
                {
                    string prodIdent = heroDefinitions.GetHeroIdentifier(hero.HeroId);
                    if (!string.IsNullOrEmpty(prodIdent)) identifier = prodIdent;
                }

                identifier = HeroDefinitionService.CleanHeroIdentifier(identifier);
                string formattedName = identifier.StartsWith("Hero_", StringComparison.OrdinalIgnoreCase)
                    ? identifier[5..].ToLowerInvariant()
                    : identifier.ToLowerInvariant();
                int timePercent = totalSeconds > 0
                    ? (int)Math.Round((double)hero.SecondsPlayed / totalSeconds * 100)
                    : 0;

                switch (i)
                {
                    case 0:
                        response.FavHero1 = formattedName;
                        response.FavHero1_2 = identifier;
                        response.FavHero1Time = timePercent;
                        break;
                    case 1:
                        response.FavHero2 = formattedName;
                        response.FavHero2_2 = identifier;
                        response.FavHero2Time = timePercent;
                        break;
                    case 2:
                        response.FavHero3 = formattedName;
                        response.FavHero3_2 = identifier;
                        response.FavHero3Time = timePercent;
                        break;
                    case 3:
                        response.FavHero4 = formattedName;
                        response.FavHero4_2 = identifier;
                        response.FavHero4Time = timePercent;
                        break;
                    case 4:
                        response.FavHero5 = formattedName;
                        response.FavHero5_2 = identifier;
                        response.FavHero5Time = timePercent;
                        break;
                }
            }

        return response;
    }

    public string SetCustomIconSlotID(Account account)
    {
        return GetCustomIconSlotID(account);
    }

    public Dictionary<string, OwnedStoreItemDTO> SetOwnedStoreItemsData(Account account)
    {
        Dictionary<string, OwnedStoreItemDTO> data = [];
        long now = TimeProvider.System.GetUtcNow().ToUnixTimeSeconds();
        long forever = now + 999999999;
        string nowStr = now.ToString(CultureInfo.InvariantCulture);
        string foreverStr = forever.ToString(CultureInfo.InvariantCulture);

        HashSet<string> expandedOwned = new(StringComparer.OrdinalIgnoreCase);
        if (account.User.InventoryItems != null)
        {
            foreach (UserInventory inv in account.User.InventoryItems)
            {
                string item = inv.ItemCode;
                if (string.IsNullOrWhiteSpace(item)) continue;

                expandedOwned.Add(item);

                if (item.StartsWith("Hero_", StringComparison.OrdinalIgnoreCase) && item.Contains('.'))
                    expandedOwned.Add("aa." + item);
                else if (item.StartsWith("aa.", StringComparison.OrdinalIgnoreCase)) expandedOwned.Add(item[3..]);
            }
        }

        foreach (string item in expandedOwned)
            data[item] = new OwnedStoreItemDTO
            {
                Data = string.Empty,
                StartTime = nowStr,
                EndTime = foreverStr,
                Used = 0,
                Score = "0",
                ExpirationDate = "0",
                Permanent = "1"
            };

        return data;
    }

    private static string GetCustomIconSlotID(Account account)
    {
        string? explicitCustomIcon = account.SelectedStoreItemCodes
            .FirstOrDefault(item => item.StartsWith("ai.custom_icon", StringComparison.OrdinalIgnoreCase));

        if (explicitCustomIcon is not null)
        {
            string slot =
                explicitCustomIcon.Replace("ai.custom_icon:", string.Empty, StringComparison.OrdinalIgnoreCase);
            return string.IsNullOrWhiteSpace(slot) ? "1" : slot;
        }

        // If the user selected an explicit non-custom store icon (e.g. ai.beer, ai.hellbourne), return "0"
        bool hasOtherStoreIcon = account.SelectedStoreItemCodes
            .Any(item => item.StartsWith("ai.", StringComparison.OrdinalIgnoreCase) &&
                         !item.Equals("ai.Default Icon", StringComparison.OrdinalIgnoreCase));

        if (hasOtherStoreIcon) return "0";

        // If user has a Discord account or Discord avatar, or owns custom_icon:1, enable custom icon slot 1
        if (!string.IsNullOrWhiteSpace(account.User?.DiscordAvatar) ||
            !string.IsNullOrWhiteSpace(account.User?.DiscordId) ||
            (account.User?.InventoryItems?.Any(item =>
                item.ItemCode.StartsWith("ai.custom_icon", StringComparison.OrdinalIgnoreCase)) ?? false))
            return "1";

        return "0";
    }

    private static List<string> GetResolvedSelectedStoreItems(Account account)
    {
        List<string> items = [.. account.SelectedStoreItemCodes];
        for (int i = 0; i < items.Count; i++)
        {
            if (items[i].Equals("ai.custom_icon", StringComparison.OrdinalIgnoreCase) ||
                items[i].Equals("custom_icon", StringComparison.OrdinalIgnoreCase))
            {
                items[i] = "ai.custom_icon:1";
            }
        }

        bool hasExplicitIcon = items.Any(item =>
            item.StartsWith("ai.", StringComparison.OrdinalIgnoreCase) &&
            !item.Equals("ai.Default Icon", StringComparison.OrdinalIgnoreCase));

        if (!hasExplicitIcon)
        {
            string slotId = GetCustomIconSlotID(account);
            if (slotId != "0")
            {
                string customCode = $"ai.custom_icon:{slotId}";
                if (!items.Contains(customCode, StringComparer.OrdinalIgnoreCase))
                {
                    items.RemoveAll(i => i.Equals("ai.Default Icon", StringComparison.OrdinalIgnoreCase));
                    items.Insert(0, customCode);
                }
            }
        }

        return items;
    }
}
