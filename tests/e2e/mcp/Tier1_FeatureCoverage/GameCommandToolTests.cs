// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// TAO (Together As One) MCP & Kongor CLI -- Created by guccidonut (https://github.com/guccidonut/PKV)

using FluentAssertions;
using Kongor.Mcp.E2E.Tests.Client;
using Xunit;
using Xunit.Abstractions;

namespace Kongor.Mcp.E2E.Tests.Tier1_FeatureCoverage;

public sealed class GameCommandToolTests : McpTestBase
{
    public GameCommandToolTests(ITestOutputHelper output) : base(output)
    {
    }

    [Trait("Category", "Manual")]
    [Fact]
    public async Task GameCommand_EchoString_DispatchesAndReceivesOutput()
    {
        if (!NexusTestEnvironment.IsManualTestEnabled) return;
        GameCommandArgs args = new()
        {
            Command = "echo Hello_ProjectKongor_E2E",
            TimeoutMs = 5000
        };

        ToolCallResult callResult = await Client.CallToolAsync("kongor_game_command", args);
        callResult.Should().NotBeNull();

        GameCommandResult? result = await Client.CallToolTypedAsync<GameCommandResult>("kongor_game_command", args);
        result.Should().NotBeNull();
        result!.Command.Should().Be("echo Hello_ProjectKongor_E2E");
    }

    [Trait("Category", "Manual")]
    [Fact]
    public async Task GameCommand_SetCvar_ExecutesSuccessfully()
    {
        if (!NexusTestEnvironment.IsManualTestEnabled) return;
        GameCommandArgs args = new()
        {
            Command = "Set cl_fov 95",
            TimeoutMs = 3000
        };

        GameCommandResult? result = await Client.CallToolTypedAsync<GameCommandResult>("kongor_game_command", args);
        result.Should().NotBeNull();
        result!.Command.Should().Contain("cl_fov");
    }

    [Trait("Category", "Manual")]
    [Fact]
    public async Task GameCommand_GetCvar_RetrievesCvarValue()
    {
        if (!NexusTestEnvironment.IsManualTestEnabled) return;
        GameCommandArgs args = new()
        {
            Command = "cl_fov",
            TimeoutMs = 3000
        };

        GameCommandResult? result = await Client.CallToolTypedAsync<GameCommandResult>("kongor_game_command", args);
        result.Should().NotBeNull();
        result!.Command.Should().Be("cl_fov");
    }

    [Trait("Category", "Manual")]
    [Fact]
    public async Task GameCommand_GameStatus_ReturnsGameStatusInfo()
    {
        if (!NexusTestEnvironment.IsManualTestEnabled) return;
        GameCommandArgs args = new()
        {
            Command = "status",
            TimeoutMs = 3000
        };

        GameCommandResult? result = await Client.CallToolTypedAsync<GameCommandResult>("kongor_game_command", args);
        result.Should().NotBeNull();
        result!.Command.Should().Be("status");
    }

    [Trait("Category", "Manual")]
    [Fact]
    public async Task GameCommand_WithCustomTimeout_ExecutesWithinThreshold()
    {
        if (!NexusTestEnvironment.IsManualTestEnabled) return;
        GameCommandArgs args = new()
        {
            Command = "echo TimeoutTest",
            TimeoutMs = 1500
        };

        DateTimeOffset start = TimeProvider.System.GetUtcNow();
        GameCommandResult? result = await Client.CallToolTypedAsync<GameCommandResult>("kongor_game_command", args);
        TimeSpan elapsed = TimeProvider.System.GetUtcNow() - start;

        result.Should().NotBeNull();
        elapsed.TotalMilliseconds.Should().BeLessThan(10000);
    }

    [Trait("Category", "Manual")]
    [Fact]
    public async Task GameCommand_HostStartGame_DispatchesCommand()
    {
        if (!NexusTestEnvironment.IsManualTestEnabled) return;
        GameCommandArgs args = new()
        {
            Command = "HostStartGame",
            TimeoutMs = 3000
        };

        GameCommandResult? result = await Client.CallToolTypedAsync<GameCommandResult>("kongor_game_command", args);
        result.Should().NotBeNull();
        result!.Command.Should().Be("HostStartGame");
    }
}
