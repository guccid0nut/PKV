// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ============================================================================
// File: BbrCongestionControlTests.cs
// System: COMPEL Tests - BBR Congestion Control & Token-Bucket Pacer Unit Tests
// Policy: Strict Explicit Typing (NO 'var')
// ============================================================================

using COMPEL.Services.Proxy.Congestion;
using FluentAssertions;
using Xunit;

namespace COMPEL.Tests;

public sealed class BbrCongestionControlTests
{
    [Fact]
    public void Bbr_InitializesInStartupMode_WithHighPacingGain()
    {
        ControllableTimeProvider timeProvider = new();
        BbrCongestionController bbr = new(timeProvider);

        bbr.Phase.Should().Be(BbrPhase.Startup);
        bbr.PacingGain.Should().Be(BbrCongestionController.HighGain);
        bbr.GetPacingRateBytesPerSec().Should().BeGreaterThanOrEqualTo(1_000_000L);
        bbr.GetTargetCongestionWindowBytes().Should().BeGreaterThanOrEqualTo(BbrCongestionController.MinCwndBytes);
    }

    [Fact]
    public void Bbr_TransitionsToDrain_WhenBandwidthPlateaus()
    {
        ControllableTimeProvider timeProvider = new();
        BbrCongestionController bbr = new(timeProvider);

        // Simulate 3 rounds where bandwidth plateaus at 2 MB/s
        for (int i = 0; i < 3; i++)
        {
            timeProvider.Advance(TimeSpan.FromMilliseconds(55));
            bbr.OnAckReceived(bytesAcked: 110_000, measuredRtt: TimeSpan.FromMilliseconds(50));
        }

        bbr.Phase.Should().Be(BbrPhase.Drain);
        bbr.PacingGain.Should().Be(BbrCongestionController.DrainGain);
    }

    [Fact]
    public void Bbr_CyclesProbeBwGains_InProbeBwPhase()
    {
        ControllableTimeProvider timeProvider = new();
        BbrCongestionController bbr = new(timeProvider);

        // Advance past Startup and Drain into ProbeBW
        for (int i = 0; i < 5; i++)
        {
            timeProvider.Advance(TimeSpan.FromMilliseconds(60));
            bbr.OnAckReceived(bytesAcked: 100_000, measuredRtt: TimeSpan.FromMilliseconds(50));
        }

        bbr.Phase.Should().Be(BbrPhase.ProbeBW);
        double firstGain = bbr.PacingGain;

        // Advance another round to cycle gain
        timeProvider.Advance(TimeSpan.FromMilliseconds(60));
        bbr.OnAckReceived(bytesAcked: 100_000, measuredRtt: TimeSpan.FromMilliseconds(50));

        bbr.PacingGain.Should().NotBe(firstGain);
    }

    [Fact]
    public void Bbr_EntersProbeRtt_WhenMinRttExpires()
    {
        ControllableTimeProvider timeProvider = new();
        BbrCongestionController bbr = new(timeProvider);

        // Reach ProbeBW
        for (int i = 0; i < 5; i++)
        {
            timeProvider.Advance(TimeSpan.FromMilliseconds(60));
            bbr.OnAckReceived(bytesAcked: 100_000, measuredRtt: TimeSpan.FromMilliseconds(50));
        }

        // Advance time by 11 seconds (exceeding MinRttFilterWindow of 10s)
        timeProvider.Advance(TimeSpan.FromSeconds(11));
        bbr.OnAckReceived(bytesAcked: 5_000, measuredRtt: TimeSpan.FromMilliseconds(80));

        bbr.Phase.Should().Be(BbrPhase.ProbeRTT);
        bbr.GetTargetCongestionWindowBytes().Should().Be(BbrCongestionController.MinCwndBytes);
    }

    [Fact]
    public void UdpTokenBucketPacer_AllowsImmediateSend_WhenTokensAvailable()
    {
        ControllableTimeProvider timeProvider = new();
        UdpTokenBucketPacer pacer = new(
            bbr: null,
            fixedPacingRateBps: 2_000_000L,
            maxBurstCapacityBytes: 16_384,
            maxDelayClampMs: 2.5,
            timeProvider: timeProvider);

        // A 1400-byte datagram with a 16KB bucket should send immediately
        PacingDecision decision = pacer.Evaluate(1400);

        decision.ShouldSendImmediately.Should().BeTrue();
        decision.RecommendedDelay.Should().Be(TimeSpan.Zero);
    }

    [Fact]
    public void UdpTokenBucketPacer_EnforcesMaxDelayClamp_AndNeverExceedsCeiling()
    {
        ControllableTimeProvider timeProvider = new();
        UdpTokenBucketPacer pacer = new(
            bbr: null,
            fixedPacingRateBps: 128_000L, // slow 128 KB/s rate
            maxBurstCapacityBytes: 1000, // small bucket
            maxDelayClampMs: 2.0, // 2ms hard safety ceiling
            timeProvider: timeProvider);

        // Drain tokens
        pacer.Evaluate(1000);

        // Next 5000 byte packet needs a large delay, but pacer MUST clamp it to 2.0ms
        PacingDecision decision = pacer.Evaluate(5000);

        decision.ShouldSendImmediately.Should().BeFalse();
        decision.RecommendedDelay.Should().BeLessOrEqualTo(TimeSpan.FromMilliseconds(2.0));
    }

    [Fact]
    public void UdpTokenBucketPacer_AutoFlushes_WhenQueueBacklogHigh()
    {
        ControllableTimeProvider timeProvider = new();
        UdpTokenBucketPacer pacer = new(
            bbr: null,
            fixedPacingRateBps: 128_000L,
            maxBurstCapacityBytes: 500,
            maxDelayClampMs: 2.0,
            timeProvider: timeProvider);

        // Drain bucket
        pacer.Evaluate(500);

        // Queue 3 packets
        pacer.Evaluate(500);
        pacer.Evaluate(500);
        pacer.Evaluate(500);

        // The 4th packet detects backlog >= 3 and immediately auto-flushes to prevent gameplay lag
        PacingDecision flushDecision = pacer.Evaluate(500);
        flushDecision.ShouldSendImmediately.Should().BeTrue();
        flushDecision.RecommendedDelay.Should().Be(TimeSpan.Zero);
    }
}
