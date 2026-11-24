// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Net;
using FluentAssertions;
using Xunit;

namespace ASPIRE.Tests.DAWNBRINGER.WebPortal.UI.Tests;

[Collection("DawnbringerE2E")]
public class DawnbringerFeatureCoverageTests(DawnbringerE2ETestFixture fixture)
{
    private readonly DawnbringerE2ETestFixture _fixture = fixture;

    [Fact]
    public async Task Route01_Root_ReturnsHttp200WithCompleteMarkup()
    {
        HttpClient client = await _fixture.CreateAuthenticatedClientAsync();

        HttpResponseMessage response = await (_fixture.CreateAnonymousClient()).GetAsync("/");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType?.MediaType.Should().Be("text/html");
        string markup = await response.Content.ReadAsStringAsync();
        markup.Should().Contain("<!DOCTYPE html>");
        markup.Should().Contain("<html");
        markup.Should().Contain("<body");
        markup.Should().Contain("Project KONGOR");
    }

    [Fact]
    public async Task Route02_Matches_ReturnsHttp200WithCompleteMarkup()
    {
        // /matches requires authentication
        HttpClient client = await _fixture.CreateAuthenticatedClientAsync();

        HttpResponseMessage response = await client.GetAsync("/matches");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType?.MediaType.Should().Be("text/html");
        string markup = await response.Content.ReadAsStringAsync();
        markup.Should().Contain("<!DOCTYPE html>");
        markup.Should().Contain("Match History");
    }

    [Fact]
    public async Task Route03_Stats_ReturnsHttp200WithCompleteMarkup()
    {
        HttpClient client = await _fixture.CreateAuthenticatedClientAsync();

        HttpResponseMessage response = await client.GetAsync("/stats");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType?.MediaType.Should().Be("text/html");
        string markup = await response.Content.ReadAsStringAsync();
        markup.Should().Contain("<!DOCTYPE html>");
        markup.Should().Contain("Player Analytics");
    }

    [Fact]
    public async Task Route04_Leaderboard_ReturnsHttp200WithCompleteMarkup()
    {
        HttpClient client = await _fixture.CreateAuthenticatedClientAsync();

        HttpResponseMessage response = await client.GetAsync("/leaderboard");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType?.MediaType.Should().Be("text/html");
        string markup = await response.Content.ReadAsStringAsync();
        markup.Should().Contain("<!DOCTYPE html>");
        markup.Should().Contain("Leaderboard");
    }

    [Fact]
    public async Task Route05_Codex_ReturnsHttp200WithCompleteMarkup()
    {
        HttpClient client = await _fixture.CreateAuthenticatedClientAsync();

        HttpResponseMessage response = await client.GetAsync("/codex");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType?.MediaType.Should().Be("text/html");
        string markup = await response.Content.ReadAsStringAsync();
        markup.Should().Contain("<!DOCTYPE html>");
        markup.Should().Contain("Hero Codex");
    }

    [Fact]
    public async Task Route06_CodexHero_ReturnsHttp200WithCompleteMarkup()
    {
        HttpClient client = await _fixture.CreateAuthenticatedClientAsync();

        HttpResponseMessage response = await client.GetAsync("/codex/Hero_Vindicator");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType?.MediaType.Should().Be("text/html");
        string markup = await response.Content.ReadAsStringAsync();
        markup.Should().Contain("<!DOCTYPE html>");
        markup.Should().Contain("Vindicator");
    }

    [Fact]
    public async Task Route07_Items_ReturnsHttp200WithCompleteMarkup()
    {
        HttpClient client = await _fixture.CreateAuthenticatedClientAsync();

        HttpResponseMessage response = await client.GetAsync("/items");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType?.MediaType.Should().Be("text/html");
        string markup = await response.Content.ReadAsStringAsync();
        markup.Should().Contain("<!DOCTYPE html>");
        markup.Should().Contain("Item Compendium");
    }

    [Fact]
    public async Task Route08_Guides_ReturnsHttp200WithCompleteMarkup()
    {
        HttpClient client = await _fixture.CreateAuthenticatedClientAsync();

        HttpResponseMessage response = await client.GetAsync("/guides");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType?.MediaType.Should().Be("text/html");
        string markup = await response.Content.ReadAsStringAsync();
        markup.Should().Contain("<!DOCTYPE html>");
        markup.Should().Contain("Guides");
    }

    [Fact]
    public async Task Route09_GuidesId_ReturnsHttp200WithCompleteMarkup()
    {
        HttpClient client = await _fixture.CreateAuthenticatedClientAsync();

        HttpResponseMessage response = await client.GetAsync("/guides/1");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType?.MediaType.Should().Be("text/html");
        string markup = await response.Content.ReadAsStringAsync();
        markup.Should().Contain("<!DOCTYPE html>");
        markup.Should().Contain("Guide");
    }

    [Fact]
    public async Task Route10_Armory_ReturnsHttp200WithCompleteMarkup()
    {
        HttpClient client = await _fixture.CreateAuthenticatedClientAsync();

        HttpResponseMessage response = await client.GetAsync("/armory");

        // Per PROJECT.md Feature 25 contract: Top-level route /armory resolves with 200 or 302 redirect
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType?.MediaType.Should().Be("text/html");
        string markup = await response.Content.ReadAsStringAsync();
        markup.Should().Contain("<!DOCTYPE html>");
        markup.Should().Contain("Store");
    }

    [Fact]
    public async Task Route11_Store_ReturnsHttp200WithCompleteMarkup()
    {
        HttpClient client = await _fixture.CreateAuthenticatedClientAsync();

        HttpResponseMessage response = await client.GetAsync("/store");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType?.MediaType.Should().Be("text/html");
        string markup = await response.Content.ReadAsStringAsync();
        markup.Should().Contain("<!DOCTYPE html>");
        markup.Should().Contain("Store");
    }

    [Fact]
    public async Task Route12_Inventory_ReturnsHttp200WithCompleteMarkup()
    {
        // Authenticate with staff credentials to verify full inventory dashboard rendering
        HttpClient client = await _fixture.CreateAuthenticatedClientAsync();

        HttpResponseMessage response = await client.GetAsync("/inventory");

        // Per PROJECT.md Feature 25 contract: Top-level route /inventory resolves with 200 or 302 redirect
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType?.MediaType.Should().Be("text/html");
        string markup = await response.Content.ReadAsStringAsync();
        markup.Should().Contain("<!DOCTYPE html>");
        markup.Should().Contain("Inventory");
    }

    [Fact]
    public async Task Route13_Plinko_ReturnsHttp200WithCompleteMarkup()
    {
        HttpClient client = await _fixture.CreateAuthenticatedClientAsync();

        HttpResponseMessage response = await client.GetAsync("/plinko");

        // Per PROJECT.md Feature 25 contract: Top-level route /plinko resolves with 200 or 302 redirect
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType?.MediaType.Should().Be("text/html");
        string markup = await response.Content.ReadAsStringAsync();
        markup.Should().Contain("<!DOCTYPE html>");
        markup.Should().Contain("Plinko");
    }

    [Fact]
    public async Task Route14_Setup_ReturnsHttp200WithCompleteMarkup()
    {
        // /setup requires authentication
        HttpClient client = await _fixture.CreateAuthenticatedClientAsync();

        HttpResponseMessage response = await client.GetAsync("/setup");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType?.MediaType.Should().Be("text/html");
        string markup = await response.Content.ReadAsStringAsync();
        markup.Should().Contain("<!DOCTYPE html>");
        markup.Should().Contain("Setup");
    }

    [Fact]
    public async Task Route15_Staff_ReturnsHttp200WithCompleteMarkup()
    {
        // /staff requires authenticated staff user
        HttpClient client = await _fixture.CreateAuthenticatedClientAsync();

        HttpResponseMessage response = await client.GetAsync("/staff");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType?.MediaType.Should().Be("text/html");
        string markup = await response.Content.ReadAsStringAsync();
        markup.Should().Contain("<!DOCTYPE html>");
        markup.Should().Contain("Staff");
    }
}
