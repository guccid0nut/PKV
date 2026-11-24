// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ============================================================================
// File: CompelJitterBufferIntegrationTests.cs
// System: COMPEL Tests - Jitter Buffer & Anomaly Shield Forwarder Integration Tests
// Policy: Strict Explicit Typing (NO 'var')
// ============================================================================

using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using COMPEL.Services.Proxy;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace COMPEL.Tests;

[Collection("SequentialUDPForwarder")]
public sealed class CompelJitterBufferIntegrationTests
{
    private static readonly TimeSpan ReceiveTimeout = TimeSpan.FromMilliseconds(500);

    private static int FreeUdpPort()
    {
        using Socket socket = new(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)socket.LocalEndPoint!).Port;
    }

    private static byte[] GameDatagram(uint challenge, ushort counter, string command)
    {
        byte[] cmdBytes = Encoding.UTF8.GetBytes(command);
        byte[] datagram = new byte[ClientPacketReader.MinimumLength(ProxyForwarderKind.Game) + cmdBytes.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(datagram.AsSpan(ClientPacketReader.ChallengeOffset), challenge);
        BinaryPrimitives.WriteUInt16LittleEndian(datagram.AsSpan(ClientPacketReader.CounterOffset), counter);
        cmdBytes.CopyTo(datagram.AsSpan(ClientPacketReader.MinimumLength(ProxyForwarderKind.Game)));
        return datagram;
    }

    private static async Task<(byte[] Payload, EndPoint Sender)?> TryReceive(Socket socket)
    {
        byte[] buffer = new byte[65535];
        EndPoint remote = new IPEndPoint(IPAddress.Any, 0);

        using CancellationTokenSource timeout = new(ReceiveTimeout);

        try
        {
            SocketReceiveFromResult result = await socket.ReceiveFromAsync(
                buffer,
                SocketFlags.None,
                remote,
                timeout.Token).ConfigureAwait(false);

            byte[] payload = new byte[result.ReceivedBytes];
            Array.Copy(buffer, payload, result.ReceivedBytes);

            return (payload, result.RemoteEndPoint);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
    }

    [Fact]
    public async Task UdpForwarder_WithSubTickAndAnomalyShield_RelaysDatagramsSuccessfully()
    {
        int publicPort = FreeUdpPort();
        int localPort = FreeUdpPort();

        using Socket server = new(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        server.Bind(new IPEndPoint(IPAddress.Loopback, localPort));

        using UDPForwarder forwarder = new(
            publicPort,
            localPort,
            ProxyForwarderKind.Game,
            TimeSpan.FromSeconds(10),
            new ViolationScoreContainer(TimeProvider.System),
            new AttackIndicatorContainer(TimeProvider.System),
            TimeProvider.System,
            NullLogger.Instance,
            enableZstdCompression: false,
            enableBbrPacing: false,
            enableSubTickNetcode: true,
            enableFec: false,
            enableAnomalyShield: true);

        using CancellationTokenSource cts = new();
        Task forwarderTask = forwarder.Run(cts.Token);

        try
        {
            using Socket client = new(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            client.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            IPEndPoint publicEndPoint = new(IPAddress.Loopback, publicPort);

            // Send packet 0 (unauthenticated challenge)
            byte[] packet0 = GameDatagram(SessionChallengeState.UnauthenticatedChallenge, counter: 0, "HelloServer");
            await client.SendToAsync(packet0, SocketFlags.None, publicEndPoint);

            (byte[] Payload, EndPoint Sender)? serverReceived = await TryReceive(server);
            serverReceived.Should().NotBeNull();
            serverReceived!.Value.Payload.Should().Equal(packet0);
        }
        finally
        {
            await cts.CancelAsync();
            try { await forwarderTask; } catch { }
        }
    }

    [Fact]
    public async Task UdpForwarder_WithAnomalyShield_DropsReplayAttackDatagrams()
    {
        int publicPort = FreeUdpPort();
        int localPort = FreeUdpPort();

        using Socket server = new(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        server.Bind(new IPEndPoint(IPAddress.Loopback, localPort));

        using UDPForwarder forwarder = new(
            publicPort,
            localPort,
            ProxyForwarderKind.Game,
            TimeSpan.FromSeconds(10),
            new ViolationScoreContainer(TimeProvider.System),
            new AttackIndicatorContainer(TimeProvider.System),
            TimeProvider.System,
            NullLogger.Instance,
            enableZstdCompression: false,
            enableBbrPacing: false,
            enableSubTickNetcode: false,
            enableFec: false,
            enableAnomalyShield: true);

        using CancellationTokenSource cts = new();
        Task forwarderTask = forwarder.Run(cts.Token);

        try
        {
            using Socket client = new(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            client.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            IPEndPoint publicEndPoint = new(IPAddress.Loopback, publicPort);

            // Send first packet with counter: 0
            byte[] packet0 = GameDatagram(SessionChallengeState.UnauthenticatedChallenge, counter: 0, "LegitPacket");
            await client.SendToAsync(packet0, SocketFlags.None, publicEndPoint);

            (byte[] Payload, EndPoint Sender)? firstReceived = await TryReceive(server);
            firstReceived.Should().NotBeNull();

            // Send duplicate packet with counter: 0 (Replay Attack)
            byte[] duplicatePacket = GameDatagram(SessionChallengeState.UnauthenticatedChallenge, counter: 0, "ReplayPacket");
            await client.SendToAsync(duplicatePacket, SocketFlags.None, publicEndPoint);

            // Server should NOT receive the duplicate packet
            (byte[] Payload, EndPoint Sender)? secondReceived = await TryReceive(server);
            secondReceived.Should().BeNull("duplicate replay attack datagram must be dropped by Anomaly Shield");
        }
        finally
        {
            await cts.CancelAsync();
            try { await forwarderTask; } catch { }
        }
    }
}
