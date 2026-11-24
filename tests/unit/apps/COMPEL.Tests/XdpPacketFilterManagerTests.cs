// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ============================================================================
// File: XdpPacketFilterManagerTests.cs
// System: COMPEL.Tests - eBPF / XDP Network Filter & Port Synchronizer Tests
// Policy: Strict Explicit Typing (NO 'var')
// ============================================================================

using System.Buffers.Binary;
using System.Net;
using COMPEL.Services.Proxy;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace COMPEL.Tests;

public sealed class XdpPacketFilterManagerTests
{
    private static readonly IPEndPoint ClientEndPoint = new(IPAddress.Parse("192.168.1.100"), 54321);

    private static byte[] CreateTestDatagram(uint challenge, ushort counter, int length = 43)
    {
        byte[] buffer = new byte[Math.Max(length, 43)];
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(ClientPacketReader.ChallengeOffset), challenge);
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(ClientPacketReader.CounterOffset), counter);
        return buffer;
    }

    [Fact]
    public void RegisterPort_AddsPortToActiveRegistry_AndIsPortActiveReturnsTrue()
    {
        // Arrange
        using XdpPacketFilterManager manager = new(NullLogger<XdpPacketFilterManager>.Instance);
        ushort testPort = 15000;

        // Act
        bool registered = manager.RegisterPort(testPort, processId: 1234, isVoice: false, isProxy: false);

        // Assert
        registered.Should().BeTrue();
        manager.IsPortActive(testPort).Should().BeTrue();
        manager.ActivePortCount.Should().Be(1);

        KongorActivePortInfo? info = manager.GetPortInfo(testPort);
        info.Should().NotBeNull();
        info!.Port.Should().Be(testPort);
        info.ProcessId.Should().Be(1234);
        info.IsVoice.Should().BeFalse();
        info.IsProxy.Should().BeFalse();
    }

    [Fact]
    public void UnregisterPort_RemovesPortFromActiveRegistry()
    {
        // Arrange
        using XdpPacketFilterManager manager = new(NullLogger<XdpPacketFilterManager>.Instance);
        ushort testPort = 15001;
        manager.RegisterPort(testPort, processId: 4321, isVoice: true, isProxy: true);

        // Act
        bool unregistered = manager.UnregisterPort(testPort);

        // Assert
        unregistered.Should().BeTrue();
        manager.GetPortInfo(testPort).Should().BeNull();
        manager.ActivePortCount.Should().Be(0);
    }

    [Fact]
    public void StaticPortBoundaries_AreRecognizedAsActiveEvenWithoutDynamicRegistration()
    {
        // Arrange
        using XdpPacketFilterManager manager = new(NullLogger<XdpPacketFilterManager>.Instance);

        // Assert
        manager.IsPortActive(11235).Should().BeTrue("Base game port is static");
        manager.IsPortActive(11735).Should().BeTrue("Base voice port is static");
        manager.IsPortActive(21235).Should().BeTrue("Base proxy game port is static");
        manager.IsPortActive(21735).Should().BeTrue("Base proxy voice port is static");
        manager.IsPortActive(9999).Should().BeFalse("Arbitrary unmapped port must not be active");
    }

    [Fact]
    public void ValidateIngressPacket_DropsPacket_WhenTargetPortIsUnregistered()
    {
        // Arrange
        using XdpPacketFilterManager manager = new(NullLogger<XdpPacketFilterManager>.Instance);
        byte[] datagram = CreateTestDatagram(challenge: 100, counter: 1);
        ushort unregisteredPort = 33333;

        // Act
        DatagramValidationResult result = manager.ValidateIngressPacket(datagram, unregisteredPort, ClientEndPoint);

        // Assert
        result.IsAdmitted.Should().BeFalse();
        result.DropReason.Should().Be("Unregistered Port");

        XdpFilterStats stats = manager.GetStats();
        stats.DroppedUnregisteredPort.Should().Be(1);
    }

    [Fact]
    public void ValidateIngressPacket_DropsPacket_WhenShorterThanMinimumRequiredLength()
    {
        // Arrange
        using XdpPacketFilterManager manager = new(NullLogger<XdpPacketFilterManager>.Instance);
        byte[] shortDatagram = new byte[20]; // Minimum is 43 for game
        ushort gamePort = 11235;

        // Act
        DatagramValidationResult result = manager.ValidateIngressPacket(shortDatagram, gamePort, ClientEndPoint);

        // Assert
        result.IsAdmitted.Should().BeFalse();
        result.DropReason.Should().Be("Too Short");

        XdpFilterStats stats = manager.GetStats();
        stats.DroppedTooShort.Should().Be(1);
    }

    [Fact]
    public void ValidateIngressPacket_DropsPacket_WhenChallengeAndCounterAreMalformedFlood()
    {
        // Arrange
        using XdpPacketFilterManager manager = new(NullLogger<XdpPacketFilterManager>.Instance);
        byte[] malformedDatagram = CreateTestDatagram(challenge: 0xFFFFFFFF, counter: 0xFFFF);
        ushort gamePort = 11235;

        // Act
        DatagramValidationResult result = manager.ValidateIngressPacket(malformedDatagram, gamePort, ClientEndPoint);

        // Assert
        result.IsAdmitted.Should().BeFalse();
        result.DropReason.Should().Be("Malformed Challenge Envelope");

        XdpFilterStats stats = manager.GetStats();
        stats.DroppedMalformed.Should().Be(1);
    }

    [Fact]
    public void ValidateIngressPacket_DropsPacket_WhenAllZeroWatermarkFlood()
    {
        // Arrange
        using XdpPacketFilterManager manager = new(NullLogger<XdpPacketFilterManager>.Instance);
        byte[] zeroDatagram = CreateTestDatagram(challenge: 0, counter: 0);
        ushort gamePort = 11235;

        // Act
        DatagramValidationResult result = manager.ValidateIngressPacket(zeroDatagram, gamePort, ClientEndPoint);

        // Assert
        result.IsAdmitted.Should().BeFalse();
        result.DropReason.Should().Be("Zero Watermark Envelope");

        XdpFilterStats stats = manager.GetStats();
        stats.DroppedMalformed.Should().Be(1);
    }

    [Fact]
    public void ValidateIngressPacket_AdmitsLegitimateDatagram_OnRegisteredPort()
    {
        // Arrange
        using XdpPacketFilterManager manager = new(NullLogger<XdpPacketFilterManager>.Instance);
        ushort activePort = 11235;
        byte[] validDatagram = CreateTestDatagram(challenge: 424242, counter: 10);

        // Act
        DatagramValidationResult result = manager.ValidateIngressPacket(validDatagram, activePort, ClientEndPoint);

        // Assert
        result.IsAdmitted.Should().BeTrue();
        result.IsAuthenticatedChallenge.Should().BeTrue();

        XdpFilterStats stats = manager.GetStats();
        stats.PassedGamePackets.Should().Be(1);
        stats.TotalIngressPackets.Should().Be(1);
    }

    [Fact]
    public void ValidateIngressPacket_EnforcesTokenBucketRateLimiting_UnderAbusiveFlood()
    {
        // Arrange
        using XdpPacketFilterManager manager = new(NullLogger<XdpPacketFilterManager>.Instance);
        ushort activePort = 11235;
        byte[] validDatagram = CreateTestDatagram(challenge: 12345, counter: 1);
        IPEndPoint floodingClient = new(IPAddress.Parse("10.0.0.99"), 60000);

        // Act - burst 5000 packets instantaneously (capacity is 250, refill is 500/s; 300 only had a 100ms margin under parallel CPU load)
        int admittedCount = 0;
        int droppedCount = 0;

        for (int i = 0; i < 5000; i++)
        {
            DatagramValidationResult result = manager.ValidateIngressPacket(validDatagram, activePort, floodingClient);
            if (result.IsAdmitted)
            {
                admittedCount++;
            }
            else if (result.DropReason == "Rate Limited")
            {
                droppedCount++;
            }
        }

        // Assert
        admittedCount.Should().BeGreaterThan(0);
        droppedCount.Should().BeGreaterThan(0, "packet burst exceeding token bucket capacity must be rate limited");

        XdpFilterStats stats = manager.GetStats();
        stats.DroppedRateLimited.Should().Be(droppedCount);
    }

    [Fact]
    public void Clear_FlushesAllActivePortsAndRateLimiters()
    {
        // Arrange
        using XdpPacketFilterManager manager = new(NullLogger<XdpPacketFilterManager>.Instance);
        manager.RegisterPort(14001, 100, isVoice: false, isProxy: false);
        manager.RegisterPort(14002, 101, isVoice: true, isProxy: false);
        manager.ActivePortCount.Should().Be(2);

        // Act
        manager.Clear();

        // Assert
        manager.ActivePortCount.Should().Be(0);
        manager.GetActivePorts().Should().BeEmpty();
    }
}
