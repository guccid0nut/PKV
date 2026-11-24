// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using StackExchange.Redis;

namespace ASPIRE.Common.Services.Distributed;

/// <summary>
///     High-performance distributed Redlock implementation providing token-safe distributed mutual exclusion
///     over Redis with automatic Lua compare-and-delete release and in-memory fallback.
/// </summary>
public sealed class RedisRedlockService : IDistributedLockService
{
    private const string ReleaseLockScript = @"
        if redis.call('get', KEYS[1]) == ARGV[1] then
            return redis.call('del', KEYS[1])
        else
            return 0
        end";

    private readonly IConnectionMultiplexer? _connectionMultiplexer;
    private readonly IDatabase? _database;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<RedisRedlockService> _logger;
    private readonly bool _allowInMemoryFallback;
    private volatile bool _luaScriptingDisabled;

    private sealed class InMemoryLockEntry
    {
        public string Token = string.Empty;
        public DateTimeOffset ExpiresAtUtc = DateTimeOffset.MinValue;
    }

    private readonly ConcurrentDictionary<string, InMemoryLockEntry> _inMemoryLocks = new();

    public RedisRedlockService(
        IConnectionMultiplexer? connectionMultiplexer = null,
        IDatabase? database = null,
        TimeProvider? timeProvider = null,
        ILogger<RedisRedlockService>? logger = null,
        Microsoft.Extensions.Hosting.IHostEnvironment? hostEnvironment = null,
        bool? allowInMemoryFallback = null)
    {
        _connectionMultiplexer = connectionMultiplexer;
        _database = database ?? connectionMultiplexer?.GetDatabase();
        _timeProvider = timeProvider ?? TimeProvider.System;
        _logger = logger ?? NullLogger<RedisRedlockService>.Instance;
        bool isProdWithRedis = hostEnvironment?.IsProduction() == true && (_database != null || _connectionMultiplexer != null);
        _allowInMemoryFallback = allowInMemoryFallback ?? !isProdWithRedis;
    }

    /// <inheritdoc />
    public async Task<IDistributedLock> AcquireLockAsync(
        string resource,
        TimeSpan expiry,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(resource))
            throw new ArgumentException("Resource cannot be null or whitespace.", nameof(resource));

        if (expiry <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(expiry), "Lock expiry must be greater than zero.");

        string token = Guid.NewGuid().ToString("N");
        long startTicks = _timeProvider.GetTimestamp();

        while (!cancellationToken.IsCancellationRequested)
        {
            DateTimeOffset now = _timeProvider.GetUtcNow();
            (bool acquired, bool usedInMemoryFallback) = await TryAcquireDirectAsync(resource, token, expiry).ConfigureAwait(false);

            if (acquired)
            {
                DateTimeOffset expiresAt = now + expiry;
                _logger.LogDebug("[Redlock] Acquired distributed lock for resource '{Resource}' with token '{Token}' (InMemoryFallback={InMemoryFallback}). Lease expires at {ExpiresAtUtc}.",
                    resource, token, usedInMemoryFallback, expiresAt);

                return new RedisDistributedLock(this, resource, token, true, usedInMemoryFallback, now, expiresAt);
            }

            TimeSpan elapsed = _timeProvider.GetElapsedTime(startTicks);
            if (elapsed >= timeout)
            {
                break;
            }

            TimeSpan remaining = timeout - elapsed;
            int jitterMs = Random.Shared.Next(15, 45);
            int delayMs = (int)Math.Min(jitterMs, remaining.TotalMilliseconds);
            if (delayMs <= 0)
            {
                break;
            }

            try
            {
                await Task.Delay(delayMs, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        DateTimeOffset failedTime = _timeProvider.GetUtcNow();
        _logger.LogWarning("[Redlock] Timed out waiting to acquire distributed lock for resource '{Resource}' after {TimeoutMs}ms.",
            resource, timeout.TotalMilliseconds);

        return new RedisDistributedLock(this, resource, token, false, false, failedTime, failedTime);
    }

    /// <inheritdoc />
    public Task<IDistributedLock> TryAcquireLockAsync(
        string resource,
        TimeSpan expiry,
        CancellationToken cancellationToken = default)
    {
        return AcquireLockAsync(resource, expiry, TimeSpan.Zero, cancellationToken);
    }

    private async Task<(bool Acquired, bool UsedInMemoryFallback)> TryAcquireDirectAsync(
        string resource,
        string token,
        TimeSpan expiry)
    {
        if (_database != null && (_connectionMultiplexer == null || _connectionMultiplexer.IsConnected))
        {
            try
            {
                bool acquiredInRedis = await _database.StringSetAsync(resource, token, expiry, false, When.NotExists).ConfigureAwait(false);
                return (acquiredInRedis, false);
            }
            catch (Exception ex)
            {
                if (!_allowInMemoryFallback)
                {
                    _logger.LogError(ex,
                        "[SECURITY-ALERT] [Redlock] Redis lock acquisition failed for '{Resource}' in Production (AllowInMemoryFallback=false). Failing closed to prevent multi-node split-brain.",
                        resource);
                    return (false, false);
                }

                _logger.LogWarning(ex, "[Redlock] Redis lock acquisition failed for '{Resource}'. Falling back to in-memory lock.", resource);
            }
        }
        else if (!_allowInMemoryFallback)
        {
            _logger.LogError(
                "[SECURITY-ALERT] [Redlock] Redis connection disconnected for '{Resource}' in Production (AllowInMemoryFallback=false). Failing closed.",
                resource);
            return (false, false);
        }

        // In-memory fallback for offline or standalone environments
        DateTimeOffset nowUtc = _timeProvider.GetUtcNow();
        if (_inMemoryLocks.Count > 64)
        {
            SweepExpiredInMemoryLocks(nowUtc);
        }

        for (int attempt = 0; attempt < 4; attempt++)
        {
            InMemoryLockEntry entry = _inMemoryLocks.GetOrAdd(resource, static _ => new InMemoryLockEntry());
            lock (entry)
            {
                if (!_inMemoryLocks.TryGetValue(resource, out InMemoryLockEntry? current) || !ReferenceEquals(current, entry))
                {
                    continue;
                }

                nowUtc = _timeProvider.GetUtcNow();
                if (string.IsNullOrEmpty(entry.Token) || nowUtc >= entry.ExpiresAtUtc)
                {
                    entry.Token = token;
                    entry.ExpiresAtUtc = nowUtc + expiry;
                    return (true, true);
                }

                return (false, true);
            }
        }

        return (false, true);
    }

    private void SweepExpiredInMemoryLocks(DateTimeOffset nowUtc)
    {
        ICollection<KeyValuePair<string, InMemoryLockEntry>> collection = _inMemoryLocks;
        foreach (KeyValuePair<string, InMemoryLockEntry> kv in _inMemoryLocks)
        {
            InMemoryLockEntry candidate = kv.Value;
            lock (candidate)
            {
                if (string.IsNullOrEmpty(candidate.Token) || nowUtc >= candidate.ExpiresAtUtc)
                {
                    candidate.Token = string.Empty;
                    candidate.ExpiresAtUtc = DateTimeOffset.MinValue;
                    collection.Remove(kv);
                }
            }
        }
    }

    internal async Task ReleaseLockAsync(string resource, string token, bool isInMemoryFallback)
    {
        if (!isInMemoryFallback && _database != null && (_connectionMultiplexer == null || _connectionMultiplexer.IsConnected))
        {
            if (!_luaScriptingDisabled)
            {
                try
                {
                    RedisResult result = await _database.ScriptEvaluateAsync(
                        ReleaseLockScript,
                        keys: [(RedisKey)resource],
                        values: [(RedisValue)token]).ConfigureAwait(false);

                    if ((int)result == 1)
                    {
                        _logger.LogDebug("[Redlock] Released distributed lock via EVAL for resource '{Resource}' with token '{Token}'.", resource, token);
                    }
                    else
                    {
                        _logger.LogWarning("[Redlock] Failed to release distributed lock via EVAL for resource '{Resource}'. Token '{Token}' mismatched or lock already expired.",
                            resource, token);
                    }
                    return;
                }
                catch (RedisServerException rex) when (rex.Message.Contains("Lua", StringComparison.OrdinalIgnoreCase) ||
                                                        rex.Message.Contains("unknown command", StringComparison.OrdinalIgnoreCase) ||
                                                        rex.Message.Contains("NOPERM", StringComparison.OrdinalIgnoreCase))
                {
                    _luaScriptingDisabled = true;
                    _logger.LogWarning(rex,
                        "[Redlock] Redis instance rejected Lua EVAL for '{Resource}'; switching permanently to atomic WATCH/MULTI/EXEC compare-and-delete.",
                        resource);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex,
                        "[Redlock] Redis Lua EVAL release failed for '{Resource}'; attempting Lua-free atomic transaction fallback.",
                        resource);
                }
            }

            if (await TryReleaseWithoutLuaAsync(resource, token).ConfigureAwait(false))
            {
                return;
            }
        }

        // In-memory fallback release (always executed if lock was acquired via in-memory fallback, even if Redis later reconnected)
        if (_inMemoryLocks.TryGetValue(resource, out InMemoryLockEntry? entry))
        {
            lock (entry)
            {
                if (entry.Token == token)
                {
                    entry.Token = string.Empty;
                    entry.ExpiresAtUtc = DateTimeOffset.MinValue;
                    ((ICollection<KeyValuePair<string, InMemoryLockEntry>>)_inMemoryLocks).Remove(
                        new KeyValuePair<string, InMemoryLockEntry>(resource, entry));
                    _logger.LogDebug("[Redlock] Released and evicted in-memory fallback lock for resource '{Resource}'.", resource);
                }
            }
        }
    }

    private async Task<bool> TryReleaseWithoutLuaAsync(string resource, string token)
    {
        if (_database == null)
        {
            return false;
        }

        for (int attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                ITransaction tx = _database.CreateTransaction();
                tx.AddCondition(Condition.StringEqual(resource, token));
                _ = tx.KeyDeleteAsync(resource);
                bool committed = await tx.ExecuteAsync().ConfigureAwait(false);
                if (committed)
                {
                    _logger.LogDebug(
                        "[Redlock] Released distributed lock via Lua-free WATCH/MULTI/EXEC for resource '{Resource}' with token '{Token}' (attempt={Attempt}).",
                        resource, token, attempt + 1);
                    return true;
                }

                RedisValue currentOwner = await _database.StringGetAsync(resource).ConfigureAwait(false);
                if (currentOwner.IsNull || currentOwner != token)
                {
                    _logger.LogDebug(
                        "[Redlock] Lua-free release skipped for '{Resource}': lock expired or held by a different token.",
                        resource);
                    return true;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "[Redlock] Lua-free WATCH/MULTI/EXEC release attempt {Attempt} failed for '{Resource}'.",
                    attempt + 1, resource);
                return false;
            }
        }

        return false;
    }

    private sealed class RedisDistributedLock : IDistributedLock
    {
        private readonly RedisRedlockService _service;
        private readonly bool _isInMemoryFallback;
        private int _disposed;

        public string Resource { get; }
        public string LockToken { get; }
        public bool IsAcquired { get; }
        public DateTimeOffset AcquiredAtUtc { get; }
        public DateTimeOffset ExpiresAtUtc { get; }

        public RedisDistributedLock(
            RedisRedlockService service,
            string resource,
            string lockToken,
            bool isAcquired,
            bool isInMemoryFallback,
            DateTimeOffset acquiredAtUtc,
            DateTimeOffset expiresAtUtc)
        {
            _service = service;
            _isInMemoryFallback = isInMemoryFallback;
            Resource = resource;
            LockToken = lockToken;
            IsAcquired = isAcquired;
            AcquiredAtUtc = acquiredAtUtc;
            ExpiresAtUtc = expiresAtUtc;
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

            if (IsAcquired)
            {
                await _service.ReleaseLockAsync(Resource, LockToken, _isInMemoryFallback).ConfigureAwait(false);
            }
        }
    }
}
