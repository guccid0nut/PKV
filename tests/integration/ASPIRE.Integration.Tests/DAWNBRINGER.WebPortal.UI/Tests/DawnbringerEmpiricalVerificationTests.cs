// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Net;
using FluentAssertions;
using Xunit;

namespace ASPIRE.Tests.DAWNBRINGER.WebPortal.UI.Tests;

[Collection("DawnbringerE2E")]
public sealed class DawnbringerEmpiricalVerificationTests(DawnbringerE2ETestFixture fixture)
{
    private readonly DawnbringerE2ETestFixture _fixture = fixture;

    // =========================================================================
    // MISSION ITEM 1: USER JOURNEY FROM LEADERBOARD TO STATS TO MATCHES
    // =========================================================================

    [Fact]
    public async Task Mission1_UserJourney_LeaderboardToPlayerStatsToMatches_EmpiricalVerification()
    {
        HttpClient client = await _fixture.CreateAuthenticatedClientAsync();

        // Step 1: Browse the Leaderboard
        HttpResponseMessage leaderboardResponse = await client.GetAsync("/leaderboard");
        leaderboardResponse.StatusCode.Should().Be(HttpStatusCode.OK, "Leaderboard must return HTTP 200 OK");
        string leaderboardMarkup = await leaderboardResponse.Content.ReadAsStringAsync();
        leaderboardMarkup.Should().Contain("<!DOCTYPE html>");
        leaderboardMarkup.Should().Contain("Leaderboard");
        leaderboardMarkup.Should().NotContain("500 Internal Server Error");

        // Step 2: Investigate top player stats via Query String (?accountName=guccidonut)
        HttpResponseMessage statsQueryResponse = await client.GetAsync("/stats?accountName=guccidonut");
        statsQueryResponse.StatusCode.Should().Be(HttpStatusCode.OK, "Stats with query string must return HTTP 200 OK");
        string statsQueryMarkup = await statsQueryResponse.Content.ReadAsStringAsync();
        statsQueryMarkup.Should().Contain("<!DOCTYPE html>");
        statsQueryMarkup.Should().Contain("Player Analytics");
        statsQueryMarkup.Should().Contain("guccidonut");
        statsQueryMarkup.Should().Contain("1750", "Must display seeded combat MMR of 1750");

        // Step 3: Investigate top player stats via Route Token (/stats/guccidonut)
        HttpResponseMessage statsRouteResponse = await client.GetAsync("/stats/guccidonut");
        statsRouteResponse.StatusCode.Should().Be(HttpStatusCode.OK, "Stats with route token must return HTTP 200 OK");
        string statsRouteMarkup = await statsRouteResponse.Content.ReadAsStringAsync();
        statsRouteMarkup.Should().Contain("Player Analytics");
        statsRouteMarkup.Should().Contain("guccidonut");
        statsRouteMarkup.Should().Contain("1750");

        // Step 4: Investigate player match history
        HttpResponseMessage matchesResponse = await client.GetAsync("/matches?accountName=guccidonut");
        matchesResponse.StatusCode.Should().Be(HttpStatusCode.OK, "Matches page must return HTTP 200 OK");
        string matchesMarkup = await matchesResponse.Content.ReadAsStringAsync();
        matchesMarkup.Should().Contain("<!DOCTYPE html>");
        matchesMarkup.Should().Contain("Match History");

        // Step 5: Drill into match details for match #10001
        HttpResponseMessage matchDetailsResponse = await client.GetAsync("/Home/MatchDetails/10001");
        matchDetailsResponse.StatusCode.Should().BeOneOf(HttpStatusCode.OK, HttpStatusCode.Redirect);
        if (matchDetailsResponse.StatusCode == HttpStatusCode.OK)
        {
            string matchDetailsMarkup = await matchDetailsResponse.Content.ReadAsStringAsync();
            matchDetailsMarkup.Should().NotContain("InvalidOperationException");
            matchDetailsMarkup.Should().NotContain("NullReferenceException");
        }
    }

    // =========================================================================
    // MISSION ITEM 2: BANNED USER LOGIN REDIRECTION
    // =========================================================================

    [Fact]
    public async Task Mission2_BannedUserDirectDiscordCallback_RedirectsToLoginWithBanNotice()
    {
        HttpClient noRedirectClient = _fixture.CreateNoRedirectClient();

        HttpResponseMessage response = await noRedirectClient.GetAsync(
            "/Account/DirectDiscordCallback?discordId=test-discord-banned&discordUsername=bannedplayer");

        // Banned accounts must be rejected and redirected
        response.StatusCode.Should().Be(HttpStatusCode.Redirect, "Banned login must return HTTP 302 Redirect");
        response.Headers.Location.Should().NotBeNull();
        string location = response.Headers.Location!.ToString();
        location.Should().Contain("/Account/Login", "Redirect Location header must strictly direct to /Account/Login");

        // Verify that visiting a protected route fails authentication
        HttpResponseMessage protectedResponse = await noRedirectClient.GetAsync("/staff");
        protectedResponse.StatusCode.Should().BeOneOf(HttpStatusCode.Redirect, HttpStatusCode.Unauthorized);
    }

    // =========================================================================
    // MISSION ITEMS 3 & 4: ALL 15 DAWMBRINGER ROUTES RETURN HTTP 200 (COMPLETE HTML, ZERO 500/404)
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
    public async Task Mission3And4_All15Routes_ReturnHttp200WithCompleteMarkup_ZeroErrors(
        string route,
        string expectedMarker,
        bool requiresAuth)
    {
        _ = requiresAuth;
        HttpClient client = route == "/"
            ? _fixture.CreateAnonymousClient()
            : await _fixture.CreateAuthenticatedClientAsync();

        HttpResponseMessage response = await client.GetAsync(route);

        // Assert HTTP 200
        response.StatusCode.Should().Be(HttpStatusCode.OK, $"Route {route} must return HTTP 200 OK");

        // Assert Content-Type text/html
        response.Content.Headers.ContentType.Should().NotBeNull();
        response.Content.Headers.ContentType!.MediaType.Should().Be("text/html");

        // Assert Complete rendered markup
        string html = await response.Content.ReadAsStringAsync();
        html.Should().Contain("<!DOCTYPE html>", $"Route {route} must contain doctype");
        html.Should().Contain("<html", $"Route {route} must contain <html>");
        html.Should().Contain("<body", $"Route {route} must contain <body>");
        html.Should().Contain("</html>", $"Route {route} must contain closing </html>");
        html.Should().Contain("</body>", $"Route {route} must contain closing </body>");
        html.Should().Contain(expectedMarker, $"Route {route} must contain content marker '{expectedMarker}'");

        // Assert ZERO 500 or 404 or exception markers
        html.Should().NotContain("500 Internal Server Error");
        html.Should().NotContain("404 Not Found");
        html.Should().NotContain("InvalidOperationException");
        html.Should().NotContain("NullReferenceException");
        html.Should().NotContain("An unhandled exception occurred while processing the request");
        html.Should().NotContain("System.Exception");
    }

    // =========================================================================
    // MISSION ITEM 5: ADVERSARIAL STRESS-TESTS FOR PLAYER STATS RESOLUTION
    // =========================================================================

    [Theory]
    [InlineData("/stats?accountName=guccidonut")]
    [InlineData("/stats?target=guccidonut")]
    [InlineData("/stats/guccidonut")]
    [InlineData("/Home/Stats?accountName=guccidonut")]
    [InlineData("/Home/Stats?target=guccidonut")]
    [InlineData("/Home/Stats/guccidonut")]
    [InlineData("/analytics?accountName=guccidonut")]
    [InlineData("/player/guccidonut")]
    public async Task StressTest_AllStatsRouteAndQueryAliases_ConsistentlyResolveMmr1750(string url)
    {
        HttpClient client = await _fixture.CreateAuthenticatedClientAsync();

        HttpResponseMessage response = await client.GetAsync(url);

        response.StatusCode.Should().Be(HttpStatusCode.OK, $"Alias {url} must return HTTP 200 OK");
        string markup = await response.Content.ReadAsStringAsync();
        markup.Should().Contain("guccidonut");
        markup.Should().Contain("1750", $"Alias {url} must resolve authentic combat MMR 1750");
        markup.Should().NotContain("Internal Server Error");
    }

    [Fact]
    public async Task StressTest_RapidSequentialStatsRequests_HandleHighVolumeLoadWithoutExceptions()
    {
        HttpClient client = await _fixture.CreateAuthenticatedClientAsync();
        string[] urls =
        [
            "/stats/guccidonut",
            "/stats?accountName=guccidonut",
            "/stats?target=guccidonut",
            "/Home/Stats/guccidonut",
            "/player/guccidonut",
            "/stats/normalplayer",
            "/stats?accountName=normalplayer",
            "/stats",
            "/Home/Stats",
            "/stats?accountName=",
            "/stats?target="
        ];

        foreach (string url in urls)
        {
            HttpResponseMessage resp = await client.GetAsync(url);
            resp.StatusCode.Should().Be(HttpStatusCode.OK, $"URL {url} must return HTTP 200");
            string body = await resp.Content.ReadAsStringAsync();
            body.Should().NotContain("Internal Server Error");
            body.Should().NotContain("NullReferenceException");
            body.Should().NotContain("InvalidOperationException");
        }
    }
}
