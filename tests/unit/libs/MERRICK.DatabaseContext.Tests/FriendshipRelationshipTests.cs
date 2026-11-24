// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using FluentAssertions;
using MERRICK.DatabaseContext.Entities.Core;
using MERRICK.DatabaseContext.Entities.Relational;
using MERRICK.DatabaseContext.Entities.Utility;
using MERRICK.DatabaseContext.Enumerations;
using MERRICK.DatabaseContext.Persistence;
using MERRICK.DatabaseContext.Repositories;
using MERRICK.DatabaseContext.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using NEXUS.Contracts.Data;
using Xunit;

namespace MERRICK.DatabaseContext.Tests;

public class FriendshipRelationshipTests
{
    private static User CreateTestUser(int id = 1, string email = "user@kongor.test") => new()
    {
        ID = id,
        EmailAddress = email,
        SRPPasswordSalt = "salt",
        SRPPasswordHash = "hash",
        PBKDF2PasswordHash = "pbkdf2",
        Role = new Role { ID = 1, Name = "User" }
    };

    private static async Task<(Account accA, Account accB, Account accC)> SeedThreeAccountsAsync(MerrickContext db)
    {
        User user = CreateTestUser(1);
        Account accA = new() { ID = 101, Name = "PlayerA", User = user };
        Account accB = new() { ID = 102, Name = "PlayerB", User = user };
        Account accC = new() { ID = 103, Name = "PlayerC", User = user };

        db.Users.Add(user);
        db.Accounts.AddRange(accA, accB, accC);
        await db.SaveChangesAsync();

        return (accA, accB, accC);
    }

    #region 1. Self-Friendship Rejection Challenge

    [Fact]
    public async Task AddFriendshipAsync_SelfFriendship_RejectedCleanly()
    {
        string dbName = Guid.NewGuid().ToString("N");
        using (MerrickContext db = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            await SeedThreeAccountsAsync(db);
        }

        using (MerrickContext db = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            FriendshipRepository repo = new(db);

            bool result = await repo.AddFriendshipAsync(101, 101);
            result.Should().BeFalse();

            List<Friendship> friendships = await db.Friendships.ToListAsync();
            friendships.Should().BeEmpty();
        }
    }

    [Fact]
    public async Task AreFriendsAsync_SelfFriendship_ReturnsFalse()
    {
        string dbName = Guid.NewGuid().ToString("N");
        using (MerrickContext db = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            await SeedThreeAccountsAsync(db);
        }

        using (MerrickContext db = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            FriendshipRepository repo = new(db);

            bool areFriends = await repo.AreFriendsAsync(101, 101);
            areFriends.Should().BeFalse();
        }
    }

    [Fact]
    public async Task AcceptFriendshipAsync_SelfFriendship_RejectedCleanly()
    {
        string dbName = Guid.NewGuid().ToString("N");
        using (MerrickContext db = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            await SeedThreeAccountsAsync(db);
        }

        using (MerrickContext db = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            FriendshipRepository repo = new(db);

            bool result = await repo.AcceptFriendshipAsync(101, 101);
            result.Should().BeFalse();

            List<Friendship> friendships = await db.Friendships.ToListAsync();
            friendships.Should().BeEmpty();
        }
    }

    [Theory]
    [InlineData(0, 101)]
    [InlineData(101, 0)]
    [InlineData(-1, 101)]
    [InlineData(101, -1)]
    [InlineData(-10, -10)]
    public async Task NonPositive_AccountIDs_AreRejectedAcrossAllMethods(int id1, int id2)
    {
        string dbName = Guid.NewGuid().ToString("N");
        using (MerrickContext db = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            await SeedThreeAccountsAsync(db);
        }

        using (MerrickContext db = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            FriendshipRepository repo = new(db);

            (await repo.AddFriendshipAsync(id1, id2)).Should().BeFalse();
            (await repo.AreFriendsAsync(id1, id2)).Should().BeFalse();
            (await repo.AcceptFriendshipAsync(id1, id2)).Should().BeFalse();
        }
    }

    [Fact]
    public async Task RemoveFriendshipBidirectionalAsync_SelfAccount_ReturnsUnchangedOrRemovesGracefully()
    {
        string dbName = Guid.NewGuid().ToString("N");
        using (MerrickContext db = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            await SeedThreeAccountsAsync(db);
        }

        using (MerrickContext db = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            FriendshipRepository repo = new(db);

            FriendshipRemovalResult result = await repo.RemoveFriendshipBidirectionalAsync(101, 101);
            result.Success.Should().BeTrue();
            result.Removed.Should().BeFalse();
            result.AccountId.Should().Be(101);
            result.BuddyId.Should().Be(101);
        }
    }

    #endregion

    #region 2. Bidirectional Symmetry Challenge

    [Fact]
    public async Task BidirectionalSymmetry_AddFriendship_BothDirectionsConsistentAcrossAllQueries()
    {
        string dbName = Guid.NewGuid().ToString("N");
        using (MerrickContext db = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            User user = CreateTestUser(1);
            Clan clanA = new() { ID = 1, Name = "AlphaClan", Tag = "ALP" };
            Clan clanB = new() { ID = 2, Name = "BetaClan", Tag = "BET" };
            Account accA = new() { ID = 201, Name = "HeroA", User = user, Clan = clanA };
            Account accB = new() { ID = 202, Name = "HeroB", User = user, Clan = clanB };

            db.Users.Add(user);
            db.Clans.AddRange(clanA, clanB);
            db.Accounts.AddRange(accA, accB);
            await db.SaveChangesAsync();
        }

        using (MerrickContext db = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            FriendshipRepository repo = new(db);
            bool added = await repo.AddFriendshipAsync(201, 202, "Squad");
            added.Should().BeTrue();

            // 1. AreFriendsAsync symmetry
            bool abFriends = await repo.AreFriendsAsync(201, 202);
            bool baFriends = await repo.AreFriendsAsync(202, 201);
            abFriends.Should().BeTrue();
            baFriends.Should().BeTrue();

            // 2. GetFriendedPeersAsync symmetry
            IReadOnlyList<FriendedPeer> peersOfA = await repo.GetFriendedPeersAsync(201);
            IReadOnlyList<FriendedPeer> peersOfB = await repo.GetFriendedPeersAsync(202);

            peersOfA.Should().ContainSingle(p => p.ID == 202 && p.Name == "HeroB" && p.ClanTag == "BET" && p.FriendGroup == "Squad");
            peersOfB.Should().ContainSingle(p => p.ID == 201 && p.Name == "HeroA" && p.ClanTag == "ALP" && p.FriendGroup == "Squad");

            // 3. GetFriendshipsAsync symmetry
            IReadOnlyList<Friendship> friendshipsOfA = await repo.GetFriendshipsAsync(201);
            IReadOnlyList<Friendship> friendshipsOfB = await repo.GetFriendshipsAsync(202);

            friendshipsOfA.Should().ContainSingle(f => f.FriendAccountID == 202 && f.Status == FriendshipStatus.Accepted);
            friendshipsOfB.Should().ContainSingle(f => f.FriendAccountID == 201 && f.Status == FriendshipStatus.Accepted);
        }
    }

    [Fact]
    public async Task BidirectionalRemoval_RemovesBothDirections_RegardlessOfCallerOrder()
    {
        string dbName = Guid.NewGuid().ToString("N");
        using (MerrickContext db = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            await SeedThreeAccountsAsync(db);
            FriendshipRepository repo = new(db);
            await repo.AddFriendshipAsync(101, 102, "General");
        }

        // Remove using (102, 101) instead of (101, 102)
        using (MerrickContext db = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            FriendshipRepository repo = new(db);
            FriendshipRemovalResult result = await repo.RemoveFriendshipBidirectionalAsync(102, 101);

            result.Success.Should().BeTrue();
            result.Removed.Should().BeTrue();

            bool ab = await repo.AreFriendsAsync(101, 102);
            bool ba = await repo.AreFriendsAsync(102, 101);
            ab.Should().BeFalse();
            ba.Should().BeFalse();

            (await repo.GetFriendedPeersAsync(101)).Should().BeEmpty();
            (await repo.GetFriendedPeersAsync(102)).Should().BeEmpty();

            List<Friendship> remaining = await db.Friendships.ToListAsync();
            remaining.Should().BeEmpty();
        }
    }

    [Fact]
    public async Task FriendshipRelationship_PendingStatus_IsNotTreatedAsAcceptedFriendship()
    {
        string dbName = Guid.NewGuid().ToString("N");
        using (MerrickContext db = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            await SeedThreeAccountsAsync(db);

            // Add pending one-way request directly into relational table
            db.Friendships.Add(new Friendship
            {
                AccountID = 101,
                FriendAccountID = 102,
                Status = FriendshipStatus.Pending,
                FriendGroup = "General"
            });
            await db.SaveChangesAsync();
        }

        using (MerrickContext db = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            FriendshipRepository repo = new(db);

            // Pending request should NOT show as friends
            (await repo.AreFriendsAsync(101, 102)).Should().BeFalse();
            (await repo.AreFriendsAsync(102, 101)).Should().BeFalse();

            // Peers projection filters by Accepted only
            (await repo.GetFriendedPeersAsync(101)).Should().BeEmpty();
            (await repo.GetFriendedPeersAsync(102)).Should().BeEmpty();

            // Explicit status filter works
            IReadOnlyList<Friendship> pendingOfA = await repo.GetFriendshipsAsync(101, FriendshipStatus.Pending);
            pendingOfA.Should().ContainSingle(f => f.FriendAccountID == 102);

            IReadOnlyList<Friendship> acceptedOfA = await repo.GetFriendshipsAsync(101, FriendshipStatus.Accepted);
            acceptedOfA.Should().BeEmpty();
        }
    }

    #endregion

    #region 3. Rapid Addition, Removal, and Re-Addition Churn Challenge

    [Fact]
    public async Task RapidChurn_SingleContext_AddRemoveReadd20Cycles_MaintainsIntegrity()
    {
        string dbName = Guid.NewGuid().ToString("N");
        using MerrickContext db = TestDbContextFactory.CreateInMemoryContext(dbName);
        await SeedThreeAccountsAsync(db);
        FriendshipRepository repo = new(db);

        for (int cycle = 1; cycle <= 20; cycle++)
        {
            string groupName = $"Group_Cycle_{cycle}";

            // Add
            bool added = await repo.AddFriendshipAsync(101, 102, groupName);
            added.Should().BeTrue();

            (await repo.AreFriendsAsync(101, 102)).Should().BeTrue();
            (await repo.AreFriendsAsync(102, 101)).Should().BeTrue();

            List<Friendship> friendshipsAfterAdd = await db.Friendships.ToListAsync();
            friendshipsAfterAdd.Should().HaveCount(2, $"Cycle {cycle} add should maintain exactly 2 rows");

            // Remove (alternating caller order)
            int remover = (cycle % 2 == 1) ? 101 : 102;
            int target = (cycle % 2 == 1) ? 102 : 101;

            FriendshipRemovalResult removal = await repo.RemoveFriendshipBidirectionalAsync(remover, target);
            removal.Success.Should().BeTrue();
            removal.Removed.Should().BeTrue();

            (await repo.AreFriendsAsync(101, 102)).Should().BeFalse();
            (await repo.AreFriendsAsync(102, 101)).Should().BeFalse();

            List<Friendship> friendshipsAfterRemove = await db.Friendships.ToListAsync();
            friendshipsAfterRemove.Should().BeEmpty($"Cycle {cycle} remove should leave 0 rows");
        }
    }

    [Fact]
    public async Task RapidChurn_MultiContext_AddRemoveReadd10Cycles_PersistsAcrossContexts()
    {
        string dbName = Guid.NewGuid().ToString("N");
        using (MerrickContext initDb = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            await SeedThreeAccountsAsync(initDb);
        }

        for (int cycle = 1; cycle <= 10; cycle++)
        {
            string group = $"MultiCtx_{cycle}";

            // 1. Add in context A
            using (MerrickContext addCtx = TestDbContextFactory.CreateInMemoryContext(dbName))
            {
                FriendshipRepository repo = new(addCtx);
                bool added = await repo.AddFriendshipAsync(101, 102, group);
                added.Should().BeTrue();
            }

            // 2. Verify in fresh context B
            using (MerrickContext verifyCtx = TestDbContextFactory.CreateInMemoryContext(dbName))
            {
                FriendshipRepository repo = new(verifyCtx);
                (await repo.AreFriendsAsync(101, 102)).Should().BeTrue();
                (await repo.AreFriendsAsync(102, 101)).Should().BeTrue();

                IReadOnlyList<FriendedPeer> peers = await repo.GetFriendedPeersAsync(101);
                peers.Should().ContainSingle(p => p.ID == 102 && p.FriendGroup == group);
            }

            // 3. Remove in fresh context C
            using (MerrickContext removeCtx = TestDbContextFactory.CreateInMemoryContext(dbName))
            {
                FriendshipRepository repo = new(removeCtx);
                FriendshipRemovalResult res = await repo.RemoveFriendshipBidirectionalAsync(101, 102);
                res.Success.Should().BeTrue();
                res.Removed.Should().BeTrue();
            }

            // 4. Verify removal in fresh context D
            using (MerrickContext verifyCleanCtx = TestDbContextFactory.CreateInMemoryContext(dbName))
            {
                FriendshipRepository repo = new(verifyCleanCtx);
                (await repo.AreFriendsAsync(101, 102)).Should().BeFalse();
                (await repo.AreFriendsAsync(102, 101)).Should().BeFalse();
                (await verifyCleanCtx.Friendships.ToListAsync()).Should().BeEmpty();
            }
        }
    }

    #endregion

    #region 4. Multi-Peer Relational Graph & Selective Removal Challenge

    [Fact]
    public async Task MultiPeerNetwork_TriangleFriendships_SelectiveRemovalDoesNotCorruptOthers()
    {
        string dbName = Guid.NewGuid().ToString("N");
        using (MerrickContext db = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            await SeedThreeAccountsAsync(db);
            FriendshipRepository repo = new(db);

            // Establish triangle: A<->B, B<->C, A<->C
            (await repo.AddFriendshipAsync(101, 102, "AB")).Should().BeTrue();
            (await repo.AddFriendshipAsync(102, 103, "BC")).Should().BeTrue();
            (await repo.AddFriendshipAsync(101, 103, "AC")).Should().BeTrue();
        }

        using (MerrickContext db = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            FriendshipRepository repo = new(db);

            // Total rows: 3 pairs * 2 directions = 6 rows
            (await db.Friendships.CountAsync()).Should().Be(6);

            // Remove only A <-> B
            FriendshipRemovalResult result = await repo.RemoveFriendshipBidirectionalAsync(101, 102);
            result.Success.Should().BeTrue();
            result.Removed.Should().BeTrue();

            // A and B are no longer friends
            (await repo.AreFriendsAsync(101, 102)).Should().BeFalse();
            (await repo.AreFriendsAsync(102, 101)).Should().BeFalse();

            // But B <-> C and A <-> C must remain fully intact
            (await repo.AreFriendsAsync(102, 103)).Should().BeTrue();
            (await repo.AreFriendsAsync(103, 102)).Should().BeTrue();
            (await repo.AreFriendsAsync(101, 103)).Should().BeTrue();
            (await repo.AreFriendsAsync(103, 101)).Should().BeTrue();

            // Verify remaining rows count is 4
            (await db.Friendships.CountAsync()).Should().Be(4);

            // Verify peer projections
            IReadOnlyList<FriendedPeer> peersA = await repo.GetFriendedPeersAsync(101);
            peersA.Should().ContainSingle(p => p.ID == 103);

            IReadOnlyList<FriendedPeer> peersB = await repo.GetFriendedPeersAsync(102);
            peersB.Should().ContainSingle(p => p.ID == 103);

            IReadOnlyList<FriendedPeer> peersC = await repo.GetFriendedPeersAsync(103);
            peersC.Should().HaveCount(2);
            peersC.Select(p => p.ID).Should().BeEquivalentTo([101, 102]);
        }
    }

    #endregion

    #region 5. Idempotence & Boundary Conditions Challenge

    [Fact]
    public async Task AddFriendshipAsync_ConsecutiveDuplicateAdds_DoNotDuplicateRows()
    {
        string dbName = Guid.NewGuid().ToString("N");
        using (MerrickContext db = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            await SeedThreeAccountsAsync(db);
        }

        using (MerrickContext db = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            FriendshipRepository repo = new(db);

            for (int i = 0; i < 5; i++)
            {
                bool added = await repo.AddFriendshipAsync(101, 102, $"Attempt_{i}");
                added.Should().BeTrue();
            }

            List<Friendship> rows = await db.Friendships.ToListAsync();
            rows.Should().HaveCount(2);
            rows.All(f => f.FriendGroup == "Attempt_4").Should().BeTrue();
        }
    }

    [Fact]
    public async Task RemoveFriendshipBidirectionalAsync_ConsecutiveRemovals_FirstSucceedsRemainingUnchanged()
    {
        string dbName = Guid.NewGuid().ToString("N");
        using (MerrickContext db = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            await SeedThreeAccountsAsync(db);
            FriendshipRepository repo = new(db);
            await repo.AddFriendshipAsync(101, 102);
        }

        using (MerrickContext db = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            FriendshipRepository repo = new(db);

            // 1st removal: succeeds and removes rows
            FriendshipRemovalResult first = await repo.RemoveFriendshipBidirectionalAsync(101, 102);
            first.Success.Should().BeTrue();
            first.Removed.Should().BeTrue();

            // 2nd removal: unchanged
            FriendshipRemovalResult second = await repo.RemoveFriendshipBidirectionalAsync(101, 102);
            second.Success.Should().BeTrue();
            second.Removed.Should().BeFalse();
            second.StatusCode.Should().Be(200);

            // 3rd removal from opposite perspective: unchanged
            FriendshipRemovalResult third = await repo.RemoveFriendshipBidirectionalAsync(102, 101);
            third.Success.Should().BeTrue();
            third.Removed.Should().BeFalse();
            third.StatusCode.Should().Be(200);
        }
    }

    [Fact]
    public async Task FriendshipRelationship_NonExistentAccounts_HandledGracefully()
    {
        string dbName = Guid.NewGuid().ToString("N");
        using (MerrickContext db = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            await SeedThreeAccountsAsync(db);
        }

        using (MerrickContext db = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            FriendshipRepository repo = new(db);

            // Add with nonexistent account
            (await repo.AddFriendshipAsync(101, 9999)).Should().BeFalse();
            (await repo.AddFriendshipAsync(9999, 101)).Should().BeFalse();
            (await repo.AddFriendshipAsync(9998, 9999)).Should().BeFalse();

            // Accept with nonexistent account
            (await repo.AcceptFriendshipAsync(101, 9999)).Should().BeFalse();
            (await repo.AcceptFriendshipAsync(9999, 101)).Should().BeFalse();

            // Remove with nonexistent accounts
            FriendshipRemovalResult resMissingBuddy = await repo.RemoveFriendshipBidirectionalAsync(101, 9999);
            resMissingBuddy.Success.Should().BeFalse();
            resMissingBuddy.StatusCode.Should().Be(404);
            resMissingBuddy.ErrorMessage.Should().Be("Buddy Not Found");

            FriendshipRemovalResult resMissingAccount = await repo.RemoveFriendshipBidirectionalAsync(9999, 101);
            resMissingAccount.Success.Should().BeFalse();
            resMissingAccount.StatusCode.Should().Be(404);
            resMissingAccount.ErrorMessage.Should().Be("Account Not Found");
        }
    }

    [Fact]
    public async Task FriendshipRelationship_AccountDeletion_CascadeDeletesFriendshipsInBothDirections()
    {
        string dbName = Guid.NewGuid().ToString("N");
        using (MerrickContext db = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            await SeedThreeAccountsAsync(db);
            FriendshipRepository repo = new(db);
            await repo.AddFriendshipAsync(101, 102);
            await repo.AddFriendshipAsync(101, 103);
        }

        using (MerrickContext db = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            // Verify initial count: 4 rows (101<->102, 101<->103)
            (await db.Friendships.CountAsync()).Should().Be(4);

            // Delete Account 101 with loaded navigations for in-memory client cascade
            Account? acc101 = await db.Accounts
                .Include(a => a.Friendships)
                .Include(a => a.ReverseFriendships)
                .FirstOrDefaultAsync(a => a.ID == 101);
            acc101.Should().NotBeNull();
            db.Accounts.Remove(acc101!);
            await db.SaveChangesAsync();

            List<Friendship> remaining = await db.Friendships.ToListAsync();
            remaining.Should().BeEmpty();
        }
    }

    #endregion

    #region 6. Advanced Social Graph & Concurrency Challenges

    [Fact]
    public async Task FriendshipRelationship_DirectionalFriendGroupIndependence_UpdatingOneSideDoesNotMutateTheOther()
    {
        string dbName = Guid.NewGuid().ToString("N");
        using (MerrickContext db = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            await SeedThreeAccountsAsync(db);
            FriendshipRepository repo = new(db);
            await repo.AddFriendshipAsync(101, 102, "General");
        }

        using (MerrickContext db = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            FriendshipRepository repo = new(db);

            // Account 101 updates their category of 102 to "VIP"
            bool updated = await repo.UpdateFriendGroupAsync(101, 102, "VIP");
            updated.Should().BeTrue();

            // Account 101's view of 102 is "VIP"
            IReadOnlyList<FriendedPeer> peersA = await repo.GetFriendedPeersAsync(101);
            peersA.Should().ContainSingle(p => p.ID == 102 && p.FriendGroup == "VIP");

            // Account 102's view of 101 must remain "General" (independent user grouping)
            IReadOnlyList<FriendedPeer> peersB = await repo.GetFriendedPeersAsync(102);
            peersB.Should().ContainSingle(p => p.ID == 101 && p.FriendGroup == "General");
        }
    }

    [Fact]
    public async Task LiveRelationalProjection_AccountNameAndClanTagUpdates_ReflectImmediatelyWithoutStaleState()
    {
        // This directly stress-tests the elimination of the legacy Account.FriendedPeers JSON blob
        // which suffered from state drift when buddy nicknames or clan tags were altered.
        string dbName = Guid.NewGuid().ToString("N");
        using (MerrickContext db = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            User user = CreateTestUser(1);
            Clan clan = new() { ID = 50, Name = "OriginalClan", Tag = "ORIG" };
            Account accA = new() { ID = 101, Name = "PlayerA", User = user };
            Account accB = new() { ID = 102, Name = "OldBuddyName", User = user, Clan = clan };

            db.Users.Add(user);
            db.Clans.Add(clan);
            db.Accounts.AddRange(accA, accB);
            await db.SaveChangesAsync();

            FriendshipRepository repo = new(db);
            await repo.AddFriendshipAsync(101, 102, "General");
        }

        // Verify initial projection
        using (MerrickContext db = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            FriendshipRepository repo = new(db);
            IReadOnlyList<FriendedPeer> initialPeers = await repo.GetFriendedPeersAsync(101);
            initialPeers.Should().ContainSingle(p => p.ID == 102 && p.Name == "OldBuddyName" && p.ClanTag == "ORIG");
        }

        // Mutate Account B's name and Clan Tag in the database
        using (MerrickContext db = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            Account? accB = await db.Accounts.Include(a => a.Clan).FirstOrDefaultAsync(a => a.ID == 102);
            accB.Should().NotBeNull();
            accB!.Name = "RenamedProPlayer";
            accB.Clan!.Tag = "NEWTAG";
            await db.SaveChangesAsync();
        }

        // Verify that Player A's query immediately projects the fresh relational data (0% state drift)
        using (MerrickContext db = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            FriendshipRepository repo = new(db);
            IReadOnlyList<FriendedPeer> freshPeers = await repo.GetFriendedPeersAsync(101);
            freshPeers.Should().ContainSingle(p => p.ID == 102 && p.Name == "RenamedProPlayer" && p.ClanTag == "NEWTAG");
        }
    }

    [Fact]
    public async Task FriendshipRelationship_AcceptFriendshipAsync_TransitionsPendingRequestToMutualAccepted()
    {
        string dbName = Guid.NewGuid().ToString("N");
        using (MerrickContext db = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            await SeedThreeAccountsAsync(db);

            // Requester 101 created pending request to Acceptor 102
            db.Friendships.Add(new Friendship
            {
                AccountID = 101,
                FriendAccountID = 102,
                Status = FriendshipStatus.Pending,
                FriendGroup = "General"
            });
            await db.SaveChangesAsync();
        }

        using (MerrickContext db = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            FriendshipRepository repo = new(db);

            // Acceptor 102 accepts friendship from 101
            bool accepted = await repo.AcceptFriendshipAsync(102, 101, "CloseFriends");
            accepted.Should().BeTrue();

            // Symmetrical mutual friends
            (await repo.AreFriendsAsync(101, 102)).Should().BeTrue();
            (await repo.AreFriendsAsync(102, 101)).Should().BeTrue();

            // Check database rows
            List<Friendship> rows = await db.Friendships.ToListAsync();
            rows.Should().HaveCount(2);

            Friendship? forward = rows.FirstOrDefault(r => r.AccountID == 101 && r.FriendAccountID == 102);
            Friendship? reciprocal = rows.FirstOrDefault(r => r.AccountID == 102 && r.FriendAccountID == 101);

            forward.Should().NotBeNull();
            forward!.Status.Should().Be(FriendshipStatus.Accepted);

            reciprocal.Should().NotBeNull();
            reciprocal!.Status.Should().Be(FriendshipStatus.Accepted);
            reciprocal.FriendGroup.Should().Be("CloseFriends");
        }
    }

    [Theory]
    [InlineData(null, "General")]
    [InlineData("", "General")]
    [InlineData("   ", "General")]
    [InlineData("  CustomGroup  ", "CustomGroup")]
    public async Task FriendshipRelationship_FriendGroupSanitization_NormalizesWhitespaceAndDefaults(string? inputGroup, string expectedGroup)
    {
        string dbName = Guid.NewGuid().ToString("N");
        using (MerrickContext db = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            await SeedThreeAccountsAsync(db);
        }

        using (MerrickContext db = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            FriendshipRepository repo = new(db);
            bool added = await repo.AddFriendshipAsync(101, 102, inputGroup!);
            added.Should().BeTrue();

            IReadOnlyList<FriendedPeer> peers = await repo.GetFriendedPeersAsync(101);
            peers.Should().ContainSingle(p => p.ID == 102 && p.FriendGroup == expectedGroup);
        }
    }

    [Fact]
    public void FriendshipDdl_SqlServerAndNpgsql_EmitsRestrictReverseFkAndUniqueCompositeIndex()
    {
        // Protects: core.Friendships relational schema against SQL Server Error 1785 (multiple cascade paths) and duplicate pairs
        // Fails when: ConfigureFriendships alters DeleteBehavior.Restrict on FriendAccountID or drops UQ_Friendships_AccountID_FriendAccountID
        // Seam: Real EF Core SqlServer and Npgsql Database.GenerateCreateScript() + relational model metadata
        DbContextOptions<MerrickContext> sqlServerOpts = new DbContextOptionsBuilder<MerrickContext>()
            .UseSqlServer("Server=(localdb)\\MSSQLLocalDB;Database=MerrickDdlProbe;Trusted_Connection=True")
            .Options;
        using MerrickContext sqlCtx = new(sqlServerOpts);
        string sqlServerDdl = sqlCtx.Database.GenerateCreateScript();

        sqlServerDdl.Should().Contain("[UQ_Friendships_AccountID_FriendAccountID]");
        sqlServerDdl.Should().Contain("[IX_Friendships_AccountID]");
        sqlServerDdl.Should().Contain("[IX_Friendships_FriendAccountID]");
        sqlServerDdl.Should().Contain("[IX_Friendships_Status]");

        Microsoft.EntityFrameworkCore.Metadata.IEntityType? entityType = sqlCtx.Model.FindEntityType(typeof(Friendship));
        entityType.Should().NotBeNull();
        Microsoft.EntityFrameworkCore.Metadata.IForeignKey ownerFk = entityType!.GetForeignKeys()
            .Single(fk => fk.Properties.Single().Name == nameof(Friendship.AccountID));
        Microsoft.EntityFrameworkCore.Metadata.IForeignKey friendFk = entityType.GetForeignKeys()
            .Single(fk => fk.Properties.Single().Name == nameof(Friendship.FriendAccountID));
        ownerFk.DeleteBehavior.Should().Be(DeleteBehavior.Cascade);
        friendFk.DeleteBehavior.Should().Be(DeleteBehavior.Restrict);

        DbContextOptions<MerrickContext> npgsqlOpts = new DbContextOptionsBuilder<MerrickContext>()
            .UseNpgsql("Host=127.0.0.1;Database=merrick_ddl_probe;Username=postgres;Password=postgres")
            .Options;
        using MerrickContext pgCtx = new(npgsqlOpts);
        string npgsqlDdl = pgCtx.Database.GenerateCreateScript();
        npgsqlDdl.Should().Contain("\"UQ_Friendships_AccountID_FriendAccountID\"");
        npgsqlDdl.Should().Contain("\"IX_Friendships_FriendAccountID\"");
    }

    [Fact]
    public async Task AccountDeletion_WhenReverseFriendshipsAreUntracked_PurgesReverseRowsAndPreservesThirdPartyGraph()
    {
        // Protects: Account deletion when ReverseFriendships are NOT eagerly loaded in ChangeTracker
        // Fails when: CascadeDeleteReverseFriendships skips untracked reverse rows or deletes unrelated sibling friendships
        // Seam: Fresh MerrickContext instances exercising both SaveChangesAsync and synchronous SaveChanges
        string dbName = Guid.NewGuid().ToString("N");
        using (MerrickContext seedDb = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            (Account accA, _, _) = await SeedThreeAccountsAsync(seedDb);
            Account accD = new() { ID = 104, Name = "PlayerD", User = accA.User };
            seedDb.Accounts.Add(accD);
            await seedDb.SaveChangesAsync();

            FriendshipRepository repo = new(seedDb);
            (await repo.AddFriendshipAsync(101, 102, "Alpha")).Should().BeTrue();
            (await repo.AddFriendshipAsync(102, 103, "Beta")).Should().BeTrue();
            (await repo.AddFriendshipAsync(103, 104, "Survivors")).Should().BeTrue();
        }

        // Delete 102 via SaveChangesAsync WITHOUT .Include(a => a.ReverseFriendships)
        using (MerrickContext deleteAsyncDb = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            Account accB = await deleteAsyncDb.Accounts.SingleAsync(a => a.ID == 102);
            deleteAsyncDb.ChangeTracker.Entries<Friendship>().Should().BeEmpty();
            deleteAsyncDb.Accounts.Remove(accB);
            await deleteAsyncDb.SaveChangesAsync();
        }

        // Delete 101 via synchronous SaveChanges WITHOUT .Include(a => a.ReverseFriendships)
        using (MerrickContext deleteSyncDb = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            Account accA = deleteSyncDb.Accounts.Single(a => a.ID == 101);
            deleteSyncDb.ChangeTracker.Entries<Friendship>().Should().BeEmpty();
            deleteSyncDb.Accounts.Remove(accA);
            deleteSyncDb.SaveChanges();
        }

        using (MerrickContext verifyDb = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            List<Friendship> remaining = await verifyDb.Friendships.ToListAsync();
            remaining.Should().HaveCount(2, "only the 103 <-> 104 bidirectional pair should survive");
            remaining.Should().OnlyContain(f =>
                (f.AccountID == 103 && f.FriendAccountID == 104) ||
                (f.AccountID == 104 && f.FriendAccountID == 103));
        }
    }

    [Fact]
    [Trait("Category", "Manual")]
    public async Task AccountDeletion_RelationalSqlServerLocalDb_EnforcesUniqueIndexAndCascadesUntrackedReverseFriendships()
    {
        // Protects: Real relational SQL Server FK_Friendships_Accounts_FriendAccountID (RESTRICT/NO ACTION) during untracked Account delete
        // Fails when: CascadeDeleteReverseFriendships skips relational providers (throws SqlException 547 FK violation) or LINQ fails SQL translation
        // Seam: Live SQL Server (localdb)\MSSQLLocalDB temporary catalog with real foreign keys and unique indexes
        if (!NexusTestEnvironment.IsManualTestEnabled) return;

        string catalog = $"KongorFriendshipTest_{Guid.NewGuid():N}";
        DbContextOptions<MerrickContext> options = new DbContextOptionsBuilder<MerrickContext>()
            .UseSqlServer($"Server=(localdb)\\MSSQLLocalDB;Database={catalog};Trusted_Connection=True;MultipleActiveResultSets=true;Encrypt=False")
            .Options;

        await using MerrickContext setupDb = new(options);
        Microsoft.EntityFrameworkCore.Storage.IRelationalDatabaseCreator creator =
            Microsoft.EntityFrameworkCore.Infrastructure.AccessorExtensions.GetService<Microsoft.EntityFrameworkCore.Storage.IRelationalDatabaseCreator>(setupDb.Database);
        await creator.CreateAsync();
        try
        {
            string ddl = setupDb.Database.GenerateCreateScript()
                .Replace("ON DELETE SET NULL", "ON DELETE NO ACTION", StringComparison.Ordinal);
            string[] coreTables = ["Roles", "Clans", "Users", "Accounts", "Friendships"];
            foreach (string stmt in System.Text.RegularExpressions.Regex.Split(ddl, @"^\s*GO\s*$|(?<=;)\r?\n", System.Text.RegularExpressions.RegexOptions.Multiline))
            {
                bool isCoreStmt = stmt.Contains("SCHEMA_ID", StringComparison.Ordinal) ||
                    coreTables.Any(t =>
                        stmt.Contains($"CREATE TABLE [core].[{t}]", StringComparison.Ordinal) ||
                        stmt.Contains($"CREATE TABLE [nexus_auth].[{t}]", StringComparison.Ordinal) ||
                        stmt.Contains($"ON [core].[{t}]", StringComparison.Ordinal) ||
                        stmt.Contains($"ON [nexus_auth].[{t}]", StringComparison.Ordinal));
                if (!string.IsNullOrWhiteSpace(stmt) && isCoreStmt)
                {
                    await setupDb.Database.ExecuteSqlRawAsync(stmt);
                }
            }
            Role userRole = new() { Name = "User" };
            User u1 = new()
            {
                EmailAddress = "rel1@kongor.test",
                SRPPasswordSalt = "s1",
                SRPPasswordHash = "h1",
                PBKDF2PasswordHash = "p1",
                Role = userRole
            };
            Account accA = new() { Name = "RelHeroA", User = u1 };
            Account accB = new() { Name = "RelHeroB", User = u1 };
            Account accC = new() { Name = "RelHeroC", User = u1 };
            setupDb.Accounts.AddRange(accA, accB, accC);
            await setupDb.SaveChangesAsync();

            FriendshipRepository repo = new(setupDb);
            (await repo.AddFriendshipAsync(accA.ID, accB.ID, "RelationalSquad")).Should().BeTrue();
            (await repo.AddFriendshipAsync(accA.ID, accC.ID, "KeepAlive")).Should().BeTrue();

            // Verify relational unique index rejects duplicate raw (AccountID, FriendAccountID) insert
            await using (MerrickContext dupDb = new(options))
            {
                dupDb.Friendships.Add(new Friendship
                {
                    AccountID = accA.ID,
                    FriendAccountID = accB.ID,
                    Status = FriendshipStatus.Accepted,
                    FriendGroup = "Duplicate"
                });
                Func<Task> act = async () => await dupDb.SaveChangesAsync();
                await act.Should().ThrowAsync<DbUpdateException>();
            }

            // Delete accB in a brand-new relational DbContext with ZERO tracked Friendship entities:
            // previously threw SqlException 547 on FK_Friendships_Accounts_FriendAccountID when gated by !Database.IsRelational()!
            await using (MerrickContext deleteDb = new(options))
            {
                Account trackedB = await deleteDb.Accounts.SingleAsync(a => a.ID == accB.ID);
                deleteDb.ChangeTracker.Entries<Friendship>().Should().BeEmpty();
                deleteDb.Accounts.Remove(trackedB);
                await deleteDb.SaveChangesAsync();
            }

            await using (MerrickContext verifyDb = new(options))
            {
                List<Friendship> remaining = await verifyDb.Friendships.ToListAsync();
                remaining.Should().HaveCount(2);
                remaining.Should().NotContain(f => f.AccountID == accB.ID || f.FriendAccountID == accB.ID);
            }
        }
        finally
        {
            await setupDb.Database.EnsureDeletedAsync();
        }
    }

    [Fact]
    public async Task ConcurrentFriendshipAdditions_IndependentContexts_PersistAllPairsCleanly()
    {
        string dbName = Guid.NewGuid().ToString("N");

        // Seed 10 distinct account pairs (20 accounts)
        using (MerrickContext seedDb = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            User user = CreateTestUser(1);
            seedDb.Users.Add(user);

            for (int i = 1; i <= 20; i++)
            {
                seedDb.Accounts.Add(new Account { ID = 1000 + i, Name = $"Concurrent_{i}", User = user });
            }
            await seedDb.SaveChangesAsync();
        }

        // Concurrently add 10 friendship pairs across separate DbContexts
        List<Task<bool>> addTasks = [];
        for (int i = 1; i <= 10; i++)
        {
            int accA = 1000 + (i * 2 - 1); // 1001, 1003, ...
            int accB = 1000 + (i * 2);     // 1002, 1004, ...
            int pairIndex = i;

            addTasks.Add(Task.Run(async () =>
            {
                using MerrickContext ctx = TestDbContextFactory.CreateInMemoryContext(dbName);
                FriendshipRepository repo = new(ctx);
                return await repo.AddFriendshipAsync(accA, accB, $"Pair_{pairIndex}");
            }));
        }

        bool[] results = await Task.WhenAll(addTasks);
        results.All(r => r).Should().BeTrue();

        // Verify full state consistency
        using (MerrickContext verifyDb = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            FriendshipRepository repo = new(verifyDb);

            // Exactly 20 directed friendship rows (10 pairs * 2)
            List<Friendship> allRows = await verifyDb.Friendships.ToListAsync();
            allRows.Should().HaveCount(20);

            for (int i = 1; i <= 10; i++)
            {
                int accA = 1000 + (i * 2 - 1);
                int accB = 1000 + (i * 2);

                (await repo.AreFriendsAsync(accA, accB)).Should().BeTrue();
                (await repo.AreFriendsAsync(accB, accA)).Should().BeTrue();
            }
        }
    }

    #endregion
}
