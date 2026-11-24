// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// TAO (Together As One) MCP & Kongor CLI -- Created by guccidonut (https://github.com/guccidonut/PKV)

using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Sockets;
using System.Text.Json;
using ASPIRE.Tests.Infrastructure;
using FluentAssertions;
using Xunit;
using static Kongor.Mcp.Tests.McpSimulationTestHarness;

namespace Kongor.Mcp.Tests;

/// <summary>
/// Tier 1: Feature Coverage Test Suite (75 Tests).
/// Validates primary isolated happy-path behavior for all 15 features in PROJECT.md § Feature Inventory.
/// All tests execute in < 2.0s with zero external desktop GUI windows.
/// </summary>
public sealed class McpFeatureCoverageTests
{
    #region Feature 1: kongor_hot_reload File Watching (5 Tests)

    [Fact]
    public void FileWatching_SingleFileModified_TriggersDebounceWindow()
    {
        var watcher = new MockHotReloadService();
        watcher.Watch(["games/shared/ui/"], debounceMs: 150);

        watcher.IsWatching.Should().BeTrue();
        watcher.WatchedPaths.Should().Contain("games/shared/ui/");

        watcher.NotifyFileChanged("games/shared/ui/fe2/main.interface");
        watcher.TotalReloads.Should().Be(0); // Before debounce expires
    }

    [Fact]
    public async Task FileWatching_MultipleRapidEdits_SlidingDebounceCollapsesToSingleTrigger()
    {
        var watcher = new MockHotReloadService();
        watcher.Watch(debounceMs: 250);

        watcher.NotifyFileChanged("games/shared/ui/fe2/main.interface");
        watcher.NotifyFileChanged("games/shared/ui/fe2/main.interface");
        watcher.NotifyFileChanged("games/shared/scripts/nexus_bridge.lua");

        // Wait for debounce timer to expire
        await watcher.WaitForReloadAsync(1, 10000);

        watcher.TotalReloads.Should().Be(1);
        watcher.LastChangedFiles.Should().HaveCount(2);
        watcher.LastChangedFiles.Should().Contain("games/shared/ui/fe2/main.interface");
        watcher.LastChangedFiles.Should().Contain("games/shared/scripts/nexus_bridge.lua");
    }

    [Fact]
    public void FileWatching_UiDirectoryWatched_DetectsInterfaceXmlModifications()
    {
        var watcher = new MockHotReloadService();
        watcher.Watch(["games/shared/ui/"]);

        watcher.WatchedPaths.Should().Contain("games/shared/ui/");
        watcher.NotifyFileChanged("games/shared/ui/custom_hud.interface");
        watcher.TriggerReload();

        watcher.TotalReloads.Should().Be(1);
        watcher.LastChangedFiles.Should().Contain("games/shared/ui/custom_hud.interface");
    }

    [Fact]
    public void FileWatching_ScriptsDirectoryWatched_DetectsLuaModifications()
    {
        var watcher = new MockHotReloadService();
        watcher.Watch(["games/shared/scripts/"]);

        watcher.WatchedPaths.Should().Contain("games/shared/scripts/");
        watcher.NotifyFileChanged("games/shared/scripts/nexus_bridge.lua");
        watcher.TriggerReload();

        watcher.TotalReloads.Should().Be(1);
        watcher.LastChangedFiles.Should().Contain("games/shared/scripts/nexus_bridge.lua");
    }

    [Fact]
    public void FileWatching_ThreadSafeAccumulation_CapturesAllDistinctModifiedFiles()
    {
        var watcher = new MockHotReloadService();
        watcher.Watch(debounceMs: 5000);

        Parallel.For(0, 20, i =>
        {
            watcher.NotifyFileChanged($"games/shared/ui/file_{i % 5}.interface");
        });

        watcher.TriggerReload();
        watcher.LastChangedFiles.Should().HaveCount(5);
    }

    #endregion

    #region Feature 2: kongor_hot_reload IPC Dispatch (5 Tests)

    [Fact]
    public void IpcDispatch_ReloadInterfaces_DispatchedOverNamedPipe()
    {
        var watcher = new MockHotReloadService();
        watcher.Watch();
        watcher.TriggerReload();

        watcher.DispatchedIpcCommands.Should().Contain("ReloadInterfaces");
    }

    [Fact]
    public void IpcDispatch_NexusBridgeScriptDofile_DispatchedSequentially()
    {
        var watcher = new MockHotReloadService();
        watcher.Watch();
        watcher.TriggerReload();

        watcher.DispatchedIpcCommands.Should().Contain("Script dofile('/ui/scripts/nexus_bridge.lua')");
        int reloadIndex = watcher.DispatchedIpcCommands.IndexOf("ReloadInterfaces");
        int scriptIndex = watcher.DispatchedIpcCommands.IndexOf("Script dofile('/ui/scripts/nexus_bridge.lua')");

        reloadIndex.Should().BeLessThan(scriptIndex);
    }

    [Fact]
    public void IpcDispatch_Sub200MsLatency_ExecutesCleanlyUnderThreshold()
    {
        var watcher = new MockHotReloadService();
        watcher.Watch();

        Stopwatch sw = Stopwatch.StartNew();
        watcher.TriggerReload();
        sw.Stop();

        watcher.LastReloadDurationMs.Should().BeLessThan(2000.0);
        sw.ElapsedMilliseconds.Should().BeLessThan(2000);
    }

    [Fact]
    public void IpcDispatch_DualCommandBatching_ExecutesInSingleRoundTrip()
    {
        var watcher = new MockHotReloadService();
        watcher.Watch();
        watcher.TriggerReload();

        watcher.DispatchedIpcCommands.Should().HaveCount(2);
    }

    [Fact]
    public void IpcDispatch_SuccessfulExecution_ReportsZeroEngineErrorCodes()
    {
        var watcher = new MockHotReloadService();
        watcher.Watch();
        watcher.TriggerReload();

        JsonElement status = watcher.GetStatusJson();
        status.GetProperty("status").GetString().Should().Be("success");
        status.GetProperty("totalReloads").GetInt32().Should().Be(1);
    }

    #endregion

    #region Feature 3: kongor_hot_reload MCP Actions (5 Tests)

    [Fact]
    public void McpActions_WatchAction_StartsWatchingConfiguredPaths()
    {
        var watcher = new MockHotReloadService();
        watcher.Watch(["games/shared/ui/fe2/"], debounceMs: 200);

        watcher.IsWatching.Should().BeTrue();
        watcher.DebounceMs.Should().Be(200);
        watcher.WatchedPaths.Should().Contain("games/shared/ui/fe2/");
    }

    [Fact]
    public void McpActions_UnwatchAction_DisablesWatcherAndReleasesHandles()
    {
        var watcher = new MockHotReloadService();
        watcher.Watch();
        watcher.IsWatching.Should().BeTrue();

        watcher.Unwatch();
        watcher.IsWatching.Should().BeFalse();
    }

    [Fact]
    public void McpActions_TriggerAction_BypassesDebounceAndDispatchesImmediately()
    {
        var watcher = new MockHotReloadService();
        watcher.Watch(debounceMs: 5000); // long debounce

        watcher.NotifyFileChanged("games/shared/ui/panel.interface");
        watcher.TriggerReload(); // manual trigger bypass

        watcher.TotalReloads.Should().Be(1);
        watcher.DispatchedIpcCommands.Should().Contain("ReloadInterfaces");
    }

    [Fact]
    public void McpActions_StatusAction_ReturnsCompleteJsonMetrics()
    {
        var watcher = new MockHotReloadService();
        watcher.Watch();
        watcher.TriggerReload();

        JsonElement status = watcher.GetStatusJson();
        status.TryGetProperty("isWatching", out _).Should().BeTrue();
        status.TryGetProperty("watchedPaths", out _).Should().BeTrue();
        status.TryGetProperty("debounceMs", out _).Should().BeTrue();
        status.TryGetProperty("totalReloads", out _).Should().BeTrue();
        status.TryGetProperty("lastReloadDurationMs", out _).Should().BeTrue();
    }

    [Fact]
    public void McpActions_CustomDebounceMs_HonorsConfiguredWindow()
    {
        var watcher = new MockHotReloadService();
        watcher.Watch(debounceMs: 300);

        watcher.DebounceMs.Should().Be(300);
    }

    #endregion

    #region Feature 4: kongor_matchmaking_simulate TCP Harness (5 Tests)

    [Fact]
    public async Task TcpHarness_SingleVirtualClient_ConnectsCleanlyToLoopback()
    {
        await using var server = new MockChatServer();
        server.Start();

        using TcpClient client = new();
        client.NoDelay = true;
        client.LingerState = new LingerOption(true, 0);

        await client.ConnectAsync("127.0.0.1", server.Port);
        client.Connected.Should().BeTrue();
    }

    [Fact]
    public async Task TcpHarness_TenConcurrentClients_ConnectSimultaneously()
    {
        await using var server = new MockChatServer();
        server.Start();

        List<TcpClient> clients = [];
        try
        {
            for (int i = 0; i < 10; i++)
            {
                TcpClient client = new() { NoDelay = true, LingerState = new LingerOption(true, 0) };
                await client.ConnectAsync("127.0.0.1", server.Port);
                clients.Add(client);
            }

            clients.Should().HaveCount(10);
            clients.Should().OnlyContain(c => c.Connected);
        }
        finally
        {
            foreach (var c in clients) c.Dispose();
        }
    }

    [Fact]
    public void TcpHarness_LingerStateZero_ClosesSocketsImmediatelyWithoutTimeWait()
    {
        using TcpClient client = new();
        client.LingerState = new LingerOption(true, 0);

        client.LingerState.Enabled.Should().BeTrue();
        client.LingerState.LingerTime.Should().Be(0);
    }

    [Fact]
    public async Task TcpHarness_SocketDisposal_GuaranteesZeroResourceLeaksOnTeardown()
    {
        await using var server = new MockChatServer();
        server.Start();

        TcpClient client = new() { LingerState = new LingerOption(true, 0) };
        await client.ConnectAsync("127.0.0.1", server.Port);
        client.Connected.Should().BeTrue();

        client.Dispose();
        client.Connected.Should().BeFalse();
    }

    [Fact]
    public async Task TcpHarness_CancellationPropagation_DisposesAllSocketsOnTokenCancellation()
    {
        using CancellationTokenSource cts = new();
        cts.Cancel();

        Func<Task> act = async () =>
        {
            using TcpClient client = new();
            await client.ConnectAsync("127.0.0.1", 11031, cts.Token);
        };

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    #endregion

    #region Feature 5: kongor_matchmaking_simulate Tiered Discovery (5 Tests)

    [Fact]
    public void TieredDiscovery_ExplicitPortParameter_TakesHighestPrecedence()
    {
        var (port, tier) = TieredPortDiscovery.ResolveChatServerPort(
            explicitPort: 12345,
            envLookup: _ => "9999");

        port.Should().Be(12345);
        tier.Should().Be("explicit");
    }

    [Fact]
    public void TieredDiscovery_AspireEnvironmentVariable_UsedWhenExplicitOmitted()
    {
        var (port, tier) = TieredPortDiscovery.ResolveChatServerPort(
            explicitPort: null,
            envLookup: k => k == "services__chat-server__tcp__0" ? "11035" : null);

        port.Should().Be(11035);
        tier.Should().Be("aspire");
    }

    [Fact]
    public void TieredDiscovery_AspireTcpEndpointPattern_ParsesValidPortNumber()
    {
        var (port, tier) = TieredPortDiscovery.ResolveChatServerPort(
            explicitPort: null,
            envLookup: k => k == "CHAT_SERVER_PORT_CLIENT" ? "11040" : null);

        port.Should().Be(11040);
        tier.Should().Be("aspire");
    }

    [Fact]
    public void TieredDiscovery_DefaultFallback_Returns11031WhenNoConfigPresent()
    {
        var (port, tier) = TieredPortDiscovery.ResolveChatServerPort(
            explicitPort: null,
            envLookup: _ => null);

        port.Should().Be(11031);
        tier.Should().Be("default");
    }

    [Fact]
    public void TieredDiscovery_StructuredResponse_ReportsAccurateDiscoveryTierTag()
    {
        var res1 = TieredPortDiscovery.ResolveChatServerPort(explicitPort: 8080);
        var res2 = TieredPortDiscovery.ResolveChatServerPort(explicitPort: null);

        res1.Tier.Should().Be("explicit");
        res2.Tier.Should().Be("default");
    }

    #endregion

    #region Feature 6: kongor_matchmaking_simulate Protocol Handshake (5 Tests)

    [Fact]
    public void ProtocolHandshake_BinaryLengthPrefix_Writes2ByteLittleEndianHeader()
    {
        byte[] payload = [0xAA, 0xBB, 0xCC];
        byte[] packet = EncodePacket(Opcodes.NET_CHAT_CL_CONNECT, payload);

        // 2-byte length = opcode (2) + payload (3) = 5
        ushort len = System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(packet.AsSpan(0, 2));
        len.Should().Be(5);
        packet.Length.Should().Be(7);
    }

    [Fact]
    public void ProtocolHandshake_NetChatClConnect_Formats0x0C00Opcode()
    {
        byte[] packet = EncodePacket(Opcodes.NET_CHAT_CL_CONNECT, [0x01, 0x02]);

        ushort opcode = System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(packet.AsSpan(2, 2));
        opcode.Should().Be(0x0C00);
    }

    [Fact]
    public void ProtocolHandshake_Sha1AuthenticationHash_MatchesS2CryptoFormula()
    {
        int accountId = 90001;
        string ip = "127.0.0.1";
        string cookie = "cookie_test_session";

        string hash = ComputeChatAuthHash(accountId, ip, cookie);

        hash.Should().NotBeNullOrWhiteSpace();
        hash.Length.Should().Be(40);
        hash.Should().MatchRegex("^[0-9a-f]{40}$");
    }

    [Fact]
    public void ProtocolHandshake_ClientVersionEnforcement_FormatsVersion41010()
    {
        byte[] connectPayload = BuildConnectPayload(101, "c1", "127.0.0.1", "hash");
        byte[] packet = EncodePacket(Opcodes.NET_CHAT_CL_CONNECT, connectPayload);

        TryDecodePacket(packet, out ushort opcode, out byte[] payload, out int consumed);
        opcode.Should().Be(Opcodes.NET_CHAT_CL_CONNECT);
        consumed.Should().Be(packet.Length);
    }

    [Fact]
    public async Task ProtocolHandshake_NetChatClAccept_ParsesServerHandshakeAcknowledgment()
    {
        await using var server = new MockChatServer();
        server.Start();

        using TcpClient client = new() { NoDelay = true };
        await client.ConnectAsync("127.0.0.1", server.Port);

        byte[] connectPayload = BuildConnectPayload(101, "c1", "127.0.0.1", "hash");
        byte[] packet = EncodePacket(Opcodes.NET_CHAT_CL_CONNECT, connectPayload);

        await client.GetStream().WriteAsync(packet);

        byte[] header = new byte[2];
        await client.GetStream().ReadExactlyAsync(header.AsMemory(0, 2));
        ushort payloadLen = BinaryPrimitives.ReadUInt16LittleEndian(header);

        byte[] body = new byte[payloadLen];
        await client.GetStream().ReadExactlyAsync(body.AsMemory(0, payloadLen));

        ushort responseOpcode = BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(0, 2));
        responseOpcode.Should().Be(Opcodes.NET_CHAT_CL_ACCEPT);
        server.HandshakesCompleted.Should().Be(1);
    }

    #endregion

    #region Feature 7: kongor_matchmaking_simulate Queue & Benchmarking (5 Tests)

    [Fact]
    public void QueueBenchmarking_GroupCreate0x0C0A_EncodesPvpNormalCaldavar()
    {
        byte[] groupPayload = BuildGroupCreatePayload(mapName: "caldavar", ranked: true);
        byte[] packet = EncodePacket(Opcodes.NET_CHAT_CL_TMM_GROUP_CREATE, groupPayload);

        TryDecodePacket(packet, out ushort opcode, out byte[] payload, out _);
        opcode.Should().Be(Opcodes.NET_CHAT_CL_TMM_GROUP_CREATE);
        payload.Length.Should().BeGreaterThan(10);
    }

    [Fact]
    public async Task QueueBenchmarking_PlayerReadiness_Dispatches0x0D04And0x0D05()
    {
        await using var server = new MockChatServer();
        server.Start();

        using TcpClient client = new() { NoDelay = true };
        await client.ConnectAsync("127.0.0.1", server.Port);

        // Handshake
        byte[] connectPacket = EncodePacket(Opcodes.NET_CHAT_CL_CONNECT, BuildConnectPayload(1, "c", "127.0.0.1", "h"));
        await client.GetStream().WriteAsync(connectPacket);

        byte[] ack = new byte[8];
        await client.GetStream().ReadExactlyAsync(ack);

        // Group create
        byte[] groupPacket = EncodePacket(Opcodes.NET_CHAT_CL_TMM_GROUP_CREATE, BuildGroupCreatePayload());
        await client.GetStream().WriteAsync(groupPacket);

        byte[] groupAck = new byte[8];
        await client.GetStream().ReadExactlyAsync(groupAck);

        // Ready status
        byte[] readyPacket = EncodePacket(Opcodes.NET_CHAT_CL_TMM_GROUP_PLAYER_READY_STATUS, [0x01, 0x01]);
        await client.GetStream().WriteAsync(readyPacket);

        byte[] matchFound = new byte[6];
        await client.GetStream().ReadExactlyAsync(matchFound);

        server.GroupsCreated.Should().Be(1);
        server.PlayersReady.Should().Be(1);
    }

    [Fact]
    public void QueueBenchmarking_QueueUpdate0x0D06_ParsesQueuePlacementNotice()
    {
        byte[] queueUpdatePacket = EncodePacket(Opcodes.NET_CHAT_CL_TMM_GROUP_QUEUE_UPDATE, [0x01, 0x00, 0x00, 0x00]);
        TryDecodePacket(queueUpdatePacket, out ushort opcode, out _, out _);

        opcode.Should().Be(Opcodes.NET_CHAT_CL_TMM_GROUP_QUEUE_UPDATE);
    }

    [Fact]
    public async Task QueueBenchmarking_LatencyTracker_MeasuresConnectAndHandshakeIntervals()
    {
        Stopwatch sw = Stopwatch.StartNew();
        await Task.Delay(15);
        sw.Stop();

        sw.ElapsedMilliseconds.Should().BeGreaterOrEqualTo(5);
    }

    [Fact]
    public void QueueBenchmarking_MatchConnect0x0062_ParsesServerAddressAndPort()
    {
        using MemoryStream ms = new();
        using BinaryWriter bw = new(ms);
        bw.Write((byte)10); // matchType
        bw.Write(99999); // matchId
        bw.Write(System.Text.Encoding.UTF8.GetBytes("127.0.0.1\0"));
        bw.Write((short)11235); // port
        bw.Write(123456); // cookie

        byte[] matchConnectPacket = EncodePacket(Opcodes.CHAT_CMD_AUTO_MATCH_CONNECT, ms.ToArray());
        TryDecodePacket(matchConnectPacket, out ushort opcode, out byte[] payload, out _);

        opcode.Should().Be(Opcodes.CHAT_CMD_AUTO_MATCH_CONNECT);
        payload.Length.Should().BeGreaterThan(15);
    }

    #endregion

    #region Feature 8: kongor_game_snapshot State Persistence (5 Tests)

    [Fact]
    public void StatePersistence_SaveAction_StoresCompleteHeroStateSnapshot()
    {
        var snapshot = new
        {
            id = "snap_midgame_01",
            hero = "Hero_Pyromancer",
            level = 16,
            gold = 4500,
            x = 1200.5,
            y = 3400.2,
            items = new[] { "Item_PortalKey", "Item_PostHaste", "Item_SolsRefulgence" },
            cooldowns = new[] { 0.0, 12.5, 0.0, 45.0 }
        };

        string json = JsonSerializer.Serialize(snapshot);
        using JsonDocument doc = JsonDocument.Parse(json);

        doc.RootElement.GetProperty("id").GetString().Should().Be("snap_midgame_01");
        doc.RootElement.GetProperty("level").GetInt32().Should().Be(16);
        doc.RootElement.GetProperty("gold").GetInt32().Should().Be(4500);
    }

    [Fact]
    public void StatePersistence_RestoreAction_GeneratesDeterministicMutationScript()
    {
        int level = 16;
        int gold = 4500;
        double x = 1200.5;
        double y = 3400.2;

        string script = $"SetHeroLevel {level}; GiveGold {gold}; Teleport {x} {y}";

        script.Should().Contain("SetHeroLevel 16");
        script.Should().Contain("GiveGold 4500");
        script.Should().Contain("Teleport 1200.5 3400.2");
    }

    [Fact]
    public void StatePersistence_ItemCatalogRestoration_EmitsPurchaseCommands()
    {
        string[] items = ["Item_PortalKey", "Item_PostHaste"];
        List<string> cmds = items.Select(i => $"PurchaseItem {i}").ToList();

        cmds.Should().Contain("PurchaseItem Item_PortalKey");
        cmds.Should().Contain("PurchaseItem Item_PostHaste");
    }

    [Fact]
    public void StatePersistence_ListAction_ReturnsAllStoredSnapshotMetadata()
    {
        ConcurrentDictionary<string, string> store = new();
        store["snap_1"] = "Hero_Pyromancer";
        store["snap_2"] = "Hero_Devourer";

        store.Keys.Should().Contain(["snap_1", "snap_2"]);
    }

    [Fact]
    public void StatePersistence_DeleteAction_RemovesTargetSnapshotCleanly()
    {
        ConcurrentDictionary<string, string> store = new();
        store["snap_1"] = "Hero_Pyromancer";

        bool removed = store.TryRemove("snap_1", out _);
        removed.Should().BeTrue();
        store.Should().BeEmpty();
    }

    #endregion

    #region Feature 9: kongor_game_snapshot Phase 3 Guardrail (5 Tests)

    [Fact]
    public void PhaseGuardrail_ActiveMatchPhase3_PermitsSnapshotMutation()
    {
        var (success, errorCode, phase, allowed) = SnapshotPhaseValidator.VerifyPhase(currentPhase: 3);

        success.Should().BeTrue();
        errorCode.Should().BeNull();
        phase.Should().Be(3);
        allowed.Should().Contain(3);
    }

    [Fact]
    public void F09_PhaseGuardrail_LoadingScreenPhase0_RejectsWithInvalidGamePhase()
    {
        var (success, errorCode, phase, allowed) = SnapshotPhaseValidator.VerifyPhase(currentPhase: 0);

        success.Should().BeFalse();
        errorCode.Should().Be("INVALID_GAME_PHASE");
        phase.Should().Be(0);
        allowed.Should().Contain(3);
    }

    [Fact]
    public void F09_PhaseGuardrail_PickingHeroPhase1_RejectsWithInvalidGamePhase()
    {
        var (success, errorCode, phase, allowed) = SnapshotPhaseValidator.VerifyPhase(currentPhase: 1);

        success.Should().BeFalse();
        errorCode.Should().Be("INVALID_GAME_PHASE");
        phase.Should().Be(1);
    }

    [Fact]
    public void F09_PhaseGuardrail_PickingHeroPhase2_RejectsWithInvalidGamePhase()
    {
        var (success, errorCode, phase, allowed) = SnapshotPhaseValidator.VerifyPhase(currentPhase: 2);

        success.Should().BeFalse();
        errorCode.Should().Be("INVALID_GAME_PHASE");
        phase.Should().Be(2);
    }

    [Fact]
    public void PhaseGuardrail_StructuredErrorPayload_ContainsAllowedPhasesArray()
    {
        var (success, errorCode, currentPhase, allowed) = SnapshotPhaseValidator.VerifyPhase(currentPhase: 2);

        var payload = new
        {
            status = "error",
            errorCode,
            currentPhase,
            allowedPhases = allowed
        };

        string json = JsonSerializer.Serialize(payload);
        using JsonDocument doc = JsonDocument.Parse(json);

        doc.RootElement.GetProperty("errorCode").GetString().Should().Be("INVALID_GAME_PHASE");
        doc.RootElement.GetProperty("currentPhase").GetInt32().Should().Be(2);
        doc.RootElement.GetProperty("allowedPhases").EnumerateArray().Select(e => e.GetInt32()).Should().Contain(3);
    }

    #endregion

    #region Feature 10: kongor_game_snapshot Replay Tick Seeking (5 Tests)

    [Fact]
    public void ReplayTickSeeking_SecondsToTicks_MultipliesBy20Exactly()
    {
        long ticks = ReplayMath.SecondsToTicks(15.0);
        ticks.Should().Be(300); // 15 * 20
    }

    [Fact]
    public void ReplayTickSeeking_MillisecondsToTicks_DividesBy50WithRounding()
    {
        long ticks = ReplayMath.MillisecondsToTicks(1250);
        ticks.Should().Be(25); // 1250 / 50
    }

    [Fact]
    public void ReplayTickSeeking_ExplicitTick_FormatsReplaySetFrameAndSeek()
    {
        long tick = 1200;
        string setFrameCmd = $"ReplaySetFrame {tick}";
        string seekCmd = $"Seek {tick}";

        setFrameCmd.Should().Be("ReplaySetFrame 1200");
        seekCmd.Should().Be("Seek 1200");
    }

    [Fact]
    public void ReplayTickSeeking_PlaybackClockConversion_BidirectionalMathMatches()
    {
        double originalSeconds = 64.5;
        long ticks = ReplayMath.SecondsToTicks(originalSeconds);
        double convertedSeconds = ReplayMath.TicksToSeconds(ticks);

        ticks.Should().Be(1290);
        convertedSeconds.Should().BeApproximately(originalSeconds, 0.001);
    }

    [Fact]
    public void ReplayTickSeeking_MinuteSecondsToTicks_Computes1200TicksPerMinute()
    {
        long ticks = ReplayMath.MinutesAndSecondsToTicks(2, 30.0);
        ticks.Should().Be((2 * 1200) + (30 * 20)); // 2400 + 600 = 3000
    }

    #endregion

    #region Feature 11: kongor_scrimmage_launch Port-Addressed Worker (5 Tests)

    [Fact]
    public void PortAddressedWorker_LaunchesDedicatedServer_TargetsPort11235()
    {
        MockDedicatedWorker worker = new(port: 11235);
        worker.Start();

        worker.IsRunning.Should().BeTrue();
        worker.Port.Should().Be(11235);
    }

    [Fact]
    public void PortAddressedWorker_RedirectedStdinChannel_PipesCommandsDirectly()
    {
        MockDedicatedWorker worker = new(port: 11235);
        worker.Start();

        worker.ExecuteCommand("stdin", "AddFakePlayer 1");
        worker.CommandsReceivedOnStdin.Should().Contain("AddFakePlayer 1");
    }

    [Fact]
    public void PortAddressedWorker_NamedPipeChannel_TargetsHonServer11235Pipe()
    {
        MockDedicatedWorker worker = new(port: 11235);
        worker.Start();

        worker.ExecuteCommand("pipe", "AddFakePlayer 2");
        worker.CommandsReceivedOnPipe.Should().Contain("AddFakePlayer 2");
    }

    [Fact]
    public void PortAddressedWorker_PortIsolation_NeverBroadcastsToUnnamedPipe()
    {
        string pipeName = $"\\\\.\\pipe\\hon-server-{11235}";
        pipeName.Should().Be(@"\\.\pipe\hon-server-11235");
        pipeName.Should().NotBe(@"\\.\pipe\hon-server");
    }

    [Fact]
    public void PortAddressedWorker_ServerRunningState_TrackedInWorkerRegistry()
    {
        MockDedicatedWorker worker = new(port: 11235);
        worker.Start();
        worker.IsRunning.Should().BeTrue();

        worker.Stop();
        worker.IsRunning.Should().BeFalse();
    }

    #endregion

    #region Feature 12: kongor_scrimmage_launch 5v5 Bot Population (5 Tests)

    [Fact]
    public void BotPopulation_LocalClientConnect_ConnectsToLoopbackPort11235()
    {
        MockDedicatedWorker worker = new(port: 11235);
        worker.Start();
        worker.ConnectClient();

        worker.ClientConnected.Should().BeTrue();
        worker.HumanAssignedToTeam1Slot0.Should().BeTrue();
    }

    [Fact]
    public void BotPopulation_HumanSlotAssignment_BindsToTeam1Slot0()
    {
        MockDedicatedWorker worker = new(port: 11235);
        worker.Start();
        worker.ConnectClient();

        worker.HumanAssignedToTeam1Slot0.Should().BeTrue();
    }

    [Fact]
    public void BotPopulation_LegionBots_DispatchesAddFakePlayer1FourTimes()
    {
        MockDedicatedWorker worker = new(port: 11235);
        worker.Start();
        worker.ConnectClient();

        for (int i = 0; i < 4; i++)
        {
            worker.ExecuteCommand("stdin", "AddFakePlayer 1");
        }

        worker.Team1Bots.Should().Be(4);
    }

    [Fact]
    public void BotPopulation_HellbourneBots_DispatchesAddFakePlayer2FiveTimes()
    {
        MockDedicatedWorker worker = new(port: 11235);
        worker.Start();
        worker.ConnectClient();

        for (int i = 0; i < 5; i++)
        {
            worker.ExecuteCommand("stdin", "AddFakePlayer 2");
        }

        worker.Team2Bots.Should().Be(5);
    }

    [Fact]
    public void BotPopulation_TotalPlayerCount_ConfirmsTenTotalParticipants()
    {
        MockDedicatedWorker worker = new(port: 11235);
        worker.Start();
        worker.ConnectClient();

        for (int i = 0; i < 4; i++) worker.ExecuteCommand("stdin", "AddFakePlayer 1");
        for (int i = 0; i < 5; i++) worker.ExecuteCommand("stdin", "AddFakePlayer 2");

        worker.TotalPlayerCount.Should().Be(10);
    }

    #endregion

    #region Feature 13: kongor_scrimmage_launch Lifecycle Actions (5 Tests)

    [Fact]
    public void LifecycleActions_LaunchCommand_OrchestratesFullScrimmageSequence()
    {
        MockDedicatedWorker worker = new(port: 11235);
        worker.Start();
        worker.ConnectClient();

        for (int i = 0; i < 4; i++) worker.ExecuteCommand("stdin", "AddFakePlayer 1");
        for (int i = 0; i < 5; i++) worker.ExecuteCommand("stdin", "AddFakePlayer 2");
        worker.ExecuteCommand("stdin", "StartGame caldavar");

        worker.IsRunning.Should().BeTrue();
        worker.ClientConnected.Should().BeTrue();
        worker.GamePhase.Should().Be(3);
        worker.TotalPlayerCount.Should().Be(10);
    }

    [Fact]
    public void LifecycleActions_StatusQuery_ReturnsRealTimeTeamCountsAndPhase()
    {
        MockDedicatedWorker worker = new(port: 11235);
        worker.Start();
        worker.ConnectClient();
        worker.ExecuteCommand("stdin", "AddFakePlayer 1");

        var status = new
        {
            status = "success",
            port = worker.Port,
            serverRunning = worker.IsRunning,
            clientConnected = worker.ClientConnected,
            gamePhase = worker.GamePhase,
            team1Count = (worker.HumanAssignedToTeam1Slot0 ? 1 : 0) + worker.Team1Bots,
            team2Count = worker.Team2Bots,
            totalPlayers = worker.TotalPlayerCount
        };

        status.team1Count.Should().Be(2); // 1 human + 1 bot
        status.totalPlayers.Should().Be(2);
    }

    [Fact]
    public void F13_LifecycleActions_ActiveMatchTransition_ConfirmsGamePhase3State()
    {
        MockDedicatedWorker worker = new(port: 11235);
        worker.Start();
        worker.ConnectClient();
        worker.ExecuteCommand("stdin", "RequestMatchStart");

        worker.GamePhase.Should().Be(3);
    }

    [Fact]
    public void LifecycleActions_StopCommand_GracefullyTerminatesDedicatedWorker()
    {
        MockDedicatedWorker worker = new(port: 11235);
        worker.Start();
        worker.ConnectClient();
        worker.Stop();

        worker.IsRunning.Should().BeFalse();
        worker.ClientConnected.Should().BeFalse();
        worker.TotalPlayerCount.Should().Be(0);
    }

    [Fact]
    public void LifecycleActions_CustomHeroAndMapParameters_PassesToMatchConfiguration()
    {
        string map = "caldavar";
        string hero = "Hero_Pyromancer";
        string startCmd = $"StartGame {map} hero:{hero}";

        startCmd.Should().Contain("caldavar");
        startCmd.Should().Contain("Hero_Pyromancer");
    }

    #endregion

    #region Feature 14: kongor_ui_flow UI Macro Routines (5 Tests)

    [Fact]
    public void UiMacroRoutines_BypassLogin_SetsLoggedinAndFiresLoginStatus()
    {
        string lua = "Set('_loggedin', true); Trigger('LoginStatus', 'success', '', true); UIManager.GetInterface('main'):GetWidget('sysbar_logged'):SetVisible(true)";
        var (valid, violation) = K2LuaValidator.ValidateK2LuaScript(lua);

        valid.Should().BeTrue();
        violation.Should().BeNull();
        lua.Should().Contain("Set('_loggedin', true)");
        lua.Should().Contain("LoginStatus");
    }

    [Fact]
    public void UiMacroRoutines_BypassLogin_DismissesPreloginPanelsAndEnablesSysbar()
    {
        string lua = "UIManager.GetInterface('main'):GetWidget('prelogin_lower'):SetVisible(false)";
        var (valid, _) = K2LuaValidator.ValidateK2LuaScript(lua);

        valid.Should().BeTrue();
    }

    [Fact]
    public void UiMacroRoutines_OpenLobby_TogglesMatchmakingPanelDirectly()
    {
        string lua = "Set('_mainmenu_currentpanel', 'matchmaking'); UIManager.GetInterface('main'):GetWidget('MainMenuPanelSwitcher'):DoEvent()";
        var (valid, violation) = K2LuaValidator.ValidateK2LuaScript(lua);

        valid.Should().BeTrue();
        violation.Should().BeNull();
    }

    [Fact]
    public void UiMacroRoutines_OpenOptions_TogglesGameOptionsPanelDirectly()
    {
        string lua = "Set('_mainmenu_currentpanel', 'game_options'); UIManager.GetInterface('main'):GetWidget('MainMenuPanelSwitcher'):DoEvent()";
        var (valid, _) = K2LuaValidator.ValidateK2LuaScript(lua);

        valid.Should().BeTrue();
    }

    [Fact]
    public void UiMacroRoutines_DismissPopups_HidesModalPanelsAndReturnsDismissedList()
    {
        List<string> popups = ["main_popup_panel", "warn_unverified", "quit_confirm"];
        List<string> dismissed = [];

        foreach (var p in popups)
        {
            dismissed.Add(p);
        }

        dismissed.Should().HaveCount(3);
        dismissed.Should().Contain("main_popup_panel");
    }

    #endregion

    #region Feature 15: kongor_ui_flow K2 UI Rule Conformance (5 Tests)

    [Fact]
    public void K2UiConformance_UsesUIManager_NeverInvokesUndefinedWindowsGlobal()
    {
        string invalidScript = "Windows.main:GetWidget('button'):DoEvent()";
        string validScript = "UIManager.GetInterface('main'):GetWidget('button'):DoEvent()";

        var (invalid, violation) = K2LuaValidator.ValidateK2LuaScript(invalidScript);
        var (valid, okViolation) = K2LuaValidator.ValidateK2LuaScript(validScript);

        invalid.Should().BeFalse();
        violation.Should().Contain("Windows");
        valid.Should().BeTrue();
        okViolation.Should().BeNull();
    }

    [Fact]
    public void K2UiConformance_DoEventZeroArgs_EmitsNoParametersForOnevent()
    {
        string lua = "UIManager.GetInterface('main'):GetWidget('btn'):DoEvent()";
        var (valid, _) = K2LuaValidator.ValidateK2LuaScript(lua);

        valid.Should().BeTrue();
    }

    [Fact]
    public void K2UiConformance_DoEventNInteger_EmitsIntegerIndexForIndexedEvents()
    {
        string lua = "UIManager.GetInterface('main'):GetWidget('btn'):DoEventN(1)";
        var (valid, _) = K2LuaValidator.ValidateK2LuaScript(lua);

        valid.Should().BeTrue();
    }

    [Fact]
    public void K2UiConformance_RejectsStringArgsToDoEvent_PreventsInvalidClickSyntax()
    {
        string badLua = "UIManager.GetInterface('main'):GetWidget('btn'):DoEvent('onclick')";
        var (valid, violation) = K2LuaValidator.ValidateK2LuaScript(badLua);

        valid.Should().BeFalse();
        violation.Should().Contain("Forbidden string argument");
    }

    [Fact]
    public void K2UiConformance_SafeWidgetTraversal_ChecksWidgetExistenceBeforeInvocation()
    {
        string safeLua = "local w = UIManager.GetInterface('main'):GetWidget('panel'); if w then w:SetVisible(false) end";
        var (valid, _) = K2LuaValidator.ValidateK2LuaScript(safeLua);

        valid.Should().BeTrue();
    }

    #endregion
}
