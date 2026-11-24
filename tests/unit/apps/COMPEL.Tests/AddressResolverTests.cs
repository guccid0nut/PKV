// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2022-2026 [K]ONGOR and Contributors
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV) -- Upstream lineage: https://github.com/Project-KONGOR-Open-Source

namespace COMPEL.Tests;

/// <summary>
///     Verifies the gateway-to-address logic and the master server endpoint derivation, for the branches that do not
///     require network access.
/// </summary>
public sealed class AddressResolverTests
{
    private static AddressResolver Resolver(string gateway, string masterServer = "masterserver.newbs.io")
    {
        return new AddressResolver(
            Options.Create(new MatchServerManagerOptions { Gateway = gateway, MasterServerAddress = masterServer }),
            NullLogger<AddressResolver>.Instance);
    }

    [Fact]
    public async Task A_Localhost_Gateway_Resolves_To_The_Loopback_Address()
    {
        string address = await Resolver("localhost").ResolveServerAddress();

        Assert.Equal("127.0.0.1", address);
    }

    [Fact]
    public async Task An_IPv4_Literal_Gateway_Is_Used_Directly()
    {
        string address = await Resolver("203.0.113.5").ResolveServerAddress();

        Assert.Equal("203.0.113.5", address);
    }

    [Fact]
    public async Task An_IPv6_Literal_Gateway_Is_Rejected()
    {
        bool threw = false;

        try
        {
            await Resolver("2001:db8::1").ResolveServerAddress();
        }
        catch (InvalidOperationException)
        {
            threw = true;
        }

        Assert.True(threw);
    }

    [Fact]
    public void The_Master_Server_Endpoint_Uses_Configured_Address_On_Localhost()
    {
        Assert.Equal("127.0.0.1:55555", Resolver("localhost").MasterServerHostAndPort);
        Assert.Equal("masterserver.newbs.io",
            Resolver("localhost", "https://masterserver.newbs.io/").MasterServerHostAndPort);
        Assert.Equal("127.0.0.1:55555", Resolver("localhost", "127.0.0.1:55555").MasterServerHostAndPort);
    }

    [Fact]
    public void The_Master_Server_Endpoint_Uses_The_Public_Gateway_Without_A_Port_Otherwise()
    {
        Assert.Equal("masterserver.newbs.io", Resolver("masterserver.newbs.io").MasterServerHostAndPort);
    }

    [Theory]
    [InlineData("127.0.0.1:55555", false, "127.0.0.1:55555")]
    [InlineData("127.0.0.1:55555", true, "host.docker.internal:55555")]
    [InlineData("localhost:55555", true, "host.docker.internal:55555")]
    [InlineData("127.0.0.1", true, "host.docker.internal")]
    [InlineData("masterserver.newbs.io:55555", true, "masterserver.newbs.io:55555")]
    public void NormalizeHostForContainer_NormalizesLoopbackCorrectly(string input, bool isContainer, string expected)
    {
        string actual = AddressResolver.NormalizeHostForContainer(input, isContainer);
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void MasterServerEndpoint_InContainerMode_ResolvesToHostDockerInternal()
    {
        string? original = Environment.GetEnvironmentVariable("COMPEL_RUNNING_IN_CONTAINER");
        try
        {
            Environment.SetEnvironmentVariable("COMPEL_RUNNING_IN_CONTAINER", "true");
            AddressResolver resolver = new(
                Options.Create(new MatchServerManagerOptions { Gateway = "localhost", MasterServerAddress = string.Empty }),
                NullLogger<AddressResolver>.Instance);

            Assert.Equal("host.docker.internal:55555", resolver.MasterServerHostAndPort);
        }
        finally
        {
            Environment.SetEnvironmentVariable("COMPEL_RUNNING_IN_CONTAINER", original);
        }
    }

    [Fact]
    public void MasterServerEndpoint_InStandaloneMode_ResolvesToLoopbackEvenWithLanGateway()
    {
        string? original = Environment.GetEnvironmentVariable("KONGOR_STANDALONE");
        try
        {
            Environment.SetEnvironmentVariable("KONGOR_STANDALONE", "true");
            AddressResolver resolver = Resolver("192.168.1.50");
            Assert.Equal("127.0.0.1:55555", resolver.MasterServerHostAndPort);
        }
        finally
        {
            Environment.SetEnvironmentVariable("KONGOR_STANDALONE", original);
        }
    }

    [Fact]
    public void IsEndpointReachable_EmptyOrInvalidAddress_ReturnsFalseFast()
    {
        Assert.False(AddressResolver.IsEndpointReachable("", timeoutMs: 50));
        Assert.False(AddressResolver.CheckMasterServerReachability(null!, timeoutMs: 50));
    }

    [Fact]
    public async Task IsEndpointReachableAsync_EmptyOrInvalidAddress_ReturnsFalseFast()
    {
        Assert.False(await AddressResolver.IsEndpointReachableAsync("", timeoutMs: 50));
        Assert.False(await AddressResolver.IsEndpointReachableAsync("   ", timeoutMs: 50));
        Assert.False(await AddressResolver.CheckMasterServerReachabilityAsync(null!, timeoutMs: 50));
    }

    [Fact]
    public async Task Reachability_CacheCoherence_BetweenSyncAndAsync()
    {
        // Both sync and async check should return false for invalid / empty endpoints without throwing
        bool syncResult = AddressResolver.CheckMasterServerReachability("127.0.0.1:0", timeoutMs: 50);
        bool asyncResult = await AddressResolver.CheckMasterServerReachabilityAsync("127.0.0.1:0", timeoutMs: 50);

        Assert.False(syncResult);
        Assert.False(asyncResult);
    }

    [Theory]
    [InlineData("AUTO")]
    [InlineData("LAN")]
    public async Task Auto_And_Lan_Gateways_Resolve_To_Valid_IPv4_And_Local_MasterServer(string gatewayToken)
    {
        AddressResolver resolver = Resolver(gatewayToken);
        string resolvedIp = await resolver.ResolveServerAddress();

        bool parsedOk = IPAddress.TryParse(resolvedIp, out IPAddress? ip);
        Assert.True(parsedOk);
        Assert.NotNull(ip);
        Assert.Equal(AddressFamily.InterNetwork, ip.AddressFamily);
        Assert.Equal("127.0.0.1:55555", resolver.MasterServerHostAndPort);
    }
}
