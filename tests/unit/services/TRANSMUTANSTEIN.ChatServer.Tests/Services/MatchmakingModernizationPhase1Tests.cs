// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Collections.Concurrent;
using ASPIRE.Common.Services.Matchmaking;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using TRANSMUTANSTEIN.ChatServer.Domain.Core;
using TRANSMUTANSTEIN.ChatServer.Domain.Matchmaking;
using TRANSMUTANSTEIN.ChatServer.Internals;
using TRANSMUTANSTEIN.ChatServer.Services;
using TRANSMUTANSTEIN.ChatServer.Utilities;
using Xunit;

namespace TRANSMUTANSTEIN.ChatServer.Tests.Services;

public sealed class MatchmakingModernizationPhase1Tests
{
    public MatchmakingModernizationPhase1Tests()
    {
        Log.Initialise(NullLogger.Instance);
    }

    [Fact]
    public void MatchServerChatSessionMetadata_Location_DefaultsToEmpty_AndCanBeAssigned()
    {
        MatchServerChatSessionMetadata metadata = new()
        {
            ServerID = 101,
            SessionCookie = "test_cookie",
            ChatProtocolVersion = (int)ChatProtocol.CHAT_PROTOCOL_EXTERNAL_VERSION
        };

        metadata.Location.Should().BeEmpty();

        metadata.Location = "us-east";
        metadata.Location.Should().Be("us-east");
    }

    [Fact]
    public void SweepExpiredPendingMatches_WhenOlderThan30Seconds_RestitutesGroupsAndResetsServerToIdle()
    {
        // Arrange
        Mock<IChatContext> mockChatContext = new();
        ConcurrentDictionary<int, ChatSession> matchServers = new();
        mockChatContext.SetupGet(c => c.MatchServerChatSessions).Returns(matchServers);
        mockChatContext.SetupGet(c => c.ClientChatSessions).Returns(new ConcurrentDictionary<string, ChatSession>());

        IConfiguration config = new ConfigurationBuilder().Build();
        MatchmakingService service = new(config, mockChatContext.Object);

        DateTimeOffset now = DateTimeOffset.UtcNow;
        DateTimeOffset expiredCreatedAt = now.AddSeconds(-35);

        // Test Server Session with LOADING status
        TestChatSession serverSession = new(mockChatContext.Object)
        {
            ServerMetadata = new MatchServerChatSessionMetadata
            {
                ServerID = 42,
                SessionCookie = "cookie",
                ChatProtocolVersion = 1,
                Status = ChatProtocol.ServerStatus.SERVER_STATUS_LOADING,
                Location = "eu-central"
            }
        };
        matchServers[42] = serverSession;

        MatchmakingGroup group = new()
        {
            QueueStartTime = null, // In match
            Information = new MatchmakingGroupInformation
            {
                ClientVersion = "1.0",
                GroupType = ChatProtocol.TMMType.TMM_TYPE_PVP,
                GameType = ChatProtocol.TMMGameType.TMM_GAME_TYPE_NORMAL,
                MapName = "caldavar",
                GameModes = ["normal"],
                GameRegions = ["eu-central"],
                Ranked = true,
                MatchFidelity = 1,
                BotDifficulty = 0,
                RandomizeBots = false
            },
            Members = []
        };

        service.PendingMatches[999] = new PendingMatchState
        {
            AllocatedServerID = 42,
            CreatedAt = expiredCreatedAt,
            Groups = [group],
            Team1Groups = [group],
            Team2Groups = []
        };

        // Act
        service.SweepExpiredPendingMatches(now);

        // Assert
        service.PendingMatches.ContainsKey(999).Should().BeFalse("Timed-out pending match must be removed from PendingMatches");
        serverSession.ServerMetadata.Status.Should().Be(ChatProtocol.ServerStatus.SERVER_STATUS_IDLE, "Server status must be reset to IDLE");
        group.QueueStartTime.Should().Be(now, "Group queue start time must be restored to restitute them back in queue");
    }

    [Fact]
    public void SweepExpiredPendingMatches_WhenUnder30Seconds_DoesNotSweepMatch()
    {
        // Arrange
        Mock<IChatContext> mockChatContext = new();
        ConcurrentDictionary<int, ChatSession> matchServers = new();
        mockChatContext.SetupGet(c => c.MatchServerChatSessions).Returns(matchServers);
        mockChatContext.SetupGet(c => c.ClientChatSessions).Returns(new ConcurrentDictionary<string, ChatSession>());

        IConfiguration config = new ConfigurationBuilder().Build();
        MatchmakingService service = new(config, mockChatContext.Object);

        DateTimeOffset now = DateTimeOffset.UtcNow;
        DateTimeOffset recentCreatedAt = now.AddSeconds(-10);

        service.PendingMatches[888] = new PendingMatchState
        {
            AllocatedServerID = 42,
            CreatedAt = recentCreatedAt,
            Groups = [],
            Team1Groups = [],
            Team2Groups = []
        };

        // Act
        service.SweepExpiredPendingMatches(now);

        // Assert
        service.PendingMatches.ContainsKey(888).Should().BeTrue("Matches waiting less than 30s should not be swept");
    }

    [Fact]
    public void Cleanup_WhenMatchServerSessionDisconnectsAbruptly_RemovesServerFromActivePool()
    {
        // Arrange
        Mock<IChatContext> mockChatContext = new();
        ConcurrentDictionary<int, ChatSession> matchServers = new();
        ConcurrentDictionary<int, ChatSession> matchManagers = new();
        mockChatContext.SetupGet(c => c.MatchServerChatSessions).Returns(matchServers);
        mockChatContext.SetupGet(c => c.MatchServerManagerChatSessions).Returns(matchManagers);
        mockChatContext.SetupGet(c => c.ClientChatSessions).Returns(new ConcurrentDictionary<string, ChatSession>());

        TestChatSession serverSession = new(mockChatContext.Object)
        {
            ServerMetadata = new MatchServerChatSessionMetadata
            {
                ServerID = 777,
                SessionCookie = "cookie_xyz",
                ChatProtocolVersion = 1,
                Status = ChatProtocol.ServerStatus.SERVER_STATUS_IDLE,
                Location = "us-east"
            }
        };

        matchServers[777] = serverSession;

        // Act
        serverSession.Cleanup();

        // Assert
        matchServers.ContainsKey(777).Should().BeFalse("Abruptly disconnected match server must be evicted from active pool");
    }

    [Fact]
    public void SweepExpiredPendingMatches_WithSlotLease_ReleasesLeaseAtomically()
    {
        // Arrange
        Mock<IChatContext> mockChatContext = new();
        ConcurrentDictionary<int, ChatSession> matchServers = new();
        mockChatContext.SetupGet(c => c.MatchServerChatSessions).Returns(matchServers);
        mockChatContext.SetupGet(c => c.ClientChatSessions).Returns(new ConcurrentDictionary<string, ChatSession>());

        Mock<IRedisServerSlotLeaseService> mockLeaseService = new();
        IConfiguration config = new ConfigurationBuilder().Build();
        MatchmakingService service = new(config, mockChatContext.Object, slotLeaseService: mockLeaseService.Object);

        DateTimeOffset now = DateTimeOffset.UtcNow;
        DateTimeOffset expiredCreatedAt = now.AddSeconds(-45);

        service.PendingMatches[555] = new PendingMatchState
        {
            AllocatedServerID = 10,
            CreatedAt = expiredCreatedAt,
            Groups = [],
            Team1Groups = [],
            Team2Groups = [],
            SlotLockKey = "lock:slot:us-east:127.0.0.1:11235",
            SlotLeaseToken = "lease_token_123"
        };

        // Act
        service.SweepExpiredPendingMatches(now);

        // Assert
        service.PendingMatches.ContainsKey(555).Should().BeFalse();
        mockLeaseService.Verify(l => l.ReleaseSlotLeaseAsync("lock:slot:us-east:127.0.0.1:11235", "lease_token_123", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ConfirmMatch_WithSlotLease_ReleasesLeaseAtomically()
    {
        // Arrange
        Mock<IChatContext> mockChatContext = new();
        ConcurrentDictionary<int, ChatSession> matchServers = new();
        mockChatContext.SetupGet(c => c.MatchServerChatSessions).Returns(matchServers);
        mockChatContext.SetupGet(c => c.ClientChatSessions).Returns(new ConcurrentDictionary<string, ChatSession>());

        Mock<IRedisServerSlotLeaseService> mockLeaseService = new();
        IConfiguration config = new ConfigurationBuilder().Build();
        MatchmakingService service = new(config, mockChatContext.Object, slotLeaseService: mockLeaseService.Object);

        MatchmakingGroup group = new()
        {
            QueueStartTime = null,
            Information = new MatchmakingGroupInformation
            {
                ClientVersion = "1.0",
                GroupType = ChatProtocol.TMMType.TMM_TYPE_PVP,
                GameType = ChatProtocol.TMMGameType.TMM_GAME_TYPE_NORMAL,
                MapName = "caldavar",
                GameModes = ["normal"],
                GameRegions = ["us-east"],
                Ranked = true,
                MatchFidelity = 1,
                BotDifficulty = 0,
                RandomizeBots = false
            },
            Members = []
        };

        service.PendingMatches[777] = new PendingMatchState
        {
            AllocatedServerID = 10,
            CreatedAt = DateTimeOffset.UtcNow,
            Groups = [group],
            Team1Groups = [group],
            Team2Groups = [],
            SlotLockKey = "lock:slot:us-east:127.0.0.1:11235",
            SlotLeaseToken = "lease_token_777"
        };

        // Act
        await service.ConfirmMatch(777, 99999, "127.0.0.1", 11235);

        // Assert
        service.PendingMatches.ContainsKey(777).Should().BeFalse();
        mockLeaseService.Verify(l => l.ReleaseSlotLeaseAsync("lock:slot:us-east:127.0.0.1:11235", "lease_token_777", It.IsAny<CancellationToken>()), Times.Once);
    }

    private sealed class TestChatSession : ChatSession
    {
        public TestChatSession(IChatContext chatContext) : base(
            new TCPServer(System.Net.IPAddress.Loopback, 0),
            CreateServices(chatContext))
        {
        }

        private static IServiceProvider CreateServices(IChatContext chatContext)
        {
            ServiceCollection sc = new();
            sc.AddSingleton(chatContext);
            sc.AddSingleton(Mock.Of<IDiscordPresencePublisherService>());
            sc.AddSingleton(Mock.Of<IMatchmakingService>());
            return sc.BuildServiceProvider();
        }

        public override bool SendAsync(ReadOnlySpan<byte> buffer) => true;
    }
}
