// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ============================================================================
// File: AdaptiveDeltaRateLimiterTests.cs
// System: Kongor Core Tests - Adaptive Delta Rate-Limiter Unit Tests
// Policy: Strict Explicit Typing (NO 'var')
// ============================================================================

using ASPIRE.Common.Netcode;
using FluentAssertions;
using Xunit;

namespace Kongor.Core.Tests.Netcode;

public sealed class AdaptiveDeltaRateLimiterTests
{
    [Fact]
    public void EvaluateCongestion_NominalConditions_ReturnsNominal()
    {
        CongestionLevel level = AdaptiveDeltaRateLimiter.EvaluateCongestion(
            inflightBytes: 5_000,
            targetCwndBytes: 20_000,
            recentLossRate: 0.00,
            currentRtt: TimeSpan.FromMilliseconds(45),
            minRtt: TimeSpan.FromMilliseconds(40));

        level.Should().Be(CongestionLevel.Nominal);
    }

    [Fact]
    public void EvaluateCongestion_ModerateElevatedLossOrInflight_ReturnsModerate()
    {
        // 1.5% loss triggers Moderate
        CongestionLevel levelByLoss = AdaptiveDeltaRateLimiter.EvaluateCongestion(
            inflightBytes: 5_000,
            targetCwndBytes: 20_000,
            recentLossRate: 0.015,
            currentRtt: TimeSpan.FromMilliseconds(40),
            minRtt: TimeSpan.FromMilliseconds(40));

        levelByLoss.Should().Be(CongestionLevel.Moderate);

        // Inflight at 80% cwnd triggers Moderate
        CongestionLevel levelByInflight = AdaptiveDeltaRateLimiter.EvaluateCongestion(
            inflightBytes: 16_000,
            targetCwndBytes: 20_000,
            recentLossRate: 0.00,
            currentRtt: TimeSpan.FromMilliseconds(40),
            minRtt: TimeSpan.FromMilliseconds(40));

        levelByInflight.Should().Be(CongestionLevel.Moderate);
    }

    [Fact]
    public void EvaluateCongestion_SevereHighLossOrCwndSaturation_ReturnsSevere()
    {
        // 4% loss triggers Severe
        CongestionLevel level = AdaptiveDeltaRateLimiter.EvaluateCongestion(
            inflightBytes: 10_000,
            targetCwndBytes: 20_000,
            recentLossRate: 0.04,
            currentRtt: TimeSpan.FromMilliseconds(40),
            minRtt: TimeSpan.FromMilliseconds(40));

        level.Should().Be(CongestionLevel.Severe);
    }

    [Fact]
    public void EvaluateCongestion_CriticalLossOrOverCwnd_ReturnsCritical()
    {
        // 9% loss triggers Critical
        CongestionLevel levelByLoss = AdaptiveDeltaRateLimiter.EvaluateCongestion(
            inflightBytes: 5_000,
            targetCwndBytes: 20_000,
            recentLossRate: 0.09,
            currentRtt: TimeSpan.FromMilliseconds(40),
            minRtt: TimeSpan.FromMilliseconds(40));

        levelByLoss.Should().Be(CongestionLevel.Critical);

        // Inflight >= targetCwnd triggers Critical
        CongestionLevel levelByInflight = AdaptiveDeltaRateLimiter.EvaluateCongestion(
            inflightBytes: 25_000,
            targetCwndBytes: 20_000,
            recentLossRate: 0.00,
            currentRtt: TimeSpan.FromMilliseconds(40),
            minRtt: TimeSpan.FromMilliseconds(40));

        levelByInflight.Should().Be(CongestionLevel.Critical);
    }

    [Fact]
    public void FilterFrame_Nominal_PreservesAllEntitiesAndComponents()
    {
        SnapshotFrameDelta frame = CreateSampleFrame();
        int initialCount = frame.Entities.Count;

        SnapshotFrameDelta filtered = AdaptiveDeltaRateLimiter.FilterFrame(
            frame,
            CongestionLevel.Nominal,
            maxPayloadBytes: 1472,
            currentTick: 100);

        filtered.Entities.Count.Should().Be(initialCount);
    }

    [Fact]
    public void FilterFrame_Moderate_DownsamplesCreeps_AndStripsNonHeroAnimations()
    {
        SnapshotFrameDelta frame = CreateSampleFrame();

        SnapshotFrameDelta filtered = AdaptiveDeltaRateLimiter.FilterFrame(
            frame,
            CongestionLevel.Moderate,
            maxPayloadBytes: 1472,
            currentTick: 100);

        // Heroes are fully preserved (including animations)
        SnapshotEntityDelta hero = filtered.Entities.First(e => e.EntityType == AdaptiveDeltaRateLimiter.EntityTypeHero);
        hero.ChangedMask.HasFlag(DeltaFieldMask.Animation).Should().BeTrue();

        // Creeps that are retained must have animation mask stripped
        List<SnapshotEntityDelta> creeps = filtered.Entities.Where(e => e.EntityType == AdaptiveDeltaRateLimiter.EntityTypeCreep).ToList();
        creeps.Should().NotBeEmpty();
        foreach (SnapshotEntityDelta creep in creeps)
        {
            creep.ChangedMask.HasFlag(DeltaFieldMask.Animation).Should().BeFalse();
        }
    }

    [Fact]
    public void FilterFrame_Severe_ShedsLowPriorityCosmetics_AndSendsCreepCoordinatesOnly()
    {
        SnapshotFrameDelta frame = CreateSampleFrame();

        SnapshotFrameDelta filtered = AdaptiveDeltaRateLimiter.FilterFrame(
            frame,
            CongestionLevel.Severe,
            maxPayloadBytes: 1472,
            currentTick: 100);

        // All cosmetic particles must be completely dropped
        filtered.Entities.Any(e => e.EntityType == AdaptiveDeltaRateLimiter.EntityTypeCosmetic).Should().BeFalse();

        // Creeps must only retain Coordinates
        List<SnapshotEntityDelta> creeps = filtered.Entities.Where(e => e.EntityType == AdaptiveDeltaRateLimiter.EntityTypeCreep).ToList();
        foreach (SnapshotEntityDelta creep in creeps)
        {
            creep.ChangedMask.Should().Be(DeltaFieldMask.Coordinates);
        }

        // Heroes still retain full combat & coordinates
        SnapshotEntityDelta hero = filtered.Entities.First(e => e.EntityType == AdaptiveDeltaRateLimiter.EntityTypeHero);
        hero.ChangedMask.HasFlag(DeltaFieldMask.Coordinates).Should().BeTrue();
        hero.ChangedMask.HasFlag(DeltaFieldMask.Combat).Should().BeTrue();
    }

    [Fact]
    public void FilterFrame_Critical_VIPHeroMode_PreservesHeroesAndHealthOnly()
    {
        SnapshotFrameDelta frame = CreateSampleFrame();

        SnapshotFrameDelta filtered = AdaptiveDeltaRateLimiter.FilterFrame(
            frame,
            CongestionLevel.Critical,
            maxPayloadBytes: 1472,
            currentTick: 100);

        // No creeps, no cosmetics
        filtered.Entities.Any(e => e.EntityType == AdaptiveDeltaRateLimiter.EntityTypeCreep).Should().BeFalse();
        filtered.Entities.Any(e => e.EntityType == AdaptiveDeltaRateLimiter.EntityTypeCosmetic).Should().BeFalse();

        // Only Heroes and Defense Buildings exist
        foreach (SnapshotEntityDelta entity in filtered.Entities)
        {
            entity.EntityType.Should().BeOneOf(
                AdaptiveDeltaRateLimiter.EntityTypeHero,
                AdaptiveDeltaRateLimiter.EntityTypeBuilding);

            if (entity.EntityType == AdaptiveDeltaRateLimiter.EntityTypeHero)
            {
                entity.ChangedMask.Should().Be(DeltaFieldMask.Coordinates | DeltaFieldMask.Combat);
            }
        }
    }

    [Fact]
    public void FilterFrame_MTUExceeded_StrictlyClampsOutputToTargetPayloadBudget()
    {
        SnapshotFrameDelta bigFrame = new()
        {
            Header = new SnapshotFrameHeader { FrameSequence = 1, ServerTick = 500 },
            Entities = []
        };

        // Add 5 Heroes
        for (ushort i = 1; i <= 5; i++)
        {
            bigFrame.Entities.Add(new SnapshotEntityDelta
            {
                EntityId = i,
                EntityType = AdaptiveDeltaRateLimiter.EntityTypeHero,
                ChangedMask = DeltaFieldMask.Coordinates | DeltaFieldMask.Combat
            });
        }

        // Add 40 Creeps
        for (ushort i = 10; i < 50; i++)
        {
            bigFrame.Entities.Add(new SnapshotEntityDelta
            {
                EntityId = i,
                EntityType = AdaptiveDeltaRateLimiter.EntityTypeCreep,
                ChangedMask = DeltaFieldMask.FullState
            });
        }

        // Add 20 Cosmetics
        for (ushort i = 100; i < 120; i++)
        {
            bigFrame.Entities.Add(new SnapshotEntityDelta
            {
                EntityId = i,
                EntityType = AdaptiveDeltaRateLimiter.EntityTypeCosmetic,
                ChangedMask = DeltaFieldMask.Coordinates | DeltaFieldMask.Animation
            });
        }

        ushort smallMtu = 1200;
        SnapshotFrameDelta clamped = AdaptiveDeltaRateLimiter.FilterFrame(
            bigFrame,
            CongestionLevel.Nominal,
            maxPayloadBytes: smallMtu,
            currentTick: 100);

        // Serialized size must strictly fit within smallMtu
        clamped.GetSerializedSize().Should().BeLessThanOrEqualTo(smallMtu);

        // All 5 heroes must still be preserved
        int heroCount = clamped.Entities.Count(e => e.EntityType == AdaptiveDeltaRateLimiter.EntityTypeHero);
        heroCount.Should().Be(5);
    }

    private static SnapshotFrameDelta CreateSampleFrame()
    {
        SnapshotFrameDelta frame = new()
        {
            Header = new SnapshotFrameHeader
            {
                FrameSequence = 10,
                ServerTick = 1000,
                ClientAckSequence = 9,
                EntityCount = 0
            },
            Entities = []
        };

        // 2 Heroes
        frame.Entities.Add(new SnapshotEntityDelta
        {
            EntityId = 1,
            EntityType = AdaptiveDeltaRateLimiter.EntityTypeHero,
            ChangedMask = DeltaFieldMask.Coordinates | DeltaFieldMask.Combat | DeltaFieldMask.Animation
        });
        frame.Entities.Add(new SnapshotEntityDelta
        {
            EntityId = 2,
            EntityType = AdaptiveDeltaRateLimiter.EntityTypeHero,
            ChangedMask = DeltaFieldMask.Coordinates | DeltaFieldMask.Combat | DeltaFieldMask.Animation
        });

        // 4 Creeps
        for (ushort i = 10; i < 14; i++)
        {
            frame.Entities.Add(new SnapshotEntityDelta
            {
                EntityId = i,
                EntityType = AdaptiveDeltaRateLimiter.EntityTypeCreep,
                ChangedMask = DeltaFieldMask.Coordinates | DeltaFieldMask.Combat | DeltaFieldMask.Animation
            });
        }

        // 1 Building
        frame.Entities.Add(new SnapshotEntityDelta
        {
            EntityId = 20,
            EntityType = AdaptiveDeltaRateLimiter.EntityTypeBuilding,
            ChangedMask = DeltaFieldMask.Combat
        });

        // 3 Cosmetics
        for (ushort i = 50; i < 53; i++)
        {
            frame.Entities.Add(new SnapshotEntityDelta
            {
                EntityId = i,
                EntityType = AdaptiveDeltaRateLimiter.EntityTypeCosmetic,
                ChangedMask = DeltaFieldMask.Coordinates | DeltaFieldMask.Animation
            });
        }

        frame.Header.EntityCount = (ushort)frame.Entities.Count;
        return frame;
    }
}
