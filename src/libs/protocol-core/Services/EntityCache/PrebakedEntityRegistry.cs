// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Collections.Frozen;
using System.Diagnostics;
using System.IO.Compression;
using ASPIRE.Common.Prebaking.Serialization;
using ASPIRE.Common.Telemetry;
using Microsoft.Extensions.Logging.Abstractions;

namespace ASPIRE.Common.Services.EntityCache;

/// <summary>
///     Thread-safe, high-performance in-memory registry holding pre-baked entity definitions
///     compiled into zero-allocation FrozenDictionary structures.
/// </summary>
public sealed class PrebakedEntityRegistry(
    ILogger<PrebakedEntityRegistry>? logger = null,
    IConfiguration? configuration = null,
    string? explicitCachePath = null,
    string? overrideDirectory = null,
    string? s2zArchivePath = null) : IPrebakedEntityRegistry, IDisposable
{
    private readonly IConfiguration? _configuration = configuration;
    private readonly string? _explicitCachePath = explicitCachePath;
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private readonly ILogger<PrebakedEntityRegistry> _logger = logger ?? NullLogger<PrebakedEntityRegistry>.Instance;
    private readonly string? _overrideDirectory = overrideDirectory;
    private readonly string? _s2zArchivePath = s2zArchivePath;

    private FrozenDictionary<uint, CosmeticUpgradeDto>
        _cosmeticsById = FrozenDictionary<uint, CosmeticUpgradeDto>.Empty;

    private int _isPrewarmedState; // 0 = uninitialized, 1 = initializing, 2 = prewarmed

    public void Dispose()
    {
        _initLock.Dispose();
    }

    public bool IsPrewarmed => Volatile.Read(ref _isPrewarmedState) == 2;

    public int TotalEntitiesLoaded =>
        HeroesById.Count +
        AbilitiesByName.Count +
        StatesByName.Count +
        ProjectilesByName.Count +
        ItemsByName.Count +
        CosmeticsByName.Count +
        AltAvatarsByKey.Count;

    public EntityCacheContainer? Container { get; private set; }

    public FrozenDictionary<uint, HeroDefinitionDto> HeroesById { get; private set; } =
        FrozenDictionary<uint, HeroDefinitionDto>.Empty;

    public FrozenDictionary<string, HeroDefinitionDto> HeroesByName { get; private set; } =
        FrozenDictionary<string, HeroDefinitionDto>.Empty;

    public FrozenDictionary<string, AbilityDefinitionDto> AbilitiesByName { get; private set; } =
        FrozenDictionary<string, AbilityDefinitionDto>.Empty;

    public FrozenDictionary<string, StateDefinitionDto> StatesByName { get; private set; } =
        FrozenDictionary<string, StateDefinitionDto>.Empty;

    public FrozenDictionary<string, ProjectileDefinitionDto> ProjectilesByName { get; private set; } =
        FrozenDictionary<string, ProjectileDefinitionDto>.Empty;

    public FrozenDictionary<string, ItemDefinitionDto> ItemsByName { get; private set; } =
        FrozenDictionary<string, ItemDefinitionDto>.Empty;

    public FrozenDictionary<string, CosmeticUpgradeDto> CosmeticsByName { get; private set; } =
        FrozenDictionary<string, CosmeticUpgradeDto>.Empty;

    public FrozenDictionary<string, AltAvatarDto> AltAvatarsByKey { get; private set; } =
        FrozenDictionary<string, AltAvatarDto>.Empty;

    public FrozenDictionary<string, EntityDefinitionDto> EntitiesByName { get; private set; } =
        FrozenDictionary<string, EntityDefinitionDto>.Empty;

    public async ValueTask InitializeAsync(CancellationToken cancellationToken = default)
    {
        await InitializeAsync(_explicitCachePath, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask InitializeAsync(string? explicitCachePath, CancellationToken cancellationToken = default)
    {
        if (Volatile.Read(ref _isPrewarmedState) == 2) return;

        try
        {
            await _initLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            return;
        }

        try
        {
            if (Volatile.Read(ref _isPrewarmedState) == 2) return;

            Interlocked.Exchange(ref _isPrewarmedState, 1);
            long startTimestamp = Stopwatch.GetTimestamp();

            string? cacheFilePath = ResolveCacheFilePath(explicitCachePath);
            if (!string.IsNullOrEmpty(cacheFilePath) && File.Exists(cacheFilePath))
            {
                _logger.LogInformation("[PrebakedEntityRegistry] Loading entity cache from '{Path}'...", cacheFilePath);
                EntityCacheContainer container = await EntityCacheDeserializer
                    .DeserializeFromFileAsync(cacheFilePath, cancellationToken).ConfigureAwait(false);
                BuildDictionariesFromContainer(container);
                TimeSpan elapsed = Stopwatch.GetElapsedTime(startTimestamp);
                _logger.LogInformation(
                    "[PrebakedEntityRegistry] Successfully pre-warmed {Total} entities from '{Path}' in {ElapsedMs:F2}ms.",
                    TotalEntitiesLoaded, cacheFilePath, elapsed.TotalMilliseconds);
                EntityCacheTelemetryLogger.LogPrewarmCompleted(TotalEntitiesLoaded, elapsed.TotalMilliseconds,
                    cacheFilePath);
            }
            else
            {
                _logger.LogWarning(
                    "[PrebakedEntityRegistry] entity_cache.bin not found at candidate paths. Initializing empty registry.");
                BuildDictionariesFromContainer(new EntityCacheContainer());
                TimeSpan elapsed = Stopwatch.GetElapsedTime(startTimestamp);
                EntityCacheTelemetryLogger.LogPrewarmCompleted(0, elapsed.TotalMilliseconds, "Empty");
            }

            Interlocked.Exchange(ref _isPrewarmedState, 2);
        }
        catch (ObjectDisposedException)
        {
            // Host is shutting down
            Interlocked.Exchange(ref _isPrewarmedState, 0);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[PrebakedEntityRegistry] Failed to initialize entity cache registry.");
            Interlocked.Exchange(ref _isPrewarmedState, 0);
            throw;
        }
        finally
        {
            try
            {
                _initLock.Release();
            }
            catch (ObjectDisposedException)
            {
            }
        }
    }

    public async ValueTask InitializeFromContainerAsync(EntityCacheContainer container,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(container);

        try
        {
            await _initLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            return;
        }

        try
        {
            Interlocked.Exchange(ref _isPrewarmedState, 1);
            long startTimestamp = Stopwatch.GetTimestamp();

            BuildDictionariesFromContainer(container);

            TimeSpan elapsed = Stopwatch.GetElapsedTime(startTimestamp);
            _logger.LogInformation(
                "[PrebakedEntityRegistry] Initialized directly from container with {Total} entities in {ElapsedMs:F2}ms.",
                TotalEntitiesLoaded, elapsed.TotalMilliseconds);
            EntityCacheTelemetryLogger.LogPrewarmCompleted(TotalEntitiesLoaded, elapsed.TotalMilliseconds, "Container");

            Interlocked.Exchange(ref _isPrewarmedState, 2);
        }
        finally
        {
            try
            {
                _initLock.Release();
            }
            catch (ObjectDisposedException)
            {
            }
        }
    }

    public HeroDefinitionDto? GetHeroById(uint heroId)
    {
        long startTimestamp = Stopwatch.GetTimestamp();
        bool found = HeroesById.TryGetValue(heroId, out HeroDefinitionDto? hero);
        double micro = Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds * 1000.0;

        if (found)
        {
            NexusMetrics.RecordEntityCacheHit("Hero");
            NexusMetrics.RecordEntityResolutionDuration(micro, "Hero", "PreBakedMemory", true);
        }
        else
        {
            NexusMetrics.RecordEntityCacheMiss("Hero");
            NexusMetrics.RecordEntityResolutionDuration(micro, "Hero", "NotFound", false);
        }

        return hero;
    }

    public HeroDefinitionDto? GetHeroByName(string heroName)
    {
        if (string.IsNullOrWhiteSpace(heroName))
        {
            NexusMetrics.RecordEntityCacheMiss("Hero");
            NexusMetrics.RecordEntityResolutionDuration(0, "Hero", "NotFound", false);
            return null;
        }

        long startTimestamp = Stopwatch.GetTimestamp();
        bool found = HeroesByName.TryGetValue(heroName, out HeroDefinitionDto? hero);
        double micro = Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds * 1000.0;

        if (found)
        {
            NexusMetrics.RecordEntityCacheHit("Hero");
            NexusMetrics.RecordEntityResolutionDuration(micro, "Hero", "PreBakedMemory", true);
        }
        else
        {
            NexusMetrics.RecordEntityCacheMiss("Hero");
            NexusMetrics.RecordEntityResolutionDuration(micro, "Hero", "NotFound", false);
        }

        return hero;
    }

    public AbilityDefinitionDto? GetAbilityByName(string abilityName)
    {
        if (string.IsNullOrWhiteSpace(abilityName))
        {
            NexusMetrics.RecordEntityCacheMiss("Ability");
            NexusMetrics.RecordEntityResolutionDuration(0, "Ability", "NotFound", false);
            return null;
        }

        long startTimestamp = Stopwatch.GetTimestamp();
        bool found = AbilitiesByName.TryGetValue(abilityName, out AbilityDefinitionDto? ability);
        double micro = Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds * 1000.0;

        if (found)
        {
            NexusMetrics.RecordEntityCacheHit("Ability");
            NexusMetrics.RecordEntityResolutionDuration(micro, "Ability", "PreBakedMemory", true);
        }
        else
        {
            NexusMetrics.RecordEntityCacheMiss("Ability");
            NexusMetrics.RecordEntityResolutionDuration(micro, "Ability", "NotFound", false);
        }

        return ability;
    }

    public StateDefinitionDto? GetStateByName(string stateName)
    {
        if (string.IsNullOrWhiteSpace(stateName))
        {
            NexusMetrics.RecordEntityCacheMiss("State");
            NexusMetrics.RecordEntityResolutionDuration(0, "State", "NotFound", false);
            return null;
        }

        long startTimestamp = Stopwatch.GetTimestamp();
        bool found = StatesByName.TryGetValue(stateName, out StateDefinitionDto? state);
        double micro = Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds * 1000.0;

        if (found)
        {
            NexusMetrics.RecordEntityCacheHit("State");
            NexusMetrics.RecordEntityResolutionDuration(micro, "State", "PreBakedMemory", true);
        }
        else
        {
            NexusMetrics.RecordEntityCacheMiss("State");
            NexusMetrics.RecordEntityResolutionDuration(micro, "State", "NotFound", false);
        }

        return state;
    }

    public ProjectileDefinitionDto? GetProjectileByName(string projectileName)
    {
        if (string.IsNullOrWhiteSpace(projectileName))
        {
            NexusMetrics.RecordEntityCacheMiss("Projectile");
            NexusMetrics.RecordEntityResolutionDuration(0, "Projectile", "NotFound", false);
            return null;
        }

        long startTimestamp = Stopwatch.GetTimestamp();
        bool found = ProjectilesByName.TryGetValue(projectileName, out ProjectileDefinitionDto? projectile);
        double micro = Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds * 1000.0;

        if (found)
        {
            NexusMetrics.RecordEntityCacheHit("Projectile");
            NexusMetrics.RecordEntityResolutionDuration(micro, "Projectile", "PreBakedMemory", true);
        }
        else
        {
            NexusMetrics.RecordEntityCacheMiss("Projectile");
            NexusMetrics.RecordEntityResolutionDuration(micro, "Projectile", "NotFound", false);
        }

        return projectile;
    }

    public ItemDefinitionDto? GetItemByName(string itemName)
    {
        if (string.IsNullOrWhiteSpace(itemName))
        {
            NexusMetrics.RecordEntityCacheMiss("Item");
            NexusMetrics.RecordEntityResolutionDuration(0, "Item", "NotFound", false);
            return null;
        }

        long startTimestamp = Stopwatch.GetTimestamp();
        bool found = ItemsByName.TryGetValue(itemName, out ItemDefinitionDto? item);
        double micro = Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds * 1000.0;

        if (found)
        {
            NexusMetrics.RecordEntityCacheHit("Item");
            NexusMetrics.RecordEntityResolutionDuration(micro, "Item", "PreBakedMemory", true);
        }
        else
        {
            NexusMetrics.RecordEntityCacheMiss("Item");
            NexusMetrics.RecordEntityResolutionDuration(micro, "Item", "NotFound", false);
        }

        return item;
    }

    public CosmeticUpgradeDto? GetCosmeticByName(string cosmeticName)
    {
        if (string.IsNullOrWhiteSpace(cosmeticName))
        {
            NexusMetrics.RecordEntityCacheMiss("Cosmetic");
            NexusMetrics.RecordEntityResolutionDuration(0, "Cosmetic", "NotFound", false);
            return null;
        }

        long startTimestamp = Stopwatch.GetTimestamp();
        bool found = CosmeticsByName.TryGetValue(cosmeticName, out CosmeticUpgradeDto? cosmetic);
        double micro = Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds * 1000.0;

        if (found)
        {
            NexusMetrics.RecordEntityCacheHit("Cosmetic");
            NexusMetrics.RecordEntityResolutionDuration(micro, "Cosmetic", "PreBakedMemory", true);
        }
        else
        {
            NexusMetrics.RecordEntityCacheMiss("Cosmetic");
            NexusMetrics.RecordEntityResolutionDuration(micro, "Cosmetic", "NotFound", false);
        }

        return cosmetic;
    }

    public CosmeticUpgradeDto? GetCosmeticById(uint cosmeticId)
    {
        long startTimestamp = Stopwatch.GetTimestamp();
        bool found = _cosmeticsById.TryGetValue(cosmeticId, out CosmeticUpgradeDto? cosmetic);
        double micro = Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds * 1000.0;

        if (found)
        {
            NexusMetrics.RecordEntityCacheHit("Cosmetic");
            NexusMetrics.RecordEntityResolutionDuration(micro, "Cosmetic", "PreBakedMemory", true);
        }
        else
        {
            NexusMetrics.RecordEntityCacheMiss("Cosmetic");
            NexusMetrics.RecordEntityResolutionDuration(micro, "Cosmetic", "NotFound", false);
        }

        return cosmetic;
    }

    public AltAvatarDto? GetAltAvatar(string heroName, string avatarKey)
    {
        if (string.IsNullOrWhiteSpace(avatarKey))
        {
            NexusMetrics.RecordEntityCacheMiss("AltAvatar");
            NexusMetrics.RecordEntityResolutionDuration(0, "AltAvatar", "NotFound", false);
            return null;
        }

        long startTimestamp = Stopwatch.GetTimestamp();
        AltAvatarDto? avatar = null;
        bool found = false;

        if (!string.IsNullOrWhiteSpace(heroName))
        {
            string composite = $"{heroName}_{avatarKey}";
            if (AltAvatarsByKey.TryGetValue(composite, out avatar)) found = true;
        }

        if (!found && AltAvatarsByKey.TryGetValue(avatarKey, out avatar)) found = true;

        double micro = Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds * 1000.0;
        if (found)
        {
            NexusMetrics.RecordEntityCacheHit("AltAvatar");
            NexusMetrics.RecordEntityResolutionDuration(micro, "AltAvatar", "PreBakedMemory", true);
        }
        else
        {
            NexusMetrics.RecordEntityCacheMiss("AltAvatar");
            NexusMetrics.RecordEntityResolutionDuration(micro, "AltAvatar", "NotFound", false);
        }

        return avatar;
    }

    public EntityDefinitionDto? GetEntityByName(string entityName)
    {
        if (string.IsNullOrWhiteSpace(entityName))
        {
            NexusMetrics.RecordEntityCacheMiss("Entity");
            NexusMetrics.RecordEntityResolutionDuration(0, "Entity", "NotFound", false);
            return null;
        }

        long startTimestamp = Stopwatch.GetTimestamp();
        bool found = EntitiesByName.TryGetValue(entityName, out EntityDefinitionDto? entity);
        double micro = Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds * 1000.0;

        if (found)
        {
            NexusMetrics.RecordEntityCacheHit(entity?.EntityType ?? "Entity");
            NexusMetrics.RecordEntityResolutionDuration(micro, entity?.EntityType ?? "Entity", "PreBakedMemory", true);
        }
        else
        {
            NexusMetrics.RecordEntityCacheMiss("Entity");
            NexusMetrics.RecordEntityResolutionDuration(micro, "Entity", "NotFound", false);
        }

        return entity;
    }

    public EntityLookupResult ResolveAsset(string resourcePath)
    {
        if (string.IsNullOrWhiteSpace(resourcePath))
        {
            NexusMetrics.RecordEntityCacheMiss("Asset");
            NexusMetrics.RecordEntityResolutionDuration(0, "Asset", "NotFound", false);
            return new EntityLookupResult(false, "NotFound", 0, string.Empty);
        }

        string normalizedPath = resourcePath.Replace('\\', '/').TrimStart('/');
        long startTimestamp = Stopwatch.GetTimestamp();

        // -------------------------------------------------------------
        // Tier 1: PreBakedMemory (FrozenDictionary fast paths)
        // -------------------------------------------------------------
        if (HeroesByName.TryGetValue(normalizedPath, out HeroDefinitionDto? hero))
        {
            long micro = (long)(Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds * 1000);
            string resolved = !string.IsNullOrEmpty(hero.ModelPath) ? hero.ModelPath :
                !string.IsNullOrEmpty(hero.DefaultModel) ? hero.DefaultModel : hero.Name;
            NexusMetrics.RecordEntityCacheHit("Hero");
            NexusMetrics.RecordEntityResolutionDuration(micro, "Hero", "PreBakedMemory", true);
            return new EntityLookupResult(true, "PreBakedMemory", micro, resolved);
        }

        if (AbilitiesByName.TryGetValue(normalizedPath, out AbilityDefinitionDto? ability))
        {
            long micro = (long)(Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds * 1000);
            string resolved = !string.IsNullOrEmpty(ability.IconPath) ? ability.IconPath :
                !string.IsNullOrEmpty(ability.Icon) ? ability.Icon : ability.Name;
            NexusMetrics.RecordEntityCacheHit("Ability");
            NexusMetrics.RecordEntityResolutionDuration(micro, "Ability", "PreBakedMemory", true);
            return new EntityLookupResult(true, "PreBakedMemory", micro, resolved);
        }

        if (ItemsByName.TryGetValue(normalizedPath, out ItemDefinitionDto? item))
        {
            long micro = (long)(Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds * 1000);
            string resolved = !string.IsNullOrEmpty(item.IconPath) ? item.IconPath :
                !string.IsNullOrEmpty(item.Icon) ? item.Icon : item.Name;
            NexusMetrics.RecordEntityCacheHit("Item");
            NexusMetrics.RecordEntityResolutionDuration(micro, "Item", "PreBakedMemory", true);
            return new EntityLookupResult(true, "PreBakedMemory", micro, resolved);
        }

        if (AltAvatarsByKey.TryGetValue(normalizedPath, out AltAvatarDto? avatar))
        {
            long micro = (long)(Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds * 1000);
            string resolved = !string.IsNullOrEmpty(avatar.ModelPath) ? avatar.ModelPath :
                !string.IsNullOrEmpty(avatar.DefaultModel) ? avatar.DefaultModel : avatar.Key;
            NexusMetrics.RecordEntityCacheHit("AltAvatar");
            NexusMetrics.RecordEntityResolutionDuration(micro, "AltAvatar", "PreBakedMemory", true);
            return new EntityLookupResult(true, "PreBakedMemory", micro, resolved);
        }

        if (EntitiesByName.TryGetValue(normalizedPath, out EntityDefinitionDto? entity))
        {
            long micro = (long)(Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds * 1000);
            string resolved = !string.IsNullOrEmpty(entity.ModelPath) ? entity.ModelPath :
                !string.IsNullOrEmpty(entity.IconPath) ? entity.IconPath : entity.Name;
            NexusMetrics.RecordEntityCacheHit(entity.EntityType ?? "Entity");
            NexusMetrics.RecordEntityResolutionDuration(micro, entity.EntityType ?? "Entity", "PreBakedMemory", true);
            return new EntityLookupResult(true, "PreBakedMemory", micro, resolved);
        }

        // -------------------------------------------------------------
        // Tier 2: PreBakedDisk (Loose files on disk)
        // -------------------------------------------------------------
        if (!string.IsNullOrEmpty(_overrideDirectory))
        {
            string diskRelative = normalizedPath.Replace('/', Path.DirectorySeparatorChar);
            string diskPath = Path.Combine(_overrideDirectory, diskRelative);
            if (File.Exists(diskPath))
            {
                long micro = (long)(Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds * 1000);
                NexusMetrics.RecordEntityCacheHit("Asset", "PreBakedDisk");
                NexusMetrics.RecordEntityResolutionDuration(micro, "Asset", "PreBakedDisk", true);
                return new EntityLookupResult(true, "PreBakedDisk", micro, diskPath);
            }
        }

        // Check relative to solution override folder
        string defaultOverridePath = Path.Combine(Directory.GetCurrentDirectory(), "games", "client", "override",
            normalizedPath.Replace('/', Path.DirectorySeparatorChar));
        if (File.Exists(defaultOverridePath))
        {
            long micro = (long)(Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds * 1000);
            NexusMetrics.RecordEntityCacheHit("Asset", "PreBakedDisk");
            NexusMetrics.RecordEntityResolutionDuration(micro, "Asset", "PreBakedDisk", true);
            return new EntityLookupResult(true, "PreBakedDisk", micro, defaultOverridePath);
        }

        // -------------------------------------------------------------
        // Tier 3: VfsArchive (S2Z archives)
        // -------------------------------------------------------------
        if (!string.IsNullOrEmpty(_s2zArchivePath) && File.Exists(_s2zArchivePath))
            try
            {
                using ZipArchive archive = ZipFile.OpenRead(_s2zArchivePath);
                ZipArchiveEntry? entry = archive.GetEntry(normalizedPath);
                if (entry != null)
                {
                    long micro = (long)(Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds * 1000);
                    NexusMetrics.RecordEntityCacheHit("Asset", "VfsArchive");
                    NexusMetrics.RecordEntityResolutionDuration(micro, "Asset", "VfsArchive", true);
                    return new EntityLookupResult(true, "VfsArchive", micro, $"{_s2zArchivePath}#{normalizedPath}");
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[PrebakedEntityRegistry] Error inspecting archive '{Archive}'",
                    _s2zArchivePath);
            }

        // -------------------------------------------------------------
        // Tier 4: NotFound
        // -------------------------------------------------------------
        long notFoundMicro = (long)(Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds * 1000);
        NexusMetrics.RecordEntityCacheMiss("Asset");
        NexusMetrics.RecordEntityResolutionDuration(notFoundMicro, "Asset", "NotFound", false);
        return new EntityLookupResult(false, "NotFound", notFoundMicro, string.Empty);
    }

    public IEnumerable<HeroDefinitionDto> GetAllHeroes()
    {
        return HeroesById.Values;
    }

    public IEnumerable<AbilityDefinitionDto> GetAllAbilities()
    {
        return AbilitiesByName.Values.Distinct();
    }

    public IEnumerable<StateDefinitionDto> GetAllStates()
    {
        return StatesByName.Values.Distinct();
    }

    public IEnumerable<ProjectileDefinitionDto> GetAllProjectiles()
    {
        return ProjectilesByName.Values.Distinct();
    }

    public IEnumerable<ItemDefinitionDto> GetAllItems()
    {
        return ItemsByName.Values.Distinct();
    }

    public IEnumerable<CosmeticUpgradeDto> GetAllCosmetics()
    {
        return CosmeticsByName.Values.Distinct();
    }

    public IEnumerable<AltAvatarDto> GetAllAltAvatars()
    {
        return AltAvatarsByKey.Values.Distinct();
    }

    private void BuildDictionariesFromContainer(EntityCacheContainer container)
    {
        Container = container;

        Dictionary<uint, HeroDefinitionDto> heroesById = [];
        Dictionary<string, HeroDefinitionDto> heroesByName = new(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, AbilityDefinitionDto> abilitiesByName = new(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, StateDefinitionDto> statesByName = new(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, ProjectileDefinitionDto> projectilesByName = new(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, ItemDefinitionDto> itemsByName = new(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, CosmeticUpgradeDto> cosmeticsByName = new(StringComparer.OrdinalIgnoreCase);
        Dictionary<uint, CosmeticUpgradeDto> cosmeticsById = [];
        Dictionary<string, AltAvatarDto> altAvatarsByKey = new(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, EntityDefinitionDto> entitiesByName = new(StringComparer.OrdinalIgnoreCase);

        // 1. Index Heroes
        if (container.Heroes != null)
            foreach (HeroDefinitionDto hero in container.Heroes)
            {
                if (hero.Id > 0) heroesById[hero.Id] = hero;

                if (!string.IsNullOrWhiteSpace(hero.Name))
                {
                    heroesByName[hero.Name] = hero;

                    // Index alternate clean/short identifiers
                    string cleanName = hero.Name.Replace("Hero_", "", StringComparison.OrdinalIgnoreCase);
                    heroesByName.TryAdd(cleanName, hero);

                    // Add to generic entity catalog
                    entitiesByName[hero.Name] = new EntityDefinitionDto
                    {
                        Name = hero.Name,
                        EntityType = "Hero",
                        DisplayName = hero.DisplayName,
                        IconPath = !string.IsNullOrEmpty(hero.IconPath) ? hero.IconPath : hero.DefaultIcon,
                        ModelPath = !string.IsNullOrEmpty(hero.ModelPath) ? hero.ModelPath : hero.DefaultModel,
                        Properties = new Dictionary<string, string>
                        {
                            ["PrimaryAttribute"] = hero.PrimaryAttribute,
                            ["AttackType"] = hero.AttackType,
                            ["Difficulty"] = hero.Difficulty.ToString()
                        }
                    };
                }

                if (!string.IsNullOrWhiteSpace(hero.DisplayName)) heroesByName.TryAdd(hero.DisplayName, hero);

                // Index embedded hero abilities
                if (hero.Abilities != null)
                    foreach (AbilityDefinitionDto ability in hero.Abilities)
                        if (!string.IsNullOrWhiteSpace(ability.Name))
                        {
                            abilitiesByName[ability.Name] = ability;

                            entitiesByName.TryAdd(ability.Name, new EntityDefinitionDto
                            {
                                Name = ability.Name,
                                EntityType = "Ability",
                                DisplayName = ability.DisplayName,
                                IconPath = !string.IsNullOrEmpty(ability.IconPath) ? ability.IconPath : ability.Icon,
                                Properties = new Dictionary<string, string>
                                {
                                    ["ManaCost"] = ability.ManaCost,
                                    ["Cooldown"] = ability.Cooldown,
                                    ["ActionType"] = ability.ActionType
                                }
                            });
                        }

                // Index embedded alt avatars
                if (hero.AltAvatars != null)
                    foreach (AltAvatarDto avatar in hero.AltAvatars)
                        RegisterAltAvatar(avatar, hero.Name, altAvatarsByKey);
            }

        // 2. Index Standalone Alt Avatars
        if (container.AltAvatars != null)
            foreach (AltAvatarDto avatar in container.AltAvatars)
                RegisterAltAvatar(avatar, avatar.HeroName, altAvatarsByKey);

        // 3. Index Abilities
        if (container.Abilities != null)
            foreach (AbilityDefinitionDto ability in container.Abilities)
                if (!string.IsNullOrWhiteSpace(ability.Name))
                {
                    abilitiesByName[ability.Name] = ability;

                    entitiesByName[ability.Name] = new EntityDefinitionDto
                    {
                        Name = ability.Name,
                        EntityType = "Ability",
                        DisplayName = ability.DisplayName,
                        IconPath = !string.IsNullOrEmpty(ability.IconPath) ? ability.IconPath : ability.Icon,
                        Properties = new Dictionary<string, string>
                        {
                            ["ManaCost"] = ability.ManaCost,
                            ["Cooldown"] = ability.Cooldown,
                            ["ActionType"] = ability.ActionType
                        }
                    };
                }

        // 4. Index States
        if (container.States != null)
            foreach (StateDefinitionDto state in container.States)
                if (!string.IsNullOrWhiteSpace(state.Name))
                {
                    statesByName[state.Name] = state;

                    entitiesByName[state.Name] = new EntityDefinitionDto
                    {
                        Name = state.Name,
                        EntityType = "State",
                        IconPath = state.Icon,
                        Properties = new Dictionary<string, string>
                        {
                            ["EffectType"] = state.EffectType,
                            ["Duration"] = state.Duration.ToString(),
                            ["PassiveEffect"] = state.PassiveEffect
                        }
                    };
                }

        // 5. Index Projectiles
        if (container.Projectiles != null)
            foreach (ProjectileDefinitionDto projectile in container.Projectiles)
                if (!string.IsNullOrWhiteSpace(projectile.Name))
                {
                    projectilesByName[projectile.Name] = projectile;

                    entitiesByName[projectile.Name] = new EntityDefinitionDto
                    {
                        Name = projectile.Name,
                        EntityType = "Projectile",
                        ModelPath = projectile.Model,
                        Properties = new Dictionary<string, string>
                        {
                            ["Speed"] = projectile.Speed.ToString(),
                            ["Lifetime"] = projectile.LifeTime.ToString(),
                            ["ImpactEffect"] = projectile.ImpactEffect
                        }
                    };
                }

        // 6. Index Items
        if (container.Items != null)
            foreach (ItemDefinitionDto item in container.Items)
            {
                if (!string.IsNullOrWhiteSpace(item.Name))
                {
                    itemsByName[item.Name] = item;

                    entitiesByName[item.Name] = new EntityDefinitionDto
                    {
                        Name = item.Name,
                        EntityType = "Item",
                        DisplayName = item.DisplayName,
                        IconPath = !string.IsNullOrEmpty(item.IconPath) ? item.IconPath : item.Icon,
                        Properties = new Dictionary<string, string>
                        {
                            ["Cost"] = item.Cost.ToString(),
                            ["CooldownTime"] = item.CooldownTime.ToString()
                        }
                    };
                }

                if (!string.IsNullOrWhiteSpace(item.DisplayName)) itemsByName.TryAdd(item.DisplayName, item);
            }

        // 7. Index Cosmetic Upgrades
        if (container.CosmeticUpgrades != null)
            foreach (CosmeticUpgradeDto cosmetic in container.CosmeticUpgrades)
                if (!string.IsNullOrWhiteSpace(cosmetic.Name))
                {
                    cosmeticsByName[cosmetic.Name] = cosmetic;
                    if (uint.TryParse(cosmetic.Name, out uint cosId)) cosmeticsById[cosId] = cosmetic;

                    entitiesByName[cosmetic.Name] = new EntityDefinitionDto
                    {
                        Name = cosmetic.Name,
                        EntityType = "Cosmetic",
                        IconPath = cosmetic.Texture,
                        Properties = new Dictionary<string, string>
                        {
                            ["Category"] = cosmetic.Category,
                            ["Modifier"] = cosmetic.Modifier,
                            ["VoiceSet"] = cosmetic.VoiceSet
                        }
                    };
                }

        // Freeze all dictionaries for zero-allocation branch-free lookups
        HeroesById = heroesById.ToFrozenDictionary();
        HeroesByName = heroesByName.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);
        AbilitiesByName = abilitiesByName.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);
        StatesByName = statesByName.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);
        ProjectilesByName = projectilesByName.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);
        ItemsByName = itemsByName.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);
        CosmeticsByName = cosmeticsByName.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);
        _cosmeticsById = cosmeticsById.ToFrozenDictionary();
        AltAvatarsByKey = altAvatarsByKey.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);
        EntitiesByName = entitiesByName.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);
    }

    private static void RegisterAltAvatar(AltAvatarDto avatar, string heroName,
        Dictionary<string, AltAvatarDto> targetMap)
    {
        if (string.IsNullOrWhiteSpace(avatar.Key)) return;

        // Key directly
        targetMap.TryAdd(avatar.Key, avatar);

        // Composite key: "{HeroName}_{Key}" (e.g. "Hero_Accursed_Hero_Accursed.Alt")
        if (!string.IsNullOrWhiteSpace(heroName))
        {
            string composite1 = $"{heroName}_{avatar.Key}";
            targetMap.TryAdd(composite1, avatar);

            string cleanHero = heroName.Replace("Hero_", "", StringComparison.OrdinalIgnoreCase);
            string composite2 = $"{cleanHero}_{avatar.Key}";
            targetMap.TryAdd(composite2, avatar);
        }
    }

    private string? ResolveCacheFilePath(string? explicitPath)
    {
        if (!string.IsNullOrWhiteSpace(explicitPath) && File.Exists(explicitPath)) return explicitPath;

        List<string> candidatePaths = [];

        // 1. From Configuration
        if (_configuration != null)
        {
            string? cfgCache = _configuration["GameClient:EntityCachePath"] ??
                               _configuration["EntityCachePath"] ??
                               _configuration["GameClient:PreBakedAssetPath"] ??
                               _configuration["PreBakedAssetPath"];
            if (!string.IsNullOrWhiteSpace(cfgCache))
            {
                if (File.Exists(cfgCache)) candidatePaths.Add(cfgCache);
                string combined = Path.Combine(cfgCache, "entity_cache.bin");
                if (File.Exists(combined)) candidatePaths.Add(combined);
            }
        }

        // 2. BaseDirectory / override paths
        string baseDir = AppContext.BaseDirectory;
        candidatePaths.Add(Path.Combine(baseDir, "prebaked_assets", "entity_cache.bin"));
        candidatePaths.Add(Path.Combine(baseDir, "override", "prebaked_assets", "entity_cache.bin"));
        candidatePaths.Add(Path.Combine(baseDir, "entity_cache.bin"));

        // 3. Current Directory & Walk-Up
        DirectoryInfo? current = new(Directory.GetCurrentDirectory());
        for (int i = 0; i < 5 && current != null; i++)
        {
            candidatePaths.Add(Path.Combine(current.FullName, "games", "client", "override", "prebaked_assets",
                "entity_cache.bin"));
            candidatePaths.Add(Path.Combine(current.FullName, "games", "shared", "override", "prebaked_assets",
                "entity_cache.bin"));
            candidatePaths.Add(Path.Combine(current.FullName, "override", "prebaked_assets", "entity_cache.bin"));
            current = current.Parent;
        }

        return candidatePaths.FirstOrDefault(File.Exists);
    }
}
