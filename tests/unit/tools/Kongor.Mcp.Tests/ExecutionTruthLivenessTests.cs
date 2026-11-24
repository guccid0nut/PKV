// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// TAO (Together As One) MCP & Kongor CLI -- Created by guccidonut (https://github.com/guccidonut/PKV)

#pragma warning disable CA2026
using System.Diagnostics;
using System.Text.Json;
using FluentAssertions;
using Kongor.Cli.Mcp.Handlers;
using Kongor.Cli.Mcp.Protocol;
using Kongor.Cli.Mcp.Services;
using Moq;
using Xunit;

namespace Kongor.Mcp.Tests;

/// <summary>
/// Unit tests covering Phase 4 Modernization (P0: Execution Truth & Zero-Touch JIT Spawning):
/// - Liveness Transparency: Standardized executionMode, targetStatus (processRunning, engineReady), and remediation
/// - Elimination of Phantom Success: Offline targets return target_offline instead of optimistic false success
/// - Single-Flight Concurrency: Coalescing parallel spawn requests onto a single task
/// - Win32 Job Object & Ephemeral Process Tracking: Process assignment and idle reaping
/// - Camera & Choreography In-Engine Verification: Dual-channel execution and execution envelopes
/// </summary>
public sealed class ExecutionTruthLivenessTests
{
    private readonly McpToolRegistry _registry = new(registerDefaults: true);

    private static JsonElement ParseResult(ToolCallResult result)
    {
        result.IsError.Should().BeFalse(result.FirstText);
        result.Content.Should().NotBeEmpty();
        string text = result.Content[0] is TextContentItem textItem ? textItem.Text : result.FirstText;
        using var doc = JsonDocument.Parse(text);
        return doc.RootElement.Clone();
    }

    #region 1. Execution Truth & Liveness Envelope Tests

    [Fact]
    public async Task BotChoreography_ClientTargetOffline_ReturnsTargetOfflineEnvelope()
    {
        var handler = _registry.BotChoreographyHandler;
        using var argsDoc = JsonDocument.Parse("""
        {
            "action": "preset",
            "preset": "pyro_mid_combo",
            "target": "client",
            "autoLaunch": false,
            "forceOfflineCheck": true
        }
        """);

        var result = await handler.HandleAsync(argsDoc.RootElement);
        var root = ParseResult(result);

        root.GetProperty("status").GetString().Should().Be("target_offline");
        root.GetProperty("executionMode").GetString().Should().Be("offline_simulation");
        root.GetProperty("target").GetString().Should().Be("client");

        // Verify authoritative targetStatus block
        var targetStatus = root.GetProperty("targetStatus");
        var client = targetStatus.GetProperty("client");
        client.GetProperty("running").GetBoolean().Should().BeFalse();
        client.GetProperty("engineReady").GetBoolean().Should().BeFalse();

        // Verify actionable remediation block
        var remediation = root.GetProperty("remediation");
        remediation.GetProperty("hint").GetString().Should().Contain("client is offline");
        remediation.GetProperty("suggestedAction").GetString().Should().Be("kongor_game_client_manage");
    }

    [Fact]
    public async Task BotChoreography_ServerTargetOffline_ReturnsTargetOfflineEnvelope()
    {
        var handler = _registry.BotChoreographyHandler;
        using var argsDoc = JsonDocument.Parse("""
        {
            "action": "preset",
            "preset": "kongor_pit_clash",
            "target": "server",
            "port": 11235,
            "autoLaunch": false,
            "forceOfflineCheck": true
        }
        """);

        var result = await handler.HandleAsync(argsDoc.RootElement);
        var root = ParseResult(result);

        root.GetProperty("status").GetString().Should().Be("target_offline");
        root.GetProperty("executionMode").GetString().Should().Be("offline_simulation");

        var targetStatus = root.GetProperty("targetStatus");
        var server = targetStatus.GetProperty("server");
        server.GetProperty("port").GetInt32().Should().Be(11235);

        var remediation = root.GetProperty("remediation");
        remediation.GetProperty("suggestedAction").GetString().Should().Be("kongor_game_server_manage");
    }

    [Fact]
    public async Task CameraControl_ClientOffline_ReturnsTargetOfflineEnvelope()
    {
        var handler = _registry.CameraControlHandler;
        using var argsDoc = JsonDocument.Parse("""
        {
            "action": "set_position",
            "x": 1000.0,
            "y": 2000.0,
            "z": 300.0,
            "autoLaunch": false,
            "forceOfflineCheck": true
        }
        """);

        var result = await handler.HandleAsync(argsDoc.RootElement);
        var root = ParseResult(result);

        root.GetProperty("status").GetString().Should().Be("target_offline");
        root.GetProperty("executionMode").GetString().Should().Be("offline_simulation");
        root.GetProperty("remediation").GetProperty("suggestedAction").GetString().Should().Be("kongor_game_client_manage");
    }

    [Fact]
    public async Task CameraControl_GetPosition_AlwaysReturnsCameraState()
    {
        var handler = _registry.CameraControlHandler;
        using var argsDoc = JsonDocument.Parse("""
        {
            "action": "get_position"
        }
        """);

        var result = await handler.HandleAsync(argsDoc.RootElement);
        var root = ParseResult(result);

        root.GetProperty("status").GetString().Should().Be("success");
        root.GetProperty("action").GetString().Should().Be("get_position");
        root.GetProperty("position").GetProperty("x").GetDouble().Should().Be(0);
        root.GetProperty("fov").GetDouble().Should().Be(55.0);
        root.GetProperty("readout").GetString().Should().Contain("FOV: 55.0");
    }

    [Fact]
    public async Task CameraControl_SetPosition_WhenConnected_DispatchesLiveCvars()
    {
        var mockIpc = new Mock<IGameIpcBridge>();
        mockIpc.SetupGet(m => m.IsConnected).Returns(true);
        mockIpc.Setup(m => m.ExecuteCommandAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("OK");

        var handler = new CameraControlHandler(gameIpcBridge: mockIpc.Object);
        using var argsDoc = JsonDocument.Parse("""
        {
            "action": "set_position",
            "x": 7500.0,
            "y": 7000.0,
            "z": 500.0
        }
        """);

        var result = await handler.HandleAsync(argsDoc.RootElement);
        var root = ParseResult(result);

        root.GetProperty("status").GetString().Should().Be("success");
        root.GetProperty("executionMode").GetString().Should().Be("live_engine");
        root.GetProperty("position").GetProperty("x").GetDouble().Should().Be(7500.0);

        mockIpc.Verify(m => m.ExecuteCommandAsync(It.Is<string>(s => s.Contains("cam_position")), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    #endregion

    #region 2. Single-Flight Spawning Concurrency Tests

    [Fact]
    public async Task SingleFlightSpawner_ParallelRequests_ExecuteFactoryExactlyOnce()
    {
        int executionCounter = 0;
        var startGate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        async Task<string> SlowFactoryAsync()
        {
            Interlocked.Increment(ref executionCounter);
            await startGate.Task;
            return "instance_result_42";
        }

        string key = $"test_worker_key_{Guid.NewGuid():N}";

        // Trigger 10 parallel invocations with the same key
        var tasks = Enumerable.Range(0, 10)
            .Select(_ => SingleFlightSpawner.ExecuteSingleFlightAsync(key, SlowFactoryAsync))
            .ToArray();

        // Release the factory gate only after all callers have queued
        startGate.SetResult(true);

        var results = await Task.WhenAll(tasks);

        // All 10 tasks must receive identical result, but factory executed exactly ONCE
        executionCounter.Should().Be(1);
        results.Should().AllBe("instance_result_42");
    }

    [Fact]
    public async Task SingleFlightSpawner_DifferentKeys_ExecuteIndependently()
    {
        int counter = 0;

        Task<int> FactoryAsync()
        {
            int val = Interlocked.Increment(ref counter);
            return Task.FromResult(val);
        }

        string keyA = $"key_a_{Guid.NewGuid():N}";
        string keyB = $"key_b_{Guid.NewGuid():N}";

        var t1 = SingleFlightSpawner.ExecuteSingleFlightAsync(keyA, FactoryAsync);
        var t2 = SingleFlightSpawner.ExecuteSingleFlightAsync(keyB, FactoryAsync);

        var results = await Task.WhenAll(t1, t2);

        counter.Should().Be(2);
        results[0].Should().NotBe(results[1]);
    }

    #endregion

    #region 3. Job Object & Process Lifecycle Tests

    [Fact]
    public void JobObjectManager_AssignProcess_NullOrExited_ReturnsFalseGracefully()
    {
        JobObjectManager.AssignProcess(null!).Should().BeFalse();

        using Process dummy = new();
        // Unstarted process
        JobObjectManager.AssignProcess(dummy).Should().BeFalse();
    }

    [Fact]
    public void JobObjectManager_RecordActivityAndReap_ProcessesWithoutError()
    {
        // Record activity for current PID
        JobObjectManager.RecordActivity(Environment.ProcessId);

        // Reap with high threshold should not reap active process
        int reaped = JobObjectManager.ReapIdleProcesses(TimeSpan.FromHours(1));
        reaped.Should().Be(0);
    }

    #endregion

    #region 4. Game Command Tool Execution Truth Tests

    [Fact]
    public async Task GameCommand_ClientOffline_ReturnsTargetOfflineEnvelope()
    {
        // When client is not running and IPC is disconnected
        var mockIpc = new Mock<IGameIpcBridge>();
        mockIpc.SetupGet(m => m.IsConnected).Returns(false);

        var handler = new GameCommandHandler(ipcBridge: mockIpc.Object);
        using var argsDoc = JsonDocument.Parse("""
        {
            "command": "Echo Hello",
            "target": "client",
            "autoLaunch": false
        }
        """);

        var result = await handler.HandleAsync(argsDoc.RootElement);
        var root = ParseResult(result);

        root.GetProperty("status").GetString().Should().Be("target_offline");
        root.GetProperty("executionMode").GetString().Should().Be("offline_simulation");
        root.GetProperty("success").GetBoolean().Should().BeFalse();

        var targetStatus = root.GetProperty("targetStatus");
        targetStatus.GetProperty("client").GetProperty("running").GetBoolean().Should().BeFalse();

        var remediation = root.GetProperty("remediation");
        remediation.GetProperty("suggestedAction").GetString().Should().Be("kongor_game_client_manage");
    }

    #endregion
}
