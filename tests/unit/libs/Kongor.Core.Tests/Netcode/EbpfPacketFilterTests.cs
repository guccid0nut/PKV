// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Buffers.Binary;
using ASPIRE.Common.Netcode.Ebpf;
using FluentAssertions;
using Xunit;

namespace Kongor.Core.Tests.Netcode;

[Trait("Category", "Experimental")]
public sealed class EbpfPacketFilterTests
{
    private static byte[] BuildPacket(
        ushort etherType = 0x0800,
        byte ipProtocol = 17,
        ushort destPort = 11235,
        uint k2HeaderPrefix = 0x01FF_FFFFU, // Realistic K2 OOB (0xFFFF) + packet opcode (0x01)
        int extraPayloadSize = 32)
    {
        int totalSize = 14 + 20 + 8 + 4 + extraPayloadSize;
        byte[] buffer = new byte[totalSize];

        // 1. Ethernet Header (14 bytes)
        // Dest MAC (6), Src MAC (6)
        buffer[0] = 0x00; buffer[1] = 0x1A; buffer[2] = 0x2B; buffer[3] = 0x3C; buffer[4] = 0x4D; buffer[5] = 0x5E;
        buffer[6] = 0x00; buffer[7] = 0x11; buffer[8] = 0x22; buffer[9] = 0x33; buffer[10] = 0x44; buffer[11] = 0x55;
        BinaryPrimitives.WriteUInt16BigEndian(buffer.AsSpan(12, 2), etherType);

        if (etherType != 0x0800) return buffer;

        // 2. IPv4 Header (20 bytes)
        int ipOffset = 14;
        buffer[ipOffset] = 0x45; // Version 4, IHL 5 (20 bytes)
        buffer[ipOffset + 9] = ipProtocol; // UDP = 17
        BinaryPrimitives.WriteUInt16BigEndian(buffer.AsSpan(ipOffset + 2, 2), (ushort)(totalSize - 14));
        // Src IP: 192.168.1.50
        buffer[ipOffset + 12] = 192; buffer[ipOffset + 13] = 168; buffer[ipOffset + 14] = 1; buffer[ipOffset + 15] = 50;
        // Dest IP: 192.168.1.1
        buffer[ipOffset + 16] = 192; buffer[ipOffset + 17] = 168; buffer[ipOffset + 18] = 1; buffer[ipOffset + 19] = 1;

        if (ipProtocol != 17) return buffer;

        // 3. UDP Header (8 bytes)
        int udpOffset = ipOffset + 20;
        BinaryPrimitives.WriteUInt16BigEndian(buffer.AsSpan(udpOffset, 2), 54321); // Source Port
        BinaryPrimitives.WriteUInt16BigEndian(buffer.AsSpan(udpOffset + 2, 2), destPort); // Dest Port
        BinaryPrimitives.WriteUInt16BigEndian(buffer.AsSpan(udpOffset + 4, 2), (ushort)(8 + 4 + extraPayloadSize)); // UDP Length

        // 4. K2 UDP Payload Prefix (4 bytes: realistic non-ASCII sequence/OOB header + opcode)
        int headerOffset = udpOffset + 8;
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(headerOffset, 4), k2HeaderPrefix);

        return buffer;
    }

    [Theory]
    [InlineData(11235)]
    [InlineData(11236)]
    [InlineData(11237)]
    [InlineData(11238)]
    public void ProcessPacket_ValidK2UdpFrameWithoutBogusMagic_PassesAndRecordsStats(ushort port)
    {
        EbpfPacketFilterVerifier verifier = new();
        // Realistic K2 sequence number 0x0042 + opcode 0x2A (NOT "K2SV" 0x4B325356!)
        byte[] packet = BuildPacket(destPort: port, k2HeaderPrefix: 0x002A_0042U);

        XdpAction action = verifier.ProcessPacket(packet);

        action.Should().Be(XdpAction.Pass);
        verifier.Stats.PacketsTotal.Should().Be(1);
        verifier.Stats.PacketsPassed.Should().Be(1);
        verifier.Stats.PacketsDroppedMagic.Should().Be(0);
        verifier.Stats.PacketsDroppedMalformed.Should().Be(0);
        verifier.Stats.BytesProcessed.Should().Be(packet.Length);
    }

    [Fact]
    public void ProcessPacket_NullAmplificationProbe_DropsPacketImmediately()
    {
        EbpfPacketFilterVerifier verifier = new();
        byte[] packet = BuildPacket(destPort: 11235, k2HeaderPrefix: EbpfPacketFilterVerifier.NullAmplificationProbe);

        XdpAction action = verifier.ProcessPacket(packet);

        action.Should().Be(XdpAction.Drop);
        verifier.Stats.PacketsTotal.Should().Be(1);
        verifier.Stats.PacketsPassed.Should().Be(0);
        verifier.Stats.PacketsDroppedMagic.Should().Be(1);
    }

    [Fact]
    public void ProcessPacket_NonGamePort_PassesThroughUntouched()
    {
        EbpfPacketFilterVerifier verifier = new();
        byte[] packet = BuildPacket(destPort: 80, k2HeaderPrefix: 0x00000000);

        XdpAction action = verifier.ProcessPacket(packet);

        action.Should().Be(XdpAction.Pass);
        verifier.Stats.PacketsPassed.Should().Be(1);
        verifier.Stats.PacketsDroppedMagic.Should().Be(0);
    }

    [Fact]
    public void ProcessPacket_NonUdpTraffic_PassesThroughUntouched()
    {
        EbpfPacketFilterVerifier verifier = new();
        byte[] packet = BuildPacket(ipProtocol: 6, destPort: 11235); // TCP

        XdpAction action = verifier.ProcessPacket(packet);

        action.Should().Be(XdpAction.Pass);
        verifier.Stats.PacketsPassed.Should().Be(1);
    }

    [Fact]
    public void ProcessPacket_NonIpTraffic_PassesThroughUntouched()
    {
        EbpfPacketFilterVerifier verifier = new();
        byte[] packet = BuildPacket(etherType: 0x0806); // ARP

        XdpAction action = verifier.ProcessPacket(packet);

        action.Should().Be(XdpAction.Pass);
        verifier.Stats.PacketsPassed.Should().Be(1);
    }

    [Fact]
    public void ProcessPacket_TruncatedMalformedPacket_Drops()
    {
        EbpfPacketFilterVerifier verifier = new();
        byte[] shortPacket = [0x00, 0x01, 0x02];

        XdpAction action = verifier.ProcessPacket(shortPacket);

        action.Should().Be(XdpAction.Drop);
        verifier.Stats.PacketsDroppedMalformed.Should().Be(1);
    }

    [Fact]
    public void ProcessPacket_RateLimiter_ExceedingBurstDropsExcessTraffic()
    {
        EbpfPacketFilterVerifier verifier = new();
        byte[] packet = BuildPacket(destPort: 11235);

        // Send 1000 allowed burst packets
        for (int i = 0; i < 1000; i++)
        {
            XdpAction action = verifier.ProcessPacket(packet, currentTimeMs: 1000);
            action.Should().Be(XdpAction.Pass);
        }

        // 1001st packet in same burst window should be dropped
        XdpAction burstDrop = verifier.ProcessPacket(packet, currentTimeMs: 1000);
        burstDrop.Should().Be(XdpAction.Drop);
        verifier.Stats.PacketsDroppedRateLimit.Should().Be(1);

        // After window expires (1000ms later), tokens replenish
        XdpAction replenishedPass = verifier.ProcessPacket(packet, currentTimeMs: 2050);
        replenishedPass.Should().Be(XdpAction.Pass);
    }
}
