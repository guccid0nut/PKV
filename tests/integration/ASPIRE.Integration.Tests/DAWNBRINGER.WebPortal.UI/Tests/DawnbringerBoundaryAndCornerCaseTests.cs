// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Net;
using FluentAssertions;
using Xunit;

namespace ASPIRE.Tests.DAWNBRINGER.WebPortal.UI.Tests;

[Collection("DawnbringerE2E")]
public class DawnbringerBoundaryAndCornerCaseTests(DawnbringerE2ETestFixture fixture)
{
    private readonly DawnbringerE2ETestFixture _fixture = fixture;

    [Fact]
    public async Task Stats_NonExistentPlayer_HandlesGracefullyWithout500()
    {
        HttpClient client = await _fixture.CreateAuthenticatedClientAsync();

        HttpResponseMessage response = await client.GetAsync("/stats?accountName=NonExistentPlayer_XYZ_99999");

        response.StatusCode.Should().NotBe(HttpStatusCode.InternalServerError);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        string markup = await response.Content.ReadAsStringAsync();
        markup.Should().Contain("<!DOCTYPE html>");
    }

    [Theory]
    [InlineData("/stats?accountName=")]
    [InlineData("/stats?target=")]
    [InlineData("/stats?accountName=&target=")]
    public async Task Stats_EmptyQueryStrings_ReturnsHttp200Without500(string url)
    {
        HttpClient client = await _fixture.CreateAuthenticatedClientAsync();

        HttpResponseMessage response = await client.GetAsync(url);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        string markup = await response.Content.ReadAsStringAsync();
        markup.Should().Contain("<!DOCTYPE html>");
        markup.Should().Contain("Player Analytics");
    }

    [Fact]
    public async Task Codex_NonExistentHero_HandlesGracefullyWithout500()
    {
        HttpClient client = await _fixture.CreateAuthenticatedClientAsync();

        HttpResponseMessage response = await client.GetAsync("/codex/Hero_NonExistent_FakeHero999");

        response.StatusCode.Should().NotBe(HttpStatusCode.InternalServerError);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        string markup = await response.Content.ReadAsStringAsync();
        markup.Should().Contain("<!DOCTYPE html>");
    }

    [Fact]
    public async Task Guides_NonExistentGuideId_ReturnsNotFound()
    {
        HttpClient client = await _fixture.CreateAuthenticatedClientAsync();

        HttpResponseMessage response = await client.GetAsync("/guides/9999999");

        // GuideController.Details returns NotFound() when guide is not in database
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Matches_EmptyTarget_ReturnsHttp200ForAuthenticatedUser()
    {
        HttpClient client = await _fixture.CreateAuthenticatedClientAsync();

        HttpResponseMessage response = await client.GetAsync("/matches?target=&accountName=");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        string markup = await response.Content.ReadAsStringAsync();
        markup.Should().Contain("<!DOCTYPE html>");
        markup.Should().Contain("Match History");
    }

    [Fact]
    public async Task Matches_UnauthenticatedGuest_RedirectsToLogin()
    {
        HttpClient client = _fixture.CreateNoRedirectClient();

        HttpResponseMessage response = await client.GetAsync("/matches");

        // [Authorize] endpoint rejects unauthenticated access via challenge/redirect
        response.StatusCode.Should().BeOneOf(HttpStatusCode.Redirect, HttpStatusCode.Unauthorized, HttpStatusCode.Found);
    }

    [Fact]
    public async Task Store_EmptyFilterAndWhitespaceSearch_ReturnsHttp200()
    {
        HttpClient client = await _fixture.CreateAuthenticatedClientAsync();

        HttpResponseMessage response = await client.GetAsync("/store?category=&search=%20%20%20&ownership=");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        string markup = await response.Content.ReadAsStringAsync();
        markup.Should().Contain("<!DOCTYPE html>");
        markup.Should().Contain("Store");
    }

    [Fact]
    public async Task Setup_UnauthenticatedGuest_RedirectsToLogin()
    {
        HttpClient client = _fixture.CreateNoRedirectClient();

        HttpResponseMessage response = await client.GetAsync("/setup");

        // [Authorize] on Setup() redirects guest to login
        response.StatusCode.Should().BeOneOf(HttpStatusCode.Redirect, HttpStatusCode.Unauthorized, HttpStatusCode.Found);
    }

    [Fact]
    public async Task Staff_UnauthenticatedGuest_RedirectsAway()
    {
        HttpClient client = _fixture.CreateNoRedirectClient();

        HttpResponseMessage response = await client.GetAsync("/staff");

        // StaffController.Index redirects unauthorized users away
        response.StatusCode.Should().BeOneOf(HttpStatusCode.Redirect, HttpStatusCode.Forbidden, HttpStatusCode.Found);
    }

    [Fact]
    public async Task Staff_NormalUserWithoutStaffRole_RedirectsAway()
    {
        HttpClient client = _fixture.CreateNoRedirectClient();
        // Login as normal player (non-staff)
        await client.GetAsync("/Account/DirectDiscordCallback?discordId=test-discord-normal&discordUsername=normalplayer");

        HttpResponseMessage response = await client.GetAsync("/staff");

        // Non-staff user must be redirected away from staff portal
        response.StatusCode.Should().BeOneOf(HttpStatusCode.Redirect, HttpStatusCode.Forbidden, HttpStatusCode.Found);
    }

    [Fact]
    public async Task DirectDiscordCallback_BannedUser_RedirectsToLoginWithBanNotice()
    {
        HttpClient client = _fixture.CreateNoRedirectClient();

        HttpResponseMessage response = await client.GetAsync("/Account/DirectDiscordCallback?discordId=test-discord-banned&discordUsername=bannedplayer");

        // Banned accounts are blocked from login and redirected to Login
        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        response.Headers.Location?.ToString().Should().Contain("/Account/Login");
    }
}
