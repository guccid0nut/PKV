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

/// <summary>
/// Empirical challenge tests authored by teamwork_preview_challenger_m2_2 for Milestone 2.
/// Stress-tests social graph isolation, pending/blocked status exclusion, dynamic clan tag & live name resolution,
/// and dual-directional cascade deletion integrity in core.Friendships.
/// </summary>
public class SocialGraphIsolationTests
{
    private static User CreateTestUser(int id, string email = "user@kongor.net") => new()
    {
        ID = id,
        EmailAddress = email,
        SRPPasswordSalt = "salt",
        SRPPasswordHash = "hash",
        PBKDF2PasswordHash = "pbkdf2",
        Role = new Role { ID = id, Name = "User" }
    };

    private static Account CreateTestAccount(int id, string name, User user, Clan? clan = null) => new()
    {
        ID = id,
        Name = name,
        UserID = user.ID,
        User = user,
        ClanID = clan?.ID,
        Clan = clan
    };

    #region Region 1: Multi-Account Social Graph Isolation

    [Fact]
    public async Task SocialGraph_MultiAccount_Partitions_RemainStrictlyIsolated()
    {
        // Scenario: 3 distinct account clusters
        // Cluster Alpha: Acc 100 mutual with 101, 102
        // Cluster Beta:  Acc 200 mutual with 201
        // Cluster Gamma: Acc 300 has 0 friends
        string dbName = Guid.NewGuid().ToString("N");
        using (MerrickContext db = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            User user1 = CreateTestUser(1, "u1@k.net");
            User user2 = CreateTestUser(2, "u2@k.net");
            db.Users.AddRange(user1, user2);

            Account a100 = CreateTestAccount(100, "Alpha_Leader", user1);
            Account a101 = CreateTestAccount(101, "Alpha_Member1", user1);
            Account a102 = CreateTestAccount(102, "Alpha_Member2", user1);

            Account b200 = CreateTestAccount(200, "Beta_Solo", user2);
            Account b201 = CreateTestAccount(201, "Beta_Partner", user2);

            Account c300 = CreateTestAccount(300, "Gamma_Hermit", user2);

            db.Accounts.AddRange(a100, a101, a102, b200, b201, c300);
            await db.SaveChangesAsync();

            FriendshipRepository repo = new(db);
            // Form Cluster Alpha mutual friendships
            await repo.AddFriendshipAsync(100, 101, "AlphaSquad");
            await repo.AddFriendshipAsync(100, 102, "AlphaSquad");
            await repo.AddFriendshipAsync(101, 102, "AlphaSquad");

            // Form Cluster Beta mutual friendship
            await repo.AddFriendshipAsync(200, 201, "BetaDuo");
        }

        using (MerrickContext db = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            FriendshipRepository repo = new(db);

            // 1. Verify Cluster Alpha isolation
            IReadOnlyList<FriendedPeer> peers100 = await repo.GetFriendedPeersAsync(100);
            peers100.Should().HaveCount(2);
            peers100.Select(p => p.ID).Should().BeEquivalentTo([101, 102]);
            peers100.Should().NotContain(p => p.ID == 200 || p.ID == 201 || p.ID == 300);

            IReadOnlyList<FriendedPeer> peers101 = await repo.GetFriendedPeersAsync(101);
            peers101.Should().HaveCount(2);
            peers101.Select(p => p.ID).Should().BeEquivalentTo([100, 102]);

            // 2. Verify Cluster Beta isolation
            IReadOnlyList<FriendedPeer> peers200 = await repo.GetFriendedPeersAsync(200);
            peers200.Should().HaveCount(1);
            peers200[0].ID.Should().Be(201);
            peers200[0].FriendGroup.Should().Be("BetaDuo");
            peers200.Should().NotContain(p => p.ID == 100 || p.ID == 101 || p.ID == 102 || p.ID == 300);

            // 3. Verify Cluster Gamma (Hermit) has zero friends
            IReadOnlyList<FriendedPeer> peers300 = await repo.GetFriendedPeersAsync(300);
            peers300.Should().BeEmpty();

            // 4. Verify cross-cluster AreFriendsAsync returns false
            (await repo.AreFriendsAsync(100, 200)).Should().BeFalse();
            (await repo.AreFriendsAsync(101, 201)).Should().BeFalse();
            (await repo.AreFriendsAsync(102, 300)).Should().BeFalse();
            (await repo.AreFriendsAsync(201, 300)).Should().BeFalse();

            // 5. Verify intra-cluster AreFriendsAsync returns true
            (await repo.AreFriendsAsync(100, 101)).Should().BeTrue();
            (await repo.AreFriendsAsync(101, 100)).Should().BeTrue();
            (await repo.AreFriendsAsync(200, 201)).Should().BeTrue();
        }
    }

    [Fact]
    public async Task SocialGraph_UnidirectionalFriendship_DoesNotLeakToTarget()
    {
        // Scenario: A one-way record exists (1 -> 2), but no reciprocal record (2 -> 1) exists.
        string dbName = Guid.NewGuid().ToString("N");
        using (MerrickContext db = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            User user = CreateTestUser(1);
            Account a1 = CreateTestAccount(1, "Initiator", user);
            Account a2 = CreateTestAccount(2, "UnawareTarget", user);

            db.Users.Add(user);
            db.Accounts.AddRange(a1, a2);

            // Directly inject asymmetric relationship
            db.Friendships.Add(new Friendship
            {
                AccountID = 1,
                FriendAccountID = 2,
                FriendGroup = "Crush",
                Status = FriendshipStatus.Accepted
            });
            await db.SaveChangesAsync();
        }

        using (MerrickContext db = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            FriendshipRepository repo = new(db);

            // Account 1 sees Account 2 in its peers
            IReadOnlyList<FriendedPeer> peers1 = await repo.GetFriendedPeersAsync(1);
            peers1.Should().ContainSingle();
            peers1[0].ID.Should().Be(2);

            // Account 2 MUST NOT see Account 1 in its peers because there is no directed edge 2 -> 1
            IReadOnlyList<FriendedPeer> peers2 = await repo.GetFriendedPeersAsync(2);
            peers2.Should().BeEmpty();

            // In-memory Account navigation properties on Account 2
            Account? acc2Loaded = await db.Accounts
                .Include(a => a.Friendships)
                .FirstOrDefaultAsync(a => a.ID == 2);
            acc2Loaded.Should().NotBeNull();
            acc2Loaded!.HasFriend(1).Should().BeFalse();
            acc2Loaded.FriendAccountIDs.Should().BeEmpty();
#pragma warning disable CS0618
            acc2Loaded.FriendedPeers.Should().BeEmpty();
#pragma warning restore CS0618
        }
    }

    [Theory]
    [InlineData(0, 10)]
    [InlineData(10, 0)]
    [InlineData(-1, 10)]
    [InlineData(10, -5)]
    [InlineData(50, 50)] // Self-friendship
    public async Task SocialGraph_BoundaryAccountIDs_RejectedGracefully(int id1, int id2)
    {
        string dbName = Guid.NewGuid().ToString("N");
        using MerrickContext db = TestDbContextFactory.CreateInMemoryContext(dbName);
        FriendshipRepository repo = new(db);

        // AreFriendsAsync must return false
        bool areFriends = await repo.AreFriendsAsync(id1, id2);
        areFriends.Should().BeFalse();

        // AddFriendshipAsync must return false
        bool added = await repo.AddFriendshipAsync(id1, id2);
        added.Should().BeFalse();

        // AcceptFriendshipAsync must return false
        bool accepted = await repo.AcceptFriendshipAsync(id1, id2);
        accepted.Should().BeFalse();
    }

    #endregion

    #region Region 2: Non-Accepted Status Exclusion (Pending, Blocked, Invalid)

    [Fact]
    public async Task StatusExclusion_PendingAndBlocked_ExcludedFromGetFriendedPeersAsync()
    {
        string dbName = Guid.NewGuid().ToString("N");
        using (MerrickContext db = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            User user = CreateTestUser(1);
            Account root = CreateTestAccount(10, "RootAccount", user);
            Account friendAcc = CreateTestAccount(11, "AcceptedBuddy", user);
            Account pendingAcc = CreateTestAccount(12, "PendingRequester", user);
            Account blockedAcc = CreateTestAccount(13, "BlockedEnemy", user);
            Account corruptAcc = CreateTestAccount(14, "CorruptStatusUser", user);

            db.Users.Add(user);
            db.Accounts.AddRange(root, friendAcc, pendingAcc, blockedAcc, corruptAcc);

            db.Friendships.AddRange(
                new Friendship { AccountID = 10, FriendAccountID = 11, Status = FriendshipStatus.Accepted, FriendGroup = "Good" },
                new Friendship { AccountID = 10, FriendAccountID = 12, Status = FriendshipStatus.Pending, FriendGroup = "Wait" },
                new Friendship { AccountID = 10, FriendAccountID = 13, Status = FriendshipStatus.Blocked, FriendGroup = "Bad" },
                new Friendship { AccountID = 10, FriendAccountID = 14, Status = (FriendshipStatus)99, FriendGroup = "Weird" }
            );
            await db.SaveChangesAsync();
        }

        using (MerrickContext db = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            FriendshipRepository repo = new(db);

            // GetFriendedPeersAsync must strictly return ONLY the Accepted peer
            IReadOnlyList<FriendedPeer> peers = await repo.GetFriendedPeersAsync(10);
            peers.Should().ContainSingle();
            peers[0].ID.Should().Be(11);
            peers[0].Name.Should().Be("AcceptedBuddy");
            peers[0].FriendGroup.Should().Be("Good");

            // Verify GetFriendshipsAsync filtering
            IReadOnlyList<Friendship> all = await repo.GetFriendshipsAsync(10);
            all.Should().HaveCount(4);

            IReadOnlyList<Friendship> onlyPending = await repo.GetFriendshipsAsync(10, FriendshipStatus.Pending);
            onlyPending.Should().ContainSingle();
            onlyPending[0].FriendAccountID.Should().Be(12);

            IReadOnlyList<Friendship> onlyBlocked = await repo.GetFriendshipsAsync(10, FriendshipStatus.Blocked);
            onlyBlocked.Should().ContainSingle();
            onlyBlocked[0].FriendAccountID.Should().Be(13);

            IReadOnlyList<Friendship> onlyAccepted = await repo.GetFriendshipsAsync(10, FriendshipStatus.Accepted);
            onlyAccepted.Should().ContainSingle();
            onlyAccepted[0].FriendAccountID.Should().Be(11);
        }
    }

    [Fact]
    public async Task StatusExclusion_InMemoryAccountHelpers_FilterOutNonAcceptedStatuses()
    {
        string dbName = Guid.NewGuid().ToString("N");
        using MerrickContext db = TestDbContextFactory.CreateInMemoryContext(dbName);
        User user = CreateTestUser(1);
        Account root = CreateTestAccount(20, "RootUser", user);
        Account friendAcc = CreateTestAccount(21, "FriendUser", user);
        Account pendingAcc = CreateTestAccount(22, "PendingUser", user);
        Account blockedAcc = CreateTestAccount(23, "BlockedUser", user);

        Friendship fAccepted = new() { AccountID = 20, FriendAccountID = 21, FriendAccount = friendAcc, Status = FriendshipStatus.Accepted, FriendGroup = "Main" };
        Friendship fPending = new() { AccountID = 20, FriendAccountID = 22, FriendAccount = pendingAcc, Status = FriendshipStatus.Pending, FriendGroup = "General" };
        Friendship fBlocked = new() { AccountID = 20, FriendAccountID = 23, FriendAccount = blockedAcc, Status = FriendshipStatus.Blocked, FriendGroup = "General" };

        root.Friendships.AddRange([fAccepted, fPending, fBlocked]);

        // Test HasFriend
        root.HasFriend(21).Should().BeTrue();
        root.HasFriend(22).Should().BeFalse();
        root.HasFriend(23).Should().BeFalse();
        root.HasFriend(999).Should().BeFalse();

        // Test FriendAccountIDs
        root.FriendAccountIDs.Should().Equal([21]);

        // Test FriendedPeers transitional projection
#pragma warning disable CS0618
        root.FriendedPeers.Should().ContainSingle();
        root.FriendedPeers[0].ID.Should().Be(21);
        root.FriendedPeers[0].Name.Should().Be("FriendUser");
        root.FriendedPeers[0].FriendGroup.Should().Be("Main");
#pragma warning restore CS0618
    }

    [Fact]
    public async Task StatusExclusion_MutualPendingOrBlocked_FailsAreFriendsAsync()
    {
        string dbName = Guid.NewGuid().ToString("N");
        using (MerrickContext db = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            User user = CreateTestUser(1);
            Account a1 = CreateTestAccount(31, "PlayerPending1", user);
            Account a2 = CreateTestAccount(32, "PlayerPending2", user);
            Account b1 = CreateTestAccount(33, "PlayerBlocked1", user);
            Account b2 = CreateTestAccount(34, "PlayerBlocked2", user);

            db.Users.Add(user);
            db.Accounts.AddRange(a1, a2, b1, b2);

            // Mutual Pending between 31 and 32
            db.Friendships.AddRange(
                new Friendship { AccountID = 31, FriendAccountID = 32, Status = FriendshipStatus.Pending },
                new Friendship { AccountID = 32, FriendAccountID = 31, Status = FriendshipStatus.Pending },
                // Mutual Blocked between 33 and 34
                new Friendship { AccountID = 33, FriendAccountID = 34, Status = FriendshipStatus.Blocked },
                new Friendship { AccountID = 34, FriendAccountID = 33, Status = FriendshipStatus.Blocked }
            );
            await db.SaveChangesAsync();
        }

        using (MerrickContext db = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            FriendshipRepository repo = new(db);

            (await repo.AreFriendsAsync(31, 32)).Should().BeFalse();
            (await repo.AreFriendsAsync(32, 31)).Should().BeFalse();

            (await repo.AreFriendsAsync(33, 34)).Should().BeFalse();
            (await repo.AreFriendsAsync(34, 33)).Should().BeFalse();
        }
    }

    [Fact]
    public async Task StatusExclusion_AcceptFriendshipAsync_TransitionsBothDirectionsToAccepted()
    {
        string dbName = Guid.NewGuid().ToString("N");
        using (MerrickContext db = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            User user = CreateTestUser(1);
            Account requester = CreateTestAccount(41, "Requester41", user);
            Account approver = CreateTestAccount(42, "Approver42", user);

            db.Users.Add(user);
            db.Accounts.AddRange(requester, approver);

            // Requester sent request (Pending)
            db.Friendships.Add(new Friendship
            {
                AccountID = 41,
                FriendAccountID = 42,
                FriendGroup = "General",
                Status = FriendshipStatus.Pending
            });
            await db.SaveChangesAsync();
        }

        using (MerrickContext db = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            FriendshipRepository repo = new(db);

            // Prior to acceptance, they are not friends
            (await repo.AreFriendsAsync(41, 42)).Should().BeFalse();
            (await repo.GetFriendedPeersAsync(41)).Should().BeEmpty();
            (await repo.GetFriendedPeersAsync(42)).Should().BeEmpty();

            // Approver (42) accepts friendship from Requester (41)
            bool acceptResult = await repo.AcceptFriendshipAsync(42, 41, "BestBuddies");
            acceptResult.Should().BeTrue();

            // After acceptance, both rows are Accepted and peers are visible
            (await repo.AreFriendsAsync(41, 42)).Should().BeTrue();
            (await repo.AreFriendsAsync(42, 41)).Should().BeTrue();

            IReadOnlyList<FriendedPeer> approverPeers = await repo.GetFriendedPeersAsync(42);
            approverPeers.Should().ContainSingle();
            approverPeers[0].ID.Should().Be(41);
            approverPeers[0].FriendGroup.Should().Be("BestBuddies");

            IReadOnlyList<FriendedPeer> requesterPeers = await repo.GetFriendedPeersAsync(41);
            requesterPeers.Should().ContainSingle();
            requesterPeers[0].ID.Should().Be(42);
            requesterPeers[0].FriendGroup.Should().Be("General");
        }
    }

    #endregion

    #region Region 3: Dynamic Clan Tag and Live Account Name Resolution

    [Fact]
    public async Task DynamicResolution_ClanTag_ReflectsLiveEntityChangesWithoutCache()
    {
        string dbName = Guid.NewGuid().ToString("N");
        using (MerrickContext db = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            User user = CreateTestUser(1);
            Clan clan = new() { ID = 1, Name = "Warlords", Tag = "WAR" };
            Account player = CreateTestAccount(50, "PlayerA", user);
            Account friend = CreateTestAccount(51, "FriendB", user, clan);

            db.Users.Add(user);
            db.Clans.Add(clan);
            db.Accounts.AddRange(player, friend);
            await db.SaveChangesAsync();

            FriendshipRepository repo = new(db);
            await repo.AddFriendshipAsync(50, 51, "General");
        }

        // 1. Initial query: ClanTag should be "WAR"
        using (MerrickContext db = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            FriendshipRepository repo = new(db);
            IReadOnlyList<FriendedPeer> peers = await repo.GetFriendedPeersAsync(50);
            peers.Should().ContainSingle();
            peers[0].ClanTag.Should().Be("WAR");
            peers[0].Name.Should().Be("FriendB");
        }

        // 2. Live Clan Tag update: Clan changes Tag to "PK"
        using (MerrickContext db = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            Clan? clan = await db.Clans.FindAsync(1);
            clan.Should().NotBeNull();
            clan!.Tag = "PK";
            await db.SaveChangesAsync();
        }

        // 3. Subsequent query: ClanTag must dynamically resolve to "PK" without stale JSON cache!
        using (MerrickContext db = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            FriendshipRepository repo = new(db);
            IReadOnlyList<FriendedPeer> peers = await repo.GetFriendedPeersAsync(50);
            peers.Should().ContainSingle();
            peers[0].ClanTag.Should().Be("PK");
            peers[0].Name.Should().Be("FriendB");
        }
    }

    [Fact]
    public async Task DynamicResolution_ClanMembership_JoinAndLeave_ReflectsImmediately()
    {
        string dbName = Guid.NewGuid().ToString("N");
        using (MerrickContext db = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            User user = CreateTestUser(1);
            Clan clan = new() { ID = 2, Name = "Templars", Tag = "TMP" };
            Account player = CreateTestAccount(60, "Player60", user);
            Account friend = CreateTestAccount(61, "Friend61", user); // Clanless initially

            db.Users.Add(user);
            db.Clans.Add(clan);
            db.Accounts.AddRange(player, friend);
            await db.SaveChangesAsync();

            FriendshipRepository repo = new(db);
            await repo.AddFriendshipAsync(60, 61, "General");
        }

        // 1. Initially clanless: ClanTag must be null
        using (MerrickContext db = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            FriendshipRepository repo = new(db);
            IReadOnlyList<FriendedPeer> peers = await repo.GetFriendedPeersAsync(60);
            peers[0].ClanTag.Should().BeNull();
        }

        // 2. Friend joins clan: ClanID set to 2
        using (MerrickContext db = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            Account? friend = await db.Accounts.FindAsync(61);
            friend!.ClanID = 2;
            await db.SaveChangesAsync();
        }

        // 3. Immediately reflects "TMP"
        using (MerrickContext db = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            FriendshipRepository repo = new(db);
            IReadOnlyList<FriendedPeer> peers = await repo.GetFriendedPeersAsync(60);
            peers[0].ClanTag.Should().Be("TMP");
        }

        // 4. Friend leaves clan: ClanID set to null
        using (MerrickContext db = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            Account? friend = await db.Accounts.FindAsync(61);
            friend!.ClanID = null;
            await db.SaveChangesAsync();
        }

        // 5. Immediately reflects null again
        using (MerrickContext db = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            FriendshipRepository repo = new(db);
            IReadOnlyList<FriendedPeer> peers = await repo.GetFriendedPeersAsync(60);
            peers[0].ClanTag.Should().BeNull();
        }
    }

    [Fact]
    public async Task DynamicResolution_AccountRename_ReflectsLiveFriendNameImmediately()
    {
        string dbName = Guid.NewGuid().ToString("N");
        using (MerrickContext db = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            User user = CreateTestUser(1);
            Account player = CreateTestAccount(70, "Player70", user);
            Account friend = CreateTestAccount(71, "OldNickname", user);

            db.Users.Add(user);
            db.Accounts.AddRange(player, friend);
            await db.SaveChangesAsync();

            FriendshipRepository repo = new(db);
            await repo.AddFriendshipAsync(70, 71);
        }

        // Update friend's name directly in Accounts table
        using (MerrickContext db = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            Account? friend = await db.Accounts.FindAsync(71);
            friend!.Name = "NewNicknameX";
            await db.SaveChangesAsync();
        }

        // Verify GetFriendedPeersAsync immediately yields the new name
        using (MerrickContext db = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            FriendshipRepository repo = new(db);
            IReadOnlyList<FriendedPeer> peers = await repo.GetFriendedPeersAsync(70);
            peers.Should().ContainSingle();
            peers[0].Name.Should().Be("NewNicknameX");
        }
    }

    [Fact]
    public async Task DynamicResolution_ClanDisbandment_ReflectsNullClanTagAcrossAllFriends()
    {
        string dbName = Guid.NewGuid().ToString("N");
        using (MerrickContext db = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            User user = CreateTestUser(1);
            Clan clan = new() { ID = 5, Name = "DoomedClan", Tag = "DOOM" };
            Account leader = CreateTestAccount(80, "Leader80", user, clan);
            Account member = CreateTestAccount(81, "Member81", user, clan);
            Account observer = CreateTestAccount(82, "Observer82", user); // External friend of member

            db.Users.Add(user);
            db.Clans.Add(clan);
            db.Accounts.AddRange(leader, member, observer);
            await db.SaveChangesAsync();

            FriendshipRepository friendRepo = new(db);
            await friendRepo.AddFriendshipAsync(82, 81); // observer -> member
        }

        // Observer sees DOOM tag
        using (MerrickContext db = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            FriendshipRepository friendRepo = new(db);
            IReadOnlyList<FriendedPeer> peers = await friendRepo.GetFriendedPeersAsync(82);
            peers[0].ClanTag.Should().Be("DOOM");

            // Disband the clan
            ClanRepository clanRepo = new(db);
            bool disbanded = await clanRepo.DisbandClanAsync(5);
            disbanded.Should().BeTrue();
        }

        // Observer now immediately sees null ClanTag for member
        using (MerrickContext db = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            FriendshipRepository friendRepo = new(db);
            IReadOnlyList<FriendedPeer> peers = await friendRepo.GetFriendedPeersAsync(82);
            peers.Should().ContainSingle();
            peers[0].ClanTag.Should().BeNull();
        }
    }

    #endregion

    #region Region 4: Cascade Deletion Integrity (Dual-Direction)

    [Fact]
    public async Task CascadeDeletion_DeletingAccount_PurgesBothForwardAndReverseFriendships()
    {
        // Scenario:
        // Mutual friendships: A(90) <-> B(91), A(90) <-> C(92), B(91) <-> C(92)
        // 6 directional rows in total.
        // Delete Account A(90).
        // Expected: All rows with AccountID==90 OR FriendAccountID==90 are deleted.
        // The mutual relationship between B and C remains completely intact (2 rows).
        string dbName = Guid.NewGuid().ToString("N");
        using (MerrickContext db = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            User user = CreateTestUser(1);
            Account a = CreateTestAccount(90, "AccA", user);
            Account b = CreateTestAccount(91, "AccB", user);
            Account c = CreateTestAccount(92, "AccC", user);

            db.Users.Add(user);
            db.Accounts.AddRange(a, b, c);
            await db.SaveChangesAsync();

            FriendshipRepository repo = new(db);
            await repo.AddFriendshipAsync(90, 91);
            await repo.AddFriendshipAsync(90, 92);
            await repo.AddFriendshipAsync(91, 92);
        }

        // Verify initial state: 6 friendships
        using (MerrickContext db = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            (await db.Friendships.CountAsync()).Should().Be(6);

            // Load Account A with its friendships and remove it
            Account? accA = await db.Accounts
                .Include(acc => acc.Friendships)
                .Include(acc => acc.ReverseFriendships)
                .FirstOrDefaultAsync(acc => acc.ID == 90);

            accA.Should().NotBeNull();
            db.Accounts.Remove(accA!);
            await db.SaveChangesAsync();
        }

        // Verify post-deletion state
        using (MerrickContext db = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            FriendshipRepository repo = new(db);

            // 1. Account 90 is gone
            Account? deleted = await db.Accounts.FindAsync(90);
            deleted.Should().BeNull();

            // 2. Zero friendships reference Account 90 in either direction
            bool anyReferencesA = await db.Friendships
                .AnyAsync(f => f.AccountID == 90 || f.FriendAccountID == 90);
            anyReferencesA.Should().BeFalse();

            // 3. Exactly 2 friendships remain: B -> C and C -> B
            List<Friendship> remaining = await db.Friendships.ToListAsync();
            remaining.Should().HaveCount(2);
            remaining.Should().ContainSingle(f => f.AccountID == 91 && f.FriendAccountID == 92);
            remaining.Should().ContainSingle(f => f.AccountID == 92 && f.FriendAccountID == 91);

            // 4. B's friends list contains only C
            IReadOnlyList<FriendedPeer> peersB = await repo.GetFriendedPeersAsync(91);
            peersB.Should().ContainSingle();
            peersB[0].ID.Should().Be(92);

            // 5. C's friends list contains only B
            IReadOnlyList<FriendedPeer> peersC = await repo.GetFriendedPeersAsync(92);
            peersC.Should().ContainSingle();
            peersC[0].ID.Should().Be(91);

            // 6. AreFriends queries
            (await repo.AreFriendsAsync(90, 91)).Should().BeFalse();
            (await repo.AreFriendsAsync(90, 92)).Should().BeFalse();
            (await repo.AreFriendsAsync(91, 92)).Should().BeTrue();
        }
    }

    [Fact]
    public async Task CascadeDeletion_DeletingAccount_PurgesPendingAndBlockedRelationships()
    {
        string dbName = Guid.NewGuid().ToString("N");
        using (MerrickContext db = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            User user = CreateTestUser(1);
            Account doomed = CreateTestAccount(110, "DoomedTarget", user);
            Account partner = CreateTestAccount(111, "Partner111", user);

            db.Users.Add(user);
            db.Accounts.AddRange(doomed, partner);

            // Doomed has pending and blocked records
            db.Friendships.AddRange(
                new Friendship { AccountID = 110, FriendAccountID = 111, Status = FriendshipStatus.Pending },
                new Friendship { AccountID = 111, FriendAccountID = 110, Status = FriendshipStatus.Blocked }
            );
            await db.SaveChangesAsync();
        }

        using (MerrickContext db = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            Account? doomed = await db.Accounts
                .Include(a => a.Friendships)
                .Include(a => a.ReverseFriendships)
                .FirstOrDefaultAsync(a => a.ID == 110);

            db.Accounts.Remove(doomed!);
            await db.SaveChangesAsync();
        }

        using (MerrickContext db = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            List<Friendship> all = await db.Friendships.ToListAsync();
            all.Should().BeEmpty();
        }
    }

    #endregion

    #region Region 5: FriendGroup Independent Updates & Mutation Concurrency

    [Fact]
    public async Task Friendship_UpdateFriendGroup_OperatesDirectlyOnTargetFriendshipWithoutAffectingOthers()
    {
        string dbName = Guid.NewGuid().ToString("N");
        using (MerrickContext db = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            User user = CreateTestUser(1);
            Account me = CreateTestAccount(120, "Me120", user);
            Account f1 = CreateTestAccount(121, "Friend121", user);
            Account f2 = CreateTestAccount(122, "Friend122", user);

            db.Users.Add(user);
            db.Accounts.AddRange(me, f1, f2);
            await db.SaveChangesAsync();

            FriendshipRepository repo = new(db);
            await repo.AddFriendshipAsync(120, 121, "DefaultGroup");
            await repo.AddFriendshipAsync(120, 122, "DefaultGroup");
        }

        using (MerrickContext db = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            FriendshipRepository repo = new(db);

            // Update only 120 -> 121 to "Favorites"
            bool updated = await repo.UpdateFriendGroupAsync(120, 121, "Favorites");
            updated.Should().BeTrue();

            // Verify 120 -> 121 is "Favorites"
            IReadOnlyList<FriendedPeer> peers = await repo.GetFriendedPeersAsync(120);
            FriendedPeer p121 = peers.Single(p => p.ID == 121);
            FriendedPeer p122 = peers.Single(p => p.ID == 122);

            p121.FriendGroup.Should().Be("Favorites");
            p122.FriendGroup.Should().Be("DefaultGroup"); // Untouched

            // Reverse relationship 121 -> 120 should remain untouched ("DefaultGroup")
            IReadOnlyList<FriendedPeer> reversePeers = await repo.GetFriendedPeersAsync(121);
            reversePeers.Single(p => p.ID == 120).FriendGroup.Should().Be("DefaultGroup");
        }
    }

    [Fact]
    public async Task Friendship_RemoveFriendshipBidirectional_HandlesNonExistentPartiesGracefully()
    {
        string dbName = Guid.NewGuid().ToString("N");
        using MerrickContext db = TestDbContextFactory.CreateInMemoryContext(dbName);
        FriendshipRepository repo = new(db);

        // Neither account exists
        FriendshipRemovalResult res1 = await repo.RemoveFriendshipBidirectionalAsync(9991, 9992);
        res1.Success.Should().BeFalse();
        res1.StatusCode.Should().Be(404);
        res1.ErrorMessage.Should().Be("Account Not Found");
    }

    #endregion

    #region Region 6: High-Cardinality, Alphabetical Ordering & Null Safety

    [Fact]
    public async Task SocialGraph_HighCardinality_ScalesAndMaintainsAlphabeticalOrder()
    {
        string dbName = Guid.NewGuid().ToString("N");
        using (MerrickContext db = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            User user = CreateTestUser(1);
            db.Users.Add(user);

            Account focal = CreateTestAccount(1, "FocalPlayer", user);
            db.Accounts.Add(focal);

            // 100 Accepted friends with varying names
            string[] rawNames = ["Echo", "Alpha", "Zulu", "Bravo", "Delta", "Charlie", "Foxtrot", "Golf", "Hotel", "India"];
            List<Account> friends = [];
            List<Friendship> friendships = [];

            for (int i = 0; i < 100; i++)
            {
                int friendId = 1000 + i;
                string name = $"{rawNames[i % rawNames.Length]}_{i:D3}";
                Account friendAcc = CreateTestAccount(friendId, name, user);
                friends.Add(friendAcc);

                friendships.Add(new Friendship
                {
                    AccountID = 1,
                    FriendAccountID = friendId,
                    FriendAccount = friendAcc,
                    Status = FriendshipStatus.Accepted,
                    FriendGroup = "General"
                });
            }

            // Also add 50 Pending relationships that MUST be excluded
            for (int i = 0; i < 50; i++)
            {
                int pendingId = 2000 + i;
                Account pendingAcc = CreateTestAccount(pendingId, $"Pending_{i:D3}", user);
                friends.Add(pendingAcc);

                friendships.Add(new Friendship
                {
                    AccountID = 1,
                    FriendAccountID = pendingId,
                    FriendAccount = pendingAcc,
                    Status = FriendshipStatus.Pending,
                    FriendGroup = "General"
                });
            }

            db.Accounts.AddRange(friends);
            db.Friendships.AddRange(friendships);
            await db.SaveChangesAsync();
        }

        using (MerrickContext db = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            FriendshipRepository repo = new(db);
            IReadOnlyList<FriendedPeer> peers = await repo.GetFriendedPeersAsync(1);

            // Must have exactly 100 accepted friends (all 50 pending excluded)
            peers.Should().HaveCount(100);
            peers.Any(p => p.Name.StartsWith("Pending_")).Should().BeFalse();

            // Must be strictly ordered alphabetically by Name
            List<string> returnedNames = peers.Select(p => p.Name).ToList();
            List<string> sortedNames = [.. returnedNames.OrderBy(n => n)];
            returnedNames.Should().Equal(sortedNames);
        }
    }

    [Fact]
    public async Task SocialGraph_NullFriendAccountNavigation_ProjectsSafelyWithoutException()
    {
        string dbName = Guid.NewGuid().ToString("N");
        using (MerrickContext db = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            User user = CreateTestUser(1);
            Account focal = CreateTestAccount(10, "Focal10", user);
            Account ghost = CreateTestAccount(11, "Ghost11", user);

            db.Users.Add(user);
            db.Accounts.AddRange(focal, ghost);

            // Friendship record pointing to ghost
            db.Friendships.Add(new Friendship
            {
                AccountID = 10,
                FriendAccountID = 11,
                Status = FriendshipStatus.Accepted,
                FriendGroup = "GhostGroup"
            });
            await db.SaveChangesAsync();
        }

        using (MerrickContext db = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            FriendshipRepository repo = new(db);
            IReadOnlyList<FriendedPeer> peers = await repo.GetFriendedPeersAsync(10);
            peers.Should().ContainSingle();
            peers[0].ID.Should().Be(11);
            peers[0].Name.Should().Be("Ghost11");
            peers[0].ClanTag.Should().BeNull();
            peers[0].FriendGroup.Should().Be("GhostGroup");
        }
    }

    [Fact]
    public async Task SocialGraph_AltAccountsUnderSameUser_CanBeMutualFriendsSafely()
    {
        string dbName = Guid.NewGuid().ToString("N");
        using (MerrickContext db = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            User singleUser = CreateTestUser(42, "sameowner@k.net");
            Account mainAcc = CreateTestAccount(501, "MainAccount", singleUser);
            Account smurfAcc = CreateTestAccount(502, "SmurfAccount", singleUser);

            db.Users.Add(singleUser);
            db.Accounts.AddRange(mainAcc, smurfAcc);
            await db.SaveChangesAsync();

            FriendshipRepository repo = new(db);
            bool added = await repo.AddFriendshipAsync(501, 502, "Alts");
            added.Should().BeTrue();
        }

        using (MerrickContext db = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            FriendshipRepository repo = new(db);

            (await repo.AreFriendsAsync(501, 502)).Should().BeTrue();
            (await repo.AreFriendsAsync(502, 501)).Should().BeTrue();

            IReadOnlyList<FriendedPeer> mainPeers = await repo.GetFriendedPeersAsync(501);
            mainPeers.Should().ContainSingle(p => p.ID == 502 && p.Name == "SmurfAccount" && p.FriendGroup == "Alts");

            IReadOnlyList<FriendedPeer> smurfPeers = await repo.GetFriendedPeersAsync(502);
            smurfPeers.Should().ContainSingle(p => p.ID == 501 && p.Name == "MainAccount" && p.FriendGroup == "Alts");
        }
    }

    #endregion
}
