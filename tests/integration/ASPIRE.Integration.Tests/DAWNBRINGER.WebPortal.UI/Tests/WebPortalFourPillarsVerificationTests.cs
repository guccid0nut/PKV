// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Net;
using System.Security.Claims;
using System.Text.Json;
using ASPIRE.Tests.Infrastructure;
using DAWNBRINGER.WebPortal.UI.Controllers;
using DAWNBRINGER.WebPortal.UI.Models;
using DAWNBRINGER.WebPortal.UI.Services;
using FluentAssertions;
using MERRICK.DatabaseContext.Entities.Core;
using MERRICK.DatabaseContext.Entities.Game;
using MERRICK.DatabaseContext.Handlers;
using MERRICK.DatabaseContext.Persistence;
using MERRICK.DatabaseContext.Repositories;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;
using NEXUS.Contracts.Clients;
using NEXUS.Contracts.CQRS.PlayerStats;
using NEXUS.Contracts.Data;
using Xunit;
using ZORGATH.WebPortal.API.Features.PlayerStats;
using UiStaffStoreItemDto = DAWNBRINGER.WebPortal.UI.Models.StaffStoreItemDto;

namespace ASPIRE.Tests.DAWNBRINGER.WebPortal.UI.Tests;

/// <summary>
/// Comprehensive integration &amp; boundary verification suite for the 4 Web Portal Pillars:
/// 1. 100% Free &amp; Unlocked HoN Store + Goblin Plinko Cosmetics
/// 2. Discord-Only Authentication Gate Across Entire Portal Except Minimalist Homepage (/)
/// 3. Local Asset Rehydration (Hero Icons &amp; 36 Map Minimaps) + Cleanroom GitHub Placeholder Preservation
/// 4. Connected Visual Forward &amp; Backward Item Crafting Trees (/item/{code} &amp; /items)
/// </summary>
[Collection("DawnbringerE2E")]
public sealed class WebPortalFourPillarsVerificationTests(DawnbringerE2ETestFixture fixture)
{
    private readonly DawnbringerE2ETestFixture _fixture = fixture;

    // =========================================================================
    // PILLAR 1: 100% FREE & UNLOCKED STORE + GOBLIN PLINKO COSMETICS
    // =========================================================================

    [Fact]
    public void Pillar1_StaffStoreService_DefaultUnlockAllItems_ZeroesPricesAndUnlocksEntireCatalog()
    {
        // protects: Store:UnlockAllItems=true default contract across all 1,567+ HoN Store items
        // fails_when: Any item retains non-zero Gold/Silver price or IsOwnedByUser=false for authenticated players
        // seam: Real StaffStoreService JSON catalog loader + OwnershipFilter("equipped")
        IConfiguration freeConfig = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Store:UnlockAllItems"] = "true"
            })
            .Build();

        StaffStoreService service = new(freeConfig, NullLogger<StaffStoreService>.Instance);
        IReadOnlyList<UiStaffStoreItemDto> allItems = service.GetAllItems();

        allItems.Count.Should().BeGreaterThan(1000, "Full HoN store catalog should load 1,500+ items");
        List<UiStaffStoreItemDto> nonRestrictedItems = allItems
            .Where(i => !NEXUS.Contracts.Store.StoreEntitlementPolicy.IsRestrictedEntitlement(i.Code, null, i.Name))
            .ToList();
        List<UiStaffStoreItemDto> restrictedItems = allItems
            .Where(i => NEXUS.Contracts.Store.StoreEntitlementPolicy.IsRestrictedEntitlement(i.Code, null, i.Name))
            .ToList();

        nonRestrictedItems.Should().NotBeEmpty();
        nonRestrictedItems.Should().OnlyContain(i => i.GoldCost == 0 && i.SilverCost == 0 && i.Purchasable && i.IsEnabled);
        restrictedItems.Should().NotBeEmpty("Canonical restricted staff/GM/SBT/GUCI cosmetics must be present in catalog");
        restrictedItems.Should().OnlyContain(i => !i.Purchasable, "Restricted staff/GM/SBT/GUCI cosmetics must stay non-purchasable under UnlockAllItems");

        string sampleEquippedCode = nonRestrictedItems[0].Code;
        StaffStoreViewModel allView = service.GetCatalog(
            category: "avatars",
            search: null,
            page: 1,
            pageSize: 16,
            userOwnedCodes: [],
            ownershipFilter: "all",
            userEquippedCodes: [sampleEquippedCode],
            isAuthenticated: true);

        allView.IsUnlockAllItemsEnabled.Should().BeTrue();
        allView.Items.Should().NotBeEmpty();
        allView.Items.Should().OnlyContain(i => i.IsOwnedByUser && i.GoldCost == 0 && i.SilverCost == 0);

        StaffStoreViewModel equippedView = service.GetCatalog(
            category: null,
            search: null,
            page: 1,
            pageSize: 16,
            userOwnedCodes: [],
            ownershipFilter: "equipped",
            userEquippedCodes: [sampleEquippedCode]);

        equippedView.TotalEquippedInFilter.Should().BeGreaterThanOrEqualTo(1);
        equippedView.Items.Should().Contain(i => i.Code == sampleEquippedCode && i.IsEquippedByUser);
    }

    [Fact]
    public async Task Pillar1_InventoryRepository_UnlockAllItems_UnlocksAll10PlinkoRewardsAndAllowsFreeRepeatedDrops()
    {
        // protects: Zero-cost Plinko drops without daily cooldown + all 10 Web Rewards unlocked & equippable
        // fails_when: Plinko charges coins/tickets, enforces 24h cooldown, or rejects equipping unlocked border/title
        // seam: Real InventoryRepository + MerrickContext EF Core persistence
        await using MerrickContext db = TestDbContextFactory.CreateInMemoryContext();
        Role role = new() { ID = 1, Name = "User" };
        User user = new()
        {
            ID = 1,
            EmailAddress = "plinko@kongor.net",
            DiscordId = "discord-plinko-1",
            DiscordUsername = "plinkotester",
            Role = role,
            GoldCoins = 0,
            SilverCoins = 0,
            PlinkoTickets = 0,
            PBKDF2PasswordHash = "h",
            SRPPasswordHash = "h",
            SRPPasswordSalt = "s"
        };
        Account account = new()
        {
            ID = 1,
            Name = "plinkotester",
            IsMain = true,
            Type = AccountType.Normal,
            TimestampCreated = DateTime.UtcNow,
            User = user,
            UserID = user.ID
        };
        user.Accounts = [account];
        db.Roles.Add(role);
        db.Users.Add(user);
        db.Accounts.Add(account);
        await db.SaveChangesAsync();

        IConfiguration freeConfig = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Store:UnlockAllItems"] = "true" })
            .Build();
        InventoryRepository repo = new(db, NullLogger<InventoryRepository>.Instance, freeConfig);

        PlinkoStatusResponseDto status = await repo.GetPlinkoStatusAsync(user.ID);
        status.CanClaimDaily.Should().BeTrue();
        status.TicketCost.Should().Be(0);
        status.GoldCost.Should().Be(0);
        status.UnlockedBorders.Should().HaveCount(5);
        status.UnlockedTitles.Should().HaveCount(5);

        // Equip an unlocked border even when zero UserWebReward rows existed beforehand (auto-upsert)
        bool equipBorderOk = await repo.EquipWebRewardAsync(user.ID, "Border", "border_flaming_kongor");
        bool equipTitleOk = await repo.EquipWebRewardAsync(user.ID, "Title", "title_immortal_legend");
        equipBorderOk.Should().BeTrue();
        equipTitleOk.Should().BeTrue();

        // Execute back-to-back Plinko drops with 0 Gold, 0 Silver, 0 Tickets and verify 0 cooldown block
        PlinkoDropResultDto drop1 = await repo.PlayPlinkoDropAsync(user.ID, "daily", true);
        PlinkoDropResultDto drop2 = await repo.PlayPlinkoDropAsync(user.ID, "gold", false);
        drop1.Success.Should().BeTrue();
        drop2.Success.Should().BeTrue();
    }

    [Fact]
    public async Task Pillar1_StoreAndPlinkoHttpEndpoints_RenderAllUnlockedVaultPillsAndZeroCostPlinko()
    {
        // protects: Live Razor /store & /api/plinko/status HTTP rendering for authenticated Discord accounts
        // fails_when: /store renders legacy "Unowned" filter pill or /api/plinko/status omits 10 unlocked rewards
        // seam: Full ASP.NET Core TestServer pipeline
        HttpClient client = await _fixture.CreateAuthenticatedClientAsync();

        HttpResponseMessage storeResp = await client.GetAsync("/store");
        storeResp.StatusCode.Should().Be(HttpStatusCode.OK);
        string storeHtml = await storeResp.Content.ReadAsStringAsync();
        storeHtml.Should().Contain("All Unlocked");
        storeHtml.Should().Contain("Equipped");
        storeHtml.Should().NotContain(">Unowned (");

        HttpResponseMessage plinkoStatusResp = await client.GetAsync("/api/plinko/status");
        plinkoStatusResp.StatusCode.Should().Be(HttpStatusCode.OK);
        using JsonDocument doc = JsonDocument.Parse(await plinkoStatusResp.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("isUnlockAllItemsEnabled").GetBoolean().Should().BeTrue();
        doc.RootElement.GetProperty("unlockedBorders").GetArrayLength().Should().Be(5);
        doc.RootElement.GetProperty("unlockedTitles").GetArrayLength().Should().Be(5);
    }

    // =========================================================================
    // PILLAR 2: DISCORD-ONLY AUTH GATE & MINIMALIST ANONYMOUS HOMEPAGE (/)
    // =========================================================================

    [Fact]
    public async Task Pillar2_AnonymousHomepage_RendersMinimalistDiscordLandingWithoutNavigationChrome()
    {
        // protects: Strict minimalist homepage contract for logged-out visitors
        // fails_when: Logged-out GET / leaks nav dropdowns, global search, soundboard, or speculationrules
        // seam: Full ASP.NET Core Razor layout + Index.cshtml rendering
        HttpClient anonClient = _fixture.CreateAnonymousClient();

        HttpResponseMessage response = await anonClient.GetAsync("/");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        string html = await response.Content.ReadAsStringAsync();

        html.Should().Contain("Connect with Discord");
        html.Should().NotContain("id=\"global-search-input\"");
        html.Should().NotContain("id=\"floating-soundboard-trigger\"");
        html.Should().NotContain("speculationrules");
        html.Should().NotContain("Matches &amp; Live");
        html.Should().NotContain("AR Mini Games");
    }

    [Theory]
    [InlineData("/store")]
    [InlineData("/codex")]
    [InlineData("/items")]
    [InlineData("/item/sustainer")]
    [InlineData("/Home/Leaderboard")]
    [InlineData("/matches")]
    [InlineData("/matches/10001")]
    [InlineData("/guides")]
    [InlineData("/System/Metrics")]
    [InlineData("/Tools/DownloadCenter")]
    public async Task Pillar2_AnonymousProtectedPageRoutes_RedirectToMinimalistHomepageWithReturnUrl(string protectedPath)
    {
        // protects: FallbackPolicy & DefaultPolicy gate across all MVC/Razor endpoints except /
        // fails_when: Any internal page renders 200 OK to an unauthenticated visitor
        // seam: ASP.NET Core AuthorizationMiddleware + CookieAuthenticationEvents.OnRedirectToLogin
        HttpClient noRedirect = _fixture.CreateNoRedirectClient();

        HttpResponseMessage response = await noRedirect.GetAsync(protectedPath);

        response.StatusCode.Should().Be(HttpStatusCode.Redirect, $"Anonymous request to {protectedPath} must redirect");
        response.Headers.Location.Should().NotBeNull();
        string location = response.Headers.Location!.ToString();
        location.Should().StartWith("/?");
        location.ToLowerInvariant().Should().Contain("returnurl=");
    }

    [Theory]
    [InlineData("/api/items")]
    [InlineData("/api/plinko/status")]
    [InlineData("/api/players/search?q=guccidonut")]
    public async Task Pillar2_AnonymousApiEndpoints_Return401UnauthorizedJsonInsteadOfHtmlRedirect(string apiPath)
    {
        // protects: API 401 status code contract for unauthenticated callers
        // fails_when: Unauthenticated /api/* returns 200 OK data leak or HTML redirect loop
        // seam: ASP.NET Core CookieAuthenticationEvents.OnRedirectToLogin API branch
        HttpClient noRedirect = _fixture.CreateNoRedirectClient();

        HttpResponseMessage response = await noRedirect.GetAsync(apiPath);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Theory]
    [InlineData("/health")]
    [InlineData("/alive")]
    [InlineData("/motd/feed.json")]
    public async Task Pillar2_MachineProbesAndLauncherMotd_RemainAccessibleAnonymously(string probePath)
    {
        // protects: Aspire orchestrator health probes & Willowmaker launcher MOTD JSON feed
        // fails_when: Fallback authorization accidentally blocks /health, /alive, or /motd/feed.json
        // seam: Full ASP.NET Core endpoint routing
        HttpClient noRedirect = _fixture.CreateNoRedirectClient();

        HttpResponseMessage response = await noRedirect.GetAsync(probePath);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Pillar2_PendingDiscordOnboardingWithoutAccountId_RedirectsFromProtectedRoutesToRegister()
    {
        // protects: Dual-claim policy (discord_id + AccountId) preventing unfinished Discord signups from bypassing IGN/Invite registration
        // fails_when: A Discord callback without an existing HoN Account can browse /store without completing /Account/Register
        // seam: DirectDiscordCallback -> pending_registration cookie -> Authorization Failure -> /Account/Register
        HttpClient noRedirect = _fixture.CreateNoRedirectClient();

        string freshDiscordId = "unregistered-discord-" + Guid.NewGuid().ToString("N");
        HttpResponseMessage callbackResp = await noRedirect.GetAsync(
            $"/Account/DirectDiscordCallback?discordId={freshDiscordId}&discordUsername=newcomer&invite=INV-TEST");

        callbackResp.StatusCode.Should().Be(HttpStatusCode.Redirect);
        callbackResp.Headers.Location!.ToString().Should().StartWith("/Account/Register");

        // Attempting to visit /store while still lacking AccountId must redirect back to /Account/Register
        HttpResponseMessage storeAttempt = await noRedirect.GetAsync("/store");
        storeAttempt.StatusCode.Should().Be(HttpStatusCode.Redirect);
        storeAttempt.Headers.Location!.ToString().Should().StartWith("/Account/Register");

        // Verify zero orphan User rows were prematurely created in MerrickContext before CompleteRegistration
        bool orphanCreated = await _fixture.DbContext.Users.AnyAsync(u => u.DiscordId == freshDiscordId);
        orphanCreated.Should().BeFalse("No orphan User record may be created prior to valid CompleteRegistration");
    }

    // =========================================================================
    // PILLAR 3: LOCAL ASSET REHYDRATION & CLEANROOM PLACEHOLDER PRESERVATION
    // =========================================================================

    [Theory]
    [InlineData("icon_bubbles", "/images/heroes/bubbles.png")]
    [InlineData("icon_bubbles.png", "/images/heroes/bubbles.png")]
    [InlineData("Hero_PuppetMaster", "/images/heroes/puppetmaster.png")]
    [InlineData("Hero_Hiro", "/images/heroes/swiftblade.png")]
    [InlineData("Hero_Salforis", "/images/heroes/lordsalforis.png")]
    [InlineData("Adrenaline", "/images/heroes/adrenaline.png")]
    public void Pillar3_HeroAndMapAssetResolver_NormalizesLegacyHeroIdentifiersAndPrefixedIcons(
        string rawHero,
        string expectedUrl)
    {
        // protects: Canonical hero portrait URL resolution across Leaderboard, Stats, Matches, and Replay Player
        // fails_when: Prefixed "icon_bubbles" or engine ID "Hero_Hiro" produces 404 cleanroom fallback when local PNG exists
        // seam: HeroAndMapAssetResolver.ResolveHeroIconUrl
        HeroAndMapAssetResolver.ResolveHeroIconUrl(rawHero).Should().Be(expectedUrl);
    }

    [Theory]
    [InlineData("Mid Wars", "/images/maps/minimap_midwars.png", "Mid Wars")]
    [InlineData("minimap_mid wars.png", "/images/maps/minimap_midwars.png", "Mid Wars")]
    [InlineData("midwars_10v10", "/images/maps/minimap_midwars_10v10.png", "Mid Wars (10v10)")]
    [InlineData("Forests of Caldavar", "/images/maps/minimap_caldavar.png", "Forests of Caldavar")]
    [InlineData("ctf", "/images/maps/minimap_capturetheflag.png", "Capture The Flag")]
    [InlineData("devo_wars", "/images/maps/minimap_devowars.png", "Devo Wars")]
    public void Pillar3_HeroAndMapAssetResolver_NormalizesAllArenaAndCustomMapMinimaps(
        string rawMap,
        string expectedMinimapUrl,
        string expectedDisplayName)
    {
        // protects: Canonical map minimap URL & human-friendly title resolution for all 36 HoN maps
        // fails_when: Spaced "Mid Wars" or "minimap_mid wars" resolves to broken "minimap_mid%20wars.png"
        // seam: HeroAndMapAssetResolver.ResolveMinimapUrl & ResolveMapDisplayName
        HeroAndMapAssetResolver.ResolveMinimapUrl(rawMap).Should().Be(expectedMinimapUrl);
        HeroAndMapAssetResolver.ResolveMapDisplayName(rawMap).Should().Be(expectedDisplayName);
    }

    [Fact]
    public void Pillar3_TryResolveStaticImageCandidate_RespectsForceCleanroomPlaceholdersFlag()
    {
        // protects: Cleanroom GitHub export mode (0 retail bytes leaked when ForceCleanroomPlaceholders=true)
        // fails_when: Cleanroom placeholder flag fails to suppress physical file resolution or mislabels SVG badge
        // seam: HeroAndMapAssetResolver.TryResolveStaticImageCandidate
        bool matched = HeroAndMapAssetResolver.TryResolveStaticImageCandidate(
            "/images/maps/minimap_mid%20wars.png",
            new NullFileProvider(),
            forceCleanroomPlaceholders: true,
            out IFileInfo? resolvedFile,
            out string category,
            out string label);

        matched.Should().BeTrue();
        resolvedFile.Should().BeNull();
        category.Should().Be("maps");
        label.Should().Be("Mid Wars");
    }

    // =========================================================================
    // PILLAR 4: CONNECTED FORWARD & BACKWARD ITEM CRAFTING TREES
    // =========================================================================

    [Fact]
    public async Task Pillar4_SeedHeroItemsBackfillAndGetAllHeroItemsHandler_PreservesMultiQuantityComponents()
    {
        // protects: Automatic backfill of HeroItemComponents when HeroItems exist with 0 component rows + GetAllHeroItemsHandler .Include(i => i.Components)
        // fails_when: Sustainer/Dawnbringer/Portal Key return empty Components or lose Quantity > 1 duplicates
        // seam: SeedDataHandlers.SeedHeroItems + GetAllHeroItemsHandler + MerrickContext
        await using MerrickContext db = TestDbContextFactory.CreateInMemoryContext();

        // Simulate pre-existing DB state where HeroItems was seeded without HeroItemComponents rows
        db.HeroItems.Add(new HeroItem
        {
            Code = "sustainer",
            Name = "Sustainer",
            Cost = 1750,
            Description = "Grants +6 Health Regen and +150% Mana Regen.",
            Flavor = "Twin-vial ornament.",
            IconPath = "/images/items/sustainer.png",
            Category = "Combative",
            IsActive = true
        });
        await db.SaveChangesAsync();

        await SeedDataHandlers.SeedHeroItems(db, NullLogger.Instance, CancellationToken.None);

        (await db.HeroItemComponents.CountAsync()).Should().BeGreaterThanOrEqualTo(2);

        GetAllHeroItemsHandler handler = new(db);
        List<HeroItemDto> dtos = await handler.Handle(new GetAllHeroItemsQuery(), CancellationToken.None);

        HeroItemDto? sustainer = dtos.FirstOrDefault(i => i.Code.Equals("sustainer", StringComparison.OrdinalIgnoreCase));
        sustainer.Should().NotBeNull();
        sustainer!.ComponentCodesJson.Should().Contain("lifetube");
        sustainer.ComponentCodesJson.Should().Contain("manatube");
    }

    [Fact]
    public async Task Pillar4_ItemDetailControllerAndView_BuildsMultiTierForwardAndBackwardCraftingTrees()
    {
        // protects: Recursive forward (BuildsInto) & backward (Components + Recipe Scroll) tree construction for /item/{code}
        // fails_when: /item/sustainer omits backward components (lifetube, manatube) or forward upgrades (Null Stone)
        // seam: Real HomeController.ItemDetail + GetAllHeroItemsHandler + ItemDetail.cshtml HTTP rendering
        typeof(HomeController)
            .GetField("_cachedHeroItems", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)
            ?.SetValue(null, null);

        await using MerrickContext db = TestDbContextFactory.CreateInMemoryContext();
        db.HeroItems.AddRange(
            new HeroItem
            {
                Code = "lifetube",
                Name = "Lifetube",
                Cost = 825,
                Description = "Grants +5 Health Regeneration.",
                Flavor = "Restorative elixir tube.",
                Category = "Accessories",
                IconPath = "/images/items/lifetube.png",
                IsActive = true
            },
            new HeroItem
            {
                Code = "manatube",
                Name = "Manatube",
                Cost = 825,
                Description = "Grants +100% Mana Regeneration.",
                Flavor = "Arcane mana conduit.",
                Category = "Accessories",
                IconPath = "/images/items/manatube.png",
                IsActive = true
            },
            new HeroItem
            {
                Code = "sustainer",
                Name = "Sustainer",
                Cost = 1650,
                Description = "Grants +6 Health Regen and +150% Mana Regen.",
                Flavor = "Twin-vial ornament.",
                Category = "Initiation",
                IconPath = "/images/items/sustainer.png",
                IsActive = true,
                Components =
                [
                    new HeroItemComponent { ComponentItemCode = "lifetube", Quantity = 1 },
                    new HeroItemComponent { ComponentItemCode = "manatube", Quantity = 1 }
                ]
            },
            new HeroItem
            {
                Code = "nullstone",
                Name = "Null Stone",
                Cost = 4700,
                Description = "Blocks single-target enemy spells every 20 seconds.",
                Flavor = "Protective warded gem.",
                Category = "Protective",
                IconPath = "/images/items/nullstone.png",
                IsActive = true,
                Components =
                [
                    new HeroItemComponent { ComponentItemCode = "sustainer", Quantity = 1 }
                ]
            }
        );
        await db.SaveChangesAsync();

        HomeController controller = new(
            TestApiClientFactory.CreatePlayerStatsApiClient(db),
            TestApiClientFactory.CreateUserApiClient(db),
            TestApiClientFactory.CreateGuideApiClient(db),
            TestApiClientFactory.CreateAdminApiClient(db),
            new ConfigurationBuilder().Build())
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(
                    [
                        new Claim(ClaimTypes.Name, "guccidonut"),
                        new Claim("discord_id", "test-discord-guccidonut"),
                        new Claim("AccountId", "1")
                    ], CookieAuthenticationDefaults.AuthenticationScheme))
                }
            }
        };

        IActionResult actionResult = await controller.ItemDetail("sustainer");
        ViewResult viewResult = actionResult.Should().BeOfType<ViewResult>().Subject;
        HeroItemDto model = viewResult.Model.Should().BeOfType<HeroItemDto>().Subject;

        model.Components.OfType<HomeController.ItemComponentView>()
            .Select(c => c.Code)
            .Should().Contain(["lifetube", "manatube"]);
        model.BuildsInto.OfType<HomeController.ItemBuildsIntoView>()
            .Select(b => b.Code)
            .Should().Contain("nullstone");

        List<HomeController.ItemCraftingTreeNode>? backwardTree =
            controller.ViewBag.BackwardTree as List<HomeController.ItemCraftingTreeNode>;
        List<HomeController.ItemCraftingTreeNode>? forwardTree =
            controller.ViewBag.ForwardTree as List<HomeController.ItemCraftingTreeNode>;

        backwardTree.Should().NotBeNull();
        backwardTree!.Select(c => c.Code).Should().Contain(["lifetube", "manatube"]);

        forwardTree.Should().NotBeNull();
        forwardTree!.Select(c => c.Code).Should().Contain("nullstone");

        typeof(HomeController)
            .GetField("_cachedHeroItems", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)
            ?.SetValue(null, null);

        // Also assert full HTTP Razor HTML rendering of /item/sustainer and /api/items BuildsInto
        HttpClient authClient = await _fixture.CreateAuthenticatedClientAsync();
        HttpResponseMessage itemHtmlResp = await authClient.GetAsync("/item/sustainer");
        itemHtmlResp.StatusCode.Should().Be(HttpStatusCode.OK);
        string itemHtml = await itemHtmlResp.Content.ReadAsStringAsync();
        itemHtml.Should().Contain("id=\"crafting-tree\"");
        itemHtml.Should().Contain("moba-tree-up");
        itemHtml.Should().Contain("moba-tree-down");
        itemHtml.Should().Contain("Lifetube");
        itemHtml.Should().Contain("Manatube");
        itemHtml.Should().Contain("Null Stone");

        HttpResponseMessage apiItemsResp = await authClient.GetAsync("/api/items");
        apiItemsResp.StatusCode.Should().Be(HttpStatusCode.OK);
        using JsonDocument itemsDoc = JsonDocument.Parse(await apiItemsResp.Content.ReadAsStringAsync());
        JsonElement sustainerJson = itemsDoc.RootElement.EnumerateArray()
            .First(el => string.Equals(el.GetProperty("code").GetString(), "sustainer", StringComparison.OrdinalIgnoreCase));
        sustainerJson.GetProperty("components").GetArrayLength().Should().BeGreaterThanOrEqualTo(2);
        sustainerJson.GetProperty("buildsInto").GetArrayLength().Should().BeGreaterThanOrEqualTo(1);

        HttpResponseMessage itemsCatalogPageResp = await authClient.GetAsync("/items");
        itemsCatalogPageResp.StatusCode.Should().Be(HttpStatusCode.OK);
        string itemsCatalogHtml = await itemsCatalogPageResp.Content.ReadAsStringAsync();
        itemsCatalogHtml.Should().Contain("function extractItemRefCode(entry)");
        itemsCatalogHtml.Should().Contain("function isRecipeRef(entry)");
    }
}
