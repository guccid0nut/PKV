// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Net;
using TRANSMUTANSTEIN.ChatServer.Domain.Security;

namespace ASPIRE.Tests.TRANSMUTANSTEIN.ChatServer.Tests;

public class IpConnectionRateLimiterTests
{
    [Fact]
    public void TryAcquireToken_WithinBurstLimit_ReturnsTrue()
    {
        IpConnectionRateLimiter limiter = new(5, 1.0);
        IPAddress ip = IPAddress.Parse("192.168.1.1");

        for (int i = 0; i < 5; i++) Assert.True(limiter.TryAcquireToken(ip));
    }

    [Fact]
    public void TryAcquireToken_ExceedsBurstLimit_ReturnsFalse()
    {
        IpConnectionRateLimiter limiter = new(3, 1.0);
        IPAddress ip = IPAddress.Parse("192.168.1.2");

        for (int i = 0; i < 3; i++) Assert.True(limiter.TryAcquireToken(ip));

        Assert.False(limiter.TryAcquireToken(ip));
    }

    [Fact]
    public void TryAcquireToken_Disabled_AlwaysReturnsTrue()
    {
        IpConnectionRateLimiter limiter = new(1, 1.0, false);
        IPAddress ip = IPAddress.Parse("192.168.1.3");

        for (int i = 0; i < 10; i++) Assert.True(limiter.TryAcquireToken(ip));
    }
}
