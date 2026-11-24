// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ============================================================================
// File: EconomyAndInventoryBoundaryTests.cs
// System: ProjectKongor Solution-Wide E2E Testing Suite
// Tier 2: Boundary & Corner Cases (Economy Ledger & Inventory Boundaries)
// Policy: Strict Explicit Typing, Adversarial Boundary Verification (>= 5 tests)
// ============================================================================

using FluentAssertions;
using MERRICK.DatabaseContext.Entities.Store;

namespace ASPIRE.Tests.E2E.Tier2_BoundaryAndCornerCases;

public sealed class EconomyAndInventoryBoundaryTests
{
    private static MerrickContext CreateInMemoryContext()
    {
        return TestDbContextFactory.CreateInMemoryContext("Eco_Boundary_" + Guid.NewGuid().ToString("N"));
    }

    [Fact]
    public async Task Boundary_InsufficientGoldBalance_FailsAtomicallyWithoutModifyingBalances()
    {
        // Arrange - User has 499 Gold, needs 500 Gold
        using MerrickContext db = CreateInMemoryContext();
        Role userRole = new() { ID = 1, Name = "User" };
        db.Roles.Add(userRole);

        User user = new()
        {
            EmailAddress = $"boundary_gold_{Guid.NewGuid():N}@kongor.test",
            PBKDF2PasswordHash = "h",
            SRPPasswordHash = "h",
            SRPPasswordSalt = "s",
            Role = userRole,
            GoldCoins = 499,
            SilverCoins = 1000,
            PlinkoTickets = 5
        };
        Account account = new()
        {
            Name = "GoldBoundaryPlayer",
            User = user,
            Type = AccountType.Normal,
            IsMain = true
        };
        db.Users.Add(user);
        db.Accounts.Add(account);
        await db.SaveChangesAsync();

        InventoryRepository repo = new(db);

        // Act - Attempt to deduct 500 Gold (1 coin short)
        bool result = await repo.DeductCurrenciesAsync(account.ID, 500, 0, 0);

        // Assert
        result.Should().BeFalse();
        User? refreshed = await db.Users.FindAsync(user.ID);
        refreshed.Should().NotBeNull();
        refreshed!.GoldCoins.Should().Be(499);
        refreshed.SilverCoins.Should().Be(1000);
        refreshed.PlinkoTickets.Should().Be(5);
    }

    [Fact]
    public async Task Boundary_ExactGoldBalance_DeductsSuccessfullyToZero()
    {
        // Arrange - User has exactly 500 Gold, needs 500 Gold
        using MerrickContext db = CreateInMemoryContext();
        Role userRole = new() { ID = 1, Name = "User" };
        db.Roles.Add(userRole);

        User user = new()
        {
            EmailAddress = $"boundary_exact_{Guid.NewGuid():N}@kongor.test",
            PBKDF2PasswordHash = "h",
            SRPPasswordHash = "h",
            SRPPasswordSalt = "s",
            Role = userRole,
            GoldCoins = 500,
            SilverCoins = 0,
            PlinkoTickets = 0
        };
        Account account = new()
        {
            Name = "ExactGoldPlayer",
            User = user,
            Type = AccountType.Normal,
            IsMain = true
        };
        db.Users.Add(user);
        db.Accounts.Add(account);
        await db.SaveChangesAsync();

        InventoryRepository repo = new(db);

        // Act
        bool result = await repo.DeductCurrenciesAsync(account.ID, 500, 0, 0);

        // Assert
        result.Should().BeTrue();
        User? refreshed = await db.Users.FindAsync(user.ID);
        refreshed.Should().NotBeNull();
        refreshed!.GoldCoins.Should().Be(0);
    }

    [Fact]
    public async Task Boundary_DuplicateItemGranting_MaintainsIdempotentSingleOwnership()
    {
        // Arrange
        using MerrickContext db = CreateInMemoryContext();
        Role userRole = new() { ID = 1, Name = "User" };
        db.Roles.Add(userRole);

        User user = new()
        {
            EmailAddress = $"duplicate_grant_{Guid.NewGuid():N}@kongor.test",
            PBKDF2PasswordHash = "h",
            SRPPasswordHash = "h",
            SRPPasswordSalt = "s",
            Role = userRole
        };
        Account account = new()
        {
            Name = "DuplicateGrantPlayer",
            User = user,
            Type = AccountType.Normal,
            IsMain = true
        };
        db.Users.Add(user);
        db.Accounts.Add(account);
        await db.SaveChangesAsync();

        InventoryRepository repo = new(db);

        // Act - Grant the same item code 3 times
        await repo.GrantItemAsync(account.ID, "Hero_Valkyrie.Alt4");
        await repo.GrantItemAsync(account.ID, "Hero_Valkyrie.Alt4");
        await repo.GrantItemAsync(account.ID, "hero_valkyrie.alt4"); // case-insensitive duplicate

        // Assert
        IReadOnlyList<string> owned = await repo.GetOwnedItemCodesAsync(account.ID);
        owned.Should().HaveCount(1);
        owned.Should().Contain("Hero_Valkyrie.Alt4");
    }

    [Fact]
    public async Task Boundary_ZeroDeltaDeductions_SucceedsWithoutAlteringBalances()
    {
        // Arrange
        using MerrickContext db = CreateInMemoryContext();
        Role userRole = new() { ID = 1, Name = "User" };
        db.Roles.Add(userRole);

        User user = new()
        {
            EmailAddress = $"zero_delta_{Guid.NewGuid():N}@kongor.test",
            PBKDF2PasswordHash = "h",
            SRPPasswordHash = "h",
            SRPPasswordSalt = "s",
            Role = userRole,
            GoldCoins = 100,
            SilverCoins = 200,
            PlinkoTickets = 3
        };
        Account account = new()
        {
            Name = "ZeroDeltaPlayer",
            User = user,
            Type = AccountType.Normal,
            IsMain = true
        };
        db.Users.Add(user);
        db.Accounts.Add(account);
        await db.SaveChangesAsync();

        InventoryRepository repo = new(db);

        // Act - Deduct zero across all currencies
        bool result = await repo.DeductCurrenciesAsync(account.ID, 0, 0, 0);

        // Assert
        result.Should().BeTrue();
        User? refreshed = await db.Users.FindAsync(user.ID);
        refreshed.Should().NotBeNull();
        refreshed!.GoldCoins.Should().Be(100);
        refreshed.SilverCoins.Should().Be(200);
        refreshed.PlinkoTickets.Should().Be(3);
    }

    [Fact]
    public async Task Boundary_NonExistentAccountDeduction_ReturnsFalseSafely()
    {
        // Arrange
        using MerrickContext db = CreateInMemoryContext();
        InventoryRepository repo = new(db);

        // Act
        bool result = await repo.DeductCurrenciesAsync(999999, 10, 10, 1);

        // Assert
        result.Should().BeFalse();
    }

    [Trait("Category", "Manual")]
    [Fact]
    public async Task Boundary_HasItemOnAccountWithoutItems_ReturnsFalseSafely()
    {
        if (!NexusTestEnvironment.IsManualTestEnabled) return;
        // Arrange
        using MerrickContext db = CreateInMemoryContext();
        Role userRole = new() { ID = 1, Name = "User" };
        db.Roles.Add(userRole);

        User user = new()
        {
            EmailAddress = $"no_items_{Guid.NewGuid():N}@kongor.test",
            PBKDF2PasswordHash = "h",
            SRPPasswordHash = "h",
            SRPPasswordSalt = "s",
            Role = userRole,
            InventoryItems = []
        };
        Account account = new()
        {
            Name = "NoItemsPlayer",
            User = user,
            Type = AccountType.Normal,
            IsMain = true
        };
        db.Users.Add(user);
        db.Accounts.Add(account);
        await db.SaveChangesAsync();

        InventoryRepository repo = new(db);

        // Act & Assert
        bool hasItem = await repo.HasItemAsync(account.ID, "Hero_Valkyrie.Alt4");
        hasItem.Should().BeFalse();
    }
}
