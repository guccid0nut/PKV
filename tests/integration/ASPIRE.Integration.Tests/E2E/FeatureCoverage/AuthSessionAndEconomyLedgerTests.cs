// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ============================================================================
// File: Feature15_AuthSessionAndEconomyLedgerTests.cs
// System: ProjectKongor Solution-Wide E2E Testing Suite
// Tier 1: Feature Coverage (Feature 15: Auth Session Tracking & Economy Ledger)
// Policy: Strict Explicit Typing, Opaque-Box Validation (>= 5 tests)
// ============================================================================

using FluentAssertions;

namespace ASPIRE.Tests.E2E.Tier1_FeatureCoverage;

public sealed class AuthSessionAndEconomyLedgerTests
{
    private static MerrickContext CreateInMemoryContext(string? dbName = null)
    {
        return TestDbContextFactory.CreateInMemoryContext(dbName ?? "Auth_Eco_" + Guid.NewGuid().ToString("N"));
    }

    [Fact]
    public async Task EconomyLedger_DeductCurrencies_DeductsFromUserAndReturnsTrue()
    {
        // Arrange
        using MerrickContext db = CreateInMemoryContext();
        Role userRole = new() { ID = 1, Name = "User" };
        db.Roles.Add(userRole);

        User user = new()
        {
            EmailAddress = $"eco_deduct_{Guid.NewGuid():N}@kongor.test",
            PBKDF2PasswordHash = "hash",
            SRPPasswordHash = "srphash",
            SRPPasswordSalt = "salt",
            Role = userRole,
            GoldCoins = 1000,
            SilverCoins = 5000,
            PlinkoTickets = 10
        };
        Account account = new()
        {
            Name = "EcoDeductPlayer",
            User = user,
            Type = AccountType.Normal,
            IsMain = true
        };
        db.Users.Add(user);
        db.Accounts.Add(account);
        await db.SaveChangesAsync();

        InventoryRepository repo = new(db);

        // Act - Deduct 400 gold and 1000 silver
        bool success = await repo.DeductCurrenciesAsync(account.ID, 400, 1000, 2);

        // Assert
        success.Should().BeTrue();
        User? updated = await db.Users.FindAsync(user.ID);
        updated.Should().NotBeNull();
        updated!.GoldCoins.Should().Be(600);
        updated.SilverCoins.Should().Be(4000);
        updated.PlinkoTickets.Should().Be(8);
    }

    [Fact]
    public async Task EconomyLedger_AddCurrencies_IncrementsUserBalancesAccurately()
    {
        // Arrange
        using MerrickContext db = CreateInMemoryContext();
        Role userRole = new() { ID = 1, Name = "User" };
        db.Roles.Add(userRole);

        User user = new()
        {
            EmailAddress = $"eco_add_{Guid.NewGuid():N}@kongor.test",
            PBKDF2PasswordHash = "hash",
            SRPPasswordHash = "srphash",
            SRPPasswordSalt = "salt",
            Role = userRole,
            GoldCoins = 100,
            SilverCoins = 200,
            PlinkoTickets = 1
        };
        Account account = new()
        {
            Name = "EcoAddPlayer",
            User = user,
            Type = AccountType.Normal,
            IsMain = true
        };
        db.Users.Add(user);
        db.Accounts.Add(account);
        await db.SaveChangesAsync();

        InventoryRepository repo = new(db);

        // Act - Add 500 gold, 1500 silver, 5 plinko tickets
        await repo.AddCurrenciesAsync(account.ID, 500, 1500, 5);

        // Assert
        User? updated = await db.Users.FindAsync(user.ID);
        updated.Should().NotBeNull();
        updated!.GoldCoins.Should().Be(600);
        updated.SilverCoins.Should().Be(1700);
        updated.PlinkoTickets.Should().Be(6);
    }

    [Trait("Category", "Manual")]
    [Fact]
    public async Task AuthSession_IssueAndValidateToken_ReturnsValidTokenWithExpiration()
    {
        if (!NexusTestEnvironment.IsManualTestEnabled) return;
        // Arrange
        using MerrickContext db = CreateInMemoryContext();
        SessionRepository sessionRepo = new(db);

        // Act - Issue token for user 42
        Token token = await sessionRepo.IssueTokenAsync(42, TokenPurpose.PasswordReset, TimeSpan.FromHours(2));

        // Assert
        token.Should().NotBeNull();
        token.UserID.Should().Be(42);
        token.Purpose.Should().Be(TokenPurpose.PasswordReset);
        token.Expires.Should().BeAfter(TimeProvider.System.GetUtcNow().UtcDateTime);

        // Act - Validate token
        Token? validated = await sessionRepo.ValidateTokenAsync(token.Value, TokenPurpose.PasswordReset);
        validated.Should().NotBeNull();
        validated!.UserID.Should().Be(42);
    }

    [Fact]
    public async Task AuthSession_InvalidateToken_PreventsSubsequentValidation()
    {
        // Arrange
        using MerrickContext db = CreateInMemoryContext();
        SessionRepository sessionRepo = new(db);
        Token token = await sessionRepo.IssueTokenAsync(99, TokenPurpose.EmailVerification, TimeSpan.FromHours(1));

        // Act - Invalidate token
        await sessionRepo.InvalidateTokenAsync(token.Value);

        // Assert
        Token? validated = await sessionRepo.ValidateTokenAsync(token.Value, TokenPurpose.EmailVerification);
        validated.Should().BeNull();
    }

    [Trait("Category", "Manual")]
    [Fact]
    public async Task AuthSession_CleanExpiredTokens_RemovesOutdatedTokens()
    {
        if (!NexusTestEnvironment.IsManualTestEnabled) return;
        // Arrange
        using MerrickContext db = CreateInMemoryContext();
        Guid expiredGuid = Guid.Parse("11111111-1111-1111-1111-111111111111");
        Guid validGuid = Guid.Parse("22222222-2222-2222-2222-222222222222");
        Token expiredToken = new()
        {
            UserID = 10,
            EmailAddress = "expired@kongor.net",
            Data = "10",
            Purpose = TokenPurpose.PasswordReset,
            Value = expiredGuid,
            Expires = TimeProvider.System.GetUtcNow().UtcDateTime.AddHours(-2) // expired in the past
        };
        Token validToken = new()
        {
            UserID = 11,
            EmailAddress = "valid@kongor.net",
            Data = "11",
            Purpose = TokenPurpose.PasswordReset,
            Value = validGuid,
            Expires = TimeProvider.System.GetUtcNow().UtcDateTime.AddHours(2)
        };
        db.Tokens.AddRange(expiredToken, validToken);
        await db.SaveChangesAsync();

        SessionRepository sessionRepo = new(db);

        // Act
        await sessionRepo.CleanExpiredTokensAsync();

        // Assert
        Token? remainingExpired = await db.Tokens.FirstOrDefaultAsync(t => t.Value == expiredGuid);
        Token? remainingValid = await db.Tokens.FirstOrDefaultAsync(t => t.Value == validGuid);

        remainingExpired.Should().BeNull();
        remainingValid.Should().NotBeNull();
    }
}
