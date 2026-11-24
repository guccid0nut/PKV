// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using ASPIRE.Common.Constants;
using ASPIRE.Common.Serialization.PHP;
using FluentAssertions;
using Xunit;

namespace Kongor.Core.Tests;

public sealed class LegacyProtocolFormatterTests
{
    [Fact]
    public void LegacyProtocolFormatter_CreateHybridSimpleStats_FormatsRootAndNestedStats()
    {
        ShowSimpleStatsResponse response = new()
        {
            NameWithClanTag = "[TEST]Player1",
            ID = 1001,
            Level = 10,
            LevelExperience = 5000,
            NumberOfAvatarsOwned = 2,
            TotalMatchesPlayed = 50,
            TotalGamesPlayedLegacy = 50,
            AccountCreationDate = "01/01/2022",
            LastActivityDate = "01/01/2026",
            TotalDisconnects = 1,
            RankedWins = 30,
            RankedLosses = 20,
            FavHero1 = "Hero_One",
            FavHero1Time = 100,
            FavHero1_2 = "Hero_One",
            FavHero2 = "Hero_Two",
            FavHero2Time = 90,
            FavHero2_2 = "Hero_Two",
            FavHero3 = "Hero_Three",
            FavHero3Time = 80,
            FavHero3_2 = "Hero_Three",
            FavHero4 = "Hero_Four",
            FavHero4Time = 70,
            FavHero4_2 = "Hero_Four",
            FavHero5 = "Hero_Five",
            FavHero5Time = 60,
            FavHero5_2 = "Hero_Five",
            CurrentSeason = 5,
            SimpleSeasonStats = new SimpleSeasonStats
            {
                RankedMatchesWon = 30,
                RankedMatchesLost = 20,
                WinStreak = 3,
                CurrentRank = "Gold I",
                RankedRating = "1600.000",
                InPlacementPhase = 0
            },
            SimpleCasualSeasonStats = new SimpleSeasonStats
            {
                RankedMatchesWon = 0,
                RankedMatchesLost = 0,
                WinStreak = 0,
                CurrentRank = "0.000",
                RankedRating = "1500.000",
                InPlacementPhase = 0
            },
            SimpleMidWarsSeasonStats = new SimpleSeasonStats
            {
                RankedMatchesWon = 0,
                RankedMatchesLost = 0,
                WinStreak = 0,
                CurrentRank = "0.000",
                RankedRating = "1500.000",
                InPlacementPhase = 0
            },
            MVPAwardsCount = "5",
            Top4AwardNames = [],
            Top4AwardCounts = [],
            CustomIconSlotID = "0",
            OwnedStoreItems = [],
            SelectedStoreItems = [],
            OwnedStoreItemsData = []
        };

        Dictionary<object, object> hybrid = LegacyProtocolFormatter.Instance.CreateHybridSimpleStats(response);

        hybrid.Should().NotBeNull();
        hybrid.Should().ContainKey("rnk_wins");
        hybrid["rnk_wins"].Should().Be("30");
        hybrid.Should().ContainKey("rnk_losses");
        hybrid["rnk_losses"].Should().Be("20");
        hybrid.Should().ContainKey("season_normal");

        Dictionary<object, object>? seasonNormal = hybrid["season_normal"] as Dictionary<object, object>;
        seasonNormal.Should().NotBeNull();
        seasonNormal!["wins"].Should().Be("30");
        seasonNormal["losses"].Should().Be("20");
        seasonNormal["rank"].Should().Be("Gold I");
    }

    [Fact]
    public void LegacyProtocolFormatter_ToHashtable_FormatsRankedAndCasualStatsProperly()
    {
        SimpleSeasonStats stats = new()
        {
            RankedMatchesWon = 15,
            RankedMatchesLost = 5,
            CasualWins = 8,
            CasualLosses = 2,
            WinStreak = 4,
            CurrentRank = "Diamond",
            RankedRating = "1800.000",
            CasualRating = "1500.000",
            InPlacementPhase = 0,
            GenericStats = new PlayerStatisticsAggregatedDTO
            {
                RankedKills = 100,
                RankedDeaths = 50,
                RankedAssists = 80
            }
        };

        Dictionary<object, object> rankedHash = LegacyProtocolFormatter.Instance.ToHashtable(stats, false);
        rankedHash["rnk_wins"].Should().Be("15");
        rankedHash["rnk_losses"].Should().Be("5");
        rankedHash["con_reward"].Should().Be("Diamond,1,3,2,11,0,0");

        Dictionary<object, object> casualHash = LegacyProtocolFormatter.Instance.ToHashtable(stats, true);
        casualHash["cs_wins"].Should().Be("8");
        casualHash["cs_losses"].Should().Be("2");
    }

    [Fact]
    public void LegacyProtocolFormatter_ConvertToPhp_HandlesPrimitivesAndCollections()
    {
        LegacyProtocolFormatter formatter = new();

        formatter.ConvertToPhp(null).Should().Be("0");
        formatter.ConvertToPhp(true).Should().Be("1");
        formatter.ConvertToPhp(false).Should().Be("0");
        formatter.ConvertToPhp(123).Should().Be("123");
        formatter.ConvertToPhp("hello").Should().Be("hello");

        Dictionary<string, string> dict = new() { ["key1"] = "val1" };
        object phpDict = formatter.ConvertToPhp(dict);
        phpDict.Should().BeOfType<Dictionary<object, object>>();
    }

    [Fact]
    public void Serialize_ShowSimpleStatsResponse_DirectZeroAlloc_ProducesValidPhpPayload()
    {
        ShowSimpleStatsResponse response = new()
        {
            NameWithClanTag = "[TEST]Player1",
            ID = 1001,
            Level = 10,
            LevelExperience = 5000,
            NumberOfAvatarsOwned = 2,
            TotalMatchesPlayed = 50,
            TotalGamesPlayedLegacy = 50,
            AccountCreationDate = "01/01/2022",
            LastActivityDate = "01/01/2026",
            TotalDisconnects = 1,
            RankedWins = 30,
            RankedLosses = 20,
            FavHero1 = "Hero_One",
            FavHero1Time = 100,
            FavHero1_2 = "Hero_One",
            FavHero2 = "Hero_Two",
            FavHero2Time = 90,
            FavHero2_2 = "Hero_Two",
            FavHero3 = "Hero_Three",
            FavHero3Time = 80,
            FavHero3_2 = "Hero_Three",
            FavHero4 = "Hero_Four",
            FavHero4Time = 70,
            FavHero4_2 = "Hero_Four",
            FavHero5 = "Hero_Five",
            FavHero5Time = 60,
            FavHero5_2 = "Hero_Five",
            CurrentSeason = 5,
            SimpleSeasonStats = new SimpleSeasonStats
            {
                RankedMatchesWon = 30,
                RankedMatchesLost = 20,
                WinStreak = 3,
                CurrentRank = "Gold I",
                RankedRating = "1600.000",
                InPlacementPhase = 0
            },
            SimpleCasualSeasonStats = new SimpleSeasonStats
            {
                RankedMatchesWon = 0,
                RankedMatchesLost = 0,
                WinStreak = 0,
                CurrentRank = "0.000",
                RankedRating = "1500.000",
                InPlacementPhase = 0
            },
            SimpleMidWarsSeasonStats = new SimpleSeasonStats
            {
                RankedMatchesWon = 0,
                RankedMatchesLost = 0,
                WinStreak = 0,
                CurrentRank = "0.000",
                RankedRating = "1500.000",
                InPlacementPhase = 0
            },
            MVPAwardsCount = "5",
            Top4AwardNames = ["MVP", "TripleKill"],
            Top4AwardCounts = ["5", "10"],
            CustomIconSlotID = "0",
            OwnedStoreItems = ["ItemA", "ItemB"],
            SelectedStoreItems = ["ItemA"],
            OwnedStoreItemsData = []
        };

        byte[] serializedBytes = LegacyProtocolFormatter.Instance.Serialize(response);
        serializedBytes.Should().NotBeNullOrEmpty();

        string payloadStr = System.Text.Encoding.UTF8.GetString(serializedBytes);
        payloadStr.Should().StartWith("a:71:{");
        payloadStr.Should().Contain("s:8:\"nickname\";s:13:\"[TEST]Player1\";");
        payloadStr.Should().Contain("s:10:\"account_id\";i:1001;");
        payloadStr.Should().Contain("s:5:\"level\";i:10;");
        payloadStr.Should().Contain("s:13:\"season_normal\";a:22:{");
        payloadStr.Should().Contain("s:4:\"wins\";i:30;");
        payloadStr.Should().Contain("s:6:\"losses\";i:20;");
        payloadStr.Should().Contain("s:4:\"rank\";s:6:\"Gold I\";");
    }

    [Theory]
    [InlineData("caldavar_ranked", MatchFilterKind.RankedFoC, "caldavar", true)]
    [InlineData("ranked", MatchFilterKind.RankedFoC, "caldavar", true)]
    [InlineData("normal", MatchFilterKind.RankedFoC, "caldavar", true)]
    [InlineData("caldavar_casual", MatchFilterKind.Quickmatch, "caldavar", true)]
    [InlineData("quickmatch", MatchFilterKind.Quickmatch, "caldavar", true)]
    [InlineData("quickmatch_ranked", MatchFilterKind.Quickmatch, "caldavar", true)]
    [InlineData("casual", MatchFilterKind.Quickmatch, "caldavar", true)]
    [InlineData("midwars_ranked", MatchFilterKind.RankedMidWars, "midwars", true)]
    [InlineData("riftwars_ranked", MatchFilterKind.RankedRiftWars, "riftwars", true)]
    [InlineData("all_public", MatchFilterKind.AllPublic, null, false)]
    [InlineData("public", MatchFilterKind.AllPublic, null, false)]
    [InlineData("caldavar_public", MatchFilterKind.PublicMap, "caldavar", false)]
    [InlineData("midwars_public", MatchFilterKind.PublicMap, "midwars", false)]
    [InlineData("devowars", MatchFilterKind.PublicMap, "devowars", false)]
    [InlineData("prophets", MatchFilterKind.PublicMap, "prophets", false)]
    [InlineData("strikers", MatchFilterKind.PublicMap, "strikers", false)]
    [InlineData("grimmscrossing", MatchFilterKind.PublicMap, "grimmscrossing", false)]
    [InlineData("watchtower", MatchFilterKind.PublicMap, "watchtower", false)]
    [InlineData("darkwoodvale", MatchFilterKind.PublicMap, "darkwoodvale", false)]
    [InlineData("ctf", MatchFilterKind.PublicMap, "ctf", false)]
    [InlineData("team_deathmatch", MatchFilterKind.PublicMap, "team_deathmatch", false)]
    [InlineData(null, MatchFilterKind.RankedFoC, "caldavar", true)]
    public void MapCatalog_ClassifyMatchFilter_ClassifiesCorrectly(string? table, MatchFilterKind expectedKind, string? expectedMap, bool expectedRanked)
    {
        var result = ASPIRE.Common.Constants.MapCatalog.ClassifyMatchFilter(table);
        result.Kind.Should().Be(expectedKind);
        result.MapKey.Should().Be(expectedMap);
        result.IsRanked.Should().Be(expectedRanked);
    }

    private static ShowSimpleStatsResponse CreateDummyResponse() => new()
    {
        NameWithClanTag = "[TEST]KongorHero",
        ID = 42,
        Level = 5,
        LevelExperience = 1200,
        NumberOfAvatarsOwned = 0,
        TotalMatchesPlayed = 20,
        TotalGamesPlayedLegacy = 20,
        AccountCreationDate = "01/01/2025",
        LastActivityDate = "01/01/2026",
        TotalDisconnects = 0,
        RankedWins = 15,
        RankedLosses = 5,
        FavHero1 = "Hero_One",
        FavHero1Time = 100,
        FavHero1_2 = "Hero_One",
        FavHero2 = "Hero_Two",
        FavHero2Time = 90,
        FavHero2_2 = "Hero_Two",
        FavHero3 = "Hero_Three",
        FavHero3Time = 80,
        FavHero3_2 = "Hero_Three",
        FavHero4 = "Hero_Four",
        FavHero4Time = 70,
        FavHero4_2 = "Hero_Four",
        FavHero5 = "Hero_Five",
        FavHero5Time = 60,
        FavHero5_2 = "Hero_Five",
        CurrentSeason = 1,
        SimpleSeasonStats = new SimpleSeasonStats
        {
            RankedMatchesWon = 15,
            RankedMatchesLost = 5,
            WinStreak = 0,
            CurrentRank = "0.000",
            RankedRating = "1500.000",
            InPlacementPhase = 0,
            GenericStats = new PlayerStatisticsAggregatedDTO
            {
                PublicMatches = 20,
                PublicWins = 15,
                PublicLosses = 5,
                PublicKills = 100,
                PublicDeaths = 50,
                PublicAssists = 80
            }
        },
        SimpleCasualSeasonStats = new SimpleSeasonStats
        {
            RankedMatchesWon = 9,
            RankedMatchesLost = 3,
            CasualWins = 9,
            CasualLosses = 3,
            WinStreak = 2,
            CurrentRank = "0.000",
            RankedRating = "1500.000",
            CasualRating = "1545.000",
            InPlacementPhase = 0,
            GenericStats = new PlayerStatisticsAggregatedDTO
            {
                CasualMatches = 12,
                CasualWins = 9,
                CasualLosses = 3,
                CasualKills = 72,
                CasualDeaths = 24,
                CasualAssists = 60
            }
        },
        SimpleMidWarsSeasonStats = new SimpleSeasonStats
        {
            RankedMatchesWon = 0,
            RankedMatchesLost = 0,
            WinStreak = 0,
            CurrentRank = "0.000",
            RankedRating = "1500.000",
            InPlacementPhase = 0
        },
        MVPAwardsCount = "0",
        Top4AwardNames = [],
        Top4AwardCounts = [],
        CustomIconSlotID = "0",
        OwnedStoreItems = [],
        SelectedStoreItems = [],
        OwnedStoreItemsData = []
    };

    [Fact]
    public void CreateHybridSimpleStats_WithPublicMapTable_FormatsPositionalPublicStats()
    {
        var response = CreateDummyResponse();
        var formatter = new LegacyProtocolFormatter();
        var result = formatter.CreateHybridSimpleStats(response, "devowars");
        result.Should().NotBeNull();
        result[0].Should().Be("[TEST]KongorHero"); // Lua arg[1]
        result[5].Should().Be(20);                 // Lua arg[6]: acc_games_played
        result[6].Should().Be(15);                 // Lua arg[7]: acc_wins
        result[7].Should().Be(5);                  // Lua arg[8]: acc_losses
    }

    [Fact]
    public void CreateHybridSimpleStats_WithQuickmatchTable_FormatsIsolatedCasualStats()
    {
        var response = CreateDummyResponse();
        var formatter = new LegacyProtocolFormatter();
        var result = formatter.CreateHybridSimpleStats(response, "caldavar_casual");
        result.Should().NotBeNull();
        result[0].Should().Be("[TEST]KongorHero");
        result[5].Should().Be(12L); // CasualMatches (isolated from RankedWins=15 + RankedLosses=5)
        result[6].Should().Be(9);   // CasualWins
        result[7].Should().Be(3);   // CasualLosses
        result[20].Should().Be(72L); // CasualKills (Lua arg[21])
        result["herokills"].Should().Be("72");
    }
}
