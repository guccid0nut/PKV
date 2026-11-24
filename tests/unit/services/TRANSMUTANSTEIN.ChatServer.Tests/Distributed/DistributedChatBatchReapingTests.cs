// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Collections.Concurrent;
using System.Net;
using FluentAssertions;
using MERRICK.DatabaseContext.Entities.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using StackExchange.Redis;
using TRANSMUTANSTEIN.ChatServer.Domain.Clans;
using TRANSMUTANSTEIN.ChatServer.Domain.Communication;
using TRANSMUTANSTEIN.ChatServer.Domain.Core;
using TRANSMUTANSTEIN.ChatServer.Domain.Matchmaking;
using TRANSMUTANSTEIN.ChatServer.Infrastructure.Distributed;
using TRANSMUTANSTEIN.ChatServer.Internals;
using TRANSMUTANSTEIN.ChatServer.Services;
using Xunit;

namespace TRANSMUTANSTEIN.ChatServer.Tests.Distributed;

public sealed class DistributedChatBatchReapingTests
{
    private readonly Mock<IConnectionMultiplexer> _mockRedis = new();
    private readonly Mock<ISubscriber> _mockSubscriber = new();
    private readonly Mock<IDatabase> _mockDatabase = new();
    private readonly Mock<IBatch> _mockBatch = new();
    private readonly Mock<IChatContext> _mockChatContext = new();
    private readonly Mock<IDiscordPresencePublisherService> _mockDiscord = new();
    private readonly Mock<IMatchmakingService> _mockMatchmaking = new();
    private readonly ConcurrentDictionary<string, ChatSession> _clientSessions = new();
    private readonly TCPServer _tcpServer;
    private readonly IServiceProvider _serviceProvider;

    public DistributedChatBatchReapingTests()
    {
        _mockRedis.Setup(r => r.GetSubscriber(It.IsAny<object>())).Returns(_mockSubscriber.Object);
        _mockRedis.Setup(r => r.GetDatabase(It.IsAny<int>(), It.IsAny<object>())).Returns(_mockDatabase.Object);
        _mockRedis.SetupGet(r => r.IsConnected).Returns(true);

        _mockDatabase.Setup(d => d.CreateBatch(It.IsAny<object>())).Returns(_mockBatch.Object);
        _mockBatch.Setup(b => b.HashDeleteAsync(It.IsAny<RedisKey>(), It.IsAny<RedisValue[]>(), It.IsAny<CommandFlags>()))
            .ReturnsAsync(1L);
        _mockBatch.Setup(b => b.SetRemoveAsync(It.IsAny<RedisKey>(), It.IsAny<RedisValue[]>(), It.IsAny<CommandFlags>()))
            .ReturnsAsync(1L);

        _mockChatContext.SetupGet(c => c.ClientChatSessions).Returns(_clientSessions);
        _mockChatContext.SetupGet(c => c.ChatChannels).Returns(new ConcurrentDictionary<string, ChatChannel>());
        _mockChatContext.SetupGet(c => c.ChatChannelsByID).Returns(new ConcurrentDictionary<int, ChatChannel>());

        ServiceCollection services = new();
        services.AddSingleton(_mockChatContext.Object);
        services.AddSingleton(_mockDiscord.Object);
        services.AddSingleton(_mockMatchmaking.Object);
        services.AddSingleton<ILogger<ChatSession>>(NullLogger<ChatSession>.Instance);
        _serviceProvider = services.BuildServiceProvider();

        _tcpServer = new TCPServer(IPAddress.Loopback, 0);
    }

    private DistributedChatStateService CreateService(IConnectionMultiplexer? redis = null)
    {
        return new DistributedChatStateService(
            _mockChatContext.Object,
            TimeProvider.System,
            NullLogger<DistributedChatStateService>.Instance,
            redis ?? _mockRedis.Object,
            _serviceProvider);
    }

    private static Account CreateAccount(int id, string name, Clan? clan = null)
    {
        return new Account
        {
            ID = id,
            Name = name,
            Clan = clan,
            User = new User
            {
                ID = id,
                EmailAddress = $"{name.ToLowerInvariant()}@kongor.net",
                SRPPasswordSalt = "salt",
                SRPPasswordHash = "hash"
            }
        };
    }

    [Fact]
    public async Task UnregisterSessionsBatchAsync_EmptyList_ReturnsZeroWithoutCallingRedis()
    {
        // Arrange
        DistributedChatStateService service = CreateService();

        // Act
        int reaped = await service.UnregisterSessionsBatchAsync([]);

        // Assert
        reaped.Should().Be(0);
        _mockDatabase.Verify(d => d.CreateBatch(It.IsAny<object>()), Times.Never);
    }

    [Fact]
    public async Task UnregisterSessionsBatchAsync_MultipleSessions_PartitionsAndExecutesBatchPipeline()
    {
        // Arrange
        DistributedChatStateService service = CreateService();
        List<(string accountName, int accountId)> sessions = new();
        for (int i = 1; i <= 50; i++)
        {
            sessions.Add(($"Player_{i}", i));
        }

        // Act
        int reaped = await service.UnregisterSessionsBatchAsync(sessions);

        // Assert
        reaped.Should().BeGreaterThan(0);
        _mockDatabase.Verify(d => d.CreateBatch(It.IsAny<object>()), Times.Once);
        _mockBatch.Verify(b => b.Execute(), Times.Once);
        _mockBatch.Verify(b => b.HashDeleteAsync(It.IsAny<RedisKey>(), It.IsAny<RedisValue[]>(), It.IsAny<CommandFlags>()), Times.AtLeast(2));
    }

    [Fact]
    public async Task UnregisterSessionsBatchAsync_LocalSessions_CleansLocalPresenceAndClanIndexes()
    {
        // Arrange
        DistributedChatStateService service = CreateService();
        Clan testClan = new() { ID = 99, Name = "TestClan", Tag = "TEST" };

        Account acc1 = CreateAccount(101, "ClanMate1", testClan);
        ChatSession session1 = new(_tcpServer, _serviceProvider) { Account = acc1 };

        Account acc2 = CreateAccount(102, "ClanMate2", testClan);
        ChatSession session2 = new(_tcpServer, _serviceProvider) { Account = acc2 };

        service.RegisterLocalSession(session1);
        service.RegisterLocalSession(session2);

        // Act
        int reaped = await service.UnregisterSessionsBatchAsync([
            (acc1.Name, acc1.ID),
            (acc2.Name, acc2.ID)
        ]);

        // Assert
        reaped.Should().BeGreaterThan(0);
        (await service.GetUserPresenceByIdAsync(101)).Should().BeNull();
        (await service.GetUserPresenceByIdAsync(102)).Should().BeNull();
    }

    [Fact]
    public async Task ReapDeadNodeSessionsAsync_ReadsNodeSetAndReapsSessions()
    {
        // Arrange
        DistributedChatStateService service = CreateService();
        string deadNodeId = "dead-node-42";
        RedisValue[] nodeMembers =
        [
            (RedisValue)"Zombie1:201",
            (RedisValue)"Zombie2:202",
            (RedisValue)"Zombie3:203"
        ];

        _mockDatabase.Setup(d => d.SetMembersAsync(
                It.Is<RedisKey>(k => k.ToString() == DistributedChatStateService.GetNodeSessionsKey(deadNodeId)),
                It.IsAny<CommandFlags>()))
            .ReturnsAsync(nodeMembers);

        _mockDatabase.Setup(d => d.KeyDeleteAsync(
                It.Is<RedisKey>(k => k.ToString() == DistributedChatStateService.GetNodeSessionsKey(deadNodeId)),
                It.IsAny<CommandFlags>()))
            .ReturnsAsync(true);

        // Act
        int reaped = await service.ReapDeadNodeSessionsAsync(deadNodeId);

        // Assert
        reaped.Should().BeGreaterThan(0);
        _mockBatch.Verify(b => b.Execute(), Times.Once);
        _mockDatabase.Verify(d => d.KeyDeleteAsync(
            It.Is<RedisKey>(k => k.ToString() == DistributedChatStateService.GetNodeSessionsKey(deadNodeId)),
            It.IsAny<CommandFlags>()), Times.Once);
    }
}
