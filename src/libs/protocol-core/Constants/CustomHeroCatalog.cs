// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Collections.Frozen;

namespace ASPIRE.Common.Constants;

/// <summary>
///     Descriptor for a map-scoped custom hero/unit entity (e.g., Enfo's Team Survival classes,
///     Archer Wars, Prophets Warlock, Nightmarish Co-Op converter) that is not registered in the
///     global FE2 store/hero catalog and therefore resolves to <c>HeroID = 0</c> outside matches.
/// </summary>
public sealed record CustomHeroDefinition(
    string CanonicalIdentifier,
    string DisplayName,
    string PortraitIconVfs,
    string FallbackBaseHeroIdentifier);

/// <summary>
///     Single source of truth for map-scoped custom hero entities across backend ingestion,
///     Hero Mastery exclusion, PostgreSQL custom stats JSON, and K2 FE2 UI fallback rendering.
/// </summary>
public static class CustomHeroCatalog
{
    private static readonly IReadOnlyList<CustomHeroDefinition> AllDefinitions =
    [
        new("Enfos_Hero_Ahlen", "Ahlen", "/heroes/hunter/icon.tga", "Hero_Hunter"),
        new("Enfos_Hero_ArcaneMistress", "Arcane Mistress", "/heroes/frosty/alt2/icon.tga", "Hero_Frosty"),
        new("Enfos_Hero_BloodDancer", "Blood Dancer", "/heroes/hiro/alt/icon.tga", "Hero_Hiro"),
        new("Enfos_Hero_Paladin", "Paladin", "/heroes/jereziah/reskin/icon.tga", "Hero_Jereziah"),
        new("Enfos_Hero_Ranger", "Ranger", "/heroes/forsaken_archer/alt/icon.tga", "Hero_ForsakenArcher"),
        new("Enfos_Hero_Thief", "Thief", "/heroes/javaras/alt/icon.tga", "Hero_Javaras"),
        new("Enfos_Hero_TimeMage", "Time Mage", "/heroes/riftmage/icon.tga", "Hero_Riftmage"),
        new("Enfos_Hero_Uther", "Uther", "/heroes/hammerstorm/icon.tga", "Hero_Hammerstorm"),
        new("Hero_Enfos_Dummy", "Training Dummy", "/heroes/prophet/icon.tga", "Hero_Prophet"),
        new("Hero_Archer", "Forsaken Archer", "/heroes/forsaken_archer/alt/icon.tga", "Hero_ForsakenArcher"),
        new("wl_Warlock", "Prophet Warlock", "/heroes/defiler/icon.tga", "Hero_Defiler"),
        new("Pet_Convert_Player", "Co-Op Defender", "/heroes/legionnaire/icon.tga", "Hero_Legionnaire")
    ];

    private static readonly FrozenDictionary<string, CustomHeroDefinition> Lookup = BuildLookup();

    private static FrozenDictionary<string, CustomHeroDefinition> BuildLookup()
    {
        Dictionary<string, CustomHeroDefinition> map = new(StringComparer.OrdinalIgnoreCase);
        foreach (CustomHeroDefinition def in AllDefinitions)
        {
            map[def.CanonicalIdentifier] = def;
            if (!def.CanonicalIdentifier.StartsWith("Hero_", StringComparison.OrdinalIgnoreCase))
            {
                map["Hero_" + def.CanonicalIdentifier] = def;
            }

            if (def.CanonicalIdentifier.StartsWith("Enfos_Hero_", StringComparison.OrdinalIgnoreCase))
            {
                string suffix = def.CanonicalIdentifier["Enfos_Hero_".Length..];
                map["Enfos_CHS_" + suffix] = def;
                map["Hero_Enfos_CHS_" + suffix] = def;
            }
        }

        return map.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);
    }

    public static bool TryResolve(string? rawIdentifier, out CustomHeroDefinition definition)
    {
        if (string.IsNullOrWhiteSpace(rawIdentifier))
        {
            definition = null!;
            return false;
        }

        string trimmed = rawIdentifier.Trim();
        int dotIdx = trimmed.IndexOf('.');
        if (dotIdx > 0)
        {
            trimmed = trimmed[..dotIdx];
        }

        return Lookup.TryGetValue(trimmed, out definition!);
    }

    public static bool IsCustomMapUnit(string? rawIdentifier)
    {
        if (string.IsNullOrWhiteSpace(rawIdentifier))
        {
            return false;
        }

        if (TryResolve(rawIdentifier, out _))
        {
            return true;
        }

        string trimmed = rawIdentifier.Trim();
        return trimmed.StartsWith("Enfos_", StringComparison.OrdinalIgnoreCase) ||
               trimmed.StartsWith("Hero_Enfos_", StringComparison.OrdinalIgnoreCase) ||
               trimmed.StartsWith("arena_Hero_", StringComparison.OrdinalIgnoreCase) ||
               trimmed.StartsWith("Hero_arena_Hero_", StringComparison.OrdinalIgnoreCase) ||
               trimmed.StartsWith("wl_", StringComparison.OrdinalIgnoreCase) ||
               trimmed.StartsWith("Hero_wl_", StringComparison.OrdinalIgnoreCase) ||
               trimmed.StartsWith("Pet_", StringComparison.OrdinalIgnoreCase) ||
               trimmed.StartsWith("Hero_Pet_", StringComparison.OrdinalIgnoreCase);
    }

    public static bool TryStripArenaHeroPrefix(string? rawIdentifier, out string baseHeroIdentifier)
    {
        if (string.IsNullOrWhiteSpace(rawIdentifier))
        {
            baseHeroIdentifier = string.Empty;
            return false;
        }

        string trimmed = rawIdentifier.Trim();
        int dotIdx = trimmed.IndexOf('.');
        if (dotIdx > 0)
        {
            trimmed = trimmed[..dotIdx];
        }

        if (trimmed.StartsWith("Hero_arena_Hero_", StringComparison.OrdinalIgnoreCase))
        {
            baseHeroIdentifier = "Hero_" + trimmed["Hero_arena_Hero_".Length..];
            return true;
        }

        if (trimmed.StartsWith("arena_Hero_", StringComparison.OrdinalIgnoreCase))
        {
            baseHeroIdentifier = "Hero_" + trimmed["arena_Hero_".Length..];
            return true;
        }

        baseHeroIdentifier = string.Empty;
        return false;
    }

    public static (string CanonicalIdentifier, string DisplayName, string PortraitIconVfs, bool IsCustomUnit) ResolveHeroDisplay(
        string? rawIdentifier,
        string? knownDisplayName = null,
        string? knownIconVfs = null)
    {
        if (TryResolve(rawIdentifier, out CustomHeroDefinition customDef))
        {
            return (customDef.CanonicalIdentifier, customDef.DisplayName, customDef.PortraitIconVfs, true);
        }

        string trimmed = string.IsNullOrWhiteSpace(rawIdentifier) ? "Hero_Unknown" : rawIdentifier.Trim();
        int dotIdx = trimmed.IndexOf('.');
        string baseId = dotIdx > 0 ? trimmed[..dotIdx] : trimmed;

        if (TryStripArenaHeroPrefix(baseId, out string arenaBaseHero))
        {
            string arenaShort = arenaBaseHero["Hero_".Length..];
            string arenaIcon = !string.IsNullOrWhiteSpace(knownIconVfs)
                ? knownIconVfs.StartsWith('/') ? knownIconVfs : $"/{knownIconVfs}"
                : $"/heroes/{arenaShort.ToLowerInvariant()}/icon.tga";
            string arenaName = !string.IsNullOrWhiteSpace(knownDisplayName)
                ? knownDisplayName
                : arenaShort.Replace("_", " ", StringComparison.Ordinal);
            return (baseId, arenaName, arenaIcon, true);
        }

        bool isCustom = IsCustomMapUnit(baseId);

        string shortCode = baseId.StartsWith("Hero_", StringComparison.OrdinalIgnoreCase)
            ? baseId["Hero_".Length..]
            : baseId;

        string fallbackIcon = !string.IsNullOrWhiteSpace(knownIconVfs)
            ? knownIconVfs.StartsWith('/') ? knownIconVfs : $"/{knownIconVfs}"
            : isCustom
                ? "/heroes/prophet/icon.tga"
                : $"/heroes/{shortCode.ToLowerInvariant()}/icon.tga";

        string displayName = !string.IsNullOrWhiteSpace(knownDisplayName)
            ? knownDisplayName
            : shortCode.Replace("_", " ", StringComparison.Ordinal);

        return (baseId, displayName, fallbackIcon, isCustom);
    }
}
