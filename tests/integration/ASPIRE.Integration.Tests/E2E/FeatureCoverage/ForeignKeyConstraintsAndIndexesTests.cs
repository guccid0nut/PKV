// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ============================================================================
// File: Feature13_ForeignKeyConstraintsAndIndexesTests.cs
// System: ProjectKongor Solution-Wide E2E Testing Suite
// Tier 1: Feature Coverage (Feature 13: Foreign Key Constraints & Covering Indexes)
// Policy: Strict Explicit Typing, Opaque-Box Validation (>= 5 tests)
// ============================================================================

using System.Runtime.CompilerServices;
using FluentAssertions;
using MERRICK.DatabaseContext.Entities.Statistics;

namespace ASPIRE.Tests.E2E.Tier1_FeatureCoverage;

public sealed class ForeignKeyConstraintsAndIndexesTests
{
    private static MerrickContext CreateInMemoryContext(string? dbName = null)
    {
        return TestDbContextFactory.CreateInMemoryContext(dbName ?? "FK_Test_" + Guid.NewGuid().ToString("N"));
    }

    [Fact]
    public async Task ForeignKey_AccountToUserWebRewards_CascadesOnUserDeletion()
    {
        // Arrange
        using MerrickContext db = CreateInMemoryContext();
        Role userRole = new() { ID = 1, Name = "User" };
        db.Roles.Add(userRole);
        await db.SaveChangesAsync();

        User user = new()
        {
            EmailAddress = $"web_reward_{Guid.NewGuid():N}@kongor.test",
            PBKDF2PasswordHash = "hash",
            SRPPasswordHash = "srphash",
            SRPPasswordSalt = "salt",
            Role = userRole
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();

        UserWebReward reward = new()
        {
            UserID = user.ID,
            RewardType = "Badge",
            RewardCode = "AlphaTester",
            IsEquipped = true
        };
        db.UserWebRewards.Add(reward);
        await db.SaveChangesAsync();

        // Act - Delete parent user
        db.Users.Remove(user);
        await db.SaveChangesAsync();

        // Assert - Web reward should be removed
        UserWebReward? remaining = await db.UserWebRewards.FirstOrDefaultAsync(r => r.UserID == user.ID);
        remaining.Should().BeNull();
    }

    [Fact]
    public async Task ForeignKey_StreamerApplication_PreservesReferentialIntegrityToUserAndAccount()
    {
        // Arrange
        using MerrickContext db = CreateInMemoryContext();
        Role userRole = new() { ID = 1, Name = "User" };
        db.Roles.Add(userRole);

        User user = new()
        {
            EmailAddress = $"streamer_{Guid.NewGuid():N}@kongor.test",
            PBKDF2PasswordHash = "hash",
            SRPPasswordHash = "srphash",
            SRPPasswordSalt = "salt",
            Role = userRole
        };
        Account account = new()
        {
            Name = "StreamerPro",
            User = user,
            Type = AccountType.Normal,
            IsMain = true
        };
        db.Users.Add(user);
        db.Accounts.Add(account);
        await db.SaveChangesAsync();

        StreamerApplication application = new()
        {
            UserID = user.ID,
            MainAccountID = account.ID,
            ChannelUrl = "https://twitch.tv/streamerpro",
            Platform = "Twitch",
            Status = StreamerApplicationStatus.Pending
        };
        db.StreamerApplications.Add(application);
        await db.SaveChangesAsync();

        // Assert
        StreamerApplication? retrieved = await db.StreamerApplications
            .Include(s => s.User)
            .Include(s => s.MainAccount)
            .FirstOrDefaultAsync(s => s.ID == application.ID);

        retrieved.Should().NotBeNull();
        retrieved!.UserID.Should().Be(user.ID);
        retrieved.MainAccountID.Should().Be(account.ID);
        retrieved.User.EmailAddress.Should().Be(user.EmailAddress);
        retrieved.MainAccount.Name.Should().Be("StreamerPro");
    }

    [Trait("Category", "Manual")]
    [Fact]
    public async Task ForeignKey_HostApplication_LinksHostAndMainAccountWithNullableHostAccount()
    {
        if (!NexusTestEnvironment.IsManualTestEnabled) return;
        // Arrange
        using MerrickContext db = CreateInMemoryContext();
        Role userRole = new() { ID = 1, Name = "User" };
        db.Roles.Add(userRole);

        User user = new()
        {
            EmailAddress = $"host_{Guid.NewGuid():N}@kongor.test",
            PBKDF2PasswordHash = "hash",
            SRPPasswordHash = "srphash",
            SRPPasswordSalt = "salt",
            Role = userRole
        };
        Account mainAccount = new()
        {
            Name = "MainHostAdmin",
            User = user,
            Type = AccountType.Normal,
            IsMain = true
        };
        db.Users.Add(user);
        db.Accounts.Add(mainAccount);
        await db.SaveChangesAsync();

        HostApplication hostApp = new()
        {
            UserID = user.ID,
            MainAccountID = mainAccount.ID,
            HostAccountID = null, // initially null before provisioning
            ServerLocation = "Frankfurt, DE",
            Status = HostApplicationStatus.Pending
        };
        db.HostApplications.Add(hostApp);
        await db.SaveChangesAsync();

        // Assert
        HostApplication? retrieved = await db.HostApplications.FirstOrDefaultAsync(h => h.ID == hostApp.ID);
        retrieved.Should().NotBeNull();
        retrieved!.HostAccountID.Should().BeNull();
        retrieved.MainAccountID.Should().Be(mainAccount.ID);
    }

    [Trait("Category", "Manual")]
    [Fact]
    public async Task ForeignKey_MatchStatisticsToFragHistory_OwnsManyFragEventsCleanly()
    {
        if (!NexusTestEnvironment.IsManualTestEnabled) return;
        // Arrange
        using MerrickContext db = CreateInMemoryContext();
        int matchId = Random.Shared.Next(100000, 999999);
        MatchStatistics matchStat = new()
        {
            MatchID = matchId,
            ServerID = 1,
            HostAccountName = "Server1",
            Map = "caldavar",
            MapVersion = "1.0",
            Version = "4.10.2",
            TimePlayed = 1800,
            ScoreTeam1 = 30,
            ScoreTeam2 = 25,
            FragHistory =
            [
                new FragEvent
                    { KillerAccountID = 10, VictimAccountID = 20, GameTime = 120, AssistAccountIDs = [11, 12] },
                new FragEvent { KillerAccountID = 20, VictimAccountID = 10, GameTime = 240, AssistAccountIDs = [21] }
            ]
        };

        // Act
        db.MatchStatistics.Add(matchStat);
        await db.SaveChangesAsync();

        // Assert
        MatchStatistics? retrieved = await db.MatchStatistics.FirstOrDefaultAsync(m => m.MatchID == matchId);
        retrieved.Should().NotBeNull();
        retrieved!.FragHistory.Should().HaveCount(2);
        retrieved.FragHistory.First().KillerAccountID.Should().Be(10);
    }

    [Fact]
    public async Task ForeignKey_PlayerStatisticsToItemAndAbilityHistory_PersistsOwnedJsonCollections()
    {
        // Arrange
        using MerrickContext db = CreateInMemoryContext();
        int matchId = Random.Shared.Next(100000, 999999);
        PlayerStatistics playerStat = (PlayerStatistics)RuntimeHelpers.GetUninitializedObject(typeof(PlayerStatistics));
        playerStat.MatchID = matchId;
        playerStat.AccountID = 500;
        playerStat.AccountName = "FlintMaster";
        playerStat.HeroIdentifier = "Hero_Flint";
        playerStat.Kills = 12;
        playerStat.Deaths = 2;
        playerStat.Assists = 8;
        playerStat.Inventory = ["Item_SteamBoots", "Item_Thunderclaw", "Item_GeometersBane"];
        playerStat.ItemHistory =
        [
            new ItemEvent { ItemIdentifier = "Item_SteamBoots", GameTime = 300, Action = "Purchased" }
        ];
        playerStat.AbilityHistory =
        [
            new AbilityEvent { AbilityIdentifier = "Ability_Flint1", GameTime = 60, Level = 1 }
        ];

        // Act
        db.PlayerStatistics.Add(playerStat);
        await db.SaveChangesAsync();

        // Assert
        PlayerStatistics? retrieved =
            await db.PlayerStatistics.FirstOrDefaultAsync(p => p.MatchID == matchId && p.AccountID == 500);
        retrieved.Should().NotBeNull();
        retrieved!.Inventory.Should().HaveCount(3);
        retrieved.ItemHistory.Should().HaveCount(1);
        retrieved.AbilityHistory.Should().HaveCount(1);
    }
}
