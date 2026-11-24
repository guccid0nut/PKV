// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// TAO (Together As One) MCP & Kongor CLI -- Created by guccidonut (https://github.com/guccidonut/PKV)

#pragma warning disable CA2026
using System.Text.Json;
using Kongor.Cli.Mcp.Protocol;
using Moq;
using Xunit;

namespace Kongor.Mcp.Tests;

/// <summary>
/// Unit test suite for Phase 3 Autonomous MCP Tools:
/// 1. kongor_camera_control
/// 2. kongor_server_perf_stream
/// 3. kongor_replay_diff
/// 4. kongor_db_pool_diagnostics
/// 5. kongor_ui_observer
/// 6. kongor_vfs_mount_graph
/// </summary>
public sealed class CameraAndPerfStreamingToolsTests
{
    private readonly McpToolRegistry _registry = new(registerDefaults: true);

    private static JsonElement ParseResult(ToolCallResult result)
    {
        Assert.False(result.IsError, result.FirstText);
        Assert.NotEmpty(result.Content);
        string text = result.Content[0] is TextContentItem textItem ? textItem.Text : result.FirstText;
        using var doc = JsonDocument.Parse(text);
        return doc.RootElement.Clone();
    }

    // =========================================================================
    // 1. kongor_camera_control
    // =========================================================================

    [Fact]
    public async Task CameraControl_GetAndSetPosition_UpdatesCoordinatesSuccessfully()
    {
        var handler = _registry.CameraControlHandler;

        // Set position
        var setArgs = JsonDocument.Parse("""{"action": "set_position", "x": 1500.5, "y": 2300.25, "z": 450.0}""").RootElement;
        var setRes = await handler.HandleAsync(setArgs);
        var setRoot = ParseResult(setRes);

        Assert.Equal("success", setRoot.GetProperty("status").GetString());
        Assert.Equal("set_position", setRoot.GetProperty("action").GetString());
        var pos = setRoot.GetProperty("position");
        Assert.Equal(1500.5, pos.GetProperty("x").GetDouble());
        Assert.Equal(2300.25, pos.GetProperty("y").GetDouble());
        Assert.Equal(450.0, pos.GetProperty("z").GetDouble());

        // Get position
        var getArgs = JsonDocument.Parse("""{"action": "get_position"}""").RootElement;
        var getRes = await handler.HandleAsync(getArgs);
        var getRoot = ParseResult(getRes);

        Assert.Equal("success", getRoot.GetProperty("status").GetString());
        Assert.Equal(1500.5, getRoot.GetProperty("position").GetProperty("x").GetDouble());
        Assert.Contains("1500.5", getRoot.GetProperty("readout").GetString());
    }

    [Fact]
    public async Task CameraControl_FovAndPitch_ClampsToValidBounds()
    {
        var handler = _registry.CameraControlHandler;

        // Set FOV clamped
        var fovArgs = JsonDocument.Parse("""{"action": "set_fov", "fov": 150.0}""").RootElement;
        var fovRes = await handler.HandleAsync(fovArgs);
        var fovRoot = ParseResult(fovRes);
        Assert.Equal(130.0, fovRoot.GetProperty("fov").GetDouble());

        // Set Pitch clamped
        var pitchArgs = JsonDocument.Parse("""{"action": "set_pitch", "pitch": -95.0}""").RootElement;
        var pitchRes = await handler.HandleAsync(pitchArgs);
        var pitchRoot = ParseResult(pitchRes);
        Assert.Equal(-89.0, pitchRoot.GetProperty("pitch").GetDouble());
    }

    [Fact]
    public async Task CameraControl_FollowEntity_UpdatesTargetEntityId()
    {
        var handler = _registry.CameraControlHandler;

        var followArgs = JsonDocument.Parse("""{"action": "follow_entity", "entity_id": 4}""").RootElement;
        var followRes = await handler.HandleAsync(followArgs);
        var followRoot = ParseResult(followRes);

        Assert.Equal("success", followRoot.GetProperty("status").GetString());
        Assert.Equal("follow_entity", followRoot.GetProperty("action").GetString());
        Assert.Equal(4, followRoot.GetProperty("trackedEntityId").GetInt32());
        Assert.Equal("follow", followRoot.GetProperty("mode").GetString());
    }

    [Fact]
    public async Task CameraControl_InterpolatePath_GeneratesSmoothCatmullRomWaypoints()
    {
        var handler = _registry.CameraControlHandler;

        var interpArgs = JsonDocument.Parse("""
        {
            "action": "interpolate_path",
            "duration_ms": 1000,
            "waypoints": [
                {"x": 100, "y": 100, "z": 50},
                {"x": 300, "y": 250, "z": 75},
                {"x": 600, "y": 500, "z": 100}
            ]
        }
        """).RootElement;

        var interpRes = await handler.HandleAsync(interpArgs);
        var interpRoot = ParseResult(interpRes);

        Assert.Equal("success", interpRoot.GetProperty("status").GetString());
        Assert.Equal("interpolate_path", interpRoot.GetProperty("action").GetString());
        Assert.Equal(3, interpRoot.GetProperty("waypointsCount").GetInt32());
        Assert.True(interpRoot.GetProperty("samplePoints").GetInt32() >= 10);
        Assert.True(interpRoot.GetProperty("trajectory").GetArrayLength() >= 10);
    }

    [Fact]
    public async Task CameraControl_CaptureView_ReturnsWindowCaptureMetadata()
    {
        const string mockBase64 = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=";
        Moq.Mock<Kongor.Cli.Mcp.Services.IWindowCapturer> mockCapturer = new();
        mockCapturer
            .Setup(c => c.CaptureAsync("hon_x64", "auto", true, null, false, Moq.It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Kongor.Cli.Mcp.Services.WindowCaptureResult
            {
                Success = true,
                Target = "hon_x64",
                WindowTitle = "Heroes of Newerth",
                Width = 1920,
                Height = 1080,
                Base64Png = mockBase64
            });

        Kongor.Cli.Mcp.Handlers.CameraControlHandler handler = new(windowCapturer: mockCapturer.Object);

        var captureArgs = JsonDocument.Parse("""{"action": "capture_view", "target": "hon_x64"}""").RootElement;
        var captureRes = await handler.HandleAsync(captureArgs);
        var captureRoot = ParseResult(captureRes);

        Assert.Equal("success", captureRoot.GetProperty("status").GetString());
        Assert.Equal("capture_view", captureRoot.GetProperty("action").GetString());
        Assert.True(captureRoot.TryGetProperty("camera", out _));
        Assert.True(captureRoot.TryGetProperty("capture", out JsonElement captureEl));
        Assert.True(captureEl.GetProperty("success").GetBoolean());
        Assert.Equal(1920, captureEl.GetProperty("width").GetInt32());
        Assert.Equal(1080, captureEl.GetProperty("height").GetInt32());
    }

    // =========================================================================
    // 2. kongor_server_perf_stream
    // =========================================================================

    [Fact]
    public async Task ServerPerfStream_RequiresTargetPort_FailsWhenOmittedPerGeminiRule()
    {
        var handler = _registry.ServerPerfStreamHandler;

        // Missing port must fail strictly per AGENTS.md Dedicated Server Command Routing Rule
        var noPortArgs = JsonDocument.Parse("""{"action": "snapshot"}""").RootElement;
        var result = await handler.HandleAsync(noPortArgs);

        Assert.True(result.IsError);
        Assert.Contains("target listen port", result.FirstText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ServerPerfStream_StreamTick_ReportsSubMillisecondTimerAccuracy()
    {
        var handler = _registry.ServerPerfStreamHandler;

        var tickArgs = JsonDocument.Parse("""{"action": "snapshot", "port": 11235, "allowSimulation": true}""").RootElement;
        var result = await handler.HandleAsync(tickArgs);
        var root = ParseResult(result);

        Assert.Equal("success", root.GetProperty("status").GetString());
        Assert.Equal(11235, root.GetProperty("port").GetInt32());

        var metrics = root.GetProperty("metrics");
        Assert.True(metrics.GetProperty("sleepDurationOvershootMs").GetDouble() < 1.5, "Sleep overshoot must verify ~1.0ms timer resolution");
        Assert.True(metrics.GetProperty("tickExecutionTimeMs").GetDouble() > 0.0);
        Assert.True(metrics.GetProperty("frameDriftMs").GetDouble() >= 0.0);
        Assert.True(metrics.GetProperty("isTimerResolutionOptimal").GetBoolean());
    }

    [Fact]
    public async Task ServerPerfStream_Lifecycle_ManagesStreamSubscription()
    {
        var handler = _registry.ServerPerfStreamHandler;

        var startArgs = JsonDocument.Parse("""{"action": "start_stream", "port": 11235, "interval_ms": 50}""").RootElement;
        var startRes = await handler.HandleAsync(startArgs);
        var startRoot = ParseResult(startRes);

        Assert.Equal("success", startRoot.GetProperty("status").GetString());
        Assert.Equal(11235, startRoot.GetProperty("port").GetInt32());
        Assert.Contains("/stream/server-perf/11235", startRoot.GetProperty("streamUri").GetString());

        var stopArgs = JsonDocument.Parse("""{"action": "stop_stream", "port": 11235}""").RootElement;
        var stopRes = await handler.HandleAsync(stopArgs);
        var stopRoot = ParseResult(stopRes);

        Assert.Equal("success", stopRoot.GetProperty("status").GetString());
    }

    [Fact]
    public async Task ServerPerfStream_ProfileEntities_ReadsLiveSharedMemoryV2RingAndDetectsHitches()
    {
        var handler = _registry.ServerPerfStreamHandler;
        int testPort = 19455;
        string tempShmPath = Path.Combine(Path.GetTempPath(), $"kongor_server_perf_test_{Guid.NewGuid():N}.shm");

        try
        {
            using (var producer = ASPIRE.Common.Telemetry.MemoryMappedTelemetryBuffer.CreateProducer(tempShmPath, capacity: 64))
            {
                producer.UpdateLiveEngineState(
                    gamePhase: 3,
                    activeMatchId: 88012,
                    connectedPlayers: 10,
                    luaMemoryKb: 14320,
                    currentMap: "caldavar",
                    playerName: "KongorSrv");

                // Frame 1: Normal 1.8ms server tick
                producer.TryWrite(new NEXUS.Contracts.Telemetry.FrameTelemetryRecord
                {
                    FrameIndex = 1,
                    TimestampQpc = 1000,
                    CpuFrameTimeMs = 1.8f,
                    ActiveEntities = 242,
                    MemoryWorkingSetMb = 612,
                    ServerTick = 1200,
                    ReservedFlags = 0
                });

                // Frame 2: Hitch tick (7.5ms > 5.0ms budget)
                producer.TryWrite(new NEXUS.Contracts.Telemetry.FrameTelemetryRecord
                {
                    FrameIndex = 2,
                    TimestampQpc = 2000,
                    CpuFrameTimeMs = 7.5f,
                    ActiveEntities = 258,
                    MemoryWorkingSetMb = 615,
                    ServerTick = 1201,
                    ReservedFlags = NEXUS.Contracts.Telemetry.FrameTelemetryRecord.FlagHitch
                });

                var profileArgs = JsonDocument.Parse(
                    $$"""{"action": "profile_entities", "port": {{testPort}}, "shm_path": {{JsonSerializer.Serialize(tempShmPath)}}}""").RootElement;
                var res = await handler.HandleAsync(profileArgs);
                var root = ParseResult(res);

                Assert.Equal("success", root.GetProperty("status").GetString());
                Assert.Equal("profile_entities", root.GetProperty("action").GetString());
                Assert.Equal("shared_memory_v2", root.GetProperty("telemetrySource").GetString());

                var metrics = root.GetProperty("metrics");
                Assert.Equal("shared_memory_v2", metrics.GetProperty("telemetrySource").GetString());
                Assert.Equal(258u, metrics.GetProperty("activeEntities").GetUInt32());
                Assert.Equal(1201u, metrics.GetProperty("serverTick").GetUInt32());
                Assert.Equal(14320u, metrics.GetProperty("luaMemoryKb").GetUInt32());
                Assert.Equal(10u, metrics.GetProperty("connectedPlayers").GetUInt32());
                Assert.Equal("caldavar", metrics.GetProperty("currentMap").GetString());
                Assert.Contains("Hitch", metrics.GetProperty("status").GetString(), StringComparison.OrdinalIgnoreCase);

                var profiler = metrics.GetProperty("entityProfiler");
                Assert.Equal(258u, profiler.GetProperty("activeEntities").GetUInt32());
                Assert.Equal(14320u, profiler.GetProperty("luaMemoryKb").GetUInt32());
                Assert.Equal(2, profiler.GetProperty("sampledFrames").GetInt32());
                Assert.Equal(1, profiler.GetProperty("hitchCount").GetInt32());
                Assert.Equal(0, profiler.GetProperty("cpuStallCount").GetInt32());
                Assert.Equal(7.5, profiler.GetProperty("maxTickTimeMs").GetDouble(), 2);
                Assert.False(profiler.GetProperty("frameBudgetHealthy").GetBoolean());
            }
        }
        finally
        {
            if (File.Exists(tempShmPath))
            {
                try { File.Delete(tempShmPath); } catch { }
            }
        }
    }

    // =========================================================================
    // 3. kongor_replay_diff
    // =========================================================================

    [Fact]
    public async Task ReplayDiff_DiffMetadata_DetectsReplayDiscrepancies()
    {
        var handler = _registry.ReplayDiffHandler;
        string serverPath = Path.GetTempFileName();
        string clientPath = Path.GetTempFileName();
        try
        {
            await File.WriteAllBytesAsync(serverPath, new byte[] { 1, 2, 3, 4 });
            await File.WriteAllBytesAsync(clientPath, new byte[] { 1, 2, 3, 4 });

            var diffArgs = JsonDocument.Parse($$"""
            {
                "action": "diff_metadata",
                "server_replay": "{{serverPath.Replace("\\", "\\\\")}}",
                "client_replay": "{{clientPath.Replace("\\", "\\\\")}}"
            }
            """).RootElement;

            var result = await handler.HandleAsync(diffArgs);
            var root = ParseResult(result);

            Assert.Equal("success", root.GetProperty("status").GetString());
            Assert.Equal("diff_metadata", root.GetProperty("action").GetString());
            Assert.True(root.GetProperty("isMatch").GetBoolean());
            Assert.True(root.GetProperty("versionMatch").GetBoolean());
            Assert.True(root.GetProperty("mapMatch").GetBoolean());
            Assert.Equal(0, root.GetProperty("comparison").GetProperty("discrepanciesCount").GetInt32());
        }
        finally
        {
            if (File.Exists(serverPath)) File.Delete(serverPath);
            if (File.Exists(clientPath)) File.Delete(clientPath);
        }
    }

    [Fact]
    public async Task ReplayDiff_CompareTicks_DetectsDesyncEventsDeterministically()
    {
        var handler = _registry.ReplayDiffHandler;
        string serverPath = Path.GetTempFileName();
        string clientPath = Path.GetTempFileName();
        try
        {
            await File.WriteAllBytesAsync(serverPath, new byte[] { 1, 2, 3, 4 });
            await File.WriteAllBytesAsync(clientPath, new byte[] { 1, 2, 3, 4 });

            var compareArgs = JsonDocument.Parse($$"""
            {
                "action": "compare_ticks",
                "server_replay": "{{serverPath.Replace("\\", "\\\\")}}",
                "client_replay": "{{clientPath.Replace("\\", "\\\\")}}",
                "tick": 1000,
                "tolerance": 0.01
            }
            """).RootElement;

            var result = await handler.HandleAsync(compareArgs);
            var root = ParseResult(result);

            Assert.Equal("success", root.GetProperty("status").GetString());
            Assert.Equal(1000, root.GetProperty("tick").GetInt32());
            Assert.Equal(0, root.GetProperty("desyncCount").GetInt32());
            Assert.True(root.GetProperty("inSync").GetBoolean());
            Assert.Equal(10, root.GetProperty("entitiesCompared").GetInt32());
        }
        finally
        {
            if (File.Exists(serverPath)) File.Delete(serverPath);
            if (File.Exists(clientPath)) File.Delete(clientPath);
        }
    }

    // =========================================================================
    // 4. kongor_db_pool_diagnostics
    // =========================================================================

    [Fact]
    public async Task DbPoolDiagnostics_Status_ReportsConnectionHeadroomAndMetrics()
    {
        var handler = _registry.DbPoolDiagnosticsHandler;

        var statusArgs = JsonDocument.Parse("""{"action": "status"}""").RootElement;
        var result = await handler.HandleAsync(statusArgs);
        var root = ParseResult(result);

        Assert.Equal("success", root.GetProperty("status").GetString());
        Assert.Equal("Healthy", root.GetProperty("health").GetString());

        var pools = root.GetProperty("pools");
        Assert.True(pools.GetProperty("npgsql").GetProperty("maxPoolSize").GetInt32() >= 100);
        Assert.True(pools.GetProperty("sqlClient").GetProperty("maxPoolSize").GetInt32() >= 100);
    }

    [Fact]
    public async Task DbPoolDiagnostics_StarvationCheck_ValidatesThreadPoolHeadroom()
    {
        var handler = _registry.DbPoolDiagnosticsHandler;

        var checkArgs = JsonDocument.Parse("""{"action": "starvation_check"}""").RootElement;
        var result = await handler.HandleAsync(checkArgs);
        var root = ParseResult(result);

        Assert.Equal("success", root.GetProperty("status").GetString());
        Assert.False(root.GetProperty("starvationDetected").GetBoolean());
        Assert.Equal("Normal", root.GetProperty("severity").GetString());
    }

    // =========================================================================
    // 5. kongor_ui_observer
    // =========================================================================

    [Fact]
    public async Task UiObserver_WaitVisible_AwaitsWidgetVisibilityCompliantWithK2Rules()
    {
        var handler = _registry.UiObserverHandler;

        var waitArgs = JsonDocument.Parse("""
        {
            "action": "wait_visible",
            "widget": "sysbar_quickStart",
            "timeout_ms": 1000
        }
        """).RootElement;

        var result = await handler.HandleAsync(waitArgs);
        var root = ParseResult(result);

        Assert.Equal("success", root.GetProperty("status").GetString());
        Assert.Equal("wait_visible", root.GetProperty("action").GetString());
        Assert.Equal("sysbar_quickStart", root.GetProperty("widget").GetString());
        Assert.True(root.GetProperty("matched").GetBoolean());
        Assert.True(root.GetProperty("visible").GetBoolean());
        Assert.True(root.GetProperty("uiManagerVerified").GetBoolean());
    }

    [Fact]
    public async Task UiObserver_WaitTextChange_AwaitsExpectedText()
    {
        var handler = _registry.UiObserverHandler;

        var textArgs = JsonDocument.Parse("""
        {
            "action": "wait_text_change",
            "widget": "hero_status_label",
            "expected_text": "Hero_Pyromancer",
            "timeout_ms": 1000
        }
        """).RootElement;

        var result = await handler.HandleAsync(textArgs);
        var root = ParseResult(result);

        Assert.Equal("success", root.GetProperty("status").GetString());
        Assert.True(root.GetProperty("matched").GetBoolean());
        Assert.Equal("Hero_Pyromancer", root.GetProperty("currentText").GetString());
    }

    // =========================================================================
    // 6. kongor_vfs_mount_graph
    // =========================================================================

    private static string CreateTempVfsClientFixture()
    {
        string baseDir = Path.Combine(Path.GetTempPath(), "kongor_vfs_fixture_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(baseDir, "override", "ui"));
        Directory.CreateDirectory(Path.Combine(baseDir, "mods"));
        Directory.CreateDirectory(Path.Combine(baseDir, "game"));
        Directory.CreateDirectory(Path.Combine(baseDir, "ClientPackageFiles", "ui"));

        File.WriteAllText(Path.Combine(baseDir, "override", "ui", "main.interface"), "<interface name=\"main_override\"/>");
        File.WriteAllText(Path.Combine(baseDir, "ClientPackageFiles", "ui", "main.interface"), "<interface name=\"main_cpf\"/>");

        using (var modZip = System.IO.Compression.ZipFile.Open(Path.Combine(baseDir, "mods", "custom_hud.s2z"), System.IO.Compression.ZipArchiveMode.Create))
        {
            var entry = modZip.CreateEntry("ui/hud.interface");
            using var writer = new StreamWriter(entry.Open());
            writer.Write("<interface name=\"hud_mod\"/>");
        }

        using (var baseZip = System.IO.Compression.ZipFile.Open(Path.Combine(baseDir, "game", "resources0.s2z"), System.IO.Compression.ZipArchiveMode.Create))
        {
            var e1 = baseZip.CreateEntry("ui/hud.interface");
            using (var w1 = new StreamWriter(e1.Open())) w1.Write("<interface name=\"hud_base\"/>");
            var e2 = baseZip.CreateEntry("ui/main.interface");
            using (var w2 = new StreamWriter(e2.Open())) w2.Write("<interface name=\"main_base\"/>");
        }

        return baseDir;
    }

    [Fact]
    public async Task VfsMountGraph_InspectMounts_ReturnsPriorityOrderedArchiveTopology()
    {
        string tempClient = CreateTempVfsClientFixture();
        try
        {
            var handler = _registry.VfsMountGraphHandler;
            var inspectArgs = JsonDocument.Parse(JsonSerializer.Serialize(new { action = "inspect_mounts", game_dir = tempClient })).RootElement;
            var result = await handler.HandleAsync(inspectArgs);
            var root = ParseResult(result);

            Assert.Equal("success", root.GetProperty("status").GetString());
            Assert.Equal("StaticOfflineAnalysis", root.GetProperty("executionFidelity").GetString());
            Assert.Equal(4, root.GetProperty("totalLayers").GetInt32());
            Assert.Contains("override/ (P1000)", root.GetProperty("topology").GetString());
            Assert.Contains("mods/custom_hud.s2z (P900)", root.GetProperty("topology").GetString());

            var layers = root.GetProperty("mountLayers");
            Assert.Equal(1000, layers[0].GetProperty("priority").GetInt32());
            Assert.Equal(900, layers[1].GetProperty("priority").GetInt32());
            Assert.Equal(500, layers[2].GetProperty("priority").GetInt32());
            Assert.Equal(5, layers[3].GetProperty("priority").GetInt32());
        }
        finally
        {
            if (Directory.Exists(tempClient)) Directory.Delete(tempClient, recursive: true);
        }
    }

    [Fact]
    public async Task VfsMountGraph_ResolveConflict_PrioritizesModArchiveOverBase()
    {
        string tempClient = CreateTempVfsClientFixture();
        try
        {
            var handler = _registry.VfsMountGraphHandler;
            var resolveArgs = JsonDocument.Parse(JsonSerializer.Serialize(new { action = "resolve_conflict", resource = "ui/hud.interface", game_dir = tempClient })).RootElement;
            var result = await handler.HandleAsync(resolveArgs);
            var root = ParseResult(result);

            Assert.Equal("success", root.GetProperty("status").GetString());
            Assert.Equal("StaticOfflineAnalysis", root.GetProperty("executionFidelity").GetString());
            Assert.Equal("mods/custom_hud.s2z", root.GetProperty("resolvedArchive").GetString());
            Assert.Equal(900, root.GetProperty("resolvedPriority").GetInt32());
            Assert.Equal(2, root.GetProperty("conflictCount").GetInt32());
        }
        finally
        {
            if (Directory.Exists(tempClient)) Directory.Delete(tempClient, recursive: true);
        }
    }

    [Fact]
    public async Task VfsMountGraph_DetectShadowing_IdentifiesShadowedAssets()
    {
        string tempClient = CreateTempVfsClientFixture();
        try
        {
            var handler = _registry.VfsMountGraphHandler;
            var shadowArgs = JsonDocument.Parse(JsonSerializer.Serialize(new { action = "detect_shadowing", game_dir = tempClient })).RootElement;
            var result = await handler.HandleAsync(shadowArgs);
            var root = ParseResult(result);

            Assert.Equal("success", root.GetProperty("status").GetString());
            Assert.Equal("StaticOfflineAnalysis", root.GetProperty("executionFidelity").GetString());
            Assert.Equal(1, root.GetProperty("shadowedFilesCount").GetInt32());
            var entries = root.GetProperty("shadowedEntries");
            Assert.Equal("ui/main.interface", entries[0].GetProperty("resource").GetString());
            Assert.Equal("override/", entries[0].GetProperty("authoritativeSource").GetString());
        }
        finally
        {
            if (Directory.Exists(tempClient)) Directory.Delete(tempClient, recursive: true);
        }
    }

    [Fact]
    public void McpToolRegistry_WhenInvoked_RegistersAllPhase3Tools()
    {
        string? prev = Environment.GetEnvironmentVariable("KONGOR_MCP_INCLUDE_SIMULATED");
        HashSet<string> tools;
        try
        {
            Environment.SetEnvironmentVariable("KONGOR_MCP_INCLUDE_SIMULATED", "1");
            tools = _registry.ListTools().Select(t => t.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        }
        finally
        {
            Environment.SetEnvironmentVariable("KONGOR_MCP_INCLUDE_SIMULATED", prev);
        }

        Assert.Contains("kongor_camera_control", tools);
        Assert.Contains("kongor_server_perf_stream", tools);
        Assert.Contains("kongor_replay_diff", tools);
        Assert.Contains("kongor_db_pool_diagnostics", tools);
        Assert.Contains("kongor_ui_observer", tools);
        Assert.Contains("kongor_vfs_mount_graph", tools);
    }
}
