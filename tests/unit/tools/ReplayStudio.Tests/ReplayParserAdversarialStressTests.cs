// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ============================================================================
// File: M1AdversarialStressTests.cs
// System: Replay Studio Desktop GUI Tool - Unit Tests
// Policy: Strict Explicit Typing (NO 'var')
// Description: Adversarial empirical challenge suite for Milestone M1
// ============================================================================

using System.Numerics;
using FluentAssertions;
using ReplayStudio.GUI.Director;
using ReplayStudio.GUI.Engine;
using ReplayStudio.GUI.Models;
using Xunit;

namespace ReplayStudio.Tests;

public sealed class ReplayParserAdversarialStressTests
{
    // ========================================================================
    // 1. SNAPSHOT RING CACHE & SEEKING STRESS TESTS
    // ========================================================================

    [Theory]
    [InlineData(-1)]
    [InlineData(-500)]
    [InlineData(-100000)]
    [InlineData(int.MinValue)]
    public void SeekNearestKeyframe_NegativeTimestamp_ClampsToFirstSnapshot(int negativeTimeMs)
    {
        // Arrange
        ReplaySnapshotEngine engine = new(ringBufferSize: 64);
        List<GameStateSnapshot> snapshots =
        [
            new GameStateSnapshot { TimestampMs = 0, KeyframeTag = "Start" },
            new GameStateSnapshot { TimestampMs = 10000, KeyframeTag = "10s" },
            new GameStateSnapshot { TimestampMs = 20000, KeyframeTag = "20s" }
        ];
        engine.LoadKeyframes(snapshots, totalDurationMs: 20000);

        // Act
        GameStateSnapshot result = engine.SeekNearestKeyframe(negativeTimeMs);

        // Assert
        result.Should().NotBeNull();
        result.TimestampMs.Should().Be(0);
        result.KeyframeTag.Should().Be("Start");
    }

    [Theory]
    [InlineData(20001)]
    [InlineData(50000)]
    [InlineData(1000000)]
    [InlineData(int.MaxValue)]
    public void SeekNearestKeyframe_PastTotalDuration_ClampsToLastSnapshot(int futureTimeMs)
    {
        // Arrange
        ReplaySnapshotEngine engine = new(ringBufferSize: 64);
        List<GameStateSnapshot> snapshots =
        [
            new GameStateSnapshot { TimestampMs = 0, KeyframeTag = "Start" },
            new GameStateSnapshot { TimestampMs = 10000, KeyframeTag = "10s" },
            new GameStateSnapshot { TimestampMs = 20000, KeyframeTag = "Final" }
        ];
        engine.LoadKeyframes(snapshots, totalDurationMs: 20000);

        // Act
        GameStateSnapshot result = engine.SeekNearestKeyframe(futureTimeMs);

        // Assert
        result.Should().NotBeNull();
        result.TimestampMs.Should().Be(20000);
        result.KeyframeTag.Should().Be("Final");
    }

    [Fact]
    public void SeekNearestKeyframe_EmptySnapshots_AutoGeneratesOriginWithoutThrowing()
    {
        // Arrange
        ReplaySnapshotEngine engine = new(ringBufferSize: 32);
        engine.LoadKeyframes([], totalDurationMs: 0);

        // Act
        GameStateSnapshot resultNegative = engine.SeekNearestKeyframe(-5000);
        GameStateSnapshot resultZero = engine.SeekNearestKeyframe(0);
        GameStateSnapshot resultPositive = engine.SeekNearestKeyframe(50000);

        // Assert
        engine.KeyframeCount.Should().Be(1);
        resultNegative.TimestampMs.Should().Be(0);
        resultZero.TimestampMs.Should().Be(0);
        resultPositive.TimestampMs.Should().Be(0);
    }

    [Fact]
    public void SeekNearestKeyframe_SingleSnapshot_AlwaysReturnsThatSnapshot()
    {
        // Arrange
        ReplaySnapshotEngine engine = new(ringBufferSize: 32);
        List<GameStateSnapshot> singleList =
        [
            new GameStateSnapshot { TimestampMs = 5000, KeyframeTag = "OnlyOne" }
        ];
        engine.LoadKeyframes(singleList, totalDurationMs: 10000);

        // Act & Assert
        engine.SeekNearestKeyframe(-100).KeyframeTag.Should().Be("OnlyOne");
        engine.SeekNearestKeyframe(5000).KeyframeTag.Should().Be("OnlyOne");
        engine.SeekNearestKeyframe(99999).KeyframeTag.Should().Be("OnlyOne");

        (GameStateSnapshot s0, GameStateSnapshot s1, float alpha) = engine.GetSurroundingKeyframes(7500);
        s0.KeyframeTag.Should().Be("OnlyOne");
        s1.KeyframeTag.Should().Be("OnlyOne");
        alpha.Should().Be(0.0f);
    }

    [Fact]
    public void SeekNearestKeyframe_HighFrequencyRandomSeeks_ZeroExceptionsAndDeterministic()
    {
        // Arrange
        ReplaySnapshotEngine engine = new(ringBufferSize: 128);
        List<GameStateSnapshot> snapshots = new();
        for (int t = 0; t <= 100000; t += 1000)
        {
            snapshots.Add(new GameStateSnapshot { TimestampMs = t, KeyframeTag = $"T_{t}" });
        }
        engine.LoadKeyframes(snapshots, totalDurationMs: 100000);

        Random rng = new(42);

        // Act & Assert: 20,000 random seeks across negative, in-range, and over-range values
        for (int i = 0; i < 20000; i++)
        {
            int queryTime = rng.Next(-20000, 150000);
            GameStateSnapshot match = engine.SeekNearestKeyframe(queryTime);
            match.Should().NotBeNull();

            if (queryTime <= 0)
            {
                match.TimestampMs.Should().Be(0);
            }
            else if (queryTime >= 100000)
            {
                match.TimestampMs.Should().Be(100000);
            }
            else
            {
                match.TimestampMs.Should().BeLessThanOrEqualTo(queryTime);
                match.TimestampMs.Should().BeGreaterThan(queryTime - 1000);
            }
        }
    }

    [Fact]
    public void GetSurroundingKeyframes_IdenticalTimestamps_HandlesGracefullyWithoutDivByZero()
    {
        // Arrange
        ReplaySnapshotEngine engine = new(ringBufferSize: 64);
        List<GameStateSnapshot> snapshots =
        [
            new GameStateSnapshot { TimestampMs = 1000, KeyframeTag = "EventA" },
            new GameStateSnapshot { TimestampMs = 1000, KeyframeTag = "EventB" }
        ];
        engine.LoadKeyframes(snapshots, totalDurationMs: 2000);

        // Act
        (GameStateSnapshot s0, GameStateSnapshot s1, float alpha) = engine.GetSurroundingKeyframes(1000);

        // Assert
        s0.Should().NotBeNull();
        s1.Should().NotBeNull();
        alpha.Should().Be(0.0f);
    }

    [Trait("Category", "Manual")]
    [Fact]
    public void GetInterpolatedSnapshot_RingBufferHighFrequencyReuse_DoesNotCorruptOrThrow()
    {
        if (!NexusTestEnvironment.IsManualTestEnabled) return;
        // Arrange
        ReplaySnapshotEngine engine = new(ringBufferSize: 16); // Small ring buffer to force thousands of wraps
        List<GameStateSnapshot> snapshots =
        [
            new GameStateSnapshot { TimestampMs = 0, ScoreLegion = 0, NetWorthAdvantage = 0 },
            new GameStateSnapshot { TimestampMs = 60000, ScoreLegion = 10, NetWorthAdvantage = 5000 }
        ];
        for (int i = 0; i < 10; i++)
        {
            snapshots[0].Heroes[i] = new HeroSnapshot { ClientIndex = i, Health = 500f, MaxHealth = 500f, Position = new Vector3(0, 0, 0) };
            snapshots[1].Heroes[i] = new HeroSnapshot { ClientIndex = i, Health = 1000f, MaxHealth = 1000f, Position = new Vector3(100, 100, 10) };
        }
        engine.LoadKeyframes(snapshots, totalDurationMs: 60000);

        // Act: Perform 50,000 sequential interpolations
        for (int step = 0; step < 50000; step++)
        {
            int targetMs = (step * 37) % 60000;
            GameStateSnapshot interpolated = engine.GetInterpolatedSnapshot(targetMs);

            interpolated.Should().NotBeNull();
            interpolated.TimestampMs.Should().Be(targetMs);
            interpolated.Heroes[0].Position.X.Should().BeGreaterThanOrEqualTo(0.0f).And.BeLessThanOrEqualTo(100.0f);
        }
    }

    [Fact]
    public void LoadKeyframes_UnorderedInputList_AutomaticallySortedMonotonically()
    {
        // Arrange
        ReplaySnapshotEngine engine = new();
        List<GameStateSnapshot> unordered =
        [
            new GameStateSnapshot { TimestampMs = 30000, KeyframeTag = "Third" },
            new GameStateSnapshot { TimestampMs = 10000, KeyframeTag = "First" },
            new GameStateSnapshot { TimestampMs = 40000, KeyframeTag = "Fourth" },
            new GameStateSnapshot { TimestampMs = 20000, KeyframeTag = "Second" }
        ];

        // Act
        engine.LoadKeyframes(unordered, totalDurationMs: 40000);

        // Assert
        engine.Keyframes[0].TimestampMs.Should().Be(10000);
        engine.Keyframes[1].TimestampMs.Should().Be(20000);
        engine.Keyframes[2].TimestampMs.Should().Be(30000);
        engine.Keyframes[3].TimestampMs.Should().Be(40000);
    }

    // ========================================================================
    // 2. ANGLE NORMALIZATION & HEADING ARITHMETIC TESTS
    // ========================================================================

    [Theory]
    [InlineData(350f, 10f, 0.5f, 0f)]     // Across 0° boundary clockwise (span 20°, mid is 0°)
    [InlineData(10f, 350f, 0.5f, 0f)]     // Across 0° boundary counter-clockwise (span -20°, mid is 0°)
    [InlineData(359f, 1f, 0.5f, 0f)]      // 2° span across 0°
    [InlineData(1f, 359f, 0.5f, 0f)]      // -2° span across 0°
    [InlineData(340f, 20f, 0.25f, 350f)]  // Quarter way: 340 -> 20 (delta +40, +10 => 350)
    [InlineData(340f, 20f, 0.75f, 10f)]   // Three quarters: 340 -> 20 (delta +40, +30 => 10)
    public void InterpolateHeading_TransitionsAcross0And360_TakesShortestArc(float a0, float a1, float alpha, float expected)
    {
        // Act
        float result = ReplaySnapshotEngine.InterpolateHeading(a0, a1, alpha);

        // Assert
        MathF.Abs(result - expected).Should().BeLessThan(0.01f);
    }

    [Theory]
    [InlineData(170f, -170f, 0.5f, 180f)]  // 170° to -170° (same as 170° to 190°), mid is 180°
    [InlineData(-170f, 170f, 0.5f, 180f)]  // -170° to 170° (same as 190° to 170°), mid is 180°
    [InlineData(179f, -179f, 0.5f, 180f)]  // 2° delta across 180°
    [InlineData(-179f, 179f, 0.5f, 180f)]  // -2° delta across 180°
    public void InterpolateHeading_TransitionsAcross180AndMinus180_TakesShortestArc(float a0, float a1, float alpha, float expected)
    {
        // Act
        float result = ReplaySnapshotEngine.InterpolateHeading(a0, a1, alpha);

        // Normalize result to [0, 360)
        float normalized = ((result % 360f) + 360f) % 360f;

        // Assert
        MathF.Abs(normalized - expected).Should().BeLessThan(0.01f);
    }

    [Theory]
    [InlineData(10800f, 10810f, 0.5f, 5f)]    // 30 rotations + 0° to 30 rotations + 10°, mid is 5°
    [InlineData(36000f, 36010f, 0.5f, 5f)]    // 100 rotations
    [InlineData(10000f, 10020f, 0.5f, 290f)]  // 10000 % 360 = 280, mid of 280 and 300 is 290
    public void InterpolateHeading_ExtremePositiveAngles_EvaluatesCorrectly(float a0, float a1, float alpha, float expected)
    {
        // Act
        float result = ReplaySnapshotEngine.InterpolateHeading(a0, a1, alpha);
        float normalized = ((result % 360f) + 360f) % 360f;

        // Assert
        MathF.Abs(normalized - expected).Should().BeLessThan(0.01f);
    }

    [Theory]
    [InlineData(-10800f, -10790f, 0.5f, 5f)]   // Extreme negative: -10800 = 0 deg, -10790 = 10 deg, mid = 5 deg
    [InlineData(-720f, -710f, 0.5f, 5f)]       // -720 = 0 deg, -710 = 10 deg, mid = 5 deg
    [InlineData(-360f, -340f, 0.5f, 10f)]      // -360 = 0 deg, -340 = 20 deg, mid = 10 deg
    public void InterpolateHeading_ExtremeNegativeAngles_EvaluatesCorrectly(float a0, float a1, float alpha, float expected)
    {
        // Act
        float result = ReplaySnapshotEngine.InterpolateHeading(a0, a1, alpha);
        float normalized = ((result % 360f) + 360f) % 360f;

        // Assert
        MathF.Abs(normalized - expected).Should().BeLessThan(0.01f);
    }

    [Fact]
    public void InterpolateHeading_FullContinuousSweep_NeverProducesDiscontinuitySpikes()
    {
        // Sweeping from 350° to 10° in 1000 steps
        float prevAngle = 350f;
        for (int i = 1; i <= 1000; i++)
        {
            float alpha = (float)i / 1000f;
            float heading = ReplaySnapshotEngine.InterpolateHeading(350f, 10f, alpha);
            float normalized = ((heading % 360f) + 360f) % 360f;

            // Difference between consecutive fractional steps must never exceed 0.1 degree
            float stepDelta = ((normalized - prevAngle + 540f) % 360f) - 180f;
            MathF.Abs(stepDelta).Should().BeLessThan(0.1f);
            prevAngle = normalized;
        }
    }

    // ========================================================================
    // 3. PLAYBACK CLOCK BOUNDARY & SPEED MULTIPLIER TESTS
    // ========================================================================

    [Fact]
    public void PlaybackClock_StepTickBackward_AtZero_ClampsToZeroAndPauses()
    {
        // Arrange
        PlaybackClock clock = new() { TotalDurationMs = 60000, CurrentTimeMs = 0 };

        // Act
        clock.StepTick(deltaTicks: -1);

        // Assert
        clock.CurrentTimeMs.Should().Be(0);
        clock.State.Should().Be(PlaybackState.Paused);
    }

    [Fact]
    public void PlaybackClock_StepTickForward_AtEnd_ClampsToTotalDurationAndPauses()
    {
        // Arrange
        PlaybackClock clock = new() { TotalDurationMs = 60000, CurrentTimeMs = 60000 };

        // Act
        clock.StepTick(deltaTicks: +1);

        // Assert
        clock.CurrentTimeMs.Should().Be(60000);
        clock.State.Should().Be(PlaybackState.Paused);
    }

    [Fact]
    public void PlaybackClock_StepTick_WithLargeDelta_ClampsWithinBounds()
    {
        // Arrange
        PlaybackClock clock = new() { TotalDurationMs = 5000, CurrentTimeMs = 2500 };

        // Act & Assert: +1000 ticks (+50,000ms)
        clock.StepTick(deltaTicks: 1000);
        clock.CurrentTimeMs.Should().Be(5000);

        // Act & Assert: -1000 ticks (-50,000ms)
        clock.StepTick(deltaTicks: -1000);
        clock.CurrentTimeMs.Should().Be(0);
    }

    [Theory]
    [InlineData(-100f, 0.25f)]
    [InlineData(0.0f, 0.25f)]
    [InlineData(0.001f, 0.25f)]
    [InlineData(100f, 8.0f)]
    [InlineData(10000f, 8.0f)]
    [InlineData(float.MaxValue, 8.0f)]
    public void PlaybackClock_SpeedMultiplier_ExtremeLowAndHigh_ClampedToValidBounds(float inputSpeed, float expectedSpeed)
    {
        // Arrange
        PlaybackClock clock = new();

        // Act
        clock.SpeedMultiplier = inputSpeed;

        // Assert
        clock.SpeedMultiplier.Should().Be(expectedSpeed);
    }

    [Fact]
    public void PlaybackClock_Update_LoopPlaybackEnabled_WrapsToInMarker()
    {
        // Arrange
        PlaybackClock clock = new()
        {
            TotalDurationMs = 10000,
            InMarkerMs = 2000,
            OutMarkerMs = 8000,
            LoopPlayback = true,
            CurrentTimeMs = 7990
        };
        clock.Play();

        // Act: Simulate elapsed wall-clock past 8000ms
        // Advance time manually by stepping or setting current time
        clock.CurrentTimeMs = 8005;
        clock.Update();

        // Assert
        clock.CurrentTimeMs.Should().BeInRange(2000, 2100);
        clock.IsPlaying.Should().BeTrue();
    }

    [Fact]
    public void PlaybackClock_Update_ZeroDurationMatch_DoesNotInfiniteLoopOrThrow()
    {
        // Arrange
        PlaybackClock clock = new() { TotalDurationMs = 0 };

        // Act
        clock.Play();
        clock.Update();

        // Assert
        clock.CurrentTimeMs.Should().Be(0);
    }

    [Fact]
    public void PlaybackClock_CurrentTimeMs_NegativeAndOverflowValues_ClampedDeterministically()
    {
        // Arrange
        PlaybackClock clock = new() { TotalDurationMs = 30000 };

        // Act & Assert
        clock.CurrentTimeMs = -5000;
        clock.CurrentTimeMs.Should().Be(0);

        clock.CurrentTimeMs = 100000;
        clock.CurrentTimeMs.Should().Be(30000);
    }

    [Fact]
    public void PlaybackClock_RapidPlayPauseToggle_MaintainsStateIntegrity()
    {
        // Arrange
        PlaybackClock clock = new() { TotalDurationMs = 60000 };

        // Act
        for (int i = 0; i < 1000; i++)
        {
            clock.TogglePlayPause();
            if (i % 2 == 0)
            {
                clock.IsPlaying.Should().BeTrue();
            }
            else
            {
                clock.IsPaused.Should().BeTrue();
            }
        }
    }

    // ========================================================================
    // 4. CAMERA DIRECTOR & SPLINE ADVERSARIAL TESTS
    // ========================================================================

    [Theory]
    [InlineData(10000f)]
    [InlineData(-10000f)]
    [InlineData(360000f)]
    public void CameraDirector_PlayerPov_ExtremeHeadings_GeneratesValidFiniteMatrices(float extremeHeading)
    {
        // Arrange
        CameraDirector director = new() { Mode = CameraMode.PlayerPov };
        Vector3 heroPos = new(10f, 20f, 1f);

        // Act
        CameraState state = director.Update(
            dt: 0.016f,
            heroPos: heroPos,
            heroHeadingDeg: extremeHeading,
            timestampMs: 5000,
            aspectRatio: 16f / 9f);

        // Assert
        float.IsFinite(state.Position.X).Should().BeTrue();
        float.IsFinite(state.Position.Y).Should().BeTrue();
        float.IsFinite(state.Position.Z).Should().BeTrue();
        float.IsFinite(state.ViewMatrix.M11).Should().BeTrue();
        float.IsFinite(state.ProjectionMatrix.M11).Should().BeTrue();
    }

    [Theory]
    [InlineData(-180f, -89f)]
    [InlineData(180f, 89f)]
    [InlineData(-90f, -89f)]
    [InlineData(90f, 89f)]
    [InlineData(1000f, 89f)]
    public void CameraDirector_FreeSpectator_ExtremePitches_ClampedToAvoidGimbalLock(float inputPitch, float expectedClampedPitch)
    {
        // Arrange
        CameraDirector director = new() { Mode = CameraMode.FreeSpectator };
        director.SetFreeSpectatorOrientation(yawDeg: 45f, pitchDeg: inputPitch);

        // Act
        CameraState state = director.Update(
            dt: 0.016f,
            heroPos: Vector3.Zero,
            heroHeadingDeg: 0f,
            timestampMs: 0,
            aspectRatio: 16f / 9f);

        // Assert
        director.FreePitchDeg.Should().Be(expectedClampedPitch);
        state.Up.Length().Should().BeApproximately(1.0f, 0.01f);
        state.ViewMatrix.M44.Should().Be(1.0f);
    }

    [Theory]
    [InlineData(0.0f)]
    [InlineData(-1.0f)]
    [InlineData(-0.016f)]
    public void CameraDirector_Update_ZeroAndNegativeDeltaTime_HandlesGracefullyWithoutNaN(float dt)
    {
        // Arrange
        CameraDirector director = new() { Mode = CameraMode.PlayerPov };

        // Act
        CameraState state = director.Update(
            dt: dt,
            heroPos: new Vector3(5, 5, 1),
            heroHeadingDeg: 90f,
            timestampMs: 1000,
            aspectRatio: 1.777f);

        // Assert
        float.IsNaN(state.Position.X).Should().BeFalse();
        float.IsNaN(state.ViewMatrix.M11).Should().BeFalse();
    }

    [Fact]
    public void CatmullRomSpline_HighFrequencyStress_10000Evaluations_EvaluatesWithinBounds()
    {
        // Arrange
        CatmullRomSpline spline = new();
        spline.AddNode(new DirectorNode { TimestampMs = 0, Position = new Vector3(0, 0, 10), LookAtTarget = Vector3.Zero });
        spline.AddNode(new DirectorNode { TimestampMs = 10000, Position = new Vector3(50, 50, 20), LookAtTarget = new Vector3(25, 25, 0) });
        spline.AddNode(new DirectorNode { TimestampMs = 20000, Position = new Vector3(100, 0, 15), LookAtTarget = new Vector3(50, 0, 0) });

        // Act & Assert
        for (int t = -5000; t <= 25000; t += 3)
        {
            (Vector3 pos, Vector3 target, Quaternion rot, float fov) = spline.Evaluate(t);
            float.IsFinite(pos.X).Should().BeTrue();
            float.IsFinite(pos.Y).Should().BeTrue();
            float.IsFinite(pos.Z).Should().BeTrue();
            float.IsFinite(rot.W).Should().BeTrue();
            fov.Should().BeInRange(20.0f, 120.0f);
        }
    }

    [Fact]
    public void MapEnvironmentManager_ProceduralSampling_ExtremeCoordinates_ReturnsFiniteHeightsAndNormals()
    {
        // Arrange
        MapEnvironmentManager manager = new();

        // Act
        float hFarOut = manager.SampleHeight(-99999f, 99999f);
        Vector3 nFarOut = manager.SampleNormal(-99999f, 99999f);

        // Assert
        float.IsFinite(hFarOut).Should().BeTrue();
        float.IsFinite(nFarOut.X).Should().BeTrue();
        float.IsFinite(nFarOut.Y).Should().BeTrue();
        float.IsFinite(nFarOut.Z).Should().BeTrue();
        nFarOut.Length().Should().BeApproximately(1.0f, 0.05f);
    }
}
