// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// TAO (Together As One) MCP & Kongor CLI -- Created by guccidonut (https://github.com/guccidonut/PKV)

using FluentAssertions;
using Kongor.Mcp.E2E.Tests.Client;
using Xunit;
using Xunit.Abstractions;

namespace Kongor.Mcp.E2E.Tests.Tier4_Scenarios;

public sealed class AutonomousMatchStartupJourneyTests : McpTestBase
{
    public AutonomousMatchStartupJourneyTests(ITestOutputHelper output) : base(output)
    {
    }

    [Trait("Category", "Manual")]
    [Fact]
    public async Task AutonomousAgent_MatchStartupAndMonitoringJourney_EndToEnd()
    {
        if (!NexusTestEnvironment.IsManualTestEnabled) return;
        Output.WriteLine("[Scenario A] Step 1: Agent checks microservice cluster readiness");
        ServerManageResult? clusterStatus = await Client.CallToolTypedAsync<ServerManageResult>(
            "kongor_server_manage",
            new ServerManageArgs { Action = "status", Target = "cluster" });
        clusterStatus.Should().NotBeNull();
        clusterStatus!.Status.Should().Be("success");

        Output.WriteLine("[Scenario A] Step 2: Agent launches WILLOWMAKER game client in practice mode");
        GameManageResult? launchResult = await Client.CallToolTypedAsync<GameManageResult>(
            "kongor_game_manage",
            new GameManageArgs
            {
                Action = "launch",
                Mode = "practice",
                Resolution = "1920x1080",
                Fullscreen = false
            });
        launchResult.Should().NotBeNull();

        Output.WriteLine("[Scenario A] Step 3: Agent dispatches engine setup cvars via IPC");
        GameCommandResult? cvarResult = await Client.CallToolTypedAsync<GameCommandResult>(
            "kongor_game_command",
            new GameCommandArgs { Command = "Set cl_fov 95", TimeoutMs = 3000 });
        cvarResult.Should().NotBeNull();

        Output.WriteLine("[Scenario A] Step 4: Agent polls engine telemetry (FPS, Memory, Map)");
        GameTelemetryResult? telemetryResult = await Client.CallToolTypedAsync<GameTelemetryResult>(
            "kongor_game_telemetry",
            new GameTelemetryArgs { TimeoutMs = 3000 });
        telemetryResult.Should().NotBeNull();

        Output.WriteLine("[Scenario A] Step 5: Agent captures running game window visual verification");
        CaptureWindowResult? captureResult = await Client.CallToolTypedAsync<CaptureWindowResult>(
            "kongor_capture_window",
            new CaptureWindowArgs { Target = "active", SaveArtifact = false });
        captureResult.Should().NotBeNull();

        Output.WriteLine("[Scenario A] Step 6: Agent cleanly terminates game client");
        GameManageResult? stopResult = await Client.CallToolTypedAsync<GameManageResult>(
            "kongor_game_manage",
            new GameManageArgs { Action = "stop" });
        stopResult.Should().NotBeNull();
        stopResult!.Running.Should().BeFalse();
    }

    [Trait("Category", "Manual")]
    [Fact]
    public async Task AutonomousAgent_MultiPlayerLobby_ConfiguresScenarioSuccessfully()
    {
        if (!NexusTestEnvironment.IsManualTestEnabled) return;
        // Configure bot match lobby
        GameManageResult? launchRes = await Client.CallToolTypedAsync<GameManageResult>(
            "kongor_game_manage",
            new GameManageArgs { Action = "launch", Mode = "bot_match", Resolution = "1280x720" });
        launchRes.Should().NotBeNull();

        // Send HostStartGame command
        GameCommandResult? cmdRes = await Client.CallToolTypedAsync<GameCommandResult>(
            "kongor_game_command",
            new GameCommandArgs { Command = "HostStartGame", TimeoutMs = 2000 });
        cmdRes.Should().NotBeNull();

        // Read dynamic telemetry
        GameTelemetryResult? telemRes = await Client.CallToolTypedAsync<GameTelemetryResult>(
            "kongor_game_telemetry",
            new GameTelemetryArgs { TimeoutMs = 2000 });
        telemRes.Should().NotBeNull();

        // Clean teardown
        await Client.CallToolAsync("kongor_game_manage", new GameManageArgs { Action = "stop" });
    }

    [Trait("Category", "Manual")]
    [Fact]
    public async Task ClusterAndClient_UserRequested_ExecutesEndToEndJourney()
    {
        if (!NexusTestEnvironment.IsManualTestEnabled) return;

        // 1. AppEval with dynamic Domain model
        var evalRes = await Client.CallToolAsync(
            "kongor_app_eval",
            new { script = "var acc = new NEXUS.Contracts.Domain.Identifiers.AccountId(42); return $\"AccountId: {acc.Value}\";" });
        evalRes.Should().NotBeNull();
        evalRes.IsError.Should().BeFalse();
        evalRes.GetFirstText().Should().Contain("AccountId: 42");

        // 2. Start Aspire cluster
        ServerManageResult? clusterStart = await Client.CallToolTypedAsync<ServerManageResult>(
            "kongor_server_manage",
            new ServerManageArgs { Action = "start", Target = "cluster" });
        clusterStart.Should().NotBeNull();

        // 3. Launch game client
        var clientLaunchRes = await Client.CallToolAsync(
            "kongor_game_client_manage",
            new { action = "launch" });
        clientLaunchRes.Should().NotBeNull();
        clientLaunchRes.IsError.Should().BeFalse();

        // 4. UI Control status/inspect check
        var uiStatusRes = await Client.CallToolAsync(
            "kongor_ui_control",
            new { action = "status", target = "foreground" });
        uiStatusRes.Should().NotBeNull();

        // 5. Game telemetry
        var telemetryRes = await Client.CallToolAsync(
            "kongor_game_telemetry",
            new { timeoutMs = 2000 });
        telemetryRes.Should().NotBeNull();

        // 6. Game command
        var cmdRes = await Client.CallToolAsync(
            "kongor_game_command",
            new { command = "echo Kongor_Cluster_Verified", autoLaunch = false, timeoutMs = 3000 });
        cmdRes.Should().NotBeNull();
        cmdRes.IsError.Should().BeFalse();

        // 7. Teardown client and cluster
        await Client.CallToolAsync("kongor_game_client_manage", new { action = "stop" });
        await Client.CallToolAsync("kongor_server_manage", new ServerManageArgs { Action = "stop", Target = "cluster" });
    }
}
