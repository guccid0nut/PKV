// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Net;
using FluentAssertions;
using TRANSMUTANSTEIN.ChatServer.Services;
using Xunit;

namespace TRANSMUTANSTEIN.ChatServer.Tests.Services;

public class MatchmakingAddressResolutionTests
{
    [Fact]
    public void ResolveClientTargetAddress_WhenClientIpIsNull_ReturnsServerAddress()
    {
        (string address, int port) = MatchmakingService.ResolveClientTargetAddress(
            "203.0.113.77", 11235, "192.168.1.100", 11235, null);

        address.Should().Be("203.0.113.77");
        port.Should().Be(11235);
    }

    [Fact]
    public void ResolveClientTargetAddress_WhenClientIsLoopback_AndServerIsLocalhost_Returns127001()
    {
        (string address, int port) = MatchmakingService.ResolveClientTargetAddress(
            "localhost", 11235, "127.0.0.1", 11235, IPAddress.Loopback);

        address.Should().Be("127.0.0.1");
        port.Should().Be(11235);
    }

    [Fact]
    public void ResolveClientTargetAddress_WhenClientIsLoopback_AndServerHasLocalAddress_ReturnsLocalAddress()
    {
        (string address, int port) = MatchmakingService.ResolveClientTargetAddress(
            "203.0.113.77", 11235, "127.0.0.1", 11235, IPAddress.Loopback);

        address.Should().Be("127.0.0.1");
        port.Should().Be(11235);
    }

    [Fact]
    public void ResolveClientTargetAddress_WhenClientIsOnPrivateSubnet_AndServerHasLocalPrivateAddress_ReturnsLocalPrivateAddress()
    {
        IPAddress clientLanIp = IPAddress.Parse("192.168.1.45");
        (string address, int port) = MatchmakingService.ResolveClientTargetAddress(
            "203.0.113.77", 11235, "192.168.1.10", 11236, clientLanIp);

        address.Should().Be("192.168.1.10");
        port.Should().Be(11236);
    }

    [Fact]
    public void ResolveClientTargetAddress_WhenClientIsOnPrivateSubnet10_AndServerHasPrivate10Address_ReturnsLocalAddress()
    {
        IPAddress clientLanIp = IPAddress.Parse("10.0.0.5");
        (string address, int port) = MatchmakingService.ResolveClientTargetAddress(
            "203.0.113.77", 11235, "10.0.0.1", 11235, clientLanIp);

        address.Should().Be("10.0.0.1");
        port.Should().Be(11235);
    }

    [Fact]
    public void ResolveClientTargetAddress_WhenClientIsPublicWan_ReturnsPublicServerAddress()
    {
        IPAddress clientWanIp = IPAddress.Parse("198.51.100.22");
        (string address, int port) = MatchmakingService.ResolveClientTargetAddress(
            "203.0.113.77", 11235, "192.168.1.10", 11236, clientWanIp);

        address.Should().Be("203.0.113.77");
        port.Should().Be(11235);
    }

    [Theory]
    [InlineData("10.1.2.3", true)]
    [InlineData("172.16.0.1", true)]
    [InlineData("172.31.255.254", true)]
    [InlineData("192.168.0.1", true)]
    [InlineData("100.64.0.1", true)]
    [InlineData("127.0.0.1", true)]
    [InlineData("8.8.8.8", false)]
    [InlineData("203.0.113.77", false)]
    [InlineData("172.32.0.1", false)]
    public void IsPrivateNetwork_DetectsCorrectly(string ipStr, bool expected)
    {
        MatchmakingService.IsPrivateNetwork(ipStr).Should().Be(expected);
    }
}
