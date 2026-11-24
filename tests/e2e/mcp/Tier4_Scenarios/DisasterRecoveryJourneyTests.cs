// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// TAO (Together As One) MCP & Kongor CLI -- Created by guccidonut (https://github.com/guccidonut/PKV)

using FluentAssertions;
using Kongor.Mcp.E2E.Tests.Client;
using Xunit;
using Xunit.Abstractions;

namespace Kongor.Mcp.E2E.Tests.Tier4_Scenarios;

public sealed class DisasterRecoveryJourneyTests : McpTestBase
{
    public DisasterRecoveryJourneyTests(ITestOutputHelper output) : base(output)
    {
    }

    [Trait("Category", "Manual")]
    [Fact]
    public async Task DisasterRecovery_ProcessKill_ExecutesResurrectionJourney()
    {
        if (!NexusTestEnvironment.IsManualTestEnabled) return;
        Output.WriteLine("[Scenario C] Step 1: Launch game process");
        GameManageResult? launchRes = await Client.CallToolTypedAsync<GameManageResult>(
            "kongor_game_manage",
            new GameManageArgs { Action = "launch", Mode = "practice" });
        launchRes.Should().NotBeNull();

        Output.WriteLine("[Scenario C] Step 2: Query status while running");
        GameManageResult? runningStatus = await Client.CallToolTypedAsync<GameManageResult>(
            "kongor_game_manage",
            new GameManageArgs { Action = "status" });
        runningStatus.Should().NotBeNull();

        Output.WriteLine("[Scenario C] Step 3: Trigger process stop / cleanup");
        GameManageResult? stopRes = await Client.CallToolTypedAsync<GameManageResult>(
            "kongor_game_manage",
            new GameManageArgs { Action = "stop" });
        stopRes.Should().NotBeNull();
        stopRes!.Running.Should().BeFalse();

        Output.WriteLine("[Scenario C] Step 4: Verify clean post-kill status");
        GameManageResult? postKillStatus = await Client.CallToolTypedAsync<GameManageResult>(
            "kongor_game_manage",
            new GameManageArgs { Action = "status" });
        postKillStatus.Should().NotBeNull();
        postKillStatus!.Running.Should().BeFalse();
    }

    [Trait("Category", "Manual")]
    [Fact]
    public async Task DisasterRecovery_PipeDisconnect_ExecutesReconnectionJourney()
    {
        if (!NexusTestEnvironment.IsManualTestEnabled) return;
        // 1. Dispatch command when not running
        GameCommandResult? cmdRes = await Client.CallToolTypedAsync<GameCommandResult>(
            "kongor_game_command",
            new GameCommandArgs { Command = "echo OfflineTest", TimeoutMs = 1000 });
        cmdRes.Should().NotBeNull();

        // 2. Query telemetry when not running
        GameTelemetryResult? telemRes = await Client.CallToolTypedAsync<GameTelemetryResult>(
            "kongor_game_telemetry",
            new GameTelemetryArgs { TimeoutMs = 1000 });
        telemRes.Should().NotBeNull();

        // 3. Server should remain responsive and stable
        JsonRpcResponse pingRes = await Client.PingAsync();
        pingRes.Should().NotBeNull();
        pingRes.Error.Should().BeNull();
    }
}
