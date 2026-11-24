// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Net;
using FluentAssertions;
using Xunit;

namespace ASPIRE.Tests.DAWNBRINGER.WebPortal.UI.Tests;

[Collection("DawnbringerE2E")]
public class DawnbringerHttpRouteResolutionTests(DawnbringerE2ETestFixture fixture)
{
    private readonly DawnbringerE2ETestFixture _fixture = fixture;

    // =========================================================================
    // 1. /guides ENDPOINT INVOCATION & RENDERING
    // =========================================================================

    [Fact]
    public async Task Route01_Guides_Anonymous_RedirectsToMinimalHomepageGate()
    {
        HttpClient noRedirectClient = _fixture.CreateNoRedirectClient();

        HttpResponseMessage response = await noRedirectClient.GetAsync("/guides");

        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        response.Headers.Location.Should().NotBeNull();
        response.Headers.Location!.ToString().Should().StartWith("/?");
        response.Headers.Location!.ToString().ToLowerInvariant().Should().Contain("returnurl");
    }

    [Fact]
    public async Task Route01b_Guides_Authenticated_ReturnsHttp200WithCompleteMarkup()
    {
        HttpClient client = await _fixture.CreateAuthenticatedClientAsync();

        HttpResponseMessage response = await client.GetAsync("/guides");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType?.MediaType.Should().Be("text/html");
        string markup = await response.Content.ReadAsStringAsync();
        markup.Should().Contain("<!DOCTYPE html>");
        markup.Should().Contain("Guides");
        markup.Should().NotContain("InvalidOperationException");
    }

    // =========================================================================
    // 2. /guides/1 ENDPOINT INVOCATION & RENDERING
    // =========================================================================

    [Fact]
    public async Task Route02_GuidesId_ReturnsHttp200WithCompleteMarkup()
    {
        HttpClient client = await _fixture.CreateAuthenticatedClientAsync();

        HttpResponseMessage response = await client.GetAsync("/guides/1");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType?.MediaType.Should().Be("text/html");
        string markup = await response.Content.ReadAsStringAsync();
        markup.Should().Contain("<!DOCTYPE html>");
        markup.Should().Contain("<html");
        markup.Should().Contain("Competitive Vindicator Mid/Carry");
        markup.Should().NotContain("InvalidOperationException");
        markup.Should().NotContain("NullReferenceException");
    }

    // =========================================================================
    // 3. /guides/detail/1 ENDPOINT INVOCATION & RENDERING
    // =========================================================================

    [Fact]
    public async Task Route03_GuidesDetailId_ReturnsHttp200WithCompleteMarkup()
    {
        HttpClient client = await _fixture.CreateAuthenticatedClientAsync();

        HttpResponseMessage response = await client.GetAsync("/guides/detail/1");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType?.MediaType.Should().Be("text/html");
        string markup = await response.Content.ReadAsStringAsync();
        markup.Should().Contain("<!DOCTYPE html>");
        markup.Should().Contain("<html");
        markup.Should().Contain("Competitive Vindicator Mid/Carry");
        markup.Should().NotContain("InvalidOperationException");
        markup.Should().NotContain("NullReferenceException");
    }

    // =========================================================================
    // 4. /guides/builder ENDPOINT INVOCATION & RENDERING
    // =========================================================================

    [Fact]
    public async Task Route04a_GuidesBuilder_Anonymous_RedirectsToLoginWithout500()
    {
        HttpClient noRedirectClient = _fixture.CreateNoRedirectClient();

        HttpResponseMessage response = await noRedirectClient.GetAsync("/guides/builder");

        // Anonymous access correctly enforces authentication redirect to minimalist homepage Discord gate
        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        response.Headers.Location.Should().NotBeNull();
        response.Headers.Location!.ToString().ToLowerInvariant().Should().Contain("returnurl");
    }

    [Fact]
    public async Task Route04b_GuidesBuilder_Authenticated_ReturnsHttp200WithCompleteMarkup()
    {
        HttpClient client = await _fixture.CreateAuthenticatedClientAsync();

        HttpResponseMessage response = await client.GetAsync("/guides/builder");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType?.MediaType.Should().Be("text/html");
        string markup = await response.Content.ReadAsStringAsync();
        markup.Should().Contain("<!DOCTYPE html>");
        markup.Should().Contain("<html");
        markup.Should().Contain("HERO GUIDE STUDIO");
        markup.Should().Contain("Create Hero Guide");
        markup.Should().NotContain("InvalidOperationException");
        markup.Should().NotContain("NullReferenceException");
    }

    [Theory]
    [InlineData("/guides/builder?hero=Hero_Pyromancer")]
    [InlineData("/guides/builder?hero=Hero_Vindicator")]
    [InlineData("/guides/create?hero=Hero_Devourer")]
    public async Task Route04c_GuidesBuilder_WithHeroParameters_ReturnsHttp200(string url)
    {
        HttpClient client = await _fixture.CreateAuthenticatedClientAsync();

        HttpResponseMessage response = await client.GetAsync(url);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType?.MediaType.Should().Be("text/html");
        string markup = await response.Content.ReadAsStringAsync();
        markup.Should().Contain("<!DOCTYPE html>");
        markup.Should().NotContain("InvalidOperationException");
    }

    // =========================================================================
    // 5. /codex ENDPOINT INVOCATION & RENDERING
    // =========================================================================

    [Fact]
    public async Task Route05_Codex_ReturnsHttp200WithCompleteMarkup()
    {
        HttpClient client = await _fixture.CreateAuthenticatedClientAsync();

        HttpResponseMessage response = await client.GetAsync("/codex");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType?.MediaType.Should().Be("text/html");
        string markup = await response.Content.ReadAsStringAsync();
        markup.Should().Contain("<!DOCTYPE html>");
        markup.Should().Contain("<html");
        markup.Should().Contain("Hero Codex");
        markup.Should().NotContain("InvalidOperationException");
        markup.Should().NotContain("NullReferenceException");
    }

    // =========================================================================
    // 6. /codex/Hero_Pyromancer ENDPOINT INVOCATION & RENDERING
    // =========================================================================

    [Fact]
    public async Task Route06_CodexHeroPyromancer_ReturnsHttp200WithCompleteMarkup()
    {
        HttpClient client = await _fixture.CreateAuthenticatedClientAsync();

        HttpResponseMessage response = await client.GetAsync("/codex/Hero_Pyromancer");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType?.MediaType.Should().Be("text/html");
        string markup = await response.Content.ReadAsStringAsync();
        markup.Should().Contain("<!DOCTYPE html>");
        markup.Should().Contain("<html");
        markup.Should().Contain("Pyromancer");
        markup.Should().NotContain("InvalidOperationException");
        markup.Should().NotContain("NullReferenceException");
    }

    // =========================================================================
    // 7. /Home/HeroDetail/Hero_Pyromancer ENDPOINT INVOCATION & RENDERING
    // =========================================================================

    [Fact]
    public async Task Route07_HomeHeroDetailHeroPyromancer_ReturnsHttp200WithCompleteMarkup()
    {
        HttpClient client = await _fixture.CreateAuthenticatedClientAsync();

        HttpResponseMessage response = await client.GetAsync("/Home/HeroDetail/Hero_Pyromancer");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType?.MediaType.Should().Be("text/html");
        string markup = await response.Content.ReadAsStringAsync();
        markup.Should().Contain("<!DOCTYPE html>");
        markup.Should().Contain("<html");
        markup.Should().Contain("Pyromancer");
        markup.Should().NotContain("InvalidOperationException");
        markup.Should().NotContain("NullReferenceException");
    }

    // =========================================================================
    // 8. /items/detail/1 OR /Home/ItemDetail/1 ENDPOINT INVOCATION & RENDERING
    // =========================================================================

    [Fact]
    public async Task Route08a_HomeItemDetail_ByCode_ReturnsHttp200WithCompleteMarkup()
    {
        HttpClient client = await _fixture.CreateAuthenticatedClientAsync();

        HttpResponseMessage response = await client.GetAsync("/Home/ItemDetail/Item_LoggersHatchet");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType?.MediaType.Should().Be("text/html");
        string markup = await response.Content.ReadAsStringAsync();
        markup.Should().Contain("<!DOCTYPE html>");
        markup.Should().Contain("<html");
        markup.Should().Contain("Logger&#x27;s Hatchet");
        markup.Should().Contain("Item Code:");
        markup.Should().Contain("Item_LoggersHatchet");
        markup.Should().NotContain("InvalidOperationException");
        markup.Should().NotContain("NullReferenceException");
    }

    [Fact]
    public async Task Route08b_HomeItemDetail_NumericParam_HandlesGracefullyWithout500()
    {
        // When requesting /Home/ItemDetail/1, HomeController checks code against item catalog.
        // If numeric '1' is not found as a code, it cleanly redirects to /Home/Items.
        HttpClient client = await _fixture.CreateAuthenticatedClientAsync();

        HttpResponseMessage response = await client.GetAsync("/Home/ItemDetail/1");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType?.MediaType.Should().Be("text/html");
        string markup = await response.Content.ReadAsStringAsync();
        markup.Should().Contain("<!DOCTYPE html>");
        markup.Should().Contain("<html");
        markup.Should().NotContain("InvalidOperationException");
        markup.Should().NotContain("NullReferenceException");
    }

    [Fact]
    public async Task Route08c_HomeItemDetail_NumericParam_NoRedirect_ReturnsRedirectResult()
    {
        HttpClient client = await _fixture.CreateAuthenticatedNoRedirectClientAsync();

        HttpResponseMessage response = await client.GetAsync("/Home/ItemDetail/1");

        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        response.Headers.Location.Should().NotBeNull();
        response.Headers.Location!.ToString().Should().Contain("Items");
    }

    [Fact]
    public async Task Route08d_Items_ItemCode_ReturnsHttp200WithCompleteMarkup()
    {
        HttpClient client = await _fixture.CreateAuthenticatedClientAsync();

        HttpResponseMessage response = await client.GetAsync("/items/Item_LoggersHatchet");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType?.MediaType.Should().Be("text/html");
        string markup = await response.Content.ReadAsStringAsync();
        markup.Should().Contain("<!DOCTYPE html>");
        markup.Should().Contain("Logger&#x27;s Hatchet");
    }

    // =========================================================================
    // 9. ADVERSARIAL BOUNDARY & NON-EXISTENT RESOURCE ROUTING
    // =========================================================================

    [Fact]
    public async Task Route09_NonExistentGuide_Returns404NotFoundWithout500()
    {
        HttpClient client = await _fixture.CreateAuthenticatedClientAsync();

        HttpResponseMessage response = await client.GetAsync("/guides/999999");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Route10_NonExistentHero_GracefullyRedirectsToCodexWithout500()
    {
        HttpClient client = await _fixture.CreateAuthenticatedNoRedirectClientAsync();

        HttpResponseMessage response = await client.GetAsync("/codex/Hero_NonExistentHero_12345");

        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        response.Headers.Location.Should().NotBeNull();
        response.Headers.Location!.ToString().Should().Contain("Codex");
    }

    [Fact]
    public async Task Route11_NonExistentItem_GracefullyRedirectsToItemsWithout500()
    {
        HttpClient client = await _fixture.CreateAuthenticatedNoRedirectClientAsync();

        HttpResponseMessage response = await client.GetAsync("/Home/ItemDetail/Item_NonExistentItem_12345");

        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        response.Headers.Location.Should().NotBeNull();
        response.Headers.Location!.ToString().Should().Contain("Items");
    }
}
