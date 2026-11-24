// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using ASPIRE.Common.Behaviors;
using ASPIRE.Common.Serialization.PHP;
using ASPIRE.Common.Services.Distributed;
using FluentAssertions;
using KONGOR.MasterServer.Controllers.Store;
using KONGOR.MasterServer.Tests.Infrastructure;
using MediatR;
using MERRICK.DatabaseContext.Entities.Core;
using MERRICK.DatabaseContext.Entities.Utility;
using MERRICK.DatabaseContext.Persistence;
using MERRICK.DatabaseContext.Repositories;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Primitives;
using Moq;
using NEXUS.Contracts.Data;
using NEXUS.Contracts.Events;
using StackExchange.Redis;
using Xunit;
using DbRole = MERRICK.DatabaseContext.Entities.Utility.Role;

namespace KONGOR.MasterServer.Tests.Controllers;

public sealed class StoreControllerTests
{
    private static MerrickContext CreateInMemoryDatabase()
    {
        return TestDbContextFactory.CreateInMemoryContext();
    }

    private static StoreController CreateController(
        MerrickContext db,
        Mock<IDatabase> mockRedis,
        Mock<IEventBus>? mockEventBus = null,
        Mock<IDistributedLockService>? mockLockService = null,
        Mock<IMediator>? mockMediator = null)
    {
        global::KONGOR.MasterServer.Services.Store.UpgradesResolutionService.UnlockAllItems = false;
        IAccountRepository accountRepo = new AccountRepository(db, NullLogger<AccountRepository>.Instance);

        IMediator mediator;
        if (mockMediator != null)
        {
            mediator = mockMediator.Object;
        }
        else
        {
            ServiceCollection services = new();
            services.AddSingleton(db);
            services.AddSingleton(accountRepo);
            services.AddSingleton(mockEventBus?.Object ?? Mock.Of<IEventBus>());
            IDistributedLockService lockService = mockLockService?.Object ?? new RedisRedlockService();
            services.AddSingleton(lockService);
            services.AddLogging();
            services.AddMediatR(cfg =>
            {
                cfg.RegisterServicesFromAssembly(typeof(StoreController).Assembly);
                cfg.AddBehavior(typeof(IPipelineBehavior<,>), typeof(DistributedLockBehavior<,>));
            });
            ServiceProvider sp = services.BuildServiceProvider();
            mediator = sp.GetRequiredService<IMediator>();
        }

        return new StoreController(
            accountRepo,
            mockRedis.Object,
            NullLogger<StoreController>.Instance,
            mediator);
    }

    private static void SetFormPayload(StoreController controller, Dictionary<string, string> formFields)
    {
        DefaultHttpContext httpContext = new();
        Dictionary<string, StringValues> formDict = [];
        foreach (KeyValuePair<string, string> field in formFields) formDict[field.Key] = new StringValues(field.Value);

        httpContext.Request.Form = new FormCollection(formDict);
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = httpContext
        };
    }

    private static (User user, Account account) CreateTestUserAndAccount(int userId = 1, int accountId = 10,
        string name = "HeroPlayer")
    {
        User user = new()
        {
            ID = userId,
            EmailAddress = $"{name.ToLowerInvariant()}@kongor.net",
            Role = new DbRole { ID = 2, Name = "User" },
            SRPPasswordSalt = "salt",
            SRPPasswordHash = "hash",
            PBKDF2PasswordHash = "pbkdf2_hash_value"
        };
        Account account = new()
        {
            ID = accountId,
            Name = name,
            User = user,
            IsMain = true
        };
        return (user, account);
    }

    [Fact]
    public async Task StoreRequester_WithoutCookie_ReturnsBadRequest()
    {
        // Arrange
        using MerrickContext db = CreateInMemoryDatabase();
        Mock<IDatabase> mockRedis = new();
        StoreController controller = CreateController(db, mockRedis);
        SetFormPayload(controller, []);

        // Act
        IActionResult result = await controller.StoreRequester();

        // Assert
        result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Trait("Category", "Manual")]
    [Fact]
    public async Task StoreRequester_WithInvalidCookie_ReturnsSessionError()
    {
        if (!NexusTestEnvironment.IsManualTestEnabled) return;
        // Arrange
        using MerrickContext db = CreateInMemoryDatabase();
        Mock<IDatabase> mockRedis = new();
        mockRedis.Setup(r => r.StringGetAsync(It.IsAny<RedisKey>(), It.IsAny<CommandFlags>()))
            .ReturnsAsync(RedisValue.Null);

        StoreController controller = CreateController(db, mockRedis);
        SetFormPayload(controller, new Dictionary<string, string>
        {
            { "cookie", "invalid_expired_cookie" }
        });

        // Act
        IActionResult result = await controller.StoreRequester();

        // Assert
        result.Should().BeOfType<PhpResult>();
    }

    [Fact]
    public async Task StoreRequester_MissingRequestCode_ReturnsBadRequest()
    {
        // Arrange
        using MerrickContext db = CreateInMemoryDatabase();
        Mock<IDatabase> mockRedis = new();
        mockRedis.Setup(r =>
                r.StringGetAsync(It.Is<RedisKey>(k => k.ToString().Contains("valid_cookie")), It.IsAny<CommandFlags>()))
            .ReturnsAsync("HeroPlayer");

        (User user, Account account) = CreateTestUserAndAccount();
        db.Users.Add(user);
        db.Accounts.Add(account);
        await db.SaveChangesAsync();

        StoreController controller = CreateController(db, mockRedis);
        SetFormPayload(controller, new Dictionary<string, string>
        {
            { "cookie", "valid_cookie" }
        });

        // Act
        IActionResult result = await controller.StoreRequester();

        // Assert
        result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task StoreRequester_AccountIdMismatch_ReturnsAccountInformationError()
    {
        // Arrange
        using MerrickContext db = CreateInMemoryDatabase();
        Mock<IDatabase> mockRedis = new();
        mockRedis.Setup(r =>
                r.StringGetAsync(It.Is<RedisKey>(k => k.ToString().Contains("valid_cookie")), It.IsAny<CommandFlags>()))
            .ReturnsAsync("HeroPlayer");

        (User user, Account account) = CreateTestUserAndAccount();
        db.Users.Add(user);
        db.Accounts.Add(account);
        await db.SaveChangesAsync();

        StoreController controller = CreateController(db, mockRedis);
        SetFormPayload(controller, new Dictionary<string, string>
        {
            { "cookie", "valid_cookie" },
            { "request_code", "1" }, // BUY_PRODUCT_REQUEST
            { "account_id", "999" } // Spoofed account ID mismatch
        });

        // Act
        IActionResult result = await controller.StoreRequester();

        // Assert
        result.Should().BeOfType<PhpResult>();
    }

    [Fact]
    public async Task PurchaseProduct_SufficientGold_DeductsBalanceAndCreatesAuditRecord()
    {
        // Arrange
        using MerrickContext db = CreateInMemoryDatabase();
        Mock<IDatabase> mockRedis = new();
        mockRedis.Setup(r => r.StringGetAsync(It.IsAny<RedisKey>(), It.IsAny<CommandFlags>()))
            .ReturnsAsync("HeroPlayer");

        (User user, Account account) = CreateTestUserAndAccount();
        user.GoldCoins = 1000;
        user.SilverCoins = 500;
        db.Users.Add(user);
        db.Accounts.Add(account);
        await db.SaveChangesAsync();

        StoreController controller = CreateController(db, mockRedis);
        SetFormPayload(controller, new Dictionary<string, string>
        {
            { "cookie", "valid_cookie" },
            { "request_code", "4" }, // ATTEMPT_PURCHASE_PRODUCT_REQUEST
            { "product_id", "15" }, // Female Pyromancer, 400 Gold
            { "currency", "0" }, // Gold
            { "category_id", "1" },
            { "page", "1" },
            { "hostTime", "123456" }
        });

        // Act
        IActionResult result = await controller.StoreRequester();

        // Assert
        result.Should().BeOfType<PhpResult>();
        User updatedUser = await db.Users.Include(u => u.InventoryItems).Include(u => u.EconomyTransactions)
            .SingleAsync(u => u.ID == user.ID);
        updatedUser.GoldCoins.Should().Be(600); // 1000 - 400
        updatedUser.InventoryItems.Should().ContainSingle(i => i.ItemCode == "aa.Hero_Pyromancer.Female");
        updatedUser.EconomyTransactions.Should()
            .ContainSingle(t => t.GoldCoinsDelta == -400 && t.GoldCoinsBalanceAfter == 600);
    }

    [Fact]
    public async Task PurchaseProduct_InsufficientGold_ReturnsPointErrorWithoutModifyingBalance()
    {
        // Arrange
        using MerrickContext db = CreateInMemoryDatabase();
        Mock<IDatabase> mockRedis = new();
        mockRedis.Setup(r => r.StringGetAsync(It.IsAny<RedisKey>(), It.IsAny<CommandFlags>()))
            .ReturnsAsync("HeroPlayer");

        (User user, Account account) = CreateTestUserAndAccount();
        user.GoldCoins = 200; // Needs 400 for item 15
        db.Users.Add(user);
        db.Accounts.Add(account);
        await db.SaveChangesAsync();

        StoreController controller = CreateController(db, mockRedis);
        SetFormPayload(controller, new Dictionary<string, string>
        {
            { "cookie", "valid_cookie" },
            { "request_code", "4" }, // ATTEMPT_PURCHASE_PRODUCT_REQUEST
            { "product_id", "15" }, // Female Pyromancer, 400 Gold
            { "currency", "0" }, // Gold
            { "category_id", "1" },
            { "page", "1" },
            { "hostTime", "123456" }
        });

        // Act
        IActionResult result = await controller.StoreRequester();

        // Assert
        result.Should().BeOfType<PhpResult>();
        User updatedUser = await db.Users.Include(u => u.InventoryItems).Include(u => u.EconomyTransactions)
            .SingleAsync(u => u.ID == user.ID);
        updatedUser.GoldCoins.Should().Be(200); // Unchanged
        updatedUser.InventoryItems.Should().BeEmpty();
        updatedUser.EconomyTransactions.Should().BeEmpty();
    }

    [Fact]
    public async Task PurchaseProduct_AlreadyOwned_ReturnsAlreadyOwnedWithoutDeduction()
    {
        // Arrange
        using MerrickContext db = CreateInMemoryDatabase();
        Mock<IDatabase> mockRedis = new();
        mockRedis.Setup(r => r.StringGetAsync(It.IsAny<RedisKey>(), It.IsAny<CommandFlags>()))
            .ReturnsAsync("HeroPlayer");

        (User user, Account account) = CreateTestUserAndAccount();
        user.GoldCoins = 1000;
        user.InventoryItems.Add(new UserInventory { ItemCode = "aa.Hero_Pyromancer.Female", Quantity = 1, IsPermanent = true });
        db.Users.Add(user);
        db.Accounts.Add(account);
        await db.SaveChangesAsync();

        StoreController controller = CreateController(db, mockRedis);
        SetFormPayload(controller, new Dictionary<string, string>
        {
            { "cookie", "valid_cookie" },
            { "request_code", "4" }, // ATTEMPT_PURCHASE_PRODUCT_REQUEST
            { "product_id", "15" }, // Female Pyromancer, 400 Gold
            { "currency", "0" }, // Gold
            { "category_id", "1" },
            { "page", "1" },
            { "hostTime", "123456" }
        });

        // Act
        IActionResult result = await controller.StoreRequester();

        // Assert
        result.Should().BeOfType<PhpResult>();
        User updatedUser = await db.Users.Include(u => u.EconomyTransactions).SingleAsync(u => u.ID == user.ID);
        updatedUser.GoldCoins.Should().Be(1000); // Unchanged
        updatedUser.EconomyTransactions.Should().BeEmpty();
    }

    [Trait("Category", "Manual")]
    [Fact]
    public async Task RedeemCode_ValidUnusedCode_AwardsCurrencyAndMarksRedeemed()
    {
        if (!NexusTestEnvironment.IsManualTestEnabled) return;
        // Arrange
        using MerrickContext db = CreateInMemoryDatabase();
        Mock<IDatabase> mockRedis = new();
        mockRedis.Setup(r => r.StringGetAsync(It.IsAny<RedisKey>(), It.IsAny<CommandFlags>()))
            .ReturnsAsync("HeroPlayer");

        (User user, Account account) = CreateTestUserAndAccount();
        user.GoldCoins = 100;
        user.SilverCoins = 50;
        db.Users.Add(user);
        db.Accounts.Add(account);

        RedeemableCode promoCode = new()
        {
            Code = "FREEGOLD100",
            GoldCoinsReward = 100,
            SilverCoinsReward = 200,
            PlinkoTicketsReward = 1
        };
        db.RedeemableCodes.Add(promoCode);
        await db.SaveChangesAsync();

        StoreController controller = CreateController(db, mockRedis);
        SetFormPayload(controller, new Dictionary<string, string>
        {
            { "cookie", "valid_cookie" },
            { "request_code", "10" }, // REDEEM_CODE_REQUEST
            { "code", "FREEGOLD100" },
            { "hostTime", "123456" }
        });

        // Act
        IActionResult result = await controller.StoreRequester();

        // Assert
        result.Should().BeOfType<PhpResult>();
        User updatedUser = await db.Users.Include(u => u.EconomyTransactions).SingleAsync(u => u.ID == user.ID);
        updatedUser.GoldCoins.Should().Be(200);
        updatedUser.SilverCoins.Should().Be(250);
        updatedUser.PlinkoTickets.Should().Be(1);

        RedeemableCode codeInDb = await db.RedeemableCodes.SingleAsync(c => c.Code == "FREEGOLD100");
        codeInDb.RedeemedByAccountID.Should().Be(account.ID);
        codeInDb.TimestampRedeemed.Should().NotBeNull();

        updatedUser.EconomyTransactions.Should().ContainSingle(t =>
            t.TransactionType == "CodeRedemption" && t.GoldCoinsDelta == 100 && t.SilverCoinsDelta == 200);
    }

    [Fact]
    public async Task RedeemCode_AlreadyUsedCode_ReturnsCodeUsedErrorWithoutModifyingBalance()
    {
        // Arrange
        using MerrickContext db = CreateInMemoryDatabase();
        Mock<IDatabase> mockRedis = new();
        mockRedis.Setup(r => r.StringGetAsync(It.IsAny<RedisKey>(), It.IsAny<CommandFlags>()))
            .ReturnsAsync("HeroPlayer");

        (User user, Account account) = CreateTestUserAndAccount();
        user.GoldCoins = 100;
        db.Users.Add(user);
        db.Accounts.Add(account);

        RedeemableCode promoCode = new()
        {
            Code = "USEDCODE",
            GoldCoinsReward = 100,
            RedeemedByAccountID = 999,
            TimestampRedeemed = TimeProvider.System.GetUtcNow()
        };
        db.RedeemableCodes.Add(promoCode);
        await db.SaveChangesAsync();

        StoreController controller = CreateController(db, mockRedis);
        SetFormPayload(controller, new Dictionary<string, string>
        {
            { "cookie", "valid_cookie" },
            { "request_code", "10" }, // REDEEM_CODE_REQUEST
            { "code", "USEDCODE" },
            { "hostTime", "123456" }
        });

        // Act
        IActionResult result = await controller.StoreRequester();

        // Assert
        result.Should().NotBeNull();
        User updatedUser = await db.Users.SingleAsync(u => u.ID == user.ID);
        updatedUser.GoldCoins.Should().Be(100); // Unchanged
    }

    [Theory]
    [InlineData("Base")]
    [InlineData("Hero")]
    [InlineData("Default")]
    public async Task PurchaseProductInMatchLobby_DefaultAvatar_ReturnsImmediateSuccess(string avatarCode)
    {
        // Arrange
        using MerrickContext db = CreateInMemoryDatabase();
        Mock<IDatabase> mockRedis = new();
        mockRedis.Setup(r => r.StringGetAsync(It.IsAny<RedisKey>(), It.IsAny<CommandFlags>()))
            .ReturnsAsync("HeroPlayer");

        (User user, Account account) = CreateTestUserAndAccount();
        user.GoldCoins = 500;
        user.SilverCoins = 1000;
        db.Users.Add(user);
        db.Accounts.Add(account);
        await db.SaveChangesAsync();

        StoreController controller = CreateController(db, mockRedis);
        SetFormPayload(controller, new Dictionary<string, string>
        {
            { "cookie", "valid_cookie" },
            { "request_code", "8" }, // BUY_PRODUCT_GAME_LOBBY_REQUEST
            { "hero_name", "Hero_Hunter" },
            { "avatar_code", avatarCode },
            { "currency", "1" }
        });

        // Act
        IActionResult result = await controller.StoreRequester();

        // Assert
        result.Should().BeOfType<PhpResult>();
        PhpResult phpResult = (PhpResult)result;
        Dictionary<string, object> dict = (Dictionary<string, object>)phpResult.Data!;
        dict["errorCode"].Should().Be(0);
        dict["popupCode"].Should().Be(-1);
    }

    [Fact]
    public async Task StoreRequester_WithoutFormContentType_ReturnsBadRequestWithoutThrowing()
    {
        // Arrange
        using MerrickContext db = CreateInMemoryDatabase();
        Mock<IDatabase> mockRedis = new();
        StoreController controller = CreateController(db, mockRedis);

        DefaultHttpContext httpContext = new();
        httpContext.Request.ContentType = "application/json";
        controller.ControllerContext = new ControllerContext { HttpContext = httpContext };

        // Act
        IActionResult result = await controller.StoreRequester();

        // Assert
        result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task StoreRequester_ListSelectedUpgrades_ReturnsSelectedProductIDs()
    {
        // Arrange
        using MerrickContext db = CreateInMemoryDatabase();
        Mock<IDatabase> mockRedis = new();
        mockRedis.Setup(r => r.StringGetAsync(It.IsAny<RedisKey>(), It.IsAny<CommandFlags>()))
            .ReturnsAsync("VaultUser");

        (User user, Account account) = CreateTestUserAndAccount(userId: 15, accountId: 150, name: "VaultUser");
        db.Users.Add(user);
        db.Accounts.Add(account);
        account.SetSelectedStoreItems(["av.ASMR", "c.wareffort_courier"]);
        await db.SaveChangesAsync();

        StoreController controller = CreateController(db, mockRedis);
        SetFormPayload(controller, new Dictionary<string, string>
        {
            { "cookie", "valid_cookie" },
            { "request_code", "7" }, // LIST_PRODUCT_IDS_SELECTED_REQUEST
            { "hostTime", "12345" }
        });

        // Act
        IActionResult result = await controller.StoreRequester();

        // Assert
        result.Should().BeOfType<PhpResult>();
        PhpResult phpResult = (PhpResult)result;
        Dictionary<string, object> dict = (Dictionary<string, object>)phpResult.Data!;
        dict["responseCode"].Should().Be(7);
        dict["selectedUpgrades"].Should().NotBeNull();
        string selectedStr = (string)dict["selectedUpgrades"];
        List<string> ids = selectedStr.Split('|', StringSplitOptions.RemoveEmptyEntries).ToList();
        ids.Should().Contain("6014"); // Seductive ASMR Announcer
        ids.Should().Contain("2910"); // Gnome / Garena Courier
    }

    [Fact]
    public async Task StoreRequester_ViewVault_ReturnsSelectedUpgrades()
    {
        // Arrange
        using MerrickContext db = CreateInMemoryDatabase();
        Mock<IDatabase> mockRedis = new();
        mockRedis.Setup(r => r.StringGetAsync(It.IsAny<RedisKey>(), It.IsAny<CommandFlags>()))
            .ReturnsAsync("VaultUser2");

        (User user, Account account) = CreateTestUserAndAccount(userId: 16, accountId: 160, name: "VaultUser2");
        db.Users.Add(user);
        db.Accounts.Add(account);
        account.SetSelectedStoreItems(["av.ASMR"]);
        await db.SaveChangesAsync();

        StoreController controller = CreateController(db, mockRedis);
        SetFormPayload(controller, new Dictionary<string, string>
        {
            { "cookie", "valid_cookie" },
            { "request_code", "2" }, // LIST_VAULT_ITEMS_REQUEST
            { "hostTime", "12345" }
        });

        // Act
        IActionResult result = await controller.StoreRequester();

        // Assert
        result.Should().BeOfType<PhpResult>();
        PhpResult phpResult = (PhpResult)result;
        Dictionary<string, object> dict = (Dictionary<string, object>)phpResult.Data!;
        dict["responseCode"].Should().Be(2);
        dict["selectedUpgrades"].Should().NotBeNull();
        string selectedStr = (string)dict["selectedUpgrades"];
        selectedStr.Should().Contain("6014");
    }

    [Fact]
    public async Task StoreRequester_WhenDistributedLockUnavailable_RejectsConcurrentPurchase()
    {
        // Arrange
        using MerrickContext db = CreateInMemoryDatabase();
        Mock<IDatabase> mockRedis = new();
        Mock<IDistributedLockService> mockLockService = new();
        Mock<IDistributedLock> mockLock = new();

        mockLock.SetupGet(l => l.IsAcquired).Returns(false);
        mockLockService.Setup(s => s.AcquireLockAsync(
                It.Is<string>(r => r.Contains("store_checkout")),
                It.IsAny<TimeSpan>(),
                It.IsAny<TimeSpan>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(mockLock.Object);

        (User user, Account account) = CreateTestUserAndAccount(userId: 30, accountId: 300, name: "LockContentionUser");
        user.SilverCoins = 5000;
        user.GoldCoins = 5000;
        db.Users.Add(user);
        db.Accounts.Add(account);
        await db.SaveChangesAsync();

        mockRedis.Setup(r => r.StringGetAsync(It.IsAny<RedisKey>(), It.IsAny<CommandFlags>()))
            .ReturnsAsync("LockContentionUser");

        StoreController controller = CreateController(db, mockRedis, mockLockService: mockLockService);
        SetFormPayload(controller, new Dictionary<string, string>
        {
            { "cookie", "valid_cookie" },
            { "request_code", "4" }, // ATTEMPT_PURCHASE_PRODUCT_REQUEST
            { "product_id", "91" },
            { "currency", "1" },
            { "category_id", "1" },
            { "page", "1" },
            { "hostTime", "12345" }
        });

        // Act
        IActionResult result = await controller.StoreRequester();

        // Assert
        result.Should().BeOfType<PhpResult>();
        PhpResult phpResult = (PhpResult)result;
        Dictionary<string, object> dict = (Dictionary<string, object>)phpResult.Data!;
        dict["errorCode"].Should().Be(22); // STORE_PURCHASE_ERROR
        mockLockService.Verify(s => s.AcquireLockAsync(
            It.Is<string>(r => r.Contains("store_checkout")),
            It.IsAny<TimeSpan>(),
            It.IsAny<TimeSpan>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task StoreRequester_SandboxMode_SetsPricesZeroAndItemsOwned()
    {
        // Arrange
        using MerrickContext db = CreateInMemoryDatabase();
        Mock<IDatabase> mockRedis = new();
        mockRedis.Setup(r => r.StringGetAsync(It.IsAny<RedisKey>(), It.IsAny<CommandFlags>()))
            .ReturnsAsync("HeroPlayer");

        (User user, Account account) = CreateTestUserAndAccount();
        db.Users.Add(user);
        db.Accounts.Add(account);
        await db.SaveChangesAsync();

        StoreController controller = CreateController(db, mockRedis);
        global::KONGOR.MasterServer.Services.Store.UpgradesResolutionService.UnlockAllItems = true;

        SetFormPayload(controller, new Dictionary<string, string>
        {
            { "cookie", "valid_cookie" },
            { "request_code", "1" }, // GET_STORE_ITEMS_REQUEST
            { "category_id", "27" }, // Taunts
            { "page", "1" },
            { "hostTime", "123456" }
        });

        // Act
        IActionResult result = await controller.StoreRequester();

        // Assert
        result.Should().BeOfType<PhpResult>();
        PhpResult phpResult = (PhpResult)result;
        Dictionary<string, object> dict = (Dictionary<string, object>)phpResult.Data!;
        dict["tauntUnlocked"].Should().Be(1);
        dict["tauntUnlockCost"].Should().Be(0);
        dict["tauntUnlockCostMMP"].Should().Be(0);
        dict["customAccountIconCost"].Should().Be(0);
        dict["customAccountIconCostMMP"].Should().Be(0);

        string prices = (string)dict["productPrices"];
        prices.Split('|').Should().OnlyContain(p => p == "0");
        string mmpCosts = (string)dict["premium_mmp_cost"];
        mmpCosts.Split('|').Should().OnlyContain(p => p == "0");
    }

    [Fact]
    public async Task StoreRequester_FeaturedCategory_SwiftbladeBundleAndAvatars_FreeInSandbox()
    {
        // Arrange
        using MerrickContext db = CreateInMemoryDatabase();
        Mock<IDatabase> mockRedis = new();
        mockRedis.Setup(r => r.StringGetAsync(It.IsAny<RedisKey>(), It.IsAny<CommandFlags>()))
            .ReturnsAsync("HeroPlayer");

        (User user, Account account) = CreateTestUserAndAccount();
        db.Users.Add(user);
        db.Accounts.Add(account);
        await db.SaveChangesAsync();

        StoreController controller = CreateController(db, mockRedis);
        global::KONGOR.MasterServer.Services.Store.UpgradesResolutionService.UnlockAllItems = true;

        SetFormPayload(controller, new Dictionary<string, string>
        {
            { "cookie", "valid_cookie" },
            { "request_code", "1" }, // GET_STORE_ITEMS_REQUEST
            { "category_id", "68" }, // Featured
            { "page", "1" },
            { "hostTime", "123456" }
        });

        // Act
        IActionResult result = await controller.StoreRequester();

        // Assert
        result.Should().BeOfType<PhpResult>();
        PhpResult phpResult = (PhpResult)result;
        Dictionary<string, object> dict = (Dictionary<string, object>)phpResult.Data!;
        dict["productNames"].Should().Be("Hero_Hiro.Alt2|Hero_Hiro.Alt3");
        string bundleCosts = (string)dict["bundleCosts"];
        bundleCosts.Split('|').Should().OnlyContain(c => c == "0");
        string bundleOwned = (string)dict["bundleAlreadyOwned"];
        bundleOwned.Split('|').Should().OnlyContain(o => o == "1");
    }
}

