// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ============================================================================
// File: CompelBbrClosedLoopTests.cs
// System: COMPEL Tests - Closed-Loop BBR Congestion Control & Inflight Feedback Integration Tests
// Policy: Strict Explicit Typing (NO 'var')
// ============================================================================

using System.Net;
using System.Net.Sockets;
using System.Text;
using ASPIRE.Common.Netcode;
using COMPEL.Services.Proxy;
using COMPEL.Services.Proxy.Congestion;
using COMPEL.Services.Security;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace COMPEL.Tests;

[Collection("SequentialUDPForwarder")]
public sealed class CompelBbrClosedLoopTests
{
    private static int FreeUdpPort()
    {
        using Socket socket = new(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)socket.LocalEndPoint!).Port;
    }

    private static byte[] GenerateTestKey(string seed)
    {
        return DatagramAuthenticator.DeriveSessionKey(Encoding.UTF8.GetBytes(seed));
    }

    [Fact]
    public async Task Compel_WithBbrPacingEnabled_InitializesSessionLevelBbrAndTracker()
    {
        int publicPort = FreeUdpPort();
        int localPort = FreeUdpPort();
        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(5));

        using Socket fakeServer = new(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        fakeServer.Bind(new IPEndPoint(IPAddress.Loopback, localPort));

        byte[] sessionKey = GenerateTestKey("BbrInitSession");
        ControllableTimeProvider timeProvider = new();
        ViolationScoreContainer scoreContainer = new(timeProvider);
        AttackIndicatorContainer attackIndicator = new(timeProvider);

        using UDPForwarder forwarder = new(
            publicPort: publicPort,
            localPort: localPort,
            kind: ProxyForwarderKind.Game,
            challengeRenewalInterval: TimeSpan.FromSeconds(60),
            scoreContainer: scoreContainer,
            attackIndicator: attackIndicator,
            timeProvider: timeProvider,
            logger: NullLogger.Instance,
            enableZstdCompression: false,
            enableBbrPacing: true,
            enableSubTickNetcode: false,
            enableFec: false,
            enableAnomalyShield: false,
            sessionKeyResolver: _ => sessionKey);

        Task forwarderTask = forwarder.Run(cts.Token);

        try
        {
            using Socket clientSocket = new(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            clientSocket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            IPEndPoint clientEndPoint = (IPEndPoint)clientSocket.LocalEndPoint!;
            IPEndPoint forwarderEndPoint = new(IPAddress.Loopback, publicPort);

            DatagramAuthenticator clientAuth = new(sessionKey, sessionId: 0x12345678);

            // Send an initial modern packet to instantiate session
            KongorPacketHeader clientHeader = new(
                sessionId: 0x12345678,
                packetSequence: 1,
                subTickDeltaUs: 10_000,
                ackSequence: 0,
                ackTimestampEchoUs: 0);

            byte[] clientPlaintext = [0x01, 0x02, 0x03, 0x04];
            byte[] clientPacket = new byte[clientPlaintext.Length + KongorPacketEnvelope.TotalOverhead];
            clientAuth.TryEncryptAndWrap(
                clientPacket.AsSpan(),
                in clientHeader,
                clientPlaintext.AsSpan(),
                DatagramDirection.ClientToServer,
                out int clientBytesWritten);

            await clientSocket.SendToAsync(clientPacket.AsMemory(0, clientBytesWritten), SocketFlags.None, forwarderEndPoint, cts.Token);

            // Receive on fake server
            byte[] serverRecv = new byte[1024];
            int serverBytes = await fakeServer.ReceiveAsync(serverRecv.AsMemory(), SocketFlags.None, cts.Token);
            serverBytes.Should().Be(4);

            // Verify that the session created has isolated BBR, tracker, and pacer
            BbrCongestionController? sessionBbr = forwarder.GetSessionBbr(clientEndPoint);
            InflightPacketTracker? sessionTracker = forwarder.GetSessionInflightTracker(clientEndPoint);
            UdpTokenBucketPacer? sessionPacer = forwarder.GetSessionPacer(clientEndPoint);

            sessionBbr.Should().NotBeNull();
            sessionTracker.Should().NotBeNull();
            sessionPacer.Should().NotBeNull();
            sessionBbr!.Phase.Should().Be(BbrPhase.Startup);
        }
        finally
        {
            cts.Cancel();
            try { await forwarderTask; } catch { }
        }
    }

    [Fact]
    public async Task Compel_ProcessesClosedLoopAck_AndDecrementsInflightBytes()
    {
        int publicPort = FreeUdpPort();
        int localPort = FreeUdpPort();
        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(5));

        using Socket fakeServer = new(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        fakeServer.Bind(new IPEndPoint(IPAddress.Loopback, localPort));

        byte[] sessionKey = GenerateTestKey("ClosedLoopAckSession");
        ControllableTimeProvider timeProvider = new();
        ViolationScoreContainer scoreContainer = new(timeProvider);
        AttackIndicatorContainer attackIndicator = new(timeProvider);

        using UDPForwarder forwarder = new(
            publicPort: publicPort,
            localPort: localPort,
            kind: ProxyForwarderKind.Game,
            challengeRenewalInterval: TimeSpan.FromSeconds(60),
            scoreContainer: scoreContainer,
            attackIndicator: attackIndicator,
            timeProvider: timeProvider,
            logger: NullLogger.Instance,
            enableZstdCompression: false,
            enableBbrPacing: true,
            enableSubTickNetcode: false,
            enableFec: false,
            enableAnomalyShield: false,
            sessionKeyResolver: _ => sessionKey);

        Task forwarderTask = forwarder.Run(cts.Token);

        try
        {
            using Socket clientSocket = new(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            clientSocket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            IPEndPoint clientEndPoint = (IPEndPoint)clientSocket.LocalEndPoint!;
            IPEndPoint forwarderEndPoint = new(IPAddress.Loopback, publicPort);

            DatagramAuthenticator clientAuth = new(sessionKey, sessionId: 0x55667788);

            // 1. Client connects with first modern packet
            KongorPacketHeader initHeader = new(
                sessionId: 0x55667788,
                packetSequence: 1,
                subTickDeltaUs: 5000);

            byte[] initPacket = new byte[4 + KongorPacketEnvelope.TotalOverhead];
            clientAuth.TryEncryptAndWrap(
                initPacket.AsSpan(),
                in initHeader,
                [0xAA, 0xBB, 0xCC, 0xDD],
                DatagramDirection.ClientToServer,
                out int initLen);

            await clientSocket.SendToAsync(initPacket.AsMemory(0, initLen), SocketFlags.None, forwarderEndPoint, cts.Token);

            // Server receives it
            byte[] srvBuf = new byte[256];
            SocketReceiveFromResult serverRecvResult = await fakeServer.ReceiveFromAsync(srvBuf.AsMemory(), SocketFlags.None, clientEndPoint, cts.Token);
            IPEndPoint upstreamProxyClient = (IPEndPoint)serverRecvResult.RemoteEndPoint;

            // 2. Server sends a game snapshot back to client through the proxy
            byte[] serverSnapshot = Encoding.ASCII.GetBytes("GameSnapshotTick100");
            await fakeServer.SendToAsync(serverSnapshot.AsMemory(), SocketFlags.None, upstreamProxyClient, cts.Token);

            // 3. Client receives encapsulated modern packet from forwarder
            byte[] clientRecvBuf = new byte[1024];
            int clientRecvBytes = await clientSocket.ReceiveAsync(clientRecvBuf.AsMemory(), SocketFlags.None, cts.Token);

            bool unwrapSuccess = clientAuth.TryUnwrapAndDecrypt(
                clientRecvBuf.AsSpan(0, clientRecvBytes),
                DatagramDirection.ServerToClient,
                srvBuf.AsSpan(),
                out KongorPacketHeader receivedServerHeader,
                out int decryptedServerLen,
                enforceAntiReplay: false);

            unwrapSuccess.Should().BeTrue();
            receivedServerHeader.PacketSequence.Should().Be(1);

            InflightPacketTracker? tracker = forwarder.GetSessionInflightTracker(clientEndPoint);
            tracker.Should().NotBeNull();
            tracker!.InflightBytes.Should().BeGreaterThan(0);

            // 4. Client sends ACK packet back echoing server sequence and timestamp
            KongorPacketHeader ackHeader = new(
                sessionId: 0x55667788,
                packetSequence: 2,
                subTickDeltaUs: 25_000,
                ackSequence: receivedServerHeader.PacketSequence,
                ackTimestampEchoUs: receivedServerHeader.SubTickDeltaUs);

            byte[] ackPacket = new byte[4 + KongorPacketEnvelope.TotalOverhead];
            clientAuth.TryEncryptAndWrap(
                ackPacket.AsSpan(),
                in ackHeader,
                [0x11, 0x22, 0x33, 0x44],
                DatagramDirection.ClientToServer,
                out int ackLen);

            await clientSocket.SendToAsync(ackPacket.AsMemory(0, ackLen), SocketFlags.None, forwarderEndPoint, cts.Token);

            // Wait for server to receive payload and forwarder to process ACK
            await fakeServer.ReceiveAsync(srvBuf.AsMemory(), SocketFlags.None, cts.Token);

            // 5. Verify inflight bytes were reclaimed upon ACK receipt
            tracker.InflightBytes.Should().Be(0);
            tracker.HighestAckedSequence.Should().Be(1);

            BbrCongestionController? bbr = forwarder.GetSessionBbr(clientEndPoint);
            bbr.Should().NotBeNull();
            bbr!.GetTargetCongestionWindowBytes().Should().BeGreaterThanOrEqualTo(BbrCongestionController.MinCwndBytes);
        }
        finally
        {
            cts.Cancel();
            try { await forwarderTask; } catch { }
        }
    }
}
