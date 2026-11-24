// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using KONGOR.MasterServer.Handlers.ClientRequester;
using KONGOR.MasterServer.Models.RequestResponse.Stats;
using KONGOR.MasterServer.Services;
using KONGOR.MasterServer.Services.Stats;
using MERRICK.DatabaseContext.Entities.Statistics;
using MERRICK.DatabaseContext.Repositories;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Primitives;
using Moq;

namespace ASPIRE.Tests.KONGOR.MasterServer.Tests;

public sealed class DualClientCompatibilityTests

{
    private static MatchStatistics CreateTestMatch(int matchId, int serverId = 1, string gameMode = "normal",
        string map = "caldavar")

    {
        return new MatchStatistics

        {
            MatchID = matchId,

            ServerID = serverId,

            HostAccountName = "TestHost",

            Map = map,

            MapVersion = "4.10.1",

            Version = "4.10.1",

            GameMode = gameMode,

            TimePlayed = 1800,

            FileSize = 1024,

            FileName = $"M{matchId}.honreplay",

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

            AwardMostKills = 10,

            AwardMostAssists = 10,

            AwardLeastDeaths = 1,

            AwardMostBuildingDamage = 5000,

            AwardMostWardsKilled = null,

            AwardMostHeroDamageDealt = 20000,

            AwardHighestCreepScore = 200,

            MVPAccountID = null
        };
    }


    private static PlayerStatistics CreateTestPlayerStat(int matchId, int accountId, string accountName,
        int rankedMatch = 1, int publicMatch = 0, int win = 1)

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

            Win = win,

            Loss = win == 1 ? 0 : 1,

            Disconnected = 0,

            Conceded = 0,

            Kicked = 0,

            PublicMatch = publicMatch,

            PublicSkillRatingChange = 0.0,

            RankedMatch = rankedMatch,

            RankedSkillRatingChange = 15.0,

            SocialBonus = 0,

            UsedToken = 0,

            ConcedeVotes = 0,

            HeroKills = 10,

            HeroDamage = 20000,

            GoldFromHeroKills = 3000,

            HeroAssists = 10,

            HeroExperience = 10000,

            HeroDeaths = 2,

            Buybacks = 0,

            GoldLostToDeath = 50,

            SecondsDead = 40,

            TeamCreepKills = 150,

            TeamCreepDamage = 15000,

            TeamCreepGold = 6000,

            TeamCreepExperience = 9000,

            NeutralCreepKills = 30,

            NeutralCreepDamage = 4000,

            NeutralCreepGold = 1500,

            NeutralCreepExperience = 2000,

            BuildingDamage = 5000,

            BuildingsRazed = 2,

            ExperienceFromBuildings = 1000,

            GoldFromBuildings = 1000,

            Denies = 10,

            ExperienceDenied = 800,

            Gold = 12000,

            GoldSpent = 11000,

            Experience = 22000,

            Actions = 2500,

            SecondsPlayed = 1800,

            HeroLevel = 20,

            ConsumablesPurchased = 3,

            WardsPlaced = 4,

            FirstBlood = 1,

            DoubleKill = 1,

            TripleKill = 0,

            QuadKill = 0,

            Annihilation = 0,

            KillStreak03 = 2,

            KillStreak04 = 1,

            KillStreak05 = 1,

            KillStreak06 = 0,

            KillStreak07 = 0,

            KillStreak08 = 0,

            KillStreak09 = 0,

            KillStreak10 = 0,

            KillStreak15 = 0,

            Smackdown = 2,

            Humiliation = 0,

            Nemesis = 1,

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

            TimeEarningExperience = 1500,

            MVP = 0
        };
    }


    [Fact]
    public void LegacyProtocolFormatter_PacksMatchIdsAndDates_ForLegacyAndV2WatchHandlers()

    {
        LegacyProtocolFormatter formatter = new();


        PlayerStatisticsAggregatedDTO agg = new()

        {
            TotalMatches = 2,

            Disconnected = 0,

            RankedMatches = 2,

            RankedWins = 2,

            RankedLosses = 0,

            RankedKills = 20,

            RankedDeaths = 4,

            RankedAssists = 20,

            RankedSeconds = 3600,

            RankedExp = 44000,

            RankedGold = 24000,

            RankedActions = 5000,

            RankedWards = 8,

            RankedDenies = 20,

            RankedTeamCreepKills = 300,

            RankedNeutralCreepKills = 60,

            RecentMatchIDs = [9999, 8888],

            RecentMatchDates =

            [
                new DateTimeOffset(2026, 8, 14, 12, 0, 0, TimeSpan.Zero),

                new DateTimeOffset(2026, 8, 13, 12, 0, 0, TimeSpan.Zero)
            ]
        };


        User user = new()

        {
            ID = 50,

            EmailAddress = "dual@test.local",

            SRPPasswordHash = "h",

            SRPPasswordSalt = "s",

            PBKDF2PasswordHash = "h",

            Role = new Role { Name = "User" },

            TotalExperience = 50000
        };

        Account account = new() { ID = 500, Name = "DualPlayer", User = user, IsMain = true };

        user.Accounts.Add(account);


        Mock<IHeroDefinitionService> heroDefinitionsMock = new();

        heroDefinitionsMock.Setup(h => h.GetHeroIdentifier(It.IsAny<uint>())).Returns("Hero_Shaman");

        heroDefinitionsMock.Setup(h => h.GetBaseHeroId(It.IsAny<uint>())).Returns(1u);


        ShowSimpleStatsResponse response = PlayerProfileStatsBuilder.Instance.CreateShowSimpleStatsResponse(
            account,
            agg,
            1,
            heroDefinitionsMock.Object);


        Dictionary<object, object> hybrid = formatter.CreateHybridSimpleStats(response, "campaign");


        // Assert 10-char fixed-width packed match IDs and dates in positional slots 52 and 53 (Lua arg[53] and arg[54])

        string packedIds = Assert.IsType<string>(hybrid[52]);

        string packedDates = Assert.IsType<string>(hybrid[53]);


        Assert.Equal("9999      8888      ", packedIds);

        Assert.Equal("08/14/202608/13/2026", packedDates);


        // Assert combat averages are accessible via both named associative keys and positional slots

        Assert.Equal("150.0", hybrid["avgCreepKills"]); // 300 / 2

        Assert.Equal("30.0", hybrid["avgNeutralKills"]); // 60 / 2

        Assert.Equal("10.0", hybrid["avgDenies"]); // 20 / 2

        Assert.Equal("10.00:1", hybrid["k_d_a"]); // (20 + 20) / 4 = 10.00:1


        Assert.Equal("150.0", hybrid[71]); // Lua Index 72 = PHP Index 71

        Assert.Equal("30.0", hybrid[72]); // Lua Index 73 = PHP Index 72

        Assert.Equal("10.0", hybrid[70]); // Lua Index 71 = PHP Index 70

        Assert.Equal("10.00:1", hybrid[67]); // Lua Index 68 = PHP Index 67


        // Assert mastery info string is present at top-level

        Assert.True(hybrid.ContainsKey("mastery_info"));

        Assert.True(hybrid.ContainsKey("mastery_rewards"));


        // Assert selected upgrades is in slot 88 (Lua arg[89]) and total matches in slot 89 (Lua arg[90])

        Assert.Equal(string.Join("|", response.SelectedStoreItems), hybrid[88]);

        Assert.Equal(2, hybrid[89]);

        Assert.Equal(2, hybrid[90]);

        Assert.Equal(0, hybrid[91]);

        Assert.Equal(0, hybrid[92]);
    }


    [Fact]
    public async Task GetSeasons_ReturnsDualCompatibleFormat_ForLegacyAndV2()

    {
        GetSeasonsHandler handler = new();

        DefaultHttpContext httpContext = new();


        IActionResult result = await handler.HandleRequestAsync(httpContext);

        ContentResult? contentResult = result as ContentResult;


        Assert.NotNull(contentResult);

        Assert.NotNull(contentResult.Content);


        // Deserializing the serialized response to verify structure

        // The serialized response should be a PHP array containing integer key 0, string key "0", and string key "seasons"

        Assert.Contains("i:0;s:", contentResult.Content);

        Assert.Contains("s:1:\"0\";s:", contentResult.Content);

        Assert.Contains("s:7:\"seasons\";s:", contentResult.Content);


        // Verify pipe-separated season items with comma-separated casual indicator

        Assert.Contains("12,false|12,true|", contentResult.Content);

        Assert.Contains("|1,false|1,true", contentResult.Content);
    }


    [Trait("Category", "Manual")]
    [Fact]
    public async Task DualClientCompatibility_GrabLastMatches_ReturnsCommaSeparatedAndHashtableFormats()

    {
        if (!NexusTestEnvironment.IsManualTestEnabled) return;
        await using MerrickContext dbContext = TestDbContextFactory.CreateInMemoryContext();


        User user = new()

        {
            ID = 51,

            EmailAddress = "recent@test.local",

            SRPPasswordHash = "h",

            SRPPasswordSalt = "s",

            PBKDF2PasswordHash = "h",

            Role = new Role { Name = "User" }
        };

        Account account = new() { ID = 510, Name = "RecentGamer", User = user, IsMain = true };

        user.Accounts.Add(account);


        MatchStatistics m1 = CreateTestMatch(10001);

        MatchStatistics m2 = CreateTestMatch(10002);

        PlayerStatistics ps1 = CreateTestPlayerStat(10001, 510, "RecentGamer");

        PlayerStatistics ps2 = CreateTestPlayerStat(10002, 510, "RecentGamer");


        await dbContext.Users.AddAsync(user);

        await dbContext.Accounts.AddAsync(account);

        await dbContext.MatchStatistics.AddRangeAsync(m1, m2);

        await dbContext.PlayerStatistics.AddRangeAsync(ps1, ps2);

        await dbContext.SaveChangesAsync();


        Mock<IDatabase> redisMock = new();

        Mock<IHeroDefinitionService> heroDefinitionsMock = new();


        MatchHistoryHandler handler = new(
            new MatchHistoryRepository(dbContext),
            new AccountRepository(dbContext),
            redisMock.Object,
            heroDefinitionsMock.Object,
            NullLogger<MatchHistoryHandler>.Instance);


        DefaultHttpContext httpContext = new()

        {
            Request =

            {
                Query = new QueryCollection(new Dictionary<string, StringValues>

                {
                    { "f", "grab_last_matches" }, { "nickname", "RecentGamer" }
                })
            }
        };


        IActionResult result = await handler.HandleRequestAsync(httpContext);

        PhpResult? phpResult = result as PhpResult;


        Assert.NotNull(phpResult);

        Dictionary<object, object>? response = phpResult.Data as Dictionary<object, object>;

        Assert.NotNull(response);


        // Verify Hashtable format for modern clients

        Dictionary<string, string>? lastStats = response["last_stats"] as Dictionary<string, string>;

        Assert.NotNull(lastStats);

        Assert.True(lastStats.ContainsKey("10002"));

        Assert.True(lastStats.ContainsKey("10001"));


        // Verify Comma-Separated format in integer 0 (for K2 engine arg[1]) and string "0" / "matches" for legacy match_stats2.lua

        Assert.Equal("10002,10001", response[0]);

        Assert.Equal("10002,10001", response["0"]);

        Assert.Equal("10002,10001", response["matches"]);

        Assert.Equal("True", response["success"]);
    }


    [Trait("Category", "Manual")]
    [Fact]
    public async Task DualClientCompatibility_ClaimMasteryReward_HandlesLegacyCmdAndV2Requests()

    {
        if (!NexusTestEnvironment.IsManualTestEnabled) return;
        await using MerrickContext dbContext = TestDbContextFactory.CreateInMemoryContext();


        User user = new()

        {
            ID = 52,

            EmailAddress = "mastery@test.local",

            SRPPasswordHash = "h",

            SRPPasswordSalt = "s",

            PBKDF2PasswordHash = "h",

            Role = new Role { Name = "User" },

            SilverCoins = 100
        };

        Account account = new() { ID = 520, Name = "MasteryClaimer", User = user, IsMain = true };

        user.Accounts.Add(account);


        await dbContext.Users.AddAsync(user);

        await dbContext.Accounts.AddAsync(account);

        await dbContext.SaveChangesAsync();


        ClaimMasteryRewardHandler handler = new(new AccountRepository(dbContext),
            NullLogger<ClaimMasteryRewardHandler>.Instance);


        // Case 1: Legacy Cmd('ReceiveMasteryReward 1') where nickname is passed via SessionAccountName

        DefaultHttpContext httpContext1 = new()

        {
            Items = { ["SessionAccountName"] = "MasteryClaimer" },

            Request =

            {
                Query = new QueryCollection(new Dictionary<string, StringValues>

                {
                    { "f", "claim_mastery_reward" }, { "level", "1" }
                })
            }
        };


        IActionResult result1 = await handler.HandleRequestAsync(httpContext1);

        PhpResult? phpResult1 = result1 as PhpResult;


        Assert.NotNull(phpResult1);

        Dictionary<string, object>? response1 = phpResult1.Data as Dictionary<string, object>;

        Assert.NotNull(response1);

        Assert.Equal("OK", response1["reward_status"]);

        Assert.Equal(true, response1["reward"]);

        Assert.Equal(true, response1["0"]);


        // Verify that claim token is persisted

        Assert.True(account.User.InventoryItems.Any(i => i.ItemCode == "mastery_reward_lv_0") ||
                    account.User.InventoryItems.Any(i => i.ItemCode == "mastery_reward_lv_1"));


        // Case 2: Attempting to claim again returns ALREADY_CLAIMED

        DefaultHttpContext httpContext2 = new()

        {
            Items = { ["SessionAccountName"] = "MasteryClaimer" },

            Request =

            {
                Query = new QueryCollection(new Dictionary<string, StringValues>

                {
                    { "f", "claim_mastery_reward" }, { "level", "1" }
                })
            }
        };


        IActionResult result2 = await handler.HandleRequestAsync(httpContext2);

        PhpResult? phpResult2 = result2 as PhpResult;

        Assert.NotNull(phpResult2);

        Dictionary<string, object>? response2 = phpResult2.Data as Dictionary<string, object>;

        Assert.NotNull(response2);

        Assert.Equal("ALREADY_CLAIMED", response2["reward_status"]);

        Assert.Equal(false, response2["reward"]);
    }


    [Trait("Category", "Manual")]
    [Fact]
    public async Task DualClientCompatibility_MatchHistoryOverview_SupportsAllTableFilters()

    {
        if (!NexusTestEnvironment.IsManualTestEnabled) return;


        await using MerrickContext dbContext = TestDbContextFactory.CreateInMemoryContext();


        User user = new()

        {
            ID = 53,

            EmailAddress = "overview@test.local",

            SRPPasswordHash = "h",

            SRPPasswordSalt = "s",

            PBKDF2PasswordHash = "h",

            Role = new Role { Name = "User" }
        };

        Account account = new() { ID = 530, Name = "OverviewUser", User = user, IsMain = true };

        user.Accounts.Add(account);


        MatchStatistics rankedMatch = CreateTestMatch(20001);

        PlayerStatistics rankedPlayerStat = CreateTestPlayerStat(20001, 530, "OverviewUser");


        MatchStatistics midwarsMatch = CreateTestMatch(20002, gameMode: "midwars", map: "midwars");

        PlayerStatistics midwarsPlayerStat = CreateTestPlayerStat(20002, 530, "OverviewUser");


        MatchStatistics publicMatch = CreateTestMatch(20003, gameMode: "casual");

        PlayerStatistics publicPlayerStat = CreateTestPlayerStat(20003, 530, "OverviewUser", 0, 1);


        await dbContext.Users.AddAsync(user);

        await dbContext.Accounts.AddAsync(account);

        await dbContext.MatchStatistics.AddRangeAsync(rankedMatch, midwarsMatch, publicMatch);

        await dbContext.PlayerStatistics.AddRangeAsync(rankedPlayerStat, midwarsPlayerStat, publicPlayerStat);

        await dbContext.SaveChangesAsync();


        Mock<IDatabase> redisMock = new();

        Mock<IHeroDefinitionService> heroDefinitionsMock = new();

        heroDefinitionsMock.Setup(h => h.CleanHeroIdentifier(It.IsAny<string>())).Returns("Hero_Shaman");

        heroDefinitionsMock.Setup(h => h.GetBaseHeroId(It.IsAny<string>())).Returns(105u);


        MatchHistoryHandler handler = new(
            new MatchHistoryRepository(dbContext),
            new AccountRepository(dbContext),
            redisMock.Object,
            heroDefinitionsMock.Object,
            NullLogger<MatchHistoryHandler>.Instance);


        // Test 1: table=ranked / campaign

        DefaultHttpContext rankedContext = new()

        {
            Request =

            {
                Query = new QueryCollection(new Dictionary<string, StringValues>

                {
                    { "f", "match_history_overview" }, { "nickname", "OverviewUser" }, { "table", "ranked" }
                })
            }
        };


        IActionResult rankedResult = await handler.HandleRequestAsync(rankedContext);

        PhpResult? rankedPhp = rankedResult as PhpResult;

        Assert.NotNull(rankedPhp);

        MatchHistoryOverviewResponse? rankedResponse = rankedPhp.Data as MatchHistoryOverviewResponse;

        Assert.NotNull(rankedResponse);

        Assert.Single(rankedResponse.Entries);

        Assert.Equal("20001", rankedResponse.Entries[0].MatchID);


        // Test 2: table=midwars

        DefaultHttpContext midwarsContext = new()

        {
            Request =

            {
                Query = new QueryCollection(new Dictionary<string, StringValues>

                {
                    { "f", "match_history_overview" },

                    { "nickname", "OverviewUser" },

                    { "table", "midwars" }
                })
            }
        };


        IActionResult midwarsResult = await handler.HandleRequestAsync(midwarsContext);

        PhpResult? midwarsPhp = midwarsResult as PhpResult;

        Assert.NotNull(midwarsPhp);

        MatchHistoryOverviewResponse? midwarsResponse = midwarsPhp.Data as MatchHistoryOverviewResponse;

        Assert.NotNull(midwarsResponse);

        Assert.Single(midwarsResponse.Entries);

        Assert.Equal("20002", midwarsResponse.Entries[0].MatchID);


        // Test 3: table=history (all matches)

        DefaultHttpContext allContext = new()

        {
            Request =

            {
                Query = new QueryCollection(new Dictionary<string, StringValues>

                {
                    { "f", "match_history_overview" },

                    { "nickname", "OverviewUser" },

                    { "table", "history" }
                })
            }
        };


        IActionResult allResult = await handler.HandleRequestAsync(allContext);

        PhpResult? allPhp = allResult as PhpResult;

        Assert.NotNull(allPhp);

        MatchHistoryOverviewResponse? allResponse = allPhp.Data as MatchHistoryOverviewResponse;

        Assert.NotNull(allResponse);

        Assert.Equal(3, allResponse.Entries.Count);
    }
}
