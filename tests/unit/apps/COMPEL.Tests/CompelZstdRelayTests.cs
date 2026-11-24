// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ============================================================================
// File: CompelZstdRelayTests.cs
// System: COMPEL.Tests - Zstandard Netcode Delta Compression & Relay Tests
// Policy: Strict Explicit Typing (NO 'var')
// ============================================================================

using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using ASPIRE.Common.Netcode;
using COMPEL.Services.Proxy;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace COMPEL.Tests;

[Collection("SequentialUDPForwarder")]
public sealed class CompelZstdRelayTests
{
    private static readonly TimeSpan ReceiveTimeout = TimeSpan.FromMilliseconds(500);

    private static Socket CreateBoundLoopbackUdpSocket()
    {
        Socket socket = new(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        if (OperatingSystem.IsWindows())
        {
            const int SioUdpConnreset = -1744830452;
            socket.IOControl((IOControlCode)SioUdpConnreset, [0, 0, 0, 0], null);
        }

        socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        return socket;
    }

    private static int FreeUDPPort()
    {
        using Socket socket = new(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)socket.LocalEndPoint!).Port;
    }

    private static async Task<(byte[] Payload, EndPoint Sender)?> TryReceive(Socket socket)
    {
        byte[] buffer = new byte[65535];
        EndPoint sender = new IPEndPoint(IPAddress.Any, 0);

        using CancellationTokenSource timeoutCts = new(ReceiveTimeout);

        try
        {
            SocketReceiveFromResult result = await socket.ReceiveFromAsync(
                buffer, SocketFlags.None, sender, timeoutCts.Token);

            return (buffer[..result.ReceivedBytes], result.RemoteEndPoint);
        }
        catch
        {
            return null;
        }
    }

    private static bool IsChallenge(byte[] packet)
    {
        return (packet.Length == 58 && packet.AsSpan(0, 40).SequenceEqual(stackalloc byte[40]))
            || (packet.Length == 18 && packet[0] == 0xFF && packet[1] == 0xFF && packet[2] == 0x40 && packet[3] == 0x00);
    }

    private static byte[] ClientInitDatagram()
    {
        byte[] datagram = new byte[ClientPacketReader.MinimumLength(ProxyForwarderKind.Game)];
        BinaryPrimitives.WriteUInt32LittleEndian(datagram.AsSpan(ClientPacketReader.ChallengeOffset), SessionChallengeState.UnauthenticatedChallenge);
        BinaryPrimitives.WriteUInt16LittleEndian(datagram.AsSpan(ClientPacketReader.CounterOffset), 0);
        return datagram;
    }

    [Fact]
    public async Task ServerToClient_LargeSnapshot_CompressesUnderMtuAndRoundTripsLosslessly()
    {
        int publicPort = FreeUDPPort();
        using Socket server = CreateBoundLoopbackUdpSocket();
        int localPort = ((IPEndPoint)server.LocalEndPoint!).Port;

        using UDPForwarder forwarder = new(publicPort, localPort, NullLogger.Instance, enableZstdCompression: true);
        using CancellationTokenSource lifetime = new();
        Task forwarderRun = forwarder.Run(lifetime.Token);

        try
        {
            using Socket client = CreateBoundLoopbackUdpSocket();

            IPEndPoint publicEndPoint = new(IPAddress.Loopback, publicPort);

            // Establish proxy session with initial datagram
            byte[] clientInit = ClientInitDatagram();
            (byte[] Payload, EndPoint Sender)? serverReceived = null;

            for (int i = 0; i < 10 && serverReceived == null; i++)
            {
                await client.SendToAsync(clientInit, SocketFlags.None, publicEndPoint);
                serverReceived = await TryReceive(server);
            }

            Assert.NotNull(serverReceived);

            // Synthesize a 1,800-byte teamfight snapshot payload
            List<byte[]> corpus = ZstdDictionaryTrainer.GenerateTrainingCorpus(5);
            byte[] largeSnapshot = corpus[0];
            if (largeSnapshot.Length < 1800)
            {
                byte[] padded = new byte[1800];
                largeSnapshot.CopyTo(padded, 0);
                largeSnapshot = padded;
            }

            // Server sends large snapshot to client via proxy upstream
            (byte[] Payload, EndPoint Sender)? clientReceived = null;
            for (int i = 0; i < 10 && clientReceived == null; i++)
            {
                await server.SendToAsync(largeSnapshot, SocketFlags.None, serverReceived.Value.Sender);
                (byte[] Payload, EndPoint Sender)? candidate = await TryReceive(client);
                if (candidate != null && !IsChallenge(candidate.Value.Payload))
                {
                    clientReceived = candidate;
                }
            }

            Assert.NotNull(clientReceived);

            // Verify packet is compressed with RFC 8878 Zstandard dictionary
            byte[] receivedBytes = clientReceived.Value.Payload;
            Assert.True(ZstdDeltaCompressor.IsDeltaCompressed(receivedBytes), "Relayed large packet should be Zstd compressed.");

            // Verify compressed size is safely under Path MTU (1472 bytes UDP payload)
            Assert.True(receivedBytes.Length <= 1472, $"Compressed size {receivedBytes.Length} should not exceed 1472 bytes (MTU safe).");
            Assert.True(receivedBytes.Length < largeSnapshot.Length, "Compressed size must be strictly smaller than original uncompressed size.");

            // Verify lossless roundtrip decompression
            byte[] decompressed = ZstdDeltaCompressor.DecompressDelta(receivedBytes);
            Assert.Equal(largeSnapshot, decompressed);
        }
        finally
        {
            await lifetime.CancelAsync();
        }
    }

    [Fact]
    public async Task ServerToClient_SmallPacket_PassesThroughUncompressed()
    {
        int publicPort = FreeUDPPort();
        using Socket server = CreateBoundLoopbackUdpSocket();
        int localPort = ((IPEndPoint)server.LocalEndPoint!).Port;

        using UDPForwarder forwarder = new(publicPort, localPort, NullLogger.Instance, enableZstdCompression: true);
        using CancellationTokenSource lifetime = new();
        Task forwarderRun = forwarder.Run(lifetime.Token);

        try
        {
            using Socket client = CreateBoundLoopbackUdpSocket();

            IPEndPoint publicEndPoint = new(IPAddress.Loopback, publicPort);

            // Establish session
            byte[] clientInit = ClientInitDatagram();
            (byte[] Payload, EndPoint Sender)? serverReceived = null;
            for (int i = 0; i < 10 && serverReceived == null; i++)
            {
                await client.SendToAsync(clientInit, SocketFlags.None, publicEndPoint);
                serverReceived = await TryReceive(server);
            }

            Assert.NotNull(serverReceived);

            // Send small ping (32 bytes)
            byte[] smallPing = Encoding.UTF8.GetBytes("PING_TICK_1234567890_HEARTBEAT");

            // Receive small packet (skipping any initial challenge)
            (byte[] Payload, EndPoint Sender)? clientReceived = null;
            for (int i = 0; i < 10 && clientReceived == null; i++)
            {
                await server.SendToAsync(smallPing, SocketFlags.None, serverReceived.Value.Sender);
                (byte[] Payload, EndPoint Sender)? candidate = await TryReceive(client);
                if (candidate != null && !IsChallenge(candidate.Value.Payload))
                {
                    clientReceived = candidate;
                }
            }

            Assert.NotNull(clientReceived);

            // Small packets must bypass compression without dictionary headers
            Assert.False(ZstdDeltaCompressor.IsDeltaCompressed(clientReceived.Value.Payload));
            Assert.Equal(smallPing, clientReceived.Value.Payload);
        }
        finally
        {
            await lifetime.CancelAsync();
        }
    }

    [Fact]
    public async Task CompelZstdRelay_ClientNetcodeRelay_DecompressesInboundPacketsTransparently()
    {
        using Socket mockServer = CreateBoundLoopbackUdpSocket();
        int mockServerPort = ((IPEndPoint)mockServer.LocalEndPoint!).Port;

        IPEndPoint mockServerEndpoint = new(IPAddress.Loopback, mockServerPort);
        await using ClientNetcodeRelay relay = new(mockServerEndpoint);
        relay.Start();

        using Socket gameClient = CreateBoundLoopbackUdpSocket();

        IPEndPoint relayLocalEndpoint = new(IPAddress.Loopback, relay.LocalPort);

        // Client initiates traffic to relay
        byte[] connectPacket = Encoding.UTF8.GetBytes("K2_CLIENT_CONNECT");
        (byte[] Payload, EndPoint Sender)? fromClientAtServer = null;

        for (int i = 0; i < 10 && fromClientAtServer == null; i++)
        {
            await gameClient.SendToAsync(connectPacket, SocketFlags.None, relayLocalEndpoint);
            fromClientAtServer = await TryReceive(mockServer);
        }

        Assert.NotNull(fromClientAtServer);

        // Server compresses an uncompressed payload
        byte[] originalData = Encoding.UTF8.GetBytes("PLAYER_HERO_STATE_COORDINATES_AND_SPELL_CAST_BURST_DATA_PACKET");
        byte[] compressedPacket = ZstdDeltaCompressor.CompressDelta(originalData, level: 1);
        Assert.True(ZstdDeltaCompressor.IsDeltaCompressed(compressedPacket));

        // Send compressed packet from server to relay (with retry loop for UDP loopback resilience)
        (byte[] Payload, EndPoint Sender)? receivedAtClient = null;
        for (int i = 0; i < 10 && receivedAtClient == null; i++)
        {
            await mockServer.SendToAsync(compressedPacket, SocketFlags.None, fromClientAtServer.Value.Sender);
            receivedAtClient = await TryReceive(gameClient);
        }

        Assert.NotNull(receivedAtClient);

        // Assert that gameClient received the original uncompressed data, NOT the compressed blob
        Assert.Equal(originalData, receivedAtClient.Value.Payload);
        Assert.False(ZstdDeltaCompressor.IsDeltaCompressed(receivedAtClient.Value.Payload));

        // Verify relay telemetry counters
        ClientRelayStats stats = relay.Stats;
        Assert.True(stats.TotalPacketsReceived > 0);
        Assert.True(stats.PacketsDecompressed > 0);
        Assert.True(stats.TotalBytesDecompressed > 0);
        Assert.Equal(0, stats.DecompressionErrors);
    }

    [Fact]
    public async Task CompelZstdRelay_CorruptedZstdPayload_RelayHandlesGracefullyWithoutThrowing()
    {
        using Socket mockServer = CreateBoundLoopbackUdpSocket();
        int mockServerPort = ((IPEndPoint)mockServer.LocalEndPoint!).Port;

        IPEndPoint mockServerEndpoint = new(IPAddress.Loopback, mockServerPort);
        await using ClientNetcodeRelay relay = new(mockServerEndpoint);
        relay.Start();

        using Socket gameClient = CreateBoundLoopbackUdpSocket();

        IPEndPoint relayLocalEndpoint = new(IPAddress.Loopback, relay.LocalPort);

        // Client registers with relay
        byte[] connectPacket = Encoding.UTF8.GetBytes("CLIENT_HELLO");
        (byte[] Payload, EndPoint Sender)? fromClientAtServer = null;
        for (int i = 0; i < 10 && fromClientAtServer == null; i++)
        {
            await gameClient.SendToAsync(connectPacket, SocketFlags.None, relayLocalEndpoint);
            fromClientAtServer = await TryReceive(mockServer);
        }
        Assert.NotNull(fromClientAtServer);

        // Craft corrupted Zstd packet (valid header, garbage compressed body)
        byte[] corruptedPacket = new byte[32];
        // Magic 0xFD2FB528
        corruptedPacket[0] = 0x28; corruptedPacket[1] = 0xB5; corruptedPacket[2] = 0x2F; corruptedPacket[3] = 0xFD;
        // Dict ID 0x484F4E31
        corruptedPacket[4] = 0x31; corruptedPacket[5] = 0x4E; corruptedPacket[6] = 0x4F; corruptedPacket[7] = 0x48;
        // Expected len 1000
        BinaryPrimitives.WriteUInt32LittleEndian(corruptedPacket.AsSpan(8, 4), 1000);
        // Garbage body
        for (int i = 12; i < 32; i++) corruptedPacket[i] = 0xFF;

        // Send corrupted packet (with retry loop for UDP loopback resilience)
        (byte[] Payload, EndPoint Sender)? clientReceived = null;
        for (int i = 0; i < 10 && clientReceived == null; i++)
        {
            await mockServer.SendToAsync(corruptedPacket, SocketFlags.None, fromClientAtServer.Value.Sender);
            clientReceived = await TryReceive(gameClient);
        }
        Assert.NotNull(clientReceived);

        // Verify that decompression error was recorded
        ClientRelayStats stats = relay.Stats;
        Assert.True(stats.DecompressionErrors > 0);
    }

    [Fact]
    public async Task DefaultOptions_DisablesZstdNetcodeCompression_SoRawK2WorldLoadSnapshotsArriveUnmodified()
    {
        COMPEL.Configuration.MatchServerManagerOptions defaultOptions = new();
        Assert.False(defaultOptions.EnableZstdNetcodeCompression);

        int publicPort = FreeUDPPort();
        using Socket server = CreateBoundLoopbackUdpSocket();
        int localPort = ((IPEndPoint)server.LocalEndPoint!).Port;

        using UDPForwarder forwarder = new(
            publicPort,
            localPort,
            NullLogger.Instance,
            enableZstdCompression: defaultOptions.EnableZstdNetcodeCompression);
        using CancellationTokenSource lifetime = new();
        Task forwarderRun = forwarder.Run(lifetime.Token);

        try
        {
            using Socket client = CreateBoundLoopbackUdpSocket();

            IPEndPoint publicEndPoint = new(IPAddress.Loopback, publicPort);
            byte[] clientInit = ClientInitDatagram();
            (byte[] Payload, EndPoint Sender)? serverReceived = null;

            for (int i = 0; i < 10 && serverReceived == null; i++)
            {
                await client.SendToAsync(clientInit, SocketFlags.None, publicEndPoint);
                serverReceived = await TryReceive(server);
            }

            Assert.NotNull(serverReceived);

            byte[] worldLoadSnapshot = new byte[1200];
            for (int i = 0; i < worldLoadSnapshot.Length; i++)
            {
                worldLoadSnapshot[i] = (byte)((i * 31) & 0xFF);
            }

            (byte[] Payload, EndPoint Sender)? clientReceived = null;
            for (int i = 0; i < 10 && clientReceived == null; i++)
            {
                await server.SendToAsync(worldLoadSnapshot, SocketFlags.None, serverReceived.Value.Sender);
                (byte[] Payload, EndPoint Sender)? candidate = await TryReceive(client);
                if (candidate != null && !IsChallenge(candidate.Value.Payload))
                {
                    clientReceived = candidate;
                }
            }

            Assert.NotNull(clientReceived);
            Assert.False(ZstdDeltaCompressor.IsDeltaCompressed(clientReceived.Value.Payload));
            Assert.Equal(worldLoadSnapshot, clientReceived.Value.Payload);
        }
        finally
        {
            await lifetime.CancelAsync();
            try
            {
                await forwarderRun;
            }
            catch (OperationCanceledException)
            {
            }
        }
    }
}
