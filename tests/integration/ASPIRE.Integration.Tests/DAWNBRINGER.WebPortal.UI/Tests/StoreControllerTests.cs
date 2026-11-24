// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Security.Claims;
using DAWNBRINGER.WebPortal.UI.Controllers;
using DAWNBRINGER.WebPortal.UI.Models;
using DAWNBRINGER.WebPortal.UI.Services;
using Kongor.Core.Services.Vfs;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NEXUS.Contracts.Clients;
using MERRICK.DatabaseContext.Entities.Store;
using StaffStoreItemDto = DAWNBRINGER.WebPortal.UI.Models.StaffStoreItemDto;

namespace ASPIRE.Tests.DAWNBRINGER.WebPortal.UI.Tests;

public sealed class StoreControllerTests
{
    private static MerrickContext CreateInMemoryContext()
    {
        return TestDbContextFactory.CreateInMemoryContext();
    }

    private static StoreController CreateController(MerrickContext context, StaffStoreService storeService)
    {
        IStoreApiClient storeApi = TestApiClientFactory.CreateStoreApiClient(context);
        IUserApiClient userApi = TestApiClientFactory.CreateUserApiClient(context);
        return new StoreController(storeApi, userApi, storeService, NullLogger<StoreController>.Instance);
    }

    [Fact]
    public async Task Index_UnauthenticatedGuest_ReturnsStoreCatalogWithoutUserContext()
    {
        // Arrange
        await using MerrickContext context = CreateInMemoryContext();
        IConfiguration config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Store:UnlockAllItems"] = "false" })
            .Build();
        StaffStoreService storeService = new(config, NullLogger<StaffStoreService>.Instance);
        StoreController controller = CreateController(context, storeService);

        // Act
        IActionResult result = await controller.Index();

        // Assert
        ViewResult viewResult = Assert.IsType<ViewResult>(result);
        StaffStoreViewModel model = Assert.IsType<StaffStoreViewModel>(viewResult.Model);
        Assert.NotNull(model.Items);
        Assert.NotEmpty(model.Items);
        Assert.False(model.IsAuthenticated);
        Assert.False(model.IsStaff);
    }

    [Fact]
    public async Task Index_AuthenticatedUser_LoadsBalancesAndOwnershipBadges()
    {
        // Arrange
        await using MerrickContext context = CreateInMemoryContext();
        User user = new()
        {
            EmailAddress = "storeuser@kongor.net",
            GoldCoins = 500,
            SilverCoins = 1200,
            Role = new Role { Name = "User" },
            PBKDF2PasswordHash = "hash",
            SRPPasswordHash = "hash",
            SRPPasswordSalt = "salt",
            InventoryItems =
            [
                new UserInventory { ItemCode = "Hero_Krixi.Alt", GrantSource = "Test" },
                new UserInventory { ItemCode = "Hero_Devourer.Oni", GrantSource = "Test" }
            ]
        };
        Account account = new() { Name = "StorePlayer", User = user, IsMain = true };
        await context.Users.AddAsync(user);
        await context.Accounts.AddAsync(account);
        await context.SaveChangesAsync();

        IConfiguration config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Store:UnlockAllItems"] = "false" })
            .Build();
        StaffStoreService storeService = new(config, NullLogger<StaffStoreService>.Instance);
        StoreController controller = CreateController(context, storeService);

        ClaimsPrincipal principal = new(new ClaimsIdentity(
        [
            new Claim(ClaimTypes.Name, "StorePlayer"),
            new Claim(ClaimTypes.Email, "storeuser@kongor.net")
        ], "TestAuth"));

        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = principal }
        };

        // Act
        IActionResult result = await controller.Index();

        // Assert
        ViewResult viewResult = Assert.IsType<ViewResult>(result);
        StaffStoreViewModel model = Assert.IsType<StaffStoreViewModel>(viewResult.Model);
        Assert.True(model.IsAuthenticated);
        Assert.Equal(500, model.UserGold);
        Assert.Equal(1200, model.UserSilver);
        Assert.Contains("Hero_Krixi.Alt", model.UserOwnedItemCodes);
    }

    [Fact]
    public async Task Purchase_WithGold_DeductsGoldAndAddsToVault()
    {
        // Arrange
        await using MerrickContext context = CreateInMemoryContext();
        User user = new()
        {
            EmailAddress = "goldbuyer@kongor.net",
            GoldCoins = 1000,
            SilverCoins = 500,
            Role = new Role { Name = "User" },
            PBKDF2PasswordHash = "hash",
            SRPPasswordHash = "hash",
            SRPPasswordSalt = "salt",
            InventoryItems = []
        };
        Account account = new() { Name = "GoldBuyer", User = user, IsMain = true };
        await context.Users.AddAsync(user);
        await context.Accounts.AddAsync(account);
        await context.SaveChangesAsync();

        IConfiguration config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Store:UnlockAllItems"] = "false" })
            .Build();
        StaffStoreService storeService = new(config, NullLogger<StaffStoreService>.Instance);
        StoreController controller = CreateController(context, storeService);

        ClaimsPrincipal principal = new(new ClaimsIdentity(
        [
            new Claim(ClaimTypes.Name, "GoldBuyer"),
            new Claim(ClaimTypes.Email, "goldbuyer@kongor.net")
        ], "TestAuth"));

        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = principal }
        };

        StaffStoreViewModel catalog = storeService.GetCatalog("all", null, 1, 10);
        StaffStoreItemDto itemToBuy = catalog.Items.First(x => x is { Purchasable: true, GoldCost: > 0 });

        // Act
        IActionResult result = await controller.Purchase(new StoreController.PurchaseItemRequest(itemToBuy.Id, "gold"));

        // Assert
        OkObjectResult okResult = Assert.IsType<OkObjectResult>(result);
        User? updatedUser = await context.Users.Include(u => u.InventoryItems).FirstOrDefaultAsync(u => u.EmailAddress == "goldbuyer@kongor.net");
        Assert.NotNull(updatedUser);
        Assert.Equal(1000 - itemToBuy.GoldCost, updatedUser.GoldCoins);
        Assert.Contains(itemToBuy.Code, updatedUser.InventoryItems.Select(i => i.ItemCode));
    }

    [Fact]
    public async Task Purchase_WithSilver_DeductsSilverAndAddsToVault()
    {
        // Arrange
        await using MerrickContext context = CreateInMemoryContext();
        User user = new()
        {
            EmailAddress = "silverbuyer@kongor.net",
            GoldCoins = 100,
            SilverCoins = 3000,
            Role = new Role { Name = "User" },
            PBKDF2PasswordHash = "hash",
            SRPPasswordHash = "hash",
            SRPPasswordSalt = "salt",
            InventoryItems = []
        };
        Account account = new() { Name = "SilverBuyer", User = user, IsMain = true };
        await context.Users.AddAsync(user);
        await context.Accounts.AddAsync(account);
        await context.SaveChangesAsync();

        IConfiguration config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Store:UnlockAllItems"] = "false" })
            .Build();
        StaffStoreService storeService = new(config, NullLogger<StaffStoreService>.Instance);
        StoreController controller = CreateController(context, storeService);

        ClaimsPrincipal principal = new(new ClaimsIdentity(
        [
            new Claim(ClaimTypes.Name, "SilverBuyer"),
            new Claim(ClaimTypes.Email, "silverbuyer@kongor.net")
        ], "TestAuth"));

        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = principal }
        };

        StaffStoreViewModel catalog = storeService.GetCatalog("all", null, 1, 10);
        StaffStoreItemDto itemToBuy = catalog.Items.First(x => x is { Purchasable: true, SilverCost: > 0 });

        // Act
        IActionResult result =
            await controller.Purchase(new StoreController.PurchaseItemRequest(itemToBuy.Id, "silver"));

        // Assert
        OkObjectResult okResult = Assert.IsType<OkObjectResult>(result);
        User? updatedUser = await context.Users.Include(u => u.InventoryItems).FirstOrDefaultAsync(u => u.EmailAddress == "silverbuyer@kongor.net");
        Assert.NotNull(updatedUser);
        Assert.Equal(3000 - itemToBuy.SilverCost, updatedUser.SilverCoins);
        Assert.Contains(itemToBuy.Code, updatedUser.InventoryItems.Select(i => i.ItemCode));
    }

    [Fact]
    public async Task Purchase_InsufficientCoins_ReturnsBadRequest()
    {
        // Arrange
        await using MerrickContext context = CreateInMemoryContext();
        User user = new()
        {
            EmailAddress = "brokeuser@kongor.net",
            GoldCoins = 0,
            SilverCoins = 0,
            Role = new Role { Name = "User" },
            PBKDF2PasswordHash = "hash",
            SRPPasswordHash = "hash",
            SRPPasswordSalt = "salt",
            InventoryItems = []
        };
        Account account = new() { Name = "BrokeUser", User = user, IsMain = true };
        await context.Users.AddAsync(user);
        await context.Accounts.AddAsync(account);
        await context.SaveChangesAsync();

        IConfiguration config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Store:UnlockAllItems"] = "false" })
            .Build();
        StaffStoreService storeService = new(config, NullLogger<StaffStoreService>.Instance);
        StoreController controller = CreateController(context, storeService);

        ClaimsPrincipal principal = new(new ClaimsIdentity(
        [
            new Claim(ClaimTypes.Name, "BrokeUser"),
            new Claim(ClaimTypes.Email, "brokeuser@kongor.net")
        ], "TestAuth"));

        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = principal }
        };

        StaffStoreViewModel catalog = storeService.GetCatalog("all", null, 1, 10);
        StaffStoreItemDto itemToBuy = catalog.Items.First(x => x is { Purchasable: true, GoldCost: > 0 });

        // Act
        IActionResult result = await controller.Purchase(new StoreController.PurchaseItemRequest(itemToBuy.Id, "gold"));

        // Assert
        BadRequestObjectResult badResult = Assert.IsType<BadRequestObjectResult>(result);
        Assert.NotNull(badResult.Value);
    }

    [Fact]
    public async Task Purchase_AlreadyOwned_ReturnsBadRequest()
    {
        // Arrange
        await using MerrickContext context = CreateInMemoryContext();
        IConfiguration config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Store:UnlockAllItems"] = "false" })
            .Build();
        StaffStoreService storeService = new(config, NullLogger<StaffStoreService>.Instance);

        StaffStoreViewModel catalog = storeService.GetCatalog("all", null, 1, 10);
        StaffStoreItemDto itemToBuy = catalog.Items.First(x => x.Purchasable);

        User user = new()
        {
            EmailAddress = "richuser@kongor.net",
            GoldCoins = 5000,
            SilverCoins = 5000,
            Role = new Role { Name = "User" },
            PBKDF2PasswordHash = "hash",
            SRPPasswordHash = "hash",
            SRPPasswordSalt = "salt",
            InventoryItems = [new UserInventory { ItemCode = itemToBuy.Code, GrantSource = "Test" }]
        };
        Account account = new() { Name = "RichUser", User = user, IsMain = true };
        await context.Users.AddAsync(user);
        await context.Accounts.AddAsync(account);
        await context.SaveChangesAsync();

        StoreController controller = CreateController(context, storeService);

        ClaimsPrincipal principal = new(new ClaimsIdentity(
        [
            new Claim(ClaimTypes.Name, "RichUser"),
            new Claim(ClaimTypes.Email, "richuser@kongor.net")
        ], "TestAuth"));

        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = principal }
        };

        // Act
        IActionResult result = await controller.Purchase(new StoreController.PurchaseItemRequest(itemToBuy.Id, "gold"));

        // Assert
        BadRequestObjectResult badResult = Assert.IsType<BadRequestObjectResult>(result);
        Assert.NotNull(badResult.Value);
    }

    [Fact]
    public async Task Item_ValidId_ReturnsItemDetailViewWithRelatedAvatars()
    {
        // Arrange
        await using MerrickContext context = CreateInMemoryContext();
        IConfiguration config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Store:UnlockAllItems"] = "false" })
            .Build();
        StaffStoreService storeService = new(config, NullLogger<StaffStoreService>.Instance);
        StoreController controller = CreateController(context, storeService);

        // Act: Fetch Female Pyromancer (#15)
        IActionResult result = await controller.Item(15);

        // Assert
        ViewResult viewResult = Assert.IsType<ViewResult>(result);
        Assert.Equal("Item", viewResult.ViewName);
        StoreItemDetailViewModel model = Assert.IsType<StoreItemDetailViewModel>(viewResult.Model);
        Assert.Equal(15, model.Item.Id);
        Assert.Equal("Female Pyromancer", model.Item.Name);
        Assert.NotNull(model.RelatedItems);
    }

    [Fact]
    public async Task Item_InvalidId_ReturnsNotFound()
    {
        // Arrange
        await using MerrickContext context = CreateInMemoryContext();
        IConfiguration config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Store:UnlockAllItems"] = "false" })
            .Build();
        StaffStoreService storeService = new(config, NullLogger<StaffStoreService>.Instance);
        StoreController controller = CreateController(context, storeService);

        // Act
        IActionResult result = await controller.Item(999999);

        // Assert
        Assert.IsType<NotFoundResult>(result);
    }

    [Fact]
    public void StaffStoreService_GetItemsByHero_ReturnsRelatedAvatars()
    {
        // Arrange
        IConfiguration config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Store:UnlockAllItems"] = "false" })
            .Build();
        StaffStoreService storeService = new(config, NullLogger<StaffStoreService>.Instance);

        // Act
        IReadOnlyList<StaffStoreItemDto> pyroAvatars = storeService.GetItemsByHero("pyromancer");

        // Assert
        Assert.NotNull(pyroAvatars);
        Assert.NotEmpty(pyroAvatars);
        Assert.Contains(pyroAvatars,
            a => a.Name.Contains("Female Pyromancer", StringComparison.OrdinalIgnoreCase) ||
                 a.Code.Contains("Female", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void StaffStoreService_GetSearchableAvatars_ReturnsAvatars()
    {
        // Arrange
        IConfiguration config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Store:UnlockAllItems"] = "false" })
            .Build();
        StaffStoreService storeService = new(config, NullLogger<StaffStoreService>.Instance);

        // Act
        IReadOnlyList<StaffStoreItemDto> avatars = storeService.GetSearchableAvatars();

        // Assert
        Assert.NotNull(avatars);
        Assert.NotEmpty(avatars);
        Assert.All(avatars,
            a => Assert.True(a.Type == 3 || a.Type == 12 ||
                             (a.Code.StartsWith("Hero_", StringComparison.OrdinalIgnoreCase) && a.Code.Contains('.'))));
    }

    [Fact]
    public void StaffStoreService_BuildNormalizedOwnedLookup_ResolvesInGamePrefixes()
    {
        // Arrange
        List<string> inGameOwned =
        [
            "aa.Hero_Mimix.Alt",
            "aa.Hero_Mimix.Alt2",
            "aa.Hero_Kunas.Alt11",
            "Hero_Succubus",
            "3204",
            "t.Wolf_Time_Taunt",
            "c.dragon_stage_3",
            "av.Samuel_L_Jackson",
            "ai.Beta_Warrior",
            "cc.gold"
        ];

        // Act
        HashSet<string> lookup = StaffStoreService.BuildNormalizedOwnedLookup(inGameOwned);

        // Assert
        Assert.Contains("Hero_Mimix.Alt", lookup);
        Assert.Contains("Mimix.Alt", lookup);
        Assert.Contains("aa.Hero_Mimix.Alt", lookup);
        Assert.Contains("Hero_Kunas.Alt11", lookup);
        Assert.Contains("Wolf_Time_Taunt", lookup);
        Assert.Contains("t.Wolf_Time_Taunt", lookup);
        Assert.Contains("dragon_stage_3", lookup);
        Assert.Contains("Samuel_L_Jackson", lookup);
        Assert.Contains("Beta_Warrior", lookup);
        Assert.Contains("gold", lookup);
    }

    [Fact]
    public void StaffStoreService_IsItemOwned_MatchesInGameAndPortalFormats()
    {
        // Arrange
        List<string> inGameOwned = ["aa.Hero_Mimix.Alt", "t.Wolf_Time_Taunt", "Mimix Elite"];
        HashSet<string> normalizedLookup = StaffStoreService.BuildNormalizedOwnedLookup(inGameOwned);

        StaffStoreItemDto mimixItem = new()
        {
            Id = 999,
            Code = "Hero_Mimix.Alt",
            HeroName = "Hero_Mimix",
            AltAvatarSuffix = "Alt",
            Name = "Mimix Elite"
        };

        StaffStoreItemDto tauntItem = new() { Id = 888, Code = "Wolf_Time_Taunt", Name = "Wolf Time Taunt" };

        StaffStoreItemDto unownedItem = new()
        {
            Id = 777,
            Code = "Hero_Chronos.Alt",
            HeroName = "Hero_Chronos",
            AltAvatarSuffix = "Alt",
            Name = "Father Time Chronos"
        };

        // Act & Assert
        Assert.True(StaffStoreService.IsItemOwned(mimixItem, normalizedLookup));
        Assert.True(StaffStoreService.IsItemOwned(tauntItem, normalizedLookup));
        Assert.False(StaffStoreService.IsItemOwned(unownedItem, normalizedLookup));
    }

    [Fact]
    public void StaffStoreService_ResolveItem_SingleSourceOfTruth_ResolvesItemCodesAndFallbacks()
    {
        // Arrange
        IConfiguration config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Store:UnlockAllItems"] = "false" })
            .Build();
        StaffStoreService storeService = new(config, NullLogger<StaffStoreService>.Instance);

        // Act
        StaffStoreItemDto? mimix = storeService.ResolveItem("aa.Hero_Mimix.Alt");
        StaffStoreItemDto? icon = storeService.ResolveItem("ai.Default_Icon");
        StaffStoreItemDto? color = storeService.ResolveItem("cc.white");
        StaffStoreItemDto? taunt = storeService.ResolveItem("t.Standard");
        StaffStoreItemDto? fallback = storeService.ResolveItem("Avatar_CustomHero.Alt1");

        // Assert
        Assert.NotNull(mimix);
        Assert.Equal("Alt Avatar", mimix.TypeName);

        Assert.NotNull(icon);
        Assert.Equal("Account Icon", icon.TypeName);
        Assert.False(icon.CanPreview3D);

        Assert.NotNull(color);
        Assert.Equal("Name Color", color.TypeName);
        Assert.False(color.CanPreview3D);

        Assert.NotNull(taunt);
        Assert.Equal("Taunt", taunt.TypeName);
        Assert.False(taunt.CanPreview3D);

        Assert.NotNull(fallback);
        Assert.Equal("Alt Avatar", fallback.TypeName);
    }

    [Fact]
    public void StaffStoreService_AccountIconsAndVanity_CanPreview3DIsFalse()
    {
        // Arrange
        IConfiguration config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Store:UnlockAllItems"] = "false" })
            .Build();
        StaffStoreService storeService = new(config, NullLogger<StaffStoreService>.Instance);

        // Act
        StaffStoreViewModel catalog = storeService.GetCatalog("all", null, 1, 500);

        // Assert
        foreach (StaffStoreItemDto item in catalog.Items)
            if (item.Type is 1 or 0 or 2 or 4 or 5 or 6 or 7 or 8 or 10 or 11)
                Assert.False(item.CanPreview3D,
                    $"Item {item.Name} (Type {item.Type}, Code {item.Code}) should NOT have CanPreview3D = true");
    }

    [Fact]
    public async Task EquipItem_OwnedItem_EquipsInAccountSelectedStoreItems()
    {
        // Arrange
        await using MerrickContext context = CreateInMemoryContext();
        User user = new()
        {
            EmailAddress = "equipper@kongor.net",
            GoldCoins = 1000,
            SilverCoins = 500,
            Role = new Role { Name = "User" },
            PBKDF2PasswordHash = "hash",
            SRPPasswordHash = "hash",
            SRPPasswordSalt = "salt",
            InventoryItems =
            [
                new UserInventory { ItemCode = "Hero_Mimix.Alt", GrantSource = "Test" },
                new UserInventory { ItemCode = "icon.skull", GrantSource = "Test" },
                new UserInventory { ItemCode = "cc.gold", GrantSource = "Test" }
            ]
        };
        Account account = new()
        {
            Name = "EquipPlayer",
            User = user,
            IsMain = true,
            SelectedStoreItems = ["ai.default", "cc.white", "aa.Hero_Mimix.Default"]
        };
        await context.Users.AddAsync(user);
        await context.Accounts.AddAsync(account);
        await context.SaveChangesAsync();

        IConfiguration config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Store:UnlockAllItems"] = "false" })
            .Build();
        StaffStoreService storeService = new(config, NullLogger<StaffStoreService>.Instance);
        StoreController controller = CreateController(context, storeService);

        ClaimsPrincipal principal = new(new ClaimsIdentity(
        [
            new Claim(ClaimTypes.Name, "EquipPlayer"),
            new Claim(ClaimTypes.Email, "equipper@kongor.net")
        ], "TestAuth"));

        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = principal }
        };

        // Act - Equip Alt Avatar
        StaffStoreItemDto? mimixItem = storeService.ResolveItem("Hero_Mimix.Alt");
        Assert.NotNull(mimixItem);

        IActionResult result =
            await controller.EquipItem(new StoreController.EquipItemRequest(mimixItem.Id, mimixItem.Code));

        // Assert
        JsonResult jsonResult = Assert.IsType<JsonResult>(result);
        Account? updatedAccount = await context.Accounts
            .Include(a => a.SelectedStoreItems)
            .FirstOrDefaultAsync(a => a.Name == "EquipPlayer");
        Assert.NotNull(updatedAccount);
        Assert.Contains(updatedAccount.SelectedStoreItemCodes,
            x => x.Contains("Mimix.Alt", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(updatedAccount.SelectedStoreItemCodes,
            x => x.Equals("aa.Hero_Mimix.Default", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task EquipItem_UnownedItem_ReturnsBadRequest()
    {
        // Arrange
        await using MerrickContext context = CreateInMemoryContext();
        User user = new()
        {
            EmailAddress = "unownedequipper@kongor.net",
            GoldCoins = 1000,
            SilverCoins = 500,
            Role = new Role { Name = "User" },
            PBKDF2PasswordHash = "hash",
            SRPPasswordHash = "hash",
            SRPPasswordSalt = "salt",
            InventoryItems = []
        };
        Account account = new()
        {
            Name = "UnownedEquipPlayer",
            User = user,
            IsMain = true,
            SelectedStoreItems = ["ai.default"]
        };
        await context.Users.AddAsync(user);
        await context.Accounts.AddAsync(account);
        await context.SaveChangesAsync();

        IConfiguration config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Store:UnlockAllItems"] = "false" })
            .Build();
        StaffStoreService storeService = new(config, NullLogger<StaffStoreService>.Instance);
        StoreController controller = CreateController(context, storeService);

        ClaimsPrincipal principal = new(new ClaimsIdentity(
        [
            new Claim(ClaimTypes.Name, "UnownedEquipPlayer"),
            new Claim(ClaimTypes.Email, "unownedequipper@kongor.net")
        ], "TestAuth"));

        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = principal }
        };

        // Act - Attempt to equip unowned item
        StaffStoreItemDto? mimixItem = storeService.ResolveItem("Hero_Mimix.Alt");
        Assert.NotNull(mimixItem);

        IActionResult result =
            await controller.EquipItem(new StoreController.EquipItemRequest(mimixItem.Id, mimixItem.Code));

        // Assert
        BadRequestObjectResult badResult = Assert.IsType<BadRequestObjectResult>(result);
        Assert.NotNull(badResult.Value);
    }

    [Fact]
    public void StaffStoreService_ApplyBulkDiscount_And_RestoreBaselinePrices()
    {
        string baseDir = AppContext.BaseDirectory;
        string? realStorePath = Path.Combine(baseDir, "Configuration", "Store", "StoreItemsConfiguration.json");
        if (!File.Exists(realStorePath))
        {
            DirectoryInfo? current = new(baseDir);
            realStorePath = null;
            while (current != null)
            {
                string candidate = Path.Combine(current.FullName, "services", "master-server", "src", "Configuration",
                    "Store",
                    "StoreItemsConfiguration.json");
                if (!File.Exists(candidate))
                    candidate = Path.Combine(current.FullName, "KONGOR.MasterServer", "Configuration", "Store",
                        "StoreItemsConfiguration.json");
                if (File.Exists(candidate))
                {
                    realStorePath = candidate;
                    break;
                }

                current = current.Parent;
            }
        }

        Assert.NotNull(realStorePath);
        string tempStorePath = Path.Combine(Path.GetTempPath(), $"StoreItemsConfiguration_{Guid.NewGuid():N}.json");
        File.Copy(realStorePath, tempStorePath, true);

        try
        {
            IConfiguration config = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { ["StaffStore:StoreJsonPath"] = tempStorePath, ["Store:UnlockAllItems"] = "false" })
                .Build();

            StaffStoreService storeService = new(config, NullLogger<StaffStoreService>.Instance);

            StaffStoreViewModel catalogBefore = storeService.GetCatalog("avatars", null, 1, 50);
            StaffStoreItemDto? testItem = catalogBefore.Items.FirstOrDefault(x => x.GoldCost >= 100);
            Assert.NotNull(testItem);
            int initialGold = testItem.GoldCost;

            // Act 1: Apply 20% discount on avatars for Gold
            bool applied = storeService.ApplyBulkDiscount("avatars", "gold", 20);
            Assert.True(applied);

            StaffStoreItemDto? discountedItem = storeService.GetItemById(testItem.Id);
            Assert.NotNull(discountedItem);
            int expectedGold = (int)Math.Max(1, Math.Round(initialGold * 0.80));
            Assert.Equal(expectedGold, discountedItem.GoldCost);

            // Act 2: Restore baseline prices
            bool restored = storeService.RestoreBaselinePrices();
            Assert.True(restored);

            StaffStoreItemDto? restoredItem = storeService.GetItemById(testItem.Id);
            Assert.NotNull(restoredItem);
            Assert.Equal(initialGold, restoredItem.GoldCost);
        }
        finally
        {
            if (File.Exists(tempStorePath)) File.Delete(tempStorePath);
        }
    }

    [Fact]
    public void StaffStoreService_IsItemEquipped_MatchesCorrectPrefixes()
    {
        // Arrange
        HashSet<string> equipped = ["aa.Hero_Mimix.Alt", "cc.gold", "ai.flag_usa", "t.smackdown"];

        StaffStoreItemDto avatarItem = new()
        {
            Code = "Hero_Mimix.Alt",
            HeroName = "Mimix",
            AltAvatarSuffix = "Alt",
            Type = 3
        };

        StaffStoreItemDto colorItem = new() { Code = "gold", Type = 0 };

        StaffStoreItemDto unequippedItem = new()
        {
            Code = "Hero_Devourer.Alt",
            HeroName = "Devourer",
            AltAvatarSuffix = "Alt",
            Type = 3
        };

        // Act & Assert
        Assert.True(StaffStoreService.IsItemEquipped(avatarItem, equipped));
        Assert.True(StaffStoreService.IsItemEquipped(colorItem, equipped));
        Assert.False(StaffStoreService.IsItemEquipped(unequippedItem, equipped));
    }

    [Trait("Category", "Manual")]
    [Fact]
    public void VfsService_GetHeroModelGlb_Krixi_GeneratesValidGlb()
    {
        if (!NexusTestEnvironment.IsManualTestEnabled) return;

        // Arrange
        IConfiguration config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Store:UnlockAllItems"] = "false" })
            .Build();
        GameArchiveVfsService vfs = new(config, NullLogger<GameArchiveVfsService>.Instance);

        // Act & Assert 1: Sexy Moon Queen with alt "Sexy"
        byte[]? glb1 = vfs.GetHeroModelGlb("Hero_Krixi", "Sexy", "/heroes/krixi/alt/icon.tga");
        Assert.NotNull(glb1);
        Assert.True(glb1.Length > 100);

        // Act & Assert 2: Female Pyromancer
        byte[]? glb2 = vfs.GetHeroModelGlb("Hero_Pyromancer", "Female", "/heroes/pyromancer/alt/icon.tga");
        Assert.NotNull(glb2);
        Assert.True(glb2.Length > 100);

        // Act & Assert 3: Ninja Bubbles
        byte[]? glb3 = vfs.GetHeroModelGlb("Hero_Bubbles", "Ninja", "/heroes/bubbles/alt/icon.tga");
        Assert.NotNull(glb3);
        Assert.True(glb3.Length > 100);
    }

    [Fact]
    public void GetCatalog_Page1_DoesNotContainDeprioritizedAvatars()
    {
        // Arrange
        IConfiguration config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Store:UnlockAllItems"] = "false" })
            .Build();
        StaffStoreService storeService = new(config, NullLogger<StaffStoreService>.Instance);

        // Act
        StaffStoreViewModel page1 = storeService.GetCatalog("avatars", null);

        // Assert
        List<string> page1Codes = [.. page1.Items.Select(x => x.Code)];
        Assert.DoesNotContain("Hero_Pyromancer.Female", page1Codes);
        Assert.DoesNotContain("Hero_Krixi.Sexy", page1Codes);
        Assert.DoesNotContain("Hero_Madman.Nightmare", page1Codes);
        Assert.DoesNotContain("Hero_Succubus.Naughty", page1Codes);
        Assert.DoesNotContain("Hero_Gauntlet.El", page1Codes);
    }
}
