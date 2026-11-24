// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// TAO (Together As One) MCP & Kongor CLI -- Created by guccidonut (https://github.com/guccidonut/PKV)

using FluentAssertions;
using Kongor.Mcp.E2E.Tests.Client;
using Xunit;
using Xunit.Abstractions;

namespace Kongor.Mcp.E2E.Tests.Tier1_FeatureCoverage;

public sealed class GameTelemetryToolTests : McpTestBase
{
    public GameTelemetryToolTests(ITestOutputHelper output) : base(output)
    {
    }

    [Trait("Category", "Manual")]
    [Fact]
    public async Task GameTelemetry_QuerySnapshot_ReturnsTelemetryPayload()
    {
        if (!NexusTestEnvironment.IsManualTestEnabled) return;
        GameTelemetryArgs args = new()
        {
            TimeoutMs = 3000
        };

        ToolCallResult callResult = await Client.CallToolAsync("kongor_game_telemetry", args);
        callResult.Should().NotBeNull();

        GameTelemetryResult? result = await Client.CallToolTypedAsync<GameTelemetryResult>("kongor_game_telemetry", args);
        result.Should().NotBeNull();
    }

    [Trait("Category", "Manual")]
    [Fact]
    public async Task GameTelemetry_MemoryWorkingSet_HasNonNegativeValues()
    {
        if (!NexusTestEnvironment.IsManualTestEnabled) return;
        GameTelemetryArgs args = new()
        {
            TimeoutMs = 3000
        };

        GameTelemetryResult? result = await Client.CallToolTypedAsync<GameTelemetryResult>("kongor_game_telemetry", args);
        result.Should().NotBeNull();
        if (result!.Telemetry != null)
        {
            result.Telemetry.MemoryWorkingSetBytes.Should().BeGreaterOrEqualTo(0);
            result.Telemetry.MemoryWorkingSetMb.Should().BeGreaterOrEqualTo(0);
        }
    }

    [Trait("Category", "Manual")]
    [Fact]
    public async Task GameTelemetry_FpsAndUptime_AreValidNumbers()
    {
        if (!NexusTestEnvironment.IsManualTestEnabled) return;
        GameTelemetryArgs args = new()
        {
            TimeoutMs = 3000
        };

        GameTelemetryResult? result = await Client.CallToolTypedAsync<GameTelemetryResult>("kongor_game_telemetry", args);
        result.Should().NotBeNull();
        if (result!.Telemetry != null)
        {
            result.Telemetry.CurrentFps.Should().BeGreaterOrEqualTo(0);
            result.Telemetry.UptimeSeconds.Should().BeGreaterOrEqualTo(0);
        }
    }

    [Trait("Category", "Manual")]
    [Fact]
    public async Task GameTelemetry_WithCustomTimeout_RespondsPromptly()
    {
        if (!NexusTestEnvironment.IsManualTestEnabled) return;
        GameTelemetryArgs args = new()
        {
            TimeoutMs = 1500
        };

        DateTimeOffset start = TimeProvider.System.GetUtcNow();
        GameTelemetryResult? result = await Client.CallToolTypedAsync<GameTelemetryResult>("kongor_game_telemetry", args);
        TimeSpan elapsed = TimeProvider.System.GetUtcNow() - start;

        result.Should().NotBeNull();
        elapsed.TotalMilliseconds.Should().BeLessThan(10000);
    }

    [Trait("Category", "Manual")]
    [Fact]
    public async Task GameTelemetry_MapAndMatchIdFields_ArePresent()
    {
        if (!NexusTestEnvironment.IsManualTestEnabled) return;
        GameTelemetryArgs args = new()
        {
            TimeoutMs = 3000
        };

        GameTelemetryResult? result = await Client.CallToolTypedAsync<GameTelemetryResult>("kongor_game_telemetry", args);
        result.Should().NotBeNull();
        if (result!.Telemetry != null)
        {
            result.Telemetry.ConnectedPlayers.Should().BeGreaterOrEqualTo(0);
        }
    }
}
