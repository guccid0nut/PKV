// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Collections.Concurrent;
using System.Net;

namespace COMPEL.Services.Proxy;

/// <summary>
///     High-performance, lock-free Token-Bucket UDP Rate Limiter.
///     Protects public proxy game and voice ports (20000-29999) from unauthenticated packet floods,
///     amplification attacks, and malicious port scans using 64-bit atomic CAS without monitor locks or heap allocation.
/// </summary>
public sealed class UdpRateLimiter
{
    private const long Scale = 10_000L; // 10,000 units per token (0.0001 token precision)

    private readonly int _burstCapacityUnits;
    private readonly long _refillRateUnitsPerSec;
    private readonly int _authenticatedBurstCapacityUnits;
    private readonly long _authenticatedRefillRateUnitsPerSec;
    private readonly TimeProvider _timeProvider;

    private readonly ConcurrentDictionary<IPAddress, TokenBucket> _buckets = new();
    private long _lastCleanupTicks;

    public UdpRateLimiter(
        double refillRatePerSecond = 300.0,
        double burstCapacity = 150.0,
        double authenticatedRefillRate = 2000.0,
        double authenticatedBurstCapacity = 1000.0,
        TimeProvider? timeProvider = null)
    {
        _burstCapacityUnits = (int)Math.Round(burstCapacity * Scale);
        _refillRateUnitsPerSec = (long)Math.Round(refillRatePerSecond * Scale);
        _authenticatedBurstCapacityUnits = (int)Math.Round(authenticatedBurstCapacity * Scale);
        _authenticatedRefillRateUnitsPerSec = (long)Math.Round(authenticatedRefillRate * Scale);
        _timeProvider = timeProvider ?? TimeProvider.System;
        _lastCleanupTicks = GetMonotonicMs();
    }

    /// <summary>
    ///     Current number of tracked per-IP token buckets (used for diagnostics and eviction verification).
    /// </summary>
    public int ActiveBucketCount => _buckets.Count;

    private long GetMonotonicMs()
    {
        long ts = _timeProvider.GetTimestamp();
        long freq = _timeProvider.TimestampFrequency;
        if (freq == 1_000L)
        {
            return ts;
        }

        return (long)(((Int128)ts * 1_000) / freq);
    }

    /// <summary>
    ///     Checks if an incoming datagram from the specified IP address should be allowed.
    ///     Returns true if allowed, false if throttled/dropped.
    ///     Completely lock-free via 64-bit atomic CompareExchange.
    /// </summary>
    public bool ShouldAllow(IPAddress clientAddress)
    {
        long now64 = GetMonotonicMs();
        uint now32 = (uint)(now64 & 0xFFFFFFFF);

        TokenBucket bucket = _buckets.GetOrAdd(clientAddress, _ =>
        {
            long initialPacked = ((long)_burstCapacityUnits << 32) | (long)now32;
            return new TokenBucket(initialPacked, now64);
        });

        Volatile.Write(ref bucket.LastActivityTicks, now64);

        long oldState = Volatile.Read(ref bucket.State);
        while (true)
        {
            uint oldTimestamp = (uint)(oldState & 0xFFFFFFFF);
            int oldTokenUnits = (int)(oldState >> 32);

            uint elapsedMs = unchecked(now32 - oldTimestamp);

            bool isAuth = Volatile.Read(ref bucket.IsAuthenticated) != 0;
            int maxCapacityUnits = isAuth ? _authenticatedBurstCapacityUnits : _burstCapacityUnits;
            long refillRateUnitsPerSec = isAuth ? _authenticatedRefillRateUnitsPerSec : _refillRateUnitsPerSec;

            long addedUnits = elapsedMs > 0 ? (elapsedMs * refillRateUnitsPerSec) / 1000L : 0;
            int currentUnits = (int)Math.Min((long)maxCapacityUnits, (long)oldTokenUnits + addedUnits);

            uint newTimestamp = elapsedMs > 0 ? now32 : oldTimestamp;

            bool allowed = currentUnits >= Scale;
            int remainingUnits = allowed ? currentUnits - (int)Scale : currentUnits;

            long newState = ((long)remainingUnits << 32) | (long)newTimestamp;
            long current = Interlocked.CompareExchange(ref bucket.State, newState, oldState);
            if (current == oldState)
            {
                return allowed;
            }

            oldState = current;
        }
    }

    /// <summary>
    ///     Marks a client IP address as authenticated once it successfully answers a proxy challenge.
    /// </summary>
    public void MarkAuthenticated(IPAddress clientAddress)
    {
        if (_buckets.TryGetValue(clientAddress, out TokenBucket? bucket))
        {
            Interlocked.Exchange(ref bucket.IsAuthenticated, 1);
            long now64 = GetMonotonicMs();
            uint now32 = (uint)(now64 & 0xFFFFFFFF);
            Volatile.Write(ref bucket.LastActivityTicks, now64);

            long oldState = Volatile.Read(ref bucket.State);
            while (true)
            {
                long newState = ((long)_authenticatedBurstCapacityUnits << 32) | (long)now32;
                long current = Interlocked.CompareExchange(ref bucket.State, newState, oldState);
                if (current == oldState)
                {
                    break;
                }

                oldState = current;
            }
        }
    }

    /// <summary>
    ///     Evicts inactive IP buckets to prevent memory accumulation over time.
    /// </summary>
    public void CleanupInactive(TimeSpan inactiveThreshold)
    {
        long now = GetMonotonicMs();
        if (now - Volatile.Read(ref _lastCleanupTicks) < 30000) // Run at most every 30 seconds
            return;

        Volatile.Write(ref _lastCleanupTicks, now);
        long cutoff = now - (long)inactiveThreshold.TotalMilliseconds;

        foreach (KeyValuePair<IPAddress, TokenBucket> kvp in _buckets)
            if (Volatile.Read(ref kvp.Value.LastActivityTicks) < cutoff)
                _buckets.TryRemove(kvp.Key, out _);
    }

    private sealed class TokenBucket
    {
        public long State;
        public int IsAuthenticated;
        public long LastActivityTicks;

        public TokenBucket(long initialState, long now)
        {
            State = initialState;
            LastActivityTicks = now;
        }
    }
}
