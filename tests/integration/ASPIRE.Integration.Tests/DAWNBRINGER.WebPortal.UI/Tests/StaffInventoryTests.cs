// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Security.Claims;
using DAWNBRINGER.WebPortal.UI.Controllers;
using DAWNBRINGER.WebPortal.UI.Models;
using DAWNBRINGER.WebPortal.UI.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NEXUS.Contracts.Clients;
using MERRICK.DatabaseContext.Entities.Store;

namespace ASPIRE.Tests.DAWNBRINGER.WebPortal.UI.Tests;

public class StaffInventoryTests
{
    private static readonly StaffStoreService SharedStoreService =
        new(new ConfigurationBuilder().Build(), NullLogger<StaffStoreService>.Instance);

    private static MerrickContext CreateInMemoryContext(string dbName = "StaffInventoryTestDb")
    {
        return TestDbContextFactory.CreateInMemoryContext($"{dbName}_{Guid.NewGuid():N}");
    }

    private static StaffController CreateController(MerrickContext context, bool isStaff = true)
    {
        IUserApiClient userApiClient = TestApiClientFactory.CreateUserApiClient(context);
        IAdminApiClient adminApiClient = TestApiClientFactory.CreateAdminApiClient(context);
        IPlayerStatsApiClient statsApiClient = TestApiClientFactory.CreatePlayerStatsApiClient(context);

        StaffController controller = new(adminApiClient, userApiClient, statsApiClient, SharedStoreService,
            logger: NullLogger<StaffController>.Instance);

        List<Claim> claims =
        [
            new(ClaimTypes.Name, "StaffUser"),
            new(ClaimTypes.NameIdentifier, "1")
        ];

        if (isStaff)
        {
            claims.Add(new Claim("is_staff", "true"));
            claims.Add(new Claim(ClaimTypes.Role, "Staff"));
        }

        ClaimsPrincipal principal = new(new ClaimsIdentity(claims, "TestAuth"));
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = principal }
        };

        return controller;
    }

    [Fact]
    public async Task Inventory_UnauthorizedUser_RedirectsToHome()
    {
        await using MerrickContext context = CreateInMemoryContext();
        StaffController controller = CreateController(context, false);

        IActionResult result = await controller.Inventory("Player1");

        RedirectToActionResult redirectResult = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal("Index", redirectResult.ActionName);
        Assert.Equal("Home", redirectResult.ControllerName);
    }

    [Trait("Category", "Manual")]
    [Fact]
    public async Task Inventory_AuthorizedStaff_LoadsAccountAndVault()
    {
        if (!NexusTestEnvironment.IsManualTestEnabled) return;
        await using MerrickContext context = CreateInMemoryContext();
        User user = new()
        {
            EmailAddress = "player@kongor.net",
            PBKDF2PasswordHash = "hash",
            SRPPasswordHash = "hash",
            SRPPasswordSalt = "salt",
            Role = new Role { Name = "User" },
            InventoryItems =
            [
                new UserInventory { ItemCode = "Avatar_Legionnaire.Plogger", GrantSource = "Test" },
                new UserInventory { ItemCode = "Taunt_Kongor", GrantSource = "Test" }
            ]
        };
        Account account = new() { Name = "VaultPlayer", User = user, IsMain = true };
        await context.Users.AddAsync(user);
        await context.Accounts.AddAsync(account);
        await context.SaveChangesAsync();

        StaffController controller = CreateController(context);

        IActionResult result = await controller.Inventory("VaultPlayer");

        ViewResult viewResult = Assert.IsType<ViewResult>(result);
        StaffInventoryViewModel model = Assert.IsType<StaffInventoryViewModel>(viewResult.Model);
        Assert.Equal("VaultPlayer", model.AccountName);
        Assert.Equal(2, model.OwnedItems.Count);
        Assert.NotEmpty(model.OwnedItemDetails);
    }

    [Trait("Category", "Manual")]
    [Fact]
    public async Task StaffInventory_AddInventoryItem_AddsItemSuccessfully()
    {
        if (!NexusTestEnvironment.IsManualTestEnabled) return;
        await using MerrickContext context = CreateInMemoryContext();
        User user = new()
        {
            EmailAddress = "player2@kongor.net",
            PBKDF2PasswordHash = "hash",
            SRPPasswordHash = "hash",
            SRPPasswordSalt = "salt",
            Role = new Role { Name = "User" },
            InventoryItems = []
        };
        Account account = new() { Name = "GrantPlayer", User = user, IsMain = true };
        await context.Users.AddAsync(user);
        await context.Accounts.AddAsync(account);
        await context.SaveChangesAsync();

        StaffController controller = CreateController(context);

        IActionResult result = await controller.AddInventoryItem("GrantPlayer", "Avatar_Devourer.Oni");

        RedirectToActionResult redirectResult = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal("Inventory", redirectResult.ActionName);

        Account? updated =
            await context.Accounts.Include(a => a.User).ThenInclude(u => u!.InventoryItems).FirstOrDefaultAsync(a => a.Name == "GrantPlayer");
        Assert.NotNull(updated);
        Assert.Contains("Avatar_Devourer.Oni", updated.User!.InventoryItems.Select(i => i.ItemCode));
    }

    [Fact]
    public async Task StaffInventory_RemoveInventoryItem_RemovesItemSuccessfully()
    {
        await using MerrickContext context = CreateInMemoryContext();
        User user = new()
        {
            EmailAddress = "player3@kongor.net",
            PBKDF2PasswordHash = "hash",
            SRPPasswordHash = "hash",
            SRPPasswordSalt = "salt",
            Role = new Role { Name = "User" },
            InventoryItems =
            [
                new UserInventory { ItemCode = "Avatar_Devourer.Oni", GrantSource = "Test" },
                new UserInventory { ItemCode = "Taunt_Kongor", GrantSource = "Test" }
            ]
        };
        Account account = new() { Name = "RevokePlayer", User = user, IsMain = true };
        await context.Users.AddAsync(user);
        await context.Accounts.AddAsync(account);
        await context.SaveChangesAsync();

        StaffController controller = CreateController(context);

        IActionResult result = await controller.RemoveInventoryItem("RevokePlayer", "Avatar_Devourer.Oni");

        RedirectToActionResult redirectResult = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal("Inventory", redirectResult.ActionName);

        Account? updated =
            await context.Accounts.Include(a => a.User).ThenInclude(u => u!.InventoryItems).FirstOrDefaultAsync(a => a.Name == "RevokePlayer");
        Assert.NotNull(updated);
        Assert.DoesNotContain("Avatar_Devourer.Oni", updated.User!.InventoryItems.Select(i => i.ItemCode));
        Assert.Contains("Taunt_Kongor", updated.User!.InventoryItems.Select(i => i.ItemCode));
    }

    [Fact]
    public async Task StaffInventory_ResetPlayerInventory_WipesAllItems()
    {
        await using MerrickContext context = CreateInMemoryContext();
        User user = new()
        {
            EmailAddress = "player4@kongor.net",
            PBKDF2PasswordHash = "hash",
            SRPPasswordHash = "hash",
            SRPPasswordSalt = "salt",
            Role = new Role { Name = "User" },
            InventoryItems =
            [
                new UserInventory { ItemCode = "Item1", GrantSource = "Test" },
                new UserInventory { ItemCode = "Item2", GrantSource = "Test" },
                new UserInventory { ItemCode = "Item3", GrantSource = "Test" }
            ]
        };
        Account account = new() { Name = "WipePlayer", User = user, IsMain = true };
        await context.Users.AddAsync(user);
        await context.Accounts.AddAsync(account);
        await context.SaveChangesAsync();

        StaffController controller = CreateController(context);

        IActionResult result = await controller.ResetPlayerInventory("WipePlayer");

        RedirectToActionResult redirectResult = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal("Inventory", redirectResult.ActionName);

        Account? updated = await context.Accounts.Include(a => a.User).ThenInclude(u => u!.InventoryItems).FirstOrDefaultAsync(a => a.Name == "WipePlayer");
        Assert.NotNull(updated);
        Assert.Empty(updated.User!.InventoryItems);
    }

    [Trait("Category", "Manual")]
    [Fact]
    public async Task StaffInventory_GrantAllStoreItems_UnlocksItems()
    {
        if (!NexusTestEnvironment.IsManualTestEnabled) return;

        await using MerrickContext context = CreateInMemoryContext();
        User user = new()
        {
            EmailAddress = "player5@kongor.net",
            PBKDF2PasswordHash = "hash",
            SRPPasswordHash = "hash",
            SRPPasswordSalt = "salt",
            Role = new Role { Name = "User" },
            InventoryItems = []
        };
        Account account = new() { Name = "MasterPlayer", User = user, IsMain = true };
        await context.Users.AddAsync(user);
        await context.Accounts.AddAsync(account);
        await context.SaveChangesAsync();

        StaffController controller = CreateController(context);

        IActionResult result = await controller.GrantAllStoreItems("MasterPlayer", "alt_avatars");

        RedirectToActionResult redirectResult = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal("Inventory", redirectResult.ActionName);

        Account? updated =
            await context.Accounts.Include(a => a.User).ThenInclude(u => u!.InventoryItems).FirstOrDefaultAsync(a => a.Name == "MasterPlayer");
        Assert.NotNull(updated);
        Assert.NotEmpty(updated.User!.InventoryItems);
    }
}
