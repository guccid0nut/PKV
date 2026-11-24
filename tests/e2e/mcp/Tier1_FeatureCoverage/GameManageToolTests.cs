// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// TAO (Together As One) MCP & Kongor CLI -- Created by guccidonut (https://github.com/guccidonut/PKV)

using FluentAssertions;
using Kongor.Mcp.E2E.Tests.Client;
using Xunit;
using Xunit.Abstractions;

namespace Kongor.Mcp.E2E.Tests.Tier1_FeatureCoverage;

public sealed class GameManageToolTests : McpTestBase
{
    public GameManageToolTests(ITestOutputHelper output) : base(output)
    {
    }

    [Trait("Category", "Manual")]
    [Fact]
    public async Task GameManage_Status_ReturnsCurrentClientState()
    {
        if (!NexusTestEnvironment.IsManualTestEnabled) return;
        GameManageArgs args = new()
        {
            Action = "status"
        };

        ToolCallResult callResult = await Client.CallToolAsync("kongor_game_manage", args);
        callResult.Should().NotBeNull();
        callResult.IsError.Should().BeFalse();

        GameManageResult? result = await Client.CallToolTypedAsync<GameManageResult>("kongor_game_manage", args);
        result.Should().NotBeNull();
        result!.Status.Should().Be("success");
        result.Action.Should().Be("status");
    }

    [Trait("Category", "Manual")]
    [Fact]
    public async Task GameManage_LaunchPracticeMode_ReturnsProcessIdAndArguments()
    {
        if (!NexusTestEnvironment.IsManualTestEnabled) return;
        GameManageArgs args = new()
        {
            Action = "launch",
            Mode = "practice",
            Resolution = "1280x720",
            Fullscreen = false
        };

        GameManageResult? result = await Client.CallToolTypedAsync<GameManageResult>("kongor_game_manage", args);
        result.Should().NotBeNull();
        result!.Status.Should().BeOneOf("success", "error");
        result.Action.Should().Be("launch");

        // Cleanup
        await Client.CallToolAsync("kongor_game_manage", new GameManageArgs { Action = "stop" });
    }

    [Trait("Category", "Manual")]
    [Fact]
    public async Task GameManage_LaunchBotMatchMode_ConfiguresBotMatchArgs()
    {
        if (!NexusTestEnvironment.IsManualTestEnabled) return;
        GameManageArgs args = new()
        {
            Action = "launch",
            Mode = "bot_match",
            Resolution = "1920x1080",
            Fullscreen = false
        };

        GameManageResult? result = await Client.CallToolTypedAsync<GameManageResult>("kongor_game_manage", args);
        result.Should().NotBeNull();
        result!.Action.Should().Be("launch");

        // Cleanup
        await Client.CallToolAsync("kongor_game_manage", new GameManageArgs { Action = "stop" });
    }

    [Trait("Category", "Manual")]
    [Fact]
    public async Task GameManage_LaunchWithCustomResolution_AppliesResolution()
    {
        if (!NexusTestEnvironment.IsManualTestEnabled) return;
        GameManageArgs args = new()
        {
            Action = "launch",
            Mode = "practice",
            Resolution = "1600x900",
            Fullscreen = false
        };

        GameManageResult? result = await Client.CallToolTypedAsync<GameManageResult>("kongor_game_manage", args);
        result.Should().NotBeNull();
        result!.Action.Should().Be("launch");

        // Cleanup
        await Client.CallToolAsync("kongor_game_manage", new GameManageArgs { Action = "stop" });
    }

    [Trait("Category", "Manual")]
    [Fact]
    public async Task GameManage_LaunchWithConnectAddress_ConfiguresNetArgs()
    {
        if (!NexusTestEnvironment.IsManualTestEnabled) return;
        GameManageArgs args = new()
        {
            Action = "launch",
            Mode = "normal",
            ConnectAddress = "127.0.0.1:11235",
            Fullscreen = false
        };

        GameManageResult? result = await Client.CallToolTypedAsync<GameManageResult>("kongor_game_manage", args);
        result.Should().NotBeNull();
        result!.Action.Should().Be("launch");

        // Cleanup
        await Client.CallToolAsync("kongor_game_manage", new GameManageArgs { Action = "stop" });
    }

    [Fact]
    public async Task GameManage_Stop_TerminatesClientProcess()
    {
        GameManageArgs stopArgs = new()
        {
            Action = "stop"
        };

        GameManageResult? result = await Client.CallToolTypedAsync<GameManageResult>("kongor_game_manage", stopArgs);
        result.Should().NotBeNull();
        result!.Running.Should().BeFalse();
    }
}
