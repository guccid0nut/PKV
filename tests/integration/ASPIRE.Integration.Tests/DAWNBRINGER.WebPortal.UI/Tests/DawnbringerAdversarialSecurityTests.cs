// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.IO;
using System.Security.Claims;
using DAWNBRINGER.WebPortal.UI.Controllers;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.Razor;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.AspNetCore.Mvc.ViewEngines;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using NEXUS.Contracts.Clients;
using Xunit;

namespace ASPIRE.Tests.DAWNBRINGER.WebPortal.UI.Tests;

[Collection("DawnbringerE2E")]
public class DawnbringerAdversarialSecurityTests(DawnbringerE2ETestFixture fixture)
{
    private readonly DawnbringerE2ETestFixture _fixture = fixture;

    private class DummyRouter : IRouter
    {
        public VirtualPathData? GetVirtualPath(VirtualPathContext context) => new(this, $"/{context.Values["controller"]}/{context.Values["action"]}");
        public Task RouteAsync(RouteContext context) => Task.CompletedTask;
    }

    private async Task<string> RenderViewToStringAsync<TModel>(
        string viewPath,
        TModel? model,
        ViewDataDictionary? customViewData = null,
        Action<HttpContext>? configureHttp = null)
    {
        using IServiceScope scope = _fixture.Services.CreateScope();
        IRazorViewEngine viewEngine = scope.ServiceProvider.GetRequiredService<IRazorViewEngine>();
        ITempDataProvider tempDataProvider = scope.ServiceProvider.GetRequiredService<ITempDataProvider>();

        DefaultHttpContext httpContext = new() { RequestServices = scope.ServiceProvider };
        configureHttp?.Invoke(httpContext);

        RouteData routeData = new();
        routeData.Routers.Add(new DummyRouter());
        ActionContext actionContext = new(httpContext, routeData, new ActionDescriptor());

        ViewEngineResult viewResult = viewEngine.GetView(null, viewPath, isMainPage: false);
        if (!viewResult.Success)
        {
            viewResult = viewEngine.FindView(actionContext, viewPath, isMainPage: false);
        }

        if (!viewResult.Success)
        {
            throw new InvalidOperationException($"Could not find view: {viewPath}. Locations: {string.Join(", ", viewResult.SearchedLocations)}");
        }

        ViewDataDictionary viewData = customViewData ?? new ViewDataDictionary<TModel>(new EmptyModelMetadataProvider(), new ModelStateDictionary());
        if (model != null && viewData.Model == null)
        {
            viewData.Model = model;
        }

        using StringWriter writer = new();
        ViewContext viewContext = new(
            actionContext,
            viewResult.View,
            viewData,
            new TempDataDictionary(httpContext, tempDataProvider),
            writer,
            new HtmlHelperOptions()
        );

        await viewResult.View.RenderAsync(viewContext);
        return writer.ToString();
    }

    // =========================================================================
    // SUITE 1: HeroGuideDto BOUNDARY AND MALFORMED INPUTS
    // =========================================================================

    [Fact]
    public async Task Guide_Index_RendersSuccessfully_WithNullAndEmptyItemLists()
    {
        List<HeroGuideDto> guides =
        [
            new HeroGuideDto
            {
                ID = 101,
                Name = "Null Items Build",
                HeroIdentifier = "Hero_Panda",
                HeroName = "Pandamonium",
                StartingItems = null,
                EarlyGameItems = null,
                CoreItems = null,
                LuxuryItems = null,
                AbilityQueue = null,
                AuthorAccountName = "Tester",
                Public = true,
                Featured = false,
                Rating = 4.5f,
                Upvotes = 10,
                Downvotes = 2
            },
            new HeroGuideDto
            {
                ID = 102,
                Name = "Empty Strings Build",
                HeroIdentifier = "Hero_Vindicator",
                HeroName = "Vindicator",
                StartingItems = "",
                EarlyGameItems = "[]",
                CoreItems = "   ",
                LuxuryItems = "|||,,,",
                AbilityQueue = "",
                AuthorAccountName = "Tester2",
                Public = true,
                Featured = true,
                Rating = 5.0f,
                Upvotes = 50,
                Downvotes = 0
            }
        ];

        string html = await RenderViewToStringAsync("/Views/Guide/Index.cshtml", (IReadOnlyList<HeroGuideDto>)guides);
        html.Should().NotBeNullOrWhiteSpace();
        html.Should().Contain("Null Items Build");
        html.Should().Contain("Empty Strings Build");
    }

    [Fact]
    public async Task Guide_Index_RendersSuccessfully_WithNullAuthorNameAndZeroVotes()
    {
        List<HeroGuideDto> guides =
        [
            new HeroGuideDto
            {
                ID = 103,
                Name = "Anonymous Zero Votes Build",
                HeroIdentifier = "Hero_Devourer",
                HeroName = "Devourer",
                AuthorAccountName = null!,
                Author = null,
                Upvotes = 0,
                Downvotes = 0,
                Rating = 0.0f,
                Public = true,
                Featured = false
            }
        ];

        string html = await RenderViewToStringAsync("/Views/Guide/Index.cshtml", (IReadOnlyList<HeroGuideDto>)guides);
        html.Should().NotBeNullOrWhiteSpace();
        html.Should().Contain("Anonymous Zero Votes Build");
        html.Should().Contain("Project KONGOR"); // Fallback author
        html.Should().Contain("(0 votes)");
    }

    [Fact]
    public async Task Guide_Index_AuthorObjectHasNullName_ThrowsNullReferenceException()
    {
        // Adversarial: guide.Featured is true and guide.Author is non-null, but Author.Name is null
        List<HeroGuideDto> guides =
        [
            new HeroGuideDto
            {
                ID = 104,
                Name = "Official Build With Null Author Name",
                HeroIdentifier = "Hero_Devourer",
                HeroName = "Devourer",
                Featured = true,
                Author = new AccountDto { ID = 99, Name = null! },
                AuthorAccountName = null!,
                Public = true
            }
        ];

        // Line 276 of Index.cshtml: !guide.Author.Name.Equals("KONGOR", ...)
        var act = async () => await RenderViewToStringAsync("/Views/Guide/Index.cshtml", (IReadOnlyList<HeroGuideDto>)guides);
        await act.Should().ThrowAsync<NullReferenceException>();
    }

    [Fact]
    public async Task Guide_Detail_RendersSuccessfully_WithNullAndEmptyItemLists()
    {
        HeroGuideDto guide = new()
        {
            ID = 201,
            Name = "Detail Null Items Build",
            HeroIdentifier = "Hero_Vindicator",
            HeroName = "Vindicator",
            StartingItems = null,
            EarlyGameItems = null,
            CoreItems = null,
            LuxuryItems = null,
            AbilityQueue = null,
            AuthorAccountName = "ProBuilder",
            Intro = null,
            Content = null,
            Public = true,
            Featured = false,
            Rating = 0.0f,
            Upvotes = 0,
            Downvotes = 0
        };

        string html = await RenderViewToStringAsync("/Views/Guide/Detail.cshtml", guide);
        html.Should().NotBeNullOrWhiteSpace();
        html.Should().Contain("Detail Null Items Build");
        html.Should().Contain("(0 votes)");
    }

    [Fact]
    public async Task Guide_Detail_RendersSuccessfully_WithMalformedAbilityQueue()
    {
        HeroGuideDto guide = new()
        {
            ID = 202,
            Name = "Malformed Queue Build",
            HeroIdentifier = "Hero_Vindicator",
            HeroName = "Vindicator",
            AbilityQueue = "99|invalid|-5|Q|W|E|R|boost|stat|unknown_token",
            StartingItems = "Item_LoggersHatchet,Item_RunesOfTheBlight",
            Public = true
        };

        string html = await RenderViewToStringAsync("/Views/Guide/Detail.cshtml", guide);
        html.Should().NotBeNullOrWhiteSpace();
        html.Should().Contain("Ability Queue");
    }

    [Fact]
    public async Task Guide_GuideBuilder_RendersSuccessfully_WithNullModel()
    {
        string html = await RenderViewToStringAsync<HeroGuideDto?>("/Views/Guide/GuideBuilder.cshtml", null);
        html.Should().NotBeNullOrWhiteSpace();
        html.Should().Contain("Create Hero Guide");
        html.Should().Contain("HERO GUIDE STUDIO");
    }

    [Fact]
    public async Task Guide_GuideBuilder_RendersSuccessfully_WithExistingMalformedGuide()
    {
        HeroGuideDto guide = new()
        {
            ID = 301,
            Name = "Studio Edit Malformed Guide",
            HeroIdentifier = "Hero_Panda",
            HeroName = "Pandamonium",
            Intro = null,
            Content = null,
            StartingItems = null,
            EarlyGameItems = null,
            CoreItems = null,
            LuxuryItems = null,
            AbilityQueue = null
        };

        string html = await RenderViewToStringAsync<HeroGuideDto?>("/Views/Guide/GuideBuilder.cshtml", guide);
        html.Should().NotBeNullOrWhiteSpace();
        html.Should().Contain("Edit Guide: Studio Edit Malformed Guide");
    }

    // =========================================================================
    // SUITE 2: HeroDetailDto BOUNDARY AND MALFORMED INPUTS
    // =========================================================================

    [Fact]
    public async Task Codex_RendersSuccessfully_WithEmptyHeroList()
    {
        List<HeroDetailDto> emptyList = [];
        string html = await RenderViewToStringAsync("/Views/Home/Codex.cshtml", emptyList);
        html.Should().NotBeNullOrWhiteSpace();
        html.Should().Contain("0 Champions Synchronized");
    }

    [Fact]
    public async Task Codex_RendersSuccessfully_WhenPrimaryAttributeIsNull()
    {
        List<HeroDetailDto> heroes =
        [
            new HeroDetailDto
            {
                ID = 501,
                HeroIdentifier = "Hero_Minimal",
                DisplayName = "Minimalist Hero",
                PrimaryAttribute = null!, // null PrimaryAttribute gracefully coalesces to "Strength" in Codex.cshtml
                AttackType = "Melee",
                IconPath = null,
                Roles = []
            }
        ];

        string html = await RenderViewToStringAsync("/Views/Home/Codex.cshtml", heroes);
        html.Should().NotBeNullOrWhiteSpace();
        html.Should().Contain("Minimalist Hero");
    }

    [Fact]
    public async Task HeroDetail_RendersSuccessfully_WithNullTelemetryAndRoles()
    {
        ViewDataDictionary viewData = new(new EmptyModelMetadataProvider(), new ModelStateDictionary())
        {
            ["HeroIdentifier"] = "Hero_Taint",
            ["DisplayName"] = "Gravekeeper",
            ["HeroDetailEntity"] = new HeroDetailDto
            {
                ID = 601,
                HeroIdentifier = "Hero_Taint",
                DisplayName = "Gravekeeper",
                PrimaryAttribute = "Strength",
                Role = null!,
                Roles = null!,
                AttackType = "Melee",
                ArmorMitigation = null!,
                IconPath = null,
                SplashImagePath = null,
                Lore = null,
                AbilitiesJson = null
            }
        };

        string html = await RenderViewToStringAsync<object?>("/Views/Home/HeroDetail.cshtml", null, viewData);
        html.Should().NotBeNullOrWhiteSpace();
        html.Should().Contain("Gravekeeper");
    }

    [Fact]
    public async Task HeroDetail_RendersSuccessfully_WithExtremeAttributeNumbers()
    {
        ViewDataDictionary viewData = new(new EmptyModelMetadataProvider(), new ModelStateDictionary())
        {
            ["HeroIdentifier"] = "Hero_Taint",
            ["DisplayName"] = "Gravekeeper",
            ["HeroDetailEntity"] = new HeroDetailDto
            {
                ID = 602,
                HeroIdentifier = "Hero_Taint",
                DisplayName = "Gravekeeper",
                PrimaryAttribute = "Strength",
                BaseStrength = 999999.99,
                StrengthGrowth = 999.99,
                BaseAgility = -500.0,
                AgilityGrowth = 0.0,
                BaseIntelligence = 1000000.0,
                IntelligenceGrowth = 1.0,
                Armor = 1234567.89,
                ArmorMitigation = "99.9%",
                MinDamage = -100,
                MaxDamage = 999999,
                MovementSpeed = 10000,
                AttackRange = 5000
            }
        };

        string html = await RenderViewToStringAsync<object?>("/Views/Home/HeroDetail.cshtml", null, viewData);
        html.Should().NotBeNullOrWhiteSpace();
        html.Should().Contain("999999");
        html.Should().Contain("1234567.89");
    }

    [Fact]
    public async Task HeroDetail_ThrowsNullReferenceException_WhenGuideNameIsNull()
    {
        // Adversarial: HeroDetail page renders guides in a dropdown and script tag:
        // Line 580 & 881: g.Name.Replace("\\'", "'") throws NullReferenceException if Name is null
        ViewDataDictionary viewData = new(new EmptyModelMetadataProvider(), new ModelStateDictionary())
        {
            ["HeroIdentifier"] = "Hero_Taint",
            ["DisplayName"] = "Gravekeeper",
            ["Guide"] = new HeroGuideDto
            {
                ID = 701,
                Name = null!, // Malformed guide with null Name
                HeroIdentifier = "Hero_Taint",
                HeroName = "Gravekeeper",
                StartingItems = "Item_LoggersHatchet",
                Public = true
            }
        };

        var act = async () => await RenderViewToStringAsync<object?>("/Views/Home/HeroDetail.cshtml", null, viewData);
        await act.Should().ThrowAsync<NullReferenceException>();
    }

    // =========================================================================
    // SUITE 3: HeroItemDto BOUNDARY AND MALFORMED INPUTS
    // =========================================================================

    [Fact]
    public async Task ItemDetail_RendersSuccessfully_WithZeroCost()
    {
        HeroItemDto item = new()
        {
            ID = 801,
            ItemIdentifier = "Item_FreeTotem",
            DisplayName = "Free Minor Totem",
            Description = "Costs zero gold.",
            Cost = 0,
            RecipeCost = 0,
            Category = "Supplies",
            IconPath = "/images/items/totem.png",
            IsActive = true
        };

        string html = await RenderViewToStringAsync("/Views/Home/ItemDetail.cshtml", item);
        html.Should().NotBeNullOrWhiteSpace();
        html.Should().Contain("💰 0g");
        html.Should().Contain("Free Minor Totem");
    }

    [Fact]
    public async Task ItemDetail_RendersSuccessfully_WithNegativeCost()
    {
        HeroItemDto item = new()
        {
            ID = 802,
            ItemIdentifier = "Item_CursedBlade",
            DisplayName = "Cursed Blade",
            Description = "Negative gold cost item for testing.",
            Cost = -500,
            RecipeCost = -100,
            Category = "Weapons",
            IconPath = "/images/items/blade.png",
            IsActive = false
        };

        string html = await RenderViewToStringAsync("/Views/Home/ItemDetail.cshtml", item);
        html.Should().NotBeNullOrWhiteSpace();
        html.Should().Contain("-500");
        html.Should().Contain("DISABLED");
    }

    [Fact]
    public async Task ItemDetail_RendersSuccessfully_WithNullDescriptionAndFlavor()
    {
        HeroItemDto item = new()
        {
            ID = 803,
            ItemIdentifier = "Item_BareItem",
            DisplayName = "Bare Item",
            Description = null!,
            Flavor = null!,
            Upgrade = null!,
            Cost = 100,
            Category = "Relics"
        };

        string html = await RenderViewToStringAsync("/Views/Home/ItemDetail.cshtml", item);
        html.Should().NotBeNullOrWhiteSpace();
        html.Should().Contain("Bare Item");
        html.Should().NotContain("SHOP LORE"); // Flavor section hidden when null
    }

    // =========================================================================
    // SUITE 4: CONTROLLER ACTION STRESS TESTS
    // =========================================================================

    [Theory]
    [InlineData(-10)]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(99999)]
    public async Task GuideController_Index_HandlesExtremePageNumbersGracefully(int page)
    {
        await using var context = TestDbContextFactory.CreateInMemoryContext();
        var userApi = TestApiClientFactory.CreateUserApiClient(context);
        var guideApi = TestApiClientFactory.CreateGuideApiClient(context);
        GuideController controller = new(guideApi, userApi, Microsoft.Extensions.Logging.Abstractions.NullLogger<GuideController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };

        IActionResult result = await controller.Index(page: page);
        result.Should().BeOfType<ViewResult>();
        ViewResult viewResult = (ViewResult)result;
        viewResult.Model.Should().NotBeNull();
        viewResult.Model.Should().BeAssignableTo<IReadOnlyList<HeroGuideDto>>();
    }

    [Theory]
    [InlineData("featured")]
    [InlineData("rating")]
    [InlineData("newest")]
    [InlineData("unknown_sort_filter_xyz")]
    [InlineData("")]
    [InlineData(null)]
    public async Task GuideController_Index_HandlesMalformedSortGracefully(string? sort)
    {
        await using var context = TestDbContextFactory.CreateInMemoryContext();
        var userApi = TestApiClientFactory.CreateUserApiClient(context);
        var guideApi = TestApiClientFactory.CreateGuideApiClient(context);
        GuideController controller = new(guideApi, userApi, Microsoft.Extensions.Logging.Abstractions.NullLogger<GuideController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };

        IActionResult result = await controller.Index(sort: sort);
        result.Should().BeOfType<ViewResult>();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(-999)]
    [InlineData(999999)]
    public async Task GuideController_Details_NonExistentId_ReturnsNotFound(int id)
    {
        await using var context = TestDbContextFactory.CreateInMemoryContext();
        var userApi = TestApiClientFactory.CreateUserApiClient(context);
        var guideApi = TestApiClientFactory.CreateGuideApiClient(context);
        GuideController controller = new(guideApi, userApi, Microsoft.Extensions.Logging.Abstractions.NullLogger<GuideController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };

        IActionResult result = await controller.Details(id);
        result.Should().BeOfType<NotFoundResult>();
    }

    [Fact]
    public async Task HomeController_ItemDetail_NonExistentCode_RedirectsToItems()
    {
        await using var context = TestDbContextFactory.CreateInMemoryContext();
        var userApi = TestApiClientFactory.CreateUserApiClient(context);
        var adminApi = TestApiClientFactory.CreateAdminApiClient(context);
        var statsApi = TestApiClientFactory.CreatePlayerStatsApiClient(context);
        var guideApi = TestApiClientFactory.CreateGuideApiClient(context);
        var config = new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build();
        HomeController controller = new(statsApi, userApi, guideApi, adminApi, config)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };

        IActionResult result = await controller.ItemDetail("NonExistentItemCode_99999");
        result.Should().BeOfType<RedirectToActionResult>();
        RedirectToActionResult redirect = (RedirectToActionResult)result;
        redirect.ActionName.Should().Be("Items");
    }

    // =========================================================================
    // SUITE 5: HIGH-ITERATION STRESS TEST HARNESS (< 200 iterations for sub-second speed)
    // =========================================================================

    [Fact]
    public async Task DawnbringerAdversarialSecurity_StressTest_RapidViewRenderingUnderAdversarialInputs()
    {
        for (int i = 0; i < 50; i++)
        {
            HeroGuideDto guide = new()
            {
                ID = 1000 + i,
                Name = $"Stress Guide #{i}",
                HeroIdentifier = i % 2 == 0 ? "Hero_Panda" : "Hero_Vindicator",
                HeroName = i % 2 == 0 ? "Pandamonium" : "Vindicator",
                StartingItems = i % 3 == 0 ? null : "Item_LoggersHatchet",
                EarlyGameItems = i % 4 == 0 ? null : "Item_Marchers",
                CoreItems = i % 5 == 0 ? null : "Item_PortalKey",
                LuxuryItems = i % 6 == 0 ? null : "Item_Immunity",
                AbilityQueue = i % 2 == 0 ? "1|2|3|4|5" : null,
                AuthorAccountName = i % 2 == 0 ? $"Author_{i}" : null!,
                Upvotes = i * 10,
                Downvotes = i,
                Rating = (float)(i % 50) / 10.0f,
                Public = true,
                Featured = (i % 5 == 0)
            };

            string html = await RenderViewToStringAsync("/Views/Guide/Detail.cshtml", guide);
            html.Should().NotBeNullOrWhiteSpace();
        }
    }
}
