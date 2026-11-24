// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ============================================================================
// File: InflightPacketTrackerLossTests.cs
// System: COMPEL Tests - Inflight Loss Rate & Congestion Signal Unit Tests
// Policy: Strict Explicit Typing (NO 'var')
// ============================================================================

using ASPIRE.Common.Netcode;
using FluentAssertions;
using Xunit;

namespace COMPEL.Tests;

public sealed class InflightPacketTrackerLossTests
{
    [Fact]
    public void RecentLossRate_AllPacketsAcked_ReturnsZeroLoss()
    {
        InflightPacketTracker tracker = new(capacity: 64);

        for (uint i = 1; i <= 20; i++)
        {
            tracker.TrackPacket(i, 1000, sendTimestampUs: i * 10_000);
            tracker.TryProcessAck(i, ackTimestampEchoUs: i * 10_000, receiveTimestampUs: (i * 10_000) + 20_000, out _, out _);
        }

        tracker.RecentLossRate.Should().Be(0.0);
        tracker.ConsecutiveLossCount.Should().Be(0);
        tracker.TotalPacketsSent.Should().Be(20);
        tracker.TotalPacketsAcked.Should().Be(20);
        tracker.TotalPacketsLost.Should().Be(0);
    }

    [Fact]
    public void RecentLossRate_ReapedPackets_CalculatesExactSlidingLossPercentage()
    {
        InflightPacketTracker tracker = new(capacity: 64);

        // Send 10 packets: 1 to 10
        for (uint i = 1; i <= 10; i++)
        {
            tracker.TrackPacket(i, 1000, sendTimestampUs: i * 10_000);
        }

        // ACK packet 1 at 30ms
        tracker.TryProcessAck(1, 10_000, receiveTimestampUs: 30_000, out _, out _);

        // Now suppose packets 2, 3, 4 are lost. Later at 300ms, packet 5 is ACKed.
        // Age of packets 2, 3, 4 exceeds 200ms -> automatically reaped as lost
        tracker.TryProcessAck(5, 50_000, receiveTimestampUs: 300_000, out _, out _);

        // Packets ACKed: 1, 5 (2 total). Packets lost: 2, 3, 4 (3 total). Total outcomes: 5.
        tracker.TotalPacketsSent.Should().Be(10);
        tracker.TotalPacketsAcked.Should().Be(2);
        tracker.TotalPacketsLost.Should().Be(3);

        // Loss rate should be 3 / 5 = 0.60 (60%)
        tracker.RecentLossRate.Should().BeApproximately(0.60, 0.01);
    }

    [Fact]
    public void ConsecutiveLossCount_TracksConsecutiveDrops_AndResetsOnAck()
    {
        InflightPacketTracker tracker = new(capacity: 64);

        // Send packets 1..5
        for (uint i = 1; i <= 5; i++)
        {
            tracker.TrackPacket(i, 500, sendTimestampUs: i * 5000);
        }

        // Packets 1, 2, 3 are skipped. Packet 4 is ACKed at 250ms, causing 1, 2, 3 to be reaped
        tracker.TryProcessAck(4, 20_000, receiveTimestampUs: 250_000, out _, out _);

        // Because packet 4 was ACKed, consecutive losses reset to 0
        tracker.ConsecutiveLossCount.Should().Be(0);
        tracker.TotalPacketsLost.Should().Be(3);
    }

    [Fact]
    public void GetCongestionSignals_ReturnsConsistentSnapshot()
    {
        InflightPacketTracker tracker = new(capacity: 64);

        tracker.TrackPacket(1, 1200, 10_000);
        tracker.TrackPacket(2, 1200, 20_000);

        tracker.GetCongestionSignals(out long inflight, out double lossRate, out int consecutiveLoss);

        inflight.Should().Be(2400);
        lossRate.Should().Be(0.0);
        consecutiveLoss.Should().Be(0);
    }
}
