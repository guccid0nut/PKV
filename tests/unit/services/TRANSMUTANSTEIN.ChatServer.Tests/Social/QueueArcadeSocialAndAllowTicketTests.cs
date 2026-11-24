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
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NEXUS.Contracts.Chat;
using NEXUS.Contracts.Data;
using NEXUS.Contracts.Domain;
using StackExchange.Redis;
using TRANSMUTANSTEIN.ChatServer.CommandProcessors.Connection;
using TRANSMUTANSTEIN.ChatServer.CommandProcessors.Social;
using TRANSMUTANSTEIN.ChatServer.Domain.Clans;
using TRANSMUTANSTEIN.ChatServer.Domain.Communication;
using TRANSMUTANSTEIN.ChatServer.Domain.Core;
using TRANSMUTANSTEIN.ChatServer.Domain.Matchmaking;
using TRANSMUTANSTEIN.ChatServer.Infrastructure.Distributed;
using TRANSMUTANSTEIN.ChatServer.Infrastructure.Services;
using TRANSMUTANSTEIN.ChatServer.Internals;
using TRANSMUTANSTEIN.ChatServer.Services;
using TRANSMUTANSTEIN.ChatServer.Utilities;
using Xunit;
using DbRole = MERRICK.DatabaseContext.Entities.Utility.Role;

namespace TRANSMUTANSTEIN.ChatServer.Tests.Social;

public sealed class QueueArcadeSocialAndAllowTicketTests : IDisposable
{
    private readonly ConcurrentDictionary<string, RedisValue> _redisStrings = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, RedisValue>> _redisHashes = new(StringComparer.Ordinal);
    private readonly Mock<IDatabase> _mockRedis;
    private readonly MerrickContext _dbContext;
    private readonly Mock<IChatContext> _mockChatContext = new();
    private readonly Mock<IDiscordPresencePublisherService> _mockDiscord = new();
    private readonly Mock<IMatchmakingService> _mockMatchmaking = new();
    private readonly Mock<IDistributedChatStateService> _mockDistributedState = new();
    private readonly ConcurrentDictionary<string, ChatSession> _clientSessions = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<int, ChatSession> _matchServerSessions = new();
    private readonly ConcurrentDictionary<string, ChatChannel> _chatChannels = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<int, ChatChannel> _chatChannelsById = new();
    private readonly ServiceProvider _serviceProvider;
    private readonly TCPServer _tcpServer;

    public QueueArcadeSocialAndAllowTicketTests()
    {
        Log.Initialise(NullLogger.Instance);
        _mockRedis = CreateMockDatabase();

        DbContextOptions<MerrickContext> dbOptions = new DbContextOptionsBuilder<MerrickContext>()
            .UseInMemoryDatabase($"QueueArcadeIsolationChat_{Guid.NewGuid():N}")
            .Options;
        _dbContext = new MerrickContext(dbOptions);

        _mockChatContext.SetupGet(c => c.ClientChatSessions).Returns(_clientSessions);
        _mockChatContext.SetupGet(c => c.MatchServerChatSessions).Returns(_matchServerSessions);
        _mockChatContext.SetupGet(c => c.ChatChannels).Returns(_chatChannels);
        _mockChatContext.SetupGet(c => c.ChatChannelsByID).Returns(_chatChannelsById);

        ServiceCollection services = new();
        services.AddSingleton(_mockRedis.Object);
        services.AddScoped(_ => new MerrickContext(dbOptions));
        services.AddSingleton(_mockChatContext.Object);
        services.AddSingleton(_mockDiscord.Object);
        services.AddSingleton(_mockMatchmaking.Object);
        services.AddSingleton(_mockDistributedState.Object);
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddSingleton<PeerInvalidationSubscriber>();
        _serviceProvider = services.BuildServiceProvider();

        _tcpServer = new TCPServer(IPAddress.Loopback, 0);
    }

    public void Dispose()
    {
        _tcpServer.Dispose();
        _dbContext.Dispose();
        _serviceProvider.Dispose();
    }

    private sealed class SimulatedTimeProvider(DateTimeOffset initialUtc) : TimeProvider
    {
        private DateTimeOffset _utcNow = initialUtc;
        public override DateTimeOffset GetUtcNow() => _utcNow;
        public void Advance(TimeSpan delta) => _utcNow = _utcNow.Add(delta);
    }

    private sealed class RecordingChatSession(TCPServer server, IServiceProvider serviceProvider)
        : ChatSession(server, serviceProvider)
    {
        public List<byte[]> SentPackets { get; } = [];

        public override bool IsSessionConnected => true;

        public override bool SendAsync(ReadOnlySpan<byte> buffer)
        {
            SentPackets.Add(buffer.ToArray());
            return true;
        }
    }

    private static readonly DbRole SharedUserRole = new() { ID = 2, Name = "User" };
    private static readonly DbRole SharedStaffRole = new() { ID = 1, Name = "Staff" };

    private static Account CreateAccount(int id, string name, AccountType type = AccountType.Normal) => new()
    {
        ID = id,
        Name = name,
        Type = type,
        AscensionLevel = 5,
        User = new User
        {
            ID = id,
            EmailAddress = $"{name.ToLowerInvariant()}@kongor.local",
            RoleID = type == AccountType.Staff ? SharedStaffRole.ID : SharedUserRole.ID,
            Role = type == AccountType.Staff ? SharedStaffRole : SharedUserRole,
            SRPPasswordSalt = "salt",
            SRPPasswordHash = "hash",
            PBKDF2PasswordHash = "hash"
        }
    };

    private static ClientChatSessionMetadata CreateClientMetadata(string remoteIp = "127.0.0.1") => new()
    {
        RemoteIP = remoteIp,
        SessionCookie = $"cookie_{Guid.NewGuid():N}",
        SessionAuthenticationHash = "hash",
        ChatProtocolVersion = 68,
        OperatingSystemIdentifier = 1,
        OperatingSystemVersionMajor = 10,
        OperatingSystemVersionMinor = 0,
        OperatingSystemVersionPatch = 0,
        OperatingSystemBuildCode = "Win",
        OperatingSystemArchitecture = "x64",
        ClientVersionMajor = 4,
        ClientVersionMinor = 10,
        ClientVersionPatch = 1,
        ClientVersionRevision = 0,
        LastKnownClientState = ChatProtocol.ChatClientStatus.CHAT_CLIENT_STATUS_CONNECTED,
        ClientChatModeState = ChatProtocol.ChatModeType.CHAT_MODE_AVAILABLE,
        ClientRegion = "USE",
        ClientLanguage = "en"
    };

    private Mock<IDatabase> CreateMockDatabase()
    {
        Mock<IDatabase> mock = new();

        mock.Setup(db => db.HashSetAsync(It.IsAny<RedisKey>(), It.IsAny<RedisValue>(), It.IsAny<RedisValue>(), It.IsAny<When>(), It.IsAny<CommandFlags>()))
            .Returns((RedisKey key, RedisValue field, RedisValue val, When _, CommandFlags _) =>
            {
                ConcurrentDictionary<string, RedisValue> hash = _redisHashes.GetOrAdd(key.ToString(), _ => new ConcurrentDictionary<string, RedisValue>(StringComparer.Ordinal));
                hash[field.ToString()] = val;
                return Task.FromResult(true);
            });

        mock.Setup(db => db.HashGetAllAsync(It.IsAny<RedisKey>(), It.IsAny<CommandFlags>()))
            .Returns((RedisKey key, CommandFlags _) =>
            {
                if (!_redisHashes.TryGetValue(key.ToString(), out ConcurrentDictionary<string, RedisValue>? hash))
                {
                    return Task.FromResult(Array.Empty<HashEntry>());
                }

                return Task.FromResult(hash.Select(kv => new HashEntry(kv.Key, kv.Value)).ToArray());
            });

        mock.Setup(db => db.StringSetAsync(It.IsAny<RedisKey>(), It.IsAny<RedisValue>(), It.IsAny<TimeSpan?>(), It.IsAny<When>(), It.IsAny<CommandFlags>()))
            .Returns((RedisKey key, RedisValue val, TimeSpan? _, When _, CommandFlags _) =>
            {
                _redisStrings[key.ToString()] = val;
                return Task.FromResult(true);
            });

        mock.Setup(db => db.StringSetAsync(It.IsAny<RedisKey>(), It.IsAny<RedisValue>(), It.IsAny<TimeSpan?>(), It.IsAny<bool>(), It.IsAny<When>(), It.IsAny<CommandFlags>()))
            .Returns((RedisKey key, RedisValue val, TimeSpan? _, bool _, When _, CommandFlags _) =>
            {
                _redisStrings[key.ToString()] = val;
                return Task.FromResult(true);
            });

        mock.Setup(db => db.StringSetAsync(It.IsAny<RedisKey>(), It.IsAny<RedisValue>(), It.IsAny<Expiration>(), It.IsAny<ValueCondition>(), It.IsAny<CommandFlags>()))
            .Returns((RedisKey key, RedisValue val, Expiration _, ValueCondition _, CommandFlags _) =>
            {
                _redisStrings[key.ToString()] = val;
                return Task.FromResult(true);
            });

        mock.Setup(db => db.StringGetAsync(It.IsAny<RedisKey>(), It.IsAny<CommandFlags>()))
            .Returns((RedisKey key, CommandFlags _) =>
                Task.FromResult(_redisStrings.TryGetValue(key.ToString(), out RedisValue val) ? val : RedisValue.Null));

        mock.Setup(db => db.KeyDeleteAsync(It.IsAny<RedisKey>(), It.IsAny<CommandFlags>()))
            .Returns((RedisKey key, CommandFlags _) =>
                Task.FromResult(_redisStrings.TryRemove(key.ToString(), out RedisValue _)));

        return mock;
    }

    [Fact]
    public void DetectQueueArcade_NeverFlipsStandardDedicatedServer_OnSpoofedLobbyGameName()
    {
        // protects: Standard Tier D servers (#D1:11335) from flipping IsQueueArcade=true when a player names a custom lobby "QUEUE ARCADE".
        // fails_when: DetectQueueArcade inspects player-controlled gameName or serverName on a Tier L/D/K port.
        // seam: Real MatchServerChatSessionMetadata.DetectQueueArcade classifier.
        MatchServerChatSessionMetadata.DetectQueueArcade(
                serverName: "QUEUE ARCADE SPOOF",
                gameName: "QUEUE ARCADE [midwars] #",
                port: 11335,
                localPort: 11335,
                hostAccountName: "guccihost_dock",
                instance: 2001)
            .Should().BeFalse("Tier D server guccihost_dock:11335 must stay IsQueueArcade=false regardless of player lobby name");

        MatchServerChatSessionMetadata.DetectQueueArcade(
                serverName: "KONGOR BATTLEGROUNDS #D1",
                gameName: "QUEUE ARCADE",
                port: 11335,
                localPort: 11335)
            .Should().BeFalse("player lobby gameName 'QUEUE ARCADE' on port 11335 must never trigger Queue Arcade classification");

        MatchServerChatSessionMetadata.DetectQueueArcade(
                serverName: "Renamed Worker",
                gameName: "MidWars",
                port: 11485,
                localPort: 11485)
            .Should().BeTrue("Tier Q port 11485 must always classify as Queue Arcade");
    }

    [Fact]
    public async Task ServerInvites_ByNameAndById_AreBlockedWith0x0026_WhenInviterIsInQueueArcade()
    {
        // protects: Friends from receiving unjoinable 0x0025 server invites from players waiting inside Queue Arcade.
        // fails_when: InviteUserByName (0x0024) or InviteUserById (0x0023) forwards 0x0025 to targetSession while inviter is on Tier Q.
        // seam: Real InviteUserByName & InviteUserById -> ServerInviteDispatcher + ChatBuffer wire framing.
        Account inviterAcc = CreateAccount(401, "ArcadeQueuer");
        Account inviteeAcc = CreateAccount(402, "LobbyFriend");

        ClientChatSessionMetadata inviterMeta = CreateClientMetadata();
        inviterMeta.LastKnownClientState = ChatProtocol.ChatClientStatus.CHAT_CLIENT_STATUS_IN_GAME;
        inviterMeta.MatchServerAddress = "127.0.0.1:11485";
        inviterMeta.MatchID = 9501;
        inviterMeta.MatchName = "QUEUE ARCADE [midwars] #";
        inviterMeta.ActiveArcadeServerId = 301;

        RecordingChatSession inviterSession = new(_tcpServer, _serviceProvider)
        {
            Account = inviterAcc,
            ClientMetadata = inviterMeta
        };
        RecordingChatSession inviteeSession = new(_tcpServer, _serviceProvider)
        {
            Account = inviteeAcc,
            ClientMetadata = CreateClientMetadata()
        };

        _clientSessions[inviterAcc.Name] = inviterSession;
        _clientSessions[inviteeAcc.Name] = inviteeSession;

        InviteUserByName byName = new(_mockChatContext.Object, _mockRedis.Object);
        InviteUserById byId = new(_mockChatContext.Object, _mockRedis.Object);

        ChatBuffer reqByName = new();
        reqByName.WriteCommand(ChatProtocol.Command.CHAT_CMD_INVITE_USER_NAME);
        reqByName.WriteString(inviteeAcc.Name);
        await byName.Process(inviterSession, new ChatBuffer(reqByName.Data));

        inviteeSession.SentPackets.Should().BeEmpty("Queue Arcade invites by name must never reach the invitee");
        inviterSession.SentPackets.Should().ContainSingle();
        new ChatBuffer(inviterSession.SentPackets[0].AsSpan(2)).ReadCommand()
            .Should().Be(ChatProtocol.Command.CHAT_CMD_INVITE_FAILED_USER);

        inviterSession.SentPackets.Clear();
        ChatBuffer reqById = new();
        reqById.WriteCommand(ChatProtocol.Command.CHAT_CMD_INVITE_USER_ID);
        reqById.WriteInt32(inviteeAcc.ID);
        await byId.Process(inviterSession, new ChatBuffer(reqById.Data));

        inviteeSession.SentPackets.Should().BeEmpty("Queue Arcade invites by ID must never reach the invitee");
        inviterSession.SentPackets.Should().ContainSingle();
        new ChatBuffer(inviterSession.SentPackets[0].AsSpan(2)).ReadCommand()
            .Should().Be(ChatProtocol.Command.CHAT_CMD_INVITE_FAILED_USER);
    }

    [Fact]
    public async Task PlayerSpectate_DeniesNonStaffViewer_WhenTargetIsInQueueArcade_WhilePermittingStaff()
    {
        // protects: Friends from being stranded spectating bot warmups when TMM pulls the queued player, while keeping Staff observation open.
        // fails_when: Non-staff viewer is relayed to a Queue Arcade server or Staff viewer receives PSRR_DENY.
        // seam: Real PlayerSpectate (0x00B8) command processor against live Queue Arcade MatchServerChatSessionMetadata.
        Account queuedTargetAcc = CreateAccount(601, "ArcadeFighter");
        Account normalViewerAcc = CreateAccount(602, "CuriousFriend");
        Account staffViewerAcc = CreateAccount(603, "StaffAdmin", type: AccountType.Staff);

        ClientChatSessionMetadata targetMeta = CreateClientMetadata();
        targetMeta.LastKnownClientState = ChatProtocol.ChatClientStatus.CHAT_CLIENT_STATUS_IN_GAME;
        targetMeta.MatchServerAddress = "127.0.0.1:11485";
        targetMeta.MatchID = 9601;
        targetMeta.ActiveArcadeServerId = 701;

        RecordingChatSession targetSession = new(_tcpServer, _serviceProvider) { Account = queuedTargetAcc, ClientMetadata = targetMeta };
        RecordingChatSession normalViewer = new(_tcpServer, _serviceProvider) { Account = normalViewerAcc, ClientMetadata = CreateClientMetadata() };
        RecordingChatSession staffViewer = new(_tcpServer, _serviceProvider) { Account = staffViewerAcc, ClientMetadata = CreateClientMetadata() };
        RecordingChatSession arcadeServerSession = new(_tcpServer, _serviceProvider)
        {
            Account = CreateAccount(9001, "guccihost_arc", type: AccountType.ServerHost),
            ServerMetadata = new MatchServerChatSessionMetadata
            {
                ServerID = 701,
                SessionCookie = "arc-gs-cookie",
                ChatProtocolVersion = 68,
                Status = ChatProtocol.ServerStatus.SERVER_STATUS_ACTIVE,
                Address = "127.0.0.1",
                Port = 11485,
                LocalAddress = "127.0.0.1",
                LocalPort = 11485,
                MatchID = 9601,
                GameName = "QUEUE ARCADE [midwars] #",
                MapName = "midwars",
                GameMode = "normal",
                TeamSize = 5,
                ClientCount = 2,
                MaxSpectators = 10,
                IsQueueArcade = true
            }
        };

        _clientSessions[queuedTargetAcc.Name] = targetSession;
        _clientSessions[normalViewerAcc.Name] = normalViewer;
        _clientSessions[staffViewerAcc.Name] = staffViewer;
        _matchServerSessions[701] = arcadeServerSession;

        PlayerSpectate processor = new(_mockChatContext.Object, _mockRedis.Object, _dbContext);

        ChatBuffer spectateReq = new();
        spectateReq.WriteCommand(ChatProtocol.Command.CHAT_CMD_PLAYER_SPECTATE_REQUEST);
        spectateReq.WriteInt8(0);
        spectateReq.WriteString(queuedTargetAcc.Name);
        spectateReq.WriteInt8(0);
        spectateReq.WriteString(string.Empty);

        // Non-staff viewer -> immediate PSRR_DENY, 0 packets relayed to Queue Arcade server
        await processor.Process(normalViewer, new ChatBuffer(spectateReq.Data));
        arcadeServerSession.SentPackets.Should().BeEmpty();
        normalViewer.SentPackets.Should().ContainSingle();
        ChatBuffer denyReader = new(normalViewer.SentPackets[0].AsSpan(2));
        denyReader.ReadCommand().Should().Be(ChatProtocol.Command.CHAT_CMD_PLAYER_SPECTATE_REQUEST);
        denyReader.ReadInt8().Should().Be(1);
        denyReader.ReadString().Should().Be(queuedTargetAcc.Name);
        denyReader.ReadInt8().Should().Be((byte)ChatProtocol.PlayerSpectateRequestResponse.PSRR_DENY);

        // Staff viewer -> relayed to Queue Arcade server for admin observation
        await processor.Process(staffViewer, new ChatBuffer(spectateReq.Data));
        arcadeServerSession.SentPackets.Should().ContainSingle("Staff accounts must retain spectator access to Queue Arcade servers");
    }

    [Fact]
    public async Task QueueArcadeCoordinator_WritesArcadeAllowTicketBeforeAutoConnect_AndRevokesOnRelease()
    {
        // protects: Authoritative Garnet/Redis ARCADE-ALLOW:{serverId}:{accountId} ticket lifecycle across push & slot release.
        // fails_when: 0x0062 is sent before ARCADE-ALLOW is persisted in Redis, or ReleasePlayerFromArcade leaves stale tickets behind.
        // seam: Real QueueArcadeCoordinatorService <-> Redis StringSetAsync / KeyDeleteAsync.
        SimulatedTimeProvider fakeTime = new(new DateTimeOffset(2026, 11, 14, 12, 0, 0, TimeSpan.Zero));
        IConfiguration config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["QueueArcade:Enabled"] = "true",
                ["QueueArcade:MinQueueWaitBeforePushSeconds"] = "2.0",
                ["QueueArcade:BotReadinessFallbackTimeoutSeconds"] = "1.0"
            })
            .Build();

        ConcurrentDictionary<int, MatchmakingGroup> groups = new();
        _mockMatchmaking.SetupGet(m => m.Groups).Returns(groups);
        _mockMatchmaking.Setup(m => m.GetMatchmakingGroupByMemberID(It.IsAny<int>()))
            .Returns((int id) => groups.Values.FirstOrDefault(g => g.Members.Any(mem => mem.Account.ID == id)));

        using QueueArcadeCoordinatorService coordinator = new(
            config,
            _mockChatContext.Object,
            _mockMatchmaking.Object,
            fakeTime,
            randomSeed: 42,
            distributedCache: _mockRedis.Object);

        RecordingChatSession arcadeSrv = new(_tcpServer, _serviceProvider)
        {
            Account = CreateAccount(9001, "guccihost_arc", type: AccountType.ServerHost),
            ServerMetadata = new MatchServerChatSessionMetadata
            {
                ServerID = 811,
                SessionCookie = "arc-cookie-811",
                ChatProtocolVersion = 68,
                Status = ChatProtocol.ServerStatus.SERVER_STATUS_IDLE,
                Address = "127.0.0.1",
                Port = 11485,
                LocalAddress = "127.0.0.1",
                LocalPort = 11485,
                Location = "USE",
                IsQueueArcade = true
            }
        };
        _matchServerSessions[811] = arcadeSrv;

        // 1. Register player in Lobby BEFORE Arcade server handshake so demand-driven pre-warm wakes #Q1
        Account playerAcc = CreateAccount(555, "QueuedHero");
        RecordingChatSession playerSession = new(_tcpServer, _serviceProvider)
        {
            Account = playerAcc,
            ClientMetadata = CreateClientMetadata()
        };
        _clientSessions[playerAcc.Name] = playerSession;

        // 2. Handshake & confirm match ready
        coordinator.OnArcadeServerHandshake(arcadeSrv);
        ChatBuffer createPkt = new(arcadeSrv.SentPackets.Last().AsSpan(2));
        createPkt.ReadCommand().Should().Be(ChatProtocol.ChatServerToGameServer.NET_CHAT_GS_CREATE_MATCH);
        _ = createPkt.ReadInt8();
        int matchUpId = createPkt.ReadInt32();

        arcadeSrv.ServerMetadata.Status = ChatProtocol.ServerStatus.SERVER_STATUS_ACTIVE;
        arcadeSrv.ServerMetadata.MatchID = 9901;
        coordinator.ConfirmArcadeMatchReady(arcadeSrv, matchUpId, 9901);
        coordinator.NotifyArcadeServerBotReadiness(811, 10, explicitReadyAck: true);

        // 3. Enqueue the solo player and advance past MinQueueWaitBeforePushSeconds

        MatchmakingGroupMember member = new(playerSession)
        {
            Slot = 0,
            IsLeader = true,
            IsReady = true,
            IsInGame = false,
            IsEligibleForMatchmaking = true,
            Rating = 1500.0
        };
        MatchmakingGroup group = new()
        {
            QueueStartTime = fakeTime.GetUtcNow(),
            Members = [member],
            Information = new MatchmakingGroupInformation
            {
                ClientVersion = "4.10.1.0",
                GroupType = ChatProtocol.TMMType.TMM_TYPE_PVP,
                GameType = ChatProtocol.TMMGameType.TMM_GAME_TYPE_CASUAL,
                MapName = "caldavar",
                GameModes = ["ap"],
                GameRegions = ["USE"],
                Ranked = true,
                MatchFidelity = 1,
                BotDifficulty = 0,
                RandomizeBots = false
            }
        };
        groups[playerAcc.ID] = group;

        string allowKey = MatchServer.GetQueueArcadeAllowKey(811, playerAcc.ID);
        _redisStrings.ContainsKey(allowKey).Should().BeFalse();

        fakeTime.Advance(TimeSpan.FromSeconds(3));
        await coordinator.EvaluateTickAsync();

        _redisStrings.TryGetValue(allowKey, out RedisValue ticketValue).Should().BeTrue("ARCADE-ALLOW ticket must be written before 0x0062 push");
        ticketValue.ToString().Should().Be("9901");

        // 3. Release player from Queue Arcade -> ticket revoked immediately
        coordinator.ReleasePlayerFromArcade(playerAcc.ID, "ranked_match_confirmed:10001");
        _redisStrings.ContainsKey(allowKey).Should().BeFalse("ARCADE-ALLOW ticket must be revoked when player leaves Queue Arcade");
    }
}
