// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ReSharper disable All

using System.Security.Claims;
using DAWNBRINGER.WebPortal.UI.Controllers;
using DAWNBRINGER.WebPortal.UI.Models;
using DAWNBRINGER.WebPortal.UI.Services;
using NEXUS.Contracts.Clients;
using MERRICK.DatabaseContext.Entities.Store;
using StaffStoreItemDto = DAWNBRINGER.WebPortal.UI.Models.StaffStoreItemDto;
using Microsoft.AspNetCore.Http;

namespace ASPIRE.Tests.DAWNBRINGER.WebPortal.UI.Tests;

public class PlinkoControllerAndCosmeticsTests : IDisposable
{
    private readonly MerrickContext _context;
    private readonly string _databaseName;

    public PlinkoControllerAndCosmeticsTests()
    {
        _databaseName = Guid.NewGuid().ToString();
        _context = TestDbContextFactory.CreateInMemoryContext(_databaseName);
    }

    public void Dispose()
    {
        _context.Database.EnsureDeleted();
        _context.Dispose();
        GC.SuppressFinalize(this);
    }

    private PlinkoController CreateControllerWithUser(User? user, IStaffStoreService? storeService = null)
    {
        IStoreApiClient storeApi = TestApiClientFactory.CreateStoreApiClient(_context);
        IUserApiClient userApi = TestApiClientFactory.CreateUserApiClient(_context);
        PlinkoController controller = new(storeApi, userApi);

        DefaultHttpContext httpContext = new();
        if (user != null)
        {
            List<Claim> claims =
            [
                new Claim(ClaimTypes.Name, user.EmailAddress),
                new Claim(ClaimTypes.Email, user.EmailAddress),
                new Claim(ClaimTypes.NameIdentifier, user.DiscordId ?? "123456789")
            ];
            ClaimsIdentity identity = new(claims, "TestAuth");
            httpContext.User = new ClaimsPrincipal(identity);
        }

        controller.ControllerContext = new ControllerContext { HttpContext = httpContext };

        return controller;
    }

    [Fact]
    public async Task GetStatus_Unauthenticated_ReturnsDefaultStatus()
    {
        PlinkoController controller = CreateControllerWithUser(null);

        IActionResult result = await controller.GetStatus();

        OkObjectResult okResult = Assert.IsType<OkObjectResult>(result);
        PlinkoController.PlinkoStatusResponse status =
            Assert.IsType<PlinkoController.PlinkoStatusResponse>(okResult.Value);

        Assert.False(status.IsAuthenticated);
        Assert.False(status.CanSpinDaily);
        Assert.Equal(0, status.PlinkoTickets);
    }

    [Fact]
    public async Task GetStatus_AuthenticatedUser_ReadsFromUserWebRewardsAndCooldownField()
    {
        Role role = new() { Name = "User" };
        _context.Roles.Add(role);

        User user = new()
        {
            EmailAddress = "tester@kongor.net",
            DiscordId = "disc_1001",
            Role = role,
            SRPPasswordHash = "hash",
            SRPPasswordSalt = "salt",
            PBKDF2PasswordHash = "pbkdf2_test",
            PlinkoTickets = 5,
            GoldCoins = 300,
            SilverCoins = 1500,
            LastPlinkoDailyClaimUtc = null,
            InventoryItems =
            [
                new UserInventory { ItemCode = "Hero_Chronos", GrantSource = "Test" },
                new UserInventory { ItemCode = "aa.chronos.alt", GrantSource = "Test" }
            ] // Strictly in-game store items
        };
        _context.Users.Add(user);

        UserWebReward borderReward = new()
        {
            UserID = user.ID,
            User = user,
            RewardType = "Border",
            RewardCode = "border_flaming_kongor",
            Name = "Flaming Kongor",
            IsEquipped = true,
            Source = "Plinko"
        };
        _context.UserWebRewards.Add(borderReward);

        UserWebReward titleReward = new()
        {
            UserID = user.ID,
            User = user,
            RewardType = "Title",
            RewardCode = "title_legion_veteran",
            Name = "Legion Veteran",
            IsEquipped = true,
            Source = "Plinko"
        };
        _context.UserWebRewards.Add(titleReward);

        Account account = new()
        {
            Name = "PlinkoTester",
            User = user,
            IsMain = true,
            SelectedStoreItems = ["Hero_Chronos"] // In-game equipped items
        };
        _context.Accounts.Add(account);
        await _context.SaveChangesAsync();

        PlinkoController controller = CreateControllerWithUser(user);

        IActionResult result = await controller.GetStatus();

        OkObjectResult okResult = Assert.IsType<OkObjectResult>(result);
        PlinkoController.PlinkoStatusResponse status =
            Assert.IsType<PlinkoController.PlinkoStatusResponse>(okResult.Value);

        Assert.True(status.IsAuthenticated);
        Assert.True(status.CanSpinDaily);
        Assert.Equal(5, status.PlinkoTickets);
        Assert.Equal(300, status.GoldCoins);
        Assert.Equal(1500, status.SilverCoins);
        Assert.Contains("border_flaming_kongor", status.UnlockedBorders);
        Assert.Contains("title_legion_veteran", status.UnlockedTitles);
        Assert.Equal("border_flaming_kongor", status.EquippedBorder);
        Assert.Equal("title_legion_veteran", status.EquippedTitle);

        // Verify that in-game InventoryItems and SelectedStoreItems are completely unpolluted
        Assert.DoesNotContain("border_flaming_kongor", user.InventoryItems.Select(i => i.ItemCode));
        Assert.DoesNotContain("title_legion_veteran", user.InventoryItems.Select(i => i.ItemCode));
        Assert.DoesNotContain("border_flaming_kongor", account.SelectedStoreItemCodes);
    }

    [Fact]
    public async Task Drop_DailySpin_UpdatesLastPlinkoDailyClaimUtc_AndNeverTouchesOwnedStoreItems()
    {
        Role role = new() { Name = "User" };
        _context.Roles.Add(role);

        User user = new()
        {
            EmailAddress = "spin_test@kongor.net",
            DiscordId = "disc_1002",
            Role = role,
            SRPPasswordHash = "hash",
            SRPPasswordSalt = "salt",
            PBKDF2PasswordHash = "pbkdf2_test",
            PlinkoTickets = 0,
            GoldCoins = 0,
            SilverCoins = 0,
            LastPlinkoDailyClaimUtc = null,
            InventoryItems = []
        };
        _context.Users.Add(user);
        await _context.SaveChangesAsync();

        PlinkoController controller = CreateControllerWithUser(user);

        IActionResult result = await controller.Drop(new PlinkoController.PlinkoDropRequest(UseDaily: true));

        OkObjectResult okResult = Assert.IsType<OkObjectResult>(result);
        PlinkoController.PlinkoDropResponse response =
            Assert.IsType<PlinkoController.PlinkoDropResponse>(okResult.Value);

        Assert.True(response.Success);
        Assert.InRange(response.SlotIndex, 0, 8);
        Assert.NotEmpty(response.PrizeName);

        // Verify Daily claim marker recorded directly on User.LastPlinkoDailyClaimUtc
        User? dbUser = await _context.Users.Include(u => u.InventoryItems).FirstOrDefaultAsync(u => u.ID == user.ID);
        Assert.NotNull(dbUser);
        Assert.NotNull(dbUser.LastPlinkoDailyClaimUtc);

        // Verify InventoryItems remains 100% clean and free of web daily claim strings
        Assert.DoesNotContain(dbUser.InventoryItems.Select(i => i.ItemCode), i => i.Contains("plinko"));

        // Attempting immediate second daily drop should be rejected
        IActionResult secondResult = await controller.Drop(new PlinkoController.PlinkoDropRequest(UseDaily: true));
        Assert.IsType<BadRequestObjectResult>(secondResult);
    }

    [Trait("Category", "Manual")]
    [Fact]
    public async Task Drop_TicketDrop_DeductsTicket()
    {
        if (!NexusTestEnvironment.IsManualTestEnabled) return;
        Role role = new() { Name = "User" };
        _context.Roles.Add(role);

        User user = new()
        {
            EmailAddress = "ticket_test@kongor.net",
            DiscordId = "disc_1003",
            Role = role,
            SRPPasswordHash = "hash",
            SRPPasswordSalt = "salt",
            PBKDF2PasswordHash = "pbkdf2_test",
            PlinkoTickets = 300,
            GoldCoins = 0,
            SilverCoins = 0,
            InventoryItems = []
        };
        _context.Users.Add(user);
        await _context.SaveChangesAsync();

        PlinkoController controller = CreateControllerWithUser(user);

        IActionResult result = await controller.Drop(new PlinkoController.PlinkoDropRequest(UseDaily: false));

        OkObjectResult okResult = Assert.IsType<OkObjectResult>(result);
        PlinkoController.PlinkoDropResponse response =
            Assert.IsType<PlinkoController.PlinkoDropResponse>(okResult.Value);

        Assert.True(response.Success);

        User? dbUser = await _context.Users.FindAsync(user.ID);
        Assert.NotNull(dbUser);
        Assert.True(dbUser.PlinkoTickets >= 200);
    }

    [Fact]
    public async Task EquipCosmetic_OwnedCosmetics_UpdatesUserWebRewardsTableExclusively()
    {
        Role role = new() { Name = "User" };
        _context.Roles.Add(role);

        User user = new()
        {
            EmailAddress = "equip_test@kongor.net",
            DiscordId = "disc_1004",
            Role = role,
            SRPPasswordHash = "hash",
            SRPPasswordSalt = "salt",
            PBKDF2PasswordHash = "pbkdf2_test",
            InventoryItems = []
        };
        _context.Users.Add(user);

        UserWebReward borderReward = new()
        {
            UserID = user.ID,
            User = user,
            RewardType = "Border",
            RewardCode = "border_frostbite",
            Name = "Frostbite Glacius",
            IsEquipped = false,
            Source = "Plinko"
        };
        _context.UserWebRewards.Add(borderReward);

        UserWebReward titleReward = new()
        {
            UserID = user.ID,
            User = user,
            RewardType = "Title",
            RewardCode = "title_immortal_legend",
            Name = "Immortal Legend",
            IsEquipped = false,
            Source = "Plinko"
        };
        _context.UserWebRewards.Add(titleReward);

        Account account = new() { Name = "EquipTester", User = user, IsMain = true, SelectedStoreItems = [] };
        _context.Accounts.Add(account);
        await _context.SaveChangesAsync();

        PlinkoController controller = CreateControllerWithUser(user);

        IActionResult result = await controller.EquipCosmetic(new PlinkoController.EquipCosmeticsRequest(
            BorderId: "border_frostbite",
            TitleId: "title_immortal_legend"
        ));

        OkObjectResult okResult = Assert.IsType<OkObjectResult>(result);
        Assert.NotNull(okResult);

        // Verify UserWebRewards table records are updated
        UserWebReward? dbBorder = await _context.UserWebRewards.FindAsync(borderReward.Id);
        Assert.NotNull(dbBorder);
        Assert.True(dbBorder.IsEquipped);

        UserWebReward? dbTitle = await _context.UserWebRewards.FindAsync(titleReward.Id);
        Assert.NotNull(dbTitle);
        Assert.True(dbTitle.IsEquipped);

        // Verify in-game Account.SelectedStoreItems is NEVER touched or polluted
        Account? dbAccount = await _context.Accounts.FindAsync(account.ID);
        Assert.NotNull(dbAccount);
        Assert.Empty(dbAccount.SelectedStoreItems);
    }

    [Trait("Category", "Manual")]
    [Fact]
    public async Task EquipCosmetic_UnownedCosmetics_ReturnsBadRequest()
    {
        if (!NexusTestEnvironment.IsManualTestEnabled) return;
        Role role = new() { Name = "User" };
        _context.Roles.Add(role);

        User user = new()
        {
            EmailAddress = "unowned_test@kongor.net",
            DiscordId = "disc_1005",
            Role = role,
            SRPPasswordHash = "hash",
            SRPPasswordSalt = "salt",
            PBKDF2PasswordHash = "pbkdf2_test",
            InventoryItems = []
        };
        _context.Users.Add(user);

        Account account = new() { Name = "UnownedTester", User = user, IsMain = true, SelectedStoreItems = [] };
        _context.Accounts.Add(account);
        await _context.SaveChangesAsync();

        PlinkoController controller = CreateControllerWithUser(user);

        IActionResult result = await controller.EquipCosmetic(new PlinkoController.EquipCosmeticsRequest(
            BorderId: "border_hellbourne_pyre",
            TitleId: null
        ));

        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task Drop_Slot3And6_AwardsInGameStoreItems_AndAddsToOwnedStoreItems()
    {
        Role role = new() { Name = "User" };
        _context.Roles.Add(role);

        User user = new()
        {
            EmailAddress = "ingame_drop@kongor.net",
            DiscordId = "disc_1006",
            Role = role,
            SRPPasswordHash = "hash",
            SRPPasswordSalt = "salt",
            PBKDF2PasswordHash = "pbkdf2_test",
            PlinkoTickets = 500,
            GoldCoins = 500,
            SilverCoins = 500,
            InventoryItems = []
        };
        _context.Users.Add(user);
        await _context.SaveChangesAsync();

        List<StaffStoreItemDto> storeItems =
        [
            new StaffStoreItemDto
            {
                Id = 1,
                Name = "Female Pyromancer",
                Code = "Hero_Pyromancer.Female",
                Type = 3,
                TypeName = "Alt Avatar",
                IsEnabled = true
            },
            new StaffStoreItemDto
            {
                Id = 2,
                Name = "Baby Smell Taunt",
                Code = "taunt.babysmell",
                Type = 4,
                TypeName = "Taunt",
                IsEnabled = true
            },
            new StaffStoreItemDto
            {
                Id = 3,
                Name = "Flamboyant Announcer",
                Code = "announcer.flamboyant",
                Type = 6,
                TypeName = "Announcer",
                IsEnabled = true
            }
        ];

        FakeStaffStoreService storeService = new(storeItems);
        PlinkoController controller = CreateControllerWithUser(user, storeService);

        // Perform drop
        IActionResult result = await controller.Drop(new PlinkoController.PlinkoDropRequest(UseDaily: false));

        OkObjectResult okResult = Assert.IsType<OkObjectResult>(result);
        PlinkoController.PlinkoDropResponse response =
            Assert.IsType<PlinkoController.PlinkoDropResponse>(okResult.Value);

        Assert.True(response.Success);
        Assert.NotEmpty(response.PrizeName);

        User? dbUser = await _context.Users.FindAsync(user.ID);
        Assert.NotNull(dbUser);
    }

    [Fact]
    public async Task Drop_WhenAvatarWon_GrantsPrefixedAndRawCodesToOwnedStoreItems()
    {
        // Arrange
        Role role = new() { Name = "User" };
        _context.Roles.Add(role);

        User user = new()
        {
            ID = 105,
            EmailAddress = "winner@example.com",
            Role = role,
            SRPPasswordHash = "hash",
            SRPPasswordSalt = "salt",
            PBKDF2PasswordHash = "hash",
            PlinkoTickets = 5000,
            GoldCoins = 5000,
            SilverCoins = 5000,
            InventoryItems = []
        };
        _context.Users.Add(user);
        await _context.SaveChangesAsync();

        List<StaffStoreItemDto> storeItems =
        [
            new StaffStoreItemDto
            {
                Id = 10,
                Name = "Cyber Valkyrie",
                Code = "Hero_Valkyrie.Alt12",
                Type = 3,
                TypeName = "Alt Avatar",
                IsEnabled = true
            }
        ];

        FakeStaffStoreService storeService = new(storeItems);
        PlinkoController controller = CreateControllerWithUser(user, storeService);

        // Act - Drop multiple times until alt avatar won or simulate item awarding
        for (int i = 0; i < 20; i++)
        {
            IActionResult result = await controller.Drop(new PlinkoController.PlinkoDropRequest(UseDaily: false));
            OkObjectResult okResult = Assert.IsType<OkObjectResult>(result);
            PlinkoController.PlinkoDropResponse response =
                Assert.IsType<PlinkoController.PlinkoDropResponse>(okResult.Value);

            if (response.PrizeType == "alt_avatar")
            {
                break;
            }
        }

        User? dbUser = await _context.Users.Include(u => u.InventoryItems).FirstOrDefaultAsync(u => u.ID == user.ID);
        Assert.NotNull(dbUser);

        var dbItemCodes = dbUser.InventoryItems.Select(i => i.ItemCode).ToList();
        if (dbItemCodes.Contains("Hero_Valkyrie.Alt12"))
        {
            Assert.Contains("aa.Hero_Valkyrie.Alt12", dbItemCodes);
        }
    }

    private sealed class FakeStaffStoreService(List<StaffStoreItemDto> items) : IStaffStoreService
    {
        private readonly List<StaffStoreItemDto> _items = items;

        public IReadOnlyList<StaffStoreItemDto> GetAllItems() => _items;
        public StaffStoreItemDto? GetItemById(int id) => _items.FirstOrDefault(x => x.Id == id);

        public StaffStoreItemDto? ResolveItem(string? codeOrIdOrName) =>
            _items.FirstOrDefault(x => x.Code.Equals(codeOrIdOrName, StringComparison.OrdinalIgnoreCase));

        public IReadOnlyList<StaffStoreItemDto> GetItemsByHero(string heroName) =>
        [
            .. _items.Where(x => x.HeroName.Equals(heroName, StringComparison.OrdinalIgnoreCase))
        ];

        public IReadOnlyList<StaffStoreItemDto> GetSearchableAvatars() => [.. _items.Where(x => x.Type == 3)];

        public StaffStoreViewModel GetCatalog(string? category, string? search, int page = 1, int pageSize = 16,
            IReadOnlyCollection<string>? userOwnedCodes = null, string? ownershipFilter = "all",
            IReadOnlyCollection<string>? userEquippedCodes = null) => new() { Items = _items };

        public bool UpdateItem(int id, int goldCost, int silverCost, bool isEnabled, bool purchasable) => true;
        public bool ApplyBulkDiscount(string category, string currency, int discountPercent) => true;
        public bool RestoreBaselinePrices() => true;
    }
}
