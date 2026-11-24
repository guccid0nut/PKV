// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors

using System.Collections.Concurrent;
using System.Net;
using System.Text;
using FluentAssertions;
using MERRICK.DatabaseContext.Entities.Core;
using MERRICK.DatabaseContext.Enumerations;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NEXUS.Contracts.Chat;
using NEXUS.Contracts.Domain;
using StackExchange.Redis;
using TRANSMUTANSTEIN.ChatServer.Attributes;
using TRANSMUTANSTEIN.ChatServer.Domain.Communication;
using TRANSMUTANSTEIN.ChatServer.Domain.Core;
using TRANSMUTANSTEIN.ChatServer.Domain.Matchmaking;
using TRANSMUTANSTEIN.ChatServer.Internals;
using TRANSMUTANSTEIN.ChatServer.Services;
using TRANSMUTANSTEIN.ChatServer.Utilities;
using Xunit;
using DbRole = MERRICK.DatabaseContext.Entities.Utility.Role;

namespace TRANSMUTANSTEIN.ChatServer.Tests.Services;

public sealed class QueueArcadeDemandHibernationTests : IDisposable
{
    private sealed class ManualTimeProvider(DateTimeOffset startUtc) : TimeProvider
    {
        private DateTimeOffset _utcNow = startUtc;
        public override DateTimeOffset GetUtcNow() => _utcNow;
        public void Advance(TimeSpan delta) => _utcNow = _utcNow.Add(delta);
    }

    private readonly ManualTimeProvider _fakeTime = new(new DateTimeOffset(2026, 11, 15, 10, 0, 0, TimeSpan.Zero));
    private readonly ConcurrentDictionary<string, ChatSession> _clientSessions = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<int, ChatSession> _matchServerSessions = new();
    private readonly ConcurrentDictionary<int, ChatSession> _managerSessions = new();
    private readonly ConcurrentDictionary<string, ChatChannel> _channelsByName = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<int, ChatChannel> _channelsById = new();
    private readonly ConcurrentDictionary<string, RedisValue> _redisStrings = new(StringComparer.Ordinal);
    private readonly Mock<IChatContext> _mockChatContext = new();
    private readonly Mock<IMatchmakingService> _mockMatchmaking = new();
    private readonly ConcurrentDictionary<int, MatchmakingGroup> _groups = new();
    private readonly Mock<IDatabase> _mockRedis = new();
    private readonly ServiceProvider _serviceProvider;
    private readonly TCPServer _tcpServer;
    private readonly QueueArcadeCoordinatorService _coordinator;

    public QueueArcadeDemandHibernationTests()
    {
        Log.Initialise(NullLogger.Instance);
        _mockChatContext.SetupGet(c => c.ClientChatSessions).Returns(_clientSessions);
        _mockChatContext.SetupGet(c => c.MatchServerChatSessions).Returns(_matchServerSessions);
        _mockChatContext.SetupGet(c => c.MatchServerManagerChatSessions).Returns(_managerSessions);
        _mockChatContext.SetupGet(c => c.ChatChannels).Returns(_channelsByName);
        _mockChatContext.SetupGet(c => c.ChatChannelsByID).Returns(_channelsById);
        _mockMatchmaking.SetupGet(m => m.Groups).Returns(_groups);

        _mockRedis.Setup(db => db.StringGetAsync(It.IsAny<RedisKey>(), It.IsAny<CommandFlags>()))
            .ReturnsAsync((RedisKey key, CommandFlags _) =>
                _redisStrings.TryGetValue(key.ToString(), out RedisValue val) ? val : RedisValue.Null);
        _mockRedis.Setup(db => db.StringSet(
                It.IsAny<RedisKey>(),
                It.IsAny<RedisValue>(),
                It.IsAny<TimeSpan?>(),
                It.IsAny<bool>(),
                It.IsAny<When>(),
                It.IsAny<CommandFlags>()))
            .Returns((RedisKey key, RedisValue val, TimeSpan? _, bool _, When _, CommandFlags _) =>
            {
                _redisStrings[key.ToString()] = val;
                return true;
            });
        _mockRedis.Setup(db => db.StringSet(
                It.IsAny<RedisKey>(),
                It.IsAny<RedisValue>(),
                It.IsAny<Expiration>(),
                It.IsAny<ValueCondition>(),
                It.IsAny<CommandFlags>()))
            .Returns((RedisKey key, RedisValue val, Expiration _, ValueCondition _, CommandFlags _) =>
            {
                _redisStrings[key.ToString()] = val;
                return true;
            });
        _mockRedis.Setup(db => db.StringSetAsync(
                It.IsAny<RedisKey>(),
                It.IsAny<RedisValue>(),
                It.IsAny<TimeSpan?>(),
                It.IsAny<bool>(),
                It.IsAny<When>(),
                It.IsAny<CommandFlags>()))
            .Returns((RedisKey key, RedisValue val, TimeSpan? _, bool _, When _, CommandFlags _) =>
            {
                _redisStrings[key.ToString()] = val;
                return Task.FromResult(true);
            });
        _mockRedis.Setup(db => db.StringSetAsync(
                It.IsAny<RedisKey>(),
                It.IsAny<RedisValue>(),
                It.IsAny<Expiration>(),
                It.IsAny<ValueCondition>(),
                It.IsAny<CommandFlags>()))
            .Returns((RedisKey key, RedisValue val, Expiration _, ValueCondition _, CommandFlags _) =>
            {
                _redisStrings[key.ToString()] = val;
                return Task.FromResult(true);
            });

        ServiceCollection services = new();
        services.AddSingleton(_mockChatContext.Object);
        services.AddSingleton(_mockMatchmaking.Object);
        services.AddSingleton(Mock.Of<IDiscordPresencePublisherService>());
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        _serviceProvider = services.BuildServiceProvider();
        _tcpServer = new TCPServer(IPAddress.Loopback, 0);

        IConfiguration config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["QueueArcade:Enabled"] = "true",
                ["QueueArcade:RoundDurationSeconds"] = "600",
                ["QueueArcade:VictoryHoldSeconds"] = "12",
            })
            .Build();

        _coordinator = new QueueArcadeCoordinatorService(
            config,
            _mockChatContext.Object,
            _mockMatchmaking.Object,
            timeProvider: _fakeTime,
            distributedCache: _mockRedis.Object,
            randomSeed: 20261115);
    }

    public void Dispose()
    {
        _coordinator.Dispose();
        _tcpServer.Dispose();
        _serviceProvider.Dispose();
    }

    private sealed class RecordingSession(TCPServer srv, IServiceProvider sp) : ChatSession(srv, sp)
    {
        public List<byte[]> SentPackets { get; } = [];

        public override bool IsSessionConnected => true;

        public override long Send(byte[] buffer)
        {
            SentPackets.Add(buffer);
            return buffer.Length;
        }

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

    private static ClientChatSessionMetadata CreateClientMetadata(string region = "USE", bool optIn = true) => new()
    {
        RemoteIP = "127.0.0.1",
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
        ClientRegion = region,
        ClientLanguage = "en",
        QueueArcadeOptIn = optIn
    };

    private RecordingSession RegisterArcadeServer(int serverId = 801, string location = "USE")
    {
        RecordingSession srv = new(_tcpServer, _serviceProvider)
        {
            Account = CreateAccount(9000 + serverId, $"guccihost_arc_{serverId}", AccountType.ServerHost),
            ServerMetadata = new MatchServerChatSessionMetadata
            {
                ServerID = serverId,
                SessionCookie = $"cookie-{serverId}",
                ChatProtocolVersion = 68,
                Status = ChatProtocol.ServerStatus.SERVER_STATUS_IDLE,
                Address = "127.0.0.1",
                Port = 11485,
                LocalAddress = "127.0.0.1",
                LocalPort = 11485,
                Location = location,
                IsQueueArcade = true
            }
        };
        srv.MarkAuthenticated(ChatPeerRole.MatchServer);
        _matchServerSessions[serverId] = srv;
        return srv;
    }

    private RecordingSession RegisterClient(int accountId, string name, AccountType type = AccountType.Normal, string region = "USE", bool optIn = true)
    {
        RecordingSession client = new(_tcpServer, _serviceProvider)
        {
            Account = CreateAccount(accountId, name, type),
            ClientMetadata = CreateClientMetadata(region, optIn)
        };
        client.MarkAuthenticated(ChatPeerRole.Client);
        _clientSessions[name] = client;
        return client;
    }

    private static List<string> ExtractRemoteCommands(RecordingSession srv)
    {
        List<string> cmds = [];
        foreach (byte[] pkt in srv.SentPackets)
        {
            if (pkt.Length < 4) continue;
            ushort cmd = BitConverter.ToUInt16(pkt, 2);
            if (cmd == ChatProtocol.ChatServerToGameServer.NET_CHAT_GS_REMOTE_COMMAND && pkt.Length > 4)
            {
                int firstNull = Array.IndexOf(pkt, (byte)0, 4);
                int cmdStart = firstNull >= 0 && firstNull + 1 < pkt.Length ? firstNull + 1 : 4;
                int secondNull = Array.IndexOf(pkt, (byte)0, cmdStart);
                int cmdEnd = secondNull >= 0 ? secondNull : pkt.Length;
                string text = Encoding.UTF8.GetString(pkt, cmdStart, cmdEnd - cmdStart);
                cmds.Add(text);
            }
        }
        return cmds;
    }

    private static int CountCreateMatchPackets(RecordingSession srv)
    {
        int count = 0;
        foreach (byte[] pkt in srv.SentPackets)
        {
            if (pkt.Length >= 4 && BitConverter.ToUInt16(pkt, 2) == ChatProtocol.ChatServerToGameServer.NET_CHAT_GS_CREATE_MATCH)
            {
                count++;
            }
        }
        return count;
    }

    [Fact]
    public async Task IdleMode_DefaultsToHibernate_HoldingServerIdleWhenZeroOptedInPlayersOnline()
    {
        // protects: CPU/power efficiency — idle Queue Arcade servers must stay warm in SERVER_STATUS_IDLE when nobody is online.
        // fails_when: Queue Arcade immediately spins up back-to-back bot matches with 0 players in Lobby/Queue.
        // seam: Real QueueArcadeCoordinatorService.OnArcadeServerHandshake + EvaluateTickAsync.
        RecordingSession srv = RegisterArcadeServer();
        _coordinator.OnArcadeServerHandshake(srv);
        await _coordinator.EvaluateTickAsync();

        CountCreateMatchPackets(srv).Should().Be(0);
        srv.ServerMetadata!.Status.Should().Be(ChatProtocol.ServerStatus.SERVER_STATUS_IDLE);

        QueueArcadeMetricsSnapshot metrics = _coordinator.GetMetricsSnapshot();
        metrics.EffectiveIdleMode.Should().Be(ArcadeIdleMode.Hibernate);
        metrics.MatchesStartedTotal.Should().Be(0);
        metrics.HibernateHoldsTotal.Should().BeGreaterThanOrEqualTo(1);
    }

    [Fact]
    public async Task LobbyPreWarm_StartsMatchOnOptedInLobbyPlayer_AndExcludesInMatchAndOptedOutPlayers()
    {
        // protects: Instant 0s Arcade readiness when an opted-in player sits in Chat/Lobby, while ignoring opted-out players
        //           and players busy inside non-Arcade Ranked/Public matches.
        // fails_when: Opted-out or non-Arcade in-game players trigger unnecessary CreateMatch, or Lobby player fails to pre-warm.
        // seam: CountOptedInLobbyOrArcadeDemandForRegion + EnsureArcadeMatchesRunning.
        RecordingSession srv = RegisterArcadeServer();

        // Opted-out user + user playing non-Arcade Ranked match -> demand stays 0!
        RegisterClient(101, "OptOutUser", optIn: false);
        RecordingSession busyPlayer = RegisterClient(102, "InRankedUser", optIn: true);
        busyPlayer.ClientMetadata!.MatchID = 8888;
        busyPlayer.ClientMetadata.LastKnownClientState = ChatProtocol.ChatClientStatus.CHAT_CLIENT_STATUS_IN_GAME;

        await _coordinator.EvaluateTickAsync();
        CountCreateMatchPackets(srv).Should().Be(0);

        // Opted-in player logs into Lobby -> wakes #Q1 immediately!
        RegisterClient(103, "LobbyWaiter", region: "USE", optIn: true);
        await _coordinator.EvaluateTickAsync();

        CountCreateMatchPackets(srv).Should().Be(1);
        srv.ServerMetadata!.Status.Should().Be(ChatProtocol.ServerStatus.SERVER_STATUS_LOADING);
        List<string> cmds = ExtractRemoteCommands(srv);
        cmds.Should().Contain(c => c.Contains("Set kongor_rl_mode ", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ActiveMatch_FinishesCurrentRound_ThenReturnsToIdleHibernateWhenDemandDropsToZero()
    {
        // protects: Graceful spin-down — ongoing matches finish their round instead of hard-killing mid-game, and then hibernate cleanly.
        // fails_when: Active match aborts prematurely when Lobby drops to 0 OR rotates into an unrequested second match after finishing.
        // seam: EnsureArcadeMatchesRunning + OnArcadeMatchEnded.
        RecordingSession srv = RegisterArcadeServer();
        RecordingSession lobbyPlayer = RegisterClient(201, "LobbyUser", region: "USE", optIn: true);

        await _coordinator.EvaluateTickAsync();
        CountCreateMatchPackets(srv).Should().Be(1);

        // Server transitions into active match 4401
        int matchUpId = BitConverter.ToInt32(srv.SentPackets.Last(p => BitConverter.ToUInt16(p, 2) == ChatProtocol.ChatServerToGameServer.NET_CHAT_GS_CREATE_MATCH), 5);
        _coordinator.ConfirmArcadeMatchReady(srv, matchUpId, 4401);

        // Player logs off -> demand drops to 0 while round is active: round stays running!
        _clientSessions.TryRemove(lobbyPlayer.Account!.Name, out _);
        await _coordinator.EvaluateTickAsync();
        _coordinator.GetMetricsSnapshot().ActiveArcadeMatchesCount.Should().Be(1);
        CountCreateMatchPackets(srv).Should().Be(1);

        // Round ends naturally -> server returns to IDLE and does NOT start a second match!
        _coordinator.OnArcadeMatchEnded(srv, 4401, "natural_finish");
        _fakeTime.Advance(TimeSpan.FromSeconds(15));
        await _coordinator.EvaluateTickAsync();

        CountCreateMatchPackets(srv).Should().Be(1);
        srv.ServerMetadata!.Status.Should().Be(ChatProtocol.ServerStatus.SERVER_STATUS_IDLE);
    }

    [Fact]
    public async Task SelfPlayMode_AndStaffArcadeChatCommand_AlternateTeamsAndPersistRedis()
    {
        // protects: Staff-only /arcade selfplay gating, Redis kongor:queue_arcade:idle_mode sync, and symmetric 5v5 Team 1 <-> Team 2 alternation.
        // fails_when: Non-staff triggers selfplay, Redis toggle is ignored, or Candidate stays pinned to one team side.
        // seam: TryHandleArcadeChatCommand + EnsureArcadeMatchesRunning + ResolveEffectiveEnabledAsync.
        RecordingSession srv = RegisterArcadeServer();
        RecordingSession regular = RegisterClient(301, "NormalUser", AccountType.Normal, optIn: false);
        RecordingSession staff = RegisterClient(302, "StaffAdmin", AccountType.Staff, optIn: false);

        // Non-staff denied
        _coordinator.TryHandleArcadeChatCommand(regular, "/arcade selfplay on").Should().BeTrue();
        _coordinator.ResolveEffectiveIdleMode().Should().Be(ArcadeIdleMode.Hibernate);
        CountCreateMatchPackets(srv).Should().Be(0);

        // Staff enables SelfPlay -> persists Redis key & launches Match #1 with CandTeam=1
        _coordinator.TryHandleArcadeChatCommand(staff, "/arcade selfplay on").Should().BeTrue();
        _redisStrings[QueueArcadeCoordinatorService.RedisIdleModeKey].ToString().Should().Be("selfplay");
        _coordinator.ResolveEffectiveIdleMode().Should().Be(ArcadeIdleMode.SelfPlay);

        CountCreateMatchPackets(srv).Should().Be(1);
        ExtractRemoteCommands(srv).Should().Contain(c => c.Contains("Set kongor_rl_mode 2", StringComparison.Ordinal) &&
                                                          c.Contains("Set kongor_rl_cand_team 1", StringComparison.Ordinal));
        QueueArcadeCoordinatorService.RlSelfPlayMapPool.Should().Contain(srv.ServerMetadata!.MapName);

        // End Match #1 -> launches Match #2 with CandTeam=2
        int matchUp1 = BitConverter.ToInt32(srv.SentPackets.Last(p => BitConverter.ToUInt16(p, 2) == ChatProtocol.ChatServerToGameServer.NET_CHAT_GS_CREATE_MATCH), 5);
        _coordinator.ConfirmArcadeMatchReady(srv, matchUp1, 5501);
        _fakeTime.Advance(TimeSpan.FromSeconds(15));
        _coordinator.OnArcadeMatchEnded(srv, 5501, "selfplay_round_1");

        CountCreateMatchPackets(srv).Should().Be(2);
        ExtractRemoteCommands(srv).Should().Contain(c => c.Contains("Set kongor_rl_mode 2", StringComparison.Ordinal) &&
                                                          c.Contains("Set kongor_rl_cand_team 2", StringComparison.Ordinal));
        _coordinator.GetMetricsSnapshot().SelfPlayMatchesStartedTotal.Should().Be(2);
    }
}
