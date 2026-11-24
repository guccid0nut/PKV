// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using ASPIRE.Tests.Infrastructure;
using FluentAssertions;
using MERRICK.DatabaseContext.Entities.Core;
using MERRICK.DatabaseContext.Entities.Store;
using MERRICK.DatabaseContext.Entities.Utility;
using MERRICK.DatabaseContext.Persistence;
using MERRICK.DatabaseContext.Repositories;
using Microsoft.EntityFrameworkCore;
using NEXUS.Contracts.Data;
using Xunit;

namespace ASPIRE.Tests.MERRICK.DatabaseContext.Tests;

public class StoreRepositoryTests
{
    private static User CreateTestUser(int id = 1, string email = "u@k.net", List<string>? ownedStoreItems = null) => new()
    {
        ID = id,
        EmailAddress = email,
        SRPPasswordSalt = "s",
        SRPPasswordHash = "h",
        PBKDF2PasswordHash = "p",
        Role = new Role { ID = 1, Name = "User" },
        InventoryItems = (ownedStoreItems ?? []).Select(code => new UserInventory { UserId = id, ItemCode = code, GrantSource = "Test" }).ToList()
    };

    [Trait("Category", "Manual")]
    [Fact]
    public async Task StoreRepository_GetProductByIdAsync_ReturnsProductWhenFound()
    {
        if (!NexusTestEnvironment.IsManualTestEnabled) return;
        string dbName = Guid.NewGuid().ToString("N");
        using (MerrickContext db = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            StoreItem item = new()
            {
                Id = 101,
                Name = "Flint Beard",
                Code = "Hero_FlintBeastwood.Alt",
                LocalContent = "item_flint_alt",
                Enabled = true,
                Purchasable = true,
                Price = 250,
                PriceInMMP = 500
            };
            db.StoreItems.Add(item);
            await db.SaveChangesAsync();
        }

        using (MerrickContext db = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            StoreRepository repo = new(db);
            StoreItem? found = await repo.GetProductByIdAsync(101);
            found.Should().NotBeNull();
            found!.Name.Should().Be("Flint Beard");
            found.Price.Should().Be(250);
        }
    }

    [Fact]
    public async Task GetProductByCodeAsync_CaseInsensitive_ReturnsProduct()
    {
        string dbName = Guid.NewGuid().ToString("N");
        using (MerrickContext db = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            StoreItem item = new()
            {
                Id = 102,
                Name = "Valkyrie Valkyrie",
                Code = "Hero_Valkyrie.Alt2",
                LocalContent = "item_valk_alt2",
                Enabled = true,
                Purchasable = true,
                Price = 300,
                PriceInMMP = 600
            };
            db.StoreItems.Add(item);
            await db.SaveChangesAsync();
        }

        using (MerrickContext db = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            StoreRepository repo = new(db);
            StoreItem? found = await repo.GetProductByCodeAsync("hero_valkyrie.alt2");
            found.Should().NotBeNull();
            found!.Id.Should().Be(102);
        }
    }

    [Fact]
    public async Task StoreRepository_GetCatalogMethods_FilterCorrectly()
    {
        string dbName = Guid.NewGuid().ToString("N");
        using (MerrickContext db = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            db.StoreItems.AddRange(
                new StoreItem { Id = 1, Name = "Item1", Code = "C1", LocalContent = "L1", Enabled = true, Purchasable = true, UpgradeType = 1 },
                new StoreItem { Id = 2, Name = "Item2", Code = "C2", LocalContent = "L2", Enabled = true, Purchasable = false, UpgradeType = 1 },
                new StoreItem { Id = 3, Name = "Item3", Code = "C3", LocalContent = "L3", Enabled = false, Purchasable = true, UpgradeType = 2 }
            );
            await db.SaveChangesAsync();
        }

        using (MerrickContext db = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            StoreRepository repo = new(db);

            IReadOnlyList<StoreItem> catalog = await repo.GetStoreCatalogAsync();
            catalog.Should().HaveCount(3);

            IReadOnlyList<StoreItem> enabled = await repo.GetEnabledProductsAsync();
            enabled.Should().HaveCount(2);

            IReadOnlyList<StoreItem> purchasable = await repo.GetPurchasableProductsAsync();
            purchasable.Should().HaveCount(1);
            purchasable[0].Id.Should().Be(1);

            IReadOnlyList<StoreItem> byType = await repo.GetProductsByTypeAsync(1);
            byType.Should().HaveCount(2);
        }
    }

    [Fact]
    public async Task StoreRepository_GetOwnedItemCodesAsync_ReturnsOwnedItemCodesFromUserInventory()
    {
        string dbName = Guid.NewGuid().ToString("N");
        using (MerrickContext db = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            User user = CreateTestUser(50, "store@k.net");
            Account account = new()
            {
                ID = 500,
                Name = "StoreUser",
                User = user
            };
            db.Users.Add(user);
            db.Accounts.Add(account);
            db.UserInventory.AddRange(
                new UserInventory { ID = 1, UserID = 50, AccountID = 500, ItemCode = "announcer.flamboyant" },
                new UserInventory { ID = 2, UserID = 50, AccountID = 500, ItemCode = "aa.Hero_Valkyrie.Alt" },
                new UserInventory { ID = 3, UserID = 50, AccountID = 500, ItemCode = "taunt.kongor" }
            );
            await db.SaveChangesAsync();
        }

        using (MerrickContext db = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            StoreRepository repo = new(db);
            IReadOnlyList<string> codes = await repo.GetOwnedItemCodesAsync("StoreUser");
            codes.Should().HaveCount(3);
            codes.Should().Contain("aa.Hero_Valkyrie.Alt");
            codes.Should().Contain("taunt.kongor");
            codes.Should().Contain("announcer.flamboyant");

            IReadOnlyList<string> codesById = await repo.GetOwnedItemCodesByAccountIdAsync(500);
            codesById.Should().HaveCount(3);
        }
    }

    [Trait("Category", "Manual")]
    [Fact]
    public async Task StoreRepository_HasItemAsync_ChecksUserInventoryDirectly()
    {
        if (!NexusTestEnvironment.IsManualTestEnabled) return;
        string dbName = Guid.NewGuid().ToString("N");
        using (MerrickContext db = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            User user = CreateTestUser(60, "store2@k.net");
            Account account = new()
            {
                ID = 600,
                Name = "ChronosUser",
                User = user
            };
            db.Users.Add(user);
            db.Accounts.Add(account);
            db.UserInventory.AddRange(
                new UserInventory { ID = 4, UserID = 60, AccountID = 600, ItemCode = "Hero_Chronos.Alt" },
                new UserInventory { ID = 5, UserID = 60, AccountID = 600, ItemCode = "Hero_Magmus.Alt" }
            );
            await db.SaveChangesAsync();
        }

        using (MerrickContext db = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            StoreRepository repo = new(db);
            bool hasChronos = await repo.HasItemAsync(600, "Hero_Chronos.Alt");
            bool hasMagmus = await repo.HasItemAsync(600, "Hero_Magmus.Alt");
            bool hasDevo = await repo.HasItemAsync(600, "Hero_Devourer.Alt");

            hasChronos.Should().BeTrue();
            hasMagmus.Should().BeTrue();
            hasDevo.Should().BeFalse();
        }
    }
}
