// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Net;
using System.Reflection;
using System.Threading.RateLimiting;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Moq;
using NEXUS.Contracts.Security;
using Xunit;
using ZORGATH.WebPortal.API.Constants;

namespace ZORGATH.WebPortal.API.Tests;

public sealed class RateLimitingPolicyTests
{
    private static DefaultHttpContext CreateContextWithServices(
        string? configuredKey = "test-internal-key-secret-2026",
        string environmentName = "Production",
        bool standalone = false)
    {
        Dictionary<string, string?> settings = new()
        {
            ["KONGOR_STANDALONE"] = standalone ? "true" : "false"
        };
        if (configuredKey is not null)
        {
            settings["InternalServiceKey"] = configuredKey;
        }

        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(settings)
            .Build();

        Mock<IHostEnvironment> mockEnv = new();
        mockEnv.Setup(e => e.EnvironmentName).Returns(environmentName);

        ServiceCollection services = new();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddSingleton<IHostEnvironment>(mockEnv.Object);

        DefaultHttpContext context = new()
        {
            RequestServices = services.BuildServiceProvider()
        };
        return context;
    }

    [Fact]
    public void AllPublicApiControllers_MustHaveEnableRateLimitingAttribute()
    {
        Assembly apiAssembly = typeof(ZORGATH).Assembly;
        Type[] controllerTypes = apiAssembly.GetTypes()
            .Where(t => typeof(ControllerBase).IsAssignableFrom(t) && !t.IsAbstract && t.Name.EndsWith("Controller"))
            .ToArray();

        controllerTypes.Should().NotBeEmpty();

        foreach (Type controller in controllerTypes)
        {
            EnableRateLimitingAttribute? attribute = controller.GetCustomAttribute<EnableRateLimitingAttribute>();
            attribute.Should().NotBeNull(
                $"Controller '{controller.Name}' must be decorated with [EnableRateLimiting] to prevent unthrottled endpoint abuse.");
        }
    }

    [Fact]
    public void RelaxedPolicy_WithValidInternalServiceKey_WithoutForwardedIp_BypassesRateLimiter()
    {
        // protects: Genuine background inter-service calls (no end-user IP) from rate-limit throttling
        // fails_when: Valid constant-time InternalServiceKey without X-Forwarded-For fails to match
        // seam: RateLimiterPolicies.CreateRelaxedPartition
        DefaultHttpContext context = CreateContextWithServices("test-internal-key-secret-2026");
        context.Request.Headers[SecurityConstants.InternalServiceKeyHeader] = "test-internal-key-secret-2026";

        RateLimitPartition<string> partition = RateLimiterPolicies.CreateRelaxedPartition(context);

        partition.PartitionKey.Should().Be("internal_service");
        RateLimiter limiter = partition.Factory(partition.PartitionKey);
        for (int i = 0; i < 250; i++)
        {
            limiter.AttemptAcquire(1).IsAcquired.Should().BeTrue();
        }
    }

    [Fact]
    public void RelaxedPolicy_WithValidInternalServiceKey_AndForwardedIp_PartitionsPerEndUserIp()
    {
        // protects: ZORGATH endpoints proxied through DAWNBRINGER so each end-user browser IP gets its own 100-token bucket
        // fails_when: Proxied browser traffic receives an unlimited "internal_service" bypass or shares DAWNBRINGER's single IP
        // seam: RateLimiterPolicies.CreateRelaxedPartition
        DefaultHttpContext context = CreateContextWithServices("test-internal-key-secret-2026");
        context.Connection.RemoteIpAddress = IPAddress.Parse("10.0.0.5"); // DAWNBRINGER container IP
        context.Request.Headers[SecurityConstants.InternalServiceKeyHeader] = "test-internal-key-secret-2026";
        context.Request.Headers["X-Forwarded-For"] = "198.51.100.42, 10.0.0.5";

        RateLimitPartition<string> partition = RateLimiterPolicies.CreateRelaxedPartition(context);

        partition.PartitionKey.Should().Be("fwd:198.51.100.42");
        RateLimiter limiter = partition.Factory(partition.PartitionKey);
        for (int i = 0; i < 100; i++)
        {
            limiter.AttemptAcquire(1).IsAcquired.Should().BeTrue();
        }

        limiter.AttemptAcquire(1).IsAcquired.Should().BeFalse();
    }

    [Fact]
    public void RelaxedPolicy_WithForgedInternalKeyOrWorkerToken_DoesNotBypassRateLimiter()
    {
        // protects: ZORGATH against trivial rate-limit bypass via forged X-Internal-Service-Key or X-Worker-Token headers
        // fails_when: Any non-empty X-Internal-Service-Key or X-Worker-Token grants GetNoLimiter("internal_service")
        // seam: RateLimiterPolicies.CreateRelaxedPartition
        DefaultHttpContext forgedKeyContext = CreateContextWithServices("real-production-secret-key");
        forgedKeyContext.Connection.RemoteIpAddress = IPAddress.Parse("203.0.113.88");
        forgedKeyContext.Request.Headers[SecurityConstants.InternalServiceKeyHeader] = "attacker-forged-key";
        forgedKeyContext.Request.Headers["X-Forwarded-For"] = "1.2.3.4"; // Spoofed XFF must ALSO be ignored!

        RateLimitPartition<string> forgedPartition = RateLimiterPolicies.CreateRelaxedPartition(forgedKeyContext);
        forgedPartition.PartitionKey.Should().Be("203.0.113.88");
        RateLimiter forgedLimiter = forgedPartition.Factory(forgedPartition.PartitionKey);
        for (int i = 0; i < 100; i++)
        {
            forgedLimiter.AttemptAcquire(1).IsAcquired.Should().BeTrue();
        }
        forgedLimiter.AttemptAcquire(1).IsAcquired.Should().BeFalse();

        DefaultHttpContext workerTokenContext = CreateContextWithServices("real-production-secret-key");
        workerTokenContext.Connection.RemoteIpAddress = IPAddress.Parse("203.0.113.89");
        workerTokenContext.Request.Headers["X-Worker-Token"] = "any-unvalidated-worker-jwt";

        RateLimitPartition<string> workerPartition = RateLimiterPolicies.CreateRelaxedPartition(workerTokenContext);
        workerPartition.PartitionKey.Should().Be("203.0.113.89");
    }

    [Fact]
    public void StrictPolicy_BehindAuthenticatedDawnbringer_IsolatesDistinctBrowserUsers_AndIgnoresUntrustedXff()
    {
        // protects: /authentication/login and /authentication/register from cross-player 429 lockouts behind DAWNBRINGER
        //           while preventing untrusted callers from rotating spoofed X-Forwarded-For headers
        // fails_when: Strict policy partitions all DAWNBRINGER users under DAWNBRINGER's IP or trusts unauthenticated XFF
        // seam: RateLimiterPolicies.CreateStrictPartition
        DefaultHttpContext trustedUserA = CreateContextWithServices("portal-shared-secret");
        trustedUserA.Connection.RemoteIpAddress = IPAddress.Parse("10.0.0.5");
        trustedUserA.Request.Headers[SecurityConstants.InternalServiceKeyHeader] = "portal-shared-secret";
        trustedUserA.Request.Headers["X-Forwarded-For"] = "198.51.100.10";

        DefaultHttpContext trustedUserB = CreateContextWithServices("portal-shared-secret");
        trustedUserB.Connection.RemoteIpAddress = IPAddress.Parse("10.0.0.5");
        trustedUserB.Request.Headers[SecurityConstants.InternalServiceKeyHeader] = "portal-shared-secret";
        trustedUserB.Request.Headers["X-Forwarded-For"] = "198.51.100.11";

        RateLimitPartition<string> partitionA = RateLimiterPolicies.CreateStrictPartition(trustedUserA);
        RateLimitPartition<string> partitionB = RateLimiterPolicies.CreateStrictPartition(trustedUserB);

        partitionA.PartitionKey.Should().Be("fwd:198.51.100.10");
        partitionB.PartitionKey.Should().Be("fwd:198.51.100.11");

        // Untrusted caller spoofing X-Forwarded-For must stay pinned to Connection.RemoteIpAddress
        DefaultHttpContext untrustedSpoofer = CreateContextWithServices("portal-shared-secret");
        untrustedSpoofer.Connection.RemoteIpAddress = IPAddress.Parse("203.0.113.200");
        untrustedSpoofer.Request.Headers["X-Forwarded-For"] = "198.51.100.99";

        RateLimitPartition<string> untrustedPartition = RateLimiterPolicies.CreateStrictPartition(untrustedSpoofer);
        untrustedPartition.PartitionKey.Should().Be("203.0.113.200");
    }

    [Fact]
    public void DefaultDevelopmentKey_IsRejectedInProductionAndStandaloneModes()
    {
        // protects: Production and standalone deployments against hardcoded DefaultInternalServiceKey rate-limit bypass
        // fails_when: DefaultInternalServiceKey is accepted outside local Development mode
        // seam: RateLimiterPolicies.IsValidInternalServiceKey
        DefaultHttpContext prodUnconfigured = CreateContextWithServices(configuredKey: null, environmentName: "Production");
        prodUnconfigured.Request.Headers[SecurityConstants.InternalServiceKeyHeader] =
            SecurityConstants.DefaultInternalServiceKey;
        RateLimiterPolicies.IsValidInternalServiceKey(prodUnconfigured).Should().BeFalse();

        DefaultHttpContext devStandalone = CreateContextWithServices(
            configuredKey: null,
            environmentName: "Development",
            standalone: true);
        devStandalone.Request.Headers[SecurityConstants.InternalServiceKeyHeader] =
            SecurityConstants.DefaultInternalServiceKey;
        RateLimiterPolicies.IsValidInternalServiceKey(devStandalone).Should().BeFalse();

        DefaultHttpContext localDev = CreateContextWithServices(
            configuredKey: null,
            environmentName: "Development",
            standalone: false);
        localDev.Request.Headers[SecurityConstants.InternalServiceKeyHeader] =
            SecurityConstants.DefaultInternalServiceKey;
        RateLimiterPolicies.IsValidInternalServiceKey(localDev).Should().BeTrue();
    }

    [Fact]
    public void RelaxedPolicy_ForPublicClients_UsesIpPartitionedTokenBucket()
    {
        DefaultHttpContext context = new();
        context.Connection.RemoteIpAddress = IPAddress.Parse("192.168.1.50");

        RateLimitPartition<string> partition = RateLimiterPolicies.CreateRelaxedPartition(context);

        partition.PartitionKey.Should().Be("192.168.1.50");
        RateLimiter limiter = partition.Factory(partition.PartitionKey);
        limiter.Should().NotBeNull();

        for (int i = 0; i < 100; i++)
        {
            limiter.AttemptAcquire(1).IsAcquired.Should().BeTrue();
        }

        limiter.AttemptAcquire(1).IsAcquired.Should().BeFalse();
    }
}
