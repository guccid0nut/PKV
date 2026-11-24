// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Collections.Frozen;
using System.Globalization;
using System.Text;
using ASPIRE.Common.Constants;
using Microsoft.Extensions.FileProviders;

namespace DAWNBRINGER.WebPortal.UI.Services;

/// <summary>
/// Canonical URL &amp; file-alias resolver for Hero portraits, Arena Map icons/minimaps, and Item icons.
/// Resolves hydrated local PNG assets from <c>games/dawnbringer-overlay</c> / <c>wwwroot</c> when present,
/// while preserving cleanroom procedural SVG fallbacks when retail assets are absent or cleanroom mode is forced.
/// </summary>
public static class HeroAndMapAssetResolver
{
    private static readonly FrozenDictionary<string, string> HeroNicknameAliases =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["salforis"] = "lordsalforis",
            ["lordsalforis"] = "lordsalforis",
            ["repulsor"] = "doctorrepulsor",
            ["drrepulsor"] = "doctorrepulsor",
            ["doctorrepulsor"] = "doctorrepulsor",
            ["benzington"] = "sirbenzington",
            ["sirbenzington"] = "sirbenzington",
            ["moa"] = "masterofarms",
            ["masterofarms"] = "masterofarms",
            ["keeper"] = "keeperoftheforest",
            ["kotf"] = "keeperoftheforest",
            ["treant"] = "keeperoftheforest",
            ["keeperoftheforest"] = "keeperoftheforest",
            ["flint"] = "flintbeastwood",
            ["flintbeastwood"] = "flintbeastwood",
            ["voodoo"] = "voodoojester",
            ["voodoojester"] = "voodoojester",
            ["cd"] = "corrupteddisciple",
            ["corrupteddisciple"] = "corrupteddisciple",
            ["hag"] = "wretchedhag",
            ["babayaga"] = "wretchedhag",
            ["wretchedhag"] = "wretchedhag",
            ["hiro"] = "swiftblade",
            ["frosty"] = "glacius",
            ["krixi"] = "valkyrie",
            ["rocky"] = "pebbles",
            ["yogi"] = "wildsoul",
            ["flameguard"] = "pyromancer",
            ["hunter"] = "bloodhunter",
            ["shaman"] = "dementedshaman",
            ["dementedshaman"] = "dementedshaman",
            ["scar"] = "madman",
            ["madman"] = "madman",
            ["themadman"] = "madman",
            ["vanya"] = "darklady",
            ["darklady"] = "darklady",
            ["thedarklady"] = "darklady",
            ["chipper"] = "chipper",
            ["thechipper"] = "chipper",
            ["gladiator"] = "gladiator",
            ["thegladiator"] = "gladiator",
            ["javaras"] = "magebane",
            ["wolfman"] = "warbeast",
            ["ra"] = "amunra",
            ["amunra"] = "amunra",
            ["prisoner"] = "prisoner945",
            ["prisoner945"] = "prisoner945",
            ["riftmage"] = "riftwalker",
            ["riftwalker"] = "riftwalker",
            ["puppet"] = "puppetmaster",
            ["puppetmaster"] = "puppetmaster",
            ["pollywog"] = "pollywogpriest",
            ["pollywogpriest"] = "pollywogpriest",
            ["chi"] = "qi",
            ["qi"] = "qi"
        }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    private static readonly FrozenSet<string> KnownMapSlugs = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "archers",
        "arena",
        "caldavar",
        "caldavar_halloween",
        "caldavar_old",
        "caldavar_reborn",
        "caldavar_wtf",
        "capturetheflag",
        "colosseum_duel",
        "darkwoodvale",
        "devowars",
        "devowars_10v10",
        "empath_maze",
        "enfos",
        "grimmscrossing",
        "mids_omg",
        "midwars",
        "midwars_10v10",
        "midwars_halloween",
        "midwars_reborn",
        "nightmarish_coop",
        "pebbles_duel",
        "persian_pass",
        "prophets",
        "riftwars",
        "soccer",
        "spotlight",
        "strikers",
        "team_deathmatch",
        "thegrimmhunt",
        "three_kingdoms",
        "truedevo",
        "truedevo_10v10",
        "tutorial",
        "valkwars",
        "watchtower"
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    private static readonly FrozenDictionary<string, string> MapAliases =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            // Forests of Caldavar & variants
            ["caldavar"] = "caldavar",
            ["forests_of_caldavar"] = "caldavar",
            ["forestsofcaldavar"] = "caldavar",
            ["caldavar_1v1"] = "caldavar",
            ["caldavar_ranked"] = "caldavar",
            ["caldavar_public"] = "caldavar",
            ["caldavar_casual"] = "caldavar",
            ["quickmatch"] = "caldavar",
            ["quickmatch_ranked"] = "caldavar",
            ["caldavar_old"] = "caldavar_old",
            ["caldavarold"] = "caldavar_old",
            ["forests_of_caldavar_old"] = "caldavar_old",
            ["caldavar_reborn"] = "caldavar_reborn",
            ["caldavarreborn"] = "caldavar_reborn",
            ["forests_of_caldavar_reborn"] = "caldavar_reborn",
            ["caldavar_halloween"] = "caldavar_halloween",
            ["caldavarhalloween"] = "caldavar_halloween",
            ["caldavar_wtf"] = "caldavar_wtf",
            ["caldavarwtf"] = "caldavar_wtf",

            // Mid Wars & variants
            ["midwars"] = "midwars",
            ["mid_wars"] = "midwars",
            ["midwars_old"] = "midwars",
            ["midwarsold"] = "midwars",
            ["mid_wars_old"] = "midwars",
            ["midwars_ranked"] = "midwars",
            ["mid_wars_ranked"] = "midwars",
            ["midwars_public"] = "midwars",
            ["mid_wars_public"] = "midwars",
            ["midwars_casual"] = "midwars",
            ["midwars_reborn"] = "midwars_reborn",
            ["mid_wars_reborn"] = "midwars_reborn",
            ["midwarsreborn"] = "midwars_reborn",
            ["midwars_10v10"] = "midwars_10v10",
            ["mid_wars_10v10"] = "midwars_10v10",
            ["midwars10v10"] = "midwars_10v10",
            ["midwars_halloween"] = "midwars_halloween",
            ["mid_wars_halloween"] = "midwars_halloween",
            ["midwarshalloween"] = "midwars_halloween",
            ["mids_omg"] = "mids_omg",
            ["midsomg"] = "mids_omg",

            // Devo Wars & variants
            ["devowars"] = "devowars",
            ["devo_wars"] = "devowars",
            ["devowars_10v10"] = "devowars_10v10",
            ["devo_wars_10v10"] = "devowars_10v10",
            ["devowars10v10"] = "devowars_10v10",
            ["truedevo"] = "truedevo",
            ["true_devo"] = "truedevo",
            ["truedevo_10v10"] = "truedevo_10v10",
            ["true_devo_10v10"] = "truedevo_10v10",
            ["truedevo10v10"] = "truedevo_10v10",

            // Official & Arena modes
            ["riftwars"] = "riftwars",
            ["rift_wars"] = "riftwars",
            ["riftwars_ranked"] = "riftwars",
            ["riftwars_public"] = "riftwars",
            ["riftwars_casual"] = "riftwars",
            ["grimmscrossing"] = "grimmscrossing",
            ["grimms_crossing"] = "grimmscrossing",
            ["grimmscross"] = "grimmscrossing",
            ["grimrnscrossing"] = "grimmscrossing",
            ["darkwoodvale"] = "darkwoodvale",
            ["darkwood_vale"] = "darkwoodvale",
            ["darkwood"] = "darkwoodvale",
            ["watchtower"] = "watchtower",
            ["watch_tower"] = "watchtower",
            ["prophets"] = "prophets",
            ["prophet"] = "prophets",
            ["the_prophets"] = "prophets",
            ["strikers"] = "strikers",
            ["striker"] = "strikers",
            ["hon_strikers"] = "strikers",
            ["honstrikers"] = "strikers",
            ["soccer"] = "soccer",
            ["capturetheflag"] = "capturetheflag",
            ["capture_the_flag"] = "capturetheflag",
            ["ctf"] = "capturetheflag",
            ["team_deathmatch"] = "team_deathmatch",
            ["team_death_match"] = "team_deathmatch",
            ["teamdeathmatch"] = "team_deathmatch",
            ["deathmatch"] = "team_deathmatch",
            ["tdm"] = "team_deathmatch",
            ["thegrimmhunt"] = "thegrimmhunt",
            ["the_grimm_hunt"] = "thegrimmhunt",
            ["grimmhunt"] = "thegrimmhunt",
            ["grimm_hunt"] = "thegrimmhunt",
            ["tutorial"] = "tutorial",

            // Custom / Community Arena Maps
            ["archers"] = "archers",
            ["archer"] = "archers",
            ["archer_wars"] = "archers",
            ["archerwars"] = "archers",
            ["arena"] = "arena",
            ["colosseum_duel"] = "colosseum_duel",
            ["colosseumduel"] = "colosseum_duel",
            ["colosseum"] = "colosseum_duel",
            ["empath_maze"] = "empath_maze",
            ["empathmaze"] = "empath_maze",
            ["enfos"] = "enfos",
            ["enfo"] = "enfos",
            ["nightmarish_coop"] = "nightmarish_coop",
            ["nightmarishcoop"] = "nightmarish_coop",
            ["pebbles_duel"] = "pebbles_duel",
            ["pebblesduel"] = "pebbles_duel",
            ["persian_pass"] = "persian_pass",
            ["persianpass"] = "persian_pass",
            ["spotlight"] = "spotlight",
            ["three_kingdoms"] = "three_kingdoms",
            ["threekingdoms"] = "three_kingdoms",
            ["3kingdoms"] = "three_kingdoms",
            ["valkwars"] = "valkwars",
            ["valk_wars"] = "valkwars"
        }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    private static readonly FrozenDictionary<string, string> MapDisplayNames =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["archers"] = "Archer Wars",
            ["arena"] = "Arena",
            ["caldavar"] = "Forests of Caldavar",
            ["caldavar_halloween"] = "Forests of Caldavar (Halloween)",
            ["caldavar_old"] = "Forests of Caldavar (Classic)",
            ["caldavar_reborn"] = "Forests of Caldavar Reborn",
            ["caldavar_wtf"] = "Caldavar WTF",
            ["capturetheflag"] = "Capture The Flag",
            ["colosseum_duel"] = "Colosseum Duel",
            ["darkwoodvale"] = "Darkwood Vale",
            ["devowars"] = "Devo Wars",
            ["devowars_10v10"] = "Devo Wars (10v10)",
            ["empath_maze"] = "Empath Maze",
            ["enfos"] = "Enfo's Team Survival",
            ["grimmscrossing"] = "Grimm's Crossing",
            ["mids_omg"] = "Mids OMG",
            ["midwars"] = "Mid Wars",
            ["midwars_10v10"] = "Mid Wars (10v10)",
            ["midwars_halloween"] = "Mid Wars (Halloween)",
            ["midwars_reborn"] = "Mid Wars Reborn",
            ["nightmarish_coop"] = "Nightmarish Co-Op",
            ["pebbles_duel"] = "Pebbles Duel",
            ["persian_pass"] = "Persian Pass",
            ["prophets"] = "Prophets",
            ["riftwars"] = "Rift Wars",
            ["soccer"] = "HoN Strikers",
            ["spotlight"] = "Hero Spotlight",
            ["strikers"] = "HoN Strikers",
            ["team_deathmatch"] = "Team Deathmatch",
            ["thegrimmhunt"] = "The Grimm Hunt",
            ["three_kingdoms"] = "Three Kingdoms",
            ["truedevo"] = "True Devo",
            ["truedevo_10v10"] = "True Devo (10v10)",
            ["tutorial"] = "Newerth Tutorial",
            ["valkwars"] = "Valkyrie Wars",
            ["watchtower"] = "Watchtower"
        }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    public static string ResolveHeroSlug(string? rawHero)
    {
        if (string.IsNullOrWhiteSpace(rawHero))
        {
            return "default";
        }

        string cleaned = SafeUnescape(rawHero).Trim().Replace('\\', '/');
        int lastSlash = cleaned.LastIndexOf('/');
        if (lastSlash >= 0 && lastSlash < cleaned.Length - 1)
        {
            cleaned = cleaned[(lastSlash + 1)..];
        }

        cleaned = StripKnownImageExtension(cleaned).Trim();

        // Strip stacked prefixes such as icon_hero_puppet_master, icon_bubbles, Hero_Hiro
        bool strippedPrefix = true;
        while (strippedPrefix && cleaned.Length > 0)
        {
            strippedPrefix = false;
            if (cleaned.StartsWith("icon_128_", StringComparison.OrdinalIgnoreCase))
            {
                cleaned = cleaned[9..];
                strippedPrefix = true;
            }
            else if (cleaned.StartsWith("icon_", StringComparison.OrdinalIgnoreCase))
            {
                cleaned = cleaned[5..];
                strippedPrefix = true;
            }
            else if (cleaned.StartsWith("hero_", StringComparison.OrdinalIgnoreCase))
            {
                cleaned = cleaned[5..];
                strippedPrefix = true;
            }
        }

        if (string.IsNullOrWhiteSpace(cleaned) || cleaned.Equals("default", StringComparison.OrdinalIgnoreCase))
        {
            return "default";
        }

        string compactKey = ToAlphanumericLower(cleaned);
        if (HeroNicknameAliases.TryGetValue(compactKey, out string? nickTarget))
        {
            return nickTarget;
        }

        // Check official S2 engine identifier table (e.g., Hero_Hiro -> Swiftblade -> swiftblade, Hero_Ebulus -> Slither -> slither)
        string? displayFromId = Heroes.GetNameByIdentifier(cleaned) ?? Heroes.GetNameByIdentifier(compactKey);
        if (!string.IsNullOrWhiteSpace(displayFromId))
        {
            string displayCompact = ToAlphanumericLower(displayFromId);
            if (HeroNicknameAliases.TryGetValue(displayCompact, out string? mappedDisplay))
            {
                return mappedDisplay;
            }

            if (!string.IsNullOrEmpty(displayCompact))
            {
                return displayCompact;
            }
        }

        // Check display-name lookup when underscores/spaces were passed (e.g. "puppet_master" -> "Puppet Master")
        string spacedCandidate = cleaned.Replace('_', ' ').Replace('-', ' ').Trim();
        string? idFromName = Heroes.GetIdentifierByName(spacedCandidate);
        if (!string.IsNullOrWhiteSpace(idFromName))
        {
            string? canonicalName = Heroes.GetNameByIdentifier(idFromName);
            if (!string.IsNullOrWhiteSpace(canonicalName))
            {
                string canonicalCompact = ToAlphanumericLower(canonicalName);
                return HeroNicknameAliases.GetValueOrDefault(canonicalCompact, canonicalCompact);
            }
        }

        return string.IsNullOrEmpty(compactKey) ? "default" : compactKey;
    }

    public static string ResolveHeroIconUrl(string? rawHero, string emptyFallbackSlug = "default")
    {
        if (string.IsNullOrWhiteSpace(rawHero))
        {
            string fallback = ResolveHeroSlug(emptyFallbackSlug);
            return $"/images/heroes/{fallback}.png";
        }

        string slug = ResolveHeroSlug(rawHero);
        return $"/images/heroes/{slug}.png";
    }

    public static string ResolveMapSlug(string? rawMap)
    {
        if (string.IsNullOrWhiteSpace(rawMap))
        {
            return "caldavar";
        }

        string cleaned = SafeUnescape(rawMap).Trim().Replace('\\', '/');
        if (cleaned.StartsWith("/images/maps/", StringComparison.OrdinalIgnoreCase))
        {
            cleaned = cleaned["/images/maps/".Length..];
        }
        else if (cleaned.StartsWith("images/maps/", StringComparison.OrdinalIgnoreCase))
        {
            cleaned = cleaned["images/maps/".Length..];
        }
        else if (cleaned.StartsWith("maps/", StringComparison.OrdinalIgnoreCase))
        {
            cleaned = cleaned["maps/".Length..];
        }

        int lastSlash = cleaned.LastIndexOf('/');
        if (lastSlash >= 0 && lastSlash < cleaned.Length - 1)
        {
            cleaned = cleaned[(lastSlash + 1)..];
        }

        if (cleaned.EndsWith(".s2z", StringComparison.OrdinalIgnoreCase))
        {
            cleaned = cleaned[..^4];
        }

        cleaned = StripKnownImageExtension(cleaned).Trim();

        while (cleaned.StartsWith("minimap_", StringComparison.OrdinalIgnoreCase) ||
               cleaned.StartsWith("minimap ", StringComparison.OrdinalIgnoreCase) ||
               cleaned.StartsWith("minimap-", StringComparison.OrdinalIgnoreCase))
        {
            cleaned = cleaned[8..].Trim();
        }

        if (string.IsNullOrWhiteSpace(cleaned))
        {
            return "caldavar";
        }

        if (cleaned.Equals("default", StringComparison.OrdinalIgnoreCase))
        {
            return "default";
        }

        // Normalize punctuation, parentheses, apostrophes, spaces, and hyphens into clean underscore tokens
        StringBuilder sb = new(cleaned.Length);
        bool prevUnderscore = false;
        foreach (char ch in cleaned)
        {
            if (char.IsLetterOrDigit(ch))
            {
                sb.Append(char.ToLowerInvariant(ch));
                prevUnderscore = false;
            }
            else if (ch is ' ' or '_' or '-' or '.')
            {
                if (!prevUnderscore && sb.Length > 0)
                {
                    sb.Append('_');
                    prevUnderscore = true;
                }
            }
        }

        string normalized = sb.ToString().Trim('_');
        if (string.IsNullOrEmpty(normalized))
        {
            return "caldavar";
        }

        if (KnownMapSlugs.Contains(normalized))
        {
            return normalized;
        }

        if (MapAliases.TryGetValue(normalized, out string? mapped))
        {
            return mapped;
        }

        string compact = normalized.Replace("_", "", StringComparison.Ordinal);
        if (MapAliases.TryGetValue(compact, out string? mappedCompact))
        {
            return mappedCompact;
        }

        return normalized;
    }

    public static string ResolveMapDisplayName(string? rawMap)
    {
        if (string.IsNullOrWhiteSpace(rawMap))
        {
            return "Forests of Caldavar";
        }

        string slug = ResolveMapSlug(rawMap);
        if (MapDisplayNames.TryGetValue(slug, out string? displayName))
        {
            return displayName;
        }

        return CultureInfo.InvariantCulture.TextInfo.ToTitleCase(slug.Replace('_', ' '));
    }

    public static string ResolveMapIconUrl(string? rawMap)
    {
        string slug = ResolveMapSlug(rawMap);
        return $"/images/maps/{slug}.png";
    }

    public static string ResolveMinimapUrl(string? rawMap)
    {
        string slug = ResolveMapSlug(rawMap);
        if (slug.Equals("default", StringComparison.OrdinalIgnoreCase))
        {
            return "/images/maps/minimap_caldavar.png";
        }

        return $"/images/maps/minimap_{slug}.png";
    }

    /// <summary>
    /// Resolves missed <c>/images/{heroes,maps,items,abilities}/*</c> requests to candidate static file subpaths
    /// in <see cref="IFileProvider"/> and computes normalized cleanroom badge labels when unhydrated.
    /// </summary>
    public static bool TryResolveStaticImageCandidate(
        string? requestPath,
        IFileProvider? staticFileProvider,
        bool forceCleanroomPlaceholders,
        out IFileInfo? resolvedFile,
        out string cleanCategory,
        out string cleanSlugLabel)
    {
        resolvedFile = null;
        cleanCategory = string.Empty;
        cleanSlugLabel = string.Empty;

        if (string.IsNullOrWhiteSpace(requestPath))
        {
            return false;
        }

        string decodedPath = SafeUnescape(requestPath).Trim();
        bool isImagesRoute = decodedPath.StartsWith("/images/", StringComparison.OrdinalIgnoreCase);
        string? trimmed = isImagesRoute
            ? decodedPath["/images/".Length..]
            : decodedPath.Length > 1 && decodedPath[0] == '/'
                ? decodedPath[1..]
                : null;

        if (string.IsNullOrWhiteSpace(trimmed))
        {
            return false;
        }

        int slashIdx = trimmed.IndexOf('/');
        if (slashIdx <= 0 || slashIdx >= trimmed.Length - 1)
        {
            return false;
        }

        string category = trimmed[..slashIdx].ToLowerInvariant();
        string rawFilePart = trimmed[(slashIdx + 1)..];
        if (category is not ("heroes" or "maps" or "items" or "abilities"))
        {
            return false;
        }

        cleanCategory = category;
        List<string> candidates = [];

        switch (category)
        {
            case "heroes":
            {
                string heroSlug = ResolveHeroSlug(rawFilePart);
                string display = Heroes.ResolveHeroDisplayName(heroSlug);
                cleanSlugLabel = heroSlug.Equals("default", StringComparison.OrdinalIgnoreCase)
                    ? "Hero"
                    : (!string.IsNullOrWhiteSpace(display) ? display : heroSlug);
                candidates.Add($"images/heroes/{heroSlug}.png");
                break;
            }
            case "maps":
            {
                bool wantsMinimap = rawFilePart.Contains("minimap", StringComparison.OrdinalIgnoreCase);
                string mapSlug = ResolveMapSlug(rawFilePart);
                cleanSlugLabel = ResolveMapDisplayName(mapSlug);
                if (wantsMinimap)
                {
                    candidates.Add($"images/maps/minimap_{mapSlug}.png");
                    candidates.Add($"images/maps/{mapSlug}.png");
                }
                else
                {
                    candidates.Add($"images/maps/{mapSlug}.png");
                    candidates.Add($"images/maps/minimap_{mapSlug}.png");
                }

                break;
            }
            case "items":
            {
                string itemStem = StripKnownImageExtension(Path.GetFileName(rawFilePart));
                if (itemStem.StartsWith("icon_", StringComparison.OrdinalIgnoreCase))
                {
                    itemStem = itemStem[5..];
                }

                if (itemStem.StartsWith("item_", StringComparison.OrdinalIgnoreCase))
                {
                    itemStem = itemStem[5..];
                }

                string compactItem = ToAlphanumericLower(itemStem);
                cleanSlugLabel = string.IsNullOrEmpty(compactItem) ? "Item" : compactItem;
                if (!string.IsNullOrEmpty(compactItem))
                {
                    candidates.Add($"images/items/{compactItem}.png");
                    candidates.Add($"images/items/item_{compactItem}.png");
                }

                break;
            }
            case "abilities":
            {
                string abilStem = StripKnownImageExtension(Path.GetFileName(rawFilePart));
                if (abilStem.StartsWith("icon_", StringComparison.OrdinalIgnoreCase))
                {
                    abilStem = abilStem[5..];
                }

                string normalizedAbil = abilStem.ToLowerInvariant();
                cleanSlugLabel = string.IsNullOrEmpty(normalizedAbil) ? "Ability" : normalizedAbil;
                if (!string.IsNullOrEmpty(normalizedAbil))
                {
                    candidates.Add($"images/abilities/{normalizedAbil}.png");
                }

                break;
            }
        }

        if (!forceCleanroomPlaceholders && staticFileProvider != null)
        {
            foreach (string subPath in candidates)
            {
                IFileInfo info = staticFileProvider.GetFileInfo(subPath);
                if (info is { Exists: true, IsDirectory: false })
                {
                    resolvedFile = info;
                    return true;
                }
            }
        }

        return true;
    }

    private static string SafeUnescape(string value)
    {
        try
        {
            return Uri.UnescapeDataString(value);
        }
        catch
        {
            return value;
        }
    }

    private static string StripKnownImageExtension(string value)
    {
        string ext = Path.GetExtension(value);
        if (ext.Equals(".png", StringComparison.OrdinalIgnoreCase) ||
            ext.Equals(".tga", StringComparison.OrdinalIgnoreCase) ||
            ext.Equals(".dds", StringComparison.OrdinalIgnoreCase) ||
            ext.Equals(".jpg", StringComparison.OrdinalIgnoreCase) ||
            ext.Equals(".jpeg", StringComparison.OrdinalIgnoreCase) ||
            ext.Equals(".webp", StringComparison.OrdinalIgnoreCase) ||
            ext.Equals(".svg", StringComparison.OrdinalIgnoreCase))
        {
            return value[..^ext.Length];
        }

        return value;
    }

    private static string ToAlphanumericLower(string value)
    {
        StringBuilder sb = new(value.Length);
        foreach (char ch in value)
        {
            if (char.IsLetterOrDigit(ch))
            {
                sb.Append(char.ToLowerInvariant(ch));
            }
        }

        return sb.ToString();
    }
}
