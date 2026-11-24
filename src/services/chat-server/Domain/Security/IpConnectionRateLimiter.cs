// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using Microsoft.Extensions.Caching.Memory;
using Log = Serilog.Log;

namespace TRANSMUTANSTEIN.ChatServer.Domain.Security;

/// <summary>
///     High-performance thread-safe token-bucket IP connection rate limiter backed by a size-bounded
///     <see cref="MemoryCache" /> so spoofed/botnet IP floods cannot exhaust server heap memory.
/// </summary>
public class IpConnectionRateLimiter : IDisposable
{
    public const int DefaultMaxTrackedIps = 65_536;
    private static readonly TimeSpan EntrySlidingExpiration = TimeSpan.FromMinutes(15);

    private readonly Lock _cacheSwapLock = new();
    private MemoryCache _buckets;

    public int MaxBurstTokens { get; set; }
    public double RefillTokensPerSecond { get; set; }
    public bool Enabled { get; set; }
    public int MaxTrackedIps { get; }
    public int TrackedIpCount => _buckets.Count;

    public IpConnectionRateLimiter(
        int maxBurstTokens = 10,
        double refillTokensPerSecond = 2.0,
        bool enabled = true,
        int maxTrackedIps = DefaultMaxTrackedIps)
    {
        MaxBurstTokens = maxBurstTokens;
        RefillTokensPerSecond = refillTokensPerSecond;
        Enabled = enabled;
        MaxTrackedIps = Math.Max(16, maxTrackedIps);
        _buckets = CreateCache(MaxTrackedIps);
    }

    private static MemoryCache CreateCache(int sizeLimit) =>
        new(new MemoryCacheOptions
        {
            SizeLimit = sizeLimit,
            CompactionPercentage = 0.20
        });

    public bool TryAcquireToken(IPAddress ip)
    {
        if (!Enabled || IPAddress.IsLoopback(ip)) return true;

        MemoryCache activeCache = _buckets;
        if (!activeCache.TryGetValue(ip, out TokenBucket? bucket) || bucket is null)
        {
            if (activeCache.Count >= MaxTrackedIps)
            {
                lock (_cacheSwapLock)
                {
                    if (activeCache.Count >= MaxTrackedIps)
                    {
                        activeCache.Compact(0.25);
                    }
                }
            }

            bucket = activeCache.GetOrCreate(ip, entry =>
            {
                entry.SetSize(1);
                entry.SetSlidingExpiration(EntrySlidingExpiration);
                return new TokenBucket(MaxBurstTokens);
            })!;
        }

        bool acquired = bucket.TryConsume(MaxBurstTokens, RefillTokensPerSecond);
        if (!acquired)
            Log.Debug("[RateLimiter] IP {IP} exceeded connection rate limit ({MaxBurstTokens} tokens)", ip,
                MaxBurstTokens);

        return acquired;
    }

    public void Reset()
    {
        MemoryCache oldCache;
        lock (_cacheSwapLock)
        {
            oldCache = _buckets;
            _buckets = CreateCache(MaxTrackedIps);
        }

        oldCache.Dispose();
    }

    public void Dispose()
    {
        lock (_cacheSwapLock)
        {
            _buckets.Dispose();
        }
        GC.SuppressFinalize(this);
    }

    private sealed class TokenBucket(double initialTokens)
    {
        private long _lastRefillTicks = Environment.TickCount64;
        private double _tokens = initialTokens;

        public bool TryConsume(int maxBurst, double refillPerSecond)
        {
            lock (this)
            {
                long now = Environment.TickCount64;
                double elapsedSeconds = (now - _lastRefillTicks) / 1000.0;
                if (elapsedSeconds > 0)
                {
                    _tokens = Math.Min(maxBurst, _tokens + elapsedSeconds * refillPerSecond);
                    _lastRefillTicks = now;
                }

                if (_tokens >= 1.0)
                {
                    _tokens -= 1.0;
                    return true;
                }

                return false;
            }
        }
    }
}
