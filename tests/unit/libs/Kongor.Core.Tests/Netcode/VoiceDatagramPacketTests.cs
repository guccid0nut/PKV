// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ============================================================================
// File: VoiceDatagramPacketTests.cs
// System: ProjectKongor - Voice Subsystem Unit Tests
// Policy: Strict Explicit Typing (NO 'var') - Sub-Millisecond Serialization Tests
// ============================================================================

using System;
using FluentAssertions;
using Kongor.Core.Netcode.Voice;
using Xunit;

namespace Kongor.Core.Tests.Netcode;

public sealed class VoiceDatagramPacketTests
{
    [Fact]
    public void RoundTripSerialization_WithPayload_PreservesAllFields()
    {
        byte[] payload = [0x01, 0x02, 0x03, 0x04, 0xAA, 0xBB, 0xCC, 0xDD];
        VoiceDatagramPacket packet = new VoiceDatagramPacket(
            VoiceChannelType.Party,
            channelId: 1337,
            senderAccountId: 42,
            sequence: 101,
            payload: payload,
            flags: VoicePacketFlags.None);

        byte[] serialized = packet.ToByteArray();
        serialized.Length.Should().Be(VoiceDatagramPacket.HeaderSize + payload.Length);

        bool success = VoiceDatagramPacket.TryParse(serialized, out VoiceDatagramPacket parsed);
        success.Should().BeTrue();
        parsed.Magic.Should().Be(VoiceDatagramPacket.PacketMagic);
        parsed.ChannelType.Should().Be(VoiceChannelType.Party);
        parsed.ChannelId.Should().Be(1337u);
        parsed.SenderAccountId.Should().Be(42u);
        parsed.Sequence.Should().Be((ushort)101);
        parsed.Flags.Should().Be(VoicePacketFlags.None);
        parsed.Payload.ToArray().Should().Equal(payload);
        parsed.Should().Be(packet);
        (parsed == packet).Should().BeTrue();
        (parsed != packet).Should().BeFalse();
    }

    [Theory]
    [InlineData(VoiceChannelType.Party)]
    [InlineData(VoiceChannelType.Team)]
    [InlineData(VoiceChannelType.Lane)]
    [InlineData(VoiceChannelType.Mentor)]
    public void ChannelTypes_ArePreservedThroughSerialization(VoiceChannelType expectedChannel)
    {
        byte[] payload = [0x10, 0x20];
        VoiceDatagramPacket packet = new VoiceDatagramPacket(
            expectedChannel,
            channelId: 9999,
            senderAccountId: 8888,
            sequence: 50,
            payload: payload);

        byte[] serialized = packet.ToByteArray();
        bool success = VoiceDatagramPacket.TryParse(serialized, out VoiceDatagramPacket parsed);

        success.Should().BeTrue();
        parsed.ChannelType.Should().Be(expectedChannel);
    }

    [Fact]
    public void EndOfTransmissionFlag_IsPreserved()
    {
        VoiceDatagramPacket packet = new VoiceDatagramPacket(
            VoiceChannelType.Team,
            channelId: 500,
            senderAccountId: 123,
            sequence: 999,
            payload: ReadOnlyMemory<byte>.Empty,
            flags: VoicePacketFlags.EndOfTransmission);

        byte[] serialized = packet.ToByteArray();
        serialized.Length.Should().Be(VoiceDatagramPacket.HeaderSize);

        bool success = VoiceDatagramPacket.TryParse(serialized, out VoiceDatagramPacket parsed);
        success.Should().BeTrue();
        parsed.Flags.Should().HaveFlag(VoicePacketFlags.EndOfTransmission);
        parsed.Payload.IsEmpty.Should().BeTrue();
    }

    [Fact]
    public void TryParse_WithBufferShorterThanHeader_ReturnsFalse()
    {
        byte[] invalidBuffer = [0x56, 0x00, 0x00, 0x00];
        bool success = VoiceDatagramPacket.TryParse(invalidBuffer, out VoiceDatagramPacket packet);

        success.Should().BeFalse();
        packet.Should().Be(default(VoiceDatagramPacket));
    }

    [Fact]
    public void TryParse_WithWrongMagicByte_ReturnsFalse()
    {
        byte[] buffer = new byte[VoiceDatagramPacket.HeaderSize];
        buffer[0] = 0xFF; // Incorrect magic

        bool success = VoiceDatagramPacket.TryParse(buffer, out VoiceDatagramPacket packet);
        success.Should().BeFalse();
    }

    [Fact]
    public void TryParse_WithTruncatedPayload_ReturnsFalse()
    {
        byte[] payload = [0x01, 0x02, 0x03, 0x04];
        VoiceDatagramPacket packet = new VoiceDatagramPacket(
            VoiceChannelType.Party,
            channelId: 1,
            senderAccountId: 2,
            sequence: 3,
            payload: payload);

        byte[] fullBytes = packet.ToByteArray();
        // Truncate by 2 bytes
        byte[] truncatedBytes = fullBytes.AsSpan(0, fullBytes.Length - 2).ToArray();

        bool success = VoiceDatagramPacket.TryParse(truncatedBytes, out VoiceDatagramPacket parsed);
        success.Should().BeFalse();
    }
}
