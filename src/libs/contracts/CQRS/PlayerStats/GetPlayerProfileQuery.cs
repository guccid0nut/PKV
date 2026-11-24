// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using MediatR;
using NEXUS.Contracts.Clients;

namespace NEXUS.Contracts.CQRS.PlayerStats;

/// <summary>
///     Query to retrieve the full player profile summary by in-game name (IGN).
///     Implements <see cref="ICacheableQuery{TResponse}"/> for automatic Redis/HybridCache resolution.
/// </summary>
public sealed record GetPlayerProfileQuery(string InGameName)
    : IRequest<PlayerProfileSummaryDto?>, ICacheableQuery<PlayerProfileSummaryDto?>
{
    public string CacheKey => $"cache:player:profile:{InGameName.Trim().ToLowerInvariant()}";

    public TimeSpan CacheDuration => TimeSpan.FromMinutes(2);
}
