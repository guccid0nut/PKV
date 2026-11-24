// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Text.Json;
using FluentAssertions;
using Kongor.Cli.Mcp.Protocol;
using Kongor.Cli.Mcp.Services;
using Xunit;

namespace Kongor.Mcp.Tests;

public sealed class Wave2TaoSdkAndAnalyticsTests
{
    [Fact]
    public async Task WorkerRoeGuard_BlocksLiveClientToolsForOfflineWorker_AndAllowsStaticVfsTools()
    {
        // protects: Subagent role isolation (kongor-offline-worker cannot call singleton live hon_x64.exe tools).
        // fails_when: kongor-offline-worker can invoke kongor_client_eval_lua or is blocked on static kongor_vfs_locate.
        // seam: McpToolRegistry.ExecuteToolAsync -> WorkerRoeAndCircuitBreakerGuard.EvaluatePreCall.
        McpToolRegistry registry = new(registerDefaults: true);
        registry.WorkerRoeGuard.WorkerRoleOverride = "kongor-offline-worker";

        using JsonDocument luaArgs = JsonDocument.Parse("""{"code":"return 1"}""");
        ToolCallResult blocked = await registry.ExecuteToolAsync("kongor_client_eval_lua", luaArgs.RootElement);
        blocked.IsError.Should().BeTrue();
        string blockedText = blocked.Content.OfType<TextContentItem>().First().Text;
        blockedText.Should().Contain("worker_roe_violation");
        blockedText.Should().Contain("kongor-game-worker");

        using JsonDocument vfsArgs = JsonDocument.Parse("""{"path":"ui/fe2/main.interface"}""");
        ToolCallResult allowed = await registry.ExecuteToolAsync("kongor_vfs_locate", vfsArgs.RootElement);
        string allowedText = allowed.Content.OfType<TextContentItem>().First().Text;
        allowedText.Should().NotContain("worker_roe_violation");
    }

    [Fact]
    public async Task ThreeStrikeCircuitBreaker_TripsAfterThreeConsecutiveErrors_AndResetsOnForceRetry()
    {
        // protects: Anti-blind-retry 3-strike circuit breaker in McpToolRegistry.
        // fails_when: A repeatedly failing tool never trips after 3 consecutive errors, or force_retry=true fails to reset it.
        // seam: McpToolRegistry.ExecuteToolAsync -> WorkerRoeAndCircuitBreakerGuard 3-strike counter.
        McpToolRegistry registry = new(registerDefaults: false);
        registry.WorkerRoeGuard.EnableThreeStrikeBreaker = true;
        registry.WorkerRoeGuard.StrikeThreshold = 3;

        int invocationCount = 0;
        registry.RegisterTool(
            new ToolDefinition
            {
                Name = "test_flaky_tool",
                Description = "Synthetic failing tool for circuit breaker verification",
                InputSchema = new ToolInputSchema()
            },
            (_, _) =>
            {
                invocationCount++;
                return Task.FromResult(invocationCount <= 3
                    ? ToolCallResult.Error($"simulated failure #{invocationCount}")
                    : ToolCallResult.Success("""{"status":"recovered"}"""));
            });

        using JsonDocument emptyArgs = JsonDocument.Parse("{}");
        for (int i = 0; i < 3; i++)
        {
            ToolCallResult r = await registry.ExecuteToolAsync("test_flaky_tool", emptyArgs.RootElement);
            r.IsError.Should().BeTrue();
        }
        invocationCount.Should().Be(3);

        // 4th call MUST be blocked by the 3-strike circuit breaker without invoking handler
        ToolCallResult tripped = await registry.ExecuteToolAsync("test_flaky_tool", emptyArgs.RootElement);
        tripped.IsError.Should().BeTrue();
        tripped.Content.OfType<TextContentItem>().First().Text.Should().Contain("three_strike_circuit_breaker");
        invocationCount.Should().Be(3);

        // force_retry=true resets the breaker and allows the recovered 4th execution
        using JsonDocument retryArgs = JsonDocument.Parse("""{"force_retry":true}""");
        ToolCallResult recovered = await registry.ExecuteToolAsync("test_flaky_tool", retryArgs.RootElement);
        recovered.IsError.Should().BeFalse();
        invocationCount.Should().Be(4);
        registry.WorkerRoeGuard.GetStrikeCount("test_flaky_tool").Should().Be(0);
    }

    [Fact]
    public async Task McpOutcomeAnalyticsService_IngestsCSharpAndPythonOutcomesIntoParquetAndQueriesViaDuckDb()
    {
        // protects: Unified C# + Python MCP outcome ingestion into Apache Parquet (Parquet.Net) and SQL analytics (DuckDB.NET).
        // fails_when: Parquet write fails, origin=test leaks into production stats, or DuckDB fails to aggregate hotspots.
        // seam: McpOutcomeAnalyticsService.IngestAndAnalyzeAsync over real JSONL/NDJSON -> real Parquet file -> DuckDB read_parquet.
        string tempDir = Path.Combine(Path.GetTempPath(), $"kongor_mcp_outcomes_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            string csharpLog = Path.Combine(tempDir, "mcp_tool_outcomes.jsonl");
            string pythonLog = Path.Combine(tempDir, "mcp_python_outcomes.ndjson");
            string parquetOut = Path.Combine(tempDir, "analytics", "mcp_outcomes.parquet");

            await File.WriteAllLinesAsync(csharpLog,
            [
                """{"timestamp":"2026-11-19T12:00:00Z","server":"kongor","targetTool":"kongor_client_eval_lua","origin":"production","isError":false,"durationMs":15.2}""",
                """{"timestamp":"2026-11-19T12:00:01Z","server":"kongor","targetTool":"kongor_client_eval_lua","origin":"production","isError":true,"durationMs":42.5,"errorMessage":"syntax error"}""",
                """{"timestamp":"2026-11-19T12:00:02Z","server":"kongor","targetTool":"kongor_wait_for","origin":"test","isError":true,"durationMs":999.0,"errorMessage":"synthetic test error"}"""
            ]);

            await File.WriteAllLinesAsync(pythonLog,
            [
                """{"timestamp":"2026-11-19T12:00:03Z","server":"voicebox","tool":"voicebox_get_status","origin":"production","isError":false,"durationMs":8.4}""",
                """{"timestamp":"2026-11-19T12:00:04Z","server":"blender","tool":"blender_pair_weapon","origin":"production","isError":true,"durationMs":110.0,"matchedIssueId":"MCP-210","errorMessage":"socket offline"}"""
            ]);

            McpOutcomeIngestionSummary summary = await McpOutcomeAnalyticsService.IngestAndAnalyzeAsync(
                tempDir,
                parquetOut,
                includeTestOrigin: false);

            summary.Success.Should().BeTrue();
            File.Exists(parquetOut).Should().BeTrue();
            summary.IngestedRows.Should().Be(4);
            summary.SkippedTestRows.Should().Be(1);
            summary.TotalErrors.Should().Be(2);
            summary.FirstCallSuccessRatePercent.Should().Be(50.0);
            summary.TopHotspots.Should().Contain(h => h.Server == "kongor" && h.Tool == "kongor_client_eval_lua" && h.ErrorCalls == 1 && h.UncodifiedErrors == 1);
            summary.TopHotspots.Should().Contain(h => h.Server == "blender" && h.Tool == "blender_pair_weapon" && h.ErrorCalls == 1 && h.UncodifiedErrors == 0);
            summary.TopHotspots.Should().Contain(h => h.Server == "voicebox" && h.Tool == "voicebox_get_status" && h.ErrorCalls == 0);
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }
}
