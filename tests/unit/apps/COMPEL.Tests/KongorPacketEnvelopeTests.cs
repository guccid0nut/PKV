// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ============================================================================
// File: KongorPacketEnvelopeTests.cs
// System: COMPEL Tests - Modern Authenticated Transport Envelope Unit Tests
// Policy: Strict Explicit Typing (NO 'var')
// ============================================================================

using ASPIRE.Common.Netcode;
using FluentAssertions;
using Xunit;

namespace COMPEL.Tests;

public sealed class KongorPacketEnvelopeTests
{
    [Fact]
    public void KongorPacketHeader_Constants_MatchWireSpecification()
    {
        KongorPacketEnvelope.MagicByte.Should().Be(0x80);
        KongorPacketEnvelope.HeaderSize.Should().Be(21);
        KongorPacketEnvelope.TagSize.Should().Be(16);
        KongorPacketEnvelope.TotalOverhead.Should().Be(37);
    }

    [Fact]
    public void KongorPacketHeader_RoundtripSerialization_PreservesAllFields()
    {
        KongorPacketHeader original = new(
            sessionId: 0x12345678,
            packetSequence: 987654,
            subTickDeltaUs: 12500,
            ackSequence: 987600,
            ackTimestampEchoUs: 1700000000);

        byte[] buffer = new byte[KongorPacketEnvelope.HeaderSize];

        bool writeSuccess = KongorPacketEnvelope.TryWriteHeader(buffer.AsSpan(), in original);
        writeSuccess.Should().BeTrue();

        bool readSuccess = KongorPacketEnvelope.TryReadHeader(buffer.AsSpan(), out KongorPacketHeader deserialized);
        readSuccess.Should().BeTrue();

        deserialized.Magic.Should().Be(0x80);
        deserialized.SessionId.Should().Be(0x12345678);
        deserialized.PacketSequence.Should().Be(987654);
        deserialized.SubTickDeltaUs.Should().Be(12500);
        deserialized.AckSequence.Should().Be(987600);
        deserialized.AckTimestampEchoUs.Should().Be(1700000000);
        deserialized.Should().Be(original);
    }

    [Fact]
    public void IsModernEnvelope_ValidatesLengthAndMagicBit()
    {
        byte[] valid = new byte[KongorPacketEnvelope.TotalOverhead + 10];
        valid[0] = 0x80;

        byte[] badMagic = new byte[KongorPacketEnvelope.TotalOverhead + 10];
        badMagic[0] = 0x40; // Legacy K2 proxy control packet

        byte[] tooShort = new byte[KongorPacketEnvelope.TotalOverhead - 1];
        tooShort[0] = 0x80;

        byte[] fecPacket = new byte[KongorPacketEnvelope.TotalOverhead + 10];
        fecPacket[0] = FecPacketHeader.FecMagic; // 0xFE must not be misclassified as modern envelope

        KongorPacketEnvelope.IsModernEnvelope(valid).Should().BeTrue();
        KongorPacketEnvelope.IsModernEnvelope(badMagic).Should().BeFalse();
        KongorPacketEnvelope.IsModernEnvelope(tooShort).Should().BeFalse();
        KongorPacketEnvelope.IsModernEnvelope(fecPacket).Should().BeFalse();
        KongorPacketEnvelope.IsModernEnvelope(ReadOnlySpan<byte>.Empty).Should().BeFalse();
    }

    [Fact]
    public void TryWrapAndTryUnwrap_Roundtrip_SeparatesHeaderPayloadAndTag()
    {
        KongorPacketHeader header = new(
            sessionId: 0xAABBCCDD,
            packetSequence: 42,
            subTickDeltaUs: 5000,
            ackSequence: 40,
            ackTimestampEchoUs: 99999);

        byte[] ciphertext = [0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08];
        byte[] tag = new byte[KongorPacketEnvelope.TagSize];
        for (int i = 0; i < tag.Length; i++)
        {
            tag[i] = (byte)(0xAA + i);
        }

        byte[] envelope = new byte[KongorPacketEnvelope.TotalOverhead + ciphertext.Length];

        bool wrapSuccess = KongorPacketEnvelope.TryWrap(
            envelope.AsSpan(),
            in header,
            ciphertext.AsSpan(),
            tag.AsSpan(),
            out int bytesWritten);

        wrapSuccess.Should().BeTrue();
        bytesWritten.Should().Be(envelope.Length);

        bool unwrapSuccess = KongorPacketEnvelope.TryUnwrap(
            envelope.AsSpan(),
            out KongorPacketHeader unwrappedHeader,
            out ReadOnlySpan<byte> unwrappedCiphertext,
            out ReadOnlySpan<byte> unwrappedTag);

        unwrapSuccess.Should().BeTrue();
        unwrappedHeader.Should().Be(header);
        unwrappedCiphertext.SequenceEqual(ciphertext).Should().BeTrue();
        unwrappedTag.SequenceEqual(tag).Should().BeTrue();
    }

    [Fact]
    public void TryWrap_WithInsufficientBuffer_ReturnsFalseWithoutThrowing()
    {
        KongorPacketHeader header = new(1, 1, 0);
        byte[] ciphertext = new byte[100];
        byte[] tag = new byte[KongorPacketEnvelope.TagSize];
        byte[] smallBuffer = new byte[50]; // Insufficient for overhead + ciphertext

        bool success = KongorPacketEnvelope.TryWrap(
            smallBuffer.AsSpan(),
            in header,
            ciphertext.AsSpan(),
            tag.AsSpan(),
            out int written);

        success.Should().BeFalse();
        written.Should().Be(0);
    }
}
