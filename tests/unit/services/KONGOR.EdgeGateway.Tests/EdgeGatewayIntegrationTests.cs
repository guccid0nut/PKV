// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ============================================================================
// File: EdgeGatewayIntegrationTests.cs
// System: KONGOR.EdgeGateway Tests - Wave 3B YARP Routing, Security, Health & 429
// Policy: Strict Explicit Typing (NO 'var')
// ============================================================================

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Xunit;

namespace KONGOR.EdgeGateway.Tests;

public sealed class EdgeGatewayIntegrationTests : IAsyncLifetime
{
    private WebApplication? _mockUpstream;
    private string _upstreamBaseAddress = string.Empty;

    public async Task InitializeAsync()
    {
        WebApplicationBuilder upstreamBuilder = WebApplication.CreateBuilder();
        upstreamBuilder.WebHost.UseUrls("http://127.0.0.1:0");
        _mockUpstream = upstreamBuilder.Build();

        _mockUpstream.MapGet("/client_requester.php", (HttpContext ctx) =>
            Results.Ok(new
            {
                upstream = "master-server",
                path = ctx.Request.Path.Value,
                forwardedFor = ctx.Request.Headers["X-Forwarded-For"].ToString(),
                forwardedProto = ctx.Request.Headers["X-Forwarded-Proto"].ToString(),
                cfConnectingIp = ctx.Request.Headers["CF-Connecting-IP"].ToString(),
                cfVisitor = ctx.Request.Headers["CF-Visitor"].ToString()
            }));

        _mockUpstream.MapGet("/hubs/presence", (HttpContext ctx) =>
            Results.Ok(new
            {
                upstream = "master-server-presence",
                path = ctx.Request.Path.Value
            }));

        _mockUpstream.MapGet("/hubs/presence/{**rest}", (HttpContext ctx, string? rest) =>
            Results.Ok(new
            {
                upstream = "master-server-presence",
                path = ctx.Request.Path.Value,
                rest
            }));

        _mockUpstream.MapGet("/api/stats/player/{accountId:int}/history", (HttpContext ctx, int accountId) =>
            Results.Ok(new
            {
                upstream = "web-api",
                accountId,
                forwardedFor = ctx.Request.Headers["X-Forwarded-For"].ToString(),
                forwardedProto = ctx.Request.Headers["X-Forwarded-Proto"].ToString(),
                cfConnectingIp = ctx.Request.Headers["CF-Connecting-IP"].ToString(),
                cfVisitor = ctx.Request.Headers["CF-Visitor"].ToString()
            }));

        _mockUpstream.MapGet("/portal-home", () =>
            Results.Ok(new { upstream = "web-ui" }));

        await _mockUpstream.StartAsync();

        IServer server = _mockUpstream.Services.GetRequiredService<IServer>();
        IServerAddressesFeature? addresses = server.Features.Get<IServerAddressesFeature>();
        if (addresses is { Addresses.Count: > 0 })
        {
            using IEnumerator<string> enumerator = addresses.Addresses.GetEnumerator();
            if (enumerator.MoveNext())
            {
                _upstreamBaseAddress = enumerator.Current;
            }
        }
        _upstreamBaseAddress.Should().NotBeNullOrWhiteSpace();
    }

    public async Task DisposeAsync()
    {
        if (_mockUpstream is not null)
        {
            await _mockUpstream.StopAsync();
            await _mockUpstream.DisposeAsync();
        }
    }

    private sealed class FixedRemoteIpStartupFilter(IPAddress remoteIp) : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)
        {
            return app =>
            {
                app.Use(async (ctx, nextMiddleware) =>
                {
                    ctx.Connection.RemoteIpAddress = remoteIp;
                    await nextMiddleware();
                });
                next(app);
            };
        }
    }

    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        public ConcurrentQueue<(LogLevel Level, string Category, string Message)> Entries { get; } = new();

        public ILogger CreateLogger(string categoryName) => new CapturingLogger(categoryName, Entries);

        public void Dispose()
        {
        }

        private sealed class CapturingLogger(
            string category,
            ConcurrentQueue<(LogLevel Level, string Category, string Message)> entries) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                entries.Enqueue((logLevel, category, formatter(state, exception)));
            }
        }
    }

    private WebApplicationFactory<global::Program> CreateGatewayFactory(
        int permitLimit = 100,
        string? environmentName = null,
        IPAddress? simulatedRemoteIp = null,
        Dictionary<string, string?>? extraConfig = null,
        bool overrideClustersToMockUpstream = true,
        ILoggerProvider? capturingLoggerProvider = null)
    {
        return new WebApplicationFactory<global::Program>()
            .WithWebHostBuilder(builder =>
            {
                if (!string.IsNullOrWhiteSpace(environmentName))
                {
                    builder.UseEnvironment(environmentName);
                }

                if (simulatedRemoteIp is not null)
                {
                    builder.ConfigureServices(services =>
                    {
                        services.AddSingleton<IStartupFilter>(new FixedRemoteIpStartupFilter(simulatedRemoteIp));
                    });
                }

                if (capturingLoggerProvider is not null)
                {
                    builder.ConfigureLogging(logging =>
                    {
                        logging.AddProvider(capturingLoggerProvider);
                    });
                }

                builder.ConfigureAppConfiguration((_, configBuilder) =>
                {
                    Dictionary<string, string?> overrides = new()
                    {
                        ["EdgeGateway:RateLimit:Enabled"] = "true",
                        ["EdgeGateway:RateLimit:PermitLimit"] = permitLimit.ToString(),
                        ["EdgeGateway:RateLimit:WindowSeconds"] = "60",
                        ["EdgeGateway:RateLimit:SegmentsPerWindow"] = "6"
                    };

                    if (overrideClustersToMockUpstream)
                    {
                        overrides["ReverseProxy:Clusters:master-server:Destinations:primary:Address"] = _upstreamBaseAddress + "/";
                        overrides["ReverseProxy:Clusters:web-api:Destinations:primary:Address"] = _upstreamBaseAddress + "/";
                        overrides["ReverseProxy:Clusters:web-ui:Destinations:primary:Address"] = _upstreamBaseAddress + "/";
                    }

                    if (extraConfig is not null)
                    {
                        foreach (KeyValuePair<string, string?> kvp in extraConfig)
                        {
                            overrides[kvp.Key] = kvp.Value;
                        }
                    }

                    configBuilder.AddInMemoryCollection(overrides);
                });
            });
    }

    [Fact]
    public async Task Healthz_Readyz_And_Topology_ReportHttpOnlyBoundary()
    {
        using WebApplicationFactory<global::Program> factory = CreateGatewayFactory();
        using HttpClient client = factory.CreateClient();

        HttpResponseMessage healthRes = await client.GetAsync("/healthz");
        healthRes.StatusCode.Should().Be(HttpStatusCode.OK);
        string healthBody = await healthRes.Content.ReadAsStringAsync();
        healthBody.Should().Contain("KONGOR.EdgeGateway");
        healthBody.Should().Contain("11031");

        HttpResponseMessage readyRes = await client.GetAsync("/readyz");
        readyRes.StatusCode.Should().Be(HttpStatusCode.OK);
        string readyBody = await readyRes.Content.ReadAsStringAsync();
        readyBody.Should().Contain("\"status\":\"ready\"");
        readyBody.Should().Contain("clusterHealth");

        HttpResponseMessage topoRes = await client.GetAsync("/gateway/topology");
        topoRes.StatusCode.Should().Be(HttpStatusCode.OK);
        string topoBody = await topoRes.Content.ReadAsStringAsync();
        topoBody.Should().Contain("master-server-route");
        topoBody.Should().Contain("presence-hub-route");
        topoBody.Should().Contain("web-api-route");
        topoBody.Should().Contain("web-ui-route");
    }

    [Fact]
    public async Task ReverseProxy_RoutesMasterApiWebApiWebUiAndPresenceHub_WithPrefixStripping()
    {
        using WebApplicationFactory<global::Program> factory = CreateGatewayFactory();
        using HttpClient client = factory.CreateClient();

        // /api/master/client_requester.php -> strips /api/master -> /client_requester.php
        HttpResponseMessage masterRes = await client.GetAsync("/api/master/client_requester.php");
        masterRes.StatusCode.Should().Be(HttpStatusCode.OK);
        string masterBody = await masterRes.Content.ReadAsStringAsync();
        masterBody.Should().Contain("\"upstream\":\"master-server\"");
        masterBody.Should().Contain("/client_requester.php");

        // /hubs/presence and /hubs/presence/negotiate -> master-server preserving path
        HttpResponseMessage presenceRootRes = await client.GetAsync("/hubs/presence");
        presenceRootRes.StatusCode.Should().Be(HttpStatusCode.OK);
        string presenceRootBody = await presenceRootRes.Content.ReadAsStringAsync();
        presenceRootBody.Should().Contain("\"upstream\":\"master-server-presence\"");
        presenceRootBody.Should().Contain("\"/hubs/presence\"");

        HttpResponseMessage presenceNegotiateRes = await client.GetAsync("/hubs/presence/negotiate");
        presenceNegotiateRes.StatusCode.Should().Be(HttpStatusCode.OK);
        string presenceNegotiateBody = await presenceNegotiateRes.Content.ReadAsStringAsync();
        presenceNegotiateBody.Should().Contain("\"upstream\":\"master-server-presence\"");
        presenceNegotiateBody.Should().Contain("/hubs/presence/negotiate");

        // /api/stats/player/42/history -> web-api preserving path
        HttpResponseMessage apiRes = await client.GetAsync("/api/stats/player/42/history");
        apiRes.StatusCode.Should().Be(HttpStatusCode.OK);
        string apiBody = await apiRes.Content.ReadAsStringAsync();
        apiBody.Should().Contain("\"upstream\":\"web-api\"");
        apiBody.Should().Contain("42");

        // /portal-home -> web-ui fallback route
        HttpResponseMessage uiRes = await client.GetAsync("/portal-home");
        uiRes.StatusCode.Should().Be(HttpStatusCode.OK);
        string uiBody = await uiRes.Content.ReadAsStringAsync();
        uiBody.Should().Contain("\"upstream\":\"web-ui\"");
    }

    [Fact]
    public async Task EdgeRateLimiter_Enforces429WithRetryAfterHeader_AndSecurityHeaders()
    {
        using WebApplicationFactory<global::Program> factory = CreateGatewayFactory(permitLimit: 3);
        using HttpClient client = factory.CreateClient();

        for (int i = 0; i < 3; i++)
        {
            HttpResponseMessage okRes = await client.GetAsync("/api/stats/player/7/history");
            okRes.StatusCode.Should().Be(HttpStatusCode.OK);
            okRes.Headers.GetValues("X-Content-Type-Options").Should().Contain("nosniff");
        }

        HttpResponseMessage throttled = await client.GetAsync("/api/stats/player/7/history");
        throttled.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        throttled.Headers.TryGetValues("Retry-After", out IEnumerable<string>? retryValues).Should().BeTrue();
        retryValues.Should().NotBeNullOrEmpty();
        throttled.Headers.GetValues("X-Content-Type-Options").Should().Contain("nosniff");
    }

    [Fact]
    public async Task SpoofedCloudflareAndForwardedHeaders_FromUntrustedPeer_AreStrippedAndOverwritten()
    {
        using CapturingLoggerProvider loggerProvider = new();
        using WebApplicationFactory<global::Program> factory = CreateGatewayFactory(
            simulatedRemoteIp: IPAddress.Parse("203.0.113.50"),
            capturingLoggerProvider: loggerProvider);
        using HttpClient client = factory.CreateClient();

        using HttpRequestMessage req = new(HttpMethod.Get, "/api/master/client_requester.php");
        req.Headers.Add("CF-Connecting-IP", "1.2.3.4");
        req.Headers.Add("CF-Visitor", "{\"scheme\":\"https\"}");
        req.Headers.Add("X-Forwarded-For", "1.2.3.4");
        req.Headers.Add("X-Forwarded-Proto", "https");

        HttpResponseMessage res = await client.SendAsync(req);
        res.StatusCode.Should().Be(HttpStatusCode.OK);
        string body = await res.Content.ReadAsStringAsync();

        // Untrusted peer's spoofed headers must NEVER survive to upstream
        body.Should().Contain("\"cfConnectingIp\":\"\"");
        body.Should().Contain("\"cfVisitor\":\"\"");
        body.Should().Contain("\"forwardedProto\":\"http\"");
        body.Should().Contain("\"forwardedFor\":\"203.0.113.50\"");
        body.Should().NotContain("1.2.3.4");

        loggerProvider.Entries.Should().Contain(e =>
            e.Level == LogLevel.Warning &&
            e.Message.Contains("Stripped spoofed Cloudflare header(s)", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TrustedProxyPeer_PromotesClientIp_PreventsGlobal429_AndStripsRawCfHeadersToUpstream()
    {
        using WebApplicationFactory<global::Program> factory = CreateGatewayFactory(
            permitLimit: 2,
            simulatedRemoteIp: IPAddress.Parse("10.0.0.5"));
        using HttpClient client = factory.CreateClient();

        // Client A exhausts its 2-request window through trusted proxy 10.0.0.5
        for (int i = 0; i < 2; i++)
        {
            using HttpRequestMessage reqA = new(HttpMethod.Get, "/api/stats/player/11/history");
            reqA.Headers.Add("X-Forwarded-For", "198.51.100.10");
            HttpResponseMessage resA = await client.SendAsync(reqA);
            resA.StatusCode.Should().Be(HttpStatusCode.OK);
        }

        using HttpRequestMessage reqAThrottled = new(HttpMethod.Get, "/api/stats/player/11/history");
        reqAThrottled.Headers.Add("X-Forwarded-For", "198.51.100.10");
        HttpResponseMessage resAThrottled = await client.SendAsync(reqAThrottled);
        resAThrottled.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);

        // Distinct Client B behind the same trusted proxy MUST still succeed (no global 429 outage)
        using HttpRequestMessage reqB = new(HttpMethod.Get, "/api/stats/player/12/history");
        reqB.Headers.Add("CF-Connecting-IP", "198.51.100.20");
        reqB.Headers.Add("CF-Visitor", "{\"scheme\":\"https\"}");
        HttpResponseMessage resB = await client.SendAsync(reqB);
        resB.StatusCode.Should().Be(HttpStatusCode.OK);
        string bodyB = await resB.Content.ReadAsStringAsync();

        // Upstream sees canonical X-Forwarded-* promoted by EdgeGateway, while raw CF-* headers are stripped
        bodyB.Should().Contain("\"forwardedFor\":\"198.51.100.20\"");
        bodyB.Should().Contain("\"forwardedProto\":\"https\"");
        bodyB.Should().Contain("\"cfConnectingIp\":\"\"");
        bodyB.Should().Contain("\"cfVisitor\":\"\"");
    }

    [Fact]
    public async Task GatewayTopology_InProduction_RequiresLoopbackAndValidInternalKey()
    {
        Dictionary<string, string?> keyConfig = new()
        {
            ["EdgeGateway:InternalKey"] = "unit-test-topology-secret-99"
        };

        // 1. Non-loopback client in Production is rejected with 403 even with valid key
        using (WebApplicationFactory<global::Program> remoteFactory = CreateGatewayFactory(
                   environmentName: Environments.Production,
                   simulatedRemoteIp: IPAddress.Parse("203.0.113.9"),
                   extraConfig: keyConfig))
        using (HttpClient remoteClient = remoteFactory.CreateClient())
        {
            using HttpRequestMessage req = new(HttpMethod.Get, "/gateway/topology");
            req.Headers.Add(EdgeGatewaySecurity.InternalKeyHeaderName, "unit-test-topology-secret-99");
            HttpResponseMessage res = await remoteClient.SendAsync(req);
            res.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        }

        // 2. Loopback client in Production requires matching X-Kongor-Internal-Key
        using (WebApplicationFactory<global::Program> localFactory = CreateGatewayFactory(
                   environmentName: Environments.Production,
                   simulatedRemoteIp: IPAddress.Loopback,
                   extraConfig: keyConfig))
        using (HttpClient localClient = localFactory.CreateClient())
        {
            HttpResponseMessage missingKeyRes = await localClient.GetAsync("/gateway/topology");
            missingKeyRes.StatusCode.Should().Be(HttpStatusCode.Forbidden);

            using HttpRequestMessage wrongKeyReq = new(HttpMethod.Get, "/gateway/topology");
            wrongKeyReq.Headers.Add(EdgeGatewaySecurity.InternalKeyHeaderName, "wrong-secret");
            HttpResponseMessage wrongKeyRes = await localClient.SendAsync(wrongKeyReq);
            wrongKeyRes.StatusCode.Should().Be(HttpStatusCode.Forbidden);

            using HttpRequestMessage validKeyReq = new(HttpMethod.Get, "/gateway/topology");
            validKeyReq.Headers.Add(EdgeGatewaySecurity.InternalKeyHeaderName, "unit-test-topology-secret-99");
            HttpResponseMessage validKeyRes = await localClient.SendAsync(validKeyReq);
            validKeyRes.StatusCode.Should().Be(HttpStatusCode.OK);
        }
    }

    [Fact]
    public async Task ReverseProxy_ForwarderError_LogsIForwarderErrorFeatureTelemetry()
    {
        // Reserve and immediately release a loopback port so connections reliably fail with connection refused
        int deadPort;
        using (TcpListener tempListener = new(IPAddress.Loopback, 0))
        {
            tempListener.Start();
            deadPort = ((IPEndPoint)tempListener.LocalEndpoint).Port;
            tempListener.Stop();
        }

        using CapturingLoggerProvider loggerProvider = new();
        Dictionary<string, string?> deadUpstreamConfig = new()
        {
            ["ReverseProxy:Clusters:web-api:Destinations:primary:Address"] = $"http://127.0.0.1:{deadPort}/"
        };

        using WebApplicationFactory<global::Program> factory = CreateGatewayFactory(
            extraConfig: deadUpstreamConfig,
            capturingLoggerProvider: loggerProvider);
        using HttpClient client = factory.CreateClient();

        HttpResponseMessage res = await client.GetAsync("/api/stats/player/99/history");
        res.StatusCode.Should().Be(HttpStatusCode.BadGateway);
        res.Headers.GetValues("X-Content-Type-Options").Should().Contain("nosniff");

        loggerProvider.Entries.Should().Contain(e =>
            e.Level == LogLevel.Error &&
            e.Message.Contains("[EdgeGateway] Reverse proxy forwarder error:", StringComparison.Ordinal) &&
            e.Message.Contains("Cluster=web-api", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AspireServiceDiscovery_ResolvesClusterDestinations_WhenExplicitOverrideAbsent()
    {
        Dictionary<string, string?> aspireConfig = new()
        {
            ["services:master-server:http:0"] = _upstreamBaseAddress,
            ["services:web-portal-api:http:0"] = _upstreamBaseAddress,
            ["services:web-portal-ui:http:0"] = _upstreamBaseAddress
        };

        using WebApplicationFactory<global::Program> factory = CreateGatewayFactory(
            extraConfig: aspireConfig,
            overrideClustersToMockUpstream: false);
        using HttpClient client = factory.CreateClient();

        HttpResponseMessage apiRes = await client.GetAsync("/api/stats/player/88/history");
        apiRes.StatusCode.Should().Be(HttpStatusCode.OK);
        string body = await apiRes.Content.ReadAsStringAsync();
        body.Should().Contain("\"upstream\":\"web-api\"");
        body.Should().Contain("88");
    }

    [Fact]
    public void RateLimitPartitioning_NormalizesIpv6ToSlash64_AndBoundsPartitionCount()
    {
        string ipv6HostA = EdgeGatewaySecurity.NormalizeClientIpPartitionKey(IPAddress.Parse("2001:db8:abcd:1234::1"));
        string ipv6HostB = EdgeGatewaySecurity.NormalizeClientIpPartitionKey(IPAddress.Parse("2001:db8:abcd:1234:ffff:ffff:ffff:9999"));
        ipv6HostA.Should().Be("2001:db8:abcd:1234::/64");
        ipv6HostB.Should().Be(ipv6HostA);

        EdgeGatewaySecurity.NormalizeClientIpPartitionKey(IPAddress.Parse("::ffff:198.51.100.7"))
            .Should().Be("198.51.100.7");
        EdgeGatewaySecurity.NormalizeClientIpPartitionKey(IPAddress.IPv6Loopback)
            .Should().Be("::1");

        EdgePartitionKeyTracker tracker = new();
        EdgeGatewayRateLimitOptions options = new()
        {
            MaxTrackedPartitions = 16,
            OverflowShardCount = 8,
            WindowSeconds = 60
        };

        HashSet<string> distinctBuckets = new(StringComparer.Ordinal);
        for (int i = 0; i < 64; i++)
        {
            string bucket = tracker.BoundPartitionKey($"198.51.100.{i}", options, logger: null);
            distinctBuckets.Add(bucket);
        }

        // First 16 unique keys + at most 8 overflow shards
        distinctBuckets.Count.Should().BeLessThanOrEqualTo(24);
        distinctBuckets.Any(k => k.StartsWith("overflow:edge-ip:", StringComparison.Ordinal)).Should().BeTrue();
    }
}
