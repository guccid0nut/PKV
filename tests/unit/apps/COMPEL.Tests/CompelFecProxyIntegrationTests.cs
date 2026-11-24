// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ============================================================================
// File: CompelFecProxyIntegrationTests.cs
// System: COMPEL Tests - Forward Error Correction Proxy Integration Tests
// Policy: Strict Explicit Typing (NO 'var')
// ============================================================================

using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using ASPIRE.Common.Netcode;
using COMPEL.Services.Proxy;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace COMPEL.Tests;

[Collection("SequentialUDPForwarder")]
public sealed class CompelFecProxyIntegrationTests
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
    public async Task UdpForwarder_WithFecEnabled_RecoversDroppedPacketAndDeliversToServer()
    {
        int publicPort = FreeUdpPort();
        int localPort = FreeUdpPort();

        using Socket server = new(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        server.Bind(new IPEndPoint(IPAddress.Loopback, localPort));

        using UDPForwarder forwarder = new(
            publicPort,
            localPort,
            NullLogger.Instance,
            enableZstdCompression: false,
            enableFec: true);

        using CancellationTokenSource lifetime = new();
        Task run = forwarder.Run(lifetime.Token);

        try
        {
            using Socket client = new(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            client.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            IPEndPoint publicEndPoint = new(IPAddress.Loopback, publicPort);

            // 1. Send initial unauthenticated handshake to trigger session and challenge
            byte[] initHello = GameDatagram(SessionChallengeState.UnauthenticatedChallenge, counter: 0, "INIT");
            await client.SendToAsync(initHello, SocketFlags.None, publicEndPoint);

            // Server receives initial handshake
            (byte[] Payload, EndPoint Sender)? serverInit = await TryReceive(server);
            serverInit.Should().NotBeNull();

            // Forwarder sends challenge to client
            forwarder.ChallengeActiveSessions();
            (byte[] Payload, EndPoint Sender)? clientChallenge = await TryReceive(client);

            // Read challenge value if received or use known active challenge
            uint activeChallenge = SessionChallengeState.UnauthenticatedChallenge;
            if (clientChallenge != null && (clientChallenge.Value.Payload.Length == 18 || clientChallenge.Value.Payload.Length >= 58))
            {
                int offset = clientChallenge.Value.Payload.Length == 18 ? 14 : (40 + 14);
                activeChallenge = BinaryPrimitives.ReadUInt32LittleEndian(clientChallenge.Value.Payload.AsSpan(offset, 4));
            }

            // 2. Prepare 4 data packets
            XorFecEncoder encoder = new(blockSize: 4);
            byte[][] rawGamePackets =
            [
                GameDatagram(activeChallenge, counter: 1, "Cmd_MoveTo(100, 200)"),
                GameDatagram(activeChallenge, counter: 2, "Cmd_CastAbility(Hero_Devourer, Hook)"),
                GameDatagram(activeChallenge, counter: 3, "Cmd_AttackTarget(Creep_9)"),
                GameDatagram(activeChallenge, counter: 4, "Cmd_Stop()")
            ];

            byte[][] encodedWirePackets = new byte[4][];
            for (int i = 0; i < 4; i++)
            {
                encodedWirePackets[i] = new byte[512];
                encoder.TryEncodeData(rawGamePackets[i], encodedWirePackets[i], out int written, out _);
                Array.Resize(ref encodedWirePackets[i], written);
            }

            byte[] parityPacket = new byte[512];
            encoder.TryEmitParity(parityPacket, out int parityLen);
            Array.Resize(ref parityPacket, parityLen);

            // 3. Send Packet 0, 2, 3 over the wire (Packet 1 is DROPPED!)
            await client.SendToAsync(encodedWirePackets[0], SocketFlags.None, publicEndPoint);
            // Packet 1 intentionally skipped/dropped!
            await client.SendToAsync(encodedWirePackets[2], SocketFlags.None, publicEndPoint);
            await client.SendToAsync(encodedWirePackets[3], SocketFlags.None, publicEndPoint);

            // Now send the Parity packet!
            await client.SendToAsync(parityPacket, SocketFlags.None, publicEndPoint);

            // 4. Verify the server socket receives all 4 packets (including recovered Packet 1)!
            List<string> commandsReceivedAtServer = [];
            for (int i = 0; i < 4; i++)
            {
                (byte[] Payload, EndPoint Sender)? fromServer = await TryReceive(server);
                if (fromServer != null)
                {
                    string text = Encoding.UTF8.GetString(fromServer.Value.Payload);
                    commandsReceivedAtServer.Add(text);
                }
            }

            // The server must have received all 4 distinct commands!
            commandsReceivedAtServer.Should().Contain(s => s.Contains("Cmd_MoveTo"));
            commandsReceivedAtServer.Should().Contain(s => s.Contains("Cmd_CastAbility(Hero_Devourer, Hook)")); // RECOVERED!
            commandsReceivedAtServer.Should().Contain(s => s.Contains("Cmd_AttackTarget"));
            commandsReceivedAtServer.Should().Contain(s => s.Contains("Cmd_Stop"));
        }
        finally
        {
            lifetime.Cancel();
            try { await run; } catch (OperationCanceledException) { }
        }
    }
}
