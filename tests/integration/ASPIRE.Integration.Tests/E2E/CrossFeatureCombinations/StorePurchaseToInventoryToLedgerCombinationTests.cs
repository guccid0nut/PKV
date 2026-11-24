// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ============================================================================
// File: StorePurchaseToInventoryToLedgerCombinationTests.cs
// System: ProjectKongor Solution-Wide E2E Testing Suite
// Tier 3: Cross-Feature Combinations (Store Purchase -> Inventory -> Economy Ledger)
// Policy: Strict Explicit Typing, Multi-Service Integration Verification
// ============================================================================

using FluentAssertions;
using MERRICK.DatabaseContext.Entities.Store;
using MERRICK.DatabaseContext.Services;

namespace ASPIRE.Tests.E2E.Tier3_CrossFeatureCombinations;

public sealed class StorePurchaseToInventoryToLedgerCombinationTests
{
    private static MerrickContext CreateInMemoryContext()
    {
        return TestDbContextFactory.CreateInMemoryContext("Purchase_Ledger_" + Guid.NewGuid().ToString("N"));
    }

    [Fact]
    public async Task Combination_GoldStorePurchase_UpdatesBalanceAndGrantsInventoryAtomically()
    {
        // Arrange
        using MerrickContext db = CreateInMemoryContext();
        Role userRole = new() { ID = 1, Name = "User" };
        db.Roles.Add(userRole);

        User user = new()
        {
            EmailAddress = $"buyer_gold_{Guid.NewGuid():N}@kongor.test",
            PBKDF2PasswordHash = "h",
            SRPPasswordHash = "h",
            SRPPasswordSalt = "s",
            Role = userRole,
            GoldCoins = 1000,
            SilverCoins = 2000,
            InventoryItems = []
        };
        Account account = new()
        {
            Name = "GoldBuyer",
            User = user,
            Type = AccountType.Normal,
            IsMain = true
        };
        StoreItem avatar = new()
        {
            Name = "Swiftblade Ronin",
            Code = "Hero_Hiro.Alt3",
            LocalContent = "hero/hiro/alt3",
            Price = 600,
            PriceInMMP = 3000,
            Purchasable = true,
            Enabled = true
        };

        db.Users.Add(user);
        db.Accounts.Add(account);
        db.StoreItems.Add(avatar);
        await db.SaveChangesAsync();

        InventoryRepository repo = new(db);

        // Act - Simulate Store purchase pipeline
        bool deducted = await repo.DeductCurrenciesAsync(account.ID, avatar.Price, 0, 0);
        deducted.Should().BeTrue();

        await repo.GrantItemAsync(account.ID, avatar.Code);

        // Assert - Currency balance decreased and item added to inventory
        User? refreshedUser = await db.Users.FindAsync(user.ID);
        refreshedUser.Should().NotBeNull();
        refreshedUser!.GoldCoins.Should().Be(400); // 1000 - 600

        bool hasItem = await repo.HasItemAsync(account.ID, "Hero_Hiro.Alt3");
        hasItem.Should().BeTrue();

        // Verify normalized UserInventory record exists and links StoreItemId
        UserInventory? invRecord =
            await db.UserInventory.FirstOrDefaultAsync(i => i.UserID == user.ID && i.ItemCode == avatar.Code);
        invRecord.Should().NotBeNull();
        invRecord!.Quantity.Should().Be(1);
        invRecord.StoreItemId.Should().Be(avatar.Id);

        // Verify audit ledger entry
        EconomyTransaction? ledgerEntry =
            await db.EconomyTransactions.FirstOrDefaultAsync(t =>
                t.UserID == user.ID && t.TransactionType == "InventoryDeduct");
        ledgerEntry.Should().NotBeNull();
        ledgerEntry!.GoldCoinsDelta.Should().Be(-600);
        ledgerEntry.GoldCoinsBalanceAfter.Should().Be(400);
    }

    [Fact]
    public async Task Combination_SilverStorePurchase_UpdatesSilverBalanceAndGrantsInventory()
    {
        // Arrange
        using MerrickContext db = CreateInMemoryContext();
        Role userRole = new() { ID = 1, Name = "User" };
        db.Roles.Add(userRole);

        User user = new()
        {
            EmailAddress = $"buyer_silver_{Guid.NewGuid():N}@kongor.test",
            PBKDF2PasswordHash = "h",
            SRPPasswordHash = "h",
            SRPPasswordSalt = "s",
            Role = userRole,
            GoldCoins = 500,
            SilverCoins = 4000,
            InventoryItems = []
        };
        Account account = new()
        {
            Name = "SilverBuyer",
            User = user,
            Type = AccountType.Normal,
            IsMain = true
        };
        StoreItem taunt = new()
        {
            Name = "Baby Taunt",
            Code = "Taunt.Baby",
            LocalContent = "taunts/baby",
            Price = 300,
            PriceInMMP = 2500,
            Purchasable = true,
            Enabled = true
        };

        db.Users.Add(user);
        db.Accounts.Add(account);
        db.StoreItems.Add(taunt);
        await db.SaveChangesAsync();

        InventoryRepository repo = new(db);

        // Act - Purchase with Silver
        bool deducted = await repo.DeductCurrenciesAsync(account.ID, 0, taunt.PriceInMMP, 0);
        deducted.Should().BeTrue();

        await repo.GrantItemAsync(account.ID, taunt.Code);

        // Assert
        User? refreshedUser = await db.Users.FindAsync(user.ID);
        refreshedUser.Should().NotBeNull();
        refreshedUser!.SilverCoins.Should().Be(1500); // 4000 - 2500

        bool hasTaunt = await repo.HasItemAsync(account.ID, "Taunt.Baby");
        hasTaunt.Should().BeTrue();

        // Verify normalized UserInventory record exists and links StoreItemId
        UserInventory? invRecord =
            await db.UserInventory.FirstOrDefaultAsync(i => i.UserID == user.ID && i.ItemCode == taunt.Code);
        invRecord.Should().NotBeNull();
        invRecord!.Quantity.Should().Be(1);
        invRecord.StoreItemId.Should().Be(taunt.Id);

        // Verify audit ledger entry
        EconomyTransaction? ledgerEntry =
            await db.EconomyTransactions.FirstOrDefaultAsync(t =>
                t.UserID == user.ID && t.TransactionType == "InventoryDeduct");
        ledgerEntry.Should().NotBeNull();
        ledgerEntry!.SilverCoinsDelta.Should().Be(-2500);
        ledgerEntry.SilverCoinsBalanceAfter.Should().Be(1500);
    }

    [Fact]
    public async Task Combination_InsufficientFunds_RejectsPurchaseAndLeavesLedgerUntouched()
    {
        // Arrange
        using MerrickContext db = CreateInMemoryContext();
        Role userRole = new() { ID = 1, Name = "User" };
        db.Roles.Add(userRole);

        User user = new()
        {
            EmailAddress = $"poor_user_{Guid.NewGuid():N}@kongor.test",
            PBKDF2PasswordHash = "h",
            SRPPasswordHash = "h",
            SRPPasswordSalt = "s",
            Role = userRole,
            GoldCoins = 100, // item costs 500
            SilverCoins = 500,
            InventoryItems = []
        };
        Account account = new()
        {
            Name = "PoorPlayer",
            User = user,
            Type = AccountType.Normal,
            IsMain = true
        };
        StoreItem premiumItem = new()
        {
            Name = "Ultimate Announcer",
            Code = "Announcer.Ultimate",
            LocalContent = "announcers/ultimate",
            Price = 500,
            PriceInMMP = 5000,
            Purchasable = true,
            Enabled = true
        };

        db.Users.Add(user);
        db.Accounts.Add(account);
        db.StoreItems.Add(premiumItem);
        await db.SaveChangesAsync();

        InventoryRepository repo = new(db);

        // Act
        bool deducted = await repo.DeductCurrenciesAsync(account.ID, premiumItem.Price, 0, 0);

        // Assert
        deducted.Should().BeFalse();

        bool hasItem = await repo.HasItemAsync(account.ID, premiumItem.Code);
        hasItem.Should().BeFalse();

        User? refreshedUser = await db.Users.FindAsync(user.ID);
        refreshedUser!.GoldCoins.Should().Be(100);

        List<EconomyTransaction> transactions = await db.EconomyTransactions
            .Where(t => t.UserID == user.ID)
            .ToListAsync();
        transactions.Should().BeEmpty();
    }

    [Fact]
    public async Task Combination_AddCurrencies_UpdatesBalanceAndInsertsLedgerAudit()
    {
        // Arrange
        using MerrickContext db = CreateInMemoryContext();
        Role userRole = new() { ID = 1, Name = "User" };
        db.Roles.Add(userRole);

        User user = new()
        {
            EmailAddress = $"reward_user_{Guid.NewGuid():N}@kongor.test",
            PBKDF2PasswordHash = "h",
            SRPPasswordHash = "h",
            SRPPasswordSalt = "s",
            Role = userRole,
            GoldCoins = 50,
            SilverCoins = 100,
            PlinkoTickets = 5,
            InventoryItems = []
        };
        Account account = new()
        {
            Name = "RewardPlayer",
            User = user,
            Type = AccountType.Normal,
            IsMain = true
        };

        db.Users.Add(user);
        db.Accounts.Add(account);
        await db.SaveChangesAsync();

        InventoryRepository repo = new(db);

        // Act
        await repo.AddCurrenciesAsync(account.ID, 100, 500, 10);

        // Assert
        User? refreshed = await db.Users.FindAsync(user.ID);
        refreshed!.GoldCoins.Should().Be(150);
        refreshed.SilverCoins.Should().Be(600);
        refreshed.PlinkoTickets.Should().Be(15);

        EconomyTransaction? ledger =
            await db.EconomyTransactions.FirstOrDefaultAsync(t =>
                t.UserID == user.ID && t.TransactionType == "InventoryAdd");
        ledger.Should().NotBeNull();
        ledger!.GoldCoinsDelta.Should().Be(100);
        ledger.SilverCoinsDelta.Should().Be(500);
        ledger.PlinkoTicketsDelta.Should().Be(10);
        ledger.GoldCoinsBalanceAfter.Should().Be(150);
        ledger.SilverCoinsBalanceAfter.Should().Be(600);
        ledger.PlinkoTicketsBalanceAfter.Should().Be(15);
    }

    [Fact]
    public async Task Combination_GetOwnedItems_ReturnsNormalizedAndLegacyItemsCombined()
    {
        // Arrange
        using MerrickContext db = CreateInMemoryContext();
        Role userRole = new() { ID = 1, Name = "User" };
        db.Roles.Add(userRole);

        User user = new()
        {
            EmailAddress = $"hybrid_user_{Guid.NewGuid():N}@kongor.test",
            PBKDF2PasswordHash = "h",
            SRPPasswordHash = "h",
            SRPPasswordSalt = "s",
            Role = userRole,
            InventoryItems =
            [
                new UserInventory { ItemCode = "legacy_taunt", GrantSource = "Test" },
                new UserInventory { ItemCode = "legacy_avatar", GrantSource = "Test" }
            ]
        };
        Account account = new()
        {
            Name = "HybridPlayer",
            User = user,
            Type = AccountType.Normal,
            IsMain = true
        };

        db.Users.Add(user);
        db.Accounts.Add(account);
        await db.SaveChangesAsync();

        InventoryRepository repo = new(db);
        await repo.GrantItemAsync(account.ID, "new_normalized_item");

        // Act
        IReadOnlyList<string> items = await repo.GetOwnedItemCodesAsync(account.ID);

        // Assert
        items.Should().Contain("legacy_taunt");
        items.Should().Contain("legacy_avatar");
        items.Should().Contain("new_normalized_item");
    }
}
