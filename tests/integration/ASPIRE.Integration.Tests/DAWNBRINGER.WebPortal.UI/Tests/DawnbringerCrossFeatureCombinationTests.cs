// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Net;
using FluentAssertions;
using Xunit;

namespace ASPIRE.Tests.DAWNBRINGER.WebPortal.UI.Tests;

[Collection("DawnbringerE2E")]
public class DawnbringerCrossFeatureCombinationTests(DawnbringerE2ETestFixture fixture)
{
    private readonly DawnbringerE2ETestFixture _fixture = fixture;

    [Fact]
    public async Task AuthSession_DirectDiscordCallback_PreservesSessionAcrossProtectedRoutes()
    {
        // 1. Authenticate via direct Discord callback simulation
        HttpClient client = _fixture.CreateAnonymousClient();
        string callbackUrl = "/Account/DirectDiscordCallback?discordId=test-discord-guccidonut&discordUsername=guccidonut";
        HttpResponseMessage loginResponse = await client.GetAsync(callbackUrl);
        loginResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        // 2. Command Center recognizes authenticated player
        HttpResponseMessage homeResponse = await client.GetAsync("/");
        homeResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        string homeMarkup = await homeResponse.Content.ReadAsStringAsync();
        homeMarkup.Should().Contain("guccidonut");

        // 3. Protected /setup route allows access without redirect to login
        HttpResponseMessage setupResponse = await client.GetAsync("/setup");
        setupResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        string setupMarkup = await setupResponse.Content.ReadAsStringAsync();
        setupMarkup.Should().Contain("Setup");

        // 4. Protected /staff portal allows access with staff claims
        HttpResponseMessage staffResponse = await client.GetAsync("/staff");
        staffResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        string staffMarkup = await staffResponse.Content.ReadAsStringAsync();
        staffMarkup.Should().Contain("Staff");
    }

    [Theory]
    [InlineData("/stats/guccidonut")]
    [InlineData("/stats?accountName=guccidonut")]
    [InlineData("/stats?target=guccidonut")]
    [InlineData("/Home/Stats?target=guccidonut")]
    public async Task PlayerStatsResolution_RouteParameterVsQueryString_Equivalence(string statsUrl)
    {
        HttpClient client = await _fixture.CreateAuthenticatedClientAsync();

        HttpResponseMessage response = await client.GetAsync(statsUrl);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        string markup = await response.Content.ReadAsStringAsync();
        markup.Should().Contain("guccidonut");
        markup.Should().Contain("Player Analytics");
        // Verify combat statistics are resolved regardless of route/query format
        markup.Should().Contain("1750"); // MMR
    }

    [Fact]
    public async Task Storefront_AuthenticatedUser_DisplaysGoldSilverBalances()
    {
        HttpClient client = await _fixture.CreateAuthenticatedClientAsync();

        HttpResponseMessage response = await client.GetAsync("/store");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        string markup = await response.Content.ReadAsStringAsync();
        // User has 5000 Gold and 10000 Silver
        markup.Should().Contain("5,000");
        markup.Should().Contain("10,000");
    }

    [Fact]
    public async Task StaffPortal_ManageInventory_AccessibleWithStaffClaims()
    {
        HttpClient client = await _fixture.CreateAuthenticatedClientAsync();

        HttpResponseMessage response = await client.GetAsync("/staff/inventory?accountName=normalplayer");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        string markup = await response.Content.ReadAsStringAsync();
        markup.Should().Contain("Inventory");
        markup.Should().Contain("normalplayer");
    }

    [Fact]
    public async Task GuidesBuilder_AuthenticatedUser_AllowsAccess()
    {
        HttpClient client = await _fixture.CreateAuthenticatedClientAsync();

        HttpResponseMessage response = await client.GetAsync("/guides/builder?hero=Hero_Vindicator");

        // Authenticated user should be able to access the guide builder
        response.StatusCode.Should().BeOneOf(HttpStatusCode.OK, HttpStatusCode.Redirect);
    }
}
