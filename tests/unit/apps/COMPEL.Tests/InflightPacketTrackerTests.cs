// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ============================================================================
// File: InflightPacketTrackerTests.cs
// System: COMPEL Tests - Microsecond Inflight Packet Ring Buffer Unit Tests
// Policy: Strict Explicit Typing (NO 'var')
// ============================================================================

using ASPIRE.Common.Netcode;
using FluentAssertions;
using Xunit;

namespace COMPEL.Tests;

public sealed class InflightPacketTrackerTests
{
    [Fact]
    public void InflightPacketTracker_CapacityMustBePowerOfTwo()
    {
        Action invalidZero = () => new InflightPacketTracker(0);
        Action invalidNonPowerOfTwo = () => new InflightPacketTracker(100);
        Action validPowerOfTwo = () => new InflightPacketTracker(128);

        invalidZero.Should().Throw<ArgumentException>();
        invalidNonPowerOfTwo.Should().Throw<ArgumentException>();
        validPowerOfTwo.Should().NotThrow();
    }

    [Fact]
    public void TrackPacket_IncreasesInflightBytes_AndTracksHighestSequence()
    {
        InflightPacketTracker tracker = new(capacity: 64);

        tracker.TrackPacket(sequence: 1, sizeBytes: 1200, sendTimestampUs: 10_000);
        tracker.InflightBytes.Should().Be(1200);
        tracker.HighestSentSequence.Should().Be(1);

        tracker.TrackPacket(sequence: 2, sizeBytes: 800, sendTimestampUs: 20_000);
        tracker.InflightBytes.Should().Be(2000);
        tracker.HighestSentSequence.Should().Be(2);
    }

    [Fact]
    public void TryProcessAck_MatchesSequence_CalculatesMeasuredRtt_AndReducesInflightBytes()
    {
        InflightPacketTracker tracker = new(capacity: 64);

        tracker.TrackPacket(sequence: 10, sizeBytes: 1400, sendTimestampUs: 100_000);

        // Receive ACK echoing send timestamp with 25ms delay (25,000 us)
        long receiveTimeUs = 125_000;
        bool ackProcessed = tracker.TryProcessAck(
            ackSequence: 10,
            ackTimestampEchoUs: 100_000,
            receiveTimestampUs: receiveTimeUs,
            out long bytesAcked,
            out TimeSpan measuredRtt);

        ackProcessed.Should().BeTrue();
        bytesAcked.Should().Be(1400);
        tracker.InflightBytes.Should().Be(0);
        tracker.HighestAckedSequence.Should().Be(10);
        measuredRtt.TotalMilliseconds.Should().Be(25.0);
    }

    [Fact]
    public void TryProcessAck_DuplicateOrInvalidAck_ReturnsFalse()
    {
        InflightPacketTracker tracker = new(capacity: 64);

        tracker.TrackPacket(sequence: 5, sizeBytes: 1000, sendTimestampUs: 50_000);

        // Process first ACK
        bool firstAck = tracker.TryProcessAck(
            ackSequence: 5,
            ackTimestampEchoUs: 50_000,
            receiveTimestampUs: 70_000,
            out long firstBytes,
            out _);

        firstAck.Should().BeTrue();
        firstBytes.Should().Be(1000);

        // Duplicate ACK for sequence 5
        bool duplicateAck = tracker.TryProcessAck(
            ackSequence: 5,
            ackTimestampEchoUs: 50_000,
            receiveTimestampUs: 75_000,
            out long dupBytes,
            out _);

        duplicateAck.Should().BeFalse();
        dupBytes.Should().Be(0);

        // Unknown sequence 99
        bool unknownAck = tracker.TryProcessAck(
            ackSequence: 99,
            ackTimestampEchoUs: 50_000,
            receiveTimestampUs: 80_000,
            out long unkBytes,
            out _);

        unknownAck.Should().BeFalse();
        unkBytes.Should().Be(0);
    }

    [Fact]
    public void TryProcessAck_ReapsLostPrecedingPackets_WhenAgeExceedsThreshold()
    {
        InflightPacketTracker tracker = new(capacity: 64);

        // Transmit packets 1, 2, and 3
        tracker.TrackPacket(sequence: 1, sizeBytes: 1000, sendTimestampUs: 10_000);
        tracker.TrackPacket(sequence: 2, sizeBytes: 1000, sendTimestampUs: 15_000); // Packet 2 will be lost on wire
        tracker.TrackPacket(sequence: 3, sizeBytes: 1000, sendTimestampUs: 20_000);

        tracker.InflightBytes.Should().Be(3000);

        // Client receives packet 1 at 35ms and ACKs it
        tracker.TryProcessAck(1, 10_000, receiveTimestampUs: 35_000, out _, out _);
        tracker.InflightBytes.Should().Be(2000); // Packets 2 and 3 still in flight

        // Suppose packet 2 was dropped by router. Later at 250ms, packet 3 is ACKed.
        // Age of packet 2 is (250_000 - 15_000) = 235ms > 200ms threshold
        bool ack3 = tracker.TryProcessAck(3, 20_000, receiveTimestampUs: 250_000, out long bytes3, out _);

        ack3.Should().BeTrue();
        bytes3.Should().Be(1000);

        // InflightBytes should be 0 because packet 3 was ACKed and lost packet 2 was automatically reaped!
        tracker.InflightBytes.Should().Be(0);
    }

    [Fact]
    public void RingBuffer_Wraparound_HandlesSequencesAcrossCapacity()
    {
        InflightPacketTracker tracker = new(capacity: 16);

        // Transmit 30 packets through a 16-slot ring buffer
        for (uint i = 1; i <= 30; i++)
        {
            tracker.TrackPacket(sequence: i, sizeBytes: 100, sendTimestampUs: i * 1000);
        }

        tracker.HighestSentSequence.Should().Be(30);

        // Slots older than 30 - 16 = 14 were overwritten, so only the most recent 16 are valid
        // Sequence 30 is valid
        bool ack30 = tracker.TryProcessAck(30, 30_000, receiveTimestampUs: 35_000, out long bytes30, out _);
        ack30.Should().BeTrue();
        bytes30.Should().Be(100);

        // Overwritten sequence 5 should not be acknowledged
        bool ack5 = tracker.TryProcessAck(5, 5_000, receiveTimestampUs: 36_000, out long bytes5, out _);
        ack5.Should().BeFalse();
        bytes5.Should().Be(0);
    }

    [Fact]
    public void Reset_ClearsAllTrackingState()
    {
        InflightPacketTracker tracker = new(capacity: 32);

        tracker.TrackPacket(1, 500, 1000);
        tracker.TrackPacket(2, 500, 2000);
        tracker.InflightBytes.Should().Be(1000);

        tracker.Reset();

        tracker.InflightBytes.Should().Be(0);
        tracker.HighestSentSequence.Should().Be(0);
        tracker.HighestAckedSequence.Should().Be(0);
    }
}
