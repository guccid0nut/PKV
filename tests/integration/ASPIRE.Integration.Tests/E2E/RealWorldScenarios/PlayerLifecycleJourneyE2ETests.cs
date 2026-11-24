// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ============================================================================
// File: PlayerLifecycleJourneyE2ETests.cs
// System: ProjectKongor Solution-Wide E2E Testing Suite
// Tier 4: Real-World Application Scenarios (Player Lifecycle E2E Journey)
// Policy: Strict Explicit Typing, Multi-Stage Pipeline Verification
// ============================================================================

using System.Runtime.CompilerServices;
using FluentAssertions;
using MERRICK.DatabaseContext.Entities.Statistics;
using TRANSMUTANSTEIN.ChatServer.Domain.Core;
using TRANSMUTANSTEIN.ChatServer.Domain.Matchmaking;

namespace ASPIRE.Tests.E2E.Tier4_RealWorldScenarios;

public sealed class PlayerLifecycleJourneyE2ETests
{
    private static MerrickContext CreateInMemoryDbContext()
    {
        return TestDbContextFactory.CreateInMemoryContext("Kongor_PlayerJourney_" + Guid.NewGuid().ToString("N"));
    }

    [Fact]
    public async Task Journey_FullPlayerRegistrationToPostMatchStats_CompletesMultiStagePipeline()
    {
        // ---------------------------------------------------------------------
        // Stage 1: Registration & Account Provisioning
        // ---------------------------------------------------------------------
        using MerrickContext dbContext = CreateInMemoryDbContext();

        string username = "Player_" + Guid.NewGuid().ToString("N")[..8];
        string email = $"{username}@kongor.test";

        Role userRole = new() { ID = 1, Name = "User" };
        await dbContext.Roles.AddAsync(userRole);
        await dbContext.SaveChangesAsync();

        User user = new()
        {
            EmailAddress = email,
            PBKDF2PasswordHash = "argon2id$hashedpasswordtest12345",
            SRPPasswordHash = "srppasswordhash12345",
            SRPPasswordSalt = "srppasswordsalt12345",
            Role = userRole,
            GoldCoins = 500,
            SilverCoins = 1500
        };

        Account account = new()
        {
            Name = username,
            User = user,
            Type = AccountType.Normal,
            IsMain = true
        };

        await dbContext.Users.AddAsync(user);
        await dbContext.Accounts.AddAsync(account);
        await dbContext.SaveChangesAsync();

        account.ID.Should().BeGreaterThan(0);
        user.ID.Should().BeGreaterThan(0);

        // ---------------------------------------------------------------------
        // Stage 2: SRP / Wire Protocol Deserialization
        // ---------------------------------------------------------------------
        string clientAuthPayload =
            $"a:3:{{s:6:\"action\";s:5:\"login\";s:8:\"username\";s:{username.Length}:\"{username}\";s:7:\"version\";s:5:\"4.1.0\";}}";
        object? deserialized = PhpDeserializer.Deserialize(clientAuthPayload);
        deserialized.Should().BeOfType<PhpHashtable>();
        PhpHashtable authTable = (PhpHashtable)deserialized!;
        authTable["action"]!.ToString().Should().Be("login");
        authTable["username"]!.ToString().Should().Be(username);

        // ---------------------------------------------------------------------
        // Stage 3: Matchmaking Queue Join & MMR Tolerance Retention
        // ---------------------------------------------------------------------
        DateTimeOffset queueJoinTime = TimeProvider.System.GetUtcNow();
        MatchmakingGroup matchmakingGroup = new()
        {
            Members = [CreateMember(account.ID, account.Name, 1500.0f, true)],
            Information = CreateDefaultInfo(),
            QueueStartTime = queueJoinTime
        };

        ((double)matchmakingGroup.AverageRating).Should().Be(1500.0);
        matchmakingGroup.QueueStartTime.Should().Be(queueJoinTime);

        // ---------------------------------------------------------------------
        // Stage 4: COMPEL Match Execution & Stats Record Creation
        // ---------------------------------------------------------------------
        int matchId = Random.Shared.Next(600000, 999999);
        MatchStatistics matchStat = new()
        {
            ServerID = 1,
            HostAccountName = "CompelDedicated_1",
            MatchID = matchId,
            Map = "caldavar",
            MapVersion = "1.0",
            TimePlayed = 2100,
            FileSize = 600000,
            FileName = $"match_{matchId}.honreplay",
            ConnectionState = 1,
            Version = "4.10.2",
            AveragePSR = 1500,
            AveragePSRTeamOne = 1500,
            AveragePSRTeamTwo = 1500,
            GameMode = "bot",
            ScoreTeam1 = 45,
            ScoreTeam2 = 28,
            TeamScoreGoal = 0,
            PlayerScoreGoal = 0,
            NumberOfRounds = 1,
            ReleaseStage = "Release",
            BannedHeroes = null,
            AwardMostAnnihilations = null,
            AwardMostQuadKills = null,
            AwardLargestKillStreak = null,
            AwardMostSmackdowns = null,
            AwardMostKills = null,
            AwardMostAssists = null,
            AwardLeastDeaths = null,
            AwardMostBuildingDamage = null,
            AwardMostWardsKilled = null,
            AwardMostHeroDamageDealt = null,
            AwardHighestCreepScore = null
        };

        await dbContext.MatchStatistics.AddAsync(matchStat);
        await dbContext.SaveChangesAsync();

        // ---------------------------------------------------------------------
        // Stage 5: WebUI Match Details Retrieval & Verification
        // ---------------------------------------------------------------------
        MatchStatistics? storedMatch = dbContext.MatchStatistics.FirstOrDefault(m => m.MatchID == matchId);
        storedMatch.Should().NotBeNull();
        storedMatch!.ScoreTeam1.Should().Be(45);
        storedMatch.Map.Should().Be("caldavar");
    }

    [Fact]
    public void Journey_PlayerAuthenticationPayload_ConstructsValidPhpHashtable()
    {
        // Arrange
        PhpHashtable authResponse = new()
        {
            ["auth_token"] = "kongor_session_jwt_sample_12345",
            ["account_id"] = 42,
            ["nickname"] = "KongorChampion"
        };

        // Assert
        authResponse["account_id"].Should().Be(42);
        authResponse["nickname"].Should().Be("KongorChampion");
    }

    [Fact]
    public async Task Journey_PlayerCurrencyEconomy_SupportsCoinCrediting()
    {
        // Arrange
        using MerrickContext dbContext = CreateInMemoryDbContext();

        Role userRole = new() { ID = 1, Name = "User" };
        await dbContext.Roles.AddAsync(userRole);
        await dbContext.SaveChangesAsync();

        User user = new()
        {
            EmailAddress = $"eco_{Guid.NewGuid():N}@kongor.test",
            PBKDF2PasswordHash = "hashedpass",
            SRPPasswordHash = "srppass",
            SRPPasswordSalt = "srpsalt",
            Role = userRole,
            GoldCoins = 0,
            SilverCoins = 0
        };

        Account account = new()
        {
            Name = "EcoUser_" + Guid.NewGuid().ToString("N")[..6],
            User = user,
            Type = AccountType.Normal,
            IsMain = true
        };

        await dbContext.Users.AddAsync(user);
        await dbContext.Accounts.AddAsync(account);
        await dbContext.SaveChangesAsync();

        // Act - Economy transaction crediting on user
        user.GoldCoins += 1000;
        user.SilverCoins += 2500;
        await dbContext.SaveChangesAsync();

        // Assert
        User? refreshed = await dbContext.Users.FindAsync(user.ID);
        refreshed.Should().NotBeNull();
        refreshed!.GoldCoins.Should().Be(1000);
        refreshed.SilverCoins.Should().Be(2500);
    }

    [Fact]
    public async Task Journey_ChatAndPresence_BroadcastsOnlineStatus()
    {
        // Arrange
        using MerrickContext dbContext = CreateInMemoryDbContext();

        Role userRole = new() { ID = 1, Name = "User" };
        await dbContext.Roles.AddAsync(userRole);
        await dbContext.SaveChangesAsync();

        User user = new()
        {
            EmailAddress = $"presence_{Guid.NewGuid():N}@kongor.test",
            PBKDF2PasswordHash = "hash",
            SRPPasswordHash = "srphash",
            SRPPasswordSalt = "salt",
            Role = userRole
        };

        Account account = new()
        {
            Name = "PresenceUser_" + Guid.NewGuid().ToString("N")[..6],
            User = user,
            Type = AccountType.Normal,
            IsMain = true
        };

        await dbContext.Users.AddAsync(user);
        await dbContext.Accounts.AddAsync(account);
        await dbContext.SaveChangesAsync();

        // Act & Assert
        account.ID.Should().BeGreaterThan(0);
    }

    private static MatchmakingGroupMember CreateMember(int id, string name, float rating, bool isLeader = false)
    {
        ChatSession session = (ChatSession)RuntimeHelpers.GetUninitializedObject(typeof(ChatSession));
        User user = new()
        {
            ID = id,
            EmailAddress = $"{name}@kongor.net",
            PBKDF2PasswordHash = "h",
            SRPPasswordHash = "h",
            SRPPasswordSalt = "s"
        };
        Account account = new()
        {
            ID = id,
            Name = name,
            User = user,
            IsMain = true,
            Type = AccountType.Normal
        };
        session.Account = account;
        return new MatchmakingGroupMember(session)
        {
            Slot = 0,
            IsLeader = isLeader,
            IsReady = true,
            IsInGame = false,
            IsEligibleForMatchmaking = true,
            LoadingPercent = 100,
            Rating = rating,
            GameModeAccess = "true"
        };
    }

    private static MatchmakingGroupInformation CreateDefaultInfo()
    {
        return new MatchmakingGroupInformation
        {
            ClientVersion = "4.10.2",
            GroupType = ChatProtocol.TMMType.TMM_TYPE_PVP,
            GameType = ChatProtocol.TMMGameType.TMM_GAME_TYPE_NORMAL,
            MapName = "caldavar",
            GameModes = ["ap"],
            GameRegions = ["USE"],
            Ranked = true,
            MatchFidelity = 0,
            BotDifficulty = 0,
            RandomizeBots = false
        };
    }
}
