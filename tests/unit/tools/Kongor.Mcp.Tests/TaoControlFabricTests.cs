// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// TAO (Together As One) MCP & Kongor CLI -- Created by guccidonut (https://github.com/guccidonut/PKV)

using System.Diagnostics;
using System.IO;
using System.Text.Json;
using FluentAssertions;
using Kongor.Cli.Mcp.Handlers;
using Kongor.Cli.Mcp.Imaging;
using Kongor.Cli.Mcp.Protocol;
using Kongor.Cli.Mcp.Services;
using Xunit;

namespace Kongor.Mcp.Tests;

public sealed class TaoControlFabricTests : IDisposable
{
    private readonly string _tempFixtureDir;
    private readonly string _tempArtifactDir;

    public TaoControlFabricTests()
    {
        _tempFixtureDir = Path.Combine(Path.GetTempPath(), "TaoTests_Fixtures_" + Guid.NewGuid().ToString("N"));
        _tempArtifactDir = Path.Combine(Path.GetTempPath(), "TaoTests_Artifacts_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempFixtureDir);
        Directory.CreateDirectory(_tempArtifactDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempFixtureDir, recursive: true); } catch { }
        try { Directory.Delete(_tempArtifactDir, recursive: true); } catch { }
    }

    [Fact]
    public async Task GameStepHandler_TargetBoth_DispatchesToClientAndServerConcurrently()
    {
        var timeProvider = TimeProvider.System;
        var clientHandler = new GameClientManageHandler(timeProvider);
        var serverHandler = new GameServerManageHandler(timeProvider);
        var ipcBridge = new GameIpcBridge();
        var stepHandler = new GameStepHandler(clientHandler, serverHandler, ipcBridge, timeProvider);

        var args = JsonElement.Parse("""
        {
            "action": "step",
            "frames": 5,
            "speed": 2.0,
            "target": "both",
            "port": 11235
        }
        """);

        ToolCallResult result = await stepHandler.HandleAsync(args, CancellationToken.None);

        result.Should().NotBeNull();
        result.IsError.Should().BeFalse();

        using var doc = JsonDocument.Parse(result.FirstText);
        var root = doc.RootElement;

        root.GetProperty("status").GetString().Should().Be("success");
        root.GetProperty("executionMode").GetString().Should().NotBeNullOrWhiteSpace();
        root.GetProperty("target").GetString().Should().Be("both");
        root.GetProperty("framesStepped").GetInt32().Should().Be(5);
        root.GetProperty("speed").GetDouble().Should().Be(2.0);

        var dispatched = root.GetProperty("dispatchedCommands");
        dispatched.GetArrayLength().Should().BeGreaterOrEqualTo(2);
    }

    [Fact]
    public async Task GameStepHandler_WithDuration_ResetsTimescaleAfterDelay()
    {
        var timeProvider = TimeProvider.System;
        var clientHandler = new GameClientManageHandler(timeProvider);
        var serverHandler = new GameServerManageHandler(timeProvider);
        var ipcBridge = new GameIpcBridge();
        var stepHandler = new GameStepHandler(clientHandler, serverHandler, ipcBridge, timeProvider);

        var args = JsonElement.Parse("""
        {
            "action": "timescale",
            "speed": 10.0,
            "duration_ms": 20,
            "target": "both"
        }
        """);

        ToolCallResult result = await stepHandler.HandleAsync(args, CancellationToken.None);

        result.Should().NotBeNull();
        result.IsError.Should().BeFalse();

        using var doc = JsonDocument.Parse(result.FirstText);
        var root = doc.RootElement;

        root.GetProperty("status").GetString().Should().Be("success");
        root.GetProperty("speed").GetDouble().Should().Be(10.0);
        root.GetProperty("restoredAfterDuration").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task AssertVisualHandler_MatchingFixture_ReturnsSuccessAndHighSsim()
    {
        var timeProvider = TimeProvider.System;
        var clientHandler = new GameClientManageHandler(timeProvider);
        var serverHandler = new GameServerManageHandler(timeProvider);
        var windowCapturer = new WindowCapturer(timeProvider: timeProvider);

        // Pre-create baseline golden fixture matching test capture generator (128x128 gradient)
        string fixturePath = Path.Combine(_tempFixtureDir, "test_golden.png");
        using (var img = new Image<Rgba32>(128, 128))
        {
            for (int y = 0; y < 128; y++)
            {
                for (int x = 0; x < 128; x++)
                {
                    img[x, y] = new Rgba32((byte)(x * 2), (byte)(y * 2), 128, 255);
                }
            }
            await img.SaveAsPngAsync(fixturePath);
        }

        var assertHandler = new AssertVisualHandler(
            windowCapturer,
            clientHandler,
            serverHandler,
            timeProvider,
            fixtureDirectory: _tempFixtureDir,
            artifactDirectory: _tempArtifactDir);

        var args = JsonElement.Parse($$"""
        {
            "fixture": "{{fixturePath.Replace('\\', '/')}}",
            "threshold": 0.95
        }
        """);

        ToolCallResult result = await assertHandler.HandleAsync(args, CancellationToken.None);

        result.Should().NotBeNull();
        result.IsError.Should().BeFalse();

        using var doc = JsonDocument.Parse(result.FirstText);
        var root = doc.RootElement;

        root.GetProperty("status").GetString().Should().Be("success");
        root.GetProperty("match").GetBoolean().Should().BeTrue();
        root.GetProperty("ssim").GetDouble().Should().BeGreaterOrEqualTo(0.95);
        root.GetProperty("executionMode").GetString().Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task AssertVisualHandler_FailingFixture_ReturnsMismatchWithMetrics()
    {
        var timeProvider = TimeProvider.System;
        var clientHandler = new GameClientManageHandler(timeProvider);
        var serverHandler = new GameServerManageHandler(timeProvider);
        var windowCapturer = new WindowCapturer(timeProvider: timeProvider);

        // Pre-create completely divergent fixture (all black)
        string fixturePath = Path.Combine(_tempFixtureDir, "test_contrast.png");
        using (var img = new Image<Rgba32>(128, 128))
        {
            for (int y = 0; y < 128; y++)
            {
                for (int x = 0; x < 128; x++)
                {
                    img[x, y] = new Rgba32(0, 0, 0, 255);
                }
            }
            await img.SaveAsPngAsync(fixturePath);
        }

        var assertHandler = new AssertVisualHandler(
            windowCapturer,
            clientHandler,
            serverHandler,
            timeProvider,
            fixtureDirectory: _tempFixtureDir,
            artifactDirectory: _tempArtifactDir);

        var args = JsonElement.Parse($$"""
        {
            "fixture": "{{fixturePath.Replace('\\', '/')}}",
            "threshold": 0.99
        }
        """);

        ToolCallResult result = await assertHandler.HandleAsync(args, CancellationToken.None);

        result.Should().NotBeNull();
        result.IsError.Should().BeTrue();

        using var doc = JsonDocument.Parse(result.FirstText);
        var root = doc.RootElement;

        root.GetProperty("status").GetString().Should().Be("mismatch");
        root.GetProperty("match").GetBoolean().Should().BeFalse();
        root.GetProperty("ssim").GetDouble().Should().BeLessThan(0.99);
    }

    [Fact]
    public async Task AssertVisualHandler_MissingFixtureWithGenerate_CreatesGoldenBaseline()
    {
        var timeProvider = TimeProvider.System;
        var clientHandler = new GameClientManageHandler(timeProvider);
        var serverHandler = new GameServerManageHandler(timeProvider);
        var windowCapturer = new WindowCapturer(timeProvider: timeProvider);

        string fixturePath = Path.Combine(_tempFixtureDir, "new_baseline.png");
        File.Exists(fixturePath).Should().BeFalse();

        var assertHandler = new AssertVisualHandler(
            windowCapturer,
            clientHandler,
            serverHandler,
            timeProvider,
            fixtureDirectory: _tempFixtureDir,
            artifactDirectory: _tempArtifactDir);

        var args = JsonElement.Parse($$"""
        {
            "fixture": "{{fixturePath.Replace('\\', '/')}}",
            "generate_fixture_if_missing": true
        }
        """);

        ToolCallResult result = await assertHandler.HandleAsync(args, CancellationToken.None);

        result.Should().NotBeNull();
        result.IsError.Should().BeFalse();
        File.Exists(fixturePath).Should().BeTrue();

        using var doc = JsonDocument.Parse(result.FirstText);
        var root = doc.RootElement;

        root.GetProperty("status").GetString().Should().Be("success");
        root.GetProperty("match").GetBoolean().Should().BeTrue();
        root.GetProperty("generated").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public void ExecutionTruthEnvelope_WhenInvoked_PopulatesLivenessAndModeDeterministically()
    {
        var timeProvider = TimeProvider.System;
        var targetStatus = new TargetStatus
        {
            Client = new ClientLivenessStatus
            {
                Running = true,
                ProcessId = 1234,
                EngineReady = true,
                IpcConnected = true,
                WindowMode = "headless"
            },
            Server = new ServerLivenessStatus
            {
                Running = true,
                ProcessId = 5678,
                Port = 11235,
                PipeConnected = true,
                Map = "caldavar"
            }
        };

        var metrics = new ExecutionMetrics
        {
            RoundtripLatencyMs = 2.4,
            EngineRenderFrame = 1420,
            Timestamp = timeProvider.GetUtcNow().ToString("o")
        };

        ToolCallResult result = ExecutionTruthEnvelope.CreateJsonResult(
            status: "success",
            executionMode: ExecutionMode.LiveEngineRpc,
            targetStatus: targetStatus,
            metrics: metrics,
            data: new { test = "ok" });

        result.IsError.Should().BeFalse();
        using var doc = JsonDocument.Parse(result.FirstText);
        var root = doc.RootElement;

        root.GetProperty("status").GetString().Should().Be("success");
        root.GetProperty("executionMode").GetString().Should().Be("live_engine_rpc");
        root.GetProperty("test").GetString().Should().Be("ok");

        var client = root.GetProperty("targetStatus").GetProperty("client");
        client.GetProperty("running").GetBoolean().Should().BeTrue();
        client.GetProperty("processId").GetInt32().Should().Be(1234);

        var server = root.GetProperty("targetStatus").GetProperty("server");
        server.GetProperty("port").GetInt32().Should().Be(11235);
    }

    [Fact]
    public void JobObjectManager_AssignProcess_ReturnsTrueOnWindows()
    {
        if (OperatingSystem.IsWindows())
        {
            using var proc = Process.GetCurrentProcess();
            bool assigned = JobObjectManager.AssignProcess(proc);
            assigned.Should().BeTrue();
        }
    }

    [Fact]
    public void TaoLua_SyntaxAndEncoding_IsValidLua()
    {
        if (!NexusTestEnvironment.HasGameAssets) return;

        string current = AppContext.BaseDirectory;
        DirectoryInfo? dir = new(current);
        string? luaPath = null;
        while (dir != null)
        {
            string candidate = Path.Combine(dir.FullName, "games", "shared", "ui", "scripts", "tao.lua");
            if (File.Exists(candidate))
            {
                luaPath = candidate;
                break;
            }
            dir = dir.Parent;
        }

        luaPath.Should().NotBeNull("tao.lua must exist in games/shared/ui/scripts/");
        File.Exists(luaPath).Should().BeTrue();

        string luaContent = File.ReadAllText(luaPath!);
        luaContent.Should().ContainAny("TAO.Version = \"2.0.0\"", "TAO.Version = \"2.1.0\"", "TAO.Version = \"2.2.0\"");
        luaContent.Should().Contain("TAO.SendEvent");
        luaContent.Should().Contain("TAO.ExecuteRpc");
        luaContent.Should().Contain("TAO.Sleep");
        luaContent.Should().Contain("TAO.GetWorldState");
        luaContent.Should().Contain("[NEXUS_EVENT]");
    }
}
