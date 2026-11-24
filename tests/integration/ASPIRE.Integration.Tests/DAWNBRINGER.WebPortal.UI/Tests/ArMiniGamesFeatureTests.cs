// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.IO;
using System.Reflection;
using System.Security.Claims;
using DAWNBRINGER.WebPortal.UI.Controllers;
using DAWNBRINGER.WebPortal.UI.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ASPIRE.Tests.DAWNBRINGER.WebPortal.UI.Tests;

public sealed class ArMiniGamesFeatureTests
{
    private static string FindRepoRoot()
    {
        string dir = Directory.GetCurrentDirectory();
        while (!string.IsNullOrEmpty(dir))
        {
            if (File.Exists(Path.Combine(dir, "ProjectKongor.sln")) || Directory.Exists(Path.Combine(dir, "src", "services", "web-ui")))
            {
                return dir;
            }
            string? parent = Directory.GetParent(dir)?.FullName;
            if (string.IsNullOrEmpty(parent) || parent == dir) break;
            dir = parent;
        }
        return Directory.GetCurrentDirectory();
    }

    [Fact]
    public void ArMiniGamesController_HasClassLevelAuthorizeAttribute()
    {
        var authAttr = typeof(ArMiniGamesController).GetCustomAttribute<AuthorizeAttribute>();
        Assert.NotNull(authAttr);
    }

    [Fact]
    public void ArMiniGamesController_HasSingleRouteAttribute_PreventingAmbiguousMatch()
    {
        var routeAttrs = typeof(ArMiniGamesController)
            .GetCustomAttributes<RouteAttribute>()
            .Select(r => r.Template)
            .ToList();

        Assert.Single(routeAttrs);
        Assert.Equal("ar-mini-games", routeAttrs[0]);
    }

    [Theory]
    [InlineData("Index")]
    [InlineData("MonkeyCheckers")]
    [InlineData("DevoWars")]
    [InlineData("Empath")]
    [InlineData("KongorChess")]
    [InlineData("Riftbreaker")]
    public void ArMiniGamesController_Actions_DoNotAllowAnonymous(string actionName)
    {
        MethodInfo? method = typeof(ArMiniGamesController).GetMethod(actionName);
        Assert.NotNull(method);

        var allowAnon = method.GetCustomAttribute<AllowAnonymousAttribute>();
        Assert.Null(allowAnon);
    }

    [Fact]
    public void ArMiniGamesController_WhenDiscordAuthenticated_ServesAllViews()
    {
        ArMiniGamesController controller = new(NullLogger<ArMiniGamesController>.Instance);
        var claims = new[]
        {
            new Claim(ClaimTypes.Name, "DiscordUser"),
            new Claim("discord_id", "999888777")
        };
        var identity = new ClaimsIdentity(claims, "DiscordAuth");
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(identity) }
        };

        // 1. Index
        IActionResult indexResult = controller.Index();
        ViewResult indexView = Assert.IsType<ViewResult>(indexResult);
        Assert.Contains("Donut Mixed Reality Arcade", controller.ViewData["Title"]?.ToString());

        // 2. MonkeyCheckers
        IActionResult checkersResult = controller.MonkeyCheckers();
        ViewResult checkersView = Assert.IsType<ViewResult>(checkersResult);
        Assert.Contains("Monkey King", controller.ViewData["Title"]?.ToString());
        Assert.Equal("MK42", controller.ViewData["InitialRoom"]);

        // 3. DevoWars
        IActionResult devoResult = controller.DevoWars();
        ViewResult devoView = Assert.IsType<ViewResult>(devoResult);
        Assert.Contains("DevoWarsAR", controller.ViewData["Title"]?.ToString());
        Assert.Equal("HOOK", controller.ViewData["InitialRoom"]);

        // 4. Empath
        IActionResult empathResult = controller.Empath();
        ViewResult empathView = Assert.IsType<ViewResult>(empathResult);
        Assert.Contains("Empath Tabletop AR", controller.ViewData["Title"]?.ToString());

        // 5. KongorChess
        IActionResult chessResult = controller.KongorChess();
        ViewResult chessView = Assert.IsType<ViewResult>(chessResult);
        Assert.Contains("KONGOR AR Chess", controller.ViewData["Title"]?.ToString());
        Assert.Equal("KNG1", controller.ViewData["InitialRoom"]);

        // 6. Riftbreaker (Prototype preserved behind Discord gate for future dev)
        IActionResult riftResult = controller.Riftbreaker("rift-99!");
        ViewResult riftView = Assert.IsType<ViewResult>(riftResult);
        Assert.Contains("Chrono-Pong: Riftbreaker", riftView.ViewData["Title"]?.ToString());
        Assert.Equal("RIFT99", riftView.ViewData["InitialRoom"]);
    }

    [Fact]
    public void ArMiniGamesController_WhenNonDiscordAuthenticated_RedirectsToDiscordLogin()
    {
        ArMiniGamesController controller = new(NullLogger<ArMiniGamesController>.Instance);
        var claims = new[]
        {
            new Claim(ClaimTypes.Name, "StandardUser")
        };
        var identity = new ClaimsIdentity(claims, "Cookies");
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(identity) }
        };

        // Index
        IActionResult indexResult = controller.Index();
        RedirectToActionResult indexRedirect = Assert.IsType<RedirectToActionResult>(indexResult);
        Assert.Equal("LoginDiscord", indexRedirect.ActionName);
        Assert.Equal("Account", indexRedirect.ControllerName);

        // MonkeyCheckers
        IActionResult checkersResult = controller.MonkeyCheckers();
        RedirectToActionResult checkersRedirect = Assert.IsType<RedirectToActionResult>(checkersResult);
        Assert.Equal("LoginDiscord", checkersRedirect.ActionName);

        // DevoWars
        IActionResult devoResult = controller.DevoWars();
        RedirectToActionResult devoRedirect = Assert.IsType<RedirectToActionResult>(devoResult);
        Assert.Equal("LoginDiscord", devoRedirect.ActionName);

        // Empath
        IActionResult empathResult = controller.Empath();
        RedirectToActionResult empathRedirect = Assert.IsType<RedirectToActionResult>(empathResult);
        Assert.Equal("LoginDiscord", empathRedirect.ActionName);

        // KongorChess
        IActionResult chessResult = controller.KongorChess();
        RedirectToActionResult chessRedirect = Assert.IsType<RedirectToActionResult>(chessResult);
        Assert.Equal("LoginDiscord", chessRedirect.ActionName);

        // Riftbreaker
        IActionResult riftResult = controller.Riftbreaker();
        RedirectToActionResult riftRedirect = Assert.IsType<RedirectToActionResult>(riftResult);
        Assert.Equal("LoginDiscord", riftRedirect.ActionName);
    }

    [Fact]
    public void ArMiniGames_AllViewsExistUnderViewsArMiniGamesDirectory()
    {
        string repoRoot = FindRepoRoot();
        string viewsDir = Path.Combine(repoRoot, "src", "services", "web-ui", "Views", "ArMiniGames");

        Assert.True(File.Exists(Path.Combine(viewsDir, "Index.cshtml")), "Index.cshtml must exist in Views/ArMiniGames");
        Assert.True(File.Exists(Path.Combine(viewsDir, "MonkeyCheckers.cshtml")), "MonkeyCheckers.cshtml must exist in Views/ArMiniGames");
        Assert.True(File.Exists(Path.Combine(viewsDir, "DevoWars.cshtml")), "DevoWars.cshtml must exist in Views/ArMiniGames");
        Assert.True(File.Exists(Path.Combine(viewsDir, "Empath.cshtml")), "Empath.cshtml must exist in Views/ArMiniGames");
        Assert.True(File.Exists(Path.Combine(viewsDir, "KongorChess.cshtml")), "KongorChess.cshtml must exist in Views/ArMiniGames");
        Assert.True(File.Exists(Path.Combine(viewsDir, "Riftbreaker.cshtml")), "Riftbreaker.cshtml must exist in Views/ArMiniGames");
    }

    [Fact]
    public void ArMiniGames_IndexView_MarksRiftbreakerAsGreyedOutFutureDevelopment()
    {
        string repoRoot = FindRepoRoot();
        string indexViewPath = Path.Combine(repoRoot, "src", "services", "web-ui", "Views", "ArMiniGames", "Index.cshtml");
        string content = File.ReadAllText(indexViewPath);

        Assert.Contains("Chrono-Pong: Riftbreaker", content);
        Assert.Contains("data-game-status=\"future-development\"", content);
        Assert.Contains("Future Development", content);
        Assert.Contains("id=\"btnRiftbreakerFutureDev\"", content);
        Assert.Contains("aria-disabled=\"true\"", content);
        Assert.DoesNotContain("/ar-mini-games/riftbreaker", content);
    }

    [Fact]
    public void ArMiniGames_OldViewsRemovedFromViewsToolsDirectory()
    {
        string repoRoot = FindRepoRoot();
        string toolsViewsDir = Path.Combine(repoRoot, "src", "services", "web-ui", "Views", "Tools");

        Assert.False(File.Exists(Path.Combine(toolsViewsDir, "MonkeyCheckers.cshtml")), "Old MonkeyCheckers.cshtml must NOT exist in Views/Tools");
        Assert.False(File.Exists(Path.Combine(toolsViewsDir, "DevoWars.cshtml")), "Old DevoWars.cshtml must NOT exist in Views/Tools");
        Assert.False(File.Exists(Path.Combine(toolsViewsDir, "EmpathAR.cshtml")), "Old EmpathAR.cshtml must NOT exist in Views/Tools");
    }

    [Fact]
    public void ToolsController_LegacyEndpoints_RedirectPermanentlyToArMiniGames()
    {
        IStaffStoreService storeService = new StaffStoreService(
            new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build(),
            NullLogger<StaffStoreService>.Instance);

        ToolsController controller = new(storeService, NullLogger<ToolsController>.Instance);

        // MonkeyCheckers
        var checkersResult = Assert.IsType<RedirectResult>(controller.MonkeyCheckers());
        Assert.True(checkersResult.Permanent);
        Assert.Equal("/ar-mini-games/monkey-checkers", checkersResult.Url);

        // DevoWars
        var devoResult = Assert.IsType<RedirectResult>(controller.DevoWars());
        Assert.True(devoResult.Permanent);
        Assert.Equal("/ar-mini-games/devo-wars", devoResult.Url);

        // EmpathAR
        var empathResult = Assert.IsType<RedirectResult>(controller.EmpathAR());
        Assert.True(empathResult.Permanent);
        Assert.Equal("/ar-mini-games/empath", empathResult.Url);
    }

    [Fact]
    public void Layout_ContainsArMiniGamesTabAndNavigation()
    {
        string repoRoot = FindRepoRoot();
        string layoutPath = Path.Combine(repoRoot, "src", "services", "web-ui", "Views", "Shared", "_Layout.cshtml");
        string content = File.ReadAllText(layoutPath);

        Assert.Contains("isArMiniGamesActive", content);
        Assert.Contains("AR Mini Games", content);
        Assert.Contains("/ar-mini-games", content);
        Assert.Contains("/ar-mini-games/monkey-checkers", content);
        Assert.Contains("/ar-mini-games/devo-wars", content);
        Assert.Contains("/ar-mini-games/chess", content);
        Assert.Contains("Chrono-Pong: Riftbreaker", content);
        Assert.Contains("Future Dev", content);
        Assert.DoesNotContain("/ar-mini-games/riftbreaker", content);
        Assert.DoesNotContain("/ar-mini-games/empath", content);

        // Ensure AR games are removed from Tools/Platform group
        Assert.DoesNotContain("/tools/monkey-checkers", content);
        Assert.DoesNotContain("/tools/empath-ar", content);
    }
}
