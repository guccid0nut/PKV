// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// TAO (Together As One) MCP & Kongor CLI -- Created by guccidonut (https://github.com/guccidonut/PKV)

using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using Kongor.Cli.Mcp.Handlers;
using Kongor.Cli.Mcp.Protocol;
using Kongor.Cli.Mcp.Services;
using Moq;
using Xunit;

namespace Kongor.Mcp.Tests;

/// <summary>
/// Unit tests for Model Context Protocol (MCP) Sampling (sampling/createMessage)
/// conforming to the official MCP 2024-11-05 specification.
/// </summary>
public sealed class McpSamplingTests
{
    [Fact]
    public void IsSamplingSupported_BeforeInitialize_ReturnsFalse()
    {
        McpServer server = new();
        server.IsSamplingSupported.Should().BeFalse();
    }

    [Fact]
    public async Task IsSamplingSupported_WhenClientOmitsSamplingCapability_ReturnsFalse()
    {
        McpServer server = new();
        string initRequest = "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{\"protocolVersion\":\"2024-11-05\",\"capabilities\":{},\"clientInfo\":{\"name\":\"test-client\",\"version\":\"1.0\"}}}";

        await server.ProcessMessageAsync(initRequest);

        server.IsSamplingSupported.Should().BeFalse();
    }

    [Fact]
    public async Task IsSamplingSupported_WhenClientDeclaresSamplingCapability_ReturnsTrue()
    {
        McpServer server = new();
        string initRequest = "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{\"protocolVersion\":\"2024-11-05\",\"capabilities\":{\"sampling\":{}},\"clientInfo\":{\"name\":\"test-client\",\"version\":\"1.0\"}}}";

        string? responseJson = await server.ProcessMessageAsync(initRequest);

        responseJson.Should().NotBeNull();
        server.IsSamplingSupported.Should().BeTrue();
    }

    [Fact]
    public async Task CreateMessageAsync_WhenSamplingNotNegotiated_ThrowsInvalidOperationException()
    {
        McpServer server = new();

        var action = () => server.CreateMessageAsync(new CreateMessageParams
        {
            Messages = [new SamplingMessage { Role = "user", Content = new SamplingContent { Type = "text", Text = "Test" } }]
        });

        await action.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*Client does not support MCP sampling*");
    }

    [Fact]
    public async Task CreateMessageAsync_SuccessfulRoundTrip_ReturnsAssistantMessage()
    {
        McpServer server = new();
        await InitializeServerWithSamplingAsync(server);

        var samplingTask = server.CreateMessageAsync(new CreateMessageParams
        {
            SystemPrompt = "You are a K2 diagnostics assistant.",
            Messages =
            [
                new SamplingMessage
                {
                    Role = "user",
                    Content = new SamplingContent
                    {
                        Type = "text",
                        Text = "Analyze this minidump log."
                    }
                }
            ],
            MaxTokens = 350,
            Temperature = 0.3
        });

        // Wait for server to dispatch sampling/createMessage request to client
        string requestJson = await WaitForSentRequestAsync(server);
        requestJson.Should().NotBeNull();

        using var doc = JsonDocument.Parse(requestJson);
        doc.RootElement.GetProperty("method").GetString().Should().Be("sampling/createMessage");
        long requestId = doc.RootElement.GetProperty("id").GetInt64();
        requestId.Should().BeGreaterThan(0);

        JsonElement paramsEl = doc.RootElement.GetProperty("params");
        paramsEl.GetProperty("systemPrompt").GetString().Should().Be("You are a K2 diagnostics assistant.");
        paramsEl.GetProperty("maxTokens").GetInt32().Should().Be(350);
        paramsEl.GetProperty("temperature").GetDouble().Should().BeApproximately(0.3, 0.01);

        // Simulate client sending response back to the server
        await RespondToSamplingAsync(server, requestId, "Root cause: 0xC0000005 access violation in K2Renderer::RenderMesh.", "claude-3-5-sonnet");

        CreateMessageResult result = await samplingTask;
        result.Should().NotBeNull();
        result.Role.Should().Be("assistant");
        result.Model.Should().Be("claude-3-5-sonnet");
        result.StopReason.Should().Be("endTurn");
        result.Content.Should().NotBeNull();
        result.Content.Type.Should().Be("text");
        result.Content.Text.Should().Be("Root cause: 0xC0000005 access violation in K2Renderer::RenderMesh.");
    }

    [Fact]
    public async Task CreateMessageAsync_MultipleConcurrentRequests_CorrelatesById()
    {
        McpServer server = new();
        await InitializeServerWithSamplingAsync(server);

        // Initiate 3 concurrent sampling requests
        var task1 = server.CreateMessageAsync(new CreateMessageParams
        {
            Messages = [new SamplingMessage { Role = "user", Content = new SamplingContent { Text = "Req1" } }]
        });
        var task2 = server.CreateMessageAsync(new CreateMessageParams
        {
            Messages = [new SamplingMessage { Role = "user", Content = new SamplingContent { Text = "Req2" } }]
        });
        var task3 = server.CreateMessageAsync(new CreateMessageParams
        {
            Messages = [new SamplingMessage { Role = "user", Content = new SamplingContent { Text = "Req3" } }]
        });

        // Wait until all 3 requests have been recorded in SentRequests
        var sw = Stopwatch.StartNew();
        while (server.SentRequests.Count < 3 && sw.ElapsedMilliseconds < 3000)
        {
            await Task.Delay(20);
        }
        server.SentRequests.Count.Should().Be(3);

        // Extract each request ID
        var requests = server.SentRequests.Take(3).Select(r =>
        {
            using var d = JsonDocument.Parse(r);
            return d.RootElement.GetProperty("id").GetInt64();
        }).ToList();

        // Deliver responses in reverse order (3, 1, 2)
        await RespondToSamplingAsync(server, requests[2], "Answer 3", "model-3");
        await RespondToSamplingAsync(server, requests[0], "Answer 1", "model-1");
        await RespondToSamplingAsync(server, requests[1], "Answer 2", "model-2");

        await Task.WhenAll(task1, task2, task3);

        (await task1).Content.Text.Should().Be("Answer 1");
        (await task2).Content.Text.Should().Be("Answer 2");
        (await task3).Content.Text.Should().Be("Answer 3");
    }

    [Fact]
    public async Task CreateMessageAsync_WhenClientReturnsJsonRpcError_ThrowsExceptionWithDetails()
    {
        McpServer server = new();
        await InitializeServerWithSamplingAsync(server);

        var samplingTask = server.CreateMessageAsync(new CreateMessageParams
        {
            Messages = [new SamplingMessage { Role = "user", Content = new SamplingContent { Text = "Error test" } }]
        });

        string requestJson = await WaitForSentRequestAsync(server);
        using var doc = JsonDocument.Parse(requestJson);
        long requestId = doc.RootElement.GetProperty("id").GetInt64();

        // Simulate client returning JSON-RPC error
        string errorResponse = $@"{{
            ""jsonrpc"": ""2.0"",
            ""id"": {requestId},
            ""error"": {{
                ""code"": -32000,
                ""message"": ""Model refusal: rate limited""
            }}
        }}";

        string? res = await server.ProcessMessageAsync(errorResponse);
        res.Should().BeNull();

        var action = async () => await samplingTask;
        await action.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*Client sampling rejected: [-32000] Model refusal: rate limited*");
    }

    [Fact]
    public async Task CreateMessageAsync_WhenCancellationRequested_CancelsPendingRequest()
    {
        McpServer server = new();
        await InitializeServerWithSamplingAsync(server);

        using var cts = new CancellationTokenSource();
        var samplingTask = server.CreateMessageAsync(new CreateMessageParams
        {
            Messages = [new SamplingMessage { Role = "user", Content = new SamplingContent { Text = "Cancel test" } }]
        }, cts.Token);

        await WaitForSentRequestAsync(server);

        cts.Cancel();

        var action = async () => await samplingTask;
        await action.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task RequestCrashAnalysisAsync_WhenSamplingSupported_SendsStructuredPromptAndReturnsAiText()
    {
        McpServer server = new();
        await InitializeServerWithSamplingAsync(server);

        var analysisTask = server.RequestCrashAnalysisAsync(
            "0xC0000005",
            "EXCEPTION_ACCESS_VIOLATION in K2SceneRenderer::Render at 0x00007FF71234");

        string requestJson = await WaitForSentRequestAsync(server);
        using var doc = JsonDocument.Parse(requestJson);
        long requestId = doc.RootElement.GetProperty("id").GetInt64();

        // Verify structured prompt
        string rawParams = doc.RootElement.GetProperty("params").GetRawText();
        rawParams.Should().Contain("0xC0000005");
        rawParams.Should().Contain("EXCEPTION_ACCESS_VIOLATION");
        rawParams.Should().Contain("expert systems engineer and K2 game engine debugger");

        await RespondToSamplingAsync(server, requestId, "Analysis: invalid vertex buffer pointer in mesh streaming cache.");

        string? analysis = await analysisTask;
        analysis.Should().Be("Analysis: invalid vertex buffer pointer in mesh streaming cache.");
    }

    [Fact]
    public async Task RequestCrashAnalysisAsync_WhenSamplingNotSupported_ReturnsNullGracefully()
    {
        McpServer server = new();
        // Do not initialize with sampling

        string? analysis = await server.RequestCrashAnalysisAsync("0xC0000005", "Crash log context");
        analysis.Should().BeNull();
    }

    [Fact]
    public async Task McpSamplingContext_WhenInvoked_AmbientContextAvailableDuringToolsCall()
    {
        McpServer server = new();
        await InitializeServerWithSamplingAsync(server);

        IMcpSamplingService? capturedServiceInsideTool = null;

        var customToolDef = new ToolDefinition
        {
            Name = "test_sampling_context_tool",
            Description = "Custom tool to inspect McpSamplingContext",
            InputSchema = new ToolInputSchema { Type = "object" }
        };

        server.Registry.RegisterTool(customToolDef, (args, ct) =>
        {
            capturedServiceInsideTool = McpSamplingContext.Current;
            return Task.FromResult(ToolCallResult.Text("ok"));
        });

        McpSamplingContext.Current.Should().BeNull("Ambient context must be null outside of tools/call execution.");

        string toolCallJson = "{\"jsonrpc\":\"2.0\",\"id\":99,\"method\":\"tools/call\",\"params\":{\"name\":\"test_sampling_context_tool\",\"arguments\":{}}}";
        string? responseJson = await server.ProcessMessageAsync(toolCallJson);

        responseJson.Should().NotBeNull();
        capturedServiceInsideTool.Should().NotBeNull();
        capturedServiceInsideTool.Should().BeSameAs(server);

        McpSamplingContext.Current.Should().BeNull("Ambient context must be restored after tools/call execution.");
    }

    [Trait("Category", "Manual")]
    [Fact]
    public async Task ClusterDiagnosticsHandler_WithSamplingEnabled_CompletesWithAiDiagnosis()
    {
        if (!NexusTestEnvironment.IsManualTestEnabled) return;

        McpServer server = new();
        await InitializeServerWithSamplingAsync(server);

        string toolCallJson = "{\"jsonrpc\":\"2.0\",\"id\":105,\"method\":\"tools/call\",\"params\":{\"name\":\"kongor_diagnostics\",\"arguments\":{\"enable_sampling\":true}}}";

        var callTask = server.ProcessMessageAsync(toolCallJson);

        // Wait for the tool to initiate sampling
        string requestJson = await WaitForSentRequestAsync(server, timeoutMs: 5000);
        using var doc = JsonDocument.Parse(requestJson);
        long samplingReqId = doc.RootElement.GetProperty("id").GetInt64();

        await RespondToSamplingAsync(server, samplingReqId, "AI Triage: Cluster Redis port 28298 offline. Check service status.");

        string? toolResponseJson = await callTask;
        toolResponseJson.Should().NotBeNull();

        JsonRpcResponse response = JsonRpcSerializer.Deserialize<JsonRpcResponse>(toolResponseJson!)!;
        response.Error.Should().BeNull();

        string resultText = JsonRpcSerializer.Serialize(response.Result);
        resultText.Should().Contain("aiDiagnosis");
        resultText.Should().Contain("AI Triage: Cluster Redis port 28298 offline.");
    }

    [Fact]
    public async Task GameClientManageHandler_ActionAiDiagnose_WhenSamplingSupported_CompletesWithAiAnalysis()
    {
        McpServer server = new();
        await InitializeServerWithSamplingAsync(server);

        string toolCallJson = "{\"jsonrpc\":\"2.0\",\"id\":200,\"method\":\"tools/call\",\"params\":{\"name\":\"kongor_game_client_manage\",\"arguments\":{\"action\":\"ai_diagnose\",\"exitCode\":\"0xC0000005\",\"context\":\"Crash at K2Interface::LoadXml\"}}}";

        var callTask = server.ProcessMessageAsync(toolCallJson);

        string requestJson = await WaitForSentRequestAsync(server, timeoutMs: 3000);
        using var doc = JsonDocument.Parse(requestJson);
        long samplingReqId = doc.RootElement.GetProperty("id").GetInt64();

        await RespondToSamplingAsync(server, samplingReqId, "AI Analysis: Missing root XML node in interface file. Verify K2 schema.");

        string? toolResponseJson = await callTask;
        toolResponseJson.Should().NotBeNull();

        JsonRpcResponse response = JsonRpcSerializer.Deserialize<JsonRpcResponse>(toolResponseJson!)!;
        response.Error.Should().BeNull();

        string resultText = JsonRpcSerializer.Serialize(response.Result);
        resultText.Should().Contain("samplingSupported").And.Contain("true");
        resultText.Should().Contain("AI Analysis: Missing root XML node in interface file.");
    }

    [Fact]
    public async Task GameClientManageHandler_ActionAiDiagnose_WhenSamplingUnsupported_ReturnsGracefulFallback()
    {
        McpServer server = new();
        // Server not initialized with sampling

        string toolCallJson = "{\"jsonrpc\":\"2.0\",\"id\":201,\"method\":\"tools/call\",\"params\":{\"name\":\"kongor_game_client_manage\",\"arguments\":{\"action\":\"ai_diagnose\",\"exitCode\":\"0xC0000005\"}}}";

        string? toolResponseJson = await server.ProcessMessageAsync(toolCallJson);
        toolResponseJson.Should().NotBeNull();

        JsonRpcResponse response = JsonRpcSerializer.Deserialize<JsonRpcResponse>(toolResponseJson!)!;
        response.Error.Should().BeNull();

        string resultText = JsonRpcSerializer.Serialize(response.Result);
        resultText.Should().Contain("samplingSupported").And.Contain("false");
        resultText.Should().Contain("Sampling not available");
    }

    // ========================================================================
    // Helpers
    // ========================================================================

    private static async Task InitializeServerWithSamplingAsync(McpServer server)
    {
        string initRequest = "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{\"protocolVersion\":\"2024-11-05\",\"capabilities\":{\"sampling\":{}},\"clientInfo\":{\"name\":\"test-client\",\"version\":\"1.0\"}}}";
        await server.ProcessMessageAsync(initRequest);
        server.ClearSentRequests();
    }

    private static async Task<string> WaitForSentRequestAsync(McpServer server, int timeoutMs = 3000)
    {
        var sw = Stopwatch.StartNew();
        while (server.SentRequests.Count == 0 && sw.ElapsedMilliseconds < timeoutMs)
        {
            await Task.Delay(20);
        }

        server.SentRequests.Should().NotBeEmpty("Expected MCP server to dispatch a sampling request to client.");
        return server.SentRequests.Last();
    }

    private static async Task RespondToSamplingAsync(McpServer server, long requestId, string responseText, string model = "claude-3-5-sonnet")
    {
        var responseObj = new
        {
            jsonrpc = "2.0",
            id = requestId,
            result = new
            {
                role = "assistant",
                content = new
                {
                    type = "text",
                    text = responseText
                },
                model = model,
                stopReason = "endTurn"
            }
        };

        string clientResponseJson = JsonRpcSerializer.Serialize(responseObj);
        string? res = await server.ProcessMessageAsync(clientResponseJson);
        res.Should().BeNull("Client responses to server-initiated requests must not produce further JSON-RPC responses.");
    }
}
