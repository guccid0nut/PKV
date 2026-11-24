// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Collections.Frozen;
using System.Diagnostics.CodeAnalysis;

namespace TRANSMUTANSTEIN.ChatServer.Domain.Matchmaking;

/// <summary>
///     Authoritative server-side allowlist and team-capacity specification for Co-Op Bot Match (<see cref="ChatProtocol.TMMType.TMM_TYPE_COOP" />) maps.
///     Kept in 1:1 parity with <c>MapSelection_V2.BOT_MAP_CATALOG</c> in <c>mapselection_v2.lua</c>.
/// </summary>
public readonly record struct BotMatchMapSpec(
    string Id,
    int TeamSize,
    string? SingleHeroBot = null,
    IReadOnlyList<string>? CustomBotPool = null);

public static class BotMatchMapCatalog
{
    /// <summary>
    ///     Authoritative 29-bot Strikers (<c>soccer</c>) roster matching C++ <c>sv_soccerHeroList</c>
    ///     (<c>game_x64.dll</c> <c>FUN_180004e50</c>). Prevents <c>randombots:</c> from picking incompatible non-Strikers heroes.
    /// </summary>
    public static readonly IReadOnlyList<string> SoccerBotPool =
    [
        "WitchSlayerBot",
        "ChronosBot",
        "AndromedaBot",
        "BlitzBot",
        "DevourerBot",
        "RallyBot",
        "RockyBot",
        "EmpathBot",
        "FluxBot",
        "MonkeyKingBot",
        "PuppetMasterBot",
        "AlunaBot",
        "MagebaneBot",
        "BushwackBot",
        "BehemothBot",
        "KenisisBot",
        "MumraBot",
        "ScoutBot",
        "KrakenBot",
        "RampageBot",
        "SilhouetteBot",
        "DrunkenMasterBot",
        "BubblesBot",
        "GladiatorBot",
        "PyromancerBot",
        "KingKloutBot",
        "ZephyrBot",
        "KaneBot",
        "LodestoneBot"
    ];

    public static readonly IReadOnlyList<BotMatchMapSpec> All =
    [
        new("caldavar", 5),
        new("midwars", 5),
        new("team_deathmatch", 5),
        new("devowars", 5, "DevourerBot"),
        new("devowars_10v10", 10, "DevourerBot"),
        new("soccer", 5, CustomBotPool: SoccerBotPool),
        new("midwars_10v10", 10),
        new("grimmscrossing", 3),
        new("thegrimmhunt", 5),
        new("darkwoodvale", 4),
        new("persian_pass", 4),
        new("watchtower", 5),
        new("three_kingdoms", 5),
        new("mids_omg", 5),
        new("caldavar_wtf", 5),
        new("caldavar_reborn", 5),
        new("midwars_reborn", 5),
        new("caldavar_old", 5),
        new("caldavar_halloween", 5),
        new("midwars_halloween", 5)
    ];

    private static readonly FrozenDictionary<string, BotMatchMapSpec> ById =
        All.ToFrozenDictionary(spec => spec.Id, StringComparer.OrdinalIgnoreCase);

    private static readonly string ValidMapSummary = string.Join(", ", All.Select(spec => spec.Id));

    public static BotMatchMapSpec Default => ById["caldavar"];

    public static bool TryGet(string? mapName, out BotMatchMapSpec spec)
    {
        if (string.IsNullOrWhiteSpace(mapName))
        {
            spec = default;
            return false;
        }

        return ById.TryGetValue(mapName.Trim(), out spec);
    }

    public static bool TryGetSingleHeroBot(string? mapName, [NotNullWhen(true)] out string? singleHeroBot)
    {
        if (TryGet(mapName, out BotMatchMapSpec spec) && !string.IsNullOrEmpty(spec.SingleHeroBot))
        {
            singleHeroBot = spec.SingleHeroBot;
            return true;
        }

        singleHeroBot = null;
        return false;
    }

    public static bool TryGetCustomBotPool(string? mapName, [NotNullWhen(true)] out IReadOnlyList<string>? customBotPool)
    {
        if (TryGet(mapName, out BotMatchMapSpec spec) && spec.CustomBotPool is { Count: > 0 })
        {
            customBotPool = spec.CustomBotPool;
            return true;
        }

        customBotPool = null;
        return false;
    }

    public static BotMatchMapSpec ResolveOrFallback(string? requestedMap)
    {
        if (string.IsNullOrWhiteSpace(requestedMap))
        {
            Log.Debug(
                "[BotMatchMapCatalog] Empty Co-Op map requested; defaulting to '{FallbackMap}'.",
                Default.Id);
            return Default;
        }

        string trimmed = requestedMap.Trim();
        if (ById.TryGetValue(trimmed, out BotMatchMapSpec spec))
        {
            return spec;
        }

        Log.Warning(
            "[BotMatchMapCatalog] Unknown or unsupported Co-Op map '{RequestedMap}'; falling back to '{FallbackMap}'. Allowed Co-Op maps: [{AllowedMaps}]",
            requestedMap,
            Default.Id,
            ValidMapSummary);
        return Default;
    }
}
