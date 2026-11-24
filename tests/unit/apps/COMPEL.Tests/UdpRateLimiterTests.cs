// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Diagnostics;
using Xunit.Abstractions;

namespace COMPEL.Tests;

/// <summary>
///     Verifies that the high-performance Token-Bucket UdpRateLimiter allows burst traffic,
///     throttles excessive unauthenticated flood traffic, grants expanded bandwidth to authenticated clients,
///     handles 32-bit millisecond wraparound cleanly, and evicts stale IP buckets deterministically via TimeProvider.
/// </summary>
public sealed class UdpRateLimiterTests(ITestOutputHelper output)
{
    private readonly ITestOutputHelper _output = output;

    [Fact]
    public void UdpRateLimiter_Allows_Burst_And_Throttles_Flood()
    {
        UdpRateLimiter limiter = new(10.0, 5.0);
        IPAddress clientIp = IPAddress.Parse("192.168.1.100");

        // The first 5 packets should be allowed by the burst capacity
        for (int i = 0; i < 5; i++) Assert.True(limiter.ShouldAllow(clientIp));

        // The 6th immediate packet should be throttled
        Assert.False(limiter.ShouldAllow(clientIp));
    }

    [Fact]
    public void Authenticated_Client_Gets_Expanded_Capacity()
    {
        UdpRateLimiter limiter = new(5.0, 2.0, 100.0, 50.0);
        IPAddress clientIp = IPAddress.Parse("192.168.1.101");

        // Consume initial burst
        limiter.ShouldAllow(clientIp);
        limiter.ShouldAllow(clientIp);
        Assert.False(limiter.ShouldAllow(clientIp));

        // Authenticate client
        limiter.MarkAuthenticated(clientIp);

        // Now client has expanded authenticated capacity
        for (int i = 0; i < 20; i++) Assert.True(limiter.ShouldAllow(clientIp));
    }

    [Fact]
    public async Task Concurrent_Access_ShouldBeLockFree_AndNeverDeadlockOrThrow()
    {
        UdpRateLimiter limiter = new(1000.0, 500.0, 5000.0, 2500.0);
        IPAddress clientIp = IPAddress.Parse("192.168.1.102");

        // Spawn 16 concurrent tasks hammering ShouldAllow simultaneously
        Task[] tasks = Enumerable.Range(0, 16).Select(_ => Task.Run(() =>
        {
            for (int i = 0; i < 1000; i++)
            {
                limiter.ShouldAllow(clientIp);
            }
        })).ToArray();

        await Task.WhenAll(tasks);

        // Verify state is clean and authentication transitions under load
        limiter.MarkAuthenticated(clientIp);
        Assert.True(limiter.ShouldAllow(clientIp));
    }

    [Fact]
    public void VirtualClock_60SecondFloodAndFractionalRefill_ExecutesDeterministicallyInMilliseconds()
    {
        ControllableTimeProvider clock = new();
        // 10 tokens/sec (1 token per 100ms), burst capacity 5 tokens
        UdpRateLimiter limiter = new(
            refillRatePerSecond: 10.0,
            burstCapacity: 5.0,
            authenticatedRefillRate: 2000.0,
            authenticatedBurstCapacity: 1000.0,
            timeProvider: clock);
        IPAddress attackerIp = IPAddress.Parse("203.0.113.77");

        // Exhaust initial burst of 5
        for (int i = 0; i < 5; i++)
        {
            Assert.True(limiter.ShouldAllow(attackerIp));
        }

        // Immediate flood of 5,000 packets at t=0ms must all drop
        int droppedInFlood = 0;
        for (int i = 0; i < 5_000; i++)
        {
            if (!limiter.ShouldAllow(attackerIp))
            {
                droppedInFlood++;
            }
        }
        Assert.Equal(5_000, droppedInFlood);

        // Advance 50ms -> 0.5 tokens accumulated (< 1.0 token) -> still dropped
        clock.Advance(TimeSpan.FromMilliseconds(50));
        Assert.False(limiter.ShouldAllow(attackerIp));

        // Advance another 50ms (total 100ms) -> exactly 1.0 token refilled -> 1 allowed, next dropped
        clock.Advance(TimeSpan.FromMilliseconds(50));
        Assert.True(limiter.ShouldAllow(attackerIp));
        Assert.False(limiter.ShouldAllow(attackerIp));

        // Simulate 60 seconds of sustained 100 pkt/s flood (1 packet every 10ms = 6,000 packets):
        // At 10 tokens/sec refill, exactly 600 packets must be allowed and 5,400 dropped.
        int allowedOverMinute = 0;
        for (int step = 0; step < 6_000; step++)
        {
            clock.Advance(TimeSpan.FromMilliseconds(10));
            if (limiter.ShouldAllow(attackerIp))
            {
                allowedOverMinute++;
            }
        }
        Assert.Equal(600, allowedOverMinute);
    }

    [Fact]
    public void VirtualClock_Uint32MillisecondWraparound_And_StaleBucketEviction_WorkCleanly()
    {
        ControllableTimeProvider clock = new();
        UdpRateLimiter limiter = new(
            refillRatePerSecond: 100.0,
            burstCapacity: 10.0,
            timeProvider: clock);

        IPAddress staleSpoofedIp = IPAddress.Parse("198.51.100.10");
        IPAddress activePlayerIp = IPAddress.Parse("198.51.100.20");

        // Advance right before 32-bit unsigned millisecond wraparound (~49.71 days = 0xFFFF_FFF0 ms)
        clock.Advance(TimeSpan.FromMilliseconds(0xFFFF_FFF0L));

        // Drain staleSpoofedIp and activePlayerIp right before uint32 wraparound
        for (int i = 0; i < 10; i++)
        {
            Assert.True(limiter.ShouldAllow(staleSpoofedIp));
            Assert.True(limiter.ShouldAllow(activePlayerIp));
        }
        Assert.False(limiter.ShouldAllow(activePlayerIp));
        Assert.Equal(2, limiter.ActiveBucketCount);

        // Cross the 32-bit wraparound boundary (+32ms -> wraps past 0xFFFF_FFFF to 0x0000_0010)
        // 32ms at 100 tokens/sec = 3.2 tokens -> exactly 3 packets allowed without negative/stuck delta!
        clock.Advance(TimeSpan.FromMilliseconds(32));
        Assert.True(limiter.ShouldAllow(activePlayerIp));
        Assert.True(limiter.ShouldAllow(activePlayerIp));
        Assert.True(limiter.ShouldAllow(activePlayerIp));
        Assert.False(limiter.ShouldAllow(activePlayerIp));

        // Cleanup before 30s throttle interval is a no-op
        limiter.CleanupInactive(TimeSpan.FromMinutes(5));
        Assert.Equal(2, limiter.ActiveBucketCount);

        // Advance 6 minutes while keeping activePlayerIp alive at minute 4
        clock.Advance(TimeSpan.FromMinutes(4));
        Assert.True(limiter.ShouldAllow(activePlayerIp));

        clock.Advance(TimeSpan.FromMinutes(2));
        limiter.CleanupInactive(TimeSpan.FromMinutes(5));

        // staleSpoofedIp (inactive 6m > 5m cutoff) is evicted; activePlayerIp (inactive 2m < 5m) remains!
        Assert.Equal(1, limiter.ActiveBucketCount);

        // Log non-gating throughput telemetry on System clock fast path (never asserted to avoid parallel CI flake)
        UdpRateLimiter prodLimiter = new();
        Stopwatch sw = Stopwatch.StartNew();
        for (int i = 0; i < 100_000; i++)
        {
            prodLimiter.ShouldAllow(activePlayerIp);
        }
        sw.Stop();
        double millionOpsPerSec = 0.1 / Math.Max(0.000_001, sw.Elapsed.TotalSeconds);
        _output.WriteLine($"[TELEMETRY] UdpRateLimiter System-clock fast path: 100,000 calls in {sw.Elapsed.TotalMilliseconds:F2} ms ({millionOpsPerSec:F2} M ops/s)");
    }
}
