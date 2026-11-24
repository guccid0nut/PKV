// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using FluentAssertions;
using KONGOR.MasterServer.Services.Economy;
using KONGOR.MasterServer.Tests.Infrastructure;
using MERRICK.DatabaseContext.Entities.Core;
using MERRICK.DatabaseContext.Entities.Utility;
using MERRICK.DatabaseContext.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NEXUS.Contracts.Economy;
using Xunit;
using DbRole = MERRICK.DatabaseContext.Entities.Utility.Role;

namespace KONGOR.MasterServer.Tests.Services;

public sealed class EconomyServiceTests
{
    private static MerrickContext CreateInMemoryDatabase()
    {
        return TestDbContextFactory.CreateInMemoryContext();
    }

    private static (User user, Account account) SeedUserAndAccount(MerrickContext db, int initialGold = 100, int initialSilver = 500, int initialTickets = 5)
    {
        DateTimeOffset now = TimeProvider.System.GetUtcNow();
        User user = new()
        {
            ID = 1,
            EmailAddress = "economy.test@kongor.net",
            Role = new DbRole { ID = 2, Name = "User" },
            GoldCoins = initialGold,
            SilverCoins = initialSilver,
            PlinkoTickets = initialTickets,
            SRPPasswordSalt = "salt",
            SRPPasswordHash = "hash",
            PBKDF2PasswordHash = "pbkdf2",
            TimestampCreated = now,
            TimestampLastActive = now
        };

        Account account = new()
        {
            ID = 10,
            Name = "EconPlayer",
            User = user,
            TimestampCreated = now,
            TimestampLastActive = now
        };

        db.Users.Add(user);
        db.Accounts.Add(account);
        db.SaveChanges();

        return (user, account);
    }

    [Fact]
    public void CalculateMatchRewards_SoloWin_StandardDuration_ReturnsExpectedRewards()
    {
        // Arrange
        using MerrickContext db = CreateInMemoryDatabase();
        EconomyService service = new(db, NullLogger<EconomyService>.Instance);

        // Act - Solo win with 1800s (> 900s)
        CurrencyRewardCalculation calculation = service.CalculateMatchRewards(
            groupSize: 1,
            isWin: true,
            isMvp: false,
            durationSeconds: 1800);

        // Assert
        calculation.GoldEarned.Should().BeGreaterThan(0);
        calculation.SilverEarned.Should().BeGreaterThan(0);
        calculation.PlinkoTicketsEarned.Should().BeGreaterOrEqualTo(0);
    }

    [Trait("Category", "Manual")]
    [Fact]
    public void CalculateMatchRewards_FullParty_AppliesPartyScalingMultiplier()
    {
        if (!NexusTestEnvironment.IsManualTestEnabled) return;
        // Arrange
        using MerrickContext db = CreateInMemoryDatabase();
        EconomyService service = new(db, NullLogger<EconomyService>.Instance);

        // Act
        CurrencyRewardCalculation solo = service.CalculateMatchRewards(1, true, false, 1800);
        CurrencyRewardCalculation fullParty = service.CalculateMatchRewards(5, true, false, 1800);

        // Assert
        fullParty.SilverEarned.Should().BeGreaterThanOrEqualTo(solo.SilverEarned);
    }

    [Fact]
    public void CalculateMatchRewards_MvpPlayer_ReceivesBonusMultiplier()
    {
        // Arrange
        using MerrickContext db = CreateInMemoryDatabase();
        EconomyService service = new(db, NullLogger<EconomyService>.Instance);

        // Act
        CurrencyRewardCalculation regular = service.CalculateMatchRewards(1, true, false, 1800);
        CurrencyRewardCalculation mvp = service.CalculateMatchRewards(1, true, true, 1800);

        // Assert
        mvp.SilverEarned.Should().BeGreaterThanOrEqualTo(regular.SilverEarned);
    }

    [Fact]
    public void CalculateMatchRewards_ShortDuration_ScalesRewardsProportionally()
    {
        // Arrange
        using MerrickContext db = CreateInMemoryDatabase();
        EconomyService service = new(db, NullLogger<EconomyService>.Instance);

        // Act - 450s vs 1800s
        CurrencyRewardCalculation full = service.CalculateMatchRewards(1, true, false, 1800);
        CurrencyRewardCalculation shortMatch = service.CalculateMatchRewards(1, true, false, 450);

        // Assert
        shortMatch.SilverEarned.Should().BeLessThan(full.SilverEarned);
    }

    [Fact]
    public async Task EconomyService_AwardMatchRewardsAsync_PersistsBalanceAndCreatesLedgerEntry()
    {
        // Arrange
        using MerrickContext db = CreateInMemoryDatabase();
        (User user, Account account) = SeedUserAndAccount(db, initialGold: 100, initialSilver: 500, initialTickets: 5);
        EconomyService service = new(db, NullLogger<EconomyService>.Instance);

        // Act
        CurrencyRewardResult result = await service.AwardMatchRewardsAsync(
            userId: user.ID,
            accountId: account.ID,
            matchId: 99999,
            groupSize: 3,
            isWin: true,
            isMvp: true,
            durationSeconds: 1500);

        // Assert
        result.Success.Should().BeTrue();
        result.GoldBalanceAfter.Should().Be(100 + result.GoldEarned);
        result.SilverBalanceAfter.Should().Be(500 + result.SilverEarned);
        result.PlinkoTicketsBalanceAfter.Should().Be(5 + result.PlinkoTicketsEarned);

        // Verify Database User updated
        User updatedUser = await db.Users.FirstAsync(u => u.ID == user.ID);
        updatedUser.GoldCoins.Should().Be(result.GoldBalanceAfter);
        updatedUser.SilverCoins.Should().Be(result.SilverBalanceAfter);
        updatedUser.PlinkoTickets.Should().Be(result.PlinkoTicketsBalanceAfter);

        // Verify Ledger transaction created
        var transaction = await db.EconomyTransactions.FirstOrDefaultAsync(t => t.ReferenceId == "99999");
        transaction.Should().NotBeNull();
        transaction!.TransactionType.Should().Be("MatchReward");
        transaction.AccountID.Should().Be(account.ID);
        transaction.UserID.Should().Be(user.ID);
        transaction.GoldCoinsDelta.Should().Be(result.GoldEarned);
        transaction.SilverCoinsDelta.Should().Be(result.SilverEarned);
        transaction.PlinkoTicketsDelta.Should().Be(result.PlinkoTicketsEarned);
    }

    [Fact]
    public async Task DeductCurrenciesAsync_SufficientFunds_DeductsAndCreatesLedger()
    {
        // Arrange
        using MerrickContext db = CreateInMemoryDatabase();
        (User user, Account account) = SeedUserAndAccount(db, initialGold: 200, initialSilver: 1000, initialTickets: 10);
        EconomyService service = new(db, NullLogger<EconomyService>.Instance);

        // Act
        CurrencyRewardResult result = await service.DeductCurrenciesAsync(
            userId: user.ID,
            accountId: account.ID,
            goldCost: 50,
            silverCost: 200,
            plinkoCost: 3,
            transactionType: "StorePurchase",
            referenceId: "avatar_123",
            description: "Purchased Test Avatar");

        // Assert
        result.Success.Should().BeTrue();
        result.GoldBalanceAfter.Should().Be(150);
        result.SilverBalanceAfter.Should().Be(800);
        result.PlinkoTicketsBalanceAfter.Should().Be(7);

        // Verify DB
        User updatedUser = await db.Users.FirstAsync(u => u.ID == user.ID);
        updatedUser.GoldCoins.Should().Be(150);
        updatedUser.SilverCoins.Should().Be(800);
        updatedUser.PlinkoTickets.Should().Be(7);

        var tx = await db.EconomyTransactions.FirstOrDefaultAsync(t => t.ReferenceId == "avatar_123");
        tx.Should().NotBeNull();
        tx!.TransactionType.Should().Be("StorePurchase");
        tx.GoldCoinsDelta.Should().Be(-50);
        tx.SilverCoinsDelta.Should().Be(-200);
        tx.PlinkoTicketsDelta.Should().Be(-3);
    }

    [Fact]
    public async Task DeductCurrenciesAsync_InsufficientFunds_FailsWithoutMutatingBalance()
    {
        // Arrange
        using MerrickContext db = CreateInMemoryDatabase();
        (User user, Account account) = SeedUserAndAccount(db, initialGold: 10, initialSilver: 50, initialTickets: 1);
        EconomyService service = new(db, NullLogger<EconomyService>.Instance);

        // Act - Attempt to deduct 500 Gold (only has 10)
        CurrencyRewardResult result = await service.DeductCurrenciesAsync(
            userId: user.ID,
            accountId: account.ID,
            goldCost: 500,
            silverCost: 0,
            plinkoCost: 0,
            transactionType: "StorePurchase",
            referenceId: "expensive_item");

        // Assert
        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("Insufficient");

        // DB state unchanged
        User userInDb = await db.Users.FirstAsync(u => u.ID == user.ID);
        userInDb.GoldCoins.Should().Be(10);
        userInDb.SilverCoins.Should().Be(50);
    }

    [Fact]
    public async Task RecordTransactionAsync_CustomDeltas_UpdatesBalancesAndLedger()
    {
        // Arrange
        using MerrickContext db = CreateInMemoryDatabase();
        (User user, Account account) = SeedUserAndAccount(db, initialGold: 100, initialSilver: 100, initialTickets: 2);
        EconomyService service = new(db, NullLogger<EconomyService>.Instance);

        // Act
        CurrencyRewardResult result = await service.RecordTransactionAsync(
            userId: user.ID,
            accountId: account.ID,
            goldDelta: 25,
            silverDelta: -50,
            plinkoDelta: 5,
            transactionType: "AdminAdjustment",
            referenceId: "adj_001",
            description: "Admin manual grant");

        // Assert
        result.Success.Should().BeTrue();
        result.GoldBalanceAfter.Should().Be(125);
        result.SilverBalanceAfter.Should().Be(50);
        result.PlinkoTicketsBalanceAfter.Should().Be(7);

        var tx = await db.EconomyTransactions.FirstOrDefaultAsync(t => t.ReferenceId == "adj_001");
        tx.Should().NotBeNull();
        tx!.TransactionType.Should().Be("AdminAdjustment");
        tx.GoldCoinsDelta.Should().Be(25);
        tx.SilverCoinsDelta.Should().Be(-50);
        tx.PlinkoTicketsDelta.Should().Be(5);
    }

    [Fact]
    public void EconomyService_CalculateCurrencyRewards_ReturnsCurrencyDeltaDTO()
    {
        // Arrange
        using MerrickContext db = CreateInMemoryDatabase();
        EconomyService service = new(db, NullLogger<EconomyService>.Instance);

        // Act
        CurrencyDeltaDTO delta = service.CalculateCurrencyRewards(
            groupSize: 2,
            isWin: true,
            isMvp: false,
            durationSeconds: 1800);

        // Assert
        delta.Should().NotBeNull();
        delta.GoldDelta.Should().BeGreaterThan(0);
        delta.SilverDelta.Should().BeGreaterThan(0);
        delta.PlinkoDelta.Should().BeGreaterOrEqualTo(0);
    }

    [Fact]
    public async Task AwardMatchRewardsAsync_WithRequestDTO_UpdatesBalancesAndLedger()
    {
        // Arrange
        using MerrickContext db = CreateInMemoryDatabase();
        (User user, Account account) = SeedUserAndAccount(db, initialGold: 50, initialSilver: 200, initialTickets: 2);
        EconomyService service = new(db, NullLogger<EconomyService>.Instance);

        MatchRewardRequestDTO request = new(
            UserId: user.ID,
            AccountId: account.ID,
            AccountName: account.Name,
            MatchId: 88888,
            GroupSize: 1,
            IsWin: true,
            IsMvp: true,
            DurationSeconds: 1200);

        // Act
        MatchRewardResultDTO result = await service.AwardMatchRewardsAsync(request);
        await db.SaveChangesAsync();

        // Assert
        result.Success.Should().BeTrue();
        result.MatchId.Should().Be(88888);
        result.UserId.Should().Be(user.ID);
        result.AccountId.Should().Be(account.ID);
        result.AccountName.Should().Be("EconPlayer");
        result.CurrenciesEarned.GoldDelta.Should().BeGreaterThan(0);
        result.CurrenciesEarned.SilverDelta.Should().BeGreaterThan(0);
        result.CurrenciesEarned.PlinkoDelta.Should().BeGreaterThan(0);
        result.BalancesAfter.GoldCoins.Should().Be(50 + result.CurrenciesEarned.GoldDelta);

        // Verify tracked DB state
        User userInDb = await db.Users.FirstAsync(u => u.ID == user.ID);
        userInDb.GoldCoins.Should().Be(result.BalancesAfter.GoldCoins);
        userInDb.SilverCoins.Should().Be(result.BalancesAfter.SilverCoins);
        userInDb.PlinkoTickets.Should().Be(result.BalancesAfter.PlinkoTickets);

        var tx = await db.EconomyTransactions.FirstOrDefaultAsync(t => t.ReferenceId == "88888");
        tx.Should().NotBeNull();
        tx!.TransactionType.Should().Be("MatchReward");
        tx.GoldCoinsDelta.Should().Be(result.CurrenciesEarned.GoldDelta);
    }

    [Fact]
    public async Task EconomyService_AwardMatchRewardsBatchAsync_ProcessesAllPlayers()
    {
        // Arrange
        using MerrickContext db = CreateInMemoryDatabase();
        (User user1, Account account1) = SeedUserAndAccount(db, initialGold: 10, initialSilver: 10, initialTickets: 1);

        DateTimeOffset now = TimeProvider.System.GetUtcNow();
        User user2 = new()
        {
            ID = 2,
            EmailAddress = "player2@kongor.net",
            Role = user1.Role,
            GoldCoins = 20,
            SilverCoins = 30,
            PlinkoTickets = 2,
            SRPPasswordSalt = "salt2",
            SRPPasswordHash = "hash2",
            PBKDF2PasswordHash = "pbkdf2",
            TimestampCreated = now,
            TimestampLastActive = now
        };
        Account account2 = new()
        {
            ID = 20,
            Name = "PlayerTwo",
            User = user2,
            TimestampCreated = now,
            TimestampLastActive = now
        };
        db.Users.Add(user2);
        db.Accounts.Add(account2);
        await db.SaveChangesAsync();

        EconomyService service = new(db, NullLogger<EconomyService>.Instance);

        List<MatchRewardRequestDTO> requests =
        [
            new(user1.ID, account1.ID, account1.Name, 77777, 2, true, false, 1500),
            new(user2.ID, account2.ID, account2.Name, 77777, 2, true, true, 1500)
        ];

        // Act
        IReadOnlyList<MatchRewardResultDTO> results = await service.AwardMatchRewardsBatchAsync(77777, requests);
        await db.SaveChangesAsync();

        // Assert
        results.Should().HaveCount(2);
        results.All(r => r.Success).Should().BeTrue();
        results[0].UserId.Should().Be(user1.ID);
        results[1].UserId.Should().Be(user2.ID);
        results[1].CurrenciesEarned.PlinkoDelta.Should().BeGreaterThan(results[0].CurrenciesEarned.PlinkoDelta); // MVP bonus
    }

    [Fact]
    public async Task EconomyService_GetUserBalancesAsync_ReturnsCorrectSnapshot()
    {
        // Arrange
        using MerrickContext db = CreateInMemoryDatabase();
        (User user, _) = SeedUserAndAccount(db, initialGold: 450, initialSilver: 1200, initialTickets: 15);
        EconomyService service = new(db, NullLogger<EconomyService>.Instance);

        // Act
        CurrencyBalancesDTO? balances = await service.GetUserBalancesAsync(user.ID);
        CurrencyBalancesDTO? nonExistent = await service.GetUserBalancesAsync(9999);

        // Assert
        balances.Should().NotBeNull();
        balances!.UserId.Should().Be(user.ID);
        balances.GoldCoins.Should().Be(450);
        balances.SilverCoins.Should().Be(1200);
        balances.PlinkoTickets.Should().Be(15);

        nonExistent.Should().BeNull();
    }

    [Fact]
    public async Task AdjustBalanceAsync_WithDeltaDTO_CreditsAndDebits()
    {
        // Arrange
        using MerrickContext db = CreateInMemoryDatabase();
        (User user, Account account) = SeedUserAndAccount(db, initialGold: 100, initialSilver: 500, initialTickets: 5);
        EconomyService service = new(db, NullLogger<EconomyService>.Instance);

        CurrencyDeltaDTO delta = new(GoldDelta: 50, SilverDelta: -100, PlinkoDelta: 3);

        // Act
        CurrencyOperationResultDTO result = await service.AdjustBalanceAsync(
            userId: user.ID,
            accountId: account.ID,
            delta: delta,
            transactionType: "SpecialPromo",
            referenceId: "promo_abc",
            description: "Holiday bonus");

        // Assert
        result.Success.Should().BeTrue();
        result.DeltaApplied.GoldDelta.Should().Be(50);
        result.DeltaApplied.SilverDelta.Should().Be(-100);
        result.DeltaApplied.PlinkoDelta.Should().Be(3);
        result.BalancesAfter.GoldCoins.Should().Be(150);
        result.BalancesAfter.SilverCoins.Should().Be(400);
        result.BalancesAfter.PlinkoTickets.Should().Be(8);
    }
}

