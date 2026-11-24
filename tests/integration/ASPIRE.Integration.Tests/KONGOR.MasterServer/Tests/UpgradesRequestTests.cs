// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2022-2026 [K]ONGOR and Contributors
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV) -- Upstream lineage: https://github.com/Project-KONGOR-Open-Source

using KONGOR.MasterServer.Extensions.Cache;
using KONGOR.MasterServer.Services;
using KONGOR.MasterServer.Services.Requester;
using MERRICK.DatabaseContext.Entities.Statistics;
using MERRICK.DatabaseContext.Entities.Store;
using EntityRole = MERRICK.DatabaseContext.Entities.Utility.Role;


namespace ASPIRE.Tests.KONGOR.MasterServer.Tests;

public sealed class UpgradesRequestTests(KONGORTestFixture fixture) : IClassFixture<KONGORTestFixture>

{
    private async
        Task<(HttpClient Client, MerrickContext DbContext, IDatabase Cache, string Cookie)> SetupAsync(
            string? accountName = null)

    {
        HttpClient client = fixture.Client;

        IServiceScope scope = fixture.CreateScope();

        MerrickContext dbContext = scope.ServiceProvider.GetRequiredService<MerrickContext>();

        IDatabase cache = scope.ServiceProvider.GetRequiredService<IDatabase>();


        string cookie = Guid.NewGuid().ToString("N");

        string name = accountName ?? $"TestUser_{Guid.NewGuid().ToString("N")[..8]}";


        User user = new()

        {
            EmailAddress = $"{name}@kongor.net",

            PBKDF2PasswordHash = "hash",

            SRPPasswordHash = "hash",

            SRPPasswordSalt = "salt",

            Role = new EntityRole { Name = UserRoles.User },

            InventoryItems =
            [
                new UserInventory { ItemCode = "item1", GrantSource = "Test" },
                new UserInventory { ItemCode = "item2", GrantSource = "Test" }
            ]
        };


        Account account = new() { Name = name, User = user, Type = AccountType.Staff, IsMain = true };


        await dbContext.Users.AddAsync(user);

        await dbContext.Accounts.AddAsync(account);

        await dbContext.SaveChangesAsync();


        // Seed some stats to verify aggregation logic

        PlayerStatistics stats1 = CreateDefaultPlayerStatistics(account.ID, 101);

        stats1.RankedMatch = 1;

        stats1.RankedSkillRatingChange = 10;

        stats1.HeroKills = 5;

        stats1.HeroDeaths = 2;

        stats1.SecondsPlayed = 1800;

        stats1.Experience = 1000;

        stats1.Gold = 500;


        PlayerStatistics stats2 = CreateDefaultPlayerStatistics(account.ID, 102);

        stats2.PublicMatch = 1;

        stats2.PublicSkillRatingChange = 5;

        stats2.HeroKills = 10;

        stats2.HeroDeaths = 0;

        stats2.SecondsPlayed = 1200;

        stats2.Experience = 800;

        stats2.Gold = 400;


        await dbContext.PlayerStatistics.AddRangeAsync(stats1, stats2);

        await dbContext.SaveChangesAsync();


        await cache.SetAccountNameForSessionCookie(cookie, account.Name);

        await cache.KeyDeleteAsync($"stats:aggregated:{account.ID}");


        return (client, dbContext, cache, cookie);
    }


    [Trait("Category", "Manual")]
    [Fact]
    public async Task GetUpgrades_SchemaParity_Returns46Fields()

    {
        if (!NexusTestEnvironment.IsManualTestEnabled) return;
        string uniqueName = $"SchemaUser_{Guid.NewGuid():N}"[..14];

        (HttpClient client, _, _, string cookie) = await SetupAsync(uniqueName);


        Dictionary<string, string> payload = new() { { "f", "get_upgrades" }, { "cookie", cookie } };

        FormUrlEncodedContent content = new(payload);


        HttpResponseMessage response = await client.PostAsync("client_requester.php", content);


        response.EnsureSuccessStatusCode();

        string body = await response.Content.ReadAsStringAsync();


        // Verify Root Keys

        string[] requiredRootKeys =

        [
            "field_stats", "standing", "points", "mmpoints", "0"
        ];

        foreach (string key in requiredRootKeys) Assert.Contains(key, body);


        // Verify Integer Typing for specific fields (Parity with upstream [K]ONGOR NEXUS)

        Assert.Contains("\"standing\";i:3;", body);

        Assert.Contains("\"season_level\";i:0;", body);

        Assert.Contains("\"creep_level\";i:0;", body);


        // Verify Field Stats Schema (The 23 Fields)

        string[] requiredStatKeys =

        [
            "account_id", "level", "level_exp", "acc_pub_skill", "rnk_amm_team_rating", "cs_amm_team_rating",

            "acc_games_played", "acc_discos", "rnk_games_played", "rnk_discos", "cs_games_played", "cs_discos",

            "mid_games_played", "mid_discos", "rift_games_played", "rift_discos", "cam_games_played", "cam_discos",

            "cam_cs_games_played", "cam_cs_discos", "acc_trial_games_played", "standing", "is_new"
        ];


        foreach (string statKey in requiredStatKeys) Assert.Contains($"\"{statKey}\";", body);


        // Verify Integer Fields in Field Stats

        Assert.Contains("\"mid_discos\";i:0;", body);

        Assert.Contains("\"rift_discos\";i:0;", body);

        Assert.Contains("\"cam_discos\";i:0;", body);


        // --- Dynamic Verification via ClientRequestHelper ---

        using IServiceScope testScope = fixture.CreateScope();

        MerrickContext dbContextScope = testScope.ServiceProvider.GetRequiredService<MerrickContext>();

        IPlayerStatisticsService statsService =
            testScope.ServiceProvider.GetRequiredService<IPlayerStatisticsService>();

        IHeroDefinitionService heroDefinitions =
            testScope.ServiceProvider.GetRequiredService<IHeroDefinitionService>();


        IDatabase cacheStore = testScope.ServiceProvider.GetRequiredService<IDatabase>();

        string? accountName = await cacheStore.GetAccountNameForSessionCookie(cookie);

        Account account = await dbContextScope.Accounts
            .Include(a => a.User)
            .Include(a => a.Clan)
            .FirstAsync(a => a.Name == accountName);


        PlayerStatisticsAggregatedDTO stats = await statsService.GetAggregatedStatisticsAsync(account.ID);


        ShowSimpleStatsResponse fullStats =
            ClientRequestHelper.CreateShowSimpleStatsResponse(account, stats, 12, heroDefinitions);


        // Assert Response matches Helper Logic for Level (integer serialization)

        Assert.Contains($"\"level\";i:{fullStats.Level};", body);

        Assert.Contains(
            $"\"level_exp\";s:{fullStats.LevelExperience.ToString().Length}:\"{fullStats.LevelExperience}\";",
            body);
    }


    private static PlayerStatistics CreateDefaultPlayerStatistics(int accountId, int matchId)

    {
        return new PlayerStatistics

        {
            MatchID = matchId,

            AccountID = accountId,

            AccountName = "Test",

            ClanID = null,

            ClanTag = null,

            Team = 1,

            LobbyPosition = 1,

            GroupNumber = 0,

            Benefit = 0,

            HeroProductID = 0,

            MVP = 0,

            Inventory = [],

            Win = 0,

            Loss = 0,

            Disconnected = 0,

            Conceded = 0,

            Kicked = 0,

            PublicMatch = 0,

            PublicSkillRatingChange = 0,

            RankedMatch = 0,

            RankedSkillRatingChange = 0,

            SocialBonus = 0,

            UsedToken = 0,

            ConcedeVotes = 0,

            HeroKills = 0,

            HeroDamage = 0,

            GoldFromHeroKills = 0,

            HeroAssists = 0,

            HeroExperience = 0,

            HeroDeaths = 0,

            Buybacks = 0,

            GoldLostToDeath = 0,

            SecondsDead = 0,

            TeamCreepKills = 0,

            TeamCreepDamage = 0,

            TeamCreepGold = 0,

            TeamCreepExperience = 0,

            NeutralCreepKills = 0,

            NeutralCreepDamage = 0,

            NeutralCreepGold = 0,

            NeutralCreepExperience = 0,

            BuildingDamage = 0,

            BuildingsRazed = 0,

            ExperienceFromBuildings = 0,

            GoldFromBuildings = 0,

            Denies = 0,

            ExperienceDenied = 0,

            Gold = 0,

            GoldSpent = 0,

            Experience = 0,

            Actions = 0,

            SecondsPlayed = 0,

            HeroLevel = 1,

            ConsumablesPurchased = 0,

            WardsPlaced = 0,

            FirstBlood = 0,

            DoubleKill = 0,

            TripleKill = 0,

            QuadKill = 0,

            Annihilation = 0,

            KillStreak03 = 0,

            KillStreak04 = 0,

            KillStreak05 = 0,

            KillStreak06 = 0,

            KillStreak07 = 0,

            KillStreak08 = 0,

            KillStreak09 = 0,

            KillStreak10 = 0,

            KillStreak15 = 0,

            Smackdown = 0,

            Humiliation = 0,

            Nemesis = 0,

            Retribution = 0,

            Score = 0,

            GameplayStat0 = 0,

            GameplayStat1 = 0,

            GameplayStat2 = 0,

            GameplayStat3 = 0,

            GameplayStat4 = 0,

            GameplayStat5 = 0,

            GameplayStat6 = 0,

            GameplayStat7 = 0,

            GameplayStat8 = 0,

            GameplayStat9 = 0,

            TimeEarningExperience = 0
        };
    }
}
