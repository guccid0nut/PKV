// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ============================================================================
// File: PlinkoDropToBalanceToLedgerCombinationTests.cs
// System: ProjectKongor Solution-Wide E2E Testing Suite
// Tier 3: Cross-Feature Combinations (Plinko Drop -> Balance -> Economy Ledger)
// Policy: Strict Explicit Typing, Multi-Service Integration Verification
// ============================================================================

using FluentAssertions;

namespace ASPIRE.Tests.E2E.Tier3_CrossFeatureCombinations;

public sealed class PlinkoDropToBalanceToLedgerCombinationTests
{
    private static MerrickContext CreateInMemoryContext()
    {
        return TestDbContextFactory.CreateInMemoryContext("Plinko_Ledger_" + Guid.NewGuid().ToString("N"));
    }

    [Trait("Category", "Manual")]
    [Fact]
    public async Task Combination_PlinkoDropWithGold_DeductsFeeAndCreditsRewardCorrectly()
    {
        if (!NexusTestEnvironment.IsManualTestEnabled) return;
        // Arrange
        using MerrickContext db = CreateInMemoryContext();
        Role userRole = new() { ID = 1, Name = "User" };
        db.Roles.Add(userRole);

        User user = new()
        {
            EmailAddress = $"plinko_gold_{Guid.NewGuid():N}@kongor.test",
            PBKDF2PasswordHash = "h",
            SRPPasswordHash = "h",
            SRPPasswordSalt = "s",
            Role = userRole,
            GoldCoins = 500,
            SilverCoins = 1000,
            PlinkoTickets = 0
        };
        Account account = new()
        {
            Name = "PlinkoGoldPlayer",
            User = user,
            Type = AccountType.Normal,
            IsMain = true
        };
        db.Users.Add(user);
        db.Accounts.Add(account);
        await db.SaveChangesAsync();

        InventoryRepository repo = new(db);

        // Act - Simulate Plinko entry fee (50 Gold)
        bool feeDeducted = await repo.DeductCurrenciesAsync(account.ID, 50, 0, 0);
        feeDeducted.Should().BeTrue();

        // Simulate landing on Silver payout slot (500 Silver)
        await repo.AddCurrenciesAsync(account.ID, 0, 500, 0);

        // Assert - Gold decreased by 50, Silver increased by 500
        User? refreshedUser = await db.Users.FindAsync(user.ID);
        refreshedUser.Should().NotBeNull();
        refreshedUser!.GoldCoins.Should().Be(450);
        refreshedUser.SilverCoins.Should().Be(1500);
    }

    [Trait("Category", "Manual")]
    [Fact]
    public async Task Combination_PlinkoDropWithTickets_DeductsFeeAndCreditsJackpotGold()
    {
        if (!NexusTestEnvironment.IsManualTestEnabled) return;
        // Arrange
        using MerrickContext db = CreateInMemoryContext();
        Role userRole = new() { ID = 1, Name = "User" };
        db.Roles.Add(userRole);

        User user = new()
        {
            EmailAddress = $"plinko_jackpot_{Guid.NewGuid():N}@kongor.test",
            PBKDF2PasswordHash = "h",
            SRPPasswordHash = "h",
            SRPPasswordSalt = "s",
            Role = userRole,
            GoldCoins = 100,
            SilverCoins = 500,
            PlinkoTickets = 250
        };
        Account account = new()
        {
            Name = "PlinkoJackpotPlayer",
            User = user,
            Type = AccountType.Normal,
            IsMain = true
        };
        db.Users.Add(user);
        db.Accounts.Add(account);
        await db.SaveChangesAsync();

        InventoryRepository repo = new(db);

        // Act - Deduct 100 Plinko tickets entry fee
        bool feeDeducted = await repo.DeductCurrenciesAsync(account.ID, 0, 0, 100);
        feeDeducted.Should().BeTrue();

        // Simulate landing on Jackpot Slot (150 Gold reward)
        await repo.AddCurrenciesAsync(account.ID, 150, 0, 0);

        // Assert
        User? refreshedUser = await db.Users.FindAsync(user.ID);
        refreshedUser.Should().NotBeNull();
        refreshedUser!.PlinkoTickets.Should().Be(150); // 250 - 100
        refreshedUser.GoldCoins.Should().Be(250); // 100 + 150
    }

    [Fact]
    public async Task Combination_PlinkoDrop_InsufficientTickets_RejectsEntryFee()
    {
        // Arrange
        using MerrickContext db = CreateInMemoryContext();
        Role userRole = new() { ID = 1, Name = "User" };
        db.Roles.Add(userRole);

        User user = new()
        {
            EmailAddress = $"plinko_broke_{Guid.NewGuid():N}@kongor.test",
            PBKDF2PasswordHash = "h",
            SRPPasswordHash = "h",
            SRPPasswordSalt = "s",
            Role = userRole,
            GoldCoins = 0,
            SilverCoins = 0,
            PlinkoTickets = 50 // needs 100
        };
        Account account = new()
        {
            Name = "PlinkoBrokePlayer",
            User = user,
            Type = AccountType.Normal,
            IsMain = true
        };
        db.Users.Add(user);
        db.Accounts.Add(account);
        await db.SaveChangesAsync();

        InventoryRepository repo = new(db);

        // Act
        bool feeDeducted = await repo.DeductCurrenciesAsync(account.ID, 0, 0, 100);

        // Assert
        feeDeducted.Should().BeFalse();
        User? refreshedUser = await db.Users.FindAsync(user.ID);
        refreshedUser!.PlinkoTickets.Should().Be(50);
    }
}
