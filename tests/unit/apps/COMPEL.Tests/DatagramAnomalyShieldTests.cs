// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ============================================================================
// File: DatagramAnomalyShieldTests.cs
// System: COMPEL Tests - Datagram Anomaly Shield & Burst Flood Guard Tests
// Policy: Strict Explicit Typing (NO 'var')
// ============================================================================

using COMPEL.Services.Proxy;
using COMPEL.Services.Security;
using FluentAssertions;
using Xunit;

namespace COMPEL.Tests;

public sealed class DatagramAnomalyShieldTests
{
    [Fact]
    public void Evaluate_SequentialPackets_AdmittedSuccessfully()
    {
        // Arrange
        ControllableTimeProvider timeProvider = new();
        DatagramAnomalyShield shield = new(timeProvider);

        // Act & Assert
        for (uint seq = 1; seq <= 20; seq++)
        {
            timeProvider.Advance(TimeSpan.FromMilliseconds(5));
            AnomalyEvaluationResult result = shield.Evaluate(seq, challenge: 100);
            result.IsAdmitted.Should().BeTrue();
            result.ViolationWeight.Should().Be(0);
        }
    }

    [Fact]
    public void Evaluate_ReplayPacket_DroppedWithDuplicateWeight()
    {
        // Arrange
        ControllableTimeProvider timeProvider = new();
        DatagramAnomalyShield shield = new(timeProvider);

        shield.Evaluate(10, challenge: 100).IsAdmitted.Should().BeTrue();

        // Act - Send duplicate
        AnomalyEvaluationResult replay = shield.Evaluate(10, challenge: 100);

        // Assert
        replay.IsAdmitted.Should().BeFalse();
        replay.ViolationWeight.Should().Be(ViolationScoreContainer.DuplicateViolationWeight);
        replay.DropReason.Should().Contain("Duplicate");
    }

    [Fact]
    public void Evaluate_StaleSequence_DroppedWithTooShortWeight()
    {
        // Arrange
        ControllableTimeProvider timeProvider = new();
        DatagramAnomalyShield shield = new(timeProvider);

        shield.Evaluate(100, challenge: 100).IsAdmitted.Should().BeTrue();

        // Act - Send packet older than 64 window (e.g. seq 30)
        AnomalyEvaluationResult stale = shield.Evaluate(30, challenge: 100);

        // Assert
        stale.IsAdmitted.Should().BeFalse();
        stale.ViolationWeight.Should().Be(ViolationScoreContainer.TooShortViolationWeight);
        stale.DropReason.Should().Contain("Stale");
    }

    [Fact]
    public void Evaluate_BurstFlood_DroppedWithRateLimitWeight()
    {
        // Arrange: Burst limit of 10 packets per 10ms
        ControllableTimeProvider timeProvider = new();
        DatagramAnomalyShield shield = new(
            timeProvider,
            burstLimit: 10,
            burstWindow: TimeSpan.FromMilliseconds(10));

        // Act: Send 10 packets within 1ms -> all admitted
        for (uint seq = 1; seq <= 10; seq++)
        {
            shield.Evaluate(seq, challenge: 100).IsAdmitted.Should().BeTrue();
        }

        // 11th packet in the same 10ms window triggers burst flood drop
        AnomalyEvaluationResult burstDrop = shield.Evaluate(11, challenge: 100);
        burstDrop.IsAdmitted.Should().BeFalse();
        burstDrop.ViolationWeight.Should().Be(ViolationScoreContainer.RateLimitViolationWeight);
        burstDrop.DropReason.Should().Contain("Burst Flood");

        // Advance past the 10ms burst window
        timeProvider.Advance(TimeSpan.FromMilliseconds(15));

        // New packets should now be admitted again
        shield.Evaluate(12, challenge: 100).IsAdmitted.Should().BeTrue();
    }

    [Fact]
    public void Evaluate_ChallengeRotation_ResetsSequenceWindow()
    {
        // Arrange
        ControllableTimeProvider timeProvider = new();
        DatagramAnomalyShield shield = new(timeProvider);

        // Session on Challenge 100 reaches counter 500
        shield.Evaluate(500, challenge: 100).IsAdmitted.Should().BeTrue();

        // Server rotates to Challenge 200, client resets counter to 1
        AnomalyEvaluationResult rotatedResult = shield.Evaluate(1, challenge: 200);

        // Must be admitted and NOT treated as stale
        rotatedResult.IsAdmitted.Should().BeTrue();
        shield.MaxSequence.Should().Be(1);
    }
}
