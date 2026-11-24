// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using MemoryPack;

namespace NEXUS.Contracts.EntityCache;

/// <summary>
///     Prebaked buff, debuff, or aura state definition.
/// </summary>
[MemoryPackable]
public partial class StateDefinitionDto
{
    public string Name { get; set; } = string.Empty; // e.g. "State_Accursed_Ability2"
    public string Icon { get; set; } = string.Empty;
    public string PassiveEffect { get; set; } = string.Empty;
    public string EffectType { get; set; } = string.Empty; // "StatusBuff", "StatusDebuff", etc.
    public bool AllowTransfer { get; set; }
    public bool IsHidden { get; set; }
    public string ModifierKey { get; set; } = string.Empty;
    public float MoveSpeedMultiplier { get; set; }
    public float AttackSpeedMultiplier { get; set; }
    public bool Stunned { get; set; }
    public bool Silenced { get; set; }
    public bool Disarmed { get; set; }
    public bool Perplexed { get; set; }
    public bool Revealed { get; set; }
    public bool Invulnerable { get; set; }
    public int Duration { get; set; }
    public string StatusEffectTooltip { get; set; } = string.Empty;
}
