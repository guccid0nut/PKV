// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// TAO (Together As One) MCP & Kongor CLI -- Created by guccidonut (https://github.com/guccidonut/PKV)

// ============================================================================
// File: SubTickNativeGuardTests.cs
// System: Kongor MCP Tests - Sub-Tick Native Guard & Ring Buffer Emission (Phase 14D)
// Policy: Strict Explicit Typing (NO 'var')
// ============================================================================

using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Kongor.Cli.Mcp.Handlers;
using Kongor.Cli.Mcp.Protocol;
using Kongor.Cli.Mcp.Services;
using Moq;
using NEXUS.Contracts.Ipc;
using Xunit;

namespace Kongor.Mcp.Tests;

public sealed class SubTickNativeGuardTests
{
    [Fact]
    public async Task NativeClientIpcBridge_InjectSubTickInput_SendsOpCode0x0012_AndParsesResponse0x0013()
    {
        string pipeName = "test-subtick-pipe-" + Guid.NewGuid().ToString("N");
        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(15));

        long simulatedNowQpc = 9876543210L;

        NamedPipeServerStream server = new(
            pipeName,
            PipeDirection.InOut,
            1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.WriteThrough);

        Task serverTask = Task.Run(async () =>
        {
            await using (server)
            {
                await server.WaitForConnectionAsync(cts.Token);

                byte[] header = new byte[12];
                await server.ReadExactlyAsync(header.AsMemory(0, 12), cts.Token);

                ushort magic = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(0, 2));
                ushort opCode = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(2, 2));
                int payloadLen = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(4, 4));
                uint reqId = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(8, 4));

                magic.Should().Be(0x574D);
                opCode.Should().Be((ushort)Kongor.Cli.Mcp.Services.IpcOpCode.InjectSubTickInput);

                byte[] payload = new byte[payloadLen];
                await server.ReadExactlyAsync(payload.AsMemory(0, payloadLen), cts.Token);
                string jsonPayload = Encoding.UTF8.GetString(payload);

                SubTickInputPayloadDto? inputDto = JsonSerializer.Deserialize<SubTickInputPayloadDto>(jsonPayload, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                inputDto.Should().NotBeNull();
                inputDto!.Action.Should().Be("CastAbility 1");
                inputDto.InputSequence.Should().Be(42u);
                inputDto.HardwareTimestampQpc.Should().Be(simulatedNowQpc - 2500L);

                // Compute synthetic latency
                float measuredLatencyMs = 0.25f;

                // Emit response frame OpCode 0x0013
                string respJson = $"{{\"success\":true,\"inputSequence\":{inputDto.InputSequence},\"measuredLatencyMs\":{measuredLatencyMs:F3},\"timestampQpc\":{simulatedNowQpc}}}";
                byte[] respPayload = Encoding.UTF8.GetBytes(respJson);
                byte[] respFrame = new byte[12 + respPayload.Length];

                BinaryPrimitives.WriteUInt16LittleEndian(respFrame.AsSpan(0, 2), 0x574D);
                BinaryPrimitives.WriteUInt16LittleEndian(respFrame.AsSpan(2, 2), (ushort)Kongor.Cli.Mcp.Services.IpcOpCode.SubTickInputResponse);
                BinaryPrimitives.WriteInt32LittleEndian(respFrame.AsSpan(4, 4), respPayload.Length);
                BinaryPrimitives.WriteUInt32LittleEndian(respFrame.AsSpan(8, 4), reqId);
                respPayload.CopyTo(respFrame.AsSpan(12));

                await server.WriteAsync(respFrame.AsMemory(0, respFrame.Length), cts.Token);
                await server.FlushAsync(cts.Token);
            }
        }, cts.Token);

        await using NativeClientIpcBridge client = new(pipeName: pipeName);
        await client.ConnectAsync(timeoutMs: 5000, cts.Token);

        SubTickInputResult result = await client.InjectSubTickInputAsync(
            "CastAbility 1",
            hardwareTimestampQpc: simulatedNowQpc - 2500L,
            inputSequence: 42,
            timeoutMs: 5000,
            cancellationToken: cts.Token);

        result.Success.Should().BeTrue();
        result.InputSequence.Should().Be(42u);
        result.MeasuredLatencyMs.Should().BeApproximately(0.25f, 0.001f);
        result.TimestampQpc.Should().Be(simulatedNowQpc);

        await serverTask;
    }

    [Fact]
    public async Task NativeClientIpcBridge_PushTelemetryUpdate_SendsOpCode0x0014_AndParsesResponse0x0015()
    {
        string pipeName = "test-telemetry-pipe-" + Guid.NewGuid().ToString("N");
        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(15));

        NamedPipeServerStream server = new(
            pipeName,
            PipeDirection.InOut,
            1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.WriteThrough);

        Task serverTask = Task.Run(async () =>
        {
            await using (server)
            {
                await server.WaitForConnectionAsync(cts.Token);

                byte[] header = new byte[12];
                await server.ReadExactlyAsync(header.AsMemory(0, 12), cts.Token);

                ushort magic = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(0, 2));
                ushort opCode = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(2, 2));
                int payloadLen = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(4, 4));
                uint reqId = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(8, 4));

                magic.Should().Be(0x574D);
                opCode.Should().Be((ushort)Kongor.Cli.Mcp.Services.IpcOpCode.UpdateTelemetryMetrics);

                byte[] payload = new byte[payloadLen];
                await server.ReadExactlyAsync(payload.AsMemory(0, payloadLen), cts.Token);
                string jsonPayload = Encoding.UTF8.GetString(payload);

                TelemetryUpdatePayloadDto? updateDto = JsonSerializer.Deserialize<TelemetryUpdatePayloadDto>(jsonPayload, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                updateDto.Should().NotBeNull();
                updateDto!.DrawCalls.Should().Be(240u);
                updateDto.ActiveEntities.Should().Be(150u);
                updateDto.ParticlesAlive.Should().Be(85u);
                updateDto.PingMicroseconds.Should().Be(14500u);
                updateDto.ServerTick.Should().Be(12040u);
                updateDto.InputLatencyMs.Should().BeApproximately(0.42f, 0.01f);

                // Emit response frame OpCode 0x0015
                string respJson = "{\"success\":true,\"telemetryUpdated\":true}";
                byte[] respPayload = Encoding.UTF8.GetBytes(respJson);
                byte[] respFrame = new byte[12 + respPayload.Length];

                BinaryPrimitives.WriteUInt16LittleEndian(respFrame.AsSpan(0, 2), 0x574D);
                BinaryPrimitives.WriteUInt16LittleEndian(respFrame.AsSpan(2, 2), (ushort)Kongor.Cli.Mcp.Services.IpcOpCode.TelemetryMetricsResponse);
                BinaryPrimitives.WriteInt32LittleEndian(respFrame.AsSpan(4, 4), respPayload.Length);
                BinaryPrimitives.WriteUInt32LittleEndian(respFrame.AsSpan(8, 4), reqId);
                respPayload.CopyTo(respFrame.AsSpan(12));

                await server.WriteAsync(respFrame.AsMemory(0, respFrame.Length), cts.Token);
                await server.FlushAsync(cts.Token);
            }
        }, cts.Token);

        await using NativeClientIpcBridge client = new(pipeName: pipeName);
        await client.ConnectAsync(timeoutMs: 5000, cts.Token);

        bool updated = await client.PushTelemetryUpdateAsync(
            drawCalls: 240,
            activeEntities: 150,
            particlesAlive: 85,
            pingMicroseconds: 14500,
            serverTick: 12040,
            inputLatencyMs: 0.42f,
            timeoutMs: 5000,
            cancellationToken: cts.Token);

        updated.Should().BeTrue();

        await serverTask;
    }

    [Fact]
    public async Task CompositeGameIpcBridge_InjectSubTickInput_RoutesToPrimary_WhenConnected()
    {
        Mock<IGameIpcBridge> mockPrimary = new();
        Mock<IGameIpcBridge> mockSecondary = new();
        Mock<IGameIpcBridge> mockTertiary = new();

        mockPrimary.SetupGet(b => b.IsConnected).Returns(true);
        mockPrimary.Setup(b => b.InjectSubTickInputAsync("Order move 100 200", 12345L, 7u, It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SubTickInputResult(true, 7u, 0.15f, 12350L, "Primary queued"));

        await using CompositeGameIpcBridge composite = new(
            mockPrimary.Object,
            mockSecondary.Object,
            mockTertiary.Object);

        SubTickInputResult result = await composite.InjectSubTickInputAsync("Order move 100 200", 12345L, 7u);

        result.Success.Should().BeTrue();
        result.InputSequence.Should().Be(7u);
        result.MeasuredLatencyMs.Should().Be(0.15f);
        result.Message.Should().Be("Primary queued");

        mockPrimary.Verify(b => b.InjectSubTickInputAsync("Order move 100 200", 12345L, 7u, It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Once);
        mockSecondary.Verify(b => b.InjectSubTickInputAsync(It.IsAny<string>(), It.IsAny<long>(), It.IsAny<uint>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task CompositeGameIpcBridge_InjectSubTickInput_FallsBackToSecondary_WhenPrimaryThrows()
    {
        Mock<IGameIpcBridge> mockPrimary = new();
        Mock<IGameIpcBridge> mockSecondary = new();

        mockPrimary.SetupGet(b => b.IsConnected).Returns(true);
        mockPrimary.Setup(b => b.InjectSubTickInputAsync(It.IsAny<string>(), It.IsAny<long>(), It.IsAny<uint>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new IOException("Primary pipe disconnected"));

        mockSecondary.SetupGet(b => b.IsConnected).Returns(true);
        mockSecondary.Setup(b => b.InjectSubTickInputAsync("CastAbility 2", 99999L, 10u, It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SubTickInputResult(true, 10u, 0.5f, 100050L, "Secondary queued"));

        await using CompositeGameIpcBridge composite = new(mockPrimary.Object, mockSecondary.Object);

        SubTickInputResult result = await composite.InjectSubTickInputAsync("CastAbility 2", 99999L, 10u);

        result.Success.Should().BeTrue();
        result.InputSequence.Should().Be(10u);
        result.Message.Should().Be("Secondary queued");

        mockPrimary.Verify(b => b.InjectSubTickInputAsync("CastAbility 2", 99999L, 10u, It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Once);
        mockSecondary.Verify(b => b.InjectSubTickInputAsync("CastAbility 2", 99999L, 10u, It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task NativeManageHandler_SubTickInput_DispatchesAndReturnsSuccess()
    {
        Mock<IGameIpcBridge> mockIpc = new();
        mockIpc.Setup(b => b.InjectSubTickInputAsync("Order attack 50 50", 12345L, 3u, It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SubTickInputResult(true, 3u, 0.12f, 12355L, "Dispatched sub-tick input"));

        NativeManageHandler handler = new(mockIpc.Object);

        string jsonArgs = "{\"action\":\"subtick_input\",\"command\":\"Order attack 50 50\",\"sequence\":3,\"timestampQpc\":12345}";
        using JsonDocument doc = JsonDocument.Parse(jsonArgs);

        ToolCallResult result = await handler.HandleAsync(doc.RootElement);

        result.IsError.Should().BeFalse();
        using JsonDocument respDoc = JsonDocument.Parse(result.FirstText);
        respDoc.RootElement.GetProperty("success").GetBoolean().Should().BeTrue();
        respDoc.RootElement.GetProperty("action").GetString().Should().Be("subtick_input");
        respDoc.RootElement.GetProperty("inputSequence").GetUInt32().Should().Be(3u);
        respDoc.RootElement.GetProperty("measuredLatencyMs").GetSingle().Should().BeApproximately(0.12f, 0.01f);
    }

    [Fact]
    public async Task NativeManageHandler_TelemetryFeed_DispatchesAndReturnsSuccess()
    {
        Mock<IGameIpcBridge> mockIpc = new();
        mockIpc.Setup(b => b.PushTelemetryUpdateAsync(300u, 200u, 50u, 8000u, 500u, 0.35f, It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        NativeManageHandler handler = new(mockIpc.Object);

        string jsonArgs = "{\"action\":\"telemetry_feed\",\"drawCalls\":300,\"activeEntities\":200,\"particlesAlive\":50,\"pingMicroseconds\":8000,\"serverTick\":500,\"inputLatencyMs\":0.35}";
        using JsonDocument doc = JsonDocument.Parse(jsonArgs);

        ToolCallResult result = await handler.HandleAsync(doc.RootElement);

        result.IsError.Should().BeFalse();
        using JsonDocument respDoc = JsonDocument.Parse(result.FirstText);
        respDoc.RootElement.GetProperty("telemetryUpdated").GetBoolean().Should().BeTrue();
        respDoc.RootElement.GetProperty("drawCalls").GetUInt32().Should().Be(300u);
        respDoc.RootElement.GetProperty("serverTick").GetUInt32().Should().Be(500u);
    }

    [Fact]
    public async Task GameActionHandler_SubTickInput_DispatchesThroughIpcBridge()
    {
        Mock<IGameIpcBridge> mockIpc = new();
        mockIpc.Setup(b => b.InjectSubTickInputAsync("Order move 300 400", 55555L, 8u, It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SubTickInputResult(true, 8u, 0.08f, 55560L, "Action sub-tick executed"));

        GameActionHandler handler = new(mockIpc.Object);

        string jsonArgs = "{\"action\":\"subtick_input\",\"command\":\"Order move 300 400\",\"sequence\":8,\"timestampQpc\":55555,\"destination\":\"client\"}";
        using JsonDocument doc = JsonDocument.Parse(jsonArgs);

        ToolCallResult result = await handler.HandleAsync(doc.RootElement);

        result.IsError.Should().BeFalse();
        using JsonDocument respDoc = JsonDocument.Parse(result.FirstText);
        respDoc.RootElement.GetProperty("success").GetBoolean().Should().BeTrue();
        respDoc.RootElement.GetProperty("action").GetString().Should().Be("subtick_input");
        respDoc.RootElement.GetProperty("inputSequence").GetUInt32().Should().Be(8u);
    }

    [Fact]
    public async Task GameTelemetryHandler_PushTelemetry_DispatchesThroughIpcBridge()
    {
        Mock<IGameIpcBridge> mockIpc = new();
        mockIpc.Setup(b => b.PushTelemetryUpdateAsync(180u, 110u, 45u, 12000u, 1000u, 0.2f, It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        GameTelemetryHandler handler = new(mockIpc.Object);

        string jsonArgs = "{\"action\":\"push\",\"drawCalls\":180,\"activeEntities\":110,\"particlesAlive\":45,\"pingMicroseconds\":12000,\"serverTick\":1000,\"inputLatencyMs\":0.2}";
        using JsonDocument doc = JsonDocument.Parse(jsonArgs);

        ToolCallResult result = await handler.HandleAsync(doc.RootElement);

        result.IsError.Should().BeFalse();
        using JsonDocument respDoc = JsonDocument.Parse(result.FirstText);
        respDoc.RootElement.GetProperty("telemetryUpdated").GetBoolean().Should().BeTrue();
        respDoc.RootElement.GetProperty("drawCalls").GetUInt32().Should().Be(180u);
    }
}
