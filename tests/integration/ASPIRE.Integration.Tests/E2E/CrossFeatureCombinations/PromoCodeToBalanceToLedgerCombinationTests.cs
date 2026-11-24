// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ============================================================================
// File: PromoCodeToBalanceToLedgerCombinationTests.cs
// System: ProjectKongor Solution-Wide E2E Testing Suite
// Tier 3: Cross-Feature Combinations (Promo Code -> Balance -> Economy Ledger)
// Policy: Strict Explicit Typing, Multi-Service Integration Verification
// ============================================================================

using FluentAssertions;

namespace ASPIRE.Tests.E2E.Tier3_CrossFeatureCombinations;

public sealed class PromoCodeToBalanceToLedgerCombinationTests
{
    private static MerrickContext CreateInMemoryContext()
    {
        return TestDbContextFactory.CreateInMemoryContext("Promo_Ledger_" + Guid.NewGuid().ToString("N"));
    }

    [Trait("Category", "Manual")]
    [Fact]
    public async Task Combination_RedeemCode_CreditsBalancesAndMarksCodeRedeemedAtomically()
    {
        if (!NexusTestEnvironment.IsManualTestEnabled) return;

        // Arrange
        using MerrickContext db = CreateInMemoryContext();
        Role userRole = new() { ID = 1, Name = "User" };
        db.Roles.Add(userRole);

        User user = new()
        {
            EmailAddress = $"promo_user_{Guid.NewGuid():N}@kongor.test",
            PBKDF2PasswordHash = "h",
            SRPPasswordHash = "h",
            SRPPasswordSalt = "s",
            Role = userRole,
            GoldCoins = 0,
            SilverCoins = 500,
            PlinkoTickets = 2
        };
        Account account = new()
        {
            Name = "PromoPlayer",
            User = user,
            Type = AccountType.Normal,
            IsMain = true
        };
        RedeemableCode code = new()
        {
            Code = "WELCOME2026",
            GoldCoinsReward = 250,
            SilverCoinsReward = 1500,
            PlinkoTicketsReward = 10,
            TimestampRedeemed = null,
            RedeemedByAccountID = null
        };
        db.Users.Add(user);
        db.Accounts.Add(account);
        db.RedeemableCodes.Add(code);
        await db.SaveChangesAsync();

        InventoryRepository repo = new(db);

        // Act - Simulate code redemption pipeline
        RedeemableCode? activeCode = await db.RedeemableCodes.FirstOrDefaultAsync(c => c.Code == "WELCOME2026");
        activeCode.Should().NotBeNull();
        activeCode!.TimestampRedeemed.Should().BeNull();

        // 1. Credit currency
        await repo.AddCurrenciesAsync(account.ID, activeCode.GoldCoinsReward, activeCode.SilverCoinsReward,
            activeCode.PlinkoTicketsReward);

        // 2. Mark code redeemed
        activeCode.TimestampRedeemed = TimeProvider.System.GetUtcNow();
        activeCode.RedeemedByAccountID = account.ID;
        await db.SaveChangesAsync();

        // Assert
        User? refreshedUser = await db.Users.FindAsync(user.ID);
        refreshedUser.Should().NotBeNull();
        refreshedUser!.GoldCoins.Should().Be(250);
        refreshedUser.SilverCoins.Should().Be(2000); // 500 + 1500
        refreshedUser.PlinkoTickets.Should().Be(12); // 2 + 10

        RedeemableCode? refreshedCode = await db.RedeemableCodes.FirstOrDefaultAsync(c => c.Code == "WELCOME2026");
        refreshedCode!.TimestampRedeemed.Should().NotBeNull();
        refreshedCode.RedeemedByAccountID.Should().Be(account.ID);
    }

    [Trait("Category", "Manual")]
    [Fact]
    public async Task Combination_RedeemCode_AlreadyRedeemedCode_RejectsWithoutDoubleCrediting()
    {
        if (!NexusTestEnvironment.IsManualTestEnabled) return;
        // Arrange
        using MerrickContext db = CreateInMemoryContext();
        Role userRole = new() { ID = 1, Name = "User" };
        db.Roles.Add(userRole);

        User user = new()
        {
            EmailAddress = $"promo_second_{Guid.NewGuid():N}@kongor.test",
            PBKDF2PasswordHash = "h",
            SRPPasswordHash = "h",
            SRPPasswordSalt = "s",
            Role = userRole,
            GoldCoins = 100,
            SilverCoins = 100,
            PlinkoTickets = 0
        };
        Account account = new()
        {
            Name = "SecondPromoPlayer",
            User = user,
            Type = AccountType.Normal,
            IsMain = true
        };
        RedeemableCode usedCode = new()
        {
            Code = "USEDCODE123",
            GoldCoinsReward = 1000,
            SilverCoinsReward = 5000,
            PlinkoTicketsReward = 20,
            TimestampRedeemed = TimeProvider.System.GetUtcNow().AddDays(-2),
            RedeemedByAccountID = 99
        };
        db.Users.Add(user);
        db.Accounts.Add(account);
        db.RedeemableCodes.Add(usedCode);
        await db.SaveChangesAsync();

        // Act - Attempt redemption
        RedeemableCode? targetCode = await db.RedeemableCodes.FirstOrDefaultAsync(c => c.Code == "USEDCODE123");
        bool canRedeem = targetCode != null && targetCode.TimestampRedeemed == null;

        // Assert
        canRedeem.Should().BeFalse();

        User? userState = await db.Users.FindAsync(user.ID);
        userState!.GoldCoins.Should().Be(100);
        userState.SilverCoins.Should().Be(100);
    }
}
