// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Text.Json;
using MERRICK.DatabaseContext.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NEXUS.Contracts.CQRS.Telemetry;

namespace ASPIRE.Tests.ZORGATH.WebPortal.API.Tests;

public class AdminMetricsTests
{
    private static readonly DateTimeOffset FixedNow = new(2026, 11, 21, 6, 0, 0, TimeSpan.Zero);

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }

    [Fact]
    public async Task AdminMetrics_GetAdminMetricsAsync_ReturnsValidMetricsPayload_WithUnavailablePopulationWhenEmpty()
    {
        FixedTimeProvider timeProvider = new(FixedNow);
        Mock<IDatabase> mockCache = new();

        mockCache
            .Setup(c => c.HashGetAllAsync(PopulationBreakdownDefaults.TelemetryRedisHashKey, It.IsAny<CommandFlags>()))
            .ReturnsAsync([]);

        using AdminMetricsService metricsService =
            new(NullLogger<AdminMetricsService>.Instance, distributedCache: mockCache.Object, timeProvider: timeProvider);

        AdminMetricsDTO metrics = await metricsService.GetAdminMetricsAsync();

        Assert.NotNull(metrics);
        Assert.Equal(0, metrics.ConcurrentConnectedUsers);
        Assert.Equal(0, metrics.ActiveMatchServers);
        Assert.Equal(0.0, metrics.P95LatencyMs);
        Assert.NotNull(metrics.ActiveGameRegions);
        Assert.NotNull(metrics.PopulationBreakdown);
        Assert.Equal(PopulationTelemetryStatus.Unavailable, metrics.PopulationBreakdown.Status);
        Assert.Equal(0, metrics.ServiceMemoryBytes["KONGOR (Master)"]);
        Assert.True(metrics.Timestamp <= FixedNow.AddSeconds(1));
    }

    [Fact]
    public async Task AdminMetrics_FreshPopulationSnapshot_ParsesLiveAndReconcilesCcu()
    {
        FixedTimeProvider timeProvider = new(FixedNow);
        PopulationBreakdownResponseDto snapshot = new()
        {
            OnlinePlayers = 5,
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
            UpdatedAtUtc = FixedNow.AddSeconds(-15)
        };

        Mock<IDatabase> mockCache = new();
        mockCache
            .Setup(c => c.HashGetAllAsync(PopulationBreakdownDefaults.TelemetryRedisHashKey, It.IsAny<CommandFlags>()))
            .ReturnsAsync(
            [
                new HashEntry("CCU", "42"),
                new HashEntry("ActiveMatchServers", "5"),
                new HashEntry("ActiveRegionsJson", "[\"US East\",\"Europe\"]"),
                new HashEntry(PopulationBreakdownDefaults.PopulationBreakdownRedisField, JsonSerializer.Serialize(snapshot))
            ]);

        using AdminMetricsService metricsService =
            new(NullLogger<AdminMetricsService>.Instance, distributedCache: mockCache.Object, timeProvider: timeProvider);

        AdminMetricsDTO metrics = await metricsService.GetAdminMetricsAsync();

        Assert.NotNull(metrics.PopulationBreakdown);
        Assert.Equal(PopulationTelemetryStatus.Live, metrics.PopulationBreakdown.Status);
        Assert.Equal(42, metrics.PopulationBreakdown.OnlinePlayers);
        Assert.Equal(2, metrics.PopulationBreakdown.GamesInProgressTotal);
        Assert.Single(metrics.PopulationBreakdown.ServersAvailableByRegion);
        Assert.Equal(3, metrics.PopulationBreakdown.ServersAvailableByRegion[0].Count);
    }

    [Fact]
    public async Task AdminMetrics_StalePopulationSnapshot_MarksStaleAndPreservesCounts()
    {
        FixedTimeProvider timeProvider = new(FixedNow);
        PopulationBreakdownResponseDto snapshot = PopulationBreakdownDefaults.CreateEmpty(FixedNow.AddSeconds(-120), 17);

        Mock<IDatabase> mockCache = new();
        mockCache
            .Setup(c => c.HashGetAllAsync(PopulationBreakdownDefaults.TelemetryRedisHashKey, It.IsAny<CommandFlags>()))
            .ReturnsAsync(
            [
                new HashEntry("CCU", "19"),
                new HashEntry(PopulationBreakdownDefaults.PopulationBreakdownRedisField, JsonSerializer.Serialize(snapshot))
            ]);

        using AdminMetricsService metricsService =
            new(NullLogger<AdminMetricsService>.Instance, distributedCache: mockCache.Object, timeProvider: timeProvider);

        AdminMetricsDTO metrics = await metricsService.GetAdminMetricsAsync();

        Assert.NotNull(metrics.PopulationBreakdown);
        Assert.Equal(PopulationTelemetryStatus.Stale, metrics.PopulationBreakdown.Status);
        Assert.Equal(19, metrics.PopulationBreakdown.OnlinePlayers);
    }

    [Fact]
    public async Task AdminMetrics_MalformedOrExpiredPopulationSnapshot_ReturnsUnavailable()
    {
        FixedTimeProvider timeProvider = new(FixedNow);
        Mock<IDatabase> mockCache = new();
        mockCache
            .Setup(c => c.HashGetAllAsync(PopulationBreakdownDefaults.TelemetryRedisHashKey, It.IsAny<CommandFlags>()))
            .ReturnsAsync(
            [
                new HashEntry("CCU", "10"),
                new HashEntry(PopulationBreakdownDefaults.PopulationBreakdownRedisField, "{not-valid-json")
            ]);

        using AdminMetricsService metricsService =
            new(NullLogger<AdminMetricsService>.Instance, distributedCache: mockCache.Object, timeProvider: timeProvider);

        AdminMetricsDTO metrics = await metricsService.GetAdminMetricsAsync();

        Assert.NotNull(metrics.PopulationBreakdown);
        Assert.Equal(PopulationTelemetryStatus.Unavailable, metrics.PopulationBreakdown.Status);
    }
}
