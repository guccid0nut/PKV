// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using KONGOR.MasterServer.Models.RequestResponse.Stats;
using KONGOR.MasterServer.Services;
using MERRICK.DatabaseContext.Entities.Statistics;
using Moq;

namespace ASPIRE.Tests.KONGOR.MasterServer.Tests;

public sealed class StatsExtensionTests

{
    [Fact]
    public void ToPlayerStatistics_ResolvesHeroId_WhenPayloadIdIsZero()

    {
        // Arrange

        Mock<IHeroDefinitionService> mockHeroService = new();

        mockHeroService.Setup(s => s.GetBaseHeroId("Hero_Valkyrie")).Returns(34);


        StatsForSubmissionRequestForm form = new()

        {
            Function = "submit_stats",

            MatchStats = new Dictionary<string, string> { { "match_id", "12345" } },

            TeamStats = [],

            PlayerStats = new Dictionary<int, Dictionary<string, Dictionary<string, string>>>

            {
                {
                    0, new Dictionary<string, Dictionary<string, string>>

                    {
                        {
                            "Hero_Valkyrie", new Dictionary<string, string>

                            {
                                { "hero_id", "0" }, // The bug: Public games send 0

                                { "nickname", "TestUser" },

                                { "team", "1" },

                                { "position", "0" },

                                { "group_num", "1" },

                                { "benefit", "0" },

                                { "wins", "1" },

                                { "losses", "0" },

                                { "discos", "0" },

                                { "concedes", "0" },

                                { "kicked", "0" },

                                { "social_bonus", "0" },

                                { "used_token", "0" },

                                { "concedevotes", "0" },

                                { "herokills", "10" },

                                { "herodmg", "5000" },

                                { "herokillsgold", "2000" },

                                { "heroassists", "5" },

                                { "heroexp", "8000" },

                                { "deaths", "2" },

                                { "buybacks", "0" },

                                { "goldlost2death", "100" },

                                { "secs_dead", "60" },

                                { "teamcreepkills", "0" },

                                { "teamcreepdmg", "0" },

                                { "teamcreepgold", "0" },

                                { "teamcreepexp", "0" },

                                { "neutralcreepkills", "20" },

                                { "neutralcreepdmg", "2000" },

                                { "neutralcreepgold", "500" },

                                { "neutralcreepexp", "600" },

                                { "bdmg", "1000" },

                                { "razed", "1" },

                                { "bdmgexp", "200" },

                                { "bgold", "300" },

                                { "denies", "5" },

                                { "exp_denied", "100" },

                                { "gold", "12000" },

                                { "gold_spent", "11000" },

                                { "exp", "9000" },

                                { "actions", "1500" },

                                { "secs", "1800" },

                                { "level", "15" },

                                { "consumables", "3" },

                                { "wards", "2" },

                                { "bloodlust", "0" },

                                { "doublekill", "1" },

                                { "triplekill", "0" },

                                { "quadkill", "0" },

                                { "annihilation", "0" },

                                { "ks3", "1" },

                                { "ks4", "0" },

                                { "ks5", "0" },

                                { "ks6", "0" },

                                { "ks7", "0" },

                                { "ks8", "0" },

                                { "ks9", "0" },

                                { "ks10", "0" },

                                { "ks15", "0" },

                                { "smackdown", "0" },

                                { "humiliation", "0" },

                                { "nemesis", "0" },

                                { "retribution", "0" },

                                { "score", "500" },

                                { "gameplaystat0", "0" },

                                { "gameplaystat1", "0" },

                                { "gameplaystat2", "0" },

                                { "gameplaystat3", "0" },

                                { "gameplaystat4", "0" },

                                { "gameplaystat5", "0" },

                                { "gameplaystat6", "0" },

                                { "gameplaystat7", "0" },

                                { "gameplaystat8", "0" },

                                { "gameplaystat9", "0" },

                                { "time_earning_exp", "1200" }
                            }
                        }
                    }
                }
            }
        };


        // Act

        PlayerStatistics result = form.ToPlayerStatistics(0, 100, "TestUser", null, null, mockHeroService.Object);


        // Assert

        Assert.Equal(34u, result.HeroProductID); // Should be resolved to Valkyrie

        mockHeroService.Verify(s => s.GetBaseHeroId("Hero_Valkyrie"), Times.Once);
    }


    [Fact]
    public void ToPlayerStatistics_UsesPayloadId_WhenItIsValid()

    {
        // Arrange

        Mock<IHeroDefinitionService> mockHeroService = new();


        StatsForSubmissionRequestForm form = new()

        {
            Function = "submit_stats",

            MatchStats = new Dictionary<string, string> { { "match_id", "12345" } },

            TeamStats = [],

            PlayerStats = new Dictionary<int, Dictionary<string, Dictionary<string, string>>>

            {
                {
                    0, new Dictionary<string, Dictionary<string, string>>

                    {
                        {
                            "Hero_Legionnaire",

                            new Dictionary<string, string> // Identifier doesn't matter here if ID is valid

                            {
                                { "hero_id", "123" }, // Valid ID

                                { "nickname", "TestUser" },

                                { "team", "1" },

                                { "position", "0" },

                                { "group_num", "1" },

                                { "benefit", "0" },

                                { "wins", "1" },

                                { "losses", "0" },

                                { "discos", "0" },

                                { "concedes", "0" },

                                { "kicked", "0" },

                                { "social_bonus", "0" },

                                { "used_token", "0" },

                                { "concedevotes", "0" },

                                { "herokills", "0" },

                                { "herodmg", "0" },

                                { "herokillsgold", "0" },

                                { "heroassists", "0" },

                                { "heroexp", "0" },

                                { "deaths", "0" },

                                { "buybacks", "0" },

                                { "goldlost2death", "0" },

                                { "secs_dead", "0" },

                                { "teamcreepkills", "0" },

                                { "teamcreepdmg", "0" },

                                { "teamcreepgold", "0" },

                                { "teamcreepexp", "0" },

                                { "neutralcreepkills", "0" },

                                { "neutralcreepdmg", "0" },

                                { "neutralcreepgold", "0" },

                                { "neutralcreepexp", "0" },

                                { "bdmg", "0" },

                                { "razed", "0" },

                                { "bdmgexp", "0" },

                                { "bgold", "0" },

                                { "denies", "0" },

                                { "exp_denied", "0" },

                                { "gold", "0" },

                                { "gold_spent", "0" },

                                { "exp", "0" },

                                { "actions", "0" },

                                { "secs", "0" },

                                { "level", "1" },

                                { "consumables", "0" },

                                { "wards", "0" },

                                { "bloodlust", "0" },

                                { "doublekill", "0" },

                                { "triplekill", "0" },

                                { "quadkill", "0" },

                                { "annihilation", "0" },

                                { "ks3", "0" },

                                { "ks4", "0" },

                                { "ks5", "0" },

                                { "ks6", "0" },

                                { "ks7", "0" },

                                { "ks8", "0" },

                                { "ks9", "0" },

                                { "ks10", "0" },

                                { "ks15", "0" },

                                { "smackdown", "0" },

                                { "humiliation", "0" },

                                { "nemesis", "0" },

                                { "retribution", "0" },

                                { "score", "0" },

                                { "gameplaystat0", "0" },

                                { "gameplaystat1", "0" },

                                { "gameplaystat2", "0" },

                                { "gameplaystat3", "0" },

                                { "gameplaystat4", "0" },

                                { "gameplaystat5", "0" },

                                { "gameplaystat6", "0" },

                                { "gameplaystat7", "0" },

                                { "gameplaystat8", "0" },

                                { "gameplaystat9", "0" },

                                { "time_earning_exp", "0" }
                            }
                        }
                    }
                }
            }
        };


        // Act

        PlayerStatistics result = form.ToPlayerStatistics(0, 100, "TestUser", null, null, mockHeroService.Object);


        // Assert

        Assert.Equal(123u, result.HeroProductID);

        mockHeroService.Verify(s => s.GetBaseHeroId(It.IsAny<string>()), Times.Never);
    }


    [Fact]
    public void ToMatchStatistics_And_ToPlayerStatistics_MapsMVPAndAwards_Correctly()

    {
        // Arrange

        Mock<IHeroDefinitionService> mockHeroService = new();

        mockHeroService.Setup(s => s.GetBaseHeroId("Hero_Calamity")).Returns(150);


        StatsForSubmissionRequestForm form = new()

        {
            Function = "submit_stats",

            ServerID = 1,

            HostAccountName = "guccihost",

            MatchStats =
                new Dictionary<string, string>

                {
                    { "match_id", "2060568725" },

                    { "map", "midwars" },

                    { "map_version", "1.0" },

                    { "time_played", "1200" },

                    { "file_size", "1024000" },

                    { "file_name", "M2060568725.honreplay" },

                    { "c_state", "2" },

                    { "version", "4.10.1" },

                    { "avgpsr", "1500" },

                    { "avgpsr_team1", "1500" },

                    { "avgpsr_team2", "1500" },

                    { "gamemode", "midwars" },

                    { "teamscoregoal", "0" },

                    { "playerscoregoal", "0" },

                    { "numrounds", "1" },

                    { "release_stage", "release" },

                    { "mvp", "101" },

                    { "awd_mann", "1" },

                    { "awd_mqk", "2" },

                    { "awd_lgks", "15" },

                    { "awd_msd", "3" },

                    { "awd_mkill", "18" },

                    { "awd_masst", "22" },

                    { "awd_ledth", "1" },

                    { "awd_mbdmg", "4500" },

                    { "awd_mwk", "8" },

                    { "awd_mhdd", "62000" },

                    { "awd_hcs", "210" }
                },

            TeamStats =
                new Dictionary<int, Dictionary<string, int>>

                {
                    { 1, new Dictionary<string, int> { { "score", 45 } } },

                    { 2, new Dictionary<string, int> { { "score", 30 } } }
                },

            PlayerStats = new Dictionary<int, Dictionary<string, Dictionary<string, string>>>

            {
                {
                    0,

                    new Dictionary<string, Dictionary<string, string>>

                    {
                        {
                            "Hero_Calamity",

                            new Dictionary<string, string>

                            {
                                { "hero_id", "150" },

                                { "nickname", "guccidonut" },

                                { "team", "1" },

                                { "position", "0" },

                                { "group_num", "1" },

                                { "benefit", "0" },

                                { "wins", "1" },

                                { "losses", "0" },

                                { "discos", "0" },

                                { "concedes", "0" },

                                { "kicked", "0" },

                                { "mvp", "1" },

                                { "social_bonus", "0" },

                                { "used_token", "0" },

                                { "concedevotes", "0" },

                                { "herokills", "18" },

                                { "herodmg", "62000" },

                                { "herokillsgold", "4000" },

                                { "heroassists", "22" },

                                { "heroexp", "15000" },

                                { "deaths", "1" },

                                { "buybacks", "0" },

                                { "goldlost2death", "50" },

                                { "secs_dead", "30" },

                                { "teamcreepkills", "210" },

                                { "teamcreepdmg", "25000" },

                                { "teamcreepgold", "12000" },

                                { "teamcreepexp", "10000" },

                                { "neutralcreepkills", "10" },

                                { "neutralcreepdmg", "1500" },

                                { "neutralcreepgold", "600" },

                                { "neutralcreepexp", "800" },

                                { "bdmg", "4500" },

                                { "razed", "3" },

                                { "bdmgexp", "500" },

                                { "bgold", "800" },

                                { "denies", "12" },

                                { "exp_denied", "300" },

                                { "gold", "18000" },

                                { "gold_spent", "17500" },

                                { "exp", "16000" },

                                { "actions", "2500" },

                                { "secs", "1200" },

                                { "level", "25" },

                                { "consumables", "5" },

                                { "wards", "8" },

                                { "bloodlust", "1" },

                                { "doublekill", "3" },

                                { "triplekill", "2" },

                                { "quadkill", "2" },

                                { "annihilation", "1" },

                                { "ks3", "2" },

                                { "ks4", "1" },

                                { "ks5", "1" },

                                { "ks6", "1" },

                                { "ks7", "1" },

                                { "ks8", "1" },

                                { "ks9", "1" },

                                { "ks10", "1" },

                                { "ks15", "1" },

                                { "smackdown", "3" },

                                { "humiliation", "0" },

                                { "nemesis", "1" },

                                { "retribution", "0" },

                                { "score", "850" },

                                { "gameplaystat0", "0" },

                                { "gameplaystat1", "0" },

                                { "gameplaystat2", "0" },

                                { "gameplaystat3", "0" },

                                { "gameplaystat4", "0" },

                                { "gameplaystat5", "0" },

                                { "gameplaystat6", "0" },

                                { "gameplaystat7", "0" },

                                { "gameplaystat8", "0" },

                                { "gameplaystat9", "0" },

                                { "time_earning_exp", "1100" }
                            }
                        }
                    }
                }
            }
        };


        // Act

        MatchStatistics matchStats = form.ToMatchStatistics(1, "guccihost");

        PlayerStatistics playerStats =
            form.ToPlayerStatistics(0, 101, "guccidonut", null, null, mockHeroService.Object);


        // Assert

        Assert.Equal(101, matchStats.MVPAccountID);

        Assert.Equal(1, matchStats.AwardMostAnnihilations);

        Assert.Equal(2, matchStats.AwardMostQuadKills);

        Assert.Equal(15, matchStats.AwardLargestKillStreak);

        Assert.Equal(3, matchStats.AwardMostSmackdowns);

        Assert.Equal(18, matchStats.AwardMostKills);

        Assert.Equal(22, matchStats.AwardMostAssists);

        Assert.Equal(1, matchStats.AwardLeastDeaths);

        Assert.Equal(4500, matchStats.AwardMostBuildingDamage);

        Assert.Equal(8, matchStats.AwardMostWardsKilled);

        Assert.Equal(62000, matchStats.AwardMostHeroDamageDealt);

        Assert.Equal(210, matchStats.AwardHighestCreepScore);


        Assert.Equal(1, playerStats.MVP);

        Assert.Equal(101, playerStats.AccountID);

        Assert.Equal("guccidonut", playerStats.AccountName);

        Assert.Equal(18, playerStats.HeroKills);

        Assert.Equal(62000, playerStats.HeroDamage);
    }


    [Fact]
    public void ToPlayerStatistics_PreservesAlternativeAvatarName_WhenAltAvatarPlayed()

    {
        // Arrange

        Mock<IHeroDefinitionService> mockHeroService = new();

        mockHeroService.Setup(s => s.GetBaseHeroId("Hero_Valkyrie.Alt12")).Returns(34);

        mockHeroService.Setup(s => s.GetBaseHeroId(34)).Returns(34);


        StatsForSubmissionRequestForm form = new()

        {
            Function = "submit_stats",

            MatchStats = new Dictionary<string, string> { { "match_id", "55555" } },

            TeamStats = [],

            PlayerStats = new Dictionary<int, Dictionary<string, Dictionary<string, string>>>

            {
                {
                    0, new Dictionary<string, Dictionary<string, string>>

                    {
                        {
                            "Hero_Valkyrie.Alt12", new Dictionary<string, string>

                            {
                                { "hero_id", "34" },

                                { "nickname", "ValkyriePlayer" },

                                { "team", "1" },

                                { "position", "0" },

                                { "group_num", "1" },

                                { "benefit", "0" },

                                { "wins", "1" },

                                { "losses", "0" },

                                { "discos", "0" },

                                { "concedes", "0" },

                                { "kicked", "0" },

                                { "social_bonus", "0" },

                                { "used_token", "0" },

                                { "concedevotes", "0" },

                                { "herokills", "10" },

                                { "herodmg", "5000" },

                                { "herokillsgold", "2000" },

                                { "heroassists", "5" },

                                { "heroexp", "8000" },

                                { "deaths", "2" },

                                { "buybacks", "0" },

                                { "goldlost2death", "100" },

                                { "secs_dead", "60" },

                                { "teamcreepkills", "0" },

                                { "teamcreepdmg", "0" },

                                { "teamcreepgold", "0" },

                                { "teamcreepexp", "0" },

                                { "neutralcreepkills", "20" },

                                { "neutralcreepdmg", "2000" },

                                { "neutralcreepgold", "500" },

                                { "neutralcreepexp", "600" },

                                { "bdmg", "1000" },

                                { "razed", "1" },

                                { "bdmgexp", "200" },

                                { "bgold", "300" },

                                { "denies", "5" },

                                { "exp_denied", "100" },

                                { "gold", "12000" },

                                { "gold_spent", "11000" },

                                { "exp", "9000" },

                                { "actions", "1500" },

                                { "secs", "1800" },

                                { "level", "15" },

                                { "consumables", "3" },

                                { "wards", "2" },

                                { "bloodlust", "0" },

                                { "doublekill", "1" },

                                { "triplekill", "0" },

                                { "quadkill", "0" },

                                { "annihilation", "0" },

                                { "ks3", "1" },

                                { "ks4", "0" },

                                { "ks5", "0" },

                                { "ks6", "0" },

                                { "ks7", "0" },

                                { "ks8", "0" },

                                { "ks9", "0" },

                                { "ks10", "0" },

                                { "ks15", "0" },

                                { "smackdown", "0" },

                                { "humiliation", "0" },

                                { "nemesis", "0" },

                                { "retribution", "0" },

                                { "score", "500" },

                                { "gameplaystat0", "0" },

                                { "gameplaystat1", "0" },

                                { "gameplaystat2", "0" },

                                { "gameplaystat3", "0" },

                                { "gameplaystat4", "0" },

                                { "gameplaystat5", "0" },

                                { "gameplaystat6", "0" },

                                { "gameplaystat7", "0" },

                                { "gameplaystat8", "0" },

                                { "gameplaystat9", "0" },

                                { "time_earning_exp", "1200" }
                            }
                        }
                    }
                }
            }
        };


        // Act

        PlayerStatistics result = form.ToPlayerStatistics(0, 100, "ValkyriePlayer", null, null, mockHeroService.Object);


        // Assert

        Assert.Equal("Hero_Valkyrie", result.HeroIdentifier);

        Assert.Equal("Hero_Valkyrie.Alt12", result.AlternativeAvatarName);
    }
}
