// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ============================================================================
// File: MatchmakingReadyCheckTests.cs
// System: TRANSMUTANSTEIN ChatServer Tests - Full-Stack 10-Player Ready-Check State Machine
// Policy: Strict Explicit Typing (NO 'var')
//
// Value-Weighted Testing Gate:
//   protects: (1) 10-player Ready-Check prompt & progress broadcast (X/10 pips),
//             (2) Immediate server allocation only when 10/10 accept,
//             (3) Ejection of offending declining/timing-out group while preserving
//                 compliant group original QueueStartTime priority at front of queue.
//   fails_when: Server allocates before 10/10 accept, compliant players lose queue priority
//               when someone else declines, or non-responders hang the matchmaker forever.
//   seam: Real MatchmakingService + PendingReadyCheckState + binary ChatBuffer wire framing.
// ============================================================================

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using FluentAssertions;
using MERRICK.DatabaseContext.Entities.Core;
using MERRICK.DatabaseContext.Enumerations;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
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

public sealed class MatchmakingReadyCheckTests : IDisposable
{
    private sealed class SimulatedTimeProvider(DateTimeOffset initialUtc) : TimeProvider
    {
        private DateTimeOffset _utcNow = initialUtc;
        public override DateTimeOffset GetUtcNow() => _utcNow;
        public void Advance(TimeSpan delta) => _utcNow = _utcNow.Add(delta);
    }

    private sealed class SimulatedChatSession(TCPServer server, IServiceProvider serviceProvider)
        : ChatSession(server, serviceProvider)
    {
        private readonly object _packetLock = new();
        public List<byte[]> SentPackets { get; } = [];

        public override bool IsSessionConnected => true;

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

    private readonly SimulatedTimeProvider _clock = new(new DateTimeOffset(2026, 11, 10, 12, 0, 0, TimeSpan.Zero));
    private readonly Mock<IChatContext> _mockChatContext = new();
    private readonly ConcurrentDictionary<string, ChatSession> _clientSessions = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<int, ChatSession> _matchServerSessions = new();
    private readonly ConcurrentDictionary<int, ChatSession> _managerSessions = new();
    private readonly ConcurrentDictionary<string, ChatChannel> _channelsByName = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<int, ChatChannel> _channelsById = new();
    private readonly FloodPreventionService _floodPrevention = new(NullLogger<FloodPreventionService>.Instance);
    private readonly TCPServer _tcpServer;
    private readonly ServiceProvider _serviceProvider;
    private readonly IConfiguration _configuration;

    public MatchmakingReadyCheckTests()
    {
        Log.Initialise(NullLogger.Instance);

        _mockChatContext.SetupGet(c => c.ClientChatSessions).Returns(_clientSessions);
        _mockChatContext.SetupGet(c => c.MatchServerChatSessions).Returns(_matchServerSessions);
        _mockChatContext.SetupGet(c => c.MatchServerManagerChatSessions).Returns(_managerSessions);
        _mockChatContext.SetupGet(c => c.ChatChannels).Returns(_channelsByName);
        _mockChatContext.SetupGet(c => c.ChatChannelsByID).Returns(_channelsById);

        ServiceCollection services = new();
        services.AddSingleton(_mockChatContext.Object);
        services.AddSingleton(Mock.Of<IDiscordPresencePublisherService>());
        services.AddSingleton(Mock.Of<IMatchmakingService>());
        services.AddSingleton(_floodPrevention);
        services.AddSingleton<ILogger<ChatSession>>(NullLogger<ChatSession>.Instance);
        _serviceProvider = services.BuildServiceProvider();

        _tcpServer = new TCPServer(IPAddress.Loopback, 0);

        _configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Matchmaking:EnableReadyCheck"] = "true",
                ["Matchmaking:ReadyCheckTimeoutSeconds"] = "15.0",
                ["Matchmaking:PlayersPerTeam"] = "5",
                ["Matchmaking:InitialRatingSpread"] = "150",
                ["Matchmaking:MaxRatingSpread"] = "300"
            })
            .Build();
    }

    public void Dispose()
    {
        _floodPrevention.Dispose();
        _serviceProvider.Dispose();
    }

    [Fact]
    public async Task ReadyCheck_AllTenPlayersAccept_BroadcastsProgressAndAllocatesMatchServer()
    {
        // protects: (1) 10-player Ready-Check prompt sent to all clients,
        //           (2) Progress updates (1..10) broadcast on every accept,
        //           (3) Server allocation triggered only when 10/10 accept.
        // fails_when: Server allocates prematurely before all players accept or status is not broadcast.
        // seam: MatchmakingService.ProcessMatchForSimulationAsync + SubmitReadyCheckResponseAsync + NET_CHAT_GS_CREATE_MATCH.
        using MatchmakingService service = new(_configuration, _mockChatContext.Object, timeProvider: _clock);
        SimulatedChatSession matchServer = RegisterIdleGameServer(serverId: 701, location: "USE");

        DateTimeOffset queueTime1 = _clock.GetUtcNow().AddSeconds(-45);
        DateTimeOffset queueTime2 = _clock.GetUtcNow().AddSeconds(-30);

        (List<SimulatedChatSession> team1Clients, MatchmakingGroup group1) = CreateGroup(service, startAccountId: 101, count: 5, queueTime1);
        (List<SimulatedChatSession> team2Clients, MatchmakingGroup group2) = CreateGroup(service, startAccountId: 106, count: 5, queueTime2);

        // 1. Trigger Ready-Check through matchmaking pipeline
        bool matchFormed = await service.ProcessMatchForSimulationAsync([group1, group2]);
        matchFormed.Should().BeTrue("Match formed with 10 players should enter Ready-Check stage");

        // Verify match server is NOT yet allocated
        matchServer.SnapshotPackets().Should().BeEmpty("Match server must NOT be allocated before players accept ready-check");
        matchServer.ServerMetadata!.Status.Should().Be(ChatProtocol.ServerStatus.SERVER_STATUS_IDLE);

        // Verify all 10 players received the Ready-Check Prompt (0x0F0A)
        List<SimulatedChatSession> allClients = [.. team1Clients, .. team2Clients];
        foreach (SimulatedChatSession client in allClients)
        {
            HasCommand(client, ChatProtocol.Matchmaking.NET_CHAT_CL_TMM_READY_CHECK_PROMPT).Should().BeTrue(
                $"Player {client.Account.ID} must receive Ready-Check prompt");
        }

        // Retrieve active ready-check
        PendingReadyCheckState state = service.PendingReadyChecks.Values.Single();
        int readyCheckId = state.ReadyCheckId;
        state.TotalPlayers.Should().Be(10);
        state.AcceptedCount.Should().Be(0);

        // 2. Players 101..109 accept one by one
        for (int i = 0; i < 9; i++)
        {
            int accountId = allClients[i].Account.ID;
            bool accepted = await service.SubmitReadyCheckResponseAsync(readyCheckId, accountId, accepted: true);
            accepted.Should().BeTrue();
            state.AcceptedCount.Should().Be(i + 1);

            // Server must still NOT be allocated while accepted < 10
            matchServer.SnapshotPackets().Should().BeEmpty();
        }

        // 3. Final player (110) accepts
        bool finalAccept = await service.SubmitReadyCheckResponseAsync(readyCheckId, allClients[9].Account.ID, accepted: true);
        finalAccept.Should().BeTrue();

        // 4. Verification: Server is now allocated!
        HasCommand(matchServer, ChatProtocol.ChatServerToGameServer.NET_CHAT_GS_CREATE_MATCH).Should().BeTrue(
            "10/10 accepts must immediately transition into match server allocation via NET_CHAT_GS_CREATE_MATCH");
        matchServer.ServerMetadata.Status.Should().Be(ChatProtocol.ServerStatus.SERVER_STATUS_LOADING);

        // Queues cleared upon match allocation
        group1.QueueStartTime.Should().BeNull();
        group2.QueueStartTime.Should().BeNull();
    }

    [Fact]
    public async Task ReadyCheck_OnePlayerDeclines_EjectsOffendingGroup_AndKeepsCompliantGroupAtFrontOfQueue()
    {
        // protects: Offending group ejection on decline while preserving compliant group's original QueueStartTime
        // fails_when: Compliant players are dequeued or lose their queue priority when a random opponent declines
        // seam: MatchmakingService.SubmitReadyCheckResponseAsync(accepted: false) + QueueStartTime preservation.
        using MatchmakingService service = new(_configuration, _mockChatContext.Object, timeProvider: _clock);
        SimulatedChatSession matchServer = RegisterIdleGameServer(serverId: 702, location: "USE");

        DateTimeOffset originalQueueTimeGroup1 = _clock.GetUtcNow().AddSeconds(-60);
        DateTimeOffset originalQueueTimeGroup2 = _clock.GetUtcNow().AddSeconds(-25);

        (List<SimulatedChatSession> team1Clients, MatchmakingGroup group1) = CreateGroup(service, startAccountId: 201, count: 5, originalQueueTimeGroup1);
        (List<SimulatedChatSession> team2Clients, MatchmakingGroup group2) = CreateGroup(service, startAccountId: 206, count: 5, originalQueueTimeGroup2);

        bool matchFormed = await service.ProcessMatchForSimulationAsync([group1, group2]);
        matchFormed.Should().BeTrue();

        PendingReadyCheckState state = service.PendingReadyChecks.Values.Single();
        int readyCheckId = state.ReadyCheckId;

        // All 5 members of Group 1 accept
        foreach (SimulatedChatSession client in team1Clients)
        {
            await service.SubmitReadyCheckResponseAsync(readyCheckId, client.Account.ID, accepted: true);
        }
        state!.AcceptedCount.Should().Be(5);

        // Member 206 (in Group 2) declines!
        bool declineResult = await service.SubmitReadyCheckResponseAsync(readyCheckId, 206, accepted: false);
        declineResult.Should().BeTrue();

        // 1. Offending Group 2 MUST be ejected from queue
        group2.QueueStartTime.Should().BeNull("Offending group must have its queue status revoked");
        foreach (SimulatedChatSession g2Client in team2Clients)
        {
            HasCommand(g2Client, ChatProtocol.Matchmaking.NET_CHAT_CL_TMM_FAILED_TO_ACCEPT_PENDING_MATCH)
                .Should().BeTrue("Offending group members must receive decline notice");
        }

        // 2. Compliant Group 1 MUST retain original QueueStartTime priority!
        group1.QueueStartTime.Should().Be(originalQueueTimeGroup1,
            "Compliant group MUST preserve its original queue start time to remain at the front of the queue");
        foreach (SimulatedChatSession g1Client in team1Clients)
        {
            HasCommand(g1Client, ChatProtocol.Matchmaking.NET_CHAT_CL_TMM_GROUP_REJOIN_QUEUE)
                .Should().BeTrue("Compliant group members must receive rejoin queue confirmation");
        }

        // 3. Match server was never allocated
        matchServer.SnapshotPackets().Should().BeEmpty();
        matchServer.ServerMetadata!.Status.Should().Be(ChatProtocol.ServerStatus.SERVER_STATUS_IDLE);

        // Ready check is cleaned up
        service.GetPendingReadyCheck(readyCheckId).Should().BeNull();
    }

    [Fact]
    public async Task ReadyCheck_TimeoutExpires_EjectsNonRespondingGroup_AndKeepsCompliantGroupAtFrontOfQueue()
    {
        // protects: Timeout sweep ejecting non-responding groups and preserving compliant groups at front of queue
        // fails_when: Ready-Check hangs indefinitely or compliant groups are penalized on timeout
        // seam: MatchmakingService.EvaluateExpiredReadyChecks + NET_CHAT_CL_TMM_GROUP_REJOIN_QUEUE.
        using MatchmakingService service = new(_configuration, _mockChatContext.Object, timeProvider: _clock);
        SimulatedChatSession matchServer = RegisterIdleGameServer(serverId: 703, location: "USE");

        DateTimeOffset originalQueueTimeGroup1 = _clock.GetUtcNow().AddSeconds(-75);
        DateTimeOffset originalQueueTimeGroup2 = _clock.GetUtcNow().AddSeconds(-35);

        (List<SimulatedChatSession> team1Clients, MatchmakingGroup group1) = CreateGroup(service, startAccountId: 301, count: 5, originalQueueTimeGroup1);
        (List<SimulatedChatSession> team2Clients, MatchmakingGroup group2) = CreateGroup(service, startAccountId: 306, count: 5, originalQueueTimeGroup2);

        bool matchFormed = await service.ProcessMatchForSimulationAsync([group1, group2]);
        matchFormed.Should().BeTrue();

        PendingReadyCheckState state = service.PendingReadyChecks.Values.Single();
        int readyCheckId = state.ReadyCheckId;

        // Group 1 accepts promptly
        foreach (SimulatedChatSession client in team1Clients)
        {
            await service.SubmitReadyCheckResponseAsync(readyCheckId, client.Account.ID, accepted: true);
        }

        // Group 2 completely ghosts the ready check. Time advances 16 seconds (> 15.0s timeout)
        _clock.Advance(TimeSpan.FromSeconds(16));

        // Broker periodic evaluation sweep
        IReadOnlyList<PendingReadyCheckState> expired = service.EvaluateExpiredReadyChecks(_clock.GetUtcNow());
        expired.Should().ContainSingle(s => s.ReadyCheckId == readyCheckId);

        // 1. Offending Group 2 is ejected
        group2.QueueStartTime.Should().BeNull("Non-responding group must be ejected from matchmaking queue");
        foreach (SimulatedChatSession g2Client in team2Clients)
        {
            HasCommand(g2Client, ChatProtocol.Matchmaking.NET_CHAT_CL_TMM_FAILED_TO_ACCEPT_PENDING_MATCH)
                .Should().BeTrue();
        }

        // 2. Compliant Group 1 is kept at front of queue with original timestamp
        group1.QueueStartTime.Should().Be(originalQueueTimeGroup1,
            "Compliant group must keep its original queue timestamp across timeout of other groups");
        foreach (SimulatedChatSession g1Client in team1Clients)
        {
            HasCommand(g1Client, ChatProtocol.Matchmaking.NET_CHAT_CL_TMM_GROUP_REJOIN_QUEUE)
                .Should().BeTrue();
        }

        // 3. Server remains idle
        matchServer.SnapshotPackets().Should().BeEmpty();
        matchServer.ServerMetadata!.Status.Should().Be(ChatProtocol.ServerStatus.SERVER_STATUS_IDLE);
    }

    private (List<SimulatedChatSession> Clients, MatchmakingGroup Group) CreateGroup(
        MatchmakingService service,
        int startAccountId,
        int count,
        DateTimeOffset queueStartTime)
    {
        List<SimulatedChatSession> clients = new(count);
        List<MatchmakingGroupMember> members = new(count);

        for (int i = 0; i < count; i++)
        {
            int accountId = startAccountId + i;
            SimulatedChatSession session = new(_tcpServer, _serviceProvider)
            {
                Account = new Account
                {
                    ID = accountId,
                    Name = $"Player_{accountId}",
                    User = new User
                    {
                        ID = accountId,
                        EmailAddress = $"player_{accountId}@kongor.test",
                        Role = new DbRole { ID = 1, Name = "User" },
                        SRPPasswordSalt = "salt",
                        SRPPasswordHash = "hash",
                        PBKDF2PasswordHash = "hash"
                    }
                },
                ClientMetadata = new ClientChatSessionMetadata
                {
                    RemoteIP = "127.0.0.1",
                    SessionCookie = $"cookie_{accountId}",
                    SessionAuthenticationHash = $"auth_hash_{accountId}",
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
                    ClientRegion = "USE",
                    ClientLanguage = "en"
                }
            };
            session.MarkAuthenticated(ChatPeerRole.Client);

            _clientSessions[$"Player_{accountId}"] = session;
            clients.Add(session);

            MatchmakingGroupMember member = new(session)
            {
                Slot = (byte)i,
                IsLeader = i == 0,
                IsReady = true,
                IsInGame = false,
                IsEligibleForMatchmaking = true,
                LoadingPercent = 100,
                Rating = 1500.0
            };
            members.Add(member);
        }

        MatchmakingGroup group = new()
        {
            Members = members,
            Information = new MatchmakingGroupInformation
            {
                ClientVersion = "4.10.1.0",
                GroupType = ChatProtocol.TMMType.TMM_TYPE_PVP,
                GameType = ChatProtocol.TMMGameType.TMM_GAME_TYPE_NORMAL,
                MapName = "caldavar",
                GameModes = ["All Pick"],
                GameRegions = ["USE"],
                Ranked = true,
                MatchFidelity = 0,
                BotDifficulty = 0,
                RandomizeBots = false
            },
            QueueStartTime = queueStartTime
        };

        service.Groups[members[0].Account.ID] = group;
        return (clients, group);
    }

    private SimulatedChatSession RegisterIdleGameServer(int serverId, string location)
    {
        SimulatedChatSession session = new(_tcpServer, _serviceProvider)
        {
            Account = new Account
            {
                ID = 9000 + serverId,
                Name = $"HOST_{serverId}",
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
                Address = "127.0.0.1",
                Port = 11235,
                LocalAddress = "127.0.0.1",
                LocalPort = 11235,
                Location = location,
                MapName = "caldavar",
                GameName = $"GameServer_{serverId}",
                IsQueueArcade = false
            }
        };
        session.MarkAuthenticated(ChatPeerRole.MatchServer);

        _matchServerSessions[serverId] = session;
        return session;
    }

    private static bool HasCommand(SimulatedChatSession session, ushort commandId)
    {
        List<byte[]> packets = session.SnapshotPackets();
        foreach (byte[] p in packets)
        {
            if (p.Length >= 4 && BitConverter.ToUInt16(p, 2) == commandId)
            {
                return true;
            }
        }
        return false;
    }
}
