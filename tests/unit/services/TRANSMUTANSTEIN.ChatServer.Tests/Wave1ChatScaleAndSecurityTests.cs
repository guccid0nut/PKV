// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Net;
using FluentAssertions;
using TRANSMUTANSTEIN.ChatServer.Domain.Security;
using TRANSMUTANSTEIN.ChatServer.Infrastructure.Distributed;
using Xunit;

namespace TRANSMUTANSTEIN.ChatServer.Tests;

public sealed class Wave1ChatScaleAndSecurityTests
{
    [Fact]
    public void DistributedChatStateService_PresenceShardKeys_UseDeterministicCrossProcessXxHash32()
    {
        // protects: Cross-replica chat presence lookup consistency across independent .NET processes (Marvin string.GetHashCode is randomized per process).
        // fails_when: GetPresenceUsersKey uses string.GetHashCode() instead of deterministic XxHash32, or case mismatches map to different shards.
        // seam: Real DistributedChatStateService.GetPresenceUsersKey & GetPresenceByIdKey static shard routers.

        string lower = DistributedChatStateService.GetPresenceUsersKey("guccidonut");
        string mixed = DistributedChatStateService.GetPresenceUsersKey("GucciDonut");

        lower.Should().Be(mixed);
        // Pin exact deterministic XxHash32("guccidonut" UTF-8) % 64 shard so any future non-deterministic regression fails immediately
        uint expectedBucket = System.IO.Hashing.XxHash32.HashToUInt32("guccidonut"u8) % 64;
        lower.Should().Be($"kongor:chat:users:presence:{expectedBucket}");

        DistributedChatStateService.GetPresenceByIdKey(130).Should().Be("kongor:chat:users:by_id:2");
    }

    [Fact]
    public void IpConnectionRateLimiter_BoundsTrackedIpMemoryUnderBotnetFlood()
    {
        // protects: ChatServer TCP accept loop against heap exhaustion when flooded by tens of thousands of unique source IPs.
        // fails_when: IpConnectionRateLimiter uses an unbounded ConcurrentDictionary that grows past MaxTrackedIps without eviction.
        // seam: Real IpConnectionRateLimiter backed by size-limited MemoryCache.

        using IpConnectionRateLimiter limiter = new(maxBurstTokens: 2, refillTokensPerSecond: 0.1, enabled: true, maxTrackedIps: 32);

        for (int i = 1; i <= 200; i++)
        {
            IPAddress ip = new([10, 0, (byte)(i / 256), (byte)(i % 256)]);
            limiter.TryAcquireToken(ip).Should().BeTrue();
        }

        // MemoryCache compacts asynchronously/on-insert around SizeLimit; verify count remains bounded well below total 200 unique IPs
        limiter.TrackedIpCount.Should().BeLessThanOrEqualTo(32);

        IPAddress throttledIp = IPAddress.Parse("198.51.100.77");
        limiter.TryAcquireToken(throttledIp).Should().BeTrue();
        limiter.TryAcquireToken(throttledIp).Should().BeTrue();
        limiter.TryAcquireToken(throttledIp).Should().BeFalse("burst limit of 2 must still throttle active IP");

        limiter.Reset();
        limiter.TrackedIpCount.Should().Be(0);
        limiter.TryAcquireToken(throttledIp).Should().BeTrue("Reset must clear rate-limit buckets cleanly");
    }
}
