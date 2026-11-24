// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using ASPIRE.Common.Caching;
using ASPIRE.Common.Extensions;

namespace ASPIRE.Tests.Controllers;

public class DawnBringerHybridCacheTests
{
    private static INexusHybridCache CreateTestCache()
    {
        ServiceCollection services = new();
        services.AddLogging();
        services.AddNexusHybridCache();
        ServiceProvider sp = services.BuildServiceProvider();
        return sp.GetRequiredService<INexusHybridCache>();
    }

    [Fact]
    public async Task GetOrCreateAsync_CachesResult_AndPreventsRedundantFactoryCalls()
    {
        // Arrange
        INexusHybridCache cache = CreateTestCache();
        string cacheKey = $"dawn:test:player:{Guid.NewGuid()}";
        int factoryInvocationCount = 0;

        // Act - Call 1 (Cache Miss)
        string? result1 = await cache.GetOrCreateAsync(
            cacheKey,
            token =>
            {
                Interlocked.Increment(ref factoryInvocationCount);
                return Task.FromResult<string?>("Player_Stats_Data");
            },
            ["players", "leaderboards"],
            TimeSpan.FromMinutes(5),
            TimeSpan.FromMinutes(10)
        );

        // Act - Call 2 (Cache Hit)
        string? result2 = await cache.GetOrCreateAsync(
            cacheKey,
            token =>
            {
                Interlocked.Increment(ref factoryInvocationCount);
                return Task.FromResult<string?>("Stale_Data");
            },
            ["players", "leaderboards"],
            TimeSpan.FromMinutes(5),
            TimeSpan.FromMinutes(10)
        );

        // Assert
        Assert.Equal("Player_Stats_Data", result1);
        Assert.Equal("Player_Stats_Data", result2);
        Assert.Equal(1, factoryInvocationCount); // Factory called only once!
    }

    [Fact]
    public async Task DawnBringerHybridCache_RemoveByTagAsync_EvictsTaggedEntries()
    {
        // Arrange
        INexusHybridCache cache = CreateTestCache();
        string cacheKey = $"dawn:test:heroes:{Guid.NewGuid()}";
        int factoryInvocationCount = 0;

        await cache.GetOrCreateAsync(
            cacheKey,
            token =>
            {
                Interlocked.Increment(ref factoryInvocationCount);
                return Task.FromResult<string?>("Hero_Bubbles");
            },
            ["heroes"]
        );

        Assert.Equal(1, factoryInvocationCount);

        // Act - Invalidate by tag
        await cache.RemoveByTagAsync("heroes");

        // Act - Query again (should invoke factory again)
        string? resultAfterEviction = await cache.GetOrCreateAsync(
            cacheKey,
            token =>
            {
                Interlocked.Increment(ref factoryInvocationCount);
                return Task.FromResult<string?>("Hero_Bubbles_Refreshed");
            },
            ["heroes"]
        );

        // Assert
        Assert.Equal("Hero_Bubbles_Refreshed", resultAfterEviction);
        Assert.Equal(2, factoryInvocationCount);
    }

    [Trait("Category", "Manual")]
    [Fact]
    public async Task GetOrCreateAsync_ConcurrentRequests_CoalesceToSingleExecution()
    {
        if (!NexusTestEnvironment.IsManualTestEnabled) return;
        // Arrange
        INexusHybridCache cache = CreateTestCache();
        string cacheKey = $"dawn:test:concurrency:{Guid.NewGuid()}";
        int factoryInvocationCount = 0;

        // Act - Spawn 20 concurrent requests for the same key
        List<Task<string?>> tasks = [];
        for (int i = 0; i < 20; i++)
            tasks.Add(cache.GetOrCreateAsync(
                cacheKey,
                async token =>
                {
                    Interlocked.Increment(ref factoryInvocationCount);
                    await Task.Delay(20, token); // Simulate DB query latency
                    return "Leaderboard_Top100";
                },
                ["leaderboards"]
            ));

        string?[] results = await Task.WhenAll(tasks);

        // Assert - All 20 requests get the exact result, but factory only executed once!
        Assert.Equal(20, results.Length);
        foreach (string? res in results) Assert.Equal("Leaderboard_Top100", res);
        Assert.Equal(1, factoryInvocationCount);
    }
}
