// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// TAO (Together As One) MCP & Kongor CLI -- Created by guccidonut (https://github.com/guccidonut/PKV)

using System.IO;
using System.Text.Json;
using FluentAssertions;
using Kongor.Cli.Mcp.Protocol;
using Xunit;

namespace Kongor.Mcp.Tests;

public sealed class McpRateLimitingTests
{
    [Fact]
    public void McpRateLimiter_AllowsPermits_UpToCategoryLimit()
    {
        using StringWriter stderr = new();
        McpRateLimiter limiter = new(stderr);

        // Simulation category has budget = 2 permits per minute
        var req1 = limiter.TryAcquire("kongor_scrimmage_launch", McpToolCategory.Simulation, "agent-1");
        var req2 = limiter.TryAcquire("kongor_scrimmage_launch", McpToolCategory.Simulation, "agent-1");
        var req3 = limiter.TryAcquire("kongor_scrimmage_launch", McpToolCategory.Simulation, "agent-1");

        req1.Permitted.Should().BeTrue();
        req1.RetryAfter.Should().BeNull();

        req2.Permitted.Should().BeTrue();
        req2.RetryAfter.Should().BeNull();

        req3.Permitted.Should().BeFalse();
        req3.RetryAfter.Should().NotBeNull();
        req3.RetryAfter!.Value.Should().BeGreaterThan(TimeSpan.Zero);

        stderr.ToString().Should().Contain("Permit granted for tool 'kongor_scrimmage_launch'");
        stderr.ToString().Should().Contain("THROTTLED: Tool 'kongor_scrimmage_launch'");
    }

    [Fact]
    public void McpRateLimiter_IsolatesAgents()
    {
        using StringWriter stderr = new();
        McpRateLimiter limiter = new(stderr);

        // Agent A exhausts budget (2 permits)
        limiter.TryAcquire("kongor_scrimmage_launch", McpToolCategory.Simulation, "agent-alpha").Permitted.Should().BeTrue();
        limiter.TryAcquire("kongor_scrimmage_launch", McpToolCategory.Simulation, "agent-alpha").Permitted.Should().BeTrue();
        limiter.TryAcquire("kongor_scrimmage_launch", McpToolCategory.Simulation, "agent-alpha").Permitted.Should().BeFalse();

        // Agent B has independent bucket and should succeed
        var agentBResult = limiter.TryAcquire("kongor_scrimmage_launch", McpToolCategory.Simulation, "agent-beta");
        agentBResult.Permitted.Should().BeTrue();
        agentBResult.RetryAfter.Should().BeNull();
    }

    [Fact]
    public void McpRateLimiter_GetCategoryStatus_ReturnsStatusForAllCategories()
    {
        using StringWriter stderr = new();
        McpRateLimiter limiter = new(stderr);

        limiter.TryAcquire("test_read", McpToolCategory.ReadOnly, "agent-inspector");

        var status = limiter.GetCategoryStatus("agent-inspector");
        status.Should().NotBeNull();

        using JsonDocument doc = JsonDocument.Parse(JsonSerializer.Serialize(status));
        var root = doc.RootElement;
        root.GetProperty("enabled").GetBoolean().Should().BeTrue();
        root.GetProperty("agentId").GetString().Should().Be("agent-inspector");

        var categories = root.GetProperty("categories");
        categories.TryGetProperty("ReadOnly", out var readOnlyProp).Should().BeTrue();
        readOnlyProp.GetProperty("Limit").GetInt32().Should().Be(60);

        categories.TryGetProperty("Simulation", out var simProp).Should().BeTrue();
        simProp.GetProperty("Limit").GetInt32().Should().Be(2);
    }

    [Fact]
    public void McpRateLimiterNoOp_AlwaysPermits()
    {
        McpRateLimiterNoOp noOp = new();
        noOp.IsEnabled.Should().BeFalse();

        for (int i = 0; i < 50; i++)
        {
            var result = noOp.TryAcquire("kongor_heavy_tool", McpToolCategory.HeavyProfiling, "agent-load");
            result.Permitted.Should().BeTrue();
            result.RetryAfter.Should().BeNull();
        }
    }

    [Fact]
    public async Task McpToolRegistry_WithRateLimiter_BlocksExcessiveCalls()
    {
        using StringWriter stderr = new();
        McpRateLimiter limiter = new(stderr);

        McpToolRegistry registry = new(registerDefaults: false, rateLimiter: limiter);

        // Register a tool with category Simulation (budget = 2)
        registry.RegisterTool(
            new ToolDefinition { Name = "test_simulation_tool", Description = "Test" },
            (args, ct) => Task.FromResult(ToolCallResult.Text("Execution success")),
            McpToolCategory.Simulation);

        using JsonDocument doc = JsonDocument.Parse("{}");

        var res1 = await registry.ExecuteToolAsync("test_simulation_tool", doc.RootElement);
        var res2 = await registry.ExecuteToolAsync("test_simulation_tool", doc.RootElement);
        var res3 = await registry.ExecuteToolAsync("test_simulation_tool", doc.RootElement);

        res1.IsError.Should().BeFalse();
        res2.IsError.Should().BeFalse();
        res3.IsError.Should().BeTrue();
        res3.FirstText.Should().Contain("Rate limit exceeded for 'Simulation' category");
        res3.FirstText.Should().Contain("Try again in");
    }

    [Fact]
    public async Task McpToolRegistry_KongorRateLimitStatus_ReturnsValidJsonReport()
    {
        using StringWriter stderr = new();
        McpRateLimiter limiter = new(stderr);

        McpToolRegistry registry = new(registerDefaults: true, rateLimiter: limiter);

        using JsonDocument doc = JsonDocument.Parse("{}");
        var result = await registry.ExecuteToolAsync("kongor_rate_limit_status", doc.RootElement);

        result.IsError.Should().BeFalse();
        result.FirstText.Should().NotBeNullOrWhiteSpace();

        using JsonDocument statusDoc = JsonDocument.Parse(result.FirstText!);
        var root = statusDoc.RootElement;
        root.GetProperty("categories").TryGetProperty("ReadOnly", out var readOnlyProp).Should().BeTrue();
        readOnlyProp.GetProperty("limit").GetInt32().Should().Be(60);
    }
}
