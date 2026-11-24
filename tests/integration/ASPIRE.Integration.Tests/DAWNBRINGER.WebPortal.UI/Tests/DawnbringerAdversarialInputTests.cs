// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.IO;
using System.Net;
using System.Reflection;
using System.Text.Json;
using ASPIRE.Common.Utilities;
using DAWNBRINGER.WebPortal.UI.Controllers;
using FluentAssertions;
using MERRICK.DatabaseContext.Entities.Game;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Moq;
using NEXUS.Contracts.Clients;
using Xunit;

namespace ASPIRE.Tests.DAWNBRINGER.WebPortal.UI.Tests;

[Collection("DawnbringerE2E")]
public sealed class DawnbringerAdversarialInputTests
{
    private readonly DawnbringerE2ETestFixture _fixture;
    private static readonly Lock _seedLock = new();

    public DawnbringerAdversarialInputTests(DawnbringerE2ETestFixture fixture)
    {
        _fixture = fixture;
        EnsureCatalogSeeded(_fixture.DbContext);
        ResetHomeControllerCache();
    }

    private static void ResetHomeControllerCache()
    {
        typeof(HomeController).GetField("_cachedHeroItems", BindingFlags.Static | BindingFlags.NonPublic)?.SetValue(null, null);
        typeof(HomeController).GetField("_cachedHeroDetails", BindingFlags.Static | BindingFlags.NonPublic)?.SetValue(null, null);
    }

    private static void EnsureCatalogSeeded(MerrickContext db)
    {
        lock (_seedLock)
        {
            if (db.HeroItems.Count() < 174)
            {
                string[] itemPaths =
                [
                    global::MERRICK.DatabaseContext.Data.DataFiles.HeroItems,
                    Path.Combine(AppContext.BaseDirectory, "Data", "HeroItems.json"),
                    Path.Combine(Directory.GetCurrentDirectory(), "src", "libs", "database-context", "Data", "HeroItems.json")
                ];

                string? foundItemPath = itemPaths.FirstOrDefault(File.Exists);
                if (foundItemPath != null)
                {
                    string json = File.ReadAllText(foundItemPath);
                    List<HeroItem>? diskItems = JsonSerializer.Deserialize<List<HeroItem>>(json,
                        new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                    if (diskItems != null && diskItems.Count > 0)
                    {
                        HashSet<string> existingCodes = new(db.HeroItems.Select(i => i.Code), StringComparer.OrdinalIgnoreCase);
                        foreach (HeroItem item in diskItems)
                        {
                            if (!existingCodes.Contains(item.Code))
                            {
                                db.HeroItems.Add(new HeroItem
                                {
                                    Code = item.Code,
                                    Name = item.Name,
                                    Cost = item.Cost,
                                    Description = item.Description,
                                    Flavor = item.Flavor,
                                    IconPath = item.IconPath,
                                    ComponentCodesJson = item.ComponentCodesJson,
                                    RecipeCost = item.RecipeCost,
                                    Upgrade = item.Upgrade,
                                    Category = item.Category,
                                    IsActive = item.IsActive
                                });
                            }
                        }
                        db.SaveChanges();
                    }
                }
            }

            if (db.HeroDetails.Count() < 139)
            {
                string[] heroPaths =
                [
                    global::MERRICK.DatabaseContext.Data.DataFiles.HeroDetails,
                    Path.Combine(AppContext.BaseDirectory, "Data", "HeroDetails.json"),
                    Path.Combine(Directory.GetCurrentDirectory(), "src", "libs", "database-context", "Data", "HeroDetails.json")
                ];

                string? foundHeroPath = heroPaths.FirstOrDefault(File.Exists);
                if (foundHeroPath != null)
                {
                    string json = File.ReadAllText(foundHeroPath);
                    List<HeroDetail>? diskHeroes = JsonSerializer.Deserialize<List<HeroDetail>>(json,
                        new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                    if (diskHeroes != null && diskHeroes.Count > 0)
                    {
                        HashSet<string> existingCodes = new(db.HeroDetails.Select(h => h.HeroCode), StringComparer.OrdinalIgnoreCase);
                        foreach (HeroDetail h in diskHeroes)
                        {
                            if (!existingCodes.Contains(h.HeroCode))
                            {
                                db.HeroDetails.Add(new HeroDetail
                                {
                                    HeroCode = h.HeroCode,
                                    DisplayName = h.DisplayName,
                                    PrimaryAttribute = h.PrimaryAttribute,
                                    AttackType = h.AttackType,
                                    BaseStrength = h.BaseStrength,
                                    StrengthGrowth = h.StrengthGrowth,
                                    BaseAgility = h.BaseAgility,
                                    AgilityGrowth = h.AgilityGrowth,
                                    BaseIntelligence = h.BaseIntelligence,
                                    IntelligenceGrowth = h.IntelligenceGrowth,
                                    Armor = h.Armor,
                                    ArmorMitigation = h.ArmorMitigation,
                                    MinDamage = h.MinDamage,
                                    MaxDamage = h.MaxDamage,
                                    MovementSpeed = h.MovementSpeed,
                                    IconPath = h.IconPath,
                                    SplashImagePath = h.SplashImagePath,
                                    Lore = h.Lore,
                                    AbilitiesJson = h.AbilitiesJson
                                });
                            }
                        }
                        db.SaveChanges();
                    }
                }
            }
        }
    }

    private static HomeController CreateDirectController(MerrickContext context)
    {
        IConfiguration config = new ConfigurationBuilder().Build();
        IUserApiClient userApi = Infrastructure.TestApiClientFactory.CreateUserApiClient(context);
        IAdminApiClient adminApi = Infrastructure.TestApiClientFactory.CreateAdminApiClient(context);
        IPlayerStatsApiClient statsApi = Infrastructure.TestApiClientFactory.CreatePlayerStatsApiClient(context);
        IGuideApiClient guideApi = Infrastructure.TestApiClientFactory.CreateGuideApiClient(context);
        HomeController controller = new(statsApi, userApi, guideApi, adminApi, config)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };
        return controller;
    }

    // =========================================================================
    // SUITE 1: ITEM COMPENDIUM & /api/items ADVERSARIAL STRESS-TESTS
    // =========================================================================

    [Fact]
    public async Task ApiItems_IsolatedContext_LoadsAll174Items()
    {
        ResetHomeControllerCache();
        await using MerrickContext freshContext = TestDbContextFactory.CreateInMemoryContext();
        HomeController controller = CreateDirectController(freshContext);

        IActionResult result = await controller.GetItems();

        result.Should().BeOfType<JsonResult>();
        JsonResult jsonResult = (JsonResult)result;
        List<HeroItemDto> items = jsonResult.Value.Should().BeOfType<List<HeroItemDto>>().Subject;

        items.Should().HaveCount(174, "in an isolated context, the full 174 items catalog is loaded");

        foreach (HeroItemDto item in items)
        {
            item.Code.Should().NotBeNullOrWhiteSpace();
            item.Name.Should().NotBeNullOrWhiteSpace();
            item.Cost.Should().BeGreaterOrEqualTo(0);
            item.Category.Should().NotBeNull();
        }
    }

    [Fact]
    public async Task ApiItems_DiskFallback_LoadsAll174Items_WhenClientThrowsException()
    {
        ResetHomeControllerCache();

        Mock<IPlayerStatsApiClient> failingClient = new();
        failingClient.Setup(c => c.GetAllHeroItemsAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("Web API offline / connection refused"));

        IConfiguration config = new ConfigurationBuilder().Build();
        Mock<IUserApiClient> userApi = new();
        Mock<IAdminApiClient> adminApi = new();
        Mock<IGuideApiClient> guideApi = new();

        HomeController controller = new(failingClient.Object, userApi.Object, guideApi.Object, adminApi.Object, config)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };

        IActionResult result = await controller.GetItems();

        result.Should().BeOfType<JsonResult>();
        JsonResult jsonResult = (JsonResult)result;
        List<HeroItemDto> items = jsonResult.Value.Should().BeOfType<List<HeroItemDto>>().Subject;

        items.Should().HaveCount(174, "disk fallback pipeline must load all 174 items when upstream API is offline");
    }

    [Fact]
    public async Task ApiItems_EdgeCases_LoggersHatchet_HasAccurateCostAndCategory()
    {
        await using MerrickContext freshContext = TestDbContextFactory.CreateInMemoryContext();
        HomeController controller = CreateDirectController(freshContext);
        IActionResult result = await controller.GetItems();
        List<HeroItemDto> items = (List<HeroItemDto>)((JsonResult)result).Value!;

        HeroItemDto? hatchet = items.FirstOrDefault(i =>
            i.Code.Equals("loggershatchet", StringComparison.OrdinalIgnoreCase) ||
            i.Code.Equals("Item_LoggersHatchet", StringComparison.OrdinalIgnoreCase));

        hatchet.Should().NotBeNull("Logger's Hatchet must be present in the catalog");
        hatchet!.Name.Should().Be("Logger's Hatchet");
        hatchet.Cost.Should().Be(150);
        hatchet.Category.Should().Be("Weapons");
        hatchet.IsActive.Should().BeTrue();
    }

    [Fact]
    public async Task ApiItems_EdgeCases_ItemsWithRecipeCost_ResolvedAccurately()
    {
        await using MerrickContext freshContext = TestDbContextFactory.CreateInMemoryContext();
        HomeController controller = CreateDirectController(freshContext);
        IActionResult result = await controller.GetItems();
        List<HeroItemDto> items = (List<HeroItemDto>)((JsonResult)result).Value!;

        List<HeroItemDto> recipeItems = items.Where(i => i.RecipeCost > 0).ToList();
        recipeItems.Should().HaveCount(71, "there must be exactly 71 items with recipe cost > 0");

        HeroItemDto? astrolabe = recipeItems.FirstOrDefault(i => i.Code.Equals("astrolabe", StringComparison.OrdinalIgnoreCase));
        astrolabe.Should().NotBeNull();
        astrolabe!.RecipeCost.Should().Be(710);
        astrolabe.Cost.Should().Be(1750);
    }

    [Fact]
    public async Task ApiItems_EdgeCases_ZeroCostItems_HandledCorrectly()
    {
        await using MerrickContext freshContext = TestDbContextFactory.CreateInMemoryContext();
        HomeController controller = CreateDirectController(freshContext);
        IActionResult result = await controller.GetItems();
        List<HeroItemDto> items = (List<HeroItemDto>)((JsonResult)result).Value!;

        List<HeroItemDto> zeroCostItems = items.Where(i => i.Cost == 0).ToList();
        zeroCostItems.Should().HaveCount(3, "there must be exactly 3 zero-cost items (Kongor's Bananas, Spyglass, Token of Life)");

        List<string> zeroCodes = zeroCostItems.Select(i => i.Code.ToLowerInvariant()).ToList();
        zeroCodes.Should().Contain("kongorsbananas");
        zeroCodes.Should().Contain("spyglass");
        zeroCodes.Should().Contain("tokenoflife");
    }

    [Fact]
    public async Task ApiItems_EdgeCases_UpgradedItems_PopulatedWithUpgradeLore()
    {
        await using MerrickContext freshContext = TestDbContextFactory.CreateInMemoryContext();
        HomeController controller = CreateDirectController(freshContext);
        IActionResult result = await controller.GetItems();
        List<HeroItemDto> items = (List<HeroItemDto>)((JsonResult)result).Value!;

        List<HeroItemDto> upgradedItems = items.Where(i => !string.IsNullOrWhiteSpace(i.Upgrade)).ToList();
        upgradedItems.Should().HaveCount(41, "there must be exactly 41 upgraded/upgradable items in the catalog");

        HeroItemDto? sheepstick = upgradedItems.FirstOrDefault(i => i.Code.Equals("kuldrassheepstick", StringComparison.OrdinalIgnoreCase));
        sheepstick.Should().NotBeNull();
        sheepstick!.Upgrade.Should().Contain("hex");
    }

    [Theory]
    [InlineData("Item_LoggersHatchet", "Logger's Hatchet")]
    [InlineData("loggershatchet", "Logger's Hatchet")]
    [InlineData("Logger's Hatchet", "Logger's Hatchet")]
    [InlineData("Item_PortalKey", "Portal Key")]
    [InlineData("portalkey", "Portal Key")]
    [InlineData("Item_BlightStones", "Blight Stones")]
    [InlineData("blightstones", "Blight Stones")]
    [InlineData("astrolabe", "Astrolabe")]
    [InlineData("kongorsbananas", "Kongor's Bananas")]
    public async Task ItemDetail_ResolvesItem_WithAndWithoutItemPrefix_AndDisplayName(string queryCode, string expectedName)
    {
        await using MerrickContext freshContext = TestDbContextFactory.CreateInMemoryContext();
        HomeController controller = CreateDirectController(freshContext);

        IActionResult result = await controller.ItemDetail(queryCode);

        result.Should().BeOfType<ViewResult>();
        ViewResult viewResult = (ViewResult)result;
        HeroItemDto item = viewResult.Model.Should().BeOfType<HeroItemDto>().Subject;
        item.Name.Should().Be(expectedName);
    }

    [Theory]
    [InlineData("NonExistentItem_XYZ_99999")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public async Task ItemDetail_NonExistentItem_GracefullyRedirectsToItems_Zero500(string? queryCode)
    {
        await using MerrickContext freshContext = TestDbContextFactory.CreateInMemoryContext();
        HomeController controller = CreateDirectController(freshContext);

        IActionResult result = await controller.ItemDetail(queryCode!);

        result.Should().BeOfType<RedirectToActionResult>();
        RedirectToActionResult redirect = (RedirectToActionResult)result;
        redirect.ActionName.Should().Be("Items");
    }

    [Fact]
    public async Task ApiItems_HighConcurrencyRapidRepeatedCalls_SucceedsUnderLoad()
    {
        await using MerrickContext freshContext = TestDbContextFactory.CreateInMemoryContext();
        HomeController controller = CreateDirectController(freshContext);

        Task<IActionResult>[] tasks = Enumerable.Range(0, 100)
            .Select(_ => controller.GetItems())
            .ToArray();

        IActionResult[] results = await Task.WhenAll(tasks);

        results.Should().HaveCount(100);
        foreach (IActionResult res in results)
        {
            res.Should().BeOfType<JsonResult>();
            List<HeroItemDto> items = (List<HeroItemDto>)((JsonResult)res).Value!;
            items.Count.Should().BeGreaterOrEqualTo(174);
        }
    }

    // =========================================================================
    // SUITE 2: HERO CODEX & /api/heroes/{id}/abilities ADVERSARIAL STRESS-TESTS
    // =========================================================================

    [Fact]
    public async Task Codex_LoadsAll139Champions_WithValidContracts()
    {
        ResetHomeControllerCache();
        await using MerrickContext freshContext = TestDbContextFactory.CreateInMemoryContext();
        HomeController controller = CreateDirectController(freshContext);

        IActionResult result = await controller.Codex();

        result.Should().BeOfType<ViewResult>();
        ViewResult viewResult = (ViewResult)result;
        List<HeroDetailDto> heroes = viewResult.Model.Should().BeOfType<List<HeroDetailDto>>().Subject;

        heroes.Should().HaveCount(139, "exactly 139 champions must be present in the Hero Codex");

        foreach (HeroDetailDto hero in heroes)
        {
            hero.HeroCode.Should().NotBeNullOrWhiteSpace("each champion must possess a HeroCode");
            hero.DisplayName.Should().NotBeNullOrWhiteSpace("each champion must possess a DisplayName");
            hero.PrimaryAttribute.Should().NotBeNullOrWhiteSpace("each champion must possess a PrimaryAttribute");
        }
    }

    [Fact]
    public async Task Codex_DiskFallback_LoadsAll139Champions_WhenClientThrowsException()
    {
        ResetHomeControllerCache();

        Mock<IPlayerStatsApiClient> failingClient = new();
        failingClient.Setup(c => c.GetAllHeroDetailsAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("Web API connection refused"));

        IConfiguration config = new ConfigurationBuilder().Build();
        Mock<IUserApiClient> userApi = new();
        Mock<IAdminApiClient> adminApi = new();
        Mock<IGuideApiClient> guideApi = new();

        HomeController controller = new(failingClient.Object, userApi.Object, guideApi.Object, adminApi.Object, config)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };

        IActionResult result = await controller.Codex();

        result.Should().BeOfType<ViewResult>();
        ViewResult viewResult = (ViewResult)result;
        List<HeroDetailDto> heroes = viewResult.Model.Should().BeOfType<List<HeroDetailDto>>().Subject;

        heroes.Should().HaveCount(139, "disk fallback pipeline must load all 139 champions when upstream API is offline");
    }

    [Fact]
    public async Task Codex_VerifiesKeyEdgeCaseChampions_Present()
    {
        await using MerrickContext freshContext = TestDbContextFactory.CreateInMemoryContext();
        HomeController controller = CreateDirectController(freshContext);
        IActionResult result = await controller.Codex();
        List<HeroDetailDto> heroes = (List<HeroDetailDto>)((ViewResult)result).Model!;

        string[] keyHeroCodes =
        [
            "Hero_Accursed",
            "Hero_Taint",
            "Hero_Pyromancer",
            "Hero_Devourer",
            "Hero_Vindicator",
            "Hero_Hiro",
            "Hero_Bubbles",
            "Hero_FlintBeastwood",
            "Hero_Panda"
        ];

        foreach (string code in keyHeroCodes)
        {
            heroes.Any(h => h.HeroCode.Equals(code, StringComparison.OrdinalIgnoreCase))
                .Should().BeTrue($"key champion '{code}' must be present in the Codex");
        }
    }

    [Theory]
    [InlineData("Hero_Accursed", "Cauterize")]
    [InlineData("Hero_Pyromancer", "Phoenix Wave")]
    [InlineData("Hero_Devourer", "Guttling Hook")]
    [InlineData("Hero_Hiro", "Blade Frenzy")]
    public async Task GetHeroAbilitiesEndpoint_ResolvesHeroesWithAbilitiesJson_WhenDetailsCached(string heroIdentifier, string expectedQAbilityName)
    {
        // When hero details are warmed in memory, heroes with seeded AbilitiesJson resolve authentic abilities
        await using MerrickContext freshContext = TestDbContextFactory.CreateInMemoryContext();
        HomeController controller = CreateDirectController(freshContext);
        await controller.Codex(); // warms _cachedHeroDetails

        IActionResult result = controller.GetHeroAbilitiesEndpoint(heroIdentifier);

        result.Should().BeOfType<JsonResult>();
        List<HomeController.AbilityDto> abilities = ((JsonResult)result).Value.Should().BeOfType<List<HomeController.AbilityDto>>().Subject;

        abilities.Should().HaveCountGreaterOrEqualTo(4, "each champion must possess at least 4 abilities (Q, W, E, R)");
        abilities.Select(a => a.Key).Should().Contain(["Q", "W", "E", "R"]);

        HomeController.AbilityDto qAbility = abilities.First(a => a.Key == "Q");
        qAbility.Name.Should().Be(expectedQAbilityName);
        qAbility.Description.Should().NotBeNullOrWhiteSpace();
        qAbility.IconUrl.Should().NotBeNullOrWhiteSpace();
    }

    [Theory]
    [InlineData("Accursed", "Cauterize")]
    [InlineData("Pyromancer", "Phoenix Wave")]
    [InlineData("Devourer", "Guttling Hook")]
    [InlineData("Swiftblade", "Blade Frenzy")]
    public async Task GetHeroAbilitiesEndpoint_ResolvesHeroesWithoutHeroPrefix_WhenDetailsCached(string heroIdentifier, string expectedQAbilityName)
    {
        await using MerrickContext freshContext = TestDbContextFactory.CreateInMemoryContext();
        HomeController controller = CreateDirectController(freshContext);
        await controller.Codex(); // warms _cachedHeroDetails

        IActionResult result = controller.GetHeroAbilitiesEndpoint(heroIdentifier);

        result.Should().BeOfType<JsonResult>();
        List<HomeController.AbilityDto> abilities = ((JsonResult)result).Value.Should().BeOfType<List<HomeController.AbilityDto>>().Subject;

        abilities.Should().HaveCountGreaterOrEqualTo(4);
        HomeController.AbilityDto qAbility = abilities.First(a => a.Key == "Q");
        qAbility.Name.Should().Be(expectedQAbilityName);
    }

    [Fact]
    public void GetHeroAbilitiesEndpoint_ColdStart_ReturnsFallbackPlaceholder_DueToUncachedHeroDetails()
    {
        // Verifies that when _cachedHeroDetails is null on cold start, GetHeroAbilitiesEndpoint
        // automatically loads hero details and resolves authentic abilities
        ResetHomeControllerCache();
        using MerrickContext freshContext = TestDbContextFactory.CreateInMemoryContext();
        HomeController controller = CreateDirectController(freshContext);

        IActionResult result = controller.GetHeroAbilitiesEndpoint("Hero_Accursed");

        result.Should().BeOfType<JsonResult>();
        List<HomeController.AbilityDto> abilities = (List<HomeController.AbilityDto>)((JsonResult)result).Value!;

        abilities.Should().HaveCount(4);
        abilities[0].Key.Should().Be("Q");
        abilities[0].Name.Should().Be("Cauterize",
            "verifies cold-start hero detail loading ensures authentic abilities on cold start");
    }

    [Theory]
    [InlineData("Hero_Vindicator", "Sage's Lore")]
    [InlineData("Hero_Bubbles", "Shell Surf")]
    [InlineData("Hero_FlintBeastwood", "Explosive Flare")]
    [InlineData("Hero_Panda", "Flurry")]
    public async Task GetHeroAbilitiesEndpoint_DemonstratesLocalizationPathBug_ForUnseededHeroes(string heroIdentifier, string actualFallbackQName)
    {
        // Verifies that heroes whose abilities are stored in ability_metadata.json load authentic names
        await using MerrickContext freshContext = TestDbContextFactory.CreateInMemoryContext();
        HomeController controller = CreateDirectController(freshContext);
        await controller.Codex(); // warms _cachedHeroDetails

        IActionResult result = controller.GetHeroAbilitiesEndpoint(heroIdentifier);

        result.Should().BeOfType<JsonResult>();
        List<HomeController.AbilityDto> abilities = (List<HomeController.AbilityDto>)((JsonResult)result).Value!;

        abilities.Should().HaveCount(4);
        abilities[0].Key.Should().Be("Q");
        abilities[0].Name.Should().Be(actualFallbackQName,
            "verifies authentic abilities loaded from ability_metadata.json via directory search");
    }

    [Theory]
    [InlineData("InvalidHero_99999")]
    [InlineData("NonExistentHero")]
    [InlineData("999999")]
    [InlineData("   ")]
    [InlineData("Hero_!@#$%")]
    [InlineData("---")]
    public void GetHeroAbilitiesEndpoint_InvalidHeroIds_GracefullyReturnsFallbackAbilities_ZeroUnhandled500(string invalidId)
    {
        HomeController controller = CreateDirectController(_fixture.DbContext);

        IActionResult result = controller.GetHeroAbilitiesEndpoint(invalidId);

        result.Should().BeOfType<JsonResult>();
        List<HomeController.AbilityDto> abilities = (List<HomeController.AbilityDto>)((JsonResult)result).Value!;

        abilities.Should().HaveCount(4, "fallback generation must supply exactly 4 abilities (Q, W, E, R)");
        abilities.Select(a => a.Key).Should().Contain(["Q", "W", "E", "R"]);
        foreach (HomeController.AbilityDto ab in abilities)
        {
            ab.Name.Should().NotBeNullOrWhiteSpace();
            ab.Description.Should().NotBeNullOrWhiteSpace();
            ab.IconUrl.Should().NotBeNullOrWhiteSpace();
        }
    }

    [Fact]
    public void GetHeroAbilitiesEndpoint_HighConcurrencyRapidRepeatedCalls_SucceedsUnderLoad()
    {
        HomeController controller = CreateDirectController(_fixture.DbContext);
        string[] heroQueries = ["Hero_Vindicator", "Hero_Devourer", "Hero_Pyromancer", "Hero_Accursed", "Invalid_1", "Bubbles"];

        Parallel.For(0, 100, i =>
        {
            string query = heroQueries[i % heroQueries.Length];
            IActionResult result = controller.GetHeroAbilitiesEndpoint(query);
            result.Should().BeOfType<JsonResult>();
            List<HomeController.AbilityDto> abilities = (List<HomeController.AbilityDto>)((JsonResult)result).Value!;
            abilities.Should().HaveCountGreaterOrEqualTo(4);
        });
    }

    // =========================================================================
    // SUITE 3: HTTP INTEGRATION & ACCEPT HEADER ADVERSARIAL TESTS
    // =========================================================================

    [Fact]
    public async Task Http_ApiItems_Returns200WithCatalog()
    {
        HttpClient client = await _fixture.CreateAuthenticatedClientAsync();

        HttpResponseMessage response = await client.GetAsync("/api/items");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/json");

        string json = await response.Content.ReadAsStringAsync();
        List<HeroItemDto>? items = JsonSerializer.Deserialize<List<HeroItemDto>>(json,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        items.Should().NotBeNull();
        items!.Count.Should().BeGreaterOrEqualTo(174, "HTTP GET /api/items must return full catalog");
    }

    [Theory]
    [InlineData("application/json")]
    [InlineData("text/plain")]
    [InlineData("*/*")]
    [InlineData("application/xml")]
    [InlineData("image/png")]
    [InlineData("malformed/header;;q=bad")]
    public async Task Http_ApiItems_HandlesVariousAcceptHeaders_ReturnsHttp200(string acceptHeader)
    {
        HttpClient client = await _fixture.CreateAuthenticatedClientAsync();
        HttpRequestMessage request = new(HttpMethod.Get, "/api/items");
        request.Headers.TryAddWithoutValidation("Accept", acceptHeader);

        HttpResponseMessage response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        string body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("loggershatchet");
    }

    [Fact]
    public async Task Http_ApiItems_RapidConcurrentHttpCalls_AllReturn200()
    {
        HttpClient client = await _fixture.CreateAuthenticatedClientAsync();

        Task<HttpResponseMessage>[] tasks = Enumerable.Range(0, 50)
            .Select(_ => client.GetAsync("/api/items"))
            .ToArray();

        HttpResponseMessage[] responses = await Task.WhenAll(tasks);

        responses.Should().HaveCount(50);
        foreach (HttpResponseMessage resp in responses)
        {
            resp.StatusCode.Should().Be(HttpStatusCode.OK);
        }
    }

    [Fact]
    public async Task Http_HeroAbilities_Returns200WithAbilities()
    {
        HttpClient client = await _fixture.CreateAuthenticatedClientAsync();

        HttpResponseMessage response = await client.GetAsync("/api/heroes/Hero_Accursed/abilities");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/json");

        string json = await response.Content.ReadAsStringAsync();
        List<HomeController.AbilityDto>? abilities = JsonSerializer.Deserialize<List<HomeController.AbilityDto>>(json,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        abilities.Should().NotBeNull();
        abilities!.Should().HaveCount(4);
    }

    [Fact]
    public async Task Http_HeroAbilities_InvalidHero_Returns200Fallback_Zero500()
    {
        HttpClient client = await _fixture.CreateAuthenticatedClientAsync();

        HttpResponseMessage response = await client.GetAsync("/api/heroes/NonExistentHero999/abilities");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        string json = await response.Content.ReadAsStringAsync();
        List<HomeController.AbilityDto>? abilities = JsonSerializer.Deserialize<List<HomeController.AbilityDto>>(json,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        abilities.Should().NotBeNull();
        abilities!.Should().HaveCount(4);
    }

    [Fact]
    public async Task Http_Codex_Returns200WithCompleteMarkup()
    {
        HttpClient client = await _fixture.CreateAuthenticatedClientAsync();

        HttpResponseMessage response = await client.GetAsync("/Home/Codex");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        string markup = await response.Content.ReadAsStringAsync();
        markup.Should().Contain("<!DOCTYPE html>");
        markup.Should().Contain("Codex");
        markup.Should().NotContain("InvalidOperationException");
        markup.Should().NotContain("NullReferenceException");
    }

    [Fact]
    public async Task Http_Items_Returns200WithCompleteMarkup()
    {
        HttpClient client = await _fixture.CreateAuthenticatedClientAsync();

        HttpResponseMessage response = await client.GetAsync("/Home/Items");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        string markup = await response.Content.ReadAsStringAsync();
        markup.Should().Contain("<!DOCTYPE html>");
        markup.Should().Contain("Item Compendium");
        markup.Should().NotContain("InvalidOperationException");
        markup.Should().NotContain("NullReferenceException");
    }
}
