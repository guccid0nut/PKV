// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using MERRICK.DatabaseContext.Entities.Core;
using MERRICK.DatabaseContext.Entities.Statistics;
using MERRICK.DatabaseContext.Enumerations;
using MERRICK.DatabaseContext.Persistence;
using MERRICK.DatabaseContext.Repositories;
using MERRICK.DatabaseContext.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using NEXUS.Contracts.Data;
using Xunit;

namespace MERRICK.DatabaseContext.Tests;

public class MatchHistoryAndCascadeTests
{
    [Fact]
    public async Task GetMatchByIdAsync_WithBansAndFragsAndAssists_ExecutesSplitQueryAndLoadsChildren()
    {
        // Arrange
        string dbName = Guid.NewGuid().ToString("N");
        int targetMatchId = 778899;

        using (MerrickContext db = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            MatchStatistics match = new()
            {
                MatchID = targetMatchId,
                Map = "caldavar",
                GameMode = "caldavar",
                Bans =
                [
                    new MatchBan { MatchID = targetMatchId, HeroIdentifier = "Hero_Devourer", BanOrder = 1 },
                    new MatchBan { MatchID = targetMatchId, HeroIdentifier = "Hero_Pebbles", BanOrder = 2 }
                ],
                Frags =
                [
                    new MatchFrag
                    {
                        MatchID = targetMatchId,
                        KillerAccountID = 101,
                        VictimAccountID = 202,
                        Assists =
                        [
                            new MatchFragAssist { AssistingAccountID = 303 },
                            new MatchFragAssist { AssistingAccountID = 404 }
                        ]
                    }
                ]
            };

            db.MatchStatistics.Add(match);
            await db.SaveChangesAsync();
        }

        // Act
        using (MerrickContext db = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            MatchHistoryRepository repo = new(db);
            MatchStatistics? result = await repo.GetMatchByIdAsync(targetMatchId);

            // Assert
            Assert.NotNull(result);
            Assert.Equal(targetMatchId, result.MatchID);
            Assert.Equal(2, result.Bans.Count);
            Assert.Single(result.Frags);
            Assert.Equal(2, result.Frags.First().Assists.Count);
        }
    }

    [Fact]
    public async Task GetPlayerMatchHistoryOverviewAsync_CaseInsensitiveFilterWithoutToLowerAllocations()
    {
        // Arrange
        string dbName = Guid.NewGuid().ToString("N");
        int targetAccountId = 555;
        string nickname = "LegionCommander";

        using (MerrickContext db = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            db.MatchStatistics.AddRange(
                new MatchStatistics { ID = 1, MatchID = 9001, Map = "Caldavar", GameMode = "Caldavar" },
                new MatchStatistics { ID = 2, MatchID = 9002, Map = "MidWars", GameMode = "MidWars" },
                new MatchStatistics { ID = 3, MatchID = 9003, Map = "Caldavar", GameMode = "Casual" }
            );

            db.PlayerStatistics.AddRange(
                new PlayerStatistics { ID = 1, MatchID = 9001, AccountID = 999999, AccountName = "legioncommander", RankedMatch = 1, PublicMatch = 0 },
                new PlayerStatistics { ID = 2, MatchID = 9002, AccountID = targetAccountId, AccountName = nickname, RankedMatch = 1, PublicMatch = 0 },
                new PlayerStatistics { ID = 3, MatchID = 9003, AccountID = targetAccountId, AccountName = nickname, RankedMatch = 0, PublicMatch = 1 }
            );

            await db.SaveChangesAsync();
        }

        // Act
        using (MerrickContext db = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            MatchHistoryRepository repo = new(db);

            // Query Ranked FoC
            IReadOnlyList<PlayerMatchRecordDTO> focMatches = await repo.GetPlayerMatchHistoryOverviewAsync(
                targetAccountId, nickname, table: "ranked", currentSeason: "1");

            // Query Ranked MidWars
            IReadOnlyList<PlayerMatchRecordDTO> midWarsMatches = await repo.GetPlayerMatchHistoryOverviewAsync(
                targetAccountId, nickname, table: "ranked_midwars", currentSeason: "1");

            // Assert
            Assert.Single(focMatches);
            Assert.Equal(9001, focMatches[0].MatchID);

            Assert.Single(midWarsMatches);
            Assert.Equal(9002, midWarsMatches[0].MatchID);
        }
    }

    [Fact]
    public async Task SaveChangesAsync_AtomicallyCascadeDeletesUntrackedReverseFriendshipRows()
    {
        // Arrange
        string dbName = Guid.NewGuid().ToString("N");
        int primaryAccountId = 1111;
        int friendAccountId = 2222;

        using (MerrickContext db = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            User user1 = new()
            {
                ID = primaryAccountId,
                EmailAddress = "p1@kongor.test",
                SRPPasswordSalt = "salt1",
                SRPPasswordHash = "hash1",
                PBKDF2PasswordHash = "pbkdf2_hash_1"
            };
            User user2 = new()
            {
                ID = friendAccountId,
                EmailAddress = "p2@kongor.test",
                SRPPasswordSalt = "salt2",
                SRPPasswordHash = "hash2",
                PBKDF2PasswordHash = "pbkdf2_hash_2"
            };
            db.Users.AddRange(user1, user2);

            Account acc1 = new() { ID = primaryAccountId, UserID = primaryAccountId, User = user1, Name = "PlayerOne" };
            Account acc2 = new() { ID = friendAccountId, UserID = friendAccountId, User = user2, Name = "PlayerTwo" };
            db.Accounts.AddRange(acc1, acc2);

            // Mutual friendships
            Friendship forward = new()
            {
                AccountID = primaryAccountId,
                FriendAccountID = friendAccountId,
                Status = FriendshipStatus.Accepted,
                FriendGroup = "General"
            };
            Friendship reverse = new()
            {
                AccountID = friendAccountId,
                FriendAccountID = primaryAccountId,
                Status = FriendshipStatus.Accepted,
                FriendGroup = "General"
            };
            db.Friendships.AddRange(forward, reverse);
            await db.SaveChangesAsync();
        }

        // Act: Open a new context, load ONLY Account1, delete it, and call SaveChangesAsync
        // Notice: reverse friendship (Account2 -> Account1) is completely untracked in this context!
        using (MerrickContext db = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            Account? acc1 = await db.Accounts.FindAsync(primaryAccountId);
            Assert.NotNull(acc1);

            db.Accounts.Remove(acc1);
            int rowsChanged = await db.SaveChangesAsync();

            Assert.True(rowsChanged > 0);
        }

        // Assert: Verify both forward and reverse friendship rows were deleted
        using (MerrickContext db = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            List<Friendship> remainingFriendships = await db.Friendships.ToListAsync();
            Assert.Empty(remainingFriendships);

            Account? remainingAcc2 = await db.Accounts.FindAsync(friendAccountId);
            Assert.NotNull(remainingAcc2); // Friend account still exists, only the friendship link is severed
        }
    }
}
