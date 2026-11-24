// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Net;
using System.Security.Claims;
using DAWNBRINGER.WebPortal.UI.Controllers;
using DAWNBRINGER.WebPortal.UI.Models;
using FluentAssertions;
using MERRICK.DatabaseContext.Entities.Core;
using MERRICK.DatabaseContext.Entities.Game;
using MERRICK.DatabaseContext.Entities.Statistics;
using MERRICK.DatabaseContext.Persistence;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using NEXUS.Contracts.Clients;
using Xunit;

namespace ASPIRE.Tests.DAWNBRINGER.WebPortal.UI.Tests;

[Collection("DawnbringerE2E")]
public sealed class DawnbringerAdversarialConcurrencyTests(DawnbringerE2ETestFixture fixture)
{
    private readonly DawnbringerE2ETestFixture _fixture = fixture;

    // =========================================================================
    // 1. ALL PARAMETER PERMUTATIONS FOR PLAYER PROFILE RESOLUTION
    // =========================================================================

    [Theory]
    [InlineData("/stats/guccidonut")]
    [InlineData("/stats?accountName=guccidonut")]
    [InlineData("/stats?target=guccidonut")]
    [InlineData("/Home/Stats/guccidonut")]
    [InlineData("/Home/Stats?accountName=guccidonut")]
    [InlineData("/Home/Stats?target=guccidonut")]
    [InlineData("/player/guccidonut")]
    public async Task AllSevenParameterPermutations_ResolveGuccidonutStats_Return200WithMmr(string url)
    {
        HttpClient client = await _fixture.CreateAuthenticatedClientAsync();

        HttpResponseMessage response = await client.GetAsync(url);

        response.StatusCode.Should().Be(HttpStatusCode.OK, $"URL {url} must return HTTP 200 OK");
        string markup = await response.Content.ReadAsStringAsync();

        markup.Should().Contain("guccidonut", $"URL {url} markup should contain account name");
        markup.Should().Contain("Player Analytics", $"URL {url} should render Player Analytics title");
        markup.Should().Contain("1750", $"URL {url} should display guccidonut's resolved MMR of 1750");

        // Zero unhandled 500 exceptions or crash signatures
        markup.Should().NotContain("InvalidOperationException");
        markup.Should().NotContain("NullReferenceException");
        markup.Should().NotContain("Internal Server Error");
    }

    [Theory]
    [InlineData("/stats/normalplayer")]
    [InlineData("/stats?accountName=normalplayer")]
    [InlineData("/stats?target=normalplayer")]
    [InlineData("/Home/Stats/normalplayer")]
    [InlineData("/Home/Stats?accountName=normalplayer")]
    [InlineData("/Home/Stats?target=normalplayer")]
    [InlineData("/player/normalplayer")]
    public async Task AllSevenParameterPermutations_ResolveNormalPlayerStats_Return200WithMmr(string url)
    {
        HttpClient client = await _fixture.CreateAuthenticatedClientAsync();

        HttpResponseMessage response = await client.GetAsync(url);

        response.StatusCode.Should().Be(HttpStatusCode.OK, $"URL {url} must return HTTP 200 OK");
        string markup = await response.Content.ReadAsStringAsync();

        markup.Should().Contain("normalplayer", $"URL {url} markup should contain normalplayer");
        markup.Should().Contain("Player Analytics");
        markup.Should().Contain("1500", $"URL {url} should display normalplayer's resolved MMR of 1500");

        markup.Should().NotContain("InvalidOperationException");
        markup.Should().NotContain("NullReferenceException");
        markup.Should().NotContain("Internal Server Error");
    }

    // =========================================================================
    // 2. CASE-INSENSITIVITY STRESS-TESTS
    // =========================================================================

    [Theory]
    [InlineData("/stats/GUCCIDONUT")]
    [InlineData("/stats?accountName=GucciDonut")]
    [InlineData("/stats?target=gUcCiDoNuT")]
    [InlineData("/Home/Stats/GUCCIDONUT")]
    [InlineData("/Home/Stats?accountName=GucciDonut")]
    [InlineData("/Home/Stats?target=gucciDONUT")]
    [InlineData("/player/GUCCIDONUT")]
    public async Task DawnbringerAdversarialConcurrency_CaseInsensitivity_ResolvesAccountAndStatsCorrectly(string url)
    {
        HttpClient client = await _fixture.CreateAuthenticatedClientAsync();

        HttpResponseMessage response = await client.GetAsync(url);

        response.StatusCode.Should().Be(HttpStatusCode.OK, $"Case variation {url} must return HTTP 200");
        string markup = await response.Content.ReadAsStringAsync();

        markup.Should().ContainEquivalentOf("guccidonut");
        markup.Should().Contain("1750", $"Case variation {url} must still resolve authentic MMR 1750");
        markup.Should().NotContain("Internal Server Error");
    }

    // =========================================================================
    // 3. EMPTY & WHITESPACE BOUNDARY TESTS
    // =========================================================================

    [Theory]
    [InlineData("/stats?accountName=")]
    [InlineData("/stats?target=")]
    [InlineData("/Home/Stats?accountName=")]
    [InlineData("/Home/Stats?target=")]
    [InlineData("/stats?accountName=&target=")]
    public async Task EmptyQueryStrings_AnonymousUser_RedirectsToMinimalHomepageGate_Without500(string url)
    {
        HttpClient noRedirectClient = _fixture.CreateNoRedirectClient();

        HttpResponseMessage response = await noRedirectClient.GetAsync(url);

        response.StatusCode.Should().Be(HttpStatusCode.Redirect, $"Anonymous empty query {url} must redirect to Discord gate without 500");
        response.Headers.Location.Should().NotBeNull();
        response.Headers.Location!.ToString().Should().StartWith("/?");
    }

    [Theory]
    [InlineData("/stats?accountName=%20%20")]
    [InlineData("/stats?target=%20%20")]
    [InlineData("/Home/Stats?accountName=%20%20")]
    [InlineData("/Home/Stats?target=%20%20")]
    public async Task WhitespaceQueries_AnonymousUser_RedirectsToMinimalHomepageGate_Without500(string url)
    {
        HttpClient noRedirectClient = _fixture.CreateNoRedirectClient();

        HttpResponseMessage response = await noRedirectClient.GetAsync(url);

        response.StatusCode.Should().Be(HttpStatusCode.Redirect, $"Anonymous whitespace query {url} must redirect to Discord gate without 500");
        response.Headers.Location.Should().NotBeNull();
        response.Headers.Location!.ToString().Should().StartWith("/?");
    }

    [Theory]
    [InlineData("/stats?accountName=")]
    [InlineData("/stats?target=")]
    [InlineData("/stats?accountName=%20%20")]
    public async Task EmptyOrWhitespaceQueries_AuthenticatedUser_FallsBackToAuthClaims_Without500(string url)
    {
        HttpClient client = await _fixture.CreateAuthenticatedClientAsync();

        HttpResponseMessage response = await client.GetAsync(url);

        response.StatusCode.Should().Be(HttpStatusCode.OK, $"Authenticated empty/whitespace query {url} must return 200");
        string markup = await response.Content.ReadAsStringAsync();

        // Authenticated fixture user is guccidonut
        markup.Should().Contain("guccidonut", "Authenticated fallback should resolve user's own claims");
        markup.Should().Contain("1750", "Should display authentic MMR for authenticated user");
        markup.Should().NotContain("Internal Server Error");
    }

    // =========================================================================
    // 4. NON-EXISTENT PLAYER ACCOUNTS (GRACEFUL FALLBACK)
    // =========================================================================

    [Theory]
    [InlineData("/stats/nonexistent_player_123")]
    [InlineData("/stats?accountName=nonexistent_player_123")]
    [InlineData("/stats?target=nonexistent_player_123")]
    [InlineData("/Home/Stats/nonexistent_player_123")]
    [InlineData("/Home/Stats?accountName=nonexistent_player_123")]
    [InlineData("/player/nonexistent_player_123")]
    public async Task NonExistentPlayer_GracefullyRendersPlaceholder_Without500(string url)
    {
        HttpClient client = await _fixture.CreateAuthenticatedClientAsync();

        HttpResponseMessage response = await client.GetAsync(url);

        response.StatusCode.Should().Be(HttpStatusCode.OK, $"Non-existent player route {url} should return 200 with placeholder");
        string markup = await response.Content.ReadAsStringAsync();

        markup.Should().Contain("nonexistent_player_123", "Placeholder view should display requested account name");
        markup.Should().Contain("Player Analytics");
        markup.Should().NotContain("InvalidOperationException");
        markup.Should().NotContain("NullReferenceException");
        markup.Should().NotContain("Internal Server Error");
    }

    // =========================================================================
    // 5. ADVERSARIAL INPUTS & BOUNDARY RESILIENCE
    // =========================================================================

    [Theory]
    [InlineData("/stats?accountName=%3Cscript%3Ealert(1)%3C%2Fscript%3E")]
    [InlineData("/stats?accountName=guccidonut%27%20OR%201=1--")]
    [InlineData("/stats?accountName=%F0%9F%A6%8D")]
    [InlineData("/stats?accountName=player+with+plus")]
    public async Task DawnbringerAdversarialConcurrency_MalformedAndAdversarialInputs_DoNotCause500Exceptions(string url)
    {
        HttpClient client = await _fixture.CreateAuthenticatedClientAsync();

        HttpResponseMessage response = await client.GetAsync(url);

        // Expect HTTP 200 (placeholder) or standard 400 Bad Request — never 500
        response.StatusCode.Should().BeOneOf(HttpStatusCode.OK, HttpStatusCode.BadRequest, HttpStatusCode.NotFound);

        string markup = await response.Content.ReadAsStringAsync();
        markup.Should().NotContain("Internal Server Error");
        markup.Should().NotContain("InvalidOperationException");
        markup.Should().NotContain("NullReferenceException");
    }

    // =========================================================================
    // 6. PARAMETER PRECEDENCE STRESS-TESTS
    // =========================================================================

    [Fact]
    public async Task Precedence_RouteParameter_TakesPrecedenceOverQueryString()
    {
        HttpClient client = await _fixture.CreateAuthenticatedClientAsync();

        // Route token is guccidonut (MMR 1750), query is normalplayer (MMR 1500)
        HttpResponseMessage response = await client.GetAsync("/stats/guccidonut?accountName=normalplayer");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        string markup = await response.Content.ReadAsStringAsync();

        markup.Should().Contain("guccidonut");
        markup.Should().Contain("1750");
    }

    [Fact]
    public async Task Precedence_AccountNameQuery_TakesPrecedenceOverTargetQuery()
    {
        HttpClient client = await _fixture.CreateAuthenticatedClientAsync();

        // accountName is guccidonut (MMR 1750), target is normalplayer (MMR 1500)
        HttpResponseMessage response = await client.GetAsync("/stats?accountName=guccidonut&target=normalplayer");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        string markup = await response.Content.ReadAsStringAsync();

        markup.Should().Contain("guccidonut");
        markup.Should().Contain("1750");
    }

    // =========================================================================
    // 7. DIRECT CONTROLLER UNIT TESTS FOR PLAYERSTATSVIEWMODEL INTROSPECTION
    // =========================================================================

    private static HomeController CreateDirectController(MerrickContext context, ClaimsPrincipal? user = null)
    {
        IConfiguration config = new ConfigurationBuilder().Build();
        IUserApiClient userApi = Infrastructure.TestApiClientFactory.CreateUserApiClient(context);
        IAdminApiClient adminApi = Infrastructure.TestApiClientFactory.CreateAdminApiClient(context);
        IPlayerStatsApiClient statsApi = Infrastructure.TestApiClientFactory.CreatePlayerStatsApiClient(context);
        IGuideApiClient guideApi = Infrastructure.TestApiClientFactory.CreateGuideApiClient(context);

        DefaultHttpContext httpContext = new();
        if (user != null)
        {
            httpContext.User = user;
        }

        HomeController controller = new(statsApi, userApi, guideApi, adminApi, config)
        {
            ControllerContext = new ControllerContext { HttpContext = httpContext }
        };
        return controller;
    }

    [Fact]
    public async Task DirectController_PopulatesStronglyTypedPlayerStatsViewModel_WithAccurateValues()
    {
        await using MerrickContext context = TestDbContextFactory.CreateInMemoryContext();
        SeedAdditionalTestData(context);

        HomeController controller = CreateDirectController(context);

        IActionResult result = await controller.Stats("testpro");

        result.Should().BeOfType<ViewResult>();
        ViewResult viewResult = (ViewResult)result;
        PlayerStatsViewModel model = viewResult.Model.Should().BeOfType<PlayerStatsViewModel>().Subject;

        model.AccountName.Should().Be("testpro");
        model.TotalMatches.Should().Be(50);
        model.MatchmakingMatches.Should().Be(50);
        model.SkillRating.Should().Be(1820);
        model.WinRate.Should().Be(64.0);
        model.PlayerStatus.Should().Be("Active");
        model.TopHeroes.Should().NotBeEmpty();
    }

    [Fact]
    public async Task DirectController_NonExistentAccount_ReturnsPlaceholderViewModel()
    {
        await using MerrickContext context = TestDbContextFactory.CreateInMemoryContext();
        HomeController controller = CreateDirectController(context);

        IActionResult result = await controller.Stats("ghost_player_999");

        result.Should().BeOfType<ViewResult>();
        ViewResult viewResult = (ViewResult)result;
        PlayerStatsViewModel model = viewResult.Model.Should().BeOfType<PlayerStatsViewModel>().Subject;

        model.AccountName.Should().Be("ghost_player_999");
        model.TotalMatches.Should().Be(0);
        model.SkillRating.Should().Be(1500);
        model.WinRate.Should().Be(0.0);
        model.TopHeroes.Should().BeEmpty();
    }

    [Fact]
    public async Task DirectController_EmptyInput_Anonymous_ReturnsGuestViewModel()
    {
        await using MerrickContext context = TestDbContextFactory.CreateInMemoryContext();
        HomeController controller = CreateDirectController(context);

        IActionResult result = await controller.Stats(null, null, null);

        result.Should().BeOfType<ViewResult>();
        ViewResult viewResult = (ViewResult)result;
        PlayerStatsViewModel model = viewResult.Model.Should().BeOfType<PlayerStatsViewModel>().Subject;

        model.AccountName.Should().Be("Guest");
        model.TotalMatches.Should().Be(0);
        model.SkillRating.Should().Be(1500);
        model.WinRate.Should().Be(0.0);
    }

    private static void SeedAdditionalTestData(MerrickContext db)
    {
        DateTimeOffset now = TimeProvider.System.GetUtcNow();
        Role role = new() { ID = 10, Name = "User" };
        User user = new()
        {
            ID = 10,
            EmailAddress = "testpro@kongor.net",
            DiscordId = "test-discord-pro",
            DiscordUsername = "testpro",
            Role = role,
            PBKDF2PasswordHash = "h",
            SRPPasswordHash = "h",
            SRPPasswordSalt = "s"
        };
        Account account = new()
        {
            ID = 10,
            Name = "testpro",
            IsMain = true,
            Type = AccountType.Normal,
            TimestampCreated = now.AddDays(-100).UtcDateTime,
            User = user,
            UserID = user.ID
        };
        user.Accounts = [account];

        db.Roles.Add(role);
        db.Users.Add(user);
        db.Accounts.Add(account);

        db.AccountStatistics.Add(new AccountStatistics
        {
            AccountID = account.ID,
            Type = AccountStatisticsType.Matchmaking,
            MatchesPlayed = 50,
            MatchesWon = 32,
            MatchesLost = 18,
            SkillRating = 1820
        });

        for (int i = 1; i <= 50; i++)
        {
            db.PlayerStatistics.Add(new PlayerStatistics
            {
                MatchID = 99000 + i,
                AccountID = account.ID,
                AccountName = account.Name,
                Team = 1,
                HeroIdentifier = "Hero_Pyromancer",
                Win = i <= 32 ? 1 : 0,
                Loss = i <= 32 ? 0 : 1,
                HeroKills = 10,
                HeroDeaths = 3,
                HeroAssists = 8,
                HeroDamage = 25000,
                Inventory = []
            });
        }

        db.SaveChanges();
    }
}
