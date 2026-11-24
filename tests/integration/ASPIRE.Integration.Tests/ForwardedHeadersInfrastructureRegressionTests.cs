// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Net;
using ASPIRE.Common.ServiceDefaults;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Xunit;
using IPNetwork = System.Net.IPNetwork;

namespace ASPIRE.Tests;

public sealed class ForwardedHeadersInfrastructureRegressionTests
{
    [Fact]
    public void ConfigureInfrastructureForwardedHeaders_DefaultConfig_TrustsRfc1918UlaAndLoopbackWithFourHopLimit()
    {
        IConfiguration config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>())
            .Build();

        ServiceCollection services = new();
        services.ConfigureInfrastructureForwardedHeaders(config);

        ServiceProvider sp = services.BuildServiceProvider();
        ForwardedHeadersOptions options = sp.GetRequiredService<IOptions<ForwardedHeadersOptions>>().Value;

        options.ForwardLimit.Should().Be(4);
        options.ForwardedHeaders.Should().HaveFlag(ForwardedHeaders.XForwardedFor);
        options.ForwardedHeaders.Should().HaveFlag(ForwardedHeaders.XForwardedProto);
        options.ForwardedHeaders.Should().HaveFlag(ForwardedHeaders.XForwardedHost);

        options.KnownIPNetworks.Should().Contain(n => n.BaseAddress.Equals(IPAddress.Parse("10.0.0.0")) && n.PrefixLength == 8);
        options.KnownIPNetworks.Should().Contain(n => n.BaseAddress.Equals(IPAddress.Parse("172.16.0.0")) && n.PrefixLength == 12);
        options.KnownIPNetworks.Should().Contain(n => n.BaseAddress.Equals(IPAddress.Parse("192.168.0.0")) && n.PrefixLength == 16);
        options.KnownIPNetworks.Should().Contain(n => n.BaseAddress.Equals(IPAddress.Parse("169.254.0.0")) && n.PrefixLength == 16);
        options.KnownIPNetworks.Should().Contain(n => n.BaseAddress.Equals(IPAddress.Parse("127.0.0.0")) && n.PrefixLength == 8);
        options.KnownIPNetworks.Should().Contain(n => n.BaseAddress.Equals(IPAddress.IPv6Loopback) && n.PrefixLength == 128);
        options.KnownIPNetworks.Should().Contain(n => n.BaseAddress.Equals(IPAddress.Parse("fc00::")) && n.PrefixLength == 7);
        options.KnownIPNetworks.Should().Contain(n => n.BaseAddress.Equals(IPAddress.Parse("fe80::")) && n.PrefixLength == 10);
    }

    [Fact]
    public void ConfigureInfrastructureForwardedHeaders_CustomForwardLimitAndTrustedProxies_AppliesConfiguration()
    {
        IConfiguration config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                { "ForwardedHeaders:ForwardLimit", "6" },
                { "ReverseProxy:TrustedProxies", "198.51.100.10, ::ffff:203.0.113.20" }
            })
            .Build();

        ServiceCollection services = new();
        services.ConfigureInfrastructureForwardedHeaders(config);

        ServiceProvider sp = services.BuildServiceProvider();
        ForwardedHeadersOptions options = sp.GetRequiredService<IOptions<ForwardedHeadersOptions>>().Value;

        options.ForwardLimit.Should().Be(6);
        options.KnownProxies.Should().Contain(IPAddress.Parse("198.51.100.10"));
        options.KnownProxies.Should().Contain(IPAddress.Parse("203.0.113.20"));
    }

    [Fact]
    public void ConfigureInfrastructureForwardedHeaders_TrustAllProxiesTrue_ClearsRestrictions()
    {
        IConfiguration config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                { "TRUST_ALL_PROXIES", "true" }
            })
            .Build();

        ServiceCollection services = new();
        services.ConfigureInfrastructureForwardedHeaders(config);

        ServiceProvider sp = services.BuildServiceProvider();
        ForwardedHeadersOptions options = sp.GetRequiredService<IOptions<ForwardedHeadersOptions>>().Value;

        options.ForwardLimit.Should().BeNull();
        options.KnownIPNetworks.Should().BeEmpty();
        options.KnownProxies.Should().BeEmpty();
    }

    /// <summary>
    ///     protects: Multi-hop Cloudflare Tunnel -> Docker Dual-Stack Bridge (::ffff:172.21.0.1) scheme, host, and viewer IP resolution.
    ///     fails_when: IPv4-mapped IPv6 peer addresses bypass IPv4 CIDR KnownIPNetworks matching, X-Forwarded-Host is dropped,
    ///                 or CF-Connecting-IP / CF-Visitor headers are ignored behind Docker NAT.
    ///     seam: Real ASP.NET Core TestServer middleware pipeline running ConfigureInfrastructureForwardedHeaders + UseInfrastructureForwardedHeaders.
    /// </summary>
    [Fact]
    public async Task UseInfrastructureForwardedHeaders_DockerMappedIpv6BridgePeer_PromotesCloudflareSchemeHostAndClientIp()
    {
        IConfiguration config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>())
            .Build();

        using IHost host = await new HostBuilder()
            .ConfigureWebHost(webBuilder =>
            {
                webBuilder.UseTestServer();
                webBuilder.ConfigureServices(services =>
                {
                    services.AddSingleton(config);
                    services.AddRouting();
                    services.ConfigureInfrastructureForwardedHeaders(config);
                });
                webBuilder.Configure(app =>
                {
                    app.UseInfrastructureForwardedHeaders(config);
                    app.Run(async ctx =>
                    {
                        await ctx.Response.WriteAsJsonAsync(new
                        {
                            Scheme = ctx.Request.Scheme,
                            Host = ctx.Request.Host.Value,
                            RemoteIp = ctx.Connection.RemoteIpAddress?.ToString()
                        });
                    });
                });
            })
            .StartAsync();

        TestServer server = host.GetTestServer();
        HttpContext resultContext = await server.SendAsync(ctx =>
        {
            // Dual-stack Kestrel socket in Linux container sees Docker gateway as ::ffff:172.21.0.1
            ctx.Connection.RemoteIpAddress = IPAddress.Parse("::ffff:172.21.0.1");
            ctx.Request.Scheme = "http";
            ctx.Request.Host = new HostString("localhost", 5266);
            ctx.Request.Path = "/signin-check";
            ctx.Request.Headers["X-Forwarded-For"] = "198.51.100.77, 172.70.42.12";
            ctx.Request.Headers["X-Forwarded-Host"] = "preview.testing-domain.dev";
            ctx.Request.Headers["CF-Connecting-IP"] = "198.51.100.77";
            ctx.Request.Headers["CF-Visitor"] = "{\"scheme\":\"https\"}";
        });

        resultContext.Request.Scheme.Should().Be("https");
        resultContext.Request.Host.Value.Should().Be("preview.testing-domain.dev");
        resultContext.Connection.RemoteIpAddress.Should().Be(IPAddress.Parse("198.51.100.77"));
    }

    /// <summary>
    ///     protects: Anti-spoofing trust boundary when an untrusted public TCP peer attempts to forge X-Forwarded-* or CF-* headers.
    ///     fails_when: UseInfrastructureForwardedHeaders trusts CF-Connecting-IP or X-Forwarded-Proto from non-RFC1918/non-loopback peers.
    ///     seam: Real ASP.NET Core TestServer middleware pipeline with an untrusted public RemoteIpAddress.
    /// </summary>
    [Fact]
    public async Task UseInfrastructureForwardedHeaders_UntrustedPublicPeer_RejectsSpoofedProxyAndCloudflareHeaders()
    {
        IConfiguration config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>())
            .Build();

        using IHost host = await new HostBuilder()
            .ConfigureWebHost(webBuilder =>
            {
                webBuilder.UseTestServer();
                webBuilder.ConfigureServices(services =>
                {
                    services.AddSingleton(config);
                    services.AddRouting();
                    services.ConfigureInfrastructureForwardedHeaders(config);
                });
                webBuilder.Configure(app =>
                {
                    app.UseInfrastructureForwardedHeaders(config);
                    app.Run(_ => Task.CompletedTask);
                });
            })
            .StartAsync();

        TestServer server = host.GetTestServer();
        HttpContext resultContext = await server.SendAsync(ctx =>
        {
            ctx.Connection.RemoteIpAddress = IPAddress.Parse("203.0.113.9");
            ctx.Request.Scheme = "http";
            ctx.Request.Host = new HostString("localhost", 5266);
            ctx.Request.Path = "/signin-check";
            ctx.Request.Headers["X-Forwarded-Proto"] = "https";
            ctx.Request.Headers["X-Forwarded-Host"] = "evil.attacker.invalid";
            ctx.Request.Headers["X-Forwarded-For"] = "1.1.1.1";
            ctx.Request.Headers["CF-Connecting-IP"] = "1.1.1.1";
            ctx.Request.Headers["CF-Visitor"] = "{\"scheme\":\"https\"}";
        });

        resultContext.Request.Scheme.Should().Be("http");
        resultContext.Request.Host.Value.Should().Be("localhost:5266");
        resultContext.Connection.RemoteIpAddress.Should().Be(IPAddress.Parse("203.0.113.9"));
    }
}
