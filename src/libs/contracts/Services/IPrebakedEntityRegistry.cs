// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Collections.Frozen;
using NEXUS.Contracts.EntityCache;

namespace NEXUS.Contracts.Services;

/// <summary>
///     High-performance in-memory registry holding all pre-baked entity definitions
///     indexed into zero-allocation FrozenDictionary structures.
/// </summary>
public interface IPrebakedEntityRegistry
{
    /// <summary>
    ///     Indicates whether the registry has completed asynchronous initialization and pre-warming.
    /// </summary>
    bool IsPrewarmed { get; }

    /// <summary>
    ///     Total count of all indexed entity items across all categories.
    /// </summary>
    int TotalEntitiesLoaded { get; }

    /// <summary>
    ///     The deserialized entity cache container if loaded.
    /// </summary>
    EntityCacheContainer? Container { get; }

    // Full FrozenDictionary Properties (zero-allocation read paths)
    FrozenDictionary<uint, HeroDefinitionDto> HeroesById { get; }
    FrozenDictionary<string, HeroDefinitionDto> HeroesByName { get; }
    FrozenDictionary<string, AbilityDefinitionDto> AbilitiesByName { get; }
    FrozenDictionary<string, StateDefinitionDto> StatesByName { get; }
    FrozenDictionary<string, ProjectileDefinitionDto> ProjectilesByName { get; }
    FrozenDictionary<string, ItemDefinitionDto> ItemsByName { get; }
    FrozenDictionary<string, CosmeticUpgradeDto> CosmeticsByName { get; }
    FrozenDictionary<string, AltAvatarDto> AltAvatarsByKey { get; }
    FrozenDictionary<string, EntityDefinitionDto> EntitiesByName { get; }

    /// <summary>
    ///     Initializes the registry asynchronously, auto-discovering entity_cache.bin.
    /// </summary>
    ValueTask InitializeAsync(CancellationToken cancellationToken = default);

    /// <summary>
    ///     Initializes the registry from an explicit file path.
    /// </summary>
    ValueTask InitializeAsync(string? explicitCachePath, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Initializes the registry directly from a pre-constructed container (e.g. for in-memory testing).
    /// </summary>
    ValueTask InitializeFromContainerAsync(EntityCacheContainer container,
        CancellationToken cancellationToken = default);

    // Specific typed lookups (O(1) via FrozenDictionary)
    HeroDefinitionDto? GetHeroById(uint heroId);
    HeroDefinitionDto? GetHeroByName(string heroName);
    AbilityDefinitionDto? GetAbilityByName(string abilityName);
    StateDefinitionDto? GetStateByName(string stateName);
    ProjectileDefinitionDto? GetProjectileByName(string projectileName);
    ItemDefinitionDto? GetItemByName(string itemName);
    CosmeticUpgradeDto? GetCosmeticByName(string cosmeticName);
    CosmeticUpgradeDto? GetCosmeticById(uint cosmeticId);
    AltAvatarDto? GetAltAvatar(string heroName, string avatarKey);

    // Generic fallback lookup for loose entity files
    EntityDefinitionDto? GetEntityByName(string entityName);

    // Tiered asset lookup (PreBakedMemory -> PreBakedDisk -> VfsArchive -> NotFound)
    EntityLookupResult ResolveAsset(string resourcePath);

    // Enumeration helpers
    IEnumerable<HeroDefinitionDto> GetAllHeroes();
    IEnumerable<AbilityDefinitionDto> GetAllAbilities();
    IEnumerable<StateDefinitionDto> GetAllStates();
    IEnumerable<ProjectileDefinitionDto> GetAllProjectiles();
    IEnumerable<ItemDefinitionDto> GetAllItems();
    IEnumerable<CosmeticUpgradeDto> GetAllCosmetics();
    IEnumerable<AltAvatarDto> GetAllAltAvatars();
}
