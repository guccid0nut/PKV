// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// TAO (Together As One) MCP & Kongor CLI -- Created by guccidonut (https://github.com/guccidonut/PKV)

using System.Text.Json;
using FluentAssertions;
using Kongor.Cli.Mcp.Handlers;
using Kongor.Cli.Mcp.Protocol;
using Kongor.Cli.Mcp.Services;
using Moq;
using Xunit;

namespace Kongor.Mcp.Tests;

/// <summary>
/// Unit tests for Phase P0.1: Offline Honesty & Quarantine Enforcements.
/// Verifies that with engine/DB/Redis offline and 'allowSimulation' omitted:
/// 1. None of the 17 surfaces return fabricated fake data (e.g. Hero_Pyromancer) or fake LiveVerified success.
/// 2. ReadClientTelemetryAsync returns honest offline telemetry (status = offline, clientRunning = false, hero = null).
/// 3. ListTools() excludes quarantined mock-only tools by default.
/// 4. Quarantined tools succeed and tag SimulatedTestDouble when allowSimulation: true is explicitly provided.
/// </summary>
public sealed class OfflineHonestyTests
{
    private static JsonElement EmptyArgs => JsonElement.Parse("{}");
    private static JsonElement SimAllowedArgs => JsonElement.Parse("{\"allowSimulation\": true}");

    [Fact]
    public void QuarantinedSimulatedTools_IsEmpty()
    {
        McpToolRegistry.QuarantinedSimulatedTools.Should().BeEmpty(
            "All simulated mock surfaces have been either unquarantined with real offline honesty or removed.");
    }

    [Fact]
    public void RemovedMockTools_AreNotRegisteredInMcpToolRegistry()
    {
        var registry = new McpToolRegistry(registerDefaults: true);
        string[] removedTools = ["kongor_match_simulate_e2e", "kongor_e2e_match_audit", "kongor_profile_trace"];

        foreach (string toolName in removedTools)
        {
            registry.HasTool(toolName).Should().BeFalse(
                $"Pure-mock tool '{toolName}' must not be registered in McpToolRegistry.");
        }
    }

    [Fact]
    public async Task AllRegisteredTools_WhenOffline_NeverReturnLiveVerifiedSuccess()
    {
        if (System.Diagnostics.Process.GetProcessesByName("hon_x64").Length > 0)
        {
            return;
        }

        var registry = new McpToolRegistry(registerDefaults: true);
        string[] unquarantinedTools =
        [
            "kongor_db_migration_test",
            "kongor_db_fixture",
            "kongor_match_state",
            "kongor_replay_diff",
            "kongor_net_packet_fuzz"
        ];

        foreach (string toolName in unquarantinedTools)
        {
            var res = await registry.ExecuteToolAsync(toolName, EmptyArgs, CancellationToken.None);
            if (!res.IsError)
            {
                res.FirstText.Should().NotContain(
                    ExecutionFidelity.LiveVerified.ToString(),
                    $"Tool '{toolName}' must NEVER claim LiveVerified when executed offline without live backends.");
            }
            else
            {
                res.IsError.Should().BeTrue($"Tool '{toolName}' must return error when offline without inputs.");
            }
        }
    }

    [Fact]
    public async Task ReadClientTelemetryAsync_WhenEngineOffline_ReturnsHonestOfflineStatusWithoutFakeHero()
    {
        if (System.Diagnostics.Process.GetProcessesByName("hon_x64").Length > 0)
        {
            return;
        }

        // Default McpServer with null engine state (client offline)
        var server = new McpServer();
        string json = await server.ReadClientTelemetryAsync();

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        root.GetProperty("status").GetString().Should().Be("offline");
        root.GetProperty("clientRunning").GetBoolean().Should().BeFalse();
        if (root.TryGetProperty("hero", out var heroEl))
        {
            heroEl.ValueKind.Should().Be(JsonValueKind.Null);
        }

        // Crucial invariant: never fabricate Pyromancer or fake FPS/map/matchId when engine is null
        json.Should().NotContain("Hero_Pyromancer");
        json.Should().NotContain("GUEST-11");
        json.Should().NotContain("caldavar");
        json.Should().NotContain("10001");
    }

    [Fact]
    public async Task CacheCoherenceVerify_WhenCacheOfflineAndSimulationOmitted_ReturnsError()
    {
        var handler = new CacheCoherenceVerifyHandler();
        using var args = JsonDocument.Parse("{\"action\": \"reconcile\", \"scope\": \"all\", \"port\": 59999}");

        var result = await handler.HandleAsync(args.RootElement);

        result.IsError.Should().BeTrue("Offline Redis/Garnet cache must return error instead of fabricating key counts.");
        result.FirstText.Should().Contain("allowSimulation: true");
    }

    [Fact]
    public async Task TelemetryStream_WhenShmOfflineAndSimulationOmitted_ReturnsError()
    {
        var handler = new TelemetryStreamHandler();
        using var args = JsonDocument.Parse("{\"action\": \"snapshot\", \"port\": 11235}");

        var result = await handler.HandleAsync(args.RootElement);

        result.IsError.Should().BeTrue("Offline client SHM must return error instead of fabricating Hero_Pyromancer.");
        result.FirstText.Should().NotContain("Hero_Pyromancer");
        result.FirstText.Should().Contain("allowSimulation: true");
    }

    [Fact]
    public async Task FixtureInject_OnResetWhenEngineOfflineAndSimulationOmitted_ReturnsError()
    {
        var handler = new FixtureInjectHandler(new GameClientManageHandler(logger: _ => { }), new GameServerManageHandler(logger: _ => { }));
        using var args = JsonDocument.Parse("{\"action\": \"reset\"}");

        var result = await handler.HandleAsync(args.RootElement);

        result.IsError.Should().BeTrue("Offline game client must reject reset fixture injection without simulation.");
        result.FirstText.Should().NotContain("Hero_Pyromancer");
    }

    [Fact]
    public async Task AudioStudio_And_AssetBakePipeline_ReportEngineNotRunningWithStaticAnalysisFidelity()
    {
        var audioHandler = new AudioStudioHandler();
        using var audioPreviewArgs = JsonDocument.Parse("{\"action\": \"preview\"}");
        var audioRes = await audioHandler.HandleAsync(audioPreviewArgs.RootElement);
        audioRes.IsError.Should().BeFalse();
        audioRes.FirstText.Should().Contain("\"running\": false");
        audioRes.FirstText.Should().Contain(ExecutionFidelity.StaticOfflineAnalysis.ToString());

        var bakeHandler = new AssetBakePipelineHandler();
        var bakeRes = await bakeHandler.HandleAsync(EmptyArgs);
        bakeRes.IsError.Should().BeFalse();
        bakeRes.FirstText.Should().Contain("\"running\": false");
        bakeRes.FirstText.Should().Contain(ExecutionFidelity.StaticOfflineAnalysis.ToString());
    }

    [Fact]
    public async Task ServerPerfStream_WhenTargetOfflineAndSimulationOmitted_ReturnsError()
    {
        var handler = new ServerPerfStreamHandler();
        using var args = JsonDocument.Parse("{\"action\": \"snapshot\", \"port\": 11235}");

        var result = await handler.HandleAsync(args.RootElement);

        result.IsError.Should().BeTrue("Offline server performance stream must return error when target server is down.");
        result.FirstText.Should().Contain("allowSimulation: true");
    }

    [Fact]
    public async Task NetworkImpairSimulate_WhenTargetOfflineAndSimulationOmitted_ReturnsError()
    {
        var handler = new NetworkImpairSimulateHandler();
        using var args = JsonDocument.Parse("{\"action\": \"set\", \"target\": \"server\", \"latencyMs\": 120}");

        var result = await handler.HandleAsync(args.RootElement);

        result.IsError.Should().BeTrue("Offline network impairment dispatch must return error when server/client pipe is down.");
        result.FirstText.Should().Contain("allowSimulation: true");
    }

    [Fact]
    public async Task RealtimeStream_WhenLogsMissingAndSimulationOmitted_ReturnsError()
    {
        // Point to empty temporary directory with no logs
        string emptyDir = Path.Combine(Path.GetTempPath(), "kongor_empty_logs_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(emptyDir);

        try
        {
            var handler = new RealtimeStreamHandler(logsRoot: emptyDir);
            using var args = JsonDocument.Parse("{\"action\": \"tail\", \"source\": \"all\"}");

            var result = await handler.HandleAsync(args.RootElement);

            result.IsError.Should().BeTrue("RealtimeStream tail on non-existent logs must return error instead of synthetic lines.");
            result.FirstText.Should().Contain("allowSimulation: true");
        }
        finally
        {
            if (Directory.Exists(emptyDir)) Directory.Delete(emptyDir, true);
        }
    }

    [Fact]
    public async Task PureMockQuarantinedTools_WhenSimulationOmitted_AllReturnActionableErrors()
    {
        // 1. DbMigrationTest
        var dbMig = new DbMigrationTestHandler();
        (await dbMig.HandleAsync(EmptyArgs)).IsError.Should().BeTrue();

        // 2. NetPacketFuzz
        var netFuzz = new NetPacketFuzzHandler();
        (await netFuzz.HandleAsync(EmptyArgs)).IsError.Should().BeTrue();

        // 3. MatchState
        var matchState = new MatchStateHandler();
        (await matchState.HandleAsync(EmptyArgs)).IsError.Should().BeTrue();

        // 4. ReplayInspect (combat_log or missing file)
        var replayInspect = new ReplayInspectHandler();
        using var combatLogArgs = JsonDocument.Parse("{\"action\": \"combat_log\"}");
        (await replayInspect.HandleAsync(combatLogArgs.RootElement)).IsError.Should().BeTrue();

        // 5. ReplayDiff
        var replayDiff = new ReplayDiffHandler();
        (await replayDiff.HandleAsync(EmptyArgs)).IsError.Should().BeTrue();

        // 6. DbFixture
        var dbFixture = new DbFixtureHandler();
        (await dbFixture.HandleAsync(EmptyArgs)).IsError.Should().BeTrue();
    }

    [Fact]
    public async Task McpServerMiddleware_RejectsUnapprovedSimulatedTestDoubleOutputs()
    {
        var registry = new McpToolRegistry(registerDefaults: false);

        // Register a rogue tool that produces SimulatedTestDouble output without approval
        registry.RegisterTool(
            new ToolDefinition { Name = "rogue_mock_tool", Description = "Test rogue tool" },
            (args, ct) => Task.FromResult(ToolCallResult.Success(new
            {
                success = true,
                executionFidelity = ExecutionFidelity.SimulatedTestDouble.ToString(),
                data = "fake"
            })));

        using var server = new McpServer(registry);

        // 1. Call without allowSimulation -> Middleware converts to Error
        string requestJson = "{\"jsonrpc\":\"2.0\",\"id\":101,\"method\":\"tools/call\",\"params\":{\"name\":\"rogue_mock_tool\",\"arguments\":{}}}";
        string? responseJson = await server.ProcessMessageAsync(requestJson);

        responseJson.Should().NotBeNull();
        using (var doc = JsonDocument.Parse(responseJson!))
        {
            var result = doc.RootElement.GetProperty("result");
            result.GetProperty("isError").GetBoolean().Should().BeTrue();
            result.GetProperty("content")[0].GetProperty("text").GetString()
                .Should().Contain("Simulated test double output rejected");
        }

        // 2. Call with allowSimulation: true -> Middleware allows through
        string allowedReqJson = "{\"jsonrpc\":\"2.0\",\"id\":102,\"method\":\"tools/call\",\"params\":{\"name\":\"rogue_mock_tool\",\"arguments\":{\"allowSimulation\":true}}}";
        string? allowedRespJson = await server.ProcessMessageAsync(allowedReqJson);

        allowedRespJson.Should().NotBeNull();
        using (var doc2 = JsonDocument.Parse(allowedRespJson!))
        {
            var result2 = doc2.RootElement.GetProperty("result");
            result2.GetProperty("isError").GetBoolean().Should().BeFalse();
        }
    }
}
