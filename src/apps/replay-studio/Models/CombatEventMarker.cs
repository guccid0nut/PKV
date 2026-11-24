// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ============================================================================
// File: CombatEventMarker.cs
// System: Replay Studio Desktop GUI Tool
// Policy: Strict Explicit Typing (NO 'var')
// ============================================================================

namespace ReplayStudio.GUI.Models;

/// <summary>
///     Enumerates the types of discrete combat timeline events.
/// </summary>
public enum CombatEventType
{
    Generic = 0,
    FirstBlood = 1,
    HeroKill = 2,
    TowerKill = 3,
    BarracksKill = 4,
    BossKill = 5,
    TeamFight = 6
}

/// <summary>
///     Represents a point-in-time combat event marker on the replay timeline.
/// </summary>
public sealed class CombatEventMarker
{
    public int TimestampMs { get; set; }
    public CombatEventType EventType { get; set; }
    public int AttackerSlot { get; set; } = -1;
    public int VictimSlot { get; set; } = -1;
    public string AttackerHero { get; set; } = string.Empty;
    public string VictimHero { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public float IntensityScore { get; set; } = 0.5f; // Normalized 0.0 to 1.0

    public CombatEventMarker Clone()
    {
        return new CombatEventMarker
        {
            TimestampMs = TimestampMs,
            EventType = EventType,
            AttackerSlot = AttackerSlot,
            VictimSlot = VictimSlot,
            AttackerHero = AttackerHero,
            VictimHero = VictimHero,
            Description = Description,
            IntensityScore = IntensityScore
        };
    }
}
