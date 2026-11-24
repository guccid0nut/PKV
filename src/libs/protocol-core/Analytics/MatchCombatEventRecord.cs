// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ============================================================================
// File: MatchCombatEventRecord.cs
// System: Kongor Protocol Core - Analytics & Replay Combat Event Record
// Policy: Strict Explicit Typing (NO 'var')
// ============================================================================

namespace Kongor.Core.Analytics;

/// <summary>
///     High-frequency match combat event categories emitted during live HoN matches.
/// </summary>
public enum CombatEventType : byte
{
    HeroKill = 0,
    HeroAssist = 1,
    HeroDeath = 2,
    AbilityCast = 3,
    ItemPurchased = 4,
    GoldTick = 5,
    WardPlaced = 6,
    TowerKill = 7,
    RoshKill = 8
}

/// <summary>
///     Immutable, compact record representing a single combat or telemetry event during a match.
///     Designed for high-throughput columnar Parquet serialization and zero database bloat.
/// </summary>
public sealed record MatchCombatEventRecord(
    long MatchId,
    int MatchTimeMs,
    int Tick,
    CombatEventType EventType,
    byte SourcePlayerIndex,
    short SourceHeroId,
    byte TargetPlayerIndex,
    short TargetHeroId,
    int AbilityOrItemId,
    float EventValue,
    float PositionX,
    float PositionY,
    float PositionZ)
{
    /// <summary>
    ///     Estimated uncompressed raw JSON payload size in bytes (approx. 180–220 bytes per JSON event).
    /// </summary>
    public const int EstimatedRawJsonSizeBytes = 200;

    /// <summary>
    ///     Estimated binary in-memory payload size in bytes (42 bytes uncompressed struct).
    /// </summary>
    public const int BinaryPayloadSizeBytes = 42;
}
