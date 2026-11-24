// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using FluentAssertions;
using TRANSMUTANSTEIN.ChatServer.Domain.Core;
using Xunit;

namespace TRANSMUTANSTEIN.ChatServer.Tests.Domain;

public sealed class SessionTokenBucketRateLimiterTests
{
    [Fact]
    public void TryConsume_WithinBurstCapacity_ReturnsTrue()
    {
        SessionTokenBucketRateLimiter limiter = new(refillRatePerSecond: 10, maxBurstCapacity: 20);

        for (int i = 0; i < 20; i++)
        {
            limiter.TryConsume(1).Should().BeTrue();
        }

        limiter.ConsecutiveViolations.Should().Be(0);
    }

    [Fact]
    public void TryConsume_ExceedingBurstCapacity_ReturnsFalseAndIncrementsViolations()
    {
        SessionTokenBucketRateLimiter limiter = new(refillRatePerSecond: 10, maxBurstCapacity: 5);

        for (int i = 0; i < 5; i++)
        {
            limiter.TryConsume(1).Should().BeTrue();
        }

        // 6th attempt exhausts bucket
        limiter.TryConsume(1).Should().BeFalse();
        limiter.ConsecutiveViolations.Should().Be(1);

        // 7th attempt increments violations again
        limiter.TryConsume(1).Should().BeFalse();
        limiter.ConsecutiveViolations.Should().Be(2);
    }

    [Fact]
    public void SessionTokenBucketRateLimiter_Reset_RestoresCapacityAndClearsViolations()
    {
        SessionTokenBucketRateLimiter limiter = new(refillRatePerSecond: 10, maxBurstCapacity: 5);

        for (int i = 0; i < 7; i++)
        {
            limiter.TryConsume(1);
        }

        limiter.ConsecutiveViolations.Should().Be(2);

        limiter.Reset();
        limiter.ConsecutiveViolations.Should().Be(0);
        limiter.TryConsume(1).Should().BeTrue();
    }
}
