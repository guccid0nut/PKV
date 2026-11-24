// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using MediatR;
using NEXUS.Contracts.DTOs;

namespace NEXUS.Contracts.CQRS.PlayerStats;

/// <summary>
///     Query to retrieve the paginated, searchable, and sortable directory of active Newerth clans.
///     Implements <see cref="ICacheableQuery{TResponse}"/> with a 60-second TTL for fast portal &amp; search responses.
/// </summary>
public sealed record GetClanDirectoryQuery(
    int Page = 1,
    int PageSize = 24,
    string? Search = null,
    string Sort = "mmr")
    : IRequest<ClanDirectoryResultDto>, ICacheableQuery<ClanDirectoryResultDto>
{
    public string CacheKey =>
        $"cache:clans:dir:p{Page}:ps{PageSize}:s{Search?.Trim().ToLowerInvariant()}:sort{Sort.Trim().ToLowerInvariant()}";

    // shortcut: 60s directory cache window since ClanRepository only invalidates clan:{id}, evict clans:directory tag in ClanRepository when real-time directory invalidation is required
    public TimeSpan CacheDuration => TimeSpan.FromSeconds(60);
}

/// <summary>
///     Query to retrieve full public details and ordered active member roster for a single clan.
///     Cached inside the handler using tag <c>clan:{ClanId}</c> so ClanRepository mutations invalidate it immediately.
/// </summary>
public sealed record GetClanDetailQuery(int ClanId) : IRequest<ClanDetailDto?>;
