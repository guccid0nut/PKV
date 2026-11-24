// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using FluentAssertions;
using MERRICK.DatabaseContext.Entities.Core;
using MERRICK.DatabaseContext.Entities.Store;
using MERRICK.DatabaseContext.Entities.Utility;
using MERRICK.DatabaseContext.Persistence;
using MERRICK.DatabaseContext.Repositories;
using MERRICK.DatabaseContext.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace MERRICK.DatabaseContext.Tests;

public sealed class InventoryRepositoryAtomicCurrencyTests
{
    private static User CreateUser(int id, string email, int gold = 1000, int silver = 2000, int plinko = 10) => new()
    {
        ID = id,
        EmailAddress = email,
        PBKDF2PasswordHash = "pbkdf2_hash",
        SRPPasswordHash = "srp_hash",
        SRPPasswordSalt = "srp_salt",
        GoldCoins = gold,
        SilverCoins = silver,
        PlinkoTickets = plinko,
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
    public async Task DeductCurrenciesAsync_SufficientBalance_DeductsAndLogsTransaction()
    {
        using MerrickContext db = TestDbContextFactory.CreateInMemoryContext();
        User user = CreateUser(1, "user1@kongor.net", gold: 500, silver: 1000, plinko: 10);
        Account account = CreateAccount(100, 1, user, "CurrencyPlayer1");
        db.Users.Add(user);
        db.Accounts.Add(account);
        await db.SaveChangesAsync();

        InventoryRepository repo = new(db);

        bool result = await repo.DeductCurrenciesAsync(account.ID, goldDelta: 200, silverDelta: 300, plinkoDelta: 2);

        result.Should().BeTrue();
        User? refreshed = await db.Users.FindAsync(user.ID);
        refreshed.Should().NotBeNull();
        refreshed!.GoldCoins.Should().Be(300);
        refreshed.SilverCoins.Should().Be(700);
        refreshed.PlinkoTickets.Should().Be(8);

        EconomyTransaction? tx = await db.EconomyTransactions.FirstOrDefaultAsync(t => t.TransactionType == "InventoryDeduct");
        tx.Should().NotBeNull();
        tx!.GoldCoinsDelta.Should().Be(-200);
        tx.SilverCoinsDelta.Should().Be(-300);
        tx.PlinkoTicketsDelta.Should().Be(-2);
        tx.GoldCoinsBalanceAfter.Should().Be(300);
    }

    [Fact]
    public async Task DeductCurrenciesAsync_InsufficientBalance_ReturnsFalseAndLeavesBalancesUntouched()
    {
        using MerrickContext db = TestDbContextFactory.CreateInMemoryContext();
        User user = CreateUser(2, "user2@kongor.net", gold: 100, silver: 500, plinko: 1);
        Account account = CreateAccount(200, 2, user, "PoorPlayer");
        db.Users.Add(user);
        db.Accounts.Add(account);
        await db.SaveChangesAsync();

        InventoryRepository repo = new(db);

        bool result = await repo.DeductCurrenciesAsync(account.ID, goldDelta: 200, silverDelta: 100, plinkoDelta: 0);

        result.Should().BeFalse();
        User? refreshed = await db.Users.FindAsync(user.ID);
        refreshed.Should().NotBeNull();
        refreshed!.GoldCoins.Should().Be(100);
        refreshed.SilverCoins.Should().Be(500);

        bool hasTx = await db.EconomyTransactions.AnyAsync(t => t.AccountID == account.ID);
        hasTx.Should().BeFalse();
    }

    [Fact]
    public async Task InventoryRepositoryAtomicCurrency_AddCurrenciesAsync_IncrementsBalancesAndLogsTransaction()
    {
        using MerrickContext db = TestDbContextFactory.CreateInMemoryContext();
        User user = CreateUser(3, "user3@kongor.net", gold: 100, silver: 200, plinko: 5);
        Account account = CreateAccount(300, 3, user, "RewardPlayer");
        db.Users.Add(user);
        db.Accounts.Add(account);
        await db.SaveChangesAsync();

        InventoryRepository repo = new(db);

        await repo.AddCurrenciesAsync(account.ID, goldDelta: 500, silverDelta: 1000, plinkoDelta: 15);

        User? refreshed = await db.Users.FindAsync(user.ID);
        refreshed.Should().NotBeNull();
        refreshed!.GoldCoins.Should().Be(600);
        refreshed.SilverCoins.Should().Be(1200);
        refreshed.PlinkoTickets.Should().Be(20);

        EconomyTransaction? tx = await db.EconomyTransactions.FirstOrDefaultAsync(t => t.TransactionType == "InventoryAdd");
        tx.Should().NotBeNull();
        tx!.GoldCoinsDelta.Should().Be(500);
        tx.SilverCoinsDelta.Should().Be(1000);
        tx.PlinkoTicketsDelta.Should().Be(15);
    }

    [Fact]
    public async Task PurchaseStoreItemAsync_SufficientGold_DeductsPriceAndGrantsInventory()
    {
        using MerrickContext db = TestDbContextFactory.CreateInMemoryContext();
        User user = CreateUser(4, "user4@kongor.net", gold: 1000, silver: 0, plinko: 0);
        Account account = CreateAccount(400, 4, user, "BuyerPlayer");
        StoreItem item = new()
        {
            Id = 55,
            Name = "Pyromancer Avatar",
            Code = "Hero_Pyromancer.Alt1",
            LocalContent = "item_pyro_alt1",
            Price = 400,
            PriceInMMP = 2000,
            Enabled = true,
            Purchasable = true
        };
        db.Users.Add(user);
        db.Accounts.Add(account);
        db.StoreItems.Add(item);
        await db.SaveChangesAsync();

        InventoryRepository repo = new(db);

        var result = await repo.PurchaseStoreItemAsync(user.ID, item.Id, "gold");

        result.Success.Should().BeTrue();
        result.NewGold.Should().Be(600);

        User? refreshed = await db.Users.FindAsync(user.ID);
        refreshed!.GoldCoins.Should().Be(600);

        UserInventory? inv = await db.UserInventory.FirstOrDefaultAsync(i => i.UserID == user.ID && i.ItemCode == item.Code);
        inv.Should().NotBeNull();
        inv!.Quantity.Should().Be(1);
    }

    [Fact]
    public async Task PurchaseStoreItemAsync_InsufficientCurrency_FailsSafely()
    {
        using MerrickContext db = TestDbContextFactory.CreateInMemoryContext();
        User user = CreateUser(5, "user5@kongor.net", gold: 100, silver: 50, plinko: 0);
        Account account = CreateAccount(500, 5, user, "BrokePlayer");
        StoreItem item = new()
        {
            Id = 56,
            Name = "Expensive Skin",
            Code = "Hero_Devourer.Alt5",
            LocalContent = "item_devo_alt5",
            Price = 500,
            PriceInMMP = 3000,
            Enabled = true,
            Purchasable = true
        };
        db.Users.Add(user);
        db.Accounts.Add(account);
        db.StoreItems.Add(item);
        await db.SaveChangesAsync();

        InventoryRepository repo = new(db);

        var result = await repo.PurchaseStoreItemAsync(user.ID, item.Id, "gold");

        result.Success.Should().BeFalse();
        result.Message.Should().Contain("Insufficient Gold Coins");

        User? refreshed = await db.Users.FindAsync(user.ID);
        refreshed!.GoldCoins.Should().Be(100);

        bool hasInv = await db.UserInventory.AnyAsync(i => i.UserID == user.ID && i.ItemCode == item.Code);
        hasInv.Should().BeFalse();
    }

    [Fact]
    public async Task PurchaseStoreItemAsync_DuplicateIdempotencyKey_DoesNotDoubleChargeOrUseDescriptionStringTag()
    {
        // protects: Web Store purchase idempotency via dedicated EconomyTransaction.IdempotencyKey without [ref:guid] Description string-matching
        // fails_when: Duplicate IdempotencyKey charges twice, increments Quantity to 2, or writes [ref:...] into Description
        // seam: InventoryRepository.PurchaseStoreItemAsync + MerrickContext
        using MerrickContext db = TestDbContextFactory.CreateInMemoryContext();
        User user = CreateUser(14, "user14@kongor.net", gold: 1000, silver: 0, plinko: 0);
        Account account = CreateAccount(1400, 14, user, "IdempotentBuyer");
        StoreItem item = new()
        {
            Id = 155,
            Name = "Consumable Mastery Boost",
            Code = "Consumable_MasteryBoost.1",
            LocalContent = "item_boost_1",
            Price = 400,
            PriceInMMP = 0,
            Enabled = true,
            Purchasable = true
        };
        db.Users.Add(user);
        db.Accounts.Add(account);
        db.StoreItems.Add(item);
        await db.SaveChangesAsync();

        InventoryRepository repo = new(db);

        var first = await repo.PurchaseStoreItemAsync(user.ID, item.Id, "gold", "client-idem-key-001");
        var duplicate = await repo.PurchaseStoreItemAsync(user.ID, item.Id, "gold", "client-idem-key-001");

        first.Success.Should().BeTrue();
        duplicate.Success.Should().BeTrue();
        duplicate.NewGold.Should().Be(600, "Duplicate idempotency key must not deduct 400G a second time");

        UserInventory inv = await db.UserInventory.SingleAsync(i => i.UserID == user.ID && i.ItemCode == item.Code);
        inv.Quantity.Should().Be(1, "Duplicate idempotency key must not increment consumable Quantity to 2");

        EconomyTransaction tx = await db.EconomyTransactions.SingleAsync(t => t.UserID == user.ID);
        tx.IdempotencyKey.Should().Be("web-store:14:155:client-idem-key-001");
        tx.ReferenceId.Should().Be(item.Code);
        tx.Description.Should().NotContain("[ref:", "Store purchase must use EconomyTransaction.IdempotencyKey column instead of Description tag");

        var secondDistinct = await repo.PurchaseStoreItemAsync(user.ID, item.Id, "gold", "client-idem-key-002");
        secondDistinct.Success.Should().BeTrue();
        secondDistinct.NewGold.Should().Be(200);
        (await db.UserInventory.SingleAsync(i => i.UserID == user.ID && i.ItemCode == item.Code)).Quantity.Should().Be(2);
    }

    [Fact]
    public void DatabaseInitializer_EconomyTransactionsDdl_DefinesIdempotencyKeyColumnAndPartialUniqueIndex()
    {
        // protects: PostgreSQL schema bootstrap & live upgrade for EconomyTransactions.IdempotencyKey unique index
        // fails_when: DatabaseInitializer omits IdempotencyKey column migration or partial unique index IX_EconomyTransactions_UserID_IdempotencyKey
        // seam: Services/DatabaseInitializer.cs DDL source contract
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Directory.Packages.props")))
        {
            dir = dir.Parent;
        }
        string repoRoot = dir?.FullName ?? throw new DirectoryNotFoundException("Repository root not found.");
        string ddlPath = Path.Combine(repoRoot, "src", "libs", "database-context", "Services", "DatabaseInitializer.cs");
        string ddl = File.ReadAllText(ddlPath);

        ddl.Should().Contain("ALTER TABLE \"\"core\"\".\"\"EconomyTransactions\"\" ADD COLUMN IF NOT EXISTS \"\"IdempotencyKey\"\" character varying(128);");
        ddl.Should().Contain("CREATE UNIQUE INDEX IF NOT EXISTS \"\"IX_EconomyTransactions_UserID_IdempotencyKey\"\" ON \"\"core\"\".\"\"EconomyTransactions\"\" (\"\"UserID\"\", \"\"IdempotencyKey\"\") WHERE \"\"IdempotencyKey\"\" IS NOT NULL;");
    }
}
