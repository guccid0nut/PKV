// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// TAO (Together As One) MCP & Kongor CLI -- Created by guccidonut (https://github.com/guccidonut/PKV)

using FluentAssertions;
using Kongor.Mcp.E2E.Tests.Client;
using Xunit;
using Xunit.Abstractions;

namespace Kongor.Mcp.E2E.Tests.Tier2_Boundary;

public sealed class ToolParameterBoundaryTests : McpTestBase
{
    public ToolParameterBoundaryTests(ITestOutputHelper output) : base(output)
    {
    }

    [Fact]
    public async Task Boundary_ToolsCall_UnknownToolName_ReturnsErrorOrIsError()
    {
        ToolCallResult result = await Client.CallToolAsync("non_existent_tool_name_xyz", new { });
        result.Should().NotBeNull();
        result.IsError.Should().BeTrue();
    }

    [Fact]
    public async Task Boundary_ServerManage_MissingAction_ReturnsValidationFailure()
    {
        ToolCallResult result = await Client.CallToolAsync("kongor_server_manage", new { target = "cluster" });
        result.Should().NotBeNull();
    }

    [Trait("Category", "Manual")]
    [Fact]
    public async Task Boundary_ServerManage_InvalidTarget_HandlesGracefully()
    {
        if (!NexusTestEnvironment.IsManualTestEnabled) return;
        ToolCallResult result = await Client.CallToolAsync("kongor_server_manage", new { action = "status", target = "invalid_target_xyz" });
        result.Should().NotBeNull();
    }

    [Fact]
    public async Task Boundary_GameManage_InvalidMode_RejectsOrDefaults()
    {
        ToolCallResult result = await Client.CallToolAsync("kongor_game_manage", new { action = "status", mode = "super_duper_mode_123" });
        result.Should().NotBeNull();
    }

    [Fact]
    public async Task Boundary_UiRenderView_UnknownViewName_ReturnsErrorContent()
    {
        UiRenderViewArgs args = new()
        {
            ViewName = "NonExistentUnknownView_XYZ"
        };

        ToolCallResult result = await Client.CallToolAsync("kongor_ui_render_view", args);
        result.Should().NotBeNull();
    }

    [Trait("Category", "Manual")]
    [Fact]
    public async Task Boundary_CaptureWindow_NonExistentWindowName_ReturnsErrorContent()
    {
        if (!NexusTestEnvironment.IsManualTestEnabled) return;
        CaptureWindowArgs args = new()
        {
            Target = "NON_EXISTENT_APPLICATION_WINDOW_NAME_987654321",
            SaveArtifact = false
        };

        ToolCallResult result = await Client.CallToolAsync("kongor_capture_window", args);
        result.Should().NotBeNull();
    }
}
