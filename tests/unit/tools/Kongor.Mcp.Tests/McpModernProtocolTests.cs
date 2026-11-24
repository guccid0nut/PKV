// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// TAO (Together As One) MCP & Kongor CLI -- Created by guccidonut (https://github.com/guccidonut/PKV)

using System.Diagnostics;
using System.Text.Json;
using FluentAssertions;
using Kongor.Cli.Mcp.Protocol;
using Xunit;

namespace Kongor.Mcp.Tests;

public sealed class McpModernProtocolTests
{
    [Fact]
    public async Task ProcessMessageAsync_ResourcesList_ReturnsAvailableResources()
    {
        McpServer server = new();
        string requestJson = "{\"jsonrpc\":\"2.0\",\"id\":10,\"method\":\"resources/list\"}";

        string? responseJson = await server.ProcessMessageAsync(requestJson);

        responseJson.Should().NotBeNull();
        JsonRpcResponse response = JsonRpcSerializer.Deserialize<JsonRpcResponse>(responseJson!)!;
        response.Id!.Value.Value.Should().Be(10L);
        response.Error.Should().BeNull();

        string resultText = JsonRpcSerializer.Serialize(response.Result);
        resultText.Should().Contain("resources");
        resultText.Should().Contain("kongor://logs/master-server");
        resultText.Should().Contain("kongor://logs/chat-server");
        resultText.Should().Contain("kongor://client/console");
        resultText.Should().Contain("kongor://engine/vfs-manifest");
    }

    [Fact]
    public async Task ProcessMessageAsync_ResourcesRead_ValidUri_ReturnsContents()
    {
        McpServer server = new();
        string requestJson = "{\"jsonrpc\":\"2.0\",\"id\":11,\"method\":\"resources/read\",\"params\":{\"uri\":\"kongor://logs/master-server\"}}";

        string? responseJson = await server.ProcessMessageAsync(requestJson);

        responseJson.Should().NotBeNull();
        JsonRpcResponse response = JsonRpcSerializer.Deserialize<JsonRpcResponse>(responseJson!)!;
        response.Id!.Value.Value.Should().Be(11L);
        response.Error.Should().BeNull();

        string resultText = JsonRpcSerializer.Serialize(response.Result);
        resultText.Should().Contain("\"uri\":\"kongor://logs/master-server\"");
        resultText.Should().Contain("\"contents\":[");
    }

    [Fact]
    public async Task ProcessMessageAsync_ResourcesRead_MissingUri_ReturnsInvalidParams()
    {
        McpServer server = new();
        string requestJson = "{\"jsonrpc\":\"2.0\",\"id\":12,\"method\":\"resources/read\",\"params\":{}}";

        string? responseJson = await server.ProcessMessageAsync(requestJson);

        responseJson.Should().NotBeNull();
        JsonRpcResponse response = JsonRpcSerializer.Deserialize<JsonRpcResponse>(responseJson!)!;
        response.Id!.Value.Value.Should().Be(12L);
        response.Error.Should().NotBeNull();
        response.Error!.Code.Should().Be(JsonRpcErrorCode.InvalidParams);
    }

    [Fact]
    public async Task ProcessMessageAsync_ResourcesRead_UnknownUri_ReturnsMethodNotFoundOrEmpty()
    {
        McpServer server = new();
        string requestJson = "{\"jsonrpc\":\"2.0\",\"id\":13,\"method\":\"resources/read\",\"params\":{\"uri\":\"kongor://unknown/resource\"}}";

        string? responseJson = await server.ProcessMessageAsync(requestJson);

        responseJson.Should().NotBeNull();
        JsonRpcResponse response = JsonRpcSerializer.Deserialize<JsonRpcResponse>(responseJson!)!;
        response.Id!.Value.Value.Should().Be(13L);
        response.Error.Should().NotBeNull();
        response.Error!.Message.Should().Contain("Unknown or unsupported resource URI");
    }

    [Fact]
    public async Task ProcessMessageAsync_PromptsList_ReturnsDefaultPrompts()
    {
        McpServer server = new();
        string requestJson = "{\"jsonrpc\":\"2.0\",\"id\":20,\"method\":\"prompts/list\"}";

        string? responseJson = await server.ProcessMessageAsync(requestJson);

        responseJson.Should().NotBeNull();
        JsonRpcResponse response = JsonRpcSerializer.Deserialize<JsonRpcResponse>(responseJson!)!;
        response.Id!.Value.Value.Should().Be(20L);
        response.Error.Should().BeNull();

        string resultText = JsonRpcSerializer.Serialize(response.Result);
        resultText.Should().Contain("prompts");
        resultText.Should().Contain("debug-k2-ui-blackscreen");
        resultText.Should().Contain("setup-scrimmage-test");
    }

    [Fact]
    public async Task ProcessMessageAsync_PromptsGet_ValidPromptName_ReturnsPromptMessages()
    {
        McpServer server = new();
        string requestJson = "{\"jsonrpc\":\"2.0\",\"id\":21,\"method\":\"prompts/get\",\"params\":{\"name\":\"debug-k2-ui-blackscreen\",\"arguments\":{\"interfacePath\":\"/ui/fe2/main.interface\"}}}";

        string? responseJson = await server.ProcessMessageAsync(requestJson);

        responseJson.Should().NotBeNull();
        JsonRpcResponse response = JsonRpcSerializer.Deserialize<JsonRpcResponse>(responseJson!)!;
        response.Id!.Value.Value.Should().Be(21L);
        response.Error.Should().BeNull();

        string resultText = JsonRpcSerializer.Serialize(response.Result);
        resultText.Should().Contain("\"description\":");
        resultText.Should().Contain("\"messages\":[");
        resultText.Should().Contain("/ui/fe2/main.interface");
    }

    [Fact]
    public async Task ProcessMessageAsync_PromptsGet_MissingName_ReturnsInvalidParams()
    {
        McpServer server = new();
        string requestJson = "{\"jsonrpc\":\"2.0\",\"id\":22,\"method\":\"prompts/get\",\"params\":{}}";

        string? responseJson = await server.ProcessMessageAsync(requestJson);

        responseJson.Should().NotBeNull();
        JsonRpcResponse response = JsonRpcSerializer.Deserialize<JsonRpcResponse>(responseJson!)!;
        response.Id!.Value.Value.Should().Be(22L);
        response.Error.Should().NotBeNull();
        response.Error!.Code.Should().Be(JsonRpcErrorCode.InvalidParams);
    }

    [Fact]
    public async Task ProcessMessageAsync_PromptsGet_UnknownPrompt_ReturnsError()
    {
        McpServer server = new();
        string requestJson = "{\"jsonrpc\":\"2.0\",\"id\":23,\"method\":\"prompts/get\",\"params\":{\"name\":\"non_existent_prompt\"}}";

        string? responseJson = await server.ProcessMessageAsync(requestJson);

        responseJson.Should().NotBeNull();
        JsonRpcResponse response = JsonRpcSerializer.Deserialize<JsonRpcResponse>(responseJson!)!;
        response.Id!.Value.Value.Should().Be(23L);
        response.Error.Should().NotBeNull();
        response.Error!.Message.Should().Contain("not found");
    }

    [Fact]
    public async Task ProcessMessageAsync_WithW3CTraceparent_PropagatesActivityContext()
    {
        McpServer server = new();
        // Standard W3C traceparent header: version 00, trace-id 4bf92f3577b34da6a3ce929d0e0e4736, parent-id 00f067aa0ba902b7, flags 01
        string requestJson = "{\"jsonrpc\":\"2.0\",\"id\":30,\"method\":\"ping\",\"_meta\":{\"traceparent\":\"00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01\"}}";

        string? responseJson = await server.ProcessMessageAsync(requestJson);

        responseJson.Should().NotBeNull();
        JsonRpcResponse response = JsonRpcSerializer.Deserialize<JsonRpcResponse>(responseJson!)!;
        response.Id!.Value.Value.Should().Be(30L);
        response.Error.Should().BeNull();
    }

    [Fact]
    public async Task ProcessMessageAsync_Initialize_NegotiatesCapabilitiesAndCapturesClientInfo()
    {
        McpServer server = new();
        string requestJson = "{\"jsonrpc\":\"2.0\",\"id\":100,\"method\":\"initialize\",\"params\":{\"protocolVersion\":\"2024-11-05\",\"capabilities\":{\"sampling\":{},\"roots\":{}},\"clientInfo\":{\"name\":\"Claude Desktop\",\"version\":\"1.0.0\"}}}";

        string? responseJson = await server.ProcessMessageAsync(requestJson);

        responseJson.Should().NotBeNull();
        JsonRpcResponse response = JsonRpcSerializer.Deserialize<JsonRpcResponse>(responseJson!)!;
        response.Id!.Value.Value.Should().Be(100L);
        response.Error.Should().BeNull();

        string resultText = JsonRpcSerializer.Serialize(response.Result);
        resultText.Should().Contain("\"protocolVersion\":\"2024-11-05\"");
        resultText.Should().Contain("\"listChanged\":true");
        resultText.Should().Contain("\"name\":\"kongor\"");

        server.ClientCapabilities.Should().NotBeNull();
        server.ClientCapabilities!.Sampling.Should().NotBeNull();
    }

    [Fact]
    public async Task ProcessMessageAsync_ResourceTemplatesList_ReturnsTemplates()
    {
        McpServer server = new();
        string requestJson = "{\"jsonrpc\":\"2.0\",\"id\":101,\"method\":\"resources/templates/list\"}";

        string? responseJson = await server.ProcessMessageAsync(requestJson);

        responseJson.Should().NotBeNull();
        JsonRpcResponse response = JsonRpcSerializer.Deserialize<JsonRpcResponse>(responseJson!)!;
        response.Id!.Value.Value.Should().Be(101L);
        response.Error.Should().BeNull();

        string resultText = JsonRpcSerializer.Serialize(response.Result);
        resultText.Should().Contain("resourceTemplates");
        resultText.Should().Contain("kongor://servers/{port}/logs");
        resultText.Should().Contain("kongor://replays/{matchId}/summary");
    }

    [Fact]
    public async Task ProcessMessageAsync_PromptsList_ReturnsAllDomainPrompts()
    {
        McpServer server = new();
        string requestJson = "{\"jsonrpc\":\"2.0\",\"id\":102,\"method\":\"prompts/list\"}";

        string? responseJson = await server.ProcessMessageAsync(requestJson);

        responseJson.Should().NotBeNull();
        JsonRpcResponse response = JsonRpcSerializer.Deserialize<JsonRpcResponse>(responseJson!)!;
        response.Id!.Value.Value.Should().Be(102L);
        response.Error.Should().BeNull();

        string resultText = JsonRpcSerializer.Serialize(response.Result);
        resultText.Should().Contain("debug-k2-ui-blackscreen");
        resultText.Should().Contain("setup-scrimmage-test");
        resultText.Should().Contain("generate_hero_mod");
        resultText.Should().Contain("analyze_replay_teamfight");
        resultText.Should().Contain("diagnose_crash_exitcode");
    }

    [Fact]
    public async Task ProcessMessageAsync_PromptsGet_GenerateHeroMod_ReturnsValidInstructions()
    {
        McpServer server = new();
        string requestJson = "{\"jsonrpc\":\"2.0\",\"id\":103,\"method\":\"prompts/get\",\"params\":{\"name\":\"generate_hero_mod\",\"arguments\":{\"heroName\":\"Hero_CustomFlint\",\"primaryAttribute\":\"Agility\",\"role\":\"Carry\"}}}";

        string? responseJson = await server.ProcessMessageAsync(requestJson);

        responseJson.Should().NotBeNull();
        JsonRpcResponse response = JsonRpcSerializer.Deserialize<JsonRpcResponse>(responseJson!)!;
        response.Id!.Value.Value.Should().Be(103L);
        response.Error.Should().BeNull();

        string resultText = JsonRpcSerializer.Serialize(response.Result);
        resultText.Should().Contain("Hero_CustomFlint");
        resultText.Should().Contain("Agility");
        resultText.Should().Contain("Carry");
        resultText.Should().Contain("kongor_mod_build");
        resultText.Should().Contain("kongor_asset_inspect");
    }

    [Fact]
    public async Task ProcessMessageAsync_PromptsGet_AnalyzeReplayTeamfight_ReturnsValidInstructions()
    {
        McpServer server = new();
        string requestJson = "{\"jsonrpc\":\"2.0\",\"id\":104,\"method\":\"prompts/get\",\"params\":{\"name\":\"analyze_replay_teamfight\",\"arguments\":{\"matchId\":\"45091\"}}}";

        string? responseJson = await server.ProcessMessageAsync(requestJson);

        responseJson.Should().NotBeNull();
        JsonRpcResponse response = JsonRpcSerializer.Deserialize<JsonRpcResponse>(responseJson!)!;
        response.Id!.Value.Value.Should().Be(104L);
        response.Error.Should().BeNull();

        string resultText = JsonRpcSerializer.Serialize(response.Result);
        resultText.Should().Contain("45091");
        resultText.Should().Contain("kongor_replay_inspect");
        resultText.Should().Contain("kongor_replay_playback");
    }

    [Fact]
    public async Task ProcessMessageAsync_PromptsGet_DiagnoseCrashExitcode_ReturnsAccessViolationAnalysis()
    {
        McpServer server = new();
        string requestJson = "{\"jsonrpc\":\"2.0\",\"id\":105,\"method\":\"prompts/get\",\"params\":{\"name\":\"diagnose_crash_exitcode\",\"arguments\":{\"exitCode\":\"0xC0000005\"}}}";

        string? responseJson = await server.ProcessMessageAsync(requestJson);

        responseJson.Should().NotBeNull();
        JsonRpcResponse response = JsonRpcSerializer.Deserialize<JsonRpcResponse>(responseJson!)!;
        response.Id!.Value.Value.Should().Be(105L);
        response.Error.Should().BeNull();

        string resultText = JsonRpcSerializer.Serialize(response.Result);
        resultText.Should().Contain("STATUS_ACCESS_VIOLATION");
        resultText.Should().Contain("0xC0000005");
        resultText.Should().Contain("kongor://client/errors");
        resultText.Should().Contain("kongor_k2_lint");
    }

    [Fact]
    public async Task ProcessMessageAsync_ResourcesList_IncludesMetricsErrorsReplaysAndTemplates()
    {
        McpServer server = new();
        string requestJson = "{\"jsonrpc\":\"2.0\",\"id\":106,\"method\":\"resources/list\"}";

        string? responseJson = await server.ProcessMessageAsync(requestJson);

        responseJson.Should().NotBeNull();
        JsonRpcResponse response = JsonRpcSerializer.Deserialize<JsonRpcResponse>(responseJson!)!;
        response.Id!.Value.Value.Should().Be(106L);
        response.Error.Should().BeNull();

        string resultText = JsonRpcSerializer.Serialize(response.Result);
        resultText.Should().Contain("kongor://cluster/metrics");
        resultText.Should().Contain("kongor://client/errors");
        resultText.Should().Contain("kongor://replays/latest/summary");
        resultText.Should().Contain("kongor://servers/{port}/logs");
    }

    [Fact]
    [Trait("Category", "Manual")]
    public async Task ProcessMessageAsync_ResourcesRead_ClusterMetrics_ReturnsValidJson()
    {
        if (!NexusTestEnvironment.IsManualTestEnabled) return;

        McpServer server = new();
        string requestJson = "{\"jsonrpc\":\"2.0\",\"id\":107,\"method\":\"resources/read\",\"params\":{\"uri\":\"kongor://cluster/metrics\"}}";

        string? responseJson = await server.ProcessMessageAsync(requestJson);

        responseJson.Should().NotBeNull();
        JsonRpcResponse response = JsonRpcSerializer.Deserialize<JsonRpcResponse>(responseJson!)!;
        response.Id!.Value.Value.Should().Be(107L);
        response.Error.Should().BeNull();

        string resultText = JsonRpcSerializer.Serialize(response.Result);
        resultText.Should().Contain("machineName");
        resultText.Should().Contain("processName");
        resultText.Should().Contain("clusterServices");
    }

    [Fact]
    public async Task ProcessMessageAsync_ResourcesRead_ClientErrors_ReturnsValidJson()
    {
        McpServer server = new();
        string requestJson = "{\"jsonrpc\":\"2.0\",\"id\":108,\"method\":\"resources/read\",\"params\":{\"uri\":\"kongor://client/errors\"}}";

        string? responseJson = await server.ProcessMessageAsync(requestJson);

        responseJson.Should().NotBeNull();
        JsonRpcResponse response = JsonRpcSerializer.Deserialize<JsonRpcResponse>(responseJson!)!;
        response.Id!.Value.Value.Should().Be(108L);
        response.Error.Should().BeNull();

        string resultText = JsonRpcSerializer.Serialize(response.Result);
        resultText.Should().Contain("status");
    }

    [Fact]
    public async Task ProcessMessageAsync_ResourcesRead_LatestReplay_ReturnsValidJson()
    {
        McpServer server = new();
        string requestJson = "{\"jsonrpc\":\"2.0\",\"id\":109,\"method\":\"resources/read\",\"params\":{\"uri\":\"kongor://replays/latest/summary\"}}";

        string? responseJson = await server.ProcessMessageAsync(requestJson);

        responseJson.Should().NotBeNull();
        JsonRpcResponse response = JsonRpcSerializer.Deserialize<JsonRpcResponse>(responseJson!)!;
        response.Id!.Value.Value.Should().Be(109L);
        response.Error.Should().BeNull();

        string resultText = JsonRpcSerializer.Serialize(response.Result);
        resultText.Should().Contain("status");
    }

    [Fact]
    public async Task ProcessMessageAsync_ResourcesRead_DedicatedServerLogs_ReturnsTail()
    {
        McpServer server = new();
        string requestJson = "{\"jsonrpc\":\"2.0\",\"id\":110,\"method\":\"resources/read\",\"params\":{\"uri\":\"kongor://servers/11235/logs\"}}";

        string? responseJson = await server.ProcessMessageAsync(requestJson);

        responseJson.Should().NotBeNull();
        JsonRpcResponse response = JsonRpcSerializer.Deserialize<JsonRpcResponse>(responseJson!)!;
        response.Id!.Value.Value.Should().Be(110L);
        response.Error.Should().BeNull();

        string resultText = JsonRpcSerializer.Serialize(response.Result);
        resultText.Should().Contain("kongor://servers/11235/logs");
    }

    [Fact]
    public async Task ProcessMessageAsync_ToolsCall_WithProgressToken_DispatchesProgressNotifications()
    {
        McpServer server = new();
        server.ClearSentNotifications();

        string tempModDir = Path.Combine(Path.GetTempPath(), "kongor_test_mod_" + Guid.NewGuid().ToString("N")[..8]);
        string tempOutPath = Path.Combine(Path.GetTempPath(), "test_" + Guid.NewGuid().ToString("N")[..8] + ".s2z");
        Directory.CreateDirectory(tempModDir);
        File.WriteAllText(Path.Combine(tempModDir, "test.entity"), "<entity name='Test'/>");

        try
        {
            string requestJson = "{\"jsonrpc\":\"2.0\",\"id\":111,\"method\":\"tools/call\",\"params\":{\"name\":\"kongor_mod_build\",\"arguments\":{\"action\":\"build_s2z\",\"source_dir\":\"" +
                tempModDir.Replace('\\', '/') + "\",\"output_path\":\"" + tempOutPath.Replace('\\', '/') + "\"},\"_meta\":{\"progressToken\":\"progress-test-token-999\"}}}";

            string? responseJson = await server.ProcessMessageAsync(requestJson);

            responseJson.Should().NotBeNull();
            JsonRpcResponse response = JsonRpcSerializer.Deserialize<JsonRpcResponse>(responseJson!)!;
            response.Id!.Value.Value.Should().Be(111L);
            response.Error.Should().BeNull();

            server.SentNotifications.Should().NotBeEmpty();
            var progressNotifs = server.SentNotifications.Where(n => n.Contains("notifications/progress")).ToList();
            progressNotifs.Should().NotBeEmpty();
            progressNotifs.Should().Contain(n => n.Contains("progress-test-token-999"));
        }
        finally
        {
            if (Directory.Exists(tempModDir)) Directory.Delete(tempModDir, true);
            if (File.Exists(tempOutPath)) File.Delete(tempOutPath);
        }
    }
}

