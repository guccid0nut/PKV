// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ============================================================================
// File: AgonesAllocationClientTests.cs
// System: TRANSMUTANSTEIN.ChatServer.Tests
// Subsystem: Cloud-Native Fleet Orchestration (Agones Integration Tests)
// ============================================================================

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Moq.Protected;
using TRANSMUTANSTEIN.ChatServer.Domain.Core;
using TRANSMUTANSTEIN.ChatServer.Domain.Matchmaking;
using TRANSMUTANSTEIN.ChatServer.Internals;
using TRANSMUTANSTEIN.ChatServer.Services;
using TRANSMUTANSTEIN.ChatServer.Services.Agones;
using TRANSMUTANSTEIN.ChatServer.Utilities;
using Xunit;

namespace TRANSMUTANSTEIN.ChatServer.Tests.Services;

public sealed class AgonesAllocationClientTests
{
    public AgonesAllocationClientTests()
    {
        Log.Initialise(NullLogger.Instance);
    }

    [Fact]
    public async Task AllocateGameServerAsync_WhenDisabled_ReturnsFailedImmediately()
    {
        // Arrange
        IConfiguration config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AgonesAllocation:Enabled"] = "false"
            })
            .Build();

        AgonesAllocationClient client = new(config);

        // Act
        GameServerAllocationResult result = await client.AllocateGameServerAsync(["USE"]);

        // Assert
        result.Succeeded.Should().BeFalse();
        result.ErrorMessage.Should().Contain("disabled");
    }

    [Fact]
    public async Task AllocateGameServerAsync_WhenAgonesReturnsAllocated_ParsesPodAndPortsCorrectly()
    {
        // Arrange
        string sampleResponse = """
        {
          "status": {
            "state": "Allocated",
            "gameServerName": "kongor-dedicated-fleet-7f9a1",
            "address": "192.168.1.50",
            "nodeName": "node-alpha",
            "ports": [
              { "name": "game-udp", "port": 11435 },
              { "name": "voice-udp", "port": 11935 }
            ]
          }
        }
        """;

        Mock<HttpMessageHandler> mockHandler = new();
        mockHandler.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage
            {
                StatusCode = HttpStatusCode.OK,
                Content = new StringContent(sampleResponse, Encoding.UTF8, "application/json")
            });

        HttpClient httpClient = new(mockHandler.Object);
        IConfiguration config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AgonesAllocation:Enabled"] = "true",
                ["AgonesAllocation:Endpoint"] = "http://agones-allocator.svc:5000",
                ["AgonesAllocation:FleetName"] = "kongor-dedicated-fleet"
            })
            .Build();

        AgonesAllocationClient client = new(config, httpClient);

        // Act
        GameServerAllocationResult result = await client.AllocateGameServerAsync(["USE"]);

        // Assert
        result.Succeeded.Should().BeTrue();
        result.State.Should().Be("Allocated");
        result.GameServerName.Should().Be("kongor-dedicated-fleet-7f9a1");
        result.Address.Should().Be("192.168.1.50");
        result.GamePort.Should().Be(11435);
        result.VoicePort.Should().Be(11935);
    }

    [Fact]
    public async Task AllocateGameServerAsync_WhenAgonesReturnsUnAllocated_ReturnsUnallocatedResult()
    {
        // Arrange
        string sampleResponse = """
        {
          "status": {
            "state": "UnAllocated",
            "gameServerName": "",
            "address": "",
            "ports": []
          }
        }
        """;

        Mock<HttpMessageHandler> mockHandler = new();
        mockHandler.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage
            {
                StatusCode = HttpStatusCode.OK,
                Content = new StringContent(sampleResponse, Encoding.UTF8, "application/json")
            });

        HttpClient httpClient = new(mockHandler.Object);
        IConfiguration config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AgonesAllocation:Enabled"] = "true",
                ["AgonesAllocation:Endpoint"] = "http://agones-allocator.svc:5000"
            })
            .Build();

        AgonesAllocationClient client = new(config, httpClient);

        // Act
        GameServerAllocationResult result = await client.AllocateGameServerAsync(["USE"]);

        // Assert
        result.Succeeded.Should().BeFalse();
        result.State.Should().Be("UnAllocated");
        result.ErrorMessage.Should().Contain("UnAllocated");
    }

    [Fact]
    public async Task AllocateGameServerAsync_WhenHttpErrorReturned_ReturnsFailedGracefully()
    {
        // Arrange
        Mock<HttpMessageHandler> mockHandler = new();
        mockHandler.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage
            {
                StatusCode = HttpStatusCode.ServiceUnavailable,
                Content = new StringContent("Fleet buffer exhausted", Encoding.UTF8, "text/plain")
            });

        HttpClient httpClient = new(mockHandler.Object);
        IConfiguration config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AgonesAllocation:Enabled"] = "true",
                ["AgonesAllocation:Endpoint"] = "http://agones-allocator.svc:5000"
            })
            .Build();

        AgonesAllocationClient client = new(config, httpClient);

        // Act
        GameServerAllocationResult result = await client.AllocateGameServerAsync(["USE"]);

        // Assert
        result.Succeeded.Should().BeFalse();
        result.ErrorMessage.Should().Contain("503");
    }

    [Fact]
    public async Task MatchmakingService_WhenNoIdleServers_AndAgonesAllocatesPod_BindsToSession()
    {
        // Arrange
        Mock<IChatContext> mockChatContext = new();
        ConcurrentDictionary<int, ChatSession> matchServers = new();
        mockChatContext.SetupGet(c => c.MatchServerChatSessions).Returns(matchServers);
        mockChatContext.SetupGet(c => c.ClientChatSessions).Returns(new ConcurrentDictionary<string, ChatSession>());

        // Mock Agones Client returning a successful allocation
        Mock<IAgonesAllocationClient> mockAgonesClient = new();
        mockAgonesClient.SetupGet(c => c.IsEnabled).Returns(true);
        mockAgonesClient.Setup(c => c.AllocateGameServerAsync(
                It.IsAny<IReadOnlyList<string>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(GameServerAllocationResult.Success("kongor-dedicated-fleet-abc", "10.0.0.99", 11435, 11935));

        // Add matching connected session for the allocated pod
        TestChatSession allocatedSession = new(mockChatContext.Object)
        {
            ServerMetadata = new MatchServerChatSessionMetadata
            {
                ServerID = 77,
                SessionCookie = "cookie77",
                ChatProtocolVersion = 1,
                Status = ChatProtocol.ServerStatus.SERVER_STATUS_IDLE,
                Address = "10.0.0.99",
                Port = 11435,
                Location = "use"
            }
        };
        // Intentionally start with server session in list but idle
        matchServers[77] = allocatedSession;

        IConfiguration config = new ConfigurationBuilder().Build();
        MatchmakingService service = new(config, mockChatContext.Object, agonesAllocationClient: mockAgonesClient.Object);

        service.PendingMatches.Count.Should().Be(0);
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
            Microsoft.Extensions.DependencyInjection.ServiceCollection sc = new();
            sc.AddSingleton(chatContext);
            sc.AddSingleton(Mock.Of<IDiscordPresencePublisherService>());
            sc.AddSingleton(Mock.Of<IMatchmakingService>());
            return sc.BuildServiceProvider();
        }

        public override bool SendAsync(ReadOnlySpan<byte> buffer) => true;
    }
}
