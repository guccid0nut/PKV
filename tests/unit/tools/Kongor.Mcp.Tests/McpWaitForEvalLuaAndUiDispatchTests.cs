// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// TAO (Together As One) MCP & Kongor CLI -- Created by guccidonut (https://github.com/guccidonut/PKV)

using System.IO.Pipes;
using System.Text.Json;
using FluentAssertions;
using Kongor.Cli.Mcp.Handlers;
using Kongor.Cli.Mcp.Protocol;
using Kongor.Cli.Mcp.Services;
using Moq;
using Xunit;

namespace Kongor.Mcp.Tests;

/// <summary>
/// Wave 5A regression tests verifying MCP-198, MCP-231, MCP-232, and MCP-234 contracts.
/// </summary>
public sealed class McpWaitForEvalLuaAndUiDispatchTests
{
    [Fact]
    public void Mcp198_WaitForSchema_ExposesIpcReadyAndClientReadyConditions()
    {
        // protects: kongor_wait_for JSON input schema condition enum parity with WaitForHandler
        // fails_when: CreateWaitForDefinition omits 'ipc_ready' or 'client_ready', causing schema rejection before handler dispatch
        // seam: McpToolRegistry.CreateWaitForDefinition
        ToolDefinition definition = McpToolRegistry.CreateWaitForDefinition();
        definition.InputSchema.Properties.Should().ContainKey("condition");

        ToolPropertySchema conditionProp = definition.InputSchema.Properties["condition"];
        conditionProp.Enum.Should().NotBeNull();
        conditionProp.Enum.Should().Contain(["ipc_ready", "engine_ready", "client_ready", "ui_visible", "ui_hidden", "ui_text", "ui_present", "lua"]);
        conditionProp.Description.Should().Contain("ipc_ready").And.Contain("client_ready");
    }

    [Theory]
    [InlineData("return GetTime() > 0", "GetTime() > 0")]
    [InlineData("  return (a == 1);  ", "(a == 1)")]
    [InlineData("return 'ready';;", "'ready'")]
    [InlineData("returnValue > 5", "returnValue > 5")]
    [InlineData("return_count == 2", "return_count == 2")]
    [InlineData("return", "nil")]
    [InlineData("", "nil")]
    public void Mcp198_NormalizeLuaWaitExpression_StripsLeadingReturnKeywordWithoutBreakingIdentifiers(
        string rawTarget,
        string expectedNormalized)
    {
        // protects: WaitForHandler Lua condition wrapping ('return tostring((<expr>))')
        // fails_when: leading 'return ' produces invalid Lua ('return tostring((return ...))') or strips 'returnValue'
        // seam: WaitForHandler.NormalizeLuaWaitExpression
        WaitForHandler.NormalizeLuaWaitExpression(rawTarget).Should().Be(expectedNormalized);
    }

    [Fact]
    public async Task Mcp198_WaitForHandler_LuaConditionWithLeadingReturn_DispatchesValidLuaExpression()
    {
        // protects: end-to-end WaitForHandler condition='lua' evaluation when caller includes 'return '
        // fails_when: WaitForHandler forwards unstripped 'return GetTime() > 0' inside tostring((...))
        // seam: WaitForHandler + IGameIpcBridge
        string? capturedLua = null;
        Mock<IGameIpcBridge> mockBridge = new();
        mockBridge.SetupGet(x => x.IsConnected).Returns(true);
        mockBridge.Setup(x => x.ExecuteLuaAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Callback<string, int, CancellationToken>((lua, _, _) => capturedLua = lua)
            .ReturnsAsync("true");

        WaitForHandler handler = new(taoClient: null, gameIpcBridge: mockBridge.Object);
        using JsonDocument doc = JsonDocument.Parse("""
        {
            "condition": "lua",
            "target": "return GetTime() > 0;",
            "timeoutMs": 500
        }
        """);

        ToolCallResult result = await handler.HandleAsync(doc.RootElement, CancellationToken.None);
        result.IsError.Should().BeFalse(result.FirstText);
        capturedLua.Should().Be("return tostring((GetTime() > 0))");

        using JsonDocument outDoc = JsonDocument.Parse(result.FirstText);
        outDoc.RootElement.GetProperty("satisfied").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task Mcp231_ClientEvalLuaHandler_FallbackCommandAvoidsLegacySpinWaitTriggerAndPreservesSemicolons()
    {
        // protects: ClientEvalLuaHandler 0x0003 fallback script wrapping and semicolon literal integrity
        // fails_when: fallback emits literal 'pcall(loadstring(' (triggering 3000ms x 3 spin-wait on legacy version.dll) or strips ';' inside Lua strings
        // seam: ClientEvalLuaHandler + IGameIpcBridge
        string? capturedFallbackCmd = null;
        Mock<IGameIpcBridge> mockBridge = new();
        mockBridge.SetupGet(x => x.IsConnected).Returns(true);
        mockBridge.Setup(x => x.ExecuteLuaAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string)null!);
        mockBridge.Setup(x => x.ExecuteCommandAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Callback<string, int, CancellationToken>((cmd, _, _) => capturedFallbackCmd = cmd)
            .ReturnsAsync("[LUA_OK] alpha;beta;gamma");

        ClientEvalLuaHandler handler = new(ipcBridge: mockBridge.Object, enablePreSendEngineGate: false);
        const string luaWithSemicolons = "local s = \"alpha;beta;gamma\"; UICmd(\"Foo(); Bar();\"); return s";
        using JsonDocument doc = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            action = "eval",
            code = luaWithSemicolons
        }));

        ToolCallResult result = await handler.HandleAsync(doc.RootElement, CancellationToken.None);
        result.IsError.Should().BeFalse(result.FirstText);
        capturedFallbackCmd.Should().NotBeNull();
        capturedFallbackCmd.Should().NotContain("pcall(loadstring(",
            "fallback command must separate loadstring and pcall so legacy version.dll never enters the 3000ms x 3 KongorSharedMem spin-wait");
        capturedFallbackCmd.Should().Contain("alpha;beta;gamma");
        capturedFallbackCmd.Should().Contain("Foo(); Bar();");
    }

    [Fact]
    public void Mcp231_NormalizeRawLua_PreservesCompoundQuotedStringLiteralsWithSemicolons()
    {
        // protects: NormalizeRawLua quote unwrapping when Lua expression starts and ends with separate string literals
        // fails_when: naive outer-quote stripping corrupts '"a;b" .. "c;d"' into 'a;b" .. "c;d'
        // seam: ClientEvalLuaHandler.NormalizeRawLua
        const string compoundExpr = "\"a;b\" .. \";\" .. \"c;d\"";
        ClientEvalLuaHandler.NormalizeRawLua(compoundExpr).Should().Be(compoundExpr);
    }

    [Fact]
    public async Task Mcp232_ClientPracticeHandler_DefaultTimeoutIs30000MsAndClampsUpTo120000Ms()
    {
        // protects: kongor_client_practice cold map-load timeout budget (~17s caldavar load)
        // fails_when: default timeoutMs reverts to 15000ms or clamps above 60000ms instead of 120000ms
        // seam: ClientPracticeHandler + McpToolRegistry.CreateClientPracticeDefinition
        ToolDefinition schema = McpToolRegistry.CreateClientPracticeDefinition();
        schema.InputSchema.Properties["timeoutMs"].Default.Should().Be(30000);

        ClientPracticeHandler handler = new();
        using (JsonDocument defaultDoc = JsonDocument.Parse("""{"hero": "Hero_Devourer", "map": "caldavar", "dryRun": true}"""))
        {
            ToolCallResult res = await handler.HandleAsync(defaultDoc.RootElement, CancellationToken.None);
            res.IsError.Should().BeFalse(res.FirstText);
            using JsonDocument outDoc = JsonDocument.Parse(res.FirstText);
            outDoc.RootElement.GetProperty("timeoutMs").GetInt32().Should().Be(30000);
        }

        using (JsonDocument largeDoc = JsonDocument.Parse("""{"hero": "Hero_Devourer", "timeoutMs": 90000, "dryRun": true}"""))
        {
            ToolCallResult res = await handler.HandleAsync(largeDoc.RootElement, CancellationToken.None);
            res.IsError.Should().BeFalse(res.FirstText);
            using JsonDocument outDoc = JsonDocument.Parse(res.FirstText);
            outDoc.RootElement.GetProperty("timeoutMs").GetInt32().Should().Be(90000);
        }
    }

    [Fact]
    public async Task Mcp234_NativeClientIpcBridge_DispatchUiEventAsync_SendsOpCode0x0009WithEscapedPayloadAndSupportsTemplateFallback()
    {
        // protects: NativeClientIpcBridge.DispatchUiEventAsync binary IPC framing (OpCode 0x0009) and quote/template safety
        // fails_when: NativeClientIpcBridge interpolates raw single quotes into Lua or omits onclicklua/GetCallback template support
        // seam: Real NamedPipeServerStream <-> NativeClientIpcBridge
        string pipeName = $"test-native-pipe-ui-{Guid.NewGuid():N}";
        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(5));
        string? receivedJson = null;

        await using NamedPipeServerStream server = new(
            pipeName,
            PipeDirection.InOut,
            1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.WriteThrough);

        Task waitForConnectionTask = server.WaitForConnectionAsync(cts.Token);

        Task serverTask = Task.Run(async () =>
        {
            await waitForConnectionTask;

            byte[] header = new byte[12];
            await server.ReadExactlyAsync(header.AsMemory(0, 12), cts.Token);
            ushort magic = System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(0, 2));
            ushort opCode = System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(2, 2));
            int payloadLen = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(4, 4));
            uint reqId = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(8, 4));

            magic.Should().Be(0x574D);
            opCode.Should().Be((ushort)Kongor.Cli.Mcp.Services.IpcOpCode.DispatchUiEvent);
            byte[] payload = new byte[payloadLen];
            await server.ReadExactlyAsync(payload.AsMemory(0, payloadLen), cts.Token);
            receivedJson = System.Text.Encoding.UTF8.GetString(payload);

            byte[] respPayload = System.Text.Encoding.UTF8.GetBytes("[OK] UI event queued");
            byte[] respFrame = new byte[12 + respPayload.Length];
            System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(respFrame.AsSpan(0, 2), 0x574D);
            System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(respFrame.AsSpan(2, 2), (ushort)Kongor.Cli.Mcp.Services.IpcOpCode.CommandResponse);
            System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(respFrame.AsSpan(4, 4), respPayload.Length);
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(respFrame.AsSpan(8, 4), reqId);
            respPayload.CopyTo(respFrame.AsSpan(12));

            await server.WriteAsync(respFrame.AsMemory(0, respFrame.Length), cts.Token);
            await server.FlushAsync(cts.Token);
            if (OperatingSystem.IsWindows())
            {
                try { server.WaitForPipeDrain(); } catch (IOException) { }
            }
            await Task.Delay(25, cts.Token);
        }, cts.Token);

        await using NativeClientIpcBridge bridge = new(pipeName: pipeName);
        await bridge.ConnectAsync(2000, cts.Token);

        string response = await bridge.DispatchUiEventAsync("store'v4\\main", "playerstats_tab_mastery'btn", "onclick", null, 2000, cts.Token);
        await serverTask;

        response.Should().Contain("[OK]");
        receivedJson.Should().NotBeNull();
        using JsonDocument parsed = JsonDocument.Parse(receivedJson!);
        parsed.RootElement.GetProperty("interfaceName").GetString().Should().Be("store'v4\\main");
        parsed.RootElement.GetProperty("widgetName").GetString().Should().Be("playerstats_tab_mastery'btn");

        string fallbackLua = NativeClientIpcBridge.BuildSafeUiDispatchLua("store'v4\\main", "playerstats_tab_mastery'btn", "click");
        fallbackLua.Should().Contain(@"store\'v4\\main");
        fallbackLua.Should().Contain(@"playerstats_tab_mastery\'btn_button");
        fallbackLua.Should().Contain("GetCallback");
        fallbackLua.Should().Contain("onclicklua");
    }

    [Fact]
    public async Task P38a_ClientEvalLuaHandler_WhenDuplexFailsWithEmptyErrorPayload_DoesNotDoubleExecuteOn0x000EFallback()
    {
        // protects: Mutating Lua RPC single-execution guarantee when 0x0080 duplex RPC returns ok=false with empty error payload
        // fails_when: ClientEvalLuaHandler or CompositeGameIpcBridge treats empty ResultOrError (RequestId > 0, TransportUnsupported=false) as unsupported transport and re-executes on 0x000E / secondary bridge
        // seam: ClientEvalLuaHandler + CompositeGameIpcBridge + DuplexLuaRpcResult.IsUninitializedDefault
        Mock<IGameIpcBridge> primary = new();
        primary.SetupGet(x => x.IsConnected).Returns(true);
        primary.Setup(x => x.EvalLuaDuplexAsync(It.IsAny<string>(), It.IsAny<uint>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DuplexLuaRpcResult(Success: false, TimedOut: false, RequestId: 1u, ElapsedMicroseconds: 120u, ResultOrError: string.Empty, TransportUnsupported: false));

        Mock<IGameIpcBridge> secondary = new();
        secondary.SetupGet(x => x.IsConnected).Returns(true);

        await using CompositeGameIpcBridge composite = new(primary.Object, secondary.Object);
        ClientEvalLuaHandler handler = new(ipcBridge: composite, enablePreSendEngineGate: false);

        using JsonDocument doc = JsonDocument.Parse("""{"action": "eval", "code": "Cmd('BuyItem(1)')"}""");
        ToolCallResult res = await handler.HandleAsync(doc.RootElement, CancellationToken.None);

        res.IsError.Should().BeTrue("duplex ok=false is a real Lua failure and must return an error without re-executing");
        primary.Verify(x => x.EvalLuaDuplexAsync(It.IsAny<string>(), It.IsAny<uint>(), It.IsAny<CancellationToken>()), Times.Once);
        primary.Verify(x => x.ExecuteLuaAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
        primary.Verify(x => x.ExecuteCommandAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
        secondary.Verify(x => x.EvalLuaDuplexAsync(It.IsAny<string>(), It.IsAny<uint>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task P38a_ClientEvalLuaHandler_WhenDuplexTransportUnsupported_FallsBackCleanlyTo0x000E()
    {
        // protects: Backward compatibility with legacy pipes where 0x0080 duplex RPC sets TransportUnsupported=true
        // fails_when: ClientEvalLuaHandler fails to fall back to 0x000E ExecuteLuaAsync when TransportUnsupported=true
        // seam: ClientEvalLuaHandler + DuplexLuaRpcResult.TransportUnsupported
        Mock<IGameIpcBridge> mockBridge = new();
        mockBridge.SetupGet(x => x.IsConnected).Returns(true);
        mockBridge.Setup(x => x.EvalLuaDuplexAsync(It.IsAny<string>(), It.IsAny<uint>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DuplexLuaRpcResult(Success: false, TimedOut: false, RequestId: 1u, ElapsedMicroseconds: 0u, ResultOrError: string.Empty, TransportUnsupported: true));
        mockBridge.Setup(x => x.ExecuteLuaAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("[LUA_OK] 42");

        ClientEvalLuaHandler handler = new(ipcBridge: mockBridge.Object, enablePreSendEngineGate: false);
        using JsonDocument doc = JsonDocument.Parse("""{"action": "eval", "code": "return 42"}""");
        ToolCallResult res = await handler.HandleAsync(doc.RootElement, CancellationToken.None);

        res.IsError.Should().BeFalse(res.FirstText);
        mockBridge.Verify(x => x.ExecuteLuaAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Once);
    }
}
