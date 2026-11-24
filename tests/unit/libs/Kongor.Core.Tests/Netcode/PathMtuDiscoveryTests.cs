// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ============================================================================
// File: PathMtuDiscoveryTests.cs
// System: Kongor Core Tests - RFC 4821 PLPMTUD Unit Tests
// Policy: Strict Explicit Typing (NO 'var')
// ============================================================================

using ASPIRE.Common.Netcode;
using FluentAssertions;
using Xunit;

namespace Kongor.Core.Tests.Netcode;

public sealed class PathMtuDiscoveryTests
{
    private sealed class TestTimeProvider : TimeProvider
    {
        private long _timestamp = 100_000_000L;
        public override long GetTimestamp() => Volatile.Read(ref _timestamp);
        public override long TimestampFrequency => 1_000_000L; // 1 MHz (1 tick = 1 microsecond)

        public void Advance(TimeSpan delta)
        {
            long ticks = (long)(delta.TotalSeconds * TimestampFrequency);
            Interlocked.Add(ref _timestamp, ticks);
        }
    }

    [Fact]
    public void PathMtuDiscovery_InitialState_StartsAtConfiguredBaseMtu()
    {
        TestTimeProvider timeProvider = new();
        PathMtuDiscoveryService pmtu = new(timeProvider, initialMtu: 1200);

        pmtu.CurrentMtu.Should().Be(1200);
        pmtu.ConfirmedMtu.Should().Be(1200);
        pmtu.PlateauIndex.Should().Be(0);
        pmtu.State.Should().Be(PmtuState.Searching);
        pmtu.ActiveProbeSequence.Should().Be(0);
    }

    [Fact]
    public void ShouldProbe_StepsThroughStandardPlateaus_UponSuccessiveAcks()
    {
        TestTimeProvider timeProvider = new();
        PathMtuDiscoveryService pmtu = new(timeProvider, initialMtu: 1200);

        // 1. First probe should target index 1 (1280 bytes)
        bool shouldProbe1 = pmtu.ShouldProbe(out ushort probeSize1);
        shouldProbe1.Should().BeTrue();
        probeSize1.Should().Be(1280);

        pmtu.OnProbeDispatched(sequence: 101, probeSize: 1280);
        pmtu.State.Should().Be(PmtuState.Validating);
        pmtu.ActiveProbeSequence.Should().Be(101);

        // While validating, ShouldProbe must return false
        pmtu.ShouldProbe(out _).Should().BeFalse();

        // 2. Client ACKs probe 101
        bool acked1 = pmtu.OnProbeAcknowledged(sequence: 101);
        acked1.Should().BeTrue();
        pmtu.CurrentMtu.Should().Be(1280);
        pmtu.ConfirmedMtu.Should().Be(1280);
        pmtu.PlateauIndex.Should().Be(1);
        pmtu.State.Should().Be(PmtuState.Searching);

        // Advance past search interval (500ms)
        timeProvider.Advance(TimeSpan.FromMilliseconds(600));

        // 3. Second probe should target index 2 (1360 bytes)
        bool shouldProbe2 = pmtu.ShouldProbe(out ushort probeSize2);
        shouldProbe2.Should().BeTrue();
        probeSize2.Should().Be(1360);

        pmtu.OnProbeDispatched(sequence: 102, probeSize: 1360);
        bool acked2 = pmtu.OnProbeAcknowledged(sequence: 102);
        acked2.Should().BeTrue();
        pmtu.CurrentMtu.Should().Be(1360);
        pmtu.ConfirmedMtu.Should().Be(1360);
        pmtu.PlateauIndex.Should().Be(2);

        // 4. Advance and probe 1420 bytes
        timeProvider.Advance(TimeSpan.FromMilliseconds(600));
        pmtu.ShouldProbe(out ushort probeSize3).Should().BeTrue();
        probeSize3.Should().Be(1420);
        pmtu.OnProbeDispatched(103, 1420);
        pmtu.OnProbeAcknowledged(103).Should().BeTrue();
        pmtu.CurrentMtu.Should().Be(1420);

        // 5. Advance and probe 1472 bytes (ceiling)
        timeProvider.Advance(TimeSpan.FromMilliseconds(600));
        pmtu.ShouldProbe(out ushort probeSize4).Should().BeTrue();
        probeSize4.Should().Be(1472);
        pmtu.OnProbeDispatched(104, 1472);
        pmtu.OnProbeAcknowledged(104).Should().BeTrue();

        // Maximum plateau reached -> Confirmed state
        pmtu.CurrentMtu.Should().Be(1472);
        pmtu.ConfirmedMtu.Should().Be(1472);
        pmtu.PlateauIndex.Should().Be(4);
        pmtu.State.Should().Be(PmtuState.Confirmed);

        // No higher plateau to probe
        pmtu.ShouldProbe(out _).Should().BeFalse();
    }

    [Fact]
    public void OnProbeTimeout_OrLost_ClampsToHighestConfirmedPlateau()
    {
        TestTimeProvider timeProvider = new();
        PathMtuDiscoveryService pmtu = new(timeProvider, initialMtu: 1280);

        // Advance and probe 1360
        timeProvider.Advance(TimeSpan.FromMilliseconds(600));
        pmtu.ShouldProbe(out ushort probeSize).Should().BeTrue();
        probeSize.Should().Be(1360);

        pmtu.OnProbeDispatched(sequence: 201, probeSize: 1360);

        // Advance beyond 1500ms validation timeout
        timeProvider.Advance(TimeSpan.FromMilliseconds(1600));

        // ShouldProbe detects timeout, resets active probe, clamps MTU
        pmtu.ShouldProbe(out _).Should().BeFalse();
        pmtu.CurrentMtu.Should().Be(1280);
        pmtu.ConfirmedMtu.Should().Be(1280);
        pmtu.ActiveProbeSequence.Should().Be(0);
    }

    [Fact]
    public void ReportConsecutiveLoss_DropsToSafeBaseMtu_AndEntersBlackHoleState()
    {
        TestTimeProvider timeProvider = new();
        PathMtuDiscoveryService pmtu = new(timeProvider, initialMtu: 1472);

        // Consecutive packet losses observed
        pmtu.ReportConsecutiveLoss(consecutiveLosses: 3);

        pmtu.CurrentMtu.Should().Be(1200);
        pmtu.ConfirmedMtu.Should().Be(1200);
        pmtu.PlateauIndex.Should().Be(0);
        pmtu.State.Should().Be(PmtuState.BlackHole);
    }

    [Fact]
    public void ProbePayload_TryWrite_And_IsProbePayload_RoundtripsSuccessfully()
    {
        byte[] buffer = new byte[1360];
        uint sequence = 998877;

        bool writeSuccess = PathMtuDiscoveryService.TryWriteProbePayload(buffer, sequence, 1360);
        writeSuccess.Should().BeTrue();

        bool isProbe = PathMtuDiscoveryService.IsProbePayload(buffer.AsSpan(0, 1360), out uint parsedSeq);
        isProbe.Should().BeTrue();
        parsedSeq.Should().Be(sequence);

        // Corrupted magic should fail
        buffer[0] = 0x00;
        bool isCorrupted = PathMtuDiscoveryService.IsProbePayload(buffer.AsSpan(0, 1360), out _);
        isCorrupted.Should().BeFalse();
    }
}
