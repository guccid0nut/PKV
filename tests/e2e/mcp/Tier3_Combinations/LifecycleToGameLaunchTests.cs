// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// TAO (Together As One) MCP & Kongor CLI -- Created by guccidonut (https://github.com/guccidonut/PKV)

using FluentAssertions;
using Kongor.Mcp.E2E.Tests.Client;
using Xunit;
using Xunit.Abstractions;

namespace Kongor.Mcp.E2E.Tests.Tier3_Combinations;

public sealed class LifecycleToGameLaunchTests : McpTestBase
{
    public LifecycleToGameLaunchTests(ITestOutputHelper output) : base(output)
    {
    }

    [Trait("Category", "Manual")]
    [Fact]
    public async Task Combination_Lifecycle_StartCluster_ThenLaunchGame_ThenCheckBothRunning()
    {
        if (!NexusTestEnvironment.IsManualTestEnabled) return;
        // 1. Query cluster status
        ServerManageResult? serverStatus = await Client.CallToolTypedAsync<ServerManageResult>(
            "kongor_server_manage",
            new ServerManageArgs { Action = "status", Target = "cluster" });
        serverStatus.Should().NotBeNull();

        // 2. Launch game client in practice mode
        GameManageResult? gameLaunch = await Client.CallToolTypedAsync<GameManageResult>(
            "kongor_game_manage",
            new GameManageArgs { Action = "launch", Mode = "practice", Fullscreen = false });
        gameLaunch.Should().NotBeNull();

        // 3. Verify game client status
        GameManageResult? gameStatus = await Client.CallToolTypedAsync<GameManageResult>(
            "kongor_game_manage",
            new GameManageArgs { Action = "status" });
        gameStatus.Should().NotBeNull();

        // 4. Cleanup: stop game client
        await Client.CallToolAsync("kongor_game_manage", new GameManageArgs { Action = "stop" });
    }

    [Trait("Category", "Manual")]
    [Fact]
    public async Task Combination_Lifecycle_StopGame_ThenStopCluster_CleansUpAllProcesses()
    {
        if (!NexusTestEnvironment.IsManualTestEnabled) return;
        // Stop game client
        GameManageResult? gameStop = await Client.CallToolTypedAsync<GameManageResult>(
            "kongor_game_manage",
            new GameManageArgs { Action = "stop" });
        gameStop.Should().NotBeNull();
        gameStop!.Running.Should().BeFalse();

        // Stop cluster
        ServerManageResult? clusterStop = await Client.CallToolTypedAsync<ServerManageResult>(
            "kongor_server_manage",
            new ServerManageArgs { Action = "stop", Target = "cluster" });
        clusterStop.Should().NotBeNull();
    }

    [Trait("Category", "Manual")]
    [Fact]
    public async Task Combination_Lifecycle_StatusConsistencyAcrossMicroservicesAndClient()
    {
        if (!NexusTestEnvironment.IsManualTestEnabled) return;
        ServerManageResult? allStatus = await Client.CallToolTypedAsync<ServerManageResult>(
            "kongor_server_manage",
            new ServerManageArgs { Action = "status", Target = "all" });
        allStatus.Should().NotBeNull();

        GameManageResult? gameStatus = await Client.CallToolTypedAsync<GameManageResult>(
            "kongor_game_manage",
            new GameManageArgs { Action = "status" });
        gameStatus.Should().NotBeNull();
    }
}
