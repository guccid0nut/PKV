// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using MediatR;

namespace NEXUS.Contracts.CQRS;

/// <summary>
///     Marker interface for MediatR queries whose response can be transparently cached.
/// </summary>
public interface ICacheableQuery<out TResponse> : IRequest<TResponse>
{
    /// <summary>
    ///     The unique cache key for the query result.
    /// </summary>
    string CacheKey { get; }

    /// <summary>
    ///     The cache time-to-live duration.
    /// </summary>
    TimeSpan CacheDuration => TimeSpan.FromMinutes(5);

    /// <summary>
    ///     When true (opt-in for read-mostly queries), permits FusionCache Fail-Safe to serve stale cached
    ///     data if the upstream query handler throws or times out. Defaults to false (fail-closed).
    /// </summary>
    bool AllowStaleOnError => false;
}
