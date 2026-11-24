// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Globalization;
using ASPIRE.Common.Models.ServerManagement;
using KONGOR.MasterServer.Configuration;
using KONGOR.MasterServer.Configuration.Economy;
using KONGOR.MasterServer.Models.RequestResponse.Stats;
using KONGOR.MasterServer.Services;
using KONGOR.MasterServer.Services.Background;
using KONGOR.MasterServer.Services.Matches;
using KONGOR.MasterServer.Services.Replay;
using MERRICK.DatabaseContext.Entities.Statistics;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace ASPIRE.Tests.KONGOR.MasterServer.Tests;

public sealed class MatchProcessingServiceTests

{
    private static Dictionary<string, string> CreatePlayerStatDict(
        string nickname,
        int heroId,
        int team,
        int position,
        int kills,
        int deaths,
        int assists)

    {
        return new Dictionary<string, string>

        {
            { "hero_id", heroId.ToString(CultureInfo.InvariantCulture) },

            { "nickname", nickname },

            { "team", team.ToString(CultureInfo.InvariantCulture) },

            { "position", position.ToString(CultureInfo.InvariantCulture) },

            { "group_num", "1" },

            { "benefit", "0" },

            { "wins", kills > deaths ? "1" : "0" },

            { "losses", kills > deaths ? "0" : "1" },

            { "discos", "0" },

            { "concedes", "0" },

            { "kicked", "0" },

            { "social_bonus", "0" },

            { "used_token", "0" },

            { "concedevotes", "0" },

            { "herokills", kills.ToString(CultureInfo.InvariantCulture) },

            { "herodmg", (kills * 1000).ToString(CultureInfo.InvariantCulture) },

            { "herokillsgold", (kills * 200).ToString(CultureInfo.InvariantCulture) },

            { "heroassists", assists.ToString(CultureInfo.InvariantCulture) },

            { "heroexp", "8000" },

            { "deaths", deaths.ToString(CultureInfo.InvariantCulture) },

            { "buybacks", "0" },

            { "goldlost2death", "100" },

            { "secs_dead", "60" },

            { "teamcreepkills", "20" },

            { "teamcreepdmg", "2000" },

            { "teamcreepgold", "500" },

            { "teamcreepexp", "400" },

            { "neutralcreepkills", "10" },

            { "neutralcreepdmg", "1000" },

            { "neutralcreepgold", "250" },

            { "neutralcreepexp", "200" },

            { "bdmg", "1500" },

            { "razed", "1" },

            { "bdmgexp", "300" },

            { "bgold", "400" },

            { "denies", "5" },

            { "exp_denied", "100" },

            { "gold", "12000" },

            { "gold_spent", "11000" },

            { "exp", "9000" },

            { "actions", "1500" },

            { "secs", "1800" },

            { "level", "15" },

            { "consumables", "2" },

            { "wards", "3" },

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
        };
    }


    private static StatsForSubmissionRequestForm CreateSampleSubmissionForm(int matchId, string player1Nick,
        string player2Nick)

    {
        StatsForSubmissionRequestForm form = new()

        {
            Function = "submit_stats",

            Session = "test_session_cookie",

            TeamStats = new Dictionary<int, Dictionary<string, int>>

            {
                { 1, new Dictionary<string, int> { { "score", 30 } } },

                { 2, new Dictionary<string, int> { { "score", 20 } } }
            },

            MatchStats = new Dictionary<string, string>

            {
                { "match_id", matchId.ToString(CultureInfo.InvariantCulture) },

                { "server_id", "1" },

                { "map", "caldavar" },

                { "map_version", "1.0" },

                { "version", "4.10.1" },

                { "time_played", "1800" },

                { "file_size", "2048" },

                { "file_name", $"M{matchId}.honreplay" },

                { "c_state", "0" },

                { "avgpsr", "1500" },

                { "avgpsr_team1", "1500" },

                { "avgpsr_team2", "1500" },

                { "gamemode", "normal" },

                { "teamscoregoal", "0" },

                { "playerscoregoal", "0" },

                { "numrounds", "1" },

                { "release_stage", "Live" },

                { "awd_mann", "0" },

                { "awd_mqk", "0" },

                { "awd_lgks", "0" },

                { "awd_msd", "0" },

                { "awd_mkill", "0" },

                { "awd_masst", "0" },

                { "awd_ledth", "0" },

                { "awd_mbdmg", "0" },

                { "awd_mwk", "0" },

                { "awd_mhdd", "0" },

                { "awd_hcs", "0" },

                { "banned_heroes", "" },

                { "winner", "1" }
            },

            PlayerStats = new Dictionary<int, Dictionary<string, Dictionary<string, string>>>

            {
                {
                    0, new Dictionary<string, Dictionary<string, string>>

                    {
                        { "Hero_Pyromancer", CreatePlayerStatDict(player1Nick, 1, 1, 0, 10, 2, 5) }
                    }
                },

                {
                    1, new Dictionary<string, Dictionary<string, string>>

                    {
                        { "Hero_WitchSlayer", CreatePlayerStatDict(player2Nick, 2, 2, 1, 2, 10, 8) }
                    }
                }
            }
        };


        return form;
    }


    [Fact]
    public async Task ProcessMatchSubmissionAsync_WithValidMatch_PersistsMatchAndPlayerStatistics()

    {
        // Arrange

        await using MerrickContext db = TestDbContextFactory.CreateInMemoryContext();

        const int matchId = 100001;


        Role role = new() { ID = 1, Name = "User" };

        User u1 = new()
        {
            ID = 1, EmailAddress = "p1@test.com", SRPPasswordHash = "h", SRPPasswordSalt = "s",
            PBKDF2PasswordHash = "h", Role = role
        };

        User u2 = new()
        {
            ID = 2, EmailAddress = "p2@test.com", SRPPasswordHash = "h", SRPPasswordSalt = "s",
            PBKDF2PasswordHash = "h", Role = role
        };

        Account a1 = new() { ID = 1, Name = "PyroMaster", IsMain = true, User = u1 };

        Account a2 = new() { ID = 2, Name = "WitchHunter", IsMain = true, User = u2 };

        u1.Accounts = [a1];

        u2.Accounts = [a2];


        await db.Users.AddRangeAsync(u1, u2);

        await db.Accounts.AddRangeAsync(a1, a2);

        await db.SaveChangesAsync();


        Mock<IPlayerStatisticsService> mockStatsService = new();

        Mock<IHeroDefinitionService> mockHeroDefService = new();

        mockHeroDefService.Setup(h => h.GetBaseHeroId("Hero_Pyromancer")).Returns(1);

        mockHeroDefService.Setup(h => h.GetBaseHeroId("Hero_WitchSlayer")).Returns(2);


        Mock<IDatabase> mockRedis = new();

        Mock<IReplayAutoUploadService> mockReplayService = new();


        MatchProcessingService service = new(
            db,
            mockStatsService.Object,
            mockHeroDefService.Object,
            mockRedis.Object,
            NullLogger<MatchProcessingService>.Instance,
            mockReplayService.Object);


        StatsForSubmissionRequestForm form = CreateSampleSubmissionForm(matchId, "PyroMaster", "WitchHunter");

        MatchServer server = new()

        {
            ID = 10,

            HostAccountID = 1,

            HostAccountName = "ServerHost",

            Name = "DedicatedServer",

            Instance = 1,

            IPAddress = "127.0.0.1",

            Port = 11235,

            Location = "Local",

            Description = "Test Server"
        };

        MatchSubmissionPayload payload = new()

        {
            MatchID = matchId,

            Form = form,

            MatchServer = server,

            EnqueuedAt = TimeProvider.System.GetUtcNow()
        };


        // Act

        await service.ProcessMatchSubmissionAsync(payload);


        // Assert

        MatchStatistics? savedMatch = await db.MatchStatistics.FirstOrDefaultAsync(m => m.MatchID == matchId);

        Assert.NotNull(savedMatch);

        Assert.Equal("caldavar", savedMatch.Map);

        Assert.Equal(1800, savedMatch.TimePlayed);


        List<PlayerStatistics> savedPlayerStats =
            await db.PlayerStatistics.Where(p => p.MatchID == matchId).ToListAsync();

        Assert.Equal(2, savedPlayerStats.Count);

        Assert.Contains(savedPlayerStats, p => p.AccountName == "PyroMaster" && p.HeroKills == 10);

        Assert.Contains(savedPlayerStats, p => p.AccountName == "WitchHunter" && p.HeroDeaths == 10);


        mockStatsService.Verify(s => s.InvalidateAggregatedStatisticsAsync(1, "PyroMaster", It.IsAny<CancellationToken>()), Times.Once);

        mockStatsService.Verify(s => s.InvalidateAggregatedStatisticsAsync(2, "WitchHunter", It.IsAny<CancellationToken>()), Times.Once);

        mockReplayService.Verify(r => r.QueueReplayUploadAsync(matchId, null, It.IsAny<CancellationToken>()),
            Times.Once);
    }


    [Fact]
    public async Task ProcessMatchSubmissionAsync_WithExistingMatchId_SkipsDuplicateMatchInsertion()

    {
        // Arrange

        await using MerrickContext db = TestDbContextFactory.CreateInMemoryContext();

        const int matchId = 100002;


        StatsForSubmissionRequestForm form = CreateSampleSubmissionForm(matchId, "PlayerOne", "PlayerTwo");

        MatchStatistics existingMatch = form.ToMatchStatistics(1, "Host");

        await db.MatchStatistics.AddAsync(existingMatch);

        await db.SaveChangesAsync();


        Mock<IPlayerStatisticsService> mockStatsService = new();

        Mock<IHeroDefinitionService> mockHeroDefService = new();

        Mock<IDatabase> mockRedis = new();


        MatchProcessingService service = new(
            db,
            mockStatsService.Object,
            mockHeroDefService.Object,
            mockRedis.Object,
            NullLogger<MatchProcessingService>.Instance);


        MatchServer server = new()

        {
            ID = 1,

            HostAccountID = 1,

            HostAccountName = "Host",

            Name = "Server",

            Instance = 1,

            IPAddress = "127.0.0.1",

            Port = 11235,

            Location = "Local",

            Description = "Test"
        };

        MatchSubmissionPayload payload = new()

        {
            MatchID = matchId,

            Form = form,

            MatchServer = server,

            EnqueuedAt = TimeProvider.System.GetUtcNow()
        };


        // Act

        await service.ProcessMatchSubmissionAsync(payload);


        // Assert

        int matchCount = await db.MatchStatistics.CountAsync(m => m.MatchID == matchId);

        Assert.Equal(1, matchCount);
    }


    [Fact]
    public async Task ProcessMatchSubmissionAsync_WithClanTagsInNicknames_CorrectlyParsesAndLinksClan()

    {
        // Arrange

        await using MerrickContext db = TestDbContextFactory.CreateInMemoryContext();

        const int matchId = 100003;


        Role role = new() { ID = 1, Name = "User" };

        Clan clan = new()
        {
            ID = 50, Name = "AlphaClan", Tag = "ALPHA", TimestampCreated = TimeProvider.System.GetUtcNow().UtcDateTime
        };

        await db.Clans.AddAsync(clan);


        User u1 = new()
        {
            ID = 10, EmailAddress = "clanplayer@test.com", SRPPasswordHash = "h", SRPPasswordSalt = "s",
            PBKDF2PasswordHash = "h", Role = role
        };

        Account a1 = new()
            { ID = 10, Name = "ClanStar", IsMain = true, User = u1, Clan = clan, ClanTier = ClanTier.Leader };

        u1.Accounts = [a1];


        await db.Users.AddAsync(u1);

        await db.Accounts.AddAsync(a1);

        await db.SaveChangesAsync();


        Mock<IPlayerStatisticsService> mockStatsService = new();

        Mock<IHeroDefinitionService> mockHeroDefService = new();

        mockHeroDefService.Setup(h => h.GetBaseHeroId(It.IsAny<string>())).Returns(1);

        Mock<IDatabase> mockRedis = new();


        MatchProcessingService service = new(
            db,
            mockStatsService.Object,
            mockHeroDefService.Object,
            mockRedis.Object,
            NullLogger<MatchProcessingService>.Instance);


        MatchServer server = new()

        {
            ID = 1,

            HostAccountID = 10,

            HostAccountName = "ClanStar",

            Name = "Server",

            Instance = 1,

            IPAddress = "127.0.0.1",

            Port = 11235,

            Location = "Local",

            Description = "Test"
        };


        // Submitting with bracketed clan tag in nickname: "[ALPHA]ClanStar"

        StatsForSubmissionRequestForm form =
            CreateSampleSubmissionForm(matchId, "[ALPHA]ClanStar", "UnregisteredOpponent");

        MatchSubmissionPayload payload = new()

        {
            MatchID = matchId,

            Form = form,

            MatchServer = server,

            EnqueuedAt = TimeProvider.System.GetUtcNow()
        };


        // Act

        await service.ProcessMatchSubmissionAsync(payload);


        // Assert

        PlayerStatistics? playerStats =
            await db.PlayerStatistics.FirstOrDefaultAsync(p => p.MatchID == matchId && p.AccountID == 10);

        Assert.NotNull(playerStats);

        Assert.Equal("ClanStar", playerStats.AccountName);

        Assert.Equal(50, playerStats.ClanID);

        Assert.Equal("ALPHA", playerStats.ClanTag);
    }


    [Fact]
    public async Task ProcessMatchSubmissionAsync_WithNonExistentAccount_SkipsGracefully()

    {
        // Arrange

        await using MerrickContext db = TestDbContextFactory.CreateInMemoryContext();

        const int matchId = 100004;


        Mock<IPlayerStatisticsService> mockStatsService = new();

        Mock<IHeroDefinitionService> mockHeroDefService = new();

        Mock<IDatabase> mockRedis = new();


        MatchProcessingService service = new(
            db,
            mockStatsService.Object,
            mockHeroDefService.Object,
            mockRedis.Object,
            NullLogger<MatchProcessingService>.Instance);


        MatchServer server = new()

        {
            ID = 1,

            HostAccountID = 1,

            HostAccountName = "ServerHost",

            Name = "Server",

            Instance = 1,

            IPAddress = "127.0.0.1",

            Port = 11235,

            Location = "Local",

            Description = "Test"
        };


        StatsForSubmissionRequestForm form = CreateSampleSubmissionForm(matchId, "GhostPlayer1", "GhostPlayer2");

        MatchSubmissionPayload payload = new()

        {
            MatchID = matchId,

            Form = form,

            MatchServer = server,

            EnqueuedAt = TimeProvider.System.GetUtcNow()
        };


        // Act - should complete without throwing

        Exception? exception = await Record.ExceptionAsync(() => service.ProcessMatchSubmissionAsync(payload));


        // Assert

        Assert.Null(exception);

        List<PlayerStatistics> playerStats = await db.PlayerStatistics.Where(p => p.MatchID == matchId).ToListAsync();

        Assert.Empty(playerStats);
    }


    [Trait("Category", "Manual")]
    [Fact]
    public async Task MatchProcessingService_ProcessMatchSubmissionAsync_CreditsCurrencyAndMasteryXpToParticipatingAccounts()

    {
        if (!NexusTestEnvironment.IsManualTestEnabled) return;
        // Arrange

        await using MerrickContext db = TestDbContextFactory.CreateInMemoryContext();

        const int matchId = 100005;


        Role role = new() { ID = 1, Name = "User" };

        User u1 = new()

        {
            ID = 101,

            EmailAddress = "p1_rewards@test.com",

            SRPPasswordHash = "h",

            SRPPasswordSalt = "s",

            PBKDF2PasswordHash = "h",

            Role = role,

            GoldCoins = 0,

            SilverCoins = 0,

            PlinkoTickets = 0,

            TotalExperience = 0,

            TotalLevel = 0
        };

        User u2 = new()

        {
            ID = 102,

            EmailAddress = "p2_rewards@test.com",

            SRPPasswordHash = "h",

            SRPPasswordSalt = "s",

            PBKDF2PasswordHash = "h",

            Role = role,

            GoldCoins = 0,

            SilverCoins = 0,

            PlinkoTickets = 0,

            TotalExperience = 0,

            TotalLevel = 0
        };

        Account a1 = new() { ID = 101, Name = "RewardPyro", IsMain = true, User = u1 };

        Account a2 = new() { ID = 102, Name = "RewardWitch", IsMain = true, User = u2 };

        u1.Accounts = [a1];

        u2.Accounts = [a2];


        await db.Users.AddRangeAsync(u1, u2);

        await db.Accounts.AddRangeAsync(a1, a2);

        await db.SaveChangesAsync();


        Mock<IPlayerStatisticsService> mockStatsService = new();

        Mock<IHeroDefinitionService> mockHeroDefService = new();

        mockHeroDefService.Setup(h => h.GetBaseHeroId("Hero_Pyromancer")).Returns(1);

        mockHeroDefService.Setup(h => h.GetBaseHeroId("Hero_WitchSlayer")).Returns(2);


        Mock<IDatabase> mockRedis = new();


        MatchProcessingService service = new(
            db,
            mockStatsService.Object,
            mockHeroDefService.Object,
            mockRedis.Object,
            NullLogger<MatchProcessingService>.Instance);


        StatsForSubmissionRequestForm form = CreateSampleSubmissionForm(matchId, "RewardPyro", "RewardWitch");

        MatchServer server = new()

        {
            ID = 1,

            HostAccountID = 101,

            HostAccountName = "RewardPyro",

            Name = "Server",

            Instance = 1,

            IPAddress = "127.0.0.1",

            Port = 11235,

            Location = "Local",

            Description = "Test"
        };

        MatchSubmissionPayload payload = new()

        {
            MatchID = matchId,

            Form = form,

            MatchServer = server,

            EnqueuedAt = TimeProvider.System.GetUtcNow()
        };


        // Act

        await service.ProcessMatchSubmissionAsync(payload);


        // Assert - Verify currencies credited directly to User

        User? user1 = await db.Users.Include(u => u.Accounts).FirstOrDefaultAsync(u => u.ID == 101);

        User? user2 = await db.Users.Include(u => u.Accounts).FirstOrDefaultAsync(u => u.ID == 102);


        Assert.NotNull(user1);

        Assert.NotNull(user2);


        Assert.True(user1.GoldCoins > 0, $"Expected user1.GoldCoins > 0 but got {user1.GoldCoins}");

        Assert.True(user1.SilverCoins > 0, $"Expected user1.SilverCoins > 0 but got {user1.SilverCoins}");

        Assert.True(user1.PlinkoTickets > 0, $"Expected user1.PlinkoTickets > 0 but got {user1.PlinkoTickets}");

        Assert.True(user1.TotalExperience > 0, $"Expected user1.TotalExperience > 0 but got {user1.TotalExperience}");


        Assert.True(user2.GoldCoins > 0, $"Expected user2.GoldCoins > 0 but got {user2.GoldCoins}");

        Assert.True(user2.SilverCoins > 0, $"Expected user2.SilverCoins > 0 but got {user2.SilverCoins}");

        Assert.True(user2.PlinkoTickets > 0, $"Expected user2.PlinkoTickets > 0 but got {user2.PlinkoTickets}");

        Assert.True(user2.TotalExperience > 0, $"Expected user2.TotalExperience > 0 but got {user2.TotalExperience}");


        // Winner gets higher rewards than loser

        Assert.True(user1.GoldCoins > user2.GoldCoins, "Winner Gold should exceed loser Gold");

        Assert.True(user1.SilverCoins > user2.SilverCoins, "Winner Silver should exceed loser Silver");


        // Verify Hero Mastery entity and hero experience

        Account? acc1 = await db.Accounts.Include(a => a.Mastery).FirstOrDefaultAsync(a => a.ID == 101);

        Account? acc2 = await db.Accounts.Include(a => a.Mastery).FirstOrDefaultAsync(a => a.ID == 102);


        Assert.NotNull(acc1?.Mastery);

        Assert.NotNull(acc2?.Mastery);


        int pyroXp = acc1.Mastery.GetHeroExperienceByHeroIdentifier("Hero_Pyromancer");

        int witchXp = acc2.Mastery.GetHeroExperienceByHeroIdentifier("Hero_WitchSlayer");


        Assert.True(pyroXp > 0, $"Expected Pyro XP > 0 but got {pyroXp}");

        Assert.True(witchXp > 0, $"Expected Witch XP > 0 but got {witchXp}");
    }


    [Fact]
    public void MatchProcessingService_CalculateCurrencyRewards_CalculatesExpectedBalances()

    {
        MatchRewards config =
            JSONConfiguration.EconomyConfiguration.MatchRewards;


        // Solo Win

        (int GoldCoins, int SilverCoins, int PlinkoTickets) soloWin =
            MatchProcessingService.CalculateCurrencyRewards(config, 1, true, false, 1800);

        Assert.Equal(config.Solo.Win.GoldCoins, soloWin.GoldCoins);

        Assert.Equal(config.Solo.Win.SilverCoins, soloWin.SilverCoins);

        Assert.Equal(config.Solo.Win.PlinkoTickets, soloWin.PlinkoTickets);


        // Solo Loss

        (int GoldCoins, int SilverCoins, int PlinkoTickets) soloLoss =
            MatchProcessingService.CalculateCurrencyRewards(config, 1, false, false, 1800);

        Assert.Equal(config.Solo.Loss.GoldCoins, soloLoss.GoldCoins);

        Assert.Equal(config.Solo.Loss.SilverCoins, soloLoss.SilverCoins);

        Assert.Equal(config.Solo.Loss.PlinkoTickets, soloLoss.PlinkoTickets);


        // MVP Solo Win gets bonus

        (int GoldCoins, int SilverCoins, int PlinkoTickets) mvpWin =
            MatchProcessingService.CalculateCurrencyRewards(config, 1, true, true, 1800);

        Assert.True(mvpWin.GoldCoins >= soloWin.GoldCoins);

        Assert.True(mvpWin.SilverCoins >= soloWin.SilverCoins);

        Assert.True(mvpWin.PlinkoTickets > soloWin.PlinkoTickets);
    }

    [Fact]
    public async Task MatchProcessingService_ProcessMatchSubmissionAsync_UpdatesAccountStatisticsWithOpenSkillBayesianRatings()
    {
        // Arrange
        await using MerrickContext db = TestDbContextFactory.CreateInMemoryContext();
        const int matchId = 100099;

        Role role = new() { ID = 1, Name = "User" };
        User u1 = new() { ID = 1, EmailAddress = "p1@test.com", SRPPasswordHash = "h", SRPPasswordSalt = "s", PBKDF2PasswordHash = "h", Role = role };
        User u2 = new() { ID = 2, EmailAddress = "p2@test.com", SRPPasswordHash = "h", SRPPasswordSalt = "s", PBKDF2PasswordHash = "h", Role = role };
        Account a1 = new() { ID = 1, Name = "LegionHero", IsMain = true, User = u1 };
        Account a2 = new() { ID = 2, Name = "HellbourneHero", IsMain = true, User = u2 };
        u1.Accounts = [a1];
        u2.Accounts = [a2];

        await db.Users.AddRangeAsync(u1, u2);
        await db.Accounts.AddRangeAsync(a1, a2);
        await db.SaveChangesAsync();

        Mock<IPlayerStatisticsService> mockStatsService = new();
        Mock<IHeroDefinitionService> mockHeroDefService = new();
        mockHeroDefService.Setup(h => h.GetBaseHeroId("Hero_Pyromancer")).Returns(1);
        mockHeroDefService.Setup(h => h.GetBaseHeroId("Hero_WitchSlayer")).Returns(2);
        Mock<IDatabase> mockRedis = new();
        Mock<IReplayAutoUploadService> mockReplayService = new();

        MatchProcessingService service = new(
            db,
            mockStatsService.Object,
            mockHeroDefService.Object,
            mockRedis.Object,
            NullLogger<MatchProcessingService>.Instance,
            mockReplayService.Object);

        StatsForSubmissionRequestForm form = CreateSampleSubmissionForm(matchId, "LegionHero", "HellbourneHero");
        MatchServer server = new()
        {
            ID = 10, HostAccountID = 1, HostAccountName = "ServerHost", Name = "DedicatedServer",
            Instance = 1, IPAddress = "127.0.0.1", Port = 11235, Location = "Local", Description = "Test Server"
        };
        MatchSubmissionPayload payload = new()
        {
            MatchID = matchId, Form = form, MatchServer = server, EnqueuedAt = TimeProvider.System.GetUtcNow()
        };

        // Act
        await service.ProcessMatchSubmissionAsync(payload);

        // Assert
        AccountStatistics? legionStats = await db.AccountStatistics
            .FirstOrDefaultAsync(s => s.AccountID == 1 && s.Type == AccountStatisticsType.Matchmaking);
        AccountStatistics? hellbourneStats = await db.AccountStatistics
            .FirstOrDefaultAsync(s => s.AccountID == 2 && s.Type == AccountStatisticsType.Matchmaking);

        Assert.NotNull(legionStats);
        Assert.NotNull(hellbourneStats);

        // Winner (Team 1) gained rating, loser (Team 2) lost rating
        Assert.True(legionStats.SkillRating > 1500.0, $"Expected Legion PSR > 1500.0, got {legionStats.SkillRating}");
        Assert.True(legionStats.Mu > 25.0, $"Expected Legion Mu > 25.0, got {legionStats.Mu}");
        Assert.True(legionStats.Sigma < 25.0 / 3.0, $"Expected Legion Sigma to collapse, got {legionStats.Sigma}");
        Assert.Equal(1, legionStats.MatchesWon);
        Assert.Equal(1, legionStats.MatchesPlayed);

        Assert.True(hellbourneStats.Mu < 25.0, $"Expected Hellbourne Mu < 25.0, got {hellbourneStats.Mu}");
        Assert.Equal(1, hellbourneStats.MatchesLost);
        Assert.Equal(1, hellbourneStats.MatchesPlayed);

        PlayerStatistics? legionPlayerStat = await db.PlayerStatistics
            .FirstOrDefaultAsync(p => p.MatchID == matchId && p.AccountID == 1);
        Assert.NotNull(legionPlayerStat);
        Assert.True(legionPlayerStat.RankedSkillRatingChange > 0.0, $"Expected positive RankedSkillRatingChange, got {legionPlayerStat.RankedSkillRatingChange}");
    }
}
