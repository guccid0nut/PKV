// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ============================================================================
// File: AdaptiveJitterBufferTests.cs
// System: Kongor Core Netcode - Zero-Allocation Adaptive Sub-Tick Jitter Buffer Unit Tests
// Policy: Strict Explicit Typing (NO 'var')
// ============================================================================

using System.Text;
using ASPIRE.Common.Netcode;
using FluentAssertions;
using Xunit;

namespace Kongor.Core.Tests.Netcode;

public sealed class AdaptiveJitterBufferTests
{
    [Fact]
    public void TryEnqueue_ZeroDelay_ImmediatelyDequeuable()
    {
        // Arrange - Zero delay buffer
        AdaptiveJitterBuffer buffer = new(capacity: 32, minDelayUs: 0, maxDelayUs: 20_000);
        byte[] payload = Encoding.UTF8.GetBytes("MoveTo(1500, 2400)");
        long nowUs = 1_000_000L;

        // Act - Enqueue
        bool enqueued = buffer.TryEnqueue(payload, sequence: 101, currentTimestampUs: nowUs, out bool droppedAsFull);

        // Assert - Enqueue
        enqueued.Should().BeTrue();
        droppedAsFull.Should().BeFalse();
        buffer.OccupiedCount.Should().Be(1);

        // Act - Dequeue at exact same timestamp
        byte[] outBuffer = new byte[128];
        bool dequeued = buffer.TryDequeue(nowUs, outBuffer, out int bytesWritten, out uint sequence);

        // Assert - Dequeue
        dequeued.Should().BeTrue();
        bytesWritten.Should().Be(payload.Length);
        sequence.Should().Be(101);
        Encoding.UTF8.GetString(outBuffer, 0, bytesWritten).Should().Be("MoveTo(1500, 2400)");
        buffer.OccupiedCount.Should().Be(0);
    }

    [Fact]
    public void TryDequeue_BeforeScheduledRelease_ReturnsFalse()
    {
        // Arrange - Buffer with minimum 10ms (10,000us) playout delay
        AdaptiveJitterBuffer buffer = new(capacity: 32, minDelayUs: 10_000, maxDelayUs: 20_000);
        byte[] payload = Encoding.UTF8.GetBytes("AttackEntity(42)");
        long enqueueTimeUs = 1_000_000L;

        // Act - Enqueue at t=1,000,000
        buffer.TryEnqueue(payload, sequence: 1, currentTimestampUs: enqueueTimeUs, out _);

        // Act & Assert 1: Query at t=1,005,000 (only 5ms elapsed, need 10ms)
        byte[] outBuffer = new byte[128];
        bool tooEarly = buffer.TryDequeue(enqueueTimeUs + 5_000L, outBuffer, out _, out _);
        tooEarly.Should().BeFalse();
        buffer.OccupiedCount.Should().Be(1);

        // Act & Assert 2: Query at t=1,010,000 (10ms elapsed -> release!)
        bool onTime = buffer.TryDequeue(enqueueTimeUs + 10_000L, outBuffer, out int bytesWritten, out uint seq);
        onTime.Should().BeTrue();
        seq.Should().Be(1);
        bytesWritten.Should().Be(payload.Length);
        buffer.OccupiedCount.Should().Be(0);
    }

    [Fact]
    public void JitterEstimator_EwmaTracksVariations_AdjustsPlayoutDelay()
    {
        // Arrange
        AdaptiveJitterBuffer buffer = new(capacity: 32, minDelayUs: 2_000, maxDelayUs: 20_000);

        buffer.TargetPlayoutDelayUs.Should().Be(2_000L); // Starts at min delay

        // Act: Simulate heavy arrival jitter (e.g. 15ms arrival interval swing)
        // Repeat delta updates to allow EWMA to adapt
        for (int i = 0; i < 20; i++)
        {
            buffer.RecordInterArrivalDelta(deltaUs: 15_000L);
        }

        // Assert: Playout delay must expand to cushion against jitter
        buffer.CurrentJitterUs.Should().BeGreaterThan(5_000L);
        buffer.TargetPlayoutDelayUs.Should().BeGreaterThan(5_000L);
        buffer.TargetPlayoutDelayUs.Should().BeLessThanOrEqualTo(20_000L);

        // Act: Simulate stable network with 0ms jitter
        for (int i = 0; i < 40; i++)
        {
            buffer.RecordInterArrivalDelta(deltaUs: 0L);
        }

        // Assert: Playout delay decays back toward minimum
        buffer.TargetPlayoutDelayUs.Should().BeLessThan(4_000L);
    }

    [Fact]
    public void TryEnqueue_RingBufferFull_DropsGracefullyWithoutException()
    {
        // Arrange: Buffer with capacity 4
        AdaptiveJitterBuffer buffer = new(capacity: 4, minDelayUs: 50_000, maxDelayUs: 100_000);
        byte[] payload = [1, 2, 3, 4];
        long nowUs = 1_000_000L;

        // Fill capacity 4
        for (uint seq = 1; seq <= 4; seq++)
        {
            bool success = buffer.TryEnqueue(payload, seq, nowUs, out bool dropped);
            success.Should().BeTrue();
            dropped.Should().BeFalse();
        }

        buffer.OccupiedCount.Should().Be(4);

        // 5th packet should drop gracefully
        bool fifth = buffer.TryEnqueue(payload, sequence: 5, nowUs, out bool droppedAsFull);
        fifth.Should().BeFalse();
        droppedAsFull.Should().BeTrue();
        buffer.OccupiedCount.Should().Be(4);
    }

    [Fact]
    public void DrainAll_FlushesAllEnqueuedPacketsImmediately()
    {
        // Arrange
        AdaptiveJitterBuffer buffer = new(capacity: 32, minDelayUs: 50_000, maxDelayUs: 100_000);
        long nowUs = 1_000_000L;

        for (uint seq = 1; seq <= 5; seq++)
        {
            buffer.TryEnqueue(Encoding.UTF8.GetBytes($"Packet_{seq}"), seq, nowUs, out _);
        }

        buffer.OccupiedCount.Should().Be(5);

        // Act - Drain All regardless of scheduled release time
        List<uint> drainedSequences = new();
        buffer.DrainAll((payload, seq) =>
        {
            drainedSequences.Add(seq);
        });

        // Assert
        buffer.OccupiedCount.Should().Be(0);
        drainedSequences.Should().Equal(1, 2, 3, 4, 5);
    }

    [Fact]
    public void HighThroughput_ZeroAllocationVerification()
    {
        // Arrange
        AdaptiveJitterBuffer buffer = new(capacity: 64, minDelayUs: 0, maxDelayUs: 20_000);
        byte[] payload = new byte[64];
        byte[] outBuffer = new byte[128];
        long nowUs = 1_000_000L;

        // Warm up
        for (uint i = 0; i < 50; i++)
        {
            buffer.TryEnqueue(payload, i, nowUs, out _);
            buffer.TryDequeue(nowUs, outBuffer, out _, out _);
        }

        // Measure allocations over 1,000 iterations
        long allocBefore = GC.GetAllocatedBytesForCurrentThread();

        for (uint i = 100; i < 1100; i++)
        {
            buffer.TryEnqueue(payload, i, nowUs, out _);
            buffer.TryDequeue(nowUs, outBuffer, out _, out _);
        }

        long allocAfter = GC.GetAllocatedBytesForCurrentThread();
        long diff = allocAfter - allocBefore;

        // Zero heap allocations
        diff.Should().Be(0);
    }
}
