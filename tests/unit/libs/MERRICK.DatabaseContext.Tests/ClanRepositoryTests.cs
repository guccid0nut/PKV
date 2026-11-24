// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using FluentAssertions;
using MERRICK.DatabaseContext.Entities.Core;
using MERRICK.DatabaseContext.Entities.Utility;
using MERRICK.DatabaseContext.Persistence;
using MERRICK.DatabaseContext.Repositories;
using MERRICK.DatabaseContext.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using NEXUS.Contracts.Data;
using NEXUS.Contracts.Domain;
using Xunit;

namespace MERRICK.DatabaseContext.Tests;

public class ClanRepositoryTests
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
    public async Task ClanRepository_GetByIdAsync_ReturnsClanWhenFound()
    {
        string dbName = Guid.NewGuid().ToString("N");
        using (MerrickContext db = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            Clan clan = new() { ID = 10, Name = "Valkyrie Clan", Tag = "VALK" };
            db.Clans.Add(clan);
            await db.SaveChangesAsync();
        }

        using (MerrickContext db = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            ClanRepository repo = new(db);
            Clan? result = await repo.GetByIdAsync(10);
            result.Should().NotBeNull();
            result!.Name.Should().Be("Valkyrie Clan");
            result.Tag.Should().Be("VALK");
        }
    }

    [Fact]
    public async Task GetByNameAsync_CaseInsensitive_ReturnsClan()
    {
        string dbName = Guid.NewGuid().ToString("N");
        using (MerrickContext db = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            Clan clan = new() { ID = 20, Name = "ShadowKnights", Tag = "SK" };
            db.Clans.Add(clan);
            await db.SaveChangesAsync();
        }

        using (MerrickContext db = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            ClanRepository repo = new(db);
            Clan? result = await repo.GetByNameAsync("ShadowKnights");
            result.Should().NotBeNull();
            result!.ID.Should().Be(20);
            result.Tag.Should().Be("SK");
        }
    }

    [Fact]
    public async Task GetByTagAsync_CaseInsensitive_ReturnsClan()
    {
        string dbName = Guid.NewGuid().ToString("N");
        using (MerrickContext db = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            Clan clan = new() { ID = 30, Name = "FrostGuard", Tag = "FG" };
            db.Clans.Add(clan);
            await db.SaveChangesAsync();
        }

        using (MerrickContext db = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            ClanRepository repo = new(db);
            Clan? result = await repo.GetByTagAsync("FG");
            result.Should().NotBeNull();
            result!.ID.Should().Be(30);
            result.Name.Should().Be("FrostGuard");
        }
    }

    [Fact]
    public async Task ClanRepository_GetClanMembersAsync_OrdersByLeaderThenOfficerThenMember()
    {
        string dbName = Guid.NewGuid().ToString("N");
        using (MerrickContext db = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            User user = CreateTestUser(1);
            Clan clan = new() { ID = 40, Name = "RanksClan", Tag = "RC" };
            Account leader = new() { ID = 401, Name = "AliceLeader", User = user, Clan = clan, ClanTier = ClanTier.Leader };
            Account officer = new() { ID = 402, Name = "BobOfficer", User = user, Clan = clan, ClanTier = ClanTier.Officer };
            Account member1 = new() { ID = 403, Name = "DanMember", User = user, Clan = clan, ClanTier = ClanTier.Member };
            Account member2 = new() { ID = 404, Name = "CharlieMember", User = user, Clan = clan, ClanTier = ClanTier.Member };

            db.Users.Add(user);
            db.Clans.Add(clan);
            db.Accounts.AddRange(leader, officer, member1, member2);
            await db.SaveChangesAsync();
        }

        using (MerrickContext db = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            ClanRepository repo = new(db);
            IReadOnlyList<Account> members = await repo.GetClanMembersAsync(40);
            members.Should().HaveCount(4);
            members[0].Name.Should().Be("AliceLeader");
            members[1].Name.Should().Be("BobOfficer");
            members[2].Name.Should().Be("CharlieMember");
            members[3].Name.Should().Be("DanMember");
        }
    }

    [Fact]
    public async Task ClanRepository_GetAccountWithClanAsync_ReturnsAccountAndClan()
    {
        string dbName = Guid.NewGuid().ToString("N");
        using (MerrickContext db = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            User user = CreateTestUser(1);
            Clan clan = new() { ID = 50, Name = "Clan50", Tag = "C50" };
            Account account = new() { ID = 500, Name = "Player500", User = user, Clan = clan, ClanTier = ClanTier.Leader };

            db.Users.Add(user);
            db.Clans.Add(clan);
            db.Accounts.Add(account);
            await db.SaveChangesAsync();
        }

        using (MerrickContext db = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            ClanRepository repo = new(db);
            Account? acc = await repo.GetAccountWithClanAsync(500);
            acc.Should().NotBeNull();
            acc!.Clan.Should().NotBeNull();
            acc.Clan!.Name.Should().Be("Clan50");
        }
    }

    [Fact]
    public async Task SetMemberRankAsync_LeadershipTransfer_Succeeds()
    {
        string dbName = Guid.NewGuid().ToString("N");
        using (MerrickContext db = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            User user = CreateTestUser(1);
            Clan clan = new() { ID = 60, Name = "AlphaClan", Tag = "ALPH" };
            Account leader = new() { ID = 601, Name = "OldLeader", User = user, Clan = clan, ClanTier = ClanTier.Leader };
            Account target = new() { ID = 602, Name = "NewLeader", User = user, Clan = clan, ClanTier = ClanTier.Officer };

            db.Users.Add(user);
            db.Clans.Add(clan);
            db.Accounts.AddRange(leader, target);
            await db.SaveChangesAsync();
        }

        using (MerrickContext db = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            ClanRepository repo = new(db);
            ClanRankUpdateResult result = await repo.SetMemberRankAsync(601, 602, 60, "Leader");
            result.Success.Should().BeTrue();
            result.NewTargetTier.Should().Be(ClanTier.Leader);
            result.NewRequesterTier.Should().Be(ClanTier.Officer);
            result.RedisPublishPayload.Should().Be("602:Leader:601:60:AlphaClan");

            Account? refreshedLeader = await db.Accounts.FindAsync(601);
            Account? refreshedTarget = await db.Accounts.FindAsync(602);
            refreshedLeader!.ClanTier.Should().Be(ClanTier.Officer);
            refreshedTarget!.ClanTier.Should().Be(ClanTier.Leader);
        }
    }

    [Fact]
    public async Task SetMemberRankAsync_OfficerKickingOfficer_Fails()
    {
        string dbName = Guid.NewGuid().ToString("N");
        using (MerrickContext db = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            User user = CreateTestUser(1);
            Clan clan = new() { ID = 70, Name = "BetaClan", Tag = "BETA" };
            Account officer1 = new() { ID = 701, Name = "Officer1", User = user, Clan = clan, ClanTier = ClanTier.Officer };
            Account officer2 = new() { ID = 702, Name = "Officer2", User = user, Clan = clan, ClanTier = ClanTier.Officer };

            db.Users.Add(user);
            db.Clans.Add(clan);
            db.Accounts.AddRange(officer1, officer2);
            await db.SaveChangesAsync();
        }

        using (MerrickContext db = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            ClanRepository repo = new(db);
            ClanRankUpdateResult result = await repo.SetMemberRankAsync(701, 702, 70, "Remove");
            result.Success.Should().BeFalse();
            result.ErrorMessage.Should().Contain("Officer cannot kick Officer");
        }
    }

    [Fact]
    public async Task SetMemberRankAsync_LeaderKickingMember_Succeeds()
    {
        string dbName = Guid.NewGuid().ToString("N");
        using (MerrickContext db = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            User user = CreateTestUser(1);
            Clan clan = new() { ID = 80, Name = "GammaClan", Tag = "GAM" };
            Account leader = new() { ID = 801, Name = "Leader80", User = user, Clan = clan, ClanTier = ClanTier.Leader };
            Account member = new() { ID = 802, Name = "Member80", User = user, Clan = clan, ClanTier = ClanTier.Member };

            db.Users.Add(user);
            db.Clans.Add(clan);
            db.Accounts.AddRange(leader, member);
            await db.SaveChangesAsync();
        }

        using (MerrickContext db = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            ClanRepository repo = new(db);
            ClanRankUpdateResult result = await repo.SetMemberRankAsync(801, 802, 80, "Remove");
            result.Success.Should().BeTrue();
            result.IsKick.Should().BeTrue();
            result.NewTargetTier.Should().Be(ClanTier.None);

            Account? refreshedMember = await db.Accounts.FindAsync(802);
            refreshedMember!.ClanID.Should().BeNull();
            refreshedMember.ClanTier.Should().Be(ClanTier.None);
        }
    }

    [Fact]
    public async Task ClanRepository_CreateClanAsync_CreatesClanAndSetsCreatorAsLeader()
    {
        string dbName = Guid.NewGuid().ToString("N");
        using (MerrickContext db = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            User user = CreateTestUser(1);
            Account creator = new() { ID = 901, Name = "CreatorAcc", User = user };
            Account founding1 = new() { ID = 902, Name = "Founding1", User = user };
            Account founding2 = new() { ID = 903, Name = "Founding2", User = user };

            db.Users.Add(user);
            db.Accounts.AddRange(creator, founding1, founding2);
            await db.SaveChangesAsync();
        }

        using (MerrickContext db = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            ClanRepository repo = new(db);
            Clan created = await repo.CreateClanAsync("NewEmpire", "EMP", 901, [902, 903]);

            created.Should().NotBeNull();
            created.Name.Should().Be("NewEmpire");
            created.Tag.Should().Be("EMP");

            Account? refreshedCreator = await db.Accounts.FindAsync(901);
            refreshedCreator!.ClanTier.Should().Be(ClanTier.Leader);
            refreshedCreator.ClanID.Should().Be(created.ID);

            Account? refreshedFounding1 = await db.Accounts.FindAsync(902);
            refreshedFounding1!.ClanTier.Should().Be(ClanTier.Member);
            refreshedFounding1.ClanID.Should().Be(created.ID);
        }
    }

    [Fact]
    public async Task ClanRepository_DisbandClanAsync_ClearsMembersAndRemovesClan()
    {
        string dbName = Guid.NewGuid().ToString("N");
        int clanId;
        using (MerrickContext db = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            User user = CreateTestUser(1);
            Clan clan = new() { ID = 100, Name = "DisbandMe", Tag = "DIS" };
            Account leader = new() { ID = 1001, Name = "L1", User = user, Clan = clan, ClanTier = ClanTier.Leader };
            Account member = new() { ID = 1002, Name = "M1", User = user, Clan = clan, ClanTier = ClanTier.Member };

            db.Users.Add(user);
            db.Clans.Add(clan);
            db.Accounts.AddRange(leader, member);
            await db.SaveChangesAsync();
            clanId = clan.ID;
        }

        using (MerrickContext db = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            ClanRepository repo = new(db);
            bool disbanded = await repo.DisbandClanAsync(clanId);
            disbanded.Should().BeTrue();

            Clan? clanInDb = await db.Clans.FindAsync(clanId);
            clanInDb.Should().BeNull();

            Account? l1 = await db.Accounts.FindAsync(1001);
            Account? m1 = await db.Accounts.FindAsync(1002);
            l1!.ClanID.Should().BeNull();
            l1.ClanTier.Should().Be(ClanTier.None);
            m1!.ClanID.Should().BeNull();
            m1.ClanTier.Should().Be(ClanTier.None);
        }
    }

    [Fact]
    public async Task ClanRepository_ChangeClanTagAsync_UpdatesTagCorrectly()
    {
        string dbName = Guid.NewGuid().ToString("N");
        using (MerrickContext db = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            Clan clan = new() { ID = 110, Name = "TagChangeClan", Tag = "OLD" };
            db.Clans.Add(clan);
            await db.SaveChangesAsync();
        }

        using (MerrickContext db = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            ClanRepository repo = new(db);
            bool updated = await repo.ChangeClanTagAsync(110, "NEW");
            updated.Should().BeTrue();

            Clan? refreshed = await db.Clans.FindAsync(110);
            refreshed!.Tag.Should().Be("NEW");
        }
    }

    [Fact]
    public async Task ClanRepository_CreateClanAsync_RejectsDuplicateTagAndNameCaseInsensitively()
    {
        string dbName = Guid.NewGuid().ToString("N");
        using (MerrickContext db = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            User user = CreateTestUser(1);
            Account creator1 = new() { ID = 1201, Name = "TagCreator1", User = user };
            Account creator2 = new() { ID = 1202, Name = "TagCreator2", User = user };
            db.Users.Add(user);
            db.Accounts.AddRange(creator1, creator2);
            await db.SaveChangesAsync();
        }

        using (MerrickContext db = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            ClanRepository repo = new(db);
            await repo.CreateClanAsync("Kongor Vanguard", "K", 1201);

            Func<Task> dupTagSameCase = () => repo.CreateClanAsync("Other Clan", "K", 1202);
            await dupTagSameCase.Should().ThrowAsync<InvalidOperationException>()
                .WithMessage("*[K]*already taken*");

            Func<Task> dupTagLowerCase = () => repo.CreateClanAsync("Another Clan", "k", 1202);
            await dupTagLowerCase.Should().ThrowAsync<InvalidOperationException>()
                .WithMessage("*[k]*already taken*");

            Func<Task> dupNameCaseInsensitive = () => repo.CreateClanAsync("kongor vanguard", "KV", 1202);
            await dupNameCaseInsensitive.Should().ThrowAsync<InvalidOperationException>()
                .WithMessage("*already taken*");
        }
    }

    [Fact]
    public async Task ClanRepository_ChangeClanTagAsync_RejectsDuplicateTagOnAnotherClan_AllowsOwnCaseChange()
    {
        string dbName = Guid.NewGuid().ToString("N");
        using (MerrickContext db = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            db.Clans.AddRange(
                new Clan { ID = 130, Name = "Alpha Clan", Tag = "ALP" },
                new Clan { ID = 131, Name = "Beta Clan", Tag = "BET" });
            await db.SaveChangesAsync();
        }

        using (MerrickContext db = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            ClanRepository repo = new(db);

            Func<Task> takeOtherTag = () => repo.ChangeClanTagAsync(131, "alp");
            await takeOtherTag.Should().ThrowAsync<InvalidOperationException>()
                .WithMessage("*[alp]*already taken*");

            // Changing casing of own tag must succeed
            bool ownCaseUpdate = await repo.ChangeClanTagAsync(130, "alp");
            ownCaseUpdate.Should().BeTrue();
            (await db.Clans.FindAsync(130))!.Tag.Should().Be("alp");
        }
    }
}
