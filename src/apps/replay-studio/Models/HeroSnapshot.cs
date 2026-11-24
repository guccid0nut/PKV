// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ============================================================================
// File: HeroSnapshot.cs
// System: Replay Studio Desktop GUI Tool
// Policy: Strict Explicit Typing (NO 'var')
// ============================================================================

using System.Numerics;

namespace ReplayStudio.GUI.Models;

/// <summary>
///     Point-in-time state of an individual hero in the match simulation.
/// </summary>
public sealed class HeroSnapshot
{
    public int ClientIndex { get; set; } // 0..9 (0..4 = Legion, 5..9 = Hellbourne)
    public int Team { get; set; } // 1 = Legion, 2 = Hellbourne
    public string PlayerName { get; set; } = string.Empty;
    public string HeroName { get; set; } = string.Empty;
    public Vector3 Position { get; set; }
    public float HeadingAngle { get; set; } // Degrees [0..360)
    public float Health { get; set; }
    public float MaxHealth { get; set; } = 1000f;
    public float Mana { get; set; }
    public float MaxMana { get; set; } = 500f;
    public int Level { get; set; } = 1;
    public int Kills { get; set; }
    public int Deaths { get; set; }
    public int Assists { get; set; }
    public int Gold { get; set; }
    public int NetWorth { get; set; }
    public int GoldPerMinute { get; set; }
    public int ExpPerMinute { get; set; }
    public bool IsAlive { get; set; } = true;
    public int RespawnTimerMs { get; set; }
    public string[] Items { get; set; } = new string[6];
    public string ActiveAnimation { get; set; } = "idle";
    public bool IsEstimatedFallback { get; set; }

    public float HealthPercent => MaxHealth > 0 ? Math.Clamp((Health / MaxHealth) * 100f, 0f, 100f) : 0f;
    public float ManaPercent => MaxMana > 0 ? Math.Clamp((Mana / MaxMana) * 100f, 0f, 100f) : 0f;

    public string CleanHeroName
    {
        get
        {
            if (string.IsNullOrWhiteSpace(HeroName)) return string.Empty;
            return HeroName.StartsWith("Hero_", StringComparison.OrdinalIgnoreCase) ? HeroName[5..] : HeroName;
        }
    }

    public HeroSnapshot Clone()
    {
        HeroSnapshot copy = new()
        {
            ClientIndex = ClientIndex,
            Team = Team,
            PlayerName = PlayerName,
            HeroName = HeroName,
            Position = Position,
            HeadingAngle = HeadingAngle,
            Health = Health,
            MaxHealth = MaxHealth,
            Mana = Mana,
            MaxMana = MaxMana,
            Level = Level,
            Kills = Kills,
            Deaths = Deaths,
            Assists = Assists,
            Gold = Gold,
            NetWorth = NetWorth,
            GoldPerMinute = GoldPerMinute,
            ExpPerMinute = ExpPerMinute,
            IsAlive = IsAlive,
            RespawnTimerMs = RespawnTimerMs,
            ActiveAnimation = ActiveAnimation,
            IsEstimatedFallback = IsEstimatedFallback,
            Items = (string[])Items.Clone()
        };
        return copy;
    }
}

/// <summary>
///     Lightweight, allocation-free interpolated render state for a hero at sub-millisecond timestamps.
/// </summary>
public readonly record struct HeroRenderState(
    int ClientIndex,
    int Team,
    string PlayerName,
    string CleanHeroName,
    Vector3 Position,
    float HeadingAngle,
    float Health,
    float MaxHealth,
    float Mana,
    float MaxMana,
    float HealthPercent,
    float ManaPercent,
    bool IsAlive,
    string ActiveAnimation
);
