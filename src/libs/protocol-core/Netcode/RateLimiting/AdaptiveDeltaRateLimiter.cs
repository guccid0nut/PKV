// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ============================================================================
// File: AdaptiveDeltaRateLimiter.cs
// System: Kongor Core Netcode - Adaptive Entity Delta Rate-Limiting & Priority Shedding
// Policy: Strict Explicit Typing (NO 'var')
// ============================================================================

namespace ASPIRE.Common.Netcode;

/// <summary>
///     Network congestion levels estimated from closed-loop BBR and inflight packet delivery metrics.
/// </summary>
internal enum CongestionLevel : byte
{
    Nominal = 0,   // Loss < 1%, inflight < 75% cwnd -> 100% full tick detail
    Moderate = 1,  // Loss 1-3%, inflight 75-90% cwnd -> Downsample creeps to 10 Hz, strip non-hero animations
    Severe = 2,    // Loss 3-8%, inflight > 90% cwnd -> Shed all cosmetics & particles, creeps coordinates-only
    Critical = 3   // Loss > 8%, inflight >= cwnd -> Hero VIP mode: Only hero coordinates and health/mana
}

/// <summary>
///     Entity classification tiers determining delivery priority under link congestion.
/// </summary>
internal enum EntityPriorityTier : byte
{
    Critical = 0,  // Player heroes (positions, velocities, health, status flags)
    High = 1,      // Active projectiles, defense towers, ancient buildings, critical orders
    Medium = 2,    // Lane creeps, jungle neutrals, player-controlled combat summons
    Low = 3        // Cosmetic summons, ambient particle entities, decorative illusions
}

/// <summary>
///     High-performance zero-allocation engine that adaptively prioritizes and rate-limits
///     game entity deltas during network congestion and teamfight data spikes.
/// </summary>
internal static class AdaptiveDeltaRateLimiter
{
    public const byte EntityTypeHero = 1;
    public const byte EntityTypeCreep = 2;
    public const byte EntityTypeNeutral = 3;
    public const byte EntityTypeBuilding = 4;
    public const byte EntityTypeProjectile = 5;
    public const byte EntityTypeSummon = 6;
    public const byte EntityTypeCosmetic = 7;

    /// <summary>
    ///     Classifies an entity into its priority tier based on its EntityType.
    /// </summary>
    public static EntityPriorityTier GetPriorityTier(byte entityType)
    {
        return entityType switch
        {
            EntityTypeHero => EntityPriorityTier.Critical,
            EntityTypeBuilding or EntityTypeProjectile => EntityPriorityTier.High,
            EntityTypeCreep or EntityTypeNeutral or EntityTypeSummon => EntityPriorityTier.Medium,
            _ => EntityPriorityTier.Low
        };
    }

    /// <summary>
    ///     Evaluates closed-loop transport telemetry to determine the operational congestion level.
    /// </summary>
    public static CongestionLevel EvaluateCongestion(
        long inflightBytes,
        int targetCwndBytes,
        double recentLossRate,
        TimeSpan currentRtt,
        TimeSpan minRtt)
    {
        // 1. Critical congestion: High loss (> 8%) or inflight saturated beyond congestion window
        if (recentLossRate >= 0.08 || (targetCwndBytes > 0 && inflightBytes >= targetCwndBytes))
        {
            return CongestionLevel.Critical;
        }

        // 2. Severe congestion: Elevated loss (> 3%), inflight near capacity (> 90%), or extreme RTT bloat (2.5x min RTT)
        if (recentLossRate >= 0.03 ||
            (targetCwndBytes > 0 && inflightBytes >= (targetCwndBytes * 9 / 10)) ||
            (minRtt > TimeSpan.Zero && currentRtt >= (minRtt * 2.5)))
        {
            return CongestionLevel.Severe;
        }

        // 3. Moderate congestion: Minor loss (> 1%), inflight elevated (> 75%), or minor bufferbloat (1.5x min RTT)
        if (recentLossRate >= 0.01 ||
            (targetCwndBytes > 0 && inflightBytes >= (targetCwndBytes * 3 / 4)) ||
            (minRtt > TimeSpan.Zero && currentRtt >= (minRtt * 1.5)))
        {
            return CongestionLevel.Moderate;
        }

        return CongestionLevel.Nominal;
    }

    /// <summary>
    ///     Filters and sheds entity deltas from a snapshot frame to fit within the specified MTU
    ///     and prevent queue bloat based on the active congestion level.
    /// </summary>
    public static SnapshotFrameDelta FilterFrame(
        SnapshotFrameDelta sourceFrame,
        CongestionLevel level,
        ushort maxPayloadBytes,
        uint currentTick)
    {
        if (sourceFrame.Entities.Count == 0)
        {
            return sourceFrame;
        }

        // Under nominal conditions with reasonable payload size, pass through directly
        if (level == CongestionLevel.Nominal && sourceFrame.GetSerializedSize() <= maxPayloadBytes)
        {
            return sourceFrame;
        }

        List<SnapshotEntityDelta> originalEntities = sourceFrame.Entities;
        List<SnapshotEntityDelta> retainedEntities = new(originalEntities.Count);

        int currentFrameSize = SnapshotFrameHeader.Size;

        // Pass 1: Select entities according to congestion policy
        for (int i = 0; i < originalEntities.Count; i++)
        {
            SnapshotEntityDelta entity = originalEntities[i];
            EntityPriorityTier tier = GetPriorityTier(entity.EntityType);

            switch (level)
            {
                case CongestionLevel.Nominal:
                    retainedEntities.Add(entity);
                    break;

                case CongestionLevel.Moderate:
                    if (tier == EntityPriorityTier.Low)
                    {
                        // Strip animations from low priority entities
                        entity.ChangedMask &= ~DeltaFieldMask.Animation;
                        retainedEntities.Add(entity);
                    }
                    else if (tier == EntityPriorityTier.Medium)
                    {
                        // Downsample medium entities (creeps/neutrals) to every 2nd tick
                        if (((currentTick + entity.EntityId) & 1) == 0)
                        {
                            entity.ChangedMask &= ~DeltaFieldMask.Animation;
                            retainedEntities.Add(entity);
                        }
                    }
                    else
                    {
                        // Heroes & High priority are fully preserved
                        retainedEntities.Add(entity);
                    }
                    break;

                case CongestionLevel.Severe:
                    // Drop Low priority (cosmetics & ambient particles) completely
                    if (tier == EntityPriorityTier.Low)
                    {
                        continue;
                    }

                    if (tier == EntityPriorityTier.Medium)
                    {
                        // Creeps: Coordinates only (strip inventory, orders, animations)
                        entity.ChangedMask &= DeltaFieldMask.Coordinates;
                        // Decimate to 10 Hz
                        if (((currentTick + entity.EntityId) & 1) == 0)
                        {
                            retainedEntities.Add(entity);
                        }
                    }
                    else
                    {
                        // Heroes & High priority
                        retainedEntities.Add(entity);
                    }
                    break;

                case CongestionLevel.Critical:
                    // VIP Mode: Strictly Heroes (Coordinates + Combat) and Towers/Base Buildings
                    if (tier == EntityPriorityTier.Critical)
                    {
                        entity.ChangedMask &= (DeltaFieldMask.Coordinates | DeltaFieldMask.Combat);
                        retainedEntities.Add(entity);
                    }
                    else if (tier == EntityPriorityTier.High && entity.EntityType == EntityTypeBuilding)
                    {
                        entity.ChangedMask &= DeltaFieldMask.Combat; // Only health bars of towers
                        retainedEntities.Add(entity);
                    }
                    break;
            }
        }

        // Pass 2: Strict MTU budget clamping
        // If total size still exceeds maxPayloadBytes, progressively shed entities from lowest tier up
        currentFrameSize += SimdEntityDelta.ComputeTotalSerializedSize(retainedEntities);
        if (currentFrameSize > maxPayloadBytes)
        {
            // Sort to ensure Critical heroes are at the front
            retainedEntities.Sort(static (a, b) =>
            {
                EntityPriorityTier tierA = GetPriorityTier(a.EntityType);
                EntityPriorityTier tierB = GetPriorityTier(b.EntityType);
                return tierA.CompareTo(tierB);
            });

            // Trim from the back until frame fits within maxPayloadBytes
            while (retainedEntities.Count > 0 && currentFrameSize > maxPayloadBytes)
            {
                int lastIdx = retainedEntities.Count - 1;
                // Never remove Critical heroes if possible, unless the hero alone exceeds maxPayloadBytes
                if (GetPriorityTier(retainedEntities[lastIdx].EntityType) == EntityPriorityTier.Critical && retainedEntities.Count == 1)
                {
                    break;
                }

                int entitySize = SimdEntityDelta.GetTotalSerializedSize(retainedEntities[lastIdx].ChangedMask);
                retainedEntities.RemoveAt(lastIdx);
                currentFrameSize -= entitySize;
            }
        }

        return new SnapshotFrameDelta
        {
            Header = new SnapshotFrameHeader
            {
                FrameSequence = sourceFrame.Header.FrameSequence,
                ServerTick = sourceFrame.Header.ServerTick,
                ClientAckSequence = sourceFrame.Header.ClientAckSequence,
                EntityCount = (ushort)retainedEntities.Count
            },
            Entities = retainedEntities
        };
    }
}
