// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Collections.Concurrent;
using System.Net;
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
using TRANSMUTANSTEIN.ChatServer.CommandProcessors.MatchState;
using TRANSMUTANSTEIN.ChatServer.CommandProcessors.Results;
using TRANSMUTANSTEIN.ChatServer.Domain.Communication;
using TRANSMUTANSTEIN.ChatServer.Domain.Core;
using TRANSMUTANSTEIN.ChatServer.Domain.Matchmaking;
using TRANSMUTANSTEIN.ChatServer.Internals;
using TRANSMUTANSTEIN.ChatServer.Services;
using TRANSMUTANSTEIN.ChatServer.Utilities;
using Xunit;
using DbRole = MERRICK.DatabaseContext.Entities.Utility.Role;

namespace TRANSMUTANSTEIN.ChatServer.Tests;

public sealed class QueueArcadeReadinessAndAbandonTests : IDisposable
{
    private sealed class ManualTimeProvider(DateTimeOffset startUtc) : TimeProvider
    {
        private DateTimeOffset _utcNow = startUtc;
        public override DateTimeOffset GetUtcNow() => _utcNow;
        public void Advance(TimeSpan delta) => _utcNow = _utcNow.Add(delta);
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

    private sealed class RecordingQueueArcadeService : IQueueArcadeService
    {
        public List<(ChatSession Session, int MatchId, string Reason)> EndedCalls { get; } = [];
        public IReadOnlyList<string> RotationHistory => [];
        public QueueArcadeMetricsSnapshot GetMetricsSnapshot() =>
            new(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, null);
        public string SelectNextMap(string? previousMap = null) => "midwars";
        public void OnArcadeServerHandshake(ChatSession serverSession) { }
        public void ConfirmArcadeMatchReady(ChatSession serverSession, int matchupId, int matchId) { }
        public void NotifyArcadeServerBotReadiness(int serverId, int activeBotCount, bool explicitReadyAck = false) { }
        public void OnArcadeMatchEnded(ChatSession serverSession, int matchId, string reason) =>
            EndedCalls.Add((serverSession, matchId, reason));
        public void OnArcadeServerNotIdle(ChatSession serverSession, byte currentStatus, int matchUpId) { }
        public bool ReleasePlayerFromArcade(int accountId, string reason, bool pauseCurrentQueueWait = false) => false;
        public void NotifyRankedMatchAllocating(IReadOnlyList<MatchmakingGroup> groups, string resolvedMapName) { }
        public void OnPlayersPulledToRankedMatch(IReadOnlyList<MatchmakingGroupMember> members, int rankedMatchId) { }
        public bool TryHandleArcadeChatCommand(ChatSession clientSession, string rawMessage) => false;
        public Task EvaluateTickAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private static readonly DbRole SharedUserRole = new() { ID = 2, Name = "User" };
    private readonly ManualTimeProvider _fakeTime = new(new DateTimeOffset(2026, 11, 17, 12, 0, 0, TimeSpan.Zero));
    private readonly ConcurrentDictionary<string, ChatSession> _clientSessions = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<int, ChatSession> _matchServerSessions = new();
    private readonly ConcurrentDictionary<int, ChatSession> _managerSessions = new();
    private readonly ConcurrentDictionary<string, ChatChannel> _channelsByName = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<int, ChatChannel> _channelsById = new();
    private readonly Mock<IChatContext> _mockChatContext = new();
    private readonly Mock<IMatchmakingService> _mockMatchmaking = new();
    private readonly ConcurrentDictionary<int, MatchmakingGroup> _groups = new();
    private readonly Mock<IDatabase> _mockRedis = new();
    private readonly Mock<IConnectionMultiplexer> _mockMux = new();
    private readonly Mock<IServer> _mockRedisServer = new();
    private readonly ServiceProvider _serviceProvider;
    private readonly TCPServer _tcpServer;

    public QueueArcadeReadinessAndAbandonTests()
    {
        Log.Initialise(NullLogger.Instance);
        _mockChatContext.SetupGet(c => c.ClientChatSessions).Returns(_clientSessions);
        _mockChatContext.SetupGet(c => c.MatchServerChatSessions).Returns(_matchServerSessions);
        _mockChatContext.SetupGet(c => c.MatchServerManagerChatSessions).Returns(_managerSessions);
        _mockChatContext.SetupGet(c => c.ChatChannels).Returns(_channelsByName);
        _mockChatContext.SetupGet(c => c.ChatChannelsByID).Returns(_channelsById);
        _mockMatchmaking.SetupGet(m => m.Groups).Returns(_groups);

        IPEndPoint redisEndpoint = new(IPAddress.Loopback, 6379);
        _mockMux.Setup(m => m.GetEndPoints(It.IsAny<bool>())).Returns([redisEndpoint]);
        _mockMux.Setup(m => m.GetServer(redisEndpoint, It.IsAny<object?>())).Returns(_mockRedisServer.Object);
        _mockRedisServer.Setup(s => s.Keys(
                It.IsAny<int>(),
                It.IsAny<RedisValue>(),
                It.IsAny<int>(),
                It.IsAny<long>(),
                It.IsAny<int>(),
                It.IsAny<CommandFlags>()))
            .Returns([]);
        _mockRedis.SetupGet(db => db.Multiplexer).Returns(_mockMux.Object);

        ServiceCollection services = new();
        services.AddSingleton(_mockChatContext.Object);
        services.AddSingleton(_mockMatchmaking.Object);
        services.AddSingleton(Mock.Of<IDiscordPresencePublisherService>());
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        _serviceProvider = services.BuildServiceProvider();
        _tcpServer = new TCPServer(IPAddress.Loopback, 0);
    }

    public void Dispose()
    {
        _tcpServer.Dispose();
        _serviceProvider.Dispose();
    }

    private static ChatBuffer BuildAbandonPacket(byte[]? payloadBytes)
    {
        ChatBuffer writer = new();
        writer.WriteInt16((short)ChatProtocol.GameServerToChatServer.NET_CHAT_GS_ABANDON_MATCH);
        if (payloadBytes is { Length: > 0 })
        {
            foreach (byte b in payloadBytes)
            {
                writer.WriteInt8(b);
            }
        }
        return writer;
    }

    private static MatchServerChatSessionMetadata CreateArcadeMetadata(int serverId, int matchId = 0) => new()
    {
        ServerID = serverId,
        SessionCookie = $"cookie-{serverId}",
        ChatProtocolVersion = 68,
        Status = ChatProtocol.ServerStatus.SERVER_STATUS_IDLE,
        Address = "127.0.0.1",
        Port = 21485,
        LocalAddress = "127.0.0.1",
        LocalPort = 11485,
        Location = "USE",
        IsQueueArcade = true,
        MatchID = matchId,
        MapName = "midwars"
    };

    private RecordingSession RegisterOptedInLobbyClient(int accountId, string name)
    {
        RecordingSession client = new(_tcpServer, _serviceProvider)
        {
            Account = new Account
            {
                ID = accountId,
                Name = name,
                Type = AccountType.Normal,
                AscensionLevel = 5,
                User = new User
                {
                    ID = accountId,
                    EmailAddress = $"{name.ToLowerInvariant()}@kongor.local",
                    RoleID = SharedUserRole.ID,
                    Role = SharedUserRole,
                    SRPPasswordSalt = "salt",
                    SRPPasswordHash = "hash",
                    PBKDF2PasswordHash = "hash"
                }
            },
            ClientMetadata = new ClientChatSessionMetadata
            {
                RemoteIP = "127.0.0.1",
                SessionCookie = $"client-cookie-{accountId}",
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
                ClientLanguage = "en",
                QueueArcadeOptIn = true
            }
        };
        client.MarkAuthenticated(ChatPeerRole.Client);
        _clientSessions[name] = client;
        return client;
    }

    [Theory]
    [InlineData(new byte[] { 0x01 })] // Canonical K2 CHostServer::EndGame 1-byte EDisconnectReason
    [InlineData(new byte[] { 0x39, 0x05, 0x00, 0x00 })] // Legacy 4-byte MatchID payload
    [InlineData(new byte[] { })] // Truncated empty payload
    [InlineData(new byte[] { 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08 })] // Oversized fuzz payload
    public void AbandonMatch_0x0504_VariousAndMalformedPayloads_DoNotThrowAndNotifyQueueArcade(byte[] payload)
    {
        // protects: 0x0504 (NET_CHAT_GS_ABANDON_MATCH) verified 3-byte canonical framing + length-fuzz safety + Queue Arcade lifecycle notification.
        // fails_when: Canonical 1-byte EDisconnectReason throws IndexOutOfRangeException on ReadInt32(), or AbandonMatch fails to notify IQueueArcadeService.
        // seam: Real AbandonMatch.Process + ChatBuffer + RecordingQueueArcadeService.
        RecordingQueueArcadeService recordingArcade = new();
        AbandonMatch processor = new(recordingArcade);

        RecordingSession serverSession = new(_tcpServer, _serviceProvider)
        {
            ServerMetadata = CreateArcadeMetadata(7001, matchId: 1434)
        };
        serverSession.MarkAuthenticated(ChatPeerRole.MatchServer);

        ChatBuffer buffer = BuildAbandonPacket(payload);
        Action act = () => processor.Process(serverSession, buffer);
        act.Should().NotThrow();

        if (payload.Length > 0)
        {
            int expectedMatchId = payload.Length == 4 ? 1337 : 1434;
            recordingArcade.EndedCalls.Should().ContainSingle(c =>
                ReferenceEquals(c.Session, serverSession) &&
                c.MatchId == expectedMatchId &&
                c.Reason == "abandon_match");
        }
        else
        {
            recordingArcade.EndedCalls.Should().BeEmpty();
        }
    }

    [Fact]
    public void AbandonMatch_0x0504_TruncatedHeaderLessThan2Bytes_ReturnsSafelyWithoutThrowing()
    {
        // protects: Layer 2B header boundary check when fewer than 2 bytes arrive.
        // fails_when: ReadCommand() throws on 1-byte or 0-byte truncated packet.
        // seam: Real AbandonMatch.Process with 1-byte ChatBuffer.
        RecordingQueueArcadeService recordingArcade = new();
        AbandonMatch processor = new(recordingArcade);
        RecordingSession session = new(_tcpServer, _serviceProvider)
        {
            ServerMetadata = CreateArcadeMetadata(7002)
        };
        session.MarkAuthenticated(ChatPeerRole.MatchServer);

        ChatBuffer buffer = new();
        buffer.WriteInt8(0x04);

        Action act = () => processor.Process(session, buffer);
        act.Should().NotThrow();
        recordingArcade.EndedCalls.Should().BeEmpty();
    }

    [Fact]
    public async Task QueueArcadeCoordinator_WhenMatchIdIsZeroAfterAnnounceMatch_NeverSynthesizesFakeMatchIdAndSeparatesAuthDegradedFromQuarantine()
    {
        // protects: Layer 2B invariant — never synthesize fake 9_000_000+ MatchIDs when f=start_game failed auth (MatchID <= 0),
        //           never false-quarantine AuthDegraded servers under LoadingQuarantine, and allow abandon_match cleanup when MatchId <= 0.
        // fails_when: QueueArcadeCoordinatorService fabricates fake MatchID >= 9_000_000, quarantines an AuthDegraded server for 60s,
        //             or ignores OnArcadeMatchEnded(..., 0, "abandon_match").
        // seam: Real QueueArcadeCoordinatorService + ConfirmArcadeMatchReady + EvaluateTickAsync + OnArcadeMatchEnded.
        IConfiguration config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["QueueArcade:Enabled"] = "true",
                ["QueueArcade:LoadingTimeoutSeconds"] = "5",
                ["QueueArcade:LoadingTimeoutQuarantineSeconds"] = "60",
                ["QueueArcade:RoundDurationSeconds"] = "600"
            })
            .Build();

        using QueueArcadeCoordinatorService coordinator = new(
            config,
            _mockChatContext.Object,
            _mockMatchmaking.Object,
            timeProvider: _fakeTime,
            distributedCache: _mockRedis.Object,
            randomSeed: 20261117);

        RecordingSession serverSession = new(_tcpServer, _serviceProvider)
        {
            ServerMetadata = CreateArcadeMetadata(8101)
        };
        serverSession.MarkAuthenticated(ChatPeerRole.MatchServer);
        _matchServerSessions[8101] = serverSession;
        coordinator.OnArcadeServerHandshake(serverSession);
        RegisterOptedInLobbyClient(1001, "ArcadeDemandUser");

        // 1. Coordinator creates lobby on #Q1
        await coordinator.EvaluateTickAsync();
        coordinator.GetActiveArcadeMatchesSnapshot().Should().ContainSingle(s => s.ServerId == 8101);
        int matchUpId = coordinator.GetActiveArcadeMatchesSnapshot().Single().MatchUpId;

        // 2. Engine calls AnnounceMatch (ConfirmArcadeMatchReady) with MatchID=0 (start_game 401 failure) -> must NOT synthesize fake >= 9_000_000 ID!
        coordinator.ConfirmArcadeMatchReady(serverSession, matchUpId, matchId: 0);
        ActiveArcadeMatchSnapshot afterAnnounce =
            coordinator.GetActiveArcadeMatchesSnapshot().Single(s => s.ServerId == 8101);
        afterAnnounce.MatchId.Should().Be(0);
        afterAnnounce.MatchUpId.Should().Be(matchUpId);
        afterAnnounce.MatchId.Should().BeLessThan(9_000_000);

        // 3. Advance 10s (> LoadingTimeoutSeconds=5s): AuthDegraded path settles/resets WITHOUT putting server into 60s LoadingQuarantine,
        //    so after the 2s settle window (+3s) and server reporting IDLE, EvaluateTickAsync immediately spins up a fresh lobby (not blocked 60s!).
        _fakeTime.Advance(TimeSpan.FromSeconds(10));
        serverSession.SentPackets.Clear();
        await coordinator.EvaluateTickAsync();
        serverSession.SentPackets.Should().NotBeEmpty("AuthDegraded cleanup dispatches EndMatch / EndGame remote command");

        serverSession.ServerMetadata!.Status = ChatProtocol.ServerStatus.SERVER_STATUS_IDLE;
        _fakeTime.Advance(TimeSpan.FromSeconds(3));
        await coordinator.EvaluateTickAsync();
        coordinator.GetActiveArcadeMatchesSnapshot().Should().ContainSingle(s => s.ServerId == 8101);

        int secondMatchUpId = coordinator.GetActiveArcadeMatchesSnapshot().Single().MatchUpId;
        secondMatchUpId.Should().NotBe(matchUpId);

        // 4. Calling OnArcadeMatchEnded(session, 0, "abandon_match") cleans up the tracked MatchId<=0 entry
        //    (MatchesCompletedTotal increments and MatchUpId rotates to a fresh lobby on the idle server).
        coordinator.OnArcadeMatchEnded(serverSession, 0, "abandon_match");
        coordinator.GetMetricsSnapshot().MatchesCompletedTotal.Should().Be(1);
        ActiveArcadeMatchSnapshot rotatedEntry = coordinator.GetActiveArcadeMatchesSnapshot().Single(s => s.ServerId == 8101);
        rotatedEntry.MatchUpId.Should().NotBe(secondMatchUpId);
        rotatedEntry.StartedAtUtc.Should().BeNull();

        // 5. Contrast with true wedged LOADING (!StartedAtUtc.HasValue -> LoadingQuarantine 60s):
        _fakeTime.Advance(TimeSpan.FromSeconds(10));
        await coordinator.EvaluateTickAsync();
        coordinator.GetActiveArcadeMatchesSnapshot().Should().BeEmpty("wedged LOADING server should be evicted into 60s LoadingQuarantine");

        serverSession.ServerMetadata!.Status = ChatProtocol.ServerStatus.SERVER_STATUS_IDLE;
        _fakeTime.Advance(TimeSpan.FromSeconds(5));
        await coordinator.EvaluateTickAsync();
        coordinator.GetActiveArcadeMatchesSnapshot().Should().BeEmpty("LoadingQuarantine must block re-allocation for 60s");
    }

    [Fact]
    public async Task MatchIdResult_WhenMatchStartDataIsNull_LogsWarningSafelyWithoutThrowing()
    {
        // protects: Layer 5C clean Warning handling in MatchIdResult when f=new_session/start_game left MatchStartData absent in Redis.
        // fails_when: Null MatchStartData throws NullReferenceException or fails to tolerate missing cache entry.
        // seam: Real MatchIdResult.Process with ChatBuffer containing MIDR_ERROR_UNSUCCESSFUL failure payload.
        _mockRedis
            .Setup(db => db.StringGetAsync(It.IsAny<RedisKey>(), It.IsAny<CommandFlags>()))
            .ReturnsAsync(RedisValue.Null);

        MatchIdResult processor = new(_mockRedis.Object);
        RecordingSession session = new(_tcpServer, _serviceProvider)
        {
            ServerMetadata = CreateArcadeMetadata(9001)
        };
        session.MarkAuthenticated(ChatPeerRole.MatchServer);

        ChatBuffer buffer = new();
        buffer.WriteInt16((short)ChatProtocol.GameServerToChatServer.NET_CHAT_GS_MATCH_ID_RESULT);
        buffer.WriteInt8((byte)ChatProtocol.MatchIDResult.MIDR_ERROR_UNSUCCESSFUL);
        buffer.WriteInt32(42);

        Func<Task> act = async () => await processor.Process(session, buffer);
        await act.Should().NotThrowAsync();
    }

    private static List<string> DecodeSentConsoleCommands(RecordingSession serverSession)
    {
        List<string> commands = [];
        foreach (byte[] raw in serverSession.SentPackets)
        {
            if (raw.Length < 4)
            {
                continue;
            }

            ChatBuffer reader = new(raw.AsSpan(2));
            ushort cmd = reader.ReadCommand();
            if (cmd == (ushort)ChatProtocol.ChatServerToGameServer.NET_CHAT_GS_REMOTE_COMMAND)
            {
                _ = reader.ReadString(); // session cookie
                commands.Add(reader.ReadString());
            }
        }

        return commands;
    }

    [Fact]
    public async Task QueueArcadeCoordinator_LoadingStatusNeverPrematurelyUnlocksBots_AndPostAddBotActiveStatusUnlocksReadiness()
    {
        IConfiguration config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["QueueArcade:Enabled"] = "true",
                ["QueueArcade:BotReadinessFallbackTimeoutSeconds"] = "5.0",
                ["QueueArcade:MinQueueWaitBeforeArcadeSeconds"] = "1.0",
                ["QueueArcade:RoundDurationSeconds"] = "600"
            })
            .Build();

        using QueueArcadeCoordinatorService coordinator = new(
            config,
            _mockChatContext.Object,
            _mockMatchmaking.Object,
            timeProvider: _fakeTime,
            distributedCache: _mockRedis.Object,
            randomSeed: 20261119);

        RecordingSession serverSession = new(_tcpServer, _serviceProvider)
        {
            ServerMetadata = CreateArcadeMetadata(8201)
        };
        serverSession.MarkAuthenticated(ChatPeerRole.MatchServer);
        _matchServerSessions[8201] = serverSession;
        coordinator.OnArcadeServerHandshake(serverSession);

        RecordingSession queuedPlayer = RegisterOptedInLobbyClient(2101, "ArcadeBotGatePlayer");
        MatchmakingGroup group = new()
        {
            QueueStartTime = _fakeTime.GetUtcNow().AddSeconds(-30),
            Members =
            [
                new MatchmakingGroupMember(queuedPlayer)
                {
                    Slot = 0,
                    IsLeader = true,
                    IsReady = true,
                    IsInGame = false,
                    IsEligibleForMatchmaking = true,
                    Rating = 1550.0
                }
            ],
            Information = new MatchmakingGroupInformation
            {
                ClientVersion = "4.10.1.0",
                GroupType = ChatProtocol.TMMType.TMM_TYPE_PVP,
                GameType = ChatProtocol.TMMGameType.TMM_GAME_TYPE_NORMAL,
                MapName = "caldavar",
                GameModes = ["ap"],
                GameRegions = ["use"],
                Ranked = true,
                MatchFidelity = 1,
                BotDifficulty = 0,
                RandomizeBots = false
            }
        };
        _groups[71] = group;

        // 1. Allocate lobby on Server 8201 (MatchId is still -1, ExpectedBotCount is still 0)
        await coordinator.EvaluateTickAsync();
        int matchUpId = coordinator.GetActiveArcadeMatchesSnapshot().Single(s => s.ServerId == 8201).MatchUpId;

        // 2. Non-explicit status packets during SERVER_STATUS_LOADING must NOT poison BotsSeededReady=true
        coordinator.NotifyArcadeServerBotReadiness(8201, activeBotCount: 0, explicitReadyAck: false);
        coordinator.NotifyArcadeServerStatus(8201, matchId: -1, ChatProtocol.ServerStatus.SERVER_STATUS_LOADING, activeClientCount: 0);

        // 3. Match announces ready -> dispatches AddBot commands and sets ExpectedBotCount > 0
        coordinator.ConfirmArcadeMatchReady(serverSession, matchUpId, matchId: 4401);

        // At +50ms (< 150ms post-AddBot settle guard), active status with 0 human clients must NOT unlock readiness yet
        _fakeTime.Advance(TimeSpan.FromMilliseconds(50));
        coordinator.NotifyArcadeServerStatus(8201, matchId: 4401, ChatProtocol.ServerStatus.SERVER_STATUS_ACTIVE, activeClientCount: 0);
        await coordinator.EvaluateTickAsync();
        coordinator.GetMetricsSnapshot().PlayersPushedTotal.Should().Be(0);

        // At +170ms (>= 150ms post-AddBot settle), active server status confirms post-seeding tick and unlocks readiness without 5s fallback
        _fakeTime.Advance(TimeSpan.FromMilliseconds(120));
        coordinator.NotifyArcadeServerStatus(8201, matchId: 4401, ChatProtocol.ServerStatus.SERVER_STATUS_ACTIVE, activeClientCount: 0);
        await coordinator.EvaluateTickAsync();

        coordinator.GetMetricsSnapshot().PlayersPushedTotal.Should().Be(1);
        coordinator.GetMetricsSnapshot().BotReadinessFallbackTimeoutsTotal.Should().Be(0);
    }

    [Fact]
    public async Task QueueArcadeCoordinator_ReleasePlayerFromArcade_AppendsKickOnRankedPullAndSessionCleanupOnly()
    {
        IConfiguration config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["QueueArcade:Enabled"] = "true",
                ["QueueArcade:RoundDurationSeconds"] = "600"
            })
            .Build();

        using QueueArcadeCoordinatorService coordinator = new(
            config,
            _mockChatContext.Object,
            _mockMatchmaking.Object,
            timeProvider: _fakeTime,
            distributedCache: _mockRedis.Object,
            randomSeed: 20261119);

        RecordingSession serverSession = new(_tcpServer, _serviceProvider)
        {
            ServerMetadata = CreateArcadeMetadata(8202)
        };
        serverSession.MarkAuthenticated(ChatPeerRole.MatchServer);
        _matchServerSessions[8202] = serverSession;
        coordinator.OnArcadeServerHandshake(serverSession);

        RecordingSession pRanked = RegisterOptedInLobbyClient(3101, "RankedPulledUser");
        RecordingSession pCleanup = RegisterOptedInLobbyClient(3102, "SessionCleanupUser");
        RecordingSession pManual = RegisterOptedInLobbyClient(3103, "ManualLeftUser");

        await coordinator.EvaluateTickAsync();
        int matchUpId = coordinator.GetActiveArcadeMatchesSnapshot().Single(s => s.ServerId == 8202).MatchUpId;
        coordinator.ConfirmArcadeMatchReady(serverSession, matchUpId, matchId: 4402);
        serverSession.SentPackets.Clear();

        pRanked.ClientMetadata!.ActiveArcadeServerId = 8202;
        pRanked.ClientMetadata.ActiveArcadeMatchId = 4402;
        coordinator.ReleasePlayerFromArcade(3101, "ranked_match_99001", pauseCurrentQueueWait: false);

        pCleanup.ClientMetadata!.ActiveArcadeServerId = 8202;
        pCleanup.ClientMetadata.ActiveArcadeMatchId = 4402;
        coordinator.ReleasePlayerFromArcade(3102, "client_session_cleanup", pauseCurrentQueueWait: false);

        pManual.ClientMetadata!.ActiveArcadeServerId = 8202;
        pManual.ClientMetadata.ActiveArcadeMatchId = 4402;
        coordinator.ReleasePlayerFromArcade(3103, "client_left_match", pauseCurrentQueueWait: true);

        List<string> sentCmds = DecodeSentConsoleCommands(serverSession);
        sentCmds.Should().Contain("kongor_arcade_release 3101 kick");
        sentCmds.Should().Contain("kongor_arcade_release 3102 kick");
        sentCmds.Should().Contain("kongor_arcade_release 3103");
        sentCmds.Should().NotContain("kongor_arcade_release 3103 kick");
    }
}
