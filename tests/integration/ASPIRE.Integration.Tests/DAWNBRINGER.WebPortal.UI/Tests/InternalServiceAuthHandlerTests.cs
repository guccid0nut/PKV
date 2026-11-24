// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Net;
using DAWNBRINGER.WebPortal.UI.Infrastructure.Handlers;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using NEXUS.Contracts.Security;

namespace ASPIRE.Tests.DAWNBRINGER.WebPortal.UI.Tests;

public sealed class InternalServiceAuthHandlerTests
{
    private sealed class CapturingInnerHandler : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            LastRequest = request;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }
    }

    [Fact]
    public async Task SendAsync_PropagatesInternalServiceKeyAndOriginatingClientIpViaXForwardedFor()
    {
        // protects: DAWNBRINGER -> ZORGATH HTTP calls so per-end-user IP rate limiting sees the real browser IP
        // fails_when: InternalServiceAuthHandler omits X-Forwarded-For or drops X-Internal-Service-Key
        // seam: InternalServiceAuthHandler pipeline over HttpMessageInvoker
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["InternalServiceKey"] = "portal-secret-key-2026"
            })
            .Build();

        DefaultHttpContext browserContext = new();
        browserContext.Connection.RemoteIpAddress = IPAddress.Parse("198.51.100.77");

        HttpContextAccessor accessor = new() { HttpContext = browserContext };
        CapturingInnerHandler inner = new();
        using InternalServiceAuthHandler handler = new(configuration, accessor)
        {
            InnerHandler = inner
        };
        using HttpMessageInvoker invoker = new(handler, disposeHandler: false);

        using HttpRequestMessage outbound = new(HttpMethod.Post, "http://web-portal-api/authentication/login");
        using HttpResponseMessage response = await invoker.SendAsync(outbound, CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(inner.LastRequest);
        Assert.True(inner.LastRequest!.Headers.TryGetValues(SecurityConstants.InternalServiceKeyHeader,
            out IEnumerable<string>? keyVals));
        Assert.Equal("portal-secret-key-2026", keyVals!.Single());

        Assert.True(inner.LastRequest.Headers.TryGetValues("X-Forwarded-For", out IEnumerable<string>? xffVals));
        Assert.Equal("198.51.100.77", xffVals!.Single());
    }

    [Fact]
    public async Task SendAsync_PrefersCfConnectingIpWhenPresent()
    {
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["InternalServiceKey"] = "portal-secret-key-2026"
            })
            .Build();

        DefaultHttpContext browserContext = new();
        browserContext.Connection.RemoteIpAddress = IPAddress.Parse("172.18.0.2");
        browserContext.Request.Headers["CF-Connecting-IP"] = "203.0.113.199";

        HttpContextAccessor accessor = new() { HttpContext = browserContext };
        CapturingInnerHandler inner = new();
        using InternalServiceAuthHandler handler = new(configuration, accessor)
        {
            InnerHandler = inner
        };
        using HttpMessageInvoker invoker = new(handler, disposeHandler: false);

        using HttpRequestMessage outbound = new(HttpMethod.Get, "http://web-portal-api/stats/leaderboard");
        using HttpResponseMessage _ = await invoker.SendAsync(outbound, CancellationToken.None);

        Assert.NotNull(inner.LastRequest);
        Assert.True(inner.LastRequest!.Headers.TryGetValues("X-Forwarded-For", out IEnumerable<string>? xffVals));
        Assert.Equal("203.0.113.199", xffVals!.Single());
    }

    [Fact]
    public async Task SendAsync_InStandaloneWithDefaultKey_ThrowsInvalidOperationException()
    {
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["KONGOR_STANDALONE"] = "true",
                ["InternalServiceKey"] = SecurityConstants.DefaultInternalServiceKey
            })
            .Build();

        CapturingInnerHandler inner = new();
        using InternalServiceAuthHandler handler = new(configuration)
        {
            InnerHandler = inner
        };
        using HttpMessageInvoker invoker = new(handler, disposeHandler: false);

        using HttpRequestMessage outbound = new(HttpMethod.Get, "http://web-portal-api/health");
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => invoker.SendAsync(outbound, CancellationToken.None));
    }
}
