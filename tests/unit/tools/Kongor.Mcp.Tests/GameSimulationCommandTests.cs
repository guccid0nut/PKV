// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// TAO (Together As One) MCP & Kongor CLI -- Created by guccidonut (https://github.com/guccidonut/PKV)

// ============================================================================
// File: GameSimulationCommandTests.cs
// System: Together as One - KONGOR MCP (TAO) Phase 2
// Description: Unit tests for Active Fixture injection, Deterministic Time Control,
//              Frame Stepping, and Zero-Copy Shared Memory Telemetry Data Plane.
// ============================================================================

using System.Diagnostics;
using System.Text.Json;
using ASPIRE.Common.Telemetry;
using FluentAssertions;
using Kongor.Cli.Mcp.Handlers;
using Kongor.Cli.Mcp.Protocol;
using Kongor.Cli.Mcp.Services;
using Moq;
using NEXUS.Contracts.Telemetry;
using Xunit;

namespace Kongor.Mcp.Tests;

public sealed class GameSimulationCommandTests
{
    [Fact]
    public void GameCommandHandler_ResolveCommand_TimescaleClamping()
    {
        // 1. Normal scale 2.5
        JsonElement args1 = JsonElement.Parse("{\"action\":\"set_timescale\",\"scale\":2.5}");
        string? cmd1 = GameCommandHandler.ResolveCommand(args1, out string act1);
        act1.Should().Be("set_timescale");
        cmd1.Should().Be("Set host_timeScale 2.5000");

        // 2. Over max (150.0 -> clamped to 100.0)
        JsonElement args2 = JsonElement.Parse("{\"action\":\"timescale\",\"value\":150.0}");
        string? cmd2 = GameCommandHandler.ResolveCommand(args2, out string act2);
        act2.Should().Be("timescale");
        cmd2.Should().Be("Set host_timeScale 100.0000");

        // 3. Under min (0.0001 -> clamped to 0.01)
        JsonElement args3 = JsonElement.Parse("{\"action\":\"set_timescale\",\"factor\":0.0001}");
        string? cmd3 = GameCommandHandler.ResolveCommand(args3, out string act3);
        act3.Should().Be("set_timescale");
        cmd3.Should().Be("Set host_timeScale 0.0100");
    }

    [Fact]
    public void GameCommandHandler_ResolveCommand_StepFramesClamping()
    {
        // 1. Normal step
        JsonElement args1 = JsonElement.Parse("{\"action\":\"step_frames\",\"frames\":10}");
        string? cmd1 = GameCommandHandler.ResolveCommand(args1, out string act1);
        act1.Should().Be("step_frames");
        cmd1.Should().Be("ReplayIncFrame 10");

        // 2. Over max (5000 -> clamped to 1000)
        JsonElement args2 = JsonElement.Parse("{\"action\":\"step\",\"count\":5000}");
        string? cmd2 = GameCommandHandler.ResolveCommand(args2, out string act2);
        act2.Should().Be("step");
        cmd2.Should().Be("ReplayIncFrame 1000");

        // 3. Under min (0 -> clamped to 1)
        JsonElement args3 = JsonElement.Parse("{\"action\":\"step_frames\",\"ticks\":-5}");
        string? cmd3 = GameCommandHandler.ResolveCommand(args3, out string act3);
        act3.Should().Be("step_frames");
        cmd3.Should().Be("ReplayIncFrame 1");
    }

    [Fact]
    public void GameCommandHandler_ResolveCommand_PauseAndResume()
    {
        JsonElement argsPause = JsonElement.Parse("{\"action\":\"pause\"}");
        string? cmdPause = GameCommandHandler.ResolveCommand(argsPause, out string actP);
        actP.Should().Be("pause");
        cmdPause.Should().Be("Pause");

        JsonElement argsResume = JsonElement.Parse("{\"action\":\"resume\"}");
        string? cmdResume = GameCommandHandler.ResolveCommand(argsResume, out string actR);
        actR.Should().Be("resume");
        cmdResume.Should().Be("Unpause");
    }

    [Fact]
    public async Task GameLuaHandler_SimulationActions_FormulateCorrectScriptCommands()
    {
        Mock<IGameIpcBridge> mockIpc = new();
        string lastDispatched = string.Empty;
        mockIpc.Setup(i => i.IsConnected).Returns(true);
        mockIpc.Setup(i => i.ExecuteCommandAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Callback<string, int, CancellationToken>((cmd, _, _) => lastDispatched = cmd)
            .ReturnsAsync("[TAO_RESPONSE] {\"status\":\"success\",\"data\":{\"ok\":true}}");

        GameLuaHandler handler = new(
            clientManageHandler: null,
            serverManageHandler: null,
            uiControlService: null,
            remediationService: null,
            timeProvider: null,
            gameIpcBridge: mockIpc.Object);

        // 1. Set Timescale
        var resTimescale = await handler.HandleAsync(JsonElement.Parse("{\"action\":\"set_timescale\",\"scale\":4.0}"));
        resTimescale.IsError.Should().BeFalse();
        lastDispatched.Should().Contain("TAO.Dispatch('set_timescale', '{\"scale\":4.0000}')");
        lastDispatched.Should().Contain("Cmd('Set host_timeScale 4.0000')");

        // 2. Step Frames
        var resStep = await handler.HandleAsync(JsonElement.Parse("{\"action\":\"step_frames\",\"frames\":20}"));
        resStep.IsError.Should().BeFalse();
        lastDispatched.Should().Contain("TAO.Dispatch('step_frames', '{\"frames\":20}')");
        lastDispatched.Should().Contain("Cmd('ReplayIncFrame 20')");

        // 3. Pause
        var resPause = await handler.HandleAsync(JsonElement.Parse("{\"action\":\"pause\"}"));
        resPause.IsError.Should().BeFalse();
        lastDispatched.Should().Contain("TAO.Dispatch('pause', '{\"paused\":true}')");

        // 4. Resume
        var resResume = await handler.HandleAsync(JsonElement.Parse("{\"action\":\"resume\"}"));
        resResume.IsError.Should().BeFalse();
        lastDispatched.Should().Contain("TAO.Dispatch('pause', '{\"paused\":false}')");

        // 5. Apply Fixture
        var resFixture = await handler.HandleAsync(JsonElement.Parse("{\"action\":\"apply_fixture\",\"hero\":\"Pyromancer\",\"level\":16,\"gold\":4500,\"items\":[\"Item_PortalKey\"]}"));
        resFixture.IsError.Should().BeFalse();
        lastDispatched.Should().Contain("TAO.Dispatch('apply_fixture'");
        lastDispatched.Should().Contain("Pyromancer");
        lastDispatched.Should().Contain("4500");
    }

    [Fact]
    public async Task GameSnapshotHandler_ApplyFixture_DispatchesTaoFixturePayload()
    {
        Mock<IGameIpcBridge> mockIpc = new();
        string executedCmd = string.Empty;
        mockIpc.Setup(i => i.IsConnected).Returns(true);
        mockIpc.Setup(i => i.ExecuteCommandAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Callback<string, int, CancellationToken>((cmd, _, _) => executedCmd = cmd)
            .ReturnsAsync("[TAO_RESPONSE] {\"status\":\"success\",\"data\":{\"hero\":\"Hero_Pyromancer\",\"level\":16,\"applied\":true}}");

        GameSnapshotHandler handler = new(gameIpcBridge: mockIpc.Object);

        JsonElement args = JsonElement.Parse(@"{
            ""action"": ""apply_fixture"",
            ""hero"": ""Hero_Pyromancer"",
            ""level"": 16,
            ""gold"": 3500,
            ""items"": [""Item_PortalKey"", ""Item_GrimoireOfPower""],
            ""pos"": { ""x"": 1500, ""y"": 2500, ""z"": 128 },
            ""resetCooldowns"": true,
            ""refreshVitality"": true
        }");

        ToolCallResult result = await handler.HandleAsync(args);
        result.IsError.Should().BeFalse();

        executedCmd.Should().Contain("TAO.Dispatch('apply_fixture'");
        executedCmd.Should().Contain("Hero_Pyromancer");
        executedCmd.Should().Contain("3500");

        string jsonText = ((TextContentItem)result.Content[0]).Text;
        jsonText.Should().Contain("\"status\": \"success\"");
        jsonText.Should().Contain("\"action\": \"apply_fixture\"");
    }

    [Fact]
    public async Task GameTelemetryHandler_SharedMemoryDataPlane_ReadsLatestFrame()
    {
        string testMapName = $"Local\\TaoPhase2_TestMap_Latest_{Guid.NewGuid():N}";

        using MemoryMappedTelemetryBuffer producer = MemoryMappedTelemetryBuffer.CreateProducer(testMapName, 1024);

        FrameTelemetryRecord testRecord = new()
        {
            FrameIndex = 1337,
            TimestampQpc = Stopwatch.GetTimestamp(),
            CpuFrameTimeMs = 4.0f, // 250 FPS
            GpuFrameTimeMs = 3.5f,
            InputLatencyMs = 2.0f,
            DrawCallCount = 345,
            ActiveEntities = 82,
            ParticlesAlive = 120,
            PingMicroseconds = 25000, // 25ms
            PacketLossPercent = 0.5f,
            MemoryWorkingSetMb = 512,
            ServerTick = 9999
        };

        producer.TryWrite(in testRecord).Should().BeTrue();

        GameTelemetryHandler handler = new();

        JsonElement args = JsonElement.Parse(JsonSerializer.Serialize(new
        {
            channel = "shm",
            action = "latest",
            mapName = testMapName
        }));

        ToolCallResult result = await handler.HandleAsync(args);
        result.IsError.Should().BeFalse();

        string jsonText = ((TextContentItem)result.Content[0]).Text;
        jsonText.Should().Contain("\"status\": \"success\"");
        jsonText.Should().Contain("\"channel\": \"shm\"");
        jsonText.Should().Contain("\"frameIndex\": 1337");
        jsonText.Should().Contain("\"currentFps\": 250");
        jsonText.Should().Contain("\"drawCallCount\": 345");
        jsonText.Should().Contain("\"activeEntities\": 82");
        jsonText.Should().Contain("\"particlesAlive\": 120");
        jsonText.Should().Contain("\"serverTick\": 9999");
    }

    [Fact]
    public async Task GameTelemetryHandler_SharedMemoryDataPlane_DrainsRingBufferBatch()
    {
        string testMapName = $"Local\\TaoPhase2_TestMap_Batch_{Guid.NewGuid():N}";

        using MemoryMappedTelemetryBuffer producer = MemoryMappedTelemetryBuffer.CreateProducer(testMapName, 1024);

        for (ulong i = 1; i <= 10; i++)
        {
            FrameTelemetryRecord rec = new()
            {
                FrameIndex = i,
                TimestampQpc = Stopwatch.GetTimestamp(),
                CpuFrameTimeMs = 5.0f, // 200 FPS
                DrawCallCount = 200 + (uint)i,
                MemoryWorkingSetMb = 400
            };
            producer.TryWrite(in rec).Should().BeTrue();
        }

        GameTelemetryHandler handler = new();

        JsonElement args = JsonElement.Parse(JsonSerializer.Serialize(new
        {
            channel = "shm",
            action = "poll_ring",
            mapName = testMapName,
            limit = 10
        }));

        ToolCallResult result = await handler.HandleAsync(args);
        result.IsError.Should().BeFalse();

        string jsonText = ((TextContentItem)result.Content[0]).Text;
        jsonText.Should().Contain("\"status\": \"success\"");
        jsonText.Should().Contain("\"action\": \"poll_ring\"");
        jsonText.Should().Contain("\"framesRead\": 10");
        jsonText.Should().Contain("\"frameIndex\": 1");
        jsonText.Should().Contain("\"frameIndex\": 10");
    }

    [Fact]
    public async Task GameTelemetryHandler_MissingMmf_AutoChannelFallsBackToIpc()
    {
        Mock<IGameIpcBridge> mockIpc = new();
        mockIpc.Setup(i => i.GetEngineStateAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new EngineStateDto
            {
                CurrentFps = 144.0,
                MemoryWorkingSetBytes = 256 * 1024 * 1024,
                ActiveMatchId = 42,
                CurrentMap = "caldavar",
                PlayerName = "TaoPlayer",
                ConnectedPlayers = 2,
                UptimeSeconds = 300
            });

        GameTelemetryHandler handler = new(mockIpc.Object);

        // When channel is omitted (or "auto"), and no MMF is running, fallback to IPC
        JsonElement args = JsonElement.Parse("{\"channel\":\"auto\",\"timeoutMs\":1000,\"pid\":999999}");

        ToolCallResult result = await handler.HandleAsync(args);
        result.IsError.Should().BeFalse();

        string jsonText = ((TextContentItem)result.Content[0]).Text;
        jsonText.Should().Contain("\"success\": true");
        jsonText.Should().Contain("\"currentFps\": 144");
        jsonText.Should().Contain("\"playerName\": \"TaoPlayer\"");
    }

    [Fact]
    public async Task GameTelemetryHandler_SharedMemoryDataPlane_QueriesHitchesSentinel()
    {
        string testMapName = $"Local\\TaoPhase2_TestMap_Hitches_{Guid.NewGuid():N}";

        using MemoryMappedTelemetryBuffer producer = MemoryMappedTelemetryBuffer.CreateProducer(testMapName, 1024);

        // Frame 1: Normal frame (4ms, no hitch)
        FrameTelemetryRecord normalRec = new()
        {
            FrameIndex = 100,
            TimestampQpc = Stopwatch.GetTimestamp(),
            CpuFrameTimeMs = 4.0f,
            GpuFrameTimeMs = 4.0f,
            ReservedFlags = 0
        };
        producer.TryWrite(in normalRec).Should().BeTrue();

        // Frame 2: CPU stall hitch (12ms delta, 10ms CPU)
        FrameTelemetryRecord cpuHitchRec = new()
        {
            FrameIndex = 101,
            TimestampQpc = Stopwatch.GetTimestamp(),
            CpuFrameTimeMs = 10.5f,
            GpuFrameTimeMs = 4.0f,
            DrawCallCount = 450,
            MemoryWorkingSetMb = 512,
            ServerTick = 5001,
            ReservedFlags = FrameTelemetryRecord.FlagHitch | FrameTelemetryRecord.FlagCpuStall
        };
        producer.TryWrite(in cpuHitchRec).Should().BeTrue();

        // Frame 3: GPU stall hitch (15ms delta, 12ms GPU)
        FrameTelemetryRecord gpuHitchRec = new()
        {
            FrameIndex = 102,
            TimestampQpc = Stopwatch.GetTimestamp(),
            CpuFrameTimeMs = 3.0f,
            GpuFrameTimeMs = 12.0f,
            DrawCallCount = 600,
            MemoryWorkingSetMb = 512,
            ServerTick = 5002,
            ReservedFlags = FrameTelemetryRecord.FlagHitch | FrameTelemetryRecord.FlagGpuStall
        };
        producer.TryWrite(in gpuHitchRec).Should().BeTrue();

        GameTelemetryHandler handler = new();

        JsonElement args = JsonElement.Parse(JsonSerializer.Serialize(new
        {
            channel = "shm",
            action = "hitches",
            mapName = testMapName,
            limit = 64
        }));

        ToolCallResult result = await handler.HandleAsync(args);
        result.IsError.Should().BeFalse();

        string jsonText = ((TextContentItem)result.Content[0]).Text;
        jsonText.Should().Contain("\"status\": \"success\"");
        jsonText.Should().Contain("\"action\": \"hitches\"");
        jsonText.Should().Contain("\"hitchCount\": 2");
        jsonText.Should().Contain("CPU_ENGINE_TICK_STALL");
        jsonText.Should().Contain("GPU_RENDER_PRESENT_STALL");
        jsonText.Should().Contain("\"frameIndex\": 101");
        jsonText.Should().Contain("\"frameIndex\": 102");
    }
}
