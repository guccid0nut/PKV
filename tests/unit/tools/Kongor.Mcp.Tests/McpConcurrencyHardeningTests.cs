// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// TAO (Together As One) MCP & Kongor CLI -- Created by guccidonut (https://github.com/guccidonut/PKV)

using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Kongor.Cli.Mcp.Handlers;
using Kongor.Cli.Mcp.Protocol;
using Kongor.Cli.Mcp.Services;
using Moq;
using Xunit;

namespace Kongor.Mcp.Tests;

/// <summary>
/// Tier 5 Adversarial Coverage Hardening Test Suite.
/// Stress-tests high concurrency, extreme boundary and overflow values,
/// malformed payloads, injection vectors, and fault-tolerance across all services and handlers.
/// </summary>
public sealed class McpConcurrencyHardeningTests
{
    private static readonly byte[] PngHeader = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    #region Concurrency & Race Condition Stress Tests

    [Fact]
    public async Task Adversarial_McpServer_HighConcurrency_50ParallelRequests_MaintainCorrelation()
    {
        Mock<IGameIpcBridge> mockIpc = new();
        mockIpc.Setup(i => i.ExecuteCommandAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string cmd, int _, CancellationToken _) => cmd);

        Mock<ILifecycleService> mockLifecycle = new();
        mockLifecycle.Setup(l => l.GetStatusAsync(It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ClusterLifecycleResult { Status = "success", ClusterState = "Running" });

        McpToolRegistry registry = new(
            registerDefaults: true,
            lifecycleService: mockLifecycle.Object,
            gameIpcBridge: mockIpc.Object);

        McpServer server = new(toolRegistry: registry, errorWriter: TextWriter.Null);
        // Initialize server first
        await server.ProcessMessageAsync("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{\"protocolVersion\":\"2024-11-05\"}}");
        await server.ProcessMessageAsync("{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\"}");

        int requestCount = 50;
        List<Task<(int Id, string Response)>> tasks = [];

        for (int i = 0; i < requestCount; i++)
        {
            int reqId = i + 1000;
            string method = (i % 4) switch
            {
                0 => "ping",
                1 => "tools/list",
                2 => "logging/setLevel",
                _ => "tools/call"
            };

            string requestJson = method switch
            {
                "ping" => $"{{\"jsonrpc\":\"2.0\",\"id\":{reqId},\"method\":\"ping\"}}",
                "tools/list" => $"{{\"jsonrpc\":\"2.0\",\"id\":{reqId},\"method\":\"tools/list\"}}",
                "logging/setLevel" => $"{{\"jsonrpc\":\"2.0\",\"id\":{reqId},\"method\":\"logging/setLevel\",\"params\":{{\"level\":\"debug\"}}}}",
                _ => $"{{\"jsonrpc\":\"2.0\",\"id\":{reqId},\"method\":\"tools/call\",\"params\":{{\"name\":\"kongor_game_command\",\"arguments\":{{\"command\":\"echo stress_{reqId}\"}}}}}}"
            };

            tasks.Add(ExecuteRequestAsync(server, reqId, requestJson));
        }

        var results = await Task.WhenAll(tasks);

        results.Length.Should().Be(requestCount);
        foreach (var (reqId, response) in results)
        {
            response.Should().NotBeNullOrWhiteSpace();
            using JsonDocument doc = JsonDocument.Parse(response);
            doc.RootElement.GetProperty("id").GetInt32().Should().Be(reqId);
            doc.RootElement.TryGetProperty("error", out JsonElement errElem).Should().BeFalse();
            doc.RootElement.TryGetProperty("result", out _).Should().BeTrue();
        }
    }

    [Trait("Category", "Manual")]
    [Fact]
    public async Task Adversarial_McpToolRegistry_ConcurrentExecution_AllSixTools_Succeeds()
    {
        if (!NexusTestEnvironment.IsManualTestEnabled) return;
        Mock<IGameIpcBridge> mockIpc = new();
        mockIpc.Setup(i => i.ExecuteCommandAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string cmd, int _, CancellationToken _) => cmd);
        mockIpc.Setup(i => i.GetEngineStateAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new EngineStateDto { CurrentFps = 60 });

        Mock<ILifecycleService> mockLifecycle = new();
        mockLifecycle.Setup(l => l.GetStatusAsync(It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ClusterLifecycleResult { Status = "success", ClusterState = "Running" });

        Mock<IHeadlessUiRenderer> mockRenderer = new();
        mockRenderer.Setup(r => r.RenderViewAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new UiRenderResult { Success = true, ViewName = "DiagnosticsView", Base64Png = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg==" });

        Mock<IWindowCapturer> mockCapturer = new();
        mockCapturer.Setup(c => c.CaptureAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<string?>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new WindowCaptureResult { Success = true, Target = "non_existent", Base64Png = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg==" });

        McpToolRegistry registry = new(
            registerDefaults: true,
            lifecycleService: mockLifecycle.Object,
            gameIpcBridge: mockIpc.Object,
            headlessUiRenderer: mockRenderer.Object,
            windowCapturer: mockCapturer.Object);

        string[] toolNames =
        [
            "kongor_server_manage",
            "kongor_game_manage",
            "kongor_game_command",
            "kongor_game_telemetry",
            "kongor_capture_window",
            "kongor_ui_render_view"
        ];

        List<Task<ToolCallResult>> tasks = [];

        for (int i = 0; i < 30; i++)
        {
            string tool = toolNames[i % toolNames.Length];
            string argsJson = tool switch
            {
                "kongor_server_manage" => "{\"action\":\"status\",\"target\":\"cluster\"}",
                "kongor_game_manage" => "{\"action\":\"status\"}",
                "kongor_game_command" => "{\"command\":\"echo test\"}",
                "kongor_game_telemetry" => "{\"timeoutMs\":100}",
                "kongor_capture_window" => "{\"target\":\"non_existent\",\"save_artifact\":false}",
                "kongor_ui_render_view" => "{\"view_name\":\"DiagnosticsView\",\"save_artifact\":false}",
                _ => "{}"
            };

            tasks.Add(ExecuteToolCallAsync(registry, tool, argsJson));
        }

        ToolCallResult[] results = await Task.WhenAll(tasks);
        results.Length.Should().Be(30);
        foreach (var r in results)
        {
            r.Should().NotBeNull();
            r.Content.Should().NotBeEmpty();
        }
    }

    [Fact]
    public async Task Adversarial_LifecycleService_ConcurrentPortProbing_DoesNotCrash()
    {
        LifecycleService service = new();
        List<Task<bool>> probeTasks = [];

        for (int i = 0; i < 20; i++)
        {
            int port = 55550 + i;
            probeTasks.Add(service.ProbePortAsync("127.0.0.1", port, timeoutMs: 50));
        }

        bool[] results = await Task.WhenAll(probeTasks);
        results.Length.Should().Be(20);
    }

    #endregion

    #region Extreme Boundary Values & Buffer Limit Tests

    [Fact]
    public void Adversarial_IpcMessage_EmptyPayload_SerializesAndDeserializesCorrectly()
    {
        IpcMessage msg = new() { OpCode = IpcOpCode.Ping, Payload = [] };
        byte[] serialized = IpcMessage.Serialize(msg);

        serialized.Length.Should().Be(IpcMessage.HeaderSize);

        bool success = IpcMessage.TryDeserialize(serialized, out IpcMessage? deserialized, out int bytesConsumed);
        success.Should().BeTrue();
        bytesConsumed.Should().Be(IpcMessage.HeaderSize);
        deserialized.Should().NotBeNull();
        deserialized!.OpCode.Should().Be(IpcOpCode.Ping);
        deserialized.Payload.Should().BeEmpty();
    }

    [Fact]
    public void Adversarial_IpcMessage_PayloadExceeding16MB_ThrowsInvalidOperationException()
    {
        byte[] largePayload = new byte[IpcMessage.MaxPayloadSize + 1];
        IpcMessage msg = new() { OpCode = IpcOpCode.ExecuteCommand, Payload = largePayload };

        Action act = () => IpcMessage.Serialize(msg);
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*exceeds maximum limit*");
    }

    [Fact]
    public void Adversarial_IpcMessage_CorruptedMagicHeader_ThrowsInvalidDataException()
    {
        byte[] buffer = new byte[IpcMessage.HeaderSize];
        buffer[0] = 0xAA;
        buffer[1] = 0xBB;

        Action act = () => IpcMessage.TryDeserialize(buffer, out _, out _);
        act.Should().Throw<InvalidDataException>()
            .WithMessage("*Invalid IPC magic header*");
    }

    [Fact]
    public void Adversarial_IpcMessage_NegativePayloadLength_ThrowsInvalidDataException()
    {
        byte[] buffer = new byte[IpcMessage.HeaderSize];
        buffer[0] = 0x4D;
        buffer[1] = 0x57;
        buffer[2] = 0x01;
        buffer[3] = 0x00;
        buffer[4] = 0xFF;
        buffer[5] = 0xFF;
        buffer[6] = 0xFF;
        buffer[7] = 0xFF;

        Action act = () => IpcMessage.TryDeserialize(buffer, out _, out _);
        act.Should().Throw<InvalidDataException>()
            .WithMessage("*Invalid IPC payload length*");
    }

    [Fact]
    public void Adversarial_IpcMessage_PartialFrames_ReturnsFalseWithoutConsuming()
    {
        byte[] partialHeader = [0x4D, 0x57, 0x01, 0x00];
        bool success = IpcMessage.TryDeserialize(partialHeader, out IpcMessage? msg, out int consumed);
        success.Should().BeFalse();
        msg.Should().BeNull();
        consumed.Should().Be(0);

        IpcMessage fullMsg = IpcMessage.CreateString(IpcOpCode.ExecuteCommand, new string('A', 100));
        byte[] fullBytes = IpcMessage.Serialize(fullMsg);
        byte[] truncatedBytes = fullBytes[..18];

        success = IpcMessage.TryDeserialize(truncatedBytes, out msg, out consumed);
        success.Should().BeFalse();
        msg.Should().BeNull();
        consumed.Should().Be(0);
    }

    [Fact]
    public void Adversarial_IpcMessage_MultipleConcatenatedFrames_ParsedSequentially()
    {
        IpcMessage msg1 = IpcMessage.CreateString(IpcOpCode.Ping, "PING1");
        IpcMessage msg2 = IpcMessage.CreateString(IpcOpCode.Pong, "PONG2");
        IpcMessage msg3 = IpcMessage.CreateString(IpcOpCode.ExecuteCommand, "CMD3");

        byte[] b1 = IpcMessage.Serialize(msg1);
        byte[] b2 = IpcMessage.Serialize(msg2);
        byte[] b3 = IpcMessage.Serialize(msg3);

        byte[] stream = new byte[b1.Length + b2.Length + b3.Length];
        Buffer.BlockCopy(b1, 0, stream, 0, b1.Length);
        Buffer.BlockCopy(b2, 0, stream, b1.Length, b2.Length);
        Buffer.BlockCopy(b3, 0, stream, b1.Length + b2.Length, b3.Length);

        int offset = 0;
        List<IpcMessage> deserialized = [];

        while (offset < stream.Length)
        {
            ReadOnlySpan<byte> remaining = stream.AsSpan(offset);
            if (IpcMessage.TryDeserialize(remaining, out IpcMessage? m, out int consumed))
            {
                deserialized.Add(m!);
                offset += consumed;
            }
            else break;
        }

        deserialized.Should().HaveCount(3);
        deserialized[0].PayloadString.Should().Be("PING1");
        deserialized[1].PayloadString.Should().Be("PONG2");
        deserialized[2].PayloadString.Should().Be("CMD3");
    }

    [Trait("Category", "Manual")]
    [Fact]
    public async Task Adversarial_McpServer_OversizedCommand_100KBString_HandlesSafely()
    {
        if (!NexusTestEnvironment.IsManualTestEnabled) return;
        McpServer server = new(errorWriter: TextWriter.Null);
        string largeCommand = new('X', 100_000);

        string requestJson = $"{{\"jsonrpc\":\"2.0\",\"id\":9001,\"method\":\"tools/call\",\"params\":{{\"name\":\"kongor_game_command\",\"arguments\":{{\"command\":\"{largeCommand}\"}}}}}}";

        string? responseJson = await server.ProcessMessageAsync(requestJson);
        responseJson.Should().NotBeNull();

        JsonRpcResponse response = JsonRpcSerializer.Deserialize<JsonRpcResponse>(responseJson!)!;
        response.Id!.Value.Value.Should().Be(9001L);
        response.Error.Should().BeNull();
    }

    [Trait("Category", "Manual")]
    [Fact]
    public async Task Adversarial_McpServer_UnicodeAndControlCharacters_HandlesSafely()
    {
        if (!NexusTestEnvironment.IsManualTestEnabled) return;
        McpServer server = new(errorWriter: TextWriter.Null);
        string trickyString = "Test ðŸ”¥ ðŸŽ® âš”ï¸ \\n \\r \\t \\u0000 \u200E\u200F Unicode RTL & Quotes: \"hello\" 'world'";

        string requestJson = $"{{\"jsonrpc\":\"2.0\",\"id\":9002,\"method\":\"tools/call\",\"params\":{{\"name\":\"kongor_game_command\",\"arguments\":{{\"command\":{JsonSerializer.Serialize(trickyString)}}}}}}}";

        string? responseJson = await server.ProcessMessageAsync(requestJson);
        responseJson.Should().NotBeNull();

        JsonRpcResponse response = JsonRpcSerializer.Deserialize<JsonRpcResponse>(responseJson!)!;
        response.Id!.Value.Value.Should().Be(9002L);
        response.Error.Should().BeNull();
    }

    [Fact]
    public async Task Adversarial_HeadlessUiRenderer_ExtremeDimensions_ClampedSafely()
    {
        HeadlessUiRenderer renderer = new();
        UiRenderResult result = await renderer.RenderViewAsync("DiagnosticsView", width: -99999, height: 999999, saveArtifact: false);

        result.Success.Should().BeTrue();
        result.Width.Should().Be(100);   // Clamped minimum
        result.Height.Should().Be(4320); // Clamped maximum
        result.Base64Png.Should().NotBeNullOrWhiteSpace();
    }

    #endregion

    #region Malformed Protocol & Injection Hardening

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t\r\n")]
    public async Task Adversarial_McpServer_WhitespaceOnlyLines_IgnoredInStdioLoop(string whitespace)
    {
        StringWriter errorLog = new();
        McpServer server = new(errorWriter: errorLog);

        using StringReader input = new(whitespace);
        using StringWriter output = new();

        await server.RunAsync(input, output, CancellationToken.None);

        output.ToString().Should().BeEmpty();
    }

    [Theory]
    [InlineData("{\"id\": 1, \"method\": \"ping\"}")]
    [InlineData("{\"jsonrpc\": \"1.0\", \"id\": 1, \"method\": \"ping\"}")]
    [InlineData("{\"jsonrpc\": \"2.0\", \"id\": 1}")]
    public async Task Adversarial_McpServer_InvalidJsonRpcHeaders_ReturnsInvalidRequest(string invalidPayload)
    {
        McpServer server = new(errorWriter: TextWriter.Null);
        string? responseJson = await server.ProcessMessageAsync(invalidPayload);

        responseJson.Should().NotBeNull();
        JsonRpcResponse response = JsonRpcSerializer.Deserialize<JsonRpcResponse>(responseJson!)!;
        response.Error.Should().NotBeNull();
        response.Error!.Code.Should().Be(JsonRpcErrorCode.InvalidRequest);
    }

    [Fact]
    public async Task Adversarial_McpServer_UnknownToolName_ReturnsStructuredToolError()
    {
        McpServer server = new(errorWriter: TextWriter.Null);
        string requestJson = "{\"jsonrpc\":\"2.0\",\"id\":9003,\"method\":\"tools/call\",\"params\":{\"name\":\"__malicious_injected_tool__\",\"arguments\":{}}}";

        string? responseJson = await server.ProcessMessageAsync(requestJson);
        responseJson.Should().NotBeNull();

        JsonRpcResponse response = JsonRpcSerializer.Deserialize<JsonRpcResponse>(responseJson!)!;
        response.Id!.Value.Value.Should().Be(9003L);
        response.Result.Should().NotBeNull();

        string resultText = JsonRpcSerializer.Serialize(response.Result);
        resultText.Should().Contain("\"isError\":true");
        resultText.Should().Contain("Unknown tool '__malicious_injected_tool__'");
    }

    [Theory]
    [InlineData("localhost", "127.0.0.1:55555")]
    [InlineData("127.0.0.1:55555", "127.0.0.1:55555")]
    [InlineData("http://localhost:55555/", "127.0.0.1:55555")]
    [InlineData("https://localhost:55555", "127.0.0.1:55555")]
    [InlineData("masterserver.newbs.io", "masterserver.newbs.io")]
    [InlineData(null, "127.0.0.1:55555")]
    [InlineData("   ", "127.0.0.1:55555")]
    public void Adversarial_GameLauncher_NormalizeMasterServerAddress_AllEdgeCases(string? input, string expected)
    {
        string actual = GameLauncherService.NormalizeMasterServerAddress(input);
        actual.Should().Be(expected);
    }

    [Theory]
    [InlineData("cluster", "cluster")]
    [InlineData("ALL", "all")]
    [InlineData("  master-server  ", "master-server")]
    [InlineData("kongor", "master-server")]
    [InlineData("chat", "chat-server")]
    [InlineData("transmutanstein", "chat-server")]
    [InlineData("api", "web-portal-api")]
    [InlineData("zorgath", "web-portal-api")]
    [InlineData("ui", "web-portal-ui")]
    [InlineData("dawnbringer", "web-portal-ui")]
    [InlineData("gameservers", "compel")]
    [InlineData("compel", "compel")]
    [InlineData("redis", "redis")]
    [InlineData("database", "database")]
    [InlineData("merrick", "database")]
    [InlineData("pyroscope", "pyroscope")]
    [InlineData("UNKNOWN_CUSTOM_SERVICE", "unknown_custom_service")]
    public async Task Adversarial_LifecycleService_NormalizeTarget_ComprehensiveMapping(string target, string expectedNormalized)
    {
        LifecycleService service = new();
        ClusterLifecycleResult result = await service.GetStatusAsync(target);

        result.Status.Should().Be("success");
        result.Target.Should().Be(expectedNormalized);
    }

    #endregion

    #region Fault Tolerance & Disconnection Handling

    [Fact]
    public async Task Adversarial_GameIpcBridge_Disconnect_FailsPendingRequestsGracefully()
    {
        GameIpcBridge bridge = new("non_existent_test_pipe_99999");

        Func<Task> sendAct = async () => await bridge.SendMessageAsync(IpcMessage.CreateString(IpcOpCode.Ping, "PING"));
        await sendAct.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*not connected*");

        await bridge.DisconnectAsync();
        bridge.IsConnected.Should().BeFalse();
    }

    [Fact]
    public async Task Adversarial_WindowCapturer_InvalidHwnds_ReturnGracefulErrors()
    {
        WindowCapturer capturer = new();

        WindowCaptureResult r1 = await capturer.CaptureHwndAsync(IntPtr.Zero);
        r1.Success.Should().BeFalse();
        r1.ErrorMessage.Should().Contain("Invalid or destroyed window handle");

        WindowCaptureResult r2 = await capturer.CaptureHwndAsync(new IntPtr(-1));
        r2.Success.Should().BeFalse();
        r2.ErrorMessage.Should().Contain("Invalid or destroyed window handle");
    }

    [Fact]
    public async Task Adversarial_GameManageHandler_InvalidActions_HandledGracefully()
    {
        GameManageHandler handler = new();
        using JsonDocument doc = JsonDocument.Parse("""{"action": "completely_invalid_action_xyz"}""");

        ToolCallResult result = await handler.HandleAsync(doc.RootElement);
        result.IsError.Should().BeFalse();
        result.Content.Should().NotBeEmpty();
    }

    [Fact]
    public async Task Adversarial_ServerManageHandler_InvalidActions_HandledGracefully()
    {
        ServerManageHandler handler = new();
        using JsonDocument doc = JsonDocument.Parse("""{"action": "completely_invalid_action_abc"}""");

        ToolCallResult result = await handler.HandleAsync(doc.RootElement);
        result.IsError.Should().BeFalse();
        result.Content.Should().NotBeEmpty();
    }

    #endregion

    private static async Task<(int Id, string Response)> ExecuteRequestAsync(McpServer server, int reqId, string requestJson)
    {
        string? response = await server.ProcessMessageAsync(requestJson).ConfigureAwait(false);
        return (reqId, response ?? string.Empty);
    }

    private static async Task<ToolCallResult> ExecuteToolCallAsync(McpToolRegistry registry, string tool, string argsJson)
    {
        using JsonDocument doc = JsonDocument.Parse(argsJson);
        return await registry.ExecuteToolAsync(tool, doc.RootElement).ConfigureAwait(false);
    }
}
