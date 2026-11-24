// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using MemoryPack;

namespace NEXUS.Contracts.EntityCache;

/// <summary>
///     Prebaked projectile simulation properties and asset bindings.
/// </summary>
[MemoryPackable]
public partial class ProjectileDefinitionDto
{
    public string Name { get; set; } = string.Empty; // e.g. "Projectile_Accursed_Alt4_Dragon"
    public string Model { get; set; } = string.Empty;
    public float ModelScale { get; set; } = 1.0f;
    public float EffectScale { get; set; } = 1.0f;
    public string ImpactEffect { get; set; } = string.Empty;
    public string TrailEffect { get; set; } = string.Empty;
    public string DeathEffect { get; set; } = string.Empty;
    public float Speed { get; set; }
    public float MaxSpeed { get; set; }
    public float Gravity { get; set; }
    public bool Flying { get; set; }
    public float FlyHeight { get; set; }
    public float TouchRadius { get; set; }
    public string TouchTargetScheme { get; set; } = string.Empty;
    public int LifeTime { get; set; }
    public bool CanTurn { get; set; }
    public bool Disjointable { get; set; }
    public bool Homing { get; set; }
    public float HomingTurnSpeed { get; set; }
}
