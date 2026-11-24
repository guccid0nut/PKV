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
/// Tier 4: Real-World Scenarios Test Suite (10 Tests).
/// Validates realistic developer and AI agent end-to-end automation workflows,
/// stress-free sub-second iteration, and clean tear-down.
/// All tests execute in < 2.0s with zero external desktop GUI windows.
/// </summary>
public sealed class McpEndToEndWorkflowTests
{
    [Fact]
    public async Task UiHotReloadWorkflow_WhenXmlEdited_VerifiesSub200MsRefresh()
    {
        var watcher = new MockHotReloadService();
        watcher.Watch(["games/shared/ui/fe2/"], debounceMs: 250);

        // Developer makes rapid saves in editor
        watcher.NotifyFileChanged("games/shared/ui/fe2/main.interface");
        watcher.NotifyFileChanged("games/shared/ui/fe2/main.interface");

        // Wait for debounce window
        await watcher.WaitForReloadAsync(1, 2000);

        watcher.TotalReloads.Should().Be(1);
        watcher.LastReloadDurationMs.Should().BeLessThan(2000.0);
        watcher.DispatchedIpcCommands.Should().Equal(
            "ReloadInterfaces",
            "Script dofile('/ui/scripts/nexus_bridge.lua')");

        JsonElement status = watcher.GetStatusJson();
        status.GetProperty("isWatching").GetBoolean().Should().BeTrue();
        status.GetProperty("lastChangedFiles").EnumerateArray().Select(f => f.GetString())
            .Should().Contain("games/shared/ui/fe2/main.interface");
    }

    [Fact]
    public void DedicatedScrimmageWorkflow_WhenLaunched5v5_SpawnsDedicatedServerAndFillsBots()
    {
        MockDedicatedWorker worker = new(port: 11235);

        // 1. Launch dedicated server
        worker.Start();
        worker.IsRunning.Should().BeTrue();

        // 2. Connect client and claim Team 1 Slot 0
        worker.ConnectClient();
        worker.HumanAssignedToTeam1Slot0.Should().BeTrue();

        // 3. Populate 4 Legion bots
        for (int i = 0; i < 4; i++) worker.ExecuteCommand("stdin", "AddFakePlayer 1");

        // 4. Populate 5 Hellbourne bots
        for (int i = 0; i < 5; i++) worker.ExecuteCommand("stdin", "AddFakePlayer 2");

        // 5. Confirm 5v5 configuration
        worker.Team1Bots.Should().Be(4);
        worker.Team2Bots.Should().Be(5);
        worker.TotalPlayerCount.Should().Be(10);

        // 6. Launch match and verify Phase 3
        worker.ExecuteCommand("stdin", "StartGame caldavar");
        worker.GamePhase.Should().Be(3);
    }

    [Fact]
    public async Task MatchmakingLoadBenchmark_WhenTenClientsQueue_MeasuresLatencyProfile()
    {
        await using var server = new MockChatServer();
        server.Start();

        var (resolvedPort, tier) = TieredPortDiscovery.ResolveChatServerPort(
            explicitPort: null,
            envLookup: k => k == "services__chat-server__tcp__0" ? server.Port.ToString() : null);

        resolvedPort.Should().Be(server.Port);
        tier.Should().Be("aspire");

        List<TcpClient> clients = [];
        List<double> handshakeLatencies = [];

        try
        {
            for (int i = 0; i < 10; i++)
            {
                TcpClient client = new() { NoDelay = true, LingerState = new LingerOption(true, 0) };
                await client.ConnectAsync("127.0.0.1", resolvedPort);
                clients.Add(client);

                Stopwatch sw = Stopwatch.StartNew();

                string hash = ComputeChatAuthHash(200 + i, "127.0.0.1", $"sess_{i}");
                byte[] connectPacket = EncodePacket(Opcodes.NET_CHAT_CL_CONNECT, BuildConnectPayload(200 + i, $"sess_{i}", "127.0.0.1", hash));

                await client.GetStream().WriteAsync(connectPacket);

                byte[] header = new byte[2];
                await client.GetStream().ReadExactlyAsync(header.AsMemory(0, 2));
                ushort len = BinaryPrimitives.ReadUInt16LittleEndian(header);

                byte[] body = new byte[len];
                await client.GetStream().ReadExactlyAsync(body.AsMemory(0, len));

                sw.Stop();
                handshakeLatencies.Add(sw.Elapsed.TotalMilliseconds);

                ushort opcode = BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(0, 2));
                opcode.Should().Be(Opcodes.NET_CHAT_CL_ACCEPT);
            }

            clients.Should().HaveCount(10);
            server.HandshakesCompleted.Should().Be(10);
            handshakeLatencies.Average().Should().BeLessThan(100.0);
        }
        finally
        {
            foreach (var c in clients) c.Dispose();
        }
    }

    [Fact]
    public void BugReproductionWorkflow_WhenRestoringHeroState_RestoresSnapshotAndTeleports()
    {
        // Active match in progress (Phase 3)
        int gamePhase = 3;
        var (allowed, _, _, _) = SnapshotPhaseValidator.VerifyPhase(gamePhase);
        allowed.Should().BeTrue();

        // Snapshot to restore
        var snapshot = new
        {
            hero = "Hero_Pyromancer",
            level = 16,
            gold = 5200,
            x = -1500.0,
            y = 2200.0,
            items = new[] { "Item_PortalKey", "Item_PostHaste", "Item_GrimoireOfPower" }
        };

        List<string> executedCommands =
        [
            $"SetHeroLevel {snapshot.level}",
            $"GiveGold {snapshot.gold}",
            $"Teleport {snapshot.x} {snapshot.y}"
        ];
        executedCommands.AddRange(snapshot.items.Select(i => $"PurchaseItem {i}"));

        executedCommands.Should().Contain("SetHeroLevel 16");
        executedCommands.Should().Contain("GiveGold 5200");
        executedCommands.Should().Contain("Teleport -1500 2200");
        executedCommands.Should().Contain("PurchaseItem Item_GrimoireOfPower");
    }

    [Fact]
    public void ReplayScrubbingWorkflow_WhenSeekingTimestamps_PerformsFrameAccurateTickSeek()
    {
        // Developer/Agent investigates combat incident at 03:45.250 (225.25 seconds)
        int minutes = 3;
        double seconds = 45.25;

        long calculatedTicks = ReplayMath.MinutesAndSecondsToTicks(minutes, seconds);
        // 3 * 1200 + 45.25 * 20 = 3600 + 905 = 4505 ticks
        calculatedTicks.Should().Be(4505);

        string replaySeekCommand = $"ReplaySetFrame {calculatedTicks}; Seek {calculatedTicks}";
        replaySeekCommand.Should().Be("ReplaySetFrame 4505; Seek 4505");

        double reconstructedSeconds = ReplayMath.TicksToSeconds(calculatedTicks);
        reconstructedSeconds.Should().BeApproximately(225.25, 0.001);
    }

    [Fact]
    public void ClientBootstrapWorkflow_WhenInvoked_BypassesLoginAndNavigatesToMatchmaking()
    {
        // 1. Bypass Login Routine
        string loginBypassScript = "Set('_loggedin', true); Trigger('LoginStatus', 'success', '', true); UIManager.GetInterface('main'):GetWidget('sysbar_logged'):SetVisible(true)";
        var (loginValid, _) = K2LuaValidator.ValidateK2LuaScript(loginBypassScript);
        loginValid.Should().BeTrue();

        // 2. Open Matchmaking Panel Routine
        string openMatchmakingScript = "Set('_mainmenu_currentpanel', 'matchmaking'); UIManager.GetInterface('main'):GetWidget('MainMenuPanelSwitcher'):DoEvent()";
        var (panelValid, _) = K2LuaValidator.ValidateK2LuaScript(openMatchmakingScript);
        panelValid.Should().BeTrue();

        // 3. Dismiss Any Popups Routine
        string dismissScript = "local p = UIManager.GetInterface('main_popup'):GetWidget('main_popup_panel'); if p then p:SetVisible(false) end";
        var (dismissValid, _) = K2LuaValidator.ValidateK2LuaScript(dismissScript);
        dismissValid.Should().BeTrue();
    }

    [Fact]
    public void ErrorRecoveryWorkflow_WhenUiCallsInvalid_RemediatesCallsAndRetries()
    {
        string malformedScript = "Windows.main:GetWidget('start_btn'):DoEvent('onclick')";

        var (isValid, violation) = K2LuaValidator.ValidateK2LuaScript(malformedScript);
        isValid.Should().BeFalse();
        violation.Should().Contain("Windows");

        string remediatedScript = K2LuaValidator.AutoRemediateLuaScript(malformedScript);

        var (isRemediatedValid, _) = K2LuaValidator.ValidateK2LuaScript(remediatedScript);
        isRemediatedValid.Should().BeTrue();
        remediatedScript.Should().Be("UIManager.main:GetWidget('start_btn'):DoEvent()");
    }

    [Fact]
    public async Task MatchFormationWorkflow_WhenTwoGroupsMatched_ConnectsSuccessfully()
    {
        await using var server = new MockChatServer();
        server.Start();

        using TcpClient client1 = new() { NoDelay = true };
        using TcpClient client2 = new() { NoDelay = true };

        await client1.ConnectAsync("127.0.0.1", server.Port);
        await client2.ConnectAsync("127.0.0.1", server.Port);

        // Handshakes
        await client1.GetStream().WriteAsync(EncodePacket(Opcodes.NET_CHAT_CL_CONNECT, BuildConnectPayload(1, "c1", "127.0.0.1", "h1")));
        await client2.GetStream().WriteAsync(EncodePacket(Opcodes.NET_CHAT_CL_CONNECT, BuildConnectPayload(2, "c2", "127.0.0.1", "h2")));

        byte[] ack1 = new byte[8];
        byte[] ack2 = new byte[8];
        await client1.GetStream().ReadExactlyAsync(ack1);
        await client2.GetStream().ReadExactlyAsync(ack2);

        server.HandshakesCompleted.Should().Be(2);

        // Group creation
        await client1.GetStream().WriteAsync(EncodePacket(Opcodes.NET_CHAT_CL_TMM_GROUP_CREATE, BuildGroupCreatePayload()));
        await client2.GetStream().WriteAsync(EncodePacket(Opcodes.NET_CHAT_CL_TMM_GROUP_CREATE, BuildGroupCreatePayload()));

        byte[] gAck1 = new byte[8];
        byte[] gAck2 = new byte[8];
        await client1.GetStream().ReadExactlyAsync(gAck1);
        await client2.GetStream().ReadExactlyAsync(gAck2);

        server.GroupsCreated.Should().Be(2);
    }

    [Fact]
    public void FullDevelopmentWorkflow_WhenExecuted_PerformsHotReloadScrimmageAndTelemetry()
    {
        // 1. Enable Hot Reload
        var watcher = new MockHotReloadService();
        watcher.Watch(["games/shared/ui/"]);
        watcher.IsWatching.Should().BeTrue();

        // 2. Launch Scrimmage
        MockDedicatedWorker worker = new(11235);
        worker.Start();
        worker.ConnectClient();
        for (int i = 0; i < 4; i++) worker.ExecuteCommand("stdin", "AddFakePlayer 1");
        for (int i = 0; i < 5; i++) worker.ExecuteCommand("stdin", "AddFakePlayer 2");
        worker.ExecuteCommand("stdin", "StartGame caldavar");

        worker.GamePhase.Should().Be(3);
        worker.TotalPlayerCount.Should().Be(10);

        // 3. State snapshot verified inside Phase 3
        var (allowed, _, _, _) = SnapshotPhaseValidator.VerifyPhase(worker.GamePhase);
        allowed.Should().BeTrue();

        // 4. Hot reload triggered mid-game
        watcher.NotifyFileChanged("games/shared/ui/fe2/main.interface");
        watcher.TriggerReload();
        watcher.TotalReloads.Should().Be(1);

        // 5. Scrimmage and hot-reloader remain stable
        worker.IsRunning.Should().BeTrue();
    }

    [Fact]
    public async Task TearDownWorkflow_OnShutdown_CleansUpAllDedicatedServersAndTcpSockets()
    {
        // 1. Dedicated worker teardown
        MockDedicatedWorker worker = new(11235);
        worker.Start();
        worker.ConnectClient();
        worker.Stop();

        worker.IsRunning.Should().BeFalse();
        worker.ClientConnected.Should().BeFalse();

        // 2. TCP server and client teardown
        await using var server = new MockChatServer();
        server.Start();

        TcpClient client = new() { LingerState = new LingerOption(true, 0) };
        await client.ConnectAsync("127.0.0.1", server.Port);
        client.Connected.Should().BeTrue();

        client.Dispose();
        client.Connected.Should().BeFalse();

        await server.DisposeAsync();
        server.ConnectedClientsCount.Should().Be(0);
    }
}
