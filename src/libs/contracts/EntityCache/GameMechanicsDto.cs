// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using MemoryPack;

namespace NEXUS.Contracts.EntityCache;

/// <summary>
///     Core combat tables, damage matrices, and game rules parsed from game mechanics XML.
/// </summary>
[MemoryPackable]
public partial class GameMechanicsDto
{
    public List<AttackTypeDto> AttackTypes { get; set; } = [];
    public List<CombatTableDto> CombatTables { get; set; } = [];
    public List<string> StealthTypes { get; set; } = [];
    public List<RevealTypeDto> RevealTypes { get; set; } = [];
    public List<string> EffectTypes { get; set; } = [];
    public List<ImmunityTypeDto> ImmunityTypes { get; set; } = [];
    public List<string> HeroCategories { get; set; } = [];
    public List<ArmorTypeDto> ArmorTypes { get; set; } = [];
    public List<PopupDto> Popups { get; set; } = [];
    public List<TargetSchemeDto> TargetSchemes { get; set; } = [];
}

[MemoryPackable]
public partial class CombatTableDto
{
    public string Name { get; set; } = string.Empty;
    public Dictionary<string, float> AttackMultipliers { get; set; } = [];
    public Dictionary<string, float> SpellMultipliers { get; set; } = [];
    public Dictionary<string, int> AggroPriorities { get; set; } = [];
    public Dictionary<string, int> AttackPriorities { get; set; } = [];
    public Dictionary<string, int> ProximityPriorities { get; set; } = [];
    public Dictionary<string, int> TargetPriorities { get; set; } = [];
}

[MemoryPackable]
public partial class AttackTypeDto
{
    public string Name { get; set; } = string.Empty;
    public float DeniedExpMultiplier { get; set; }
    public float UphillMissChance { get; set; }
}

[MemoryPackable]
public partial class RevealTypeDto
{
    public string Name { get; set; } = string.Empty;
    public string Reveal { get; set; } = string.Empty;
}

[MemoryPackable]
public partial class ImmunityTypeDto
{
    public string Name { get; set; } = string.Empty;
    public string Immune { get; set; } = string.Empty;
}

[MemoryPackable]
public partial class ArmorTypeDto
{
    public string Name { get; set; } = string.Empty;
    public string Effects { get; set; } = string.Empty;
    public float Factor { get; set; }
}

[MemoryPackable]
public partial class PopupDto
{
    public string Name { get; set; } = string.Empty;
    public string Color { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public bool Value { get; set; }
    public int Duration { get; set; }
    public int FadeTime { get; set; }
    public string Visibility { get; set; } = string.Empty;
}

[MemoryPackable]
public partial class TargetSchemeDto
{
    public string Name { get; set; } = string.Empty;
    public string Allow { get; set; } = string.Empty;
}
