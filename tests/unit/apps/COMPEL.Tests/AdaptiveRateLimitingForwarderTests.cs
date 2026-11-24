// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ============================================================================
// File: AdaptiveRateLimitingForwarderTests.cs
// System: COMPEL Tests - PMTUD & Adaptive Rate-Limiter Integration Tests
// Policy: Strict Explicit Typing (NO 'var')
// ============================================================================

using System.Net;
using ASPIRE.Common.Netcode;
using FluentAssertions;
using Xunit;

namespace COMPEL.Tests;

public sealed class AdaptiveRateLimitingForwarderTests
{
    private sealed class TestTimeProvider : TimeProvider
    {
        private long _timestamp = 50_000_000L;
        public override long GetTimestamp() => Volatile.Read(ref _timestamp);
        public override long TimestampFrequency => 1_000_000L;

        public void Advance(TimeSpan delta)
        {
            long ticks = (long)(delta.TotalSeconds * TimestampFrequency);
            Interlocked.Add(ref _timestamp, ticks);
        }
    }

    [Fact]
    public void PmtuDiscovery_ProbeAck_RaisesCurrentMtu_AndClampsToCeiling()
    {
        TestTimeProvider timeProvider = new();
        PathMtuDiscoveryService pmtu = new(timeProvider, initialMtu: 1200);

        pmtu.CurrentMtu.Should().Be(1200);

        // Advance and probe 1280
        bool probe1 = pmtu.ShouldProbe(out ushort size1);
        probe1.Should().BeTrue();
        size1.Should().Be(1280);

        pmtu.OnProbeDispatched(sequence: 501, probeSize: 1280);
        bool acked1 = pmtu.OnProbeAcknowledged(501);
        acked1.Should().BeTrue();
        pmtu.CurrentMtu.Should().Be(1280);

        // Advance 600ms and probe 1360
        timeProvider.Advance(TimeSpan.FromMilliseconds(600));
        pmtu.ShouldProbe(out ushort size2).Should().BeTrue();
        size2.Should().Be(1360);

        pmtu.OnProbeDispatched(sequence: 502, probeSize: 1360);
        pmtu.OnProbeAcknowledged(502).Should().BeTrue();
        pmtu.CurrentMtu.Should().Be(1360);
    }

    [Fact]
    public void PmtuDiscovery_ConsecutiveLoss_DropsToSafeBaseMtu_AndBlackHoleTriggered()
    {
        TestTimeProvider timeProvider = new();
        PathMtuDiscoveryService pmtu = new(timeProvider, initialMtu: 1472);

        // 3 consecutive packet losses detected by InflightPacketTracker
        pmtu.ReportConsecutiveLoss(3);

        pmtu.CurrentMtu.Should().Be(1200);
        pmtu.ConfirmedMtu.Should().Be(1200);
        pmtu.State.Should().Be(PmtuState.BlackHole);
    }

    [Fact]
    public void TeamfightSimulation_AdaptiveRateLimiter_PreservesAllHeroes_UnderCriticalCongestion()
    {
        SnapshotFrameDelta teamfightFrame = new()
        {
            Header = new SnapshotFrameHeader
            {
                FrameSequence = 100,
                ServerTick = 5000,
                ClientAckSequence = 99
            },
            Entities = []
        };

        // 10 Heroes in active combat
        for (ushort i = 1; i <= 10; i++)
        {
            teamfightFrame.Entities.Add(new SnapshotEntityDelta
            {
                EntityId = i,
                EntityType = AdaptiveDeltaRateLimiter.EntityTypeHero,
                ChangedMask = DeltaFieldMask.Coordinates | DeltaFieldMask.Combat | DeltaFieldMask.Animation | DeltaFieldMask.Orders
            });
        }

        // 40 Lane Creeps
        for (ushort i = 50; i < 90; i++)
        {
            teamfightFrame.Entities.Add(new SnapshotEntityDelta
            {
                EntityId = i,
                EntityType = AdaptiveDeltaRateLimiter.EntityTypeCreep,
                ChangedMask = DeltaFieldMask.FullState
            });
        }

        // 15 Cosmetic Summon & Particle Entities
        for (ushort i = 150; i < 165; i++)
        {
            teamfightFrame.Entities.Add(new SnapshotEntityDelta
            {
                EntityId = i,
                EntityType = AdaptiveDeltaRateLimiter.EntityTypeCosmetic,
                ChangedMask = DeltaFieldMask.Coordinates | DeltaFieldMask.Animation
            });
        }

        teamfightFrame.Header.EntityCount = (ushort)teamfightFrame.Entities.Count;

        // Discovered PMTU is 1360 bytes (e.g. WireGuard / cellular hotspot)
        ushort discoveredPmtu = 1360;

        // Apply Critical congestion (loss > 8% or queue saturated)
        SnapshotFrameDelta filtered = AdaptiveDeltaRateLimiter.FilterFrame(
            teamfightFrame,
            CongestionLevel.Critical,
            maxPayloadBytes: discoveredPmtu,
            currentTick: 5000);

        // 1. Output frame must strictly fit within 1360 bytes
        filtered.GetSerializedSize().Should().BeLessThanOrEqualTo(discoveredPmtu);

        // 2. All 10 Heroes must be 100% retained
        int heroCount = filtered.Entities.Count(e => e.EntityType == AdaptiveDeltaRateLimiter.EntityTypeHero);
        heroCount.Should().Be(10);

        // 3. All heroes must retain critical Coordinates and Combat status
        foreach (SnapshotEntityDelta hero in filtered.Entities.Where(e => e.EntityType == AdaptiveDeltaRateLimiter.EntityTypeHero))
        {
            hero.ChangedMask.HasFlag(DeltaFieldMask.Coordinates).Should().BeTrue();
            hero.ChangedMask.HasFlag(DeltaFieldMask.Combat).Should().BeTrue();
        }

        // 4. All cosmetics and creeps must be shed
        filtered.Entities.Any(e => e.EntityType == AdaptiveDeltaRateLimiter.EntityTypeCosmetic).Should().BeFalse();
        filtered.Entities.Any(e => e.EntityType == AdaptiveDeltaRateLimiter.EntityTypeCreep).Should().BeFalse();
    }
}
