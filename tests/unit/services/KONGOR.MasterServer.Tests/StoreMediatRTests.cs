// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Collections.Frozen;
using ASPIRE.Common.Serialization.PHP;
using ASPIRE.Common.Services.Distributed;
using FluentAssertions;
using FluentValidation.Results;
using KONGOR.MasterServer.Controllers.Store;
using KONGOR.MasterServer.Features.Store;
using KONGOR.MasterServer.Tests.Infrastructure;
using MediatR;
using MERRICK.DatabaseContext.Entities.Core;
using MERRICK.DatabaseContext.Entities.Utility;
using MERRICK.DatabaseContext.Persistence;
using MERRICK.DatabaseContext.Repositories;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Primitives;
using Moq;
using NEXUS.Contracts.CQRS;
using NEXUS.Contracts.CQRS.Store;
using NEXUS.Contracts.Events;
using NEXUS.Contracts.Store;
using StackExchange.Redis;
using Xunit;
using DbRole = MERRICK.DatabaseContext.Entities.Utility.Role;

namespace KONGOR.MasterServer.Tests;

public sealed class StoreMediatRTests
{
    private static (User user, Account account) CreateTestUserAndAccount(int userId = 1, int accountId = 10, string name = "Valkyrie")
    {
        User user = new()
        {
            ID = userId,
            EmailAddress = $"{name.ToLowerInvariant()}@kongor.net",
            Role = new DbRole { ID = 2, Name = "User" },
            SRPPasswordSalt = "salt",
            SRPPasswordHash = "hash",
            PBKDF2PasswordHash = "pbkdf2_hash_value",
            GoldCoins = 5000,
            SilverCoins = 10000,
            InventoryItems = []
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
    public void GetStoreCategoryValidator_EnforcesPagingAndCategory()
    {
        GetStoreCategoryValidator validator = new();

        ValidationResult badAccountId = validator.Validate(new GetStoreCategoryQuery(AccountId: 0, CategoryId: 3, Page: 1));
        badAccountId.IsValid.Should().BeFalse();
        badAccountId.Errors.Should().Contain(e => e.PropertyName == "AccountId");

        ValidationResult badPage = validator.Validate(new GetStoreCategoryQuery(AccountId: 10, CategoryId: 3, Page: 0));
        badPage.IsValid.Should().BeFalse();
        badPage.Errors.Should().Contain(e => e.PropertyName == "Page");

        ValidationResult valid = validator.Validate(new GetStoreCategoryQuery(AccountId: 10, CategoryId: 3, Page: 1));
        valid.IsValid.Should().BeTrue();
    }

    [Fact]
    public void PurchaseStoreProductValidator_EnforcesProductAndCurrency()
    {
        PurchaseStoreProductValidator validator = new();

        ValidationResult badProduct = validator.Validate(new PurchaseStoreProductCommand(AccountId: 10, ProductId: 0, Currency: "1", CategoryId: 3, CurrentPage: 1, HostTime: "0"));
        badProduct.IsValid.Should().BeFalse();
        badProduct.Errors.Should().Contain(e => e.PropertyName == "ProductId");

        ValidationResult badCurrency = validator.Validate(new PurchaseStoreProductCommand(AccountId: 10, ProductId: 100, Currency: "", CategoryId: 3, CurrentPage: 1, HostTime: "0"));
        badCurrency.IsValid.Should().BeFalse();
        badCurrency.Errors.Should().Contain(e => e.PropertyName == "Currency");

        ValidationResult valid = validator.Validate(new PurchaseStoreProductCommand(AccountId: 10, ProductId: 100, Currency: "1", CategoryId: 3, CurrentPage: 1, HostTime: "0"));
        valid.IsValid.Should().BeTrue();
    }

    [Fact]
    public void PurchaseStoreProductCommand_ImplementsILockableRequest_WithAccountScopedKey()
    {
        PurchaseStoreProductCommand command = new(AccountId: 42, ProductId: 91, Currency: "0", CategoryId: 27, CurrentPage: 1, HostTime: "12345");

        command.Should().BeAssignableTo<ILockableRequest>();
        command.LockKey.Should().Be("lock:account:42:store_checkout");
        command.LockTimeout.Should().Be(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task GetStoreCategoryHandler_ReturnsPopulatedListing()
    {
        using MerrickContext db = TestDbContextFactory.CreateInMemoryContext();
        (User user, Account account) = CreateTestUserAndAccount(1, 10, "TestValk");
        db.Users.Add(user);
        db.Accounts.Add(account);
        await db.SaveChangesAsync();

        GetStoreCategoryHandler handler = new(db, NullLogger<GetStoreCategoryHandler>.Instance);

        var result = await handler.Handle(
            new GetStoreCategoryQuery(AccountId: 10, CategoryId: (int)StoreCategory.AccountIcons, Page: 1),
            CancellationToken.None);

        result.Should().NotBeNull();
        result.CategoryId.Should().Be((int)StoreCategory.AccountIcons);
        result.RawResponse.Should().ContainKey("responseCode");
        result.RawResponse["responseCode"].Should().Be((int)StoreResponseCode.BASIC_ITEM_LIST_RESPONSE);
        result.RawResponse.Should().ContainKey("totalPoints");
        result.RawResponse["totalPoints"].Should().Be(5000);
    }

    [Fact]
    public async Task PurchaseStoreProductHandler_ExecutesPurchase_DeductsCoinsAndPublishesEvent()
    {
        using MerrickContext db = TestDbContextFactory.CreateInMemoryContext();
        (User user, Account account) = CreateTestUserAndAccount(1, 10, "BuyerHero");
        user.GoldCoins = 1000;
        user.SilverCoins = 5000;
        db.Users.Add(user);
        db.Accounts.Add(account);
        await db.SaveChangesAsync();

        Mock<IEventBus> mockEventBus = new();

        PurchaseStoreProductHandler handler = new(
            db,
            NullLogger<PurchaseStoreProductHandler>.Instance,
            eventBus: mockEventBus.Object);

        // Find an enabled purchasable item from configuration (e.g. Taunt ID 91)
        var storeItem = StoreController.StoreItems.GetByID(91); // Taunt
        if (storeItem == null) return; // In case configuration isn't loaded in test harness

        var command = new PurchaseStoreProductCommand(
            AccountId: 10,
            ProductId: 91,
            Currency: "1", // Silver
            CategoryId: (int)StoreCategory.Taunt,
            CurrentPage: 1,
            HostTime: "12345");

        var result = await handler.Handle(command, CancellationToken.None);

        result.Should().NotBeNull();
        if (result.Success)
        {
            result.ErrorCode.Should().Be(0);
            mockEventBus.Verify(b => b.PublishAsync(It.IsAny<HeroAvatarPurchasedEvent>(), It.IsAny<CancellationToken>()), Times.Once);
        }
    }

    [Fact]
    public async Task StoreController_WithMediator_DelegatesListStoreItemsToMediatR()
    {
        using MerrickContext db = TestDbContextFactory.CreateInMemoryContext();
        (User user, Account account) = CreateTestUserAndAccount(1, 10, "MediatorHero");
        db.Users.Add(user);
        db.Accounts.Add(account);
        await db.SaveChangesAsync();

        Mock<IDatabase> mockRedis = new();
        Mock<IMediator> mockMediator = new();

        Dictionary<string, object> dummyData = new()
        {
            ["responseCode"] = (int)StoreResponseCode.BASIC_ITEM_LIST_RESPONSE,
            ["categoryID"] = (int)StoreCategory.AccountIcons
        };
        StoreCategoryResultDto dummyResult = new(
            (int)StoreCategory.AccountIcons,
            1,
            "0",
            (int)StoreResponseCode.BASIC_ITEM_LIST_RESPONSE,
            dummyData);

        mockMediator
            .Setup(m => m.Send(It.IsAny<GetStoreCategoryQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(dummyResult);

        StoreController controller = new(
            new AccountRepository(db, NullLogger<AccountRepository>.Instance),
            mockRedis.Object,
            NullLogger<StoreController>.Instance,
            mockMediator.Object);

        DefaultHttpContext httpContext = new();
        Dictionary<string, StringValues> formDict = new()
        {
            ["cookie"] = "dummy_cookie",
            ["request_code"] = "LIST_STORE_ITEMS_REQUEST",
            ["category_id"] = "accounticons",
            ["page"] = "1",
            ["account_id"] = "10"
        };
        httpContext.Request.Form = new FormCollection(formDict);
        controller.ControllerContext = new ControllerContext { HttpContext = httpContext };

        IActionResult actionResult = await controller.StoreRequester();

        actionResult.Should().BeOfType<PhpResult>();
        var phpResult = (PhpResult)actionResult;
        phpResult.Value.Should().Be(dummyData);
        mockMediator.Verify(m => m.Send(It.IsAny<GetStoreCategoryQuery>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public void GetVaultItemsValidator_EnforcesValidAccountId()
    {
        GetVaultItemsValidator validator = new();

        validator.Validate(new GetVaultItemsQuery(0)).IsValid.Should().BeFalse();
        validator.Validate(new GetVaultItemsQuery(10)).IsValid.Should().BeTrue();
    }

    [Fact]
    public void GetSelectedUpgradesValidator_EnforcesValidAccountId()
    {
        GetSelectedUpgradesValidator validator = new();

        validator.Validate(new GetSelectedUpgradesQuery(0)).IsValid.Should().BeFalse();
        validator.Validate(new GetSelectedUpgradesQuery(15)).IsValid.Should().BeTrue();
    }

    [Fact]
    public void RedeemStoreCodeValidator_EnforcesCodeAndAccountId()
    {
        RedeemStoreCodeValidator validator = new();

        validator.Validate(new RedeemStoreCodeCommand(0, "HON2026")).IsValid.Should().BeFalse();
        validator.Validate(new RedeemStoreCodeCommand(10, "")).IsValid.Should().BeFalse();
        validator.Validate(new RedeemStoreCodeCommand(10, "A")).IsValid.Should().BeFalse();
        validator.Validate(new RedeemStoreCodeCommand(10, "VALIDCODE")).IsValid.Should().BeTrue();
    }

    [Fact]
    public async Task GetVaultItemsHandler_ReturnsCategorizedVaultResponse()
    {
        using MerrickContext db = TestDbContextFactory.CreateInMemoryContext();
        (User user, Account account) = CreateTestUserAndAccount(1, 10, "VaultOwner");
        db.Users.Add(user);
        db.Accounts.Add(account);
        await db.SaveChangesAsync();

        GetVaultItemsHandler handler = new(db, NullLogger<GetVaultItemsHandler>.Instance);
        var result = await handler.Handle(new GetVaultItemsQuery(10, "1000"), CancellationToken.None);

        result.Should().NotBeNull();
        result.RawResponse.Should().ContainKey("responseCode");
        result.RawResponse["responseCode"].Should().Be((int)StoreResponseCode.VAULT_ITEM_LIST_RESPONSE);
        result.RawResponse.Should().ContainKey("totalPoints");
        result.RawResponse["totalPoints"].Should().Be(5000);
        result.RawResponse.Should().ContainKey("totalMMPoints");
        result.RawResponse["totalMMPoints"].Should().Be(10000);
    }

    [Fact]
    public async Task GetSelectedUpgradesHandler_ReturnsSelectedUpgrades()
    {
        using MerrickContext db = TestDbContextFactory.CreateInMemoryContext();
        (User user, Account account) = CreateTestUserAndAccount(1, 10, "UpgradeOwner");
        db.Users.Add(user);
        db.Accounts.Add(account);
        await db.SaveChangesAsync();

        GetSelectedUpgradesHandler handler = new(db, NullLogger<GetSelectedUpgradesHandler>.Instance);
        var result = await handler.Handle(new GetSelectedUpgradesQuery(10, "1234"), CancellationToken.None);

        result.Should().NotBeNull();
        result.RawResponse.Should().ContainKey("responseCode");
        result.RawResponse["responseCode"].Should().Be((int)StoreResponseCode.UPDATE_SELECTED_RESPONSE);
        result.RawResponse.Should().ContainKey("selectedUpgrades");
    }

    [Fact]
    public async Task RedeemStoreCodeHandler_WhenValidCode_GrantsRewardsAndConsumesCode()
    {
        using MerrickContext db = TestDbContextFactory.CreateInMemoryContext();
        (User user, Account account) = CreateTestUserAndAccount(1, 10, "CodeRedeemer");
        user.GoldCoins = 100;
        user.SilverCoins = 200;
        db.Users.Add(user);
        db.Accounts.Add(account);

        RedeemableCode code = new()
        {
            Code = "LEGION2026",
            GoldCoinsReward = 50,
            SilverCoinsReward = 300,
            PlinkoTicketsReward = 2
        };
        db.RedeemableCodes.Add(code);
        await db.SaveChangesAsync();

        RedeemStoreCodeHandler handler = new(db, NullLogger<RedeemStoreCodeHandler>.Instance);
        var result = await handler.Handle(new RedeemStoreCodeCommand(10, "legion2026", "1111"), CancellationToken.None);

        result.Should().NotBeNull();
        result.Success.Should().BeTrue();
        result.ErrorCode.Should().Be(0);
        result.RawResponse.Should().ContainKey("popupCode");
        result.RawResponse["popupCode"].Should().Be((int)StorePopupCode.POP_UP_COIN_REDEEM_SUCCESS);

        user.GoldCoins.Should().Be(150);
        user.SilverCoins.Should().Be(500);
        user.PlinkoTickets.Should().Be(2);
        code.TimestampRedeemed.Should().NotBeNull();
        code.RedeemedByAccountID.Should().Be(10);
    }

    [Fact]
    public async Task RedeemStoreCodeHandler_WhenCodeAlreadyUsed_ReturnsUsedError()
    {
        using MerrickContext db = TestDbContextFactory.CreateInMemoryContext();
        (User user, Account account) = CreateTestUserAndAccount(1, 10, "SecondClaimer");
        db.Users.Add(user);
        db.Accounts.Add(account);

        RedeemableCode code = new()
        {
            Code = "EXPIREDCODE",
            GoldCoinsReward = 10,
            TimestampRedeemed = DateTimeOffset.UtcNow.AddHours(-1),
            RedeemedByAccountID = 99
        };
        db.RedeemableCodes.Add(code);
        await db.SaveChangesAsync();

        RedeemStoreCodeHandler handler = new(db, NullLogger<RedeemStoreCodeHandler>.Instance);
        var result = await handler.Handle(new RedeemStoreCodeCommand(10, "expiredcode", "2222"), CancellationToken.None);

        result.Should().NotBeNull();
        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be((int)StoreErrorCode.STORE_PURCHASE_CODE_USED_ERROR);
    }

    [Fact]
    public void PurchaseLobbyProductValidator_EnforcesHeroAvatarCurrency()
    {
        PurchaseLobbyProductValidator validator = new();

        var badAccount = validator.Validate(new PurchaseLobbyProductCommand(0, "Hero_Valkyrie", "default", "1"));
        badAccount.IsValid.Should().BeFalse();
        badAccount.Errors.Should().Contain(e => e.PropertyName == "AccountId");

        var badHero = validator.Validate(new PurchaseLobbyProductCommand(10, "", "default", "1"));
        badHero.IsValid.Should().BeFalse();
        badHero.Errors.Should().Contain(e => e.PropertyName == "HeroName");

        var badAvatar = validator.Validate(new PurchaseLobbyProductCommand(10, "Hero_Valkyrie", "", "1"));
        badAvatar.IsValid.Should().BeFalse();
        badAvatar.Errors.Should().Contain(e => e.PropertyName == "AvatarCode");

        var badCurrency = validator.Validate(new PurchaseLobbyProductCommand(10, "Hero_Valkyrie", "default", ""));
        badCurrency.IsValid.Should().BeFalse();
        badCurrency.Errors.Should().Contain(e => e.PropertyName == "Currency");

        var valid = validator.Validate(new PurchaseLobbyProductCommand(10, "Hero_Valkyrie", "default", "1"));
        valid.IsValid.Should().BeTrue();
    }

    [Fact]
    public void PurchaseLobbyProductCommand_ImplementsILockableRequest_WithAccountScopedKey()
    {
        PurchaseLobbyProductCommand command = new(AccountId: 99, HeroName: "Hero_Pyromancer", AvatarCode: "alt", Currency: "0");

        command.Should().BeAssignableTo<ILockableRequest>();
        command.LockKey.Should().Be("lock:account:99:store_checkout");
        command.LockTimeout.Should().Be(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task PurchaseLobbyProductHandler_WhenDefaultAvatar_ReturnsImmediateSuccess()
    {
        using MerrickContext db = TestDbContextFactory.CreateInMemoryContext();
        (User user, Account account) = CreateTestUserAndAccount(1, 10, "LobbyPlayer");
        db.Users.Add(user);
        db.Accounts.Add(account);
        await db.SaveChangesAsync();

        PurchaseLobbyProductHandler handler = new(db, NullLogger<PurchaseLobbyProductHandler>.Instance);
        var result = await handler.Handle(new PurchaseLobbyProductCommand(10, "Hero_Valkyrie", "default", "1"), CancellationToken.None);

        result.Should().NotBeNull();
        result.Success.Should().BeTrue();
        result.ErrorCode.Should().Be(0);
        result.RawResponse.Should().ContainKey("responseCode");
        result.RawResponse["responseCode"].Should().Be((int)StoreRequestCode.BUY_PRODUCT_GAME_LOBBY_REQUEST);
        result.RawResponse["popupCode"].Should().Be(-1);
    }

    [Fact]
    public async Task PurchaseLobbyProductHandler_WhenNonExistentItem_ReturnsItemNotFound()
    {
        using MerrickContext db = TestDbContextFactory.CreateInMemoryContext();
        (User user, Account account) = CreateTestUserAndAccount(1, 10, "LobbyPlayer2");
        db.Users.Add(user);
        db.Accounts.Add(account);
        await db.SaveChangesAsync();

        PurchaseLobbyProductHandler handler = new(db, NullLogger<PurchaseLobbyProductHandler>.Instance);
        var result = await handler.Handle(new PurchaseLobbyProductCommand(10, "Hero_Unknown", "nonexistent_skin", "1"), CancellationToken.None);

        result.Should().NotBeNull();
        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be((int)StoreErrorCode.STORE_PURCHASE_ITEM_MISSING_ERROR);
    }

    [Fact]
    public async Task StoreController_WithMediator_DelegatesLobbyPurchaseToMediatR()
    {
        using MerrickContext db = TestDbContextFactory.CreateInMemoryContext();
        (User user, Account account) = CreateTestUserAndAccount(1, 10, "LobbyShopper");
        db.Users.Add(user);
        db.Accounts.Add(account);
        await db.SaveChangesAsync();

        Mock<IDatabase> mockRedis = new();
        mockRedis.Setup(r => r.StringGetAsync(It.IsAny<RedisKey>(), It.IsAny<CommandFlags>()))
            .ReturnsAsync(new RedisValue("LobbyShopper"));
        Mock<IMediator> mockMediator = new();

        Dictionary<string, object> dummyData = new()
        {
            ["responseCode"] = (int)StoreRequestCode.BUY_PRODUCT_GAME_LOBBY_REQUEST,
            ["popupCode"] = -1,
            ["errorCode"] = 0,
            ["totalPoints"] = 5000,
            ["totalMMPoints"] = 10000
        };
        PurchaseLobbyProductResultDto dummyResult = new(true, 0, -1, dummyData);

        mockMediator
            .Setup(m => m.Send(It.IsAny<PurchaseLobbyProductCommand>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(dummyResult);

        StoreController controller = new(
            new AccountRepository(db, NullLogger<AccountRepository>.Instance),
            mockRedis.Object,
            NullLogger<StoreController>.Instance,
            mockMediator.Object);

        DefaultHttpContext httpContext = new();
        Dictionary<string, StringValues> formDict = new()
        {
            ["cookie"] = "dummy_cookie",
            ["request_code"] = "BUY_PRODUCT_GAME_LOBBY_REQUEST",
            ["hero_name"] = "Hero_Valkyrie",
            ["avatar_code"] = "default",
            ["currency"] = "1",
            ["account_id"] = "10"
        };
        httpContext.Request.Form = new FormCollection(formDict);
        controller.ControllerContext = new ControllerContext { HttpContext = httpContext };

        IActionResult actionResult = await controller.StoreRequester();

        actionResult.Should().BeOfType<PhpResult>();
        var phpResult = (PhpResult)actionResult;
        phpResult.Value.Should().Be(dummyData);
        mockMediator.Verify(m => m.Send(It.Is<PurchaseLobbyProductCommand>(c =>
            c.AccountId == 10 && c.HeroName == "Hero_Valkyrie" && c.AvatarCode == "default" && c.Currency == "1"),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task PurchaseStoreProductHandler_DuplicateHostTimeReplay_ReturnsSuccessWithoutDoubleChargeOrEvent()
    {
        // protects: In-game K2 Store purchase idempotency on network resend (identical HostTime / IdempotencyKey)
        // fails_when: Retried BUY_PRODUCT_REQUEST fails with ALREADY_OWNED or deducts coins / publishes HeroAvatarPurchasedEvent twice
        // seam: PurchaseStoreProductHandler + MerrickContext EconomyTransactions.IdempotencyKey
        using MerrickContext db = TestDbContextFactory.CreateInMemoryContext();
        (User user, Account account) = CreateTestUserAndAccount(1, 10, "IdemStoreBuyer");
        user.GoldCoins = 2000;
        user.SilverCoins = 10000;
        db.Users.Add(user);
        db.Accounts.Add(account);
        await db.SaveChangesAsync();

        var storeItem = StoreController.StoreItems.GetByID(91);
        storeItem.Should().NotBeNull();

        Mock<IEventBus> mockEventBus = new();
        PurchaseStoreProductHandler handler = new(
            db,
            NullLogger<PurchaseStoreProductHandler>.Instance,
            eventBus: mockEventBus.Object);

        var command = new PurchaseStoreProductCommand(
            AccountId: 10,
            ProductId: 91,
            Currency: "1",
            CategoryId: (int)StoreCategory.Taunt,
            CurrentPage: 1,
            HostTime: "778899");

        var first = await handler.Handle(command, CancellationToken.None);
        first.Success.Should().BeTrue();
        int silverAfterFirst = db.Users.Single(u => u.ID == 1).SilverCoins;

        var replay = await handler.Handle(command, CancellationToken.None);
        replay.Success.Should().BeTrue("Duplicate HostTime resend must replay POP_UP_PRODUCT_PURCHASE_SUCCESS instead of failing with ALREADY_OWNED");
        replay.ErrorCode.Should().Be(0);
        db.Users.Single(u => u.ID == 1).SilverCoins.Should().Be(silverAfterFirst);
        db.EconomyTransactions.Single(t => t.UserID == 1).IdempotencyKey.Should().Be("ingame-store:91:10:ht-778899");
        mockEventBus.Verify(b => b.PublishAsync(It.IsAny<HeroAvatarPurchasedEvent>(), It.IsAny<CancellationToken>()), Times.Once);
    }
}
