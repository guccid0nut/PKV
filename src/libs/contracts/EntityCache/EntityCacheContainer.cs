// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using MemoryPack;

namespace NEXUS.Contracts.EntityCache;

/// <summary>
///     Root container holding all prebaked entity definitions for zero-allocation game initialization.
/// </summary>
[MemoryPackable]
public partial class EntityCacheContainer
{
    public uint Version { get; set; } = 1;
    public string BuildTimestampUtc { get; set; } = string.Empty;
    public string BuildTimestamp { get; set; } = string.Empty;
    [MemoryPackIgnore]
    public ulong ChecksumXxHash3 { get; set; }
    public int TotalEntityCount { get; set; }
    public List<HeroDefinitionDto> Heroes { get; set; } = [];
    public List<AltAvatarDto> AltAvatars { get; set; } = [];
    public List<AbilityDefinitionDto> Abilities { get; set; } = [];
    public List<StateDefinitionDto> States { get; set; } = [];
    public List<ProjectileDefinitionDto> Projectiles { get; set; } = [];
    public List<ItemDefinitionDto> Items { get; set; } = [];
    public List<CosmeticUpgradeDto> CosmeticUpgrades { get; set; } = [];
    public GameMechanicsDto? GameMechanics { get; set; }
    public List<string> AllPreloadResources { get; set; } = [];
    public List<PreloadResourceDto> PreloadResources { get; set; } = [];
}
