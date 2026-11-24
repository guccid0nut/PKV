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
using NEXUS.Contracts.Data;
using StackExchange.Redis;
using TRANSMUTANSTEIN.ChatServer.Domain.Core;
using TRANSMUTANSTEIN.ChatServer.Domain.Matchmaking;
using TRANSMUTANSTEIN.ChatServer.Infrastructure.Services;
using TRANSMUTANSTEIN.ChatServer.Internals;
using TRANSMUTANSTEIN.ChatServer.Services;
using TRANSMUTANSTEIN.ChatServer.Utilities;
using Xunit;
using DbRole = MERRICK.DatabaseContext.Entities.Utility.Role;

namespace TRANSMUTANSTEIN.ChatServer.Tests.Services;

public sealed class PlayerStatusHydrationSubscriberTests
{
    private readonly Mock<IConnectionMultiplexer> _mockRedis = new();
    private readonly Mock<ISubscriber> _mockSubscriber = new();
    private readonly Mock<IChatContext> _mockChatContext = new();
    private readonly Mock<IDiscordPresencePublisherService> _mockDiscord = new();
    private readonly Mock<IMatchmakingService> _mockMatchmaking = new();
    private readonly Mock<IAccountRepository> _mockAccountRepo = new();
    private readonly ConcurrentDictionary<string, ChatSession> _clientSessions = new();
    private readonly TCPServer _tcpServer;
    private readonly IServiceProvider _serviceProvider;

    public PlayerStatusHydrationSubscriberTests()
    {
        Log.Initialise(NullLogger.Instance);

        _mockRedis.Setup(r => r.GetSubscriber(It.IsAny<object>())).Returns(_mockSubscriber.Object);
        _mockChatContext.SetupGet(c => c.ClientChatSessions).Returns(_clientSessions);

        ServiceCollection services = new();
        services.AddSingleton(_mockChatContext.Object);
        services.AddSingleton(_mockDiscord.Object);
        services.AddSingleton(_mockMatchmaking.Object);
        services.AddSingleton(_mockAccountRepo.Object);
        services.AddSingleton<ILogger<ChatSession>>(NullLogger<ChatSession>.Instance);
        _serviceProvider = services.BuildServiceProvider();

        _tcpServer = new TCPServer(IPAddress.Loopback, 0);
    }

    private sealed class MockChatSession : ChatSession
    {
        public List<byte[]> SentPackets { get; } = [];

        public MockChatSession(TCPServer server, IServiceProvider serviceProvider)
            : base(server, serviceProvider)
        {
        }

        public override bool SendAsync(ReadOnlySpan<byte> buffer)
        {
            SentPackets.Add(buffer.ToArray());
            return true;
        }
    }

    private static Account CreateAccount(int id, string name)
    {
        return new Account
        {
            ID = id,
            Name = name,
            User = new User
            {
                ID = id,
                EmailAddress = $"{name.ToLowerInvariant()}@kongor.net",
                Role = new DbRole { ID = 2, Name = "User" },
                SRPPasswordSalt = "salt",
                SRPPasswordHash = "hash",
                PBKDF2PasswordHash = "hash"
            }
        };
    }

    private static ClientChatSessionMetadata CreateMetadata(string? matchServerAddress = null, string? matchName = null, int? matchId = null)
    {
        return new ClientChatSessionMetadata
        {
            RemoteIP = "127.0.0.1",
            SessionCookie = "cookie123",
            SessionAuthenticationHash = "hash123",
            ChatProtocolVersion = 68,
            OperatingSystemIdentifier = 1,
            OperatingSystemVersionMajor = 10,
            OperatingSystemVersionMinor = 0,
            OperatingSystemVersionPatch = 0,
            OperatingSystemBuildCode = "Windows",
            OperatingSystemArchitecture = "x64",
            ClientVersionMajor = 4,
            ClientVersionMinor = 10,
            ClientVersionPatch = 1,
            ClientVersionRevision = 0,
            LastKnownClientState = ChatProtocol.ChatClientStatus.CHAT_CLIENT_STATUS_IN_GAME,
            ClientChatModeState = ChatProtocol.ChatModeType.CHAT_MODE_AVAILABLE,
            ClientRegion = "US",
            ClientLanguage = "en",
            MatchServerAddress = matchServerAddress,
            MatchName = matchName,
            MatchID = matchId
        };
    }

    [Fact]
    public async Task ProcessHydration_WhenSpectatorOnline_SendsUpdateStatusPacket()
    {
        // Arrange
        MockChatSession spectatorSession = new(_tcpServer, _serviceProvider)
        {
            Account = CreateAccount(50, "SpectatorUser")
        };
        _clientSessions["SpectatorUser"] = spectatorSession;

        PlayerStatusHydrationSubscriber subscriber = new(
            _mockRedis.Object,
            NullLogger<PlayerStatusHydrationSubscriber>.Instance,
            _serviceProvider,
            _mockChatContext.Object);

        // Act
        await subscriber.ProcessHydrationAsync(
            spectatorAccountId: 50,
            starPlayerAccountId: 100,
            matchId: 777,
            serverAddress: "192.168.1.150:11235",
            matchName: "Grand Championship");

        // Assert
        spectatorSession.SentPackets.Should().HaveCount(1);
        byte[] rawPacket = spectatorSession.SentPackets[0];
        rawPacket.Length.Should().BeGreaterThan(2);

        // Parse wire format (skip 2-byte packet length prefix)
        ChatBuffer reader = new(rawPacket.AsSpan(2));
        ushort command = reader.ReadCommand();
        command.Should().Be((ushort)ChatProtocol.Command.CHAT_CMD_UPDATE_STATUS);

        int accountId = reader.ReadInt32();
        accountId.Should().Be(100);

        byte status = reader.ReadInt8();
        status.Should().Be((byte)ChatProtocol.ChatClientStatus.CHAT_CLIENT_STATUS_IN_GAME);

        byte flags = reader.ReadInt8();
        int clanId = reader.ReadInt32();
        string clanName = reader.ReadString();
        string chatSymbol = reader.ReadString();
        string nameColour = reader.ReadString();
        string icon = reader.ReadString();

        string parsedServerAddress = reader.ReadString();
        parsedServerAddress.Should().Be("192.168.1.150:11235");

        string parsedMatchName = reader.ReadString();
        parsedMatchName.Should().Be("Grand Championship");

        int parsedMatchId = reader.ReadInt32();
        parsedMatchId.Should().Be(777);
    }

    [Fact]
    public async Task ProcessHydration_WhenSpectatorOffline_DoesNotThrow()
    {
        // Arrange
        PlayerStatusHydrationSubscriber subscriber = new(
            _mockRedis.Object,
            NullLogger<PlayerStatusHydrationSubscriber>.Instance,
            _serviceProvider,
            _mockChatContext.Object);

        // Act
        Func<Task> act = async () => await subscriber.ProcessHydrationAsync(
            spectatorAccountId: 999,
            starPlayerAccountId: 100,
            matchId: 777,
            serverAddress: "127.0.0.1:11235",
            matchName: "Solo Match");

        // Assert
        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task ProcessHydration_WhenStarPlayerOnline_PrefersLiveMetadataOverFallback()
    {
        // Arrange
        MockChatSession spectatorSession = new(_tcpServer, _serviceProvider)
        {
            Account = CreateAccount(50, "SpectatorUser")
        };
        _clientSessions["SpectatorUser"] = spectatorSession;

        MockChatSession starSession = new(_tcpServer, _serviceProvider)
        {
            Account = CreateAccount(100, "StarUser"),
            ClientMetadata = CreateMetadata(
                matchServerAddress: "10.0.0.99:11236",
                matchName: "Live Live Finals",
                matchId: 888)
        };
        _clientSessions["StarUser"] = starSession;

        PlayerStatusHydrationSubscriber subscriber = new(
            _mockRedis.Object,
            NullLogger<PlayerStatusHydrationSubscriber>.Instance,
            _serviceProvider,
            _mockChatContext.Object);

        // Act - provide fallback values that should be overridden by starSession.ClientMetadata
        await subscriber.ProcessHydrationAsync(
            spectatorAccountId: 50,
            starPlayerAccountId: 100,
            matchId: 777,
            serverAddress: "127.0.0.1:11235",
            matchName: "Fallback Match");

        // Assert
        spectatorSession.SentPackets.Should().HaveCount(1);
        ChatBuffer reader = new(spectatorSession.SentPackets[0].AsSpan(2));
        reader.ReadCommand().Should().Be((ushort)ChatProtocol.Command.CHAT_CMD_UPDATE_STATUS);
        reader.ReadInt32().Should().Be(100);
        reader.ReadInt8(); // status
        reader.ReadInt8(); // flags
        reader.ReadInt32(); // clanId
        reader.ReadString(); // clanName
        reader.ReadString(); // chatSymbol
        reader.ReadString(); // nameColour
        reader.ReadString(); // icon

        string parsedServerAddress = reader.ReadString();
        parsedServerAddress.Should().Be("10.0.0.99:11236");

        string parsedMatchName = reader.ReadString();
        parsedMatchName.Should().Be("Live Live Finals");

        int parsedMatchId = reader.ReadInt32();
        parsedMatchId.Should().Be(888);
    }

    [Fact]
    public async Task StartAsync_SubscribesToRedisChannel_AndDispatchesIncomingMessage()
    {
        // Arrange
        MockChatSession spectatorSession = new(_tcpServer, _serviceProvider)
        {
            Account = CreateAccount(50, "SpectatorUser")
        };
        _clientSessions["SpectatorUser"] = spectatorSession;

        Action<RedisChannel, RedisValue>? capturedCallback = null;

        _mockSubscriber.Setup(s => s.SubscribeAsync(
                RedisChannel.Literal(PlayerStatusHydrationSubscriber.ChannelName),
                It.IsAny<Action<RedisChannel, RedisValue>>(),
                It.IsAny<CommandFlags>()))
            .Callback<RedisChannel, Action<RedisChannel, RedisValue>, CommandFlags>((ch, cb, flags) =>
            {
                capturedCallback = cb;
            })
            .Returns(Task.CompletedTask);

        PlayerStatusHydrationSubscriber subscriber = new(
            _mockRedis.Object,
            NullLogger<PlayerStatusHydrationSubscriber>.Instance,
            _serviceProvider,
            _mockChatContext.Object);

        // Act
        await subscriber.StartAsync(CancellationToken.None);

        capturedCallback.Should().NotBeNull();

        // Simulate inbound Redis event from Master Server
        string jsonEvent = """
        {
            "spectator_account_id": 50,
            "star_player_account_id": 100,
            "match_id": 999,
            "server_address": "192.168.1.200:11235",
            "match_name": "Ranked Caldavar"
        }
        """;

        capturedCallback!(RedisChannel.Literal(PlayerStatusHydrationSubscriber.ChannelName), jsonEvent);

        // Assert
        spectatorSession.SentPackets.Should().HaveCount(1);
        ChatBuffer reader = new(spectatorSession.SentPackets[0].AsSpan(2));
        reader.ReadCommand().Should().Be((ushort)ChatProtocol.Command.CHAT_CMD_UPDATE_STATUS);
        reader.ReadInt32().Should().Be(100);
        reader.ReadInt8(); // status
        reader.ReadInt8(); // flags
        reader.ReadInt32(); // clanId
        reader.ReadString(); // clanName
        reader.ReadString(); // chatSymbol
        reader.ReadString(); // nameColour
        reader.ReadString(); // icon
        reader.ReadString().Should().Be("192.168.1.200:11235");
        reader.ReadString().Should().Be("Ranked Caldavar");
        reader.ReadInt32().Should().Be(999);
    }

    [Fact]
    public async Task PlayerStatusHydrationSubscriber_StopAsync_UnsubscribesFromRedisChannel()
    {
        // Arrange
        _mockSubscriber.Setup(s => s.SubscribeAsync(
                RedisChannel.Literal(PlayerStatusHydrationSubscriber.ChannelName),
                It.IsAny<Action<RedisChannel, RedisValue>>(),
                It.IsAny<CommandFlags>()))
            .Returns(Task.CompletedTask);

        _mockSubscriber.Setup(s => s.UnsubscribeAsync(
                RedisChannel.Literal(PlayerStatusHydrationSubscriber.ChannelName),
                It.IsAny<Action<RedisChannel, RedisValue>>(),
                It.IsAny<CommandFlags>()))
            .Returns(Task.CompletedTask);

        PlayerStatusHydrationSubscriber subscriber = new(
            _mockRedis.Object,
            NullLogger<PlayerStatusHydrationSubscriber>.Instance,
            _serviceProvider,
            _mockChatContext.Object);

        // Act
        await subscriber.StartAsync(CancellationToken.None);
        await subscriber.StopAsync(CancellationToken.None);

        // Assert
        _mockSubscriber.Verify(s => s.UnsubscribeAsync(
            RedisChannel.Literal(PlayerStatusHydrationSubscriber.ChannelName),
            It.IsAny<Action<RedisChannel, RedisValue>>(),
            It.IsAny<CommandFlags>()), Times.Once);
    }

    [Fact]
    public async Task OnMessageReceived_WithMalformedJson_DoesNotThrow()
    {
        // Arrange
        Action<RedisChannel, RedisValue>? capturedCallback = null;

        _mockSubscriber.Setup(s => s.SubscribeAsync(
                RedisChannel.Literal(PlayerStatusHydrationSubscriber.ChannelName),
                It.IsAny<Action<RedisChannel, RedisValue>>(),
                It.IsAny<CommandFlags>()))
            .Callback<RedisChannel, Action<RedisChannel, RedisValue>, CommandFlags>((ch, cb, flags) =>
            {
                capturedCallback = cb;
            })
            .Returns(Task.CompletedTask);

        PlayerStatusHydrationSubscriber subscriber = new(
            _mockRedis.Object,
            NullLogger<PlayerStatusHydrationSubscriber>.Instance,
            _serviceProvider,
            _mockChatContext.Object);

        await subscriber.StartAsync(CancellationToken.None);
        capturedCallback.Should().NotBeNull();

        // Act
        Action act = () => capturedCallback!(RedisChannel.Literal(PlayerStatusHydrationSubscriber.ChannelName), "not valid json {[[[");

        // Assert
        act.Should().NotThrow();
    }

    [Fact]
    public async Task OnMessageReceived_WithStarPlayerName_ResolvesStarPlayerSessionByName_WhenIdIsZero()
    {
        // Arrange
        MockChatSession spectatorSession = new(_tcpServer, _serviceProvider)
        {
            Account = CreateAccount(50, "SpectatorUser")
        };
        _clientSessions["SpectatorUser"] = spectatorSession;

        MockChatSession starSession = new(_tcpServer, _serviceProvider)
        {
            Account = CreateAccount(105, "StarNamedUser")
        };
        _clientSessions["StarNamedUser"] = starSession;

        Action<RedisChannel, RedisValue>? capturedCallback = null;

        _mockSubscriber.Setup(s => s.SubscribeAsync(
                RedisChannel.Literal(PlayerStatusHydrationSubscriber.ChannelName),
                It.IsAny<Action<RedisChannel, RedisValue>>(),
                It.IsAny<CommandFlags>()))
            .Callback<RedisChannel, Action<RedisChannel, RedisValue>, CommandFlags>((ch, cb, flags) =>
            {
                capturedCallback = cb;
            })
            .Returns(Task.CompletedTask);

        PlayerStatusHydrationSubscriber subscriber = new(
            _mockRedis.Object,
            NullLogger<PlayerStatusHydrationSubscriber>.Instance,
            _serviceProvider,
            _mockChatContext.Object);

        await subscriber.StartAsync(CancellationToken.None);

        string jsonEvent = """
        {
            "spectator_account_id": 50,
            "star_player_account_id": 0,
            "star_player_name": "StarNamedUser",
            "match_id": 777,
            "server_address": "10.0.0.1:11235",
            "match_name": "Ranked Match"
        }
        """;

        // Act
        capturedCallback!(RedisChannel.Literal(PlayerStatusHydrationSubscriber.ChannelName), jsonEvent);

        // Assert
        spectatorSession.SentPackets.Should().HaveCount(1);
        ChatBuffer reader = new(spectatorSession.SentPackets[0].AsSpan(2));
        reader.ReadCommand().Should().Be((ushort)ChatProtocol.Command.CHAT_CMD_UPDATE_STATUS);
        reader.ReadInt32().Should().Be(105); // Resolved from StarNamedUser session!
        reader.ReadInt8(); // status
        reader.ReadInt8(); // flags
        reader.ReadInt32(); // clanId
        reader.ReadString(); // clanName
        reader.ReadString(); // chatSymbol
        reader.ReadString(); // nameColour
        reader.ReadString(); // icon
        reader.ReadString().Should().Be("10.0.0.1:11235");
        reader.ReadString().Should().Be("Ranked Match");
        reader.ReadInt32().Should().Be(777);
    }

    [Fact]
    public async Task OnMessageReceived_WithSpectatorAccountName_ResolvesSpectatorSessionByName_WhenIdIsZero()
    {
        // Arrange
        MockChatSession spectatorSession = new(_tcpServer, _serviceProvider)
        {
            Account = CreateAccount(88, "NamedSpectator")
        };
        _clientSessions["NamedSpectator"] = spectatorSession;

        Action<RedisChannel, RedisValue>? capturedCallback = null;

        _mockSubscriber.Setup(s => s.SubscribeAsync(
                RedisChannel.Literal(PlayerStatusHydrationSubscriber.ChannelName),
                It.IsAny<Action<RedisChannel, RedisValue>>(),
                It.IsAny<CommandFlags>()))
            .Callback<RedisChannel, Action<RedisChannel, RedisValue>, CommandFlags>((ch, cb, flags) =>
            {
                capturedCallback = cb;
            })
            .Returns(Task.CompletedTask);

        PlayerStatusHydrationSubscriber subscriber = new(
            _mockRedis.Object,
            NullLogger<PlayerStatusHydrationSubscriber>.Instance,
            _serviceProvider,
            _mockChatContext.Object);

        await subscriber.StartAsync(CancellationToken.None);

        string jsonEvent = """
        {
            "spectator_account_id": 0,
            "spectator_account_name": "NamedSpectator",
            "star_player_account_id": 200,
            "match_id": 888,
            "server_address": "10.0.0.2:11235",
            "match_name": "Midwars Match"
        }
        """;

        // Act
        capturedCallback!(RedisChannel.Literal(PlayerStatusHydrationSubscriber.ChannelName), jsonEvent);

        // Assert
        spectatorSession.SentPackets.Should().HaveCount(1);
        ChatBuffer reader = new(spectatorSession.SentPackets[0].AsSpan(2));
        reader.ReadCommand().Should().Be((ushort)ChatProtocol.Command.CHAT_CMD_UPDATE_STATUS);
        reader.ReadInt32().Should().Be(200);
    }

    [Fact]
    public async Task ProcessHydrationAsync_WhenSpectatorOnline_ResolvesStarAccountViaAsyncRepository()
    {
        // Arrange
        MockChatSession spectatorSession = new(_tcpServer, _serviceProvider)
        {
            Account = CreateAccount(50, "SpectatorUser")
        };
        _clientSessions["SpectatorUser"] = spectatorSession;

        Account starAccountDb = CreateAccount(100, "DbStarPlayer");
        _mockAccountRepo.Setup(r => r.GetByIdAsync(100)).ReturnsAsync(starAccountDb);

        PlayerStatusHydrationSubscriber subscriber = new(
            _mockRedis.Object,
            NullLogger<PlayerStatusHydrationSubscriber>.Instance,
            _serviceProvider,
            _mockChatContext.Object);

        // Act
        await subscriber.ProcessHydrationAsync(
            spectatorAccountId: 50,
            starPlayerAccountId: 100,
            matchId: 777,
            serverAddress: "192.168.1.150:11235",
            matchName: "Grand Championship");

        // Assert
        spectatorSession.SentPackets.Should().HaveCount(1);
        ChatBuffer reader = new(spectatorSession.SentPackets[0].AsSpan(2));
        reader.ReadCommand().Should().Be((ushort)ChatProtocol.Command.CHAT_CMD_UPDATE_STATUS);
        reader.ReadInt32().Should().Be(100);
        _mockAccountRepo.Verify(r => r.GetByIdAsync(100), Times.Once);
    }
}
