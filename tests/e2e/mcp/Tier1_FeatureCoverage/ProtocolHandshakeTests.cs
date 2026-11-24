// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// TAO (Together As One) MCP & Kongor CLI -- Created by guccidonut (https://github.com/guccidonut/PKV)

using FluentAssertions;
using Kongor.Mcp.E2E.Tests.Client;
using Xunit;
using Xunit.Abstractions;

namespace Kongor.Mcp.E2E.Tests.Tier1_FeatureCoverage;

public sealed class ProtocolHandshakeTests : IAsyncLifetime
{
    private readonly ITestOutputHelper _output;
    private McpProcessClient _client = null!;

    public ProtocolHandshakeTests(ITestOutputHelper output)
    {
        _output = output;
    }

    public async Task InitializeAsync()
    {
        _client = new McpProcessClient(msg => _output.WriteLine(msg));
        await _client.StartAsync();
    }

    public async Task DisposeAsync()
    {
        await _client.DisposeAsync();
    }

    [Fact]
    public async Task Initialize_ValidHandshake_ReturnsProtocolVersionAndCapabilities()
    {
        InitializeParams initParams = new()
        {
            ProtocolVersion = "2024-11-05",
            ClientInfo = new ClientInfo { Name = "KongorTestClient", Version = "1.0.0" },
            Capabilities = new ClientCapabilities()
        };

        JsonRpcResponse resp = await _client.SendRequestAsync("initialize", initParams);

        resp.Should().NotBeNull();
        resp.Error.Should().BeNull();
        resp.Result.Should().NotBeNull();

        InitializeResult? result = System.Text.Json.JsonSerializer.Deserialize<InitializeResult>(
            resp.Result!.Value.GetRawText(),
            new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        result.Should().NotBeNull();
        result!.ProtocolVersion.Should().NotBeNullOrWhiteSpace();
        result.Capabilities.Should().NotBeNull();
    }

    [Fact]
    public async Task Initialize_ClientInfoPassed_ReturnsServerInfo()
    {
        InitializeParams initParams = new()
        {
            ProtocolVersion = "2024-11-05",
            ClientInfo = new ClientInfo { Name = "E2EAgent", Version = "5.0.0" },
            Capabilities = new ClientCapabilities()
        };

        JsonRpcResponse resp = await _client.SendRequestAsync("initialize", initParams);
        resp.Error.Should().BeNull();

        InitializeResult? result = System.Text.Json.JsonSerializer.Deserialize<InitializeResult>(
            resp.Result!.Value.GetRawText(),
            new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        result!.ServerInfo.Should().NotBeNull();
        result.ServerInfo!.Name.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task Initialize_FollowedByNotificationInitialized_Succeeds()
    {
        await _client.InitializeAsync("E2ETestClient", "1.0.0");
        _client.IsProcessRunning.Should().BeTrue();
    }

    [Fact]
    public async Task Ping_WhenDispatched_ReturnsEmptyResultObject()
    {
        await _client.InitializeAsync();
        JsonRpcResponse pingResp = await _client.PingAsync();

        pingResp.Should().NotBeNull();
        pingResp.Error.Should().BeNull();
        pingResp.Result.Should().NotBeNull();
    }

    [Fact]
    public async Task ToolsList_WhenQueried_ReturnsRegisteredToolsWithSchemas()
    {
        await _client.InitializeAsync();
        ToolsListResult? toolsList = await _client.ListToolsAsync();

        toolsList.Should().NotBeNull();
        toolsList!.Tools.Should().NotBeNull();
        toolsList.Tools.Should().HaveCountGreaterOrEqualTo(6);

        List<string> toolNames = toolsList.Tools.Select(t => t.Name).ToList();
        toolNames.Should().Contain("kongor_server_manage");
        toolNames.Should().Contain("kongor_game_client_manage");
        toolNames.Should().Contain("kongor_game_command");
        toolNames.Should().Contain("kongor_capture_window");
        toolNames.Should().Contain("kongor_health_check");
        toolNames.Should().Contain("kongor_call_tool");

        // Dynamically enabling all toolsets should expose extended forensics & UI tools
        ToolCallResult enableResp = await _client.CallToolAsync("kongor_enable_toolset", new { domain = "all" });
        enableResp.IsError.Should().BeFalse();

        ToolsListResult? expandedList = await _client.ListToolsAsync();
        expandedList.Should().NotBeNull();
        List<string> expandedNames = expandedList!.Tools.Select(t => t.Name).ToList();
        expandedNames.Should().Contain("kongor_game_telemetry");
        expandedNames.Should().Contain("kongor_ui_render_view");
    }

    [Fact]
    public async Task ToolsList_OnSchemaInspection_EveryToolHasDescriptionAndInputSchema()
    {
        await _client.InitializeAsync();
        ToolsListResult? toolsList = await _client.ListToolsAsync();

        foreach (ToolDefinition tool in toolsList!.Tools)
        {
            tool.Name.Should().NotBeNullOrWhiteSpace();
            tool.Description.Should().NotBeNullOrWhiteSpace();
            tool.InputSchema.Should().NotBeNull();
        }
    }
}
