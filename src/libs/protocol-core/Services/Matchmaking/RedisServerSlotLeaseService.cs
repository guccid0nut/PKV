// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Collections.Concurrent;
using MemoryPack;
using Microsoft.Extensions.Logging;
using NEXUS.Contracts.Domain.Matches;
using StackExchange.Redis;

namespace ASPIRE.Common.Services.Matchmaking;

/// <summary>
///     Result of an atomic server slot lease reservation attempt.
/// </summary>
public sealed record SlotLeaseResult(
    bool Succeeded,
    string LockKey,
    string LeaseToken,
    string? ContestedBy = null,
    string? Message = null);

/// <summary>
///     Provides distributed mutex and lease management for dedicated game server slot allocations.
///     Prevents split-brain server double-booking across multi-instance matchmaker clusters.
/// </summary>
public interface IRedisServerSlotLeaseService
{
    Task<SlotLeaseResult> TryAcquireSlotLeaseAsync(
        string region,
        string serverAddress,
        int serverPort,
        string workerId,
        TimeSpan leaseDuration,
        CancellationToken ct = default);

    Task<bool> RenewSlotLeaseAsync(
        string lockKey,
        string leaseToken,
        TimeSpan extensionDuration,
        CancellationToken ct = default);

    Task<bool> ReleaseSlotLeaseAsync(
        string lockKey,
        string leaseToken,
        CancellationToken ct = default);
}

/// <summary>
///     Redis/Garnet-backed distributed slot lease manager using atomic Lua scripts with in-memory fallback.
/// </summary>
public sealed class RedisServerSlotLeaseService : IRedisServerSlotLeaseService
{
    private const string AcquireScript = @"
        if redis.call('SET', KEYS[1], ARGV[1], 'NX', 'PX', ARGV[2]) then
            return 1
        else
            return redis.call('GET', KEYS[1])
        end";

    private const string RenewScript = @"
        if redis.call('GET', KEYS[1]) == ARGV[1] then
            return redis.call('PEXPIRE', KEYS[1], ARGV[2])
        else
            return 0
        end";

    private const string ReleaseScript = @"
        if redis.call('GET', KEYS[1]) == ARGV[1] then
            return redis.call('DEL', KEYS[1])
        else
            return 0
        end";

    private readonly IConnectionMultiplexer? _redis;
    private readonly ILogger<RedisServerSlotLeaseService> _logger;
    private readonly TimeProvider _timeProvider;
    private readonly ConcurrentDictionary<string, (string Holder, DateTimeOffset ExpiresAt)> _localFallback = new();

    public RedisServerSlotLeaseService(
        ILogger<RedisServerSlotLeaseService> logger,
        IConnectionMultiplexer? redis = null,
        TimeProvider? timeProvider = null)
    {
        _logger = logger;
        _redis = redis;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public static string GenerateLockKey(string region, string serverAddress, int serverPort) =>
        $"lock:server_slot:{region.ToLowerInvariant()}:{serverAddress}:{serverPort}";

    public async Task<SlotLeaseResult> TryAcquireSlotLeaseAsync(
        string region,
        string serverAddress,
        int serverPort,
        string workerId,
        TimeSpan leaseDuration,
        CancellationToken ct = default)
    {
        string lockKey = GenerateLockKey(region, serverAddress, serverPort);
        long ttlMs = (long)Math.Max(1000, leaseDuration.TotalMilliseconds);

        if (_redis != null && _redis.IsConnected)
        {
            try
            {
                IDatabase db = _redis.GetDatabase();
                RedisResult result = await db.ScriptEvaluateAsync(
                    AcquireScript,
                    [new RedisKey(lockKey)],
                    [new RedisValue(workerId), new RedisValue(ttlMs.ToString())]).ConfigureAwait(false);

                if (result.ToString() == "1")
                {
                    _logger.LogInformation(
                        "[ServerSlotLease] Successfully acquired distributed lease on {LockKey} for Worker {WorkerId} (TTL: {TtlMs}ms)",
                        lockKey, workerId, ttlMs);

                    return new SlotLeaseResult(true, lockKey, workerId, Message: "Acquired");
                }

                string contestedBy = result.ToString() ?? "Unknown";
                _logger.LogWarning(
                    "[ServerSlotLease] Slot {LockKey} contested by existing lease holder: {Holder}. Cannot allocate.",
                    lockKey, contestedBy);

                return new SlotLeaseResult(false, lockKey, workerId, ContestedBy: contestedBy, Message: "Contested");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[ServerSlotLease] Redis error while acquiring lease; falling back to local memory.");
            }
        }

        // Local In-Memory Fallback
        DateTimeOffset now = _timeProvider.GetUtcNow();
        bool acquired = false;
        string? existingHolder = null;

        _localFallback.AddOrUpdate(
            lockKey,
            _ =>
            {
                acquired = true;
                return (workerId, now.AddMilliseconds(ttlMs));
            },
            (_, existing) =>
            {
                if (existing.ExpiresAt <= now)
                {
                    acquired = true;
                    return (workerId, now.AddMilliseconds(ttlMs));
                }

                existingHolder = existing.Holder;
                return existing;
            });

        if (acquired)
        {
            return new SlotLeaseResult(true, lockKey, workerId, Message: "Acquired (Local Fallback)");
        }

        return new SlotLeaseResult(false, lockKey, workerId, ContestedBy: existingHolder, Message: "Contested (Local Fallback)");
    }

    public async Task<bool> RenewSlotLeaseAsync(
        string lockKey,
        string leaseToken,
        TimeSpan extensionDuration,
        CancellationToken ct = default)
    {
        long ttlMs = (long)Math.Max(1000, extensionDuration.TotalMilliseconds);

        if (_redis != null && _redis.IsConnected)
        {
            try
            {
                IDatabase db = _redis.GetDatabase();
                RedisResult result = await db.ScriptEvaluateAsync(
                    RenewScript,
                    [new RedisKey(lockKey)],
                    [new RedisValue(leaseToken), new RedisValue(ttlMs.ToString())]).ConfigureAwait(false);

                return result.ToString() == "1";
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[ServerSlotLease] Redis error renewing lease for {LockKey}.", lockKey);
            }
        }

        // Local In-Memory Fallback
        if (_localFallback.TryGetValue(lockKey, out var existing) && existing.Holder == leaseToken)
        {
            _localFallback[lockKey] = (leaseToken, _timeProvider.GetUtcNow().AddMilliseconds(ttlMs));
            return true;
        }

        return false;
    }

    public async Task<bool> ReleaseSlotLeaseAsync(
        string lockKey,
        string leaseToken,
        CancellationToken ct = default)
    {
        if (_redis != null && _redis.IsConnected)
        {
            try
            {
                IDatabase db = _redis.GetDatabase();
                RedisResult result = await db.ScriptEvaluateAsync(
                    ReleaseScript,
                    [new RedisKey(lockKey)],
                    [new RedisValue(leaseToken)]).ConfigureAwait(false);

                bool released = result.ToString() == "1";
                if (released)
                {
                    _logger.LogInformation("[ServerSlotLease] Released distributed lease on {LockKey}.", lockKey);
                }

                return released;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[ServerSlotLease] Redis error releasing lease for {LockKey}.", lockKey);
            }
        }

        // Local In-Memory Fallback
        if (_localFallback.TryGetValue(lockKey, out var existing) && existing.Holder == leaseToken)
        {
            return _localFallback.TryRemove(lockKey, out _);
        }

        return false;
    }
}

/// <summary>
///     Service for publishing and consuming matchmaking tickets via Redis Streams.
/// </summary>
public interface IRedisMatchmakingStreamService
{
    Task<string?> PublishTicketAsync(MatchmakingTicketDto ticket, CancellationToken ct = default);

    Task<IReadOnlyList<(string MessageId, MatchmakingTicketDto Ticket)>> ReadTicketsAsync(
        string region,
        string gameType,
        string mapName,
        string consumerGroup,
        string consumerName,
        int count = 10,
        CancellationToken ct = default);

    Task<bool> AcknowledgeTicketAsync(
        string streamKey,
        string consumerGroup,
        string messageId,
        CancellationToken ct = default);
}

/// <summary>
///     Redis Streams implementation for distributed matchmaking queues.
/// </summary>
public sealed class RedisMatchmakingStreamService : IRedisMatchmakingStreamService
{
    private readonly IConnectionMultiplexer? _redis;
    private readonly ILogger<RedisMatchmakingStreamService> _logger;
    private readonly ConcurrentDictionary<string, List<MatchmakingTicketDto>> _localQueue = new();

    public RedisMatchmakingStreamService(
        ILogger<RedisMatchmakingStreamService> logger,
        IConnectionMultiplexer? redis = null)
    {
        _logger = logger;
        _redis = redis;
    }

    public static string GenerateStreamKey(string region, string gameType, string mapName) =>
        $"matchmaking:stream:{region.ToLowerInvariant()}:{gameType.ToLowerInvariant()}:{mapName.ToLowerInvariant()}";

    public async Task<string?> PublishTicketAsync(MatchmakingTicketDto ticket, CancellationToken ct = default)
    {
        string streamKey = GenerateStreamKey(ticket.Region, ticket.GameType, ticket.MapName);
        byte[] payloadBytes = MemoryPackSerializer.Serialize(ticket);

        if (_redis != null && _redis.IsConnected)
        {
            try
            {
                IDatabase db = _redis.GetDatabase();
                RedisValue messageId = await db.StreamAddAsync(
                    streamKey,
                    [new NameValueEntry("payload", payloadBytes)]).ConfigureAwait(false);

                _logger.LogInformation(
                    "[MatchmakingStream] Published ticket {TicketId} (Group {GroupGuid}) to Redis Stream {StreamKey} as {MessageId}",
                    ticket.TicketId, ticket.GroupGuid, streamKey, messageId);

                return messageId.ToString();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[MatchmakingStream] Failed publishing ticket to Redis Stream; falling back to local queue.");
            }
        }

        // Local Queue Fallback
        _localQueue.AddOrUpdate(
            streamKey,
            _ => [ticket],
            (_, list) =>
            {
                lock (list)
                {
                    list.Add(ticket);
                }
                return list;
            });

        return Guid.NewGuid().ToString();
    }

    public async Task<IReadOnlyList<(string MessageId, MatchmakingTicketDto Ticket)>> ReadTicketsAsync(
        string region,
        string gameType,
        string mapName,
        string consumerGroup,
        string consumerName,
        int count = 10,
        CancellationToken ct = default)
    {
        string streamKey = GenerateStreamKey(region, gameType, mapName);
        List<(string, MatchmakingTicketDto)> results = [];

        if (_redis != null && _redis.IsConnected)
        {
            try
            {
                IDatabase db = _redis.GetDatabase();

                // Ensure consumer group exists
                try
                {
                    await db.StreamCreateConsumerGroupAsync(streamKey, consumerGroup, StreamPosition.Beginning).ConfigureAwait(false);
                }
                catch (RedisServerException)
                {
                    // BusyGroup / already exists - expected
                }

                StreamEntry[] entries = await db.StreamReadGroupAsync(
                    streamKey,
                    consumerGroup,
                    consumerName,
                    StreamPosition.NewMessages,
                    count).ConfigureAwait(false);

                foreach (StreamEntry entry in entries)
                {
                    RedisValue val = entry["payload"];
                    if (val.HasValue)
                    {
                        byte[] bytes = (byte[])val!;
                        MatchmakingTicketDto? ticket = MemoryPackSerializer.Deserialize<MatchmakingTicketDto>(bytes);
                        if (ticket != null)
                        {
                            results.Add((entry.Id.ToString(), ticket));
                        }
                    }
                }

                return results;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[MatchmakingStream] Error reading from Redis Stream {StreamKey}; checking local fallback.", streamKey);
            }
        }

        // Local Queue Fallback
        if (_localQueue.TryGetValue(streamKey, out var list))
        {
            lock (list)
            {
                int toTake = Math.Min(count, list.Count);
                for (int i = 0; i < toTake; i++)
                {
                    results.Add((Guid.NewGuid().ToString(), list[i]));
                }
                list.RemoveRange(0, toTake);
            }
        }

        return results;
    }

    public async Task<bool> AcknowledgeTicketAsync(
        string streamKey,
        string consumerGroup,
        string messageId,
        CancellationToken ct = default)
    {
        if (_redis != null && _redis.IsConnected)
        {
            try
            {
                IDatabase db = _redis.GetDatabase();
                long ackCount = await db.StreamAcknowledgeAsync(streamKey, consumerGroup, messageId).ConfigureAwait(false);
                return ackCount > 0;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[MatchmakingStream] Error acknowledging message {MessageId} on {StreamKey}.", messageId, streamKey);
            }
        }

        return true;
    }
}
