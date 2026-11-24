// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using MediatR;
using NEXUS.Contracts.DTOs;

namespace NEXUS.Contracts.CQRS.PlayerStats;

/// <summary>
///     Query to retrieve a structured custom-map scoreboard for a match.
/// </summary>
public sealed record GetMatchCustomStatsQuery(int MatchId) : IRequest<CustomMatchStatsResponseDto?>;

/// <summary>
///     Query to retrieve per-map career statistics for a player.
/// </summary>
public sealed record GetPlayerMapStatsQuery(string Nickname, string? MapSlug) : IRequest<PlayerMapStatsResponseDto?>;

/// <summary>
///     Query to retrieve a player's aggregated overview across played maps.
/// </summary>
public sealed record GetPlayerMapsOverviewQuery(string Nickname) : IRequest<IReadOnlyList<PlayerMapOverviewItemDto>>;
