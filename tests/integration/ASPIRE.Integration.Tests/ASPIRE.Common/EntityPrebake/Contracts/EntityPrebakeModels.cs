// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using MemoryPack;

namespace ASPIRE.Tests.ASPIRE.Common.EntityPrebake.Contracts;

[MemoryPackable]
public partial class EntityCacheContainer
{
    public uint Version { get; set; } = 1;
    public string BuildTimestamp { get; set; } = string.Empty;
    public List<HeroDefinitionDto> Heroes { get; set; } = [];
    public List<AltAvatarDto> AltAvatars { get; set; } = [];
    public List<AbilityDefinitionDto> Abilities { get; set; } = [];
    public List<StateDefinitionDto> States { get; set; } = [];
    public List<ProjectileDefinitionDto> Projectiles { get; set; } = [];
    public List<ItemDefinitionDto> Items { get; set; } = [];
}

[MemoryPackable]
public partial class HeroDefinitionDto
{
    public uint Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string PrimaryAttribute { get; set; } = string.Empty;
    public string IconPath { get; set; } = string.Empty;
    public string ModelPath { get; set; } = string.Empty;
    public List<string> AbilityNames { get; set; } = [];
    public List<string> AltAvatarKeys { get; set; } = [];
    public List<string> PreloadResources { get; set; } = [];
}

[MemoryPackable]
public partial class AltAvatarDto
{
    public string Key { get; set; } = string.Empty;
    public string HeroName { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string IconPath { get; set; } = string.Empty;
    public string ModelPath { get; set; } = string.Empty;
    public int Priority { get; set; } = 0;
    public List<string> Modifiers { get; set; } = [];
    public List<string> PreloadResources { get; set; } = [];
}

[MemoryPackable]
public partial class AbilityDefinitionDto
{
    public string Name { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string IconPath { get; set; } = string.Empty;
    public string ManaCost { get; set; } = string.Empty;
    public string Cooldown { get; set; } = string.Empty;
    public string ActionType { get; set; } = string.Empty;
    public List<string> RequiredResources { get; set; } = [];
}

[MemoryPackable]
public partial class StateDefinitionDto
{
    public string Name { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string IconPath { get; set; } = string.Empty;
    public string EffectPath { get; set; } = string.Empty;
    public bool IsDebuff { get; set; }
    public float Duration { get; set; }
}

[MemoryPackable]
public partial class ProjectileDefinitionDto
{
    public string Name { get; set; } = string.Empty;
    public string ModelPath { get; set; } = string.Empty;
    public string EffectPath { get; set; } = string.Empty;
    public float Speed { get; set; }
    public float Lifetime { get; set; }
}

[MemoryPackable]
public partial class ItemDefinitionDto
{
    public uint Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public int Cost { get; set; }
    public string IconPath { get; set; } = string.Empty;
    public List<string> Components { get; set; } = [];
}

[MemoryPackable]
public partial class EntityDefinitionDto
{
    public string Name { get; set; } = string.Empty;
    public string EntityType { get; set; } = string.Empty; // "Hero", "Ability", "State", "Projectile", "Item"
    public string SourcePath { get; set; } = string.Empty;
    public Dictionary<string, string> Properties { get; set; } = [];
}

public readonly record struct EntityLookupResult(
    bool Found,
    string SourceTier, // "PreBakedMemory", "PreBakedDisk", "VfsArchive", "NotFound"
    long LatencyMicroseconds,
    string ResolvedPath
);

public interface IPrebakedEntityRegistry
{
    bool IsPrewarmed { get; }
    int TotalEntitiesLoaded { get; }
    ValueTask InitializeAsync(CancellationToken cancellationToken = default);
    HeroDefinitionDto? GetHeroById(uint heroId);
    HeroDefinitionDto? GetHeroByName(string heroName);
    EntityDefinitionDto? GetEntityByName(string entityName);
    AltAvatarDto? GetAltAvatar(string heroName, string avatarKey);
    EntityLookupResult ResolveAsset(string resourcePath);
}

public sealed class PrebakeCliOptions
{
    public string InputPath { get; set; } = string.Empty;
    public string OutputPath { get; set; } = string.Empty;
    public bool CompressWithZstd { get; set; } = true;
    public int ZstdCompressionLevel { get; set; } = 3;
    public bool EmitLuaManifest { get; set; } = true;
    public string LuaOutputPath { get; set; } = string.Empty;
    public bool CreateS2zArchive { get; set; } = true;
    public string S2zOutputPath { get; set; } = string.Empty;
}

public sealed class PrebakeTelemetryRecord
{
    public DateTime TimestampUtc { get; set; } = TimeProvider.System.GetUtcNow().UtcDateTime;
    public string EventType { get; set; } = string.Empty; // "CacheHit", "CacheMiss", "PrewarmCompleted", "Resolution"
    public string EntityName { get; set; } = string.Empty;
    public string SourceTier { get; set; } = string.Empty;
    public long DurationMicroseconds { get; set; }
    public bool Success { get; set; }
    public string? ErrorMessage { get; set; }
}
