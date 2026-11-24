// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;
using ASPIRE.Common.Utilities;
using ASPIRE.Tests.Infrastructure;
using DAWNBRINGER.WebPortal.UI.Controllers;
using FluentAssertions;
using MERRICK.DatabaseContext.Entities.Game;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using NEXUS.Contracts.Clients;
using Xunit;

namespace ASPIRE.Tests.DAWNBRINGER.WebPortal.UI.Tests;

[Collection("DawnbringerE2E")]
public sealed class DawnbringerModelValidationTests
{
    private readonly DawnbringerE2ETestFixture _fixture;

    public DawnbringerModelValidationTests(DawnbringerE2ETestFixture fixture)
    {
        _fixture = fixture;
        ResetHomeControllerCache();
    }

    private static void ResetHomeControllerCache()
    {
        typeof(HomeController).GetField("_cachedHeroItems", BindingFlags.Static | BindingFlags.NonPublic)?.SetValue(null, null);
        typeof(HomeController).GetField("_cachedHeroDetails", BindingFlags.Static | BindingFlags.NonPublic)?.SetValue(null, null);
    }

    private static HomeController CreateDirectController(MerrickContext context)
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

    // =========================================================================
    // 1. COLD-START ABILITY RESOLUTION TESTS (WITHOUT VISITING CODEX FIRST)
    // =========================================================================

    [Fact]
    public void ColdStart_HeroVindicator_ResolvesAuthenticAbilities_WithoutVisitingCodex()
    {
        ResetHomeControllerCache();
        using MerrickContext freshContext = TestDbContextFactory.CreateInMemoryContext();
        HomeController controller = CreateDirectController(freshContext);

        IActionResult result = controller.GetHeroAbilitiesEndpoint("Hero_Vindicator");

        result.Should().BeOfType<JsonResult>();
        List<HomeController.AbilityDto> abilities = ((JsonResult)result).Value.Should().BeOfType<List<HomeController.AbilityDto>>().Subject;

        abilities.Should().HaveCount(4);
        abilities[0].Key.Should().Be("Q");
        abilities[0].Name.Should().Be("Sage's Lore", "Vindicator Q must resolve authentic HoN name 'Sage's Lore'");
        abilities[0].Name.Should().NotBe("Vindicator Q", "Must not return generic procedural fallback string");

        abilities[1].Key.Should().Be("W");
        abilities[1].Name.Should().Be("Master's Incantation");

        abilities[2].Key.Should().Be("E");
        abilities[2].Name.Should().Be("Glyph of Silence");

        abilities[3].Key.Should().Be("R");
        abilities[3].Name.Should().Be("Final Chapter");
    }

    [Fact]
    public void ColdStart_HeroBubbles_ResolvesAuthenticAbilities_WithoutVisitingCodex()
    {
        ResetHomeControllerCache();
        using MerrickContext freshContext = TestDbContextFactory.CreateInMemoryContext();
        HomeController controller = CreateDirectController(freshContext);

        IActionResult result = controller.GetHeroAbilitiesEndpoint("Hero_Bubbles");

        result.Should().BeOfType<JsonResult>();
        List<HomeController.AbilityDto> abilities = ((JsonResult)result).Value.Should().BeOfType<List<HomeController.AbilityDto>>().Subject;

        abilities.Should().HaveCount(4);
        abilities[0].Key.Should().Be("Q");
        abilities[0].Name.Should().Be("Shell Surf", "Bubbles Q must resolve authentic HoN name 'Shell Surf'");
        abilities[0].Name.Should().NotBe("Bubbles Q", "Must not return generic procedural fallback string");

        abilities[1].Key.Should().Be("W");
        abilities[1].Name.Should().Be("Song of the Sea");

        abilities[2].Key.Should().Be("E");
        abilities[2].Name.Should().Be("Take Cover");

        abilities[3].Key.Should().Be("R");
        abilities[3].Name.Should().Be("Kelp Field");
    }

    [Fact]
    public void ColdStart_HeroAccursed_ResolvesAuthenticAbilities_WithoutVisitingCodex()
    {
        ResetHomeControllerCache();
        using MerrickContext freshContext = TestDbContextFactory.CreateInMemoryContext();
        HomeController controller = CreateDirectController(freshContext);

        IActionResult result = controller.GetHeroAbilitiesEndpoint("Hero_Accursed");

        result.Should().BeOfType<JsonResult>();
        List<HomeController.AbilityDto> abilities = ((JsonResult)result).Value.Should().BeOfType<List<HomeController.AbilityDto>>().Subject;

        abilities.Should().HaveCount(4);
        abilities[0].Key.Should().Be("Q");
        abilities[0].Name.Should().Be("Cauterize", "Accursed Q must resolve authentic HoN name 'Cauterize'");
        abilities[0].Name.Should().NotBe("Accursed Q", "Must not return generic procedural fallback string");

        abilities[1].Key.Should().Be("W");
        abilities[1].Name.Should().Be("Volatile Shield");

        abilities[2].Key.Should().Be("E");
        abilities[2].Name.Should().Be("Sear");

        abilities[3].Key.Should().Be("R");
        abilities[3].Name.Should().Be("Flame Consumer");
    }

    [Theory]
    [InlineData("Hero_Vindicator", "Sage's Lore")]
    [InlineData("Hero_Bubbles", "Shell Surf")]
    [InlineData("Hero_Accursed", "Cauterize")]
    public async Task Http_ColdStart_HeroAbilities_Returns200WithAuthenticName(string heroId, string expectedQName)
    {
        HttpClient client = await _fixture.CreateAuthenticatedClientAsync();

        HttpResponseMessage response = await client.GetAsync($"/api/heroes/{heroId}/abilities");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/json");

        string json = await response.Content.ReadAsStringAsync();
        List<HomeController.AbilityDto>? abilities = JsonSerializer.Deserialize<List<HomeController.AbilityDto>>(json,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        abilities.Should().NotBeNull();
        abilities!.Should().HaveCount(4);
        abilities![0].Name.Should().Be(expectedQName);
    }

    // =========================================================================
    // 2. ALL 174 ITEMS ACCESSIBILITY & CLEAN RENDERING (NO REDIRECTION LOOPS)
    // =========================================================================

    [Fact]
    public async Task DawnbringerModelValidation_ItemCompendium_CatalogHasExactly174Items()
    {
        ResetHomeControllerCache();
        await using MerrickContext freshContext = TestDbContextFactory.CreateInMemoryContext();
        HomeController controller = CreateDirectController(freshContext);

        IActionResult result = await controller.GetItems();
        result.Should().BeOfType<JsonResult>();
        List<HeroItemDto> items = ((JsonResult)result).Value.Should().BeOfType<List<HeroItemDto>>().Subject;

        items.Should().HaveCount(174, "The compendium must contain exactly 174 items");
    }

    [Fact]
    public async Task ItemDetail_All174Items_AreIndividuallyAccessible_WithoutRedirectLoops()
    {
        ResetHomeControllerCache();
        await using MerrickContext freshContext = TestDbContextFactory.CreateInMemoryContext();
        HomeController controller = CreateDirectController(freshContext);

        IActionResult itemsResult = await controller.GetItems();
        List<HeroItemDto> items = (List<HeroItemDto>)((JsonResult)itemsResult).Value!;
        items.Should().HaveCount(174);

        int verifiedCount = 0;
        foreach (HeroItemDto item in items)
        {
            item.Code.Should().NotBeNullOrWhiteSpace();

            // Test 1: Access by item.Code
            IActionResult detailResult = await controller.ItemDetail(item.Code);
            detailResult.Should().BeOfType<ViewResult>(
                $"Item '{item.Code}' ({item.Name}) must return ViewResult, but returned {detailResult.GetType().Name}");

            ViewResult view = (ViewResult)detailResult;
            HeroItemDto model = view.Model.Should().BeOfType<HeroItemDto>().Subject;
            model.Name.Should().Be(item.Name);
            model.Cost.Should().Be(item.Cost);

            // Test 2: Access with Item_ prefix
            string prefixedCode = item.Code.StartsWith("Item_", StringComparison.OrdinalIgnoreCase)
                ? item.Code
                : $"Item_{item.Code}";

            IActionResult prefixedResult = await controller.ItemDetail(prefixedCode);
            prefixedResult.Should().BeOfType<ViewResult>(
                $"Item '{prefixedCode}' must resolve cleanly without redirection");

            verifiedCount++;
        }

        verifiedCount.Should().Be(174, "All 174 items must be independently verified");
    }

    [Fact]
    public async Task Http_ItemDetail_RepresentativeItems_Return200WithoutRedirectLoops()
    {
        HttpClient client = await _fixture.CreateAuthenticatedClientAsync();

        string[] testItems =
        [
            "Item_LoggersHatchet",
            "loggershatchet",
            "Item_PortalKey",
            "portalkey",
            "Item_BlightStones",
            "astrolabe",
            "kongorsbananas",
            "Item_RunesOfTheBlight",
            "Item_IronBuckler",
            "Item_PostHaste"
        ];

        foreach (string itemCode in testItems)
        {
            HttpResponseMessage response = await client.GetAsync($"/items/{itemCode}");
            response.StatusCode.Should().Be(HttpStatusCode.OK, $"GET /items/{itemCode} must return 200 OK");
            string html = await response.Content.ReadAsStringAsync();
            html.Should().Contain("<!DOCTYPE html>");
            html.Should().Contain("Item Compendium");
            html.Should().NotContain("InvalidOperationException");
            html.Should().NotContain("NullReferenceException");
        }
    }

    [Fact]
    public async Task Http_ItemDetail_InvalidCode_RedirectsOnceWithoutInfiniteLoop()
    {
        HttpClient noRedirectClient = await _fixture.CreateAuthenticatedNoRedirectClientAsync();

        HttpResponseMessage response = await noRedirectClient.GetAsync("/items/CompletelyFakeItemXYZ9999");

        // Must redirect to /items (single hop)
        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        response.Headers.Location.Should().NotBeNull();
        response.Headers.Location!.ToString().Should().ContainEquivalentOf("items");

        // And following the redirect gives 200 OK (compendium page)
        HttpClient followingClient = await _fixture.CreateAuthenticatedClientAsync();
        HttpResponseMessage followedResponse = await followingClient.GetAsync("/items/CompletelyFakeItemXYZ9999");
        followedResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        string body = await followedResponse.Content.ReadAsStringAsync();
        body.Should().Contain("Item Compendium");
    }
}
