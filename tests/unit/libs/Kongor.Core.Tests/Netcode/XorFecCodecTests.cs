// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ============================================================================
// File: XorFecCodecTests.cs
// System: Kongor Core Netcode - Zero-Allocation XOR Forward Error Correction (FEC) Unit Tests
// Policy: Strict Explicit Typing (NO 'var')
// ============================================================================

using System.Buffers.Binary;
using System.Text;
using ASPIRE.Common.Netcode;
using FluentAssertions;
using Xunit;

namespace Kongor.Core.Tests.Netcode;

public sealed class XorFecCodecTests
{
    [Fact]
    public void FecHeader_DataHeaderRoundtrip_PreservesAllFields()
    {
        // Arrange
        ushort groupId = 1042;
        byte packetIndex = 2;
        byte blockSize = 4;
        ushort payloadLength = 128;

        FecPacketHeader original = new(groupId, packetIndex, blockSize, payloadLength);
        byte[] buffer = new byte[32];

        // Act - Serialize
        bool writeSuccess = original.TryWriteDataHeader(buffer, out int bytesWritten);

        // Assert - Serialize
        writeSuccess.Should().BeTrue();
        bytesWritten.Should().Be(FecPacketHeader.DataHeaderSize);
        FecPacketHeader.IsFecPacket(buffer).Should().BeTrue();

        // Act - Deserialize
        ushort[] lengths = new ushort[4];
        bool parseSuccess = FecPacketHeader.TryParse(
            buffer.AsSpan(0, bytesWritten),
            out FecPacketHeader parsed,
            lengths,
            out int headerSize);

        // Assert - Deserialize
        parseSuccess.Should().BeTrue();
        headerSize.Should().Be(FecPacketHeader.DataHeaderSize);
        parsed.GroupId.Should().Be(groupId);
        parsed.PacketIndex.Should().Be(packetIndex);
        parsed.BlockSize.Should().Be(blockSize);
        parsed.PayloadLength.Should().Be(payloadLength);
        parsed.IsParity.Should().BeFalse();
    }

    [Fact]
    public void FecHeader_ParityHeaderRoundtrip_PreservesLengthsMapAndMetadata()
    {
        // Arrange
        ushort groupId = 2055;
        byte blockSize = 4;
        ushort maxPayloadLength = 85;
        ushort[] originalLengths = [38, 85, 42, 60];

        FecPacketHeader parityHeader = new(groupId, blockSize, blockSize, maxPayloadLength);
        byte[] buffer = new byte[64];

        // Act - Serialize
        bool writeSuccess = parityHeader.TryWriteParityHeader(buffer, originalLengths, out int bytesWritten);

        // Assert - Serialize
        writeSuccess.Should().BeTrue();
        bytesWritten.Should().Be(FecPacketHeader.DataHeaderSize + (blockSize * 2));
        FecPacketHeader.IsFecPacket(buffer).Should().BeTrue();

        // Act - Deserialize
        ushort[] readLengths = new ushort[4];
        bool parseSuccess = FecPacketHeader.TryParse(
            buffer.AsSpan(0, bytesWritten),
            out FecPacketHeader parsed,
            readLengths,
            out int headerSize);

        // Assert - Deserialize
        parseSuccess.Should().BeTrue();
        headerSize.Should().Be(bytesWritten);
        parsed.GroupId.Should().Be(groupId);
        parsed.PacketIndex.Should().Be(blockSize);
        parsed.BlockSize.Should().Be(blockSize);
        parsed.PayloadLength.Should().Be(maxPayloadLength);
        parsed.IsParity.Should().BeTrue();
        readLengths.Should().Equal(originalLengths);
    }

    [Fact]
    public void XorFecMath_SimdVsScalar_ProducesIdenticalParity()
    {
        // Arrange
        byte[] bufferSimd = new byte[128];
        byte[] bufferScalar = new byte[128];
        byte[] source = new byte[128];

        Random rng = new(42);
        rng.NextBytes(bufferSimd);
        Array.Copy(bufferSimd, bufferScalar, 128);
        rng.NextBytes(source);

        // Act - SIMD
        XorFecMath.AccumulateXor(bufferSimd, source);

        // Act - Scalar reference
        for (int i = 0; i < 128; i++)
        {
            bufferScalar[i] ^= source[i];
        }

        // Assert
        bufferSimd.Should().Equal(bufferScalar);
    }

    [Fact]
    public void XorFec_ZeroLoss_PassesAllDataPacketsThroughImmediately()
    {
        // Arrange
        XorFecEncoder encoder = new(blockSize: 4);
        XorFecDecoder decoder = new();

        byte[][] payloads =
        [
            Encoding.UTF8.GetBytes("MoveTo(100, 200)"),
            Encoding.UTF8.GetBytes("CastAbility(Hero_Pyromancer, 1)"),
            Encoding.UTF8.GetBytes("AttackEntity(Creep_42)"),
            Encoding.UTF8.GetBytes("Stop()")
        ];

        List<byte[]> deliveredPackets = [];

        // Act
        for (int i = 0; i < payloads.Length; i++)
        {
            byte[] encoded = new byte[256];
            bool encOk = encoder.TryEncodeData(payloads[i], encoded, out int written, out bool isFull);
            encOk.Should().BeTrue();

            bool decOk = decoder.TryProcessIncomingPacket(
                encoded.AsSpan(0, written),
                out FecDecodeResult result);

            decOk.Should().BeTrue();
            result.Action.Should().Be(FecPacketAction.PassThrough);
            deliveredPackets.Add(result.DeliveredPayload.ToArray());
        }

        // Emit parity
        byte[] parityPacket = new byte[256];
        bool parityOk = encoder.TryEmitParity(parityPacket, out int parityWritten);
        parityOk.Should().BeTrue();

        bool decParityOk = decoder.TryProcessIncomingPacket(
            parityPacket.AsSpan(0, parityWritten),
            out FecDecodeResult parityResult);

        decParityOk.Should().BeTrue();
        parityResult.Action.Should().Be(FecPacketAction.ParityAcknowledged);

        // Assert
        deliveredPackets.Count.Should().Be(4);
        for (int i = 0; i < 4; i++)
        {
            deliveredPackets[i].Should().Equal(payloads[i]);
        }

        decoder.Stats.TotalPacketsReceived.Should().Be(5);
        decoder.Stats.DataPacketsDelivered.Should().Be(4);
        decoder.Stats.ParityPacketsReceived.Should().Be(1);
        decoder.Stats.PacketsRecovered.Should().Be(0);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void XorFec_DropAnySinglePacket_RecoversMissingPacketInZeroMs(int droppedIndex)
    {
        // Arrange
        XorFecEncoder encoder = new(blockSize: 4);
        XorFecDecoder decoder = new();

        byte[][] payloads =
        [
            Encoding.UTF8.GetBytes("Packet_0_Alpha_Movement_Vector_At_Tick_1400"),
            Encoding.UTF8.GetBytes("Packet_1_Beta_Targeted_Ability_Cast_Spell_Slot_2_On_Enemy_Hero_42"),
            Encoding.UTF8.GetBytes("Packet_2_Gamma_Camera_Orientation_Yaw_3_14"),
            Encoding.UTF8.GetBytes("Packet_3_Delta_Player_Chat_Message_Push_Mid")
        ];

        List<byte[]> receivedPackets = [];
        byte[]? recoveredPayload = null;

        // Act - Send 3 data packets (simulate 1 dropped packet)
        byte[][] wirePackets = new byte[4][];
        for (int i = 0; i < payloads.Length; i++)
        {
            wirePackets[i] = new byte[256];
            encoder.TryEncodeData(payloads[i], wirePackets[i], out int written, out _);
            Array.Resize(ref wirePackets[i], written);

            if (i != droppedIndex)
            {
                decoder.TryProcessIncomingPacket(wirePackets[i], out FecDecodeResult result);
                result.Action.Should().Be(FecPacketAction.PassThrough);
                receivedPackets.Add(result.DeliveredPayload.ToArray());
            }
        }

        // Emit parity packet and deliver to decoder
        byte[] parityPacket = new byte[512];
        encoder.TryEmitParity(parityPacket, out int parityBytes);

        bool parityProcessed = decoder.TryProcessIncomingPacket(
            parityPacket.AsSpan(0, parityBytes),
            out FecDecodeResult recoveryResult);

        // Assert - Instant Recovery on Parity Arrival
        parityProcessed.Should().BeTrue();
        recoveryResult.Action.Should().Be(FecPacketAction.PacketRecovered);
        recoveryResult.RecoveredIndex.Should().Be(droppedIndex);
        recoveredPayload = recoveryResult.DeliveredPayload.ToArray();

        // The recovered payload must be 100% byte-for-byte identical to the dropped payload!
        recoveredPayload.Should().Equal(payloads[droppedIndex]);
        decoder.Stats.PacketsRecovered.Should().Be(1);
    }

    [Fact]
    public void XorFec_VariableLengthPackets_ReconstructsExactBytesWithoutPaddingCorruption()
    {
        // Arrange - Packets with starkly differing lengths (10 bytes, 140 bytes, 3 bytes, 75 bytes)
        XorFecEncoder encoder = new(blockSize: 4);
        XorFecDecoder decoder = new();

        byte[][] variablePayloads =
        [
            new byte[10] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10 },
            Encoding.UTF8.GetBytes("LongPayload_" + new string('X', 120)),
            new byte[3] { 0xAA, 0xBB, 0xCC },
            Encoding.UTF8.GetBytes("MediumPayload_" + new string('Y', 55))
        ];

        int droppedIndex = 1; // Drop the 132-byte packet
        byte[][] encodedData = new byte[4][];

        for (int i = 0; i < 4; i++)
        {
            encodedData[i] = new byte[512];
            encoder.TryEncodeData(variablePayloads[i], encodedData[i], out int written, out _);
            Array.Resize(ref encodedData[i], written);

            if (i != droppedIndex)
            {
                decoder.TryProcessIncomingPacket(encodedData[i], out _);
            }
        }

        byte[] parity = new byte[512];
        encoder.TryEmitParity(parity, out int parityLen);

        // Act
        decoder.TryProcessIncomingPacket(parity.AsSpan(0, parityLen), out FecDecodeResult result);

        // Assert
        result.Action.Should().Be(FecPacketAction.PacketRecovered);
        result.DeliveredPayload.Length.Should().Be(variablePayloads[droppedIndex].Length);
        result.DeliveredPayload.ToArray().Should().Equal(variablePayloads[droppedIndex]);
    }

    [Fact]
    public void XorFec_LateArrivingPacket_IsSuppressedAsDuplicate()
    {
        // Arrange
        XorFecEncoder encoder = new(blockSize: 4);
        XorFecDecoder decoder = new();

        byte[][] payloads =
        [
            Encoding.UTF8.GetBytes("Packet0"),
            Encoding.UTF8.GetBytes("Packet1_DelayedByJitter"),
            Encoding.UTF8.GetBytes("Packet2"),
            Encoding.UTF8.GetBytes("Packet3")
        ];

        byte[][] wire = new byte[4][];
        for (int i = 0; i < 4; i++)
        {
            wire[i] = new byte[256];
            encoder.TryEncodeData(payloads[i], wire[i], out int written, out _);
            Array.Resize(ref wire[i], written);
        }

        byte[] parity = new byte[256];
        encoder.TryEmitParity(parity, out int parityLen);

        // Packet 0, 2, 3 arrive
        decoder.TryProcessIncomingPacket(wire[0], out _);
        decoder.TryProcessIncomingPacket(wire[2], out _);
        decoder.TryProcessIncomingPacket(wire[3], out _);

        // Parity arrives -> Packet 1 is recovered!
        decoder.TryProcessIncomingPacket(parity.AsSpan(0, parityLen), out FecDecodeResult reconResult);
        reconResult.Action.Should().Be(FecPacketAction.PacketRecovered);
        decoder.Stats.PacketsRecovered.Should().Be(1);

        // Act - 30ms later, the late original Packet 1 arrives on the wire
        bool lateProcessed = decoder.TryProcessIncomingPacket(wire[1], out FecDecodeResult lateResult);

        // Assert - Must be recognized and suppressed as a duplicate!
        lateProcessed.Should().BeTrue();
        lateResult.Action.Should().Be(FecPacketAction.DuplicateIgnored);
        decoder.Stats.DuplicatePacketsDropped.Should().Be(1);
    }

    [Fact]
    public void XorFec_TwoDroppedPackets_GracefullyReportsUnrecoverableWithoutThrowing()
    {
        // Arrange
        XorFecEncoder encoder = new(blockSize: 4);
        XorFecDecoder decoder = new();

        byte[][] payloads =
        [
            Encoding.UTF8.GetBytes("P0"),
            Encoding.UTF8.GetBytes("P1"),
            Encoding.UTF8.GetBytes("P2"),
            Encoding.UTF8.GetBytes("P3")
        ];

        byte[][] wire = new byte[4][];
        for (int i = 0; i < 4; i++)
        {
            wire[i] = new byte[128];
            encoder.TryEncodeData(payloads[i], wire[i], out int written, out _);
            Array.Resize(ref wire[i], written);
        }

        byte[] parity = new byte[256];
        encoder.TryEmitParity(parity, out int parityLen);

        // Deliver P0 and P3 (P1 and P2 are lost - 2 packet drop burst)
        decoder.TryProcessIncomingPacket(wire[0], out _);
        decoder.TryProcessIncomingPacket(wire[3], out _);

        // Act - Parity arrives
        bool parityProcessed = decoder.TryProcessIncomingPacket(parity.AsSpan(0, parityLen), out FecDecodeResult result);

        // Assert - Unrecoverable, reports gracefully without throwing
        parityProcessed.Should().BeTrue();
        result.Action.Should().Be(FecPacketAction.Unrecoverable);
        decoder.Stats.UnrecoverableBlocks.Should().Be(1);
        decoder.Stats.PacketsRecovered.Should().Be(0);
    }
}
