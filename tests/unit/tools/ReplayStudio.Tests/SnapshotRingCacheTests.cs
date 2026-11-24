// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ============================================================================
// File: SnapshotRingCacheTests.cs
// System: Replay Studio Desktop GUI Tool
// Policy: Strict Explicit Typing (NO 'var')
// ============================================================================

using System.Numerics;
using FluentAssertions;
using ReplayStudio.GUI.Engine;
using ReplayStudio.GUI.Models;
using Xunit;

namespace ReplayStudio.Tests;

public sealed class SnapshotRingCacheTests
{
    [Fact]
    public void LoadKeyframes_AscendingTimestamps_MaintainsMonotonicOrder()
    {
        // Arrange
        ReplaySnapshotEngine engine = new(ringBufferSize: 32);
        List<GameStateSnapshot> unordered =
        [
            new GameStateSnapshot { TimestampMs = 30000, IsKeyframe = true },
            new GameStateSnapshot { TimestampMs = 10000, IsKeyframe = true },
            new GameStateSnapshot { TimestampMs = 0, IsKeyframe = true },
            new GameStateSnapshot { TimestampMs = 20000, IsKeyframe = true }
        ];

        // Act
        engine.LoadKeyframes(unordered, 30000);

        // Assert
        engine.KeyframeCount.Should().Be(4);
        engine.Keyframes[0].TimestampMs.Should().Be(0);
        engine.Keyframes[1].TimestampMs.Should().Be(10000);
        engine.Keyframes[2].TimestampMs.Should().Be(20000);
        engine.Keyframes[3].TimestampMs.Should().Be(30000);
    }

    [Fact]
    public void SeekNearestKeyframe_ExactTimestamp_ReturnsTargetSnapshot()
    {
        // Arrange
        ReplaySnapshotEngine engine = new();
        List<GameStateSnapshot> keyframes =
        [
            new GameStateSnapshot { TimestampMs = 0, KeyframeTag = "Start" },
            new GameStateSnapshot { TimestampMs = 10000, KeyframeTag = "10s" },
            new GameStateSnapshot { TimestampMs = 20000, KeyframeTag = "20s" }
        ];
        engine.LoadKeyframes(keyframes, 20000);

        // Act
        GameStateSnapshot result = engine.SeekNearestKeyframe(10000);

        // Assert
        result.Should().NotBeNull();
        result.TimestampMs.Should().Be(10000);
        result.KeyframeTag.Should().Be("10s");
    }

    [Fact]
    public void SeekNearestKeyframe_BetweenKeyframes_ReturnsPrecedingSnapshotAndAlpha()
    {
        // Arrange
        ReplaySnapshotEngine engine = new();
        List<GameStateSnapshot> keyframes =
        [
            new GameStateSnapshot { TimestampMs = 0, KeyframeTag = "Start" },
            new GameStateSnapshot { TimestampMs = 10000, KeyframeTag = "10s" },
            new GameStateSnapshot { TimestampMs = 20000, KeyframeTag = "20s" }
        ];
        engine.LoadKeyframes(keyframes, 20000);

        // Act
        GameStateSnapshot nearest = engine.SeekNearestKeyframe(15000);
        (GameStateSnapshot s0, GameStateSnapshot s1, float alpha) = engine.GetSurroundingKeyframes(15000);

        // Assert
        nearest.TimestampMs.Should().Be(10000);
        s0.TimestampMs.Should().Be(10000);
        s1.TimestampMs.Should().Be(20000);
        alpha.Should().BeApproximately(0.5f, 1e-4f);
    }

    [Fact]
    public void SeekNearestKeyframe_TimestampBeforeStart_ClampsToFirstKeyframe()
    {
        // Arrange
        ReplaySnapshotEngine engine = new();
        List<GameStateSnapshot> keyframes =
        [
            new GameStateSnapshot { TimestampMs = 5000, KeyframeTag = "First" },
            new GameStateSnapshot { TimestampMs = 15000, KeyframeTag = "Second" }
        ];
        engine.LoadKeyframes(keyframes, 15000);

        // Act
        GameStateSnapshot nearest = engine.SeekNearestKeyframe(0);
        (GameStateSnapshot s0, GameStateSnapshot s1, float alpha) = engine.GetSurroundingKeyframes(-1000);

        // Assert
        nearest.TimestampMs.Should().Be(5000);
        s0.TimestampMs.Should().Be(5000);
        s1.TimestampMs.Should().Be(5000);
        alpha.Should().Be(0.0f);
    }

    [Fact]
    public void SeekNearestKeyframe_TimestampAfterEnd_ClampsToFinalKeyframe()
    {
        // Arrange
        ReplaySnapshotEngine engine = new();
        List<GameStateSnapshot> keyframes =
        [
            new GameStateSnapshot { TimestampMs = 0, KeyframeTag = "Start" },
            new GameStateSnapshot { TimestampMs = 10000, KeyframeTag = "End" }
        ];
        engine.LoadKeyframes(keyframes, 10000);

        // Act
        GameStateSnapshot nearest = engine.SeekNearestKeyframe(25000);
        (GameStateSnapshot s0, GameStateSnapshot s1, float alpha) = engine.GetSurroundingKeyframes(25000);

        // Assert
        nearest.TimestampMs.Should().Be(10000);
        s0.TimestampMs.Should().Be(10000);
        s1.TimestampMs.Should().Be(10000);
        alpha.Should().Be(0.0f);
    }

    [Fact]
    public void SeekNearestKeyframe_BinarySearch_ExecutesInLogarithmicStepBudget()
    {
        // Arrange: 1024 synthetic keyframes
        ReplaySnapshotEngine engine = new();
        List<GameStateSnapshot> keyframes = new(1024);
        for (int i = 0; i < 1024; i++)
        {
            keyframes.Add(new GameStateSnapshot { TimestampMs = i * 1000 });
        }
        engine.LoadKeyframes(keyframes, 1024000);

        // Act: Seek across random points
        int[] targetQueries = [42150, 789123, 1000, 999999, 500500];
        foreach (int target in targetQueries)
        {
            GameStateSnapshot found = engine.SeekNearestKeyframe(target);
            int expectedMs = (target / 1000) * 1000;
            found.TimestampMs.Should().Be(expectedMs);
        }
    }

    [Fact]
    public void InterpolateHero_LinearLERP_CorrectlyInterpolatesPositionAndStats()
    {
        // Arrange
        HeroSnapshot h0 = new()
        {
            ClientIndex = 0,
            Position = new Vector3(0, 0, 0),
            Health = 1000f,
            MaxHealth = 1000f,
            Mana = 200f,
            MaxMana = 400f,
            Gold = 100
        };

        HeroSnapshot h1 = new()
        {
            ClientIndex = 0,
            Position = new Vector3(100, 200, 300),
            Health = 600f,
            MaxHealth = 1000f,
            Mana = 400f,
            MaxMana = 400f,
            Gold = 500
        };

        HeroSnapshot dest = new();

        // Act & Assert at alpha = 0.5
        ReplaySnapshotEngine.InterpolateHeroInto(h0, h1, 0.5f, dest);
        dest.Position.Should().Be(new Vector3(50, 100, 150));
        dest.Health.Should().BeApproximately(800f, 1e-4f);
        dest.Mana.Should().BeApproximately(300f, 1e-4f);
        dest.Gold.Should().Be(300);

        // Act & Assert at alpha = 0.25
        ReplaySnapshotEngine.InterpolateHeroInto(h0, h1, 0.25f, dest);
        dest.Position.Should().Be(new Vector3(25, 50, 75));
        dest.Health.Should().BeApproximately(900f, 1e-4f);
        dest.Mana.Should().BeApproximately(250f, 1e-4f);
        dest.Gold.Should().Be(200);
    }

    [Fact]
    public void InterpolateHero_AngleWrapAround_TakesShortestAngularArc()
    {
        // Test 1: 350 deg -> 10 deg (shortest arc crosses 0 deg, diff is +20 deg)
        // Midpoint alpha = 0.5 should be 0 deg (or 360 deg)
        float heading1 = ReplaySnapshotEngine.InterpolateHeading(350f, 10f, 0.5f);
        heading1.Should().BeApproximately(0.0f, 1e-3f);

        // Test 2: 10 deg -> 350 deg (shortest arc is -20 deg)
        // Midpoint alpha = 0.5 should be 0 deg
        float heading2 = ReplaySnapshotEngine.InterpolateHeading(10f, 350f, 0.5f);
        heading2.Should().BeApproximately(0.0f, 1e-3f);

        // Test 3: 45 deg -> 135 deg (diff is +90 deg)
        // Midpoint alpha = 0.5 should be 90 deg
        float heading3 = ReplaySnapshotEngine.InterpolateHeading(45f, 135f, 0.5f);
        heading3.Should().BeApproximately(90.0f, 1e-3f);

        // Test 4: 170 deg -> 190 deg (diff is +20 deg)
        // Midpoint alpha = 0.5 should be 180 deg
        float heading4 = ReplaySnapshotEngine.InterpolateHeading(170f, 190f, 0.5f);
        heading4.Should().BeApproximately(180.0f, 1e-3f);
    }

    [Fact]
    public void InterpolateHero_DeathAndAliveTransitions_HandlesGracefully()
    {
        // Arrange: Hero is alive at t0 and dead at t1
        HeroSnapshot h0 = new() { ClientIndex = 1, IsAlive = true, Health = 100f };
        HeroSnapshot h1 = new() { ClientIndex = 1, IsAlive = false, Health = 0f };
        HeroSnapshot dest = new();

        // Act & Assert: At alpha = 0.5, hero is still alive; at alpha = 0.96, hero is dead
        ReplaySnapshotEngine.InterpolateHeroInto(h0, h1, 0.5f, dest);
        dest.IsAlive.Should().BeTrue();
        dest.Health.Should().BeApproximately(50f, 1e-4f);

        ReplaySnapshotEngine.InterpolateHeroInto(h0, h1, 0.98f, dest);
        dest.IsAlive.Should().BeFalse();
    }

    [Fact]
    public void RingBuffer_CircularReuse_DoesNotThrowOnManyInterpolations()
    {
        // Arrange
        ReplaySnapshotEngine engine = new(ringBufferSize: 16);
        List<GameStateSnapshot> keyframes =
        [
            new GameStateSnapshot { TimestampMs = 0 },
            new GameStateSnapshot { TimestampMs = 100000 }
        ];
        engine.LoadKeyframes(keyframes, 100000);

        // Act: Request 1000 interpolated snapshots
        for (int i = 0; i < 1000; i++)
        {
            GameStateSnapshot snap = engine.GetInterpolatedSnapshot(i * 100);
            snap.Should().NotBeNull();
            snap.TimestampMs.Should().Be(i * 100);
        }
    }

    [Fact]
    public void InsertCombatEventSnapshot_HighPriority_DoesNotEvictRegularIntervalKeyframe()
    {
        // Arrange
        ReplaySnapshotEngine engine = new();
        GameStateSnapshot snap0 = new() { TimestampMs = 0 };
        GameStateSnapshot snapFb = new()
        {
            TimestampMs = 4500,
            IsKeyframe = true,
            KeyframeTag = "First Blood",
            ActiveEvents = [new CombatEventMarker { EventType = CombatEventType.FirstBlood, TimestampMs = 4500 }]
        };
        GameStateSnapshot snap1 = new() { TimestampMs = 10000 };

        engine.LoadKeyframes([snap0, snapFb, snap1], 10000);

        // Act
        GameStateSnapshot nearestToFb = engine.SeekNearestKeyframe(4500);
        GameStateSnapshot nearestAfterFb = engine.SeekNearestKeyframe(7000);

        // Assert
        nearestToFb.TimestampMs.Should().Be(4500);
        nearestToFb.KeyframeTag.Should().Be("First Blood");
        nearestAfterFb.TimestampMs.Should().Be(4500);
    }
}
