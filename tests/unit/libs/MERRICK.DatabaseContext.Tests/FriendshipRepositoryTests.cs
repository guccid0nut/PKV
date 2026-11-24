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

public class FriendshipRepositoryTests
{
    private static User CreateTestUser(int id = 1, string email = "u@k.net") => new()
    {
        ID = id,
        EmailAddress = email,
        SRPPasswordSalt = "s",
        SRPPasswordHash = "h",
        PBKDF2PasswordHash = "p",
        Role = new Role { ID = 1, Name = "User" }
    };

    [Fact]
    public async Task FriendshipRepository_SearchAccountsByPrefixAsync_MatchesPrefixAndLimit()
    {
        string dbName = Guid.NewGuid().ToString("N");
        using (MerrickContext db = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            User user = CreateTestUser(1);
            db.Users.Add(user);
            db.Accounts.AddRange(
                new Account { ID = 1, Name = "Savage1", User = user },
                new Account { ID = 2, Name = "Savage2", User = user },
                new Account { ID = 3, Name = "Savage3", User = user },
                new Account { ID = 4, Name = "OtherPlayer", User = user }
            );
            await db.SaveChangesAsync();
        }

        using (MerrickContext db = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            FriendshipRepository repo = new(db);
            IReadOnlyList<Account> matches = await repo.SearchAccountsByPrefixAsync("Sav", 2);
            matches.Should().HaveCount(2);
            matches.All(a => a.Name.StartsWith("Savage", StringComparison.OrdinalIgnoreCase)).Should().BeTrue();
        }
    }

    [Fact]
    public async Task FriendshipRepository_GetFriendedPeersAsync_ReturnsAccountFriendedPeers()
    {
        string dbName = Guid.NewGuid().ToString("N");
        using (MerrickContext db = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            User user = CreateTestUser(1);
            Clan clanValk = new() { ID = 1, Name = "Valkyries", Tag = "VALK" };
            Clan clanPro = new() { ID = 2, Name = "Pros", Tag = "PRO" };

            Account account = new() { ID = 10, Name = "Account10", User = user };
            Account buddy20 = new() { ID = 20, Name = "Buddy20", User = user, Clan = clanValk };
            Account buddy30 = new() { ID = 30, Name = "Buddy30", User = user, Clan = clanPro };

            Friendship f1 = new()
            {
                AccountID = 10,
                FriendAccountID = 20,
                FriendGroup = "General",
                Status = FriendshipStatus.Accepted
            };
            Friendship f2 = new()
            {
                AccountID = 10,
                FriendAccountID = 30,
                FriendGroup = "Favorites",
                Status = FriendshipStatus.Accepted
            };

            db.Users.Add(user);
            db.Clans.AddRange(clanValk, clanPro);
            db.Accounts.AddRange(account, buddy20, buddy30);
            db.Friendships.AddRange(f1, f2);
            await db.SaveChangesAsync();
        }

        using (MerrickContext db = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            FriendshipRepository repo = new(db);
            IReadOnlyList<FriendedPeer> peers = await repo.GetFriendedPeersAsync(10);
            peers.Should().HaveCount(2);
            peers.Should().Contain(p => p.ID == 20 && p.Name == "Buddy20" && p.ClanTag == "VALK" && p.FriendGroup == "General");
            peers.Should().Contain(p => p.ID == 30 && p.Name == "Buddy30" && p.ClanTag == "PRO" && p.FriendGroup == "Favorites");
        }
    }

    [Fact]
    public async Task FriendshipRepository_GetFriendshipsAsync_ReturnsRelationalFriendshipsWithFriendAccount()
    {
        string dbName = Guid.NewGuid().ToString("N");
        using (MerrickContext db = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            User user = CreateTestUser(1);
            Clan clan = new() { ID = 1, Name = "EliteClan", Tag = "ELT" };
            Account acc1 = new() { ID = 100, Name = "Acc100", User = user };
            Account acc2 = new() { ID = 200, Name = "Acc200", User = user, Clan = clan };

            Friendship friendship = new()
            {
                ID = 1,
                AccountID = 100,
                FriendAccountID = 200,
                FriendGroup = "Competitive",
                Status = FriendshipStatus.Accepted
            };

            db.Users.Add(user);
            db.Clans.Add(clan);
            db.Accounts.AddRange(acc1, acc2);
            db.Friendships.Add(friendship);
            await db.SaveChangesAsync();
        }

        using (MerrickContext db = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            FriendshipRepository repo = new(db);
            IReadOnlyList<Friendship> list = await repo.GetFriendshipsAsync(100);
            list.Should().HaveCount(1);
            list[0].FriendAccount.Should().NotBeNull();
            list[0].FriendAccount!.Name.Should().Be("Acc200");
            list[0].FriendAccount!.Clan.Should().NotBeNull();
            list[0].FriendAccount!.Clan!.Tag.Should().Be("ELT");
        }
    }

    [Fact]
    public async Task FriendshipRepository_AreFriendsAsync_ReturnsTrueWhenInRelationalTable()
    {
        string dbName = Guid.NewGuid().ToString("N");
        using (MerrickContext db = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            User user = CreateTestUser(1);
            Account acc1 = new() { ID = 11, Name = "Acc11", User = user };
            Account acc2 = new() { ID = 12, Name = "Acc12", User = user };
            Account acc3 = new() { ID = 13, Name = "Acc13", User = user };

            Friendship f1 = new()
            {
                AccountID = 11,
                FriendAccountID = 12,
                FriendGroup = "General",
                Status = FriendshipStatus.Accepted
            };

            db.Users.Add(user);
            db.Accounts.AddRange(acc1, acc2, acc3);
            db.Friendships.Add(f1);
            await db.SaveChangesAsync();
        }

        using (MerrickContext db = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            FriendshipRepository repo = new(db);
            bool areFriends12 = await repo.AreFriendsAsync(11, 12);
            bool areFriends21 = await repo.AreFriendsAsync(12, 11);
            bool areFriends13 = await repo.AreFriendsAsync(11, 13);

            areFriends12.Should().BeTrue();
            areFriends21.Should().BeTrue();
            areFriends13.Should().BeFalse();
        }
    }

    [Fact]
    public async Task FriendshipRepository_AddFriendshipAsync_AddsBidirectionallyToFriendshipsTable()
    {
        string dbName = Guid.NewGuid().ToString("N");
        using (MerrickContext db = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            User user = CreateTestUser(1);
            Clan clan = new() { ID = 2, Name = "Clan2", Tag = "C2" };
            Account accA = new() { ID = 21, Name = "PlayerA", User = user };
            Account accB = new() { ID = 22, Name = "PlayerB", User = user, Clan = clan };

            db.Users.Add(user);
            db.Clans.Add(clan);
            db.Accounts.AddRange(accA, accB);
            await db.SaveChangesAsync();
        }

        using (MerrickContext db = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            FriendshipRepository repo = new(db);
            bool added = await repo.AddFriendshipAsync(21, 22, "Teammates");
            added.Should().BeTrue();

            List<Friendship> friendships = await db.Friendships
                .Include(f => f.FriendAccount)
                    .ThenInclude(fa => fa!.Clan)
                .ToListAsync();

            friendships.Should().HaveCount(2);
            friendships.Should().ContainSingle(f => f.AccountID == 21 && f.FriendAccountID == 22 && f.FriendGroup == "Teammates" && f.FriendAccount!.Name == "PlayerB" && f.FriendAccount.Clan!.Tag == "C2");
            friendships.Should().ContainSingle(f => f.AccountID == 22 && f.FriendAccountID == 21 && f.FriendGroup == "Teammates" && f.FriendAccount!.Name == "PlayerA");
        }
    }

    [Fact]
    public async Task FriendshipRepository_RemoveFriendshipBidirectionalAsync_RemovesFromFriendshipsTable()
    {
        string dbName = Guid.NewGuid().ToString("N");
        using (MerrickContext db = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            User user = CreateTestUser(1);
            Account accA = new() { ID = 31, Name = "RemA", User = user };
            Account accB = new() { ID = 32, Name = "RemB", User = user };

            Friendship f1 = new() { AccountID = 31, FriendAccountID = 32, FriendGroup = "General", Status = FriendshipStatus.Accepted };
            Friendship f2 = new() { AccountID = 32, FriendAccountID = 31, FriendGroup = "General", Status = FriendshipStatus.Accepted };

            db.Users.Add(user);
            db.Accounts.AddRange(accA, accB);
            db.Friendships.AddRange(f1, f2);
            await db.SaveChangesAsync();
        }

        using (MerrickContext db = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            FriendshipRepository repo = new(db);
            FriendshipRemovalResult result = await repo.RemoveFriendshipBidirectionalAsync(31, 32);
            result.Success.Should().BeTrue();
            result.Removed.Should().BeTrue();
            result.AccountId.Should().Be(31);
            result.BuddyId.Should().Be(32);

            List<Friendship> remainingFriendships = await db.Friendships.ToListAsync();
            remainingFriendships.Should().BeEmpty();
        }
    }

    [Fact]
    public async Task FriendshipRepository_UpdateFriendGroupAsync_UpdatesGroupNameInFriendshipsTable()
    {
        string dbName = Guid.NewGuid().ToString("N");
        using (MerrickContext db = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            User user = CreateTestUser(1);
            Account accA = new() { ID = 41, Name = "GrpA", User = user };
            Account accB = new() { ID = 42, Name = "GrpB", User = user };
            Friendship f1 = new() { AccountID = 41, FriendAccountID = 42, FriendGroup = "General", Status = FriendshipStatus.Accepted };

            db.Users.Add(user);
            db.Accounts.AddRange(accA, accB);
            db.Friendships.Add(f1);
            await db.SaveChangesAsync();
        }

        using (MerrickContext db = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            FriendshipRepository repo = new(db);
            bool updated = await repo.UpdateFriendGroupAsync(41, 42, "VIP");
            updated.Should().BeTrue();

            Friendship? refreshedFriendship = await db.Friendships.FirstOrDefaultAsync(f => f.AccountID == 41 && f.FriendAccountID == 42);
            refreshedFriendship.Should().NotBeNull();
            refreshedFriendship!.FriendGroup.Should().Be("VIP");
            refreshedFriendship.TimestampUpdated.Should().NotBeNull();
        }
    }

    [Fact]
    public async Task GetFriendedPeersAsync_FriendWithoutClan_ProjectsNullClanTag()
    {
        string dbName = Guid.NewGuid().ToString("N");
        using (MerrickContext db = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            User user = CreateTestUser(1);
            Account account = new() { ID = 51, Name = "SoloAccount", User = user };
            Account friend = new() { ID = 52, Name = "ClanlessFriend", User = user, Clan = null };

            Friendship f = new()
            {
                AccountID = 51,
                FriendAccountID = 52,
                FriendGroup = "General",
                Status = FriendshipStatus.Accepted
            };

            db.Users.Add(user);
            db.Accounts.AddRange(account, friend);
            db.Friendships.Add(f);
            await db.SaveChangesAsync();
        }

        using (MerrickContext db = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            FriendshipRepository repo = new(db);
            IReadOnlyList<FriendedPeer> peers = await repo.GetFriendedPeersAsync(51);
            peers.Should().ContainSingle();
            peers[0].ID.Should().Be(52);
            peers[0].Name.Should().Be("ClanlessFriend");
            peers[0].ClanTag.Should().BeNull();
        }
    }

    [Fact]
    public async Task AddFriendshipAsync_Idempotent_ReaddingExistingDoesNotDuplicateRows()
    {
        string dbName = Guid.NewGuid().ToString("N");
        using (MerrickContext db = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            User user = CreateTestUser(1);
            Account accA = new() { ID = 61, Name = "IdemA", User = user };
            Account accB = new() { ID = 62, Name = "IdemB", User = user };
            db.Users.Add(user);
            db.Accounts.AddRange(accA, accB);
            await db.SaveChangesAsync();
        }

        using (MerrickContext db = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            FriendshipRepository repo = new(db);
            bool firstAdd = await repo.AddFriendshipAsync(61, 62, "Group1");
            bool secondAdd = await repo.AddFriendshipAsync(61, 62, "Group2");

            firstAdd.Should().BeTrue();
            secondAdd.Should().BeTrue();

            List<Friendship> friendships = await db.Friendships.ToListAsync();
            friendships.Should().HaveCount(2); // exactly 1 forward, 1 reverse
            friendships.All(f => f.FriendGroup == "Group2").Should().BeTrue();
        }
    }

    [Fact]
    public async Task FriendshipRepository_GetFriendshipsAsync_FiltersByAcceptedStatus()
    {
        string dbName = Guid.NewGuid().ToString("N");
        using (MerrickContext db = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            User user = CreateTestUser(1);
            Account acc1 = new() { ID = 71, Name = "FilterUser", User = user };
            Account acc2 = new() { ID = 72, Name = "AcceptedFriend", User = user };
            Account acc3 = new() { ID = 73, Name = "PendingFriend", User = user };

            Friendship fAccepted = new() { AccountID = 71, FriendAccountID = 72, Status = FriendshipStatus.Accepted };
            Friendship fPending = new() { AccountID = 71, FriendAccountID = 73, Status = FriendshipStatus.Pending };

            db.Users.Add(user);
            db.Accounts.AddRange(acc1, acc2, acc3);
            db.Friendships.AddRange(fAccepted, fPending);
            await db.SaveChangesAsync();
        }

        using (MerrickContext db = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            FriendshipRepository repo = new(db);
            IReadOnlyList<FriendedPeer> peers = await repo.GetFriendedPeersAsync(71);
            peers.Should().ContainSingle(p => p.ID == 72);
            peers.Should().NotContain(p => p.ID == 73);
        }
    }
}
