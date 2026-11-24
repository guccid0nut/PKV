// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// TAO (Together As One) MCP & Kongor CLI -- Created by guccidonut (https://github.com/guccidonut/PKV)

using System.Buffers.Binary;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Kongor.Cli.Mcp.Protocol;
using Kongor.Cli.Mcp.Services;
using Moq;
using NEXUS.Contracts.Ipc;
using Xunit;

namespace Kongor.Mcp.Tests;

public sealed class NativeClientIpcTests
{
    [Fact]
    public async Task Constructor_Defaults_InitializesNativePipeName()
    {
        await using NativeClientIpcBridge bridge = new();

        bridge.PipeName.Should().Be("kongor-native-client");
        bridge.ServerName.Should().Be(".");
        bridge.IsConnected.Should().BeFalse();
        bridge.IsAuthenticated.Should().BeFalse();
    }

    [Fact]
    public void McpToolRegistry_InitializesWithCompositeBridge_ContainingNativeClientIpcBridge()
    {
        McpToolRegistry registry = new(registerDefaults: true);
        registry.Should().NotBeNull();
        registry.GameIpcBridge.Should().BeOfType<CompositeGameIpcBridge>();

        var composite = (CompositeGameIpcBridge)registry.GameIpcBridge;
        composite.Primary.Should().BeOfType<NativeClientIpcBridge>();
        composite.Secondary.Should().BeOfType<McpServerIpcBridge>();
        composite.Tertiary.Should().BeOfType<NexusMailboxIpcBridge>();
    }

    [Fact]
    public async Task ExecuteCommandAsync_WithInvalidInput_ThrowsOnNullOrEmpty()
    {
        await using NativeClientIpcBridge bridge = new();

        var actNull = () => bridge.ExecuteCommandAsync(null!);
        await actNull.Should().ThrowAsync<ArgumentException>();

        var actEmpty = () => bridge.ExecuteCommandAsync("   ");
        await actEmpty.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task ExecuteLuaAsync_WithInvalidInput_ThrowsOnNullOrEmpty()
    {
        await using NativeClientIpcBridge bridge = new();

        var actNull = () => bridge.ExecuteLuaAsync(null!);
        await actNull.Should().ThrowAsync<ArgumentException>();

        var actEmpty = () => bridge.ExecuteLuaAsync("   ");
        await actEmpty.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task DispatchUiEventAsync_WithInvalidInput_ThrowsOnNullOrEmpty()
    {
        await using NativeClientIpcBridge bridge = new();

        var actNull = () => bridge.DispatchUiEventAsync("main", null!, "click");
        await actNull.Should().ThrowAsync<ArgumentException>();

        var actEmpty = () => bridge.DispatchUiEventAsync("main", "   ", "click");
        await actEmpty.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task CompositeGameIpcBridge_3Tier_RoutesCommandToPrimary_WhenPrimaryIsConnected()
    {
        Mock<IGameIpcBridge> mockPrimary = new();
        Mock<IGameIpcBridge> mockSecondary = new();
        Mock<IGameIpcBridge> mockTertiary = new();

        mockPrimary.SetupGet(b => b.IsConnected).Returns(true);
        mockPrimary.Setup(b => b.ExecuteCommandAsync("echo hello", It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("echoed: hello");

        await using CompositeGameIpcBridge composite = new(
            mockPrimary.Object,
            mockSecondary.Object,
            mockTertiary.Object);

        string result = await composite.ExecuteCommandAsync("echo hello");
        result.Should().Be("echoed: hello");

        mockPrimary.Verify(b => b.ExecuteCommandAsync("echo hello", It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Once);
        mockSecondary.Verify(b => b.ExecuteCommandAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
        mockTertiary.Verify(b => b.ExecuteCommandAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task CompositeGameIpcBridge_3Tier_FallsBackToSecondary_WhenPrimaryThrows()
    {
        Mock<IGameIpcBridge> mockPrimary = new();
        Mock<IGameIpcBridge> mockSecondary = new();
        Mock<IGameIpcBridge> mockTertiary = new();

        mockPrimary.SetupGet(b => b.IsConnected).Returns(true);
        mockPrimary.Setup(b => b.ExecuteCommandAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new IOException("Pipe broke"));

        mockSecondary.SetupGet(b => b.IsConnected).Returns(true);
        mockSecondary.Setup(b => b.ExecuteCommandAsync("echo fallback", It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("secondary echoed");

        await using CompositeGameIpcBridge composite = new(
            mockPrimary.Object,
            mockSecondary.Object,
            mockTertiary.Object);

        string result = await composite.ExecuteCommandAsync("echo fallback");
        result.Should().Be("secondary echoed");

        mockPrimary.Verify(b => b.ExecuteCommandAsync("echo fallback", It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Once);
        mockSecondary.Verify(b => b.ExecuteCommandAsync("echo fallback", It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Once);
        mockTertiary.Verify(b => b.ExecuteCommandAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task CompositeGameIpcBridge_3Tier_FallsBackToTertiary_WhenPrimaryAndSecondaryThrow()
    {
        Mock<IGameIpcBridge> mockPrimary = new();
        Mock<IGameIpcBridge> mockSecondary = new();
        Mock<IGameIpcBridge> mockTertiary = new();

        mockPrimary.SetupGet(b => b.IsConnected).Returns(true);
        mockPrimary.Setup(b => b.ExecuteCommandAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new IOException("Pipe broke"));

        mockSecondary.SetupGet(b => b.IsConnected).Returns(true);
        mockSecondary.Setup(b => b.ExecuteCommandAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new IOException("Server offline"));

        mockTertiary.SetupGet(b => b.IsConnected).Returns(true);
        mockTertiary.Setup(b => b.ExecuteCommandAsync("echo mailbox", It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("mailbox echoed");

        await using CompositeGameIpcBridge composite = new(
            mockPrimary.Object,
            mockSecondary.Object,
            mockTertiary.Object);

        string result = await composite.ExecuteCommandAsync("echo mailbox");
        result.Should().Be("mailbox echoed");

        mockTertiary.Verify(b => b.ExecuteCommandAsync("echo mailbox", It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task CompositeGameIpcBridge_ExecuteLua_PrioritizesNativeThenMailbox()
    {
        Mock<IGameIpcBridge> mockNative = new();
        Mock<IGameIpcBridge> mockServer = new();
        Mock<IGameIpcBridge> mockMailbox = new();

        mockNative.SetupGet(b => b.IsConnected).Returns(true);
        mockNative.Setup(b => b.ExecuteLuaAsync("Echo('Lua')", It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("native lua executed");

        await using CompositeGameIpcBridge composite = new(
            mockNative.Object,
            mockServer.Object,
            mockMailbox.Object);

        string result = await composite.ExecuteLuaAsync("Echo('Lua')");
        result.Should().Be("native lua executed");

        mockNative.Verify(b => b.ExecuteLuaAsync("Echo('Lua')", It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Once);
        mockMailbox.Verify(b => b.ExecuteLuaAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task NativeClientIpcBridge_SimulatedPipeServer_ExecutesCommandAndParsesResponse()
    {
        string pipeName = "test-native-pipe-" + Guid.NewGuid().ToString("N");
        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(10));

        await using NamedPipeServerStream server = new(
            pipeName,
            PipeDirection.InOut,
            1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.WriteThrough);

        Task waitForConnectionTask = server.WaitForConnectionAsync(cts.Token);

        // Start simulated in-process pipe server mimicking version.dll
        var serverTask = Task.Run(async () =>
        {
            await waitForConnectionTask;

            byte[] header = new byte[12];
            await server.ReadExactlyAsync(header.AsMemory(0, 12), cts.Token);

            ushort magic = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(0, 2));
            ushort opCode = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(2, 2));
            int payloadLen = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(4, 4));
            uint reqId = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(8, 4));

            magic.Should().Be(0x574D);
            opCode.Should().Be((ushort)Kongor.Cli.Mcp.Services.IpcOpCode.ExecuteCommand);

            byte[] payload = new byte[payloadLen];
            await server.ReadExactlyAsync(payload.AsMemory(0, payloadLen), cts.Token);
            string cmd = Encoding.UTF8.GetString(payload);
            cmd.Should().Be("StartGame practice");

            // Write back response frame
            string respStr = "{\"id\":" + reqId + ",\"success\":true,\"result\":\"Match started\"}";
            byte[] respPayload = Encoding.UTF8.GetBytes(respStr);
            byte[] respFrame = new byte[12 + respPayload.Length];
            BinaryPrimitives.WriteUInt16LittleEndian(respFrame.AsSpan(0, 2), 0x574D);
            BinaryPrimitives.WriteUInt16LittleEndian(respFrame.AsSpan(2, 2), (ushort)Kongor.Cli.Mcp.Services.IpcOpCode.CommandResponse);
            BinaryPrimitives.WriteInt32LittleEndian(respFrame.AsSpan(4, 4), respPayload.Length);
            BinaryPrimitives.WriteUInt32LittleEndian(respFrame.AsSpan(8, 4), reqId);
            respPayload.CopyTo(respFrame.AsSpan(12));

            await server.WriteAsync(respFrame.AsMemory(0, respFrame.Length), cts.Token);
            await server.FlushAsync(cts.Token);
            if (OperatingSystem.IsWindows())
            {
                try { server.WaitForPipeDrain(); } catch { }
            }
            await Task.Delay(50, cts.Token);
        }, cts.Token);

        await using NativeClientIpcBridge client = new(pipeName: pipeName);
        await client.ConnectAsync(timeoutMs: 3000, cts.Token);

        string response = await client.ExecuteCommandAsync("StartGame practice", timeoutMs: 3000, cts.Token);
        response.Should().Contain("Match started");

        await serverTask;
    }

    [Fact]
    public async Task NativeClientIpcBridge_SimulatedPipeServer_ExecutesLuaSyncAndUnwrapsResult()
    {
        // Protects: 1-frame synchronous native Lua evaluation (OpCode 0x000E -> 0x000F) without CConsole::Execute mangling (; / "" / {} / [])
        // Fails when: Lua code is wrapped in 'Script ...', semicolons are stripped, or EvalLuaResponse JSON envelope is not unwrapped
        // Seam: Real NamedPipeServerStream <-> NativeClientIpcBridge binary framing
        string pipeName = "test-native-pipe-" + Guid.NewGuid().ToString("N");
        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(10));
        const string complexLua = "local t = { a = 1, b = \"two;three\" }; return tostring(t['a']) .. ':' .. t.b";

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

            ushort magic = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(0, 2));
            ushort opCode = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(2, 2));
            int payloadLen = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(4, 4));
            uint reqId = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(8, 4));

            magic.Should().Be(0x574D);
            opCode.Should().Be((ushort)Kongor.Cli.Mcp.Services.IpcOpCode.EvalLuaSync);

            byte[] payload = new byte[payloadLen];
            await server.ReadExactlyAsync(payload.AsMemory(0, payloadLen), cts.Token);
            string receivedLua = Encoding.UTF8.GetString(payload);
            receivedLua.Should().Be(complexLua);

            string respStr = "{\"id\":" + reqId + ",\"success\":true,\"result\":\"1:two;three\"}";
            byte[] respPayload = Encoding.UTF8.GetBytes(respStr);
            byte[] respFrame = new byte[12 + respPayload.Length];
            BinaryPrimitives.WriteUInt16LittleEndian(respFrame.AsSpan(0, 2), 0x574D);
            BinaryPrimitives.WriteUInt16LittleEndian(respFrame.AsSpan(2, 2), (ushort)Kongor.Cli.Mcp.Services.IpcOpCode.EvalLuaResponse);
            BinaryPrimitives.WriteInt32LittleEndian(respFrame.AsSpan(4, 4), respPayload.Length);
            BinaryPrimitives.WriteUInt32LittleEndian(respFrame.AsSpan(8, 4), reqId);
            respPayload.CopyTo(respFrame.AsSpan(12));

            await server.WriteAsync(respFrame.AsMemory(0, respFrame.Length), cts.Token);
            await server.FlushAsync(cts.Token);
            if (OperatingSystem.IsWindows())
            {
                try { server.WaitForPipeDrain(); } catch { }
            }
            await Task.Delay(50, cts.Token);
        }, cts.Token);

        await using NativeClientIpcBridge client = new(pipeName: pipeName);
        await client.ConnectAsync(timeoutMs: 3000, cts.Token);

        string result = await client.ExecuteLuaAsync(complexLua, timeoutMs: 3000, cts.Token);
        result.Should().Be("1:two;three");

        await serverTask;
    }

    [Trait("Category", "Manual")]
    [Fact]
    public async Task LiveClient_WhenRunning_ConnectsAndExecutesPingAndCommand()
    {
        if (!NexusTestEnvironment.IsManualTestEnabled) return;

        await using NativeClientIpcBridge bridge = new();
        await bridge.ConnectAsync(timeoutMs: 2000);
        bridge.IsConnected.Should().BeTrue();

        var state = await bridge.GetEngineStateAsync(timeoutMs: 2000);
        state.Should().NotBeNull();

        string resp = await bridge.ExecuteCommandAsync("Echo [LiveIntegrationTest] Passed", timeoutMs: 2000);
        resp.Should().Contain("success");
    }
}
