// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// TAO (Together As One) MCP & Kongor CLI -- Created by guccidonut (https://github.com/guccidonut/PKV)

using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using DAWNBRINGER.WebPortal.UI.Controllers;
using DAWNBRINGER.WebPortal.UI.Hubs;
using DAWNBRINGER.WebPortal.UI.Infrastructure;
using DAWNBRINGER.WebPortal.UI.Models.DeveloperIssues;
using DAWNBRINGER.WebPortal.UI.Services.DeveloperIssues;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NEXUS.Contracts.Services;
using Moq;
using Xunit;

namespace Kongor.Mcp.Tests;

public sealed class DeveloperIssuesStaffAuthorizationTests : IDisposable
{
    private readonly string _testTempDir;
    private readonly Mock<IHostEnvironment> _mockEnv;
    private readonly Mock<ILogger<DeveloperIssueAdminService>> _mockServiceLogger;
    private readonly Mock<ILogger<DeveloperIssuesHub>> _mockHubLogger;
    private readonly Mock<ILogger<SystemController>> _mockControllerLogger;
    private readonly Mock<IHubContext<DeveloperIssuesHub, IDeveloperIssuesClient>> _mockHubContext;
    private readonly Mock<IHubClients<IDeveloperIssuesClient>> _mockHubClients;
    private readonly Mock<IDeveloperIssuesClient> _mockClientProxy;
    private readonly DeveloperIssueAdminService _adminService;

    public DeveloperIssuesStaffAuthorizationTests()
    {
        _testTempDir = Path.Combine(Path.GetTempPath(), $"kongor_staff_auth_tests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testTempDir);

        _mockEnv = new Mock<IHostEnvironment>();
        _mockEnv.Setup(e => e.ContentRootPath).Returns(_testTempDir);

        _mockServiceLogger = new Mock<ILogger<DeveloperIssueAdminService>>();
        _mockHubLogger = new Mock<ILogger<DeveloperIssuesHub>>();
        _mockControllerLogger = new Mock<ILogger<SystemController>>();

        _mockHubContext = new Mock<IHubContext<DeveloperIssuesHub, IDeveloperIssuesClient>>();
        _mockHubClients = new Mock<IHubClients<IDeveloperIssuesClient>>();
        _mockClientProxy = new Mock<IDeveloperIssuesClient>();

        _mockHubClients.Setup(c => c.Group(DeveloperIssuesHub.FeedGroupName)).Returns(_mockClientProxy.Object);
        _mockHubContext.Setup(h => h.Clients).Returns(_mockHubClients.Object);

        string ndjson = Path.Combine(_testTempDir, $"issues_{Guid.NewGuid():N}.ndjson");
        _adminService = new DeveloperIssueAdminService(
            scopeFactory: null,
            hubContext: _mockHubContext.Object,
            env: _mockEnv.Object,
            logger: _mockServiceLogger.Object,
            customNdjsonFilePath: ndjson);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_testTempDir))
            {
                Directory.Delete(_testTempDir, recursive: true);
            }
        }
        catch { }
    }

    private static ClaimsPrincipal CreateUser(bool authenticated, string? role = null, bool isStaffClaim = false)
    {
        if (!authenticated)
        {
            return new ClaimsPrincipal(new ClaimsIdentity());
        }

        var claims = new List<Claim>
        {
            new(ClaimTypes.Name, "TestPlayer"),
            new(ClaimTypes.NameIdentifier, "player-12345")
        };

        if (isStaffClaim)
        {
            claims.Add(new Claim("is_staff", "true"));
        }

        if (!string.IsNullOrEmpty(role))
        {
            claims.Add(new Claim(ClaimTypes.Role, role));
        }

        return new ClaimsPrincipal(new ClaimsIdentity(claims, "TestAuth"));
    }

    [Fact]
    public void StaffAuthorizationHelper_UnauthenticatedUser_IsNeverStaff()
    {
        var anonymousUser = CreateUser(authenticated: false);
        Assert.False(StaffAuthorizationHelper.IsStaff(anonymousUser));
        Assert.False(StaffAuthorizationHelper.IsStaff(null));
    }

    [Fact]
    public void StaffAuthorizationHelper_StandardPlayer_IsNeverStaff()
    {
        var regularUser = CreateUser(authenticated: true, role: "User", isStaffClaim: false);
        Assert.False(StaffAuthorizationHelper.IsStaff(regularUser));
    }

    [Theory]
    [InlineData("Staff")]
    [InlineData("staff")]
    [InlineData("Admin")]
    [InlineData("Administrator")]
    [InlineData("GameMaster")]
    [InlineData("MatchModerator")]
    public void StaffAuthorizationHelper_StaffRoles_AreAuthorized(string role)
    {
        var staffUser = CreateUser(authenticated: true, role: role, isStaffClaim: false);
        Assert.True(StaffAuthorizationHelper.IsStaff(staffUser));
    }

    [Fact]
    public void StaffAuthorizationHelper_IsStaffClaim_IsAuthorized()
    {
        var staffClaimUser = CreateUser(authenticated: true, role: null, isStaffClaim: true);
        Assert.True(StaffAuthorizationHelper.IsStaff(staffClaimUser));
    }

    [Fact]
    public async Task SystemController_Issues_UnauthenticatedOrNonStaff_ReturnsForbid()
    {
        var controller = new SystemController(
            metricsService: Mock.Of<IAdminMetricsService>(),
            developerIssueAdminService: _adminService,
            logger: _mockControllerLogger.Object);

        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = CreateUser(authenticated: true, role: "User", isStaffClaim: false)
            }
        };

        var result = await controller.Issues();
        Assert.IsType<ForbidResult>(result);
    }

    [Fact]
    public async Task SystemController_Issues_StaffUser_ReturnsViewResult()
    {
        var controller = new SystemController(
            metricsService: Mock.Of<IAdminMetricsService>(),
            developerIssueAdminService: _adminService,
            logger: _mockControllerLogger.Object);

        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = CreateUser(authenticated: true, isStaffClaim: true)
            }
        };

        var result = await controller.Issues();
        var viewResult = Assert.IsType<ViewResult>(result);
        Assert.IsType<DeveloperIssuesDashboardViewModel>(viewResult.Model);
    }

    [Fact]
    public async Task SystemController_GetIssuesApi_NonStaff_Returns403Forbidden()
    {
        var controller = new SystemController(
            metricsService: Mock.Of<IAdminMetricsService>(),
            developerIssueAdminService: _adminService,
            logger: _mockControllerLogger.Object);

        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = CreateUser(authenticated: true, role: "Guest", isStaffClaim: false)
            }
        };

        var result = await controller.GetIssuesApi();
        var statusResult = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status403Forbidden, statusResult.StatusCode);
    }

    [Fact]
    public async Task SystemController_TriggerReflexHeal_NonStaff_Returns403Forbidden()
    {
        var controller = new SystemController(
            metricsService: Mock.Of<IAdminMetricsService>(),
            developerIssueAdminService: _adminService,
            logger: _mockControllerLogger.Object);

        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = CreateUser(authenticated: false)
            }
        };

        var result = await controller.TriggerReflexHeal("fake-fingerprint");
        var statusResult = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status403Forbidden, statusResult.StatusCode);
    }

    [Fact]
    public async Task SystemController_TriggerReflexHeal_Staff_Succeeds()
    {
        var controller = new SystemController(
            metricsService: Mock.Of<IAdminMetricsService>(),
            developerIssueAdminService: _adminService,
            logger: _mockControllerLogger.Object);

        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = CreateUser(authenticated: true, role: "Administrator")
            }
        };

        // Seed an issue
        string fp = "staff-test-fp-101";
        await _adminService.IngestOrUpdateIssueAsync(new DeveloperIssueItemDto
        {
            Id = "staff-test-id",
            Fingerprint = fp,
            Subsystem = "AssetPipeline",
            Severity = "Error",
            Status = "ActionRequired",
            Title = "Missing Texture pyro_diff.tga",
            RawMessage = "AssetBake!BitmapLoad:pyro_diff.tga",
            NormalizedSignature = "AssetBake!BitmapLoad:pyro_diff.tga"
        });

        var result = await controller.TriggerReflexHeal(fp);
        var okResult = Assert.IsType<OkObjectResult>(result);
        var reflexResult = Assert.IsType<ReflexTriggerResultDto>(okResult.Value);
        Assert.True(reflexResult.Success);
        Assert.Equal("MissingTextureRecipe", reflexResult.RecipeName);
    }

    [Fact]
    public async Task DeveloperIssuesHub_NonStaff_TriggerReflexHeal_IsRejected()
    {
        var hub = new DeveloperIssuesHub(_adminService, _mockHubLogger.Object);
        var mockCallerContext = new Mock<HubCallerContext>();
        mockCallerContext.Setup(c => c.ConnectionId).Returns("conn-unauthorized-001");
        mockCallerContext.Setup(c => c.User).Returns(CreateUser(authenticated: true, role: "User"));
        hub.Context = mockCallerContext.Object;

        var result = await hub.TriggerReflexHeal("unauth-fp");

        Assert.False(result.Success);
        Assert.Equal("StaffAuthorizationDenied", result.RecipeName);
        Assert.Contains("restricted to staff members", result.Receipt);
    }

    [Fact]
    public async Task DeveloperIssuesHub_Staff_TriggerReflexHeal_ExecutesSuccessfully()
    {
        var hub = new DeveloperIssuesHub(_adminService, _mockHubLogger.Object);
        var mockCallerContext = new Mock<HubCallerContext>();
        mockCallerContext.Setup(c => c.ConnectionId).Returns("conn-staff-001");
        mockCallerContext.Setup(c => c.User).Returns(CreateUser(authenticated: true, role: "Staff"));
        hub.Context = mockCallerContext.Object;

        string fp = "staff-hub-fp-202";
        await _adminService.IngestOrUpdateIssueAsync(new DeveloperIssueItemDto
        {
            Id = "staff-hub-id",
            Fingerprint = fp,
            Subsystem = "AssetPipeline",
            Severity = "Error",
            Status = "ActionRequired",
            Title = "Missing Texture hero_icon.tga",
            RawMessage = "AssetBake!BitmapLoad:hero_icon.tga",
            NormalizedSignature = "AssetBake!BitmapLoad:hero_icon.tga"
        });

        var result = await hub.TriggerReflexHeal(fp);

        Assert.True(result.Success);
        Assert.Equal("MissingTextureRecipe", result.RecipeName);
    }
}
