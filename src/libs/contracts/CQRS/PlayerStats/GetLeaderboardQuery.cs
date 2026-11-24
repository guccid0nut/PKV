// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using MediatR;
using NEXUS.Contracts.Clients;

namespace NEXUS.Contracts.CQRS.PlayerStats;

/// <summary>
///     Query to retrieve the paginated and filtered player leaderboard.
///     Implements <see cref="ICacheableQuery{TResponse}"/> for automatic Redis/HybridCache resolution.
/// </summary>
public sealed record GetLeaderboardQuery(
    int Page = 1,
    int PageSize = 50,
    string? Search = null,
    string? Mode = null,
    string? Map = null,
    string Sort = "winrate")
    : IRequest<LeaderboardResultDto>, ICacheableQuery<LeaderboardResultDto>
{
    public string CacheKey =>
        $"cache:leaderboard:p{Page}:ps{PageSize}:s{Search?.Trim().ToLowerInvariant()}:m{Mode?.Trim().ToLowerInvariant()}:map{Map?.Trim().ToLowerInvariant()}:sort{Sort.Trim().ToLowerInvariant()}";

    public TimeSpan CacheDuration => TimeSpan.FromMinutes(1);
}
