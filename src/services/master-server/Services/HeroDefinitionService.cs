// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ReSharper disable LogMessageIsConditional

using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Text.Json;
using ASPIRE.Common.Telemetry;
using KONGOR.MasterServer.Serialization;
using NEXUS.Contracts.EntityCache;
using NEXUS.Contracts.Services;
using HeroesConstants = ASPIRE.Common.Constants.Heroes;

namespace KONGOR.MasterServer.Services;

public interface IHeroDefinitionService
{
    string GetHeroIdentifier(uint heroId);
    uint GetBaseHeroId(uint productId);
    uint GetBaseHeroId(string identifier);
    bool IsHero(uint heroId);
    IEnumerable<uint> GetAllHeroIds();

    string CleanHeroIdentifier(string? rawCode)
    {
        return HeroDefinitionService.CleanHeroIdentifier(rawCode);
    }

    HeroDefinitionDto? GetHeroDefinition(uint heroId)
    {
        return null;
    }

    HeroDefinitionDto? GetHeroDefinition(string identifier)
    {
        return null;
    }

    AltAvatarDto? GetAltAvatar(string heroName, string avatarKey)
    {
        return null;
    }

    IReadOnlyCollection<HeroDefinitionDto> GetAllHeroDefinitions()
    {
        return [];
    }
}

public class HeroDefinitionService : IHeroDefinitionService
{
    private static readonly FrozenDictionary<string, string> LegacyCodenameToCanonicalIdentifier =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "Hero_Torturer", "Hero_Xalynx" },
            { "Hero_Slither", "Hero_Ebulus" },
            { "Hero_BloodHunter", "Hero_Hunter" },
            { "Hero_Glacius", "Hero_Frosty" },
            { "Hero_Nymphora", "Hero_Fairy" },
            { "Hero_Swiftblade", "Hero_Hiro" },
            { "Hero_Madman", "Hero_Scar" },
            { "Hero_Magebringer", "Hero_Javaras" },
            { "Hero_Wildsoul", "Hero_Yogi" },
            { "Hero_Pebbles", "Hero_Rocky" },
            { "Hero_MoonQueen", "Hero_Krixi" },
            { "Hero_Gravekeeper", "Hero_Taint" },
            { "Hero_WarBeast", "Hero_WolfMan" },
            { "Hero_AlphaMale", "Hero_WitchSlayer" },
            { "Hero_Bangkok", "Hero_WitchSlayer" },
            { "Hero_TheGladiator", "Hero_Gladiator" },
            { "Hero_TheDarkLady", "Hero_Vanya" },
            { "Hero_DarkLady", "Hero_Vanya" },
            { "Hero_KeeperoftheForest", "Hero_Treant" },
            { "Hero_Keeper", "Hero_Treant" },
            { "Hero_VoodooJester", "Hero_Voodoo" },
            { "Hero_DementedShaman", "Hero_Shaman" },
            { "Hero_LordSalforis", "Hero_Dreadknight" },
            { "Hero_Salforis", "Hero_Dreadknight" },
            { "Hero_Pandamonium", "Hero_Panda" },
            { "Hero_Jeraziah", "Hero_Jereziah" }
        }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    private static readonly ConcurrentDictionary<string, string> CleanedIdentifierCache = new(StringComparer.Ordinal);
    private readonly IHostEnvironment _env; // To find ContentRootPath
    private readonly FrozenSet<uint> _heroIdsSet;
    private readonly FrozenDictionary<uint, string> _heroMappings;
    private readonly FrozenDictionary<string, uint> _identifierToBaseId;
    private readonly ILogger<HeroDefinitionService> _logger;
    private readonly IPrebakedEntityRegistry? _prebakedRegistry;

    public HeroDefinitionService(
        ILogger<HeroDefinitionService> logger,
        IHostEnvironment env,
        IPrebakedEntityRegistry? prebakedRegistry = null)
    {
        _logger = logger;
        _env = env;
        _prebakedRegistry = prebakedRegistry;
        Dictionary<uint, string> tempHeroMappings = [];
        Dictionary<string, uint> tempIdentifierToBaseId = new(StringComparer.OrdinalIgnoreCase);

        LoadBaseHeroes(tempHeroMappings, tempIdentifierToBaseId); // Pre-seed canonical base heroes first
        LoadDefinitions(tempHeroMappings, tempIdentifierToBaseId); // Load avatars/overrides from JSON second

        _heroMappings = tempHeroMappings.ToFrozenDictionary();
        _identifierToBaseId = tempIdentifierToBaseId
            .GroupBy(kvp => kvp.Key, StringComparer.OrdinalIgnoreCase)
            .ToFrozenDictionary(g => g.Key, g => g.First().Value, StringComparer.OrdinalIgnoreCase);
        _heroIdsSet = tempHeroMappings.Keys.ToFrozenSet();
    }

    public uint GetBaseHeroId(string identifier)
    {
        if (string.IsNullOrWhiteSpace(identifier)) return 0;

        if (_prebakedRegistry != null)
        {
            HeroDefinitionDto? prebakedHero = _prebakedRegistry.GetHeroByName(identifier);
            if (prebakedHero != null && prebakedHero.Id > 0) return prebakedHero.Id;
        }

        if (_identifierToBaseId.TryGetValue(identifier, out uint baseId))
        {
            NexusMetrics.RecordEntityCacheHit("Hero", "LegacyMemory");
            return baseId;
        }

        NexusMetrics.RecordEntityCacheMiss("Hero");
        return 0;
    }

    public uint GetBaseHeroId(uint productId)
    {
        // 1. Get Identifier for the product ID (e.g. 121 -> "Hero_Jereziah")
        string identifier = GetHeroIdentifier(productId);

        // 2. Look up the authoritative Base ID for this Identifier (e.g. "Hero_Jereziah" -> 12)
        if (_identifierToBaseId.TryGetValue(identifier, out uint baseId)) return baseId;

        // 3. String Parsing Fallback: Handle identifiers that might be Avatars (e.g. "Hero_Legionnaire.Alt1")
        // The authoritative dictionary only contains Base Heroes. We need to strip the specific avatar suffix using zero-allocation span slicing.
        ReadOnlySpan<char> span = identifier.AsSpan();
        int dotIndex = span.IndexOf('.');
        if (dotIndex >= 0)
        {
            string baseIdentifier = new(span[..dotIndex]);
            if (_identifierToBaseId.TryGetValue(baseIdentifier, out uint parsedBaseId)) return parsedBaseId;
        }

        // 4. Fallback: If no authoritative Base ID found, return the product ID itself
        // (This handles cases where the product ID IS the base ID, or we just don't know better)
        return productId;
    }

    public string GetHeroIdentifier(uint heroId)
    {
        if (_prebakedRegistry != null)
        {
            HeroDefinitionDto? prebakedHero = _prebakedRegistry.GetHeroById(heroId);
            if (prebakedHero != null && !string.IsNullOrWhiteSpace(prebakedHero.Name))
                return CleanHeroIdentifier(prebakedHero.Name);
        }

        if (_heroMappings.TryGetValue(heroId, out string? identifier))
        {
            NexusMetrics.RecordEntityCacheHit("Hero", "LegacyMemory");
            return CleanHeroIdentifier(identifier);
        }

        NexusMetrics.RecordEntityCacheMiss("Hero");
        if (_logger.IsEnabled(LogLevel.Warning)) _logger.LogWarning("Missing mapping for HeroID: {HeroID}", heroId);

        return "Hero_Legionnaire";
    }

    public bool IsHero(uint heroId)
    {
        if (_prebakedRegistry != null && _prebakedRegistry.HeroesById.ContainsKey(heroId)) return true;

        return _heroIdsSet.Contains(heroId);
    }

    public IEnumerable<uint> GetAllHeroIds()
    {
        if (_prebakedRegistry != null && _prebakedRegistry.HeroesById.Count > 0)
            return _heroIdsSet.Union(_prebakedRegistry.HeroesById.Keys);

        return _heroIdsSet;
    }

    public HeroDefinitionDto? GetHeroDefinition(uint heroId)
    {
        return _prebakedRegistry?.GetHeroById(heroId);
    }

    public HeroDefinitionDto? GetHeroDefinition(string identifier)
    {
        return _prebakedRegistry?.GetHeroByName(identifier);
    }

    public AltAvatarDto? GetAltAvatar(string heroName, string avatarKey)
    {
        return _prebakedRegistry?.GetAltAvatar(heroName, avatarKey);
    }

    public IReadOnlyCollection<HeroDefinitionDto> GetAllHeroDefinitions()
    {
        if (_prebakedRegistry != null && _prebakedRegistry.HeroesById.Count > 0)
            return _prebakedRegistry.HeroesById.Values;

        return [];
    }

    public static string CleanHeroIdentifier(string? rawCode)
    {
        if (string.IsNullOrWhiteSpace(rawCode)) return "Hero_Legionnaire";

        if (CleanedIdentifierCache.TryGetValue(rawCode, out string? cached)) return cached;

        string result = ComputeCleanHeroIdentifier(rawCode);
        CleanedIdentifierCache.TryAdd(rawCode, result);
        return result;
    }

    private static string ComputeCleanHeroIdentifier(string rawCode)
    {
        int dotIndex = rawCode.IndexOf('.');
        if (dotIndex > 0) rawCode = rawCode[..dotIndex];

        if (rawCode.StartsWith("Hero-", StringComparison.OrdinalIgnoreCase)) rawCode = rawCode[5..].Trim();

        rawCode = rawCode.Replace(" ", "");

        if (rawCode.StartsWith("Avatar_", StringComparison.OrdinalIgnoreCase))
        {
            string sub = rawCode[7..];
            int underscoreIdx = sub.IndexOf('_');
            if (underscoreIdx > 0) sub = sub[..underscoreIdx];

            rawCode = "Hero_" + sub;
        }

        if (!rawCode.StartsWith("Hero_", StringComparison.OrdinalIgnoreCase)) rawCode = "Hero_" + rawCode;

        return LegacyCodenameToCanonicalIdentifier.TryGetValue(rawCode, out string? canonical) ? canonical : rawCode;
    }

    private void LoadBaseHeroes(Dictionary<uint, string> heroMappings, Dictionary<string, uint> identifierToBaseId)
    {
        // ReSharper disable LogMessageIsConditional
        _logger.LogInformation("Loading Base Heroes from Constants.Heroes...");

        try
        {
            Type heroesType = typeof(HeroesConstants);
            int count = 0;

            foreach (Type nestedType in heroesType.GetNestedTypes())
            {
                FieldInfo? idField = nestedType.GetField("HeroId");
                FieldInfo? identifierField = nestedType.GetField("Identifier");

                if (idField != null && identifierField != null)
                {
                    uint id = (uint)idField.GetValue(null)!;
                    string identifier = (string)identifierField.GetValue(null)!;

                    if (id > 0 && !string.IsNullOrEmpty(identifier))
                    {
                        heroMappings[id] = identifier;
                        identifierToBaseId[identifier] = id; // Cache reverse mapping
                        count++;
                    }
                }
            }

            if (_logger.IsEnabled(LogLevel.Information))
                _logger.LogInformation("Loaded {Count} base heroes from Constants (Total Mapped: {Total})", count,
                    heroMappings.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load base heroes from Constants.Heroes");
        }

        // Manual fallbacks for IDs not yet in Constants.Heroes
        Dictionary<uint, string> manualBaseHeroes = new()
        {
            { 0, "Hero_Legionnaire" },
            // Legion
            { 3, "Hero_Vindicator" },
            { 4, "Hero_Scout" },
            { 6, "Hero_Engineer" },
            { 7, "Hero_Rocky" },
            { 8, "Hero_Chipper" },
            { 9, "Hero_Bubbles" },
            { 11, "Hero_Tremble" },
            { 12, "Hero_Jereziah" },
            { 14, "Hero_Yogi" },
            { 16, "Hero_Scar" },
            { 17, "Hero_Javaras" },
            { 18, "Hero_Chronos" },
            { 19, "Hero_Ravenor" },
            { 20, "Hero_Rampage" },
            { 21, "Hero_Chronos" },
            { 24, "Hero_Tundra" },
            { 25, "Hero_Fade" },
            { 26, "Hero_Pyromancer" },
            { 27, "Hero_Voodoo" },
            { 32, "Hero_Scout" },
            { 34, "Hero_Valkyrie" },
            { 36, "Hero_Fairy" },
            { 40, "Hero_Tempest" },
            { 42, "Hero_DwarfMagi" },
            { 44, "Hero_WitchSlayer" },
            { 10, "Hero_Hammerstorm" },
            { 13, "Hero_Krixi" },
            { 22, "Hero_Hiro" },
            { 96, "Hero_DoctorRepulsor" },
            // Hellbourne
            { 91, "Hero_Deadwood" },
            { 92, "Hero_Defiler" },
            { 93, "Hero_Devourer" },
            { 94, "Hero_Electrician" },
            { 95, "Hero_Kinesis" },
            { 103, "Hero_Frosty" },
            { 104, "Hero_Ophelia" },
            { 105, "Hero_Gravekeeper" },
            { 106, "Hero_Hellbringer" },
            { 109, "Hero_Hunter" },
            { 110, "Hero_Hydromancer" },
            { 111, "Hero_WitchSlayer" },
            { 112, "Hero_Ra" },
            { 114, "Hero_Armadon" },
            { 115, "Hero_Behemoth" },
            { 116, "Hero_Hammerstorm" },
            { 119, "Hero_Vanya" },
            { 120, "Hero_Predator" },
            { 121, "Hero_Jereziah" },
            { 122, "Hero_Panda" },
            { 123, "Hero_Rampage" },
            { 124, "Hero_Tundra" },
            { 125, "Hero_Gladiator" },
            { 126, "Hero_Chronos" },
            // Common IDs
            { 153, "Hero_Accursed" },
            { 155, "Hero_Maliken" },
            { 156, "Hero_Magmar" },
            { 152, "Hero_PuppetMaster" },
            { 162, "Hero_Shaman" },
            { 168, "Hero_Ebulus" },
            { 172, "Hero_Soulstealer" },
            { 173, "Hero_Succubus" },
            { 178, "Hero_Xalynx" },
            { 185, "Hero_WitchSlayer" },
            // Other
            { 671, "Hero_MonkeyKing" },
            { 227, "Hero_Solstice" },
            { 228, "Hero_EmeraldWarden" },
            { 232, "Hero_Midas" },
            { 236, "Hero_Nomad" },
            { 240, "Hero_Artesia" },
            { 245, "Hero_Silhouette" },
            { 246, "Hero_Gemini" },
            { 249, "Hero_Berzerker" },
            { 250, "Hero_FlameDragon" },
            { 43, "Hero_FlameDragon" }
        };

        foreach (KeyValuePair<uint, string> kvp in manualBaseHeroes.Where(kvp => !heroMappings.ContainsKey(kvp.Key)))
        {
            heroMappings[kvp.Key] = kvp.Value;
            if (!identifierToBaseId.ContainsKey(kvp.Value)) identifierToBaseId[kvp.Value] = kvp.Key;
        }
        if (!identifierToBaseId.ContainsKey("Hero_Draconis")) identifierToBaseId["Hero_Draconis"] = 43;

        List<string> allIdentifiers = HeroesConstants.AllHeroIdentifiers();
        uint fallbackIdCounter = 300;
        foreach (string identifier in allIdentifiers)
            if (!identifierToBaseId.ContainsKey(identifier))
            {
                uint foundId = heroMappings
                    .FirstOrDefault(x => x.Value.Equals(identifier, StringComparison.OrdinalIgnoreCase)).Key;
                if (foundId == 0)
                {
                    while (heroMappings.ContainsKey(fallbackIdCounter)) fallbackIdCounter++;

                    foundId = fallbackIdCounter;
                    heroMappings[foundId] = identifier;
                }

                identifierToBaseId[identifier] = foundId;
            }
    }

    private void LoadDefinitions(Dictionary<uint, string> heroMappings, Dictionary<string, uint> identifierToBaseId)
    {
        string[] possiblePaths =
        [
            Path.Combine(_env.ContentRootPath, "Data", "Seed", "Upgrades.JSON"),
            Path.Combine(AppContext.BaseDirectory, "Data", "Seed", "Upgrades.JSON"),
            Path.Combine(Directory.GetCurrentDirectory(), "Data", "Seed", "Upgrades.JSON")
        ];

        string? path = possiblePaths.FirstOrDefault(File.Exists);

        if (path == null)
        {
            if (_logger.IsEnabled(LogLevel.Critical))
                _logger.LogCritical("Upgrades.JSON NOT FOUND! Checked: {Paths}", string.Join(", ", possiblePaths));

            return;
        }

        if (_logger.IsEnabled(LogLevel.Information))
            _logger.LogInformation("Loading Hero Definitions from: {Path}", path);

        try
        {
            string json = File.ReadAllText(path);
            List<UpgradeDefinition>? upgrades = JsonSerializer.Deserialize(json, MasterServerJsonContext.Default.ListUpgradeDefinition);

            if (upgrades == null)
            {
                _logger.LogError("Failed to deserialize Upgrades.JSON (Result was null).");
                return;
            }

            int count = 0;
            foreach (UpgradeDefinition upgrade in upgrades)
                if (uint.TryParse(upgrade.UpgradeId, out uint id))
                {
                    string targetIdentifier = "";

                    // Extract base hero folder from LocalContent if present (e.g. "/heroes/witch_slayer/alt7/icon.tga" or "/heroes/xalynx/icon.tga")
                    if (!string.IsNullOrEmpty(upgrade.LocalContent) &&
                        upgrade.LocalContent.StartsWith("/heroes/", StringComparison.OrdinalIgnoreCase))
                    {
                        string[] parts = upgrade.LocalContent.Split('/', StringSplitOptions.RemoveEmptyEntries);
                        if (parts.Length >= 2)
                        {
                            string folder = parts[1]; // e.g. "witch_slayer" or "xalynx"
                            string rawIdent = "Hero_" + folder;
                            string cleanIdent = CleanHeroIdentifier(rawIdent);

                            if (identifierToBaseId.TryGetValue(cleanIdent, out uint baseHeroId) &&
                                heroMappings.TryGetValue(baseHeroId, out string? canonicalName))
                            {
                                targetIdentifier = canonicalName;
                            }
                            else
                            {
                                string cleanFolder = folder.Replace("_", "");
                                string candidate2 = CleanHeroIdentifier("Hero_" + cleanFolder);
                                if (identifierToBaseId.TryGetValue(candidate2, out uint baseId2) &&
                                    heroMappings.TryGetValue(baseId2, out string? canonicalName2))
                                    targetIdentifier = canonicalName2;
                            }
                        }
                    }

                    if (string.IsNullOrEmpty(targetIdentifier))
                        targetIdentifier = !string.IsNullOrEmpty(upgrade.Code)
                            ? CleanHeroIdentifier(upgrade.Code)
                            : "Hero_Legionnaire";

                    if (heroMappings.TryAdd(id, targetIdentifier)) count++;
                }

            if (_logger.IsEnabled(LogLevel.Information))
                _logger.LogInformation("Loaded {Count} hero mappings from JSON (Total Mapped: {Total})", count,
                    heroMappings.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load hero mappings from: {Path}", path);
        }

        string[] storeItemPaths =
        [
            Path.Combine(_env.ContentRootPath, "Configuration", "Store", "StoreItemsConfiguration.json"),
            Path.Combine(AppContext.BaseDirectory, "Configuration", "Store", "StoreItemsConfiguration.json"),
            Path.Combine(Directory.GetCurrentDirectory(), "Configuration", "Store", "StoreItemsConfiguration.json")
        ];

        string? storePath = storeItemPaths.FirstOrDefault(File.Exists);

        if (storePath != null)
            try
            {
                string json = File.ReadAllText(storePath);
                List<StoreItemDefinition>? storeItems =
                    JsonSerializer.Deserialize(json, MasterServerJsonContext.Default.ListStoreItemDefinition);

                if (storeItems != null)
                {
                    int storeCount = 0;
                    foreach (StoreItemDefinition item in storeItems)
                        if (item.ID > 0)
                        {
                            string targetIdentifier = "";

                            if (!string.IsNullOrEmpty(item.Resource) &&
                                item.Resource.StartsWith("/heroes/", StringComparison.OrdinalIgnoreCase))
                            {
                                string[] parts = item.Resource.Split('/', StringSplitOptions.RemoveEmptyEntries);
                                if (parts.Length >= 2)
                                {
                                    string folder = parts[1];
                                    string rawIdent = "Hero_" + folder;
                                    string cleanIdent = CleanHeroIdentifier(rawIdent);

                                    if (identifierToBaseId.TryGetValue(cleanIdent, out uint baseHeroId) &&
                                        heroMappings.TryGetValue(baseHeroId, out string? canonicalName))
                                    {
                                        targetIdentifier = canonicalName;
                                    }
                                    else
                                    {
                                        string cleanFolder = folder.Replace("_", "");
                                        string candidate2 = CleanHeroIdentifier("Hero_" + cleanFolder);
                                        if (identifierToBaseId.TryGetValue(candidate2, out uint baseId2) &&
                                            heroMappings.TryGetValue(baseId2, out string? canonicalName2))
                                            targetIdentifier = canonicalName2;
                                    }
                                }
                            }

                            if (string.IsNullOrEmpty(targetIdentifier) && !string.IsNullOrEmpty(item.Code))
                                targetIdentifier = CleanHeroIdentifier(item.Code);

                            if (!string.IsNullOrEmpty(targetIdentifier) && !heroMappings.ContainsKey((uint)item.ID))
                            {
                                heroMappings[(uint)item.ID] = targetIdentifier;
                                storeCount++;
                            }
                        }

                    if (_logger.IsEnabled(LogLevel.Information))
                        _logger.LogInformation(
                            "Loaded {Count} store hero mappings from StoreItemsConfiguration.json (Total Mapped: {Total})",
                            storeCount, heroMappings.Count);
                }
            }
            catch (Exception ex)
            {
                if (_logger.IsEnabled(LogLevel.Error))
                    _logger.LogError(ex, "Error loading StoreItemsConfiguration.json.");
            }
    }

    // ReSharper disable ClassNeverInstantiated.Local
    // ReSharper disable AutoPropertyCanBeMadeGetOnly.Local
    // ReSharper disable UnusedAutoPropertyAccessor.Local
    public class UpgradeDefinition
    {
        public string UpgradeId { get; set; } = string.Empty;
        public int UpgradeType { get; set; }
        public string Code { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string LocalContent { get; set; } = string.Empty;
    }

    public class StoreItemDefinition
    {
        public int ID { get; set; }
        public string Code { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string Resource { get; set; } = string.Empty;
    }
}
