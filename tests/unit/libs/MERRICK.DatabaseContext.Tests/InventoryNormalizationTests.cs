// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using FluentAssertions;
using MERRICK.DatabaseContext.Entities.Core;
using MERRICK.DatabaseContext.Entities.Store;
using MERRICK.DatabaseContext.Entities.Utility;
using MERRICK.DatabaseContext.Persistence;
using MERRICK.DatabaseContext.Repositories;
using MERRICK.DatabaseContext.Services;
using MERRICK.DatabaseContext.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace MERRICK.DatabaseContext.Tests;

public sealed class InventoryNormalizationTests
{
    private static User CreateUser(int id, string email) => new()
    {
        ID = id,
        EmailAddress = email,
        PBKDF2PasswordHash = "pbkdf2_hash",
        SRPPasswordHash = "srp_hash",
        SRPPasswordSalt = "srp_salt",
        GoldCoins = 1000,
        SilverCoins = 5000,
        PlinkoTickets = 5,
        Role = new Role { ID = 1, Name = "User" }
    };

    private static Account CreateAccount(int accountId, int userId, User user, string name) => new()
    {
        ID = accountId,
        UserID = userId,
        User = user,
        Name = name,
        IsMain = true
    };

    [Fact]
    public async Task GrantItemAsync_NewItem_CreatesUserInventoryAndSyncsOwnedStoreItems()
    {
        using MerrickContext db = TestDbContextFactory.CreateInMemoryContext();
        User user = CreateUser(1, "norm1@kongor.net");
        Account account = CreateAccount(101, 1, user, "NormPlayer1");
        db.Users.Add(user);
        db.Accounts.Add(account);
        await db.SaveChangesAsync();

        InventoryRepository repo = new(db);

        await repo.GrantItemAsync(account.ID, "Hero_Pyromancer.alt1", quantity: 1);

        UserInventory? inv = await db.UserInventory
            .FirstOrDefaultAsync(i => i.UserID == user.ID && i.ItemCode == "Hero_Pyromancer.alt1");
        inv.Should().NotBeNull();
        inv!.Quantity.Should().Be(1);
        inv.AccountID.Should().Be(account.ID);
        inv.IsPermanent.Should().BeTrue();

        User? refreshedUser = await db.Users.Include(u => u.InventoryItems).FirstOrDefaultAsync(u => u.ID == user.ID);
        refreshedUser!.InventoryItems.Should().Contain(i => i.ItemCode == "Hero_Pyromancer.alt1");
    }

    [Fact]
    public async Task RevokeItemAsync_ExistingItem_RemovesFromUserInventoryAndOwnedStoreItems()
    {
        using MerrickContext db = TestDbContextFactory.CreateInMemoryContext();
        User user = CreateUser(2, "norm2@kongor.net");
        Account account = CreateAccount(102, 2, user, "NormPlayer2");
        db.Users.Add(user);
        db.Accounts.Add(account);
        await db.SaveChangesAsync();

        InventoryRepository repo = new(db);

        await repo.GrantItemAsync(account.ID, "Hero_Devourer.alt2", quantity: 1);

        bool revoked = await repo.RevokeItemAsync(account.ID, "Hero_Devourer.alt2");

        revoked.Should().BeTrue();

        UserInventory? inv = await db.UserInventory
            .FirstOrDefaultAsync(i => i.UserID == user.ID && i.ItemCode == "Hero_Devourer.alt2");
        inv.Should().BeNull();

        User? refreshedUser = await db.Users.Include(u => u.InventoryItems).FirstOrDefaultAsync(u => u.ID == user.ID);
        refreshedUser!.InventoryItems.Should().NotContain(i => i.ItemCode == "Hero_Devourer.alt2");
    }

    [Fact]
    public async Task RevokeItemAsync_QuantityGreaterThanOne_DecrementsQuantity()
    {
        using MerrickContext db = TestDbContextFactory.CreateInMemoryContext();
        User user = CreateUser(3, "norm3@kongor.net");
        Account account = CreateAccount(103, 3, user, "NormPlayer3");
        db.Users.Add(user);
        db.Accounts.Add(account);
        await db.SaveChangesAsync();

        InventoryRepository repo = new(db);

        await repo.GrantItemAsync(account.ID, "Item_Ticket", quantity: 2);

        bool revoked = await repo.RevokeItemAsync(account.ID, "Item_Ticket");

        revoked.Should().BeTrue();

        UserInventory? inv = await db.UserInventory
            .FirstOrDefaultAsync(i => i.UserID == user.ID && i.ItemCode == "Item_Ticket");
        inv.Should().NotBeNull();
        inv!.Quantity.Should().Be(1);
    }

    [Fact]
    public async Task RevokeItemAsync_NonExistentItem_ReturnsFalse()
    {
        using MerrickContext db = TestDbContextFactory.CreateInMemoryContext();
        User user = CreateUser(4, "norm4@kongor.net");
        Account account = CreateAccount(104, 4, user, "NormPlayer4");
        db.Users.Add(user);
        db.Accounts.Add(account);
        await db.SaveChangesAsync();

        InventoryRepository repo = new(db);

        bool revoked = await repo.RevokeItemAsync(account.ID, "NonExistentItem");

        revoked.Should().BeFalse();
    }

    [Fact]
    public async Task GetOwnedItemCodesAsync_NormalizesAndDeduplicatesUserInventory()
    {
        using MerrickContext db = TestDbContextFactory.CreateInMemoryContext();
        User user = CreateUser(5, "norm5@kongor.net");
        Account account = CreateAccount(105, 5, user, "NormPlayer5");

        db.Users.Add(user);
        db.Accounts.Add(account);
        db.UserInventory.AddRange(
            new UserInventory
            {
                UserID = user.ID,
                AccountID = account.ID,
                ItemCode = "Item_A",
                Quantity = 1,
                IsPermanent = true,
                TimestampAcquired = TimeProvider.System.GetUtcNow()
            },
            new UserInventory
            {
                UserID = user.ID,
                AccountID = account.ID,
                ItemCode = "Item_B",
                Quantity = 1,
                IsPermanent = true,
                TimestampAcquired = TimeProvider.System.GetUtcNow()
            },
            new UserInventory
            {
                UserID = user.ID,
                AccountID = account.ID,
                ItemCode = "Item_Shared",
                Quantity = 1,
                IsPermanent = true,
                TimestampAcquired = TimeProvider.System.GetUtcNow()
            },
            new UserInventory
            {
                UserID = user.ID,
                AccountID = account.ID,
                ItemCode = "Item_C",
                Quantity = 1,
                IsPermanent = true,
                TimestampAcquired = TimeProvider.System.GetUtcNow()
            }
        );
        await db.SaveChangesAsync();

        // Verify drop method executes safely and idempotently
        await DatabaseInitializer.EnsureLegacyOwnedStoreItemsDroppedAsync(db);
        await DatabaseInitializer.EnsureLegacyOwnedStoreItemsDroppedAsync(db);

        InventoryRepository repo = new(db);

        IReadOnlyList<string> codes = await repo.GetOwnedItemCodesAsync(account.ID);

        codes.Should().HaveCount(4);
        codes.Should().Contain(["Item_A", "Item_B", "Item_Shared", "Item_C"]);
    }
}
