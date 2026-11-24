// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ============================================================================
// File: MatchmakingReadyCheckDualBridgeTests.cs
// System: TRANSMUTANSTEIN ChatServer Tests - Dual-Protocol Ready-Check Bridge & Default Activation
// Policy: Strict Explicit Typing (NO 'var')
//
// Value-Weighted Testing Gate:
//   protects: (1) Default appsettings.json activation of ReadyCheck (EnableReadyCheck=true),
//             (2) Dual-protocol whisper prompt & status broadcasts from "__kongor_ready_check__",
//             (3) 10-slot accept/decline/timeout responses sent via CHAT_CMD_IM / CHAT_CMD_WHISPER,
//             (4) Kill-switch rollback bypassing ReadyCheck when EnableReadyCheck=false.
//   fails_when: Whispers are not mirrored, IM responses to "__kongor_ready_check__" are ignored,
//               or kill-switch does not bypass ReadyCheck.
//   seam: Real MatchmakingService + SendInstantMessage + SendWhisper + ChatBuffer wire framing.
// ============================================================================

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
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
using TRANSMUTANSTEIN.ChatServer.CommandProcessors.Communication;
using TRANSMUTANSTEIN.ChatServer.CommandProcessors.Matchmaking;
using TRANSMUTANSTEIN.ChatServer.Domain.Communication;
using TRANSMUTANSTEIN.ChatServer.Domain.Core;
using TRANSMUTANSTEIN.ChatServer.Domain.Matchmaking;
using TRANSMUTANSTEIN.ChatServer.Internals;
using TRANSMUTANSTEIN.ChatServer.Services;
using TRANSMUTANSTEIN.ChatServer.Utilities;
using Xunit;
using DbRole = MERRICK.DatabaseContext.Entities.Utility.Role;

namespace TRANSMUTANSTEIN.ChatServer.Tests.Services;

public sealed class MatchmakingReadyCheckDualBridgeTests : IDisposable
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
    private readonly IConfiguration _defaultConfiguration;

    public MatchmakingReadyCheckDualBridgeTests()
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

        _defaultConfiguration = new ConfigurationBuilder()
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
    public async Task DefaultAppSettings_EnablesReadyCheckByDefault_AndGatesQueueArcadePullUntilAllPlayersAccept()
    {
        // protects: (1) Default configuration in src/services/chat-server/appsettings.json sets EnableReadyCheck=true,
        //           (2) Players actively inside a Queue Arcade game receive mid-game 0x0F04 + System whisper prompt,
        //           (3) Players are NOT pulled out of Queue Arcade (NotifyRankedMatchAllocating / OnPlayersPulledToRankedMatch / 0x0062)
        //               until ALL players click Accept Match (0x0F05) and game server confirms match.
        // fails_when: appsettings.json disables ReadyCheck, or Queue Arcade players are pulled out before all players accept.
        // seam: appsettings.json -> MatchmakingService -> IQueueArcadeService -> ConfirmMatch -> 0x0062 AUTO_MATCH_CONNECT.
        string appSettingsPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../src/services/chat-server/appsettings.json"));
        if (!File.Exists(appSettingsPath))
        {
            string repoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../../../"));
            string candidate = Path.Combine(repoRoot, "src/services/chat-server/appsettings.json");
            if (File.Exists(candidate))
            {
                appSettingsPath = candidate;
            }
        }

        File.Exists(appSettingsPath).Should().BeTrue($"appsettings.json must exist at {appSettingsPath}");

        IConfigurationRoot appConfig = new ConfigurationBuilder()
            .AddJsonFile(appSettingsPath, optional: false)
            .Build();

        bool enabledByDefault = appConfig.GetValue("Matchmaking:EnableReadyCheck", false);
        enabledByDefault.Should().BeTrue("Matchmaking:EnableReadyCheck must be true by default in appsettings.json");

        RecordingQueueArcadeService recordingArcade = new();
        using MatchmakingService service = new(appConfig, _mockChatContext.Object, timeProvider: _clock);
        service.AttachQueueArcadeService(recordingArcade);

        SimulatedChatSession matchServer = RegisterIdleGameServer(serverId: 800, location: "USE");
        (List<SimulatedChatSession> team1, MatchmakingGroup group1) = CreateGroup(service, startAccountId: 501, count: 1, _clock.GetUtcNow().AddSeconds(-30));
        (List<SimulatedChatSession> team2, MatchmakingGroup group2) = CreateGroup(service, startAccountId: 506, count: 1, _clock.GetUtcNow().AddSeconds(-20));

        // Put team1[0] inside an active Queue Arcade game (mid-game)
        team1[0].ClientMetadata.ActiveArcadeMatchId = 5001;
        team1[0].ClientMetadata.ActiveArcadeServerId = 77;
        team1[0].ClientMetadata.LastKnownClientState = ChatProtocol.ChatClientStatus.CHAT_CLIENT_STATUS_IN_GAME;
        group1.Members[0].IsInGame = true;

        bool matchInitiated = await service.ProcessMatchForSimulationAsync([group1, group2]);
        matchInitiated.Should().BeTrue();
        service.PendingReadyChecks.Should().ContainSingle("Default configuration must initiate ReadyCheck before allocating Ranked server");
        matchServer.SnapshotPackets().Should().BeEmpty("Ranked server must NOT be allocated before all players accept");
        recordingArcade.NotifyRankedMatchAllocatingCalls.Should().Be(0);
        recordingArcade.PulledRankedMatchIds.Should().BeEmpty();

        // Verify mid-game Queue Arcade player got 0x0F04 + mid-game System whisper (using READY CHECK prefix so tactical_hud.lua does NOT prematurely disconnect), while non-Arcade player only got 0x0F04
        GetNativePendingMatchPackets(team1[0]).Should().ContainSingle();
        GetWhispersFrom(team1[0], "System").Should().ContainSingle(w => w.Contains("[QUEUE ARCADE] MATCHMAKING READY CHECK"));
        GetWhispersFrom(team1[0], "System").Should().NotContain(w => w.Contains("[QUEUE ARCADE] RANKED MATCH FOUND"));
        GetWhispersFrom(team2[0], "System").Should().BeEmpty();

        // Player 1 (in Queue Arcade) clicks Accept Match (0x0F05) -> STILL stays in Queue Arcade while waiting for Player 2!
        AcceptPendingMatch acceptProcessor = new(service);
        ChatBuffer acceptBuf1 = new();
        acceptBuf1.WriteCommand(ChatProtocol.Matchmaking.NET_CHAT_CL_TMM_ACCEPT_PENDING_MATCH);
        await acceptProcessor.Process(team1[0], new ChatBuffer(acceptBuf1.AsSpan()));

        matchServer.SnapshotPackets().Should().BeEmpty("Player in Queue Arcade must NOT be pulled out when only 1/2 accepted");
        recordingArcade.PulledRankedMatchIds.Should().BeEmpty();
        HasCommand(team1[0], 0x0062).Should().BeFalse("No AUTO_MATCH_CONNECT may fire before all players accept");

        // Player 2 clicks Accept Match (0x0F05) -> Quorum reached -> Ranked server allocated -> ConfirmMatch pulls player from Queue Arcade!
        ChatBuffer acceptBuf2 = new();
        acceptBuf2.WriteCommand(ChatProtocol.Matchmaking.NET_CHAT_CL_TMM_ACCEPT_PENDING_MATCH);
        await acceptProcessor.Process(team2[0], new ChatBuffer(acceptBuf2.AsSpan()));

        HasCommand(matchServer, ChatProtocol.ChatServerToGameServer.NET_CHAT_GS_CREATE_MATCH).Should().BeTrue();
        recordingArcade.NotifyRankedMatchAllocatingCalls.Should().Be(1);
        recordingArcade.PulledRankedMatchIds.Should().BeEmpty("Actual pull occurs on ConfirmMatch once Ranked server finishes loading map");

        int matchUpId = service.PendingMatches.Keys.Single();
        await service.ConfirmMatch(matchUpId, matchId: 99001, serverAddress: "127.0.0.1", serverPort: 11235);

        recordingArcade.PulledRankedMatchIds.Should().ContainSingle().Which.Should().Be(99001);
        HasCommand(team1[0], 0x0062).Should().BeTrue("Queue Arcade player receives 0x0062 AUTO_MATCH_CONNECT into Ranked match after quorum + ConfirmMatch");
        team1[0].ClientMetadata.AwaitingArcadeDisconnectForRankedPull.Should().BeTrue();
    }

    [Fact]
    public async Task QueueArcadeMidGameReadyCheck_WhenOtherPlayerTimesOut_KeepsArcadePlayerInMiniGameUninterrupted()
    {
        // protects: If a player in Queue Arcade accepts ReadyCheck (0x0F05) and another player AFK-times-out,
        //           the Queue Arcade player stays inside their Arcade match without being pulled out,
        //           has their prior ArcadePushCooldownUntilUtc restored, and keeps front-of-queue priority.
        // fails_when: Timeout pulls compliant Queue Arcade players out of their mini-game or leaves stale push cooldowns.
        // seam: MatchmakingService.ProcessMatchForSimulationAsync -> AcceptPendingMatch -> EvaluateExpiredReadyChecks.
        RecordingQueueArcadeService recordingArcade = new();
        using MatchmakingService service = new(_defaultConfiguration, _mockChatContext.Object, timeProvider: _clock);
        service.AttachQueueArcadeService(recordingArcade);

        SimulatedChatSession matchServer = RegisterIdleGameServer(serverId: 821, location: "USE");
        SimulatedChatSession arcadeServer = RegisterIdleGameServer(serverId: 88, location: "USE");
        arcadeServer.ServerMetadata!.IsQueueArcade = true;
        arcadeServer.ServerMetadata.Status = ChatProtocol.ServerStatus.SERVER_STATUS_ACTIVE;

        DateTimeOffset origQueueStart = _clock.GetUtcNow().AddSeconds(-60);
        (List<SimulatedChatSession> team1, MatchmakingGroup group1) = CreateGroup(service, startAccountId: 601, count: 5, origQueueStart);
        (List<SimulatedChatSession> team2, MatchmakingGroup group2) = CreateGroup(service, startAccountId: 606, count: 5, _clock.GetUtcNow().AddSeconds(-40));

        // Team 1 AND AFK player team2[4] (Player_610) are actively playing a Queue Arcade mini-game; team2[0..3] are on Main Menu
        foreach (SimulatedChatSession c in team1.Append(team2[4]))
        {
            c.ClientMetadata.ActiveArcadeMatchId = 7777;
            c.ClientMetadata.ActiveArcadeServerId = 88;
            c.ClientMetadata.LastKnownClientState = ChatProtocol.ChatClientStatus.CHAT_CLIENT_STATUS_IN_GAME;
        }

        (await service.ProcessMatchForSimulationAsync([group1, group2])).Should().BeTrue();

        // Verify ReadyCheck temporarily guards ArcadePushCooldownUntilUtc so Main Menu players aren't pushed into Arcade mid-prompt
        team2[0].ClientMetadata.ArcadePushCooldownUntilUtc.Should().NotBeNull();

        // All 5 Team 1 Queue Arcade players + 4 of 5 Team 2 players accept; team2[4] (Player_610, inside Queue Arcade) AFKs!
        AcceptPendingMatch acceptProcessor = new(service);
        foreach (SimulatedChatSession c in team1.Concat(team2.Take(4)))
        {
            ChatBuffer buf = new();
            buf.WriteCommand(ChatProtocol.Matchmaking.NET_CHAT_CL_TMM_ACCEPT_PENDING_MATCH);
            await acceptProcessor.Process(c, new ChatBuffer(buf.AsSpan()));
        }

        _clock.Advance(TimeSpan.FromSeconds(16));
        service.EvaluateExpiredReadyChecks(_clock.GetUtcNow()).Should().ContainSingle();

        // 1. Compliant Queue Arcade players (team1[0..4]) were NEVER pulled or released, stay in mini-game, keep queue priority, and get reassurance whisper
        recordingArcade.PulledRankedMatchIds.Should().BeEmpty();
        recordingArcade.ReleasedAccounts.Select(r => r.AccountId).Should().NotContain([601, 602, 603, 604, 605]);
        matchServer.SnapshotPackets().Should().BeEmpty();
        group1.QueueStartTime.Should().Be(origQueueStart);
        team1[0].ClientMetadata.ArcadePushCooldownUntilUtc.Should().BeNull("Prior null cooldown restored cleanly");
        GetWhispersFrom(team1[0], "System").Should().Contain(w => w.Contains("keeping you in your mini-game at the front of the Ranked queue"));
        HasCommand(team1[0], ChatProtocol.Matchmaking.NET_CHAT_CL_TMM_GROUP_REJOIN_QUEUE).Should().BeTrue();

        // 2. Non-accepting AFK Queue Arcade player (team2[4] / Account 610) IS removed from BOTH matchmaking queue AND Queue Arcade (slot released + server Kick + 0x0F06)!
        group2.QueueStartTime.Should().BeNull();
        team2[0].ClientMetadata.ArcadePushCooldownUntilUtc.Should().BeNull("Prior null cooldown restored cleanly");
        recordingArcade.ReleasedAccounts.Should().Contain((610, "ready_check_failed_to_accept"));
        HasCommand(arcadeServer, ChatProtocol.ChatServerToGameServer.NET_CHAT_GS_REMOTE_COMMAND).Should().BeTrue("AFK Queue Arcade player must be kicked off the Queue Arcade server");
        ReadFailedToAcceptAccountIdsWithSentinel(team2[4]).Should().Equal(610, -1);
        GetWhispersFrom(team2[4], "System").Should().Contain(w => w.Contains("removed from both the matchmaking queue and Queue Arcade"));
    }

    [Fact]
    public async Task ReadyCheck_NativeS2PendingMatchProtocol_0x0F04_0x0F05_0x0F06_DrivesClassicDialogAndSentinelTimeout()
    {
        // protects: (1) Native k2_x64.dll 0x0F04 (NET_CHAT_CL_TMM_PENDING_MATCH) wire framing (FUN_180695ed0),
        //           (2) Native 0x0F05 (AcceptPendingMatch) zero-payload command processor lookup by Account.ID,
        //           (3) Personalized bAccepted flag + remainingMs=0 clear packet on quorum & timeout,
        //           (4) -1 sentinel termination on 0x0F06 (NET_CHAT_CL_TMM_FAILED_TO_ACCEPT_PENDING_MATCH) for FUN_180695cc0.
        // fails_when: Classic HoN matchmaker_accept_game_dialog fails to open, ignores Accept Match clicks, lingers after 10/10,
        //             or drops TMMMatchFailedToAccept due to missing -1 Int32 terminator.
        // seam: AcceptPendingMatch (0x0F05) -> MatchmakingService -> binary 0x0F04 / 0x0F06 ChatBuffer packets.
        using MatchmakingService service = new(_defaultConfiguration, _mockChatContext.Object, timeProvider: _clock);
        SimulatedChatSession matchServer = RegisterIdleGameServer(serverId: 809, location: "USE");

        (List<SimulatedChatSession> team1, MatchmakingGroup group1) = CreateGroup(service, startAccountId: 951, count: 5, _clock.GetUtcNow().AddSeconds(-50));
        (List<SimulatedChatSession> team2, MatchmakingGroup group2) = CreateGroup(service, startAccountId: 956, count: 5, _clock.GetUtcNow().AddSeconds(-35));
        List<SimulatedChatSession> allClients = [.. team1, .. team2];

        bool matchFormed = await service.ProcessMatchForSimulationAsync([group1, group2]);
        matchFormed.Should().BeTrue();

        // 1. Verify initial 0x0F04 packet layout (15000ms, 15000ms, 10 players, 0 accepted, bAccepted=0)
        foreach (SimulatedChatSession client in allClients)
        {
            List<(int RemainingMs, int TotalMs, int TotalPlayers, int AcceptedPlayers, byte AcceptedFlag)> initialPending =
                GetNativePendingMatchPackets(client);
            initialPending.Should().ContainSingle();
            initialPending[0].Should().Be((15000, 15000, 10, 0, (byte)0));
        }

        AcceptPendingMatch acceptProcessor = new(service);

        // 2. First 9 players click classic HoN 'Accept Match' button (sends 2-byte 0x0F05 with no payload)
        for (int i = 0; i < 9; i++)
        {
            ChatBuffer acceptPacket = new();
            acceptPacket.WriteCommand(ChatProtocol.Matchmaking.NET_CHAT_CL_TMM_ACCEPT_PENDING_MATCH);
            ChatBuffer readAccept = new(acceptPacket.AsSpan());
            await acceptProcessor.Process(allClients[i], readAccept);
        }

        // Accepted player 0 sees bAccepted=1 & accepted=9; unaccepted player 9 sees bAccepted=0 & accepted=9
        (int RemainingMs, int TotalMs, int TotalPlayers, int AcceptedPlayers, byte AcceptedFlag) p0Latest =
            GetNativePendingMatchPackets(allClients[0]).Last();
        p0Latest.TotalPlayers.Should().Be(10);
        p0Latest.AcceptedPlayers.Should().Be(9);
        p0Latest.AcceptedFlag.Should().Be(1);
        p0Latest.RemainingMs.Should().BeGreaterThan(0);

        (int RemainingMs, int TotalMs, int TotalPlayers, int AcceptedPlayers, byte AcceptedFlag) p9Latest =
            GetNativePendingMatchPackets(allClients[9]).Last();
        p9Latest.AcceptedPlayers.Should().Be(9);
        p9Latest.AcceptedFlag.Should().Be(0);

        // 3. Player 10 clicks Accept Match -> quorum reached -> remainingMs=0 clear packet sent + server allocated
        ChatBuffer finalAccept = new();
        finalAccept.WriteCommand(ChatProtocol.Matchmaking.NET_CHAT_CL_TMM_ACCEPT_PENDING_MATCH);
        await acceptProcessor.Process(allClients[9], new ChatBuffer(finalAccept.AsSpan()));

        foreach (SimulatedChatSession client in allClients)
        {
            (int RemainingMs, int TotalMs, int TotalPlayers, int AcceptedPlayers, byte AcceptedFlag) cleared =
                GetNativePendingMatchPackets(client).Last();
            cleared.RemainingMs.Should().Be(0, "Quorum 0x0F04 update must send remainingTimeMs=0 to dismiss matchmaker_accept_game_dialog");
            cleared.AcceptedPlayers.Should().Be(10);
            cleared.AcceptedFlag.Should().Be(1);
        }

        HasCommand(matchServer, ChatProtocol.ChatServerToGameServer.NET_CHAT_GS_CREATE_MATCH).Should().BeTrue();

        // 4. Verify -1 sentinel terminator on 0x0F06 timeout notice (k2_x64.dll FUN_180695cc0)
        (List<SimulatedChatSession> t3, MatchmakingGroup g3) = CreateGroup(service, startAccountId: 971, count: 5, _clock.GetUtcNow().AddSeconds(-60));
        (List<SimulatedChatSession> t4, MatchmakingGroup g4) = CreateGroup(service, startAccountId: 976, count: 5, _clock.GetUtcNow().AddSeconds(-40));
        bool secondFormed = await service.ProcessMatchForSimulationAsync([g3, g4]);
        secondFormed.Should().BeTrue();

        foreach (SimulatedChatSession c in t3)
        {
            ChatBuffer acc = new();
            acc.WriteCommand(ChatProtocol.Matchmaking.NET_CHAT_CL_TMM_ACCEPT_PENDING_MATCH);
            await acceptProcessor.Process(c, new ChatBuffer(acc.AsSpan()));
        }

        _clock.Advance(TimeSpan.FromSeconds(16));
        service.EvaluateExpiredReadyChecks(_clock.GetUtcNow());

        List<int> failIds = ReadFailedToAcceptAccountIdsWithSentinel(t4[0]);
        failIds.Should().NotBeEmpty();
        failIds.Last().Should().Be(-1, "k2_x64.dll FUN_180695cc0 requires trailing -1 Int32 sentinel to fire TMMMatchFailedToAccept");
        failIds.Take(failIds.Count - 1).Should().BeEquivalentTo([976, 977, 978, 979, 980]);

        // Compliant group t3 also received 0x0F04 remainingMs=0 clear packet so dialog closes cleanly
        GetNativePendingMatchPackets(t3[0]).Last().RemainingMs.Should().Be(0);
    }

    [Fact]
    public async Task KillSwitchRollback_EnableReadyCheckFalse_AllocatesMatchServerDirectly()
    {
        // protects: Kill-switch rollback capability via configuration (Matchmaking:EnableReadyCheck = false)
        // fails_when: ReadyCheck still intercepts match allocation when disabled via configuration
        // seam: MatchmakingService.ProcessMatchUnsafe with EnableReadyCheck = false
        IConfiguration disabledConfig = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Matchmaking:EnableReadyCheck"] = "false",
                ["Matchmaking:PlayersPerTeam"] = "5"
            })
            .Build();

        using MatchmakingService service = new(disabledConfig, _mockChatContext.Object, timeProvider: _clock);
        SimulatedChatSession matchServer = RegisterIdleGameServer(serverId: 801, location: "USE");

        (List<SimulatedChatSession> team1, MatchmakingGroup group1) = CreateGroup(service, startAccountId: 601, count: 5, _clock.GetUtcNow().AddSeconds(-40));
        (List<SimulatedChatSession> team2, MatchmakingGroup group2) = CreateGroup(service, startAccountId: 606, count: 5, _clock.GetUtcNow().AddSeconds(-25));

        bool matchFormed = await service.ProcessMatchForSimulationAsync([group1, group2]);
        matchFormed.Should().BeTrue();

        service.PendingReadyChecks.Should().BeEmpty("ReadyCheck must NOT be initiated when kill switch EnableReadyCheck=false");
        HasCommand(matchServer, ChatProtocol.ChatServerToGameServer.NET_CHAT_GS_CREATE_MATCH).Should().BeTrue(
            "Game server must be directly allocated when ReadyCheck is disabled");
    }

    [Fact]
    public async Task ReadyCheck_DualProtocol_10SlotAccept_OverKongorReadyCheck_AllocatesServer()
    {
        // protects: (1) Prompt and status mirrored via CHAT_CMD_WHISPER from "__kongor_ready_check__",
        //           (2) Client responses accepted via CHAT_CMD_IM and CHAT_CMD_WHISPER ("ACCEPT|<id>" and "1|<id>"),
        //           (3) Quorum allocation only when all 10 players accept.
        // fails_when: Whispers are not delivered or IM responses to "__kongor_ready_check__" fail to advance quorum.
        // seam: SendInstantMessage + SendWhisper -> MatchmakingService.SubmitReadyCheckResponse -> GS_CREATE_MATCH.
        using MatchmakingService service = new(_defaultConfiguration, _mockChatContext.Object, timeProvider: _clock);
        SimulatedChatSession matchServer = RegisterIdleGameServer(serverId: 802, location: "USE");

        (List<SimulatedChatSession> team1, MatchmakingGroup group1) = CreateGroup(service, startAccountId: 701, count: 5, _clock.GetUtcNow().AddSeconds(-45));
        (List<SimulatedChatSession> team2, MatchmakingGroup group2) = CreateGroup(service, startAccountId: 706, count: 5, _clock.GetUtcNow().AddSeconds(-30));
        List<SimulatedChatSession> allClients = [.. team1, .. team2];

        bool matchFormed = await service.ProcessMatchForSimulationAsync([group1, group2]);
        matchFormed.Should().BeTrue();

        PendingReadyCheckState state = service.PendingReadyChecks.Values.Single();
        int readyCheckId = state.ReadyCheckId;

        // 1. Verify every player received binary prompt & status AND mirrored whisper prompt & status
        foreach (SimulatedChatSession client in allClients)
        {
            HasCommand(client, ChatProtocol.Matchmaking.NET_CHAT_CL_TMM_READY_CHECK_PROMPT).Should().BeTrue();
            HasCommand(client, ChatProtocol.Matchmaking.NET_CHAT_CL_TMM_READY_CHECK_STATUS).Should().BeTrue();

            List<string> whispers = GetWhispersFrom(client, MatchmakingService.ReadyCheckSenderName);
            whispers.Should().Contain(w => w.StartsWith($"PROMPT|{readyCheckId}|10|", StringComparison.Ordinal),
                $"Player {client.Account.ID} must receive PROMPT whisper from __kongor_ready_check__");
            whispers.Should().Contain($"STATUS|{readyCheckId}|0|10|0",
                $"Player {client.Account.ID} must receive initial STATUS whisper from __kongor_ready_check__");
        }

        SendInstantMessage imHandler = new(_mockChatContext.Object, queueArcadeService: null, matchmakingService: service);
        SendWhisper whisperHandler = new(_mockChatContext.Object, distributedState: null, queueArcadeService: null, matchmakingService: service);

        // 2. Players 0..4 accept via IM with "ACCEPT|<id>"
        for (int i = 0; i < 5; i++)
        {
            SimulatedChatSession sender = allClients[i];
            SendClientIM(imHandler, sender, MatchmakingService.ReadyCheckSenderName, $"ACCEPT|{readyCheckId}");
            state.AcceptedCount.Should().Be(i + 1);

            // Verify progress update broadcast
            foreach (SimulatedChatSession client in allClients)
            {
                List<string> whispers = GetWhispersFrom(client, MatchmakingService.ReadyCheckSenderName);
                whispers.Should().Contain($"STATUS|{readyCheckId}|{i + 1}|10|0");
            }
        }

        // 3. Players 5..8 accept via IM with shorthand numeric "1|<id>"
        for (int i = 5; i < 9; i++)
        {
            SimulatedChatSession sender = allClients[i];
            SendClientIM(imHandler, sender, MatchmakingService.ReadyCheckSenderName, $"1|{readyCheckId}");
            state.AcceptedCount.Should().Be(i + 1);
        }

        // Match server still idle with 9/10 accepted
        matchServer.SnapshotPackets().Should().BeEmpty();

        // 4. Final player (9) accepts via SendWhisper with "ACCEPT|<id>"
        SimulatedChatSession finalSender = allClients[9];
        SendClientWhisper(whisperHandler, finalSender, MatchmakingService.ReadyCheckSenderName, $"ACCEPT|{readyCheckId}");

        DateTimeOffset deadline = DateTimeOffset.UtcNow.AddSeconds(3);
        while (!HasCommand(matchServer, ChatProtocol.ChatServerToGameServer.NET_CHAT_GS_CREATE_MATCH) && DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(15);
        }

        // 5. Verification: Quorum reached!
        foreach (SimulatedChatSession client in allClients)
        {
            List<string> whispers = GetWhispersFrom(client, MatchmakingService.ReadyCheckSenderName);
            whispers.Should().Contain($"STATUS|{readyCheckId}|10|10|1",
                "All clients must receive quorum STATUS whisper indicating match launch");
        }

        HasCommand(matchServer, ChatProtocol.ChatServerToGameServer.NET_CHAT_GS_CREATE_MATCH).Should().BeTrue(
            "10/10 accepts over dual-protocol bridge must immediately allocate match server");
    }

    [Fact]
    public async Task ReadyCheck_DualProtocol_Decline_OverKongorReadyCheck_EjectsOffender_PreservesCompliantQueue()
    {
        // protects: Client declining via IM to "__kongor_ready_check__" ejects offending group and preserves compliant queue priority
        // fails_when: Decline message is ignored or compliant players lose their queue priority
        // seam: SendInstantMessage("DECLINE|<id>") -> MatchmakingService.TrySubmitReadyCheckResponseCore
        using MatchmakingService service = new(_defaultConfiguration, _mockChatContext.Object, timeProvider: _clock);
        SimulatedChatSession matchServer = RegisterIdleGameServer(serverId: 803, location: "USE");

        DateTimeOffset origQueueTime1 = _clock.GetUtcNow().AddSeconds(-60);
        DateTimeOffset origQueueTime2 = _clock.GetUtcNow().AddSeconds(-35);

        (List<SimulatedChatSession> team1, MatchmakingGroup group1) = CreateGroup(service, startAccountId: 801, count: 5, origQueueTime1);
        (List<SimulatedChatSession> team2, MatchmakingGroup group2) = CreateGroup(service, startAccountId: 806, count: 5, origQueueTime2);
        List<SimulatedChatSession> allClients = [.. team1, .. team2];

        bool matchFormed = await service.ProcessMatchForSimulationAsync([group1, group2]);
        matchFormed.Should().BeTrue();

        PendingReadyCheckState state = service.PendingReadyChecks.Values.Single();
        int readyCheckId = state.ReadyCheckId;

        SendInstantMessage imHandler = new(_mockChatContext.Object, queueArcadeService: null, matchmakingService: service);

        // Player 801 declines via IM
        SendClientIM(imHandler, team1[0], MatchmakingService.ReadyCheckSenderName, $"DECLINE|{readyCheckId}");

        // Verify decline status whisper broadcast
        foreach (SimulatedChatSession client in allClients)
        {
            List<string> whispers = GetWhispersFrom(client, MatchmakingService.ReadyCheckSenderName);
            whispers.Should().Contain(w => w.StartsWith($"STATUS|{readyCheckId}|", StringComparison.Ordinal) && w.EndsWith("|2", StringComparison.Ordinal),
                "All clients must receive decline STATUS whisper (state=2)");
        }

        // Offending group 1 ejected
        group1.QueueStartTime.Should().BeNull();
        foreach (SimulatedChatSession g1Client in team1)
        {
            HasCommand(g1Client, ChatProtocol.Matchmaking.NET_CHAT_CL_TMM_FAILED_TO_ACCEPT_PENDING_MATCH).Should().BeTrue();
        }

        // Compliant group 2 kept at front of queue with original timestamp
        group2.QueueStartTime.Should().Be(origQueueTime2);
        foreach (SimulatedChatSession g2Client in team2)
        {
            HasCommand(g2Client, ChatProtocol.Matchmaking.NET_CHAT_CL_TMM_GROUP_REJOIN_QUEUE).Should().BeTrue();
        }

        matchServer.SnapshotPackets().Should().BeEmpty();
    }

    [Fact]
    public async Task ReadyCheck_DualProtocol_Timeout_EjectsNonResponders_PreservesCompliantQueue()
    {
        // protects: Ready-check timeout sweep broadcasts timeout status whisper (state=3) and ejects ghosting groups
        // fails_when: Non-responding players hold up the matchmaker or timeout status is not broadcast
        // seam: MatchmakingService.EvaluateExpiredReadyChecks + CreateReadyCheckWhisperPacket
        using MatchmakingService service = new(_defaultConfiguration, _mockChatContext.Object, timeProvider: _clock);
        SimulatedChatSession matchServer = RegisterIdleGameServer(serverId: 804, location: "USE");

        DateTimeOffset origQueueTime1 = _clock.GetUtcNow().AddSeconds(-80);
        DateTimeOffset origQueueTime2 = _clock.GetUtcNow().AddSeconds(-40);

        (List<SimulatedChatSession> team1, MatchmakingGroup group1) = CreateGroup(service, startAccountId: 901, count: 5, origQueueTime1);
        (List<SimulatedChatSession> team2, MatchmakingGroup group2) = CreateGroup(service, startAccountId: 906, count: 5, origQueueTime2);
        List<SimulatedChatSession> allClients = [.. team1, .. team2];

        bool matchFormed = await service.ProcessMatchForSimulationAsync([group1, group2]);
        matchFormed.Should().BeTrue();

        PendingReadyCheckState state = service.PendingReadyChecks.Values.Single();
        int readyCheckId = state.ReadyCheckId;

        SendInstantMessage imHandler = new(_mockChatContext.Object, queueArcadeService: null, matchmakingService: service);

        // Team 1 accepts
        foreach (SimulatedChatSession g1Client in team1)
        {
            SendClientIM(imHandler, g1Client, MatchmakingService.ReadyCheckSenderName, $"ACCEPT|{readyCheckId}");
        }

        // Team 2 ghosts. Clock advances 16s (> 15.0s timeout)
        _clock.Advance(TimeSpan.FromSeconds(16));

        IReadOnlyList<PendingReadyCheckState> expired = service.EvaluateExpiredReadyChecks(_clock.GetUtcNow());
        expired.Should().ContainSingle(s => s.ReadyCheckId == readyCheckId);

        // Verify timeout status whisper broadcast (state=3)
        foreach (SimulatedChatSession client in allClients)
        {
            List<string> whispers = GetWhispersFrom(client, MatchmakingService.ReadyCheckSenderName);
            whispers.Should().Contain(w => w.StartsWith($"STATUS|{readyCheckId}|5|10|3", StringComparison.Ordinal),
                "All clients must receive timeout STATUS whisper with state=3");
        }

        // Team 2 ejected
        group2.QueueStartTime.Should().BeNull();
        foreach (SimulatedChatSession g2Client in team2)
        {
            HasCommand(g2Client, ChatProtocol.Matchmaking.NET_CHAT_CL_TMM_FAILED_TO_ACCEPT_PENDING_MATCH).Should().BeTrue();
        }

        // Team 1 retained with original queue start time
        group1.QueueStartTime.Should().Be(origQueueTime1);
        foreach (SimulatedChatSession g1Client in team1)
        {
            HasCommand(g1Client, ChatProtocol.Matchmaking.NET_CHAT_CL_TMM_GROUP_REJOIN_QUEUE).Should().BeTrue();
        }
    }

    private static void SendClientIM(SendInstantMessage imHandler, SimulatedChatSession sender, string target, string message)
    {
        ChatBuffer buffer = new();
        buffer.WriteCommand(ChatProtocol.Command.CHAT_CMD_IM);
        buffer.WriteString(target);
        buffer.WriteString(message);
        buffer.WriteInt8(0); // flags

        ChatBuffer readBuf = new(buffer.AsSpan());
        imHandler.Process(sender, readBuf);
    }

    private static void SendClientWhisper(SendWhisper whisperHandler, SimulatedChatSession sender, string target, string message)
    {
        ChatBuffer buffer = new();
        buffer.WriteCommand(ChatProtocol.Command.CHAT_CMD_WHISPER);
        buffer.WriteString(target);
        buffer.WriteString(message);

        ChatBuffer readBuf = new(buffer.AsSpan());
        whisperHandler.Process(sender, readBuf);
    }

    private static List<string> GetWhispersFrom(SimulatedChatSession session, string senderName)
    {
        List<string> results = [];
        List<byte[]> packets = session.SnapshotPackets();
        foreach (byte[] p in packets)
        {
            if (p.Length >= 4 && BitConverter.ToUInt16(p, 2) == (ushort)ChatProtocol.Command.CHAT_CMD_WHISPER)
            {
                ChatBuffer buffer = new(p);
                buffer.ReadInt16(); // length
                buffer.ReadInt16(); // command
                string sender = buffer.ReadString();
                string message = buffer.ReadString();
                if (string.Equals(sender, senderName, StringComparison.OrdinalIgnoreCase))
                {
                    results.Add(message);
                }
            }
        }

        return results;
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

    private static List<(int RemainingMs, int TotalMs, int TotalPlayers, int AcceptedPlayers, byte AcceptedFlag)> GetNativePendingMatchPackets(
        SimulatedChatSession session)
    {
        List<(int, int, int, int, byte)> results = [];
        List<byte[]> packets = session.SnapshotPackets();
        foreach (byte[] p in packets)
        {
            if (p.Length >= 4 && BitConverter.ToUInt16(p, 2) == ChatProtocol.Matchmaking.NET_CHAT_CL_TMM_PENDING_MATCH)
            {
                ChatBuffer buffer = new(p);
                buffer.ReadInt16(); // length
                buffer.ReadInt16(); // command
                int remMs = buffer.ReadInt32();
                int totMs = buffer.ReadInt32();
                int totPlayers = buffer.ReadInt32();
                int accPlayers = buffer.ReadInt32();
                byte accFlag = buffer.ReadInt8();
                results.Add((remMs, totMs, totPlayers, accPlayers, accFlag));
            }
        }

        return results;
    }

    private static List<int> ReadFailedToAcceptAccountIdsWithSentinel(SimulatedChatSession session)
    {
        List<int> ids = [];
        List<byte[]> packets = session.SnapshotPackets();
        foreach (byte[] p in packets)
        {
            if (p.Length >= 4 && BitConverter.ToUInt16(p, 2) == ChatProtocol.Matchmaking.NET_CHAT_CL_TMM_FAILED_TO_ACCEPT_PENDING_MATCH)
            {
                ChatBuffer buffer = new(p);
                buffer.ReadInt16(); // length
                buffer.ReadInt16(); // command
                while (true)
                {
                    int id = buffer.ReadInt32();
                    ids.Add(id);
                    if (id == -1)
                    {
                        break;
                    }
                }
            }
        }

        return ids;
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

    private sealed class RecordingQueueArcadeService : IQueueArcadeService
    {
        public int NotifyRankedMatchAllocatingCalls { get; private set; }

        public List<int> PulledRankedMatchIds { get; } = [];

        public List<(int AccountId, string Reason)> ReleasedAccounts { get; } = [];

        public IReadOnlyList<string> RotationHistory => [];

        public QueueArcadeMetricsSnapshot GetMetricsSnapshot() =>
            new(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, null);

        public string SelectNextMap(string? previousMap = null) => "midwars";

        public void OnArcadeServerHandshake(ChatSession serverSession)
        {
        }

        public void ConfirmArcadeMatchReady(ChatSession serverSession, int matchupId, int matchId)
        {
        }

        public void NotifyArcadeServerBotReadiness(int serverId, int activeBotCount, bool explicitReadyAck = false)
        {
        }

        public void OnArcadeMatchEnded(ChatSession serverSession, int matchId, string reason)
        {
        }

        public void OnArcadeServerNotIdle(ChatSession serverSession, byte currentStatus, int matchUpId)
        {
        }

        public bool ReleasePlayerFromArcade(int accountId, string reason, bool pauseCurrentQueueWait = false)
        {
            ReleasedAccounts.Add((accountId, reason));
            return false;
        }

        public void NotifyRankedMatchAllocating(IReadOnlyList<MatchmakingGroup> groups, string resolvedMapName)
        {
            NotifyRankedMatchAllocatingCalls++;
        }

        public void OnPlayersPulledToRankedMatch(IReadOnlyList<MatchmakingGroupMember> members, int rankedMatchId)
        {
            PulledRankedMatchIds.Add(rankedMatchId);
        }

        public bool TryHandleArcadeChatCommand(ChatSession clientSession, string rawMessage) => false;

        public Task EvaluateTickAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
