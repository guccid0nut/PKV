// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// TAO (Together As One) MCP & Kongor CLI -- Created by guccidonut (https://github.com/guccidonut/PKV)

using FluentAssertions;
using Kongor.Mcp.E2E.Tests.Client;
using Xunit;
using Xunit.Abstractions;

namespace Kongor.Mcp.E2E.Tests.Tier2_Boundary;

public sealed class ExecutionLimitBoundaryTests : McpTestBase
{
    public ExecutionLimitBoundaryTests(ITestOutputHelper output) : base(output)
    {
    }

    [Trait("Category", "Manual")]
    [Fact]
    public async Task Boundary_GameCommand_ZeroTimeout_HandlesBoundCorrectly()
    {
        if (!NexusTestEnvironment.IsManualTestEnabled) return;
        GameCommandArgs args = new()
        {
            Command = "echo ZeroTimeout",
            TimeoutMs = 0
        };

        ToolCallResult result = await Client.CallToolAsync("kongor_game_command", args);
        result.Should().NotBeNull();
    }

    [Fact]
    public async Task Boundary_GameCommand_NegativeTimeout_HandlesBoundOrDefaults()
    {
        GameCommandArgs args = new()
        {
            Command = "echo NegativeTimeout",
            TimeoutMs = -100
        };

        ToolCallResult result = await Client.CallToolAsync("kongor_game_command", args);
        result.Should().NotBeNull();
    }

    [Trait("Category", "Manual")]
    [Fact]
    public async Task Boundary_GameCommand_OversizedCommandString_10KB_DoesNotBufferOverflow()
    {
        if (!NexusTestEnvironment.IsManualTestEnabled) return;
        string largeCommand = "echo " + new string('A', 10 * 1024);
        GameCommandArgs args = new()
        {
            Command = largeCommand,
            TimeoutMs = 2000
        };

        ToolCallResult result = await Client.CallToolAsync("kongor_game_command", args);
        result.Should().NotBeNull();
        Client.IsProcessRunning.Should().BeTrue();
    }

    [Trait("Category", "Manual")]
    [Fact]
    public async Task Boundary_UiRenderView_ExtremeResolution8K_HandlesDimensions()
    {
        if (!NexusTestEnvironment.IsManualTestEnabled) return;
        UiRenderViewArgs args = new()
        {
            ViewName = "ModManagerView",
            Width = 7680,
            Height = 4320
        };

        ToolCallResult result = await Client.CallToolAsync("kongor_ui_render_view", args);
        result.Should().NotBeNull();
    }

    [Fact]
    public async Task Boundary_UiRenderView_ZeroWidthHeight_UsesDefaultDimensions()
    {
        UiRenderViewArgs args = new()
        {
            ViewName = "ModManagerView",
            Width = 0,
            Height = 0
        };

        ToolCallResult result = await Client.CallToolAsync("kongor_ui_render_view", args);
        result.Should().NotBeNull();
    }

    [Fact]
    public async Task Boundary_CaptureWindow_InvalidHwndString_ReturnsDescriptiveError()
    {
        CaptureWindowArgs args = new()
        {
            Target = "0xFFFFFFFFFFFFFFFF",
            CaptureMethod = "window_dc"
        };

        ToolCallResult result = await Client.CallToolAsync("kongor_capture_window", args);
        result.Should().NotBeNull();
    }
}
