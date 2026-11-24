// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ============================================================================
// File: SocketRebindPolicyTests.cs
// System: ProjectKongor Netcode Tests [SR-08]
// Subsystem: Zero-Downtime Socket Rebinding Verification
// ============================================================================

using System;
using System.Net;
using System.Net.Sockets;
using ASPIRE.Common.Netcode.Sockets;
using FluentAssertions;
using Xunit;

namespace Kongor.Core.Tests.Netcode;

public sealed class SocketRebindPolicyTests
{
    [Fact]
    public void ConfigureForFastRebind_EnablesReuseAddress_AndDisablesExclusiveAddressUse()
    {
        using Socket socket = new(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);

        socket.ConfigureForFastRebind();

        socket.ExclusiveAddressUse.Should().BeFalse();
        object reuseOption = socket.GetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress)!;
        ((int)reuseOption).Should().NotBe(0);
    }

    [Fact]
    public void ConfigureForFastRebind_OnTcpSocket_DisablesTimeWaitLinger()
    {
        using Socket tcpSocket = new(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);

        tcpSocket.ConfigureForFastRebind();

        LingerOption? linger = tcpSocket.LingerState;
        linger.Should().NotBeNull();
        linger!.Enabled.Should().BeFalse();
        linger.LingerTime.Should().Be(0);
    }

    [Fact]
    public void FastRebind_AllowsImmediateRebindOnSamePort_WithoutAddressAlreadyInUse()
    {
        int boundPort;

        // Bind first socket to ephemeral port
        using (Socket socket1 = new(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp))
        {
            socket1.ConfigureForFastRebind();
            socket1.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            boundPort = ((IPEndPoint)socket1.LocalEndPoint!).Port;
            boundPort.Should().BeGreaterThan(0);
        }

        // Immediately bind second socket to the exact same port with 0ms delay
        using (Socket socket2 = new(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp))
        {
            socket2.ConfigureForFastRebind();
            Action bindAction = () => socket2.Bind(new IPEndPoint(IPAddress.Loopback, boundPort));

            bindAction.Should().NotThrow<SocketException>();
            ((IPEndPoint)socket2.LocalEndPoint!).Port.Should().Be(boundPort);
        }
    }

    [Fact]
    public void CreateFastRebindUdpSocket_ReturnsConfiguredSocketWithBuffers()
    {
        using Socket socket = SocketRebindPolicy.CreateFastRebindUdpSocket(AddressFamily.InterNetwork, bufferSizeBytes: 512 * 1024);

        socket.Should().NotBeNull();
        socket.ReceiveBufferSize.Should().BeGreaterThanOrEqualTo(512 * 1024);
        socket.SendBufferSize.Should().BeGreaterThanOrEqualTo(512 * 1024);
        socket.ExclusiveAddressUse.Should().BeFalse();
    }
}
