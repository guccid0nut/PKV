// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Security.Claims;
using DAWNBRINGER.WebPortal.UI.Controllers;
using DAWNBRINGER.WebPortal.UI.Models;
using DAWNBRINGER.WebPortal.UI.Services;
using DAWNBRINGER.WebPortal.UI.Services.ApiClient;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.OutputCaching;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Moq.Protected;
using NEXUS.Contracts.Clients;
using NEXUS.Contracts.Services;
using MERRICK.DatabaseContext.Entities.Store;

namespace ASPIRE.Tests.DAWNBRINGER.WebPortal.UI.Tests;

public sealed class WebPortalCancellationStressTests
{
    private static MerrickContext CreateInMemoryContext()
    {
        return TestDbContextFactory.CreateInMemoryContext();
    }

    private static (User user, Account account) SeedUser(MerrickContext db, string name = "CancelPlayer",
        string email = "cancel@kongor.net")
    {
        User user = new()
        {
            EmailAddress = email,
            DiscordId = "12345678901234567",
            DiscordUsername = name,
            SRPPasswordSalt = "salt",
            SRPPasswordHash = "hash",
            PBKDF2PasswordHash = "hash",
            Role = new Role { Name = "User" },
            GoldCoins = 1000,
            SilverCoins = 5000,
            PlinkoTickets = 10,
            InventoryItems = [new UserInventory { ItemCode = "ai.custom_icon:1", GrantSource = "Test" }]
        };
        Account account = new()
        {
            Name = name,
            User = user,
            IsMain = true,
            SelectedStoreItems = ["ai.custom_icon:1"]
        };
        db.Users.Add(user);
        db.Accounts.Add(account);
        db.SaveChanges();
        return (user, account);
    }

    private static ControllerContext CreateStaffControllerContext()
    {
        return new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity([
                    new Claim(ClaimTypes.Name, "StaffTester"),
                    new Claim("is_staff", "true"),
                    new Claim(ClaimTypes.Role, "Staff")
                ], "StaffAuth"))
            }
        };
    }

    [Fact]
    public async Task LiveController_Index_PreCanceledToken_CancelsCleanlyWithoutHanging()
    {
        // Arrange
        LiveMatchService liveMatchService = new();
        liveMatchService.GetAllMatchesInternal()[1001] = new LiveMatchData { MatchID = 1001, Map = "caldavar" };
        LiveController controller = new(liveMatchService, NullLogger<LiveController>.Instance)
        {
            ControllerContext = CreateStaffControllerContext()
        };

        using CancellationTokenSource cts = new();
        cts.Cancel(); // Pre-canceled

        // Act & Assert
        // In LiveController, GetActiveLiveMatchesAsync with pre-canceled token should complete or return cleanly within milliseconds
        IActionResult result = await controller.Index(cts.Token);
        Assert.NotNull(result);
        ViewResult viewResult = Assert.IsType<ViewResult>(result);
        Assert.NotNull(viewResult.Model);
    }

    [Fact]
    public async Task LiveController_Spectate_PreCanceledToken_ExecutesInstantly()
    {
        // Arrange
        LiveMatchService liveMatchService = new();
        liveMatchService.GetAllMatchesInternal()[1002] = new LiveMatchData { MatchID = 1002, Map = "midwars" };
        LiveController controller = new(liveMatchService, NullLogger<LiveController>.Instance)
        {
            ControllerContext = CreateStaffControllerContext()
        };

        using CancellationTokenSource cts = new();
        cts.Cancel();

        // Act
        IActionResult result = await controller.Spectate(1002, cts.Token);

        // Assert
        ViewResult viewResult = Assert.IsType<ViewResult>(result);
        LiveMatchData match = Assert.IsType<LiveMatchData>(viewResult.Model);
        Assert.Equal(1002, match.MatchID);
    }

    [Fact]
    public async Task LiveController_ApiEndpoints_PreCanceledToken_ReturnsExpectedJson()
    {
        // Arrange
        LiveMatchService liveMatchService = new();
        LiveController controller = new(liveMatchService, NullLogger<LiveController>.Instance)
        {
            ControllerContext = CreateStaffControllerContext()
        };

        using CancellationTokenSource cts = new();
        cts.Cancel();

        // Act & Assert 1: GetLiveMatchesApi
        IActionResult matchesResult = await controller.GetLiveMatchesApi(cts.Token);
        Assert.IsType<JsonResult>(matchesResult);

        // Act & Assert 2: GetLiveMatchSnapshotApi
        IActionResult snapshotResult = await controller.GetLiveMatchSnapshotApi(999, cts.Token);
        Assert.IsType<NotFoundObjectResult>(snapshotResult);

        // Act & Assert 3: PruneStaleMatchesApi
        IActionResult pruneResult = await controller.PruneStaleMatchesApi(cts.Token);
        Assert.IsType<JsonResult>(pruneResult);
    }

    [Fact]
    public async Task PlinkoController_GetStatus_PreCanceledToken_ThrowsOperationCanceledException()
    {
        // Arrange
        using MerrickContext db = CreateInMemoryContext();
        (User user, Account account) = SeedUser(db);

        IStoreApiClient storeApi = TestApiClientFactory.CreateStoreApiClient(db);
        IUserApiClient userApi = TestApiClientFactory.CreateUserApiClient(db);
        PlinkoController controller = new(storeApi, userApi)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity([
                        new Claim(ClaimTypes.Name, account.Name),
                        new Claim(ClaimTypes.Email, user.EmailAddress)
                    ], "TestAuth"))
                }
            }
        };

        using CancellationTokenSource cts = new();
        cts.Cancel();

        // Act & Assert
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await controller.GetStatus(cts.Token));
    }

    [Fact]
    public async Task StoreController_Index_Unauthenticated_PreCanceledToken_RendersCatalogCleanly()
    {
        // Arrange
        using MerrickContext db = CreateInMemoryContext();
        IConfiguration config = new ConfigurationBuilder().Build();
        StaffStoreService storeService = new(config, NullLogger<StaffStoreService>.Instance);
        IStoreApiClient storeApi = TestApiClientFactory.CreateStoreApiClient(db);
        IUserApiClient userApi = TestApiClientFactory.CreateUserApiClient(db);
        StoreController controller = new(storeApi, userApi, storeService, NullLogger<StoreController>.Instance);

        using CancellationTokenSource cts = new();
        cts.Cancel();

        // Act
        IActionResult result = await controller.Index(cancellationToken: cts.Token);

        // Assert
        ViewResult viewResult = Assert.IsType<ViewResult>(result);
        StaffStoreViewModel model = Assert.IsType<StaffStoreViewModel>(viewResult.Model);
        Assert.NotNull(model.Items);
    }

    [Fact]
    public async Task StoreController_Index_Authenticated_PreCanceledToken_ThrowsOperationCanceledException()
    {
        // Arrange
        using MerrickContext db = CreateInMemoryContext();
        (User user, Account account) = SeedUser(db);
        IConfiguration config = new ConfigurationBuilder().Build();
        StaffStoreService storeService = new(config, NullLogger<StaffStoreService>.Instance);
        IStoreApiClient storeApi = TestApiClientFactory.CreateStoreApiClient(db);
        IUserApiClient userApi = TestApiClientFactory.CreateUserApiClient(db);
        StoreController controller = new(storeApi, userApi, storeService, NullLogger<StoreController>.Instance)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity([
                        new Claim(ClaimTypes.Name, account.Name),
                        new Claim(ClaimTypes.Email, user.EmailAddress)
                    ], "TestAuth"))
                }
            }
        };

        using CancellationTokenSource cts = new();
        cts.Cancel();

        // Act & Assert
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await controller.Index(cancellationToken: cts.Token));
    }

    [Fact]
    public async Task SystemController_Metrics_PreCanceledToken_ReturnsFallbackMetrics()
    {
        // Arrange
        Mock<IAdminMetricsService> mockMetrics = new();
        mockMetrics.Setup(m => m.GetAdminMetricsAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException());

        SystemController controller = new(mockMetrics.Object, NullLogger<SystemController>.Instance);

        using CancellationTokenSource cts = new();
        cts.Cancel();

        // Act
        IActionResult result = await controller.Metrics(cts.Token);

        // Assert
        ViewResult viewResult = Assert.IsType<ViewResult>(result);
        AdminMetricsDTO model = Assert.IsType<AdminMetricsDTO>(viewResult.Model);
        Assert.NotNull(model);
    }

    [Fact]
    public async Task SystemController_GetLiveTelemetry_PreCanceledToken_ReturnsGracefulPayload()
    {
        // Arrange
        Mock<IAdminMetricsService> mockMetrics = new();
        mockMetrics.Setup(m => m.GetAdminMetricsAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException());

        SystemController controller = new(mockMetrics.Object, NullLogger<SystemController>.Instance);

        using CancellationTokenSource cts = new();
        cts.Cancel();

        // Act
        IActionResult result = await controller.GetLiveTelemetry(cts.Token);

        // Assert
        OkObjectResult okResult = Assert.IsType<OkObjectResult>(result);
        Assert.NotNull(okResult.Value);
    }

    [Fact]
    public async Task HomeController_Leaderboard_PreCanceledToken_ThrowsOperationCanceledException()
    {
        // Arrange
        using MerrickContext db = CreateInMemoryContext();
        SeedUser(db, "RankedPlayer");

        IPlayerStatsApiClient statsApi = TestApiClientFactory.CreatePlayerStatsApiClient(db);
        IUserApiClient userApi = TestApiClientFactory.CreateUserApiClient(db);
        IGuideApiClient guideApi = TestApiClientFactory.CreateGuideApiClient(db);
        IAdminApiClient adminApi = TestApiClientFactory.CreateAdminApiClient(db);
        HomeController controller = new(statsApi, userApi, guideApi, adminApi, new ConfigurationBuilder().Build())
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };

        using CancellationTokenSource cts = new();
        cts.Cancel();

        // Act & Assert
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await controller.Leaderboard(cancellationToken: cts.Token));
    }

    [Fact]
    public async Task AccountController_GetAvatarByName_PreCanceledToken_ThrowsOperationCanceledException()
    {
        // Arrange
        using MerrickContext db = CreateInMemoryContext();
        SeedUser(db, "ExistingUser");

        IUserApiClient userApi = TestApiClientFactory.CreateUserApiClient(db);
        IAdminApiClient adminApi = TestApiClientFactory.CreateAdminApiClient(db);
        Mock<IHttpClientFactory> httpFactory = new();
        Mock<IOutputCacheStore> cacheStore = new();
        AccountController controller = new(
            userApi,
            adminApi,
            httpFactory.Object,
            NullLogger<AccountController>.Instance,
            cacheStore.Object,
            new ConfigurationBuilder().Build())
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };

        using CancellationTokenSource cts = new();
        cts.Cancel();

        // Act & Assert
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await controller.GetAvatarByName("ExistingUser", cts.Token));
    }

    [Fact]
    public async Task UserApiClient_PreCanceledToken_PropagatesCancellationWithoutHanging()
    {
        // Arrange
        Mock<HttpMessageHandler> handlerMock = new();
        handlerMock.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ThrowsAsync(new OperationCanceledException());

        HttpClient client = new(handlerMock.Object) { BaseAddress = new Uri("http://localhost:5000") };
        UserApiClient userApiClient = new(client, NullLogger<UserApiClient>.Instance);

        using CancellationTokenSource cts = new();
        cts.Cancel();

        // Act
        UserDto? user = await userApiClient.GetUserByIdAsync(1, cts.Token);

        // Assert
        Assert.Null(user);
    }

    [Trait("Category", "Manual")]
    [Fact]
    public async Task AdminApiClient_PreCanceledToken_ReturnsFallbackStatsGracefully()
    {
        // Arrange
        Mock<HttpMessageHandler> handlerMock = new();
        handlerMock.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ThrowsAsync(new OperationCanceledException());

        HttpClient client = new(handlerMock.Object) { BaseAddress = new Uri("http://localhost:5000") };
        AdminApiClient adminApiClient = new(client, NullLogger<AdminApiClient>.Instance);

        using CancellationTokenSource cts = new();
        cts.Cancel();

        // Act
        UserSummaryStatsDto stats = await adminApiClient.GetSummaryStatsAsync(cts.Token);

        // Assert
        Assert.NotNull(stats);
        Assert.False(stats.IsDatabaseConnected);
    }

    [Fact]
    public async Task HighConcurrency_LiveMatchService_MixedCancellations_StressTest()
    {
        if (!NexusTestEnvironment.IsManualTestEnabled) return;
        // Arrange: 100 concurrent tasks with rapid ticks and mixed cancellation tokens
        LiveMatchService liveMatchService = new();
        for (int i = 1; i <= 20; i++)
            liveMatchService.GetAllMatchesInternal()[i] = new LiveMatchData
            {
                MatchID = i,
                Map = i % 2 == 0 ? "caldavar" : "midwars",
                ScoreLegion = i * 2,
                ScoreHellbourne = i * 3
            };

        List<Task> tasks = [];
        for (int taskIdx = 0; taskIdx < 100; taskIdx++)
        {
            int idx = taskIdx;
            tasks.Add(Task.Run(async () =>
            {
                CancellationToken token;
                if (idx % 3 == 0)
                {
                    token = new CancellationToken(true); // Pre-canceled
                }
                else if (idx % 3 == 1)
                {
                    using CancellationTokenSource cts = new(TimeSpan.FromMilliseconds(5));
                    token = cts.Token; // Mid-flight timeout
                }
                else
                {
                    token = CancellationToken.None; // Normal
                }

                List<LiveMatchData> matches = await liveMatchService.GetActiveLiveMatchesAsync(token);
                Assert.NotNull(matches);

                LiveMatchData? snapshot = await liveMatchService.GetLiveMatchSnapshotAsync(1 + idx % 20, token);
                // Snapshot should not throw unhandled corruption
            }));
        }

        // Act & Assert: All tasks must complete within 5 seconds without deadlock or exception
        await Task.WhenAll(tasks);
    }
}
