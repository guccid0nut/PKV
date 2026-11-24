// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Text.Json.Serialization;
using MemoryPack;

namespace NEXUS.Contracts.CQRS.Telemetry;

/// <summary>
/// DTO representing an aggregated count for a single canonical region or pool label.
/// </summary>
[MemoryPackable]
public sealed partial class RegionCountDto
{
    /// <summary>
    /// Canonical display name of the region or pool (e.g., <c>All locations</c>, <c>US East</c>, <c>US (shared pool)</c>).
    /// </summary>
    [JsonPropertyName("region")]
    public string Region { get; set; } = string.Empty;

    /// <summary>
    /// Count of active games, available servers, or queued players associated with <see cref="Region"/>.
    /// </summary>
    [JsonPropertyName("count")]
    public int Count { get; set; }
}

/// <summary>
/// DTO representing queued TMM player counts for a single matchmaking queue title broken down by region pool.
/// </summary>
[MemoryPackable]
public sealed partial class TmmQueueBreakdownDto
{
    /// <summary>
    /// Display title of the matchmaking queue (e.g., <c>Forests of Caldavar | Ranked Pick (Normal)</c>, <c>Mid Wars</c>).
    /// </summary>
    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;

    /// <summary>
    /// Total distinct players currently queued in this queue across all selected regions.
    /// </summary>
    [JsonPropertyName("totalQueued")]
    public int TotalQueued { get; set; }

    /// <summary>
    /// Per-region queued player counts (players selecting multiple distinct pools appear in each selected pool;
    /// USE and USW selections are deduplicated per matchmaking group into <c>US (shared pool)</c>).
    /// </summary>
    [JsonPropertyName("regions")]
    public List<RegionCountDto> Regions { get; set; } = [];
}

/// <summary>
/// Canonical freshness/availability status values for live population telemetry payloads.
/// </summary>
public static class PopulationTelemetryStatus
{
    public const string Live = "live";
    public const string Stale = "stale";
    public const string Unavailable = "unavailable";
}

/// <summary>
/// DTO representing the detailed live population breakdown for the SysBar "Players Online" popup
/// and DAWNBRINGER Web Portal infrastructure / public status views.
/// </summary>
[MemoryPackable]
public sealed partial class PopulationBreakdownResponseDto
{
    /// <summary>
    /// Live concurrent online players (CCU).
    /// </summary>
    [JsonPropertyName("onlinePlayers")]
    public int OnlinePlayers { get; set; }

    /// <summary>
    /// Total non-arcade games currently in progress (<c>ACTIVE</c> or <c>LOADING</c>) across all locations.
    /// </summary>
    [JsonPropertyName("gamesInProgressTotal")]
    public int GamesInProgressTotal { get; set; }

    /// <summary>
    /// Games in progress starting with <c>All locations</c> (matching <see cref="GamesInProgressTotal"/>)
    /// followed by each active region with at least one game in progress.
    /// </summary>
    [JsonPropertyName("gamesInProgressByRegion")]
    public List<RegionCountDto> GamesInProgressByRegion { get; set; } = [];

    /// <summary>
    /// Idle or sleeping non-arcade game servers available to host matches, grouped by canonical region.
    /// </summary>
    [JsonPropertyName("serversAvailableByRegion")]
    public List<RegionCountDto> ServersAvailableByRegion { get; set; } = [];

    /// <summary>
    /// Players currently queued for TMM grouped by queue title and broken down by TMM region pool.
    /// Always includes default sections for <c>Forests of Caldavar | Ranked Pick (Normal)</c> and <c>Mid Wars</c>.
    /// </summary>
    [JsonPropertyName("tmmQueues")]
    public List<TmmQueueBreakdownDto> TmmQueues { get; set; } = [];

    /// <summary>
    /// UTC timestamp when this population snapshot was computed.
    /// </summary>
    [JsonPropertyName("updatedAtUtc")]
    public DateTimeOffset UpdatedAtUtc { get; set; }

    /// <summary>
    /// Freshness/availability indicator (<c>live</c>, <c>stale</c>, or <c>unavailable</c>).
    /// </summary>
    [JsonPropertyName("status")]
    public string Status { get; set; } = PopulationTelemetryStatus.Live;
}

/// <summary>
/// Shared canonical region mappings, default TMM queue labels, and safe empty/fallback builders
/// shared between ChatServer aggregation and MasterServer fallback rendering.
/// </summary>
public static class PopulationBreakdownDefaults
{
    /// <summary>
    /// Redis hash key where live telemetry metrics and population breakdown snapshots are stored.
    /// </summary>
    public const string TelemetryRedisHashKey = "TELEMETRY:METRICS";

    /// <summary>
    /// Redis hash field storing the serialized <see cref="PopulationBreakdownResponseDto"/> JSON snapshot.
    /// </summary>
    public const string PopulationBreakdownRedisField = "PopulationBreakdownJson";

    /// <summary>
    /// Aggregate row label representing all regions summed together under Games in progress.
    /// </summary>
    public const string AllLocationsLabel = "All locations";

    /// <summary>
    /// Canonical display name for Australia region.
    /// </summary>
    public const string RegionAustralia = "Australia";

    /// <summary>
    /// Canonical display name for Europe region.
    /// </summary>
    public const string RegionEurope = "Europe";

    /// <summary>
    /// Canonical display name for Latin America region.
    /// </summary>
    public const string RegionLatinAmerica = "Latin America";

    /// <summary>
    /// Canonical display name for Southeast Asia region.
    /// </summary>
    public const string RegionSoutheastAsia = "Southeast Asia";

    /// <summary>
    /// Canonical display name for US East server region.
    /// </summary>
    public const string RegionUsEast = "US East";

    /// <summary>
    /// Canonical display name for US West server region.
    /// </summary>
    public const string RegionUsWest = "US West";

    /// <summary>
    /// Canonical TMM region label when a group has no specific region filter or selected ANY/ALL/AUTO.
    /// </summary>
    public const string TmmRegionAny = "Any region";

    /// <summary>
    /// Canonical TMM shared US pool label combining USE and USW selections with per-group deduplication.
    /// </summary>
    public const string TmmRegionUsSharedPool = "US (shared pool)";

    /// <summary>
    /// Default primary TMM queue title for Forests of Caldavar Normal / Ranked Pick.
    /// </summary>
    public const string QueueTitleForestsOfCaldavarRankedNormal = "Forests of Caldavar | Ranked Pick (Normal)";

    /// <summary>
    /// Default secondary TMM queue title for Mid Wars.
    /// </summary>
    public const string QueueTitleMidWars = "Mid Wars";

    /// <summary>
    /// Ordered list of default TMM queue titles always included in population breakdown payloads.
    /// </summary>
    public static readonly IReadOnlyList<string> StandardTmmQueueTitles =
    [
        QueueTitleForestsOfCaldavarRankedNormal,
        QueueTitleMidWars
    ];

    /// <summary>
    /// Ordered list of standard TMM region pool labels always included per TMM queue section.
    /// </summary>
    public static readonly IReadOnlyList<string> StandardTmmRegions =
    [
        RegionAustralia,
        RegionEurope,
        RegionLatinAmerica,
        TmmRegionAny,
        RegionSoutheastAsia,
        TmmRegionUsSharedPool
    ];

    /// <summary>
    /// Maps a raw server location code (e.g., <c>USE</c>, <c>USW</c>, <c>EU</c>, <c>AU</c>, <c>SEA</c>, <c>LAT</c>, <c>SA</c>, <c>BR</c>)
    /// to its canonical human-readable display name.
    /// </summary>
    public static string NormalizeServerRegion(string? location)
    {
        if (string.IsNullOrWhiteSpace(location))
        {
            return RegionUsEast;
        }

        string trimmed = location.Trim();
        string upper = trimmed.ToUpperInvariant();
        return upper switch
        {
            "USE" or "US_EAST" or "US-EAST" or "US EAST" or "US" or "USC" or "USS" or "NEWERTH" or "LOCAL" or "LAN" or "DEFAULT" => RegionUsEast,
            "USW" or "US_WEST" or "US-WEST" or "US WEST" => RegionUsWest,
            "EU" or "EUROPE" or "EUW" or "EUE" or "DE" or "UK" or "NL" or "FR" or "RU" => RegionEurope,
            "AU" or "AUS" or "AUSTRALIA" or "OCE" => RegionAustralia,
            "SEA" or "SOUTHEAST_ASIA" or "SOUTHEAST-ASIA" or "SOUTHEAST ASIA" or "SG" or "MY" or "PH" or "TH" or "VN" or "ID" => RegionSoutheastAsia,
            "LAT" or "LATAM" or "SA" or "BR" or "BRAZIL" or "LATIN_AMERICA" or "LATIN-AMERICA" or "LATIN AMERICA" or "SOUTH_AMERICA" or "SOUTH-AMERICA" or "SOUTH AMERICA" => RegionLatinAmerica,
            _ => trimmed
        };
    }

    /// <summary>
    /// Maps a raw TMM group region selection code into its canonical TMM pool label,
    /// collapsing both <c>USE</c> and <c>USW</c> into <c>US (shared pool)</c> and empty/<c>ANY</c>/<c>ALL</c>/<c>AUTO</c>
    /// into <c>Any region</c>.
    /// </summary>
    public static string NormalizeTmmRegion(string? region)
    {
        if (string.IsNullOrWhiteSpace(region))
        {
            return TmmRegionAny;
        }

        string trimmed = region.Trim();
        string upper = trimmed.ToUpperInvariant();
        return upper switch
        {
            "ANY" or "ALL" or "AUTO" or "ANY REGION" or "ANY_REGION" or "ANY-REGION" => TmmRegionAny,
            "USE" or "USW" or "US" or "US_EAST" or "US-EAST" or "US EAST" or "US_WEST" or "US-WEST" or "US WEST" or "USC" or "USS" or "US (SHARED POOL)" or "US_SHARED" => TmmRegionUsSharedPool,
            "AU" or "AUS" or "AUSTRALIA" or "OCE" => RegionAustralia,
            "EU" or "EUROPE" or "EUW" or "EUE" or "DE" or "UK" or "NL" or "FR" or "RU" => RegionEurope,
            "LAT" or "LATAM" or "SA" or "BR" or "BRAZIL" or "LATIN_AMERICA" or "LATIN-AMERICA" or "LATIN AMERICA" or "SOUTH_AMERICA" or "SOUTH-AMERICA" or "SOUTH AMERICA" => RegionLatinAmerica,
            "SEA" or "SOUTHEAST_ASIA" or "SOUTHEAST-ASIA" or "SOUTHEAST ASIA" or "SG" or "MY" or "PH" or "TH" or "VN" or "ID" => RegionSoutheastAsia,
            _ => trimmed
        };
    }

    /// <summary>
    /// Creates zero-filled default TMM queue sections (<c>Forests of Caldavar | Ranked Pick (Normal)</c> and <c>Mid Wars</c>)
    /// across all standard TMM region pools.
    /// </summary>
    public static List<TmmQueueBreakdownDto> CreateDefaultTmmQueues()
    {
        List<TmmQueueBreakdownDto> queues = new(StandardTmmQueueTitles.Count);
        foreach (string title in StandardTmmQueueTitles)
        {
            List<RegionCountDto> regions = new(StandardTmmRegions.Count);
            foreach (string region in StandardTmmRegions)
            {
                regions.Add(new RegionCountDto { Region = region, Count = 0 });
            }

            queues.Add(new TmmQueueBreakdownDto
            {
                Title = title,
                TotalQueued = 0,
                Regions = regions
            });
        }

        return queues;
    }

    /// <summary>
    /// Creates a valid zero-filled <see cref="PopulationBreakdownResponseDto"/> payload with <c>All locations</c> = 0
    /// and standard zero-filled TMM queue sections.
    /// </summary>
    public static PopulationBreakdownResponseDto CreateEmpty(
        DateTimeOffset updatedAtUtc,
        int onlinePlayers = 0,
        string status = PopulationTelemetryStatus.Live)
    {
        return new PopulationBreakdownResponseDto
        {
            Status = status,
            OnlinePlayers = Math.Max(0, onlinePlayers),
            GamesInProgressTotal = 0,
            GamesInProgressByRegion =
            [
                new RegionCountDto { Region = AllLocationsLabel, Count = 0 }
            ],
            ServersAvailableByRegion = [],
            TmmQueues = CreateDefaultTmmQueues(),
            UpdatedAtUtc = updatedAtUtc
        };
    }

    /// <summary>
    /// Creates an honest degraded <see cref="PopulationBreakdownResponseDto"/> payload marked <c>unavailable</c>
    /// so UI views render a syncing/unavailable indicator instead of misleading zero counts.
    /// </summary>
    public static PopulationBreakdownResponseDto CreateUnavailable(DateTimeOffset updatedAtUtc)
    {
        return CreateEmpty(updatedAtUtc, onlinePlayers: 0, status: PopulationTelemetryStatus.Unavailable);
    }

    /// <summary>
    /// Normalizes a deserialized <see cref="PopulationBreakdownResponseDto"/> snapshot in place:
    /// reconciles live CCU, clamps negative counts, ensures <c>All locations</c> and default TMM queue sections exist,
    /// and assigns a valid <see cref="PopulationBreakdownResponseDto.Status"/>.
    /// </summary>
    public static void NormalizeForOutput(
        PopulationBreakdownResponseDto snapshot,
        int? overrideOnlinePlayers = null,
        string? status = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        snapshot.OnlinePlayers = overrideOnlinePlayers.HasValue
            ? Math.Max(0, overrideOnlinePlayers.Value)
            : Math.Max(0, snapshot.OnlinePlayers);
        snapshot.GamesInProgressTotal = Math.Max(0, snapshot.GamesInProgressTotal);
        snapshot.GamesInProgressByRegion ??= [];
        snapshot.ServersAvailableByRegion ??= [];
        snapshot.TmmQueues ??= [];

        bool hasAllLocations = snapshot.GamesInProgressByRegion.Any(static r =>
            string.Equals(r.Region, AllLocationsLabel, StringComparison.OrdinalIgnoreCase));
        if (!hasAllLocations)
        {
            snapshot.GamesInProgressByRegion.Insert(0, new RegionCountDto
            {
                Region = AllLocationsLabel,
                Count = snapshot.GamesInProgressTotal
            });
        }

        if (snapshot.TmmQueues.Count == 0)
        {
            snapshot.TmmQueues = CreateDefaultTmmQueues();
        }

        if (!string.IsNullOrWhiteSpace(status))
        {
            snapshot.Status = status;
        }
        else if (string.IsNullOrWhiteSpace(snapshot.Status))
        {
            snapshot.Status = PopulationTelemetryStatus.Live;
        }
    }
}
