// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using ASPIRE.Common.Caching;
using ASPIRE.Common.Extensions;
using FluentAssertions;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;
using ZiggyCreatures.Caching.Fusion;

namespace Kongor.Core.Tests.Caching;

public class NexusHybridCacheResilienceTests
{
    [Fact]
    public async Task GetOrCreateAsync_WithStandardCache_ReturnsCachedValueAndExposesFusionHybridCache()
    {
        ServiceCollection services = new();
        services.AddLogging();
        services.AddNexusHybridCache();
        await using ServiceProvider serviceProvider = services.BuildServiceProvider();

        INexusHybridCache cache = serviceProvider.GetRequiredService<INexusHybridCache>();
        IFusionCache fusionCache = serviceProvider.GetRequiredService<IFusionCache>();
        HybridCache hybridCache = serviceProvider.GetRequiredService<HybridCache>();

        fusionCache.Should().NotBeNull();
        hybridCache.Should().NotBeNull();

        int factoryInvocations = 0;

        Task<string?> Factory(CancellationToken ct)
        {
            factoryInvocations++;
            return Task.FromResult<string?>("HON_HERO_PYROMANCER");
        }

        string? result1 = await cache.GetOrCreateAsync("hero:pyromancer", Factory);
        string? result2 = await cache.GetOrCreateAsync("hero:pyromancer", Factory);

        result1.Should().Be("HON_HERO_PYROMANCER");
        result2.Should().Be("HON_HERO_PYROMANCER");
        factoryInvocations.Should().Be(1);
    }

    [Fact]
    public async Task NexusHybridCacheResilience_RemoveByTagAsync_InvalidatesTaggedEntries()
    {
        ServiceCollection services = new();
        services.AddLogging();
        services.AddNexusHybridCache();
        await using ServiceProvider serviceProvider = services.BuildServiceProvider();

        INexusHybridCache cache = serviceProvider.GetRequiredService<INexusHybridCache>();
        int factoryInvocations = 0;

        Task<string?> Factory(CancellationToken ct)
        {
            factoryInvocations++;
            return Task.FromResult<string?>($"VAL_{factoryInvocations}");
        }

        string? val1 = await cache.GetOrCreateAsync("hero:devourer", Factory, tags: ["heroes"]);
        await cache.RemoveByTagAsync("heroes");
        string? val2 = await cache.GetOrCreateAsync("hero:devourer", Factory, tags: ["heroes"]);

        val1.Should().Be("VAL_1");
        val2.Should().Be("VAL_2");
        factoryInvocations.Should().Be(2);
    }

    [Fact]
    public async Task GetOrCreateAsync_DefaultAllowStaleFalse_FailsClosedWhenFactoryThrowsAfterExpiry()
    {
        ServiceCollection services = new();
        services.AddLogging();
        services.AddNexusHybridCache();
        await using ServiceProvider serviceProvider = services.BuildServiceProvider();

        INexusHybridCache cache = serviceProvider.GetRequiredService<INexusHybridCache>();
        bool simulateOutage = false;
        int factoryCalls = 0;

        Task<string?> WalletFactory(CancellationToken ct)
        {
            factoryCalls++;
            if (simulateOutage)
            {
                throw new InvalidOperationException("Simulated upstream PostgreSQL wallet outage");
            }

            return Task.FromResult<string?>("GOLD_COINS:500");
        }

        string? initial = await cache.GetOrCreateAsync(
            "wallet:user:42",
            WalletFactory,
            l1Expiration: TimeSpan.FromMilliseconds(40),
            l2Expiration: TimeSpan.FromMilliseconds(40));

        initial.Should().Be("GOLD_COINS:500");
        factoryCalls.Should().Be(1);

        await Task.Delay(75);
        simulateOutage = true;

        Func<Task> act = async () => await cache.GetOrCreateAsync(
            "wallet:user:42",
            WalletFactory,
            l1Expiration: TimeSpan.FromMilliseconds(40),
            l2Expiration: TimeSpan.FromMilliseconds(40));

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*Simulated upstream PostgreSQL wallet outage*");
        factoryCalls.Should().Be(2);
    }

    [Fact]
    public async Task GetOrCreateAsync_AllowStaleOnErrorTrue_ServesLastKnownGoodPayloadDuringUpstreamOutage()
    {
        ServiceCollection services = new();
        services.AddLogging();
        services.AddNexusHybridCache();
        await using ServiceProvider serviceProvider = services.BuildServiceProvider();

        INexusHybridCache cache = serviceProvider.GetRequiredService<INexusHybridCache>();
        bool simulateOutage = false;
        int factoryCalls = 0;

        Task<string?> ServerListFactory(CancellationToken ct)
        {
            factoryCalls++;
            if (simulateOutage)
            {
                throw new InvalidOperationException("Simulated upstream Redis/DB transient outage");
            }

            return Task.FromResult<string?>("SERVER_LIST_USE_SNAPSHOT_V1");
        }

        string? initial = await cache.GetOrCreateAsync(
            "SERVER-LIST:10:USE:test",
            ServerListFactory,
            tags: ["server-list"],
            l1Expiration: TimeSpan.FromMilliseconds(40),
            l2Expiration: TimeSpan.FromMilliseconds(40),
            allowStaleOnError: true);

        initial.Should().Be("SERVER_LIST_USE_SNAPSHOT_V1");
        factoryCalls.Should().Be(1);

        await Task.Delay(75);
        simulateOutage = true;

        string? staleFallback = await cache.GetOrCreateAsync(
            "SERVER-LIST:10:USE:test",
            ServerListFactory,
            tags: ["server-list"],
            l1Expiration: TimeSpan.FromMilliseconds(40),
            l2Expiration: TimeSpan.FromMilliseconds(40),
            allowStaleOnError: true);

        staleFallback.Should().Be("SERVER_LIST_USE_SNAPSHOT_V1");
        factoryCalls.Should().Be(2);
    }

    [Fact]
    public async Task AddNexusHybridCache_ConfiguresOneSecondDefaultSoftTimeout_AndEmitsSingleStructuredWarningWithoutSyntheticExceptionWarning()
    {
        // protects: WAN PostgreSQL cold-cache resilience (1000ms soft-timeout vs 537ms split-query RTT) and single-line
        //           structured SoftTimeout logging without multi-line FusionCache internal SyntheticTimeoutException warnings.
        // fails_when: DefaultFactorySoftTimeout regresses to 500ms or FactorySyntheticTimeoutsLogLevel emits Warning stacks.
        // seam: Real DI-built IFusionCache + NexusHybridCache + ILoggerProvider pipeline.
        List<(Microsoft.Extensions.Logging.LogLevel Level, string Message, Exception? Exception)> logs = [];
        ServiceCollection services = new();
        services.AddLogging(b =>
        {
            b.SetMinimumLevel(Microsoft.Extensions.Logging.LogLevel.Debug);
            b.AddProvider(new CapturingLoggerProvider(logs));
        });
        services.AddNexusHybridCache();
        await using ServiceProvider serviceProvider = services.BuildServiceProvider();

        INexusHybridCache hybridCache = serviceProvider.GetRequiredService<INexusHybridCache>();

        // 1. Verify a 550ms factory (simulating 537ms cold WAN SimpleStats) completes cleanly on the first call without soft-timeout
        string? value = await hybridCache.GetOrCreateAsync(
            "profile:simple_stats:wan_cold_probe:ranked",
            async ct =>
            {
                await Task.Delay(550, ct);
                return "SERIALIZED_PHP_STATS_OK";
            });

        value.Should().Be("SERIALIZED_PHP_STATS_OK");
        lock (logs)
        {
            logs.Should().NotContain(l => l.Message.Contains("[FusionCache:SoftTimeout]"));
        }

        // 2. Seed a cached entry, expire L1/L2, then trigger a 1120ms slow refresh (> 1000ms soft-timeout) with fail-safe enabled
        await hybridCache.GetOrCreateAsync(
            "profile:simple_stats:slow_refresh:ranked",
            _ => Task.FromResult<string?>("STALE_OK"),
            l1Expiration: TimeSpan.FromMilliseconds(30),
            l2Expiration: TimeSpan.FromMilliseconds(30),
            allowStaleOnError: true);
        await Task.Delay(50);
        lock (logs)
        {
            logs.Clear();
        }

        string? fallback = await hybridCache.GetOrCreateAsync(
            "profile:simple_stats:slow_refresh:ranked",
            async ct =>
            {
                await Task.Delay(1350, ct);
                return "REFRESHED_OK";
            },
            l1Expiration: TimeSpan.FromMilliseconds(30),
            l2Expiration: TimeSpan.FromMilliseconds(30),
            allowStaleOnError: true);

        fallback.Should().Be("STALE_OK");

        // FusionCache dispatches FactoryTimedOut event handlers asynchronously on the ThreadPool;
        // wait briefly under 14-project parallel saturation before snapshotting under lock(logs).
        DateTime softTimeoutDeadline = DateTime.UtcNow.AddMilliseconds(750);
        bool sawSoftTimeoutWarning = false;
        while (DateTime.UtcNow < softTimeoutDeadline)
        {
            lock (logs)
            {
                if (logs.Any(l =>
                        l.Level == Microsoft.Extensions.Logging.LogLevel.Warning &&
                        l.Message.Contains("[FusionCache:SoftTimeout]")))
                {
                    sawSoftTimeoutWarning = true;
                    break;
                }
            }
            await Task.Delay(10);
        }

        (Microsoft.Extensions.Logging.LogLevel Level, string Message, Exception? Exception)[] snapshot;
        lock (logs)
        {
            snapshot = [.. logs];
        }

        sawSoftTimeoutWarning.Should().BeTrue();
        snapshot.Should().Contain(l =>
            l.Level == Microsoft.Extensions.Logging.LogLevel.Warning &&
            l.Message.Contains("[FusionCache:SoftTimeout]"));
        snapshot.Should().NotContain(l =>
            l.Level >= Microsoft.Extensions.Logging.LogLevel.Warning &&
            l.Exception is SyntheticTimeoutException);
    }

    private sealed class CapturingLoggerProvider(
        List<(Microsoft.Extensions.Logging.LogLevel Level, string Message, Exception? Exception)> sink)
        : Microsoft.Extensions.Logging.ILoggerProvider
    {
        public Microsoft.Extensions.Logging.ILogger CreateLogger(string categoryName) => new CapturingLogger(sink);
        public void Dispose() { }

        private sealed class CapturingLogger(
            List<(Microsoft.Extensions.Logging.LogLevel Level, string Message, Exception? Exception)> sink)
            : Microsoft.Extensions.Logging.ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;
            public void Log<TState>(
                Microsoft.Extensions.Logging.LogLevel logLevel,
                Microsoft.Extensions.Logging.EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                lock (sink)
                {
                    sink.Add((logLevel, formatter(state, exception), exception));
                }
            }
        }
    }
}
