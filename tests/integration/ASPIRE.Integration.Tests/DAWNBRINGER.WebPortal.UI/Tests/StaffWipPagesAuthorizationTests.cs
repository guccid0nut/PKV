// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Security.Claims;
using ASPIRE.Common.DTOs;
using DAWNBRINGER.WebPortal.UI.Controllers;
using DAWNBRINGER.WebPortal.UI.Hubs;
using DAWNBRINGER.WebPortal.UI.Services;
using DAWNBRINGER.WebPortal.UI.Services.ApiClient;
using NEXUS.Contracts.Admin;
using NEXUS.Contracts.Clients;
using NEXUS.Contracts.Domain;
using NEXUS.Contracts.Fleet;
using NEXUS.Contracts.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace ASPIRE.Tests.DAWNBRINGER.WebPortal.UI.Tests;

public sealed class StaffWipPagesAuthorizationTests
{
    private static ClaimsPrincipal CreatePrincipal(bool isStaff, bool hasDiscord = true)
    {
        List<Claim> claims = [];
        if (isStaff)
        {
            claims.Add(new Claim("is_staff", "true"));
            claims.Add(new Claim(ClaimTypes.Role, "Staff"));
            claims.Add(new Claim(ClaimTypes.Name, "StaffMember"));
        }
        else
        {
            claims.Add(new Claim(ClaimTypes.Name, "RegularPlayer"));
        }

        if (hasDiscord)
        {
            claims.Add(new Claim("discord_id", "123456789012345678"));
        }

        ClaimsIdentity identity = new(claims, "TestCookieAuth");
        return new ClaimsPrincipal(identity);
    }

    #region Live Matches Authorization

    [Fact]
    public async Task LiveController_Index_NonStaff_ReturnsForbid()
    {
        // Arrange
        LiveMatchService service = new();
        LiveController controller = new(service, NullLogger<LiveController>.Instance)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { User = CreatePrincipal(isStaff: false) }
            }
        };

        // Act
        IActionResult result = await controller.Index();

        // Assert
        Assert.IsType<ForbidResult>(result);
    }

    [Fact]
    public async Task LiveController_Index_Staff_ReturnsView()
    {
        // Arrange
        LiveMatchService service = new();
        LiveController controller = new(service, NullLogger<LiveController>.Instance)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { User = CreatePrincipal(isStaff: true) }
            }
        };

        // Act
        IActionResult result = await controller.Index();

        // Assert
        Assert.IsType<ViewResult>(result);
    }

    [Fact]
    public async Task LiveController_Spectate_NonStaff_ReturnsForbid()
    {
        // Arrange
        LiveMatchService service = new();
        LiveController controller = new(service, NullLogger<LiveController>.Instance)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { User = CreatePrincipal(isStaff: false) }
            }
        };

        // Act
        IActionResult result = await controller.Spectate(999);

        // Assert
        Assert.IsType<ForbidResult>(result);
    }

    [Fact]
    public async Task LiveController_GetLiveMatchesApi_NonStaff_Returns403()
    {
        // Arrange
        LiveMatchService service = new();
        LiveController controller = new(service, NullLogger<LiveController>.Instance)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { User = CreatePrincipal(isStaff: false) }
            }
        };

        // Act
        IActionResult result = await controller.GetLiveMatchesApi();

        // Assert
        ObjectResult objResult = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status403Forbidden, objResult.StatusCode);
    }

    [Fact]
    public async Task LiveMatchesHub_JoinOverview_NonStaff_DoesNotAddToGroup()
    {
        // Arrange
        Mock<ILiveMatchService> mockService = new();
        Mock<IHubCallerClients<ILiveMatchesClient>> mockClients = new();
        Mock<IGroupManager> mockGroups = new();
        Mock<HubCallerContext> mockContext = new();

        mockContext.Setup(c => c.ConnectionId).Returns("conn-regular-1");
        mockContext.Setup(c => c.User).Returns(CreatePrincipal(isStaff: false));

        LiveMatchesHub hub = new(mockService.Object, NullLogger<LiveMatchesHub>.Instance)
        {
            Context = mockContext.Object,
            Clients = mockClients.Object,
            Groups = mockGroups.Object
        };

        // Act
        await hub.JoinOverview();

        // Assert: Never joined group
        mockGroups.Verify(g => g.AddToGroupAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    #endregion

    #region Replay Studio Authorization

    [Fact]
    public void ToolsController_ReplayStudio_NonStaff_ReturnsForbid()
    {
        // Arrange
        Mock<IStaffStoreService> mockStoreService = new();
        ToolsController controller = new(mockStoreService.Object, NullLogger<ToolsController>.Instance)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { User = CreatePrincipal(isStaff: false) }
            }
        };

        // Act
        IActionResult result = controller.ReplayStudio();

        // Assert
        Assert.IsType<ForbidResult>(result);
    }

    [Fact]
    public void ToolsController_ReplayStudio_Staff_ReturnsView()
    {
        // Arrange
        Mock<IStaffStoreService> mockStoreService = new();
        ToolsController controller = new(mockStoreService.Object, NullLogger<ToolsController>.Instance)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { User = CreatePrincipal(isStaff: true, hasDiscord: true) }
            }
        };

        // Act
        IActionResult result = controller.ReplayStudio();

        // Assert
        ViewResult viewResult = Assert.IsType<ViewResult>(result);
        Assert.Equal("ReplayStudio", viewResult.ViewName);
    }

    [Fact]
    public void ToolsController_EnqueueRenderJob_NonStaff_Returns403()
    {
        // Arrange
        Mock<IStaffStoreService> mockStoreService = new();
        ToolsController controller = new(mockStoreService.Object, NullLogger<ToolsController>.Instance)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { User = CreatePrincipal(isStaff: false) }
            }
        };

        // Act
        IActionResult result = controller.EnqueueRenderJob(new ReplayRenderJobRequestDto { MatchId = 12345 });

        // Assert
        ObjectResult objResult = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status403Forbidden, objResult.StatusCode);
    }

    [Fact]
    public void ToolsController_GetQueueStatus_NonStaff_Returns403()
    {
        // Arrange
        Mock<IStaffStoreService> mockStoreService = new();
        ToolsController controller = new(mockStoreService.Object, NullLogger<ToolsController>.Instance)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { User = CreatePrincipal(isStaff: false) }
            }
        };

        // Act
        IActionResult result = controller.GetQueueStatus();

        // Assert
        ObjectResult objResult = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status403Forbidden, objResult.StatusCode);
    }

    #endregion

    #region Newerth Lore Authorization

    [Fact]
    public async Task LoreController_Index_NonStaff_ReturnsForbid()
    {
        // Arrange
        Mock<ILoreApiClient> mockLore = new();
        Mock<IUserApiClient> mockUser = new();

        LoreController controller = new(mockLore.Object, mockUser.Object, NullLogger<LoreController>.Instance)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { User = CreatePrincipal(isStaff: false, hasDiscord: true) }
            }
        };

        // Act
        IActionResult result = await controller.Index();

        // Assert
        Assert.IsType<ForbidResult>(result);
    }

    [Fact]
    public async Task LoreController_Index_Staff_ReturnsView()
    {
        // Arrange
        Mock<ILoreApiClient> mockLore = new();
        mockLore.Setup(l => l.GetArcsAsync(true, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<LoreArcDto>());
        mockLore.Setup(l => l.GetComicsAsync(null, true, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<LoreComicSummaryDto>());

        Mock<IUserApiClient> mockUser = new();

        LoreController controller = new(mockLore.Object, mockUser.Object, NullLogger<LoreController>.Instance)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { User = CreatePrincipal(isStaff: true, hasDiscord: true) }
            }
        };

        // Act
        IActionResult result = await controller.Index();

        // Assert
        Assert.IsType<ViewResult>(result);
    }

    [Fact]
    public async Task LoreController_Reader_NonStaff_ReturnsForbid()
    {
        // Arrange
        Mock<ILoreApiClient> mockLore = new();
        Mock<IUserApiClient> mockUser = new();

        LoreController controller = new(mockLore.Object, mockUser.Object, NullLogger<LoreController>.Instance)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { User = CreatePrincipal(isStaff: false, hasDiscord: true) }
            }
        };

        // Act
        IActionResult result = await controller.Reader("chapter-1");

        // Assert
        Assert.IsType<ForbidResult>(result);
    }

    #endregion

    #region Global Search Privacy

    [Fact]
    public async Task GlobalSearch_NonStaff_FiltersOutWipPages()
    {
        // Arrange
        Mock<IPlayerStatsApiClient> mockPlayerStats = new();
        Mock<IUserApiClient> mockUser = new();
        Mock<IGuideApiClient> mockGuide = new();
        Mock<IAdminApiClient> mockAdmin = new();
        IConfiguration config = new ConfigurationBuilder().Build();

        HomeController controller = new(
            mockPlayerStats.Object,
            mockUser.Object,
            mockGuide.Object,
            mockAdmin.Object,
            config,
            logger: NullLogger<HomeController>.Instance)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { User = CreatePrincipal(isStaff: false) }
            }
        };

        // Act: Search for "lore"
        IActionResult resultLore = await controller.SearchGlobal("lore");
        JsonResult jsonLore = Assert.IsType<JsonResult>(resultLore);
        var itemsLore = Assert.IsAssignableFrom<System.Collections.IEnumerable>(jsonLore.Value);
        Assert.Empty(itemsLore);

        // Act: Search for "replay" (Replay Studio is deferred future work and omitted from global search)
        IActionResult resultReplay = await controller.SearchGlobal("replay");
        JsonResult jsonReplay = Assert.IsType<JsonResult>(resultReplay);
        var itemsReplay = Assert.IsAssignableFrom<System.Collections.IEnumerable>(jsonReplay.Value);
        Assert.Empty(itemsReplay);

        // Act: Search for "inventory" as Non-Staff
        IActionResult resultInv = await controller.SearchGlobal("inventory");
        JsonResult jsonInv = Assert.IsType<JsonResult>(resultInv);
        var itemsInv = Assert.IsAssignableFrom<System.Collections.IEnumerable>(jsonInv.Value);
        Assert.Empty(itemsInv);

        // Act: Search for "live"
        IActionResult resultLive = await controller.SearchGlobal("live");
        JsonResult jsonLive = Assert.IsType<JsonResult>(resultLive);
        var itemsLive = Assert.IsAssignableFrom<System.Collections.IEnumerable>(jsonLive.Value);
        Assert.Empty(itemsLive);
    }

    [Fact]
    public async Task GlobalSearch_Staff_IncludesWipPages()
    {
        // Arrange
        Mock<IPlayerStatsApiClient> mockPlayerStats = new();
        Mock<IUserApiClient> mockUser = new();
        Mock<IGuideApiClient> mockGuide = new();
        Mock<IAdminApiClient> mockAdmin = new();
        IConfiguration config = new ConfigurationBuilder().Build();

        HomeController controller = new(
            mockPlayerStats.Object,
            mockUser.Object,
            mockGuide.Object,
            mockAdmin.Object,
            config,
            logger: NullLogger<HomeController>.Instance)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { User = CreatePrincipal(isStaff: true) }
            }
        };

        // Act: Search for "lore" as Staff
        IActionResult resultLore = await controller.SearchGlobal("lore");
        JsonResult jsonLore = Assert.IsType<JsonResult>(resultLore);
        var itemsLore = Assert.IsAssignableFrom<System.Collections.IEnumerable>(jsonLore.Value);
        Assert.NotEmpty(itemsLore);

        // Act: Search for "replay" as Staff (deferred future work -> omitted from active search navigation)
        IActionResult resultReplay = await controller.SearchGlobal("replay");
        JsonResult jsonReplay = Assert.IsType<JsonResult>(resultReplay);
        var itemsReplay = Assert.IsAssignableFrom<System.Collections.IEnumerable>(jsonReplay.Value);
        Assert.Empty(itemsReplay);

        // Act: Search for "inventory" as Staff
        IActionResult resultInv = await controller.SearchGlobal("inventory");
        JsonResult jsonInv = Assert.IsType<JsonResult>(resultInv);
        var itemsInv = Assert.IsAssignableFrom<System.Collections.IEnumerable>(jsonInv.Value);
        Assert.NotEmpty(itemsInv);

        // Act: Search for "live" as Staff
        IActionResult resultLive = await controller.SearchGlobal("live");
        JsonResult jsonLive = Assert.IsType<JsonResult>(resultLive);
        var itemsLive = Assert.IsAssignableFrom<System.Collections.IEnumerable>(jsonLive.Value);
        Assert.NotEmpty(itemsLive);
    }

    #endregion
}
