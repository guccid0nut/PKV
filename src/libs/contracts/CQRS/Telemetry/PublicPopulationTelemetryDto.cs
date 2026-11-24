// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Text.Json.Serialization;
using MemoryPack;

namespace NEXUS.Contracts.CQRS.Telemetry;

/// <summary>
/// Strictly scoped, non-PII public aggregate telemetry contract exposed by anonymous portal endpoints
/// (<c>GET /api/telemetry/population</c>). Intentionally decoupled from <c>AdminMetricsDTO</c> so
/// internal infrastructure metrics (RAM, SQL warnings, latency, host topology) can never serialize
/// through this contract.
/// </summary>
[MemoryPackable]
public sealed partial class PublicPopulationTelemetryDto
{
    /// <summary>
    /// Freshness/availability indicator (<c>live</c>, <c>stale</c>, or <c>unavailable</c>).
    /// </summary>
    [JsonPropertyName("status")]
    public string Status { get; set; } = PopulationTelemetryStatus.Live;

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
    /// Games in progress starting with <c>All locations</c> followed by each active region.
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
    /// </summary>
    [JsonPropertyName("tmmQueues")]
    public List<TmmQueueBreakdownDto> TmmQueues { get; set; } = [];

    /// <summary>
    /// UTC timestamp when this population snapshot was computed.
    /// </summary>
    [JsonPropertyName("updatedAtUtc")]
    public DateTimeOffset UpdatedAtUtc { get; set; }

    /// <summary>
    /// Projects a <see cref="PopulationBreakdownResponseDto"/> into an isolated public payload.
    /// </summary>
    public static PublicPopulationTelemetryDto FromBreakdown(PopulationBreakdownResponseDto? source, DateTimeOffset fallbackUtc)
    {
        PopulationBreakdownResponseDto resolved = source ?? PopulationBreakdownDefaults.CreateUnavailable(fallbackUtc);
        return new PublicPopulationTelemetryDto
        {
            Status = string.IsNullOrWhiteSpace(resolved.Status) ? PopulationTelemetryStatus.Live : resolved.Status,
            OnlinePlayers = Math.Max(0, resolved.OnlinePlayers),
            GamesInProgressTotal = Math.Max(0, resolved.GamesInProgressTotal),
            GamesInProgressByRegion = resolved.GamesInProgressByRegion ?? [],
            ServersAvailableByRegion = resolved.ServersAvailableByRegion ?? [],
            TmmQueues = resolved.TmmQueues ?? [],
            UpdatedAtUtc = resolved.UpdatedAtUtc == default ? fallbackUtc : resolved.UpdatedAtUtc
        };
    }

    /// <summary>
    /// Creates an honest degraded public payload marked <c>unavailable</c>.
    /// </summary>
    public static PublicPopulationTelemetryDto CreateUnavailable(DateTimeOffset updatedAtUtc)
    {
        return FromBreakdown(PopulationBreakdownDefaults.CreateUnavailable(updatedAtUtc), updatedAtUtc);
    }
}
