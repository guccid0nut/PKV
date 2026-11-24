// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ASPIRE.Common.Serialization.PHP;
using FluentAssertions;
using KONGOR.MasterServer.Handlers.ClientRequester;
using MERRICK.DatabaseContext.Entities.Core;
using MERRICK.DatabaseContext.Entities.Utility;
using NEXUS.Contracts.Data;
using NEXUS.Contracts.Domain;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Primitives;
using Moq;
using Xunit;

namespace KONGOR.MasterServer.Tests;

public sealed class SelectedUpgradesHandlerTests
{
    private static (Account account, User user) CreateTestAccount(string name = "guccidonut", int accountId = 1)
    {
        User user = new()
        {
            ID = accountId,
            EmailAddress = $"{name}@kongor.net",
            PBKDF2PasswordHash = "h",
            SRPPasswordHash = "h",
            SRPPasswordSalt = "s",
            DiscordId = "123456789012345678",
            InventoryItems =
            [
                new UserInventory { UserID = accountId, ItemCode = "ai.custom_icon:1", Quantity = 1, IsPermanent = true },
                new UserInventory { UserID = accountId, ItemCode = "cs.zephyr", Quantity = 1, IsPermanent = true },
                new UserInventory { UserID = accountId, ItemCode = "cs.kongor", Quantity = 1, IsPermanent = true },
                new UserInventory { UserID = accountId, ItemCode = "cc.white", Quantity = 1, IsPermanent = true }
            ],
            Role = new Role { ID = 2, Name = "User" }
        };

        Account account = new()
        {
            ID = accountId,
            Name = name,
            User = user,
            Type = AccountType.Normal,
            IsMain = true
        };

        return (account, user);
    }

    private static HttpContext CreateHttpContext(string cookie, Dictionary<string, string[]> formFields)
    {
        DefaultHttpContext context = new();
        Dictionary<string, StringValues> formDict = [];
        formDict["cookie"] = new StringValues(cookie);
        foreach (KeyValuePair<string, string[]> field in formFields)
        {
            formDict[field.Key] = new StringValues(field.Value);
        }

        context.Request.ContentType = "application/x-www-form-urlencoded";
        context.Request.Form = new FormCollection(formDict);
        return context;
    }

    [Fact]
    public async Task SelectedUpgradesHandler_EquippingChatSymbol_PreservesEquippedCustomIconAndCategoryIndependence()
    {
        (Account account, User user) = CreateTestAccount();
        account.SetSelectedStoreItems(["ai.custom_icon:1", "cc.white"]);

        Mock<IAccountRepository> mockAccountRepo = new();
        Mock<ISessionRepository> mockSessionRepo = new();

        string cookie = "test_cookie_123";
        mockSessionRepo
            .Setup(s => s.GetAccountNameForSessionCookieAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(account.Name);

        mockAccountRepo
            .Setup(a => a.GetByNicknameAsync(account.Name, It.IsAny<CancellationToken>()))
            .ReturnsAsync(account);



        SelectedUpgradesHandler handler = new(
            mockAccountRepo.Object,
            mockSessionRepo.Object,
            NullLogger<SelectedUpgradesHandler>.Instance);

        // Equip cs.zephyr while ai.custom_icon:1 is already equipped
        HttpContext httpContext = CreateHttpContext(cookie, new Dictionary<string, string[]>
        {
            ["selected_upgrades[]"] = ["cs.zephyr"]
        });

        IActionResult result = await handler.HandleRequestAsync(httpContext);

        result.Should().BeOfType<PhpResult>();
        account.SelectedStoreItemCodes.Should().Contain("ai.custom_icon:1", "Account icon must be preserved when equipping a chat symbol");
        account.SelectedStoreItemCodes.Should().Contain(x => x.Equals("cs.zephyr", StringComparison.OrdinalIgnoreCase), "Chat symbol must be equipped");
        account.SelectedStoreItemCodes.Should().Contain("cc.white", "Chat name color must remain intact");
    }

    [Fact]
    public async Task SelectedUpgradesHandler_EquippingCustomIcon_PreservesEquippedChatSymbol()
    {
        (Account account, User user) = CreateTestAccount();
        account.SetSelectedStoreItems(["cs.zephyr", "cc.white"]);

        Mock<IAccountRepository> mockAccountRepo = new();
        Mock<ISessionRepository> mockSessionRepo = new();

        string cookie = "test_cookie_456";
        mockSessionRepo
            .Setup(s => s.GetAccountNameForSessionCookieAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(account.Name);

        mockAccountRepo
            .Setup(a => a.GetByNicknameAsync(account.Name, It.IsAny<CancellationToken>()))
            .ReturnsAsync(account);



        SelectedUpgradesHandler handler = new(
            mockAccountRepo.Object,
            mockSessionRepo.Object,
            NullLogger<SelectedUpgradesHandler>.Instance);

        // Equip custom_icon:1 while cs.zephyr is already equipped
        HttpContext httpContext = CreateHttpContext(cookie, new Dictionary<string, string[]>
        {
            ["selected_upgrades[]"] = ["custom_icon:1"]
        });

        IActionResult result = await handler.HandleRequestAsync(httpContext);

        result.Should().BeOfType<PhpResult>();
        account.SelectedStoreItemCodes.Should().Contain("cs.zephyr", "Chat symbol must be preserved when equipping custom icon");
        account.SelectedStoreItemCodes.Should().Contain("ai.custom_icon:1", "Custom account icon must be equipped with ai. prefix");
        account.SelectedStoreItemCodes.Should().Contain("cc.white", "Chat name color must remain intact");
    }

    [Fact]
    public async Task SelectedUpgradesHandler_EquippingNewChatSymbol_ReplacesOldSymbolOnly()
    {
        (Account account, User user) = CreateTestAccount();
        account.SetSelectedStoreItems(["ai.custom_icon:1", "cs.zephyr", "cc.white"]);

        Mock<IAccountRepository> mockAccountRepo = new();
        Mock<ISessionRepository> mockSessionRepo = new();

        string cookie = "test_cookie_789";
        mockSessionRepo
            .Setup(s => s.GetAccountNameForSessionCookieAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(account.Name);

        mockAccountRepo
            .Setup(a => a.GetByNicknameAsync(account.Name, It.IsAny<CancellationToken>()))
            .ReturnsAsync(account);



        SelectedUpgradesHandler handler = new(
            mockAccountRepo.Object,
            mockSessionRepo.Object,
            NullLogger<SelectedUpgradesHandler>.Instance);

        // Equip cs.kongor to replace cs.zephyr
        HttpContext httpContext = CreateHttpContext(cookie, new Dictionary<string, string[]>
        {
            ["selected_upgrades[]"] = ["cs.kongor"]
        });

        IActionResult result = await handler.HandleRequestAsync(httpContext);

        result.Should().BeOfType<PhpResult>();
        account.SelectedStoreItemCodes.Should().Contain("cs.kongor", "New chat symbol must be equipped");
        account.SelectedStoreItemCodes.Should().NotContain("cs.zephyr", "Old chat symbol must be replaced");
        account.SelectedStoreItemCodes.Should().Contain("ai.custom_icon:1", "Account icon must be preserved");
        account.SelectedStoreItemCodes.Should().Contain("cc.white", "Chat color must be preserved");
    }

    [Fact]
    public async Task SelectedUpgradesHandler_ExplicitUnequip_RemovesOnlyTargetCategory()
    {
        (Account account, User user) = CreateTestAccount();
        account.SetSelectedStoreItems(["ai.custom_icon:1", "cs.zephyr", "cc.white"]);

        Mock<IAccountRepository> mockAccountRepo = new();
        Mock<ISessionRepository> mockSessionRepo = new();

        string cookie = "test_cookie_unequip";
        mockSessionRepo
            .Setup(s => s.GetAccountNameForSessionCookieAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(account.Name);

        mockAccountRepo
            .Setup(a => a.GetByNicknameAsync(account.Name, It.IsAny<CancellationToken>()))
            .ReturnsAsync(account);



        SelectedUpgradesHandler handler = new(
            mockAccountRepo.Object,
            mockSessionRepo.Object,
            NullLogger<SelectedUpgradesHandler>.Instance);

        // Unequip chat symbol using cs.0
        HttpContext httpContext = CreateHttpContext(cookie, new Dictionary<string, string[]>
        {
            ["selected_upgrades[]"] = ["cs.0"]
        });

        IActionResult result = await handler.HandleRequestAsync(httpContext);

        result.Should().BeOfType<PhpResult>();
        account.SelectedStoreItemCodes.Should().NotContain("cs.zephyr", "Chat symbol must be unequipped");
        account.SelectedStoreItemCodes.Should().Contain("ai.custom_icon:1", "Account icon must be preserved");
        account.SelectedStoreItemCodes.Should().Contain("cc.white", "Chat color must be preserved");
    }

    [Fact]
    public async Task SelectedUpgradesHandler_MultiItemSync_UnequipsOmittedCategoryWhilePreservingRemaining()
    {
        (Account account, User user) = CreateTestAccount();
        user.InventoryItems.AddRange([
            new UserInventory { ItemCode = "c.wareffort_courier", Quantity = 1, IsPermanent = true },
            new UserInventory { ItemCode = "w.default_ward", Quantity = 1, IsPermanent = true }
        ]);
        account.SetSelectedStoreItems(["c.wareffort_courier", "w.default_ward", "ai.custom_icon:1"]);

        Mock<IAccountRepository> mockAccountRepo = new();
        Mock<ISessionRepository> mockSessionRepo = new();

        string cookie = "test_cookie_multi_sync";
        mockSessionRepo
            .Setup(s => s.GetAccountNameForSessionCookieAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(account.Name);

        mockAccountRepo
            .Setup(a => a.GetByNicknameAsync(account.Name, It.IsAny<CancellationToken>()))
            .ReturnsAsync(account);

        SelectedUpgradesHandler handler = new(
            mockAccountRepo.Object,
            mockSessionRepo.Object,
            NullLogger<SelectedUpgradesHandler>.Instance);

        // K2 sends Courier and Icon, omitting Ward (which was cleared in client)
        HttpContext httpContext = CreateHttpContext(cookie, new Dictionary<string, string[]>
        {
            ["selected_upgrades[]"] = ["c.wareffort_courier", "ai.custom_icon:1"]
        });

        IActionResult result = await handler.HandleRequestAsync(httpContext);

        result.Should().BeOfType<PhpResult>();
        account.SelectedStoreItemCodes.Should().Contain("c.wareffort_courier", "Courier was retained in multi-item sync");
        account.SelectedStoreItemCodes.Should().Contain("ai.custom_icon:1", "Account icon was retained in multi-item sync");
        account.SelectedStoreItemCodes.Should().NotContain("w.default_ward", "Ward was omitted by K2 and should be unequipped");
    }

    [Fact]
    public async Task SelectedUpgradesHandler_SingleItemRemainder_UnequipsMissingCategoryWhenRemainderAlreadyEquipped()
    {
        (Account account, User user) = CreateTestAccount();
        user.InventoryItems.Add(new UserInventory { ItemCode = "c.wareffort_courier", Quantity = 1, IsPermanent = true });
        account.SetSelectedStoreItems(["c.wareffort_courier", "ai.custom_icon:1"]);

        Mock<IAccountRepository> mockAccountRepo = new();
        Mock<ISessionRepository> mockSessionRepo = new();

        string cookie = "test_cookie_single_remainder";
        mockSessionRepo
            .Setup(s => s.GetAccountNameForSessionCookieAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(account.Name);

        mockAccountRepo
            .Setup(a => a.GetByNicknameAsync(account.Name, It.IsAny<CancellationToken>()))
            .ReturnsAsync(account);

        SelectedUpgradesHandler handler = new(
            mockAccountRepo.Object,
            mockSessionRepo.Object,
            NullLogger<SelectedUpgradesHandler>.Instance);

        // Account had Courier and Icon. Player unequipped courier in K2, leaving only ai.custom_icon:1
        HttpContext httpContext = CreateHttpContext(cookie, new Dictionary<string, string[]>
        {
            ["selected_upgrades[]"] = ["ai.custom_icon:1"]
        });

        IActionResult result = await handler.HandleRequestAsync(httpContext);

        result.Should().BeOfType<PhpResult>();
        account.SelectedStoreItemCodes.Should().Contain("ai.custom_icon:1", "Active item must stay equipped");
        account.SelectedStoreItemCodes.Should().Contain("c.wareffort_courier", "Other category must not be wiped out by single-item equip");
    }

    [Fact]
    public async Task SelectedUpgradesHandler_PrefixOnlyUnequip_RemovesTargetCategory()
    {
        (Account account, User user) = CreateTestAccount();
        user.InventoryItems.Add(new UserInventory { ItemCode = "c.wareffort_courier", Quantity = 1, IsPermanent = true });
        account.SetSelectedStoreItems(["c.wareffort_courier", "ai.custom_icon:1"]);

        Mock<IAccountRepository> mockAccountRepo = new();
        Mock<ISessionRepository> mockSessionRepo = new();

        string cookie = "test_cookie_prefix_unequip";
        mockSessionRepo
            .Setup(s => s.GetAccountNameForSessionCookieAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(account.Name);

        mockAccountRepo
            .Setup(a => a.GetByNicknameAsync(account.Name, It.IsAny<CancellationToken>()))
            .ReturnsAsync(account);

        SelectedUpgradesHandler handler = new(
            mockAccountRepo.Object,
            mockSessionRepo.Object,
            NullLogger<SelectedUpgradesHandler>.Instance);

        // Prefix-only code from Lua ClearUpgrade('c')
        HttpContext httpContext = CreateHttpContext(cookie, new Dictionary<string, string[]>
        {
            ["selected_upgrades[]"] = ["c"]
        });

        IActionResult result = await handler.HandleRequestAsync(httpContext);

        result.Should().BeOfType<PhpResult>();
        account.SelectedStoreItemCodes.Should().NotContain("c.wareffort_courier", "Courier category must be cleared");
        account.SelectedStoreItemCodes.Should().Contain("ai.custom_icon:1", "Account icon must be preserved");
    }

    [Fact]
    public async Task SelectedUpgradesHandler_EmptyPayload_ClearsAllSelections()
    {
        (Account account, User user) = CreateTestAccount();
        account.SetSelectedStoreItems(["ai.custom_icon:1", "cs.zephyr"]);

        Mock<IAccountRepository> mockAccountRepo = new();
        Mock<ISessionRepository> mockSessionRepo = new();

        string cookie = "test_cookie_empty";
        mockSessionRepo
            .Setup(s => s.GetAccountNameForSessionCookieAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(account.Name);

        mockAccountRepo
            .Setup(a => a.GetByNicknameAsync(account.Name, It.IsAny<CancellationToken>()))
            .ReturnsAsync(account);

        SelectedUpgradesHandler handler = new(
            mockAccountRepo.Object,
            mockSessionRepo.Object,
            NullLogger<SelectedUpgradesHandler>.Instance);

        // Empty selected_upgrades form
        HttpContext httpContext = CreateHttpContext(cookie, new Dictionary<string, string[]>());

        IActionResult result = await handler.HandleRequestAsync(httpContext);

        result.Should().BeOfType<PhpResult>();
        account.SelectedStoreItemCodes.Should().BeEmpty("All selections must be cleared when empty payload is dispatched");
    }

    [Fact]
    public async Task SelectedUpgradesHandler_BareTauntPrefixUnequip_RemovesTauntCategory()
    {
        (Account account, User user) = CreateTestAccount();
        account.SetSelectedStoreItems(["t.Wolf_Time_Taunt", "m.Super-Taunt", "ai.custom_icon:1"]);

        Mock<IAccountRepository> mockAccountRepo = new();
        Mock<ISessionRepository> mockSessionRepo = new();

        string cookie = "test_cookie_taunt_unequip";
        mockSessionRepo
            .Setup(s => s.GetAccountNameForSessionCookieAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(account.Name);

        mockAccountRepo
            .Setup(a => a.GetByNicknameAsync(account.Name, It.IsAny<CancellationToken>()))
            .ReturnsAsync(account);

        SelectedUpgradesHandler handler = new(
            mockAccountRepo.Object,
            mockSessionRepo.Object,
            NullLogger<SelectedUpgradesHandler>.Instance);

        // Dispatched by ClearUpgrade('t') from the Vault UI Default Taunt tile
        HttpContext httpContext = CreateHttpContext(cookie, new Dictionary<string, string[]>
        {
            ["selected_upgrades[]"] = ["t"]
        });

        IActionResult result = await handler.HandleRequestAsync(httpContext);

        result.Should().BeOfType<PhpResult>();
        account.SelectedStoreItemCodes.Should().NotContain("t.Wolf_Time_Taunt", "Taunt category must be cleared on bare 't' prefix unequip");
        account.SelectedStoreItemCodes.Should().Contain("m.Super-Taunt", "Super-Taunt perk must be preserved");
        account.SelectedStoreItemCodes.Should().Contain("ai.custom_icon:1", "Account icon must be preserved");
    }

    [Fact]
    public async Task SelectedUpgradesHandler_SystemObjectOrUnknownPrefix_IsRejectedAndNotEquipped()
    {
        (Account account, User user) = CreateTestAccount();
        account.SetSelectedStoreItems(["t.Wolf_Time_Taunt", "ai.custom_icon:1"]);

        Mock<IAccountRepository> mockAccountRepo = new();
        Mock<ISessionRepository> mockSessionRepo = new();

        string cookie = "test_cookie_reject_unknown";
        mockSessionRepo
            .Setup(s => s.GetAccountNameForSessionCookieAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(account.Name);

        mockAccountRepo
            .Setup(a => a.GetByNicknameAsync(account.Name, It.IsAny<CancellationToken>()))
            .ReturnsAsync(account);

        SelectedUpgradesHandler handler = new(
            mockAccountRepo.Object,
            mockSessionRepo.Object,
            NullLogger<SelectedUpgradesHandler>.Instance);

        // Attempting to post non-whitelisted dotted string (e.g. System.Object[])
        HttpContext httpContext = CreateHttpContext(cookie, new Dictionary<string, string[]>
        {
            ["selected_upgrades[]"] = ["System.Object[]"]
        });

        IActionResult result = await handler.HandleRequestAsync(httpContext);

        result.Should().BeOfType<PhpResult>();
        account.SelectedStoreItemCodes.Should().NotContain("System.Object[]", "Malformed non-whitelisted prefix must never be equipped");
        user.InventoryItems.Should().NotContain(i => i.ItemCode == "System.Object[]", "Non-whitelisted item must not be auto-granted to inventory");
    }

    [Fact]
    public async Task SelectedUpgradesHandler_InitialSelections_AutoPurgesNonWhitelistedPrefixes()
    {
        (Account account, User user) = CreateTestAccount();
        // Stale database record containing a legacy malformed entry
        account.SetSelectedStoreItems(["System.Object[]", "t.Wolf_Time_Taunt", "ai.custom_icon:1"]);

        Mock<IAccountRepository> mockAccountRepo = new();
        Mock<ISessionRepository> mockSessionRepo = new();

        string cookie = "test_cookie_auto_purge";
        mockSessionRepo
            .Setup(s => s.GetAccountNameForSessionCookieAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(account.Name);

        mockAccountRepo
            .Setup(a => a.GetByNicknameAsync(account.Name, It.IsAny<CancellationToken>()))
            .ReturnsAsync(account);

        SelectedUpgradesHandler handler = new(
            mockAccountRepo.Object,
            mockSessionRepo.Object,
            NullLogger<SelectedUpgradesHandler>.Instance);

        // Valid update payload
        HttpContext httpContext = CreateHttpContext(cookie, new Dictionary<string, string[]>
        {
            ["selected_upgrades[]"] = ["t.Wolf_Time_Taunt"]
        });

        IActionResult result = await handler.HandleRequestAsync(httpContext);

        result.Should().BeOfType<PhpResult>();
        account.SelectedStoreItemCodes.Should().NotContain("System.Object[]", "Non-whitelisted prefix must be purged during sync");
        account.SelectedStoreItemCodes.Should().Contain("t.Wolf_Time_Taunt");
    }
}
