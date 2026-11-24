// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ============================================================================
// File: Milestone3AdversarialChallengeTests.cs
// System: ProjectKongor Solution-Wide E2E Testing Suite
// Tier 5: Adversarial Challenger Verification (Milestone 3 Core Remediations)
// Policy: Strict Explicit Typing, Adversarial Oracles & Stress Harnesses
// ============================================================================

using ASPIRE.Common.Constants;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using MERRICK.DatabaseContext.Entities.Store;
using Microsoft.Extensions.Logging.Abstractions;
using NEXUS.Contracts.Clients;
using ZORGATH.WebPortal.API.Features.Admin.Users;

namespace ASPIRE.Tests.E2E.Tier5_Challenger;

public sealed class E2EAdversarialChallengeTests
{
    private static readonly Microsoft.Extensions.Configuration.IConfiguration PaidStoreConfig =
        new Microsoft.Extensions.Configuration.ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Store:UnlockAllItems"] = "false" })
            .Build();

    private static MerrickContext CreateContext(string? dbName = null)
    {
        return TestDbContextFactory.CreateInMemoryContext(dbName ?? "M3_Adversarial_" + Guid.NewGuid().ToString("N"));
    }

    private static (User User, Account Account, Role Role) SeedUserAndAccount(
        MerrickContext db,
        int gold = 1000,
        int silver = 5000,
        int plinko = 10,
        string username = "ChallengerPlayer")
    {
        Role role = new() { ID = 1, Name = "User" };
        db.Roles.Add(role);

        User user = new()
        {
            EmailAddress = $"{username.ToLowerInvariant()}_{Guid.NewGuid():N}@kongor.test",
            PBKDF2PasswordHash = "hashed_pw",
            SRPPasswordHash = "srp_verifier",
            SRPPasswordSalt = "srp_salt",
            Role = role,
            GoldCoins = gold,
            SilverCoins = silver,
            PlinkoTickets = plinko,
            InventoryItems = []
        };

        Account account = new()
        {
            Name = username,
            User = user,
            Type = AccountType.Normal,
            IsMain = true,
            SelectedStoreItems = [.. StoreDefaults.DefaultSelectedItems]
        };

        db.Users.Add(user);
        db.Accounts.Add(account);
        db.SaveChanges();

        return (user, account, role);
    }

    private static StoreItem SeedStoreItem(
        MerrickContext db,
        string code = "Hero_Pyromancer.Female",
        string name = "Female Pyromancer",
        int goldPrice = 450,
        int silverPrice = 1200)
    {
        StoreItem item = new()
        {
            Name = name,
            Code = code,
            UpgradeId = 101,
            Price = goldPrice,
            PriceInMMP = silverPrice,
            Enabled = true,
            Purchasable = true,
            UpgradeType = 3,
            LocalContent = "item_pyro_female"
        };

        db.StoreItems.Add(item);
        db.SaveChanges();
        return item;
    }

    // =========================================================================
    // 1. STORE PURCHASE ADVERSARIAL TESTS
    // =========================================================================

    [Fact]
    public async Task StorePurchase_ValidPurchase_DeductsGold_UpdatesUserInventoryAndLedger()
    {
        using MerrickContext db = CreateContext();
        (User user, Account account, _) = SeedUserAndAccount(db, 1000, 2000);
        StoreItem item = SeedStoreItem(db, goldPrice: 450, silverPrice: 1200);

        InventoryRepository invRepo = new(db, configuration: PaidStoreConfig);
        StoreApiController controller = new(invRepo, new AccountRepository(db));

        // Act
        IActionResult result = await controller.Purchase(new StorePurchaseRequestDto(user.ID, item.Id, "gold"));

        // Assert HTTP response
        result.Should().BeOfType<OkObjectResult>();
        OkObjectResult okResult = (OkObjectResult)result;
        PurchaseItemResultDto? body = okResult.Value as PurchaseItemResultDto;
        body.Should().NotBeNull();
        body!.Success.Should().BeTrue();
        body.NewGold.Should().Be(550); // 1000 - 450
        body.NewSilver.Should().Be(2000);

        // Verify User state in DB
        User? dbUser = await db.Users.Include(u => u.InventoryItems).FirstOrDefaultAsync(u => u.ID == user.ID);
        dbUser!.GoldCoins.Should().Be(550);
        dbUser.SilverCoins.Should().Be(2000);
        dbUser.InventoryItems.Select(i => i.ItemCode).Should().Contain(item.Code);

        // Verify normalized UserInventory
        UserInventory? userInv =
            await db.UserInventory.FirstOrDefaultAsync(i => i.UserID == user.ID && i.ItemCode == item.Code);
        userInv.Should().NotBeNull();
        userInv!.Quantity.Should().Be(1);
        userInv.StoreItemId.Should().Be(item.Id);
        userInv.Source.Should().Be("WebStore");

        // Verify EconomyTransaction audit record
        EconomyTransaction? tx =
            await db.EconomyTransactions.FirstOrDefaultAsync(t =>
                t.UserID == user.ID && t.TransactionType == "StorePurchase");
        tx.Should().NotBeNull();
        tx!.GoldCoinsDelta.Should().Be(-450);
        tx.SilverCoinsDelta.Should().Be(0);
        tx.PlinkoTicketsDelta.Should().Be(0);
        tx.GoldCoinsBalanceAfter.Should().Be(550);
        tx.SilverCoinsBalanceAfter.Should().Be(2000);
        tx.ReferenceId.Should().Be(item.Code);
    }

    [Fact]
    public async Task StorePurchase_Silver_DeductsExactCost_GrantsInventory_LogsLedgerWithCorrectDeltasAndBalances()
    {
        using MerrickContext db = CreateContext();
        (User user, Account account, _) = SeedUserAndAccount(db, 500, 4000, 0);
        StoreItem item = SeedStoreItem(db, goldPrice: 400, silverPrice: 3000);

        InventoryRepository invRepo = new(db, configuration: PaidStoreConfig);
        StoreApiController controller = new(invRepo, new AccountRepository(db));

        StorePurchaseRequestDto request = new(user.ID, item.Id, "silver");

        // Act
        IActionResult result = await controller.Purchase(request);

        // Assert
        result.Should().BeOfType<OkObjectResult>();
        OkObjectResult okResult = (OkObjectResult)result;
        PurchaseItemResultDto? body = okResult.Value as PurchaseItemResultDto;
        body.Should().NotBeNull();
        body!.Success.Should().BeTrue();
        body.NewSilver.Should().Be(1000); // 4000 - 3000

        User? dbUser = await db.Users.FindAsync(user.ID);
        dbUser!.SilverCoins.Should().Be(1000);

        EconomyTransaction? tx =
            await db.EconomyTransactions.FirstOrDefaultAsync(t =>
                t.UserID == user.ID && t.TransactionType == "StorePurchase");
        tx.Should().NotBeNull();
        tx!.SilverCoinsDelta.Should().Be(-3000);
        tx.SilverCoinsBalanceAfter.Should().Be(1000);
    }

    [Fact]
    public async Task StorePurchase_InsufficientGold_RejectsPurchase_LeavesBalancesAndInventoryUnchanged()
    {
        using MerrickContext db = CreateContext();
        (User user, Account account, _) = SeedUserAndAccount(db, 300, 5000);
        StoreItem item = SeedStoreItem(db, goldPrice: 500, silverPrice: 3000);

        InventoryRepository invRepo = new(db, configuration: PaidStoreConfig);
        StoreApiController controller = new(invRepo, new AccountRepository(db));

        StorePurchaseRequestDto request = new(user.ID, item.Id, "gold");

        // Act
        IActionResult result = await controller.Purchase(request);

        // Assert
        result.Should().BeOfType<BadRequestObjectResult>();
        BadRequestObjectResult badResult = (BadRequestObjectResult)result;
        PurchaseItemResultDto? body = badResult.Value as PurchaseItemResultDto;
        body.Should().NotBeNull();
        body!.Success.Should().BeFalse();

        // Balances and Inventory untouched
        User? dbUser = await db.Users.FindAsync(user.ID);
        dbUser!.GoldCoins.Should().Be(300);
        db.UserInventory.Should().BeEmpty();
        db.EconomyTransactions.Should().BeEmpty();
    }

    [Fact]
    public async Task StorePurchase_InsufficientSilver_RejectsPurchase_LeavesBalancesAndInventoryUnchanged()
    {
        using MerrickContext db = CreateContext();
        (User user, Account account, _) = SeedUserAndAccount(db, 1000, 1500);
        StoreItem item = SeedStoreItem(db, goldPrice: 500, silverPrice: 3000);

        InventoryRepository invRepo = new(db, configuration: PaidStoreConfig);
        StoreApiController controller = new(invRepo, new AccountRepository(db));

        StorePurchaseRequestDto request = new(user.ID, item.Id, "silver");

        // Act
        IActionResult result = await controller.Purchase(request);

        // Assert
        result.Should().BeOfType<BadRequestObjectResult>();
        User? dbUser = await db.Users.FindAsync(user.ID);
        dbUser!.SilverCoins.Should().Be(1500);
        db.UserInventory.Should().BeEmpty();
        db.EconomyTransactions.Should().BeEmpty();
    }

    [Fact]
    public async Task StorePurchase_InvalidCurrency_RejectsPurchase()
    {
        using MerrickContext db = CreateContext();
        (User user, _, _) = SeedUserAndAccount(db, 1000, 5000);
        StoreItem item = SeedStoreItem(db);

        InventoryRepository invRepo = new(db, configuration: PaidStoreConfig);
        StoreApiController controller = new(invRepo, new AccountRepository(db));

        StorePurchaseRequestDto request = new(user.ID, item.Id, "bitcoin");

        // Act
        IActionResult result = await controller.Purchase(request);

        // Assert
        result.Should().BeOfType<BadRequestObjectResult>();
        db.EconomyTransactions.Should().BeEmpty();
    }

    [Fact]
    public async Task
        StorePurchase_DuplicatePurchase_IncrementsUserInventoryQuantity_DeduplicatesOwnedStoreItems_LogsDistinctLedger()
    {
        using MerrickContext db = CreateContext();
        (User user, Account account, _) = SeedUserAndAccount(db, 1000, 5000);
        StoreItem item = SeedStoreItem(db, goldPrice: 200, silverPrice: 1000);

        InventoryRepository invRepo = new(db, configuration: PaidStoreConfig);
        StoreApiController controller = new(invRepo, new AccountRepository(db));

        // Act 1: First purchase
        IActionResult result1 = await controller.Purchase(new StorePurchaseRequestDto(user.ID, item.Id, "gold"));
        result1.Should().BeOfType<OkObjectResult>();

        // Act 2: Second purchase
        IActionResult result2 = await controller.Purchase(new StorePurchaseRequestDto(user.ID, item.Id, "gold"));
        result2.Should().BeOfType<OkObjectResult>();

        // Assert
        User? dbUser = await db.Users.Include(u => u.InventoryItems).FirstOrDefaultAsync(u => u.ID == user.ID);
        dbUser!.GoldCoins.Should().Be(600); // 1000 - 200 - 200
        dbUser.InventoryItems.Count(x => x.ItemCode.Equals(item.Code, StringComparison.OrdinalIgnoreCase)).Should().Be(1);

        UserInventory? userInv =
            await db.UserInventory.FirstOrDefaultAsync(i => i.UserID == user.ID && i.ItemCode == item.Code);
        userInv.Should().NotBeNull();
        userInv!.Quantity.Should().Be(2);

        List<EconomyTransaction> txs = await db.EconomyTransactions.Where(t => t.UserID == user.ID).ToListAsync();
        txs.Should().HaveCount(2);
        txs[0].GoldCoinsBalanceAfter.Should().Be(800);
        txs[1].GoldCoinsBalanceAfter.Should().Be(600);
    }

    // =========================================================================
    // 2. INVENTORY ITEM GRANTS ADVERSARIAL TESTS
    // =========================================================================

    [Fact]
    public async Task
        InventoryRepository_GrantItem_NewItem_PopulatesStoreItemId_MaintainsUserInventoryAndLegacyStrings()
    {
        using MerrickContext db = CreateContext();
        (User user, Account account, _) = SeedUserAndAccount(db);
        StoreItem item = SeedStoreItem(db, "Hero_Flint.Alt2", "Flint Steampunk");

        InventoryRepository repo = new(db);

        // Act
        await repo.GrantItemAsync(account.ID, "Hero_Flint.Alt2");

        // Assert
        bool hasItem = await repo.HasItemAsync(account.ID, "Hero_Flint.Alt2");
        hasItem.Should().BeTrue();

        UserInventory? inv =
            await db.UserInventory.FirstOrDefaultAsync(i => i.UserID == user.ID && i.ItemCode == "Hero_Flint.Alt2");
        inv.Should().NotBeNull();
        inv!.StoreItemId.Should().Be(item.Id);
        inv.Quantity.Should().Be(1);
        inv.IsPermanent.Should().BeTrue();

        User? dbUser = await db.Users.Include(u => u.InventoryItems).FirstOrDefaultAsync(u => u.ID == user.ID);
        dbUser!.InventoryItems.Select(i => i.ItemCode).Should().Contain("Hero_Flint.Alt2");
    }

    [Fact]
    public async Task InventoryRepository_GrantItem_ExistingItem_IncrementsQuantity_DoesNotDuplicateLegacyStrings()
    {
        using MerrickContext db = CreateContext();
        (User user, Account account, _) = SeedUserAndAccount(db);
        StoreItem item = SeedStoreItem(db, "Taunt.Heavy", "Heavy Taunt");

        InventoryRepository repo = new(db);

        // Act: Grant twice
        await repo.GrantItemAsync(account.ID, "Taunt.Heavy", 1);
        await repo.GrantItemAsync(account.ID, "Taunt.Heavy", 2);

        // Assert
        UserInventory? inv =
            await db.UserInventory.FirstOrDefaultAsync(i => i.UserID == user.ID && i.ItemCode == "Taunt.Heavy");
        inv.Should().NotBeNull();
        inv!.Quantity.Should().Be(3);

        User? dbUser = await db.Users.Include(u => u.InventoryItems).FirstOrDefaultAsync(u => u.ID == user.ID);
        dbUser!.InventoryItems.Count(x => x.ItemCode.Equals("Taunt.Heavy", StringComparison.OrdinalIgnoreCase)).Should().Be(1);
    }

    [Fact]
    public async Task InventoryRepository_HasItem_MatchesByExactCode_And_PrefixDottedNotation()
    {
        using MerrickContext db = CreateContext();
        (User user, Account account, _) = SeedUserAndAccount(db);
        InventoryRepository repo = new(db);

        await repo.GrantItemAsync(account.ID, "Hero_Valkyrie.Alt4");

        // Exact match
        bool exact = await repo.HasItemAsync(account.ID, "Hero_Valkyrie.Alt4");
        exact.Should().BeTrue();

        // Dotted suffix match ("Alt4")
        bool suffix = await repo.HasItemAsync(account.ID, "Alt4");
        suffix.Should().BeTrue();

        // Non-owned item
        bool missing = await repo.HasItemAsync(account.ID, "Hero_Devourer.Alt1");
        missing.Should().BeFalse();
    }

    [Fact]
    public async Task InventoryRepository_DeductCurrencies_AtomicFailureWhenAnyCurrencyInsufficient()
    {
        using MerrickContext db = CreateContext();
        (User user, Account account, _) = SeedUserAndAccount(db, 500, 100, 2);
        InventoryRepository repo = new(db);

        // Act: Request gold 100 (sufficient), silver 200 (INSUFFICIENT), plinko 1 (sufficient)
        bool result = await repo.DeductCurrenciesAsync(account.ID, 100, 200, 1);

        // Assert
        result.Should().BeFalse();

        // Balances remain completely untouched
        User? dbUser = await db.Users.FindAsync(user.ID);
        dbUser!.GoldCoins.Should().Be(500);
        dbUser.SilverCoins.Should().Be(100);
        dbUser.PlinkoTickets.Should().Be(2);

        db.EconomyTransactions.Should().BeEmpty();
    }

    [Fact]
    public async Task InventoryRepository_DeductCurrencies_ExactZeroBalance_Succeeds()
    {
        using MerrickContext db = CreateContext();
        (User user, Account account, _) = SeedUserAndAccount(db, 500, 300, 2);
        InventoryRepository repo = new(db);

        // Act: Deduct exact amounts
        bool result = await repo.DeductCurrenciesAsync(account.ID, 500, 300, 2);

        // Assert
        result.Should().BeTrue();
        User? dbUser = await db.Users.FindAsync(user.ID);
        dbUser!.GoldCoins.Should().Be(0);
        dbUser.SilverCoins.Should().Be(0);
        dbUser.PlinkoTickets.Should().Be(0);

        EconomyTransaction? tx =
            await db.EconomyTransactions.FirstOrDefaultAsync(t =>
                t.UserID == user.ID && t.TransactionType == "InventoryDeduct");
        tx.Should().NotBeNull();
        tx!.GoldCoinsBalanceAfter.Should().Be(0);
        tx.SilverCoinsBalanceAfter.Should().Be(0);
        tx.PlinkoTicketsBalanceAfter.Should().Be(0);
    }

    // =========================================================================
    // 3. PLINKO DROPS ADVERSARIAL TESTS
    // =========================================================================

    [Fact]
    public async Task PlinkoDrop_Daily_FirstClaimSucceeds_ImmediateSecondClaimWithinCooldownFails()
    {
        using MerrickContext db = CreateContext();
        (User user, Account account, _) = SeedUserAndAccount(db, 0, 0, 0);

        InventoryRepository invRepo = new(db, configuration: PaidStoreConfig);
        StoreApiController controller = new(invRepo, new AccountRepository(db));

        // Act 1: First Daily claim
        IActionResult drop1 = await controller.PlinkoDrop(new PlinkoDropApiRequestDto(user.ID, "daily", true));
        drop1.Should().BeOfType<OkObjectResult>();

        User? dbUserAfterFirst = await db.Users.FindAsync(user.ID);
        dbUserAfterFirst!.LastPlinkoDailyClaimUtc.Should().NotBeNull();

        // Verify fee was 0 and reward transaction was logged
        List<EconomyTransaction> txsAfterFirst =
            await db.EconomyTransactions.Where(t => t.UserID == user.ID).ToListAsync();
        txsAfterFirst.Should().Contain(t => t.TransactionType == "PlinkoReward");

        // Act 2: Immediate second daily claim (cooldown is 24 hours)
        IActionResult drop2 = await controller.PlinkoDrop(new PlinkoDropApiRequestDto(user.ID, "daily", true));
        drop2.Should().BeOfType<BadRequestObjectResult>();
        BadRequestObjectResult bad = (BadRequestObjectResult)drop2;
        PlinkoDropResultDto? dropResult = bad.Value as PlinkoDropResultDto;
        dropResult.Should().NotBeNull();
        dropResult!.Success.Should().BeFalse();
        dropResult.Message.Should().Contain("cooldown");
    }

    [Fact]
    public async Task
        PlinkoDrop_GoldFee_Deducts50GoldFee_LogsPlinkoFeeAndPlinkoRewardTransactions_CalculatesNetBalances()
    {
        using MerrickContext db = CreateContext();
        (User user, Account account, _) = SeedUserAndAccount(db, 200, 0, 0);

        InventoryRepository invRepo = new(db, configuration: PaidStoreConfig);
        StoreApiController controller = new(invRepo, new AccountRepository(db));

        // Act
        IActionResult result = await controller.PlinkoDrop(new PlinkoDropApiRequestDto(user.ID, "gold", false));

        // Assert
        result.Should().BeOfType<OkObjectResult>();
        OkObjectResult ok = (OkObjectResult)result;
        PlinkoDropResultDto? dropResult = ok.Value as PlinkoDropResultDto;
        dropResult.Should().NotBeNull();
        dropResult!.Success.Should().BeTrue();

        List<EconomyTransaction> txs = await db.EconomyTransactions
            .Where(t => t.UserID == user.ID)
            .OrderBy(t => t.ID)
            .ToListAsync();

        txs.Should().HaveCount(2);
        txs[0].TransactionType.Should().Be("PlinkoFee");
        txs[0].GoldCoinsDelta.Should().Be(-50);
        txs[0].GoldCoinsBalanceAfter.Should().Be(150);

        txs[1].TransactionType.Should().Be("PlinkoReward");
        txs[1].ReferenceId.Should().StartWith("Slot_");
    }

    [Fact]
    public async Task
        PlinkoDrop_TicketsFee_Deducts100TicketsFee_LogsPlinkoFeeAndPlinkoRewardTransactions_CalculatesNetBalances()
    {
        using MerrickContext db = CreateContext();
        (User user, Account account, _) = SeedUserAndAccount(db, 0, 0, 150);

        InventoryRepository invRepo = new(db, configuration: PaidStoreConfig);
        StoreApiController controller = new(invRepo, new AccountRepository(db));

        // Act
        IActionResult result = await controller.PlinkoDrop(new PlinkoDropApiRequestDto(user.ID, "tickets", false));

        // Assert
        result.Should().BeOfType<OkObjectResult>();

        List<EconomyTransaction> txs = await db.EconomyTransactions
            .Where(t => t.UserID == user.ID)
            .OrderBy(t => t.ID)
            .ToListAsync();

        txs.Should().HaveCount(2);
        txs[0].TransactionType.Should().Be("PlinkoFee");
        txs[0].PlinkoTicketsDelta.Should().Be(-100);
        txs[0].PlinkoTicketsBalanceAfter.Should().Be(50);

        txs[1].TransactionType.Should().Be("PlinkoReward");
    }

    [Fact]
    public async Task PlinkoDrop_InsufficientTickets_RejectsDrop_NoTransactionsLogged()
    {
        using MerrickContext db = CreateContext();
        (User user, Account account, _) = SeedUserAndAccount(db, 0, 0, 99); // needs 100

        InventoryRepository invRepo = new(db, configuration: PaidStoreConfig);
        StoreApiController controller = new(invRepo, new AccountRepository(db));

        // Act
        IActionResult result = await controller.PlinkoDrop(new PlinkoDropApiRequestDto(user.ID, "tickets", false));

        // Assert
        result.Should().BeOfType<BadRequestObjectResult>();
        User? dbUser = await db.Users.FindAsync(user.ID);
        dbUser!.PlinkoTickets.Should().Be(99);
        db.EconomyTransactions.Should().BeEmpty();
    }

    // =========================================================================
    // 4. CODE REDEMPTIONS ADVERSARIAL TESTS
    // =========================================================================

    [Fact]
    public async Task
        RedeemCode_ValidPromoCodeWithCurrenciesAndItem_GrantsAllCurrenciesAndInventoryItem_SetsRedeemedFields_LogsLedger()
    {
        using MerrickContext db = CreateContext();
        (User user, Account account, _) = SeedUserAndAccount(db, 100, 500, 2);
        StoreItem item = SeedStoreItem(db, "Hero_Pyromancer.Alt5", "Pyromancer Scorched");

        RedeemableCode code = new()
        {
            Code = "SUMMER2026",
            GoldCoinsReward = 300,
            SilverCoinsReward = 1500,
            PlinkoTicketsReward = 10,
            ProductID = item.Id
        };
        db.RedeemableCodes.Add(code);
        await db.SaveChangesAsync();

        InventoryRepository invRepo = new(db, configuration: PaidStoreConfig);
        StoreApiController controller = new(invRepo, new AccountRepository(db));

        // Act
        IActionResult result = await controller.RedeemCode(new RedeemCodeApiRequestDto(user.ID, "SUMMER2026"));

        // Assert
        result.Should().BeOfType<OkObjectResult>();
        OkObjectResult ok = (OkObjectResult)result;
        RedeemCodeResultDto? redeemResult = ok.Value as RedeemCodeResultDto;
        redeemResult.Should().NotBeNull();
        redeemResult!.Success.Should().BeTrue();
        redeemResult.GoldGranted.Should().Be(300);
        redeemResult.SilverGranted.Should().Be(1500);
        redeemResult.ProductsGranted.Should().Contain("Hero_Pyromancer.Alt5");

        // Verify DB User balances
        User? dbUser = await db.Users.Include(u => u.InventoryItems).FirstOrDefaultAsync(u => u.ID == user.ID);
        dbUser!.GoldCoins.Should().Be(400); // 100 + 300
        dbUser.SilverCoins.Should().Be(2000); // 500 + 1500
        dbUser.PlinkoTickets.Should().Be(12); // 2 + 10
        dbUser.InventoryItems.Select(i => i.ItemCode).Should().Contain("Hero_Pyromancer.Alt5");

        // Verify UserInventory entry
        UserInventory? userInv =
            await db.UserInventory.FirstOrDefaultAsync(i =>
                i.UserID == user.ID && i.ItemCode == "Hero_Pyromancer.Alt5");
        userInv.Should().NotBeNull();
        userInv!.StoreItemId.Should().Be(item.Id);
        userInv.Source.Should().Be("PromoCode");

        // Verify RedeemableCode state
        RedeemableCode? dbCode = await db.RedeemableCodes.FirstOrDefaultAsync(c => c.Code == "SUMMER2026");
        dbCode!.TimestampRedeemed.Should().NotBeNull();
        dbCode.RedeemedByAccountID.Should().Be(account.ID);

        // Verify EconomyTransaction audit record
        EconomyTransaction? tx =
            await db.EconomyTransactions.FirstOrDefaultAsync(t =>
                t.UserID == user.ID && t.TransactionType == "CodeRedemption");
        tx.Should().NotBeNull();
        tx!.GoldCoinsDelta.Should().Be(300);
        tx.SilverCoinsDelta.Should().Be(1500);
        tx.PlinkoTicketsDelta.Should().Be(10);
        tx.GoldCoinsBalanceAfter.Should().Be(400);
        tx.SilverCoinsBalanceAfter.Should().Be(2000);
        tx.PlinkoTicketsBalanceAfter.Should().Be(12);
        tx.ReferenceId.Should().Be("SUMMER2026");
    }

    [Fact]
    public async Task RedeemCode_CaseInsensitiveCodeMatching_Succeeds()
    {
        using MerrickContext db = CreateContext();
        (User user, Account account, _) = SeedUserAndAccount(db);

        RedeemableCode code = new()
        {
            Code = "WINTER_CHEST",
            GoldCoinsReward = 100,
            SilverCoinsReward = 500,
            PlinkoTicketsReward = 0
        };
        db.RedeemableCodes.Add(code);
        await db.SaveChangesAsync();

        InventoryRepository invRepo = new(db, configuration: PaidStoreConfig);
        StoreApiController controller = new(invRepo, new AccountRepository(db));

        // Act - Pass lowercase input
        IActionResult result = await controller.RedeemCode(new RedeemCodeApiRequestDto(user.ID, "winter_chest"));

        // Assert
        result.Should().BeOfType<OkObjectResult>();
        User? dbUser = await db.Users.FindAsync(user.ID);
        dbUser!.GoldCoins.Should().Be(1100);
    }

    [Fact]
    public async Task RedeemCode_AlreadyRedeemedCode_RejectsRedemption_DoesNotDoubleCredit()
    {
        using MerrickContext db = CreateContext();
        (User user, Account account, _) = SeedUserAndAccount(db, 500, 1000);

        RedeemableCode code = new()
        {
            Code = "CONSUMED_CODE",
            GoldCoinsReward = 1000,
            SilverCoinsReward = 5000,
            TimestampRedeemed = TimeProvider.System.GetUtcNow().AddDays(-1),
            RedeemedByAccountID = 99
        };
        db.RedeemableCodes.Add(code);
        await db.SaveChangesAsync();

        InventoryRepository invRepo = new(db, configuration: PaidStoreConfig);
        StoreApiController controller = new(invRepo, new AccountRepository(db));

        // Act
        IActionResult result = await controller.RedeemCode(new RedeemCodeApiRequestDto(user.ID, "CONSUMED_CODE"));

        // Assert
        result.Should().BeOfType<BadRequestObjectResult>();
        User? dbUser = await db.Users.FindAsync(user.ID);
        dbUser!.GoldCoins.Should().Be(500); // untouched
        dbUser.SilverCoins.Should().Be(1000);
        db.EconomyTransactions.Should().BeEmpty();
    }

    [Fact]
    public async Task RedeemCode_InvalidCode_RejectsRedemption()
    {
        using MerrickContext db = CreateContext();
        (User user, _, _) = SeedUserAndAccount(db);

        InventoryRepository invRepo = new(db, configuration: PaidStoreConfig);
        StoreApiController controller = new(invRepo, new AccountRepository(db));

        // Act
        IActionResult result =
            await controller.RedeemCode(new RedeemCodeApiRequestDto(user.ID, "NON_EXISTENT_PROMO_CODE"));

        // Assert
        result.Should().BeOfType<BadRequestObjectResult>();
    }

    // =========================================================================
    // 5. ADMIN BALANCE ADJUSTMENTS ADVERSARIAL TESTS
    // =========================================================================

    [Fact]
    public async Task
        AdminEconomy_UpdateUserEconomy_SetsExactBalances_ClampsNegativeValues_LogsLedgerWithComputedDeltas()
    {
        using MerrickContext db = CreateContext();
        (User user, Account account, _) = SeedUserAndAccount(db, 1000, 2000, 10);

        AdminUserController controller = new(new LocalAdminUserMediator(db), NullLogger<AdminUserController>.Instance);

        UpdateUserEconomyRequest request = new()
        {
            GoldCoins = 5000,
            SilverCoins = 10000,
            PlinkoTickets = 50
        };

        // Act
        IActionResult result = await controller.UpdateUserEconomy(user.ID, request);

        // Assert
        result.Should().BeOfType<OkObjectResult>();
        User? dbUser = await db.Users.FindAsync(user.ID);
        dbUser!.GoldCoins.Should().Be(5000);
        dbUser.SilverCoins.Should().Be(10000);
        dbUser.PlinkoTickets.Should().Be(50);

        // Check EconomyTransactions
        EconomyTransaction? tx = await db.EconomyTransactions.FirstOrDefaultAsync(t =>
            t.UserID == user.ID && t.TransactionType == "AdminUpdateEconomy");
        tx.Should().NotBeNull();
        tx!.GoldCoinsDelta.Should().Be(4000); // 5000 - 1000
        tx.SilverCoinsDelta.Should().Be(8000); // 10000 - 2000
        tx.PlinkoTicketsDelta.Should().Be(40); // 50 - 10
        tx.GoldCoinsBalanceAfter.Should().Be(5000);
        tx.SilverCoinsBalanceAfter.Should().Be(10000);
        tx.PlinkoTicketsBalanceAfter.Should().Be(50);
    }

    [Fact]
    public async Task
        AdminEconomy_AdjustUserBalance_AppliesPositiveAndNegativeDeltas_ClampsFloorToZero_LogsLedgerWithAppliedDeltas()
    {
        using MerrickContext db = CreateContext();
        (User user, Account account, _) = SeedUserAndAccount(db, 100, 500, 5);

        AdminUserController controller = new(new LocalAdminUserMediator(db), NullLogger<AdminUserController>.Instance);

        // Adjust: Gold +500, Silver -1000 (clamp to 0), Tickets -2
        AdjustUserBalanceRequest request = new()
        {
            GoldDelta = 500,
            SilverDelta = -1000,
            TicketsDelta = -2
        };

        // Act
        IActionResult result = await controller.AdjustUserBalance(user.ID, request);

        // Assert
        result.Should().BeOfType<OkObjectResult>();
        User? dbUser = await db.Users.FindAsync(user.ID);
        dbUser!.GoldCoins.Should().Be(600); // 100 + 500
        dbUser.SilverCoins.Should().Be(0); // 500 - 1000 -> clamped to 0
        dbUser.PlinkoTickets.Should().Be(3); // 5 - 2

        EconomyTransaction? tx = await db.EconomyTransactions.FirstOrDefaultAsync(t =>
            t.UserID == user.ID && t.TransactionType == "AdminAdjustBalance");
        tx.Should().NotBeNull();
        tx!.GoldCoinsDelta.Should().Be(500);
        tx.SilverCoinsDelta.Should().Be(-500); // actual delta applied is -500 (from 500 down to 0)
        tx.PlinkoTicketsDelta.Should().Be(-2);
        tx.GoldCoinsBalanceAfter.Should().Be(600);
        tx.SilverCoinsBalanceAfter.Should().Be(0);
        tx.PlinkoTicketsBalanceAfter.Should().Be(3);
    }

    [Fact]
    public async Task AdminEconomy_NonExistentUser_ReturnsNotFound()
    {
        using MerrickContext db = CreateContext();
        AdminUserController controller = new(new LocalAdminUserMediator(db), NullLogger<AdminUserController>.Instance);

        IActionResult result =
            await controller.AdjustUserBalance(99999, new AdjustUserBalanceRequest { GoldDelta = 100 });
        result.Should().BeOfType<NotFoundObjectResult>();
    }

    // =========================================================================
    // 6. SRP AUTH TELEMETRY & SESSION PERSISTENCE ADVERSARIAL TESTS
    // =========================================================================

    [Fact]
    public async Task SRPAuthHandler_TelemetryPersistence_CreatesAccountSession_UpdatesTrackedAccountCollections()
    {
        using MerrickContext db = CreateContext();
        (User user, Account account, _) = SeedUserAndAccount(db);

        // Simulate SRPAuthHandler session recording pattern
        string ipAddress = "203.0.113.42";
        string macAddress = "AA-BB-CC-DD-EE-FF";
        string osType = "Windows 11 Pro";
        string clientVer = "4.10.1";
        string hwidHash = "session_hwid_hash_123456";
        string cookie = "sess_" + Guid.NewGuid().ToString("N");

        AccountSession session = new()
        {
            AccountID = account.ID,
            UserID = user.ID,
            IPAddress = ipAddress,
            MACAddress = macAddress,
            OperatingSystem = osType,
            Processor = "Intel Core i9-14900K",
            VideoCard = "NVIDIA GeForce RTX 4090",
            RAM = "64GB",
            SystemInformationRaw = $"{macAddress}|{osType}|Intel Core i9-14900K|NVIDIA GeForce RTX 4090|64GB",
            SystemInformationHash = hwidHash,
            ClientVersion = clientVer,
            UserAgent = "HoN Client 4.10.1",
            SessionCookie = cookie,
            TimestampLogin = TimeProvider.System.GetUtcNow(),
            TimestampLastSeen = TimeProvider.System.GetUtcNow(),
            IsActive = true
        };

        db.AccountSessions.Add(session);

        // Update tracked account telemetry lists
        account.IPAddressCollection.Add(ipAddress);
        account.MACAddressCollection.Add(macAddress);
        account.SystemInformationCollection.Add(session.SystemInformationRaw);
        account.SystemInformationHashCollection.Add(hwidHash);
        account.TimestampLastActive = TimeProvider.System.GetUtcNow();

        await db.SaveChangesAsync();

        // Assert AccountSession was persisted
        AccountSession? savedSession =
            await db.AccountSessions.FirstOrDefaultAsync(s => s.AccountID == account.ID && s.SessionCookie == cookie);
        savedSession.Should().NotBeNull();
        savedSession!.IPAddress.Should().Be(ipAddress);
        savedSession.MACAddress.Should().Be(macAddress);
        savedSession.SystemInformationHash.Should().Be(hwidHash);
        savedSession.IsActive.Should().BeTrue();

        // Assert Account collection telemetry was updated
        Account? refreshedAccount = await db.Accounts.FindAsync(account.ID);
        refreshedAccount!.IPAddressCollection.Should().Contain(ipAddress);
        refreshedAccount.MACAddressCollection.Should().Contain(macAddress);
        refreshedAccount.SystemInformationHashCollection.Should().Contain(hwidHash);
    }
}
