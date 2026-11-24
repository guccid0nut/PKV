// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Net;
using FluentAssertions;
using Xunit;

namespace ASPIRE.Tests.DAWNBRINGER.WebPortal.UI.Tests;

[Collection("DawnbringerE2E")]
public class DawnbringerRealWorldScenarioTests(DawnbringerE2ETestFixture fixture)
{
    private readonly DawnbringerE2ETestFixture _fixture = fixture;

    [Fact]
    public async Task DawnbringerRealWorldScenario_UserJourney_CompleteCodexAndCompendiumToGuideFlow()
    {
        HttpClient client = await _fixture.CreateAuthenticatedClientAsync();

        // Step 1: Visit Command Center home page
        HttpResponseMessage homeResponse = await client.GetAsync("/");
        homeResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        string homeMarkup = await homeResponse.Content.ReadAsStringAsync();
        homeMarkup.Should().Contain("Project KONGOR");

        // Step 2: Navigate to Hero Codex
        HttpResponseMessage codexResponse = await client.GetAsync("/codex");
        codexResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        string codexMarkup = await codexResponse.Content.ReadAsStringAsync();
        codexMarkup.Should().Contain("Hero Codex");

        // Step 3: Click into Hero Detail for Vindicator
        HttpResponseMessage heroDetailResponse = await client.GetAsync("/codex/Hero_Vindicator");
        heroDetailResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        string heroDetailMarkup = await heroDetailResponse.Content.ReadAsStringAsync();
        heroDetailMarkup.Should().Contain("Vindicator");

        // Step 4: Navigate to Item Compendium
        HttpResponseMessage itemsResponse = await client.GetAsync("/items");
        itemsResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        string itemsMarkup = await itemsResponse.Content.ReadAsStringAsync();
        itemsMarkup.Should().Contain("Item Compendium");

        // Step 5: Filter guides for Vindicator
        HttpResponseMessage guidesResponse = await client.GetAsync("/guides?hero=Hero_Vindicator");
        guidesResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        // Step 6: Inspect specific guide
        HttpResponseMessage guideDetailResponse = await client.GetAsync("/guides/1");
        guideDetailResponse.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task UserJourney_PlayerInvestigationFlow_FromLeaderboardToStatsToMatches()
    {
        HttpClient client = await _fixture.CreateAuthenticatedClientAsync();

        // Step 1: Browse Leaderboard
        HttpResponseMessage leaderboardResponse = await client.GetAsync("/leaderboard");
        leaderboardResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        string leaderboardMarkup = await leaderboardResponse.Content.ReadAsStringAsync();
        leaderboardMarkup.Should().Contain("Leaderboard");

        // Step 2: Investigate top player profile on Stats page
        HttpResponseMessage statsResponse = await client.GetAsync("/stats?accountName=guccidonut");
        statsResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        string statsMarkup = await statsResponse.Content.ReadAsStringAsync();
        statsMarkup.Should().Contain("guccidonut");
        statsMarkup.Should().Contain("1750");

        // Step 3: Investigate player match history
        HttpResponseMessage matchesResponse = await client.GetAsync("/matches?accountName=guccidonut");
        matchesResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        string matchesMarkup = await matchesResponse.Content.ReadAsStringAsync();
        matchesMarkup.Should().Contain("Match History");

        // Step 4: Open specific match details
        HttpResponseMessage matchDetailsResponse = await client.GetAsync("/Home/MatchDetails/10001");
        matchDetailsResponse.StatusCode.Should().BeOneOf(HttpStatusCode.OK, HttpStatusCode.Redirect);
    }

    [Fact]
    public async Task UserJourney_FullAuthenticatedSession_StoreBrowsingToInventoryToStaff()
    {
        // Step 1: Sign in via Direct Discord OAuth Callback
        HttpClient client = await _fixture.CreateAuthenticatedClientAsync(
            discordId: "test-discord-guccidonut",
            discordUsername: "guccidonut");

        // Step 2: Visit Command Center and verify authenticated identity
        HttpResponseMessage homeResponse = await client.GetAsync("/");
        homeResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        string homeMarkup = await homeResponse.Content.ReadAsStringAsync();
        homeMarkup.Should().Contain("guccidonut");

        // Step 3: Browse Storefront catalog with category filtering
        HttpResponseMessage storeResponse = await client.GetAsync("/store?category=announcers");
        storeResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        string storeMarkup = await storeResponse.Content.ReadAsStringAsync();
        storeMarkup.Should().Contain("Store");

        // Step 4: Access Staff Operations Dashboard
        HttpResponseMessage staffResponse = await client.GetAsync("/staff");
        staffResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        string staffMarkup = await staffResponse.Content.ReadAsStringAsync();
        staffMarkup.Should().Contain("Staff");

        // Step 5: Manage user inventory from staff console
        HttpResponseMessage inventoryResponse = await client.GetAsync("/staff/inventory?accountName=normalplayer");
        inventoryResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        string inventoryMarkup = await inventoryResponse.Content.ReadAsStringAsync();
        inventoryMarkup.Should().Contain("Inventory");
        inventoryMarkup.Should().Contain("normalplayer");
    }
}
