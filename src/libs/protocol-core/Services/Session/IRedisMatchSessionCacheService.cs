// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ============================================================================
// File: IRedisMatchSessionCacheService.cs
// System: Kongor Protocol Core - Live Redis Match Session Cache & Pub/Sub Contract
// Policy: Strict Explicit Typing (NO 'var')
// ============================================================================

using NEXUS.Contracts.Telemetry;

namespace Kongor.Core.Services.Session;

/// <summary>
///     Contract for ultra-fast O(1) live match state caching and real-time event streaming
///     via Redis, with seamless fallback to in-memory caching when Redis is disconnected.
/// </summary>
public interface IRedisMatchSessionCacheService
{
    /// <summary>
    ///     Caches live match state with sliding TTL (default: 2 hours).
    /// </summary>
    Task SetLiveMatchStateAsync(
        int matchId,
        LiveServerTickPayloadDto payload,
        TimeSpan? ttl = null,
        CancellationToken ct = default);

    /// <summary>
    ///     Retrieves the current cached live match state, or null if match is inactive or expired.
    /// </summary>
    Task<LiveServerTickPayloadDto?> GetLiveMatchStateAsync(
        int matchId,
        CancellationToken ct = default);

    /// <summary>
    ///     Removes live match state from cache upon match conclusion.
    /// </summary>
    Task RemoveLiveMatchStateAsync(
        int matchId,
        CancellationToken ct = default);

    /// <summary>
    ///     Publishes a live combat or match state event to the Redis Pub/Sub channel.
    /// </summary>
    Task PublishMatchEventAsync(
        int matchId,
        string eventType,
        string eventJson,
        CancellationToken ct = default);

    /// <summary>
    ///     Indicates whether the underlying Redis connection is active and connected.
    /// </summary>
    bool IsRedisConnected { get; }
}
