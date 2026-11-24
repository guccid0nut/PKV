// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// TAO (Together As One) MCP & Kongor CLI -- Created by guccidonut (https://github.com/guccidonut/PKV)

// ============================================================================
// File: TelemetryStreamHandlerLiveTests.cs
// System: Kongor MCP Tests - Live Shared Memory Telemetry Stream Tests
// Policy: Strict Explicit Typing (NO 'var')
// ============================================================================

#pragma warning disable CA2026
using System.Text.Json;
using ASPIRE.Common.Telemetry;
using FluentAssertions;
using Kongor.Cli.Mcp.Handlers;
using Kongor.Cli.Mcp.Protocol;
using NEXUS.Contracts.Telemetry;
using Xunit;

namespace Kongor.Mcp.Tests;

public sealed class TelemetryStreamHandlerLiveTests
{
    private static string GetJsonText(ToolCallResult result)
    {
        if (result.Content.Count > 0 && result.Content[0] is TextContentItem textItem)
        {
            return textItem.Text;
        }
        return result.FirstText;
    }

    [Fact]
    public async Task TelemetryStream_Snapshot_WhenNoProducer_ReturnsOfflineFallback()
    {
        TelemetryStreamHandler handler = new();
        int unusedPort = 59876;
        JsonElement args = JsonDocument.Parse($$"""{"action": "snapshot", "port": {{unusedPort}}, "allowSimulation": true}""").RootElement;

        ToolCallResult result = await handler.HandleAsync(args);

        result.IsError.Should().BeFalse();
        using JsonDocument doc = JsonDocument.Parse(GetJsonText(result));
        JsonElement root = doc.RootElement;

        root.GetProperty("success").GetBoolean().Should().BeTrue();
        root.GetProperty("action").GetString().Should().Be("snapshot");
        root.GetProperty("serverPort").GetInt32().Should().Be(unusedPort);

        JsonElement perf = root.GetProperty("enginePerformance");
        perf.GetProperty("liveSharedMemory").GetBoolean().Should().BeFalse();
        perf.GetProperty("status").GetString().Should().Be("shared_memory_unavailable");

        // Synthetic entities remain intact so consumers never crash
        JsonElement entities = root.GetProperty("entities");
        entities.GetProperty("heroCount").GetInt32().Should().Be(3);
        entities.GetProperty("creepCount").GetInt32().Should().Be(3);
    }

    [Fact]
    public async Task TelemetryStream_Snapshot_WithLiveProducerBuffer_ExtractsRealMetrics()
    {
        string uniqueMap = $"Local\\KongorTestTelemetry_Live_{Guid.NewGuid():N}";
        using MemoryMappedTelemetryBuffer producer = MemoryMappedTelemetryBuffer.CreateProducer(uniqueMap, capacity: 256);

        // Write 60 smooth frames (16.66ms per frame = 60 FPS)
        for (ulong i = 1; i <= 60; i++)
        {
            FrameTelemetryRecord record = new()
            {
                FrameIndex = i,
                CpuFrameTimeMs = 16.66f,
                GpuFrameTimeMs = 11.5f,
                InputLatencyMs = 6.2f,
                PingMicroseconds = 18_500,
                PacketLossPercent = 0.0f,
                ServerTick = (uint)(1000 + i),
                ActiveEntities = 142,
                DrawCallCount = 480,
                ParticlesAlive = 1200,
                MemoryWorkingSetMb = 1850,
                AudioVoicesActive = 24
            };
            producer.TryWrite(in record);
        }

        TelemetryStreamHandler handler = new();
        string uniqueSession = $"live_snap_{Guid.NewGuid():N}";
        JsonElement args = JsonDocument.Parse($$"""{"action": "snapshot", "sessionId": "{{uniqueSession}}", "mapName": "{{uniqueMap.Replace("\\", "\\\\")}}"}""").RootElement;

        ToolCallResult result = await handler.HandleAsync(args);

        result.IsError.Should().BeFalse();
        using JsonDocument doc = JsonDocument.Parse(GetJsonText(result));
        JsonElement root = doc.RootElement;

        root.GetProperty("success").GetBoolean().Should().BeTrue();
        root.GetProperty("tick").GetInt64().Should().Be(1060);

        JsonElement perf = root.GetProperty("enginePerformance");
        perf.GetProperty("liveSharedMemory").GetBoolean().Should().BeTrue();
        perf.GetProperty("sharedMemoryMap").GetString().Should().Be(uniqueMap);
        perf.GetProperty("totalFramesRead").GetUInt64().Should().Be(60);
        perf.GetProperty("currentFps").GetDouble().Should().BeInRange(59.0, 61.0);
        perf.GetProperty("low1PercentFps").GetDouble().Should().BeInRange(59.0, 61.0);
        perf.GetProperty("drawCalls").GetUInt32().Should().Be(480);
        perf.GetProperty("activeEntities").GetUInt32().Should().Be(142);
        perf.GetProperty("particlesAlive").GetUInt32().Should().Be(1200);
        perf.GetProperty("memoryWorkingSetMb").GetUInt32().Should().Be(1850);
        perf.GetProperty("audioVoicesActive").GetUInt32().Should().Be(24);
        perf.GetProperty("pingMs").GetDouble().Should().Be(18.5);
    }

    [Fact]
    public async Task TelemetryStream_ClientPid_ResolvesCorrectMapName()
    {
        int testPid = 77123;
        string expectedMapName = SharedMemoryTelemetryConsumer.GetClientMapName(testPid);
        using MemoryMappedTelemetryBuffer producer = MemoryMappedTelemetryBuffer.CreateProducer(expectedMapName, capacity: 128);

        FrameTelemetryRecord frame = new()
        {
            FrameIndex = 999,
            CpuFrameTimeMs = 6.94f, // 144 FPS
            GpuFrameTimeMs = 5.2f,
            InputLatencyMs = 3.1f,
            PingMicroseconds = 12_000,
            ServerTick = 7777,
            ActiveEntities = 85,
            DrawCallCount = 310
        };
        producer.TryWrite(in frame);

        TelemetryStreamHandler handler = new();
        JsonElement args = JsonDocument.Parse($$"""{"action": "snapshot", "pid": {{testPid}}}""").RootElement;

        ToolCallResult result = await handler.HandleAsync(args);

        result.IsError.Should().BeFalse();
        using JsonDocument doc = JsonDocument.Parse(GetJsonText(result));
        JsonElement root = doc.RootElement;

        root.GetProperty("processId").GetInt32().Should().Be(testPid);

        JsonElement perf = root.GetProperty("enginePerformance");
        perf.GetProperty("liveSharedMemory").GetBoolean().Should().BeTrue();
        perf.GetProperty("sharedMemoryMap").GetString().Should().Be(expectedMapName);
        perf.GetProperty("drawCalls").GetUInt32().Should().Be(310);
        perf.GetProperty("serverTick").GetUInt32().Should().Be(7777);
    }

    [Fact]
    public async Task TelemetryStream_StartAndStop_ManagesActiveConsumerSession()
    {
        string uniqueMap = $"Local\\KongorTestTelemetry_Session_{Guid.NewGuid():N}";
        string sessionId = $"test_session_{Guid.NewGuid():N}";
        using MemoryMappedTelemetryBuffer producer = MemoryMappedTelemetryBuffer.CreateProducer(uniqueMap, capacity: 256);

        FrameTelemetryRecord record = new()
        {
            FrameIndex = 1,
            CpuFrameTimeMs = 16.66f,
            ServerTick = 500,
            ActiveEntities = 50,
            DrawCallCount = 200
        };
        producer.TryWrite(in record);

        TelemetryStreamHandler handler = new();

        // 1. Start streaming session
        JsonElement startArgs = JsonDocument.Parse($$"""
        {
            "action": "start",
            "sessionId": "{{sessionId}}",
            "mapName": "{{uniqueMap.Replace("\\", "\\\\")}}",
            "rateHz": 60
        }
        """).RootElement;

        ToolCallResult startResult = await handler.HandleAsync(startArgs);
        startResult.IsError.Should().BeFalse();
        using JsonDocument startDoc = JsonDocument.Parse(GetJsonText(startResult));
        startDoc.RootElement.GetProperty("status").GetString().Should().Be("streaming");
        startDoc.RootElement.GetProperty("liveSharedMemory").GetBoolean().Should().BeTrue();
        startDoc.RootElement.GetProperty("rateHz").GetInt32().Should().Be(60);

        // 2. Snapshot using active session
        JsonElement snapArgs = JsonDocument.Parse($$"""
        {
            "action": "snapshot",
            "sessionId": "{{sessionId}}"
        }
        """).RootElement;

        ToolCallResult snapResult = await handler.HandleAsync(snapArgs);
        snapResult.IsError.Should().BeFalse();
        using JsonDocument snapDoc = JsonDocument.Parse(GetJsonText(snapResult));
        snapDoc.RootElement.GetProperty("enginePerformance").GetProperty("liveSharedMemory").GetBoolean().Should().BeTrue();

        // 3. Stop streaming session
        JsonElement stopArgs = JsonDocument.Parse($$"""
        {
            "action": "stop",
            "sessionId": "{{sessionId}}"
        }
        """).RootElement;

        ToolCallResult stopResult = await handler.HandleAsync(stopArgs);
        stopResult.IsError.Should().BeFalse();
        using JsonDocument stopDoc = JsonDocument.Parse(GetJsonText(stopResult));
        stopDoc.RootElement.GetProperty("status").GetString().Should().Be("stopped");
        stopDoc.RootElement.GetProperty("totalFramesStreamed").GetInt64().Should().Be(1);
    }

    [Fact]
    public async Task GameTelemetryHandler_Latest_WithLiveSharedMemoryProducer_ReturnsLiveTelemetry()
    {
        string uniqueMap = $"Local\\KongorClientTelemetry_Test_{Guid.NewGuid():N}";
        using MemoryMappedTelemetryBuffer producer = MemoryMappedTelemetryBuffer.CreateProducer(uniqueMap, capacity: 256);

        FrameTelemetryRecord record = new()
        {
            FrameIndex = 42,
            CpuFrameTimeMs = 6.94f,
            GpuFrameTimeMs = 5.20f,
            InputLatencyMs = 0.42f,
            PingMicroseconds = 14_200,
            PacketLossPercent = 0.0f,
            ServerTick = 5000,
            ActiveEntities = 312,
            DrawCallCount = 850,
            ParticlesAlive = 120,
            MemoryWorkingSetMb = 940,
            AudioVoicesActive = 18
        };
        producer.TryWrite(in record);

        GameTelemetryHandler handler = new();
        JsonElement args = JsonDocument.Parse($$"""
        {
            "action": "latest",
            "channel": "shm",
            "mapName": "{{uniqueMap.Replace("\\", "\\\\")}}"
        }
        """).RootElement;

        ToolCallResult result = await handler.HandleAsync(args);
        result.IsError.Should().BeFalse();

        using JsonDocument doc = JsonDocument.Parse(GetJsonText(result));
        JsonElement root = doc.RootElement;
        root.GetProperty("success").GetBoolean().Should().BeTrue();
        root.GetProperty("channel").GetString().Should().Be("shm");

        JsonElement telem = root.GetProperty("telemetry");
        telem.GetProperty("frameIndex").GetUInt64().Should().Be(42);
        telem.GetProperty("drawCallCount").GetUInt32().Should().Be(850);
        telem.GetProperty("activeEntities").GetUInt32().Should().Be(312);
        telem.GetProperty("particlesAlive").GetUInt32().Should().Be(120);
        telem.GetProperty("memoryWorkingSetMb").GetDouble().Should().Be(940);
        telem.GetProperty("serverTick").GetUInt32().Should().Be(5000);
    }
}
