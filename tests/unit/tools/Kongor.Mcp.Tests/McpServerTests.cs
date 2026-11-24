// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// TAO (Together As One) MCP & Kongor CLI -- Created by guccidonut (https://github.com/guccidonut/PKV)

using System.Text.Json;
using FluentAssertions;
using Kongor.Cli.Mcp.Protocol;
using Xunit;

namespace Kongor.Mcp.Tests;

public sealed class McpServerTests
{
    [Fact]
    public async Task ProcessMessageAsync_InitializeHandshake_ReturnsServerInfoAndCapabilities()
    {
        StringWriter logWriter = new();
        McpServer server = new(errorWriter: logWriter);

        string requestJson = "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{\"protocolVersion\":\"2024-11-05\",\"clientInfo\":{\"name\":\"test-client\",\"version\":\"1.0.0\"}}}";

        string? responseJson = await server.ProcessMessageAsync(requestJson);

        responseJson.Should().NotBeNull();
        JsonRpcResponse response = JsonRpcSerializer.Deserialize<JsonRpcResponse>(responseJson!)!;
        response.Id!.Value.Value.Should().Be(1L);
        response.Error.Should().BeNull();
        response.Result.Should().NotBeNull();

        string resultText = JsonRpcSerializer.Serialize(response.Result);
        resultText.Should().Contain("\"protocolVersion\":\"2024-11-05\"");
        resultText.Should().Contain("\"name\":\"kongor\"");
        resultText.Should().Contain($"\"version\":\"{NEXUS.Contracts.Domain.NexusVersionInfo.PlatformVersion}\"");
    }

    [Fact]
    public async Task ProcessMessageAsync_InitializedNotification_SetsServerInitialized()
    {
        McpServer server = new();
        server.IsInitialized.Should().BeFalse();

        string notificationJson = "{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\"}";
        string? responseJson = await server.ProcessMessageAsync(notificationJson);

        responseJson.Should().BeNull(); // Notifications have no response
        server.IsInitialized.Should().BeTrue();
    }

    [Fact]
    public async Task ProcessMessageAsync_Ping_ReturnsEmptyResult()
    {
        McpServer server = new();
        string pingJson = "{\"jsonrpc\":\"2.0\",\"id\":\"ping-42\",\"method\":\"ping\"}";

        string? responseJson = await server.ProcessMessageAsync(pingJson);

        responseJson.Should().NotBeNull();
        JsonRpcResponse response = JsonRpcSerializer.Deserialize<JsonRpcResponse>(responseJson!)!;
        response.Id!.Value.Value.Should().Be("ping-42");
        response.Error.Should().BeNull();
        response.Result.Should().NotBeNull();
    }

    [Fact]
    public async Task ProcessMessageAsync_ToolsList_ReturnsAllRegisteredTools()
    {
        McpServer server = new();
        string listJson = "{\"jsonrpc\":\"2.0\",\"id\":100,\"method\":\"tools/list\"}";

        string? responseJson = await server.ProcessMessageAsync(listJson);

        responseJson.Should().NotBeNull();
        JsonRpcResponse response = JsonRpcSerializer.Deserialize<JsonRpcResponse>(responseJson!)!;
        response.Id!.Value.Value.Should().Be(100L);
        response.Error.Should().BeNull();

        string resultText = JsonRpcSerializer.Serialize(response.Result);
        resultText.Should().Contain("kongor_server_manage");
        resultText.Should().Contain("kongor_game_manage");
        resultText.Should().Contain("kongor_game_command");
        resultText.Should().Contain("kongor_game_telemetry");
        resultText.Should().Contain("kongor_capture_window");
        resultText.Should().Contain("kongor_ui_render_view");
    }

    [Fact]
    public async Task ProcessMessageAsync_ToolsCall_DispatchesToHandlerAndReturnsEnvelope()
    {
        McpServer server = new();
        string callJson = "{\"jsonrpc\":\"2.0\",\"id\":200,\"method\":\"tools/call\",\"params\":{\"name\":\"kongor_server_manage\",\"arguments\":{\"action\":\"status\"}}}";

        string? responseJson = await server.ProcessMessageAsync(callJson);

        responseJson.Should().NotBeNull();
        JsonRpcResponse response = JsonRpcSerializer.Deserialize<JsonRpcResponse>(responseJson!)!;
        response.Id!.Value.Value.Should().Be(200L);
        response.Error.Should().BeNull();

        string resultText = JsonRpcSerializer.Serialize(response.Result);
        resultText.Should().Contain("\"content\":[");
        resultText.Should().Contain("\"isError\":false");
        resultText.Should().Contain("status");
    }

    [Fact]
    public async Task ProcessMessageAsync_ToolsCall_MissingName_ReturnsInvalidParams()
    {
        McpServer server = new();
        string callJson = "{\"jsonrpc\":\"2.0\",\"id\":201,\"method\":\"tools/call\",\"params\":{\"arguments\":{}}}";

        string? responseJson = await server.ProcessMessageAsync(callJson);

        responseJson.Should().NotBeNull();
        JsonRpcResponse response = JsonRpcSerializer.Deserialize<JsonRpcResponse>(responseJson!)!;
        response.Id!.Value.Value.Should().Be(201L);
        response.Error.Should().NotBeNull();
        response.Error!.Code.Should().Be(JsonRpcErrorCode.InvalidParams);
    }

    [Fact]
    public async Task ProcessMessageAsync_MalformedJson_ReturnsParseErrorWithNullId()
    {
        McpServer server = new();
        string invalidJson = "{\"jsonrpc\":\"2.0\", id: INVALID";

        string? responseJson = await server.ProcessMessageAsync(invalidJson);

        responseJson.Should().NotBeNull();
        JsonRpcResponse response = JsonRpcSerializer.Deserialize<JsonRpcResponse>(responseJson!)!;
        response.Id.Should().BeNull();
        response.Error.Should().NotBeNull();
        response.Error!.Code.Should().Be(JsonRpcErrorCode.ParseError);
    }

    [Fact]
    public async Task ProcessMessageAsync_UnknownMethod_ReturnsMethodNotFound()
    {
        McpServer server = new();
        string unknownMethodJson = "{\"jsonrpc\":\"2.0\",\"id\":300,\"method\":\"non_existent_rpc\"}";

        string? responseJson = await server.ProcessMessageAsync(unknownMethodJson);

        responseJson.Should().NotBeNull();
        JsonRpcResponse response = JsonRpcSerializer.Deserialize<JsonRpcResponse>(responseJson!)!;
        response.Id!.Value.Value.Should().Be(300L);
        response.Error.Should().NotBeNull();
        response.Error!.Code.Should().Be(JsonRpcErrorCode.MethodNotFound);
    }

    [Fact]
    [Trait("Category", "Manual")]
    public async Task RunAsync_StdioStreamPump_ProcessesFullSession()
    {
        if (!NexusTestEnvironment.IsManualTestEnabled) return;

        using StringWriter rawLog = new();
        using TextWriter errorLog = TextWriter.Synchronized(rawLog);
        McpServer server = new(errorWriter: errorLog);

        string inputScript = string.Join("\n", [
            "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{\"protocolVersion\":\"2024-11-05\"}}",
            "{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\"}",
            "{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"ping\"}",
            "{\"jsonrpc\":\"2.0\",\"id\":3,\"method\":\"tools/list\"}",
            "{\"jsonrpc\":\"2.0\",\"id\":4,\"method\":\"tools/call\",\"params\":{\"name\":\"kongor_server_manage\",\"arguments\":{\"action\":\"status\"}}}"
        ]);

        using StringReader inputReader = new(inputScript);
        using StringWriter outputWriter = new();

        await server.RunAsync(inputReader, outputWriter, CancellationToken.None);

        string outputText = outputWriter.ToString();
        string[] outputLines = outputText.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);

        // 4 requests (initialize, ping, tools/list, tools/call) should yield 4 responses
        outputLines.Should().HaveCount(4);

        // Verify all 4 responses have valid JSON-RPC framing
        foreach (string line in outputLines)
        {
            line.Should().StartWith("{\"jsonrpc\":\"2.0\"");
        }

        // Verify stderr received diagnostic logs
        string logs = rawLog.ToString();
        logs.Should().Contain("[MCP Server] Starting ProjectKongor MCP JSON-RPC Server");
        logs.Should().Contain("Ready for tool execution.");
    }

    [Fact]
    public async Task RunAsync_ClientCancellationNotification_CancelsInFlightRequest()
    {
        McpToolRegistry registry = new(registerDefaults: false);
        registry.RegisterTool(
            new ToolDefinition
            {
                Name = "test_cancelable_tool",
                Description = "Test tool that waits for cancellation",
                InputSchema = new ToolInputSchema { Type = "object" }
            },
            async (args, ct) =>
            {
                await Task.Delay(5000, ct);
                return ToolCallResult.Success("Completed without cancellation");
            });

        using StringWriter rawLog = new();
        using TextWriter errorLog = TextWriter.Synchronized(rawLog);
        McpServer server = new(toolRegistry: registry, errorWriter: errorLog);

        string inputScript = string.Join("\n", [
            "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{\"protocolVersion\":\"2024-11-05\"}}",
            "{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\"}",
            "{\"jsonrpc\":\"2.0\",\"id\":500,\"method\":\"tools/call\",\"params\":{\"name\":\"test_cancelable_tool\",\"arguments\":{}}}",
            "{\"jsonrpc\":\"2.0\",\"method\":\"notifications/cancelled\",\"params\":{\"requestId\":500,\"reason\":\"User requested abort\"}}"
        ]);

        using StringReader inputReader = new(inputScript);
        using StringWriter outputWriter = new();

        var sw = System.Diagnostics.Stopwatch.StartNew();
        await server.RunAsync(inputReader, outputWriter, CancellationToken.None);
        sw.Stop();

        // Must complete in well under the 5000ms tool delay due to prompt cancellation
        sw.ElapsedMilliseconds.Should().BeLessThan(2000, "Cancellation notification must cancel tool promptly");

        string outputText = outputWriter.ToString();
        string[] outputLines = outputText.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);

        // Expect 2 responses: initialize (id: 1) and cancelled tool (id: 500)
        outputLines.Should().HaveCount(2);

        string? cancelledResponse = outputLines.FirstOrDefault(l => l.Contains("\"id\":500"));
        cancelledResponse.Should().NotBeNull();
        cancelledResponse.Should().Contain("-32800", "Cancelled request must return JSON-RPC error code -32800");
    }

    [Fact]
    public async Task RunAsync_ConcurrentRequests_DoNotBlockHeadOfLine()
    {
        McpToolRegistry registry = new(registerDefaults: false);
        registry.RegisterTool(
            new ToolDefinition
            {
                Name = "test_delayed_tool",
                Description = "Test tool that delays 150ms",
                InputSchema = new ToolInputSchema { Type = "object" }
            },
            async (args, ct) =>
            {
                await Task.Delay(150, ct);
                return ToolCallResult.Success("Delayed result");
            });

        using StringWriter rawLog = new();
        using TextWriter errorLog = TextWriter.Synchronized(rawLog);
        McpServer server = new(toolRegistry: registry, errorWriter: errorLog);

        string inputScript = string.Join("\n", [
            "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{\"protocolVersion\":\"2024-11-05\"}}",
            "{\"jsonrpc\":\"2.0\",\"id\":10,\"method\":\"tools/call\",\"params\":{\"name\":\"test_delayed_tool\",\"arguments\":{}}}",
            "{\"jsonrpc\":\"2.0\",\"id\":11,\"method\":\"ping\"}"
        ]);

        using StringReader inputReader = new(inputScript);
        using StringWriter outputWriter = new();

        await server.RunAsync(inputReader, outputWriter, CancellationToken.None);

        string outputText = outputWriter.ToString();
        string[] outputLines = outputText.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);

        // 3 responses: initialize (1), delayed tool (10), ping (11)
        outputLines.Should().HaveCount(3);

        outputLines.Should().Contain(l => l.Contains("\"id\":11") && l.Contains("\"result\":"));
        outputLines.Should().Contain(l => l.Contains("\"id\":10") && l.Contains("\"result\":"));
    }
}