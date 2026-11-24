// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// TAO (Together As One) MCP & Kongor CLI -- Created by guccidonut (https://github.com/guccidonut/PKV)

using System.Diagnostics;
using System.Text.Json;
using Kongor.Cli.Mcp.Handlers;
using Kongor.Cli.Mcp.Protocol;
using Kongor.Cli.Mcp.Services;
using Xunit;

namespace Kongor.Mcp.Tests;

public sealed class EngineStateAndDiagnosticsStatusTests
{
    private readonly TimeProvider _timeProvider = TimeProvider.System;

    [Fact]
    public void VfsDiagnostics_ReturnsCanonicalSearchOrder_WithOverrideFirst()
    {
        var result = VfsDiagnostics.GetVfsStatus();

        Assert.NotNull(result);
        Assert.True(result.OverrideMountedFirst);
        Assert.NotEmpty(result.SearchOrder);
        Assert.Equal("override", result.SearchOrder[0]);
        Assert.Contains("base", result.SearchOrder);
        Assert.Contains("game", result.SearchOrder);
        Assert.Contains("../shared", result.SearchOrder);
        Assert.True(result.OverrideFilesCount >= 0);
        Assert.True(result.ShadowedFilesCount >= 0);
    }

    [Theory]
    [InlineData(0, "Login (0-1)")]
    [InlineData(1, "Login (0-1)")]
    [InlineData(2, "Lobby/Chat (2-4)")]
    [InlineData(3, "Lobby/Chat (2-4)")]
    [InlineData(4, "Lobby/Chat (2-4)")]
    [InlineData(5, "HeroSelect (5)")]
    [InlineData(6, "LoadingMap (6)")]
    [InlineData(7, "InGame (7)")]
    public void EngineTelemetryDiagnostics_FormatPhaseName_MapsPhasesCorrectly(int phase, string expected)
    {
        string name = EngineTelemetryDiagnostics.FormatPhaseName(phase);
        Assert.Equal(expected, name);
    }

    [Fact]
    public void EngineTelemetryDiagnostics_CheckIpcConnectivity_ReturnsFalse_WhenPipeMissing()
    {
        bool connected = EngineTelemetryDiagnostics.CheckIpcConnectivity("non-existent-pipe-for-unit-test-12345", 50);
        Assert.False(connected);
    }

    [Fact]
    public void EngineTelemetryDiagnostics_GetGraphicsStatus_ReturnsValidStructure()
    {
        var graphics = EngineTelemetryDiagnostics.GetGraphicsStatus();
        Assert.NotNull(graphics);
        Assert.False(string.IsNullOrWhiteSpace(graphics.Backend));
        Assert.False(string.IsNullOrWhiteSpace(graphics.Driver));
        Assert.False(string.IsNullOrWhiteSpace(graphics.Adapter));
        Assert.False(string.IsNullOrWhiteSpace(graphics.TargetRefreshRate));
        Assert.True(graphics.MaxFps > 0);
    }

    [Fact]
    public void EngineTelemetryDiagnostics_GetAudioStatus_ReturnsValidStructure()
    {
        var audio = EngineTelemetryDiagnostics.GetAudioStatus();
        Assert.NotNull(audio);
        Assert.False(string.IsNullOrWhiteSpace(audio.DriverName));
        Assert.False(string.IsNullOrWhiteSpace(audio.Api));
        Assert.Equal(48000, audio.SampleRate);
        Assert.Equal(64, audio.Channels);
    }

    [Fact]
    public void EngineTelemetryDiagnostics_GetClientNextAction_RecommendsLaunch_WhenNotRunning()
    {
        string action = EngineTelemetryDiagnostics.GetClientNextAction(false, false, 0, 0);
        Assert.Contains("kongor_game_client_manage action:launch", action);
    }

    [Fact]
    public void EngineTelemetryDiagnostics_GetClientNextAction_RecommendsIpcCheck_WhenRunningWithoutIpc()
    {
        string action = EngineTelemetryDiagnostics.GetClientNextAction(true, false, 1, 0);
        Assert.Contains("willowmaker-ipc", action);
    }

    [Fact]
    public void EngineTelemetryDiagnostics_GetClientNextAction_RecommendsMasterServer_WhenStuckOnLogin()
    {
        string action = EngineTelemetryDiagnostics.GetClientNextAction(true, true, 1, 0);
        Assert.Contains("MasterServer", action);
    }

    [Fact]
    public void EngineTelemetryDiagnostics_GetClientNextAction_RecommendsCachePurge_WhenShadowedFilesPresent()
    {
        string action = EngineTelemetryDiagnostics.GetClientNextAction(true, true, 7, 5);
        Assert.Contains("kongor_engine_cache_purge", action);
    }

    [Fact]
    public async Task GameClientManageHandler_Status_ReturnsFullRichTelemetry()
    {
        var handler = new GameClientManageHandler(_timeProvider);
        using var doc = JsonDocument.Parse("""{"action":"status"}""");

        ToolCallResult result = await handler.HandleAsync(doc.RootElement);

        Assert.False(result.IsError);
        using var resDoc = JsonDocument.Parse(((TextContentItem)result.Content[0]).Text);
        JsonElement root = resDoc.RootElement;

        Assert.Equal("success", root.GetProperty("status").GetString());
        Assert.Equal("status", root.GetProperty("action").GetString());
        bool isRunning = root.GetProperty("running").GetBoolean();
        if (!isRunning)
        {
            Assert.False(root.GetProperty("ipcConnected").GetBoolean());
            Assert.Equal("Offline", root.GetProperty("gamePhaseName").GetString());
        }
        else
        {
            Assert.NotNull(root.GetProperty("gamePhaseName").GetString());
        }

        // Graphics check
        Assert.True(root.TryGetProperty("graphics", out JsonElement gElem));
        Assert.False(string.IsNullOrWhiteSpace(gElem.GetProperty("backend").GetString()));
        Assert.False(string.IsNullOrWhiteSpace(gElem.GetProperty("driver").GetString()));

        // Audio check
        Assert.True(root.TryGetProperty("audio", out JsonElement aElem));
        Assert.Equal(48000, aElem.GetProperty("sampleRate").GetInt32());
        Assert.Equal(64, aElem.GetProperty("channels").GetInt32());

        // VFS check
        Assert.True(root.TryGetProperty("vfs", out JsonElement vElem));
        Assert.True(vElem.GetProperty("overrideMountedFirst").GetBoolean());
        Assert.True(vElem.GetProperty("searchOrder").GetArrayLength() > 0);

        // NextAction check
        Assert.True(root.TryGetProperty("nextAction", out JsonElement naElem));
        Assert.Contains("launch", naElem.GetString());
    }

    [Fact]
    public void ClusterDiagnosticsService_ComputeRemediationHints_FlagsRedisDown()
    {
        var redis = new RedisProbeResult { Connected = false };
        var db = new DatabaseProbeResult { Connected = true };
        var master = new MasterServerHealthResult { Connected = true };
        var compel = new CompelPoolStateResult { Connected = true };

        var (nextAction, hints) = ClusterDiagnosticsService.ComputeRemediationHints(
            redis, db, master, compel, [], []);

        Assert.Contains("target:redis", nextAction);
        Assert.NotEmpty(hints);
    }

    [Fact]
    public void ClusterDiagnosticsService_ComputeRemediationHints_FlagsRedisEviction()
    {
        var redis = new RedisProbeResult
        {
            Connected = true,
            EvictedKeys = 15,
            EvictionWarning = true
        };
        var db = new DatabaseProbeResult { Connected = true };
        var master = new MasterServerHealthResult { Connected = true };
        var compel = new CompelPoolStateResult { Connected = true };

        var (nextAction, hints) = ClusterDiagnosticsService.ComputeRemediationHints(
            redis, db, master, compel, [], []);

        Assert.Contains("evicted keys", nextAction);
        Assert.NotEmpty(hints);
    }

    [Fact]
    public void ClusterDiagnosticsService_ComputeRemediationHints_FlagsMasterServerDown()
    {
        var redis = new RedisProbeResult { Connected = true };
        var db = new DatabaseProbeResult { Connected = true };
        var master = new MasterServerHealthResult { Connected = false };
        var compel = new CompelPoolStateResult { Connected = true };

        var (nextAction, hints) = ClusterDiagnosticsService.ComputeRemediationHints(
            redis, db, master, compel, [], []);

        Assert.Contains("target:master-server", nextAction);
        Assert.NotEmpty(hints);
    }

    [Fact]
    public async Task ServerManageHandler_Status_ReturnsNextActionHint()
    {
        var lifecycle = new LifecycleService(null);
        var handler = new ServerManageHandler(lifecycle, _timeProvider);
        using var doc = JsonDocument.Parse("""{"action":"status","target":"cluster"}""");

        ToolCallResult result = await handler.HandleAsync(doc.RootElement);

        Assert.False(result.IsError);
        using var resDoc = JsonDocument.Parse(((TextContentItem)result.Content[0]).Text);
        JsonElement root = resDoc.RootElement;

        Assert.True(root.TryGetProperty("nextAction", out JsonElement naElem));
        Assert.False(string.IsNullOrWhiteSpace(naElem.GetString()));
    }
}
