// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// TAO (Together As One) MCP & Kongor CLI -- Created by guccidonut (https://github.com/guccidonut/PKV)

// ============================================================================
// File: PerfWindowTests.cs
// System: Project Kongor MCP Tests - Phase 5 Performance Window & Attribution Suite
// Policy: Strict Explicit Typing (NO 'var')
// ============================================================================

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ASPIRE.Common.Telemetry;
using FluentAssertions;
using Kongor.Cli.Mcp.Handlers;
using Kongor.Cli.Mcp.Protocol;
using Kongor.Cli.Mcp.Services;
using NEXUS.Contracts.Telemetry;
using Moq;
using Xunit;
using Xunit.Abstractions;

namespace Kongor.Mcp.Tests;

public sealed class PerfWindowTests
{
    private readonly ITestOutputHelper _output;

    public PerfWindowTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public async Task Start_InitializesActiveWindow_ReturnsSuccess()
    {
        Mock<ITaoClient> mockTao = new();
        mockTao.SetupGet(t => t.IsConnected).Returns(true);

        string mockLogJson = "{\"status\":\"success\",\"transitions\":[],\"total\":0,\"currentFrame\":1000,\"currentHostTime\":50000}";
        using JsonDocument doc = JsonDocument.Parse(mockLogJson);
        mockTao.Setup(t => t.DispatchAsync(It.IsAny<string>(), It.IsAny<object>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(TaoResponse.Ok("get_transition_log", doc.RootElement.Clone(), 5.0));

        PerfWindowHandler handler = new(taoClient: mockTao.Object, logger: msg => _output.WriteLine(msg));

        using JsonDocument argDoc = JsonDocument.Parse("{\"action\":\"start\"}");
        ToolCallResult res = await handler.HandleAsync(argDoc.RootElement);

        res.IsError.Should().BeFalse();
        using JsonDocument resDoc = JsonDocument.Parse(((TextContentItem)res.Content[0]).Text);
        resDoc.RootElement.GetProperty("status").GetString().Should().Be("recording");
        resDoc.RootElement.GetProperty("active").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task Status_WhenIdleAndActive_ReportsAccurateState()
    {
        Mock<ITaoClient> mockTao = new();
        mockTao.SetupGet(t => t.IsConnected).Returns(true);

        string mockLogJson = "{\"status\":\"success\",\"transitions\":[],\"total\":0,\"currentFrame\":1000,\"currentHostTime\":50000}";
        using JsonDocument doc = JsonDocument.Parse(mockLogJson);
        mockTao.Setup(t => t.DispatchAsync(It.IsAny<string>(), It.IsAny<object>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(TaoResponse.Ok("get_transition_log", doc.RootElement.Clone(), 5.0));

        PerfWindowHandler handler = new(taoClient: mockTao.Object, logger: msg => _output.WriteLine(msg));

        // 1. Idle status
        using JsonDocument idleDoc = JsonDocument.Parse("{\"action\":\"status\"}");
        ToolCallResult idleRes = await handler.HandleAsync(idleDoc.RootElement);
        using JsonDocument idleResDoc = JsonDocument.Parse(((TextContentItem)idleRes.Content[0]).Text);
        idleResDoc.RootElement.GetProperty("active").GetBoolean().Should().BeFalse();
        idleResDoc.RootElement.GetProperty("status").GetString().Should().Be("idle");

        // 2. Start window
        using JsonDocument startDoc = JsonDocument.Parse("{\"action\":\"start\"}");
        await handler.HandleAsync(startDoc.RootElement);

        // 3. Active status
        ToolCallResult activeRes = await handler.HandleAsync(idleDoc.RootElement);
        using JsonDocument activeResDoc = JsonDocument.Parse(((TextContentItem)activeRes.Content[0]).Text);
        activeResDoc.RootElement.GetProperty("active").GetBoolean().Should().BeTrue();
        activeResDoc.RootElement.GetProperty("status").GetString().Should().Be("recording");
    }

    [Fact]
    public async Task Stop_WithoutActiveWindow_ReturnsErrorWithActionableRemediation()
    {
        Mock<ITaoClient> mockTao = new();
        PerfWindowHandler handler = new(taoClient: mockTao.Object, logger: msg => _output.WriteLine(msg));

        using JsonDocument stopDoc = JsonDocument.Parse("{\"action\":\"stop\"}");
        ToolCallResult res = await handler.HandleAsync(stopDoc.RootElement);

        res.IsError.Should().BeTrue();
        using JsonDocument resDoc = JsonDocument.Parse(((TextContentItem)res.Content[0]).Text);
        resDoc.RootElement.GetProperty("status").GetString().Should().Be("failed");
        resDoc.RootElement.GetProperty("actionableRemediation").GetString().Should().Contain("Call 'kongor_perf_window' with action 'start'");
    }

    [Fact]
    public async Task Stop_ComputesAccurateMetrics_AndDetectsHitches()
    {
        Mock<ITaoClient> mockTao = new();
        mockTao.SetupGet(t => t.IsConnected).Returns(true);

        string mockLogJson = "{\"status\":\"success\",\"transitions\":[],\"total\":0,\"currentFrame\":1000,\"currentHostTime\":50000}";
        using JsonDocument doc = JsonDocument.Parse(mockLogJson);
        mockTao.Setup(t => t.DispatchAsync(It.IsAny<string>(), It.IsAny<object>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(TaoResponse.Ok("get_transition_log", doc.RootElement.Clone(), 5.0));

        // Inject 50 synthetic records:
        // 45 frames @ 6.94 ms (144 FPS)
        // 3 frames @ 35.0 ms (> 30 ms minor hitch)
        // 2 frames @ 55.0 ms (> 50 ms major hitch)
        List<FrameTelemetryRecord> testRecords = [];
        ulong frameIdx = 1000;
        for (int i = 0; i < 45; i++)
        {
            testRecords.Add(new FrameTelemetryRecord
            {
                FrameIndex = frameIdx++,
                CpuFrameTimeMs = 6.94f,
                GpuFrameTimeMs = 3.5f,
                MemoryWorkingSetMb = 800
            });
        }
        for (int i = 0; i < 3; i++)
        {
            testRecords.Add(new FrameTelemetryRecord
            {
                FrameIndex = frameIdx++,
                CpuFrameTimeMs = 35.0f,
                GpuFrameTimeMs = 5.0f,
                MemoryWorkingSetMb = 805
            });
        }
        for (int i = 0; i < 2; i++)
        {
            testRecords.Add(new FrameTelemetryRecord
            {
                FrameIndex = frameIdx++,
                CpuFrameTimeMs = 55.0f,
                GpuFrameTimeMs = 8.0f,
                MemoryWorkingSetMb = 810
            });
        }

        PerfWindowHandler handler = new(
            taoClient: mockTao.Object,
            logger: msg => _output.WriteLine(msg),
            testRecordsProvider: _ => testRecords);

        // Start
        using JsonDocument startDoc = JsonDocument.Parse("{\"action\":\"start\"}");
        await handler.HandleAsync(startDoc.RootElement);

        // Stop
        using JsonDocument stopDoc = JsonDocument.Parse("{\"action\":\"stop\"}");
        ToolCallResult res = await handler.HandleAsync(stopDoc.RootElement);

        res.IsError.Should().BeFalse();
        using JsonDocument resDoc = JsonDocument.Parse(((TextContentItem)res.Content[0]).Text);
        JsonElement root = resDoc.RootElement;

        root.GetProperty("status").GetString().Should().Be("completed");
        JsonElement summary = root.GetProperty("summary");
        summary.GetProperty("totalFrames").GetInt32().Should().Be(50);
        summary.GetProperty("hitchCount30ms").GetInt32().Should().Be(5); // 3 (35ms) + 2 (55ms)
        summary.GetProperty("hitchCount50ms").GetInt32().Should().Be(2); // 2 (55ms)
        summary.GetProperty("minFrameTimeMs").GetDouble().Should().BeApproximately(6.94, 0.05);
        summary.GetProperty("maxFrameTimeMs").GetDouble().Should().BeApproximately(55.0, 0.05);
        summary.GetProperty("avgFrameTimeMs").GetDouble().Should().BeGreaterThan(6.94);
        summary.GetProperty("averageFps").GetDouble().Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task Stop_CorrelatesHitchesWithLuaTransitions_ByFrameNumber()
    {
        Mock<ITaoClient> mockTao = new();
        mockTao.SetupGet(t => t.IsConnected).Returns(true);

        // Lua transition log contains a transition on frame 2010
        string mockLogJson = @"{
            ""status"": ""success"",
            ""transitions"": [
                {
                    ""frame"": 2010,
                    ""hostTime"": 50200,
                    ""from"": ""main_menu"",
                    ""to"": ""options_menu"",
                    ""action"": ""click_btnOptions"",
                    ""durationMs"": 0.85,
                    ""budgetExceeded"": false
                }
            ],
            ""total"": 1,
            ""currentFrame"": 2015,
            ""currentHostTime"": 50300
        }";
        using JsonDocument doc = JsonDocument.Parse(mockLogJson);
        mockTao.Setup(t => t.DispatchAsync(It.Is<string>(a => a == "get_transition_log"), It.IsAny<object>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(TaoResponse.Ok("get_transition_log", doc.RootElement.Clone(), 5.0));

        // Create records where frame 2010 experiences a 60ms hitch
        List<FrameTelemetryRecord> testRecords =
        [
            new() { FrameIndex = 2008, CpuFrameTimeMs = 6.9f },
            new() { FrameIndex = 2009, CpuFrameTimeMs = 7.0f },
            new() { FrameIndex = 2010, CpuFrameTimeMs = 60.0f, GpuFrameTimeMs = 9.0f },
            new() { FrameIndex = 2011, CpuFrameTimeMs = 6.9f }
        ];

        PerfWindowHandler handler = new(
            taoClient: mockTao.Object,
            logger: msg => _output.WriteLine(msg),
            testRecordsProvider: _ => testRecords);

        // Start & Stop
        using JsonDocument startDoc = JsonDocument.Parse("{\"action\":\"start\"}");
        await handler.HandleAsync(startDoc.RootElement);

        using JsonDocument stopDoc = JsonDocument.Parse("{\"action\":\"stop\"}");
        ToolCallResult res = await handler.HandleAsync(stopDoc.RootElement);

        res.IsError.Should().BeFalse();
        using JsonDocument resDoc = JsonDocument.Parse(((TextContentItem)res.Content[0]).Text);
        JsonElement root = resDoc.RootElement;

        JsonElement summary = root.GetProperty("summary");
        summary.GetProperty("hitchCount50ms").GetInt32().Should().Be(1);

        JsonElement hitches = root.GetProperty("hitches");
        hitches.GetArrayLength().Should().Be(1);

        JsonElement hitch = hitches[0];
        hitch.GetProperty("frameIndex").GetUInt64().Should().Be(2010);
        hitch.GetProperty("cpuFrameTimeMs").GetDouble().Should().Be(60.0);
        
        // Exact attribution by frame number
        hitch.TryGetProperty("attributedTransition", out JsonElement attr).Should().BeTrue();
        attr.GetProperty("action").GetString().Should().Be("click_btnOptions");
        attr.GetProperty("from").GetString().Should().Be("main_menu");
        attr.GetProperty("to").GetString().Should().Be("options_menu");
        attr.GetProperty("durationMs").GetDouble().Should().Be(0.85);
    }

    [Fact]
    public async Task Stop_EnforcesFrameBudget_FlagsViolations()
    {
        Mock<ITaoClient> mockTao = new();
        mockTao.SetupGet(t => t.IsConnected).Returns(true);

        // Lua transition log contains one clean transition and one budget violation (3.25 ms > 1.0 ms)
        string mockLogJson = @"{
            ""status"": ""success"",
            ""transitions"": [
                {
                    ""frame"": 3005,
                    ""hostTime"": 75000,
                    ""from"": ""main"",
                    ""to"": ""store"",
                    ""action"": ""navigate"",
                    ""durationMs"": 0.72,
                    ""budgetExceeded"": false
                },
                {
                    ""frame"": 3050,
                    ""hostTime"": 76000,
                    ""from"": ""store"",
                    ""to"": ""alt_avatars"",
                    ""action"": ""heavy_layout_recalc"",
                    ""durationMs"": 3.25,
                    ""budgetExceeded"": true
                }
            ],
            ""total"": 2,
            ""currentFrame"": 3055,
            ""currentHostTime"": 76100
        }";
        using JsonDocument doc = JsonDocument.Parse(mockLogJson);
        mockTao.Setup(t => t.DispatchAsync(It.Is<string>(a => a == "get_transition_log"), It.IsAny<object>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(TaoResponse.Ok("get_transition_log", doc.RootElement.Clone(), 5.0));

        List<FrameTelemetryRecord> testRecords =
        [
            new() { FrameIndex = 3000, CpuFrameTimeMs = 6.9f },
            new() { FrameIndex = 3050, CpuFrameTimeMs = 8.5f }
        ];

        PerfWindowHandler handler = new(
            taoClient: mockTao.Object,
            logger: msg => _output.WriteLine(msg),
            testRecordsProvider: _ => testRecords);

        // Start & Stop
        using JsonDocument startDoc = JsonDocument.Parse("{\"action\":\"start\"}");
        await handler.HandleAsync(startDoc.RootElement);

        using JsonDocument stopDoc = JsonDocument.Parse("{\"action\":\"stop\"}");
        ToolCallResult res = await handler.HandleAsync(stopDoc.RootElement);

        res.IsError.Should().BeFalse();
        using JsonDocument resDoc = JsonDocument.Parse(((TextContentItem)res.Content[0]).Text);
        JsonElement root = resDoc.RootElement;

        // Frame budget check inside summary
        JsonElement summary = root.GetProperty("summary");
        summary.GetProperty("frameBudgetPassed").GetBoolean().Should().BeFalse();
        summary.GetProperty("budgetViolationsCount").GetInt32().Should().Be(1);

        JsonElement violations = root.GetProperty("budgetViolations");
        violations.GetArrayLength().Should().Be(1);

        JsonElement violation = violations[0];
        violation.GetProperty("action").GetString().Should().Be("heavy_layout_recalc");
        violation.GetProperty("durationMs").GetDouble().Should().Be(3.25);
        violation.GetProperty("budgetExceeded").GetBoolean().Should().Be(true);
    }

    [Fact]
    public async Task Stop_CorrelatesForcedBusyLoopHitch_ToTargetPanel_AcceptanceTest()
    {
        // Phase 5 Acceptance Test:
        // A hitch forced deliberately (> 30 ms busy-loop in a test handler)
        // shows up accurately attributed to the correct panel.
        Mock<ITaoClient> mockTao = new();
        mockTao.SetupGet(t => t.IsConnected).Returns(true);

        // TAO transition log reports a forced busy-loop on the "store" panel at frame 5020
        string mockLogJson = @"{
            ""status"": ""success"",
            ""transitions"": [
                {
                    ""frame"": 5020,
                    ""hostTime"": 81200,
                    ""from"": ""main_menu"",
                    ""to"": ""store"",
                    ""action"": ""forced_busy_loop"",
                    ""durationMs"": 38.5,
                    ""budgetExceeded"": true,
                    ""extra"": { ""forced"": true, ""targetDurationMs"": 35.0 }
                }
            ],
            ""total"": 1,
            ""currentFrame"": 5025,
            ""currentHostTime"": 81300
        }";
        using JsonDocument doc = JsonDocument.Parse(mockLogJson);
        mockTao.Setup(t => t.DispatchAsync(It.Is<string>(a => a == "get_transition_log"), It.IsAny<object>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(TaoResponse.Ok("get_transition_log", doc.RootElement.Clone(), 5.0));

        // Native shared memory telemetry captures a 38.5 ms CPU frame stall on frame 5020
        List<FrameTelemetryRecord> testRecords =
        [
            new() { FrameIndex = 5018, CpuFrameTimeMs = 6.9f },
            new() { FrameIndex = 5019, CpuFrameTimeMs = 6.8f },
            new() { FrameIndex = 5020, CpuFrameTimeMs = 38.5f, GpuFrameTimeMs = 5.2f, ReservedFlags = FrameTelemetryRecord.FlagHitch | FrameTelemetryRecord.FlagCpuStall },
            new() { FrameIndex = 5021, CpuFrameTimeMs = 7.1f }
        ];

        PerfWindowHandler handler = new(
            taoClient: mockTao.Object,
            logger: msg => _output.WriteLine(msg),
            testRecordsProvider: _ => testRecords);

        // 1. Start window
        using JsonDocument startDoc = JsonDocument.Parse("{\"action\":\"start\"}");
        await handler.HandleAsync(startDoc.RootElement);

        // 2. Stop window & verify attribution
        using JsonDocument stopDoc = JsonDocument.Parse("{\"action\":\"stop\"}");
        ToolCallResult res = await handler.HandleAsync(stopDoc.RootElement);

        res.IsError.Should().BeFalse();
        using JsonDocument resDoc = JsonDocument.Parse(((TextContentItem)res.Content[0]).Text);
        JsonElement root = resDoc.RootElement;

        // Verify summary
        JsonElement summary = root.GetProperty("summary");
        summary.GetProperty("hitchCount30ms").GetInt32().Should().Be(1);
        summary.GetProperty("frameBudgetPassed").GetBoolean().Should().BeFalse();

        // Verify hitch attribution
        JsonElement hitches = root.GetProperty("hitches");
        hitches.GetArrayLength().Should().Be(1);

        JsonElement hitch = hitches[0];
        hitch.GetProperty("frameIndex").GetUInt64().Should().Be(5020);
        hitch.GetProperty("cpuFrameTimeMs").GetDouble().Should().BeApproximately(38.5, 0.05);

        // Confirms hitch is accurately attributed to panel "store"
        hitch.TryGetProperty("attributedTransition", out JsonElement attr).Should().BeTrue();
        attr.GetProperty("to").GetString().Should().Be("store");
        attr.GetProperty("from").GetString().Should().Be("main_menu");
        attr.GetProperty("action").GetString().Should().Be("forced_busy_loop");
        attr.GetProperty("durationMs").GetDouble().Should().Be(38.5);
        attr.GetProperty("budgetExceeded").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task Stop_WhenFrameIndexZero_FallsBackToClockSyncAttribution()
    {
        // When Lua cannot read the native frame counter directly (frame == 0),
        // fallback to clock-sync baseline (QPC elapsed <-> Lua host time elapsed).
        Mock<ITaoClient> mockTao = new();
        mockTao.SetupGet(t => t.IsConnected).Returns(true);

        string mockLogJson = @"{
            ""status"": ""success"",
            ""transitions"": [
                {
                    ""frame"": 0,
                    ""timestamp"": 1050.0,
                    ""hostTime"": 1050.0,
                    ""from"": ""main_menu"",
                    ""to"": ""store"",
                    ""action"": ""panel_toggle"",
                    ""durationMs"": 42.0,
                    ""budgetExceeded"": true
                }
            ],
            ""total"": 1,
            ""currentFrame"": 0,
            ""currentTimestamp"": 1000.0,
            ""currentHostTime"": 1000.0
        }";
        using JsonDocument doc = JsonDocument.Parse(mockLogJson);
        mockTao.Setup(t => t.DispatchAsync(It.Is<string>(a => a == "sync_frame"), It.IsAny<object>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(TaoResponse.Ok("sync_frame", doc.RootElement.Clone(), 5.0));
        mockTao.Setup(t => t.DispatchAsync(It.Is<string>(a => a == "get_transition_log"), It.IsAny<object>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(TaoResponse.Ok("get_transition_log", doc.RootElement.Clone(), 5.0));

        // Native record at ~50ms elapsed QPC time
        long qpcFreq = Stopwatch.Frequency;
        long startQpc = 1000000;
        long hitchQpc = startQpc + (long)(0.050 * qpcFreq); // +50ms

        List<FrameTelemetryRecord> testRecords =
        [
            new() { FrameIndex = 100, TimestampQpc = hitchQpc, CpuFrameTimeMs = 42.0f, ReservedFlags = FrameTelemetryRecord.FlagHitch }
        ];

        PerfWindowHandler handler = new(
            taoClient: mockTao.Object,
            logger: msg => _output.WriteLine(msg),
            testRecordsProvider: _ => testRecords);

        // Start
        using JsonDocument startDoc = JsonDocument.Parse("{\"action\":\"start\"}");
        await handler.HandleAsync(startDoc.RootElement);

        // Stop
        using JsonDocument stopDoc = JsonDocument.Parse("{\"action\":\"stop\"}");
        ToolCallResult res = await handler.HandleAsync(stopDoc.RootElement);

        res.IsError.Should().BeFalse();
        using JsonDocument resDoc = JsonDocument.Parse(((TextContentItem)res.Content[0]).Text);
        JsonElement root = resDoc.RootElement;

        JsonElement hitches = root.GetProperty("hitches");
        hitches.GetArrayLength().Should().Be(1);
        hitches[0].TryGetProperty("attributedTransition", out JsonElement attr).Should().BeTrue();
        attr.GetProperty("to").GetString().Should().Be("store");
        attr.GetProperty("action").GetString().Should().Be("panel_toggle");
    }

    [Fact]
    public async Task SimulateHitch_DispatchesToTaoClient_ReturnsSuccess()
    {
        Mock<ITaoClient> mockTao = new();
        mockTao.SetupGet(t => t.IsConnected).Returns(true);

        string mockRespJson = "{\"status\":\"success\",\"simulatedHitchMs\":36.2,\"entry\":{\"action\":\"forced_busy_loop\",\"to\":\"store\"}}";
        using JsonDocument doc = JsonDocument.Parse(mockRespJson);
        mockTao.Setup(t => t.DispatchAsync(It.Is<string>(a => a == "simulate_hitch"), It.IsAny<object>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(TaoResponse.Ok("simulate_hitch", doc.RootElement.Clone(), 5.0));

        PerfWindowHandler handler = new(taoClient: mockTao.Object, logger: msg => _output.WriteLine(msg));

        using JsonDocument argDoc = JsonDocument.Parse("{\"action\":\"simulate_hitch\",\"durationMs\":35.0,\"panel\":\"store\"}");
        ToolCallResult res = await handler.HandleAsync(argDoc.RootElement);

        res.IsError.Should().BeFalse();
        using JsonDocument resDoc = JsonDocument.Parse(((TextContentItem)res.Content[0]).Text);
        resDoc.RootElement.GetProperty("status").GetString().Should().Be("success");
        resDoc.RootElement.GetProperty("simulatedHitchMs").GetDouble().Should().BeApproximately(36.2, 0.05);
    }

    [Fact]
    public async Task RingPriority_PrefersVidVulkanFastBusOver20HzGuardRing_AndDisablesSentinelFlagsOnFallback()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"kongor_pw_prio_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        string fastBusPath = Path.Combine(tempDir, "fastbus.shm");
        string guardPath = Path.Combine(tempDir, "guard.shm");

        try
        {
            const int fakePid = 42424;
            long qpcFreq = Stopwatch.Frequency;
            long ticksPer5Ms = (long)Math.Round(0.005 * qpcFreq);
            long ticksPer14Ms = (long)Math.Round(0.014 * qpcFreq);
            long ticksPer50Ms = (long)Math.Round(0.050 * qpcFreq);

            using MemoryMappedTelemetryBuffer fastBusProd = MemoryMappedTelemetryBuffer.CreateProducer(fastBusPath, 1024, (ulong)fakePid);
            using MemoryMappedTelemetryBuffer guardProd = MemoryMappedTelemetryBuffer.CreateProducer(guardPath, 1024, (ulong)fakePid);

            // Seed initial baseline frame #1 on both rings
            long fbQpc = 10_000_000;
            long gdQpc = 10_000_000;
            fastBusProd.TryWrite(new FrameTelemetryRecord { FrameIndex = 1, TimestampQpc = fbQpc, CpuFrameTimeMs = 2.0f, GpuFrameTimeMs = 2.5f, InputLatencyMs = 0.5f, DrawCallCount = 1400 });
            guardProd.TryWrite(new FrameTelemetryRecord { FrameIndex = 1, TimestampQpc = gdQpc, CpuFrameTimeMs = 5.0f, GpuFrameTimeMs = 4.0f, ReservedFlags = FrameTelemetryRecord.FlagHitch });

            // Case A: Both maps available -> must pick vid_vulkan_render_fastbus (Local\KongorTelemetryBuffer)
            using PerfWindowHandler fastBusHandler = new(
                logger: msg => _output.WriteLine(msg),
                mapOpenerOverride: name => name switch
                {
                    "Local\\KongorTelemetryBuffer" => MemoryMappedTelemetryBuffer.OpenConsumer(fastBusPath),
                    "Local\\KongorClientTelemetry_42424" => MemoryMappedTelemetryBuffer.OpenConsumer(guardPath),
                    _ => null
                });

            using JsonDocument startDocA = JsonDocument.Parse($"{{\"action\":\"start\",\"pid\":{fakePid}}}");
            ToolCallResult startResA = await fastBusHandler.HandleAsync(startDocA.RootElement);
            startResA.IsError.Should().BeFalse();

            for (ulong f = 2; f <= 201; f++)
            {
                bool isCpuStall = (f == 100 || f == 150);
                fbQpc += isCpuStall ? ticksPer14Ms : ticksPer5Ms;
                fastBusProd.TryWrite(new FrameTelemetryRecord
                {
                    FrameIndex = f,
                    TimestampQpc = fbQpc,
                    CpuFrameTimeMs = isCpuStall ? 11.5f : 2.1f,
                    GpuFrameTimeMs = 2.4f,
                    InputLatencyMs = 0.5f,
                    DrawCallCount = 1500,
                    ReservedFlags = isCpuStall ? (FrameTelemetryRecord.FlagHitch | FrameTelemetryRecord.FlagCpuStall) : 0U
                });
            }

            using JsonDocument stopDoc = JsonDocument.Parse("{\"action\":\"stop\"}");
            ToolCallResult stopResA = await fastBusHandler.HandleAsync(stopDoc.RootElement);
            stopResA.IsError.Should().BeFalse();

            using JsonDocument resDocA = JsonDocument.Parse(((TextContentItem)stopResA.Content[0]).Text);
            JsonElement summaryA = resDocA.RootElement.GetProperty("summary");
            summaryA.GetProperty("telemetrySource").GetString().Should().Be("vid_vulkan_render_fastbus");
            summaryA.GetProperty("isRenderFrameAccurate").GetBoolean().Should().BeTrue();
            summaryA.GetProperty("sampledFrames").GetInt32().Should().Be(200);
            summaryA.GetProperty("droppedRingFrames").GetUInt64().Should().Be(0UL);
            summaryA.GetProperty("medianFps").GetDouble().Should().BeApproximately(200.0, 2.0);
            summaryA.GetProperty("hitches").GetProperty("gt10ms").GetInt32().Should().Be(2);
            summaryA.GetProperty("sentinelHitchFlags").GetProperty("hitch").GetInt32().Should().Be(2);
            summaryA.GetProperty("sentinelHitchFlags").GetProperty("cpuStall").GetInt32().Should().Be(2);
            summaryA.GetProperty("avgDrawCalls").GetDouble().Should().BeApproximately(1500.0, 1.0);

            // Case B: FastBus absent -> falls back to version.dll 20Hz guard ring and MUST NOT score PTT bit 0 as sentinel hitches
            using PerfWindowHandler fallbackHandler = new(
                logger: msg => _output.WriteLine(msg),
                mapOpenerOverride: name => name == "Local\\KongorClientTelemetry_42424"
                    ? MemoryMappedTelemetryBuffer.OpenConsumer(guardPath)
                    : null);

            ToolCallResult startResB = await fallbackHandler.HandleAsync(startDocA.RootElement);
            startResB.IsError.Should().BeFalse();

            for (ulong t = 2; t <= 42; t++)
            {
                gdQpc += ticksPer50Ms;
                guardProd.TryWrite(new FrameTelemetryRecord
                {
                    FrameIndex = t,
                    TimestampQpc = gdQpc,
                    CpuFrameTimeMs = 5.5f,
                    GpuFrameTimeMs = 4.5f,
                    ReservedFlags = FrameTelemetryRecord.FlagHitch // simulates g_voicePttFlags bit 0 collision
                });
            }

            ToolCallResult stopResB = await fallbackHandler.HandleAsync(stopDoc.RootElement);
            stopResB.IsError.Should().BeFalse();

            using JsonDocument resDocB = JsonDocument.Parse(((TextContentItem)stopResB.Content[0]).Text);
            JsonElement summaryB = resDocB.RootElement.GetProperty("summary");
            summaryB.GetProperty("telemetrySource").GetString().Should().Be("native_guard_20hz_fallback");
            summaryB.GetProperty("isRenderFrameAccurate").GetBoolean().Should().BeFalse();
            summaryB.GetProperty("sentinelHitchFlags").GetProperty("hitch").GetInt32().Should().Be(0);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task LosslessDrain_CapturesOver4096FramesAcrossRingWraps_WithZeroDropsAndHighHzStats()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"kongor_pw_drain_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        string fastBusPath = Path.Combine(tempDir, "fastbus_wrap.shm");

        try
        {
            const int fakePid = 51515;
            // Use capacity 2048 and write 6000 frames (~3 full wraps) across ~600ms so 50ms PeriodicTimer drains losslessly with >4x jitter margin
            using MemoryMappedTelemetryBuffer prod = MemoryMappedTelemetryBuffer.CreateProducer(fastBusPath, 2048, (ulong)fakePid);
            long qpcFreq = Stopwatch.Frequency;
            long qpc = 50_000_000;
            prod.TryWrite(new FrameTelemetryRecord { FrameIndex = 1, TimestampQpc = qpc, CpuFrameTimeMs = 2.0f, GpuFrameTimeMs = 2.0f, DrawCallCount = 1550 });

            using PerfWindowHandler handler = new(
                logger: msg => _output.WriteLine(msg),
                mapOpenerOverride: name => name == "Local\\KongorTelemetryBuffer"
                    ? MemoryMappedTelemetryBuffer.OpenConsumer(fastBusPath)
                    : null);

            using JsonDocument startDoc = JsonDocument.Parse($"{{\"action\":\"start\",\"pid\":{fakePid}}}");
            ToolCallResult startRes = await handler.HandleAsync(startDoc.RootElement);
            startRes.IsError.Should().BeFalse();

            // Write 6,000 frames in 60 batches of 100 frames (~20ms per batch -> ~400ms to fill 2048-slot ring,
            // giving >8x PeriodicTimer(50ms) jitter margin even under 12-assembly parallel CI CPU saturation)
            const int totalToWrite = 6000;
            const int batchSize = 100;
            int written = 0;
            ulong frameIdx = 1;
            while (written < totalToWrite)
            {
                for (int b = 0; b < batchSize && written < totalToWrite; b++)
                {
                    written++;
                    frameIdx++;
                    double deltaMs = written switch
                    {
                        <= 3 => 38.0,
                        <= 15 => 20.0,
                        <= 75 => 12.5,
                        <= 195 => 9.0,
                        _ => 5.0
                    };
                    qpc += (long)Math.Round((deltaMs / 1000.0) * qpcFreq);
                    prod.TryWrite(new FrameTelemetryRecord
                    {
                        FrameIndex = frameIdx,
                        TimestampQpc = qpc,
                        CpuFrameTimeMs = (float)(deltaMs * 0.5),
                        GpuFrameTimeMs = (float)(deltaMs * 0.4),
                        InputLatencyMs = (float)(deltaMs * 0.1),
                        DrawCallCount = 1550,
                        ReservedFlags = deltaMs > 10.0 ? (FrameTelemetryRecord.FlagHitch | FrameTelemetryRecord.FlagCpuStall) : 0U
                    });
                }

                await Task.Delay(20);
            }

            // Allow one final drain cycle
            await Task.Delay(120);

            using JsonDocument stopDoc = JsonDocument.Parse("{\"action\":\"stop\"}");
            ToolCallResult stopRes = await handler.HandleAsync(stopDoc.RootElement);
            stopRes.IsError.Should().BeFalse();

            using JsonDocument resDoc = JsonDocument.Parse(((TextContentItem)stopRes.Content[0]).Text);
            JsonElement summary = resDoc.RootElement.GetProperty("summary");
            summary.GetProperty("sampledFrames").GetInt32().Should().Be(totalToWrite);
            summary.GetProperty("engineFramesElapsed").GetUInt64().Should().Be((ulong)totalToWrite);
            summary.GetProperty("droppedRingFrames").GetUInt64().Should().Be(0UL);
            summary.GetProperty("medianFps").GetDouble().Should().BeApproximately(200.0, 2.0);
            summary.GetProperty("p1LowFps").GetDouble().Should().BeGreaterThan(50.0).And.BeLessThan(100.0);
            summary.GetProperty("p01LowFps").GetDouble().Should().BeLessThan(45.0);

            JsonElement hitchesObj = summary.GetProperty("hitches");
            hitchesObj.GetProperty("gt8ms").GetInt32().Should().Be(195);
            hitchesObj.GetProperty("gt10ms").GetInt32().Should().Be(75);
            hitchesObj.GetProperty("gt16_67ms").GetInt32().Should().Be(15);
            hitchesObj.GetProperty("gt33_33ms").GetInt32().Should().Be(3);
            hitchesObj.GetProperty("gt50ms").GetInt32().Should().Be(0);
            summary.GetProperty("sentinelHitchFlags").GetProperty("cpuStall").GetInt32().Should().Be(75);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task AuditRuntime_ComputesSha256AndDetectsVkLayerAndOverlayDrift()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"kongor_pw_audit_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        string fakeVid = Path.Combine(tempDir, "vid_vulkan_x64.dll");
        string fakeVer = Path.Combine(tempDir, "version.dll");
        File.WriteAllText(fakeVid, "mock-vid-vulkan-binary-content");
        File.WriteAllText(fakeVer, "mock-version-binary-content");

        try
        {
            // Clean run
            using PerfWindowHandler cleanHandler = new(
                logger: msg => _output.WriteLine(msg),
                moduleEnumeratorOverride: _ =>
                [
                    ("vid_vulkan_x64.dll", fakeVid),
                    ("version.dll", fakeVer),
                    ("d3d9.dll", Path.Combine(tempDir, "d3d9.dll"))
                ]);

            using JsonDocument cleanArg = JsonDocument.Parse("{\"action\":\"audit_runtime\",\"pid\":9999,\"expectedVkLayerLoaded\":false}");
            ToolCallResult cleanRes = await cleanHandler.HandleAsync(cleanArg.RootElement);
            cleanRes.IsError.Should().BeFalse();

            using JsonDocument cleanDoc = JsonDocument.Parse(((TextContentItem)cleanRes.Content[0]).Text);
            cleanDoc.RootElement.GetProperty("status").GetString().Should().Be("passed");
            JsonElement cleanAudit = cleanDoc.RootElement.GetProperty("audit");
            cleanAudit.GetProperty("passed").GetBoolean().Should().BeTrue();
            cleanAudit.GetProperty("vkLayerReplayCaptureLoaded").GetBoolean().Should().BeFalse();
            cleanAudit.GetProperty("keyModules")[0].GetProperty("sha256").GetString().Should().HaveLength(64);

            // Drifted run
            using PerfWindowHandler driftedHandler = new(
                logger: msg => _output.WriteLine(msg),
                moduleEnumeratorOverride: _ =>
                [
                    ("vid_vulkan_x64.dll", fakeVid),
                    ("VkLayer_hon_replay_capture.dll", Path.Combine(tempDir, "VkLayer_hon_replay_capture.dll")),
                    ("RTSSHooks64.dll", @"C:\Program Files (x86)\RivaTuner Statistics Server\RTSSHooks64.dll")
                ]);

            ToolCallResult driftedRes = await driftedHandler.HandleAsync(cleanArg.RootElement);
            driftedRes.IsError.Should().BeFalse();

            using JsonDocument driftedDoc = JsonDocument.Parse(((TextContentItem)driftedRes.Content[0]).Text);
            driftedDoc.RootElement.GetProperty("status").GetString().Should().Be("drift_detected");
            JsonElement driftedAudit = driftedDoc.RootElement.GetProperty("audit");
            driftedAudit.GetProperty("passed").GetBoolean().Should().BeFalse();
            driftedAudit.GetProperty("vkLayerReplayCaptureLoaded").GetBoolean().Should().BeTrue();
            driftedAudit.GetProperty("detectedDeniedHooks").GetArrayLength().Should().Be(1);
            driftedAudit.GetProperty("findings").GetArrayLength().Should().Be(2);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }
}
