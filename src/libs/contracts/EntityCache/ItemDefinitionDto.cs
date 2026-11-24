// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using MemoryPack;

namespace NEXUS.Contracts.EntityCache;

/// <summary>
///     Prebaked store and inventory item definition.
/// </summary>
[MemoryPackable]
public partial class ItemDefinitionDto
{
    public string Name { get; set; } = string.Empty; // e.g. "Item_Bottle", "Item_LifeSteal5"
    public string DisplayName { get; set; } = string.Empty;
    public string Icon { get; set; } = string.Empty;
    public string IconPath { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public int Cost { get; set; }
    public List<string> Components { get; set; } = [];
    public string ActionType { get; set; } = string.Empty;
    public string ActivateScheme { get; set; } = string.Empty;
    public int CastTime { get; set; }
    public int CastActionTime { get; set; }
    public int CooldownTime { get; set; }
    public string CooldownType { get; set; } = string.Empty;
    public string CastEffect { get; set; } = string.Empty;
    public string StatusEffectTooltip { get; set; } = string.Empty;
    public bool AllowSharing { get; set; }
    public bool NonInterrupting { get; set; }
    public bool NoBreakChannel { get; set; }
    public bool NonAggressive { get; set; }
    public float Strength { get; set; }
    public float Agility { get; set; }
    public float Intelligence { get; set; }
    public float Armor { get; set; }
    public float MagicArmor { get; set; }
    public float Damage { get; set; }
    public List<string> Modifiers { get; set; } = [];
}
