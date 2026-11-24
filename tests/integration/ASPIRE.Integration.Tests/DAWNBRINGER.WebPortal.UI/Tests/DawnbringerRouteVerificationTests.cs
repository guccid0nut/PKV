// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace ASPIRE.Tests.DAWNBRINGER.WebPortal.UI.Tests;

[Collection("DawnbringerE2E")]
public class DawnbringerRouteVerificationTests(DawnbringerE2ETestFixture fixture)
{
    private readonly DawnbringerE2ETestFixture _fixture = fixture;

    private static async Task<string> FetchStaffAntiforgeryTokenAsync(HttpClient client)
    {
        HttpResponseMessage getResponse = await client.GetAsync("/staff");
        getResponse.StatusCode.Should().Be(HttpStatusCode.OK, "Authenticated staff GET /staff must return 200 OK to mint antiforgery cookie/token");
        string html = await getResponse.Content.ReadAsStringAsync();
        Match match = Regex.Match(
            html,
            @"name=""__RequestVerificationToken""[^>]*value=""([^""]+)""|value=""([^""]+)""[^>]*name=""__RequestVerificationToken""",
            RegexOptions.IgnoreCase);
        match.Success.Should().BeTrue("GET /staff must render __RequestVerificationToken input");
        return !string.IsNullOrEmpty(match.Groups[1].Value) ? match.Groups[1].Value : match.Groups[2].Value;
    }

    // =========================================================================
    // 1. VERIFY ALL 15 CORE DAWMBRINGER ROUTES (HTTP 200 WITH COMPLETE HTML)
    // =========================================================================

    [Theory]
    [InlineData("/", "Project KONGOR", false)]
    [InlineData("/matches", "Match History", true)]
    [InlineData("/stats", "Player Analytics", false)]
    [InlineData("/leaderboard", "Leaderboard", false)]
    [InlineData("/codex", "Hero Codex", false)]
    [InlineData("/codex/Hero_Vindicator", "Vindicator", false)]
    [InlineData("/items", "Item Compendium", false)]
    [InlineData("/guides", "Guides", false)]
    [InlineData("/guides/1", "Guide", false)]
    [InlineData("/armory", "Store", false)]
    [InlineData("/store", "Store", false)]
    [InlineData("/inventory", "Inventory", true)]
    [InlineData("/plinko", "Plinko", false)]
    [InlineData("/setup", "Setup", true)]
    [InlineData("/staff", "Staff", true)]
    public async Task Route_All15DawnbringerRoutes_ReturnHttp200WithCompleteRenderedHtml(
        string route,
        string expectedContentMarker,
        bool requiresAuth)
    {
        _ = requiresAuth;
        HttpClient client = route == "/"
            ? _fixture.CreateAnonymousClient()
            : await _fixture.CreateAuthenticatedClientAsync();

        HttpResponseMessage response = await client.GetAsync(route);

        response.StatusCode.Should().Be(HttpStatusCode.OK, $"Route {route} must return 200 OK");
        response.Content.Headers.ContentType?.MediaType.Should().Be("text/html");

        string html = await response.Content.ReadAsStringAsync();
        html.Should().Contain("<!DOCTYPE html>", $"Route {route} must have full HTML document doctype");
        html.Should().Contain("<html", $"Route {route} must render <html> tag");
        html.Should().Contain("<body", $"Route {route} must render <body> tag");
        html.Should().Contain(expectedContentMarker, $"Route {route} must contain expected content: {expectedContentMarker}");

        // Assert no unhandled exception markers or error stack traces leaked
        html.Should().NotContain("InvalidOperationException");
        html.Should().NotContain("NullReferenceException");
        html.Should().NotContain("Internal Server Error");
    }

    // =========================================================================
    // 2. SPECIFIC VERIFICATION OF NEW TOP-LEVEL ROUTES & REDIRECTION LOGIC
    // =========================================================================

    [Fact]
    public async Task TopLevelRoute_Armory_WithoutRedirectFollow_ReturnsHttp302RedirectingToStore()
    {
        HttpClient noRedirectClient = await _fixture.CreateAuthenticatedNoRedirectClientAsync();

        HttpResponseMessage response = await noRedirectClient.GetAsync("/armory");

        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        response.Headers.Location.Should().NotBeNull();
        response.Headers.Location!.ToString().Should().Be("/store");
    }

    [Fact]
    public async Task TopLevelRoute_Armory_PreservesQueryParametersOnRedirect()
    {
        HttpClient noRedirectClient = await _fixture.CreateAuthenticatedNoRedirectClientAsync();

        HttpResponseMessage response = await noRedirectClient.GetAsync("/armory?category=announcers&page=2");

        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        response.Headers.Location.Should().NotBeNull();
        response.Headers.Location!.ToString().Should().Be("/store?category=announcers&page=2");
    }

    [Fact]
    public async Task TopLevelRoute_Armory_FollowingRedirect_RendersStoreWithHttp200()
    {
        HttpClient client = await _fixture.CreateAuthenticatedClientAsync();

        HttpResponseMessage response = await client.GetAsync("/armory");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        string markup = await response.Content.ReadAsStringAsync();
        markup.Should().Contain("Store");
        markup.Should().NotContain("404 Not Found");
        markup.Should().NotContain("Page Not Found");
    }

    [Fact]
    public async Task TopLevelRoute_Inventory_WithoutRedirectFollow_ReturnsHttp302RedirectingToStaffInventory()
    {
        HttpClient noRedirectClient = await _fixture.CreateAuthenticatedNoRedirectClientAsync();

        HttpResponseMessage response = await noRedirectClient.GetAsync("/inventory");

        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        response.Headers.Location.Should().NotBeNull();
        response.Headers.Location!.ToString().Should().ContainEquivalentOf("inventory");
    }

    [Fact]
    public async Task TopLevelRoute_Inventory_WithAccountQuery_PreservesAccountNameInRedirect()
    {
        HttpClient noRedirectClient = await _fixture.CreateAuthenticatedNoRedirectClientAsync();

        HttpResponseMessage response = await noRedirectClient.GetAsync("/inventory?accountName=guccidonut");

        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        response.Headers.Location.Should().NotBeNull();
        response.Headers.Location!.ToString().Should().ContainEquivalentOf("accountName=guccidonut");
    }

    [Fact]
    public async Task TopLevelRoute_Inventory_AuthenticatedStaff_FollowingRedirect_ReturnsHttp200()
    {
        HttpClient client = await _fixture.CreateAuthenticatedClientAsync();

        HttpResponseMessage response = await client.GetAsync("/inventory");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        string markup = await response.Content.ReadAsStringAsync();
        markup.Should().Contain("Inventory");
        markup.Should().NotContain("404 Not Found");
        markup.Should().NotContain("Page Not Found");
    }

    [Fact]
    public async Task TopLevelRoute_Plinko_WithoutRedirectFollow_ReturnsHttp302RedirectingToStoreCategoryPlinko()
    {
        HttpClient noRedirectClient = await _fixture.CreateAuthenticatedNoRedirectClientAsync();

        HttpResponseMessage response = await noRedirectClient.GetAsync("/plinko");

        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        response.Headers.Location.Should().NotBeNull();
        response.Headers.Location!.ToString().Should().Be("/store?category=plinko");
    }

    [Fact]
    public async Task TopLevelRoute_Plinko_FollowingRedirect_RendersPlinkoWithHttp200()
    {
        HttpClient client = await _fixture.CreateAuthenticatedClientAsync();

        HttpResponseMessage response = await client.GetAsync("/plinko");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        string markup = await response.Content.ReadAsStringAsync();
        markup.Should().Contain("Plinko");
        markup.Should().NotContain("404 Not Found");
        markup.Should().NotContain("Page Not Found");
    }

    // =========================================================================
    // 3. /staff/reports/create-direct POST HANDLING & ANTIFORGERY ENFORCEMENT
    // =========================================================================

    [Fact]
    public async Task StaffReports_CreateDirect_PostWithoutAntiforgeryToken_IsRejectedWithHttp400()
    {
        HttpClient client = await _fixture.CreateAuthenticatedClientAsync();

        Dictionary<string, string> formData = new()
        {
            { "targetAccountName", "normalplayer" },
            { "punishmentType", "MUTE" },
            { "reason", "Missing CSRF token must fail closed" },
            { "durationHours", "24" }
        };

        HttpRequestMessage request = new(HttpMethod.Post, "/staff/reports/create-direct")
        {
            Content = new FormUrlEncodedContent(formData)
        };
        request.Headers.Add("X-Requested-With", "XMLHttpRequest");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        HttpResponseMessage response = await client.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest,
            "Direct punishment POST without __RequestVerificationToken must be rejected with 400 Bad Request");
    }

    [Fact]
    public async Task StaffReports_CreateDirect_PostWithAntiforgeryToken_SucceedsWithHttp200Json()
    {
        HttpClient client = await _fixture.CreateAuthenticatedClientAsync();
        string antiforgeryToken = await FetchStaffAntiforgeryTokenAsync(client);

        Dictionary<string, string> formData = new()
        {
            { "__RequestVerificationToken", antiforgeryToken },
            { "targetAccountName", "normalplayer" },
            { "punishmentType", "MUTE" },
            { "reason", "Toxic in-game communications" },
            { "durationHours", "24" },
            { "notes", "Automated challenger verification action" }
        };

        HttpRequestMessage request = new(HttpMethod.Post, "/staff/reports/create-direct")
        {
            Content = new FormUrlEncodedContent(formData)
        };
        request.Headers.Add("X-Requested-With", "XMLHttpRequest");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        HttpResponseMessage response = await client.SendAsync(request);
        string responseBody = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK,
            $"Direct punishment endpoint with valid antiforgery token must return 200 OK, but returned {(int)response.StatusCode} with body: {responseBody}");

        using JsonDocument doc = JsonDocument.Parse(responseBody);
        doc.RootElement.GetProperty("success").GetBoolean().Should().BeTrue();
        doc.RootElement.GetProperty("message").GetString().Should().Contain("normalplayer");
    }

    [Fact]
    public async Task StaffReports_CreateDirect_PostWithAlternativeFieldNames_ResolvesCorrectly()
    {
        HttpClient client = await _fixture.CreateAuthenticatedClientAsync();
        string antiforgeryToken = await FetchStaffAntiforgeryTokenAsync(client);

        Dictionary<string, string> formData = new()
        {
            { "__RequestVerificationToken", antiforgeryToken },
            { "targetAccountName", "normalplayer" },
            { "judgmentAction", "WARNING" },
            { "description", "Alternative field test notes" }
        };

        HttpRequestMessage request = new(HttpMethod.Post, "/staff/reports/create-direct")
        {
            Content = new FormUrlEncodedContent(formData)
        };
        request.Headers.Add("X-Requested-With", "XMLHttpRequest");

        HttpResponseMessage response = await client.SendAsync(request);
        string jsonContent = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK, $"Should return 200 OK, but returned {(int)response.StatusCode}: {jsonContent}");
        using JsonDocument doc = JsonDocument.Parse(jsonContent);
        doc.RootElement.GetProperty("success").GetBoolean().Should().BeTrue();
        doc.RootElement.GetProperty("message").GetString().Should().Contain("WARNING");
    }

    [Fact]
    public async Task StaffReports_CreateDirect_NonAjaxPost_RedirectsToIndexWithAntiforgeryToken()
    {
        HttpClient noRedirectClient = _fixture.CreateNoRedirectClient();
        string authCallback = "/Account/DirectDiscordCallback?discordId=test-discord-guccidonut&discordUsername=guccidonut";
        await noRedirectClient.GetAsync(authCallback);
        string antiforgeryToken = await FetchStaffAntiforgeryTokenAsync(noRedirectClient);

        Dictionary<string, string> formData = new()
        {
            { "__RequestVerificationToken", antiforgeryToken },
            { "targetAccountName", "normalplayer" },
            { "punishmentType", "BAN" },
            { "reason", "Non-AJAX standard form submission test" },
            { "durationHours", "48" }
        };

        HttpRequestMessage request = new(HttpMethod.Post, "/staff/reports/create-direct")
        {
            Content = new FormUrlEncodedContent(formData)
        };

        HttpResponseMessage response = await noRedirectClient.SendAsync(request);
        string body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.Redirect, $"Non-AJAX post should redirect, but returned {(int)response.StatusCode}: {body}");
        response.Headers.Location.Should().NotBeNull();
        response.Headers.Location!.ToString().Should().Contain("message=");
    }

    [Fact]
    public async Task StaffReports_CreateDirect_AnonymousUser_IsForbiddenOrRedirected()
    {
        HttpClient noRedirectClient = _fixture.CreateNoRedirectClient();
        HttpResponseMessage getHome = await noRedirectClient.GetAsync("/");
        string homeHtml = await getHome.Content.ReadAsStringAsync();
        Match tokenMatch = Regex.Match(homeHtml, @"name=""__RequestVerificationToken""[^>]*value=""([^""]+)""");

        Dictionary<string, string> formData = new()
        {
            { "targetAccountName", "normalplayer" },
            { "punishmentType", "BAN" }
        };
        if (tokenMatch.Success)
        {
            formData["__RequestVerificationToken"] = tokenMatch.Groups[1].Value;
        }

        HttpRequestMessage request = new(HttpMethod.Post, "/staff/reports/create-direct")
        {
            Content = new FormUrlEncodedContent(formData)
        };

        HttpResponseMessage response = await noRedirectClient.SendAsync(request);
        string body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().BeOneOf(
            [HttpStatusCode.Redirect, HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden, HttpStatusCode.BadRequest],
            $"Anonymous user should be rejected, but got {(int)response.StatusCode}: {body}");
    }

    [Fact]
    public async Task StaffReports_CreateDirect_GetRequest_CleanlyRedirectsToIndexWithout404Or500()
    {
        HttpClient noRedirectClient = _fixture.CreateNoRedirectClient();
        string authCallback = "/Account/DirectDiscordCallback?discordId=test-discord-guccidonut&discordUsername=guccidonut";
        await noRedirectClient.GetAsync(authCallback);

        HttpResponseMessage response = await noRedirectClient.GetAsync("/staff/reports/create-direct");
        string body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.Redirect, $"GET create-direct should redirect, but got {(int)response.StatusCode}: {body}");
        response.Headers.Location.Should().NotBeNull();
    }

    // =========================================================================
    // 4. API ALIASES & DATA PIPELINES INTEGRITY
    // =========================================================================

    [Fact]
    public async Task DawnbringerRouteVerification_ApiItems_ReturnsHttp200WithJsonCatalog()
    {
        HttpClient client = await _fixture.CreateAuthenticatedClientAsync();

        HttpResponseMessage response = await client.GetAsync("/api/items");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/json");

        string json = await response.Content.ReadAsStringAsync();
        using JsonDocument doc = JsonDocument.Parse(json);
        doc.RootElement.ValueKind.Should().Be(JsonValueKind.Array);
        doc.RootElement.GetArrayLength().Should().BeGreaterThan(0, "Item compendium must serve items");
    }

    [Fact]
    public async Task DawnbringerRouteVerification_ApiHeroAbilities_ReturnsHttp200WithJsonAbilities()
    {
        HttpClient client = await _fixture.CreateAuthenticatedClientAsync();

        HttpResponseMessage response = await client.GetAsync("/api/heroes/Hero_Vindicator/abilities");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/json");
    }

    [Theory]
    [InlineData("/api/players/search?q=guccidonut")]
    [InlineData("/api/players/search?query=guccidonut")]
    public async Task DawnbringerRouteVerification_ApiPlayerSearch_SupportsBothQAndQueryParameters(string url)
    {
        HttpClient client = await _fixture.CreateAuthenticatedClientAsync();

        HttpResponseMessage response = await client.GetAsync(url);
        string json = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK, $"URL {url} must return 200 OK, but returned {(int)response.StatusCode} with body: {json}");
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/json");
        using JsonDocument doc = JsonDocument.Parse(json);
        doc.RootElement.ValueKind.Should().Be(JsonValueKind.Array);
    }

    [Fact]
    public async Task PublicPopulationEndpoint_AllowsAnonymousAndExposesStrictlyAllowlistedKeys()
    {
        HttpClient anon = _fixture.CreateAnonymousClient();

        HttpResponseMessage response = await anon.GetAsync("/api/telemetry/population");
        string json = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK, $"Anonymous /api/telemetry/population failed: {json}");
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/json");
        response.Headers.CacheControl?.Public.Should().BeTrue();
        response.Headers.CacheControl?.MaxAge.Should().Be(TimeSpan.FromSeconds(5));

        using JsonDocument doc = JsonDocument.Parse(json);
        HashSet<string> actualKeys = doc.RootElement.EnumerateObject()
            .Select(static p => p.Name)
            .ToHashSet(StringComparer.Ordinal);
        HashSet<string> expectedKeys =
        [
            "status",
            "onlinePlayers",
            "gamesInProgressTotal",
            "gamesInProgressByRegion",
            "serversAvailableByRegion",
            "tmmQueues",
            "updatedAtUtc"
        ];
        actualKeys.Should().BeEquivalentTo(expectedKeys);
    }

    [Fact]
    public async Task AuthBoundaries_RejectAnonymousMetricsAndTelemetryHub_WhileRenderingAuthenticatedMetricsAndPublicLandingCard()
    {
        HttpClient anonNoRedirect = _fixture.CreateNoRedirectClient();

        HttpResponseMessage anonMetrics = await anonNoRedirect.GetAsync("/System/Metrics");
        anonMetrics.StatusCode.Should().BeOneOf(HttpStatusCode.Redirect, HttpStatusCode.Unauthorized);

        HttpResponseMessage anonHub = await anonNoRedirect.PostAsync("/hubs/telemetry/negotiate?negotiateVersion=1", content: null);
        anonHub.StatusCode.Should().BeOneOf(HttpStatusCode.Redirect, HttpStatusCode.Unauthorized);

        HttpResponseMessage anonHome = await anonNoRedirect.GetAsync("/");
        anonHome.StatusCode.Should().Be(HttpStatusCode.OK);
        string homeHtml = await anonHome.Content.ReadAsStringAsync();
        homeHtml.Should().Contain("id=\"public-population-card\"");
        homeHtml.Should().Contain("/js/live-population-card.js");

        HttpClient authed = await _fixture.CreateAuthenticatedClientAsync();
        HttpResponseMessage authedMetrics = await authed.GetAsync("/System/Metrics");
        authedMetrics.StatusCode.Should().Be(HttpStatusCode.OK);
        string metricsHtml = await authedMetrics.Content.ReadAsStringAsync();
        metricsHtml.Should().Contain("id=\"infra-population-card\"");
        metricsHtml.Should().Contain("id=\"regions-container\"");
        metricsHtml.Should().Contain("id=\"regions-count-badge\"");
        metricsHtml.Should().Contain("Games in progress");
        metricsHtml.Should().Contain("Servers available");
        metricsHtml.Should().Contain("Players queued for TMM");
    }
}
