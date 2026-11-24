// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using MediatR;
using NEXUS.Contracts.Fleet;

namespace NEXUS.Contracts.CQRS.PlayerStats;

/// <summary>
///     Query to retrieve the host leaderboard ranked by hosting ratio and games hosted.
///     Implements <see cref="ICacheableQuery{TResponse}"/> for in-memory / distributed caching.
/// </summary>
public sealed record GetHostLeaderboardQuery()
    : IRequest<IReadOnlyList<HostLeaderboardEntryDto>>, ICacheableQuery<IReadOnlyList<HostLeaderboardEntryDto>>
{
    public string CacheKey => "cache:leaderboard:hosts";
    public TimeSpan CacheDuration => TimeSpan.FromMinutes(2);
}
