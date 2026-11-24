// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Threading.RateLimiting;
using KONGOR.MasterServer.Constants;

namespace ASPIRE.Tests.KONGOR.MasterServer.Tests;

public sealed class MasterServerRateLimitingTests

{
    [Fact]
    public void TokenBucketLimiter_AllowsRequestsWithinBurstLimit_AndBlocksExcess()

    {
        TokenBucketRateLimiterOptions options = new()

        {
            TokenLimit = 5,

            QueueLimit = 0,

            ReplenishmentPeriod = TimeSpan.FromMinutes(1),

            TokensPerPeriod = 1,

            AutoReplenishment = false
        };


        using TokenBucketRateLimiter limiter = new(options);


        // First 5 requests should be permitted

        for (int i = 0; i < 5; i++)

        {
            using RateLimitLease lease = limiter.AttemptAcquire();

            Assert.True(lease.IsAcquired, $"Request {i + 1} should be permitted");
        }


        // 6th request exceeding TokenLimit should be rejected

        using RateLimitLease excessLease = limiter.AttemptAcquire();

        Assert.False(excessLease.IsAcquired, "Request exceeding TokenLimit must be rejected");
    }


    [Fact]
    public void RateLimiterPolicies_PerIpTokenBucketConstant_IsDefinedCorrectly()

    {
        Assert.Equal("PerIpTokenBucket", RateLimiterPolicies.PerIpTokenBucket);
    }
}
