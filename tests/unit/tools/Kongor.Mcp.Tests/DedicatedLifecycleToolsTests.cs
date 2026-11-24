// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// TAO (Together As One) MCP & Kongor CLI -- Created by guccidonut (https://github.com/guccidonut/PKV)

using System.Text.Json;
using FluentAssertions;
using Kongor.Cli.Mcp.Handlers;
using Kongor.Cli.Mcp.Protocol;
using Xunit;

namespace Kongor.Mcp.Tests;

public sealed class DedicatedLifecycleToolsTests
{
    private static JsonElement ParseJson(string json)
    {
        return JsonElement.Parse(json);
    }

    [Fact]
    public void McpToolRegistry_WhenInvoked_RegistersAllFourDedicatedToolsAndAliases()
    {
        McpToolRegistry registry = new(registerDefaults: true);

        // Verify Player Game Client tool and alias
        registry.HasTool("kongor_game_client_manage").Should().BeTrue();
        registry.HasTool("kongor_game_client_manager").Should().BeTrue();

        // Verify Desktop Launcher tool and alias
        registry.HasTool("kongor_willowmaker_manage").Should().BeTrue();
        registry.HasTool("kongor_willowmaker_manager").Should().BeTrue();

        // Verify COMPEL Supervisor tool and alias
        registry.HasTool("kongor_compel_manage").Should().BeTrue();
        registry.HasTool("kongor_compel_manager").Should().BeTrue();

        // Verify Dedicated Game Server tool and alias
        registry.HasTool("kongor_game_server_manage").Should().BeTrue();
        registry.HasTool("kongor_game_server_manager").Should().BeTrue();

        // Verify Aspire cluster manage tool
        registry.HasTool("kongor_server_manage").Should().BeTrue();

        // Verify Backwards-compatible Game Manage router
        registry.HasTool("kongor_game_manage").Should().BeTrue();
    }

    [Fact]
    public async Task GameClientManage_StatusAction_ReturnsValidTelemetryAndState()
    {
        GameClientManageHandler handler = new();
        JsonElement args = ParseJson("{\"action\": \"status\"}");

        ToolCallResult result = await handler.HandleAsync(args);

        result.IsError.Should().BeFalse();
        string text = ((TextContentItem)result.Content[0]).Text;
        text.Should().Contain("\"action\": \"status\"");
        text.Should().Contain("\"running\":");
        text.Should().Contain("\"processId\":");
        text.Should().Contain("\"mainWindowHandle\":");
        text.Should().Contain("\"timestamp\":");
    }

    [Fact]
    public async Task GameClientManage_StopAction_ReturnsSuccessWhenNoProcess()
    {
        GameClientManageHandler handler = new();
        JsonElement args = ParseJson("{\"action\": \"stop\"}");

        ToolCallResult result = await handler.HandleAsync(args);

        result.IsError.Should().BeFalse();
        string text = ((TextContentItem)result.Content[0]).Text;
        text.Should().Contain("\"action\": \"stop\"");
        text.Should().Contain("\"running\": false");
    }

    [Fact]
    public async Task WillowmakerManage_StatusAction_ReturnsValidState()
    {
        WillowmakerManageHandler handler = new();
        JsonElement args = ParseJson("{\"action\": \"status\"}");

        ToolCallResult result = await handler.HandleAsync(args);

        result.IsError.Should().BeFalse();
        string text = ((TextContentItem)result.Content[0]).Text;
        text.Should().Contain("\"action\": \"status\"");
        text.Should().Contain("\"running\":");
        text.Should().Contain("\"processId\":");
        text.Should().Contain("\"mainWindowHandle\":");
    }

    [Trait("Category", "Manual")]
    [Fact]
    public async Task CompelManage_StatusAction_QueriesNodeHealth()
    {
        if (!NexusTestEnvironment.IsManualTestEnabled) return;
        CompelManageHandler handler = new();
        JsonElement args = ParseJson("{\"action\": \"status\"}");

        ToolCallResult result = await handler.HandleAsync(args);

        result.IsError.Should().BeFalse();
        string text = ((TextContentItem)result.Content[0]).Text;
        text.Should().Contain("\"action\": \"status\"");
        text.Should().Contain("\"host\": \"http://127.0.0.1:11234\"");
        text.Should().Contain("\"ports\":");
        text.Should().Contain("\"controlPlane\": 11234");
    }

    [Fact]
    public async Task GameServerManage_StatusAction_ReturnsWorkerInventory()
    {
        GameServerManageHandler handler = new();
        JsonElement args = ParseJson("{\"action\": \"status\"}");

        ToolCallResult result = await handler.HandleAsync(args);

        result.IsError.Should().BeFalse();
        string text = ((TextContentItem)result.Content[0]).Text;
        text.Should().Contain("\"action\": \"status\"");
        text.Should().Contain("\"totalWorkers\":");
        text.Should().Contain("\"workers\":");
    }

    [Fact]
    public async Task GameManageHandler_OnConstruction_RoutesToGameClientByDefault()
    {
        GameManageHandler handler = new();
        JsonElement args = ParseJson("{\"action\": \"status\"}");

        ToolCallResult result = await handler.HandleAsync(args);

        result.IsError.Should().BeFalse();
        string text = ((TextContentItem)result.Content[0]).Text;
        text.Should().Contain("\"action\": \"status\"");
        text.Should().Contain("\"running\":");
    }

    [Fact]
    public async Task GameManageHandler_WhenInvoked_RoutesToWillowmakerWhenTargetIsLauncher()
    {
        GameManageHandler handler = new();
        JsonElement args = ParseJson("{\"action\": \"status\", \"target\": \"willowmaker\"}");

        ToolCallResult result = await handler.HandleAsync(args);

        result.IsError.Should().BeFalse();
        string text = ((TextContentItem)result.Content[0]).Text;
        text.Should().Contain("\"action\": \"status\"");
        text.Should().Contain("\"running\":");
    }
}
