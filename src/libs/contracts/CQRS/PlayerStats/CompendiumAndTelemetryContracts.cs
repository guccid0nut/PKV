// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using MediatR;
using NEXUS.Contracts.Clients;
using NEXUS.Contracts.Telemetry;

namespace NEXUS.Contracts.CQRS.PlayerStats;

/// <summary>
///     Query to retrieve recorded evidence assets for a match.
/// </summary>
/// <param name="MatchId">The unique match identifier.</param>
public sealed record GetMatchEvidenceQuery(int MatchId) : IRequest<MatchEvidenceDto?>;

/// <summary>
///     Command to create or update match evidence records.
/// </summary>
/// <param name="MatchId">The unique match identifier.</param>
/// <param name="Evidence">The match evidence payload.</param>
public sealed record SaveMatchEvidenceCommand(int MatchId, MatchEvidenceDto Evidence) : IRequest<bool>;

/// <summary>
///     Query to retrieve all approved community game server hosts.
/// </summary>
public sealed record GetServerHostsQuery : IRequest<List<ServerHostDto>>;

/// <summary>
///     Query to retrieve compendium details for all Heroes of Newerth heroes.
/// </summary>
public sealed record GetAllHeroDetailsQuery : IRequest<List<HeroDetailDto>>, ICacheableQuery<List<HeroDetailDto>>
{
    /// <inheritdoc />
    public string CacheKey => "cache:compendium:heroes:all";

    /// <inheritdoc />
    public TimeSpan CacheDuration => TimeSpan.FromHours(1);
}

/// <summary>
///     Query to retrieve compendium details for a specific hero by code or name.
/// </summary>
/// <param name="HeroId">The hero code identifier or display name.</param>
public sealed record GetHeroDetailQuery(string HeroId) : IRequest<HeroDetailDto?>, ICacheableQuery<HeroDetailDto?>
{
    /// <inheritdoc />
    public string CacheKey => $"cache:compendium:hero:{HeroId.Trim().ToLowerInvariant()}";

    /// <inheritdoc />
    public TimeSpan CacheDuration => TimeSpan.FromHours(1);
}

/// <summary>
///     Query to retrieve compendium definitions and attributes for all hero items.
/// </summary>
public sealed record GetAllHeroItemsQuery : IRequest<List<HeroItemDto>>, ICacheableQuery<List<HeroItemDto>>
{
    /// <inheritdoc />
    public string CacheKey => "cache:compendium:items:all";

    /// <inheritdoc />
    public TimeSpan CacheDuration => TimeSpan.FromHours(1);
}

/// <summary>
///     Command to submit a player conduct report for moderation review.
/// </summary>
/// <param name="Report">The player report submission payload.</param>
public sealed record SubmitPlayerReportCommand(PlayerReportDto Report) : IRequest<bool>;

/// <summary>
///     Query to retrieve a list of login names for approved content creators and streamers.
/// </summary>
public sealed record GetApprovedStreamersQuery : IRequest<List<string>>, ICacheableQuery<List<string>>
{
    /// <inheritdoc />
    public string CacheKey => "cache:streamers:approved";

    /// <inheritdoc />
    public TimeSpan CacheDuration => TimeSpan.FromMinutes(10);
}

/// <summary>
///     Query to retrieve live server cluster telemetry including active players and ongoing matches.
/// </summary>
public sealed record GetLiveClusterTelemetryQuery : IRequest<LiveTelemetryDataDto>;

/// <summary>
///     Query to retrieve live in-memory/Redis cached match telemetry for an active match.
/// </summary>
/// <param name="MatchId">The unique match identifier.</param>
public sealed record GetLiveMatchTelemetryQuery(int MatchId) : IRequest<LiveServerTickPayloadDto?>;
