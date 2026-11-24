// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using ASPIRE.Common.Services.Leaderboard;
using NEXUS.Contracts.Services;

namespace ASPIRE.Tests.Common;

public class RedisLeaderboardCacheTests
{
    [Fact]
    public async Task GetTopRankedAsync_WhenRedisNull_ReturnsEmptyListGracefully()
    {
        ILeaderboardCacheService service = new RedisLeaderboardCacheService();

        List<(int AccountId, double Score, long Rank)> result = await service.GetTopRankedAsync("1", "ranked");

        Assert.NotNull(result);
        Assert.Empty(result);
    }

    [Fact]
    public async Task GetPlayerRankAsync_WhenRedisNull_ReturnsNullGracefully()
    {
        ILeaderboardCacheService service = new RedisLeaderboardCacheService();

        long? rank = await service.GetPlayerRankAsync("1", "ranked", 101);

        Assert.Null(rank);
    }

    [Fact]
    public async Task GetPlayerScoreAsync_WhenRedisNull_ReturnsNullGracefully()
    {
        ILeaderboardCacheService service = new RedisLeaderboardCacheService();

        double? score = await service.GetPlayerScoreAsync("1", "ranked", 101);

        Assert.Null(score);
    }

    [Fact]
    public async Task RecordScoreAsync_WhenRedisNull_DoesNotThrow()
    {
        ILeaderboardCacheService service = new RedisLeaderboardCacheService();

        await service.RecordScoreAsync("1", "ranked", 101, 1850.5);
    }

    [Fact]
    public async Task RecordScoresBatchAsync_WhenRedisNull_DoesNotThrow()
    {
        ILeaderboardCacheService service = new RedisLeaderboardCacheService();

        List<(int AccountId, double Score)> scores = [(101, 1850.5), (102, 1720.0)];
        await service.RecordScoresBatchAsync("1", "ranked", scores);
    }
}
