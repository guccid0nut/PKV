// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ============================================================================
// File: QueueArcadeSimulationTests.cs
// System: TRANSMUTANSTEIN ChatServer Tests - End-to-End Queue Mini-Game Push/Pull & Map Rotation Simulations
// Policy: Strict Explicit Typing (NO 'var')
// ============================================================================

using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Text;
using System.Xml;
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
using TRANSMUTANSTEIN.ChatServer.CommandProcessors.Channels;
using TRANSMUTANSTEIN.ChatServer.CommandProcessors.Communication;
using TRANSMUTANSTEIN.ChatServer.CommandProcessors.Matchmaking;
using TRANSMUTANSTEIN.ChatServer.CommandProcessors.MatchState;
using TRANSMUTANSTEIN.ChatServer.Domain.Communication;
using TRANSMUTANSTEIN.ChatServer.Domain.Core;
using TRANSMUTANSTEIN.ChatServer.Domain.Matchmaking;
using TRANSMUTANSTEIN.ChatServer.Internals;
using TRANSMUTANSTEIN.ChatServer.Services;
using TRANSMUTANSTEIN.ChatServer.Utilities;
using Xunit;
using Xunit.Abstractions;
using DbRole = MERRICK.DatabaseContext.Entities.Utility.Role;

namespace TRANSMUTANSTEIN.ChatServer.Tests.Services;

public sealed class QueueArcadeSimulationTests : IDisposable
{
    private sealed class SimulatedTimeProvider(DateTimeOffset initialUtc) : TimeProvider
    {
        private DateTimeOffset _utcNow = initialUtc;
        public override DateTimeOffset GetUtcNow() => _utcNow;
        public void Advance(TimeSpan delta) => _utcNow = _utcNow.Add(delta);
    }

    private readonly ITestOutputHelper _output;
    private readonly SimulatedTimeProvider _fakeTime = new(new DateTimeOffset(2026, 11, 7, 20, 0, 0, TimeSpan.Zero));
    private readonly Mock<IChatContext> _mockChatContext = new();
    private readonly Mock<IDiscordPresencePublisherService> _mockDiscord = new();
    private readonly ConcurrentDictionary<string, ChatSession> _clientSessions = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<int, ChatSession> _matchServerSessions = new();
    private readonly ConcurrentDictionary<int, ChatSession> _managerSessions = new();
    private readonly ConcurrentDictionary<string, ChatChannel> _channelsByName = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<int, ChatChannel> _channelsById = new();
    private readonly FloodPreventionService _floodPrevention = new(NullLogger<FloodPreventionService>.Instance);
    private readonly TCPServer _tcpServer;
    private readonly ServiceProvider _serviceProvider;
    private readonly IConfiguration _configuration;
    private readonly MatchmakingService _matchmakingService;
    private readonly QueueArcadeCoordinatorService _arcadeCoordinator;

    public QueueArcadeSimulationTests(ITestOutputHelper output)
    {
        _output = output;
        Log.Initialise(NullLogger.Instance);

        _mockChatContext.SetupGet(c => c.ClientChatSessions).Returns(_clientSessions);
        _mockChatContext.SetupGet(c => c.MatchServerChatSessions).Returns(_matchServerSessions);
        _mockChatContext.SetupGet(c => c.MatchServerManagerChatSessions).Returns(_managerSessions);
        _mockChatContext.SetupGet(c => c.ChatChannels).Returns(_channelsByName);
        _mockChatContext.SetupGet(c => c.ChatChannelsByID).Returns(_channelsById);

        _configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Matchmaking:PlayersPerTeam"] = "5",
                ["Matchmaking:InitialRatingSpread"] = "150",
                ["Matchmaking:MaxRatingSpread"] = "300",
                ["QueueArcade:Enabled"] = "true",
                ["QueueArcade:MinQueueWaitBeforePushSeconds"] = "5",
                ["QueueArcade:BotReadinessFallbackTimeoutSeconds"] = "3.0",
                ["QueueArcade:WastedLoadPlayerThreshold"] = "8",
                ["QueueArcade:RoundDurationSeconds"] = "600"
            })
            .Build();

        _matchmakingService = new MatchmakingService(_configuration, _mockChatContext.Object, timeProvider: _fakeTime);
        _arcadeCoordinator = new QueueArcadeCoordinatorService(
            _configuration,
            _mockChatContext.Object,
            _matchmakingService,
            timeProvider: _fakeTime,
            randomSeed: 20261107);

        ServiceCollection services = new();
        services.AddSingleton(_mockChatContext.Object);
        services.AddSingleton(_mockDiscord.Object);
        services.AddSingleton<IMatchmakingService>(_matchmakingService);
        services.AddSingleton<IQueueArcadeService>(_arcadeCoordinator);
        services.AddSingleton(_floodPrevention);
        services.AddSingleton<ILogger<ChatSession>>(NullLogger<ChatSession>.Instance);
        _serviceProvider = services.BuildServiceProvider();

        _tcpServer = new TCPServer(IPAddress.Loopback, 0);
    }

    public void Dispose()
    {
        _arcadeCoordinator.Dispose();
        _matchmakingService.Dispose();
        _floodPrevention.Dispose();
        _serviceProvider.Dispose();
    }

    private sealed class SimulatedChatSession(TCPServer server, IServiceProvider serviceProvider)
        : ChatSession(server, serviceProvider)
    {
        private readonly object _packetLock = new();
        public bool ConnectedFlag { get; set; } = true;
        public List<byte[]> SentPackets { get; } = [];

        public override bool IsSessionConnected => ConnectedFlag;

        public override bool SendAsync(ReadOnlySpan<byte> buffer)
        {
            byte[] copy = buffer.ToArray();
            lock (_packetLock)
            {
                SentPackets.Add(copy);
            }
            return true;
        }

        public List<byte[]> SnapshotPackets()
        {
            lock (_packetLock)
            {
                return [.. SentPackets];
            }
        }
    }

    private sealed record DecodedAutoConnectPacket(
        byte MatchType,
        int MatchId,
        string Address,
        short Port,
        int ChallengeSalt);

    private sealed record DecodedCreateMatchPacket(
        byte MatchType,
        int MatchUpId,
        string MatchNamePrefix,
        string MatchSettings,
        byte AssignedHumanCount);

    private SimulatedChatSession RegisterGameServer(int serverId, string serverName, int port, int localPort, string location = "use")
    {
        bool isArcade = MatchServerChatSessionMetadata.DetectQueueArcade(serverName, null, port, localPort);
        SimulatedChatSession session = new(_tcpServer, _serviceProvider)
        {
            Account = new Account
            {
                ID = 9000 + serverId,
                Name = $"HOST_{serverId}",
                Type = AccountType.ServerHost,
                User = new User
                {
                    ID = 9000 + serverId,
                    EmailAddress = $"host_{serverId}@kongor.test",
                    Role = new DbRole { ID = 2, Name = "User" },
                    SRPPasswordSalt = "salt",
                    SRPPasswordHash = "hash",
                    PBKDF2PasswordHash = "hash"
                }
            },
            ServerMetadata = new MatchServerChatSessionMetadata
            {
                ServerID = serverId,
                SessionCookie = $"srv_cookie_{serverId}",
                ChatProtocolVersion = (int)ChatProtocol.CHAT_PROTOCOL_EXTERNAL_VERSION,
                Status = ChatProtocol.ServerStatus.SERVER_STATUS_IDLE,
                Address = "203.0.113.10",
                Port = port,
                LocalAddress = "127.0.0.1",
                LocalPort = localPort,
                Location = location,
                IsQueueArcade = isArcade
            }
        };
        session.MarkAuthenticated(ChatPeerRole.MatchServer);
        _matchServerSessions[serverId] = session;

        // Legacy simulation waves verify matchmaking, map rotation, overflow, and ticket mechanics assuming
        // an opted-in player is present in that region's Lobby (dedicated hibernation tests cover 0-player IDLE behavior).
        if (isArcade && !_clientSessions.ContainsKey($"LobbyObserver_{serverId}"))
        {
            SimulatedChatSession observer = CreateClientSession(990000 + serverId, $"LobbyObserver_{serverId}");
            observer.ClientMetadata!.ClientRegion = location;
            observer.ClientMetadata.QueueArcadeOptIn = true;
        }

        return session;
    }

    private (SimulatedChatSession Session, MatchmakingGroup Group) EnqueueSoloPlayer(
        int accountId,
        string name,
        double rating = 1550.0,
        ChatProtocol.TMMGameType gameType = ChatProtocol.TMMGameType.TMM_GAME_TYPE_CASUAL)
    {
        SimulatedChatSession session = CreateClientSession(accountId, name);
        MatchmakingGroupMember member = new(session)
        {
            Slot = 0,
            IsLeader = true,
            IsReady = true,
            IsInGame = false,
            IsEligibleForMatchmaking = true,
            Rating = rating
        };

        MatchmakingGroup group = new()
        {
            QueueStartTime = _fakeTime.GetUtcNow(),
            Members = [member],
            Information = CreateRankedGroupInfo(gameType)
        };

        _matchmakingService.Groups[accountId] = group;
        return (session, group);
    }

    private (List<SimulatedChatSession> Sessions, MatchmakingGroup Group) EnqueuePremadeParty(
        int groupKey,
        IReadOnlyList<(int AccountId, string Name)> players,
        ChatProtocol.TMMGameType gameType = ChatProtocol.TMMGameType.TMM_GAME_TYPE_CASUAL)
    {
        List<SimulatedChatSession> sessions = new(players.Count);
        List<MatchmakingGroupMember> members = new(players.Count);

        for (int i = 0; i < players.Count; i++)
        {
            SimulatedChatSession session = CreateClientSession(players[i].AccountId, players[i].Name);
            sessions.Add(session);
            members.Add(new MatchmakingGroupMember(session)
            {
                Slot = (byte)i,
                IsLeader = i == 0,
                IsReady = true,
                IsInGame = false,
                IsEligibleForMatchmaking = true,
                Rating = 1550.0
            });
        }

        MatchmakingGroup group = new()
        {
            QueueStartTime = _fakeTime.GetUtcNow(),
            Members = members,
            Information = CreateRankedGroupInfo(gameType)
        };

        _matchmakingService.Groups[groupKey] = group;
        return (sessions, group);
    }

    private SimulatedChatSession CreateClientSession(int accountId, string name)
    {
        SimulatedChatSession session = new(_tcpServer, _serviceProvider)
        {
            Account = new Account
            {
                ID = accountId,
                Name = name,
                User = new User
                {
                    ID = accountId,
                    EmailAddress = $"{name.ToLowerInvariant()}@kongor.test",
                    Role = new DbRole { ID = 2, Name = "User" },
                    SRPPasswordSalt = "salt",
                    SRPPasswordHash = "hash",
                    PBKDF2PasswordHash = "hash"
                }
            },
            ClientMetadata = new ClientChatSessionMetadata
            {
                RemoteIP = "127.0.0.1",
                SessionCookie = $"cl_cookie_{accountId}",
                SessionAuthenticationHash = $"cl_hash_{accountId}",
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
                ClientRegion = "use",
                ClientLanguage = "en"
            }
        };
        session.MarkAuthenticated(ChatPeerRole.Client);
        _clientSessions[$"cl_cookie_{accountId}"] = session;
        return session;
    }

    private static MatchmakingGroupInformation CreateRankedGroupInfo(
        ChatProtocol.TMMGameType gameType = ChatProtocol.TMMGameType.TMM_GAME_TYPE_CASUAL) => new()
    {
        ClientVersion = "4.10.1.0",
        GroupType = ChatProtocol.TMMType.TMM_TYPE_PVP,
        GameType = gameType,
        MapName = "caldavar",
        GameModes = ["ap"],
        GameRegions = ["use"],
        Ranked = true,
        MatchFidelity = 1,
        BotDifficulty = 0,
        RandomizeBots = false
    };

    private void SimulateServerAnnouncesMatch(
        SimulatedChatSession serverSession,
        int matchUpId,
        int realMatchId,
        QueueArcadeCoordinatorService? coordinator = null)
    {
        AnnounceMatch processor = new(_matchmakingService, coordinator ?? _arcadeCoordinator);
        ChatBuffer buf = new();
        buf.WriteCommand(ChatProtocol.GameServerToChatServer.NET_CHAT_GS_ANNOUNCE_MATCH);
        buf.WriteInt32(matchUpId);
        buf.WriteInt32(0); // Challenge
        buf.WriteInt32(0); // GroupIDs count
        buf.WriteInt32(realMatchId);
        processor.Process(serverSession, buf);
    }

    private void SimulateServerMatchComplete(
        SimulatedChatSession serverSession,
        int realMatchId,
        IReadOnlyList<int> accountIds,
        QueueArcadeCoordinatorService? coordinator = null)
    {
        MatchComplete processor = new(
            NullLogger<MatchComplete>.Instance,
            _matchmakingService,
            _mockChatContext.Object,
            telemetryPublisher: null,
            queueArcadeService: coordinator ?? _arcadeCoordinator);

        ChatBuffer buf = new();
        buf.WriteCommand(ChatProtocol.GameServerToChatServer.NET_CHAT_GS_MATCH_ENDED);
        buf.WriteInt32(realMatchId);
        buf.WriteInt8((byte)ChatProtocol.MatchEndedReason.MATCH_ENDED_FINISHED);
        buf.WriteInt8(1); // WinningTeam
        buf.WriteInt8((byte)accountIds.Count);
        foreach (int id in accountIds)
        {
            buf.WriteInt32(id);
        }
        processor.Process(serverSession, buf);
    }

    private static List<string> ExtractRemoteCommands(SimulatedChatSession serverSession)
    {
        List<string> results = [];
        foreach (byte[] raw in serverSession.SnapshotPackets())
        {
            if (raw.Length < 4) continue;
            ChatBuffer reader = new(raw.AsSpan(2));
            ushort cmd = reader.ReadCommand();
            if (cmd != (ushort)ChatProtocol.ChatServerToGameServer.NET_CHAT_GS_REMOTE_COMMAND) continue;

            _ = reader.ReadString(); // cookie
            results.Add(reader.ReadString());
        }
        return results;
    }

    private static List<DecodedCreateMatchPacket> ExtractCreateMatchPackets(SimulatedChatSession serverSession)
    {
        List<DecodedCreateMatchPacket> results = [];
        foreach (byte[] raw in serverSession.SnapshotPackets())
        {
            if (raw.Length < 4) continue;
            ChatBuffer reader = new(raw.AsSpan(2));
            ushort cmd = reader.ReadCommand();
            if (cmd != (ushort)ChatProtocol.ChatServerToGameServer.NET_CHAT_GS_CREATE_MATCH) continue;

            byte matchType = (byte)reader.ReadInt8();
            int matchUpId = reader.ReadInt32();
            _ = reader.ReadInt32(); // unknown1
            _ = reader.ReadInt32(); // password
            string prefix = reader.ReadString();
            string settings = reader.ReadString();
            _ = reader.ReadInt8(); // useNewMmr
            _ = reader.ReadInt8(); // unknown3
            byte playerCount = (byte)reader.ReadInt8();
            for (int p = 0; p < playerCount; p++)
            {
                _ = reader.ReadInt32(); // accountId
                _ = reader.ReadInt8();  // team
                _ = reader.ReadInt8();  // slot
                _ = reader.ReadInt8();  // groupSize
                _ = reader.ReadInt32(); // loadingPercent / rating float
                _ = reader.ReadInt32(); // float2
                _ = reader.ReadInt8();  // flag1
                _ = reader.ReadInt8();  // flag2
                _ = reader.ReadInt8();  // flag3
            }

            int groupIdsCount = reader.ReadInt32(); // Mandatory K2 CServerChatConnection GroupIds Count trailer!
            groupIdsCount.Should().Be(0, "K2 CServerChatConnection::ProcessPacket reads Int32 GroupIdsCount after player entries");
            reader.HasRemainingData().Should().BeFalse("NET_CHAT_GS_CREATE_MATCH must have exact wire length with zero trailing bytes");
            results.Add(new DecodedCreateMatchPacket(matchType, matchUpId, prefix, settings, playerCount));
        }
        return results;
    }

    private static List<DecodedAutoConnectPacket> ExtractAutoConnectPackets(SimulatedChatSession clientSession)
    {
        List<DecodedAutoConnectPacket> results = [];
        foreach (byte[] raw in clientSession.SnapshotPackets())
        {
            if (raw.Length < 4) continue;
            ChatBuffer reader = new(raw.AsSpan(2));
            ushort cmd = reader.ReadCommand();
            if (cmd != 0x0062) continue;

            byte matchType = (byte)reader.ReadInt8();
            int matchId = reader.ReadInt32();
            string address = reader.ReadString();
            short port = reader.ReadInt16();
            int salt = reader.ReadInt32();
            results.Add(new DecodedAutoConnectPacket(matchType, matchId, address, port, salt));
        }
        return results;
    }

    private static List<string> ExtractSystemWhispers(SimulatedChatSession clientSession)
    {
        List<string> results = [];
        foreach (byte[] raw in clientSession.SentPackets)
        {
            if (raw.Length < 4) continue;
            ChatBuffer reader = new(raw.AsSpan(2));
            ushort cmd = reader.ReadCommand();
            if (cmd != (ushort)ChatProtocol.Command.CHAT_CMD_WHISPER) continue;

            string sender = reader.ReadString();
            string text = reader.ReadString();
            results.Add($"{sender}: {text}");
        }
        return results;
    }

    [Fact]
    public async Task Simulation_Wave1_AutoPushQueuedPlayers_PreservesQueueAndEnforcesWastedLoadGate()
    {
        // protects: Automatic push into ongoing Queue Arcade mini-game after 5s wait while retaining QueueStartTime,
        //           and Wasted-Load Gate blocking pushes when compatible queued players reach >= 8/10.
        // fails_when: QueueStartTime is cleared on Arcade push, wrong 0x0062 matchType is sent, kongor_arcade_reserve
        //             is omitted, or players 8..9 get pushed right before Ranked pops.
        // seam: Real QueueArcadeCoordinatorService + AnnounceMatch binary wire packets.
        SimulatedChatSession arcadeServer = RegisterGameServer(251, "QUEUE ARCADE #1", 21485, 11485);
        _arcadeCoordinator.OnArcadeServerHandshake(arcadeServer);

        List<DecodedCreateMatchPacket> createPackets = ExtractCreateMatchPackets(arcadeServer);
        createPackets.Should().ContainSingle();
        int arcadeMatchUpId = createPackets[0].MatchUpId;
        arcadeMatchUpId.Should().BeLessThan(0, "Queue Arcade correlation IDs must be negative so they never collide with Ranked PendingMatches");

        SimulateServerAnnouncesMatch(arcadeServer, arcadeMatchUpId, realMatchId: 5001);
        _output.WriteLine($"[SIM-WAVE-1] Arcade Server #251 started Match #5001 on map '{arcadeServer.ServerMetadata.MapName}'");

        // Enqueue 6 solo players into Ranked 5v5 queue
        List<(SimulatedChatSession Session, MatchmakingGroup Group)> firstSix = [];
        for (int i = 1; i <= 6; i++)
        {
            firstSix.Add(EnqueueSoloPlayer(100 + i, $"QueuedHero_{i}"));
        }

        // At t = +2s (< 5s MinQueueWaitBeforePushSeconds), nobody should be pushed yet
        _fakeTime.Advance(TimeSpan.FromSeconds(2));
        await _arcadeCoordinator.EvaluateTickAsync();
        firstSix.All(p => ExtractAutoConnectPackets(p.Session).Count == 0).Should().BeTrue();

        // At t = +6s (>= 5s wait, 6/10 compatible players < 8 threshold), all 6 are pushed into Match #5001!
        _fakeTime.Advance(TimeSpan.FromSeconds(4));
        await _arcadeCoordinator.EvaluateTickAsync();

        foreach ((SimulatedChatSession session, MatchmakingGroup group) in firstSix)
        {
            group.QueueStartTime.Should().NotBeNull("Players pushed into Queue Arcade MUST remain in the Ranked matchmaking queue");
            session.ClientMetadata.ActiveArcadeMatchId.Should().Be(5001);

            List<DecodedAutoConnectPacket> connects = ExtractAutoConnectPackets(session);
            connects.Should().ContainSingle();
            connects[0].MatchType.Should().Be((byte)ChatProtocol.ArrangedMatchType.AM_UNSCHEDULED_MATCH);
            connects[0].MatchId.Should().Be(5001);
        }

        List<string> remoteCmds = ExtractRemoteCommands(arcadeServer);
        for (int i = 1; i <= 6; i++)
        {
            remoteCmds.Any(c => c.Contains($"kongor_arcade_reserve {100 + i} ", StringComparison.Ordinal))
                .Should().BeTrue($"Player {100 + i} must have a pre-reserved K2 CPlayer slot on the Arcade server");
        }

        // Balance check: 6 solo players should balance 3 Legion vs 3 Hellbourne
        ActiveArcadeMatchSnapshot activeSnap = _arcadeCoordinator.GetActiveArcadeMatchesSnapshot().Single();
        activeSnap.Team1HumanCount.Should().Be(3);
        activeSnap.Team2HumanCount.Should().Be(3);
        _output.WriteLine($"[SIM-WAVE-1] Pushed 6 queued players into Arcade Match #5001 (Legion={activeSnap.Team1HumanCount}, Hellbourne={activeSnap.Team2HumanCount})");

        // Now enqueue Players 7, 8, and 9 -> total compatible queued players in bucket = 9 (>= 8 WastedLoadPlayerThreshold)!
        (SimulatedChatSession p7, _) = EnqueueSoloPlayer(107, "QueuedHero_7");
        (SimulatedChatSession p8, _) = EnqueueSoloPlayer(108, "QueuedHero_8");
        (SimulatedChatSession p9, _) = EnqueueSoloPlayer(109, "QueuedHero_9");

        _fakeTime.Advance(TimeSpan.FromSeconds(6));
        await _arcadeCoordinator.EvaluateTickAsync();

        ExtractAutoConnectPackets(p7).Should().BeEmpty("At 9/10 compatible players, Wasted-Load Gate must block pushing new loaders");
        ExtractAutoConnectPackets(p8).Should().BeEmpty();
        ExtractAutoConnectPackets(p9).Should().BeEmpty();
        _arcadeCoordinator.GetMetricsSnapshot().WastedLoadBlocksTotal.Should().BeGreaterThanOrEqualTo(3);
        _output.WriteLine($"[SIM-WAVE-1] Wasted-Load Gate blocked {(_arcadeCoordinator.GetMetricsSnapshot().WastedLoadBlocksTotal)} imminent-pop pushes at 9/10 bucket occupancy.");
    }

    [Fact]
    public async Task Simulation_Wave2_PartyCohesion_PlacesAllPremadeGroupMembersOnSameArcadeTeam()
    {
        // protects: Premade party cohesion inside Queue Arcade so friends queuing together always land on the same team.
        // fails_when: Party members are split across Legion and Hellbourne during hot-push.
        // seam: Real QueueArcadeCoordinatorService party allocation & kongor_arcade_reserve command generation.
        SimulatedChatSession arcadeServer = RegisterGameServer(251, "QUEUE ARCADE #1", 21485, 11485);
        _arcadeCoordinator.OnArcadeServerHandshake(arcadeServer);
        int matchUpId = ExtractCreateMatchPackets(arcadeServer).Single().MatchUpId;
        SimulateServerAnnouncesMatch(arcadeServer, matchUpId, realMatchId: 5002);

        (List<SimulatedChatSession> partyA, _) = EnqueuePremadeParty(1, [(201, "Alpha1"), (202, "Alpha2"), (203, "Alpha3")]);
        (List<SimulatedChatSession> partyB, _) = EnqueuePremadeParty(2, [(204, "Bravo1"), (205, "Bravo2")]);

        _fakeTime.Advance(TimeSpan.FromSeconds(6));
        await _arcadeCoordinator.EvaluateTickAsync();

        partyA.All(s => s.ClientMetadata.ActiveArcadeMatchId == 5002).Should().BeTrue();
        partyB.All(s => s.ClientMetadata.ActiveArcadeMatchId == 5002).Should().BeTrue();

        List<string> remoteCmds = ExtractRemoteCommands(arcadeServer);
        string partyACmd = remoteCmds.Single(c => c.Contains("kongor_arcade_reserve 201 ", StringComparison.Ordinal));
        partyACmd.Should().Contain("kongor_arcade_reserve 201 1 \"Alpha1\"");
        partyACmd.Should().Contain("kongor_arcade_reserve 202 1 \"Alpha2\"");
        partyACmd.Should().Contain("kongor_arcade_reserve 203 1 \"Alpha3\"");

        string partyBCmd = remoteCmds.Single(c => c.Contains("kongor_arcade_reserve 204 ", StringComparison.Ordinal));
        partyBCmd.Should().Contain("kongor_arcade_reserve 204 2 \"Bravo1\"");
        partyBCmd.Should().Contain("kongor_arcade_reserve 205 2 \"Bravo2\"");

        _output.WriteLine("[SIM-WAVE-2] Verified 3-stack Party A -> Team 1 (Legion) and 2-stack Party B -> Team 2 (Hellbourne).");
    }

    [Fact]
    public async Task Simulation_Wave3_VoluntaryLeaveAndOptOut_StaysInQueueWithoutForceRePush_AndRejoinsOnCommand()
    {
        // protects: Voluntary Leave Mini-Game (LeftMatch 0x0013) keeping player in matchmaking queue without re-pushing
        //           them 1s later against their will, plus /arcade off|on|status chat slash commands.
        // fails_when: LeftMatch drops queue, omits kongor_arcade_release, or next tick immediately forces them back in.
        // seam: Real LeftMatch, SendChannelMessage, GroupLeaveQueue, and QueueArcadeCoordinatorService.
        SimulatedChatSession arcadeServer = RegisterGameServer(251, "QUEUE ARCADE #1", 21485, 11485);
        _arcadeCoordinator.OnArcadeServerHandshake(arcadeServer);
        int matchUpId = ExtractCreateMatchPackets(arcadeServer).Single().MatchUpId;
        SimulateServerAnnouncesMatch(arcadeServer, matchUpId, realMatchId: 5003);

        (SimulatedChatSession playerSession, MatchmakingGroup group) = EnqueueSoloPlayer(301, "ChillWaiter");
        DateTimeOffset originalQueueStart = group.QueueStartTime!.Value;

        _fakeTime.Advance(TimeSpan.FromSeconds(6));
        await _arcadeCoordinator.EvaluateTickAsync();
        ExtractAutoConnectPackets(playerSession).Should().HaveCount(1);
        playerSession.ClientMetadata.ActiveArcadeMatchId.Should().Be(5003);

        // Player voluntarily clicks [Leave Mini-Game] -> client sends CHAT_CMD_LEFT_GAME
        LeftMatch leftMatchProcessor = new(_mockDiscord.Object, _matchmakingService, _arcadeCoordinator);
        leftMatchProcessor.Process(playerSession, new ChatBuffer());

        group.QueueStartTime.Should().Be(originalQueueStart, "Leaving the mini-game must NEVER remove the player from the Ranked queue");
        playerSession.ClientMetadata.ActiveArcadeMatchId.Should().BeNull();
        ExtractRemoteCommands(arcadeServer).Any(c => c == "kongor_arcade_release 301").Should().BeTrue();

        // Advance 30 more seconds in the same queue wait: player MUST NOT be force-pushed back in!
        _fakeTime.Advance(TimeSpan.FromSeconds(30));
        await _arcadeCoordinator.EvaluateTickAsync();
        ExtractAutoConnectPackets(playerSession).Should().HaveCount(1, "Voluntarily leaving must pause auto-push for the remainder of the current queue wait");

        // Player types "/arcade on" in chat -> intercepted privately (never broadcast to channel) and re-enables push!
        ChatChannel generalChannel = ChatChannel.GetOrCreate(_mockChatContext.Object, playerSession, "KONGOR 1");
        generalChannel.TryAddMember(playerSession.Account.Name, new ChatChannelMember(playerSession, generalChannel));

        SendChannelMessage chatProcessor = new(_floodPrevention, _mockChatContext.Object, distributedState: null, queueArcadeService: _arcadeCoordinator);
        ChatBuffer slashOn = new();
        slashOn.WriteCommand(ChatProtocol.Command.CHAT_CMD_CHANNEL_MSG);
        slashOn.WriteString("/arcade on");
        slashOn.WriteInt32(generalChannel.ID);
        chatProcessor.Process(playerSession, slashOn);

        ExtractSystemWhispers(playerSession).Any(w => w.Contains("Queue Mini-Games ENABLED", StringComparison.Ordinal)).Should().BeTrue();

        _fakeTime.Advance(TimeSpan.FromSeconds(1));
        await _arcadeCoordinator.EvaluateTickAsync();
        ExtractAutoConnectPackets(playerSession).Should().HaveCount(2, "Typing /arcade on must let the queued player jump back into the mini-game");

        // Now test "/arcade off" -> disables opt-in and releases slot
        ChatBuffer slashOff = new();
        slashOff.WriteCommand(ChatProtocol.Command.CHAT_CMD_CHANNEL_MSG);
        slashOff.WriteString("/arcade off");
        slashOff.WriteInt32(generalChannel.ID);
        chatProcessor.Process(playerSession, slashOff);

        playerSession.ClientMetadata.QueueArcadeOptIn.Should().BeFalse();
        playerSession.ClientMetadata.ActiveArcadeMatchId.Should().BeNull();
        _output.WriteLine("[SIM-WAVE-3] Verified voluntary leave queue-retention, anti-repush guard, and /arcade on|off slash commands.");
    }

    [Fact]
    public async Task Simulation_Wave4_RankedQueuePop_AllocatesRegularServer_FiresPrePullAlert_AndPullsPlayersFromArcade()
    {
        // protects: Full push -> Ranked pop -> strict server isolation (Ranked NEVER steals Queue Arcade server) ->
        //           3s pre-transfer whisper alert -> ConfirmMatch auto-pull into Ranked Match while Arcade stays alive at 0 humans.
        // fails_when: Ranked allocates Arcade Server #251, omits pre-pull alert or kongor_arcade_release, or kills Arcade match.
        // seam: Real MatchmakingService + QueueArcadeCoordinatorService + AnnounceMatch end-to-end orchestration.
        SimulatedChatSession arcadeServer = RegisterGameServer(251, "QUEUE ARCADE #1", 21485, 11485);
        SimulatedChatSession rankedServer = RegisterGameServer(101, "KONGOR BATTLEGROUNDS #1", 21235, 11235);

        _arcadeCoordinator.OnArcadeServerHandshake(arcadeServer);
        int arcadeMatchUpId = ExtractCreateMatchPackets(arcadeServer).Single().MatchUpId;
        SimulateServerAnnouncesMatch(arcadeServer, arcadeMatchUpId, realMatchId: 5010);

        // Wave 4A: First 5 players enter Ranked queue and get pushed into Queue Arcade Match #5010
        List<(SimulatedChatSession Session, MatchmakingGroup Group)> allTen = [];
        for (int i = 1; i <= 5; i++)
        {
            allTen.Add(EnqueueSoloPlayer(400 + i, $"RankedCompetitor_{i}"));
        }

        _fakeTime.Advance(TimeSpan.FromSeconds(6));
        await _arcadeCoordinator.EvaluateTickAsync();
        allTen.Take(5).All(p => p.Session.ClientMetadata.ActiveArcadeMatchId == 5010).Should().BeTrue();
        _output.WriteLine("[SIM-WAVE-4] First 5 Ranked queue players pushed into Queue Arcade Match #5010.");

        // Also simulate Player 6 joining Arcade just 1.5s before Ranked pops (Mid-Load Overlap Guard test!)
        (SimulatedChatSession p6, MatchmakingGroup g6) = EnqueueSoloPlayer(406, "MidLoadCompetitor_6");
        allTen.Add((p6, g6));
        _fakeTime.Advance(TimeSpan.FromSeconds(6));
        await _arcadeCoordinator.EvaluateTickAsync();
        p6.ClientMetadata.ActiveArcadeMatchId.Should().Be(5010);

        // Wave 4B: Players 7..10 join Ranked queue -> 10/10 full Ranked match forms within 1.5s!
        _fakeTime.Advance(TimeSpan.FromMilliseconds(1500));
        for (int i = 7; i <= 10; i++)
        {
            allTen.Add(EnqueueSoloPlayer(400 + i, $"RankedCompetitor_{i}"));
        }

        List<MatchmakingGroup> tenGroups = allTen.Select(x => x.Group).ToList();
        bool allocated = await _matchmakingService.ProcessMatchForSimulationAsync(tenGroups);
        allocated.Should().BeTrue("Ranked 5v5 match must allocate on Regular Server #101");

        // Verify Ranked allocated Regular Server #101 (port 21235) and NEVER touched Queue Arcade Server #251!
        rankedServer.ServerMetadata.Status.Should().Be(ChatProtocol.ServerStatus.SERVER_STATUS_LOADING);
        arcadeServer.ServerMetadata.Status.Should().Be(ChatProtocol.ServerStatus.SERVER_STATUS_ACTIVE);

        List<DecodedCreateMatchPacket> rankedCreates = ExtractCreateMatchPackets(rankedServer);
        rankedCreates.Should().ContainSingle();
        int rankedMatchUpId = rankedCreates[0].MatchUpId;
        rankedMatchUpId.Should().BeGreaterThan(0);

        // Verify all 6 Arcade participants immediately received the ~3s pre-transfer whisper alert while Ranked loads!
        foreach ((SimulatedChatSession session, _) in allTen.Take(6))
        {
            ExtractSystemWhispers(session)
                .Any(w => w.Contains("[QUEUE ARCADE] RANKED MATCH FOUND", StringComparison.Ordinal))
                .Should().BeTrue("Every player inside Queue Arcade must receive the ~3s pre-transfer alert before Ranked pull");
        }
        _arcadeCoordinator.GetMetricsSnapshot().PrePullWarningsSentTotal.Should().Be(6);

        // Simulate 2 seconds of Ranked server map load (so p6 is at 1.5s + 2.0s = 3.5s < 4.0s mid-load guard!), then Ranked #101 announces Match #90001 ready!
        _fakeTime.Advance(TimeSpan.FromSeconds(2));
        SimulateServerAnnouncesMatch(rankedServer, rankedMatchUpId, realMatchId: 90001);

        // Assert all 10 players are now connected to Ranked Match #90001 (port 11235 local split-horizon) and all 6 Arcade slots were released!
        foreach ((SimulatedChatSession session, MatchmakingGroup group) in allTen)
        {
            group.QueueStartTime.Should().BeNull("Ranked queue pop must clear QueueStartTime");
            session.ClientMetadata.ActiveArcadeMatchId.Should().BeNull("Ranked pull must clear ActiveArcadeMatchId");
            session.ClientMetadata.MatchID.Should().Be(90001);

            DecodedAutoConnectPacket lastConnect = ExtractAutoConnectPackets(session).Last();
            lastConnect.MatchId.Should().Be(90001);
            lastConnect.Port.Should().Be(11235);
            lastConnect.MatchType.Should().Be(10); // AM_MATCHMAKING_CAMPAIGN / Normal Ranked
        }

        List<string> arcadeCmds = ExtractRemoteCommands(arcadeServer);
        for (int i = 1; i <= 6; i++)
        {
            arcadeCmds.Any(c => c == $"kongor_arcade_release {400 + i} kick").Should().BeTrue();
        }

        QueueArcadeMetricsSnapshot postPullMetrics = _arcadeCoordinator.GetMetricsSnapshot();
        postPullMetrics.RankedPullsTotal.Should().Be(6);
        postPullMetrics.MidLoadPullOverridesTotal.Should().Be(1, "Player 6 joined Arcade 3.5s (<4.0s) before Ranked pull");
        postPullMetrics.ActiveArcadeMatchesCount.Should().Be(1, "Queue Arcade Match #5010 must remain warm and running at 0 human players!");
        postPullMetrics.ActiveHumanPlayersInArcadeCount.Should().Be(0);

        _output.WriteLine($"[SIM-WAVE-4] Pulled 6 players from Arcade #5010 -> Ranked #90001; Arcade remains warm (ActiveHumanPlayers={postPullMetrics.ActiveHumanPlayersInArcadeCount}).");
    }

    [Fact]
    public async Task Simulation_Wave5_DefaultBotSafeMapRotation_Across20Rounds_CyclesAll10BotSafeMapsEvenly_AndNeverRepeatsBackToBack()
    {
        // protects: Default bot-safe Fisher-Yates shuffle-bag rotation across all 10 verified BotSafeArcadeMapPool maps
        //           (5v5 classic, TDM, Devowars, and 10v10 Chaos) even when only 1 player is queued, preventing 0xC0000374
        //           teambotbrain crashes on waypoint-less custom party maps; guarantees 0 back-to-back repeats, exact
        //           even coverage per cycle, DevourerBot single-hero overrides, and RoundDurationSeconds timer rotation.
        // fails_when: Unverified custom party maps (soccer/pebbles_duel/arena) roll by default, consecutive rounds repeat
        //             back-to-back, distribution skews, Cycle 2 repeats Cycle 1 deterministically, or wrong teamsize/bot args emit.
        // seam: Real MatchComplete + AnnounceMatch + QueueArcadeCoordinatorService across 20 complete rounds (2 full cycles).
        SimulatedChatSession arcadeServer = RegisterGameServer(251, "QUEUE ARCADE #1", 21485, 11485);
        _arcadeCoordinator.OnArcadeServerHandshake(arcadeServer);

        int poolSize = QueueArcadeCoordinatorService.BotSafeArcadeMapPool.Length;
        poolSize.Should().Be(9);
        QueueArcadeCoordinatorService.BotSafeArcadeMapPool.Should().NotContain("caldavar");
        QueueArcadeCoordinatorService.BotSafeArcadeMapPool.Should().NotContain("watchtower");
        QueueArcadeCoordinatorService.BotSafeArcadeMapPool.Should().Contain("caldavar_wtf");
        int totalRounds = poolSize * 2; // 18 rounds = 2 complete 9-map shuffle-bag cycles
        int simulatedMatchId = 6000;
        int expectedTimerRotations = 0;

        // Keep ONLY 1 solo player in queue the entire time to prove 10v10 & Brawl maps roll evenly without waiting for high pop!
        EnqueueSoloPlayer(501, "SoloWaiter_1");

        // Confirm Round 1
        DecodedCreateMatchPacket firstCreate = ExtractCreateMatchPackets(arcadeServer).Last();
        QueueArcadeCoordinatorService.IsBotActiveMap(arcadeServer.ServerMetadata.MapName).Should().BeTrue();
        AssertArcadeMatchSettingsForMap(arcadeServer, firstCreate, arcadeServer.ServerMetadata.MapName);
        SimulateServerAnnouncesMatch(arcadeServer, firstCreate.MatchUpId, ++simulatedMatchId);

        for (int r = 1; r < totalRounds; r++)
        {
            if (r % 5 == 0)
            {
                // Periodically trigger RoundDurationSeconds timer expiration ("StartPostMatch 1 false true") in addition to natural MatchComplete
                _fakeTime.Advance(TimeSpan.FromSeconds(605));
                await _arcadeCoordinator.EvaluateTickAsync();
                ExtractRemoteCommands(arcadeServer).Last().Should().Be(QueueArcadeCoordinatorService.EndArcadeMatchCommand);
                expectedTimerRotations++;
            }

            SimulateServerMatchComplete(arcadeServer, simulatedMatchId, []);
            DecodedCreateMatchPacket nextCreate = ExtractCreateMatchPackets(arcadeServer).Last();
            string selectedMap = arcadeServer.ServerMetadata.MapName;

            QueueArcadeCoordinatorService.BotSafeArcadeMapPool.Should().Contain(selectedMap);
            QueueArcadeCoordinatorService.IsBotActiveMap(selectedMap).Should().BeTrue("Default Queue Arcade pool must only schedule verified bot-active maps");
            AssertArcadeMatchSettingsForMap(arcadeServer, nextCreate, selectedMap);

            SimulateServerAnnouncesMatch(arcadeServer, nextCreate.MatchUpId, ++simulatedMatchId);
        }

        IReadOnlyList<string> history = _arcadeCoordinator.RotationHistory;
        history.Count.Should().Be(totalRounds);

        // 1. Core invariant: ZERO back-to-back repeats across all 20 consecutive rounds (including Round 10 -> 11 reshuffle!)
        for (int i = 1; i < history.Count; i++)
        {
            history[i].Should().NotBeEquivalentTo(
                history[i - 1],
                $"Mini-game round {i + 1} ('{history[i]}') must NEVER repeat round {i} ('{history[i - 1]}') back-to-back");
        }

        // 2. Even distribution invariant: Every single map in BotSafeArcadeMapPool (5v5, TDM, Devowars, 10v10)
        //    appears ONCE in Cycle 1 (1..10) and ONCE in Cycle 2 (11..20) -> exact 2 appearances each!
        IReadOnlyList<string> cycle1 = history.Take(poolSize).ToList();
        IReadOnlyList<string> cycle2 = history.Skip(poolSize).Take(poolSize).ToList();

        cycle1.Distinct(StringComparer.OrdinalIgnoreCase).Count().Should().Be(poolSize);
        cycle2.Distinct(StringComparer.OrdinalIgnoreCase).Count().Should().Be(poolSize);

        foreach (string expectedMap in QueueArcadeCoordinatorService.BotSafeArcadeMapPool)
        {
            int appearances = history.Count(m => m.Equals(expectedMap, StringComparison.OrdinalIgnoreCase));
            appearances.Should().Be(2, $"Map '{expectedMap}' must rotate evenly across 2 shuffle-bag cycles even with 1 player queued");
        }

        // 3. Genuine randomness invariant: Cycle 2 permutation must NOT equal Cycle 1 permutation!
        cycle1.SequenceEqual(cycle2, StringComparer.OrdinalIgnoreCase)
            .Should().BeFalse("Each shuffle-bag refill must produce a genuinely fresh random map order");

        _output.WriteLine($"[SIM-WAVE-5] Verified {totalRounds}-Round Bot-Safe Uniform Rotation (PoolSize={poolSize}, QueuedHumans=1):");
        _output.WriteLine($"  Cycle 1 (1..{poolSize}) : {string.Join(" -> ", cycle1)}");
        _output.WriteLine($"  Cycle 2 ({poolSize + 1}..{totalRounds}): {string.Join(" -> ", cycle2)}");
        _arcadeCoordinator.GetMetricsSnapshot().RoundTimerRotationsTotal.Should().Be(expectedTimerRotations);
    }

    private static void AssertArcadeMatchSettingsForMap(
        SimulatedChatSession arcadeServer,
        DecodedCreateMatchPacket createPacket,
        string selectedMap)
    {
        bool is10v10 = selectedMap.EndsWith("_10v10", StringComparison.OrdinalIgnoreCase);
        int expectedTeamSize = is10v10 ? 10 : 5;
        createPacket.MatchSettings.Should().Contain($"teamsize:{expectedTeamSize}");
        createPacket.MatchSettings.Should().Contain("nostats:true", "Queue Arcade must NEVER record stats or affect Leave %");
        createPacket.MatchSettings.Should().NotContain("noleaver:", "Queue Arcade must NEVER set strict leaver enforcement");

        if (QueueArcadeCoordinatorService.TryGetSingleHeroBotDefinition(selectedMap, out string? botDef))
        {
            createPacket.MatchSettings.Should().Contain("allowduplicate:true");
            createPacket.MatchSettings.Should().Contain($"bots1:{botDef}");
            createPacket.MatchSettings.Should().Contain($"bots2:{botDef}");
            int expectedSingleHeroDiff = QueueArcadeCoordinatorService.IsBotActiveMap(selectedMap) ? 3 : 0;
            createPacket.MatchSettings.Should().Contain($"botdifficulty:{expectedSingleHeroDiff}");
        }
        else if (BotMatchMapCatalog.TryGetCustomBotPool(selectedMap, out IReadOnlyList<string>? customPool))
        {
            createPacket.MatchSettings.Should().NotContain("randombots:");
            createPacket.MatchSettings.Should().Contain("botdifficulty:3");
            string[] tokens = createPacket.MatchSettings.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            string[] bots1 = tokens.Single(t => t.StartsWith("bots1:", StringComparison.Ordinal))["bots1:".Length..].Split('|');
            string[] bots2 = tokens.Single(t => t.StartsWith("bots2:", StringComparison.Ordinal))["bots2:".Length..].Split('|');
            bots1.Should().HaveCount(expectedTeamSize);
            bots2.Should().HaveCount(expectedTeamSize);
            string[] combined = [.. bots1, .. bots2];
            combined.Should().OnlyContain(b => customPool.Contains(b));
            combined.Distinct(StringComparer.Ordinal).Should().HaveCount(combined.Length);
        }
        else
        {
            createPacket.MatchSettings.Should().Contain($"randombots:{expectedTeamSize}|{expectedTeamSize}");
            int expectedStandardDiff = QueueArcadeCoordinatorService.IsBotActiveMap(selectedMap) ? 3 : 0;
            createPacket.MatchSettings.Should().Contain($"botdifficulty:{expectedStandardDiff}");
        }

        int expectedArcadeMode = QueueArcadeCoordinatorService.IsBotActiveMap(selectedMap) ? 1 : 2;
        ExtractRemoteCommands(arcadeServer)
            .TakeLast(2)
            .Any(c => c.Contains($"kongor_arcade_mode {expectedArcadeMode}", StringComparison.Ordinal))
            .Should().BeTrue($"Map '{selectedMap}' must configure kongor_arcade_mode {expectedArcadeMode}");
    }

    [Fact]
    public async Task SimulationWave6_LiveRedisToggle_PausesNewPushesWithGentleDrain_AndFallsBackToConfigWhenRedisMissing()
    {
        // protects: Live Redis toggle key (kongor:queue_arcade:enabled) 5s-cached override, gentle drain when paused,
        //           and clean fallback to QueueArcade:Enabled config when Redis key is absent.
        // fails_when: Pausing Queue Arcade requires restarting ChatServer, abruptly kicks players mid-round, or ignores Redis state.
        // seam: Real QueueArcadeCoordinatorService + MatchmakingService + Mock<IDatabase> + SimulatedTimeProvider.
        RedisValue currentRedisVal = "true";
        Mock<IDatabase> mockRedis = new();
        mockRedis
            .Setup(r => r.StringGetAsync(
                It.Is<RedisKey>(k => (string)k! == QueueArcadeCoordinatorService.RedisEnabledKey),
                It.IsAny<CommandFlags>()))
            .Returns(() => Task.FromResult(currentRedisVal));

        using QueueArcadeCoordinatorService redisCoordinator = new(
            _configuration,
            _mockChatContext.Object,
            _matchmakingService,
            distributedCache: mockRedis.Object,
            timeProvider: _fakeTime,
            randomSeed: 20261108);

        SimulatedChatSession arcadeQ1 = RegisterGameServer(401, "QUEUE ARCADE #Q1", port: 21485, localPort: 11485);
        await redisCoordinator.EvaluateTickAsync();
        DecodedCreateMatchPacket createPkt = ExtractCreateMatchPackets(arcadeQ1).Single();
        SimulateServerAnnouncesMatch(arcadeQ1, createPkt.MatchUpId, realMatchId: 9501, coordinator: redisCoordinator);

        // 1. Enabled=true -> Player 1 is pushed into #Q1 after 6s wait
        (SimulatedChatSession p1Session, _) = EnqueueSoloPlayer(601, "ArcadeToggleP1");
        _fakeTime.Advance(TimeSpan.FromSeconds(6));
        await redisCoordinator.EvaluateTickAsync();
        ExtractAutoConnectPackets(p1Session).Should().ContainSingle(p => p.MatchId == 9501);

        // 2. Flip Redis key to "false" and advance > 5s cache TTL -> Player 2 is NOT pushed, while #Q1 drains gently!
        currentRedisVal = "false";
        (SimulatedChatSession p2Session, _) = EnqueueSoloPlayer(602, "ArcadeToggleP2");
        _fakeTime.Advance(TimeSpan.FromSeconds(7));
        await redisCoordinator.EvaluateTickAsync();
        ExtractAutoConnectPackets(p2Session).Should().BeEmpty("Paused Queue Arcade must stop pushing newly queued players");

        // Gentle drain proof: round timer expiry on #Q1 is still maintained while paused!
        _fakeTime.Advance(TimeSpan.FromSeconds(605));
        await redisCoordinator.EvaluateTickAsync();
        ExtractRemoteCommands(arcadeQ1).Should().Contain(QueueArcadeCoordinatorService.EndArcadeMatchCommand);

        // 3. Clear Redis key (RedisValue.Null) -> falls back to appsettings QueueArcade:Enabled=true and pushes Player 2!
        currentRedisVal = RedisValue.Null;
        SimulateServerMatchComplete(arcadeQ1, 9501, [], coordinator: redisCoordinator);
        _fakeTime.Advance(TimeSpan.FromSeconds(7));
        await redisCoordinator.EvaluateTickAsync();
        DecodedCreateMatchPacket resumedCreate = ExtractCreateMatchPackets(arcadeQ1).Last();
        SimulateServerAnnouncesMatch(arcadeQ1, resumedCreate.MatchUpId, realMatchId: 9502, coordinator: redisCoordinator);
        _fakeTime.Advance(TimeSpan.FromSeconds(3.5));
        await redisCoordinator.EvaluateTickAsync();
        ExtractAutoConnectPackets(p2Session).Should().ContainSingle(p => p.MatchId == 9502);
    }

    [Fact]
    public async Task SimulationWave7_TwoInstanceOverflowAndPacking_KeepsQ2IdleUntil80PercentAndPacksQ1First()
    {
        // protects: Demand-driven 2-instance capacity (#Q2 stays idle at 0 bot CPU until #Q1 hits >= 80% human fill)
        //           and #Q1-first player packing before overflowing to #Q2.
        // fails_when: Both #Q1 and #Q2 spin up full bot matches with 1-3 humans queued, or players split across empty lobbies.
        // seam: Real QueueArcadeCoordinatorService multi-server warmup & group packing across #Q1 (11485) and #Q2 (11486).
        SimulatedChatSession arcadeQ1 = RegisterGameServer(501, "QUEUE ARCADE #Q1", port: 21485, localPort: 11485);
        SimulatedChatSession arcadeQ2 = RegisterGameServer(502, "QUEUE ARCADE #Q2", port: 21486, localPort: 11486);

        // Initial tick: #Q1 warms 1 match, #Q2 stays completely idle!
        await _arcadeCoordinator.EvaluateTickAsync();
        ExtractCreateMatchPackets(arcadeQ1).Should().HaveCount(1);
        ExtractCreateMatchPackets(arcadeQ2).Should().BeEmpty("#Q2 must stay idle with 0 bot CPU until #Q1 approaches capacity");

        DecodedCreateMatchPacket q1Create = ExtractCreateMatchPackets(arcadeQ1).Single();
        bool q1Is10v10 = q1Create.MatchSettings.Contains("teamsize:10", StringComparison.Ordinal);
        int q1MaxHumans = q1Is10v10 ? 20 : 10;
        SimulateServerAnnouncesMatch(arcadeQ1, q1Create.MatchUpId, realMatchId: 9601);

        // Enqueue 3 solo players (< 80% of 10 or 20): all 3 pack into #Q1, and #Q2 STILL stays idle!
        List<SimulatedChatSession> allPlayerSessions = [];
        for (int i = 1; i <= 3; i++)
        {
            (SimulatedChatSession session, _) = EnqueueSoloPlayer(700 + i, $"PackPlayer_{i}");
            allPlayerSessions.Add(session);
        }
        _fakeTime.Advance(TimeSpan.FromSeconds(6));
        await _arcadeCoordinator.EvaluateTickAsync();

        allPlayerSessions.Count(s => ExtractAutoConnectPackets(s).Any(p => p.MatchId == 9601)).Should().Be(3);
        ExtractCreateMatchPackets(arcadeQ2).Should().BeEmpty("#Q2 must remain idle while #Q1 human occupancy is < 80%");

        // Now enqueue enough additional players across distinct Ranked buckets (< 8 per Ranked bucket so Ranked doesn't pop yet)
        // to fill #Q1 to 100% capacity + 2 overflow players!
        (ChatProtocol.TMMGameType GameType, string Map)[] distinctBuckets =
        [
            (ChatProtocol.TMMGameType.TMM_GAME_TYPE_NORMAL, "caldavar"),
            (ChatProtocol.TMMGameType.TMM_GAME_TYPE_MIDWARS, "midwars"),
            (ChatProtocol.TMMGameType.TMM_GAME_TYPE_NORMAL, "grimmscrossing"),
            (ChatProtocol.TMMGameType.TMM_GAME_TYPE_CASUAL, "grimmscrossing"),
            (ChatProtocol.TMMGameType.TMM_GAME_TYPE_NORMAL, "watchtower")
        ];
        int targetTotalHumans = q1MaxHumans + 2;
        for (int i = 4; i <= targetTotalHumans; i++)
        {
            (ChatProtocol.TMMGameType gType, string bucketMap) = distinctBuckets[(i - 4) / 4 % distinctBuckets.Length];
            (SimulatedChatSession session, MatchmakingGroup grp) = EnqueueSoloPlayer(700 + i, $"PackPlayer_{i}", gameType: gType);
            grp.Information.MapName = bucketMap;
            allPlayerSessions.Add(session);
        }
        _fakeTime.Advance(TimeSpan.FromSeconds(6));
        await _arcadeCoordinator.EvaluateTickAsync();

        // #Q1 fills to 100% capacity first, and #Q2 now spins up its overflow match!
        allPlayerSessions.Count(s => ExtractAutoConnectPackets(s).Any(p => p.MatchId == 9601)).Should().Be(q1MaxHumans);
        ExtractCreateMatchPackets(arcadeQ2).Should().HaveCount(1, "#Q2 must spin up automatically once #Q1 reaches >= 80% occupancy");

        DecodedCreateMatchPacket q2Create = ExtractCreateMatchPackets(arcadeQ2).Single();
        SimulateServerAnnouncesMatch(arcadeQ2, q2Create.MatchUpId, realMatchId: 9602);
        _fakeTime.Advance(TimeSpan.FromSeconds(3.5));

        await _arcadeCoordinator.EvaluateTickAsync();
        allPlayerSessions.Count(s => ExtractAutoConnectPackets(s).Any(p => p.MatchId == 9602))
            .Should().Be(2, "Remaining 2 overflow players must route into #Q2");
    }

    [Fact]
    public async Task SimulationWave8_StrictRegionMatching_NeverPullsCrossRegionPlayers_AndIsolatesRegionalOverflow()
    {
        // protects: Strict regional isolation (EU-only players never pulled into USE Queue Arcade, 0 USE #Q2 wakeup from EU traffic),
        //           'US' umbrella compatibility for USE/USW, ClientRegion lowest-ping tie-breaker on multi-region queues,
        //           and automatic slot release when a queued player changes GameRegions away from their active Arcade server.
        // fails_when: QueueArcadeCoordinatorService treats region as a soft preference, counts foreign-region queue demand toward #Q2,
        //             or leaves players stranded in a mismatched-region Arcade server after updating TMM regions.
        // seam: Real QueueArcadeCoordinatorService + MatchmakingService + SimulatedTimeProvider across USE (#Q1/#Q2), EU (#Q1), and US servers.
        SimulatedChatSession useArcadeQ1 = RegisterGameServer(801, "QUEUE ARCADE #Q1", port: 21485, localPort: 11485, location: "USE");
        SimulatedChatSession useArcadeQ2 = RegisterGameServer(802, "QUEUE ARCADE #Q2", port: 21486, localPort: 11486, location: "USE");

        await _arcadeCoordinator.EvaluateTickAsync();
        DecodedCreateMatchPacket useQ1Create = ExtractCreateMatchPackets(useArcadeQ1).Single();
        ExtractCreateMatchPackets(useArcadeQ2).Should().BeEmpty();
        SimulateServerAnnouncesMatch(useArcadeQ1, useQ1Create.MatchUpId, realMatchId: 9801);

        // 1. 12 EU-only players queue across distinct Ranked buckets (< 8 per bucket so Ranked doesn't pop):
        //    ZERO EU players may be pulled into USE #Q1, and USE #Q2 must stay completely IDLE (0 CPU)!
        (ChatProtocol.TMMGameType GameType, string Map)[] euBuckets =
        [
            (ChatProtocol.TMMGameType.TMM_GAME_TYPE_NORMAL, "caldavar"),
            (ChatProtocol.TMMGameType.TMM_GAME_TYPE_MIDWARS, "midwars"),
            (ChatProtocol.TMMGameType.TMM_GAME_TYPE_NORMAL, "grimmscrossing")
        ];
        List<SimulatedChatSession> euSessions = [];
        for (int i = 1; i <= 12; i++)
        {
            (ChatProtocol.TMMGameType gType, string map) = euBuckets[(i - 1) / 4];
            (SimulatedChatSession euSess, MatchmakingGroup euGroup) = EnqueueSoloPlayer(8100 + i, $"EuOnly_{i}", gameType: gType);
            euSess.ClientMetadata!.ClientRegion = "eu";
            euGroup.Information.MapName = map;
            euGroup.Information.GameRegions = ["eu"];
            euSessions.Add(euSess);
        }

        // Also enqueue 1 USW-only player: USE must NOT pull a USW-only player either!
        (SimulatedChatSession uswOnlySess, MatchmakingGroup uswOnlyGrp) = EnqueueSoloPlayer(8201, "UswOnly_1");
        uswOnlySess.ClientMetadata!.ClientRegion = "usw";
        uswOnlyGrp.Information.GameRegions = ["usw"];

        _fakeTime.Advance(TimeSpan.FromSeconds(6));
        await _arcadeCoordinator.EvaluateTickAsync();

        euSessions.Sum(s => ExtractAutoConnectPackets(s).Count).Should().Be(0, "EU-only queued players must NEVER be pulled into a USE Queue Arcade server");
        ExtractAutoConnectPackets(uswOnlySess).Should().BeEmpty("USW-only queued players must NEVER be pulled into a USE Queue Arcade server");
        ExtractCreateMatchPackets(useArcadeQ2).Should().BeEmpty("Foreign-region (EU/USW) queue demand must NOT spin up USE #Q2");
        _arcadeCoordinator.GetMetricsSnapshot().RegionMismatchSkipsTotal.Should().BeGreaterThanOrEqualTo(13);

        // 2. Enqueue 1 USE player -> pulled into USE #Q1 immediately!
        (SimulatedChatSession useSess, MatchmakingGroup useGrp) = EnqueueSoloPlayer(8301, "UsePlayer_1");
        useGrp.Information.GameRegions = ["use"];
        _fakeTime.Advance(TimeSpan.FromSeconds(6));
        await _arcadeCoordinator.EvaluateTickAsync();
        ExtractAutoConnectPackets(useSess).Should().ContainSingle(p => p.MatchId == 9801);

        // 3. Bring EU Queue Arcade fleet (#Q1 + #Q2) online: waiting EU players pack into EU,
        //    and a multi-region player queuing ["use", "eu"] with ClientRegion="eu" prefers EU over USE for lowest ping!
        SimulatedChatSession euArcadeQ1 = RegisterGameServer(803, "QUEUE ARCADE EU #Q1", port: 21487, localPort: 11487, location: "EU");
        SimulatedChatSession euArcadeQ2 = RegisterGameServer(804, "QUEUE ARCADE EU #Q2", port: 21488, localPort: 11488, location: "EU");
        await _arcadeCoordinator.EvaluateTickAsync();
        DecodedCreateMatchPacket euQ1Create = ExtractCreateMatchPackets(euArcadeQ1).Single();
        SimulateServerAnnouncesMatch(euArcadeQ1, euQ1Create.MatchUpId, realMatchId: 9803);
        _fakeTime.Advance(TimeSpan.FromSeconds(3.5));
        await _arcadeCoordinator.EvaluateTickAsync();
        if (ExtractCreateMatchPackets(euArcadeQ2).Count > 0)
        {
            DecodedCreateMatchPacket euQ2Create = ExtractCreateMatchPackets(euArcadeQ2).Single();
            SimulateServerAnnouncesMatch(euArcadeQ2, euQ2Create.MatchUpId, realMatchId: 9804);
            _fakeTime.Advance(TimeSpan.FromSeconds(3.5));
            await _arcadeCoordinator.EvaluateTickAsync();
        }

        HashSet<int> euMatchIds = [9803, 9804];
        euSessions.All(s => ExtractAutoConnectPackets(s).Any(p => euMatchIds.Contains(p.MatchId)))
            .Should().BeTrue("All 12 waiting EU players must route into EU Queue Arcade once EU fleet comes online");

        (SimulatedChatSession multiRegionEuHomeSess, MatchmakingGroup multiGrp) = EnqueueSoloPlayer(8401, "MultiEuHome_1");
        multiRegionEuHomeSess.ClientMetadata!.ClientRegion = "eu";
        multiGrp.Information.GameRegions = ["use", "eu"];
        _fakeTime.Advance(TimeSpan.FromSeconds(6));
        await _arcadeCoordinator.EvaluateTickAsync();
        ExtractAutoConnectPackets(multiRegionEuHomeSess).Should().ContainSingle(
            p => euMatchIds.Contains(p.MatchId),
            "Multi-region player with ClientRegion='eu' must prefer EU Queue Arcade over USE for lowest ping");

        // 4. Mid-queue region update: UsePlayer_1 changes GameRegions from ["use"] -> ["eu"]
        //    -> coordinator releases slot on USE #Q1 and seamlessly connects them to EU!
        useGrp.Information.GameRegions = ["eu"];
        await _arcadeCoordinator.EvaluateTickAsync();
        ExtractRemoteCommands(useArcadeQ1).Should().Contain(c => c.Contains("kongor_arcade_release 8301", StringComparison.Ordinal));
        euMatchIds.Contains(ExtractAutoConnectPackets(useSess).Last().MatchId).Should().BeTrue();

        // 5. Unit proof of 'US' umbrella compatibility for self-hosters configuring CompelLocation='US'
        QueueArcadeCoordinatorService.IsArcadeServerRegionCompatible(["use"], "US").Should().BeTrue();
        QueueArcadeCoordinatorService.IsArcadeServerRegionCompatible(["usw"], "US").Should().BeTrue();
        QueueArcadeCoordinatorService.IsArcadeServerRegionCompatible(["eu"], "US").Should().BeFalse();
        QueueArcadeCoordinatorService.IsArcadeServerRegionCompatible(["usw"], "USE").Should().BeFalse();
    }

    [Fact]
    public async Task SimulationWave9_ConcurrentServerHandshakesAndHigherQ1ServerId_WarmsOnlyQ1AndMatchesExactK2BinaryPacket()
    {
        // protects: Atomic lock reservation across parallel TCP OnArcadeServerHandshake threads, LocalPort (#Q1=11485)
        //           tie-breaker when K2 assigns a smaller random ServerID to #Q2 (11486), and exact K2 NET_CHAT_GS_CREATE_MATCH
        //           binary wire layout (including mandatory Int32 GroupIdsCount trailer so CPacket::ReadInt never underflows).
        // fails_when: Concurrent handshakes double-dispatch CreateMatch, #Q2 warms before #Q1 due to random ServerID ordering,
        //             or NET_CHAT_GS_CREATE_MATCH omits the 4-byte GroupIdsCount trailer.
        // seam: Real QueueArcadeCoordinatorService parallel multi-thread OnArcadeServerHandshake + ChatBuffer wire parser.
        SimulatedChatSession arcadeQ1 = RegisterGameServer(
            serverId: 1784826586,
            serverName: "QUEUE ARCADE #Q1",
            port: 11485,
            localPort: 11485,
            location: "USE");
        SimulatedChatSession arcadeQ2 = RegisterGameServer(
            serverId: 880398235,
            serverName: "QUEUE ARCADE #Q2",
            port: 11486,
            localPort: 11486,
            location: "USE");

        using ManualResetEventSlim startGate = new(false);
        Task[] handshakeTasks = new Task[8];
        for (int i = 0; i < handshakeTasks.Length; i++)
        {
            SimulatedChatSession target = (i % 2 == 0) ? arcadeQ1 : arcadeQ2;
            handshakeTasks[i] = Task.Run(() =>
            {
                startGate.Wait();
                _arcadeCoordinator.OnArcadeServerHandshake(target);
            });
        }

        startGate.Set();
        await Task.WhenAll(handshakeTasks);

        List<DecodedCreateMatchPacket> q1Packets = ExtractCreateMatchPackets(arcadeQ1);
        List<DecodedCreateMatchPacket> q2Packets = ExtractCreateMatchPackets(arcadeQ2);

        q1Packets.Should().ContainSingle("#Q1 (port 11485) must be atomically selected once even when #Q2 has a smaller random ServerID");
        q2Packets.Should().BeEmpty("#Q2 (port 11486) must remain strictly IDLE under concurrent server handshakes");
        arcadeQ1.ServerMetadata!.Status.Should().Be(ChatProtocol.ServerStatus.SERVER_STATUS_LOADING);
        arcadeQ2.ServerMetadata!.Status.Should().Be(ChatProtocol.ServerStatus.SERVER_STATUS_IDLE);
        _arcadeCoordinator.GetMetricsSnapshot().MatchesStartedTotal.Should().Be(1);
    }

    [Fact]
    public async Task SimulationWave10_PostLoadWorldBotSeeding_DuplicateMatchComplete_AndGamePhase8Settle_WorkEndToEnd()
    {
        // protects: (1) Post-LoadWorld AddBot seeding inside ConfirmArcadeMatchReady (after CTeamInfo 1 & 2 exist in K2),
        //           (2) sv_gameEndPhaseTime 500 + GamePhase 8 teardown map-rewind when K2 replies 0x0508 carrying just-ended MatchUpID,
        //           (3) protection against K2's duplicate 0x0515 (MatchComplete carrying negative MatchUpID) clobbering a rotated match,
        //           and (4) exact 15-byte 0x0508 (NET_CHAT_GS_NOT_IDLE) binary wire parsing + StartPostMatch 1 false true recovery.
        // fails_when: Bots are requested before LoadWorld (CTeamInfo == NULL -> 0 bots), GamePhase 8 skips a map or stalls 5 minutes,
        //             duplicate 0x0515 resets a newly-started match to IDLE, or desynced 0x0508 emits nonexistent 'EndGame'.
        // seam: Real QueueArcadeCoordinatorService + MatchComplete (0x0515) + ServerNotIdle (0x0508) + ChatCommandRegistry.
        ChatCommandRegistry.TryGetDescriptor(0x0508, out ChatCommandDescriptor? notIdleDescriptor).Should().BeTrue();
        notIdleDescriptor!.AllowedRoles.Should().Be(ChatPeerRole.MatchServer);

        SimulatedChatSession arcadeQ1 = RegisterGameServer(901, "QUEUE ARCADE #Q1", port: 11485, localPort: 11485, location: "USE");
        await _arcadeCoordinator.EvaluateTickAsync();

        // 1. Verify pre-match remote console commands isolate kongor_arcade_mode in its own packet before 500ms GamePhase 8 teardown CVars
        List<string> preMatchRemoteCmds = ExtractRemoteCommands(arcadeQ1);
        preMatchRemoteCmds.Should().HaveCount(2);
        preMatchRemoteCmds[0].Should().StartWith("kongor_arcade_mode ").And.NotContain(";");
        preMatchRemoteCmds[1].Should().Contain("sv_gameEndPhaseTime 500");
        preMatchRemoteCmds[1].Should().Contain("sv_matchmakingDisconnectCheck false");
        preMatchRemoteCmds[1].Should().NotContain("kongor_arcade_mode");

        // 2. Confirm AnnounceMatch dispatches exact 2*TeamSize post-LoadWorld AddBot commands across Team 1 and Team 2!
        DecodedCreateMatchPacket firstCreate = ExtractCreateMatchPackets(arcadeQ1).Single();
        string firstMap = arcadeQ1.ServerMetadata!.MapName;
        int expectedBots = firstMap.EndsWith("_10v10", StringComparison.OrdinalIgnoreCase) ? 20 : 10;

        SimulateServerAnnouncesMatch(arcadeQ1, firstCreate.MatchUpId, realMatchId: 9901);
        arcadeQ1.ServerMetadata.Status.Should().Be(ChatProtocol.ServerStatus.SERVER_STATUS_ACTIVE);

        List<string> allAddBotTokens = ExtractRemoteCommands(arcadeQ1)
            .Skip(preMatchRemoteCmds.Count)
            .SelectMany(cmd => cmd.Split(';', StringSplitOptions.RemoveEmptyEntries))
            .Where(t => t.StartsWith("AddBot ", StringComparison.Ordinal))
            .ToList();
        allAddBotTokens.Should().HaveCount(expectedBots, "ConfirmArcadeMatchReady must spawn all 2*TeamSize bots post-LoadWorld");
        allAddBotTokens.Count(t => t.StartsWith("AddBot 1 ", StringComparison.Ordinal)).Should().Be(expectedBots / 2);
        allAddBotTokens.Count(t => t.StartsWith("AddBot 2 ", StringComparison.Ordinal)).Should().Be(expectedBots / 2);

        // 3. K2 sends 1st 0x0515 (carrying real MatchID=9901) -> rotates immediately to 2nd map (Status=LOADING)
        SimulateServerMatchComplete(arcadeQ1, realMatchId: 9901, accountIds: []);
        List<DecodedCreateMatchPacket> createsAfterFirstEnd = ExtractCreateMatchPackets(arcadeQ1);
        createsAfterFirstEnd.Should().HaveCount(2, "First MatchComplete (9901) must rotate immediately to the 2nd map");
        DecodedCreateMatchPacket secondCreate = createsAfterFirstEnd[1];
        string secondMap = arcadeQ1.ServerMetadata.MapName;
        secondCreate.MatchUpId.Should().NotBe(firstCreate.MatchUpId);
        arcadeQ1.ServerMetadata.Status.Should().Be(ChatProtocol.ServerStatus.SERVER_STATUS_LOADING);
        _arcadeCoordinator.GetMetricsSnapshot().MatchesCompletedTotal.Should().Be(1);

        // 4. Simulate K2 replying 0x0508 (NET_CHAT_GS_NOT_IDLE) carrying firstCreate.MatchUpId during its 500ms GamePhase 8 teardown:
        //    Coordinator must rewind secondMap onto _shuffleBag (WITHOUT sending StartPostMatch) and re-launch secondMap on next IDLE!
        ServerNotIdle notIdleProcessor = new(NullLogger<ServerNotIdle>.Instance, _arcadeCoordinator);
        ChatBuffer teardownNotIdle = new();
        teardownNotIdle.WriteCommand(0x0508);
        teardownNotIdle.WriteInt8((byte)ChatProtocol.ServerStatus.SERVER_STATUS_ACTIVE);
        teardownNotIdle.WriteInt32(firstCreate.MatchUpId);
        teardownNotIdle.WriteInt32(0);
        teardownNotIdle.WriteInt32(0);
        notIdleProcessor.Process(arcadeQ1, teardownNotIdle);

        ExtractRemoteCommands(arcadeQ1).Should().NotContain(
            QueueArcadeCoordinatorService.EndArcadeMatchCommand,
            "500ms GamePhase 8 teardown settle must rewind map without spamming StartPostMatch");

        // 1.5s later the coordinator's automatic PostMatchSettleRetry timer marks IDLE (even without an unsolicited K2 0x0502) and launches exact rewound secondMap!
        _fakeTime.Advance(TimeSpan.FromSeconds(2));
        await _arcadeCoordinator.EvaluateTickAsync();
        DecodedCreateMatchPacket relaunchedSecondCreate = ExtractCreateMatchPackets(arcadeQ1).Last();
        arcadeQ1.ServerMetadata.MapName.Should().Be(secondMap, "Rewound shuffle-bag entry must launch automatically after 1.5s settle without skipping a map");

        // 5. K2's trailing duplicate 0x0515 carrying the OLD negative MatchUpID (firstCreate.MatchUpId) MUST be ignored!
        SimulateServerMatchComplete(arcadeQ1, realMatchId: firstCreate.MatchUpId, accountIds: []);
        arcadeQ1.ServerMetadata.Status.Should().Be(ChatProtocol.ServerStatus.SERVER_STATUS_LOADING);
        _arcadeCoordinator.GetMetricsSnapshot().MatchesCompletedTotal.Should().Be(1);

        // Confirm 2nd match announces cleanly and stays ACTIVE
        SimulateServerAnnouncesMatch(arcadeQ1, relaunchedSecondCreate.MatchUpId, realMatchId: 9902);
        arcadeQ1.ServerMetadata.Status.Should().Be(ChatProtocol.ServerStatus.SERVER_STATUS_ACTIVE);
        arcadeQ1.ServerMetadata.MatchID.Should().Be(9902);

        // 6. Simulate genuine desync 0x0508 carrying unrelated stale MatchUpID=-10049 -> dispatches StartPostMatch 1 false true!
        ChatBuffer desyncNotIdle = new();
        desyncNotIdle.WriteCommand(0x0508);
        desyncNotIdle.WriteInt8((byte)ChatProtocol.ServerStatus.SERVER_STATUS_ACTIVE);
        desyncNotIdle.WriteInt32(-10049);
        desyncNotIdle.WriteInt32(0);
        desyncNotIdle.WriteInt32(0);
        desyncNotIdle.Size.Should().Be(15, "Exact K2 0x0508 wire payload with 0 groups is 15 bytes");

        notIdleProcessor.Process(arcadeQ1, desyncNotIdle);
        ExtractRemoteCommands(arcadeQ1).Last().Should().Be(
            QueueArcadeCoordinatorService.EndArcadeMatchCommand,
            "Desynced 0x0508 must self-heal by dispatching StartPostMatch 1 false true");
    }

    [Fact]
    public async Task SimulationWave11_DynamicRoster0x0517ByteLayout_StrictPacketOrdering_AndMatchCompleteRealMatchIsolation()
    {
        // protects: (1) Exact K2 0x0517 (NET_CHAT_GS_PLAYER_BENEFITS) wire payload [u16 0x0517, u8 count, (i32 accountId, u8 0)*n]
        //               so k2_x64.dll+0x97e33f inserts queued players into CHostServer::m_mapArrangedRoster (+0x4630) and prevents
        //               disconnect_not_on_roster (0x24),
        //           (2) Strict FIFO send ordering on the server socket: 0x0517 BEFORE 0x1504 (kongor_arcade_reserve),
        //           and (3) MatchComplete (0x0515) isolation so when an Arcade match ends carrying AccountIDs from m_mapArrangedRoster,
        //               players who already moved into a real Ranked/MidWars match do NOT have their live MatchID or IsInGame wiped!
        // fails_when: 0x0517 is omitted, malformed, or ordered after kongor_arcade_reserve, or MatchComplete clears real MatchID/IsInGame.
        // seam: Real QueueArcadeCoordinatorService + MatchComplete (0x0515) + ChatBuffer wire framing.
        SimulatedChatSession arcadeQ1 = RegisterGameServer(951, "QUEUE ARCADE #Q1", port: 11485, localPort: 11485, location: "USE");
        await _arcadeCoordinator.EvaluateTickAsync();
        DecodedCreateMatchPacket firstCreate = ExtractCreateMatchPackets(arcadeQ1).Single();
        SimulateServerAnnouncesMatch(arcadeQ1, firstCreate.MatchUpId, realMatchId: 9951);

        int baselineServerPacketCount = arcadeQ1.SnapshotPackets().Count;

        // Enqueue a 2-player party (AccountIDs 59 and 60) -> wait 6s -> pushed into Queue Arcade Match #9951!
        (List<SimulatedChatSession> partySessions, MatchmakingGroup partyGroup) = EnqueuePremadeParty(
            groupKey: 9510,
            players:
            [
                (59, "guccidonut"),
                (60, "wingman")
            ]);
        partySessions.Should().HaveCount(2);

        _fakeTime.Advance(TimeSpan.FromSeconds(6));
        await _arcadeCoordinator.EvaluateTickAsync();

        List<byte[]> postPushServerPackets = arcadeQ1.SnapshotPackets().Skip(baselineServerPacketCount).ToList();
        postPushServerPackets.Should().HaveCountGreaterThanOrEqualTo(2, "Server must receive 0x0517 roster packet followed by 0x1504 reserve packet");

        // 1. Assert FIRST packet sent to arcadeQ1 is 0x0517 with exact binary byte layout:
        //    [2B size=13][u16 0x0517][u8 count=2][i32 59][u8 0][i32 60][u8 0] -> 2 + 2 + 1 + 5 + 5 = 15 raw framed bytes!
        byte[] rawRosterPacket = postPushServerPackets[0];
        rawRosterPacket.Length.Should().Be(15);
        ChatBuffer rosterReader = new(rawRosterPacket.AsSpan(2));
        rosterReader.ReadCommand().Should().Be(ChatProtocol.ChatServerToGameServer.NET_CHAT_GS_PLAYER_BENEFITS);
        rosterReader.ReadInt8().Should().Be(2);
        rosterReader.ReadInt32().Should().Be(59);
        rosterReader.ReadInt8().Should().Be(0);
        rosterReader.ReadInt32().Should().Be(60);
        rosterReader.ReadInt8().Should().Be(0);

        // 2. Assert SECOND packet sent to arcadeQ1 is 0x1504 carrying both kongor_arcade_reserve commands!
        byte[] rawReservePacket = postPushServerPackets[1];
        ChatBuffer reserveReader = new(rawReservePacket.AsSpan(2));
        reserveReader.ReadCommand().Should().Be(ChatProtocol.ChatServerToGameServer.NET_CHAT_GS_REMOTE_COMMAND);
        _ = reserveReader.ReadString(); // cookie
        string reserveCmd = reserveReader.ReadString();
        reserveCmd.Should().Contain("kongor_arcade_reserve 59 ");
        reserveCmd.Should().Contain("kongor_arcade_reserve 60 ");

        // 3. Simulate party getting pulled into a REAL match (MatchID=77777, IsInGame=true, IN_GAME status),
        //    and THEN Queue Arcade Match #9951 ends and reports AccountIDs=[59, 60] from its arranged roster!
        foreach (MatchmakingGroupMember member in partyGroup.Members)
        {
            member.IsInGame = true;
            member.Session.ClientMetadata!.ActiveArcadeMatchId = null;
            member.Session.ClientMetadata.MatchID = 77777;
            member.Session.ClientMetadata.MatchName = "TMM Match #77777";
            member.Session.ClientMetadata.LastKnownClientState = ChatProtocol.ChatClientStatus.CHAT_CLIENT_STATUS_IN_GAME;
        }

        SimulateServerMatchComplete(arcadeQ1, realMatchId: 9951, accountIds: [59, 60]);

        foreach (MatchmakingGroupMember member in partyGroup.Members)
        {
            member.IsInGame.Should().BeTrue("Ending a Queue Arcade match must NEVER clear IsInGame for players in a real match");
            member.Session.ClientMetadata!.MatchID.Should().Be(77777, "Ending a Queue Arcade match must NEVER wipe real MatchID");
            member.Session.ClientMetadata.LastKnownClientState.Should().Be(ChatProtocol.ChatClientStatus.CHAT_CLIENT_STATUS_IN_GAME);
        }

        // Confirm Queue Arcade still rotated cleanly to the next map on arcadeQ1!
        arcadeQ1.ServerMetadata!.Status.Should().Be(ChatProtocol.ServerStatus.SERVER_STATUS_LOADING);
    }

    [Fact]
    public void SimulationWave12_IncludeCustomPartyMapsOptIn_RotatesAll16Maps_AndSkipsLaneBotSeedingOnNonBotMaps()
    {
        // protects: Opt-in QueueArcade:IncludeCustomPartyMaps=true 18-map rotation AND ConfirmArcadeMatchReady skipping
        //           post-LoadWorld AddBot commands whenever ArcadeMode == 2 (soccer, pebbles_duel, arena, etc.) so
        //           teambotbrain never crashes non-waypoint party maps with 0xC0000374.
        // fails_when: IncludeCustomPartyMaps=true fails to unlock UnifiedArcadeMapPool, or ConfirmArcadeMatchReady dispatches
        //             AddBot commands onto ArcadeMode == 2 maps.
        // seam: Real QueueArcadeCoordinatorService + AnnounceMatch + MatchComplete across 36 rounds (2 full 18-map cycles).
        IConfiguration customMapConfig = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Matchmaking:PlayersPerTeam"] = "5",
                ["QueueArcade:Enabled"] = "true",
                ["QueueArcade:IncludeCustomPartyMaps"] = "true",
                ["QueueArcade:MinQueueWaitBeforePushSeconds"] = "5",
                ["QueueArcade:BotReadinessFallbackTimeoutSeconds"] = "3.0",
                ["QueueArcade:WastedLoadPlayerThreshold"] = "8",
                ["QueueArcade:RoundDurationSeconds"] = "600"
            })
            .Build();

        using QueueArcadeCoordinatorService fullPoolCoordinator = new(
            customMapConfig,
            _mockChatContext.Object,
            _matchmakingService,
            timeProvider: _fakeTime,
            randomSeed: 20261109);

        SimulatedChatSession arcadeServer = RegisterGameServer(981, "QUEUE ARCADE CUSTOM #1", 21489, 11489);
        fullPoolCoordinator.OnArcadeServerHandshake(arcadeServer);

        int poolSize = QueueArcadeCoordinatorService.UnifiedArcadeMapPool.Length;
        poolSize.Should().Be(16);
        QueueArcadeCoordinatorService.UnifiedArcadeMapPool.Should().NotContain("caldavar");
        QueueArcadeCoordinatorService.UnifiedArcadeMapPool.Should().NotContain("watchtower");
        QueueArcadeCoordinatorService.UnifiedArcadeMapPool.Should().Contain("caldavar_wtf");
        QueueArcadeCoordinatorService.RlSelfPlayMapPool.Should().NotContain("caldavar");
        QueueArcadeCoordinatorService.RlSelfPlayMapPool.Should().NotContain("watchtower");
        int totalRounds = poolSize * 2;
        int matchIdCounter = 12000;
        int nonBotRoundsVerified = 0;
        int botActiveRoundsVerified = 0;

        for (int r = 0; r < totalRounds; r++)
        {
            if (r > 0)
            {
                SimulateServerMatchComplete(arcadeServer, matchIdCounter, [], coordinator: fullPoolCoordinator);
            }

            DecodedCreateMatchPacket createPkt = ExtractCreateMatchPackets(arcadeServer).Last();
            string selectedMap = arcadeServer.ServerMetadata!.MapName;
            bool isBotMap = QueueArcadeCoordinatorService.IsBotActiveMap(selectedMap);
            int remoteCmdBaseline = ExtractRemoteCommands(arcadeServer).Count;

            SimulateServerAnnouncesMatch(arcadeServer, createPkt.MatchUpId, ++matchIdCounter, coordinator: fullPoolCoordinator);

            List<string> postReadyCmds = ExtractRemoteCommands(arcadeServer).Skip(remoteCmdBaseline).ToList();
            if (isBotMap)
            {
                int expectedBotCount = selectedMap.EndsWith("_10v10", StringComparison.OrdinalIgnoreCase) ? 20 : 10;
                List<string> addBotTokens = postReadyCmds
                    .SelectMany(c => c.Split(';', StringSplitOptions.RemoveEmptyEntries))
                    .Where(t => t.StartsWith("AddBot ", StringComparison.Ordinal))
                    .ToList();
                addBotTokens.Count.Should().Be(expectedBotCount, $"Bot-active map '{selectedMap}' must spawn {expectedBotCount} bots");
                if (selectedMap.Equals("soccer", StringComparison.OrdinalIgnoreCase))
                {
                    List<string> seededBotDefs = addBotTokens
                        .Select(t => t.Split(' ', StringSplitOptions.RemoveEmptyEntries)[2])
                        .ToList();
                    seededBotDefs.Should().OnlyContain(b => BotMatchMapCatalog.SoccerBotPool.Contains(b));
                    seededBotDefs.Distinct(StringComparer.Ordinal).Should().HaveCount(10);
                }

                botActiveRoundsVerified++;
            }
            else
            {
                postReadyCmds.Any(c => c.Contains("AddBot ", StringComparison.Ordinal))
                    .Should().BeFalse($"Custom party map '{selectedMap}' (ArcadeMode=2) must NEVER dispatch lane AddBot commands");
                nonBotRoundsVerified++;
            }
        }

        nonBotRoundsVerified.Should().Be(14, "7 custom non-bot party maps x 2 cycles = 14 verified non-bot rounds");
        botActiveRoundsVerified.Should().Be(18, "9 bot-active maps (including Strikers soccer) x 2 cycles = 18 verified bot-seeded rounds");

        IReadOnlyList<string> history = fullPoolCoordinator.RotationHistory;
        history.Count.Should().Be(totalRounds);
        foreach (string expectedMap in QueueArcadeCoordinatorService.UnifiedArcadeMapPool)
        {
            history.Count(m => m.Equals(expectedMap, StringComparison.OrdinalIgnoreCase)).Should().Be(2);
        }
    }

    [Fact]
    public void QueueArcadeHudPackage_XmlDom_AnchorsTopLeftClearOfClock_HasWhiteTimerLabel_AndLeaveGameButton()
    {
        // protects: K2 UI XML widget geometry and child layout for tactical_queue_arcade_banner so it sits in the open
        //           top-left viewport header (x=20.6h, y=0.2h, right=53.0h < 56.5h clock), renders a dedicated white
        //           (#FFFFFF) MM:SS timer non-overlapping status/button, and labels all 3 button states 'LEAVE GAME'.
        // fails_when: Banner drifts back under game_top_clock, child bounds overlap, timer defaults non-white, or
        //             button reverts to 'LEAVE MINI-GAME'.
        // seam: Real System.Xml.XmlDocument DOM parse of games/client/override/ui/tactical_hud.package.
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Directory.Build.props")))
        {
            dir = dir.Parent;
        }

        if (dir is null)
        {
            return;
        }

        string pkgPath = Path.Combine(dir.FullName, "games", "client", "override", "ui", "tactical_hud.package");
        if (!File.Exists(pkgPath))
        {
            // Cleanroom CI environment without games/client overlay tree
            return;
        }

        XmlDocument doc = new();
        doc.Load(pkgPath);

        XmlElement? banner = doc.SelectSingleNode("//panel[@name='tactical_queue_arcade_banner']") as XmlElement;
        banner.Should().NotBeNull();
        banner!.GetAttribute("align").Should().Be("left");
        banner.GetAttribute("valign").Should().Be("top");
        banner.GetAttribute("x").Should().Be("20.6h");
        banner.GetAttribute("y").Should().Be("0.2h");
        banner.GetAttribute("width").Should().Be("32.4h");
        banner.GetAttribute("height").Should().Be("3.0h");

        XmlElement? statusLabel = banner.SelectSingleNode("./label[@name='tactical_queue_arcade_status_label']") as XmlElement;
        statusLabel.Should().NotBeNull();
        statusLabel!.InnerText.Should().Be("QUEUE ARCADE - IN QUEUE");

        XmlElement? timerLabel = banner.SelectSingleNode("./label[@name='tactical_queue_arcade_timer_label']") as XmlElement;
        timerLabel.Should().NotBeNull();
        timerLabel!.GetAttribute("color").Should().Be("#FFFFFF");
        timerLabel.InnerText.Should().Be("00:00");

        XmlElement? leaveBtn = banner.SelectSingleNode("./button[@name='tactical_queue_arcade_leave_btn']") as XmlElement;
        leaveBtn.Should().NotBeNull();
        XmlNodeList? stateLabels = leaveBtn!.SelectNodes("./widgetstate/label");
        stateLabels.Should().NotBeNull();
        stateLabels!.Count.Should().Be(3);
        foreach (XmlNode node in stateLabels)
        {
            node.InnerText.Should().Be("LEAVE GAME");
        }
    }

    [Fact]
    public async Task SimulationWave13_PreQueuedBotWarmup_ClanTagReservation_DisconnectCooldown_AndSafeModeCircuitBreaker()
    {
        // protects: (1) Post-AnnounceMatch 3.0s bot-seeding warmup holding already-queued players until all 10-20 AddBot
        //               commands finish spawning,
        //           (2) Passing full '[CLAN]Name' via Account.GetNameWithClanTag() in kongor_arcade_reserve so NativeGuard
        //               stamps CPlayer name + clan tag without showing bot names in-game,
        //           (3) JOINING_GAME + 6s ServerDisconnectRepushCooldownSeconds preventing stacked whispers / machine-gun
        //               0x0062 packets when a Queue Arcade server disconnects mid-handshake,
        //           and (4) Rapid-exit (<20s) Safe-Mode retirement circuit breaker backing off CreateMatch loops for 15s+.
        // fails_when: Pre-queued players are pushed 600ms into AddBot seeding, clan tag is omitted from kongor_arcade_reserve,
        //             clients get re-pushed while still JOINING_GAME or unwinding a dead server, or Safe-Mode Quit spins in a tight loop.
        // seam: Real QueueArcadeCoordinatorService + JoiningMatch + JoinedMatch + LeftMatch + SimulatedTimeProvider.
        (SimulatedChatSession playerSession, _) = EnqueueSoloPlayer(
            accountId: 59,
            name: "guccidonut",
            gameType: ChatProtocol.TMMGameType.TMM_GAME_TYPE_NORMAL);
        playerSession.Account.Clan = new Clan
        {
            ID = 7,
            Name = "Gucci Squad",
            Tag = "GUCI"
        };

        // Player sits in FoC Ranked queue for 20s BEFORE Queue Arcade #Q1 starts up!
        _fakeTime.Advance(TimeSpan.FromSeconds(20));
        await _arcadeCoordinator.EvaluateTickAsync();
        ExtractAutoConnectPackets(playerSession).Should().BeEmpty();

        SimulatedChatSession arcadeQ1 = RegisterGameServer(1301, "QUEUE ARCADE #Q1", port: 11485, localPort: 11485, location: "USE");
        SimulatedChatSession arcadeQ2 = RegisterGameServer(1302, "QUEUE ARCADE #Q2", port: 11486, localPort: 11486, location: "USE");
        _arcadeCoordinator.OnArcadeServerHandshake(arcadeQ1);
        DecodedCreateMatchPacket q1Create = ExtractCreateMatchPackets(arcadeQ1).Single();

        // Server #Q1 announces Match #13001 at t = 20s (starts post-LoadWorld AddBot seeding)
        SimulateServerAnnouncesMatch(arcadeQ1, q1Create.MatchUpId, realMatchId: 13001);

        // At +0.6s and +2.0s after AnnounceMatch (< 3.0s MinMatchWarmupBeforePushSeconds), pre-queued player MUST NOT be pushed!
        _fakeTime.Advance(TimeSpan.FromMilliseconds(600));
        await _arcadeCoordinator.EvaluateTickAsync();
        ExtractAutoConnectPackets(playerSession).Should().BeEmpty("Pre-queued player must not be pushed 600ms into post-LoadWorld AddBot seeding");

        _fakeTime.Advance(TimeSpan.FromMilliseconds(1400));
        await _arcadeCoordinator.EvaluateTickAsync();
        ExtractAutoConnectPackets(playerSession).Should().BeEmpty("Pre-queued player must wait for full 3.0s bot-seeding warmup");

        // At +3.1s (>= 3.0s warmup), player IS pushed once, and kongor_arcade_reserve carries "[GUCI]guccidonut"!
        _fakeTime.Advance(TimeSpan.FromMilliseconds(1100));
        await _arcadeCoordinator.EvaluateTickAsync();
        ExtractAutoConnectPackets(playerSession).Should().ContainSingle(p => p.MatchId == 13001);
        ExtractRemoteCommands(arcadeQ1).Should().Contain(
            c => c.Contains("kongor_arcade_reserve 59 ", StringComparison.Ordinal) &&
                 c.Contains("\"[GUCI]guccidonut\"", StringComparison.Ordinal),
            "kongor_arcade_reserve must forward full [CLAN]Name so NativeGuard stamps CPlayer name + clan tag");

        // Client begins connecting -> sends CHAT_CMD_JOINING_GAME (0x0011)
        JoiningMatch joiningProcessor = new(_mockDiscord.Object, _matchmakingService);
        ChatBuffer joiningBuf = new();
        joiningBuf.WriteCommand(ChatProtocol.Command.CHAT_CMD_JOINING_GAME);
        joiningBuf.WriteString("127.0.0.1:11485");
        joiningProcessor.Process(playerSession, joiningBuf);
        playerSession.ClientMetadata!.LastKnownClientState.Should().Be(ChatProtocol.ChatClientStatus.CHAT_CLIENT_STATUS_JOINING_GAME);

        // Now simulate Server #Q1 dying/retiring rapidly (< 20s lifetime: 3.1s + 1.0s = 4.1s) while client is mid-handshake!
        _fakeTime.Advance(TimeSpan.FromSeconds(1));
        arcadeQ1.ConnectedFlag = false;
        await _arcadeCoordinator.EvaluateTickAsync();

        // 1) Client's ActiveArcadeMatchId was cleared, 6s disconnect cooldown applied, and NO stacked 2nd AutoConnect was sent!
        playerSession.ClientMetadata.ActiveArcadeMatchId.Should().BeNull();
        playerSession.ClientMetadata.ArcadePushCooldownUntilUtc.Should().NotBeNull();
        ExtractAutoConnectPackets(playerSession).Should().HaveCount(1, "Must not machine-gun re-push while client unwinds failed connection");

        // 2) Rapid-exit (<20s) circuit breaker MUST hold CreateMatch on idle backup #Q2 for 15s!
        ExtractCreateMatchPackets(arcadeQ2).Should().BeEmpty("Rapid (<20s) server exit must trip 15s CreateMatch circuit breaker");

        // Client finishes unwinding to 1-main and sends CHAT_CMD_LEFT_GAME (since ActiveArcadeMatchId was already cleared by
        // server disconnect, queue wait is NOT paused — player remains eligible once cooldown & circuit-breaker expire!)
        LeftMatch leftProcessor = new(_mockDiscord.Object, _matchmakingService, _arcadeCoordinator);
        leftProcessor.Process(playerSession, new ChatBuffer());
        playerSession.ClientMetadata.LastKnownClientState.Should().Be(ChatProtocol.ChatClientStatus.CHAT_CLIENT_STATUS_CONNECTED);
        playerSession.ClientMetadata.PauseArcadeUntilNextQueueUtc.Should().BeNull();

        // At +10s (< 15s circuit breaker), #Q2 is still held back
        _fakeTime.Advance(TimeSpan.FromSeconds(10));
        await _arcadeCoordinator.EvaluateTickAsync();
        ExtractCreateMatchPackets(arcadeQ2).Should().BeEmpty();

        // At +16s (>= 15s circuit breaker & >= 6s client cooldown), #Q2 launches cleanly, warms for 3.5s, and pushes player cleanly!
        _fakeTime.Advance(TimeSpan.FromSeconds(6));
        await _arcadeCoordinator.EvaluateTickAsync();
        DecodedCreateMatchPacket q2Create = ExtractCreateMatchPackets(arcadeQ2).Single();
        SimulateServerAnnouncesMatch(arcadeQ2, q2Create.MatchUpId, realMatchId: 13002);

        _fakeTime.Advance(TimeSpan.FromSeconds(3.5));
        await _arcadeCoordinator.EvaluateTickAsync();
        ExtractAutoConnectPackets(playerSession).Should().HaveCount(2);
        ExtractAutoConnectPackets(playerSession).Last().MatchId.Should().Be(13002);

        // Finally verify JoinedMatch clears LastArcadePushAttemptAtUtc & ArcadePushCooldownUntilUtc on successful match entry!
        JoinedMatch joinedProcessor = new(new Mock<IDatabase>().Object, _mockDiscord.Object, _matchmakingService);
        ChatBuffer joinedBuf = new();
        joinedBuf.WriteCommand(ChatProtocol.Command.CHAT_CMD_JOINED_GAME);
        joinedBuf.WriteString("QUEUE ARCADE [midwars] #");
        joinedBuf.WriteInt32(13002);
        joinedBuf.WriteInt8(0);
        await joinedProcessor.Process(playerSession, joinedBuf);

        playerSession.ClientMetadata.LastKnownClientState.Should().Be(ChatProtocol.ChatClientStatus.CHAT_CLIENT_STATUS_IN_GAME);
        playerSession.ClientMetadata.LastArcadePushAttemptAtUtc.Should().BeNull();
        playerSession.ClientMetadata.ArcadePushCooldownUntilUtc.Should().BeNull();
    }

    [Fact]
    public async Task SimulationWave14_PendingArcadeJoinRetryAndTimeout_PlusRankedConnectRetryAndKickFallback_SelfHealDropped0x0062()
    {
        // protects: (1) Re-sending Queue Arcade 0x0062 (+6s/+12s/+18s) when a client temporarily drops the initial push
        //               (e.g. opt-in PreloadWorld holding CHostClient non-idle) and releasing the slot at 24s PendingJoinTimeoutSeconds
        //               instead of stranding the player for the full 10-minute bot match,
        //           (2) Preserving PendingRankedMatchId across LeftMatch when unwinding from Queue Arcade for a Ranked pull,
        //           and (3) MatchmakingService.SweepPendingRankedConnects retrying unacknowledged Ranked 0x0062 (+1.5s) and firing
        //               the 2.5s Arcade server Kick fallback if a client remains IN_GAME until JoiningMatch/JoinedMatch acknowledges.
        // fails_when: A dropped 0x0062 leaves ActiveArcadeMatchId stuck for 600s, LeftMatch wipes the Ranked MatchID during pull,
        //             or Ranked ConfirmMatch fires 0x0062 only once without retry/kick recovery.
        // seam: Real QueueArcadeCoordinatorService + MatchmakingService + JoiningMatch + JoinedMatch + LeftMatch + SimulatedTimeProvider.
        SimulatedChatSession arcadeServer = RegisterGameServer(1401, "QUEUE ARCADE #Q1", port: 11485, localPort: 11485, location: "USE");
        SimulatedChatSession rankedServer = RegisterGameServer(1402, "KONGOR RANKED #1", port: 11235, localPort: 11235, location: "USE");
        _arcadeCoordinator.OnArcadeServerHandshake(arcadeServer);
        int arcadeMatchUpId = ExtractCreateMatchPackets(arcadeServer).Single().MatchUpId;
        SimulateServerAnnouncesMatch(arcadeServer, arcadeMatchUpId, realMatchId: 14001);

        // Part A: Solo player enters Mid Wars queue, gets pushed at +6s, and drops all 0x0062 packets (stays CONNECTED)
        (SimulatedChatSession stalledPlayer, _) = EnqueueSoloPlayer(901, "PreloadStalled", gameType: ChatProtocol.TMMGameType.TMM_GAME_TYPE_MIDWARS);
        _fakeTime.Advance(TimeSpan.FromSeconds(6));
        await _arcadeCoordinator.EvaluateTickAsync();
        ExtractAutoConnectPackets(stalledPlayer).Should().HaveCount(1);
        stalledPlayer.ClientMetadata!.ActiveArcadeMatchId.Should().Be(14001);

        // At +6.1s, +12.2s, +18.3s -> Coordinator retries 0x0062 three times while player has not sent JoiningMatch/JoinedMatch!
        for (int retry = 1; retry <= 3; retry++)
        {
            _fakeTime.Advance(TimeSpan.FromSeconds(6.1));
            await _arcadeCoordinator.EvaluateTickAsync();
            ExtractAutoConnectPackets(stalledPlayer).Should().HaveCount(1 + retry);
        }

        // At +24.4s (>= 24s PendingJoinTimeoutSeconds) -> Coordinator releases slot 901 so player is NOT stranded for 10 minutes!
        _fakeTime.Advance(TimeSpan.FromSeconds(6.1));
        await _arcadeCoordinator.EvaluateTickAsync();
        stalledPlayer.ClientMetadata.ActiveArcadeMatchId.Should().BeNull();
        ExtractRemoteCommands(arcadeServer).Should().Contain("kongor_arcade_release 901");

        // After 3.5s cooldown, player is re-pushed cleanly and acknowledges via JoinedMatch!
        _fakeTime.Advance(TimeSpan.FromSeconds(3.5));
        await _arcadeCoordinator.EvaluateTickAsync();
        stalledPlayer.ClientMetadata.ActiveArcadeMatchId.Should().Be(14001);

        JoinedMatch joinedProcessor = new(new Mock<IDatabase>().Object, _mockDiscord.Object, _matchmakingService);
        ChatBuffer arcadeJoinedBuf = new();
        arcadeJoinedBuf.WriteCommand(ChatProtocol.Command.CHAT_CMD_JOINED_GAME);
        arcadeJoinedBuf.WriteString("QUEUE ARCADE [midwars] #");
        arcadeJoinedBuf.WriteInt32(14001);
        arcadeJoinedBuf.WriteInt8(0);
        await joinedProcessor.Process(stalledPlayer, arcadeJoinedBuf);

        // Part B: 9 more Mid Wars players join so a full 10-player Ranked Mid Wars match forms while Player 901 is IN_GAME in Arcade!
        List<MatchmakingGroup> matchGroups = [_matchmakingService.GetMatchmakingGroupByMemberID(901)!];
        for (int i = 2; i <= 10; i++)
        {
            (_, MatchmakingGroup grp) = EnqueueSoloPlayer(900 + i, $"MwPeer_{i}", gameType: ChatProtocol.TMMGameType.TMM_GAME_TYPE_MIDWARS);
            matchGroups.Add(grp);
        }

        (await _matchmakingService.ProcessMatchForSimulationAsync(matchGroups)).Should().BeTrue();
        int rankedMatchUpId = ExtractCreateMatchPackets(rankedServer).Single().MatchUpId;
        SimulateServerAnnouncesMatch(rankedServer, rankedMatchUpId, realMatchId: 94001);

        int rankedConnectCountInitial = ExtractAutoConnectPackets(stalledPlayer).Count(p => p.MatchId == 94001);
        rankedConnectCountInitial.Should().Be(1);

        // Simulate Player 901 still stuck IN_GAME on Arcade at +2.6s -> SweepPendingRankedConnects fires fallback Kick AND retries Ranked 0x0062!
        _fakeTime.Advance(TimeSpan.FromSeconds(2.6));
        _matchmakingService.SweepPendingRankedConnects(_fakeTime.GetUtcNow());
        ExtractRemoteCommands(arcadeServer).Should().Contain("Kick \"PreloadStalled\"");
        ExtractAutoConnectPackets(stalledPlayer).Count(p => p.MatchId == 94001).Should().Be(2);

        // Client unwinds from Arcade and sends LeftMatch -> PendingRankedMatchId preserves MatchID=94001 instead of wiping to null!
        LeftMatch leftProcessor = new(_mockDiscord.Object, _matchmakingService, _arcadeCoordinator);
        leftProcessor.Process(stalledPlayer, new ChatBuffer());
        stalledPlayer.ClientMetadata.MatchID.Should().Be(94001, "LeftMatch from Queue Arcade must preserve PendingRankedMatchId");

        // Next 1.6s sweep delivers Ranked 0x0062 while client is CONNECTED (IDLE), client replies JoiningMatch -> retries stop!
        _fakeTime.Advance(TimeSpan.FromSeconds(1.6));
        _matchmakingService.SweepPendingRankedConnects(_fakeTime.GetUtcNow());
        ExtractAutoConnectPackets(stalledPlayer).Count(p => p.MatchId == 94001).Should().Be(3);

        JoiningMatch joiningProcessor = new(_mockDiscord.Object, _matchmakingService);
        ChatBuffer rankedJoiningBuf = new();
        rankedJoiningBuf.WriteCommand(ChatProtocol.Command.CHAT_CMD_JOINING_GAME);
        rankedJoiningBuf.WriteString("127.0.0.1:11235");
        joiningProcessor.Process(stalledPlayer, rankedJoiningBuf);

        _fakeTime.Advance(TimeSpan.FromSeconds(3.0));
        _matchmakingService.SweepPendingRankedConnects(_fakeTime.GetUtcNow());
        ExtractAutoConnectPackets(stalledPlayer).Count(p => p.MatchId == 94001).Should().Be(3, "Retries must stop once JoiningMatch acknowledges Ranked endpoint");
    }

    [Fact]
    public void ArcadeBotDifficulty_DefaultsToHard3_AndClampsConfiguredOverridesTo1Through3()
    {
        // protects: Queue Arcade default Hard (3) bot difficulty and strict [1..3] clamping matching core.lua profiles.
        // fails_when: QueueArcadeCoordinatorService reverts to Easy (1), exceeds core.lua's [1..3] difficulty range, or overrides soccer/non-bot party maps.
        // seam: Real QueueArcadeCoordinatorService CreateMatch packet generation across InMemoryCollection IConfiguration values.
        (int? ConfigValue, int ExpectedStandardDiff)[] cases =
        [
            (null, 3),
            (2, 2),
            (1, 1),
            (0, 1),
            (-5, 1),
            (4, 3),
            (99, 3)
        ];

        foreach ((int? configValue, int expectedDiff) in cases)
        {
            Dictionary<string, string?> cfg = new()
            {
                ["QueueArcade:Enabled"] = "true",
                ["QueueArcade:IncludeCustomPartyMaps"] = "false"
            };
            if (configValue.HasValue)
            {
                cfg["QueueArcade:BotDifficulty"] = configValue.Value.ToString();
            }

            IConfiguration customConfig = new ConfigurationBuilder()
                .AddInMemoryCollection(cfg)
                .Build();

            QueueArcadeCoordinatorService svc = new(
                customConfig,
                _mockChatContext.Object,
                _matchmakingService,
                _fakeTime,
                randomSeed: 1337,
                distributedCache: null);

            SimulatedChatSession srv = RegisterGameServer(800 + (configValue ?? 50), "QUEUE ARCADE #DIFF", 21900, 11900);
            svc.OnArcadeServerHandshake(srv);

            DecodedCreateMatchPacket pkt = ExtractCreateMatchPackets(srv).Single();
            string mapName = srv.ServerMetadata!.MapName;
            int expectedForMap = mapName.Equals("soccer", StringComparison.OrdinalIgnoreCase) ? 3 : expectedDiff;
            pkt.MatchSettings.Should().Contain(
                $"botdifficulty:{expectedForMap}",
                $"Config QueueArcade:BotDifficulty={configValue?.ToString() ?? "<default>"} on map '{mapName}' must emit botdifficulty:{expectedForMap}");
        }
    }

    [Fact]
    public void ArcadeBotPool_ContainsAll100VerifiedBotDefinitions_WithValidHeroAndRootLuaOnDisk()
    {
        // protects: Full A-Z 100-hero Queue Arcade bot pool integrity against missing .bot XMLs, broken rootlua paths, or missing heroname attributes.
        // fails_when: ArcadeBotPool drops back to the truncated 40-bot A-H list, contains duplicates, or references a .bot file whose Lua brain script is missing.
        // seam: Real QueueArcadeCoordinatorService.ArcadeBotPool audited against games/server/override/bots/definitions/*.bot on disk.
        QueueArcadeCoordinatorService.ArcadeBotPool.Should().HaveCount(100);
        QueueArcadeCoordinatorService.ArcadeBotPool.Distinct(StringComparer.OrdinalIgnoreCase)
            .Should().HaveCount(100, "Every ArcadeBotPool entry must be unique");

        string? cursor = AppContext.BaseDirectory;
        string? botDefsDir = null;
        while (!string.IsNullOrEmpty(cursor))
        {
            string candidate = Path.Combine(cursor, "games", "server", "override", "bots", "definitions");
            if (Directory.Exists(candidate))
            {
                botDefsDir = candidate;
                break;
            }
            cursor = Directory.GetParent(cursor)?.FullName;
        }

        if (botDefsDir == null)
        {
            // In cleanroom mode / public export verification, games/ is excluded from export.
            return;
        }

        Dictionary<string, (string HeroName, string RootLuaPath)> diskBots = new(StringComparer.Ordinal);
        foreach (string botFile in Directory.GetFiles(botDefsDir!, "*.bot"))
        {
            System.Xml.Linq.XDocument doc = System.Xml.Linq.XDocument.Load(botFile);
            System.Xml.Linq.XElement? root = doc.Root;
            root.Should().NotBeNull();
            string name = root!.Attribute("name")?.Value ?? string.Empty;
            string heroName = root.Attribute("heroname")?.Value ?? string.Empty;
            string rootLua = root.Attribute("rootlua")?.Value ?? string.Empty;

            name.Should().NotBeNullOrWhiteSpace($"File '{Path.GetFileName(botFile)}' must declare name");
            heroName.Should().StartWith("Hero_", $"Bot '{name}' must reference a valid Hero_* entity");
            rootLua.Should().NotBeNullOrWhiteSpace($"Bot '{name}' must declare rootlua");

            string fullLuaPath = Path.GetFullPath(Path.Combine(botDefsDir!, rootLua));
            File.Exists(fullLuaPath).Should().BeTrue($"Bot '{name}' rootlua '{fullLuaPath}' must exist on disk");
            diskBots[name] = (heroName, fullLuaPath);
        }

        QueueArcadeCoordinatorService.ArcadeBotPool.Should().BeEquivalentTo(
            diskBots.Keys,
            "ArcadeBotPool must include all 100 verified .bot definitions without omissions or typos");
    }

    [Fact]
    public async Task InProcessMatchRotation_RefreshesServerSessionCookieFromRedisCache_AndIgnoresOlderCompelIdleHeartbeats()
    {
        // protects: Multi-round in-process Queue Arcade rotation (CHostServer calling f=new_session without reconnecting TCP ChatSession)
        //           so 0x0522 NET_CHAT_GS_REMOTE_COMMAND packets (AddBot, kongor_arcade_reserve, kongor_arcade_release) are never rejected
        //           with 'HandleRemoteCommand() - invalid session cookie.' while ignoring stale COMPEL idle heartbeat cookies.
        // fails_when: serverSession.ServerMetadata.SessionCookie stays pinned to the initial TCP handshake cookie across in-process
        //             match rotations, or gets overwritten by COMPEL's older startup cookie during idle heartbeats.
        // seam: Real QueueArcadeCoordinatorService + mocked IDatabase (MATCH-SERVER-INDEX & MATCH-SERVERS MemoryPack blobs).
        Mock<IDatabase> mockDb = new();
        ASPIRE.Common.Models.ServerManagement.MatchServer? currentCachedServer = null;

        mockDb.Setup(db => db.HashGet("MATCH-SERVER-INDEX", (RedisValue)910, It.IsAny<CommandFlags>()))
            .Returns(() => currentCachedServer is not null ? (RedisValue)"slave1:11485" : RedisValue.Null);
        mockDb.Setup(db => db.HashGet("MATCH-SERVERS", (RedisValue)"slave1:11485", It.IsAny<CommandFlags>()))
            .Returns(() => currentCachedServer is not null
                ? (RedisValue)MemoryPack.MemoryPackSerializer.Serialize(currentCachedServer)
                : RedisValue.Null);
        mockDb.Setup(db => db.HashGetAsync("MATCH-SERVER-INDEX", (RedisValue)910, It.IsAny<CommandFlags>()))
            .ReturnsAsync(() => currentCachedServer is not null ? (RedisValue)"slave1:11485" : RedisValue.Null);
        mockDb.Setup(db => db.HashGetAsync("MATCH-SERVERS", (RedisValue)"slave1:11485", It.IsAny<CommandFlags>()))
            .ReturnsAsync(() => currentCachedServer is not null
                ? (RedisValue)MemoryPack.MemoryPackSerializer.Serialize(currentCachedServer)
                : RedisValue.Null);

        QueueArcadeCoordinatorService coordinator = new(
            _configuration,
            _mockChatContext.Object,
            _matchmakingService,
            _fakeTime,
            randomSeed: 77,
            distributedCache: mockDb.Object);

        SimulatedChatSession srv = RegisterGameServer(910, "QUEUE ARCADE #Q1", 21485, 11485);
        srv.ServerMetadata!.SessionCookie = "cookie_round_1_initial";

        DateTimeOffset t0 = _fakeTime.GetUtcNow();
        currentCachedServer = new ASPIRE.Common.Models.ServerManagement.MatchServer
        {
            ID = 910,
            HostAccountID = 1001,
            HostAccountName = "slave1",
            Name = "QUEUE ARCADE #Q1",
            Instance = 1,
            IPAddress = "127.0.0.1",
            Port = 11485,
            Location = "USE",
            Description = string.Empty,
            Cookie = "cookie_round_2_rotated",
            Status = ASPIRE.Common.Models.ServerManagement.ServerStatus.SERVER_STATUS_LOADING,
            TimestampRegistered = t0.AddSeconds(10),
            TimestampLastHeartbeat = t0.AddSeconds(10)
        };

        await coordinator.EvaluateTickAsync();
        srv.ServerMetadata.SessionCookie.Should().Be(
            "cookie_round_2_rotated",
            "EvaluateTickAsync must adopt the new engine session cookie after in-process f=new_session rotation");

        // Simulate COMPEL's 15s idle worker heartbeat overwriting Redis with an older TimestampRegistered and IDLE status
        currentCachedServer = new ASPIRE.Common.Models.ServerManagement.MatchServer
        {
            ID = 910,
            HostAccountID = 1001,
            HostAccountName = "slave1",
            Name = "QUEUE ARCADE #Q1",
            Instance = 1,
            IPAddress = "127.0.0.1",
            Port = 11485,
            Location = "USE",
            Description = "QUEUE ARCADE #Q1",
            Cookie = "cookie_compel_startup_old",
            Status = ASPIRE.Common.Models.ServerManagement.ServerStatus.SERVER_STATUS_IDLE,
            TimestampRegistered = t0.AddMinutes(-5),
            TimestampLastHeartbeat = t0.AddSeconds(25)
        };

        await coordinator.EvaluateTickAsync();
        srv.ServerMetadata.SessionCookie.Should().Be(
            "cookie_round_2_rotated",
            "COMPEL's synthetic idle heartbeat (older TimestampRegistered) must never overwrite the live engine session cookie");
    }

    [Fact]
    public async Task VoluntaryLeaveAndArcadeChatCommands_UnpausesOnJoinOrSmartToggle_RoutesViaSendIMBridge_AndDebouncesSpam()
    {
        // protects: Voluntary mid-match leave whisper + pause, /arcade & !arcade IM bridge (SendInstantMessage '__kongor_arcade__'),
        //           smart bare /arcade unpause when paused, and 5s anti-spam debounce on /arcade join.
        // fails_when: Leaving a running mini-game fails to whisper or pause, ChatSendIM('__kongor_arcade__', ...) emits CHAT_CMD_IM_FAILED,
        //             bare '/arcade' toggles OptIn to false while paused, or /arcade join spam bypasses the 5s debounce.
        // seam: Real QueueArcadeCoordinatorService + LeftMatch + SendInstantMessage + SendWhisper + MatchmakingService.
        SimulatedChatSession arcadeSrv = RegisterGameServer(920, "QUEUE ARCADE #Q1", 21485, 11485);
        await _arcadeCoordinator.EvaluateTickAsync();
        DecodedCreateMatchPacket createPkt = ExtractCreateMatchPackets(arcadeSrv).Single();
        SimulateServerAnnouncesMatch(arcadeSrv, createPkt.MatchUpId, realMatchId: 19201);

        (SimulatedChatSession player, MatchmakingGroup group) = EnqueueSoloPlayer(9201, "HopBackHero");
        _fakeTime.Advance(TimeSpan.FromSeconds(6));
        await _arcadeCoordinator.EvaluateTickAsync();

        ExtractAutoConnectPackets(player).Should().ContainSingle(p => p.MatchId == 19201);

        // 1. Player clicks Disconnect while match 19201 is actively running -> sets QueueArcadeOptIn = false across current & future queues!
        LeftMatch leftProcessor = new(_mockDiscord.Object, _matchmakingService, _arcadeCoordinator);
        leftProcessor.Process(player, new ChatBuffer());

        player.ClientMetadata!.QueueArcadeOptIn.Should().BeFalse();
        player.ClientMetadata.PauseArcadeUntilNextQueueUtc.Should().BeNull();
        ExtractSystemWhispers(player).Should().Contain(w => w.Contains("Queue Mini-Games DISABLED after disconnecting", StringComparison.Ordinal));

        // Next tick does NOT push the opted-out player back in against their will
        _fakeTime.Advance(TimeSpan.FromSeconds(4));
        await _arcadeCoordinator.EvaluateTickAsync();
        ExtractAutoConnectPackets(player).Should().HaveCount(1);

        // 2. Player types bare "/arcade" on STATUS/Channel tab -> routed via SendInstantMessage('__kongor_arcade__', '/arcade').
        //    Unified Toggle Rule flips QueueArcadeOptIn false -> true and rejoins!
        SendInstantMessage imProcessor = new(_mockChatContext.Object, _arcadeCoordinator);
        ChatBuffer bareToggleIm = new();
        bareToggleIm.WriteString("__kongor_arcade__");
        bareToggleIm.WriteString("/arcade");
        bareToggleIm.WriteInt8(1);
        imProcessor.Process(player, bareToggleIm);

        player.ClientMetadata.QueueArcadeOptIn.Should().BeTrue("Bare /arcade while disabled must re-enable OptIn and rejoin");
        player.ClientMetadata.PauseArcadeUntilNextQueueUtc.Should().BeNull();
        ExtractSystemWhispers(player).Last().Should().Contain("Re-joining Queue Arcade");

        // Next tick pushes the player right back into Match 19201!
        await _arcadeCoordinator.EvaluateTickAsync();
        ExtractAutoConnectPackets(player).Should().HaveCount(2);

        // 3. Player types "!arcade leave" via IM bridge -> sets QueueArcadeOptIn = false & releases slot
        ChatBuffer leaveCmdIm = new();
        leaveCmdIm.WriteString("__kongor_arcade__");
        leaveCmdIm.WriteString("!arcade leave");
        leaveCmdIm.WriteInt8(1);
        imProcessor.Process(player, leaveCmdIm);

        player.ClientMetadata.QueueArcadeOptIn.Should().BeFalse();
        player.ClientMetadata.ActiveArcadeMatchId.Should().BeNull();

        // 4. Immediate "/arcade join" 1s later (< 5s debounce window) is debounced cleanly
        _fakeTime.Advance(TimeSpan.FromSeconds(1));
        ChatBuffer rapidRejoinIm = new();
        rapidRejoinIm.WriteString("__kongor_arcade__");
        rapidRejoinIm.WriteString("/arcade join");
        rapidRejoinIm.WriteInt8(1);
        imProcessor.Process(player, rapidRejoinIm);
        player.ClientMetadata.QueueArcadeOptIn.Should().BeFalse(
            "/arcade join within 5s of previous join must be debounced");
        ExtractSystemWhispers(player).Last().Should().Contain("Please wait a few seconds");

        // 5. Advance past 5s debounce window -> "/arcade rejoin" succeeds and pushes player back in!
        _fakeTime.Advance(TimeSpan.FromSeconds(5));
        ChatBuffer validRejoinIm = new();
        validRejoinIm.WriteString("__kongor_arcade__");
        validRejoinIm.WriteString("/arcade rejoin");
        validRejoinIm.WriteInt8(1);
        imProcessor.Process(player, validRejoinIm);

        player.ClientMetadata.QueueArcadeOptIn.Should().BeTrue();
        player.ClientMetadata.PauseArcadeUntilNextQueueUtc.Should().BeNull();
        await _arcadeCoordinator.EvaluateTickAsync();
        ExtractAutoConnectPackets(player).Should().HaveCount(3);
    }

    [Fact]
    public async Task ManualDisconnect_EvenWhenFollowedImmediatelyByMatchComplete_OrAcrossFutureQueues_OrFromVictoryScreen_KeepsOptInFalseUntilReEnabled()
    {
        // protects: User requirement that ANY manual disconnect from a Queue Arcade match (mid-game even when sole-human
        //           disconnect immediately triggers 0x0515 MatchComplete, or manual disconnect during the 12s Victory screen)
        //           keeps QueueArcadeOptIn = false across the current queue AND future matchmaking queues until re-enabled via
        //           ARCADE button or /arcade command, while resetting to true on a fresh login session.
        // fails_when: MatchComplete within 5s wipes the opt-out, leaving queue & re-queuing auto-pushes without opt-in,
        //             or manually disconnecting on the Victory screen leaves QueueArcadeOptIn = true.
        // seam: Real QueueArcadeCoordinatorService + LeftMatch + GroupLeaveQueue + MatchComplete + SendInstantMessage.
        SimulatedChatSession arcadeSrv = RegisterGameServer(930, "QUEUE ARCADE #Q1", 21485, 11485);
        await _arcadeCoordinator.EvaluateTickAsync();
        DecodedCreateMatchPacket createRound1 = ExtractCreateMatchPackets(arcadeSrv).Single();
        SimulateServerAnnouncesMatch(arcadeSrv, createRound1.MatchUpId, realMatchId: 19301);

        (SimulatedChatSession player, MatchmakingGroup group) = EnqueueSoloPlayer(9301, "ManualLeaver");
        player.ClientMetadata!.QueueArcadeOptIn.Should().BeTrue("Fresh login session must default QueueArcadeOptIn to true");

        _fakeTime.Advance(TimeSpan.FromSeconds(6));
        await _arcadeCoordinator.EvaluateTickAsync();
        ExtractAutoConnectPackets(player).Should().ContainSingle(p => p.MatchId == 19301);

        // 1. Player manually disconnects -> 200ms later sole-human server emits 0x0515 MatchComplete!
        LeftMatch leftProcessor = new(_mockDiscord.Object, _matchmakingService, _arcadeCoordinator);
        leftProcessor.Process(player, new ChatBuffer());
        player.ClientMetadata.QueueArcadeOptIn.Should().BeFalse();

        _fakeTime.Advance(TimeSpan.FromMilliseconds(200));
        SimulateServerMatchComplete(arcadeSrv, realMatchId: 19301, accountIds: [9301]);

        player.ClientMetadata.QueueArcadeOptIn.Should().BeFalse(
            "MatchComplete arriving right after manual disconnect must NEVER wipe the player's opt-out");

        // Round 2 spins up on #Q1 -> player stays in lobby (NOT re-pulled!)
        DecodedCreateMatchPacket createRound2 = ExtractCreateMatchPackets(arcadeSrv).Last();
        SimulateServerAnnouncesMatch(arcadeSrv, createRound2.MatchUpId, realMatchId: 19302);
        _fakeTime.Advance(TimeSpan.FromSeconds(10));
        await _arcadeCoordinator.EvaluateTickAsync();
        ExtractAutoConnectPackets(player).Should().HaveCount(1, "Disconnected player must NOT be auto-pulled into Round 2");

        // 2. Player leaves queue and joins a brand-new matchmaking queue later -> STILL stays OFF!
        GroupLeaveQueue leaveQueueProcessor = new(_matchmakingService, _mockDiscord.Object, _arcadeCoordinator);
        ChatBuffer leaveQueueBuf = new();
        leaveQueueBuf.WriteCommand(ChatProtocol.Matchmaking.NET_CHAT_CL_TMM_GROUP_LEAVE_QUEUE);
        leaveQueueProcessor.Process(player, leaveQueueBuf);
        group.QueueStartTime.Should().BeNull();
        player.ClientMetadata.QueueArcadeOptIn.Should().BeFalse("Leaving queue must preserve QueueArcadeOptIn = false across future queues");

        _fakeTime.Advance(TimeSpan.FromSeconds(30));
        group.QueueStartTime = _fakeTime.GetUtcNow();
        _fakeTime.Advance(TimeSpan.FromSeconds(10));
        await _arcadeCoordinator.EvaluateTickAsync();
        ExtractAutoConnectPackets(player).Should().HaveCount(1, "Future queue in same login session must still keep Queue Arcade OFF");

        // 3. Player clicks ARCADE: OFF button (sends !arcade join via IM bridge) -> hops right into Round 2 (19302)!
        SendInstantMessage imProcessor = new(_mockChatContext.Object, _arcadeCoordinator);
        ChatBuffer clickArcadeBtn = new();
        clickArcadeBtn.WriteString("__kongor_arcade__");
        clickArcadeBtn.WriteString("!arcade join");
        clickArcadeBtn.WriteInt8(1);
        imProcessor.Process(player, clickArcadeBtn);

        player.ClientMetadata.QueueArcadeOptIn.Should().BeTrue();
        await _arcadeCoordinator.EvaluateTickAsync();
        ExtractAutoConnectPackets(player).Should().Contain(p => p.MatchId == 19302);

        // 4. Player enters Round 2 IN_GAME, Round 2 finishes -> enters 12s Victory hold, then player MANUALLY disconnects at t=3s!
        player.ClientMetadata.LastKnownClientState = ChatProtocol.ChatClientStatus.CHAT_CLIENT_STATUS_IN_GAME;
        player.ClientMetadata.MatchID = 19302;
        SimulateServerMatchComplete(arcadeSrv, realMatchId: 19302, accountIds: [9301]);
        player.ClientMetadata.PostArcadeVictoryMatchId.Should().Be(19302);
        player.ClientMetadata.QueueArcadeOptIn.Should().BeTrue("Entering Victory screen naturally keeps OptIn = true for 12s auto-pull");

        _fakeTime.Advance(TimeSpan.FromSeconds(3));
        leftProcessor.Process(player, new ChatBuffer());
        player.ClientMetadata.QueueArcadeOptIn.Should().BeFalse("Manually clicking Disconnect on the Victory screen must set QueueArcadeOptIn = false");
        player.ClientMetadata.PostArcadeVictoryMatchId.Should().BeNull();
    }

    [Fact]
    public async Task EventDrivenBotReadiness_WhenExplicitAckArrives_ImmediatelyUnlocksPushUnder100Ms()
    {
        // protects: Event-driven bot readiness unlocking queued players as soon as match server acknowledges bots (0ms wasted sleep)
        // fails_when: Server bot readiness is ignored and players wait the full fallback sleep duration
        // seam: Real QueueArcadeCoordinatorService + NotifyArcadeServerBotReadiness + SimulatedTimeProvider
        (SimulatedChatSession playerSession, _) = EnqueueSoloPlayer(
            accountId: 9401,
            name: "speedy_bot_player",
            gameType: ChatProtocol.TMMGameType.TMM_GAME_TYPE_NORMAL);

        _fakeTime.Advance(TimeSpan.FromSeconds(20));
        SimulatedChatSession arcadeSrv = RegisterGameServer(1401, "QUEUE ARCADE #FAST", port: 11490, localPort: 11490, location: "USE");
        _arcadeCoordinator.OnArcadeServerHandshake(arcadeSrv);
        DecodedCreateMatchPacket createPacket = ExtractCreateMatchPackets(arcadeSrv).Single();

        SimulateServerAnnouncesMatch(arcadeSrv, createPacket.MatchUpId, realMatchId: 14001);

        // At +50ms after AnnounceMatch, before any bot readiness ack, player must NOT be pushed
        _fakeTime.Advance(TimeSpan.FromMilliseconds(50));
        await _arcadeCoordinator.EvaluateTickAsync();
        ExtractAutoConnectPackets(playerSession).Should().BeEmpty("Push is gated while BotsSeededReady is false before fallback timeout");

        // Server reports active bots seeded (10 bots) in < 80ms
        _arcadeCoordinator.NotifyArcadeServerBotReadiness(1401, activeBotCount: 10, explicitReadyAck: true);

        // Next evaluation tick immediately pushes the player into the match (< 100ms total elapsed!)
        _fakeTime.Advance(TimeSpan.FromMilliseconds(30));
        await _arcadeCoordinator.EvaluateTickAsync();
        ExtractAutoConnectPackets(playerSession).Should().ContainSingle(
            p => p.MatchId == 14001,
            "Calling NotifyArcadeServerBotReadiness must immediately unlock player push in < 100ms without waiting for fallback timeout");
    }

    [Fact]
    public async Task EventDrivenBotReadiness_WhenAckNeverArrives_FallbackTimeoutUnlocksPushAndIncrementsTelemetry()
    {
        // protects: Bounded safety fallback timeout unlocking queued players when server drops status packets
        // fails_when: Queue Arcade match hangs forever on dropped status updates or fails to record fallback telemetry
        // seam: QueueArcadeCoordinatorService fallback timeout + QueueArcadeMetricsSnapshot.BotReadinessFallbackTimeoutsTotal
        long baselineFallbackTimeouts = _arcadeCoordinator.GetMetricsSnapshot().BotReadinessFallbackTimeoutsTotal;

        (SimulatedChatSession playerSession, _) = EnqueueSoloPlayer(
            accountId: 9402,
            name: "fallback_bot_player",
            gameType: ChatProtocol.TMMGameType.TMM_GAME_TYPE_NORMAL);

        _fakeTime.Advance(TimeSpan.FromSeconds(20));
        SimulatedChatSession arcadeSrv = RegisterGameServer(1402, "QUEUE ARCADE #FALLBACK", port: 11491, localPort: 11491, location: "USE");
        _arcadeCoordinator.OnArcadeServerHandshake(arcadeSrv);
        DecodedCreateMatchPacket createPacket = ExtractCreateMatchPackets(arcadeSrv).Single();

        SimulateServerAnnouncesMatch(arcadeSrv, createPacket.MatchUpId, realMatchId: 14002);

        // At 1.5s and 2.5s (< 3.0s fallback timeout), player is still gated
        _fakeTime.Advance(TimeSpan.FromMilliseconds(1500));
        await _arcadeCoordinator.EvaluateTickAsync();
        ExtractAutoConnectPackets(playerSession).Should().BeEmpty();

        _fakeTime.Advance(TimeSpan.FromMilliseconds(1000));
        await _arcadeCoordinator.EvaluateTickAsync();
        ExtractAutoConnectPackets(playerSession).Should().BeEmpty();

        // At 3.1s (>= 3.0s fallback timeout), fallback unlocks push and increments telemetry counter
        _fakeTime.Advance(TimeSpan.FromMilliseconds(600));
        await _arcadeCoordinator.EvaluateTickAsync();

        ExtractAutoConnectPackets(playerSession).Should().ContainSingle(
            p => p.MatchId == 14002,
            "Bounded fallback timeout must unlock push even if explicit ack never arrived");

        QueueArcadeMetricsSnapshot metrics = _arcadeCoordinator.GetMetricsSnapshot();
        metrics.BotReadinessFallbackTimeoutsTotal.Should().Be(
            baselineFallbackTimeouts + 1,
            "Fallback timeout activation must increment BotReadinessFallbackTimeoutsTotal telemetry");
    }

    [Fact]
    public async Task VictoryScreenAutoPull_Holds12SecondsPrewarmsQ2AndPullsInGamePlayerWithoutLeftMatchWipe()
    {
        // protects: 12s Victory screen auto-pull end-to-end flow: #Q1 holds out of IDLE on MatchComplete while #Q2 pre-warms
        //           immediately at T=0s, IN_GAME player stays on Victory screen for full 12s before receiving 0x0062 to #Q2,
        //           and #Q1's trailing LeftMatch packet is absorbed without wiping #Q2's ActiveArcadeMatchId/MatchID or pausing queue.
        // fails_when: #Q2 fails to pre-warm on MatchComplete, IN_GAME player is skipped or pulled before 12s, periodic 0x0502 IDLE
        //             heartbeat re-schedules #Q1 during hold, or LeftMatch from #Q1 teardown wipes #Q2 state / pauses Queue Arcade.
        // seam: Real QueueArcadeCoordinatorService + JoinedMatch + MatchComplete + ServerStatus + LeftMatch + SimulatedTimeProvider.
        SimulatedChatSession arcadeQ1 = RegisterGameServer(1501, "QUEUE ARCADE #Q1", port: 21485, localPort: 11485, location: "USE");
        SimulatedChatSession arcadeQ2 = RegisterGameServer(1502, "QUEUE ARCADE #Q2", port: 21486, localPort: 11486, location: "USE");

        await _arcadeCoordinator.EvaluateTickAsync();
        DecodedCreateMatchPacket q1Create = ExtractCreateMatchPackets(arcadeQ1).Single();
        ExtractCreateMatchPackets(arcadeQ2).Should().BeEmpty("#Q2 stays warm-idle until #Q1 finishes or hits >=80% fill");
        SimulateServerAnnouncesMatch(arcadeQ1, q1Create.MatchUpId, realMatchId: 20101);
        _arcadeCoordinator.NotifyArcadeServerBotReadiness(1501, activeBotCount: 10, explicitReadyAck: true);

        (SimulatedChatSession player, MatchmakingGroup group) = EnqueueSoloPlayer(9501, "VictoryAutoPullHero");
        _fakeTime.Advance(TimeSpan.FromSeconds(6));
        await _arcadeCoordinator.EvaluateTickAsync();
        ExtractAutoConnectPackets(player).Should().ContainSingle(p => p.MatchId == 20101 && p.Port == 11485);

        // Client finishes connecting into #Q1 Match 20101 -> sends real CHAT_CMD_JOINED_GAME (LastKnownClientState = IN_GAME)
        Mock<IDatabase> mockDb = new();
        JoinedMatch joinedProcessor = new(mockDb.Object, _mockDiscord.Object, _matchmakingService);
        ChatBuffer joinedBuf = new();
        joinedBuf.WriteCommand(ChatProtocol.Command.CHAT_CMD_JOINED_GAME);
        joinedBuf.WriteString("QUEUE ARCADE #Q1");
        joinedBuf.WriteInt32(20101);
        joinedBuf.WriteInt8(1);
        await joinedProcessor.Process(player, joinedBuf);
        player.ClientMetadata!.LastKnownClientState.Should().Be(ChatProtocol.ChatClientStatus.CHAT_CLIENT_STATUS_IN_GAME);

        // Round 1 ends on #Q1 while player is still on Victory screen (0 manual disconnect!)
        SimulateServerMatchComplete(arcadeQ1, realMatchId: 20101, accountIds: [9501]);

        player.ClientMetadata.PostArcadeVictoryMatchId.Should().Be(20101);
        player.ClientMetadata.PostArcadeVictoryServerId.Should().Be(1501);
        ExtractSystemWhispers(player).Last().Should().Contain("Auto-pulling you into the next mini-game in 12 seconds");

        // #Q1 stays held out of IDLE while #Q2 immediately receives CreateMatch at T=0s to pre-warm Round 2!
        ExtractCreateMatchPackets(arcadeQ1).Should().HaveCount(1, "#Q1 must stay held during Victory screen instead of clobbering itself");
        ExtractCreateMatchPackets(arcadeQ2).Should().ContainSingle("#Q2 must immediately pre-warm the next mini-game at T=0s while #Q1 holds Victory screen");

        // Simulate a periodic 0x0502 SERVER_STATUS_IDLE heartbeat from #Q1 during the 20s hold window -> must NOT re-schedule #Q1!
        arcadeQ1.ServerMetadata!.Status = ChatProtocol.ServerStatus.SERVER_STATUS_IDLE;
        await _arcadeCoordinator.EvaluateTickAsync();
        ExtractCreateMatchPackets(arcadeQ1).Should().HaveCount(1, "_postMatchVictoryHoldUntilUtc must block #Q1 even if 0x0502 sets Status=IDLE");

        // At T=6s (< 12s Victory hold), #Q2 finishes loading Match 20102 and seeds bots -> player MUST stay on Victory screen until 12s!
        _fakeTime.Advance(TimeSpan.FromSeconds(6));
        DecodedCreateMatchPacket q2Create = ExtractCreateMatchPackets(arcadeQ2).Single();
        SimulateServerAnnouncesMatch(arcadeQ2, q2Create.MatchUpId, realMatchId: 20102);
        _arcadeCoordinator.NotifyArcadeServerBotReadiness(1502, activeBotCount: 10, explicitReadyAck: true);
        await _arcadeCoordinator.EvaluateTickAsync();
        ExtractAutoConnectPackets(player).Should().HaveCount(1, "Player must enjoy the full 12s Victory screen before auto-pull");

        // Advance to T=12.2s (>= 12s Victory hold) -> IN_GAME player is auto-pulled directly into #Q2 Match 20102!
        _fakeTime.Advance(TimeSpan.FromSeconds(6.2));
        await _arcadeCoordinator.EvaluateTickAsync();

        ExtractAutoConnectPackets(player).Should().HaveCount(2);
        DecodedAutoConnectPacket secondConnect = ExtractAutoConnectPackets(player).Last();
        secondConnect.MatchId.Should().Be(20102);
        secondConnect.Port.Should().Be(11486);

        player.ClientMetadata.ActiveArcadeMatchId.Should().Be(20102);
        player.ClientMetadata.ActiveArcadeServerId.Should().Be(1502);
        player.ClientMetadata.AwaitingVictoryTransferFromServerId.Should().Be(1501);
        player.ClientMetadata.PostArcadeVictoryMatchId.Should().BeNull();

        // K2 client disconnects from #Q1's Victory screen to connect to #Q2 -> emits trailing LeftMatch for #Q1!
        LeftMatch leftProcessor = new(_mockDiscord.Object, _matchmakingService, _arcadeCoordinator);
        leftProcessor.Process(player, new ChatBuffer());

        player.ClientMetadata.AwaitingVictoryTransferFromServerId.Should().BeNull("Trailing LeftMatch from #Q1 must consume the transfer guard");
        player.ClientMetadata.ActiveArcadeMatchId.Should().Be(20102, "Trailing LeftMatch from #Q1 must NEVER wipe #Q2's ActiveArcadeMatchId");
        player.ClientMetadata.ActiveArcadeServerId.Should().Be(1502, "Trailing LeftMatch from #Q1 must NEVER wipe #Q2's ActiveArcadeServerId");
        player.ClientMetadata.LastArcadePushAttemptAtUtc.Should().NotBeNull("Suppressed LeftMatch must NEVER wipe LastArcadePushAttemptAtUtc");
        player.ClientMetadata.LastKnownClientState.Should().Be(
            ChatProtocol.ChatClientStatus.CHAT_CLIENT_STATUS_IN_GAME,
            "Suppressed LeftMatch must NEVER broadcast a bogus CONNECTED lobby status mid-transfer");
        player.ClientMetadata.PauseArcadeUntilNextQueueUtc.Should().BeNull("Trailing LeftMatch during Victory transfer must NEVER pause Queue Arcade");
        group.QueueStartTime.Should().NotBeNull("Player remains in matchmaking queue throughout multi-round Arcade auto-pulls");
    }

    [Fact]
    public async Task VictoryScreenHold_WhenRankedMatchPopsDuring12sHold_IncludesVictoryServerInKickFallback()
    {
        // protects: Ranked matchmaking pop while a player is sitting on #Q1's 12s Post-Match Victory screen (ActiveArcadeServerId == null,
        //           PostArcadeVictoryServerId == #Q1), ensuring MatchmakingService registers #Q1 for the 2.5s clientkick fallback if K2
        //           drops 0x0062 on the post-game stats screen.
        // fails_when: MatchmakingService only checks ActiveArcadeServerId (already cleared on MatchComplete) and strands the player on #Q1.
        // seam: Real MatchmakingService + QueueArcadeCoordinatorService + JoinedMatch + MatchComplete + AnnounceMatch.
        SimulatedChatSession arcadeQ1 = RegisterGameServer(1511, "QUEUE ARCADE #Q1", port: 21485, localPort: 11485, location: "USE");
        SimulatedChatSession rankedSrv = RegisterGameServer(1599, "KONGOR RANKED #L1", port: 21235, localPort: 11235, location: "USE");

        await _arcadeCoordinator.EvaluateTickAsync();
        DecodedCreateMatchPacket q1Create = ExtractCreateMatchPackets(arcadeQ1).Single();
        SimulateServerAnnouncesMatch(arcadeQ1, q1Create.MatchUpId, realMatchId: 20201);
        _arcadeCoordinator.NotifyArcadeServerBotReadiness(1511, activeBotCount: 10, explicitReadyAck: true);

        (SimulatedChatSession p1, MatchmakingGroup g1) = EnqueueSoloPlayer(9601, "VictoryRankedHero");
        _fakeTime.Advance(TimeSpan.FromSeconds(6));
        await _arcadeCoordinator.EvaluateTickAsync();

        Mock<IDatabase> mockDb = new();
        JoinedMatch joinedProcessor = new(mockDb.Object, _mockDiscord.Object, _matchmakingService);
        ChatBuffer joinedBuf = new();
        joinedBuf.WriteCommand(ChatProtocol.Command.CHAT_CMD_JOINED_GAME);
        joinedBuf.WriteString("QUEUE ARCADE #Q1");
        joinedBuf.WriteInt32(20201);
        joinedBuf.WriteInt8(1);
        await joinedProcessor.Process(p1, joinedBuf);

        // #Q1 ends -> p1 is now on #Q1's Victory screen (ActiveArcadeServerId is null, PostArcadeVictoryServerId is 1511)
        SimulateServerMatchComplete(arcadeQ1, realMatchId: 20201, accountIds: [9601]);
        p1.ClientMetadata!.ActiveArcadeServerId.Should().BeNull();
        p1.ClientMetadata.PostArcadeVictoryServerId.Should().Be(1511);

        // 9 more Ranked players enqueue -> 5v5 Ranked match forms on rankedSrv (#1599) during the 12s Victory hold!
        List<MatchmakingGroup> tenGroups = [g1];
        for (int i = 2; i <= 10; i++)
        {
            (_, MatchmakingGroup grp) = EnqueueSoloPlayer(9600 + i, $"RankedPeer_{i}");
            tenGroups.Add(grp);
        }

        (await _matchmakingService.ProcessMatchForSimulationAsync(tenGroups)).Should().BeTrue();
        DecodedCreateMatchPacket rankedCreate = ExtractCreateMatchPackets(rankedSrv).Single();

        _fakeTime.Advance(TimeSpan.FromSeconds(2));
        SimulateServerAnnouncesMatch(rankedSrv, rankedCreate.MatchUpId, realMatchId: 90201);

        p1.ClientMetadata.MatchID.Should().Be(90201);
        p1.ClientMetadata.PostArcadeVictoryMatchId.Should().BeNull("Ranked pull must clear PostArcadeVictoryMatchId");
        p1.ClientMetadata.AwaitingArcadeDisconnectForRankedPull.Should().BeTrue("Victory screen IN_GAME player must arm Ranked kick fallback");

        // Simulate K2 post-game screen ignoring 0x0062 for 3s -> MatchmakingService fallback kick fires on #Q1 (1511)!
        _fakeTime.Advance(TimeSpan.FromSeconds(3));
        _matchmakingService.SweepPendingRankedConnects(_fakeTime.GetUtcNow());
        ExtractRemoteCommands(arcadeQ1).Should().Contain(
            cmd => cmd.Contains("Kick \"VictoryRankedHero\"", StringComparison.Ordinal),
            "MatchmakingService must kick Victory screen holdouts from PostArcadeVictoryServerId so Ranked connect completes");
    }

    [Fact]
    public async Task LoadingTimeoutFailover_QuarantinesWedgedLoadingServerAndFailsOverToHealthyIdleServer()
    {
        // protects: Wedged SERVER_STATUS_LOADING quarantine (60s backoff + SERVER_STATUS_KILLED + 'Quit' command) so a hung
        //           dedicated slave never spins in a 0s tight loop or blocks healthy standby workers from running Queue Arcade.
        // fails_when: EvaluateTickAsync resets a timed-out LOADING server straight back to SERVER_STATUS_IDLE with 0s backoff.
        // seam: Real QueueArcadeCoordinatorService across wedged #Q1 (11485) and healthy #Q2 (11486) + SimulatedTimeProvider.
        SimulatedChatSession wedgedQ1 = RegisterGameServer(1601, "QUEUE ARCADE #Q1", port: 21485, localPort: 11485, location: "USE");
        SimulatedChatSession healthyQ2 = RegisterGameServer(1602, "QUEUE ARCADE #Q2", port: 21486, localPort: 11486, location: "USE");

        await _arcadeCoordinator.EvaluateTickAsync();
        ExtractCreateMatchPackets(wedgedQ1).Should().HaveCount(1);
        ExtractCreateMatchPackets(healthyQ2).Should().BeEmpty();
        wedgedQ1.ServerMetadata!.Status.Should().Be(ChatProtocol.ServerStatus.SERVER_STATUS_LOADING);

        // #Q1 never sends AnnounceMatch (MatchId <= 0) for 16s (> 15s LoadingTimeoutSeconds)
        _fakeTime.Advance(TimeSpan.FromSeconds(16));
        await _arcadeCoordinator.EvaluateTickAsync();

        wedgedQ1.ServerMetadata.Status.Should().Be(
            ChatProtocol.ServerStatus.SERVER_STATUS_KILLED,
            "Wedged LOADING slave must be marked KILLED rather than reset to IDLE");
        ExtractRemoteCommands(wedgedQ1).Should().Contain(
            "Quit",
            "Coordinator must send 'Quit' so COMPEL reaps and respawns a clean dedicated worker");
        ExtractCreateMatchPackets(wedgedQ1).Should().HaveCount(
            1,
            "Quarantined wedged slave must NOT immediately receive a second doomed CreateMatch");
        ExtractCreateMatchPackets(healthyQ2).Should().ContainSingle(
            "Healthy standby worker #Q2 must immediately take over when #Q1 is quarantined");

        // Even if wedgedQ1 emits a stale 0x0502 IDLE heartbeat 5s later (< 60s quarantine), it stays excluded!
        wedgedQ1.ServerMetadata.Status = ChatProtocol.ServerStatus.SERVER_STATUS_IDLE;
        _fakeTime.Advance(TimeSpan.FromSeconds(5));
        await _arcadeCoordinator.EvaluateTickAsync();
        ExtractCreateMatchPackets(wedgedQ1).Should().HaveCount(1);
    }

    [Fact]
    public void ArcadeChatCommand_DeduplicatesIdenticalCommandWithinOneSecond()
    {
        // protects: 1.0s server-side duplicate command deduplication in TryHandleClientChatCommand so rapid double-Enter
        //           or overlapping channel/IM delivery never self-cancels a bare '/arcade' toggle.
        // fails_when: Two identical '!arcade' / '/arcade' packets within <1.0s flip QueueArcadeOptIn true -> false -> true.
        // seam: Real QueueArcadeCoordinatorService + SendChannelMessage + SendInstantMessage + SimulatedTimeProvider.
        (SimulatedChatSession player, _) = EnqueueSoloPlayer(9701, "DedupHero");
        ChatChannel channel = ChatChannel.GetOrCreate(_mockChatContext.Object, player, "TERMINAL 1");
        channel.TryAddMember(player.Account!.Name, new ChatChannelMember(player, channel));

        SendChannelMessage channelProcessor = new(_floodPrevention, _mockChatContext.Object, distributedState: null, queueArcadeService: _arcadeCoordinator);
        SendInstantMessage imProcessor = new(_mockChatContext.Object, _arcadeCoordinator);

        // 1st command via channel ('!arcade' wire rewrite of '/arcade'): toggles OptIn from true -> false
        ChatBuffer firstCmd = new();
        firstCmd.WriteCommand(ChatProtocol.Command.CHAT_CMD_CHANNEL_MSG);
        firstCmd.WriteString("!arcade");
        firstCmd.WriteInt32(channel.ID);
        channelProcessor.Process(player, firstCmd);
        player.ClientMetadata!.QueueArcadeOptIn.Should().BeFalse();
        int whisperCountAfterFirst = ExtractSystemWhispers(player).Count;

        // 2nd identical command 150ms later (< 1.0s) via IM bridge ('/arcade'): deduplicated cleanly!
        _fakeTime.Advance(TimeSpan.FromMilliseconds(150));
        ChatBuffer duplicateCmd = new();
        duplicateCmd.WriteString("__kongor_arcade__");
        duplicateCmd.WriteString("/arcade");
        duplicateCmd.WriteInt8(1);
        imProcessor.Process(player, duplicateCmd);

        player.ClientMetadata.QueueArcadeOptIn.Should().BeFalse("Duplicate /arcade within 1.0s must NOT flip OptIn back to true");
        ExtractSystemWhispers(player).Count.Should().Be(whisperCountAfterFirst);

        // After 1.2s (> 1.0s dedup window), '/arcade' toggles OptIn back to true as expected!
        _fakeTime.Advance(TimeSpan.FromMilliseconds(1200));
        ChatBuffer deliberateToggleBack = new();
        deliberateToggleBack.WriteString("__kongor_arcade__");
        deliberateToggleBack.WriteString("!arcade");
        deliberateToggleBack.WriteInt8(1);
        imProcessor.Process(player, deliberateToggleBack);

        player.ClientMetadata.QueueArcadeOptIn.Should().BeTrue();
    }

    [Fact]
    public async Task SimulationWave15_SweepPendingRankedConnects_DispatchesClanTaggedAndPlainKickFallback()
    {
        // protects: Dispatching both Kick "[CLAN]PlayerName" and Kick "PlayerName" compound fallback command when a
        //           clan-tagged player remains IN_GAME on an Arcade server after a Ranked match confirms and the 2.5s fallback expires.
        // fails_when: SweepPendingRankedConnects omits the [CLAN] prefix, sends only the un-tagged name when a clan tag exists,
        //             or fails to emit the compound Kick "[CLAN]Name";Kick "Name" command string matching EvictOffendingMemberFromQueueArcade.
        // seam: Real MatchmakingService.SweepPendingRankedConnects + QueueArcadeCoordinatorService + Account.Clan metadata + SimulatedTimeProvider.
        SimulatedChatSession arcadeServer = RegisterGameServer(1701, "QUEUE ARCADE #Q1", port: 11485, localPort: 11485, location: "USE");
        SimulatedChatSession rankedServer = RegisterGameServer(1702, "KONGOR RANKED #1", port: 11235, localPort: 11235, location: "USE");
        _arcadeCoordinator.OnArcadeServerHandshake(arcadeServer);
        int arcadeMatchUpId = ExtractCreateMatchPackets(arcadeServer).Single().MatchUpId;
        SimulateServerAnnouncesMatch(arcadeServer, arcadeMatchUpId, realMatchId: 17001);

        // Enqueue solo player with an active Clan tag [ELIT]
        (SimulatedChatSession clanPlayer, _) = EnqueueSoloPlayer(9801, "TopClansman", gameType: ChatProtocol.TMMGameType.TMM_GAME_TYPE_MIDWARS);
        clanPlayer.Account.Clan = new Clan
        {
            ID = 88,
            Name = "Elite Clan",
            Tag = "ELIT"
        };

        // Advance 6s so player enters Queue Arcade #Q1 and acknowledges via JoinedMatch
        _fakeTime.Advance(TimeSpan.FromSeconds(6));
        await _arcadeCoordinator.EvaluateTickAsync();
        clanPlayer.ClientMetadata!.ActiveArcadeMatchId.Should().Be(17001);

        JoinedMatch joinedProcessor = new(new Mock<IDatabase>().Object, _mockDiscord.Object, _matchmakingService);
        ChatBuffer arcadeJoinedBuf = new();
        arcadeJoinedBuf.WriteCommand(ChatProtocol.Command.CHAT_CMD_JOINED_GAME);
        arcadeJoinedBuf.WriteString("QUEUE ARCADE [midwars] #");
        arcadeJoinedBuf.WriteInt32(17001);
        arcadeJoinedBuf.WriteInt8(0);
        await joinedProcessor.Process(clanPlayer, arcadeJoinedBuf);

        // 9 more players enqueue so a full 10-player Ranked match forms while TopClansman is IN_GAME in Arcade
        List<MatchmakingGroup> matchGroups = [_matchmakingService.GetMatchmakingGroupByMemberID(9801)!];
        for (int i = 2; i <= 10; i++)
        {
            (_, MatchmakingGroup grp) = EnqueueSoloPlayer(9800 + i, $"MwPeer_{i}", gameType: ChatProtocol.TMMGameType.TMM_GAME_TYPE_MIDWARS);
            matchGroups.Add(grp);
        }

        (await _matchmakingService.ProcessMatchForSimulationAsync(matchGroups)).Should().BeTrue();
        int rankedMatchUpId = ExtractCreateMatchPackets(rankedServer).Single().MatchUpId;
        SimulateServerAnnouncesMatch(rankedServer, rankedMatchUpId, realMatchId: 98001);

        int rankedConnectInitial = ExtractAutoConnectPackets(clanPlayer).Count(p => p.MatchId == 98001);
        rankedConnectInitial.Should().Be(1);

        // Advance 2.6s (>= 2.5s ArcadeKickFallbackSeconds) while TopClansman remains IN_GAME on Arcade
        _fakeTime.Advance(TimeSpan.FromSeconds(2.6));
        _matchmakingService.SweepPendingRankedConnects(_fakeTime.GetUtcNow());

        // Verify compound Kick "[CLAN]Name";Kick "Name" remote command was dispatched
        List<string> remoteCmds = ExtractRemoteCommands(arcadeServer);
        remoteCmds.Should().Contain(
            "Kick \"[ELIT]TopClansman\";Kick \"TopClansman\"",
            "SweepPendingRankedConnects must emit both [CLAN]Name and Name kick commands when Clan is present");

        // Verify kick command is sent at most once (idempotent guard)
        int cmdCountBefore = remoteCmds.Count;
        _fakeTime.Advance(TimeSpan.FromSeconds(1.6));
        _matchmakingService.SweepPendingRankedConnects(_fakeTime.GetUtcNow());
        ExtractRemoteCommands(arcadeServer).Count.Should().Be(cmdCountBefore, "SweepPendingRankedConnects must not resend kick once ArcadeKickSent is true");
    }
}
