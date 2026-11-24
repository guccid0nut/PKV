// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Collections.Concurrent;
using System.Net;
using ASPIRE.Common.Extensions.Cache;
using ASPIRE.Common.Models.ServerManagement;
using ASPIRE.Common.Services;
using FluentAssertions;
using MERRICK.DatabaseContext.Entities.Core;
using MERRICK.DatabaseContext.Enumerations;
using MERRICK.DatabaseContext.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NEXUS.Contracts.Chat;
using NEXUS.Contracts.Domain;
using StackExchange.Redis;
using TRANSMUTANSTEIN.ChatServer.CommandProcessors.Channels;
using TRANSMUTANSTEIN.ChatServer.CommandProcessors.Connection;
using TRANSMUTANSTEIN.ChatServer.CommandProcessors.MatchState;
using TRANSMUTANSTEIN.ChatServer.Domain.Communication;
using TRANSMUTANSTEIN.ChatServer.Domain.Core;
using TRANSMUTANSTEIN.ChatServer.Domain.Matchmaking;
using TRANSMUTANSTEIN.ChatServer.Internals;
using TRANSMUTANSTEIN.ChatServer.Services;
using TRANSMUTANSTEIN.ChatServer.Utilities;
using Xunit;
using DbRole = MERRICK.DatabaseContext.Entities.Utility.Role;

namespace TRANSMUTANSTEIN.ChatServer.Tests.Security;

public sealed class ChatHandshakeAndPeerRoleZeroTrustTests : IDisposable
{
    private readonly InMemoryRedisHashStore _redisStore = new();
    private readonly Mock<IDatabase> _mockRedis;
    private readonly MerrickContext _dbContext;
    private readonly Mock<IChatContext> _mockChatContext = new();
    private readonly Mock<IDiscordPresencePublisherService> _mockDiscord = new();
    private readonly Mock<IMatchmakingService> _mockMatchmaking = new();
    private readonly Mock<ITelemetryPublisherService> _mockTelemetry = new();
    private readonly ConcurrentDictionary<string, ChatSession> _clientSessions = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<int, ChatSession> _matchServerSessions = new();
    private readonly ConcurrentDictionary<int, ChatSession> _managerSessions = new();
    private readonly ConcurrentDictionary<string, ChatChannel> _channelsByName = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<int, ChatChannel> _channelsById = new();
    private readonly FloodPreventionService _floodPrevention = new(NullLogger<FloodPreventionService>.Instance);
    private readonly ServiceProvider _serviceProvider;
    private readonly TCPServer _genericServer;
    private readonly ClientChatServer _clientListener;
    private readonly MatchServerChatServer _matchServerListener;
    private readonly MatchServerManagerChatServer _managerListener;

    public ChatHandshakeAndPeerRoleZeroTrustTests()
    {
        Log.Initialise(NullLogger.Instance);
        _mockRedis = _redisStore.CreateMockDatabase();

        DbContextOptions<MerrickContext> dbOptions = new DbContextOptionsBuilder<MerrickContext>()
            .UseInMemoryDatabase($"Wave1BChatZeroTrust_{Guid.NewGuid():N}")
            .Options;
        _dbContext = new MerrickContext(dbOptions);

        _mockChatContext.SetupGet(c => c.ClientChatSessions).Returns(_clientSessions);
        _mockChatContext.SetupGet(c => c.MatchServerChatSessions).Returns(_matchServerSessions);
        _mockChatContext.SetupGet(c => c.MatchServerManagerChatSessions).Returns(_managerSessions);
        _mockChatContext.SetupGet(c => c.ChatChannels).Returns(_channelsByName);
        _mockChatContext.SetupGet(c => c.ChatChannelsByID).Returns(_channelsById);

        ServiceCollection services = new();
        services.AddSingleton(_mockRedis.Object);
        services.AddSingleton(_dbContext);
        services.AddSingleton(_mockChatContext.Object);
        services.AddSingleton(_mockDiscord.Object);
        services.AddSingleton(_mockMatchmaking.Object);
        services.AddSingleton(_mockTelemetry.Object);
        services.AddSingleton(_floodPrevention);
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddTransient<ServerHandshake>();
        services.AddTransient<ServerManagerHandshake>();
        services.AddTransient<MatchStarted>();
        services.AddTransient<MatchComplete>();
        services.AddTransient<SendChannelMessage>();
        services.AddTransient<Ping>();
        services.AddTransient<Pong>();

        _serviceProvider = services.BuildServiceProvider();
        _genericServer = new TCPServer(IPAddress.Loopback, 0);
        _clientListener = new ClientChatServer(_serviceProvider, IPAddress.Loopback, 0);
        _matchServerListener = new MatchServerChatServer(_serviceProvider, IPAddress.Loopback, 0);
        _managerListener = new MatchServerManagerChatServer(_serviceProvider, IPAddress.Loopback, 0);
    }

    public void Dispose()
    {
        _floodPrevention.Dispose();
        _managerListener.Dispose();
        _matchServerListener.Dispose();
        _clientListener.Dispose();
        _genericServer.Dispose();
        _dbContext.Dispose();
        _serviceProvider.Dispose();
    }

    // protects: Active dedicated match server pool in MatchServerChatSessions and Redis MATCH-SERVERS / MATCH-SERVER-INDEX
    // fails_when: ServerHandshake assigns ServerMetadata prior to cookie/host validation or Cleanup evicts a live session owned by another socket
    // seam: Real ServerHandshake + DistributedCacheExtensions + MerrickContext + ChatSession.Cleanup
    [Fact]
    public async Task ServerHandshake_WhenSpoofedPreAuthRequestTargetsLiveServerId_PreservesLiveServerAndRedisState()
    {
        const int liveServerId = 777;
        const string validCookie = "cookie-valid-srv-777";

        Account hostAccount = CreateAccount(9001, "DedicatedHost01", AccountType.ServerHost);
        Account regularAccount = CreateAccount(9002, "TestPlayer", AccountType.Normal);
        _dbContext.Accounts.AddRange(hostAccount, regularAccount);
        await _dbContext.SaveChangesAsync();

        MatchServer liveServer = new()
        {
            ID = liveServerId,
            HostAccountID = hostAccount.ID,
            HostAccountName = hostAccount.Name,
            Name = "KONGOR US-EAST #1",
            Instance = 1,
            IPAddress = "198.51.100.10",
            Port = 11235,
            Location = "USE",
            Description = "Dedicated Arena",
            Cookie = validCookie
        };
        await _mockRedis.Object.SetMatchServer(hostAccount.Name, liveServer);

        // Also seed a second server with a Regular (non-ServerHost) account to verify permission failure cannot evict liveServerId
        MatchServer otherServer = new()
        {
            ID = 888,
            HostAccountID = regularAccount.ID,
            HostAccountName = regularAccount.Name,
            Name = "OTHER",
            Instance = 2,
            IPAddress = "198.51.100.11",
            Port = 11236,
            Location = "USE",
            Description = "Other",
            Cookie = "cookie-other-srv-888"
        };
        await _mockRedis.Object.SetMatchServer(regularAccount.Name, otherServer);

        RecordingChatSession liveSession = new(_matchServerListener, _serviceProvider)
        {
            Account = hostAccount,
            ServerMetadata = new MatchServerChatSessionMetadata
            {
                ServerID = liveServerId,
                SessionCookie = validCookie,
                ChatProtocolVersion = (int)ChatProtocol.CHAT_PROTOCOL_EXTERNAL_VERSION,
                Status = ChatProtocol.ServerStatus.SERVER_STATUS_ACTIVE
            }
        };
        _matchServerSessions[liveServerId] = liveSession;

        ServerHandshake processor = _serviceProvider.GetRequiredService<ServerHandshake>();

        // Attack 1: Spoofed live ServerId with bogus cookie
        RecordingChatSession attackerInvalidCookie = new(_matchServerListener, _serviceProvider);
        ChatBuffer invalidCookiePacket = BuildServerHandshakeBuffer(
            liveServerId,
            "bogus-attacker-cookie",
            (int)ChatProtocol.CHAT_PROTOCOL_EXTERNAL_VERSION);
        await processor.Process(attackerInvalidCookie, invalidCookiePacket);
        attackerInvalidCookie.Cleanup();

        // Attack 2: Spoofed live ServerId using another server's cookie (ID mismatch)
        RecordingChatSession attackerMismatchedId = new(_matchServerListener, _serviceProvider);
        ChatBuffer mismatchedIdPacket = BuildServerHandshakeBuffer(
            liveServerId,
            otherServer.Cookie,
            (int)ChatProtocol.CHAT_PROTOCOL_EXTERNAL_VERSION);
        await processor.Process(attackerMismatchedId, mismatchedIdPacket);
        attackerMismatchedId.Cleanup();

        // Attack 3: Protocol version mismatch targeting live ServerId
        RecordingChatSession attackerBadVersion = new(_matchServerListener, _serviceProvider);
        ChatBuffer badVersionPacket = BuildServerHandshakeBuffer(liveServerId, validCookie, 99999);
        await processor.Process(attackerBadVersion, badVersionPacket);
        attackerBadVersion.Cleanup();

        // Assert live server was NEVER evicted from memory or Redis
        _matchServerSessions.TryGetValue(liveServerId, out ChatSession? currentSession).Should().BeTrue();
        currentSession.Should().BeSameAs(liveSession);
        (await _mockRedis.Object.GetMatchServerByID(liveServerId)).Should().NotBeNull("pre-auth spoofed handshake must never delete live server from Redis");
        attackerInvalidCookie.ServerMetadata.Should().BeNull();
        attackerMismatchedId.ServerMetadata.Should().BeNull();
        attackerBadVersion.ServerMetadata.Should().BeNull();
    }

    // protects: Redis MATCH-SERVERS / MATCH-SERVER-INDEX / MATCH-SERVER-SESSIONS across transient TCP chat disconnects (e.g. 25s map load)
    // fails_when: ServerDisconnect or Cleanup removes MatchServer from Redis or sends remote "quit" to the running dedicated server
    // seam: Real ServerDisconnect + ChatSession.TerminateMatchServer + ChatSession.Cleanup + ServerHandshake reconnect
    [Fact]
    public async Task ServerDisconnect_AndAbruptCleanup_PreserveRedisMatchServerSessionAndAllowReconnect()
    {
        const int serverId = 1128928427;
        const string validCookie = "cookie-enfos-load-25s-reconnect";

        Account hostAccount = CreateAccount(9009, "compel_slave_2002", AccountType.ServerHost);
        _dbContext.Accounts.Add(hostAccount);
        await _dbContext.SaveChangesAsync();

        MatchServer matchServer = new()
        {
            ID = serverId,
            Name = "Slave2002",
            HostAccountName = hostAccount.Name,
            HostAccountID = hostAccount.ID,
            Instance = 4002,
            IPAddress = "127.0.0.1",
            Port = 11485,
            LocalPort = 11485,
            Location = "US",
            Description = "DevoWars 10v10",
            Cookie = validCookie
        };
        await _mockRedis.Object.SetMatchServer(hostAccount.Name, matchServer);

        RecordingChatSession initialSession = new(_matchServerListener, _serviceProvider)
        {
            Account = hostAccount,
            ServerMetadata = new MatchServerChatSessionMetadata
            {
                ServerID = serverId,
                SessionCookie = validCookie,
                ChatProtocolVersion = (int)ChatProtocol.CHAT_PROTOCOL_EXTERNAL_VERSION,
                Status = ChatProtocol.ServerStatus.SERVER_STATUS_ACTIVE,
                Address = "127.0.0.1",
                Port = 11485
            }
        };
        _matchServerSessions[serverId] = initialSession;

        // 1. Explicit NET_CHAT_GS_DISCONNECT (timeout after long map load): removes socket from pool,
        // preserves Redis MatchServer record & session cookie, and NEVER sends remote "quit".
        ChatBuffer disconnectBuffer = new();
        disconnectBuffer.WriteCommand(ChatProtocol.GameServerToChatServer.NET_CHAT_GS_DISCONNECT);
        await ChatPacketDispatcher.DispatchAsync(
            _serviceProvider,
            initialSession,
            ChatProtocol.GameServerToChatServer.NET_CHAT_GS_DISCONNECT,
            disconnectBuffer);

        _matchServerSessions.ContainsKey(serverId).Should().BeFalse();
        initialSession.SentPackets.Should().BeEmpty("transient chat disconnect must NEVER send remote 'quit' to the match server");
        (await _mockRedis.Object.GetMatchServerByID(serverId)).Should().NotBeNull();
        (await _mockRedis.Object.GetMatchServerBySessionCookie(validCookie)).Should().NotBeNull();

        // 2. Immediate reconnect via ServerHandshake must succeed with NET_CHAT_GS_ACCEPT (zero NET_CHAT_GS_REJECT)
        RecordingChatSession reconnectSession = new(_matchServerListener, _serviceProvider);
        ServerHandshake handshakeProcessor = _serviceProvider.GetRequiredService<ServerHandshake>();
        await handshakeProcessor.Process(
            reconnectSession,
            BuildServerHandshakeBuffer(serverId, validCookie, (int)ChatProtocol.CHAT_PROTOCOL_EXTERNAL_VERSION));

        _matchServerSessions.TryGetValue(serverId, out ChatSession? reconnected).Should().BeTrue();
        reconnected.Should().BeSameAs(reconnectSession);
        reconnectSession.SentPackets.Should().Contain(pkt =>
            pkt.Length >= 4 &&
            BitConverter.ToUInt16(pkt, 2) == ChatProtocol.ChatServerToGameServer.NET_CHAT_GS_ACCEPT);

        // 3. Abrupt TCP drop cleanup (Cleanup) must also preserve Redis MatchServer session for automatic recovery
        reconnectSession.Cleanup();
        _matchServerSessions.ContainsKey(serverId).Should().BeFalse();
        (await _mockRedis.Object.GetMatchServerByID(serverId)).Should().NotBeNull();
        (await _mockRedis.Object.GetMatchServerBySessionCookie(validCookie)).Should().NotBeNull();
    }

    // protects: Active COMPEL server manager pool in MatchServerManagerChatSessions and Redis MATCH-SERVER-MANAGERS
    // fails_when: ServerManagerHandshake assigns ManagerMetadata prior to cookie/host validation or Cleanup evicts a live manager session
    // seam: Real ServerManagerHandshake + DistributedCacheExtensions + MerrickContext + ChatSession.Cleanup
    [Fact]
    public async Task ServerManagerHandshake_WhenSpoofedPreAuthRequestTargetsLiveManagerId_PreservesLiveManagerAndRedisState()
    {
        const int liveManagerId = 42;
        const string validManagerCookie = "cookie-valid-mgr-42";

        Account hostAccount = CreateAccount(9101, "CompelManagerHost", AccountType.ServerHost);
        _dbContext.Accounts.Add(hostAccount);
        await _dbContext.SaveChangesAsync();

        MatchServerManager liveManager = new()
        {
            ID = liveManagerId,
            HostAccountID = hostAccount.ID,
            HostAccountName = hostAccount.Name,
            IPAddress = "198.51.100.20",
            Cookie = validManagerCookie
        };
        await _mockRedis.Object.SetMatchServerManager(hostAccount.Name, liveManager);

        RecordingChatSession liveManagerSession = new(_managerListener, _serviceProvider)
        {
            Account = hostAccount,
            ManagerMetadata = new MatchServerManagerChatSessionMetadata
            {
                ServerManagerID = liveManagerId,
                SessionCookie = validManagerCookie,
                ChatProtocolVersion = (int)ChatProtocol.CHAT_PROTOCOL_EXTERNAL_VERSION
            }
        };
        _managerSessions[liveManagerId] = liveManagerSession;

        ServerManagerHandshake processor = _serviceProvider.GetRequiredService<ServerManagerHandshake>();

        RecordingChatSession attackerSession = new(_managerListener, _serviceProvider);
        ChatBuffer spoofPacket = new();
        spoofPacket.WriteCommand(ChatProtocol.ServerManagerToChatServer.NET_CHAT_SM_CONNECT);
        spoofPacket.WriteInt32(liveManagerId);
        spoofPacket.WriteString("forged-manager-cookie");
        spoofPacket.WriteInt32((int)ChatProtocol.CHAT_PROTOCOL_EXTERNAL_VERSION);

        await processor.Process(attackerSession, spoofPacket);
        attackerSession.Cleanup();

        _managerSessions.TryGetValue(liveManagerId, out ChatSession? currentSession).Should().BeTrue();
        currentSession.Should().BeSameAs(liveManagerSession);
        (await _mockRedis.Object.GetMatchServerManagerByID(liveManagerId)).Should().NotBeNull("pre-auth spoofed manager handshake must never delete live manager from Redis");
        attackerSession.ManagerMetadata.Should().BeNull();
    }

    // protects: Pre-authentication gateway firewall across 11031/11032/11033 sockets
    // fails_when: Unauthenticated sockets (prior to successful handshake) can execute post-auth commands such as MatchStarted, MatchComplete, or SendChannelMessage
    // seam: Real ChatPacketDispatcher.DispatchAsync + ChatCommandRegistry + command processors
    [Fact]
    public async Task ChatPacketDispatcher_WhenSessionIsUnauthenticated_BlocksPostAuthCommandsWhilePermittingPing()
    {
        RecordingChatSession unauthServerSocket = new(_matchServerListener, _serviceProvider);
        RecordingChatSession unauthClientSocket = new(_clientListener, _serviceProvider);

        // Set up observable side effects that WOULD trigger if post-auth processors ran
        RecordingChatSession bobSession = new(_clientListener, _serviceProvider)
        {
            Account = CreateAccount(200, "BobPeer", AccountType.Normal),
            ClientMetadata = CreateClientMetadata(200)
        };
        ChatChannel channel = ChatChannel.GetOrCreate(_mockChatContext.Object, bobSession, "General");
        channel.TryAddMember(bobSession.Account.Name, new ChatChannelMember(bobSession, channel));

        // 1. Pre-auth MatchStarted (0x0505) must be blocked
        ChatBuffer matchStarted = new();
        matchStarted.WriteCommand(ChatProtocol.GameServerToChatServer.NET_CHAT_GS_MATCH_STARTED);
        matchStarted.WriteInt32(55501);
        await ChatPacketDispatcher.DispatchAsync(
            _serviceProvider,
            unauthServerSocket,
            ChatProtocol.GameServerToChatServer.NET_CHAT_GS_MATCH_STARTED,
            matchStarted);

        _mockTelemetry.Verify(t => t.OnMatchStartedAsync(It.IsAny<int>()), Times.Never);

        // 2. Pre-auth MatchComplete (0x0515) must be blocked
        ChatBuffer matchComplete = new();
        matchComplete.WriteCommand(ChatProtocol.GameServerToChatServer.NET_CHAT_GS_MATCH_ENDED);
        matchComplete.WriteInt32(55501);
        matchComplete.WriteInt8(1);
        matchComplete.WriteInt8(1);
        matchComplete.WriteInt8(0);
        await ChatPacketDispatcher.DispatchAsync(
            _serviceProvider,
            unauthServerSocket,
            ChatProtocol.GameServerToChatServer.NET_CHAT_GS_MATCH_ENDED,
            matchComplete);

        _mockTelemetry.Verify(t => t.OnMatchEndedAsync(It.IsAny<int>()), Times.Never);

        // 3. Pre-auth SendChannelMessage (0x0003) must be blocked
        ChatBuffer chatMsg = new();
        chatMsg.WriteCommand(ChatProtocol.Command.CHAT_CMD_CHANNEL_MSG);
        chatMsg.WriteString("unauthenticated-spam");
        chatMsg.WriteInt32(channel.ID);
        await ChatPacketDispatcher.DispatchAsync(
            _serviceProvider,
            unauthClientSocket,
            ChatProtocol.Command.CHAT_CMD_CHANNEL_MSG,
            chatMsg);

        bobSession.SentPackets.Should().BeEmpty();

        // 4. Pre-auth Ping (0x2A00) IS permitted as keepalive and replies with Pong (0x2A01)
        ChatBuffer ping = new();
        ping.WriteCommand(ChatProtocol.Bidirectional.NET_CHAT_PING);
        await ChatPacketDispatcher.DispatchAsync(
            _serviceProvider,
            unauthClientSocket,
            ChatProtocol.Bidirectional.NET_CHAT_PING,
            ping);

        unauthClientSocket.SentPackets.Should().ContainSingle();
        ChatBuffer pongReader = new(unauthClientSocket.SentPackets[0].AsSpan(2));
        pongReader.ReadCommand().Should().Be(ChatProtocol.Bidirectional.NET_CHAT_PONG);
    }

    // protects: Listener peer-role isolation between Client (11031), MatchServer (11032), and ServerManager (11033)
    // fails_when: An authenticated player client socket on 11031 can invoke MatchStarted (0x0505), StatSubmissionResult (0x0514), MatchComplete (0x0515), or ServerHandshake (0x0500)
    // seam: Real ChatPacketDispatcher.DispatchAsync + ClientChatServer listener role enforcement
    [Fact]
    public async Task ChatPacketDispatcher_WhenAuthenticatedClientPeerInvokesServerOrManagerOpcodes_RejectsCrossRoleExecution()
    {
        Account playerAccount = CreateAccount(301, "MaliciousClient", AccountType.Normal);
        ClientChatSessionMetadata clientMeta = CreateClientMetadata(301);
        clientMeta.LastKnownClientState = ChatProtocol.ChatClientStatus.CHAT_CLIENT_STATUS_IN_GAME;
        clientMeta.MatchID = 99001;

        RecordingChatSession authenticatedClient = new(_clientListener, _serviceProvider)
        {
            Account = playerAccount,
            ClientMetadata = clientMeta
        };
        _clientSessions[playerAccount.Name] = authenticatedClient;

        // Attempt to forge MatchStarted (0x0505) from Client listener
        ChatBuffer forgedMatchStarted = new();
        forgedMatchStarted.WriteCommand(ChatProtocol.GameServerToChatServer.NET_CHAT_GS_MATCH_STARTED);
        forgedMatchStarted.WriteInt32(99001);
        await ChatPacketDispatcher.DispatchAsync(
            _serviceProvider,
            authenticatedClient,
            ChatProtocol.GameServerToChatServer.NET_CHAT_GS_MATCH_STARTED,
            forgedMatchStarted);

        // Attempt to forge MatchComplete (0x0515) from Client listener to clear active match state
        ChatBuffer forgedMatchComplete = new();
        forgedMatchComplete.WriteCommand(ChatProtocol.GameServerToChatServer.NET_CHAT_GS_MATCH_ENDED);
        forgedMatchComplete.WriteInt32(99001);
        forgedMatchComplete.WriteInt8(1);
        forgedMatchComplete.WriteInt8(1);
        forgedMatchComplete.WriteInt8(1);
        forgedMatchComplete.WriteInt32(playerAccount.ID);
        await ChatPacketDispatcher.DispatchAsync(
            _serviceProvider,
            authenticatedClient,
            ChatProtocol.GameServerToChatServer.NET_CHAT_GS_MATCH_ENDED,
            forgedMatchComplete);

        // Attempt to invoke ServerHandshake (0x0500) on Client listener
        ChatBuffer forgedServerHandshake = BuildServerHandshakeBuffer(
            777,
            "any-cookie",
            (int)ChatProtocol.CHAT_PROTOCOL_EXTERNAL_VERSION);
        await ChatPacketDispatcher.DispatchAsync(
            _serviceProvider,
            authenticatedClient,
            ChatProtocol.GameServerToChatServer.NET_CHAT_GS_CONNECT,
            forgedServerHandshake);

        _mockTelemetry.Verify(t => t.OnMatchStartedAsync(It.IsAny<int>()), Times.Never);
        _mockTelemetry.Verify(t => t.OnMatchEndedAsync(It.IsAny<int>()), Times.Never);
        authenticatedClient.ClientMetadata.MatchID.Should().Be(99001, "Client peer must not be able to run MatchComplete to clear its own MatchID");
        authenticatedClient.SentPackets.Should().BeEmpty("ServerHandshake must not run on ClientChatServer listener");
    }

    private static ChatBuffer BuildServerHandshakeBuffer(int serverId, string cookie, int protocolVersion)
    {
        ChatBuffer buffer = new();
        buffer.WriteCommand(ChatProtocol.GameServerToChatServer.NET_CHAT_GS_CONNECT);
        buffer.WriteInt32(serverId);
        buffer.WriteString(cookie);
        buffer.WriteInt32(protocolVersion);
        return buffer;
    }

    private static ClientChatSessionMetadata CreateClientMetadata(int id)
    {
        return new ClientChatSessionMetadata
        {
            RemoteIP = "127.0.0.1",
            SessionCookie = $"cookie-{id}",
            SessionAuthenticationHash = $"hash-{id}",
            ChatProtocolVersion = (int)ChatProtocol.CHAT_PROTOCOL_EXTERNAL_VERSION,
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
            LastKnownClientState = ChatProtocol.ChatClientStatus.CHAT_CLIENT_STATUS_CONNECTED,
            ClientChatModeState = ChatProtocol.ChatModeType.CHAT_MODE_AVAILABLE,
            ClientRegion = "US",
            ClientLanguage = "en"
        };
    }

    private static Account CreateAccount(int id, string name, AccountType type)
    {
        return new Account
        {
            ID = id,
            Name = name,
            Type = type,
            User = new User
            {
                ID = id,
                EmailAddress = $"{name.ToLowerInvariant()}@kongor.local",
                Role = new DbRole { ID = id, Name = "User" },
                SRPPasswordSalt = "synthetic-salt",
                SRPPasswordHash = "synthetic-hash",
                PBKDF2PasswordHash = "synthetic-hash"
            }
        };
    }

    private sealed class RecordingChatSession(TCPServer server, IServiceProvider sp) : ChatSession(server, sp)
    {
        public List<byte[]> SentPackets { get; } = [];

        public override bool SendAsync(ReadOnlySpan<byte> buffer)
        {
            SentPackets.Add(buffer.ToArray());
            return true;
        }
    }

    private sealed class InMemoryRedisHashStore
    {
        private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, RedisValue>> _hashes = new(StringComparer.Ordinal);

        public Mock<IDatabase> CreateMockDatabase()
        {
            Mock<IDatabase> mock = new();

            mock.Setup(db => db.HashSetAsync(It.IsAny<RedisKey>(), It.IsAny<HashEntry[]>(), It.IsAny<CommandFlags>()))
                .Returns((RedisKey key, HashEntry[] entries, CommandFlags _) =>
                {
                    ConcurrentDictionary<string, RedisValue> hash = _hashes.GetOrAdd(key.ToString(), _ => new ConcurrentDictionary<string, RedisValue>(StringComparer.Ordinal));
                    foreach (HashEntry entry in entries)
                    {
                        hash[entry.Name.ToString()] = entry.Value;
                    }
                    return Task.CompletedTask;
                });

            mock.Setup(db => db.HashGetAsync(It.IsAny<RedisKey>(), It.IsAny<RedisValue>(), It.IsAny<CommandFlags>()))
                .Returns((RedisKey key, RedisValue field, CommandFlags _) =>
                {
                    if (_hashes.TryGetValue(key.ToString(), out ConcurrentDictionary<string, RedisValue>? hash) &&
                        hash.TryGetValue(field.ToString(), out RedisValue val))
                    {
                        return Task.FromResult(val);
                    }
                    return Task.FromResult(RedisValue.Null);
                });

            mock.Setup(db => db.HashGetAllAsync(It.IsAny<RedisKey>(), It.IsAny<CommandFlags>()))
                .Returns((RedisKey key, CommandFlags _) =>
                {
                    if (!_hashes.TryGetValue(key.ToString(), out ConcurrentDictionary<string, RedisValue>? hash))
                    {
                        return Task.FromResult(Array.Empty<HashEntry>());
                    }

                    HashEntry[] entries = hash.Select(kvp => new HashEntry(kvp.Key, kvp.Value)).ToArray();
                    return Task.FromResult(entries);
                });

            mock.Setup(db => db.HashDeleteAsync(It.IsAny<RedisKey>(), It.IsAny<RedisValue>(), It.IsAny<CommandFlags>()))
                .Returns((RedisKey key, RedisValue field, CommandFlags flags) =>
                {
                    bool removed = _hashes.TryGetValue(key.ToString(), out ConcurrentDictionary<string, RedisValue>? hash) &&
                                   hash.TryRemove(field.ToString(), out RedisValue _);
                    return Task.FromResult(removed);
                });

            return mock;
        }
    }
}
