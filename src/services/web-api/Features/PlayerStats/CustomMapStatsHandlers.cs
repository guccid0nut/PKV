// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using MediatR;
using Microsoft.Extensions.Logging;
using NEXUS.Contracts.CQRS.PlayerStats;
using NEXUS.Contracts.Data;
using NEXUS.Contracts.DTOs;

namespace ZORGATH.WebPortal.API.Features.PlayerStats;

/// <summary>
///     MediatR handler for structured custom-map match scoreboards.
/// </summary>
public sealed class GetMatchCustomStatsHandler(
    ICustomMapStatsRepository customMapStatsRepository,
    ILogger<GetMatchCustomStatsHandler>? logger = null)
    : IRequestHandler<GetMatchCustomStatsQuery, CustomMatchStatsResponseDto?>
{
    public async Task<CustomMatchStatsResponseDto?> Handle(
        GetMatchCustomStatsQuery request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        CustomMatchStatsResponseDto? result = await customMapStatsRepository
            .GetMatchCustomStatsAsync(request.MatchId, heroResolver: null, ct: cancellationToken)
            .ConfigureAwait(false);
        logger?.LogDebug(
            "[CustomMapStats] Handled GetMatchCustomStatsQuery(MatchId={MatchId}) -> Found={Found}.",
            request.MatchId,
            result is not null);
        return result;
    }
}

/// <summary>
///     MediatR handler for per-map player career statistics.
/// </summary>
public sealed class GetPlayerMapStatsHandler(
    ICustomMapStatsRepository customMapStatsRepository,
    ILogger<GetPlayerMapStatsHandler>? logger = null)
    : IRequestHandler<GetPlayerMapStatsQuery, PlayerMapStatsResponseDto?>
{
    public async Task<PlayerMapStatsResponseDto?> Handle(
        GetPlayerMapStatsQuery request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        PlayerMapStatsResponseDto? result = await customMapStatsRepository
            .GetPlayerMapStatsAsync(request.Nickname, request.MapSlug, heroResolver: null, ct: cancellationToken)
            .ConfigureAwait(false);
        logger?.LogDebug(
            "[CustomMapStats] Handled GetPlayerMapStatsQuery(Nickname='{Nickname}', Map='{MapSlug}') -> Found={Found}.",
            request.Nickname,
            request.MapSlug,
            result is not null);
        return result;
    }
}

/// <summary>
///     MediatR handler for player multi-map overview statistics.
/// </summary>
public sealed class GetPlayerMapsOverviewHandler(
    ICustomMapStatsRepository customMapStatsRepository,
    ILogger<GetPlayerMapsOverviewHandler>? logger = null)
    : IRequestHandler<GetPlayerMapsOverviewQuery, IReadOnlyList<PlayerMapOverviewItemDto>>
{
    public async Task<IReadOnlyList<PlayerMapOverviewItemDto>> Handle(
        GetPlayerMapsOverviewQuery request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        IReadOnlyList<PlayerMapOverviewItemDto> result = await customMapStatsRepository
            .GetPlayerMapOverviewAsync(request.Nickname, cancellationToken)
            .ConfigureAwait(false);
        logger?.LogDebug(
            "[CustomMapStats] Handled GetPlayerMapsOverviewQuery(Nickname='{Nickname}') -> MapCount={Count}.",
            request.Nickname,
            result.Count);
        return result;
    }
}
