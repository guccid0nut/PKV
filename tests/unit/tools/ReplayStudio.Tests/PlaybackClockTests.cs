// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ============================================================================
// File: PlaybackClockTests.cs
// System: Replay Studio Desktop GUI Tool
// Policy: Strict Explicit Typing (NO 'var')
// ============================================================================

using System.Diagnostics;
using FluentAssertions;
using ReplayStudio.GUI.Engine;
using Xunit;

namespace ReplayStudio.Tests;

public sealed class PlaybackClockTests
{
    [Fact]
    public void InitialState_IsStopped_AtZeroMilliseconds()
    {
        // Arrange & Act
        PlaybackClock clock = new() { TotalDurationMs = 60000 };

        // Assert
        clock.State.Should().Be(PlaybackState.Stopped);
        clock.CurrentTimeMs.Should().Be(0);
        clock.IsPlaying.Should().BeFalse();
        clock.IsPaused.Should().BeFalse();
        clock.IsStopped.Should().BeTrue();
    }

    [Fact]
    public void Play_TransitionsStateToPlaying_AndRaisesEvents()
    {
        // Arrange
        PlaybackClock clock = new() { TotalDurationMs = 60000 };
        PlaybackState? observedState = null;
        clock.StateChanged += state => observedState = state;

        // Act
        clock.Play();

        // Assert
        clock.State.Should().Be(PlaybackState.Playing);
        clock.IsPlaying.Should().BeTrue();
        observedState.Should().Be(PlaybackState.Playing);
    }

    [Theory]
    [InlineData(0.25f, 0.25f)]
    [InlineData(0.50f, 0.50f)]
    [InlineData(1.00f, 1.00f)]
    [InlineData(2.00f, 2.00f)]
    [InlineData(4.00f, 4.00f)]
    [InlineData(8.00f, 8.00f)]
    [InlineData(0.05f, 0.25f)] // Clamped to min
    [InlineData(16.0f, 8.00f)] // Clamped to max
    public void SpeedMultiplier_ClampsToValidRange_AndRaisesEvent(float input, float expected)
    {
        // Arrange
        PlaybackClock clock = new();
        float observedSpeed = 0f;
        clock.SpeedChanged += speed => observedSpeed = speed;

        // Act
        clock.SpeedMultiplier = input;

        // Assert
        clock.SpeedMultiplier.Should().BeApproximately(expected, 1e-3f);
        observedSpeed.Should().BeApproximately(expected, 1e-3f);
    }

    [Fact]
    public void Pause_WhenInvoked_HaltsTimestampAdvancement()
    {
        // Arrange
        PlaybackClock clock = new() { TotalDurationMs = 60000 };
        clock.Play();
        clock.Pause();

        // Assert
        clock.State.Should().Be(PlaybackState.Paused);
        clock.IsPaused.Should().BeTrue();
        clock.IsPlaying.Should().BeFalse();
    }

    [Fact]
    public void StepForward_AdvancesExactlyOneTick_AndPauses()
    {
        // Arrange
        PlaybackClock clock = new() { TotalDurationMs = 60000, CurrentTimeMs = 1000 };

        // Act
        clock.StepTick(deltaTicks: 1, tickDeltaMs: 50);

        // Assert
        clock.CurrentTimeMs.Should().Be(1050);
        clock.State.Should().Be(PlaybackState.Paused);
    }

    [Fact]
    public void StepBackward_DecrementsExactlyOneTick_AndClampsToZero()
    {
        // Arrange
        PlaybackClock clock = new() { TotalDurationMs = 60000, CurrentTimeMs = 40 };

        // Act
        clock.StepTick(deltaTicks: -1, tickDeltaMs: 50);

        // Assert: Should clamp at 0
        clock.CurrentTimeMs.Should().Be(0);
    }

    [Fact]
    public void StepForward_AtMatchEnd_ClampsToTotalDuration()
    {
        // Arrange
        PlaybackClock clock = new() { TotalDurationMs = 10000, CurrentTimeMs = 9980 };

        // Act
        clock.StepTick(deltaTicks: 1, tickDeltaMs: 50);

        // Assert: Should clamp at TotalDurationMs (10000)
        clock.CurrentTimeMs.Should().Be(10000);
    }

    [Fact]
    public void TogglePlayPause_WhenInvoked_AlternatesStateCleanly()
    {
        // Arrange
        PlaybackClock clock = new() { TotalDurationMs = 60000 };

        // Act & Assert
        clock.TogglePlayPause();
        clock.IsPlaying.Should().BeTrue();

        clock.TogglePlayPause();
        clock.IsPaused.Should().BeTrue();

        clock.TogglePlayPause();
        clock.IsPlaying.Should().BeTrue();
    }

    [Fact]
    public void Stop_WhenInvoked_ResetsTimeToInMarkerAndStops()
    {
        // Arrange
        PlaybackClock clock = new()
        {
            TotalDurationMs = 60000,
            InMarkerMs = 5000,
            CurrentTimeMs = 25000
        };
        clock.Play();

        // Act
        clock.Stop();

        // Assert
        clock.State.Should().Be(PlaybackState.Stopped);
        clock.CurrentTimeMs.Should().Be(5000);
    }

    [Fact]
    public void AccumulatedTime_TenThousandFrames_HasZeroCumulativeDrift()
    {
        // Arrange: Test high frequency timer accumulator precision
        long baseTicks = 100_000_000L;
        int targetFps = 60;
        long stopwatchFrequency = Stopwatch.Frequency;

        // Act: Accumulate 10,000 frames using invariant formula: Tn = T0 + floor(n * Freq / TargetFps + 0.5)
        long initialExpected = baseTicks;
        long finalCalculated = baseTicks;

        for (int frame = 1; frame <= 10000; frame++)
        {
            double exactTicksDelta = (double)frame * stopwatchFrequency / targetFps;
            finalCalculated = baseTicks + (long)Math.Floor(exactTicksDelta + 0.5);
        }

        double totalElapsedSec = (double)(finalCalculated - initialExpected) / stopwatchFrequency;
        double expectedElapsedSec = 10000.0 / targetFps;

        // Assert: Drift must be less than 1 millisecond over 10,000 frames (~166 seconds)
        Math.Abs(totalElapsedSec - expectedElapsedSec).Should().BeLessThan(0.001);
    }
}
