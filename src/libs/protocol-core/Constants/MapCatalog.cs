// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Collections.Frozen;
using System.Globalization;

namespace ASPIRE.Common.Constants;

public record MapDefinition(
    string Key,
    string DisplayName,
    string IconEmoji,
    string IconPath,
    bool IsMatchmaking,
    int TeamSize = 5,
    bool IsCustomMode = false,
    bool IsCoOpPvE = false,
    string SchemaKey = "moba");

public enum MatchFilterKind
{
    All,
    RankedFoC,
    RankedMidWars,
    RankedRiftWars,
    Quickmatch,
    AllPublic,
    PublicMap
}

public record MatchFilterClassification(MatchFilterKind Kind, string? MapKey = null, bool IsRanked = false);

public static class MapCatalog
{
    public static readonly List<MapDefinition> AllMaps =
    [
        // Core Ranked / TMM & Variant Lane Maps
        new("caldavar", "Forests of Caldavar", "🌲", "/images/maps/caldavar.png", true, 5, false, false, "moba"),
        new("caldavar_reborn", "Forests of Caldavar Reborn", "🌲", "/images/maps/caldavar.png", false, 5, false, false, "moba"),
        new("caldavar_old", "Forests of Caldavar (Old)", "🌲", "/images/maps/caldavar.png", false, 5, false, false, "moba"),
        new("caldavar_halloween", "Forests of Caldavar (Halloween)", "🎃", "/images/maps/caldavar.png", true, 5, false, false, "moba"),
        new("caldavar_wtf", "Caldavar (WTF)", "⚡", "/images/maps/caldavar.png", false, 5, true, false, "moba"),
        new("midwars", "Mid Wars", "⚔️", "/images/maps/midwars.png", true, 5, false, false, "moba"),
        new("midwars_reborn", "Mid Wars Reborn", "⚔️", "/images/maps/midwars.png", false, 5, false, false, "moba"),
        new("midwars_halloween", "Mid Wars (Halloween)", "🎃", "/images/maps/midwars.png", true, 5, false, false, "moba"),
        new("midwars_10v10", "Mid Wars (10v10)", "💥", "/images/maps/midwars.png", false, 10, true, false, "moba_10v10"),
        new("mids_omg", "Mid Wars Classic", "⚔️", "/images/maps/midwars.png", false, 5, true, false, "moba"),
        new("riftwars", "Rift Wars", "🌀", "/images/maps/riftwars.png", true, 5, true, false, "riftwars"),
        new("grimmscrossing", "Grimm's Crossing", "🏰", "/images/maps/grimmscrossing.png", false, 3, false, false, "moba"),
        new("darkwoodvale", "Darkwood Vale", "🌳", "/images/maps/darkwoodvale.png", false, 4, false, false, "moba"),
        new("watchtower", "Watchtower", "🗼", "/images/maps/watchtower.png", false, 5, false, false, "moba"),
        new("three_kingdoms", "Three Kingdoms", "🏮", "/images/maps/caldavar.png", false, 5, false, false, "moba"),
        new("persian_pass", "Persian Pass", "🏜️", "/images/maps/caldavar.png", false, 4, false, false, "moba"),

        // Custom Objective, Skillshot, Arena & Survival Maps
        new("strikers", "HoN Strikers", "⚽", "/images/maps/strikers.png", false, 5, true, false, "soccer"),
        new("ctf", "Capture The Flag", "🚩", "/images/maps/ctf.png", false, 5, true, false, "ctf"),
        new("devowars", "Devo Wars", "🎣", "/images/maps/devowars.png", false, 5, true, false, "devowars"),
        new("devowars_10v10", "Devo Wars (10v10)", "🎣", "/images/maps/devowars.png", false, 10, true, false, "devowars"),
        new("truedevo", "True Devo Wars", "🪝", "/images/maps/devowars.png", false, 5, true, false, "truedevo"),
        new("truedevo_10v10", "True Devo Wars (10v10)", "🪝", "/images/maps/devowars.png", false, 10, true, false, "truedevo"),
        new("prophets", "Prophets", "🔮", "/images/maps/prophets.png", false, 5, true, false, "prophets"),
        new("team_deathmatch", "Team Deathmatch", "💀", "/images/maps/team_deathmatch.png", false, 5, true, false, "tdm"),
        new("thegrimmhunt", "The Grimm Hunt", "🐑", "/images/maps/caldavar.png", false, 5, true, false, "thegrimmhunt"),
        new("enfos", "Enfo's Team Survival", "🛡️", "/images/maps/caldavar.png", false, 5, true, false, "enfos"),
        new("arena", "HoN Arena", "🏟️", "/images/maps/caldavar.png", false, 5, true, false, "arena"),
        new("valkwars", "Valkyrie Wars", "🏹", "/images/maps/caldavar.png", false, 5, true, false, "valkwars"),
        new("archers", "Archer Wars", "🎯", "/images/maps/caldavar.png", false, 5, true, false, "archers"),
        new("pebbles_duel", "Pebbles Duel", "🪨", "/images/maps/caldavar.png", false, 1, true, false, "duel_lives"),
        new("colosseum_duel", "Colosseum Duel", "⚔️", "/images/maps/caldavar.png", false, 1, true, false, "duel_lives"),
        new("empath_maze", "Empath Maze", "❄️", "/images/maps/caldavar.png", false, 5, true, false, "duel_lives"),
        new("nightmarish_coop", "Nightmarish Co-Op", "🐛", "/images/maps/caldavar.png", false, 5, true, true, "coop_survival"),
        new("tutorial", "Tutorial", "📘", "/images/maps/caldavar.png", false, 1, true, true, "moba"),
        new("spotlight", "Spotlight", "🌟", "/images/maps/caldavar.png", false, 1, true, true, "moba")
    ];

    private static readonly FrozenDictionary<string, string> SlugAliases =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["strikers"] = "soccer",
            ["striker"] = "soccer",
            ["hon_strikers"] = "soccer",
            ["honstrikers"] = "soccer",
            ["ctf"] = "capturetheflag",
            ["capture_the_flag"] = "capturetheflag",
            ["devo_wars"] = "devowars",
            ["devo_wars_10v10"] = "devowars_10v10",
            ["devowars10v10"] = "devowars_10v10",
            ["true_devo"] = "truedevo",
            ["true_devo_10v10"] = "truedevo_10v10",
            ["truedevo10v10"] = "truedevo_10v10",
            ["mid_wars"] = "midwars",
            ["mid_wars_10v10"] = "midwars_10v10",
            ["midwars10v10"] = "midwars_10v10",
            ["mid_wars_halloween"] = "midwars_halloween",
            ["midwarshalloween"] = "midwars_halloween",
            ["prophet"] = "prophets",
            ["the_prophets"] = "prophets",
            ["teamdeathmatch"] = "team_deathmatch",
            ["deathmatch"] = "team_deathmatch",
            ["grimrnscrossing"] = "grimmscrossing",
            ["grimm"] = "grimmscrossing",
            ["darkwood"] = "darkwoodvale",
            ["grimm_hunt"] = "thegrimmhunt",
            ["the_grimm_hunt"] = "thegrimmhunt",
            ["enfos_007"] = "enfos",
            ["nightmarish"] = "nightmarish_coop"
        }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    private static readonly FrozenSet<string> TmmEligibleSlugs =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "caldavar",
            "caldavar_halloween",
            "midwars",
            "midwars_halloween",
            "riftwars"
        }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    public static string NormalizeSlug(string? rawMap)
    {
        if (string.IsNullOrWhiteSpace(rawMap))
        {
            return "caldavar";
        }

        string clean = rawMap.Trim().ToLowerInvariant()
            .Replace("\\", "/")
            .Replace("maps/", "")
            .Replace(".s2z", "")
            .Trim();

        return SlugAliases.TryGetValue(clean, out string? canonical) ? canonical : clean;
    }

    public static bool IsTmmEligibleMap(string? rawMap)
    {
        string canonical = NormalizeSlug(rawMap);
        return TmmEligibleSlugs.Contains(canonical);
    }

    public static bool Is10v10Map(string? rawMap)
    {
        string canonical = NormalizeSlug(rawMap);
        return canonical.EndsWith("_10v10", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsCoOpMap(string? rawMap)
    {
        string canonical = NormalizeSlug(rawMap);
        return canonical is "nightmarish_coop" or "tutorial" or "spotlight";
    }

    public static MapDefinition? GetDefinition(string? rawMap)
    {
        string canonical = NormalizeSlug(rawMap);
        return AllMaps.FirstOrDefault(m =>
            string.Equals(NormalizeSlug(m.Key), canonical, StringComparison.OrdinalIgnoreCase));
    }

    public static string FormatMapName(string? rawMap)
    {
        if (string.IsNullOrWhiteSpace(rawMap)) return "Forests of Caldavar";

        string cleanMap = rawMap.ToLowerInvariant().Replace("maps/", "").Replace(".s2z", "").Trim();

        if (cleanMap == "caldavar_ranked") return "Forests of Caldavar (Ranked)";

        if (cleanMap is "caldavar_casual" or "quickmatch" or "quickmatch_ranked") return "Quickmatch (Casual)";

        if (cleanMap == "caldavar_public") return "Forests of Caldavar (Public)";

        if (cleanMap == "midwars_ranked") return "Mid Wars (Ranked)";

        if (cleanMap == "midwars_public") return "Mid Wars (Public)";

        if (cleanMap == "riftwars_ranked") return "Rift Wars (Ranked)";

        if (cleanMap == "riftwars_public") return "Rift Wars (Public)";

        MapDefinition? exact = AllMaps.FirstOrDefault(m =>
            string.Equals(m.Key, cleanMap, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(NormalizeSlug(m.Key), NormalizeSlug(cleanMap), StringComparison.OrdinalIgnoreCase));
        if (exact is not null) return exact.DisplayName;

        if (cleanMap.Contains("caldavar_reborn")) return "Forests of Caldavar Reborn";
        if (cleanMap.Contains("caldavar_old")) return "Forests of Caldavar (Old)";
        if (cleanMap.Contains("caldavar")) return "Forests of Caldavar";
        if (cleanMap.Contains("midwars_10v10")) return "Mid Wars (10v10)";
        if (cleanMap.Contains("midwars_reborn")) return "Mid Wars Reborn";
        if (cleanMap.Contains("midwars")) return "Mid Wars";
        if (cleanMap.Contains("riftwars")) return "Rift Wars";
        if (cleanMap.Contains("devowars_10v10")) return "Devo Wars (10v10)";
        if (cleanMap.Contains("devowars") || cleanMap.Contains("devo_wars")) return "Devo Wars";
        if (cleanMap.Contains("truedevo_10v10")) return "True Devo Wars (10v10)";
        if (cleanMap.Contains("truedevo")) return "True Devo Wars";
        if (cleanMap.Contains("prophet")) return "Prophets";
        if (cleanMap.Contains("soccer") || cleanMap.Contains("striker")) return "HoN Strikers";
        if (cleanMap.Contains("grimmscrossing") || cleanMap.Contains("grimrnscrossing")) return "Grimm's Crossing";
        if (cleanMap.Contains("thegrimmhunt")) return "The Grimm Hunt";
        if (cleanMap.Contains("watchtower")) return "Watchtower";
        if (cleanMap.Contains("darkwoodvale")) return "Darkwood Vale";
        if (cleanMap.Contains("capturetheflag") || cleanMap.Contains("ctf")) return "Capture The Flag";
        if (cleanMap.Contains("team_deathmatch") || cleanMap.Contains("teamdeathmatch")) return "Team Deathmatch";

        return CultureInfo.CurrentCulture.TextInfo.ToTitleCase(cleanMap.Replace("_", " "));
    }

    public static string FormatMapIconPath(string? rawMap)
    {
        if (string.IsNullOrWhiteSpace(rawMap)) return AllMaps[0].IconPath;

        string key = rawMap.ToLowerInvariant();
        MapDefinition? def = AllMaps.FirstOrDefault(m => m.Key == key || key.Contains(m.Key));
        return def?.IconPath ?? "/images/maps/caldavar.png";
    }

    public static string GetMapEmoji(string? rawMap)
    {
        if (string.IsNullOrWhiteSpace(rawMap)) return "🌲";

        string cleanMap = rawMap.ToLowerInvariant();

        if (cleanMap.Contains("caldavar")) return "🌲";

        if (cleanMap.Contains("midwars")) return "⚔️";

        if (cleanMap.Contains("riftwars")) return "🌀";

        if (cleanMap.Contains("devo")) return "🎣";

        if (cleanMap.Contains("prophet")) return "🔮";

        if (cleanMap.Contains("striker") || cleanMap.Contains("soccer")) return "⚽";

        if (cleanMap.Contains("grimm")) return "🏰";

        if (cleanMap.Contains("watchtower")) return "🗼";

        if (cleanMap.Contains("darkwood")) return "🌳";

        if (cleanMap.Contains("ctf") || cleanMap.Contains("capture")) return "🚩";

        // ReSharper disable once ConvertIfStatementToReturnStatement
        if (cleanMap.Contains("deathmatch")) return "💀";

        return "🗺️";
    }

    public static MatchFilterClassification ClassifyMatchFilter(string? rawTable)
    {
        if (string.IsNullOrWhiteSpace(rawTable))
            return new MatchFilterClassification(MatchFilterKind.RankedFoC, "caldavar", true);

        string clean = rawTable.Trim().ToLowerInvariant();

        return clean switch
        {
            "all" => new MatchFilterClassification(MatchFilterKind.All),

            "caldavar_ranked" or "ranked" or "ranked_foc" or "foc_ranked" or "season" or "pvp" or "campaign" or "campaign_normal" or "season_normal" or "normal" or "rnk" =>
                new MatchFilterClassification(MatchFilterKind.RankedFoC, "caldavar", true),

            "midwars_ranked" or "ranked_midwars" =>
                new MatchFilterClassification(MatchFilterKind.RankedMidWars, "midwars", true),

            "riftwars_ranked" or "ranked_riftwars" =>
                new MatchFilterClassification(MatchFilterKind.RankedRiftWars, "riftwars", true),

            "caldavar_casual" or "quickmatch" or "quickmatch_ranked" or "casual" or "campaign_casual" =>
                new MatchFilterClassification(MatchFilterKind.Quickmatch, "caldavar", true),

            "all_public" or "public" =>
                new MatchFilterClassification(MatchFilterKind.AllPublic, null, false),

            "caldavar_public" =>
                new MatchFilterClassification(MatchFilterKind.PublicMap, "caldavar", false),

            "midwars_public" or "midwars_casual" or "midwars" =>
                new MatchFilterClassification(MatchFilterKind.PublicMap, "midwars", false),

            "riftwars_public" or "riftwars_casual" or "riftwars" =>
                new MatchFilterClassification(MatchFilterKind.PublicMap, "riftwars", false),

            "devowars" or "devo_wars" =>
                new MatchFilterClassification(MatchFilterKind.PublicMap, "devowars", false),

            "prophets" or "prophet" =>
                new MatchFilterClassification(MatchFilterKind.PublicMap, "prophets", false),

            "strikers" or "striker" or "soccer" =>
                new MatchFilterClassification(MatchFilterKind.PublicMap, "strikers", false),

            "grimmscrossing" or "grimrnscrossing" or "grimm" =>
                new MatchFilterClassification(MatchFilterKind.PublicMap, "grimmscrossing", false),

            "watchtower" =>
                new MatchFilterClassification(MatchFilterKind.PublicMap, "watchtower", false),

            "darkwoodvale" or "darkwood" =>
                new MatchFilterClassification(MatchFilterKind.PublicMap, "darkwoodvale", false),

            "ctf" or "capturetheflag" =>
                new MatchFilterClassification(MatchFilterKind.PublicMap, "ctf", false),

            "team_deathmatch" or "teamdeathmatch" or "deathmatch" =>
                new MatchFilterClassification(MatchFilterKind.PublicMap, "team_deathmatch", false),

            "devowars_10v10" =>
                new MatchFilterClassification(MatchFilterKind.PublicMap, "devowars_10v10", false),

            "truedevo" =>
                new MatchFilterClassification(MatchFilterKind.PublicMap, "truedevo", false),

            "truedevo_10v10" =>
                new MatchFilterClassification(MatchFilterKind.PublicMap, "truedevo_10v10", false),

            "midwars_10v10" =>
                new MatchFilterClassification(MatchFilterKind.PublicMap, "midwars_10v10", false),

            "caldavar_10v10" =>
                new MatchFilterClassification(MatchFilterKind.PublicMap, "caldavar_10v10", false),

            "caldavar_wtf" or "caldavar_wtf_10v10" =>
                new MatchFilterClassification(MatchFilterKind.PublicMap, "caldavar_wtf", false),

            "mids_omg" =>
                new MatchFilterClassification(MatchFilterKind.PublicMap, "mids_omg", false),

            "enfos" =>
                new MatchFilterClassification(MatchFilterKind.PublicMap, "enfos", false),

            "arena" =>
                new MatchFilterClassification(MatchFilterKind.PublicMap, "arena", false),

            "valkwars" or "valk_wars" =>
                new MatchFilterClassification(MatchFilterKind.PublicMap, "valkwars", false),

            "archers" or "archer_wars" =>
                new MatchFilterClassification(MatchFilterKind.PublicMap, "archers", false),

            "thegrimmhunt" or "grimmhunt" =>
                new MatchFilterClassification(MatchFilterKind.PublicMap, "thegrimmhunt", false),

            "pebbles_duel" or "pebbles" =>
                new MatchFilterClassification(MatchFilterKind.PublicMap, "pebbles_duel", false),

            "colosseum_duel" or "colosseum" =>
                new MatchFilterClassification(MatchFilterKind.PublicMap, "colosseum_duel", false),

            "empath_maze" or "maze" =>
                new MatchFilterClassification(MatchFilterKind.PublicMap, "empath_maze", false),

            "nightmarish_coop" or "nightmarish" =>
                new MatchFilterClassification(MatchFilterKind.PublicMap, "nightmarish_coop", false),

            "three_kingdoms" =>
                new MatchFilterClassification(MatchFilterKind.PublicMap, "three_kingdoms", false),

            "persian_pass" =>
                new MatchFilterClassification(MatchFilterKind.PublicMap, "persian_pass", false),

            "caldavar_halloween" =>
                new MatchFilterClassification(MatchFilterKind.PublicMap, "caldavar_halloween", false),

            "midwars_halloween" =>
                new MatchFilterClassification(MatchFilterKind.PublicMap, "midwars_halloween", false),

            "other" or "others" =>
                new MatchFilterClassification(MatchFilterKind.PublicMap, "other", false),

            _ => new MatchFilterClassification(MatchFilterKind.PublicMap, NormalizeSlug(clean), false)
        };
    }
}
