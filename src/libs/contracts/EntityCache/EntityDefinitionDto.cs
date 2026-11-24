// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using MemoryPack;

namespace NEXUS.Contracts.EntityCache;

/// <summary>
///     Generic entity definition model representing abilities, states, projectiles, items, heroes, or cosmetics.
/// </summary>
[MemoryPackable]
public partial class EntityDefinitionDto
{
    public string Name { get; set; } = string.Empty;

    public string EntityType { get; set; } =
        string.Empty; // "Hero", "Ability", "State", "Projectile", "Item", "Cosmetic"

    public string SourcePath { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string IconPath { get; set; } = string.Empty;
    public string ModelPath { get; set; } = string.Empty;
    public Dictionary<string, string> Properties { get; set; } = [];
}
