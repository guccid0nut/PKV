// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using DAWNBRINGER.WebPortal.UI.Controllers;
using MERRICK.DatabaseContext.Entities.Game;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NEXUS.Contracts.Clients;

namespace ASPIRE.Tests.DAWNBRINGER.WebPortal.UI.Tests;

[Collection("DawnbringerE2E")]
public sealed class WebPortalViewRenderingTests : IDisposable
{
    public WebPortalViewRenderingTests()
    {
        HomeController.InvalidateHeroDataCache();
    }

    public void Dispose()
    {
        HomeController.InvalidateHeroDataCache();
    }

    private static MerrickContext CreateInMemoryContext()
    {
        return TestDbContextFactory.CreateInMemoryContext();
    }

    private static HomeController CreateController(MerrickContext context)
    {
        IConfiguration config = new ConfigurationBuilder().Build();
        IUserApiClient userApi = TestApiClientFactory.CreateUserApiClient(context);
        IAdminApiClient adminApi = TestApiClientFactory.CreateAdminApiClient(context);
        IPlayerStatsApiClient statsApi = TestApiClientFactory.CreatePlayerStatsApiClient(context);
        IGuideApiClient guideApi = TestApiClientFactory.CreateGuideApiClient(context);
        HomeController controller = new(statsApi, userApi, guideApi, adminApi, config)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };
        return controller;
    }

    private static GuideController CreateGuideController(MerrickContext context, string userName = "TestUser")
    {
        IGuideApiClient guideApi = TestApiClientFactory.CreateGuideApiClient(context);
        IUserApiClient userApi = TestApiClientFactory.CreateUserApiClient(context);
        GuideController controller = new(guideApi, userApi, NullLogger<GuideController>.Instance)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new System.Security.Claims.ClaimsPrincipal(
                        new System.Security.Claims.ClaimsIdentity([
                            new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.Name, userName)
                        ], "TestAuth"))
                }
            }
        };
        return controller;
    }

    [Fact]
    public async Task WebPortalViewRendering_Index_ReturnsViewResult()
    {
        await using MerrickContext context = CreateInMemoryContext();
        HomeController controller = CreateController(context);

        IActionResult result = await controller.Index();

        Assert.IsType<ViewResult>(result);
    }

    [Trait("Category", "Manual")]
    [Fact]
    public async Task WebPortalViewRendering_Codex_ReturnsViewResult()
    {
        if (!NexusTestEnvironment.IsManualTestEnabled) return;

        await using MerrickContext context = CreateInMemoryContext();
        HomeController controller = CreateController(context);

        IActionResult result = await controller.Codex();

        Assert.IsType<ViewResult>(result);
    }

    [Fact]
    public async Task WebPortalViewRendering_Leaderboard_ReturnsViewResult()
    {
        await using MerrickContext context = CreateInMemoryContext();
        HomeController controller = CreateController(context);

        IActionResult result = await controller.Leaderboard();

        Assert.IsType<ViewResult>(result);
    }

    [Fact]
    public async Task WebPortalViewRendering_GetHeroesApi_ReturnsJsonResult()
    {
        await using MerrickContext context = CreateInMemoryContext();
        HomeController controller = CreateController(context);

        IActionResult result = await controller.GetHeroes();

        Assert.IsType<JsonResult>(result);
    }

    [Fact]
    public async Task WebPortalViewRendering_GetItemsApi_ReturnsJsonResult()
    {
        await using MerrickContext context = CreateInMemoryContext();
        HomeController controller = CreateController(context);

        IActionResult result = await controller.GetItems();

        Assert.IsType<JsonResult>(result);
    }

    [Fact]
    public async Task Codex_ReturnsViewResult_WithHeroDetailDtoModel()
    {
        await using MerrickContext context = CreateInMemoryContext();
        context.HeroDetails.Add(new HeroDetail
        {
            HeroCode = "Hero_Taint",
            DisplayName = "Gravekeeper",
            PrimaryAttribute = "Strength",
            IconPath = "/images/heroes/hero_taint.png",
            SplashImagePath = "/images/heroes/splash_taint.png",
            Lore = "Ancient Gravekeeper",
            AbilitiesJson = "[]"
        });
        await context.SaveChangesAsync();

        HomeController controller = CreateController(context);
        IActionResult result = await controller.Codex();

        ViewResult viewResult = Assert.IsType<ViewResult>(result);
        Assert.Equal("Codex", viewResult.ViewName);
        Assert.NotNull(viewResult.Model);
        Assert.IsAssignableFrom<List<HeroDetailDto>>(viewResult.Model);
    }

    [Fact]
    public async Task HeroDetail_ReturnsViewResult_WithHeroDetailDtoAndGuideDtoData()
    {
        await using MerrickContext context = CreateInMemoryContext();
        context.HeroDetails.Add(new HeroDetail
        {
            HeroCode = "Hero_Taint",
            DisplayName = "Gravekeeper",
            PrimaryAttribute = "Strength",
            IconPath = "/images/heroes/hero_taint.png",
            SplashImagePath = "/images/heroes/splash_taint.png",
            Lore = "Ancient Gravekeeper",
            AbilitiesJson = "[]"
        });
        await context.SaveChangesAsync();

        HomeController controller = CreateController(context);
        IActionResult result = await controller.HeroDetail("Hero_Taint");

        ViewResult viewResult = Assert.IsType<ViewResult>(result);
        Assert.NotNull(viewResult.ViewData["HeroDetailEntity"]);
        Assert.IsType<HeroDetailDto>(viewResult.ViewData["HeroDetailEntity"]);
        Assert.NotNull(controller.ViewBag.AllGuides);
        Assert.IsAssignableFrom<List<HeroGuideDto>>(controller.ViewBag.AllGuides);
    }

    [Fact]
    public async Task ItemDetail_ReturnsViewResult_WithHeroItemDtoModel()
    {
        typeof(HomeController).GetField("_cachedHeroItems", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)?.SetValue(null, null);
        await using MerrickContext context = CreateInMemoryContext();
        context.HeroItems.Add(new HeroItem
        {
            Code = "Item_RunesOfTheBlight",
            Name = "Runes of the Blight",
            Cost = 90,
            Description = "Consumable regen",
            Flavor = "Ancient lore",
            IconPath = "/images/items/blight.png"
        });
        await context.SaveChangesAsync();

        HomeController controller = CreateController(context);
        IActionResult result = await controller.ItemDetail("Item_RunesOfTheBlight");

        ViewResult viewResult = Assert.IsType<ViewResult>(result);
        Assert.NotNull(viewResult.Model);
        Assert.IsType<HeroItemDto>(viewResult.Model);
        Assert.NotNull(controller.ViewBag.Components);
        Assert.NotNull(controller.ViewBag.BuildsInto);
        Assert.NotNull(controller.ViewBag.LevelTiers);
    }

    [Fact]
    public async Task ItemDetail_BidirectionalRecipeNavigation_ResolvesComponentsAndBuildsInto()
    {
        typeof(HomeController).GetField("_cachedHeroItems", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)?.SetValue(null, null);
        await using MerrickContext context = CreateInMemoryContext();
        context.HeroItems.AddRange(
            new HeroItem
            {
                Code = "minortotem",
                Name = "Minor Totem",
                Cost = 53,
                Description = "Basic stat boost",
                Flavor = "Ancient totem lore",
                IconPath = "/images/items/minortotem.png",
                ComponentCodesJson = "[]"
            },
            new HeroItem
            {
                Code = "orbofzamos",
                Name = "Orb of Zamos",
                Cost = 300,
                Description = "Stats and spell damage",
                Flavor = "Orb lore",
                IconPath = "/images/items/orbofzamos.png",
                ComponentCodesJson = "[\"minortotem\", \"recipe\"]",
                RecipeCost = 250
            },
            new HeroItem
            {
                Code = "shieldbreaker",
                Name = "Shieldbreaker",
                Cost = 4600,
                Description = "Armor piercing weapon",
                Flavor = "Weapon lore",
                Upgrade = "Upgradable to Level 3 with Recipe Scroll (1,000 gold)",
                IconPath = "/images/items/shieldbreaker.png",
                ComponentCodesJson = "[\"broadsword\", \"warhammer\", \"recipe\"]",
                RecipeCost = 1000
            }
        );
        await context.SaveChangesAsync();

        HomeController controller = CreateController(context);

        // 1. Test backward path for Orb of Zamos (has components: minortotem + recipe scroll)
        IActionResult zamosResult = await controller.ItemDetail("orbofzamos");
        ViewResult zamosView = Assert.IsType<ViewResult>(zamosResult);
        List<HomeController.ItemComponentView> zamosComps = (List<HomeController.ItemComponentView>)controller.ViewBag.Components;
        Assert.Equal(2, zamosComps.Count);
        Assert.Contains(zamosComps, c => c.Code == "minortotem" && !c.IsRecipe);
        Assert.Contains(zamosComps, c => c.IsRecipe && c.Cost == "250");

        // 2. Test forward path for Minor Totem (builds into Orb of Zamos)
        IActionResult totemResult = await controller.ItemDetail("minortotem");
        ViewResult totemView = Assert.IsType<ViewResult>(totemResult);
        List<HomeController.ItemBuildsIntoView> totemBuilds = (List<HomeController.ItemBuildsIntoView>)controller.ViewBag.BuildsInto;
        Assert.NotEmpty(totemBuilds);
        Assert.Contains(totemBuilds, b => b.Code == "orbofzamos");

        // 3. Test multi-tier upgrade pricing for Shieldbreaker (Levels 1 to 3)
        IActionResult sbResult = await controller.ItemDetail("shieldbreaker");
        ViewResult sbView = Assert.IsType<ViewResult>(sbResult);
        List<HomeController.ItemLevelTierView> sbTiers = (List<HomeController.ItemLevelTierView>)controller.ViewBag.LevelTiers;
        Assert.Equal(3, sbTiers.Count);
        Assert.Equal(1, sbTiers[0].Level);
        Assert.Equal(4600, sbTiers[0].TotalCost);
        Assert.Equal(2, sbTiers[1].Level);
        Assert.Equal(5600, sbTiers[1].TotalCost);
        Assert.Equal(3, sbTiers[2].Level);
        Assert.Equal(6600, sbTiers[2].TotalCost);
    }

    [Fact]
    public async Task GetItems_ReturnsEnrichedItems_WithComponentsAndBuildsInto()
    {
        typeof(HomeController).GetField("_cachedHeroItems", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)?.SetValue(null, null);
        await using MerrickContext context = CreateInMemoryContext();
        context.HeroItems.AddRange(
            new HeroItem
            {
                Code = "minortotem",
                Name = "Minor Totem",
                Cost = 53,
                Description = "Basic stat boost",
                Flavor = "Ancient totem lore",
                IconPath = "/images/items/minortotem.png",
                ComponentCodesJson = "[]"
            },
            new HeroItem
            {
                Code = "orbofzamos",
                Name = "Orb of Zamos",
                Cost = 300,
                Description = "Stats and spell damage",
                Flavor = "Orb lore",
                IconPath = "/images/items/orbofzamos.png",
                ComponentCodesJson = "[\"minortotem\", \"recipe\"]",
                RecipeCost = 250
            }
        );
        await context.SaveChangesAsync();

        HomeController controller = CreateController(context);
        IActionResult result = await controller.GetItems();
        JsonResult jsonResult = Assert.IsType<JsonResult>(result);
        Assert.NotNull(jsonResult.Value);
    }

    [Fact]
    public async Task GuideController_Details_ReturnsDetailViewName()
    {
        await using MerrickContext context = CreateInMemoryContext();
        User user = new()
        {
            EmailAddress = "test@kongor.net",
            PBKDF2PasswordHash = "hash",
            SRPPasswordHash = "hash",
            SRPPasswordSalt = "salt",
            Role = new Role { Name = "User" }
        };
        Account account = new() { Name = "TestUser", User = user, IsMain = true };
        context.Accounts.Add(account);

        HeroGuide guide = new()
        {
            Name = "Pro Gravekeeper Guide",
            HeroIdentifier = "Hero_Taint",
            HeroName = "Gravekeeper",
            Author = account,
            Public = true,
            Featured = false,
            TimestampCreated = TimeProvider.System.GetUtcNow(),
            TimestampLastUpdated = TimeProvider.System.GetUtcNow()
        };
        context.HeroGuides.Add(guide);
        await context.SaveChangesAsync();

        GuideController controller = CreateGuideController(context, "TestUser");
        IActionResult result = await controller.Details(guide.ID);

        ViewResult viewResult = Assert.IsType<ViewResult>(result);
        Assert.Equal("Detail", viewResult.ViewName);
        Assert.NotNull(viewResult.Model);
        Assert.IsType<HeroGuideDto>(viewResult.Model);
    }

    [Fact]
    public async Task GuideController_Builder_ReturnsGuideBuilderViewName()
    {
        await using MerrickContext context = CreateInMemoryContext();
        User user = new()
        {
            EmailAddress = "builder@kongor.net",
            PBKDF2PasswordHash = "hash",
            SRPPasswordHash = "hash",
            SRPPasswordSalt = "salt",
            Role = new Role { Name = "User" }
        };
        Account account = new() { Name = "BuilderUser", User = user, IsMain = true };
        context.Accounts.Add(account);
        await context.SaveChangesAsync();

        GuideController controller = CreateGuideController(context, "BuilderUser");

        IActionResult result = await controller.Builder(null, "Hero_Taint");

        ViewResult viewResult = Assert.IsType<ViewResult>(result);
        Assert.Equal("GuideBuilder", viewResult.ViewName);
    }

    [Fact]
    public void ToolsController_MessageGenerator_ReturnsViewResult()
    {
        ToolsController controller = new(null!, NullLogger<ToolsController>.Instance);
        IActionResult result = controller.MessageGenerator();
        ViewResult viewResult = Assert.IsType<ViewResult>(result);
        Assert.Equal("HoN Color Message Generator", controller.ViewData["Title"]);
    }

    [Fact]
    public void WebPortalViewRendering_CompiledRazorViews_AreEmbeddedInWebUiAssembly()
    {
        // Protects against MSBuild graph poisoning (e.g. ProjectReference Properties="RazorCompileOnBuild=false")
        // stripping compiled Razor views and [ProvideApplicationPartFactory] from DAWNBRINGER.WebPortal.UI.dll,
        // which causes runtime InvalidOperationException: "The view 'Index' was not found."
        System.Reflection.Assembly webUiAssembly = typeof(HomeController).Assembly;

        ServiceCollection services = new();
        IMvcBuilder mvcBuilder = services
            .AddControllersWithViews()
            .AddApplicationPart(webUiAssembly);

        Microsoft.AspNetCore.Mvc.Razor.Compilation.ViewsFeature viewsFeature = new();
        mvcBuilder.PartManager.PopulateFeature(viewsFeature);

        HashSet<string> compiledPaths = viewsFeature.ViewDescriptors
            .Select(d => d.RelativePath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        Assert.True(
            compiledPaths.Count > 0,
            $"DAWNBRINGER.WebPortal.UI.dll ({webUiAssembly.Location}) contains 0 compiled Razor views. " +
            "Ensure RazorCompileOnBuild is never set to false on DAWNBRINGER.WebPortal.UI or its ProjectReferences, " +
            "then run: dotnet build src/services/web-ui/DAWNBRINGER.WebPortal.UI.csproj --no-incremental");

        string[] requiredViews =
        [
            "/Views/Home/Index.cshtml",
            "/Views/Shared/_Layout.cshtml",
            "/Views/Store/Index.cshtml",
            "/Views/Home/Codex.cshtml",
            "/Views/Home/Items.cshtml"
        ];

        foreach (string requiredView in requiredViews)
        {
            Assert.Contains(requiredView, compiledPaths);
        }
    }
}
