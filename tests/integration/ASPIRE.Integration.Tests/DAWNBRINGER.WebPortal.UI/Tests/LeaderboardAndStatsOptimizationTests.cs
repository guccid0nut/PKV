// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2022-2026 [K]ONGOR and Contributors
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV) -- Upstream lineage: https://github.com/Project-KONGOR-Open-Source

using DAWNBRINGER.WebPortal.UI.Controllers;
using MERRICK.DatabaseContext.Entities.Statistics;
using MERRICK.DatabaseContext.Queries;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using NEXUS.Contracts.Clients;
using LeaderboardEntryDto = DAWNBRINGER.WebPortal.UI.Controllers.LeaderboardEntryDto;

namespace ASPIRE.Tests.DAWNBRINGER.WebPortal.UI.Tests;

public sealed class LeaderboardAndStatsOptimizationTests
{
    private static MerrickContext CreateInMemoryContext()
    {
        return TestDbContextFactory.CreateInMemoryContext();
    }

    private static HomeController CreateController(MerrickContext context)
    {
        IConfiguration config = new ConfigurationBuilder().Build();
        IUserApiClient userApi = TestApiClientFactory.CreateUserApiClient(context);
        IAdminApiClient adminApi = TestApiClientFactory.CreateAdminApiClient(context);
        IPlayerStatsApiClient statsApi = TestApiClientFactory.CreatePlayerStatsApiClient(context);
        IGuideApiClient guideApi = TestApiClientFactory.CreateGuideApiClient(context);
        HomeController controller = new(statsApi, userApi, guideApi, adminApi, config)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };
        return controller;
    }

    [Fact]
    public async Task Leaderboard_GlobalMode_CorrectlyAggregatesAndResolvesTopHeroes()
    {
        await using MerrickContext context = CreateInMemoryContext();

        Role userRole = new() { ID = 1, Name = "User" };

        User user1 = new()
        {
            ID = 1,
            EmailAddress = "player1@test.com",
            Role = userRole,
            SRPPasswordHash = "hash1",
            SRPPasswordSalt = "salt1",
            PBKDF2PasswordHash = "pbkdf2_1"
        };
        User user2 = new()
        {
            ID = 2,
            EmailAddress = "player2@test.com",
            Role = userRole,
            SRPPasswordHash = "hash2",
            SRPPasswordSalt = "salt2",
            PBKDF2PasswordHash = "pbkdf2_2"
        };

        Account account1 = new()
        {
            ID = 101,
            Name = "AlphaPlayer",
            User = user1,
            IsMain = true,
            Type = AccountType.Normal
        };
        Account account2 = new()
        {
            ID = 102,
            Name = "BetaPlayer",
            User = user2,
            IsMain = true,
            Type = AccountType.Normal
        };

        AccountStatistics stat1 = new()
        {
            AccountID = 101,
            Account = account1,
            Type = AccountStatisticsType.Matchmaking,
            MatchesPlayed = 20,
            MatchesWon = 15,
            MatchesLost = 5,
            SkillRating = 1850
        };

        AccountStatistics stat2 = new()
        {
            AccountID = 102,
            Account = account2,
            Type = AccountStatisticsType.Matchmaking,
            MatchesPlayed = 10,
            MatchesWon = 9,
            MatchesLost = 1,
            SkillRating = 1900
        };

        account1.Statistics.Add(stat1);
        account2.Statistics.Add(stat2);

        PlayerStatistics ps1 = new()
        {
            MatchID = 1,
            AccountID = 101,
            AccountName = "AlphaPlayer",
            HeroIdentifier = "Hero_Pyromancer",
            HeroProductID = 1,
            MVP = 1,
            Team = 1,
            LobbyPosition = 0,
            GroupNumber = 0,
            Benefit = 0,
            Win = 1,
            Loss = 0,
            Disconnected = 0,
            Conceded = 0,
            Kicked = 0,
            PublicMatch = 0,
            PublicSkillRatingChange = 0,
            RankedMatch = 1,
            RankedSkillRatingChange = 10,
            SocialBonus = 0,
            UsedToken = 0,
            ConcedeVotes = 0,
            HeroKills = 10,
            HeroDamage = 15000,
            GoldFromHeroKills = 2000,
            HeroAssists = 5,
            HeroExperience = 10000,
            HeroDeaths = 2,
            Buybacks = 0,
            GoldLostToDeath = 0,
            SecondsDead = 30,
            TeamCreepKills = 50,
            TeamCreepDamage = 20000,
            TeamCreepGold = 3000,
            TeamCreepExperience = 5000,
            NeutralCreepKills = 0,
            NeutralCreepDamage = 0,
            NeutralCreepGold = 0,
            NeutralCreepExperience = 0,
            BuildingDamage = 1000,
            BuildingsRazed = 1,
            ExperienceFromBuildings = 500,
            GoldFromBuildings = 500,
            Denies = 10,
            ExperienceDenied = 200,
            Gold = 500,
            GoldSpent = 4500,
            Experience = 15000,
            Actions = 2000,
            SecondsPlayed = 1800,
            HeroLevel = 16,
            ConsumablesPurchased = 5,
            WardsPlaced = 2,
            FirstBlood = 1,
            DoubleKill = 1,
            TripleKill = 0,
            QuadKill = 0,
            Annihilation = 0,
            KillStreak03 = 1,
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
            Score = 100,
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
            TimeEarningExperience = 1800,
            ClanID = null,
            ClanTag = null,
            Inventory = []
        };

        context.Roles.Add(userRole);
        context.Users.AddRange(user1, user2);
        context.Accounts.AddRange(account1, account2);
        context.AccountStatistics.AddRange(stat1, stat2);
        context.PlayerStatistics.Add(ps1);
        await context.SaveChangesAsync();

        HomeController controller = CreateController(context);
        IActionResult result = await controller.Leaderboard();

        ViewResult viewResult = Assert.IsType<ViewResult>(result);
        List<LeaderboardEntryDto> entries = Assert.IsType<List<LeaderboardEntryDto>>(viewResult.Model);

        Assert.Equal(2, entries.Count);
        Assert.Equal("BetaPlayer", entries[0].AccountName);
        Assert.Equal(90.0, entries[0].WinRate);
        Assert.Equal(1, entries[0].Rank);

        Assert.Equal("AlphaPlayer", entries[1].AccountName);
        Assert.Equal(75.0, entries[1].WinRate);
        Assert.Equal(2, entries[1].Rank);
        Assert.Equal("Pyromancer", entries[1].TopHeroName);
    }

    [Fact]
    public async Task Stats_ProjectedQuery_CalculatesAggregateStatisticsAndTop5Heroes()
    {
        await using MerrickContext context = CreateInMemoryContext();

        Role userRole = new() { ID = 1, Name = "User" };

        User user = new()
        {
            ID = 1,
            EmailAddress = "test@user.com",
            Role = userRole,
            SRPPasswordHash = "hash",
            SRPPasswordSalt = "salt",
            PBKDF2PasswordHash = "pbkdf2"
        };
        Account account = new()
        {
            ID = 201,
            Name = "ProGamer",
            User = user,
            IsMain = true,
            Type = AccountType.Normal
        };
        AccountStatistics matchStats = new()
        {
            AccountID = 201,
            Account = account,
            Type = AccountStatisticsType.Matchmaking,
            MatchesPlayed = 3,
            MatchesWon = 2,
            MatchesLost = 1,
            SkillRating = 1750
        };
        account.Statistics.Add(matchStats);

        PlayerStatistics m1 = new()
        {
            MatchID = 10,
            AccountID = 201,
            AccountName = "ProGamer",
            HeroIdentifier = "Hero_Devourer",
            HeroProductID = 1,
            MVP = 1,
            Team = 1,
            LobbyPosition = 0,
            GroupNumber = 0,
            Benefit = 0,
            Win = 1,
            Loss = 0,
            Disconnected = 0,
            Conceded = 0,
            Kicked = 0,
            PublicMatch = 0,
            PublicSkillRatingChange = 0,
            RankedMatch = 1,
            RankedSkillRatingChange = 10,
            SocialBonus = 0,
            UsedToken = 0,
            ConcedeVotes = 0,
            HeroKills = 8,
            HeroDamage = 10000,
            GoldFromHeroKills = 1500,
            HeroAssists = 4,
            HeroExperience = 8000,
            HeroDeaths = 2,
            Buybacks = 0,
            GoldLostToDeath = 0,
            SecondsDead = 30,
            TeamCreepKills = 40,
            TeamCreepDamage = 10000,
            TeamCreepGold = 2000,
            TeamCreepExperience = 4000,
            NeutralCreepKills = 0,
            NeutralCreepDamage = 0,
            NeutralCreepGold = 0,
            NeutralCreepExperience = 0,
            BuildingDamage = 500,
            BuildingsRazed = 0,
            ExperienceFromBuildings = 200,
            GoldFromBuildings = 200,
            Denies = 5,
            ExperienceDenied = 100,
            Gold = 400,
            GoldSpent = 3000,
            Experience = 10000,
            Actions = 1500,
            SecondsPlayed = 1500,
            HeroLevel = 14,
            ConsumablesPurchased = 2,
            WardsPlaced = 1,
            FirstBlood = 0,
            DoubleKill = 1,
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
            Score = 80,
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
            TimeEarningExperience = 1500,
            ClanID = null,
            ClanTag = null,
            Inventory = []
        };

        PlayerStatistics m2 = new()
        {
            MatchID = 11,
            AccountID = 201,
            AccountName = "ProGamer",
            HeroIdentifier = "Hero_Devourer",
            HeroProductID = 1,
            MVP = 0,
            Team = 1,
            LobbyPosition = 0,
            GroupNumber = 0,
            Benefit = 0,
            Win = 1,
            Loss = 0,
            Disconnected = 0,
            Conceded = 0,
            Kicked = 0,
            PublicMatch = 0,
            PublicSkillRatingChange = 0,
            RankedMatch = 1,
            RankedSkillRatingChange = 10,
            SocialBonus = 0,
            UsedToken = 0,
            ConcedeVotes = 0,
            HeroKills = 12,
            HeroDamage = 16000,
            GoldFromHeroKills = 2500,
            HeroAssists = 6,
            HeroExperience = 12000,
            HeroDeaths = 3,
            Buybacks = 0,
            GoldLostToDeath = 0,
            SecondsDead = 50,
            TeamCreepKills = 60,
            TeamCreepDamage = 18000,
            TeamCreepGold = 3500,
            TeamCreepExperience = 6000,
            NeutralCreepKills = 0,
            NeutralCreepDamage = 0,
            NeutralCreepGold = 0,
            NeutralCreepExperience = 0,
            BuildingDamage = 1200,
            BuildingsRazed = 1,
            ExperienceFromBuildings = 600,
            GoldFromBuildings = 600,
            Denies = 8,
            ExperienceDenied = 150,
            Gold = 600,
            GoldSpent = 5000,
            Experience = 14000,
            Actions = 2200,
            SecondsPlayed = 2000,
            HeroLevel = 17,
            ConsumablesPurchased = 3,
            WardsPlaced = 0,
            FirstBlood = 1,
            DoubleKill = 2,
            TripleKill = 1,
            QuadKill = 0,
            Annihilation = 0,
            KillStreak03 = 1,
            KillStreak04 = 1,
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
            Score = 120,
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
            TimeEarningExperience = 2000,
            ClanID = null,
            ClanTag = null,
            Inventory = []
        };

        context.Roles.Add(userRole);
        context.Users.Add(user);
        context.Accounts.Add(account);
        context.AccountStatistics.Add(matchStats);
        context.PlayerStatistics.AddRange(m1, m2);
        await context.SaveChangesAsync();

        HomeController controller = CreateController(context);
        IActionResult result = await controller.Stats("ProGamer");

        ViewResult viewResult = Assert.IsType<ViewResult>(result);
        Assert.Equal(2, controller.ViewData["TotalMatches"]);
        Assert.Equal(100.0, controller.ViewData["WinRate"]);
        Assert.Equal(4.0, controller.ViewData["KDR"]); // 20 kills / 5 deaths = 4.0

        List<HeroPerformanceDto>? topHeroes = controller.ViewData["TopHeroes"] as List<HeroPerformanceDto>;
        Assert.NotNull(topHeroes);
        Assert.Single(topHeroes);
        Assert.Equal("Devourer", topHeroes[0].HeroCode);
        Assert.Equal(2, topHeroes[0].MatchesPlayed);
        Assert.Equal(100.0, topHeroes[0].WinRate);
    }

    [Fact]
    public async Task CompiledQueries_AccountAndUserLookups_ExecuteAccurately()
    {
        await using MerrickContext context = CreateInMemoryContext();

        Role userRole = new() { ID = 1, Name = "User" };

        User user = new()
        {
            ID = 10,
            EmailAddress = "compiled@test.com",
            Role = userRole,
            SRPPasswordHash = "hash",
            SRPPasswordSalt = "salt",
            PBKDF2PasswordHash = "pbkdf2"
        };
        Account account = new()
        {
            ID = 301,
            Name = "CompiledAccount",
            User = user,
            IsMain = true,
            Type = AccountType.Normal
        };

        context.Roles.Add(userRole);
        context.Users.Add(user);
        context.Accounts.Add(account);
        await context.SaveChangesAsync();

        bool accountExists = await CompiledQueries.AccountExistsByNameQuery(context, "CompiledAccount");
        bool accountMissing = await CompiledQueries.AccountExistsByNameQuery(context, "MissingAccount");
        Assert.True(accountExists);
        Assert.False(accountMissing);

        bool userExists = await CompiledQueries.UserExistsByEmailQuery(context, "compiled@test.com");
        bool userMissing = await CompiledQueries.UserExistsByEmailQuery(context, "missing@test.com");
        Assert.True(userExists);
        Assert.False(userMissing);

        Account? queriedAccount =
            await CompiledQueries.GetAccountByNameWithUserAndClanQuery(context, "CompiledAccount");
        Assert.NotNull(queriedAccount);
        Assert.Equal(301, queriedAccount.ID);
        Assert.NotNull(queriedAccount.User);
        Assert.Equal("compiled@test.com", queriedAccount.User.EmailAddress);

        User? queriedUser =
            await CompiledQueries.GetUserByEmailWithAccountsTrackingQuery(context, "compiled@test.com");
        Assert.NotNull(queriedUser);
        Assert.Single(queriedUser.Accounts);
        Assert.Equal("CompiledAccount", queriedUser.Accounts[0].Name);
    }
}
