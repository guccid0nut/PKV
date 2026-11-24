// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using MemoryPack;

namespace NEXUS.Contracts.EntityCache;

/// <summary>
///     Prebaked hero or unit ability definition.
/// </summary>
[MemoryPackable]
public partial class AbilityDefinitionDto
{
    public string Name { get; set; } = string.Empty; // e.g. "Ability_Accursed1"
    public string DisplayName { get; set; } = string.Empty;
    public int Slot { get; set; } // 0, 1, 2, 3, etc.
    public string Icon { get; set; } = string.Empty; // e.g. "/heroes/accursed/ability_01/icon.tga"
    public string IconPath { get; set; } = string.Empty;
    public string Anim { get; set; } = string.Empty; // e.g. "ability_1"
    public int MaxLevel { get; set; } = 4;
    public string RequiredLevel { get; set; } = string.Empty; // e.g. "1,3,5,7"
    public string ActionType { get; set; } = string.Empty; // e.g. "target_entity", "no_target", "target_position"
    public string TargetScheme { get; set; } = string.Empty; // e.g. "all_other_units", "ally_units"
    public string CastEffectType { get; set; } = string.Empty; // e.g. "SuperiorMagic"
    public string CastEffect { get; set; } = string.Empty; // e.g. "cast.effect"
    public int CastTime { get; set; }
    public int CastActionTime { get; set; }
    public string ManaCost { get; set; } = string.Empty; // e.g. "75" or "75,85,95,105"
    public string CooldownTime { get; set; } = string.Empty; // e.g. "6000"
    public string Cooldown { get; set; } = string.Empty;
    public string Range { get; set; } = string.Empty; // e.g. "700"
    public string CastRange { get; set; } = string.Empty;
    public string TargetRadius { get; set; } = string.Empty; // e.g. "0"
    public bool KeepToggleOnWhenDead { get; set; }
    public string TooltipNumParam1 { get; set; } = string.Empty;
    public string TooltipNumParam2 { get; set; } = string.Empty;
    public string AreaCastMaterial { get; set; } = string.Empty;
    public bool ShowAreaCast { get; set; }
    public string TargetPriorities { get; set; } = string.Empty;
    public List<string> ModifierKeys { get; set; } = [];
    public List<string> AppliedStates { get; set; } = [];
}
