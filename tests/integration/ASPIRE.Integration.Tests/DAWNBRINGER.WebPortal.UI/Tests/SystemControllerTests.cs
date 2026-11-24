// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Reflection;
using System.Text.Json;
using DAWNBRINGER.WebPortal.UI.Controllers;
using DAWNBRINGER.WebPortal.UI.Hubs;
using DAWNBRINGER.WebPortal.UI.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NEXUS.Contracts.CQRS.Telemetry;
using NEXUS.Contracts.Services;
using StackExchange.Redis;

namespace ASPIRE.Tests.DAWNBRINGER.WebPortal.UI.Tests;

public class SystemControllerTests
{
    private static readonly DateTimeOffset FixedNow = new(2026, 11, 21, 6, 0, 0, TimeSpan.Zero);

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }

    [Fact]
    public async Task Metrics_ReturnsViewResult_WithAdminMetricsDTOModelAndPopulationBreakdown()
    {
        PopulationBreakdownResponseDto breakdown = new()
        {
            Status = PopulationTelemetryStatus.Live,
            OnlinePlayers = 10,
            GamesInProgressTotal = 2,
            GamesInProgressByRegion =
            [
                new RegionCountDto { Region = "All locations", Count = 2 },
                new RegionCountDto { Region = "US East", Count = 2 }
            ],
            ServersAvailableByRegion =
            [
                new RegionCountDto { Region = "US East", Count = 3 }
            ],
            TmmQueues = PopulationBreakdownDefaults.CreateDefaultTmmQueues(),
            UpdatedAtUtc = FixedNow
        };

        AdminMetricsDTO expectedMetrics = new(
            10,
            2,
            15.0,
            4,
            ["US East", "EU West"],
            0,
            100,
            50,
            12.5,
            [],
            FixedNow,
            breakdown
        );

        Mock<IAdminMetricsService> mockMetricsService = new();
        mockMetricsService
            .Setup(s => s.GetAdminMetricsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(expectedMetrics);

        SystemController controller = new(
            mockMetricsService.Object,
            NullLogger<SystemController>.Instance
        );

        IActionResult result = await controller.Metrics();

        ViewResult viewResult = Assert.IsType<ViewResult>(result);
        AdminMetricsDTO model = Assert.IsType<AdminMetricsDTO>(viewResult.Model);

        Assert.Equal(10, model.ConcurrentConnectedUsers);
        Assert.Equal(4, model.ActiveMatchServers);
        Assert.Equal(2, model.ActiveGameRegions.Count);
        Assert.NotNull(model.PopulationBreakdown);
        Assert.Equal(PopulationTelemetryStatus.Live, model.PopulationBreakdown.Status);
        Assert.Equal(2, model.PopulationBreakdown.GamesInProgressTotal);
    }

    [Fact]
    public async Task Metrics_OnServiceException_ReturnsHonestZeroesAndUnavailablePopulation()
    {
        Mock<IAdminMetricsService> mockMetricsService = new();
        mockMetricsService
            .Setup(s => s.GetAdminMetricsAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("ZORGATH unreachable"));

        SystemController controller = new(
            mockMetricsService.Object,
            NullLogger<SystemController>.Instance
        );

        IActionResult result = await controller.Metrics();

        ViewResult viewResult = Assert.IsType<ViewResult>(result);
        AdminMetricsDTO model = Assert.IsType<AdminMetricsDTO>(viewResult.Model);

        Assert.Equal(0, model.ConcurrentConnectedUsers);
        Assert.Equal(0, model.ActiveMatchServers);
        Assert.Equal(0.0, model.P95LatencyMs);
        Assert.NotNull(model.PopulationBreakdown);
        Assert.Equal(PopulationTelemetryStatus.Unavailable, model.PopulationBreakdown.Status);
    }

    [Fact]
    public async Task GetPublicPopulation_SerializesStrictlyAllowlistedKeys_AndSetsCacheControl()
    {
        PublicPopulationTelemetryDto dto = new()
        {
            Status = PopulationTelemetryStatus.Live,
            OnlinePlayers = 27,
            GamesInProgressTotal = 3,
            GamesInProgressByRegion =
            [
                new RegionCountDto { Region = "All locations", Count = 3 },
                new RegionCountDto { Region = "US East", Count = 3 }
            ],
            ServersAvailableByRegion =
            [
                new RegionCountDto { Region = "US East", Count = 4 }
            ],
            TmmQueues = PopulationBreakdownDefaults.CreateDefaultTmmQueues(),
            UpdatedAtUtc = FixedNow
        };

        Mock<IAdminMetricsService> adminMock = new();
        Mock<IPublicPopulationTelemetryService> pubMock = new();
        pubMock.Setup(p => p.GetPublicPopulationAsync(It.IsAny<CancellationToken>())).ReturnsAsync(dto);

        SystemController controller = new(
            adminMock.Object,
            pubMock.Object,
            developerIssueAdminService: null,
            logger: NullLogger<SystemController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };

        IActionResult result = await controller.GetPublicPopulation();

        OkObjectResult ok = Assert.IsType<OkObjectResult>(result);
        PublicPopulationTelemetryDto payload = Assert.IsType<PublicPopulationTelemetryDto>(ok.Value);
        Assert.Equal("public, max-age=5, stale-while-revalidate=10", controller.Response.Headers.CacheControl.ToString());

        string json = JsonSerializer.Serialize(payload);
        using JsonDocument doc = JsonDocument.Parse(json);
        HashSet<string> actualKeys = doc.RootElement.EnumerateObject().Select(p => p.Name).ToHashSet(StringComparer.Ordinal);
        HashSet<string> expectedKeys =
        [
            "status",
            "onlinePlayers",
            "gamesInProgressTotal",
            "gamesInProgressByRegion",
            "serversAvailableByRegion",
            "tmmQueues",
            "updatedAtUtc"
        ];

        Assert.Equal(expectedKeys, actualKeys);
        adminMock.Verify(a => a.GetAdminMetricsAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public void AuthorizationAndRateLimitBoundaries_ProtectAdminRoutesAndAllowPublicPopulation()
    {
        MethodInfo? metricsMethod = typeof(SystemController).GetMethod(nameof(SystemController.Metrics));
        MethodInfo? publicPopMethod = typeof(SystemController).GetMethod(nameof(SystemController.GetPublicPopulation));

        Assert.NotNull(metricsMethod);
        Assert.NotNull(publicPopMethod);

        Assert.Null(metricsMethod.GetCustomAttribute<AllowAnonymousAttribute>());
        Assert.NotNull(typeof(TelemetryHub).GetCustomAttribute<AuthorizeAttribute>());

        Assert.NotNull(publicPopMethod.GetCustomAttribute<AllowAnonymousAttribute>());
        EnableRateLimitingAttribute? rateLimitAttr = publicPopMethod.GetCustomAttribute<EnableRateLimitingAttribute>();
        Assert.NotNull(rateLimitAttr);
        Assert.Equal(PublicPopulationTelemetryService.RateLimitPolicyName, rateLimitAttr.PolicyName);
    }

    [Fact]
    public async Task PublicPopulationTelemetryService_ReadsDirectlyFromValkeyWithFiveSecondCache()
    {
        FixedTimeProvider timeProvider = new(FixedNow);
        PopulationBreakdownResponseDto snapshot = new()
        {
            OnlinePlayers = 11,
            GamesInProgressTotal = 1,
            GamesInProgressByRegion =
            [
                new RegionCountDto { Region = "All locations", Count = 1 },
                new RegionCountDto { Region = "Europe", Count = 1 }
            ],
            ServersAvailableByRegion =
            [
                new RegionCountDto { Region = "US East", Count = 2 }
            ],
            TmmQueues = PopulationBreakdownDefaults.CreateDefaultTmmQueues(),
            UpdatedAtUtc = FixedNow.AddSeconds(-8)
        };

        Mock<IDatabase> dbMock = new();
        dbMock
            .Setup(d => d.HashGetAsync(
                PopulationBreakdownDefaults.TelemetryRedisHashKey,
                It.IsAny<RedisValue[]>(),
                CommandFlags.None))
            .ReturnsAsync(
            [
                new RedisValue("15"),
                new RedisValue(JsonSerializer.Serialize(snapshot))
            ]);

        Mock<IConnectionMultiplexer> muxMock = new();
        muxMock.Setup(m => m.GetDatabase(It.IsAny<int>(), It.IsAny<object>())).Returns(dbMock.Object);

        PublicPopulationTelemetryService service = new(
            NullLogger<PublicPopulationTelemetryService>.Instance,
            hybridCache: null,
            multiplexer: muxMock.Object,
            scopeFactory: null,
            timeProvider: timeProvider);

        PublicPopulationTelemetryDto first = await service.GetPublicPopulationAsync();
        PublicPopulationTelemetryDto second = await service.GetPublicPopulationAsync();

        Assert.Equal(PopulationTelemetryStatus.Live, first.Status);
        Assert.Equal(15, first.OnlinePlayers);
        Assert.Equal(1, first.GamesInProgressTotal);
        Assert.Same(first, second);
        dbMock.Verify(
            d => d.HashGetAsync(PopulationBreakdownDefaults.TelemetryRedisHashKey, It.IsAny<RedisValue[]>(), CommandFlags.None),
            Times.Once);
    }
}
