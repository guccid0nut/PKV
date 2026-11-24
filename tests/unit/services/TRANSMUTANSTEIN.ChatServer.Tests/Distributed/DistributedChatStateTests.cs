// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Collections.Concurrent;
using System.Net;
using FluentAssertions;
using MERRICK.DatabaseContext.Entities.Core;
using MERRICK.DatabaseContext.Enumerations;
using MemoryPack;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NEXUS.Contracts.Chat;
using StackExchange.Redis;
using TRANSMUTANSTEIN.ChatServer.CommandProcessors.Channels;
using TRANSMUTANSTEIN.ChatServer.CommandProcessors.Clans;
using TRANSMUTANSTEIN.ChatServer.CommandProcessors.Communication;
using TRANSMUTANSTEIN.ChatServer.CommandProcessors.Social;
using TRANSMUTANSTEIN.ChatServer.Domain.Clans;
using TRANSMUTANSTEIN.ChatServer.Domain.Communication;
using TRANSMUTANSTEIN.ChatServer.Domain.Core;
using TRANSMUTANSTEIN.ChatServer.Domain.Matchmaking;
using TRANSMUTANSTEIN.ChatServer.Infrastructure.Distributed;
using TRANSMUTANSTEIN.ChatServer.Internals;
using TRANSMUTANSTEIN.ChatServer.Services;
using TRANSMUTANSTEIN.ChatServer.Utilities;
using Xunit;
using DbRole = MERRICK.DatabaseContext.Entities.Utility.Role;

namespace TRANSMUTANSTEIN.ChatServer.Tests.Distributed;

public sealed class DistributedChatStateTests
{
    private readonly Mock<IConnectionMultiplexer> _mockRedis = new();
    private readonly Mock<ISubscriber> _mockSubscriber = new();
    private readonly Mock<IDatabase> _mockDatabase = new();
    private readonly Mock<IChatContext> _mockChatContext = new();
    private readonly Mock<IDiscordPresencePublisherService> _mockDiscord = new();
    private readonly Mock<IMatchmakingService> _mockMatchmaking = new();
    private readonly ConcurrentDictionary<string, ChatSession> _clientSessions = new();
    private readonly ConcurrentDictionary<string, ChatChannel> _chatChannels = new();
    private readonly ConcurrentDictionary<int, ChatChannel> _chatChannelsById = new();
    private readonly TCPServer _tcpServer;
    private readonly IServiceProvider _serviceProvider;

    public DistributedChatStateTests()
    {
        Log.Initialise(NullLogger.Instance);

        _mockRedis.Setup(r => r.GetSubscriber(It.IsAny<object>())).Returns(_mockSubscriber.Object);
        _mockRedis.Setup(r => r.GetDatabase(It.IsAny<int>(), It.IsAny<object>())).Returns(_mockDatabase.Object);
        _mockRedis.SetupGet(r => r.IsConnected).Returns(true);

        _mockChatContext.SetupGet(c => c.ClientChatSessions).Returns(_clientSessions);
        _mockChatContext.SetupGet(c => c.ChatChannels).Returns(_chatChannels);
        _mockChatContext.SetupGet(c => c.ChatChannelsByID).Returns(_chatChannelsById);

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
            redis);
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
                Role = new DbRole { ID = 2, Name = "User" },
                SRPPasswordSalt = "salt",
                SRPPasswordHash = "hash",
                PBKDF2PasswordHash = "hash"
            }
        };
    }

    private static ClientChatSessionMetadata CreateMetadata(
        ChatProtocol.ChatClientStatus status = ChatProtocol.ChatClientStatus.CHAT_CLIENT_STATUS_CONNECTED,
        ChatProtocol.ChatModeType mode = ChatProtocol.ChatModeType.CHAT_MODE_AVAILABLE)
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
            LastKnownClientState = status,
            ClientChatModeState = mode,
            ClientRegion = "US",
            ClientLanguage = "en"
        };
    }

    [Fact]
    public void Standalone_WhenNoRedis_IsDistributedIsFalse()
    {
        // Arrange & Act
        DistributedChatStateService service = CreateService(redis: null);

        // Assert
        service.IsDistributed.Should().BeFalse();
        service.NodeId.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task Standalone_RegisterAndUnregisterSession_MaintainsLocalPresence()
    {
        // Arrange
        DistributedChatStateService service = CreateService(redis: null);

        MockChatSession session = new(_tcpServer, _serviceProvider)
        {
            Account = CreateAccount(100, "Alice"),
            ClientMetadata = CreateMetadata()
        };

        // Act - Register
        await service.RegisterSessionAsync(session);

        // Assert - Found locally
        DistributedUserPresence? presenceByName = await service.GetUserPresenceAsync("Alice");
        presenceByName.Should().NotBeNull();
        presenceByName!.AccountID.Should().Be(100);
        presenceByName.AccountName.Should().Be("Alice");

        DistributedUserPresence? presenceById = await service.GetUserPresenceByIdAsync(100);
        presenceById.Should().NotBeNull();
        presenceById!.AccountID.Should().Be(100);

        // Act - Update Presence
        await service.UpdatePresenceAsync(100, "Alice",
            ChatProtocol.ChatClientStatus.CHAT_CLIENT_STATUS_IN_GAME,
            ChatProtocol.ChatModeType.CHAT_MODE_AVAILABLE,
            matchAddress: "127.0.0.1:11235",
            matchId: 42);

        DistributedUserPresence? updated = await service.GetUserPresenceAsync("Alice");
        updated.Should().NotBeNull();
        updated!.ClientStatus.Should().Be((byte)ChatProtocol.ChatClientStatus.CHAT_CLIENT_STATUS_IN_GAME);
        updated.MatchAddress.Should().Be("127.0.0.1:11235");
        updated.MatchID.Should().Be(42);

        // Act - Unregister
        await service.UnregisterSessionAsync("Alice", 100);

        DistributedUserPresence? removed = await service.GetUserPresenceAsync("Alice");
        removed.Should().BeNull();
    }

    [Fact]
    public async Task Standalone_RouteWhisperAsync_WhenLocalRecipient_SendsWhisperPacket()
    {
        // Arrange
        DistributedChatStateService service = CreateService(redis: null);

        MockChatSession bobSession = new(_tcpServer, _serviceProvider)
        {
            Account = CreateAccount(200, "Bob"),
            ClientMetadata = CreateMetadata()
        };
        _clientSessions["Bob"] = bobSession;

        // Act
        bool result = await service.RouteWhisperAsync("Alice", 100, "Bob", "Hello Bob!");

        // Assert
        result.Should().BeTrue();
        bobSession.SentPackets.Should().HaveCount(1);

        ChatBuffer reader = new(bobSession.SentPackets[0].AsSpan(2));
        ushort cmd = reader.ReadCommand();
        cmd.Should().Be((ushort)ChatProtocol.Command.CHAT_CMD_WHISPER);
        string sender = reader.ReadString();
        sender.Should().Be("Alice");
        string msg = reader.ReadString();
        msg.Should().Be("Hello Bob!");
    }

    [Fact]
    public async Task Standalone_RouteWhisperAsync_WhenRecipientDND_SendsAutoReply()
    {
        // Arrange
        DistributedChatStateService service = CreateService(redis: null);

        MockChatSession aliceSession = new(_tcpServer, _serviceProvider)
        {
            Account = CreateAccount(100, "Alice"),
            ClientMetadata = CreateMetadata()
        };
        _clientSessions["Alice"] = aliceSession;

        MockChatSession charlieSession = new(_tcpServer, _serviceProvider)
        {
            Account = CreateAccount(300, "Charlie"),
            ClientMetadata = CreateMetadata(mode: ChatProtocol.ChatModeType.CHAT_MODE_DND)
        };
        _clientSessions["Charlie"] = charlieSession;

        // Act
        bool result = await service.RouteWhisperAsync("Alice", 100, "Charlie", "Are you available?");

        // Assert
        result.Should().BeTrue();
        charlieSession.SentPackets.Should().BeEmpty(); // DND drops recipient delivery
        aliceSession.SentPackets.Should().HaveCount(1); // Sender gets auto-reply

        ChatBuffer reader = new(aliceSession.SentPackets[0].AsSpan(2));
        reader.ReadCommand().Should().Be((ushort)ChatProtocol.Command.CHAT_CMD_WHISPER);
        reader.ReadString().Should().Be("Charlie");
        reader.ReadString().Should().Be("[Do Not Disturb]");
    }

    [Fact]
    public async Task Standalone_RouteWhisperAsync_WhenRecipientAFK_DeliversAndSendsAutoReply()
    {
        // Arrange
        DistributedChatStateService service = CreateService(redis: null);

        MockChatSession aliceSession = new(_tcpServer, _serviceProvider)
        {
            Account = CreateAccount(100, "Alice"),
            ClientMetadata = CreateMetadata()
        };
        _clientSessions["Alice"] = aliceSession;

        MockChatSession davidSession = new(_tcpServer, _serviceProvider)
        {
            Account = CreateAccount(400, "David"),
            ClientMetadata = CreateMetadata(mode: ChatProtocol.ChatModeType.CHAT_MODE_AFK)
        };
        _clientSessions["David"] = davidSession;

        // Act
        bool result = await service.RouteWhisperAsync("Alice", 100, "David", "Hey David!");

        // Assert
        result.Should().BeTrue();
        davidSession.SentPackets.Should().HaveCount(1); // AFK delivers message
        aliceSession.SentPackets.Should().HaveCount(1); // and sends auto-reply to sender

        ChatBuffer recipientReader = new(davidSession.SentPackets[0].AsSpan(2));
        recipientReader.ReadCommand().Should().Be((ushort)ChatProtocol.Command.CHAT_CMD_WHISPER);
        recipientReader.ReadString().Should().Be("Alice");
        recipientReader.ReadString().Should().Be("Hey David!");

        ChatBuffer senderReader = new(aliceSession.SentPackets[0].AsSpan(2));
        senderReader.ReadCommand().Should().Be((ushort)ChatProtocol.Command.CHAT_CMD_WHISPER);
        senderReader.ReadString().Should().Be("David");
        senderReader.ReadString().Should().Be("[Away From Keyboard]");
    }

    [Fact]
    public async Task Standalone_RouteWhisperAsync_WhenRecipientOffline_ReturnsFalse()
    {
        // Arrange
        DistributedChatStateService service = CreateService(redis: null);

        // Act
        bool result = await service.RouteWhisperAsync("Alice", 100, "OfflineUser", "Hello?");

        // Assert
        result.Should().BeFalse();
    }

    [Fact]
    public async Task Distributed_StartAsync_SubscribesToChannelsAndRegistersNodeHeartbeat()
    {
        // Arrange
        DistributedChatStateService service = CreateService(redis: _mockRedis.Object);

        // Act
        await service.StartAsync(CancellationToken.None);

        // Assert - Verify baseline subscriptions
        _mockSubscriber.Verify(s => s.SubscribeAsync(
            It.Is<RedisChannel>(c => c.ToString() == $"kongor:chat:whispers:{service.NodeId}"),
            It.IsAny<Action<RedisChannel, RedisValue>>(),
            It.IsAny<CommandFlags>()), Times.Once);

        _mockSubscriber.Verify(s => s.SubscribeAsync(
            It.Is<RedisChannel>(c => c.ToString() == "kongor:chat:cluster:evict"),
            It.IsAny<Action<RedisChannel, RedisValue>>(),
            It.IsAny<CommandFlags>()), Times.Once);

        _mockSubscriber.Verify(s => s.SubscribeAsync(
            It.Is<RedisChannel>(c => c.ToString() == "account:peer_invalidation"),
            It.IsAny<Action<RedisChannel, RedisValue>>(),
            It.IsAny<CommandFlags>()), Times.Once);

        // Assert - Wildcard channel and clan patterns must NEVER be subscribed at startup
        _mockSubscriber.Verify(s => s.SubscribeAsync(
            It.Is<RedisChannel>(c => c.ToString() == "kongor:chat:chan:*"),
            It.IsAny<Action<RedisChannel, RedisValue>>(),
            It.IsAny<CommandFlags>()), Times.Never);

        _mockSubscriber.Verify(s => s.SubscribeAsync(
            It.Is<RedisChannel>(c => c.ToString() == "kongor:chat:clan:*"),
            It.IsAny<Action<RedisChannel, RedisValue>>(),
            It.IsAny<CommandFlags>()), Times.Never);

        // Assert - Verify heartbeat registration
        _mockDatabase.Verify(db => db.HashSetAsync(
            "kongor:chat:nodes",
            service.NodeId,
            It.IsAny<RedisValue>(),
            It.IsAny<When>(),
            It.IsAny<CommandFlags>()), Times.Once);

        await service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Distributed_RegisterSessionAsync_StoresPresenceInRedis()
    {
        // Arrange
        DistributedChatStateService service = CreateService(redis: _mockRedis.Object);

        MockChatSession session = new(_tcpServer, _serviceProvider)
        {
            Account = CreateAccount(100, "Alice"),
            ClientMetadata = CreateMetadata()
        };

        // Act
        await service.RegisterSessionAsync(session);

        // Assert - Presence stored in Redis hashes
        _mockDatabase.Verify(db => db.HashSetAsync(
            DistributedChatStateService.GetPresenceUsersKey("alice"),
            "alice",
            It.IsAny<RedisValue>(),
            It.IsAny<When>(),
            It.IsAny<CommandFlags>()), Times.Once);

        _mockDatabase.Verify(db => db.HashSetAsync(
            DistributedChatStateService.GetPresenceByIdKey(100),
            100,
            "alice",
            It.IsAny<When>(),
            It.IsAny<CommandFlags>()), Times.Once);
    }

    [Fact]
    public async Task Distributed_EvictConcurrentSessionAsync_PublishesToEvictChannel()
    {
        // Arrange
        DistributedChatStateService service = CreateService(redis: _mockRedis.Object);

        // Act
        await service.EvictConcurrentSessionAsync(100, "Alice", (byte)ChatProtocol.ChatRejectReason.ECR_ACCOUNT_SHARING);

        // Assert
        _mockSubscriber.Verify(s => s.PublishAsync(
            It.Is<RedisChannel>(c => c.ToString() == "kongor:chat:cluster:evict"),
            It.Is<RedisValue>(v => v.Length() > 0),
            It.IsAny<CommandFlags>()), Times.Once);
    }

    [Fact]
    public async Task Distributed_RouteWhisperAsync_WhenRemoteRecipient_PublishesToTargetNode()
    {
        // Arrange
        DistributedChatStateService service = CreateService(redis: _mockRedis.Object);

        DistributedUserPresence remoteBob = new()
        {
            AccountID = 200,
            AccountName = "Bob",
            NodeID = "remote-node-beta",
            ClientStatus = (byte)ChatProtocol.ChatClientStatus.CHAT_CLIENT_STATUS_CONNECTED,
            ChatMode = (byte)ChatProtocol.ChatModeType.CHAT_MODE_AVAILABLE,
            LastHeartbeatUtc = TimeProvider.System.GetUtcNow()
        };

        byte[] serializedBob = MemoryPackSerializer.Serialize(remoteBob);

        _mockDatabase
            .Setup(db => db.HashGetAsync(DistributedChatStateService.GetPresenceUsersKey("bob"), "bob", It.IsAny<CommandFlags>()))
            .ReturnsAsync(serializedBob);

        // Act
        bool result = await service.RouteWhisperAsync("Alice", 100, "Bob", "Hello remote Bob!");

        // Assert
        result.Should().BeTrue();

        _mockSubscriber.Verify(s => s.PublishAsync(
            It.Is<RedisChannel>(c => c.ToString() == "kongor:chat:whispers:remote-node-beta"),
            It.Is<RedisValue>(v => v.Length() > 0),
            It.IsAny<CommandFlags>()), Times.Once);
    }

    [Fact]
    public async Task Distributed_OnWhisperReceived_WhenInboundFromRemoteNode_DeliversToLocalSession()
    {
        // Arrange
        Action<RedisChannel, RedisValue>? whisperHandler = null;

        _mockSubscriber
            .Setup(s => s.SubscribeAsync(
                It.Is<RedisChannel>(c => c.ToString().StartsWith("kongor:chat:whispers:")),
                It.IsAny<Action<RedisChannel, RedisValue>>(),
                It.IsAny<CommandFlags>()))
            .Callback<RedisChannel, Action<RedisChannel, RedisValue>, CommandFlags>((_, handler, _) => whisperHandler = handler)
            .Returns(Task.CompletedTask);

        DistributedChatStateService service = CreateService(redis: _mockRedis.Object);

        await service.StartAsync(CancellationToken.None);

        MockChatSession aliceSession = new(_tcpServer, _serviceProvider)
        {
            Account = CreateAccount(100, "Alice"),
            ClientMetadata = CreateMetadata()
        };
        _clientSessions["Alice"] = aliceSession;

        DistributedWhisperEnvelope envelope = new()
        {
            SenderName = "RemoteBob",
            SenderAccountID = 200,
            TargetName = "Alice",
            Message = "Greetings across nodes!",
            OriginNodeID = "remote-node-beta",
            TimestampUtc = TimeProvider.System.GetUtcNow()
        };

        byte[] payload = MemoryPackSerializer.Serialize(envelope);

        // Act - Simulate incoming Redis pub/sub message
        whisperHandler.Should().NotBeNull();
        whisperHandler!(RedisChannel.Literal($"kongor:chat:whispers:{service.NodeId}"), payload);

        // Assert - Local session received the whisper packet
        aliceSession.SentPackets.Should().HaveCount(1);
        ChatBuffer reader = new(aliceSession.SentPackets[0].AsSpan(2));
        reader.ReadCommand().Should().Be((ushort)ChatProtocol.Command.CHAT_CMD_WHISPER);
        reader.ReadString().Should().Be("RemoteBob");
        reader.ReadString().Should().Be("Greetings across nodes!");

        await service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Distributed_PublishChannelMessageAsync_PublishesEnvelopeToRedis()
    {
        // Arrange
        DistributedChatStateService service = CreateService(redis: _mockRedis.Object);

        // Act
        await service.PublishChannelMessageAsync(
            channelId: 5,
            channelName: "General",
            senderId: 100,
            senderName: "Alice",
            message: "Hello cluster!");

        // Assert
        _mockSubscriber.Verify(s => s.PublishAsync(
            It.Is<RedisChannel>(c => c.ToString() == "kongor:chat:chan:general"),
            It.Is<RedisValue>(v => v.Length() > 0),
            It.IsAny<CommandFlags>()), Times.Once);
    }

    [Fact]
    public async Task Distributed_OnChannelMessageReceived_WhenFromRemoteNode_DeliversToLocalChannelMembers()
    {
        // Arrange
        Action<RedisChannel, RedisValue>? channelMsgHandler = null;

        _mockSubscriber
            .Setup(s => s.SubscribeAsync(
                It.Is<RedisChannel>(c => c.ToString() == "kongor:chat:chan:general"),
                It.IsAny<Action<RedisChannel, RedisValue>>(),
                It.IsAny<CommandFlags>()))
            .Callback<RedisChannel, Action<RedisChannel, RedisValue>, CommandFlags>((_, handler, _) => channelMsgHandler = handler)
            .Returns(Task.CompletedTask);

        DistributedChatStateService service = CreateService(redis: _mockRedis.Object);

        await service.StartAsync(CancellationToken.None);

        MockChatSession aliceSession = new(_tcpServer, _serviceProvider)
        {
            Account = CreateAccount(100, "Alice"),
            ClientMetadata = CreateMetadata()
        };

        ChatChannel channel = ChatChannel.GetOrCreate(_mockChatContext.Object, aliceSession, "General");
        channel.TryAddMember("Alice", new ChatChannelMember(aliceSession, channel));
        await service.JoinChannelAsync("General", 100, "Alice", 0);

        DistributedChannelMessageEnvelope envelope = new()
        {
            ChannelID = channel.ID,
            ChannelName = "General",
            SenderAccountID = 200,
            SenderName = "RemoteBob",
            Message = "Broadcast to all nodes",
            OriginNodeID = "different-node-123",
            TimestampUtc = TimeProvider.System.GetUtcNow()
        };

        byte[] payload = MemoryPackSerializer.Serialize(envelope);

        // Act - Simulate incoming targeted multicast
        channelMsgHandler.Should().NotBeNull();
        channelMsgHandler!(RedisChannel.Literal("kongor:chat:chan:general"), payload);

        // Assert - Alice received the channel message packet
        aliceSession.SentPackets.Should().HaveCount(1);
        ChatBuffer reader = new(aliceSession.SentPackets[0].AsSpan(2));
        reader.ReadCommand().Should().Be((ushort)ChatProtocol.Command.CHAT_CMD_CHANNEL_MSG);
        int senderId = reader.ReadInt32();
        senderId.Should().Be(200);
        int chanId = reader.ReadInt32();
        chanId.Should().Be(channel.ID);
        string text = reader.ReadString();
        text.Should().Be("Broadcast to all nodes");

        await service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Distributed_PublishClanWhisperAsync_PublishesToRedis()
    {
        // Arrange
        DistributedChatStateService service = CreateService(redis: _mockRedis.Object);

        // Act
        await service.PublishClanWhisperAsync(
            clanId: 77,
            senderId: 100,
            senderName: "Alice",
            message: "Clan match starting soon!");

        // Assert
        _mockSubscriber.Verify(s => s.PublishAsync(
            It.Is<RedisChannel>(c => c.ToString() == "kongor:chat:clan:77"),
            It.Is<RedisValue>(v => v.Length() > 0),
            It.IsAny<CommandFlags>()), Times.Once);
    }

    [Fact]
    public async Task Distributed_OnClanWhisperReceived_WhenFromRemoteNode_DeliversToLocalClanMembers()
    {
        // Arrange
        Action<RedisChannel, RedisValue>? clanWhisperHandler = null;

        _mockSubscriber
            .Setup(s => s.SubscribeAsync(
                It.Is<RedisChannel>(c => c.ToString() == "kongor:chat:clan:77"),
                It.IsAny<Action<RedisChannel, RedisValue>>(),
                It.IsAny<CommandFlags>()))
            .Callback<RedisChannel, Action<RedisChannel, RedisValue>, CommandFlags>((_, handler, _) => clanWhisperHandler = handler)
            .Returns(Task.CompletedTask);

        DistributedChatStateService service = CreateService(redis: _mockRedis.Object);

        await service.StartAsync(CancellationToken.None);

        Clan testClan = new() { ID = 77, Name = "Heroes", Tag = "HERO" };
        Clan otherClan = new() { ID = 88, Name = "Villains", Tag = "VILL" };

        MockChatSession clanMemberSession = new(_tcpServer, _serviceProvider)
        {
            Account = CreateAccount(101, "ClanMember", testClan),
            ClientMetadata = CreateMetadata()
        };
        _clientSessions["ClanMember"] = clanMemberSession;
        await service.RegisterSessionAsync(clanMemberSession);

        MockChatSession otherPlayerSession = new(_tcpServer, _serviceProvider)
        {
            Account = CreateAccount(102, "OtherPlayer", otherClan),
            ClientMetadata = CreateMetadata()
        };
        _clientSessions["OtherPlayer"] = otherPlayerSession;

        DistributedClanWhisperEnvelope envelope = new()
        {
            ClanID = 77,
            SenderAccountID = 200,
            SenderName = "RemoteLeader",
            Message = "Raid time!",
            OriginNodeID = "remote-node-gamma",
            TimestampUtc = TimeProvider.System.GetUtcNow()
        };

        byte[] payload = MemoryPackSerializer.Serialize(envelope);

        // Act - Simulate incoming targeted clan multicast
        clanWhisperHandler.Should().NotBeNull();
        clanWhisperHandler!(RedisChannel.Literal("kongor:chat:clan:77"), payload);

        // Assert - Clan member received the response packet
        clanMemberSession.SentPackets.Should().HaveCount(1);
        otherPlayerSession.SentPackets.Should().BeEmpty();

        await service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Distributed_OnEvictionReceived_WhenSessionExistsLocally_EvictsSession()
    {
        // Arrange
        Action<RedisChannel, RedisValue>? evictionHandler = null;

        _mockSubscriber
            .Setup(s => s.SubscribeAsync(
                It.Is<RedisChannel>(c => c.ToString() == "kongor:chat:cluster:evict"),
                It.IsAny<Action<RedisChannel, RedisValue>>(),
                It.IsAny<CommandFlags>()))
            .Callback<RedisChannel, Action<RedisChannel, RedisValue>, CommandFlags>((_, handler, _) => evictionHandler = handler)
            .Returns(Task.CompletedTask);

        DistributedChatStateService service = CreateService(redis: _mockRedis.Object);

        await service.StartAsync(CancellationToken.None);

        MockChatSession localSession = new(_tcpServer, _serviceProvider)
        {
            Account = CreateAccount(100, "Alice"),
            ClientMetadata = CreateMetadata()
        };
        _clientSessions["Alice"] = localSession;

        DistributedEvictionEnvelope envelope = new()
        {
            AccountID = 100,
            AccountName = "Alice",
            RequestingNodeID = "brand-new-node",
            ReasonCode = (byte)ChatProtocol.ChatRejectReason.ECR_ACCOUNT_SHARING
        };

        byte[] payload = MemoryPackSerializer.Serialize(envelope);

        // Act
        evictionHandler.Should().NotBeNull();
        evictionHandler!(RedisChannel.Literal("kongor:chat:cluster:evict"), payload);

        // Assert - The session receives reject packet and is terminated
        localSession.SentPackets.Should().NotBeEmpty();
        byte[]? rejectPacket = localSession.SentPackets.FirstOrDefault(p =>
        {
            ChatBuffer r = new(p.AsSpan(2));
            return r.ReadCommand() == (ushort)ChatProtocol.ChatServerToClient.NET_CHAT_CL_REJECT;
        });
        rejectPacket.Should().NotBeNull();
        ChatBuffer reader = new(rejectPacket!.AsSpan(2));
        reader.ReadCommand().Should().Be((ushort)ChatProtocol.ChatServerToClient.NET_CHAT_CL_REJECT);
        byte reason = reader.ReadInt8();
        reason.Should().Be((byte)ChatProtocol.ChatRejectReason.ECR_ACCOUNT_SHARING);

        await service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Distributed_GetUserStatus_ResolvesRemoteUserPresence()
    {
        // Arrange
        DistributedChatStateService service = CreateService(redis: _mockRedis.Object);

        DistributedUserPresence remoteUser = new()
        {
            AccountID = 300,
            AccountName = "RemoteCharlie",
            NodeID = "node-zeta",
            ClientStatus = (byte)ChatProtocol.ChatClientStatus.CHAT_CLIENT_STATUS_IN_GAME,
            ChatMode = (byte)ChatProtocol.ChatModeType.CHAT_MODE_AVAILABLE,
            MatchAddress = "10.0.0.1:11235",
            MatchID = 999,
            AscensionLevel = 5,
            NameColour = "#FF0000",
            AccountIcon = "hero_icon",
            LastHeartbeatUtc = TimeProvider.System.GetUtcNow()
        };

        _mockDatabase
            .Setup(db => db.HashGetAsync(DistributedChatStateService.GetPresenceByIdKey(300), 300, It.IsAny<CommandFlags>()))
            .ReturnsAsync("remotecharlie");

        _mockDatabase
            .Setup(db => db.HashGetAsync(DistributedChatStateService.GetPresenceUsersKey("remotecharlie"), "remotecharlie", It.IsAny<CommandFlags>()))
            .ReturnsAsync(MemoryPackSerializer.Serialize(remoteUser));

        GetUserStatus processor = new(_mockChatContext.Object, service);

        MockChatSession requesterSession = new(_tcpServer, _serviceProvider)
        {
            Account = CreateAccount(100, "Alice"),
            ClientMetadata = CreateMetadata()
        };

        ChatBuffer buffer = new();
        buffer.WriteCommand(ChatProtocol.ClientToChatServer.NET_CHAT_CL_GET_USER_STATUS);
        buffer.WriteInt32(300);

        // Act
        await processor.Process(requesterSession, buffer);

        // Assert
        requesterSession.SentPackets.Should().HaveCount(1);
        ChatBuffer reader = new(requesterSession.SentPackets[0].AsSpan(2));
        reader.ReadCommand().Should().Be((ushort)ChatProtocol.ChatServerToClient.NET_CHAT_CL_USER_STATUS);
        int accountId = reader.ReadInt32();
        accountId.Should().Be(300);
        byte status = reader.ReadInt8();
        status.Should().Be((byte)ChatProtocol.ChatClientStatus.CHAT_CLIENT_STATUS_IN_GAME);
        byte flags = reader.ReadInt8();
        string symbol = reader.ReadString();
        string colour = reader.ReadString();
        colour.Should().Be("#FF0000");
        string icon = reader.ReadString();
        icon.Should().Be("hero_icon");
        int ascension = reader.ReadInt32();
        ascension.Should().Be(5);
    }

    [Fact]
    public async Task Standalone_RouteWhisperAsync_WhenRecipientHasIgnoredSender_SuppressesDeliveryAndNotifiesSender()
    {
        // Arrange
        DistributedChatStateService service = CreateService(redis: null);

        MockChatSession aliceSession = new(_tcpServer, _serviceProvider)
        {
            Account = CreateAccount(100, "Alice"),
            ClientMetadata = CreateMetadata()
        };
        _clientSessions["Alice"] = aliceSession;

        MockChatSession charlieSession = new(_tcpServer, _serviceProvider)
        {
            Account = CreateAccount(300, "Charlie"),
            ClientMetadata = CreateMetadata()
        };
        // Charlie has ignored Alice (100)
        charlieSession.Account.Peers.Add(new AccountPeer
        {
            AccountID = 300,
            PeerAccountID = 100,
            Type = PeerType.Ignored,
            TimestampCreated = TimeProvider.System.GetUtcNow()
        });
        _clientSessions["Charlie"] = charlieSession;

        // Act
        bool result = await service.RouteWhisperAsync("Alice", 100, "Charlie", "Hey Charlie!");

        // Assert
        result.Should().BeTrue();
        charlieSession.SentPackets.Should().BeEmpty(); // Suppressed from recipient
        aliceSession.SentPackets.Should().HaveCount(1); // Sender gets trigger notice

        ChatBuffer reader = new(aliceSession.SentPackets[0].AsSpan(2));
        reader.ReadCommand().Should().Be((ushort)ChatProtocol.Command.CHAT_CMD_WHISPER);
        reader.ReadString().Should().Be("System");
        reader.ReadString().Should().Be("[System] Player Charlie has you on their ignore list.");
    }

    [Fact]
    public async Task Distributed_ChannelMulticast_RefCounting_SubscribesOnFirstJoin_AndUnsubscribesOnLastLeave()
    {
        // Arrange
        DistributedChatStateService service = CreateService(redis: _mockRedis.Object);
        await service.StartAsync(CancellationToken.None);

        // Act 1: First user joins "Heroes" (0 -> 1 transition)
        await service.JoinChannelAsync("Heroes", 101, "Alice", 0);

        // Assert 1: Subscribed to kongor:chat:chan:heroes
        _mockSubscriber.Verify(s => s.SubscribeAsync(
            It.Is<RedisChannel>(c => c.ToString() == "kongor:chat:chan:heroes"),
            It.IsAny<Action<RedisChannel, RedisValue>>(),
            It.IsAny<CommandFlags>()), Times.Once);

        // Act 2: Second user joins "Heroes" (1 -> 2 transition)
        await service.JoinChannelAsync("heroes", 102, "Bob", 0);

        // Assert 2: SubscribeAsync NOT called again (ref count = 2)
        _mockSubscriber.Verify(s => s.SubscribeAsync(
            It.Is<RedisChannel>(c => c.ToString() == "kongor:chat:chan:heroes"),
            It.IsAny<Action<RedisChannel, RedisValue>>(),
            It.IsAny<CommandFlags>()), Times.Once);

        // Act 3: First user leaves "Heroes" (2 -> 1 transition)
        await service.LeaveChannelAsync("Heroes", "Alice");

        // Assert 3: UnsubscribeAsync NOT called yet (Bob still in channel)
        _mockSubscriber.Verify(s => s.UnsubscribeAsync(
            It.Is<RedisChannel>(c => c.ToString() == "kongor:chat:chan:heroes"),
            It.IsAny<Action<RedisChannel, RedisValue>>(),
            It.IsAny<CommandFlags>()), Times.Never);

        // Act 4: Second user leaves "Heroes" (1 -> 0 transition)
        await service.LeaveChannelAsync("heroes", "Bob");

        // Assert 4: Unsubscribed from kongor:chat:chan:heroes
        _mockSubscriber.Verify(s => s.UnsubscribeAsync(
            It.Is<RedisChannel>(c => c.ToString() == "kongor:chat:chan:heroes"),
            It.IsAny<Action<RedisChannel, RedisValue>>(),
            It.IsAny<CommandFlags>()), Times.Once);

        await service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Distributed_ClanMulticast_RefCounting_SubscribesOnFirstSession_AndUnsubscribesOnLastSession()
    {
        // Arrange
        DistributedChatStateService service = CreateService(redis: _mockRedis.Object);
        await service.StartAsync(CancellationToken.None);

        Clan testClan = new() { ID = 99, Name = "Legion", Tag = "LEG" };

        MockChatSession session1 = new(_tcpServer, _serviceProvider)
        {
            Account = CreateAccount(201, "PlayerOne", testClan),
            ClientMetadata = CreateMetadata()
        };
        _clientSessions["PlayerOne"] = session1;

        MockChatSession session2 = new(_tcpServer, _serviceProvider)
        {
            Account = CreateAccount(202, "PlayerTwo", testClan),
            ClientMetadata = CreateMetadata()
        };
        _clientSessions["PlayerTwo"] = session2;

        // Act 1: First clan member connects (0 -> 1 transition)
        await service.RegisterSessionAsync(session1);

        // Assert 1: Subscribed to kongor:chat:clan:99
        _mockSubscriber.Verify(s => s.SubscribeAsync(
            It.Is<RedisChannel>(c => c.ToString() == "kongor:chat:clan:99"),
            It.IsAny<Action<RedisChannel, RedisValue>>(),
            It.IsAny<CommandFlags>()), Times.Once);

        // Act 2: Second clan member connects (1 -> 2 transition)
        await service.RegisterSessionAsync(session2);

        // Assert 2: SubscribeAsync NOT called again (ref count = 2)
        _mockSubscriber.Verify(s => s.SubscribeAsync(
            It.Is<RedisChannel>(c => c.ToString() == "kongor:chat:clan:99"),
            It.IsAny<Action<RedisChannel, RedisValue>>(),
            It.IsAny<CommandFlags>()), Times.Once);

        // Act 3: First clan member disconnects (2 -> 1 transition)
        await service.UnregisterSessionAsync("PlayerOne", 201);

        // Assert 3: UnsubscribeAsync NOT called yet
        _mockSubscriber.Verify(s => s.UnsubscribeAsync(
            It.Is<RedisChannel>(c => c.ToString() == "kongor:chat:clan:99"),
            It.IsAny<Action<RedisChannel, RedisValue>>(),
            It.IsAny<CommandFlags>()), Times.Never);

        // Act 4: Second clan member disconnects (1 -> 0 transition)
        await service.UnregisterSessionAsync("PlayerTwo", 202);

        // Assert 4: Unsubscribed from kongor:chat:clan:99
        _mockSubscriber.Verify(s => s.UnsubscribeAsync(
            It.Is<RedisChannel>(c => c.ToString() == "kongor:chat:clan:99"),
            It.IsAny<Action<RedisChannel, RedisValue>>(),
            It.IsAny<CommandFlags>()), Times.Once);

        await service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Distributed_ChatChannel_JoinAndLeave_DispatchesToDistributedChatStateService()
    {
        // Arrange
        DistributedChatStateService service = CreateService(redis: _mockRedis.Object);
        await service.StartAsync(CancellationToken.None);

        ServiceCollection sc = new();
        sc.AddSingleton(_mockChatContext.Object);
        sc.AddSingleton(_mockDiscord.Object);
        sc.AddSingleton(_mockMatchmaking.Object);
        sc.AddSingleton<ILogger<ChatSession>>(NullLogger<ChatSession>.Instance);
        sc.AddSingleton<IDistributedChatStateService>(service);
        IServiceProvider sp = sc.BuildServiceProvider();

        MockChatSession aliceSession = new(_tcpServer, sp)
        {
            Account = CreateAccount(100, "Alice"),
            ClientMetadata = CreateMetadata()
        };

        ChatChannel channel = ChatChannel.GetOrCreate(_mockChatContext.Object, aliceSession, "Savage");

        // Act 1: Alice joins channel through ChatChannel.Join
        channel.Join(aliceSession);

        await Task.Delay(50);

        // Assert 1: DistributedChatState subscribed to kongor:chat:chan:savage
        _mockSubscriber.Verify(s => s.SubscribeAsync(
            It.Is<RedisChannel>(c => c.ToString() == "kongor:chat:chan:savage"),
            It.IsAny<Action<RedisChannel, RedisValue>>(),
            It.IsAny<CommandFlags>()), Times.Once);

        // Act 2: Alice leaves channel through ChatChannel.Leave
        channel.Leave(_mockChatContext.Object, aliceSession);

        await Task.Delay(50);

        // Assert 2: DistributedChatState unsubscribed from kongor:chat:chan:savage
        _mockSubscriber.Verify(s => s.UnsubscribeAsync(
            It.Is<RedisChannel>(c => c.ToString() == "kongor:chat:chan:savage"),
            It.IsAny<Action<RedisChannel, RedisValue>>(),
            It.IsAny<CommandFlags>()), Times.Once);

        await service.StopAsync(CancellationToken.None);
    }
}
