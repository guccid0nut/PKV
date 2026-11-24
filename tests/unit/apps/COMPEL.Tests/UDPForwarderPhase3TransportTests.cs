// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ============================================================================
// File: UDPForwarderPhase3TransportTests.cs
// System: COMPEL.Tests - Phase 3 Modernized Netcode & Dual-Stack Transport Tests
// Policy: Strict Explicit Typing (NO 'var')
// ============================================================================

using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using ASPIRE.Common.Netcode;
using COMPEL.Configuration;
using COMPEL.Services.Proxy;
using COMPEL.Services.Supervision;
using COMPEL.Services.Telemetry;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Kongor.Core.Security;
using Xunit;

namespace COMPEL.Tests;

[Collection("SequentialUDPForwarder")]
public sealed class UDPForwarderPhase3TransportTests
{
    private static int FreeUdpPort()
    {
        using Socket socket = new(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)socket.LocalEndPoint!).Port;
    }

    [Fact]
    public void UdpForwarder_DualStackBinding_InitializesWithoutException()
    {
        // Arrange
        int publicPort = FreeUdpPort();
        int localPort = FreeUdpPort();

        // Act
        using UDPForwarder forwarder = new(publicPort, localPort, NullLogger.Instance);

        // Assert
        forwarder.PublicPort.Should().Be(publicPort);
        forwarder.LocalPort.Should().Be(localPort);
    }

    [Fact]
    public void S2Crypto_ComputeSrpFromPrehashedSha256_ProducesDeterministic64HexHash()
    {
        // Arrange
        string salt = S2Crypto.GenerateSrpSalt();
        string plaintext = "SuperSecretDedicatedServerPassword2026!";
        string sha256Hex = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(plaintext)));

        // Act
        string srpSha256 = S2Crypto.ComputeSrpFromPrehashedSha256(sha256Hex, salt);
        string srpSha256Repeat = S2Crypto.ComputeSrpFromPrehashedSha256(sha256Hex, salt);

        // Assert
        srpSha256.Should().HaveLength(64);
        srpSha256.All(char.IsAsciiHexDigitLower).Should().BeTrue();
        srpSha256.Should().Be(srpSha256Repeat, "cryptographic hashing must be completely deterministic for identical credentials and salt");

        string srpMd5 = S2Crypto.ComputeSrpFromPlaintext(plaintext, salt);
        srpSha256.Should().NotBe(srpMd5, "SHA-256 SRP hash must differ from legacy MD5-based SRP hash");
    }

    [Fact]
    public void MatchServerManagerOptions_UseSha256Authentication_DefaultsToFalseForLegacyParity()
    {
        // Arrange & Act
        MatchServerManagerOptions options = new();

        // Assert
        options.UseSha256Authentication.Should().BeFalse("legacy compatibility requires defaulting to false");
    }

    [Fact]
    public void MatchServerManagerSupervisor_GetHashedPassword_WithSha256Option_Emits64HexHash()
    {
        // Arrange
        string plainPassword = "TestPassword123!";
        string expectedSha256 = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(plainPassword)));

        MatchServerManagerOptions options = new()
        {
            Password = plainPassword,
            UseSha256Authentication = true
        };

        MatchServerManagerSupervisor supervisor = new(
            Options.Create(options),
            NullLogger<MatchServerManagerSupervisor>.Instance);

        // Act - Call internal testing or reflection method
        System.Reflection.MethodInfo? method = typeof(MatchServerManagerSupervisor)
            .GetMethod("GetHashedPassword", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

        method.Should().NotBeNull();
        string? result = method!.Invoke(supervisor, null) as string;

        // Assert
        result.Should().Be(expectedSha256);
        result.Should().HaveLength(64);
    }

    [Fact]
    public void MatchServerManagerSupervisor_GetHashedPassword_WithLegacyDefault_Emits32HexMd5()
    {
        // Arrange
        string plainPassword = "TestPassword123!";
        string expectedMd5 = Convert.ToHexStringLower(MD5.HashData(Encoding.UTF8.GetBytes(plainPassword)));

        MatchServerManagerOptions options = new()
        {
            Password = plainPassword,
            UseSha256Authentication = false
        };

        MatchServerManagerSupervisor supervisor = new(
            Options.Create(options),
            NullLogger<MatchServerManagerSupervisor>.Instance);

        System.Reflection.MethodInfo? method = typeof(MatchServerManagerSupervisor)
            .GetMethod("GetHashedPassword", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

        method.Should().NotBeNull();
        string? result = method!.Invoke(supervisor, null) as string;

        // Assert
        result.Should().Be(expectedMd5);
        result.Should().HaveLength(32);
    }

    [Fact]
    public async Task UdpForwarder_BatchedReceive_RelaysMultipleDatagramsSuccessfully()
    {
        // Arrange
        int publicPort = FreeUdpPort();
        int localPort = FreeUdpPort();

        using UDPForwarder forwarder = new(publicPort, localPort, NullLogger.Instance, enableZstdCompression: false);
        using CancellationTokenSource cts = new();

        Task forwarderTask = Task.Run(() => forwarder.Run(cts.Token));

        using Socket clientSocket = new(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        clientSocket.Bind(new IPEndPoint(IPAddress.Loopback, 0));

        using Socket serverSocket = new(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        serverSocket.Bind(new IPEndPoint(IPAddress.Loopback, localPort));

        IPEndPoint forwarderEndpoint = new(IPAddress.Loopback, publicPort);

        try
        {
            // Initial packet to trigger challenge issuance
            byte[] initDatagram = [0x01, 0x02, 0x03, 0x04];
            await clientSocket.SendToAsync(initDatagram, SocketFlags.None, forwarderEndpoint, cts.Token);

            // Give proxy a moment to spawn session and issue challenge
            byte[] challengeBuffer = new byte[1024];
            EndPoint serverRemote = new IPEndPoint(IPAddress.Any, 0);
            using CancellationTokenSource challengeTimeout = new(TimeSpan.FromSeconds(2));
            SocketReceiveFromResult challengeResult = await clientSocket.ReceiveFromAsync(challengeBuffer, SocketFlags.None, serverRemote, challengeTimeout.Token);

            challengeResult.ReceivedBytes.Should().BeGreaterThan(0);

            // Extract challenge value
            challengeBuffer[2].Should().Be(0x40, "Packet must be proxy packet flag");
            uint challengeVal = BinaryPrimitives.ReadUInt32LittleEndian(challengeBuffer.AsSpan(14, 4));

            // Burst transmit 5 valid datagrams in rapid succession to exercise batched socket draining
            for (ushort counter = 1; counter <= 5; counter++)
            {
                byte[] cmdBytes = Encoding.UTF8.GetBytes($"BurstPacket_{counter}");
                byte[] burstDatagram = new byte[ClientPacketReader.MinimumLength(ProxyForwarderKind.Game) + cmdBytes.Length];
                BinaryPrimitives.WriteUInt32LittleEndian(burstDatagram.AsSpan(ClientPacketReader.ChallengeOffset), challengeVal);
                BinaryPrimitives.WriteUInt16LittleEndian(burstDatagram.AsSpan(ClientPacketReader.CounterOffset), counter);
                cmdBytes.CopyTo(burstDatagram.AsSpan(ClientPacketReader.MinimumLength(ProxyForwarderKind.Game)));

                await clientSocket.SendToAsync(burstDatagram, SocketFlags.None, forwarderEndpoint, cts.Token);
            }

            // Verify server receives the burst datagrams
            int receivedCount = 0;
            byte[] serverBuffer = new byte[2048];
            using CancellationTokenSource receiveTimeout = new(TimeSpan.FromSeconds(3));

            while (receivedCount < 5 && !receiveTimeout.Token.IsCancellationRequested)
            {
                try
                {
                    SocketReceiveFromResult sResult = await serverSocket.ReceiveFromAsync(
                        serverBuffer,
                        SocketFlags.None,
                        serverRemote,
                        receiveTimeout.Token);

                    if (sResult.ReceivedBytes > 0)
                    {
                        receivedCount++;
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }

            receivedCount.Should().Be(5, "all 5 burst datagrams must be cleanly drained and relayed to the game server");
        }
        finally
        {
            cts.Cancel();
            try
            {
                await forwarderTask.WaitAsync(TimeSpan.FromMilliseconds(500));
            }
            catch
            {
                // Expected cancellation
            }
        }
    }
}
