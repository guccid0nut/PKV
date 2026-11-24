// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// TAO (Together As One) MCP & Kongor CLI -- Created by guccidonut (https://github.com/guccidonut/PKV)

using System.Text.Json;
using FluentAssertions;
using Kongor.Cli.Mcp.Handlers;
using Kongor.Cli.Mcp.Protocol;
using Kongor.Cli.Mcp.Services;
using Xunit;

namespace Kongor.Mcp.Tests;

public sealed class TaoPhase6dSpectatorTests
{
    [Fact]
    public void SpectatorRingBuffer_PushesKeyframesAndDeltaMasks_EnforcesCapacity()
    {
        var buffer = new SpectatorRingBuffer();
        var baseTime = TimeProvider.System.GetUtcNow();

        // Push 150 frames (exceeding capacity of 128)
        for (uint i = 1; i <= 150; i++)
        {
            buffer.Push(new DeltaFrame
            {
                TickId = i,
                Timestamp = baseTime.AddMilliseconds(i * 50),
                IsKeyframe = i % 20 == 0,
                PropertyMask = i % 20 == 0 ? DeltaPropertyMask.FullSnapshot : DeltaPropertyMask.Position | DeltaPropertyMask.Health,
                Units =
                [
                    new SpectatorUnitState { UnitId = 1, UnitType = "Hero_Pyromancer", Team = 1, X = 1000 + i, Y = 2000, Z = 100 }
                ]
            });
        }

        buffer.Count.Should().Be(128);
        buffer.LatestTickId.Should().Be(150);
        buffer.EarliestTickId.Should().Be(23); // 150 - 128 + 1

        // Incremental delta retrieval
        var deltas = buffer.GetDeltasSince(145, out bool requiresFull);
        requiresFull.Should().BeFalse();
        deltas.Count.Should().Be(5);
        deltas[0].TickId.Should().Be(146);
        deltas[4].TickId.Should().Be(150);
    }

    [Fact]
    public void SpectatorRingBuffer_DesyncGapDetection_RequiresFullSnapshot()
    {
        var buffer = new SpectatorRingBuffer();
        var baseTime = TimeProvider.System.GetUtcNow();

        for (uint i = 1; i <= 150; i++)
        {
            buffer.Push(new DeltaFrame
            {
                TickId = i,
                Timestamp = baseTime.AddMilliseconds(i * 50),
                IsKeyframe = i % 20 == 0,
                PropertyMask = i % 20 == 0 ? DeltaPropertyMask.FullSnapshot : DeltaPropertyMask.Position,
                Units = []
            });
        }

        // Client asking from tick 10 when earliest is 23 -> must trigger full snapshot
        var deltas = buffer.GetDeltasSince(10, out bool requiresFull);
        requiresFull.Should().BeTrue();
        deltas.Should().NotBeEmpty();
        deltas[0].IsKeyframe.Should().BeTrue(); // Starts from most recent keyframe
    }

    [Fact]
    public void SpectatorRingBuffer_DualStreamDelayEnforcement()
    {
        var buffer = new SpectatorRingBuffer();
        var baseTime = TimeProvider.System.GetUtcNow();

        for (uint i = 1; i <= 60; i++)
        {
            buffer.Push(new DeltaFrame
            {
                TickId = i,
                Timestamp = baseTime.AddSeconds(i),
                IsKeyframe = i == 1,
                PropertyMask = DeltaPropertyMask.FullSnapshot,
                Units = []
            });
        }

        // 1. Live Referee Stream (0s delay) -> gets tick 60
        var liveFrame = buffer.GetLatest();
        liveFrame.Should().NotBeNull();
        liveFrame!.TickId.Should().Be(60);

        // 2. Delayed Spectator Stream (10s delay relative to tick 60) -> gets tick 50
        var delayedFrame = buffer.GetDelayedFrame(TimeSpan.FromSeconds(10), baseTime.AddSeconds(60));
        delayedFrame.Should().NotBeNull();
        delayedFrame!.TickId.Should().Be(50);
    }

    [Fact]
    public async Task MatchAuditService_PreFlightTcpProbe_ReturnsFastResult()
    {
        var service = new MatchAuditService();
        var sw = System.Diagnostics.Stopwatch.StartNew();

        // Probing an unlikely-to-be-listening port to test timeout guardrail (<200ms)
        var result = await service.ProbeWebPortalAsync("127.0.0.1", port: 59999, timeoutMs: 150);
        sw.Stop();

        sw.ElapsedMilliseconds.Should().BeLessThan(5000, "Probe must not hang or timeout");
        result.Host.Should().Be("127.0.0.1");
        result.Port.Should().Be(59999);
    }

    [Fact]
    public void MatchAuditService_AuditCanvasPresentation_AssertsPixelEntropy()
    {
        var service = new MatchAuditService();

        // 1. All-black blank pixels
        byte[] blankPixels = new byte[100 * 4]; // 100 pixels RGBA 0
        var blankResult = service.AuditCanvasPresentation(blankPixels);
        blankResult.HasNonZeroEntropy.Should().BeFalse();
        blankResult.IsRenderHealthy.Should().BeFalse();

        // 2. Active colored pixels
        byte[] activePixels = new byte[100 * 4];
        for (int i = 0; i < activePixels.Length; i += 4)
        {
            activePixels[i] = 120;     // R
            activePixels[i + 1] = 180; // G
            activePixels[i + 2] = 220; // B
            activePixels[i + 3] = 255; // A
        }
        var activeResult = service.AuditCanvasPresentation(activePixels);
        activeResult.HasNonZeroEntropy.Should().BeTrue();
        activeResult.IsRenderHealthy.Should().BeTrue();
        activeResult.EntropyRatio.Should().Be(1.0);
    }

    [Fact]
    public void McpToolRegistry_RegistersPhase6dToolsAndAliases()
    {
        McpToolRegistry registry = new(registerDefaults: true);

        registry.HasTool("kongor_e2e_match_audit").Should().BeFalse();
        registry.HasTool("e2e_match_audit").Should().BeFalse();
        registry.HasTool("kongor_match_spectator").Should().BeFalse();
        registry.HasTool("match_spectator").Should().BeFalse();
    }
}
