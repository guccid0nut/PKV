// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

namespace NEXUS.Contracts.Domain;

/// <summary>
///     Detailed hero metadata, abilities, props, and animations extracted from VFS archives.
/// </summary>
public class HeroEntityInfoDto
{
    public string HeroName { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string IconPath { get; set; } = string.Empty;
    public string PrimaryAttribute { get; set; } = "Agility";
    public float Difficulty { get; set; } = 2.0f;
    public string Roles { get; set; } = "Carry";
    public List<string> AbilityIcons { get; set; } = [];
    public List<HeroAbilityPropDto> AbilityProps { get; set; } = [];
    public List<HeroAnimDetailDto> Animations { get; set; } = [];
}

public class HeroAbilityPropDto
{
    public string AbilitySlot { get; set; } = string.Empty; // e.g. "ability_1", "ability_4"
    public string PropName { get; set; } = string.Empty; // e.g. "hook", "wards", "claw"
    public string ModelPath { get; set; } = string.Empty;
    public string? AttachBone { get; set; } // e.g. "_bone_weapon3", "Bip01 R Hand"
    public bool IsProjectile { get; set; }
    public string GlbUrl { get; set; } = string.Empty;
}

public class HeroAnimFrameEventDto
{
    public int Frame { get; set; }
    public string? SoundAction { get; set; }
    public string? EffectType { get; set; }
}

public class HeroAnimDetailDto
{
    public string Name { get; set; } = string.Empty;
    public int Fps { get; set; } = 30;
    public bool Loop { get; set; } = true;
    public List<HeroAnimFrameEventDto> FrameEvents { get; set; } = [];
}

public class HeroAvatarModelDto
{
    public required string Name { get; set; }
    public required string GlbUrl { get; set; }
}
