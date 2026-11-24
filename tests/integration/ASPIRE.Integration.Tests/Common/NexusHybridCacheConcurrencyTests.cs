// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using ASPIRE.Common.Caching;
using ASPIRE.Common.Extensions;

namespace ASPIRE.Tests.Common;

public sealed class NexusHybridCacheConcurrencyTests
{
    private readonly INexusHybridCache _cache;

    public NexusHybridCacheConcurrencyTests()
    {
        ServiceCollection services = new();
        services.AddNexusHybridCache();
        ServiceProvider sp = services.BuildServiceProvider();
        _cache = sp.GetRequiredService<INexusHybridCache>();
    }

    [Trait("Category", "Manual")]
    [Fact]
    public async Task GetOrCreateAsync_When50ConcurrentRequests_OnlyExecutesFactoryOnce()
    {
        if (!NexusTestEnvironment.IsManualTestEnabled) return;

        int factoryExecutionCount = 0;

        const int concurrency = 50;
        Task<string?>[] tasks = new Task<string?>[concurrency];

        for (int i = 0; i < concurrency; i++)
            tasks[i] = Task.Run(() => _cache.GetOrCreateAsync("stampede_key", ExpensiveDbFactory));

        string?[] results = await Task.WhenAll(tasks);

        Assert.Equal(1, factoryExecutionCount);
        Assert.All(results, result => Assert.Equal("stamped_value", result));
        return;

        async Task<string?> ExpensiveDbFactory(CancellationToken ct)
        {
            Interlocked.Increment(ref factoryExecutionCount);
            // Simulate 20ms database query latency
            await Task.Delay(20, ct);
            return "stamped_value";
        }
    }
}
