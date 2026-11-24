// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Net;
using FluentAssertions;
using KONGOR.MasterServer.Services.Network;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace KONGOR.MasterServer.Tests.Network;

public sealed class ChatRoutingSplitHorizonTests
{
    private static NetworkTopologyResolverService CreateResolver(
        Dictionary<string, string>? envVars = null,
        HttpClient? httpClient = null)
    {
        envVars ??= new Dictionary<string, string>();
        return new NetworkTopologyResolverService(
            NullLogger<NetworkTopologyResolverService>.Instance,
            httpClient,
            key => envVars.TryGetValue(key, out string? val) ? val : null);
    }

    [Fact]
    public void ResolveChatServer_Case1_LoopbackClient_ResolvesToLocalHostAndPort()
    {
        // Case 1: client 127.0.0.1 -> host 127.0.0.1, port 11031
        NetworkTopologyResolverService resolver = CreateResolver();
        IPAddress clientIp = IPAddress.Parse("127.0.0.1");

        (string host, int port) = resolver.ResolveChatServer(clientIp, hostHeader: null, "testuser", "Staff");

        host.Should().Be("127.0.0.1");
        port.Should().Be(11031);
    }

    [Fact]
    public void ResolveChatServer_Case2_LanClient_WithoutLanEnv_ResolvesToHostHeaderHostAndLocalPort()
    {
        // Case 2: client 192.168.1.50, hostHeader "192.168.1.10:55555", CHAT_SERVER_LAN_HOST unset -> host 192.168.1.10, port 11031
        NetworkTopologyResolverService resolver = CreateResolver();
        IPAddress clientIp = IPAddress.Parse("192.168.1.50");

        (string host, int port) = resolver.ResolveChatServer(clientIp, "192.168.1.10:55555", "testuser", "Staff");

        host.Should().Be("192.168.1.10");
        port.Should().Be(11031);
    }

    [Fact]
    public void ResolveChatServer_Case3_TailscaleClient_WithoutLanEnv_ResolvesToHostHeaderHostAndLocalPort()
    {
        // Case 3: client 100.101.102.103 (Tailscale), hostHeader "100.64.0.5:55555", CHAT_SERVER_LAN_HOST unset -> host 100.64.0.5, port 11031
        NetworkTopologyResolverService resolver = CreateResolver();
        IPAddress clientIp = IPAddress.Parse("100.101.102.103");

        (string host, int port) = resolver.ResolveChatServer(clientIp, "100.64.0.5:55555", "testuser", "Staff");

        host.Should().Be("100.64.0.5");
        port.Should().Be(11031);
    }

    [Fact]
    public void ResolveChatServer_Case4_LanClient_WithLanEnvConfigured_PrioritizesEnvHostAndLocalPort()
    {
        // Case 4: client 192.168.1.50, hostHeader "192.168.1.10:55555", CHAT_SERVER_LAN_HOST = "10.0.0.7" -> host 10.0.0.7, port 11031
        Dictionary<string, string> env = new()
        {
            ["CHAT_SERVER_LAN_HOST"] = "10.0.0.7"
        };
        NetworkTopologyResolverService resolver = CreateResolver(env);
        IPAddress clientIp = IPAddress.Parse("192.168.1.50");

        (string host, int port) = resolver.ResolveChatServer(clientIp, "192.168.1.10:55555", "testuser", "Staff");

        host.Should().Be("10.0.0.7");
        port.Should().Be(11031);
    }

    [Fact]
    public void ResolveChatServer_Case5_WanClient_ResolvesToPublicChatServerHostAndPort()
    {
        // Case 5: client 8.8.8.8, CHAT_SERVER_PUBLIC_HOST = "203.0.113.9" -> host 203.0.113.9, port 11031
        Dictionary<string, string> env = new()
        {
            ["CHAT_SERVER_PUBLIC_HOST"] = "203.0.113.9"
        };
        NetworkTopologyResolverService resolver = CreateResolver(env);
        IPAddress clientIp = IPAddress.Parse("8.8.8.8");

        (string host, int port) = resolver.ResolveChatServer(clientIp, hostHeader: null, "testuser", "Staff");

        host.Should().Be("203.0.113.9");
        port.Should().Be(11031);
    }

    [Fact]
    public async Task GetPublicIPAsync_And_StartAsync_WithStandaloneTrue_MakesZeroHttpCalls()
    {
        // Case 6: GetPublicIPAsync() / StartAsync() with KONGOR_STANDALONE=true and KONGOR_DISCOVER_PUBLIC_IP unset
        // makes 0 HTTP calls on an injected HttpMessageHandler stub (throws if called!).
        ThrowingHttpMessageHandler handler = new();
        using HttpClient httpClient = new(handler);
        Dictionary<string, string> env = new()
        {
            ["KONGOR_STANDALONE"] = "true"
        };

        NetworkTopologyResolverService.ResetCachedPublicIPForTesting();
        NetworkTopologyResolverService resolver = CreateResolver(env, httpClient);

        // Act 1: GetPublicIPAsync should not throw and make 0 outbound HTTP calls
        string? publicIp = await resolver.GetPublicIPAsync();
        publicIp.Should().NotBeNullOrWhiteSpace();
        handler.RequestCount.Should().Be(0);

        // Act 2: StartAsync should not invoke HTTP polling and complete immediately
        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(5));
        await resolver.StartAsync(cts.Token);
        handler.RequestCount.Should().Be(0);

        await resolver.StopAsync(CancellationToken.None);
        handler.RequestCount.Should().Be(0);
    }

    private sealed class ThrowingHttpMessageHandler : HttpMessageHandler
    {
        public int RequestCount;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref RequestCount);
            throw new InvalidOperationException($"Outbound HTTP call unexpectedly initiated to: {request.RequestUri}");
        }
    }
}
