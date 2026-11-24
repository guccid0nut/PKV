// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using FluentAssertions;
using MERRICK.DatabaseContext.Entities.Core;
using MERRICK.DatabaseContext.Entities.Relational;
using MERRICK.DatabaseContext.Entities.Store;
using MERRICK.DatabaseContext.Entities.Utility;
using MERRICK.DatabaseContext.Enumerations;
using MERRICK.DatabaseContext.Persistence;
using MERRICK.DatabaseContext.Repositories;
using MERRICK.DatabaseContext.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using NEXUS.Contracts.Data;
using NEXUS.Contracts.Domain;
using Xunit;

namespace MERRICK.DatabaseContext.Tests;

public class AdversarialRepositoryEdgeCasesTests
{
    private static int _nextInvId = 1000;

    private static User CreateTestUser(int id = 1, string email = "u@k.net", List<string>? ownedStoreItems = null) => new()
    {
        ID = id,
        EmailAddress = email,
        SRPPasswordSalt = "s",
        SRPPasswordHash = "h",
        PBKDF2PasswordHash = "p",
        Role = new Role { ID = 1, Name = "User" },
        InventoryItems = ownedStoreItems?.Select(code => new UserInventory
        {
            ID = System.Threading.Interlocked.Increment(ref _nextInvId),
            UserID = id,
            ItemCode = code,
            Quantity = 1,
            IsPermanent = true,
            TimestampAcquired = TimeProvider.System.GetUtcNow(),
            Source = "Test"
        }).ToList() ?? []
    };

    #region Clan Rank Permission Edge Cases

    [Trait("Category", "Manual")]
    [Fact]
    public async Task Clan_Officer_Cannot_Kick_Officer()
    {
        if (!NexusTestEnvironment.IsManualTestEnabled) return;
        string dbName = Guid.NewGuid().ToString("N");
        using (MerrickContext db = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            User user = CreateTestUser(1);
            Clan clan = new() { ID = 1, Name = "OfficersGuild", Tag = "OG" };
            Account off1 = new() { ID = 10, Name = "OfficerAlpha", User = user, Clan = clan, ClanTier = ClanTier.Officer };
            Account off2 = new() { ID = 11, Name = "OfficerBeta", User = user, Clan = clan, ClanTier = ClanTier.Officer };

            db.Users.Add(user);
            db.Clans.Add(clan);
            db.Accounts.AddRange(off1, off2);
            await db.SaveChangesAsync();
        }

        using (MerrickContext db = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            ClanRepository repo = new(db);
            ClanRankUpdateResult result = await repo.SetMemberRankAsync(10, 11, 1, "Remove");
            result.Success.Should().BeFalse();
            result.StatusCode.Should().Be(400);
            result.ErrorMessage.Should().Be("Officer cannot kick Officer");

            Account? off2Refreshed = await db.Accounts.FindAsync(11);
            off2Refreshed!.ClanID.Should().Be(1);
            off2Refreshed.ClanTier.Should().Be(ClanTier.Officer);
        }
    }

    [Fact]
    public async Task Clan_Officer_Cannot_Demote_Leader()
    {
        string dbName = Guid.NewGuid().ToString("N");
        using (MerrickContext db = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            User user = CreateTestUser(1);
            Clan clan = new() { ID = 2, Name = "RoyalGuild", Tag = "RG" };
            Account officer = new() { ID = 20, Name = "OfficerBob", User = user, Clan = clan, ClanTier = ClanTier.Officer };
            Account leader = new() { ID = 21, Name = "LeaderAlice", User = user, Clan = clan, ClanTier = ClanTier.Leader };

            db.Users.Add(user);
            db.Clans.Add(clan);
            db.Accounts.AddRange(officer, leader);
            await db.SaveChangesAsync();
        }

        using (MerrickContext db = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            ClanRepository repo = new(db);
            ClanRankUpdateResult result = await repo.SetMemberRankAsync(20, 21, 2, "Member");
            result.Success.Should().BeFalse();
            result.StatusCode.Should().Be(400);
            result.ErrorMessage.Should().Be("Cannot demote higher/equal rank");

            Account? leaderRefreshed = await db.Accounts.FindAsync(21);
            leaderRefreshed!.ClanTier.Should().Be(ClanTier.Leader);
        }
    }

    [Fact]
    public async Task Clan_Member_Cannot_Kick_Anyone()
    {
        string dbName = Guid.NewGuid().ToString("N");
        using (MerrickContext db = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            User user = CreateTestUser(1);
            Clan clan = new() { ID = 3, Name = "RebelGuild", Tag = "RBL" };
            Account member = new() { ID = 30, Name = "MemberCharlie", User = user, Clan = clan, ClanTier = ClanTier.Member };
            Account targetMember = new() { ID = 31, Name = "MemberDave", User = user, Clan = clan, ClanTier = ClanTier.Member };
            Account leader = new() { ID = 32, Name = "LeaderEve", User = user, Clan = clan, ClanTier = ClanTier.Leader };

            db.Users.Add(user);
            db.Clans.Add(clan);
            db.Accounts.AddRange(member, targetMember, leader);
            await db.SaveChangesAsync();
        }

        using (MerrickContext db = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            ClanRepository repo = new(db);

            // Member trying to kick Member
            ClanRankUpdateResult result1 = await repo.SetMemberRankAsync(30, 31, 3, "Remove");
            result1.Success.Should().BeFalse();
            result1.StatusCode.Should().Be(400);
            result1.ErrorMessage.Should().Be("Insufficient Permissions");

            // Member trying to kick Leader
            ClanRankUpdateResult result2 = await repo.SetMemberRankAsync(30, 32, 3, "Remove");
            result2.Success.Should().BeFalse();
            result2.StatusCode.Should().Be(400);
            result2.ErrorMessage.Should().Be("Insufficient Permissions");
        }
    }

    [Fact]
    public async Task Clan_Leader_Promoting_Officer_To_Leader_AutoDemotes_Requester()
    {
        string dbName = Guid.NewGuid().ToString("N");
        using (MerrickContext db = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            User user = CreateTestUser(1);
            Clan clan = new() { ID = 4, Name = "HierarchyClan", Tag = "HC" };
            Account oldLeader = new() { ID = 40, Name = "OldKing", User = user, Clan = clan, ClanTier = ClanTier.Leader };
            Account officer = new() { ID = 41, Name = "NewKing", User = user, Clan = clan, ClanTier = ClanTier.Officer };

            db.Users.Add(user);
            db.Clans.Add(clan);
            db.Accounts.AddRange(oldLeader, officer);
            await db.SaveChangesAsync();
        }

        using (MerrickContext db = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            ClanRepository repo = new(db);
            ClanRankUpdateResult result = await repo.SetMemberRankAsync(40, 41, 4, "Leader");
            result.Success.Should().BeTrue();
            result.StatusCode.Should().Be(200);
            result.NewTargetTier.Should().Be(ClanTier.Leader);
            result.NewRequesterTier.Should().Be(ClanTier.Officer);
            result.RedisPublishPayload.Should().Be("41:Leader:40:4:HierarchyClan");

            Account? oldLeaderRefreshed = await db.Accounts.FindAsync(40);
            Account? newLeaderRefreshed = await db.Accounts.FindAsync(41);
            oldLeaderRefreshed!.ClanTier.Should().Be(ClanTier.Officer);
            newLeaderRefreshed!.ClanTier.Should().Be(ClanTier.Leader);
        }
    }

    [Fact]
    public async Task Clan_Disbanding_Clan_Clears_All_Active_Members()
    {
        string dbName = Guid.NewGuid().ToString("N");
        using (MerrickContext db = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            User user = CreateTestUser(1);
            Clan clan = new() { ID = 5, Name = "DisbandClan", Tag = "DIS" };
            Account leader = new() { ID = 50, Name = "Leader50", User = user, Clan = clan, ClanTier = ClanTier.Leader };
            Account officer = new() { ID = 51, Name = "Officer51", User = user, Clan = clan, ClanTier = ClanTier.Officer };
            Account member1 = new() { ID = 52, Name = "Member52", User = user, Clan = clan, ClanTier = ClanTier.Member };
            Account member2 = new() { ID = 53, Name = "Member53", User = user, Clan = clan, ClanTier = ClanTier.Member };

            db.Users.Add(user);
            db.Clans.Add(clan);
            db.Accounts.AddRange(leader, officer, member1, member2);
            await db.SaveChangesAsync();
        }

        using (MerrickContext db = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            ClanRepository repo = new(db);
            bool result = await repo.DisbandClanAsync(5);
            result.Should().BeTrue();

            Clan? clanInDb = await db.Clans.FindAsync(5);
            clanInDb.Should().BeNull();

            List<Account> allMembers = await db.Accounts.Where(a => a.ID >= 50 && a.ID <= 53).ToListAsync();
            allMembers.Should().HaveCount(4);
            foreach (Account m in allMembers)
            {
                m.ClanID.Should().BeNull();
                m.ClanTier.Should().Be(ClanTier.None);
                m.TimestampJoinedClan.Should().BeNull();
            }
        }
    }

    [Fact]
    public async Task Clan_Officer_Cannot_Promote_Officer_To_Officer()
    {
        string dbName = Guid.NewGuid().ToString("N");
        using (MerrickContext db = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            User user = CreateTestUser(1);
            Clan clan = new() { ID = 6, Name = "PeerClan", Tag = "PC" };
            Account off1 = new() { ID = 60, Name = "Officer1", User = user, Clan = clan, ClanTier = ClanTier.Officer };
            Account off2 = new() { ID = 61, Name = "Officer2", User = user, Clan = clan, ClanTier = ClanTier.Officer };

            db.Users.Add(user);
            db.Clans.Add(clan);
            db.Accounts.AddRange(off1, off2);
            await db.SaveChangesAsync();
        }

        using (MerrickContext db = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            ClanRepository repo = new(db);
            ClanRankUpdateResult result = await repo.SetMemberRankAsync(60, 61, 6, "Officer");
            result.Success.Should().BeFalse();
            result.StatusCode.Should().Be(400);
            result.ErrorMessage.Should().Be("Officer cannot promote Officer");
        }
    }

    #endregion

    #region Friendship Bidirectional Removal Edge Cases

    [Fact]
    public async Task Friendship_Removing_When_Only_One_Side_Has_Relational_Record_Succeeds()
    {
        string dbName = Guid.NewGuid().ToString("N");
        using (MerrickContext db = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            User user = CreateTestUser(1);
            Account accA = new() { ID = 101, Name = "AsymA", User = user };
            Account accB = new() { ID = 102, Name = "AsymB", User = user };
            Friendship fAsym = new() { AccountID = 101, FriendAccountID = 102, FriendGroup = "General", Status = FriendshipStatus.Accepted };

            db.Users.Add(user);
            db.Accounts.AddRange(accA, accB);
            db.Friendships.Add(fAsym);
            await db.SaveChangesAsync();
        }

        using (MerrickContext db = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            FriendshipRepository repo = new(db);
            FriendshipRemovalResult result = await repo.RemoveFriendshipBidirectionalAsync(101, 102);
            result.Success.Should().BeTrue();
            result.Removed.Should().BeTrue();
            result.AccountId.Should().Be(101);
            result.BuddyId.Should().Be(102);

            List<Friendship> remaining = await db.Friendships.ToListAsync();
            remaining.Should().BeEmpty();
        }
    }

    [Fact]
    public async Task Friendship_Removing_When_Only_Relational_Record_Exists_Succeeds()
    {
        string dbName = Guid.NewGuid().ToString("N");
        using (MerrickContext db = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            User user = CreateTestUser(1);
            Account accA = new() { ID = 201, Name = "RelA", User = user };
            Account accB = new() { ID = 202, Name = "RelB", User = user };
            Friendship f = new() { AccountID = 201, FriendAccountID = 202, FriendGroup = "General", Status = FriendshipStatus.Accepted };

            db.Users.Add(user);
            db.Accounts.AddRange(accA, accB);
            db.Friendships.Add(f);
            await db.SaveChangesAsync();
        }

        using (MerrickContext db = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            FriendshipRepository repo = new(db);
            FriendshipRemovalResult result = await repo.RemoveFriendshipBidirectionalAsync(201, 202);
            result.Success.Should().BeTrue();
            result.Removed.Should().BeTrue();

            List<Friendship> remaining = await db.Friendships.ToListAsync();
            remaining.Should().BeEmpty();
        }
    }

    [Fact]
    public async Task Friendship_Removing_NonExistent_Friendship_Returns_Unchanged()
    {
        string dbName = Guid.NewGuid().ToString("N");
        using (MerrickContext db = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            User user = CreateTestUser(1);
            Account accA = new() { ID = 301, Name = "StrangerA", User = user };
            Account accB = new() { ID = 302, Name = "StrangerB", User = user };

            db.Users.Add(user);
            db.Accounts.AddRange(accA, accB);
            await db.SaveChangesAsync();
        }

        using (MerrickContext db = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            FriendshipRepository repo = new(db);
            FriendshipRemovalResult result = await repo.RemoveFriendshipBidirectionalAsync(301, 302);
            result.Success.Should().BeTrue();
            result.Removed.Should().BeFalse();
            result.StatusCode.Should().Be(200);
            result.AccountId.Should().Be(301);
            result.BuddyId.Should().Be(302);
        }
    }

    [Fact]
    public async Task Friendship_Removing_When_Account_Or_Buddy_NotFound_Returns_404()
    {
        string dbName = Guid.NewGuid().ToString("N");
        using (MerrickContext db = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            User user = CreateTestUser(1);
            Account accA = new() { ID = 401, Name = "RealAcc", User = user };

            db.Users.Add(user);
            db.Accounts.Add(accA);
            await db.SaveChangesAsync();
        }

        using (MerrickContext db = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            FriendshipRepository repo = new(db);

            FriendshipRemovalResult result1 = await repo.RemoveFriendshipBidirectionalAsync(999, 401);
            result1.Success.Should().BeFalse();
            result1.Removed.Should().BeFalse();
            result1.StatusCode.Should().Be(404);
            result1.ErrorMessage.Should().Be("Account Not Found");

            FriendshipRemovalResult result2 = await repo.RemoveFriendshipBidirectionalAsync(401, 999);
            result2.Success.Should().BeFalse();
            result2.Removed.Should().BeFalse();
            result2.StatusCode.Should().Be(404);
            result2.ErrorMessage.Should().Be("Buddy Not Found");
        }
    }

    [Fact]
    public async Task Friendship_Removing_Bidirectional_Relational_Records_Cleans_Both_Directions()
    {
        string dbName = Guid.NewGuid().ToString("N");
        using (MerrickContext db = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            User user = CreateTestUser(1);
            Account accA = new() { ID = 501, Name = "FullA", User = user };
            Account accB = new() { ID = 502, Name = "FullB", User = user };

            Friendship f1 = new() { AccountID = 501, FriendAccountID = 502, FriendGroup = "General", Status = FriendshipStatus.Accepted };
            Friendship f2 = new() { AccountID = 502, FriendAccountID = 501, FriendGroup = "General", Status = FriendshipStatus.Accepted };

            db.Users.Add(user);
            db.Accounts.AddRange(accA, accB);
            db.Friendships.AddRange(f1, f2);
            await db.SaveChangesAsync();
        }

        using (MerrickContext db = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            FriendshipRepository repo = new(db);
            FriendshipRemovalResult result = await repo.RemoveFriendshipBidirectionalAsync(501, 502);
            result.Success.Should().BeTrue();
            result.Removed.Should().BeTrue();

            List<Friendship> remaining = await db.Friendships.ToListAsync();
            remaining.Should().BeEmpty();
        }
    }

    #endregion

    #region Store Item Ownership Edge Cases

    [Fact]
    public async Task Store_ProductLookup_And_HasItem_CaseInsensitivity()
    {
        string dbName = Guid.NewGuid().ToString("N");
        using (MerrickContext db = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            StoreItem item = new()
            {
                Id = 1,
                Code = "Hero_Valkyrie.Alt2",
                Name = "Battle Valkyrie",
                LocalContent = "item_valk_alt2",
                Enabled = true,
                Purchasable = true
            };
            User user = CreateTestUser(10, "ci@k.net", ["HERO_CHRONOS.ALT"]);
            Account account = new() { ID = 100, Name = "CaseUser", User = user };
            UserInventory inv = new() { ID = 1, UserID = 10, AccountID = 100, ItemCode = "Hero_Magmus.Alt" };

            db.StoreItems.Add(item);
            db.Users.Add(user);
            db.Accounts.Add(account);
            db.UserInventory.Add(inv);
            await db.SaveChangesAsync();
        }

        using (MerrickContext db = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            StoreRepository repo = new(db);

            // Product by code lookup
            StoreItem? found = await repo.GetProductByCodeAsync("Hero_Valkyrie.Alt2");
            found.Should().NotBeNull();
            found!.Id.Should().Be(1);

            // Account ownership by nickname
            IReadOnlyList<string> codes = await repo.GetOwnedItemCodesAsync("CaseUser");
            codes.Should().HaveCount(2);
            codes.Should().Contain("HERO_CHRONOS.ALT");
            codes.Should().Contain("Hero_Magmus.Alt");

            // HasItemAsync
            bool hasChronos = await repo.HasItemAsync(100, "HERO_CHRONOS.ALT");
            bool hasMagmus = await repo.HasItemAsync(100, "Hero_Magmus.Alt");

            hasChronos.Should().BeTrue();
            hasMagmus.Should().BeTrue();
        }
    }

    [Fact]
    public async Task Store_Prefixed_ItemCodes_Matching_Logic()
    {
        string dbName = Guid.NewGuid().ToString("N");
        using (MerrickContext db = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            User user = CreateTestUser(20, "prefix@k.net", ["aa.Hero_Valkyrie.alt_skin"]);
            Account account = new() { ID = 200, Name = "PrefixUser", User = user };
            UserInventory inv = new() { ID = 2, UserID = 20, AccountID = 200, ItemCode = "Hero_Devourer.alt_clown" };

            db.Users.Add(user);
            db.Accounts.Add(account);
            db.UserInventory.Add(inv);
            await db.SaveChangesAsync();
        }

        using (MerrickContext db = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            StoreRepository repo = new(db);

            // Querying with exact full code
            bool hasFullValk = await repo.HasItemAsync(200, "aa.Hero_Valkyrie.alt_skin");
            hasFullValk.Should().BeTrue();

            // Querying with suffix after first dot
            bool hasSubValk = await repo.HasItemAsync(200, "Hero_Valkyrie.alt_skin");
            hasSubValk.Should().BeTrue();

            // Querying with tail suffix
            bool hasTailValk = await repo.HasItemAsync(200, "alt_skin");
            hasTailValk.Should().BeTrue();

            // Querying tail suffix for inventory item
            bool hasTailDevo = await repo.HasItemAsync(200, "alt_clown");
            hasTailDevo.Should().BeTrue();

            // Non-matching partial token (without dot separator) should NOT match
            bool hasPartial = await repo.HasItemAsync(200, "skin");
            hasPartial.Should().BeFalse();
        }
    }

    [Fact]
    public async Task Store_Empty_And_Whitespace_ItemCodes_HandledGracefully()
    {
        string dbName = Guid.NewGuid().ToString("N");
        using (MerrickContext db = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            User user = CreateTestUser(30, "empty@k.net");
            Account account = new() { ID = 300, Name = "EmptyUser", User = user };

            db.Users.Add(user);
            db.Accounts.Add(account);
            await db.SaveChangesAsync();
        }

        using (MerrickContext db = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            StoreRepository repo = new(db);

            StoreItem? productEmpty = await repo.GetProductByCodeAsync("");
            StoreItem? productWs = await repo.GetProductByCodeAsync("   ");
            productEmpty.Should().BeNull();
            productWs.Should().BeNull();

            bool hasItemEmpty = await repo.HasItemAsync(300, "");
            bool hasItemWs = await repo.HasItemAsync(300, "   ");
            hasItemEmpty.Should().BeFalse();
            hasItemWs.Should().BeFalse();

            IReadOnlyList<string> codesEmpty = await repo.GetOwnedItemCodesAsync("");
            IReadOnlyList<string> codesWs = await repo.GetOwnedItemCodesAsync("   ");
            codesEmpty.Should().BeEmpty();
            codesWs.Should().BeEmpty();
        }
    }

    [Fact]
    public async Task Store_Accounts_Without_Linked_Users_HandledGracefully()
    {
        string dbName = Guid.NewGuid().ToString("N");
        using (MerrickContext db = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            Account orphanedAccount = new() { ID = 400, Name = "OrphanAcc", User = null! };
            db.Accounts.Add(orphanedAccount);
            await db.SaveChangesAsync();
        }

        using (MerrickContext db = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            StoreRepository repo = new(db);

            IReadOnlyList<string> codesByName = await repo.GetOwnedItemCodesAsync("OrphanAcc");
            codesByName.Should().BeEmpty();

            IReadOnlyList<string> codesById = await repo.GetOwnedItemCodesByAccountIdAsync(400);
            codesById.Should().BeEmpty();

            bool hasItem = await repo.HasItemAsync(400, "Hero_Valkyrie.Alt");
            hasItem.Should().BeFalse();

            Account? storeAcc = await repo.GetAccountWithUserForStoreAsync("OrphanAcc");
            storeAcc.Should().BeNull();
        }
    }

    #endregion
}
