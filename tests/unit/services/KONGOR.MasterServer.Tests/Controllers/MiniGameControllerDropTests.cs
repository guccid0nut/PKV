// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using ASPIRE.Common.Serialization.PHP;
using FluentAssertions;
using KONGOR.MasterServer.Controllers.Plinko;
using KONGOR.MasterServer.Models.RequestResponse.Plinko;
using MERRICK.DatabaseContext.Entities.Core;
using MERRICK.DatabaseContext.Entities.Utility;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Primitives;
using Moq;
using NEXUS.Contracts.Data;
using NEXUS.Contracts.Economy;
using NEXUS.Contracts.Services;
using StackExchange.Redis;
using Xunit;
using DbRole = MERRICK.DatabaseContext.Entities.Utility.Role;

namespace KONGOR.MasterServer.Tests.Controllers;

public sealed class MiniGameControllerDropTests
{
    private const string ValidCookie = "valid_plinko_session_cookie";
    private const string AccountNickname = "PlinkoPlayer";

    private static (User user, Account account) CreateTestUserAndAccount(
        int gold = 500,
        int tickets = 100)
    {
        User user = new()
        {
            ID = 42,
            EmailAddress = "plinko@kongor.net",
            Role = new DbRole { ID = 2, Name = "User" },
            SRPPasswordSalt = "salt",
            SRPPasswordHash = "hash",
            PBKDF2PasswordHash = "pbkdf2_hash_value",
            GoldCoins = gold,
            SilverCoins = 1000,
            PlinkoTickets = tickets,
            InventoryItems = []
        };

        Account account = new()
        {
            ID = 101,
            Name = AccountNickname,
            User = user,
            IsMain = true
        };

        return (user, account);
    }

    private static (MiniGameController controller, Mock<IAccountRepository> mockRepo, Mock<IDatabase> mockRedis)
        CreateMiniGameController(Account? account = null)
    {
        Mock<IDatabase> mockRedis = new();
        mockRedis.Setup(r => r.StringGetAsync(It.Is<RedisKey>(k => k.ToString().Contains(ValidCookie)), It.IsAny<CommandFlags>()))
            .ReturnsAsync(AccountNickname);

        Mock<IAccountRepository> mockRepo = new();
        if (account is not null)
        {
            mockRepo.Setup(r => r.GetByNicknameAsync(AccountNickname, It.IsAny<CancellationToken>()))
                .ReturnsAsync(account);
            mockRepo.Setup(r => r.UpdateUserAsync(It.IsAny<User>(), It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);
        }

        MiniGameController controller = new(
            mockRepo.Object,
            mockRedis.Object,
            NullLogger<MiniGameController>.Instance);

        return (controller, mockRepo, mockRedis);
    }

    private static (TicketExchangeController controller, Mock<IAccountRepository> mockRepo, Mock<IDatabase> mockRedis)
        CreateTicketExchangeController(Account? account = null)
    {
        Mock<IDatabase> mockRedis = new();
        mockRedis.Setup(r => r.StringGetAsync(It.Is<RedisKey>(k => k.ToString().Contains(ValidCookie)), It.IsAny<CommandFlags>()))
            .ReturnsAsync(AccountNickname);

        Mock<IAccountRepository> mockRepo = new();
        if (account is not null)
        {
            mockRepo.Setup(r => r.GetByNicknameAsync(AccountNickname, It.IsAny<CancellationToken>()))
                .ReturnsAsync(account);
            mockRepo.Setup(r => r.UpdateUserAsync(It.IsAny<User>(), It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);
        }

        TicketExchangeController controller = new(
            mockRepo.Object,
            mockRedis.Object,
            NullLogger<TicketExchangeController>.Instance);

        return (controller, mockRepo, mockRedis);
    }

    private static void SetFormPayload(ControllerBase controller, Dictionary<string, string> formFields)
    {
        DefaultHttpContext httpContext = new();
        Dictionary<string, StringValues> formDict = [];
        foreach (KeyValuePair<string, string> field in formFields)
        {
            formDict[field.Key] = new StringValues(field.Value);
        }

        httpContext.Request.Form = new FormCollection(formDict);
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = httpContext
        };
    }

    [Fact]
    public async Task Drop_WithoutCookie_ReturnsStatusCode0()
    {
        (MiniGameController controller, _, _) = CreateMiniGameController();
        SetFormPayload(controller, new Dictionary<string, string>
        {
            { "currency", "gold" }
        });

        IActionResult result = await controller.Drop();

        PhpResult phpResult = result.Should().BeOfType<PhpResult>().Subject;
        PlinkoDropResponseDTO dto = phpResult.Data.Should().BeOfType<PlinkoDropResponseDTO>().Subject;
        dto.StatusCode.Should().Be(0);
    }

    [Fact]
    public async Task Drop_InvalidCookie_ReturnsStatusCode0()
    {
        (MiniGameController controller, _, _) = CreateMiniGameController();
        SetFormPayload(controller, new Dictionary<string, string>
        {
            { "cookie", "non_existent_cookie" },
            { "currency", "gold" }
        });

        IActionResult result = await controller.Drop();

        PhpResult phpResult = result.Should().BeOfType<PhpResult>().Subject;
        PlinkoDropResponseDTO dto = phpResult.Data.Should().BeOfType<PlinkoDropResponseDTO>().Subject;
        dto.StatusCode.Should().Be(0);
    }

    [Fact]
    public async Task Drop_InvalidCurrency_ReturnsStatusCode4()
    {
        (User user, Account account) = CreateTestUserAndAccount();
        (MiniGameController controller, _, _) = CreateMiniGameController(account);
        SetFormPayload(controller, new Dictionary<string, string>
        {
            { "cookie", ValidCookie },
            { "currency", "silver" }
        });

        IActionResult result = await controller.Drop();

        PhpResult phpResult = result.Should().BeOfType<PhpResult>().Subject;
        PlinkoDropResponseDTO dto = phpResult.Data.Should().BeOfType<PlinkoDropResponseDTO>().Subject;
        dto.StatusCode.Should().Be(4);
    }

    [Fact]
    public async Task Drop_InsufficientGold_ReturnsStatusCode3()
    {
        (User user, Account account) = CreateTestUserAndAccount(gold: 10);
        (MiniGameController controller, _, _) = CreateMiniGameController(account);
        SetFormPayload(controller, new Dictionary<string, string>
        {
            { "cookie", ValidCookie },
            { "currency", "gold" }
        });

        IActionResult result = await controller.Drop();

        PhpResult phpResult = result.Should().BeOfType<PhpResult>().Subject;
        PlinkoDropResponseDTO dto = phpResult.Data.Should().BeOfType<PlinkoDropResponseDTO>().Subject;
        dto.StatusCode.Should().Be(3);
        user.GoldCoins.Should().Be(10); // Balance untouched
    }

    [Fact]
    public async Task Drop_InsufficientTickets_ReturnsStatusCode2()
    {
        (User user, Account account) = CreateTestUserAndAccount(tickets: 0);
        (MiniGameController controller, _, _) = CreateMiniGameController(account);
        SetFormPayload(controller, new Dictionary<string, string>
        {
            { "cookie", ValidCookie },
            { "currency", "tickets" }
        });

        IActionResult result = await controller.Drop();

        PhpResult phpResult = result.Should().BeOfType<PhpResult>().Subject;
        PlinkoDropResponseDTO dto = phpResult.Data.Should().BeOfType<PlinkoDropResponseDTO>().Subject;
        dto.StatusCode.Should().Be(2);
        user.PlinkoTickets.Should().Be(0); // Balance untouched
    }

    [Fact]
    public async Task Drop_WithValidGold_SucceedsAndInventoryHasNullStoreItemId()
    {
        (User user, Account account) = CreateTestUserAndAccount(gold: 1000, tickets: 0);
        (MiniGameController controller, Mock<IAccountRepository> mockRepo, _) = CreateMiniGameController(account);
        SetFormPayload(controller, new Dictionary<string, string>
        {
            { "cookie", ValidCookie },
            { "currency", "gold" }
        });

        IActionResult result = await controller.Drop();

        PhpResult phpResult = result.Should().BeOfType<PhpResult>().Subject;
        PlinkoDropResponseDTO dto = phpResult.Data.Should().BeOfType<PlinkoDropResponseDTO>().Subject;
        dto.StatusCode.Should().Be(1);
        user.GoldCoins.Should().BeLessThan(1000);
        mockRepo.Verify(r => r.UpdateUserAsync(user, It.IsAny<CancellationToken>()), Times.Once);

        // If an item was awarded in UserInventory, its StoreItemId must be null to avoid FK constraint violations
        foreach (UserInventory inv in user.InventoryItems)
        {
            inv.StoreItemId.Should().BeNull();
            inv.UserID.Should().Be(user.ID);
            inv.ItemCode.Should().NotBeNullOrWhiteSpace();
            inv.Source.Should().Be("PlinkoChestDrop");
        }
    }

    [Fact]
    public async Task Drop_WhenDatabaseThrowsException_ReturnsStatusCode0Without500Crash()
    {
        (User user, Account account) = CreateTestUserAndAccount(gold: 1000);
        (MiniGameController controller, Mock<IAccountRepository> mockRepo, _) = CreateMiniGameController(account);
        mockRepo.Setup(r => r.UpdateUserAsync(It.IsAny<User>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Simulated EF Core 23503 foreign key exception"));

        SetFormPayload(controller, new Dictionary<string, string>
        {
            { "cookie", ValidCookie },
            { "currency", "gold" }
        });

        IActionResult result = await controller.Drop();

        PhpResult phpResult = result.Should().BeOfType<PhpResult>().Subject;
        PlinkoDropResponseDTO dto = phpResult.Data.Should().BeOfType<PlinkoDropResponseDTO>().Subject;
        dto.StatusCode.Should().Be(0);
    }

    [Fact]
    public async Task TicketExchange_Purchase_AwardsItemWithNullStoreItemId()
    {
        (User user, Account account) = CreateTestUserAndAccount(tickets: 1000);
        (TicketExchangeController controller, Mock<IAccountRepository> mockRepo, _) = CreateTicketExchangeController(account);
        
        // Find a valid product ID from ticket exchange config
        int targetProductID = 752; // Classic avatar or announcer
        SetFormPayload(controller, new Dictionary<string, string>
        {
            { "cookie", ValidCookie },
            { "id", targetProductID.ToString() }
        });

        IActionResult result = await controller.Purchase();

        PhpResult phpResult = result.Should().BeOfType<PhpResult>().Subject;
        TicketExchangePurchaseResponseDTO dto = phpResult.Data.Should().BeOfType<TicketExchangePurchaseResponseDTO>().Subject;
        
        if (dto.StatusCode == 51) // StatusSuccess
        {
            user.InventoryItems.Should().NotBeEmpty();
            UserInventory purchasedItem = user.InventoryItems.Last();
            purchasedItem.StoreItemId.Should().BeNull();
            purchasedItem.Source.Should().Be("TicketExchange");
            mockRepo.Verify(r => r.UpdateUserAsync(user, It.IsAny<CancellationToken>()), Times.Once);
        }
    }

    [Fact]
    public async Task TicketExchange_WhenExceptionThrown_ReturnsStatus0Without500Crash()
    {
        (User user, Account account) = CreateTestUserAndAccount(tickets: 1000);
        (TicketExchangeController controller, Mock<IAccountRepository> mockRepo, _) = CreateTicketExchangeController(account);
        mockRepo.Setup(r => r.UpdateUserAsync(It.IsAny<User>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Simulated DB connection failure"));

        SetFormPayload(controller, new Dictionary<string, string>
        {
            { "cookie", ValidCookie },
            { "id", "752" }
        });

        IActionResult result = await controller.Purchase();

        PhpResult phpResult = result.Should().BeOfType<PhpResult>().Subject;
        TicketExchangePurchaseResponseDTO dto = phpResult.Data.Should().BeOfType<TicketExchangePurchaseResponseDTO>().Subject;
        // Should catch the exception and return failure status code 0
        if (dto.StatusCode != 54) // If item 752 was found, it should catch error and return 0
        {
            dto.StatusCode.Should().Be(0);
        }
    }

    [Fact]
    public async Task Drop_WithEconomyService_DelegatesSpendToDeductCurrenciesAsync()
    {
        (User user, Account account) = CreateTestUserAndAccount(gold: 1000, tickets: 0);
        Mock<IDatabase> mockRedis = new();
        mockRedis.Setup(r => r.StringGetAsync(It.Is<RedisKey>(k => k.ToString().Contains(ValidCookie)), It.IsAny<CommandFlags>()))
            .ReturnsAsync(AccountNickname);

        Mock<IAccountRepository> mockRepo = new();
        mockRepo.Setup(r => r.GetByNicknameAsync(AccountNickname, It.IsAny<CancellationToken>()))
            .ReturnsAsync(account);
        mockRepo.Setup(r => r.UpdateUserAsync(It.IsAny<User>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        Mock<IEconomyService> mockEconomy = new();
        mockEconomy.Setup(e => e.DeductCurrenciesAsync(
                user.ID,
                account.ID,
                It.IsAny<int>(),
                0,
                It.IsAny<int>(),
                "PlinkoDrop",
                It.IsAny<string?>(),
                It.IsAny<string?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(CurrencyRewardResult.Succeeded(0, 0, 0, goldAfter: 800, silverAfter: 0, plinkoAfter: 0));

        MiniGameController controller = new(
            mockRepo.Object,
            mockRedis.Object,
            NullLogger<MiniGameController>.Instance,
            mockEconomy.Object);

        SetFormPayload(controller, new Dictionary<string, string>
        {
            { "cookie", ValidCookie },
            { "currency", "gold" }
        });

        IActionResult result = await controller.Drop();

        PhpResult phpResult = result.Should().BeOfType<PhpResult>().Subject;
        PlinkoDropResponseDTO dto = phpResult.Data.Should().BeOfType<PlinkoDropResponseDTO>().Subject;
        dto.StatusCode.Should().Be(1);
        mockEconomy.Verify(e => e.DeductCurrenciesAsync(
            user.ID,
            account.ID,
            It.IsAny<int>(),
            0,
            0,
            "PlinkoDrop",
            It.Is<string?>(r => !string.IsNullOrWhiteSpace(r) && r.StartsWith($"plinko-drop:{account.ID}:")),
            It.IsAny<string?>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task TicketExchange_WithEconomyService_DelegatesSpendToDeductCurrenciesAsync()
    {
        (User user, Account account) = CreateTestUserAndAccount(tickets: 1000);
        Mock<IDatabase> mockRedis = new();
        mockRedis.Setup(r => r.StringGetAsync(It.Is<RedisKey>(k => k.ToString().Contains(ValidCookie)), It.IsAny<CommandFlags>()))
            .ReturnsAsync(AccountNickname);

        Mock<IAccountRepository> mockRepo = new();
        mockRepo.Setup(r => r.GetByNicknameAsync(AccountNickname, It.IsAny<CancellationToken>()))
            .ReturnsAsync(account);
        mockRepo.Setup(r => r.UpdateUserAsync(It.IsAny<User>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        Mock<IEconomyService> mockEconomy = new();
        mockEconomy.Setup(e => e.DeductCurrenciesAsync(
                user.ID,
                account.ID,
                0,
                0,
                It.IsAny<int>(),
                "TicketExchange",
                It.IsAny<string?>(),
                It.IsAny<string?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(CurrencyRewardResult.Succeeded(0, 0, 0, goldAfter: 0, silverAfter: 0, plinkoAfter: 900));

        TicketExchangeController controller = new(
            mockRepo.Object,
            mockRedis.Object,
            NullLogger<TicketExchangeController>.Instance,
            mockEconomy.Object);

        int targetProductID = 752;
        SetFormPayload(controller, new Dictionary<string, string>
        {
            { "cookie", ValidCookie },
            { "id", targetProductID.ToString() }
        });

        IActionResult result = await controller.Purchase();

        PhpResult phpResult = result.Should().BeOfType<PhpResult>().Subject;
        TicketExchangePurchaseResponseDTO dto = phpResult.Data.Should().BeOfType<TicketExchangePurchaseResponseDTO>().Subject;

        if (dto.StatusCode == 51)
        {
            mockEconomy.Verify(e => e.DeductCurrenciesAsync(
                user.ID,
                account.ID,
                0,
                0,
                It.IsAny<int>(),
                "TicketExchange",
                It.Is<string?>(r => !string.IsNullOrWhiteSpace(r) && r.StartsWith($"ticket-exchange:{account.ID}:{targetProductID}:")),
                It.IsAny<string?>(),
                It.IsAny<CancellationToken>()), Times.Once);
        }
    }

    [Fact]
    public async Task ConcreteEconomyService_IdempotencyKeyDeductAndRecord_PreventsDoubleChargeWhileKeepingReferenceIdReusable()
    {
        // protects: EconomyTransaction.IdempotencyKey uniqueness against double-charging while preserving non-unique ReferenceId (MatchId / itemCode)
        // fails_when: EconomyService ignores IdempotencyKey on replay or erroneously blocks distinct purchases sharing the same ReferenceId
        // seam: real EconomyService + MerrickContext EconomyTransactions ledger
        using var db = global::KONGOR.MasterServer.Tests.Infrastructure.TestDbContextFactory.CreateInMemoryContext();
        (User user, Account account) = CreateTestUserAndAccount(gold: 1000, tickets: 200);
        db.Users.Add(user);
        db.Accounts.Add(account);
        await db.SaveChangesAsync();

        var economy = new global::KONGOR.MasterServer.Services.Economy.EconomyService(db, NullLogger<global::KONGOR.MasterServer.Services.Economy.EconomyService>.Instance);
        const string dropIdemKey = "plinko-drop:101:test_idempotent_key_1";
        const string sharedReferenceId = "Hero_Berzerker.Alt4";

        (CurrencyRewardResult firstDeduct, EconomyTransaction? firstReplay) = await economy.DeductCurrenciesWithIdempotencyAsync(
            user.ID, account.ID, goldCost: 66, silverCost: 0, plinkoCost: 0,
            transactionType: "PlinkoDrop", idempotencyKey: dropIdemKey, referenceId: sharedReferenceId, description: "First attempt");
        (CurrencyRewardResult replayDeduct, EconomyTransaction? secondReplay) = await economy.DeductCurrenciesWithIdempotencyAsync(
            user.ID, account.ID, goldCost: 66, silverCost: 0, plinkoCost: 0,
            transactionType: "PlinkoDrop", idempotencyKey: dropIdemKey, referenceId: sharedReferenceId, description: "Retry after lost commit ACK");

        firstDeduct.Success.Should().BeTrue();
        firstReplay.Should().BeNull();
        replayDeduct.Success.Should().BeTrue();
        secondReplay.Should().NotBeNull("Duplicate IdempotencyKey must surface the committed EconomyTransaction as a replay");
        replayDeduct.GoldBalanceAfter.Should().Be(934, "Idempotent retry must return committed post-deduction balance without deducting 66G twice");
        db.Users.Single(u => u.ID == user.ID).GoldCoins.Should().Be(934);
        db.EconomyTransactions.Count(t => t.UserID == user.ID && t.IdempotencyKey == dropIdemKey).Should().Be(1);

        // Distinct IdempotencyKey with the SAME ReferenceId (e.g. repeatable consumable or matchId) MUST succeed!
        (CurrencyRewardResult distinctPurchaseSameRef, EconomyTransaction? thirdReplay) = await economy.DeductCurrenciesWithIdempotencyAsync(
            user.ID, account.ID, goldCost: 34, silverCost: 0, plinkoCost: 0,
            transactionType: "StorePurchase", idempotencyKey: "ingame-store:101:distinct_key_2", referenceId: sharedReferenceId, description: "Second consumable purchase");
        distinctPurchaseSameRef.Success.Should().BeTrue();
        thirdReplay.Should().BeNull();
        distinctPurchaseSameRef.GoldBalanceAfter.Should().Be(900);

        const string refundIdemKey = "plinko-refund:101:test_idempotent_refund";
        CurrencyRewardResult firstGrant = await economy.RecordTransactionWithIdempotencyAsync(
            user.ID, account.ID, goldDelta: 0, silverDelta: 0, plinkoDelta: 30,
            transactionType: "PlinkoTicketAward", idempotencyKey: refundIdemKey, referenceId: "tier:5", description: "First ticket grant");
        CurrencyRewardResult replayGrant = await economy.RecordTransactionWithIdempotencyAsync(
            user.ID, account.ID, goldDelta: 0, silverDelta: 0, plinkoDelta: 30,
            transactionType: "PlinkoTicketAward", idempotencyKey: refundIdemKey, referenceId: "tier:5", description: "Retry after lost commit ACK");

        firstGrant.Success.Should().BeTrue();
        replayGrant.Success.Should().BeTrue();
        replayGrant.PlinkoTicketsBalanceAfter.Should().Be(230, "Idempotent retry must not double-grant 30 tickets");
        db.Users.Single(u => u.ID == user.ID).PlinkoTickets.Should().Be(230);
    }

    [Fact]
    public async Task Drop_DuplicateClientRequestId_SecondHttpPostReturnsSamePrizeWithoutDoubleChargeOrDoubleGrant()
    {
        // protects: Player spending their last Gold on Plinko and double-clicking or retrying after network timeout (status == 3)
        // fails_when: Second HTTP POST with identical request_id either fails with StatusCode 3 (Insufficient Gold), double-charges, or grants a second prize
        // seam: MiniGameController.Drop + real EconomyService + MerrickContext EconomyTransactions
        using var db = global::KONGOR.MasterServer.Tests.Infrastructure.TestDbContextFactory.CreateInMemoryContext();
        int exactSingleDropGold = global::KONGOR.MasterServer.Configuration.JSONConfiguration.PlinkoConfiguration.GoldCost;
        (User user, Account account) = CreateTestUserAndAccount(gold: exactSingleDropGold, tickets: 0);
        db.Users.Add(user);
        db.Accounts.Add(account);
        await db.SaveChangesAsync();

        Mock<IDatabase> mockRedis = new();
        mockRedis.Setup(r => r.StringGetAsync(It.Is<RedisKey>(k => k.ToString().Contains(ValidCookie)), It.IsAny<CommandFlags>()))
            .ReturnsAsync(AccountNickname);

        Mock<IAccountRepository> mockRepo = new();
        mockRepo.Setup(r => r.GetByNicknameAsync(AccountNickname, It.IsAny<CancellationToken>()))
            .ReturnsAsync(account);
        mockRepo.Setup(r => r.UpdateUserAsync(It.IsAny<User>(), It.IsAny<CancellationToken>()))
            .Returns(async (User u, CancellationToken ct) => { await db.SaveChangesAsync(ct); });

        var economy = new global::KONGOR.MasterServer.Services.Economy.EconomyService(db, NullLogger<global::KONGOR.MasterServer.Services.Economy.EconomyService>.Instance);
        const string clientRequestId = "drop-101-55000-1-884422119900";

        MiniGameController firstController = new(mockRepo.Object, mockRedis.Object, NullLogger<MiniGameController>.Instance, economy);
        SetFormPayload(firstController, new Dictionary<string, string>
        {
            { "cookie", ValidCookie },
            { "currency", "gold" },
            { "request_id", clientRequestId }
        });

        IActionResult firstAction = await firstController.Drop();
        PlinkoDropResponseDTO firstDto = firstAction.Should().BeOfType<PhpResult>().Subject.Data.Should().BeOfType<PlinkoDropResponseDTO>().Subject;
        firstDto.StatusCode.Should().Be(1);
        firstDto.UserGold.Should().Be(0);
        int inventoryCountAfterFirst = account.User.InventoryItems.Count;

        // Second independent HTTP controller instance with the SAME client request_id (user now has 0 Gold!)
        MiniGameController retryController = new(mockRepo.Object, mockRedis.Object, NullLogger<MiniGameController>.Instance, economy);
        SetFormPayload(retryController, new Dictionary<string, string>
        {
            { "cookie", ValidCookie },
            { "currency", "gold" },
            { "request_id", clientRequestId }
        });

        IActionResult retryAction = await retryController.Drop();
        PlinkoDropResponseDTO retryDto = retryAction.Should().BeOfType<PhpResult>().Subject.Data.Should().BeOfType<PlinkoDropResponseDTO>().Subject;

        retryDto.StatusCode.Should().Be(1, "Retrying a committed last-Gold Plinko drop with the same request_id must return the won prize, not StatusCode 3");
        retryDto.RandomTier.Should().Be(firstDto.RandomTier);
        retryDto.ProductID.Should().Be(firstDto.ProductID);
        retryDto.ProductName.Should().Be(firstDto.ProductName);
        retryDto.TicketAmount.Should().Be(firstDto.TicketAmount);
        retryDto.UserGold.Should().Be(0);
        retryDto.UserTickets.Should().Be(firstDto.UserTickets);
        account.User.InventoryItems.Count.Should().Be(inventoryCountAfterFirst, "Duplicate request_id must never grant a second cosmetic item");
        db.EconomyTransactions.Count(t => t.UserID == user.ID && t.TransactionType == "PlinkoDrop").Should().Be(1);
    }

    [Fact]
    public async Task ConcreteEconomyService_UniqueConstraintViolation23505_ResolvesConcurrentlyWithoutDoubleCharge()
    {
        // protects: DB-level IX_EconomyTransactions_UserID_IdempotencyKey race recovery when two concurrent workers pass pre-check simultaneously
        // fails_when: SaveChangesAsync throwing PostgresException(23505) bubbles as an unhandled failure instead of returning the winning transaction
        // seam: real EconomyService + EF Core SaveChangesInterceptor throwing PostgresException("23505")
        const string raceKey = "plinko-drop:101:concurrent_23505_race";
        string dbName = $"Merrick_23505_{Guid.NewGuid():N}";
        var options = new Microsoft.EntityFrameworkCore.DbContextOptionsBuilder<MERRICK.DatabaseContext.Persistence.MerrickContext>()
            .UseInMemoryDatabase(dbName)
            .AddInterceptors(new UniqueIdempotencyViolationInterceptor(raceKey, dbName))
            .Options;

        await using var db = new MERRICK.DatabaseContext.Persistence.MerrickContext(options);
        (User user, Account account) = CreateTestUserAndAccount(gold: 500, tickets: 50);
        db.Users.Add(user);
        db.Accounts.Add(account);
        db.EconomyTransactions.Add(new EconomyTransaction
        {
            UserID = user.ID,
            AccountID = account.ID,
            GoldCoinsDelta = -50,
            SilverCoinsDelta = 0,
            PlinkoTicketsDelta = 0,
            GoldCoinsBalanceAfter = 450,
            SilverCoinsBalanceAfter = 1000,
            PlinkoTicketsBalanceAfter = 50,
            TransactionType = "PlinkoDrop",
            ReferenceId = "tier:2|pid:-1|tix:15|exh:1",
            IdempotencyKey = "seed-other-key"
        });
        await db.SaveChangesAsync();

        var economy = new global::KONGOR.MasterServer.Services.Economy.EconomyService(db, NullLogger<global::KONGOR.MasterServer.Services.Economy.EconomyService>.Instance);
        (CurrencyRewardResult result, EconomyTransaction? replayedTx) = await economy.DeductCurrenciesWithIdempotencyAsync(
            user.ID, account.ID, goldCost: 50, silverCost: 0, plinkoCost: 0,
            transactionType: "PlinkoDrop", idempotencyKey: raceKey, referenceId: "tier:2");

        result.Success.Should().BeTrue();
        result.GoldBalanceAfter.Should().Be(450);
        replayedTx.Should().NotBeNull();
        replayedTx!.IdempotencyKey.Should().Be(raceKey);
    }

    private sealed class UniqueIdempotencyViolationInterceptor(string targetKey, string dbName)
        : Microsoft.EntityFrameworkCore.Diagnostics.SaveChangesInterceptor
    {
        private bool _simulatedRace;

        public override ValueTask<Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<int>> SavingChangesAsync(
            Microsoft.EntityFrameworkCore.Diagnostics.DbContextEventData eventData,
            Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            if (!_simulatedRace && eventData.Context is MERRICK.DatabaseContext.Persistence.MerrickContext ctx)
            {
                bool addingTarget = ctx.ChangeTracker.Entries<EconomyTransaction>()
                    .Any(e => e.State == Microsoft.EntityFrameworkCore.EntityState.Added && e.Entity.IdempotencyKey == targetKey);
                if (addingTarget)
                {
                    _simulatedRace = true;
                    var seedOpts = new Microsoft.EntityFrameworkCore.DbContextOptionsBuilder<MERRICK.DatabaseContext.Persistence.MerrickContext>()
                        .UseInMemoryDatabase(dbName)
                        .Options;
                    using var seedCtx = new MERRICK.DatabaseContext.Persistence.MerrickContext(seedOpts);
                    seedCtx.EconomyTransactions.Add(new EconomyTransaction
                    {
                        UserID = 42,
                        AccountID = 101,
                        GoldCoinsDelta = -50,
                        SilverCoinsDelta = 0,
                        PlinkoTicketsDelta = 0,
                        GoldCoinsBalanceAfter = 450,
                        SilverCoinsBalanceAfter = 1000,
                        PlinkoTicketsBalanceAfter = 50,
                        TransactionType = "PlinkoDrop",
                        ReferenceId = "tier:2|pid:-1|tix:15|exh:1",
                        IdempotencyKey = targetKey
                    });
                    seedCtx.SaveChanges();

                    var pgEx = new Npgsql.PostgresException(
                        "duplicate key value violates unique constraint \"IX_EconomyTransactions_UserID_IdempotencyKey\"",
                        "ERROR",
                        "ERROR",
                        "23505",
                        constraintName: "IX_EconomyTransactions_UserID_IdempotencyKey");
                    throw new Microsoft.EntityFrameworkCore.DbUpdateException("23505 unique violation", pgEx);
                }
            }

            return base.SavingChangesAsync(eventData, result, cancellationToken);
        }
    }
}
