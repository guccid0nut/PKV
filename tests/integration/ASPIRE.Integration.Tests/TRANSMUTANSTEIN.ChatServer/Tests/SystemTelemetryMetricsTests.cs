// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using MERRICK.DatabaseContext.Services;
using Microsoft.Extensions.Logging.Abstractions;
using TRANSMUTANSTEIN.ChatServer.Services;

namespace ASPIRE.Tests.TRANSMUTANSTEIN.ChatServer.Tests;

public class SystemTelemetryMetricsTests
{
    [Fact]
    public async Task TelemetryPublisher_ResetBaseline_SetsCountsToZero()
    {
        DbContextOptions<MerrickContext> options = TestDbContextFactory.CreateOptions();

        ServiceCollection services = [];
        services.AddScoped(_ => new MerrickContext(options));
        ServiceProvider serviceProvider = services.BuildServiceProvider();

        await using TelemetryPublisherService publisher = new(
            serviceProvider,
            NullLogger<TelemetryPublisherService>.Instance,
            multiplexer: null,
            timeProvider: null,
            databaseSyncThrottle: TimeSpan.Zero,
            commandFlushInterval: TimeSpan.Zero);

        await publisher.ResetBaselineOnStartupAsync();

        using IServiceScope scope = serviceProvider.CreateScope();
        MerrickContext context = scope.ServiceProvider.GetRequiredService<MerrickContext>();
        SystemTelemetryMetrics? metrics = await context.SystemTelemetryMetrics.FirstOrDefaultAsync(m => m.ID == 1);

        Assert.NotNull(metrics);
        Assert.Equal(0, metrics.ConcurrentConnectedUsers);
        Assert.Equal(0, metrics.ActiveMatchServers);
        Assert.Equal(0, metrics.ActiveMatchmakingQueues);
        Assert.Equal(0.0, metrics.AverageWaitTimeSeconds);
    }

    [Fact]
    public async Task TelemetryPublisher_OnClientConnectedAndDisconnected_UpdatesCCUCorrectly()
    {
        DbContextOptions<MerrickContext> options = TestDbContextFactory.CreateOptions();

        ServiceCollection services = [];
        services.AddScoped(_ => new MerrickContext(options));
        ServiceProvider serviceProvider = services.BuildServiceProvider();

        await using TelemetryPublisherService publisher = new(
            serviceProvider,
            NullLogger<TelemetryPublisherService>.Instance,
            multiplexer: null,
            timeProvider: null,
            databaseSyncThrottle: TimeSpan.Zero,
            commandFlushInterval: TimeSpan.Zero);

        await publisher.ResetBaselineOnStartupAsync();

        await publisher.OnClientConnectedAsync("Player1");
        await publisher.OnClientConnectedAsync("Player2");

        using (IServiceScope scope = serviceProvider.CreateScope())
        {
            MerrickContext context = scope.ServiceProvider.GetRequiredService<MerrickContext>();
            SystemTelemetryMetrics? metrics = await context.SystemTelemetryMetrics.FirstOrDefaultAsync(m => m.ID == 1);
            Assert.NotNull(metrics);
            Assert.Equal(2, metrics.ConcurrentConnectedUsers);
        }

        await publisher.OnClientDisconnectedAsync("Player1");

        using (IServiceScope scope = serviceProvider.CreateScope())
        {
            MerrickContext context = scope.ServiceProvider.GetRequiredService<MerrickContext>();
            SystemTelemetryMetrics? metrics = await context.SystemTelemetryMetrics.FirstOrDefaultAsync(m => m.ID == 1);
            Assert.NotNull(metrics);
            Assert.Equal(1, metrics.ConcurrentConnectedUsers);
        }
    }

    [Fact]
    public async Task TelemetryPublisher_OnMatchServerConnectedAndDisconnected_UpdatesServerCount()
    {
        DbContextOptions<MerrickContext> options = TestDbContextFactory.CreateOptions();

        ServiceCollection services = [];
        services.AddScoped(_ => new MerrickContext(options));
        ServiceProvider serviceProvider = services.BuildServiceProvider();

        await using TelemetryPublisherService publisher = new(
            serviceProvider,
            NullLogger<TelemetryPublisherService>.Instance,
            multiplexer: null,
            timeProvider: null,
            databaseSyncThrottle: TimeSpan.Zero,
            commandFlushInterval: TimeSpan.Zero);

        await publisher.ResetBaselineOnStartupAsync();

        await publisher.OnMatchServerConnectedAsync(1, "US East");

        using (IServiceScope scope = serviceProvider.CreateScope())
        {
            MerrickContext context = scope.ServiceProvider.GetRequiredService<MerrickContext>();
            SystemTelemetryMetrics? metrics = await context.SystemTelemetryMetrics.FirstOrDefaultAsync(m => m.ID == 1);
            Assert.NotNull(metrics);
            Assert.Equal(1, metrics.ActiveMatchServers);
        }

        await publisher.OnMatchServerDisconnectedAsync(1, "US East");

        using (IServiceScope scope = serviceProvider.CreateScope())
        {
            MerrickContext context = scope.ServiceProvider.GetRequiredService<MerrickContext>();
            SystemTelemetryMetrics? metrics = await context.SystemTelemetryMetrics.FirstOrDefaultAsync(m => m.ID == 1);
            Assert.NotNull(metrics);
            Assert.Equal(0, metrics.ActiveMatchServers);
        }
    }

    [Fact]
    public async Task TelemetryPublisher_RecordMatchFoundWaitTime_SavesEntryAndCalculatesAverage()
    {
        DbContextOptions<MerrickContext> options = TestDbContextFactory.CreateOptions();

        ServiceCollection services = [];
        services.AddScoped(_ => new MerrickContext(options));
        ServiceProvider serviceProvider = services.BuildServiceProvider();

        await using TelemetryPublisherService publisher = new(
            serviceProvider,
            NullLogger<TelemetryPublisherService>.Instance,
            multiplexer: null,
            timeProvider: null,
            databaseSyncThrottle: TimeSpan.Zero,
            commandFlushInterval: TimeSpan.Zero);

        await publisher.RecordMatchFoundWaitTimeAsync("5v5_Ranked", 10, 15.0);
        await publisher.RecordMatchFoundWaitTimeAsync("5v5_Ranked", 10, 25.0);

        using IServiceScope scope = serviceProvider.CreateScope();
        MerrickContext context = scope.ServiceProvider.GetRequiredService<MerrickContext>();
        SystemTelemetryMetrics? metrics = await context.SystemTelemetryMetrics.FirstOrDefaultAsync(m => m.ID == 1);

        Assert.NotNull(metrics);
        Assert.Equal(20.0, metrics.AverageWaitTimeSeconds);
        Assert.Equal(2, await context.MatchmakingQueueWaitEntries.CountAsync());
    }

    [Fact]
    public async Task AdminMetricsService_ReadsFromDatabaseContext_WhenRedisIsCold()
    {
        DbContextOptions<MerrickContext> options = TestDbContextFactory.CreateOptions();

        await using (MerrickContext context = new(options))
        {
            context.SystemTelemetryMetrics.Add(new SystemTelemetryMetrics
            {
                ConcurrentConnectedUsers = 4,
                ActiveMatchServers = 2,
                ActiveMatchmakingQueues = 1,
                AverageWaitTimeSeconds = 14.5,
                ActiveRegionsJson = "[\"US East\"]",
                TimestampLastUpdated = TimeProvider.System.GetUtcNow()
            });
            await context.SaveChangesAsync();
        }

        await using (MerrickContext context = new(options))
        {
            AdminMetricsService service = new(
                NullLogger<AdminMetricsService>.Instance,
                context);

            AdminMetricsDTO dto = await service.GetAdminMetricsAsync();

            Assert.Equal(4, dto.ConcurrentConnectedUsers);
            Assert.Equal(2, dto.ActiveMatchServers);
            Assert.Equal(1, dto.ActiveMatchmakingQueues);
            Assert.Equal(14.5, dto.AverageWaitTimeSeconds);
        }
    }
}
