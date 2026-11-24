// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Reflection;
using System.Security.Claims;
using DAWNBRINGER.WebPortal.UI.Controllers;
using DAWNBRINGER.WebPortal.UI.Infrastructure;
using FluentAssertions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Moq;
using NEXUS.Contracts.Clients;
using Xunit;

namespace ASPIRE.Tests;

public sealed class DeveloperDocsAuthTests
{
    [Fact]
    public void DeveloperDocs_WhenExecuted_HasAuthorizeAttribute()
    {
        MethodInfo? method = typeof(HomeController).GetMethod(nameof(HomeController.DeveloperDocs));
        method.Should().NotBeNull();

        AuthorizeAttribute? authorizeAttr = method!.GetCustomAttribute<AuthorizeAttribute>();
        authorizeAttr.Should().NotBeNull("DeveloperDocs must be restricted to authenticated staff users via [Authorize]");
        authorizeAttr!.Policy.Should().Be(StaffAuthorizationHelper.PolicyName);
    }

    [Fact]
    public void DeveloperDocs_WhenExecuted_HasExpectedRouteAttributes()
    {
        MethodInfo? method = typeof(HomeController).GetMethod(nameof(HomeController.DeveloperDocs));
        method.Should().NotBeNull();

        IEnumerable<HttpGetAttribute> httpGetAttrs = method!.GetCustomAttributes<HttpGetAttribute>();
        List<string?> routes = httpGetAttrs.Select(a => a.Template).ToList();

        routes.Should().Contain("docs");
        routes.Should().Contain("developer-docs");
    }

    [Fact]
    public void DeveloperDocs_WhenCalled_ByNonStaffUser_ReturnsForbid()
    {
        IConfiguration config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>())
            .Build();

        HomeController controller = new(
            new Mock<IPlayerStatsApiClient>().Object,
            new Mock<IUserApiClient>().Object,
            new Mock<IGuideApiClient>().Object,
            new Mock<IAdminApiClient>().Object,
            config
        );

        DefaultHttpContext httpContext = new();
        ClaimsIdentity nonStaffIdentity = new([new Claim(ClaimTypes.Name, "RegularPlayer")], "Cookie");
        httpContext.User = new ClaimsPrincipal(nonStaffIdentity);
        controller.ControllerContext = new ControllerContext { HttpContext = httpContext };

        IActionResult result = controller.DeveloperDocs();
        result.Should().BeOfType<ForbidResult>();
    }

    [Fact]
    public void DeveloperDocs_WhenCalled_RedirectsToDefaultOrConfiguredDocsUrl()
    {
        IConfiguration configWithDefault = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>())
            .Build();

        Mock<IPlayerStatsApiClient> mockStats = new();
        Mock<IUserApiClient> mockUser = new();
        Mock<IGuideApiClient> mockGuide = new();
        Mock<IAdminApiClient> mockAdmin = new();

        HomeController controller = new(
            mockStats.Object,
            mockUser.Object,
            mockGuide.Object,
            mockAdmin.Object,
            configWithDefault
        );

        DefaultHttpContext httpContext = new();
        ClaimsIdentity staffIdentity = new(
            [
                new Claim(ClaimTypes.Name, "KongorDev"),
                new Claim("is_staff", "true"),
                new Claim(ClaimTypes.Role, "Staff")
            ],
            "Cookie");
        httpContext.User = new ClaimsPrincipal(staffIdentity);
        controller.ControllerContext = new ControllerContext { HttpContext = httpContext };

        IActionResult result = controller.DeveloperDocs();
        RedirectResult redirectResult = result.Should().BeOfType<RedirectResult>().Subject;
        redirectResult.Url.Should().Be("http://localhost:8080");

        // Now test with custom configured URL
        IConfiguration configWithCustom = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                { "DeveloperDocs:Url", "https://docs.projectkongor.com" }
            })
            .Build();

        HomeController controllerCustom = new(
            mockStats.Object,
            mockUser.Object,
            mockGuide.Object,
            mockAdmin.Object,
            configWithCustom
        );
        controllerCustom.ControllerContext = new ControllerContext { HttpContext = httpContext };

        IActionResult customResult = controllerCustom.DeveloperDocs();
        RedirectResult customRedirect = customResult.Should().BeOfType<RedirectResult>().Subject;
        customRedirect.Url.Should().Be("https://docs.projectkongor.com");
    }
}
