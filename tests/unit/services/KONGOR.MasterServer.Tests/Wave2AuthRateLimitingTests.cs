// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Net;
using System.Reflection;
using FluentAssertions;
using KONGOR.MasterServer.Constants;
using KONGOR.MasterServer.Controllers;
using KONGOR.MasterServer.Controllers.ClientRequesterController;
using KONGOR.MasterServer.Services.Requester;
using KONGOR.MasterServer.Services.Security;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using StackExchange.Redis;
using Xunit;

namespace KONGOR.MasterServer.Tests;

public sealed class Wave2AuthRateLimitingTests
{
    private sealed class MutableFakeTimeProvider : TimeProvider
    {
        private DateTimeOffset _utcNow = new(2026, 11, 19, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _utcNow;
        public void Advance(TimeSpan delta) => _utcNow = _utcNow.Add(delta);
    }

    [Fact]
    public void PerIpAuthRateLimiter_BlocksAfterThreshold_IsolatesOtherIps_AndRecoversAfterWindow()
    {
        // protects: Per-IP brute-force protection on client_requester.php authentication functions.
        // fails_when: Auth attempts exceed per-IP sliding window without 429 block, or block leaks to other IPs / non-auth calls.
        // seam: PerIpAuthRateLimiter with deterministic TimeProvider.
        MutableFakeTimeProvider clock = new();
        PerIpAuthRateLimiter limiter = new(
            timeProvider: clock,
            maxAttemptsOverride: 3,
            windowOverride: TimeSpan.FromSeconds(60));

        limiter.TryAcquire("203.0.113.10", "auth").IsAllowed.Should().BeTrue();
        limiter.TryAcquire("203.0.113.10", "pre_auth").IsAllowed.Should().BeTrue();
        limiter.TryAcquire("203.0.113.10", "srpauth").IsAllowed.Should().BeTrue();

        // 4th attempt from same IP must be rejected with positive RetryAfterSeconds
        AuthRateLimitDecision blocked = limiter.TryAcquire("203.0.113.10", "auth");
        blocked.IsAllowed.Should().BeFalse();
        blocked.RetryAfterSeconds.Should().BeGreaterThan(0);

        // Distinct IP and non-auth functions must NOT be blocked
        limiter.TryAcquire("203.0.113.11", "auth").IsAllowed.Should().BeTrue();
        limiter.TryAcquire("203.0.113.10", "ping").IsAllowed.Should().BeTrue();
        limiter.TryAcquire("203.0.113.10", "get_upgrades").IsAllowed.Should().BeTrue();

        // Advancing past sliding window restores capacity cleanly
        clock.Advance(TimeSpan.FromSeconds(61));
        limiter.TryAcquire("203.0.113.10", "auth").IsAllowed.Should().BeTrue();
    }

    [Fact]
    public async Task ClientRequesterController_ReturnsHttp429WithRetryAfter_WhenAuthRateLimitExceeded()
    {
        // protects: Real HTTP 429 enforcement on client_requester.php?f=auth before handler dispatch.
        // fails_when: ClientRequesterController fails to short-circuit with 429 or omits Retry-After header.
        // seam: ClientRequesterController.ProcessRequest -> PerIpAuthRateLimiter.
        MutableFakeTimeProvider clock = new();
        PerIpAuthRateLimiter limiter = new(
            timeProvider: clock,
            maxAttemptsOverride: 1,
            windowOverride: TimeSpan.FromSeconds(60));

        // Exhaust the single permit for 198.51.100.7
        limiter.TryAcquire("198.51.100.7", "auth").IsAllowed.Should().BeTrue();

        Mock<IDatabase> mockRedis = new();
        ClientRequestDispatcher dispatcher = new(new Mock<IServiceProvider>().Object);
        ClientRequesterController controller = new(
            mockRedis.Object,
            NullLogger<ClientRequesterController>.Instance,
            dispatcher,
            limiter);

        DefaultHttpContext httpCtx = new();
        httpCtx.Request.Method = "GET";
        httpCtx.Request.QueryString = new QueryString("?f=auth&cookie=NULL");
        httpCtx.Connection.RemoteIpAddress = IPAddress.Parse("198.51.100.7");
        controller.ControllerContext = new ControllerContext { HttpContext = httpCtx };

        IActionResult result = await controller.ProcessRequest();

        ObjectResult statusObj = result.Should().BeOfType<ObjectResult>().Subject;
        statusObj.StatusCode.Should().Be(StatusCodes.Status429TooManyRequests);
        httpCtx.Response.Headers.RetryAfter.ToString().Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void AccountManagementController_DeclaresStrictRateLimitingPolicy()
    {
        // protects: Legacy signup/nick-check/password-retrieve endpoints against enumeration and flood abuse.
        // fails_when: [EnableRateLimiting(RateLimiterPolicies.Strict)] is removed from AccountManagementController.
        // seam: Controller metadata reflection contract.
        EnableRateLimitingAttribute? attr = typeof(AccountManagementController)
            .GetCustomAttribute<EnableRateLimitingAttribute>();
        attr.Should().NotBeNull();
        attr!.PolicyName.Should().Be(RateLimiterPolicies.Strict);
    }
}
