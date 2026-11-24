// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ============================================================================
// File: RedisMatchSessionCacheService.cs
// System: Kongor Protocol Core - Live Redis Match Session Cache & Pub/Sub
// Policy: Strict Explicit Typing (NO 'var')
// ============================================================================

using System.Globalization;
using System.Text;
using System.Text.Json;
using Kongor.Core.Serialization.JSON;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NEXUS.Contracts.Telemetry;
using StackExchange.Redis;

namespace Kongor.Core.Services.Session;

/// <summary>
///     High-performance Redis match session cache and Pub/Sub publisher for live match telemetry.
///     Gracefully degrades to local memory caching (IMemoryCache) if Redis is disconnected or unavailable.
/// </summary>
public sealed class RedisMatchSessionCacheService : IRedisMatchSessionCacheService
{
    private static readonly TimeSpan DefaultSlidingTtl = TimeSpan.FromHours(2);
    public static readonly RedisChannel MatchEventsChannel = RedisChannel.Literal("matches:live:events");

    private readonly IConnectionMultiplexer? _muxer;
    private readonly IMemoryCache _memoryCache;
    private readonly ILogger<RedisMatchSessionCacheService> _logger;

    public RedisMatchSessionCacheService(
        IConnectionMultiplexer? muxer = null,
        IMemoryCache? memoryCache = null,
        ILogger<RedisMatchSessionCacheService>? logger = null)
    {
        _muxer = muxer;
        _memoryCache = memoryCache ?? new MemoryCache(new MemoryCacheOptions());
        _logger = logger ?? NullLogger<RedisMatchSessionCacheService>.Instance;
    }

    /// <inheritdoc />
    public bool IsRedisConnected
    {
        get
        {
            try
            {
                return _muxer != null && _muxer.IsConnected;
            }
            catch
            {
                return false;
            }
        }
    }

    private static string BuildMatchKey(int matchId)
    {
        string idStr = matchId.ToString(CultureInfo.InvariantCulture);
        const string prefix = "match:live:";
        return string.Create(prefix.Length + idStr.Length, (prefix, idStr), static (span, state) =>
        {
            state.prefix.AsSpan().CopyTo(span);
            state.idStr.AsSpan().CopyTo(span[state.prefix.Length..]);
        });
    }

    /// <inheritdoc />
    public async Task SetLiveMatchStateAsync(
        int matchId,
        LiveServerTickPayloadDto payload,
        TimeSpan? ttl = null,
        CancellationToken ct = default)
    {
        TimeSpan effectiveTtl = ttl ?? DefaultSlidingTtl;
        string key = BuildMatchKey(matchId);

        byte[] utf8Bytes = JsonSerializer.SerializeToUtf8Bytes(
            payload, KongorCoreJsonContext.Default.LiveServerTickPayloadDto);

        // Always update local memory cache for immediate L1 read speed
        _memoryCache.Set(key, payload, effectiveTtl);

        if (IsRedisConnected && _muxer != null)
        {
            try
            {
                IDatabase db = _muxer.GetDatabase();
                await db.StringSetAsync(key, utf8Bytes, effectiveTtl).ConfigureAwait(false);

                _logger.LogDebug(
                    "[RedisMatchSessionCache] Successfully stored state for Match #{MatchId} in Redis ({ByteCount} bytes, TTL: {TTL})",
                    matchId, utf8Bytes.Length, effectiveTtl);
                return;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "[RedisMatchSessionCache] Failed writing Match #{MatchId} to Redis; retained in L1 memory cache",
                    matchId);
            }
        }
        else
        {
            _logger.LogDebug(
                "[RedisMatchSessionCache] Redis offline; cached Match #{MatchId} in L1 memory cache only",
                matchId);
        }
    }

    /// <inheritdoc />
    public async Task<LiveServerTickPayloadDto?> GetLiveMatchStateAsync(
        int matchId,
        CancellationToken ct = default)
    {
        string key = BuildMatchKey(matchId);

        // 1. Check L1 memory cache first for sub-microsecond retrieval
        if (_memoryCache.TryGetValue(key, out LiveServerTickPayloadDto? memoryCached) && memoryCached != null)
        {
            return memoryCached;
        }

        // 2. Check Redis distributed cache
        if (IsRedisConnected && _muxer != null)
        {
            try
            {
                IDatabase db = _muxer.GetDatabase();
                RedisValue redisVal = await db.StringGetAsync(key).ConfigureAwait(false);

                if (!redisVal.IsNullOrEmpty)
                {
                    byte[] rawBytes = (byte[]?)redisVal ?? Encoding.UTF8.GetBytes(redisVal.ToString());
                    LiveServerTickPayloadDto? deserialized = JsonSerializer.Deserialize(
                        rawBytes, KongorCoreJsonContext.Default.LiveServerTickPayloadDto);

                    if (deserialized != null)
                    {
                        // Repopulate L1 memory cache
                        _memoryCache.Set(key, deserialized, DefaultSlidingTtl);
                        return deserialized;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "[RedisMatchSessionCache] Error retrieving Match #{MatchId} from Redis",
                    matchId);
            }
        }

        return null;
    }

    /// <inheritdoc />
    public async Task RemoveLiveMatchStateAsync(
        int matchId,
        CancellationToken ct = default)
    {
        string key = BuildMatchKey(matchId);
        _memoryCache.Remove(key);

        if (IsRedisConnected && _muxer != null)
        {
            try
            {
                IDatabase db = _muxer.GetDatabase();
                await db.KeyDeleteAsync(key).ConfigureAwait(false);
                _logger.LogInformation(
                    "[RedisMatchSessionCache] Evicted live match state for concluded Match #{MatchId} from Redis",
                    matchId);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "[RedisMatchSessionCache] Error evicting Match #{MatchId} from Redis",
                    matchId);
            }
        }
    }

    /// <inheritdoc />
    public async Task PublishMatchEventAsync(
        int matchId,
        string eventType,
        string eventJson,
        CancellationToken ct = default)
    {
        string payload = $$"""{"matchId":{{matchId}},"eventType":"{{eventType}}","payload":{{eventJson}}}""";

        _logger.LogInformation(
            "[RedisMatchSessionCache] Publishing live match event: {EventType} for Match #{MatchId}",
            eventType, matchId);

        if (IsRedisConnected && _muxer != null)
        {
            try
            {
                ISubscriber sub = _muxer.GetSubscriber();
                long recipientCount = await sub.PublishAsync(MatchEventsChannel, payload).ConfigureAwait(false);

                _logger.LogDebug(
                    "[RedisMatchSessionCache] Broadcast event {EventType} to {RecipientCount} Redis subscribers",
                    eventType, recipientCount);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "[RedisMatchSessionCache] Failed publishing event {EventType} to Redis channel {Channel}",
                    eventType, MatchEventsChannel);
            }
        }
    }
}
