// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ============================================================================
// File: CompelDualStackForwarderTests.cs
// System: COMPEL Tests - Dual-Stack Forwarder & Modern Envelope Integration Tests
// Policy: Strict Explicit Typing (NO 'var')
// ============================================================================

using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using ASPIRE.Common.Netcode;
using COMPEL.Services.Proxy;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace COMPEL.Tests;

[Collection("SequentialUDPForwarder")]
public sealed class CompelDualStackForwarderTests
{
    private static readonly TimeSpan ReceiveTimeout = TimeSpan.FromMilliseconds(750);
    private static readonly byte[] SharedMatchSecret = Encoding.UTF8.GetBytes("SuperMatchSecretKey2026Kongor");

    private static int FreeUdpPort()
    {
        using Socket socket = new(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)socket.LocalEndPoint!).Port;
    }

    private static byte[] LegacyGameDatagram(uint challenge, ushort counter, string command)
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
    public async Task UdpForwarder_DualStackClassifier_RelaysBothLegacyAndModernPackets()
    {
        int publicPort = FreeUdpPort();
        int localPort = FreeUdpPort();
        byte[] sessionKey = DatagramAuthenticator.DeriveSessionKey(SharedMatchSecret);

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
            enableAnomalyShield: true,
            sessionKeyResolver: _ => sessionKey);

        using CancellationTokenSource cts = new();
        Task forwarderTask = forwarder.Run(cts.Token);

        try
        {
            IPEndPoint publicEndPoint = new(IPAddress.Loopback, publicPort);

            // ================================================================
            // 1. Client A: Legacy Protocol Traffic
            // ================================================================
            using Socket legacyClient = new(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            legacyClient.Bind(new IPEndPoint(IPAddress.Loopback, 0));

            // Packet 0: Unauthenticated challenge
            byte[] legacyPacket0 = LegacyGameDatagram(SessionChallengeState.UnauthenticatedChallenge, counter: 0, "LegacyClient_Hello");
            await legacyClient.SendToAsync(legacyPacket0, SocketFlags.None, publicEndPoint);

            (byte[] Payload, EndPoint Sender)? serverPacket0 = await TryReceive(server);
            serverPacket0.Should().NotBeNull();

            // Forwarder issues challenge back to legacy client
            (byte[] Payload, EndPoint Sender)? challengeResponse = await TryReceive(legacyClient);
            challengeResponse.Should().NotBeNull();
            challengeResponse!.Value.Payload.Length.Should().BeGreaterThanOrEqualTo(18);

            // Extract issued challenge (last 4 bytes of challenge payload)
            int challengeOffset = challengeResponse.Value.Payload.Length == 18 ? 14 : (40 + 14);
            uint issuedChallenge = BinaryPrimitives.ReadUInt32LittleEndian(challengeResponse.Value.Payload.AsSpan(challengeOffset, 4));

            // Packet 1: Valid challenge echo
            byte[] legacyPacket1 = LegacyGameDatagram(issuedChallenge, counter: 1, "LegacyClient_Command1");
            await legacyClient.SendToAsync(legacyPacket1, SocketFlags.None, publicEndPoint);

            // Dedicated server receives pure legacy packet
            (byte[] Payload, EndPoint Sender)? serverReceivedLegacy = await TryReceive(server);
            serverReceivedLegacy.Should().NotBeNull();
            string legacyCommand = Encoding.UTF8.GetString(serverReceivedLegacy!.Value.Payload.AsSpan(ClientPacketReader.MinimumLength(ProxyForwarderKind.Game)));
            legacyCommand.Should().Be("LegacyClient_Command1");

            // ================================================================
            // 2. Client B: Modern ChaCha20-Poly1305 Envelope Traffic
            // ================================================================
            using Socket modernClient = new(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            modernClient.Bind(new IPEndPoint(IPAddress.Loopback, 0));

            using DatagramAuthenticator clientCrypto = new(sessionKey, sessionId: 0);

            byte[] modernPlaintext = Encoding.UTF8.GetBytes("ModernClient_SubTickCommand");
            KongorPacketHeader modernHeader = new(
                sessionId: 12345,
                packetSequence: 1,
                subTickDeltaUs: 12500,
                ackSequence: 0,
                ackTimestampEchoUs: 0);

            byte[] modernEnvelope = new byte[KongorPacketEnvelope.TotalOverhead + modernPlaintext.Length];
            bool encryptOk = clientCrypto.TryEncryptAndWrap(
                modernEnvelope.AsSpan(),
                in modernHeader,
                modernPlaintext.AsSpan(),
                DatagramDirection.ClientToServer,
                out int modernWritten);
            encryptOk.Should().BeTrue();

            await modernClient.SendToAsync(modernEnvelope.AsMemory(0, modernWritten), SocketFlags.None, publicEndPoint);

            // Dedicated server receives decrypted vanilla payload directly
            (byte[] Payload, EndPoint Sender)? serverReceivedModern = await TryReceive(server);
            serverReceivedModern.Should().NotBeNull();
            string modernCommand = Encoding.UTF8.GetString(serverReceivedModern!.Value.Payload);
            modernCommand.Should().Be("ModernClient_SubTickCommand");

            // Verify downstream response to modern client is also wrapped in modern envelope
            await server.SendToAsync(Encoding.UTF8.GetBytes("Server_Snapshot_42"), SocketFlags.None, serverReceivedModern.Value.Sender);

            (byte[] Payload, EndPoint Sender)? modernClientReceived = await TryReceive(modernClient);
            modernClientReceived.Should().NotBeNull();
            KongorPacketEnvelope.IsModernEnvelope(modernClientReceived!.Value.Payload).Should().BeTrue();

            byte[] clientDecryptedSnapshot = new byte[128];
            bool decryptSnapshotOk = clientCrypto.TryUnwrapAndDecrypt(
                modernClientReceived.Value.Payload,
                DatagramDirection.ServerToClient,
                clientDecryptedSnapshot.AsSpan(),
                out _,
                out int decryptedSnapshotLen);

            decryptSnapshotOk.Should().BeTrue();
            string serverSnapshotText = Encoding.UTF8.GetString(clientDecryptedSnapshot.AsSpan(0, decryptedSnapshotLen));
            serverSnapshotText.Should().Be("Server_Snapshot_42");
        }
        finally
        {
            cts.Cancel();
            try { await forwarderTask; } catch { }
        }
    }

    [Fact]
    public async Task UdpForwarder_DropsTamperedModernEnvelope_AndDoesNotForwardToServer()
    {
        int publicPort = FreeUdpPort();
        int localPort = FreeUdpPort();
        byte[] sessionKey = DatagramAuthenticator.DeriveSessionKey(SharedMatchSecret);

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
            sessionKeyResolver: _ => sessionKey);

        using CancellationTokenSource cts = new();
        Task forwarderTask = forwarder.Run(cts.Token);

        try
        {
            IPEndPoint publicEndPoint = new(IPAddress.Loopback, publicPort);

            using Socket attackerClient = new(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            attackerClient.Bind(new IPEndPoint(IPAddress.Loopback, 0));

            using DatagramAuthenticator attackerCrypto = new(sessionKey, sessionId: 0);

            byte[] plaintext = Encoding.UTF8.GetBytes("HackedPlayerCommand");
            KongorPacketHeader header = new(sessionId: 9999, packetSequence: 1, subTickDeltaUs: 0);

            byte[] envelope = new byte[KongorPacketEnvelope.TotalOverhead + plaintext.Length];
            attackerCrypto.TryEncryptAndWrap(
                envelope.AsSpan(),
                in header,
                plaintext.AsSpan(),
                DatagramDirection.ClientToServer,
                out int written);

            // Tamper with ciphertext
            envelope[25] ^= 0xFF;

            await attackerClient.SendToAsync(envelope.AsMemory(0, written), SocketFlags.None, publicEndPoint);

            // Dedicated server should receive nothing
            (byte[] Payload, EndPoint Sender)? serverReceived = await TryReceive(server);
            serverReceived.Should().BeNull();

            // Forwarder recorded dropped datagram
            forwarder.DroppedDatagramCount.Should().BeGreaterThanOrEqualTo(1);
        }
        finally
        {
            cts.Cancel();
            try { await forwarderTask; } catch { }
        }
    }
}
