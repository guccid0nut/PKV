// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Collections.Concurrent;
using System.Net;
using ASPIRE.Common.Extensions.Cache;
using ASPIRE.Common.Models.ServerManagement;
using ASPIRE.Common.Network;
using FluentAssertions;
using MERRICK.DatabaseContext.Entities.Core;
using MERRICK.DatabaseContext.Enumerations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NEXUS.Contracts.Chat;
using NEXUS.Contracts.Domain;
using StackExchange.Redis;
using TRANSMUTANSTEIN.ChatServer.Domain.Core;
using TRANSMUTANSTEIN.ChatServer.Internals;
using TRANSMUTANSTEIN.ChatServer.Services;
using TRANSMUTANSTEIN.ChatServer.Utilities;
using Xunit;
using DbRole = MERRICK.DatabaseContext.Entities.Utility.Role;

namespace TRANSMUTANSTEIN.ChatServer.Tests.Services;

/// <summary>
///     Verifies Issue 12.1 split-horizon game-server endpoint resolution across:
///     1. SplitHorizonEndpointResolver (Loopback, LAN/Tailscale VPN, NAT Hairpin, External WAN)
///     2. DistributedCacheExtensions.GetMatchServerByIPAddressAndPort (PrepareToJoinMatch split-horizon lookup)
///     3. ChatSession.BroadcastConnectionStatusUpdate, ReceiveFriendAndClanMemberConnectionStatus &amp; 0x00B8 spectate command dispatch
///     4. SplitHorizonPublicIpDiscoveryService standalone zero-HTTP guard
/// </summary>
public sealed class SplitHorizonEndpointResolverTests : IDisposable
{
    private readonly Mock<IChatContext> _mockChatContext = new();
    private readonly Mock<IDiscordPresencePublisherService> _mockDiscord = new();
    private readonly Mock<IMatchmakingService> _mockMatchmaking = new();
    private readonly ConcurrentDictionary<string, ChatSession> _clientSessions = new();
    private readonly ConcurrentDictionary<int, ChatSession> _matchServerSessions = new();
    private readonly TCPServer _tcpServer;
    private readonly ServiceProvider _serviceProvider;

    public SplitHorizonEndpointResolverTests()
    {
        Log.Initialise(NullLogger.Instance);

        _mockChatContext.SetupGet(c => c.ClientChatSessions).Returns(_clientSessions);
        _mockChatContext.SetupGet(c => c.MatchServerChatSessions).Returns(_matchServerSessions);

        ServiceCollection services = new();
        services.AddSingleton(_mockChatContext.Object);
        services.AddSingleton(_mockDiscord.Object);
        services.AddSingleton(_mockMatchmaking.Object);
        services.AddSingleton<ILogger<ChatSession>>(NullLogger<ChatSession>.Instance);
        _serviceProvider = services.BuildServiceProvider();

        _tcpServer = new TCPServer(IPAddress.Loopback, 0);
    }

    public void Dispose()
    {
        _serviceProvider.Dispose();
        _tcpServer.Dispose();
    }

    private sealed class MockChatSession(TCPServer server, IServiceProvider serviceProvider)
        : ChatSession(server, serviceProvider)
    {
        public List<byte[]> SentPackets { get; } = [];

        public override bool SendAsync(ReadOnlySpan<byte> buffer)
        {
            SentPackets.Add(buffer.ToArray());
            return true;
        }
    }

    private MockChatSession CreateSession(int id, string name, string remoteIp, List<Friendship>? friendships = null)
    {
        MockChatSession session = new(_tcpServer, _serviceProvider)
        {
            Account = new Account
            {
                ID = id,
                Name = name,
                Friendships = friendships ?? [],
                User = new User
                {
                    ID = id,
                    EmailAddress = $"{name.ToLowerInvariant()}@kongor.net",
                    Role = new DbRole { ID = 2, Name = "User" },
                    SRPPasswordSalt = "salt",
                    SRPPasswordHash = "hash",
                    PBKDF2PasswordHash = "hash"
                }
            },
            ClientMetadata = new ClientChatSessionMetadata
            {
                RemoteIP = remoteIp,
                SessionCookie = $"cookie_{id}",
                SessionAuthenticationHash = $"hash_{id}",
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
                LastKnownClientState = ChatProtocol.ChatClientStatus.CHAT_CLIENT_STATUS_CONNECTED,
                ClientChatModeState = ChatProtocol.ChatModeType.CHAT_MODE_AVAILABLE,
                ClientRegion = "US",
                ClientLanguage = "en"
            }
        };

        _clientSessions[name] = session;
        return session;
    }

    private static Mock<IDatabase> CreateInMemoryRedisDatabase()
    {
        ConcurrentDictionary<string, ConcurrentDictionary<RedisValue, RedisValue>> hashes = new();
        Mock<IDatabase> mockDb = new();

        mockDb.Setup(db => db.HashSetAsync(
                It.IsAny<RedisKey>(),
                It.IsAny<HashEntry[]>(),
                It.IsAny<CommandFlags>()))
            .Callback<RedisKey, HashEntry[], CommandFlags>((key, entries, _) =>
            {
                ConcurrentDictionary<RedisValue, RedisValue> map = hashes.GetOrAdd(key.ToString(), _ => new());
                foreach (HashEntry entry in entries)
                {
                    map[entry.Name] = entry.Value;
                }
            })
            .Returns(Task.CompletedTask);

        mockDb.Setup(db => db.HashGetAllAsync(It.IsAny<RedisKey>(), It.IsAny<CommandFlags>()))
            .Returns<RedisKey, CommandFlags>((key, _) =>
            {
                if (!hashes.TryGetValue(key.ToString(), out ConcurrentDictionary<RedisValue, RedisValue>? map))
                {
                    return Task.FromResult(Array.Empty<HashEntry>());
                }

                HashEntry[] result = [.. map.Select(kvp => new HashEntry(kvp.Key, kvp.Value))];
                return Task.FromResult(result);
            });

        return mockDb;
    }

    [Theory]
    [InlineData("127.0.0.1", "127.0.0.1", 11235)]
    [InlineData("192.168.1.90", "192.168.1.10", 11235)]
    [InlineData("100.101.102.103", "192.168.1.10", 11235)]
    [InlineData("203.0.113.77", "192.168.1.10", 11235)] // NAT Hairpin
    [InlineData("198.51.100.246", "203.0.113.77", 11235)]  // External WAN tester (never 127.0.0.1!)
    public void ResolveGameServerEndpoint_WhenSelfHostedLoopbackServer_RoutesByClientTopology(
        string clientIpStr,
        string expectedHost,
        int expectedPort)
    {
        IPAddress clientIp = IPAddress.Parse(clientIpStr);

        (string host, int port) = SplitHorizonEndpointResolver.ResolveGameServerEndpoint(
            serverAddress: "127.0.0.1",
            serverPort: 11235,
            localAddress: "127.0.0.1",
            localPort: 11235,
            clientIp: clientIp,
            serverTruePublicIp: null,
            discoveredLanIp: "192.168.1.10",
            discoveredPublicIp: "203.0.113.77");

        host.Should().Be(expectedHost);
        port.Should().Be(expectedPort);
    }

    [Fact]
    public async Task PrepareToJoinMatch_And_FriendPresence_And_Spectate_RewritesPerRecipientTopology()
    {
        // Arrange: Self-hosted MatchServer registered by COMPEL at 127.0.0.1:11235 with TruePublicIPAddress 203.0.113.77
        Mock<IDatabase> mockDb = CreateInMemoryRedisDatabase();
        MatchServer donutArena = new()
        {
            ID = 1,
            Instance = 1,
            HostAccountID = 1,
            HostAccountName = "HOST",
            Name = "DONUT ARENA #L1",
            IPAddress = "127.0.0.1",
            LocalIPAddress = "192.168.1.10",
            TruePublicIPAddress = "203.0.113.77",
            Port = 11235,
            LocalPort = 11235,
            Location = "US",
            Description = "Playtest"
        };
        await mockDb.Object.SetMatchServer("HOST", donutArena);

        // Host on loopback, LAN friend on 192.168.1.90, WAN friend on 198.51.100.246 (both friended Host ID 100)
        MockChatSession hostSession = CreateSession(100, "guccidonut", "127.0.0.1");
        MockChatSession lanFriend = CreateSession(200, "GUEST-10", "192.168.1.90",
            [new Friendship { AccountID = 200, FriendAccountID = 100, Status = FriendshipStatus.Accepted }]);
        MockChatSession wanFriend = CreateSession(300, "RemoteTester1", "198.51.100.246",
            [new Friendship { AccountID = 300, FriendAccountID = 100, Status = FriendshipStatus.Accepted }]);

        // Act 1: External WAN client joins via resolved WAN IP 203.0.113.77:11235 -> cache lookup must match MatchServer #1!
        MatchServer? wanLookup = await mockDb.Object.GetMatchServerByIPAddressAndPort("203.0.113.77", 11235);
        wanLookup.Should().NotBeNull();
        wanLookup!.ID.Should().Be(1);

        // Act 2: Host enters IN_GAME on DONUT ARENA #L1 -> broadcasts CHAT_CMD_UPDATE_STATUS to self, LAN friend, and WAN friend
        hostSession.ClientMetadata.MatchID = 1052;
        hostSession.ClientMetadata.MatchName = "TMM #1052";
        hostSession.ClientMetadata.MatchServerAddress = "127.0.0.1:11235";
        hostSession.BroadcastConnectionStatusUpdate(ChatProtocol.ChatClientStatus.CHAT_CLIENT_STATUS_IN_GAME, donutArena);

        ReadStatusServerAddress(hostSession.SentPackets.Single()).Should().Be("127.0.0.1:11235");
        ReadStatusServerAddress(lanFriend.SentPackets.Single()).Should().Be("192.168.1.10:11235");
        ReadStatusServerAddress(wanFriend.SentPackets.Single()).Should().Be("203.0.113.77:11235");

        // Act 3: Late-joining WAN friend & Spectate request against stored "127.0.0.1:11235" with cached public IP
        SplitHorizonEndpointResolver.SetCachedPublicIP("203.0.113.77");
        try
        {
            MockChatSession lateWanObserver = CreateSession(400, "Robert", "198.51.100.77",
                [new Friendship { AccountID = 400, FriendAccountID = 100, Status = FriendshipStatus.Accepted }]);

            lateWanObserver.ReceiveFriendAndClanMemberConnectionStatus();
            lateWanObserver.SentPackets.Should().HaveCount(1);
            ReadInitialStatusFirstPeerServerAddress(lateWanObserver.SentPackets[0]).Should().Be("203.0.113.77:11235");

            MockChatSession matchServerSession = new(_tcpServer, _serviceProvider)
            {
                Account = new Account
                {
                    ID = 1,
                    Name = "HOST",
                    Type = AccountType.ServerHost,
                    User = new User
                    {
                        ID = 1,
                        EmailAddress = "host@kongor.net",
                        Role = new DbRole { ID = 1, Name = "Host" },
                        SRPPasswordSalt = "salt",
                        SRPPasswordHash = "hash",
                        PBKDF2PasswordHash = "hash"
                    }
                },
                ServerMetadata = new MatchServerChatSessionMetadata
                {
                    ServerID = 1,
                    SessionCookie = "gs_cookie_1",
                    ChatProtocolVersion = 68,
                    Status = ChatProtocol.ServerStatus.SERVER_STATUS_ACTIVE,
                    Address = "127.0.0.1",
                    Port = 11235,
                    LocalAddress = "192.168.1.10",
                    LocalPort = 11235,
                    MatchID = 1052,
                    GameName = "TMM #1052",
                    MapName = "caldavar",
                    GameMode = "normal",
                    TeamSize = 5,
                    ClientCount = 2
                }
            };
            _matchServerSessions[1] = matchServerSession;

            lateWanObserver.SentPackets.Clear();
            ChatBuffer spectateReq = new();
            spectateReq.WriteCommand(ChatProtocol.Command.CHAT_CMD_PLAYER_SPECTATE_REQUEST);
            spectateReq.WriteInt8(0);
            spectateReq.WriteString("guccidonut");
            spectateReq.WriteInt8(0);
            spectateReq.WriteString(string.Empty);

            ChatCommandRegistry.TryGetDescriptor(ChatProtocol.Command.CHAT_CMD_PLAYER_SPECTATE_REQUEST, out ChatCommandDescriptor? spectateDescriptor)
                .Should().BeTrue();
            await spectateDescriptor!.ExecuteAsync(_serviceProvider, lateWanObserver, spectateReq);

            // Step 1: ChatServer relays 0x00B8 SubType=0 to MatchServer and waits for IGame+0x6b0 approval
            lateWanObserver.SentPackets.Should().BeEmpty();
            matchServerSession.SentPackets.Should().HaveCount(1);

            // Step 2: MatchServer approves via 0x00B8 SubType=1 through ChatCommandRegistry.AuthorizeCommand
            ChatBuffer gsSpectateApprove = new();
            gsSpectateApprove.WriteCommand(ChatProtocol.Command.CHAT_CMD_PLAYER_SPECTATE_REQUEST);
            gsSpectateApprove.WriteInt8((byte)ChatProtocol.PlayerSpectateRequest.PLAYER_SPECTATE_REQUEST_RESPONSE);
            gsSpectateApprove.WriteString("robert");
            gsSpectateApprove.WriteString("guccidonut");
            gsSpectateApprove.WriteInt8((byte)ChatProtocol.PlayerSpectateRequestResponse.PSRR_ALLOW);
            await spectateDescriptor.ExecuteAsync(_serviceProvider, matchServerSession, new ChatBuffer(gsSpectateApprove.Data));

            // Native k2_x64.dll FUN_18069c600 reads server IP:port from ChatClientInfo hydrated via 0x000C,
            // followed by 0x00B8 (subType=1, string=spectateeName, byte=PSRR_ALLOW, 0 trailing bytes).
            lateWanObserver.SentPackets.Should().HaveCount(2);
            ReadStatusServerAddress(lateWanObserver.SentPackets[0]).Should().Be("203.0.113.77:11235");

            ReadOnlySpan<byte> spectatePayload = lateWanObserver.SentPackets[1].AsSpan(2);
            ChatBuffer spectateReader = new(spectatePayload);
            spectateReader.ReadCommand().Should().Be((ushort)ChatProtocol.Command.CHAT_CMD_PLAYER_SPECTATE_REQUEST);
            spectateReader.ReadInt8().Should().Be((byte)ChatProtocol.PlayerSpectateRequest.PLAYER_SPECTATE_REQUEST_RESPONSE);
            spectateReader.ReadString().Should().Be("guccidonut");
            spectateReader.ReadInt8().Should().Be((byte)ChatProtocol.PlayerSpectateRequestResponse.PSRR_ALLOW);
            spectatePayload.Length.Should().Be(2 + 1 + ("guccidonut".Length + 1) + 1);
        }
        finally
        {
            SplitHorizonEndpointResolver.ResetCachedPublicIPForTesting();
        }
    }

    [Fact]
    public async Task GetMatchServerByIPAddressAndPort_WhenUnregisteredPortRequested_ReturnsNull()
    {
        Mock<IDatabase> mockDb = CreateInMemoryRedisDatabase();
        await mockDb.Object.SetMatchServer("HOST", new MatchServer
        {
            ID = 1,
            Instance = 1,
            HostAccountID = 1,
            HostAccountName = "HOST",
            Name = "DONUT ARENA #L1",
            IPAddress = "127.0.0.1",
            Port = 11235,
            Location = "US",
            Description = "Playtest"
        });

        MatchServer? miss = await mockDb.Object.GetMatchServerByIPAddressAndPort("203.0.113.77", 11299);
        miss.Should().BeNull();
    }

    [Fact]
    public async Task SplitHorizonPublicIpDiscoveryService_WhenStandaloneWithoutDiscoverFlag_MakesZeroHttpCalls()
    {
        int httpRequests = 0;
        using HttpClient client = new(new DelegatingStubHandler(_ =>
        {
            Interlocked.Increment(ref httpRequests);
            throw new InvalidOperationException("Unexpected outbound HTTP call in offline standalone mode");
        }));

        Dictionary<string, string?> env = new()
        {
            ["KONGOR_STANDALONE"] = "true"
        };

        using SplitHorizonPublicIpDiscoveryService service = new(
            NullLogger<SplitHorizonPublicIpDiscoveryService>.Instance,
            httpClientFactory: null,
            httpClient: client,
            environmentReader: key => env.GetValueOrDefault(key));

        await service.StartAsync(CancellationToken.None);
        await service.StopAsync(CancellationToken.None);

        httpRequests.Should().Be(0);
    }

    private static string ReadStatusServerAddress(byte[] framedPacket)
    {
        ChatBuffer reader = new(framedPacket.AsSpan(2));
        reader.ReadCommand().Should().Be((ushort)ChatProtocol.Command.CHAT_CMD_UPDATE_STATUS);
        _ = reader.ReadInt32();  // Account ID
        _ = reader.ReadInt8();   // Status
        _ = reader.ReadInt8();   // Flags
        _ = reader.ReadInt32();  // Clan ID
        _ = reader.ReadString(); // Clan Name
        _ = reader.ReadString(); // Chat Symbol
        _ = reader.ReadString(); // Name Colour
        _ = reader.ReadString(); // Account Icon
        return reader.ReadString();
    }

    private static string ReadInitialStatusFirstPeerServerAddress(byte[] framedPacket)
    {
        ChatBuffer reader = new(framedPacket.AsSpan(2));
        reader.ReadCommand().Should().Be((ushort)ChatProtocol.Command.CHAT_CMD_INITIAL_STATUS);
        reader.ReadInt32().Should().Be(1);
        _ = reader.ReadInt32();  // Peer Account ID
        _ = reader.ReadInt8();   // Status
        _ = reader.ReadInt8();   // Flags
        _ = reader.ReadString(); // Name Colour
        _ = reader.ReadString(); // Account Icon
        return reader.ReadString();
    }

    private sealed class DelegatingStubHandler(Func<HttpRequestMessage, HttpResponseMessage> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(handler(request));
    }
}
