// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// TAO (Together As One) MCP & Kongor CLI -- Created by guccidonut (https://github.com/guccidonut/PKV)

using FluentAssertions;
using Kongor.Mcp.E2E.Tests.Client;
using Xunit;
using Xunit.Abstractions;

namespace Kongor.Mcp.E2E.Tests.Tier3_Combinations;

public sealed class GameLaunchToIpcControlTests : McpTestBase
{
    public GameLaunchToIpcControlTests(ITestOutputHelper output) : base(output)
    {
    }

    [Trait("Category", "Manual")]
    [Fact]
    public async Task Combination_GameLaunch_ThenDispatchCvar_ThenReadTelemetry_Workflow()
    {
        if (!NexusTestEnvironment.IsManualTestEnabled) return;
        // 1. Launch game
        await Client.CallToolAsync("kongor_game_manage", new GameManageArgs { Action = "launch", Mode = "practice", Fullscreen = false });

        // 2. Dispatch in-game command
        GameCommandResult? cmdResult = await Client.CallToolTypedAsync<GameCommandResult>(
            "kongor_game_command",
            new GameCommandArgs { Command = "Set cl_fov 95", TimeoutMs = 3000 });
        cmdResult.Should().NotBeNull();

        // 3. Read telemetry snapshot
        GameTelemetryResult? telemetry = await Client.CallToolTypedAsync<GameTelemetryResult>(
            "kongor_game_telemetry",
            new GameTelemetryArgs { TimeoutMs = 3000 });
        telemetry.Should().NotBeNull();

        // 4. Cleanup
        await Client.CallToolAsync("kongor_game_manage", new GameManageArgs { Action = "stop" });
    }

    [Trait("Category", "Manual")]
    [Fact]
    public async Task Combination_GameLaunch_DispatchesMultipleCommandsInSequence()
    {
        if (!NexusTestEnvironment.IsManualTestEnabled) return;
        string[] commands = ["echo Command1", "echo Command2", "echo Command3"];
        foreach (string cmd in commands)
        {
            GameCommandResult? res = await Client.CallToolTypedAsync<GameCommandResult>(
                "kongor_game_command",
                new GameCommandArgs { Command = cmd, TimeoutMs = 2000 });
            res.Should().NotBeNull();
            res!.Command.Should().Be(cmd);
        }
    }

    [Trait("Category", "Manual")]
    [Fact]
    public async Task Combination_GameTelemetryPolling_ReflectsDynamicEngineState()
    {
        if (!NexusTestEnvironment.IsManualTestEnabled) return;
        for (int i = 0; i < 3; i++)
        {
            GameTelemetryResult? res = await Client.CallToolTypedAsync<GameTelemetryResult>(
                "kongor_game_telemetry",
                new GameTelemetryArgs { TimeoutMs = 2000 });
            res.Should().NotBeNull();
            await Task.Delay(50);
        }
    }
}
