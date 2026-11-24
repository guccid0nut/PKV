// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ReSharper disable ConditionalAccessQualifierIsNonNullableAccordingToAPIContract

// ReSharper disable UseObjectOrCollectionInitializer


using KONGOR.MasterServer.Handlers.ClientRequester;
using KONGOR.MasterServer.Models.RequestResponse.Stats;
using KONGOR.MasterServer.Services;
using KONGOR.MasterServer.Services.Security;
using KONGOR.MasterServer.Services.Stats;
using MERRICK.DatabaseContext.Entities.Statistics;
using MERRICK.DatabaseContext.Repositories;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Primitives;
using Moq;

namespace ASPIRE.Tests.KONGOR.MasterServer.Tests;

public sealed class ProfileAndStatsIntegrationTests

{
    private static MatchStatistics CreateTestMatchStatistics(int matchId, string hostName = "HostPlayer",
        string fileName = "")

    {
        return new MatchStatistics

        {
            MatchID = matchId,

            ServerID = 1,

            HostAccountName = hostName,

            Map = "caldavar",

            MapVersion = "4.10.1",

            Version = "4.10.1",

            GameMode = "normal",

            TimePlayed = 1800,

            FileSize = 0,

            FileName = string.IsNullOrWhiteSpace(fileName) ? $"M{matchId}.honreplay" : fileName,

            ConnectionState = 0,

            AveragePSR = 1500,

            AveragePSRTeamOne = 1500,

            AveragePSRTeamTwo = 1500,

            ScoreTeam1 = 0,

            ScoreTeam2 = 0,

            TeamScoreGoal = 0,

            PlayerScoreGoal = 0,

            NumberOfRounds = 1,

            ReleaseStage = "Live",

            BannedHeroes = null,

            AwardMostAnnihilations = null,

            AwardMostQuadKills = null,

            AwardLargestKillStreak = null,

            AwardMostSmackdowns = null,

            AwardMostKills = 22,

            AwardMostAssists = 18,

            AwardLeastDeaths = 1,

            AwardMostBuildingDamage = 15000,

            AwardMostWardsKilled = null,

            AwardMostHeroDamageDealt = 45000,

            AwardHighestCreepScore = 320,

            MVPAccountID = 200
        };
    }


    private static PlayerStatistics CreateTestPlayerStatistics(int matchId, int accountId, string accountName)

    {
        return new PlayerStatistics

        {
            MatchID = matchId,

            AccountID = accountId,

            AccountName = accountName,

            ClanID = null,

            ClanTag = null,

            Team = 1,

            LobbyPosition = 1,

            GroupNumber = 1,

            Benefit = 1,

            HeroProductID = 105u,

            Inventory = [],

            Win = 1,

            Loss = 0,

            Disconnected = 0,

            Conceded = 0,

            Kicked = 0,

            PublicMatch = 0,

            PublicSkillRatingChange = 0.0,

            RankedMatch = 1,

            RankedSkillRatingChange = 15.5,

            SocialBonus = 0,

            UsedToken = 0,

            ConcedeVotes = 0,

            HeroKills = 22,

            HeroDamage = 45000,

            GoldFromHeroKills = 5000,

            HeroAssists = 18,

            HeroExperience = 15000,

            HeroDeaths = 1,

            Buybacks = 0,

            GoldLostToDeath = 100,

            SecondsDead = 30,

            TeamCreepKills = 280,

            TeamCreepDamage = 25000,

            TeamCreepGold = 12000,

            TeamCreepExperience = 18000,

            NeutralCreepKills = 40,

            NeutralCreepDamage = 8000,

            NeutralCreepGold = 3000,

            NeutralCreepExperience = 4000,

            BuildingDamage = 15000,

            BuildingsRazed = 3,

            ExperienceFromBuildings = 2000,

            GoldFromBuildings = 1500,

            Denies = 15,

            ExperienceDenied = 1200,

            Gold = 21500,

            GoldSpent = 20000,

            Experience = 39000,

            Actions = 3500,

            SecondsPlayed = 1800,

            HeroLevel = 25,

            ConsumablesPurchased = 5,

            WardsPlaced = 8,

            FirstBlood = 1,

            DoubleKill = 2,

            TripleKill = 1,

            QuadKill = 0,

            Annihilation = 1,

            KillStreak03 = 3,

            KillStreak04 = 2,

            KillStreak05 = 2,

            KillStreak06 = 1,

            KillStreak07 = 1,

            KillStreak08 = 1,

            KillStreak09 = 1,

            KillStreak10 = 1,

            KillStreak15 = 1,

            Smackdown = 3,

            Humiliation = 0,

            Nemesis = 2,

            Retribution = 1,

            Score = 1500,

            GameplayStat0 = 0.0,

            GameplayStat1 = 0.0,

            GameplayStat2 = 0.0,

            GameplayStat3 = 0.0,

            GameplayStat4 = 0.0,

            GameplayStat5 = 0.0,

            GameplayStat6 = 0.0,

            GameplayStat7 = 0.0,

            GameplayStat8 = 0.0,

            GameplayStat9 = 0.0,

            TimeEarningExperience = 1600,

            MVP = 1
        };
    }


    [Trait("Category", "Manual")]
    [Fact]
    public async Task ProfileAndStatsIntegration_HandleGetAccountAllHeroStats_ReturnsIntegerPositionalKeysForLuaVarargs()

    {
        if (!NexusTestEnvironment.IsManualTestEnabled) return;
        await using MerrickContext dbContext = TestDbContextFactory.CreateInMemoryContext();


        User user = new()

        {
            ID = 1,

            EmailAddress = "test@nexus.local",

            SRPPasswordHash = "h",

            SRPPasswordSalt = "s",

            PBKDF2PasswordHash = "h",

            Role = new Role { Name = "User" }
        };

        Account account = new() { ID = 100, Name = "TestPlayer", User = user, IsMain = true };

        Mastery mastery = new()

        {
            AccountID = 100,

            Account = account,

            HeroExperiences =

            [
                new HeroMasteryExperience { HeroIdentifier = "Hero_Shaman", Experience = 2500 },

                new HeroMasteryExperience { HeroIdentifier = "Hero_Devourer", Experience = 1000 }
            ]
        };

        account.Mastery = mastery;

        user.Accounts.Add(account);


        await dbContext.Users.AddAsync(user);

        await dbContext.Accounts.AddAsync(account);

        await dbContext.Masteries.AddAsync(mastery);

        await dbContext.SaveChangesAsync();


        Mock<ISessionContextResolver> sessionResolverMock = new();

        sessionResolverMock.Setup(s => s.ResolveAccountAsync(It.IsAny<HttpContext>()))
            .ReturnsAsync(account);


        Mock<IHeroDefinitionService> heroDefinitionsMock = new();

        heroDefinitionsMock.Setup(h => h.GetBaseHeroId(It.IsAny<string>())).Returns(1u);

        heroDefinitionsMock.Setup(h => h.GetBaseHeroId("Hero_Shaman")).Returns(105u);


        HeroStatsHandler handler = new(
            new MatchHistoryRepository(dbContext),
            new AccountRepository(dbContext),
            sessionResolverMock.Object,
            heroDefinitionsMock.Object,
            NullLogger<HeroStatsHandler>.Instance);


        DefaultHttpContext httpContext = new()

        {
            Request =

            {
                Query = new QueryCollection(new Dictionary<string, StringValues>

                {
                    { "f", "get_account_all_hero_stats" }, { "nickname", "TestPlayer" }
                })
            }
        };


        IActionResult result = await handler.HandleRequestAsync(httpContext);

        PhpResult? phpResult = result as PhpResult;


        Assert.NotNull(phpResult);

        Dictionary<string, object>? response = phpResult.Data as Dictionary<string, object>;

        Assert.NotNull(response);

        Assert.Equal(100, response["account_id"]);


        List<object>? masteryList = response["mastery_info"] as List<object>;

        Assert.NotNull(masteryList);

        Assert.NotEmpty(masteryList);


        Dictionary<object, object>? shamanRecord = masteryList
            .OfType<Dictionary<object, object>>()
            .FirstOrDefault(r => r[2]?.ToString() == "Hero_Shaman" || r["hero_name"]?.ToString() == "Hero_Shaman");


        Assert.NotNull(shamanRecord);

        // Verify 1-based integer keys matching K2 engine arg[1]..arg[17] varargs

        Assert.Equal(105, shamanRecord[1]);

        Assert.Equal("Hero_Shaman", shamanRecord[2]);

        Assert.Equal("2500", shamanRecord[17]);


        // Verify string key aliases

        Assert.Equal(105, shamanRecord["hero_id"]);

        Assert.Equal("Hero_Shaman", shamanRecord["hero_name"]);

        Assert.Equal(2500, shamanRecord["exp"]);


        // Verify all_hero_stats dictionary for GetMasteryHeroInfo client support

        Dictionary<string, object>? allHeroStats = response["all_hero_stats"] as Dictionary<string, object>;

        Assert.NotNull(allHeroStats);

        Assert.True(allHeroStats.ContainsKey("Hero_Shaman"));

        Dictionary<string, object>? shamanStats = allHeroStats["Hero_Shaman"] as Dictionary<string, object>;

        Assert.NotNull(shamanStats);

        Assert.Equal("Hero_Shaman", shamanStats["cli_name"]);

        Assert.True(shamanStats.ContainsKey("ranked"));

        Assert.True(shamanStats.ContainsKey("casual"));

        Assert.True(shamanStats.ContainsKey("campaign"));

        Assert.True(shamanStats.ContainsKey("campaign_casual"));

        Assert.True(shamanStats.ContainsKey("mastery"));

        Dictionary<string, object>? shamanMastery = shamanStats["mastery"] as Dictionary<string, object>;

        Assert.NotNull(shamanMastery);

        Assert.Equal(2500, shamanMastery["level_exp"]);

        Assert.Equal(2500, shamanStats["level_exp"]);
    }


    [Trait("Category", "Manual")]
    [Fact]
    public async Task ProfileAndStatsIntegration_GetPlayerAwardSummHandler_CalculatesAccurateAwardsFromMatches()

    {
        if (!NexusTestEnvironment.IsManualTestEnabled) return;
        await using MerrickContext dbContext = TestDbContextFactory.CreateInMemoryContext();


        User user = new()

        {
            ID = 2,

            EmailAddress = "award@nexus.local",

            SRPPasswordHash = "h",

            SRPPasswordSalt = "s",

            PBKDF2PasswordHash = "h",

            Role = new Role { Name = "User" }
        };

        Account account = new() { ID = 200, Name = "AwardWinner", User = user, IsMain = true };

        user.Accounts.Add(account);


        MatchStatistics match = CreateTestMatchStatistics(1001);

        PlayerStatistics playerStat = CreateTestPlayerStatistics(1001, 200, "AwardWinner");


        await dbContext.Users.AddAsync(user);

        await dbContext.Accounts.AddAsync(account);

        await dbContext.MatchStatistics.AddAsync(match);

        await dbContext.PlayerStatistics.AddAsync(playerStat);

        await dbContext.SaveChangesAsync();


        Mock<IPlayerStatisticsService> statsServiceMock = new();

        statsServiceMock.Setup(s => s.GetAggregatedStatisticsAsync(200))
            .ReturnsAsync(new PlayerStatisticsAggregatedDTO

            {
                TotalMatches = 1,

                MVP = 1,

                Smackdowns = 3,

                Annihilations = 1,

                HighestKillStreak = 15
            });


        Mock<ISessionContextResolver> sessionResolverMock = new();

        sessionResolverMock.Setup(s => s.ResolveAccountAsync(It.IsAny<HttpContext>()))
            .ReturnsAsync(account);


        GetPlayerAwardSummHandler handler = new(
            new MatchHistoryRepository(dbContext),
            new AccountRepository(dbContext),
            statsServiceMock.Object,
            sessionResolverMock.Object,
            NullLogger<GetPlayerAwardSummHandler>.Instance);


        DefaultHttpContext httpContext = new();

        httpContext.Request.ContentType = "application/x-www-form-urlencoded";

        httpContext.Request.Form = new FormCollection(new Dictionary<string, StringValues>

        {
            { "cookie", "session_cookie" }, { "nickname", "AwardWinner" }
        });


        IActionResult result = await handler.HandleRequestAsync(httpContext);

        PhpResult? phpResult = result as PhpResult;


        Assert.NotNull(phpResult);

        Dictionary<object, object>? response = phpResult.Data as Dictionary<object, object>;

        Assert.NotNull(response);


        // Assert Named Keys for player_stats_v2.lua

        Assert.Equal("1", response["annihilation"]);

        Assert.Equal("15", response["ks15"]);

        Assert.Equal("3", response["smackdown"]);

        Assert.Equal("1", response["mkill"]);

        Assert.Equal("1", response["most_assists"]);

        Assert.Equal("1", response["least_deaths"]);

        Assert.Equal("1", response["most_building_damage"]);

        Assert.Equal("1", response["most_hero_damage"]);

        Assert.Equal("1", response["most_creeps"]);

        Assert.Equal("1", response["mvp_score"]);


        // Assert Canonical XML Form Keys (from player_stats_v2.package)

        Assert.Equal("1", response["awd_mann"]);

        Assert.Equal("15", response["awd_lgks"]);

        Assert.Equal("3", response["awd_msd"]);

        Assert.Equal("1", response["awd_mkill"]);

        Assert.Equal("1", response["awd_masst"]);

        Assert.Equal("1", response["awd_ledth"]);

        Assert.Equal("1", response["awd_mbdmg"]);

        Assert.Equal("1", response["awd_mhdd"]);

        Assert.Equal("1", response["awd_hcs"]);

        Assert.Equal("200", response["account_id"]);

        Assert.Equal("1", response["mvp"]);


        // Assert Positional Integer Keys

        Assert.Equal("1", response[1]);

        Assert.Equal("15", response[3]);

        Assert.Equal("3", response[4]);

        Assert.Equal("1", response[5]);

        Assert.Equal("1", response[6]);

        Assert.Equal("1", response[7]);

        Assert.Equal("1", response[8]);

        Assert.Equal("1", response[10]);

        Assert.Equal("1", response[11]);

        Assert.Equal("200", response[12]);

        Assert.Equal("1", response[13]);
    }


    [Fact]
    public void ProfileAndStatsIntegration_CreateHybridSimpleStats_SetsAccurateCombatAveragesAndTotalMatches()

    {
        LegacyProtocolFormatter formatter = new();


        PlayerStatisticsAggregatedDTO agg = new()

        {
            TotalMatches = 50,

            Disconnected = 2,

            RankedMatches = 30,

            RankedWins = 20,

            RankedLosses = 10,

            RankedDiscos = 1,

            RankedKills = 150,

            RankedDeaths = 75,

            RankedAssists = 225,

            RankedSeconds = 54000, // 900 minutes = 30 mins / match

            RankedExp = 450000,

            RankedGold = 360000,

            RankedActions = 90000,

            RankedWards = 60,

            RankedDenies = 300,

            RankedTeamCreepKills = 3000,

            RankedNeutralCreepKills = 600,

            CasualMatches = 20,

            CasualWins = 12,

            CasualLosses = 8,

            CasualDiscos = 1
        };


        User user = new()

        {
            ID = 3,

            EmailAddress = "progamer@nexus.local",

            SRPPasswordHash = "h",

            SRPPasswordSalt = "s",

            PBKDF2PasswordHash = "h",

            Role = new Role { Name = "User" },

            TotalExperience = 150000
        };

        Account account = new() { ID = 100, Name = "ProGamer", User = user, IsMain = true };
        account.SetSelectedStoreItems(["ai.custom_icon:1", "cc.white", "t.Standard"]);

        user.Accounts.Add(account);


        Mock<IHeroDefinitionService> heroDefinitionsMock = new();

        heroDefinitionsMock.Setup(h => h.GetHeroIdentifier(It.IsAny<uint>())).Returns("Hero_Shaman");

        heroDefinitionsMock.Setup(h => h.GetBaseHeroId(It.IsAny<uint>())).Returns(1u);


        ShowSimpleStatsResponse showStatsResponse = PlayerProfileStatsBuilder.Instance.CreateShowSimpleStatsResponse(
            account,
            agg,
            1,
            heroDefinitionsMock.Object);


        Dictionary<object, object> hybrid = formatter.CreateHybridSimpleStats(showStatsResponse, "campaign");


        // Assert Top Bar and General Info metrics

        Assert.Equal("50", hybrid["matches"]);

        Assert.Equal("50", hybrid["total_games_played"]);

        Assert.Equal("2", hybrid["total_discos"]);

        Assert.Equal("2", hybrid["disconnects"]);


        // Assert Combat Averages

        Assert.Equal("100.0", hybrid["avgCreepKills"]); // 3000 / 30

        Assert.Equal("20.0", hybrid["avgNeutralKills"]); // 600 / 30

        Assert.Equal("10.0", hybrid["avgDenies"]); // 300 / 30

        Assert.Equal("500.0", hybrid["avgXP_min"]); // 450000 / 900

        Assert.Equal("400.0", hybrid["avgGold_min"]); // 360000 / 900

        Assert.Equal("100.0", hybrid["avgActions_min"]); // 90000 / 900

        Assert.Equal("2.0", hybrid["avgWardsUsed"]); // 60 / 30

        Assert.Equal("5.00:1", hybrid["k_d_a"]); // (150 + 225) / 75 = 375 / 75 = 5.00

        Assert.Equal("1800.0", hybrid["avgGameLength"]); // 54000 / 30 = 1800s = 30m


        // Assert Positional slots are populated with real values instead of placeholders

        Assert.Equal("100.0", hybrid[71]); // Lua Index 72 = PHP Index 71 (avgCreepKills)

        Assert.Equal("10.0", hybrid[70]); // Lua Index 71 = PHP Index 70 (avgDenies)

        Assert.Equal("500.0", hybrid[69]); // Lua Index 70 = PHP Index 69 (avgXP_min)

        Assert.Equal("100.0", hybrid[73]); // Lua Index 74 = PHP Index 73 (avgActions_min)

        Assert.Equal("2.0", hybrid[74]); // Lua Index 75 = PHP Index 74 (avgWardsUsed)

        Assert.Equal("5.00:1", hybrid[67]); // Lua Index 68 = PHP Index 67 (k_d_a)

        Assert.Equal("1800.0", hybrid[68]); // Lua Index 69 = PHP Index 68 (avgGameLength)

        Assert.Equal("20.0", hybrid[72]); // Lua Index 73 = PHP Index 72 (avgNeutralKills)

        Assert.Equal("ai.custom_icon:1|cc.white|t.Standard",
            hybrid[88]); // Lua Index 89 = PHP Index 88 (selected_upgrades)

        Assert.Equal(50, hybrid[89]); // Lua Index 90 = PHP Index 89 (acc_games_played = totalAllMatches)

        Assert.Equal(30, hybrid[90]); // Lua Index 91 = PHP Index 90 (cs_games_played = generic.RankedMatches)

        Assert.Equal(2, hybrid[91]); // Lua Index 92 = PHP Index 91 (acc_discos = totalAllDiscos)

        Assert.Equal(50, hybrid[125]); // Lua Index 126 = PHP Index 125 (totalAllMatches)

        Assert.Equal(2, hybrid[126]); // Lua Index 127 = PHP Index 126 (totalAllDiscos)
    }


    [Fact]
    public void MatchSummary_NormalizesFileName_AndFindsReplayInStandardPaths()

    {
        string tempDir = Path.Combine(Path.GetTempPath(), "nexus_test_replays_" + Guid.NewGuid().ToString("N"));

        Directory.CreateDirectory(tempDir);


        try

        {
            string testReplayFile = Path.Combine(tempDir, "M5555.honreplay");

            File.WriteAllBytes(testReplayFile, new byte[2048]);


            MatchSummary.SetReplayDirectories(tempDir);


            MatchStatistics match = CreateTestMatchStatistics(5555, "HostPlayer", "gucchost");


            MatchSummary summary = new(match, [], null);


            Assert.Equal("M5555.honreplay", summary.FileName);

            Assert.Equal(2048, summary.FileSize);


            bool found = MatchSummary.TryFindReplayFile(5555, "gucchost", out string resolvedPath, out int size);

            Assert.True(found);

            Assert.Equal(testReplayFile, resolvedPath);

            Assert.Equal(2048, size);
        }

        finally

        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }
}
