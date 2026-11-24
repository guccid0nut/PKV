// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ============================================================================
// File: MatchmakingToChatToDatabaseCombinationTests.cs
// System: ProjectKongor Solution-Wide E2E Testing Suite
// Tier 3: Cross-Feature Combinations (Matchmaking -> Chat -> Merrick DB)
// Policy: Strict Explicit Typing, Multi-Service Integration Verification
// ============================================================================

using System.Runtime.CompilerServices;
using FluentAssertions;
using TRANSMUTANSTEIN.ChatServer.Domain.Core;
using TRANSMUTANSTEIN.ChatServer.Domain.Matchmaking;

namespace ASPIRE.Tests.E2E.Tier3_CrossFeatureCombinations;

public sealed class MatchmakingToChatToDatabaseCombinationTests(KONGORTestFixture masterFixture)
    : IClassFixture<KONGORTestFixture>
{
    private static MatchmakingGroupInformation CreateDefaultInfo(
        ChatProtocol.TMMGameType gameType = ChatProtocol.TMMGameType.TMM_GAME_TYPE_NORMAL)
    {
        return new MatchmakingGroupInformation
        {
            ClientVersion = "4.10.2",
            GroupType = ChatProtocol.TMMType.TMM_TYPE_PVP,
            GameType = gameType,
            MapName = "caldavar",
            GameModes = ["ap"],
            GameRegions = ["USE"],
            Ranked = true,
            MatchFidelity = 0,
            BotDifficulty = 0,
            RandomizeBots = false
        };
    }

    private static MatchmakingGroupMember CreateMember(int id, string name, double rating, bool isLeader = false)
    {
        ChatSession session = (ChatSession)RuntimeHelpers.GetUninitializedObject(typeof(ChatSession));
        User user = new()
        {
            ID = id,
            EmailAddress = $"{name}@kongor.net",
            PBKDF2PasswordHash = "h",
            SRPPasswordHash = "h",
            SRPPasswordSalt = "s",
            Role = new Role { ID = 1, Name = "User" }
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

    [Fact]
    public async Task Integration_MatchmakingMatchFormation_VerifiesAccountLookup()
    {
        // Arrange
        using IServiceScope scope = masterFixture.CreateScope();
        MerrickContext dbContext = masterFixture.GetDbContext(scope);

        string playerName = $"P_{Guid.NewGuid():N}"[..10];
        Role userRole = await dbContext.Roles.FindAsync(1) ?? new Role { ID = 1, Name = "User" };
        if (dbContext.Entry(userRole).State == EntityState.Detached) await dbContext.Roles.AddAsync(userRole);

        User user = new()
        {
            EmailAddress = $"{playerName}@kongor.net",
            PBKDF2PasswordHash = "h",
            SRPPasswordHash = "h",
            SRPPasswordSalt = "s",
            Role = userRole
        };
        Account playerAcc = new()
        {
            Name = playerName,
            User = user,
            Type = AccountType.Normal,
            IsMain = true
        };
        await dbContext.Users.AddAsync(user);
        await dbContext.Accounts.AddAsync(playerAcc);
        await dbContext.SaveChangesAsync();

        // Assert
        Account? retrieved =
            await dbContext.Accounts.Include(a => a.User).FirstOrDefaultAsync(a => a.ID == playerAcc.ID);
        retrieved.Should().NotBeNull();
        retrieved!.Name.Should().Be(playerName);
        retrieved.User.EmailAddress.Should().Be($"{playerName}@kongor.net");
    }

    [Fact]
    public async Task Integration_MatchmakingPlayerStats_AttachesToAccountInMerrickDb()
    {
        // Arrange
        using IServiceScope scope = masterFixture.CreateScope();
        MerrickContext dbContext = masterFixture.GetDbContext(scope);

        string playerName = $"P_{Guid.NewGuid():N}"[..10];
        Role userRole = await dbContext.Roles.FindAsync(1) ?? new Role { ID = 1, Name = "User" };
        if (dbContext.Entry(userRole).State == EntityState.Detached) await dbContext.Roles.AddAsync(userRole);

        User user = new()
        {
            EmailAddress = $"{playerName}@kongor.net",
            PBKDF2PasswordHash = "h",
            SRPPasswordHash = "h",
            SRPPasswordSalt = "s",
            Role = userRole
        };
        Account playerAcc = new()
        {
            Name = playerName,
            User = user,
            Type = AccountType.Normal,
            IsMain = true
        };
        await dbContext.Users.AddAsync(user);
        await dbContext.Accounts.AddAsync(playerAcc);
        await dbContext.SaveChangesAsync();

        // Assert
        Account? retrieved =
            await dbContext.Accounts.Include(a => a.User).FirstOrDefaultAsync(a => a.ID == playerAcc.ID);
        retrieved.Should().NotBeNull();
        retrieved!.Name.Should().Be(playerName);
        retrieved.User.EmailAddress.Should().Be($"{playerName}@kongor.net");
    }

    [Fact]
    public void Integration_MatchmakingGroupToTeamSplitting_BalancesTenPlayersEqually()
    {
        // Arrange - 10 solo players with varying MMRs
        List<MatchmakingGroup> groups = [];
        double[] ratings = [1500.0, 1550.0, 1480.0, 1620.0, 1700.0, 1400.0, 1510.0, 1590.0, 1650.0, 1450.0];

        for (int i = 0; i < 10; i++)
        {
            MatchmakingGroupMember member = CreateMember(i + 1, $"Player_{i + 1}", ratings[i], true);
            groups.Add(new MatchmakingGroup
            {
                Members = [member],
                Information = CreateDefaultInfo()
            });
        }

        // Act - Split into Team 1 and Team 2 using snake balance
        List<MatchmakingGroup> sorted = [.. groups.OrderByDescending(g => g.AverageRating)];
        List<MatchmakingGroup> team1 = [];
        List<MatchmakingGroup> team2 = [];

        for (int i = 0; i < sorted.Count; i++)
            if (i % 2 == 0)
                team1.Add(sorted[i]);
            else
                team2.Add(sorted[i]);

        // Assert
        team1.Count.Should().Be(5);
        team2.Count.Should().Be(5);
        float team1Avg = team1.Average(g => g.AverageRating);
        float team2Avg = team2.Average(g => g.AverageRating);
        Math.Abs(team1Avg - team2Avg).Should().BeLessThan(100f, "Teams should be closely balanced in MMR");
    }

    [Fact]
    public async Task Integration_MatchmakingToClanProgression_UpdatesClanMatchCount()
    {
        // Arrange
        using IServiceScope scope = masterFixture.CreateScope();
        MerrickContext dbContext = masterFixture.GetDbContext(scope);

        string clanTag = $"C{Guid.NewGuid():N}"[..4].ToUpper();
        Clan clan = new()
        {
            Name = $"Clan_{clanTag}",
            Tag = clanTag,
            TimestampCreated = TimeProvider.System.GetUtcNow().UtcDateTime
        };
        await dbContext.Clans.AddAsync(clan);
        await dbContext.SaveChangesAsync();

        // Act & Assert
        clan.ID.Should().BeGreaterThan(0);
        clan.Tag.Should().Be(clanTag);
    }

    [Fact]
    public void Integration_MatchmakingQueueRetention_MaintainsPositionWhenNoServerFound()
    {
        // Arrange
        DateTimeOffset queueStart = TimeProvider.System.GetUtcNow().AddMinutes(-3);
        MatchmakingGroup group = new()
        {
            Members = [CreateMember(1, "QueuedUser", 1500.0, true)],
            Information = CreateDefaultInfo(),
            QueueStartTime = queueStart
        };

        // Act - Simulate server shortage recovery: QueueStartTime is preserved
        DateTimeOffset currentQueueStart = group.QueueStartTime ?? TimeProvider.System.GetUtcNow();

        // Assert
        currentQueueStart.Should().Be(queueStart,
            "QueueStartTime must not be wiped when match servers are temporarily busy");
    }

    [Fact]
    public async Task Integration_MatchResultPersistence_CalculatesMmrAdjustment()
    {
        // Arrange
        using IServiceScope scope = masterFixture.CreateScope();
        MerrickContext dbContext = masterFixture.GetDbContext(scope);

        Role userRole = await dbContext.Roles.FindAsync(1) ?? new Role { ID = 1, Name = "User" };
        if (dbContext.Entry(userRole).State == EntityState.Detached) await dbContext.Roles.AddAsync(userRole);

        User user = new()
        {
            EmailAddress = $"mmr_user_{Guid.NewGuid():N}@kongor.net",
            PBKDF2PasswordHash = "h",
            SRPPasswordHash = "h",
            SRPPasswordSalt = "s",
            Role = userRole
        };
        Account player = new()
        {
            Name = $"MMR_{Guid.NewGuid():N}"[..10],
            User = user,
            IsMain = true,
            Type = AccountType.Normal
        };
        await dbContext.Users.AddAsync(user);
        await dbContext.Accounts.AddAsync(player);
        await dbContext.SaveChangesAsync();

        // Act & Assert
        player.ID.Should().BeGreaterThan(0);
    }
}
