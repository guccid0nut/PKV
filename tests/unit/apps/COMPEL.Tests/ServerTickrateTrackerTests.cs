// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ============================================================================
// File: ServerTickrateTrackerTests.cs
// System: COMPEL Tests - Real-Time Server Tickrate & Observability Unit Tests
// Policy: Strict Explicit Typing (NO 'var')
// ============================================================================

using COMPEL.Services.Telemetry;
using FluentAssertions;
using Xunit;

namespace COMPEL.Tests;

public sealed class ServerTickrateTrackerTests
{
    [Fact]
    public void IdealTickrate_SteadyTicks_ComputesExactFrequency()
    {
        // Arrange: 60 Hz target (16.666 ms per tick)
        ControllableTimeProvider timeProvider = new();
        ServerTickrateTracker tracker = new(targetTickRate: 60, timeProvider: timeProvider);

        // Act: Simulate 60 steady ticks at 16.667ms
        tracker.RecordTick(); // Seed initial timestamp
        for (int i = 0; i < 60; i++)
        {
            timeProvider.Advance(TimeSpan.FromMilliseconds(16.6666667));
            tracker.RecordTick();
        }

        // Assert
        tracker.CurrentTickrateHz.Should().BeInRange(59.5, 60.5);
        tracker.AverageTickDurationMs.Should().BeInRange(16.5, 16.8);
        tracker.TickVarianceMs.Should().BeInRange(0.0, 0.2, "steady ticks should produce near-zero variance");
        tracker.SlowTickCount.Should().Be(0);
        tracker.IsStalled.Should().BeFalse();
    }

    [Fact]
    public void TickVariance_JitteryTicks_ComputesStandardDeviation()
    {
        // Arrange: alternating 10ms and 30ms ticks (mean = 20ms, standard deviation = 10ms)
        ControllableTimeProvider timeProvider = new();
        ServerTickrateTracker tracker = new(targetTickRate: 50, windowSize: 64, timeProvider: timeProvider);

        tracker.RecordTick(); // Seed
        for (int i = 0; i < 32; i++)
        {
            timeProvider.Advance(TimeSpan.FromMilliseconds(10));
            tracker.RecordTick();

            timeProvider.Advance(TimeSpan.FromMilliseconds(30));
            tracker.RecordTick();
        }

        // Assert
        tracker.AverageTickDurationMs.Should().BeInRange(19.5, 20.5);
        tracker.TickVarianceMs.Should().BeInRange(9.5, 10.5, "standard deviation of alternating 10ms and 30ms ticks is exactly 10ms");
        tracker.CurrentTickrateHz.Should().BeInRange(49.0, 51.0);
    }

    [Fact]
    public void SlowTicks_ExceedingThreshold_IncrementsCounter()
    {
        // Arrange: 60 Hz (target: 16.67 ms, 1.25x threshold = 20.83 ms)
        ControllableTimeProvider timeProvider = new();
        ServerTickrateTracker tracker = new(targetTickRate: 60, timeProvider: timeProvider);

        tracker.RecordTick(); // Seed

        // 5 normal ticks
        for (int i = 0; i < 5; i++)
        {
            timeProvider.Advance(TimeSpan.FromMilliseconds(16.67));
            tracker.RecordTick();
        }
        tracker.SlowTickCount.Should().Be(0);

        // 1 slow tick taking 35ms (severe simulation spike)
        timeProvider.Advance(TimeSpan.FromMilliseconds(35.0));
        tracker.RecordTick();
        tracker.SlowTickCount.Should().Be(1);

        // 1 more slow tick taking 25ms
        timeProvider.Advance(TimeSpan.FromMilliseconds(25.0));
        tracker.RecordTick();
        tracker.SlowTickCount.Should().Be(2);
    }

    [Fact]
    public void ZeroAllocation_ContinuousTracking_AllocatesZeroBytes()
    {
        // Arrange
        ControllableTimeProvider timeProvider = new();
        ServerTickrateTracker tracker = new(targetTickRate: 60, windowSize: 64, timeProvider: timeProvider);

        // Warmup: populate circular buffer
        tracker.RecordTick();
        for (int i = 0; i < 100; i++)
        {
            timeProvider.Advance(TimeSpan.FromMilliseconds(16.67));
            tracker.RecordTick();
        }

        // Act: Measure GC allocations across 1,000 tick records
        long beforeAllocated = GC.GetAllocatedBytesForCurrentThread();

        for (int i = 0; i < 1000; i++)
        {
            timeProvider.Advance(TimeSpan.FromMilliseconds(16.67));
            tracker.RecordTick();
        }

        long afterAllocated = GC.GetAllocatedBytesForCurrentThread();
        long totalAllocated = afterAllocated - beforeAllocated;

        // Assert
        totalAllocated.Should().Be(0, "hot-path simulation tick recording must produce strictly zero heap allocations");
    }

    [Fact]
    public void StallDetection_DelayedTick_ReportsIsStalled()
    {
        // Arrange: 60 Hz (target: 16.67 ms, 3x stall threshold = 50.0 ms)
        ControllableTimeProvider timeProvider = new();
        ServerTickrateTracker tracker = new(targetTickRate: 60, timeProvider: timeProvider);

        tracker.RecordTick(); // Seed
        timeProvider.Advance(TimeSpan.FromMilliseconds(16.67));
        tracker.RecordTick();

        tracker.IsStalled.Should().BeFalse();

        // Advance 250ms without a tick (engine frozen)
        timeProvider.Advance(TimeSpan.FromMilliseconds(250));
        tracker.IsStalled.Should().BeTrue("server stopped ticking for > 3x target tick duration");
    }

    [Fact]
    public void CompelMetrics_Instruments_RegisteredAndRecordable()
    {
        // Assert that new OpenTelemetry metric instruments exist and record without throwing
        CompelMetrics.ServerTickrateHz.Should().NotBeNull();
        CompelMetrics.ServerTickDurationMs.Should().NotBeNull();
        CompelMetrics.ServerTickVarianceMs.Should().NotBeNull();
        CompelMetrics.ServerSlowTicks.Should().NotBeNull();

        CompelMetrics.FecPacketsRecovered.Should().NotBeNull();
        CompelMetrics.FecRecoveryFailed.Should().NotBeNull();
        CompelMetrics.FecParityPacketsGenerated.Should().NotBeNull();
        CompelMetrics.JitterDelayMs.Should().NotBeNull();
        CompelMetrics.ShieldReplaysDropped.Should().NotBeNull();
        CompelMetrics.ShieldBurstViolations.Should().NotBeNull();

        // Act: verify recording values does not throw
        CompelMetrics.ServerTickrateHz.Record(60.0);
        CompelMetrics.ServerTickDurationMs.Record(16.67);
        CompelMetrics.ServerTickVarianceMs.Record(0.05);
        CompelMetrics.ServerSlowTicks.Add(1);
        CompelMetrics.FecPacketsRecovered.Add(1);
        CompelMetrics.FecRecoveryFailed.Add(1);
        CompelMetrics.FecParityPacketsGenerated.Add(1);
        CompelMetrics.JitterDelayMs.Record(5.0);
        CompelMetrics.ShieldReplaysDropped.Add(1);
        CompelMetrics.ShieldBurstViolations.Add(1);
    }
}
