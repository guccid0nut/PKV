// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ============================================================================
// File: SlidingBitmaskAntiReplayTests.cs
// System: Kongor Core Netcode - RFC 4303 64-Bit Sliding Bitmask Anti-Replay Unit Tests
// Policy: Strict Explicit Typing (NO 'var')
// ============================================================================

using ASPIRE.Common.Netcode;
using FluentAssertions;
using Xunit;

namespace Kongor.Core.Tests.Netcode;

public sealed class SlidingBitmaskAntiReplayTests
{
    [Fact]
    public void CheckAndAdmit_SequentialInOrder_AllAcceptedAsNewest()
    {
        // Arrange
        SlidingBitmaskAntiReplay antiReplay = new();

        // Act & Assert
        for (uint seq = 1; seq <= 100; seq++)
        {
            AntiReplayStatus status = antiReplay.CheckAndAdmit(seq);
            status.Should().Be(AntiReplayStatus.AcceptedNewest);
            antiReplay.MaxSequence.Should().Be(seq);
        }
    }

    [Fact]
    public void CheckAndAdmit_OutofOrderWithinWindow_AcceptedAsReordered()
    {
        // Arrange
        SlidingBitmaskAntiReplay antiReplay = new();

        // Send sequence 5 first
        AntiReplayStatus status5 = antiReplay.CheckAndAdmit(5);
        status5.Should().Be(AntiReplayStatus.AcceptedNewest);
        antiReplay.MaxSequence.Should().Be(5);

        // Send out of order: 2, 4, 3, 1 (all < 5 and within 64-window)
        AntiReplayStatus status2 = antiReplay.CheckAndAdmit(2);
        status2.Should().Be(AntiReplayStatus.AcceptedReordered);

        AntiReplayStatus status4 = antiReplay.CheckAndAdmit(4);
        status4.Should().Be(AntiReplayStatus.AcceptedReordered);

        AntiReplayStatus status3 = antiReplay.CheckAndAdmit(3);
        status3.Should().Be(AntiReplayStatus.AcceptedReordered);

        AntiReplayStatus status1 = antiReplay.CheckAndAdmit(1);
        status1.Should().Be(AntiReplayStatus.AcceptedReordered);

        // Max sequence should still be 5
        antiReplay.MaxSequence.Should().Be(5);
    }

    [Fact]
    public void CheckAndAdmit_DuplicatePackets_ReturnsDuplicate()
    {
        // Arrange
        SlidingBitmaskAntiReplay antiReplay = new();

        // Admit 10
        antiReplay.CheckAndAdmit(10).Should().Be(AntiReplayStatus.AcceptedNewest);

        // Replay 10
        antiReplay.CheckAndAdmit(10).Should().Be(AntiReplayStatus.Duplicate);

        // Admit 8 (reordered)
        antiReplay.CheckAndAdmit(8).Should().Be(AntiReplayStatus.AcceptedReordered);

        // Replay 8
        antiReplay.CheckAndAdmit(8).Should().Be(AntiReplayStatus.Duplicate);
    }

    [Fact]
    public void CheckAndAdmit_StalePacketsBeyond64Window_ReturnsStale()
    {
        // Arrange
        SlidingBitmaskAntiReplay antiReplay = new();

        // Admit sequence 100
        antiReplay.CheckAndAdmit(100).Should().Be(AntiReplayStatus.AcceptedNewest);

        // Packet 36 is exactly (100 - 64) -> Stale boundary
        antiReplay.CheckAndAdmit(36).Should().Be(AntiReplayStatus.Stale);

        // Packet 10 is way too old (100 - 90) -> Stale
        antiReplay.CheckAndAdmit(10).Should().Be(AntiReplayStatus.Stale);

        // Packet 37 is (100 - 63) -> Within 64 window!
        antiReplay.CheckAndAdmit(37).Should().Be(AntiReplayStatus.AcceptedReordered);

        // Replaying 37 now returns Duplicate
        antiReplay.CheckAndAdmit(37).Should().Be(AntiReplayStatus.Duplicate);
    }

    [Fact]
    public void CheckAndAdmit_LargeForwardJump_AdvancesWindowCleanly()
    {
        // Arrange
        SlidingBitmaskAntiReplay antiReplay = new();

        antiReplay.CheckAndAdmit(10).Should().Be(AntiReplayStatus.AcceptedNewest);

        // Jump forward by 100 packets (well past 64 window size)
        antiReplay.CheckAndAdmit(110).Should().Be(AntiReplayStatus.AcceptedNewest);
        antiReplay.MaxSequence.Should().Be(110);

        // Packets from the old window (<= 46) must now be stale
        antiReplay.CheckAndAdmit(10).Should().Be(AntiReplayStatus.Stale);
        antiReplay.CheckAndAdmit(46).Should().Be(AntiReplayStatus.Stale);

        // Packets in the new window (110 - 63 = 47) must be accepted as reordered
        antiReplay.CheckAndAdmit(47).Should().Be(AntiReplayStatus.AcceptedReordered);
    }

    [Fact]
    public void CheckAndAdmit_Unsigned32BitRollover_HandlesMaxIntCleanly()
    {
        // Arrange: Start sequence right before uint.MaxValue
        SlidingBitmaskAntiReplay antiReplay = new();

        uint startSeq = uint.MaxValue - 2; // 0xFFFFFFFD
        antiReplay.CheckAndAdmit(startSeq).Should().Be(AntiReplayStatus.AcceptedNewest);

        // Advance to uint.MaxValue - 1
        antiReplay.CheckAndAdmit(startSeq + 1).Should().Be(AntiReplayStatus.AcceptedNewest);

        // Advance to uint.MaxValue
        antiReplay.CheckAndAdmit(uint.MaxValue).Should().Be(AntiReplayStatus.AcceptedNewest);

        // Rollover to 0
        antiReplay.CheckAndAdmit(0).Should().Be(AntiReplayStatus.AcceptedNewest);
        antiReplay.MaxSequence.Should().Be(0);

        // Advance to 1
        antiReplay.CheckAndAdmit(1).Should().Be(AntiReplayStatus.AcceptedNewest);

        // Out of order packet uint.MaxValue - 1 (within 64 of rollover max) should be duplicate (already seen)
        antiReplay.CheckAndAdmit(uint.MaxValue - 1).Should().Be(AntiReplayStatus.Duplicate);

        // Packet uint.MaxValue - 2 was seen, replaying is duplicate
        antiReplay.CheckAndAdmit(uint.MaxValue - 2).Should().Be(AntiReplayStatus.Duplicate);
    }

    [Fact]
    public void Reset_ClearsAllState()
    {
        // Arrange
        SlidingBitmaskAntiReplay antiReplay = new();
        antiReplay.CheckAndAdmit(42).Should().Be(AntiReplayStatus.AcceptedNewest);
        antiReplay.MaxSequence.Should().Be(42);

        // Act
        antiReplay.Reset();

        // Assert
        antiReplay.IsInitialized.Should().BeFalse();
        antiReplay.MaxSequence.Should().Be(0);

        // Re-admitting 42 after reset should succeed as AcceptedNewest
        antiReplay.CheckAndAdmit(42).Should().Be(AntiReplayStatus.AcceptedNewest);
    }
}
