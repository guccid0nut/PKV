// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using ASPIRE.Common.Caching;
using ASPIRE.Common.Extensions;
using MemoryPack;

namespace ASPIRE.Tests.Common;

public class NexusHybridCacheTests
{
    private static INexusHybridCache CreateTestCache()
    {
        ServiceCollection services = new();
        services.AddNexusHybridCache();
        ServiceProvider sp = services.BuildServiceProvider();
        return sp.GetRequiredService<INexusHybridCache>();
    }

    [Fact]
    public async Task GetOrCreateAsync_ReturnsFromL1MemoryCache_OnSecondCall()
    {
        INexusHybridCache cache = CreateTestCache();
        int factoryCallCount = 0;

        string? val1 = await cache.GetOrCreateAsync("test_key", Factory);
        string? val2 = await cache.GetOrCreateAsync("test_key", Factory);

        Assert.Equal("cached_value", val1);
        Assert.Equal("cached_value", val2);
        Assert.Equal(1, factoryCallCount);
        return;

        Task<string?> Factory(CancellationToken ct)
        {
            factoryCallCount++;
            return Task.FromResult<string?>("cached_value");
        }
    }

    [Fact]
    public async Task NexusHybridCache_RemoveAsync_EvictsKeyFromL1MemoryCache()
    {
        INexusHybridCache cache = CreateTestCache();
        int factoryCallCount = 0;

        string? val1 = await cache.GetOrCreateAsync("evict_key", Factory);
        await cache.RemoveAsync("evict_key");
        string? val2 = await cache.GetOrCreateAsync("evict_key", Factory);

        Assert.Equal("value_1", val1);
        Assert.Equal("value_2", val2);
        Assert.Equal(2, factoryCallCount);
        return;

        Task<string?> Factory(CancellationToken ct)
        {
            factoryCallCount++;
            return Task.FromResult<string?>("value_" + factoryCallCount);
        }
    }

    [Fact]
    public async Task NexusHybridCache_RemoveByTagAsync_EvictsTaggedEntriesFromCache()
    {
        INexusHybridCache cache = CreateTestCache();
        int factoryCallCount = 0;
        const string tag = "account:12345";

        PlayerStatisticsAggregatedDTO? stats1 = await cache.GetOrCreateAsync(
            "stats:aggregated:12345",
            Factory,
            [tag]);

        Assert.NotNull(stats1);
        Assert.Equal(1, stats1.Kills);
        Assert.Equal(1, factoryCallCount);

        // Invalidate by tag
        await cache.RemoveByTagAsync(tag);

        PlayerStatisticsAggregatedDTO? stats2 = await cache.GetOrCreateAsync(
            "stats:aggregated:12345",
            Factory,
            [tag]);

        Assert.NotNull(stats2);
        Assert.Equal(2, stats2.Kills);
        Assert.Equal(2, factoryCallCount);
        return;

        Task<PlayerStatisticsAggregatedDTO?> Factory(CancellationToken ct)
        {
            factoryCallCount++;
            PlayerStatisticsAggregatedDTO dto = new() { Kills = factoryCallCount, RankedKills = factoryCallCount * 2 };
            return Task.FromResult<PlayerStatisticsAggregatedDTO?>(dto);
        }
    }

    [Fact]
    public void MemoryPackSerializer_SerializesAndDeserializes_PlayerStatisticsAggregatedDTO()
    {
        PlayerStatisticsAggregatedDTO original = new()
        {
            TotalMatches = 150,
            Kills = 750,
            Deaths = 250,
            Assists = 1200,
            RankedWins = 100,
            RankedLosses = 50,
            RankedRatingChange = 254.5,
            TopHeroes =
            [
                new FavHeroDTO { HeroId = 1, SecondsPlayed = 36000 },
                new FavHeroDTO { HeroId = 12, SecondsPlayed = 18000 }
            ],
            RecentMatchIDs = [1001, 1002, 1003]
        };

        byte[] binary = MemoryPackSerializer.Serialize(original);
        Assert.NotNull(binary);
        Assert.True(binary.Length > 0);

        PlayerStatisticsAggregatedDTO? deserialized =
            MemoryPackSerializer.Deserialize<PlayerStatisticsAggregatedDTO>(binary);
        Assert.NotNull(deserialized);
        Assert.Equal(original.TotalMatches, deserialized.TotalMatches);
        Assert.Equal(original.Kills, deserialized.Kills);
        Assert.Equal(original.RankedRatingChange, deserialized.RankedRatingChange);
        Assert.Equal(2, deserialized.TopHeroes.Count);
        Assert.Equal(1u, deserialized.TopHeroes[0].HeroId);
        Assert.Equal(3, deserialized.RecentMatchIDs.Count);
    }
}
