// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ============================================================================
// File: RedisMatchSessionCacheTests.cs
// System: Kongor Core Unit Tests - Live Redis Match Session Cache
// Policy: Strict Explicit Typing (NO 'var')
// ============================================================================

using FluentAssertions;
using Kongor.Core.Services.Session;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using NEXUS.Contracts.Telemetry;
using Xunit;

namespace Kongor.Core.Tests.Caching;

public sealed class RedisMatchSessionCacheTests
{
    [Fact]
    public async Task SetAndGetLiveMatchState_WhenRedisOffline_FallsBackToMemoryCache()
    {
        // Arrange
        IMemoryCache memoryCache = new MemoryCache(new MemoryCacheOptions());
        RedisMatchSessionCacheService service = new(
            muxer: null,
            memoryCache: memoryCache,
            logger: NullLogger<RedisMatchSessionCacheService>.Instance);

        LiveServerTickPayloadDto payload = new()
        {
            MatchID = 15984210,
            Map = "caldavar",
            GameMode = "Ranked (All Pick)",
            ElapsedSeconds = 900,
            ScoreLegion = 12,
            ScoreHellbourne = 8,
            GoldAdvantageLegion = 2500,
            GoldAdvantageHellbourne = 0,
            Players =
            [
                new LivePlayerTelemetryDto
                {
                    Slot = 0,
                    AccountID = 58,
                    AccountName = "guccidonut",
                    Team = 1,
                    HeroIdentifier = "Hero_Pyromancer",
                    Level = 11,
                    PositionX = 0.25f,
                    PositionY = 0.35f,
                    Kills = 5,
                    Deaths = 1,
                    Assists = 4,
                    CurrentGold = 1800,
                    IsAlive = true
                }
            ]
        };

        // Act
        await service.SetLiveMatchStateAsync(payload.MatchID, payload);
        LiveServerTickPayloadDto? retrieved = await service.GetLiveMatchStateAsync(payload.MatchID);

        // Assert
        retrieved.Should().NotBeNull();
        retrieved!.MatchID.Should().Be(15984210);
        retrieved.Map.Should().Be("caldavar");
        retrieved.ScoreLegion.Should().Be(12);
        retrieved.ScoreHellbourne.Should().Be(8);
        retrieved.Players.Should().HaveCount(1);
        retrieved.Players[0].HeroIdentifier.Should().Be("Hero_Pyromancer");
        retrieved.Players[0].Kills.Should().Be(5);
    }

    [Fact]
    public async Task RemoveLiveMatchState_EvictsFromCache()
    {
        // Arrange
        IMemoryCache memoryCache = new MemoryCache(new MemoryCacheOptions());
        RedisMatchSessionCacheService service = new(
            muxer: null,
            memoryCache: memoryCache,
            logger: NullLogger<RedisMatchSessionCacheService>.Instance);

        LiveServerTickPayloadDto payload = new()
        {
            MatchID = 991122,
            Map = "caldavar",
            ElapsedSeconds = 120
        };

        await service.SetLiveMatchStateAsync(payload.MatchID, payload);

        // Act
        await service.RemoveLiveMatchStateAsync(payload.MatchID);
        LiveServerTickPayloadDto? retrieved = await service.GetLiveMatchStateAsync(payload.MatchID);

        // Assert
        retrieved.Should().BeNull();
    }

    [Fact]
    public async Task PublishMatchEvent_DoesNotThrowWhenDisconnected()
    {
        // Arrange
        RedisMatchSessionCacheService service = new(
            muxer: null,
            memoryCache: new MemoryCache(new MemoryCacheOptions()),
            logger: NullLogger<RedisMatchSessionCacheService>.Instance);

        // Act & Assert
        Func<Task> act = async () => await service.PublishMatchEventAsync(
            15984210, "HERO_KILL", "{\"killer\":58,\"victim\":1077}");

        await act.Should().NotThrowAsync();
    }
}
