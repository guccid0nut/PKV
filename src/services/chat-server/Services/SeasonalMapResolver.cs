// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using ASPIRE.Common.Network;

namespace TRANSMUTANSTEIN.ChatServer.Services;

internal sealed class SeasonalMapOptions
{
    public const string SectionName = "Matchmaking:SeasonalMaps";

    /// <summary>
    ///     Master switch for automatic seasonal map resolution in Online Matchmaking &amp; Co-Op Bots.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    ///     Season override: "auto" (calendar-based UTC window), "halloween" (force Halloween maps year-round),
    ///     or "none" / "classic" (disable seasonal map swaps).
    /// </summary>
    public string ForceSeason { get; set; } = "auto";

    /// <summary>
    ///     Whether Ranked 5v5 queues (TMM_GAME_TYPE_NORMAL / CAMPAIGN_NORMAL) also rotate to caldavar_halloween
    ///     during Halloween season. Defaults to true.
    /// </summary>
    public bool IncludeRankedQueues { get; set; } = true;

    /// <summary>
    ///     Optional explicit path to games/server/game/maps/ for archive existence verification.
    ///     When null/empty, SeasonalMapResolver probes standard relative server map locations if present.
    /// </summary>
    public string? ServerMapsDirectory { get; set; }
}

public static class SeasonalMapResolver
{
    private static readonly Dictionary<string, string> HalloweenMapAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["midwars"] = "midwars_halloween",
        ["caldavar"] = "caldavar_halloween"
    };

    /// <summary>
    ///     Returns true during the authoritative UTC Halloween window:
    ///     October 1 00:00:00 UTC through November 1 11:59:59 UTC (inclusive of all US/EU/SEA Halloween evenings).
    /// </summary>
    public static bool IsHalloweenWindowUtc(DateTimeOffset utcNow)
    {
        DateTimeOffset utc = utcNow.ToUniversalTime();
        return utc.Month == 10 || (utc.Month == 11 && utc.Day == 1 && utc.Hour < 12);
    }

    internal static string ResolveActiveSeason(SeasonalMapOptions options, DateTimeOffset utcNow)
    {
        if (!options.Enabled)
        {
            return "none";
        }

        string forced = (options.ForceSeason ?? "auto").Trim().ToLowerInvariant();
        return forced switch
        {
            "none" or "classic" or "off" or "false" => "none",
            "halloween" => "halloween",
            _ => IsHalloweenWindowUtc(utcNow) ? "halloween" : "none"
        };
    }

    public static bool IsRankedGameType(ChatProtocol.TMMGameType gameType)
    {
        return gameType is ChatProtocol.TMMGameType.TMM_GAME_TYPE_NORMAL
            or ChatProtocol.TMMGameType.TMM_GAME_TYPE_CAMPAIGN_NORMAL
            or ChatProtocol.TMMGameType.TMM_GAME_TYPE_REBORN_NORMAL;
    }

    internal static SeasonalMapOptions BindOptions(IConfiguration? configuration)
    {
        SeasonalMapOptions options = new();
        if (configuration == null)
        {
            return options;
        }

        IConfigurationSection section = configuration.GetSection(SeasonalMapOptions.SectionName);
        if (!section.Exists())
        {
            return options;
        }

        if (bool.TryParse(section[nameof(SeasonalMapOptions.Enabled)], out bool enabled))
        {
            options.Enabled = enabled;
        }

        string? forceSeason = section[nameof(SeasonalMapOptions.ForceSeason)];
        if (!string.IsNullOrWhiteSpace(forceSeason))
        {
            options.ForceSeason = forceSeason;
        }

        if (bool.TryParse(section[nameof(SeasonalMapOptions.IncludeRankedQueues)], out bool includeRanked))
        {
            options.IncludeRankedQueues = includeRanked;
        }

        string? mapsDir = section[nameof(SeasonalMapOptions.ServerMapsDirectory)];
        if (!string.IsNullOrWhiteSpace(mapsDir))
        {
            options.ServerMapsDirectory = mapsDir;
        }

        return options;
    }

    public static string ResolveMatchmakingMap(
        string? requestedMap,
        ChatProtocol.TMMType groupType,
        ChatProtocol.TMMGameType gameType,
        IConfiguration? configuration,
        TimeProvider? timeProvider = null)
    {
        SeasonalMapOptions options = BindOptions(configuration);
        DateTimeOffset utcNow = (timeProvider ?? TimeProvider.System).GetUtcNow();
        return ResolveMatchmakingMap(requestedMap, groupType, gameType, options, utcNow);
    }

    internal static string ResolveMatchmakingMap(
        string? requestedMap,
        ChatProtocol.TMMType groupType,
        ChatProtocol.TMMGameType gameType,
        SeasonalMapOptions options,
        DateTimeOffset utcNow)
    {
        string fallback = string.IsNullOrWhiteSpace(requestedMap) ? "caldavar" : requestedMap.Trim();
        if (groupType == ChatProtocol.TMMType.TMM_TYPE_COOP)
        {
            Log.Debug(
                "[SeasonalMaps] Co-Op queue ({GroupType}) preserves explicit map selection '{Map}' without automatic seasonal aliasing.",
                groupType,
                fallback);
            return fallback;
        }

        string season = ResolveActiveSeason(options, utcNow);
        if (!string.Equals(season, "halloween", StringComparison.OrdinalIgnoreCase))
        {
            return fallback;
        }

        if (groupType != ChatProtocol.TMMType.TMM_TYPE_COOP &&
            !options.IncludeRankedQueues &&
            IsRankedGameType(gameType))
        {
            Log.Debug(
                "[SeasonalMaps] Ranked queue {GameType} exempted from Halloween map swap (IncludeRankedQueues=false); keeping '{Map}'.",
                gameType,
                fallback);
            return fallback;
        }

        if (!HalloweenMapAliases.TryGetValue(fallback, out string? seasonalMap))
        {
            return fallback;
        }

        string? mapsDir = ResolveServerMapsDirectory(options.ServerMapsDirectory);
        if (!string.IsNullOrEmpty(mapsDir))
        {
            string expectedArchive = Path.Combine(mapsDir, $"{seasonalMap}.s2z");
            if (!File.Exists(expectedArchive))
            {
                Log.Warning(
                    "[SeasonalMaps] Seasonal archive '{ExpectedArchive}' not found on disk; falling back to '{FallbackMap}'. Build it via 'python scripts/assets/build_seasonal_halloween_maps.py'.",
                    expectedArchive,
                    fallback);
                return fallback;
            }
        }

        Log.Information(
            "[SeasonalMaps] Active season '{Season}' (ForceSeason='{ForceSeason}', UtcNow={UtcNow:O}): resolved '{RequestedMap}' -> '{SeasonalMap}' for {GroupType}/{GameType}.",
            season,
            options.ForceSeason,
            utcNow,
            fallback,
            seasonalMap,
            groupType,
            gameType);
        return seasonalMap;
    }

    private static string? ResolveServerMapsDirectory(string? configuredDir)
    {
        if (!string.IsNullOrWhiteSpace(configuredDir))
        {
            return Directory.Exists(configuredDir) ? configuredDir : configuredDir;
        }

        // Only enforce disk probe when standard games/server/game/maps directory exists locally (preserving hermetic unit tests)
        string? cur = AppContext.BaseDirectory;
        for (int depth = 0; depth < 7 && !string.IsNullOrEmpty(cur); depth++)
        {
            string candidate = Path.Combine(cur, "games", "server", "game", "maps");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }

            cur = Directory.GetParent(cur)?.FullName;
        }

        return null;
    }
}
