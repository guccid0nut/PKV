// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using FluentAssertions;
using MERRICK.DatabaseContext.Entities.Core;
using MERRICK.DatabaseContext.Entities.Utility;
using MERRICK.DatabaseContext.Enumerations;
using MERRICK.DatabaseContext.Persistence;
using MERRICK.DatabaseContext.Repositories;
using MERRICK.DatabaseContext.Tests.Infrastructure;
using NEXUS.Contracts.Domain;
using Xunit;

namespace MERRICK.DatabaseContext.Tests;

public class AccountAuthenticationRepositoryTests
{
    private static User CreateTestUser(int id = 1, string email = "auth_player@kongor.net", Role? role = null) => new()
    {
        ID = id,
        EmailAddress = email,
        SRPPasswordSalt = "test_salt",
        SRPPasswordHash = "test_hash",
        PBKDF2PasswordHash = "test_pbkdf2",
        Role = role ?? new Role { ID = 1, Name = "User" }
    };

    [Fact]
    public async Task GetForAuthenticationAsync_LoadsUserAccountsAndClanMembersInSingleGraph()
    {
        string dbName = Guid.NewGuid().ToString("N");
        DateTimeOffset now = TimeProvider.System.GetUtcNow();

        using (MerrickContext seedDb = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            Role role = new() { ID = 1, Name = "User" };
            User user = CreateTestUser(10, "single_query@kongor.net", role);
            Clan clan = new() { ID = 20, Name = "Iron Legion", Tag = "IRON" };

            Account mainAccount = new()
            {
                ID = 100,
                Name = "LegionCommander",
                User = user,
                IsMain = true,
                Clan = clan,
                Type = AccountType.Normal,
                TimestampCreated = now.AddDays(-10)
            };

            Account subAccount = new()
            {
                ID = 101,
                Name = "LegionSub",
                User = user,
                IsMain = false,
                Clan = clan,
                Type = AccountType.Normal,
                TimestampCreated = now.AddDays(-2)
            };

            User otherUser = CreateTestUser(11, "clanmate@kongor.net", role);
            Account clanMate = new()
            {
                ID = 102,
                Name = "LegionMember",
                User = otherUser,
                IsMain = true,
                Clan = clan,
                Type = AccountType.Normal,
                TimestampCreated = now.AddDays(-5)
            };

            UserInventory inv = new()
            {
                ID = 1,
                UserID = 10,
                ItemCode = "Item_Token"
            };

            seedDb.Users.AddRange(user, otherUser);
            seedDb.Clans.Add(clan);
            seedDb.Accounts.AddRange(mainAccount, subAccount, clanMate);
            seedDb.UserInventories.Add(inv);
            await seedDb.SaveChangesAsync();
        }

        using (MerrickContext queryDb = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            AccountRepository repository = new(queryDb);

            // Act
            Account? account = await repository.GetForAuthenticationAsync("LegionCommander");

            // Assert
            account.Should().NotBeNull();
            account!.Name.Should().Be("LegionCommander");
            account.User.Should().NotBeNull();
            account.User!.Accounts.Should().HaveCount(2);
            account.User.Accounts[0].Name.Should().Be("LegionCommander");
            account.User.Accounts[1].Name.Should().Be("LegionSub");
            account.User.InventoryItems.Should().HaveCount(1);
            account.Clan.Should().NotBeNull();
            account.Clan!.Members.Should().HaveCount(3);
        }
    }

    [Fact]
    public async Task GetForAuthenticationAsync_CaseInsensitiveNickname_ResolvesCorrectAccount()
    {
        string dbName = Guid.NewGuid().ToString("N");

        using (MerrickContext seedDb = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            User user = CreateTestUser(30, "case_test@kongor.net");
            Account account = new()
            {
                ID = 300,
                Name = "ShadowBlade",
                User = user,
                IsMain = true,
                Type = AccountType.Normal
            };

            seedDb.Users.Add(user);
            seedDb.Accounts.Add(account);
            await seedDb.SaveChangesAsync();
        }

        using (MerrickContext queryDb = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            AccountRepository repository = new(queryDb);

            // Act - mixed casing query
            Account? result = await repository.GetForAuthenticationAsync("sHaDoWbLaDe");

            // Assert
            result.Should().NotBeNull();
            result!.ID.Should().Be(300);
            result.Name.Should().Be("ShadowBlade");
        }
    }

    [Fact]
    public async Task GetForPreAuthAsync_CaseInsensitive_LoadsUserAndClan()
    {
        string dbName = Guid.NewGuid().ToString("N");

        using (MerrickContext seedDb = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            User user = CreateTestUser(40, "preauth_test@kongor.net");
            Clan clan = new() { ID = 40, Name = "Alpha Squad", Tag = "ALPH" };
            Account account = new()
            {
                ID = 400,
                Name = "AlphaOne",
                User = user,
                Clan = clan,
                IsMain = true,
                Type = AccountType.Normal
            };

            seedDb.Users.Add(user);
            seedDb.Clans.Add(clan);
            seedDb.Accounts.Add(account);
            await seedDb.SaveChangesAsync();
        }

        using (MerrickContext queryDb = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            AccountRepository repository = new(queryDb);

            // Act - uppercase query
            Account? preAuth = await repository.GetForPreAuthAsync("ALPHAONE");

            // Assert
            preAuth.Should().NotBeNull();
            preAuth!.Name.Should().Be("AlphaOne");
            preAuth.User.Should().NotBeNull();
            preAuth.User.SRPPasswordSalt.Should().Be("test_salt");
            preAuth.Clan.Should().NotBeNull();
            preAuth.Clan!.Tag.Should().Be("ALPH");
        }
    }

    [Fact]
    public async Task GetForSwitchAuthAsync_LoadsSubAccountsAndClanMembers()
    {
        string dbName = Guid.NewGuid().ToString("N");
        DateTimeOffset now = TimeProvider.System.GetUtcNow();

        using (MerrickContext seedDb = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            User user = CreateTestUser(50, "switch_test@kongor.net");
            Clan clan = new() { ID = 50, Name = "Omega Clan", Tag = "OMEG" };

            Account main = new()
            {
                ID = 500,
                Name = "OmegaMain",
                User = user,
                Clan = clan,
                IsMain = true,
                TimestampCreated = now.AddDays(-10)
            };

            Account sub = new()
            {
                ID = 501,
                Name = "OmegaSmurf",
                User = user,
                Clan = clan,
                IsMain = false,
                TimestampCreated = now.AddDays(-1)
            };

            seedDb.Users.Add(user);
            seedDb.Clans.Add(clan);
            seedDb.Accounts.AddRange(main, sub);
            await seedDb.SaveChangesAsync();
        }

        using (MerrickContext queryDb = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            AccountRepository repository = new(queryDb);

            // Act - query by name with lowercase
            Account? switched = await repository.GetForSwitchAuthAsync(0, "omegasmurf");

            // Assert
            switched.Should().NotBeNull();
            switched!.ID.Should().Be(501);
            switched.User.Should().NotBeNull();
            switched.User.Accounts.Should().HaveCount(2);
            switched.Clan.Should().NotBeNull();
            switched.Clan!.Members.Should().HaveCount(2);
        }
    }

    [Fact]
    public async Task GetAccountIdByNicknameAsync_CaseInsensitive_ResolvesId()
    {
        string dbName = Guid.NewGuid().ToString("N");

        using (MerrickContext seedDb = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            User user = CreateTestUser(60, "nick_id@kongor.net");
            Account account = new()
            {
                ID = 600,
                Name = "Valkyrie",
                User = user,
                IsMain = true
            };

            seedDb.Users.Add(user);
            seedDb.Accounts.Add(account);
            await seedDb.SaveChangesAsync();
        }

        using (MerrickContext queryDb = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            AccountRepository repository = new(queryDb);

            int? id = await repository.GetAccountIdByNicknameAsync("vAlKyRiE");

            id.Should().Be(600);
        }
    }

    [Fact]
    public async Task ExistsByEmailAsync_And_GetUserByEmailAsync_CaseInsensitive()
    {
        string dbName = Guid.NewGuid().ToString("N");

        using (MerrickContext seedDb = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            User user = CreateTestUser(70, "player.one@kongor.net");
            Account account = new()
            {
                ID = 700,
                Name = "PlayerOne",
                User = user,
                IsMain = true
            };

            seedDb.Users.Add(user);
            seedDb.Accounts.Add(account);
            await seedDb.SaveChangesAsync();
        }

        using (MerrickContext queryDb = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            AccountRepository repository = new(queryDb);

            bool exists = await repository.ExistsByEmailAsync("PLAYER.ONE@KONGOR.NET");
            exists.Should().BeTrue();

            User? user = await repository.GetUserByEmailAsync("PLAYER.ONE@KONGOR.NET");
            user.Should().NotBeNull();
            user!.ID.Should().Be(70);

            User? userByAccount = await repository.GetUserByAccountNameAsync("playerone");
            userByAccount.Should().NotBeNull();
            userByAccount!.ID.Should().Be(70);
        }
    }
}
