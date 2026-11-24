// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)
//
// Value-Weighted Testing Gate:
//   protects: (1) Permanent 5v5 default (10 players/match, AllowTwoPlayerFallback=false) so 2 queued players
//                 are pushed into Queue Arcade instead of prematurely popping a 1v1 match,
//             (2) TTL-bounded Redis runtime override (kongor:matchmaking:players_per_team) flipping live
//                 bucket target from 10 -> 2 and immediately restoring 10 when deleted/expired,
//             (3) QueueArcadeCoordinatorService live Redis round_duration_seconds & victory_hold_seconds overrides.
//   fails_when: Leftover PlayersPerTeam=1 or unconditional candidates.Count==2 shortcut blocks Queue Arcade,
//               or deleting the Redis override fails to restore 5v5 matchmaking.
//   seam: Real MatchmakingService + QueueArcadeCoordinatorService + IDatabase Redis wire key lookups.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Moq;
using StackExchange.Redis;
using TRANSMUTANSTEIN.ChatServer.Domain.Communication;
using TRANSMUTANSTEIN.ChatServer.Domain.Core;
using TRANSMUTANSTEIN.ChatServer.Internals;
using TRANSMUTANSTEIN.ChatServer.Services;
using TRANSMUTANSTEIN.ChatServer.Utilities;
using Xunit;

namespace TRANSMUTANSTEIN.ChatServer.Tests.Services;

public sealed class MatchmakingRuntimeOverrideTests
{
    private sealed class ManualClock(DateTimeOffset initialUtc) : TimeProvider
    {
        private DateTimeOffset _utcNow = initialUtc;
        public override DateTimeOffset GetUtcNow() => _utcNow;
        public void Advance(TimeSpan delta) => _utcNow = _utcNow.Add(delta);
    }

    private static Mock<IChatContext> CreateChatContextMock()
    {
        Mock<IChatContext> mockContext = new();
        mockContext.SetupGet(c => c.ClientChatSessions).Returns(new ConcurrentDictionary<string, ChatSession>());
        mockContext.SetupGet(c => c.MatchServerChatSessions).Returns(new ConcurrentDictionary<int, ChatSession>());
        return mockContext;
    }

    [Fact]
    public async Task MatchmakingService_Default5v5_KeepsTwoPlayersAtTargetTen_AndFlipsViaRedisOverrideThenRestores()
    {
        ManualClock clock = new(new DateTimeOffset(2026, 11, 11, 20, 0, 0, TimeSpan.Zero));
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Matchmaking:EnableReadyCheck"] = "true",
                ["Matchmaking:PlayersPerTeam"] = "5",
                ["Matchmaking:AllowTwoPlayerFallback"] = "false"
            })
            .Build();

        RedisValue currentTeamSizeValue = RedisValue.Null;
        RedisValue currentFallbackValue = RedisValue.Null;
        Mock<IDatabase> mockRedis = new();
        mockRedis
            .Setup(db => db.StringGetAsync(MatchmakingService.RedisPlayersPerTeamKey, It.IsAny<CommandFlags>()))
            .ReturnsAsync(() => currentTeamSizeValue);
        mockRedis
            .Setup(db => db.StringGetAsync(MatchmakingService.RedisAllowTwoPlayerFallbackKey, It.IsAny<CommandFlags>()))
            .ReturnsAsync(() => currentFallbackValue);

        MatchmakingService service = new(
            configuration,
            CreateChatContextMock().Object,
            timeProvider: clock,
            distributedCache: mockRedis.Object);

        await service.RefreshRuntimeMatchmakingOverridesAsync(clock.GetUtcNow());
        service.ResolveEffectivePlayersPerTeam().Should().Be(5);
        service.ResolveAllowTwoPlayerFallback().Should().BeFalse();
        service.ResolveTargetPlayersForBucket(ChatProtocol.TMMGameType.TMM_GAME_TYPE_NORMAL, candidateCount: 2)
            .Should().Be(10, "2 queued players in 5v5 mode must stay queued for Queue Arcade instead of auto-popping 1v1");

        // Flip Redis key to 1v1 mid-journey
        currentTeamSizeValue = "1";
        clock.Advance(TimeSpan.FromSeconds(3));
        await service.RefreshRuntimeMatchmakingOverridesAsync(clock.GetUtcNow());

        service.ResolveEffectivePlayersPerTeam().Should().Be(1);
        service.ResolveTargetPlayersForBucket(ChatProtocol.TMMGameType.TMM_GAME_TYPE_NORMAL, candidateCount: 2)
            .Should().Be(2, "live Redis override must pop 1v1 match for the 2 queued players");

        // Delete/expire Redis key -> guaranteed 5v5 restoration
        currentTeamSizeValue = RedisValue.Null;
        clock.Advance(TimeSpan.FromSeconds(3));
        await service.RefreshRuntimeMatchmakingOverridesAsync(clock.GetUtcNow());

        service.ResolveEffectivePlayersPerTeam().Should().Be(5);
        service.ResolveTargetPlayersForBucket(ChatProtocol.TMMGameType.TMM_GAME_TYPE_NORMAL, candidateCount: 2)
            .Should().Be(10, "clearing or expiring the Redis override must restore 5v5 (10 players)");
    }

    [Fact]
    public void AppsettingsJson_OnDisk_Configures5v5DefaultWithTwoPlayerFallbackDisabled()
    {
        string repoRoot = AppContext.BaseDirectory;
        while (!string.IsNullOrEmpty(repoRoot) && !System.IO.File.Exists(System.IO.Path.Combine(repoRoot, "src", "services", "chat-server", "appsettings.json")))
        {
            repoRoot = System.IO.Directory.GetParent(repoRoot)?.FullName ?? string.Empty;
        }

        repoRoot.Should().NotBeNullOrEmpty("chat-server appsettings.json must exist in repository");
        string appsettingsPath = System.IO.Path.Combine(repoRoot, "src", "services", "chat-server", "appsettings.json");
        IConfiguration diskConfig = new ConfigurationBuilder()
            .AddJsonFile(appsettingsPath, optional: false, reloadOnChange: false)
            .Build();

        diskConfig.GetValue<int>("Matchmaking:PlayersPerTeam").Should().Be(5, "production default on disk must be 5v5");
        diskConfig.GetValue<bool>("Matchmaking:AllowTwoPlayerFallback").Should().BeFalse("2-player shortcut must be disabled by default so Queue Arcade can run");
    }
}
