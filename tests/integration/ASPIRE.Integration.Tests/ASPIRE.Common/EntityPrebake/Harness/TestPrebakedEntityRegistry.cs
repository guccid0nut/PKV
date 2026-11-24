// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Collections.Frozen;
using System.Diagnostics;
using System.IO.Compression;
using ASPIRE.Tests.ASPIRE.Common.EntityPrebake.Contracts;

namespace ASPIRE.Tests.ASPIRE.Common.EntityPrebake.Harness;

public sealed class TestPrebakedEntityRegistry(
    EntityCacheContainer? sourceContainer = null,
    string? overrideDirectory = null,
    string? s2zArchivePath = null,
    int simulatedPrewarmDelayMs = 0) : IPrebakedEntityRegistry, IDisposable
{
    private readonly string? _overrideDirectory = overrideDirectory;
    private readonly string? _s2zArchivePath = s2zArchivePath;
    private readonly int _simulatedPrewarmDelayMs = simulatedPrewarmDelayMs;

    private readonly EntityCacheContainer? _sourceContainer = sourceContainer;
    private readonly ConcurrentBag<PrebakeTelemetryRecord> _telemetryRecords = [];
    private FrozenDictionary<string, AltAvatarDto> _altAvatars = FrozenDictionary<string, AltAvatarDto>.Empty;

    private FrozenDictionary<string, EntityDefinitionDto> _entitiesByName =
        FrozenDictionary<string, EntityDefinitionDto>.Empty;

    private FrozenDictionary<uint, HeroDefinitionDto> _heroesById = FrozenDictionary<uint, HeroDefinitionDto>.Empty;

    private FrozenDictionary<string, HeroDefinitionDto> _heroesByName =
        FrozenDictionary<string, HeroDefinitionDto>.Empty;

    private int _isPrewarmedState;
    public IReadOnlyCollection<PrebakeTelemetryRecord> TelemetryRecords => _telemetryRecords;

    public void Dispose()
    {
        _telemetryRecords.Clear();
    }

    public bool IsPrewarmed => Volatile.Read(ref _isPrewarmedState) == 1;
    public int TotalEntitiesLoaded => _heroesById.Count + _altAvatars.Count + _entitiesByName.Count;

    public async ValueTask InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (Interlocked.CompareExchange(ref _isPrewarmedState, 0, 0) == 1) return;

        long startTimestamp = Stopwatch.GetTimestamp();

        await Task.Run(async () =>
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (_simulatedPrewarmDelayMs > 0) await Task.Delay(_simulatedPrewarmDelayMs, cancellationToken);

            if (_sourceContainer != null)
            {
                Dictionary<uint, HeroDefinitionDto> idMap = [];
                Dictionary<string, HeroDefinitionDto> nameMap = new(StringComparer.OrdinalIgnoreCase);
                Dictionary<string, AltAvatarDto> avatarMap = new(StringComparer.OrdinalIgnoreCase);
                Dictionary<string, EntityDefinitionDto> entityMap = new(StringComparer.OrdinalIgnoreCase);

                foreach (HeroDefinitionDto hero in _sourceContainer.Heroes)
                {
                    idMap[hero.Id] = hero;
                    nameMap[hero.Name] = hero;
                }

                foreach (AltAvatarDto avatar in _sourceContainer.AltAvatars)
                {
                    string compositeKey = $"{avatar.HeroName}_{avatar.Key}";
                    avatarMap[compositeKey] = avatar;
                }

                foreach (AbilityDefinitionDto ability in _sourceContainer.Abilities)
                    entityMap[ability.Name] = new EntityDefinitionDto
                    {
                        Name = ability.Name,
                        EntityType = "Ability",
                        Properties = new Dictionary<string, string>
                        {
                            ["DisplayName"] = ability.DisplayName,
                            ["ManaCost"] = ability.ManaCost,
                            ["Cooldown"] = ability.Cooldown
                        }
                    };

                foreach (StateDefinitionDto state in _sourceContainer.States)
                    entityMap[state.Name] = new EntityDefinitionDto
                    {
                        Name = state.Name,
                        EntityType = "State",
                        Properties = new Dictionary<string, string>
                        {
                            ["DisplayName"] = state.DisplayName,
                            ["Duration"] = state.Duration.ToString()
                        }
                    };

                foreach (ProjectileDefinitionDto projectile in _sourceContainer.Projectiles)
                    entityMap[projectile.Name] = new EntityDefinitionDto
                    {
                        Name = projectile.Name,
                        EntityType = "Projectile",
                        Properties = new Dictionary<string, string>
                        {
                            ["Speed"] = projectile.Speed.ToString(),
                            ["Lifetime"] = projectile.Lifetime.ToString()
                        }
                    };

                foreach (ItemDefinitionDto item in _sourceContainer.Items)
                    entityMap[item.Name] = new EntityDefinitionDto
                    {
                        Name = item.Name,
                        EntityType = "Item",
                        Properties = new Dictionary<string, string>
                        {
                            ["DisplayName"] = item.DisplayName,
                            ["Cost"] = item.Cost.ToString()
                        }
                    };

                _heroesById = idMap.ToFrozenDictionary();
                _heroesByName = nameMap.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);
                _altAvatars = avatarMap.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);
                _entitiesByName = entityMap.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);
            }

            Interlocked.Exchange(ref _isPrewarmedState, 1);
        }, cancellationToken);

        TimeSpan elapsed = Stopwatch.GetElapsedTime(startTimestamp);
        _telemetryRecords.Add(new PrebakeTelemetryRecord
        {
            EventType = "PrewarmCompleted",
            EntityName = "CatalogRegistry",
            SourceTier = "PreBakedMemory",
            DurationMicroseconds = (long)(elapsed.TotalMilliseconds * 1000),
            Success = true
        });
    }

    public HeroDefinitionDto? GetHeroById(uint heroId)
    {
        long start = Stopwatch.GetTimestamp();
        bool found = _heroesById.TryGetValue(heroId, out HeroDefinitionDto? hero);
        long elapsedMicro = (long)(Stopwatch.GetElapsedTime(start).TotalMilliseconds * 1000);

        _telemetryRecords.Add(new PrebakeTelemetryRecord
        {
            EventType = found ? "CacheHit" : "CacheMiss",
            EntityName = heroId.ToString(),
            SourceTier = "PreBakedMemory",
            DurationMicroseconds = elapsedMicro,
            Success = found
        });

        return hero;
    }

    public HeroDefinitionDto? GetHeroByName(string heroName)
    {
        if (string.IsNullOrEmpty(heroName)) return null;

        long start = Stopwatch.GetTimestamp();
        bool found = _heroesByName.TryGetValue(heroName, out HeroDefinitionDto? hero);
        long elapsedMicro = (long)(Stopwatch.GetElapsedTime(start).TotalMilliseconds * 1000);

        _telemetryRecords.Add(new PrebakeTelemetryRecord
        {
            EventType = found ? "CacheHit" : "CacheMiss",
            EntityName = heroName,
            SourceTier = "PreBakedMemory",
            DurationMicroseconds = elapsedMicro,
            Success = found
        });

        return hero;
    }

    public EntityDefinitionDto? GetEntityByName(string entityName)
    {
        if (string.IsNullOrEmpty(entityName)) return null;

        long start = Stopwatch.GetTimestamp();
        bool found = _entitiesByName.TryGetValue(entityName, out EntityDefinitionDto? entity);
        long elapsedMicro = (long)(Stopwatch.GetElapsedTime(start).TotalMilliseconds * 1000);

        _telemetryRecords.Add(new PrebakeTelemetryRecord
        {
            EventType = found ? "CacheHit" : "CacheMiss",
            EntityName = entityName,
            SourceTier = "PreBakedMemory",
            DurationMicroseconds = elapsedMicro,
            Success = found
        });

        return entity;
    }

    public AltAvatarDto? GetAltAvatar(string heroName, string avatarKey)
    {
        if (string.IsNullOrEmpty(heroName) || string.IsNullOrEmpty(avatarKey)) return null;

        string compositeKey = $"{heroName}_{avatarKey}";
        long start = Stopwatch.GetTimestamp();
        bool found = _altAvatars.TryGetValue(compositeKey, out AltAvatarDto? avatar);
        long elapsedMicro = (long)(Stopwatch.GetElapsedTime(start).TotalMilliseconds * 1000);

        _telemetryRecords.Add(new PrebakeTelemetryRecord
        {
            EventType = found ? "CacheHit" : "CacheMiss",
            EntityName = compositeKey,
            SourceTier = "PreBakedMemory",
            DurationMicroseconds = elapsedMicro,
            Success = found
        });

        return avatar;
    }

    public EntityLookupResult ResolveAsset(string resourcePath)
    {
        if (string.IsNullOrWhiteSpace(resourcePath)) return new EntityLookupResult(false, "NotFound", 0, string.Empty);

        string normalizedPath = resourcePath.Replace('\\', '/').TrimStart('/');
        long start = Stopwatch.GetTimestamp();

        // 1. Check in-memory prewarmed entities
        if (_heroesByName.TryGetValue(normalizedPath, out HeroDefinitionDto? hero))
        {
            long micro = (long)(Stopwatch.GetElapsedTime(start).TotalMilliseconds * 1000);
            return new EntityLookupResult(true, "PreBakedMemory", micro, hero.ModelPath);
        }

        // 2. Check loose file override on disk
        if (!string.IsNullOrEmpty(_overrideDirectory))
        {
            string diskRelative = normalizedPath.Replace('/', Path.DirectorySeparatorChar);
            string diskPath = Path.Combine(_overrideDirectory, diskRelative);
            if (File.Exists(diskPath))
            {
                long micro = (long)(Stopwatch.GetElapsedTime(start).TotalMilliseconds * 1000);
                return new EntityLookupResult(true, "PreBakedDisk", micro, diskPath);
            }
        }

        // 3. Check S2Z archive
        if (!string.IsNullOrEmpty(_s2zArchivePath) && File.Exists(_s2zArchivePath))
        {
            using ZipArchive archive = ZipFile.OpenRead(_s2zArchivePath);
            ZipArchiveEntry? entry = archive.GetEntry(normalizedPath);
            if (entry != null)
            {
                long micro = (long)(Stopwatch.GetElapsedTime(start).TotalMilliseconds * 1000);
                return new EntityLookupResult(true, "VfsArchive", micro, $"{_s2zArchivePath}#{normalizedPath}");
            }
        }

        long notFoundMicro = (long)(Stopwatch.GetElapsedTime(start).TotalMilliseconds * 1000);
        return new EntityLookupResult(false, "NotFound", notFoundMicro, string.Empty);
    }
}
