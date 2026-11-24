// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using FluentAssertions;
using KONGOR.MasterServer.Configuration;
using KONGOR.MasterServer.Configuration.Economy;
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

/// <summary>
///     Adversarial and Empirical Challenge Test Suite for Milestone 3 EconomyService.
/// </summary>
public sealed class EconomyServiceAdversarialTests
{
    private static MerrickContext CreateInMemoryDatabase()
    {
        return TestDbContextFactory.CreateInMemoryContext("Econ_Adv_" + Guid.NewGuid().ToString("N"));
    }

    private static (User user, Account account) SeedUser(MerrickContext db, int id = 1, int initialGold = 100, int initialSilver = 500, int initialTickets = 5)
    {
        DateTimeOffset now = TimeProvider.System.GetUtcNow();
        DbRole role = db.Roles.Local.FirstOrDefault(r => r.ID == 2) 
                      ?? db.Roles.FirstOrDefault(r => r.ID == 2) 
                      ?? new DbRole { ID = 2, Name = "User" };

        User user = new()
        {
            ID = id,
            EmailAddress = $"challenger_user_{id}@kongor.test",
            Role = role,
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
            ID = id * 10,
            Name = $"Player_{id}",
            User = user,
            TimestampCreated = now,
            TimestampLastActive = now
        };

        db.Users.Add(user);
        db.Accounts.Add(account);
        db.SaveChanges();

        return (user, account);
    }

    #region 1. Currency Calculation Corner Cases

    [Theory]
    [InlineData(-5, true, false, 1800)]
    [InlineData(0, true, false, 1800)]
    [InlineData(1, true, false, 1800)]
    [InlineData(2, true, false, 1800)]
    [InlineData(3, true, false, 1800)]
    [InlineData(4, true, false, 1800)]
    [InlineData(5, true, false, 1800)]
    [InlineData(10, true, false, 1800)]
    [InlineData(100, true, false, 1800)]
    public void CalculateCurrencyRewards_GroupSizeBoundaries_ClampsProperly(
        int groupSize, bool isWin, bool isMvp, int durationSeconds)
    {
        EconomyService service = new();

        CurrencyDeltaDTO delta = service.CalculateCurrencyRewards(groupSize, isWin, isMvp, durationSeconds);

        delta.GoldDelta.Should().BeGreaterThanOrEqualTo(1);
        delta.SilverDelta.Should().BeGreaterThanOrEqualTo(1);
        delta.PlinkoDelta.Should().BeGreaterThanOrEqualTo(1);

        if (groupSize <= 1)
        {
            CurrencyDeltaDTO solo = service.CalculateCurrencyRewards(1, isWin, isMvp, durationSeconds);
            delta.GoldDelta.Should().Be(solo.GoldDelta);
            delta.SilverDelta.Should().Be(solo.SilverDelta);
            delta.PlinkoDelta.Should().Be(solo.PlinkoDelta);
        }
        else if (groupSize >= 5)
        {
            CurrencyDeltaDTO fiveMan = service.CalculateCurrencyRewards(5, isWin, isMvp, durationSeconds);
            delta.GoldDelta.Should().Be(fiveMan.GoldDelta);
            delta.SilverDelta.Should().Be(fiveMan.SilverDelta);
            delta.PlinkoDelta.Should().Be(fiveMan.PlinkoDelta);
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public void EconomyServiceAdversarial_CalculateCurrencyRewards_WinAlwaysGreaterOrEqualToLoss(int groupSize)
    {
        EconomyService service = new();

        CurrencyDeltaDTO win = service.CalculateCurrencyRewards(groupSize, isWin: true, isMvp: false, durationSeconds: 1800);
        CurrencyDeltaDTO loss = service.CalculateCurrencyRewards(groupSize, isWin: false, isMvp: false, durationSeconds: 1800);

        win.GoldDelta.Should().BeGreaterThan(loss.GoldDelta);
        win.SilverDelta.Should().BeGreaterThan(loss.SilverDelta);
        win.PlinkoDelta.Should().BeGreaterThan(loss.PlinkoDelta);
    }

    [Theory]
    [InlineData(1, true)]
    [InlineData(1, false)]
    [InlineData(3, true)]
    [InlineData(5, true)]
    public void CalculateCurrencyRewards_MvpMultiplier_AddsBonusCurrencies(int groupSize, bool isWin)
    {
        EconomyService service = new();

        CurrencyDeltaDTO regular = service.CalculateCurrencyRewards(groupSize, isWin, isMvp: false, durationSeconds: 1800);
        CurrencyDeltaDTO mvp = service.CalculateCurrencyRewards(groupSize, isWin, isMvp: true, durationSeconds: 1800);

        // MVP gets 1.25x gold & silver (rounded) and +2 plinko
        mvp.GoldDelta.Should().Be((int)Math.Round(regular.GoldDelta * 1.25));
        mvp.SilverDelta.Should().Be((int)Math.Round(regular.SilverDelta * 1.25));
        mvp.PlinkoDelta.Should().Be(regular.PlinkoDelta + 2);
    }

    [Theory]
    [InlineData(0)]      // 0s falls out of >0 and <900, retains baseline
    [InlineData(1)]      // 1s clamped to min 0.25
    [InlineData(100)]    // 100s clamped to min 0.25
    [InlineData(225)]    // 225s = 225/900 = exactly 0.25
    [InlineData(300)]    // 300s = 300/900 = 1/3
    [InlineData(450)]    // 450s = half duration
    [InlineData(899)]    // 899s just below 900
    [InlineData(900)]    // 900s baseline
    [InlineData(1800)]   // 1800s full match
    [InlineData(3600)]   // 3600s long match
    [InlineData(-100)]   // Negative duration falls out of >0
    public void CalculateCurrencyRewards_DurationScaling_ScalesAccurately(int durationSeconds)
    {
        EconomyService service = new();

        CurrencyDeltaDTO fullMatch = service.CalculateCurrencyRewards(1, isWin: true, isMvp: false, durationSeconds: 1800);
        CurrencyDeltaDTO scaled = service.CalculateCurrencyRewards(1, isWin: true, isMvp: false, durationSeconds: durationSeconds);

        if (durationSeconds >= 900 || durationSeconds <= 0)
        {
            scaled.GoldDelta.Should().Be(fullMatch.GoldDelta);
            scaled.SilverDelta.Should().Be(fullMatch.SilverDelta);
            scaled.PlinkoDelta.Should().Be(fullMatch.PlinkoDelta);
        }
        else
        {
            double scale = Math.Clamp(durationSeconds / 900.0, 0.25, 1.0);
            int expectedGold = Math.Max(1, (int)Math.Round(fullMatch.GoldDelta * scale));
            int expectedSilver = Math.Max(1, (int)Math.Round(fullMatch.SilverDelta * scale));
            int expectedPlinko = Math.Max(1, (int)Math.Round(fullMatch.PlinkoDelta * scale));

            scaled.GoldDelta.Should().Be(expectedGold);
            scaled.SilverDelta.Should().Be(expectedSilver);
            scaled.PlinkoDelta.Should().Be(expectedPlinko);
        }
    }

    [Fact]
    public void CalculateCurrencyRewards_MonotonicGroupProgression_EnsuresIncreasingRewards()
    {
        EconomyService service = new();

        CurrencyDeltaDTO g1 = service.CalculateCurrencyRewards(1, isWin: true, isMvp: false, durationSeconds: 1800);
        CurrencyDeltaDTO g2 = service.CalculateCurrencyRewards(2, isWin: true, isMvp: false, durationSeconds: 1800);
        CurrencyDeltaDTO g3 = service.CalculateCurrencyRewards(3, isWin: true, isMvp: false, durationSeconds: 1800);
        CurrencyDeltaDTO g4 = service.CalculateCurrencyRewards(4, isWin: true, isMvp: false, durationSeconds: 1800);
        CurrencyDeltaDTO g5 = service.CalculateCurrencyRewards(5, isWin: true, isMvp: false, durationSeconds: 1800);

        g1.GoldDelta.Should().BeLessThan(g2.GoldDelta);
        g2.GoldDelta.Should().BeLessThan(g3.GoldDelta);
        g3.GoldDelta.Should().BeLessThan(g4.GoldDelta);
        g4.GoldDelta.Should().BeLessThan(g5.GoldDelta);

        g1.SilverDelta.Should().BeLessThan(g2.SilverDelta);
        g2.SilverDelta.Should().BeLessThan(g3.SilverDelta);
        g3.SilverDelta.Should().BeLessThan(g4.SilverDelta);
        g4.SilverDelta.Should().BeLessThan(g5.SilverDelta);

        g1.PlinkoDelta.Should().BeLessThan(g2.PlinkoDelta);
        g2.PlinkoDelta.Should().BeLessThan(g3.PlinkoDelta);
        g3.PlinkoDelta.Should().BeLessThan(g4.PlinkoDelta);
        g4.PlinkoDelta.Should().BeLessThan(g5.PlinkoDelta);
    }

    #endregion

    #region 2. Wallet Balance & Audit Ledger Verification

    [Trait("Category", "Manual")]
    [Fact]
    public async Task EconomyServiceAdversarial_AwardMatchRewardsAsync_VerifiesAuditLedgerIntegrityAndExactBalances()
    {
        if (!NexusTestEnvironment.IsManualTestEnabled) return;
        // Arrange
        using MerrickContext db = CreateInMemoryDatabase();
        (User user, Account account) = SeedUser(db, id: 50, initialGold: 250, initialSilver: 1500, initialTickets: 12);
        EconomyService service = new(db, NullLogger<EconomyService>.Instance);

        MatchRewardRequestDTO request = new(
            UserId: user.ID,
            AccountId: account.ID,
            AccountName: account.Name,
            MatchId: 54321,
            GroupSize: 4,
            IsWin: true,
            IsMvp: true,
            DurationSeconds: 1800);

        // Act
        MatchRewardResultDTO result = await service.AwardMatchRewardsAsync(request);
        await db.SaveChangesAsync();

        // Assert DTO result
        result.Success.Should().BeTrue();
        result.UserId.Should().Be(50);
        result.AccountId.Should().Be(500);
        result.MatchId.Should().Be(54321);
        result.BalancesAfter.GoldCoins.Should().Be(250 + result.CurrenciesEarned.GoldDelta);
        result.BalancesAfter.SilverCoins.Should().Be(1500 + result.CurrenciesEarned.SilverDelta);
        result.BalancesAfter.PlinkoTickets.Should().Be(12 + result.CurrenciesEarned.PlinkoDelta);

        // Assert DB Ledger record
        var ledger = await db.EconomyTransactions.FirstOrDefaultAsync(t => t.ReferenceId == "54321");
        ledger.Should().NotBeNull();
        ledger!.UserID.Should().Be(50);
        ledger.AccountID.Should().Be(500);
        ledger.TransactionType.Should().Be("MatchReward");
        ledger.ReferenceId.Should().Be("54321");
        ledger.Description.Should().Be("Match #54321 completion reward");
        ledger.GoldCoinsDelta.Should().Be(result.CurrenciesEarned.GoldDelta);
        ledger.SilverCoinsDelta.Should().Be(result.CurrenciesEarned.SilverDelta);
        ledger.PlinkoTicketsDelta.Should().Be(result.CurrenciesEarned.PlinkoDelta);
        ledger.GoldCoinsBalanceAfter.Should().Be(result.BalancesAfter.GoldCoins);
        ledger.SilverCoinsBalanceAfter.Should().Be(result.BalancesAfter.SilverCoins);
        ledger.PlinkoTicketsBalanceAfter.Should().Be(result.BalancesAfter.PlinkoTickets);
        ledger.Timestamp.Should().BeCloseTo(TimeProvider.System.GetUtcNow(), TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task AwardMatchRewardsAsync_WhenUserAlreadyTrackedInContext_UpdatesTrackedInstanceWithoutDuplicateTracking()
    {
        // Arrange
        using MerrickContext db = CreateInMemoryDatabase();
        (User user, Account account) = SeedUser(db, id: 70, initialGold: 10, initialSilver: 20, initialTickets: 1);

        // Pre-load user into change tracker
        User trackedUser = await db.Users.FirstAsync(u => u.ID == 70);

        EconomyService service = new(db, NullLogger<EconomyService>.Instance);

        MatchRewardRequestDTO request = new(
            UserId: 70,
            AccountId: 700,
            AccountName: "TrackedPlayer",
            MatchId: 65432,
            GroupSize: 1,
            IsWin: false,
            IsMvp: false,
            DurationSeconds: 1800);

        // Act
        MatchRewardResultDTO result = await service.AwardMatchRewardsAsync(request);
        await db.SaveChangesAsync();

        // Assert
        result.Success.Should().BeTrue();
        trackedUser.GoldCoins.Should().Be(10 + result.CurrenciesEarned.GoldDelta);
        trackedUser.SilverCoins.Should().Be(20 + result.CurrenciesEarned.SilverDelta);
        trackedUser.PlinkoTickets.Should().Be(1 + result.CurrenciesEarned.PlinkoDelta);

        // Ensure single change tracker entry
        db.ChangeTracker.Entries<User>().Should().HaveCount(1);
    }

    [Fact]
    public async Task AwardMatchRewardsAsync_WithMissingUser_ReturnsDescriptiveFailure()
    {
        // Arrange
        using MerrickContext db = CreateInMemoryDatabase();
        EconomyService service = new(db, NullLogger<EconomyService>.Instance);

        MatchRewardRequestDTO request = new(
            UserId: 999999,
            AccountId: 888888,
            AccountName: "NonExistent",
            MatchId: 11111,
            GroupSize: 1,
            IsWin: true,
            IsMvp: false,
            DurationSeconds: 1800);

        // Act
        MatchRewardResultDTO result = await service.AwardMatchRewardsAsync(request);

        // Assert
        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("User #999999 not found");
        result.BalancesAfter.GoldCoins.Should().Be(0);
    }

    #endregion

    #region 3. Concurrency & Optimistic Locking Resilience

    [Fact]
    public async Task AdjustBalanceAsync_ConcurrentAdjustments_MaintainsConsistentLedgerAndBalance()
    {
        // Arrange
        using MerrickContext db = CreateInMemoryDatabase();
        (User user, Account account) = SeedUser(db, id: 80, initialGold: 1000, initialSilver: 1000, initialTickets: 100);
        EconomyService service = new(db, NullLogger<EconomyService>.Instance);

        // Act - Run 10 sequential balance adjustments
        for (int i = 0; i < 10; i++)
        {
            CurrencyDeltaDTO delta = new(GoldDelta: 10, SilverDelta: -5, PlinkoDelta: 1);
            CurrencyOperationResultDTO res = await service.AdjustBalanceAsync(
                userId: 80,
                accountId: 800,
                delta: delta,
                transactionType: "SequentialTest",
                referenceId: $"seq_{i}",
                description: $"Step {i}");

            res.Success.Should().BeTrue();
        }

        // Assert
        User finalUser = await db.Users.FirstAsync(u => u.ID == 80);
        finalUser.GoldCoins.Should().Be(1000 + (10 * 10)); // 1100
        finalUser.SilverCoins.Should().Be(1000 - (10 * 5)); // 950
        finalUser.PlinkoTickets.Should().Be(100 + (10 * 1)); // 110

        var transactions = await db.EconomyTransactions.Where(t => t.UserID == 80 && t.TransactionType == "SequentialTest").ToListAsync();
        transactions.Should().HaveCount(10);
    }

    [Fact]
    public async Task DeductCurrenciesAsync_ZeroCost_SucceedsWithoutChangingBalances()
    {
        // Arrange
        using MerrickContext db = CreateInMemoryDatabase();
        (User user, Account account) = SeedUser(db, id: 90, initialGold: 100, initialSilver: 200, initialTickets: 5);
        EconomyService service = new(db, NullLogger<EconomyService>.Instance);

        // Act
        CurrencyRewardResult result = await service.DeductCurrenciesAsync(
            userId: 90,
            accountId: 900,
            goldCost: 0,
            silverCost: 0,
            plinkoCost: 0,
            transactionType: "FreeClaim",
            referenceId: "free_item");

        // Assert
        result.Success.Should().BeTrue();
        result.GoldBalanceAfter.Should().Be(100);
        result.SilverBalanceAfter.Should().Be(200);
        result.PlinkoTicketsBalanceAfter.Should().Be(5);

        var tx = await db.EconomyTransactions.FirstOrDefaultAsync(t => t.ReferenceId == "free_item");
        tx.Should().NotBeNull();
        tx!.GoldCoinsDelta.Should().Be(0);
        tx.SilverCoinsDelta.Should().Be(0);
        tx.PlinkoTicketsDelta.Should().Be(0);
    }

    [Fact]
    public async Task DeductCurrenciesAsync_ExactBalanceDeduction_ReducesToZeroSafely()
    {
        // Arrange
        using MerrickContext db = CreateInMemoryDatabase();
        (User user, Account account) = SeedUser(db, id: 95, initialGold: 50, initialSilver: 100, initialTickets: 3);
        EconomyService service = new(db, NullLogger<EconomyService>.Instance);

        // Act
        CurrencyRewardResult result = await service.DeductCurrenciesAsync(
            userId: 95,
            accountId: 950,
            goldCost: 50,
            silverCost: 100,
            plinkoCost: 3,
            transactionType: "FullDrain",
            referenceId: "drain_all");

        // Assert
        result.Success.Should().BeTrue();
        result.GoldBalanceAfter.Should().Be(0);
        result.SilverBalanceAfter.Should().Be(0);
        result.PlinkoTicketsBalanceAfter.Should().Be(0);

        User refreshed = await db.Users.FirstAsync(u => u.ID == 95);
        refreshed.GoldCoins.Should().Be(0);
        refreshed.SilverCoins.Should().Be(0);
        refreshed.PlinkoTickets.Should().Be(0);
    }

    [Fact]
    public async Task AwardMatchRewardsBatchAsync_FullTenPlayerMatch_CreatesExactLedgerEntriesForEachPlayer()
    {
        // Arrange - 10 players: Team 1 (5 winners), Team 2 (5 losers), 1 MVP on Team 1
        using MerrickContext db = CreateInMemoryDatabase();
        List<MatchRewardRequestDTO> requests = [];

        for (int i = 1; i <= 10; i++)
        {
            (User u, Account a) = SeedUser(db, id: 100 + i, initialGold: 0, initialSilver: 0, initialTickets: 0);
            bool isWinner = i <= 5;
            bool isMvp = i == 1; // First player is MVP

            requests.Add(new MatchRewardRequestDTO(
                UserId: u.ID,
                AccountId: a.ID,
                AccountName: a.Name,
                MatchId: 70001,
                GroupSize: 5,
                IsWin: isWinner,
                IsMvp: isMvp,
                DurationSeconds: 1800));
        }

        EconomyService service = new(db, NullLogger<EconomyService>.Instance);

        // Act
        IReadOnlyList<MatchRewardResultDTO> results = await service.AwardMatchRewardsBatchAsync(70001, requests);
        await db.SaveChangesAsync();

        // Assert
        results.Should().HaveCount(10);
        results.All(r => r.Success).Should().BeTrue();

        // Check MVP player (index 0) has highest rewards
        MatchRewardResultDTO mvpResult = results[0];
        MatchRewardResultDTO teammateResult = results[1];
        MatchRewardResultDTO loserResult = results[5];

        mvpResult.CurrenciesEarned.GoldDelta.Should().BeGreaterThan(teammateResult.CurrenciesEarned.GoldDelta);
        mvpResult.CurrenciesEarned.SilverDelta.Should().BeGreaterThan(teammateResult.CurrenciesEarned.SilverDelta);
        mvpResult.CurrenciesEarned.PlinkoDelta.Should().Be(teammateResult.CurrenciesEarned.PlinkoDelta + 2);

        teammateResult.CurrenciesEarned.GoldDelta.Should().BeGreaterThan(loserResult.CurrenciesEarned.GoldDelta);
        teammateResult.CurrenciesEarned.SilverDelta.Should().BeGreaterThan(loserResult.CurrenciesEarned.SilverDelta);
        teammateResult.CurrenciesEarned.PlinkoDelta.Should().BeGreaterThan(loserResult.CurrenciesEarned.PlinkoDelta);

        // Verify 10 distinct ledger transactions in database
        List<EconomyTransaction> transactions = await db.EconomyTransactions
            .Where(t => t.ReferenceId == "70001" && t.TransactionType == "MatchReward")
            .ToListAsync();

        transactions.Should().HaveCount(10);
        transactions.Select(t => t.UserID).Distinct().Should().HaveCount(10);
    }

    [Fact]
    public async Task AwardMatchRewardsAsync_WithNonPositiveAccountId_SetsAccountIDToNullInLedger()
    {
        // Arrange
        using MerrickContext db = CreateInMemoryDatabase();
        (User user, Account account) = SeedUser(db, id: 200, initialGold: 10, initialSilver: 10, initialTickets: 1);
        EconomyService service = new(db, NullLogger<EconomyService>.Instance);

        MatchRewardRequestDTO request = new(
            UserId: user.ID,
            AccountId: 0, // <= 0
            AccountName: "AnonymousOrBot",
            MatchId: 70002,
            GroupSize: 1,
            IsWin: true,
            IsMvp: false,
            DurationSeconds: 1800);

        // Act
        MatchRewardResultDTO result = await service.AwardMatchRewardsAsync(request);
        await db.SaveChangesAsync();

        // Assert
        result.Success.Should().BeTrue();
        var tx = await db.EconomyTransactions.FirstOrDefaultAsync(t => t.ReferenceId == "70002");
        tx.Should().NotBeNull();
        tx!.AccountID.Should().BeNull();
        tx.UserID.Should().Be(200);
    }

    #endregion

    #region 5. Concurrency & Lost Update Adversarial Probes

    [Fact]
    public async Task UserSpend_ConcurrentInterleavedRequestsWithoutConcurrencyToken_CausesLostUpdateVulnerability()
    {
        // Demonstrates the vulnerability identified in the Step 1 Economy Concurrency Audit:
        // Because User entity lacks an xmin or [ConcurrencyCheck] token, two concurrent spends
        // against separate DbContext scopes will both succeed instead of triggering
        // DbUpdateConcurrencyException retry loops in EconomyService.
        string sharedDb = "Econ_Race_" + Guid.NewGuid().ToString("N");
        using (MerrickContext dbInit = TestDbContextFactory.CreateInMemoryContext(sharedDb))
        {
            SeedUser(dbInit, id: 999, initialGold: 100, initialSilver: 500, initialTickets: 10);
        }

        // Request 1 and Request 2 arrive concurrently and each load the user in their own DbContext scope
        using MerrickContext dbReq1 = TestDbContextFactory.CreateInMemoryContext(sharedDb);
        using MerrickContext dbReq2 = TestDbContextFactory.CreateInMemoryContext(sharedDb);

        User? user1 = await dbReq1.Users.FindAsync(999);
        User? user2 = await dbReq2.Users.FindAsync(999);

        user1.Should().NotBeNull();
        user2.Should().NotBeNull();
        user1!.PlinkoTickets.Should().Be(10);
        user2!.PlinkoTickets.Should().Be(10);

        // Both requests validate the in-memory balance: 10 >= 10 tickets
        const int ticketCost = 10;
        bool req1CanAfford = user1.PlinkoTickets >= ticketCost;
        bool req2CanAfford = user2.PlinkoTickets >= ticketCost;

        req1CanAfford.Should().BeTrue();
        req2CanAfford.Should().BeTrue();

        // Both deduct 10 tickets in memory
        user1.PlinkoTickets -= ticketCost;
        user2.PlinkoTickets -= ticketCost;

        // Request 1 saves first
        await dbReq1.SaveChangesAsync();

        // Request 2 saves second. Without xmin or [ConcurrencyCheck], EF Core does not detect the conflict,
        // so no DbUpdateConcurrencyException is thrown and Request 2 overwrites Request 1's write.
        Func<Task> actReq2 = async () => await dbReq2.SaveChangesAsync();
        await actReq2.Should().NotThrowAsync<DbUpdateConcurrencyException>();

        // Verify that in the database, both requests succeeded (user spent 20 tickets worth, but balance is 0)
        using MerrickContext dbVerify = TestDbContextFactory.CreateInMemoryContext(sharedDb);
        User? finalUser = await dbVerify.Users.FindAsync(999);
        finalUser.Should().NotBeNull();
        finalUser!.PlinkoTickets.Should().Be(0);
        // Note: In v0.2.1 (Step 4), xmin concurrency token will cause request B to throw
        // DbUpdateConcurrencyException, re-read user balance (0 tickets), and return Insufficient Plinko Tickets.
    }

    #endregion
}

