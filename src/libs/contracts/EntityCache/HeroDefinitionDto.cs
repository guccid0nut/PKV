// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using MemoryPack;

namespace NEXUS.Contracts.EntityCache;

/// <summary>
///     Prebaked hero character definition including attributes, abilities, alt avatars, and ratings.
/// </summary>
[MemoryPackable]
public partial class HeroDefinitionDto
{
    public uint Id { get; set; }
    public string Name { get; set; } = string.Empty; // e.g. "Hero_Accursed"
    public string DisplayName { get; set; } = string.Empty; // e.g. "Accursed"
    public string Path { get; set; } = string.Empty; // e.g. "/heroes/accursed"
    public string EntityPath { get; set; } = string.Empty; // e.g. "/hero.entity"
    public string DefaultIcon { get; set; } = string.Empty; // e.g. "/heroes/accursed/icon.tga"
    public string IconPath { get; set; } = string.Empty;
    public string DefaultModel { get; set; } = string.Empty; // e.g. "hd_accursed/preview.mdf"
    public string ModelPath { get; set; } = string.Empty;
    public string DefaultEffect { get; set; } = string.Empty; // e.g. "effects/body.effect"
    public string Category { get; set; } = string.Empty; // e.g. "tank protective"
    public string Team { get; set; } = "Legion"; // "Legion", "Hellbourne"
    public string Faction { get; set; } = "Legion"; // "Legion", "Hellbourne"
    public float Difficulty { get; set; }
    public string PrimaryAttribute { get; set; } = string.Empty; // "strength", "agility", "intelligence"
    public float Strength { get; set; }
    public float StrengthPerLevel { get; set; }
    public float Agility { get; set; }
    public float AgilityPerLevel { get; set; }
    public float Intelligence { get; set; }
    public float IntelligencePerLevel { get; set; }
    public string AttackType { get; set; } = string.Empty; // "Melee", "Ranged"
    public float AttackDamageMin { get; set; }
    public float AttackDamageMax { get; set; }
    public float AttackRange { get; set; }
    public int AttackCooldown { get; set; }
    public float MoveSpeed { get; set; }
    public float Armor { get; set; }
    public float MagicArmor { get; set; }

    // Ratings
    public int SoloRating { get; set; }
    public int JungleRating { get; set; }
    public int CarryRating { get; set; }
    public int SupportRating { get; set; }
    public int InitiatorRating { get; set; }
    public int GankerRating { get; set; }
    public int PusherRating { get; set; }
    public int RangedRating { get; set; }
    public int MeleeRating { get; set; }

    // Child definitions
    public List<AbilityDefinitionDto> Abilities { get; set; } = [];
    public List<string> AbilityNames { get; set; } = [];
    public List<string> InventorySlots { get; set; } = []; // inventory0..12
    public List<string> RecommendedItems { get; set; } = [];
    public List<string> AltAvatarKeys { get; set; } = [];
    public List<AltAvatarDto> AltAvatars { get; set; } = [];
    public List<string> PreloadResources { get; set; } = [];
}
