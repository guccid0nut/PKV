// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Buffers;
using System.Buffers.Binary;
using System.Text;
using FluentAssertions;
using TRANSMUTANSTEIN.ChatServer.CommandProcessors.Channels;
using TRANSMUTANSTEIN.ChatServer.Domain.Communication;
using TRANSMUTANSTEIN.ChatServer.Domain.Core;
using Xunit;

namespace TRANSMUTANSTEIN.ChatServer.Tests.Domain;

public sealed class ChatFramingFirewallTests
{
    #region ChatSession Framing Firewall Tests

    [Fact]
    public void ChatSession_MaxFrameLength_ShouldBe8192()
    {
        ChatSession.MaxFrameLength.Should().Be(8192);
    }

    [Fact]
    public void TryReadPacketFrame_ValidFrame_ExtractsPayloadAndAdvancesBuffer()
    {
        byte[] payload = [0x01, 0x02, 0x03, 0x04, 0x05];
        byte[] packet = new byte[2 + payload.Length];
        BinaryPrimitives.WriteUInt16LittleEndian(packet, (ushort)payload.Length);
        payload.CopyTo(packet.AsSpan(2));

        ReadOnlySequence<byte> buffer = new(packet);

        bool success = ChatSession.TryReadPacketFrame(
            ref buffer,
            out ReadOnlySequence<byte> extractedPayload,
            out bool framingViolation,
            out ushort declaredLength);

        success.Should().BeTrue();
        framingViolation.Should().BeFalse();
        declaredLength.Should().Be((ushort)payload.Length);
        extractedPayload.ToArray().Should().Equal(payload);
        buffer.IsEmpty.Should().BeTrue();
    }

    [Fact]
    public void TryReadPacketFrame_IncompleteFrame_ReturnsFalseWithoutViolation()
    {
        byte[] packet = new byte[10];
        BinaryPrimitives.WriteUInt16LittleEndian(packet, 50); // Declares 50 bytes, but only 8 available

        ReadOnlySequence<byte> buffer = new(packet);

        bool success = ChatSession.TryReadPacketFrame(
            ref buffer,
            out ReadOnlySequence<byte> extractedPayload,
            out bool framingViolation,
            out ushort declaredLength);

        success.Should().BeFalse();
        framingViolation.Should().BeFalse();
        declaredLength.Should().Be(50);
        extractedPayload.IsEmpty.Should().BeTrue();
        buffer.Length.Should().Be(10); // Buffer not advanced
    }

    [Fact]
    public void TryReadPacketFrame_OversizedFrame_FlagsFramingViolation()
    {
        byte[] packet = new byte[10];
        // Exceeds MaxFrameLength (8192)
        BinaryPrimitives.WriteUInt16LittleEndian(packet, 8193);

        ReadOnlySequence<byte> buffer = new(packet);

        bool success = ChatSession.TryReadPacketFrame(
            ref buffer,
            out ReadOnlySequence<byte> extractedPayload,
            out bool framingViolation,
            out ushort declaredLength);

        success.Should().BeFalse();
        framingViolation.Should().BeTrue();
        declaredLength.Should().Be(8193);
        extractedPayload.IsEmpty.Should().BeTrue();
    }

    [Fact]
    public void TryReadPacketFrame_MaximumBoundaryFrame_Succeeds()
    {
        byte[] payload = new byte[8192];
        Array.Fill(payload, (byte)0xAA);

        byte[] packet = new byte[2 + payload.Length];
        BinaryPrimitives.WriteUInt16LittleEndian(packet, (ushort)payload.Length);
        payload.CopyTo(packet.AsSpan(2));

        ReadOnlySequence<byte> buffer = new(packet);

        bool success = ChatSession.TryReadPacketFrame(
            ref buffer,
            out ReadOnlySequence<byte> extractedPayload,
            out bool framingViolation,
            out ushort declaredLength);

        success.Should().BeTrue();
        framingViolation.Should().BeFalse();
        declaredLength.Should().Be(8192);
        extractedPayload.Length.Should().Be(8192);
        buffer.IsEmpty.Should().BeTrue();
    }

    [Fact]
    public void TryReadPacketFrame_BufferUnderTwoBytes_ReturnsFalseWithoutViolation()
    {
        byte[] packet = [0x01];
        ReadOnlySequence<byte> buffer = new(packet);

        bool success = ChatSession.TryReadPacketFrame(
            ref buffer,
            out ReadOnlySequence<byte> extractedPayload,
            out bool framingViolation,
            out ushort declaredLength);

        success.Should().BeFalse();
        framingViolation.Should().BeFalse();
        declaredLength.Should().Be(0);
        extractedPayload.IsEmpty.Should().BeTrue();
    }

    private sealed class SequenceSegment : ReadOnlySequenceSegment<byte>
    {
        public SequenceSegment(ReadOnlyMemory<byte> memory)
        {
            Memory = memory;
        }

        public SequenceSegment Append(ReadOnlyMemory<byte> memory)
        {
            SequenceSegment segment = new(memory)
            {
                RunningIndex = RunningIndex + Memory.Length
            };
            Next = segment;
            return segment;
        }
    }

    [Fact]
    public void TryReadPacketFrame_HeaderSplitAcrossSegments_CorrectlyParses()
    {
        // 2-byte header split: byte 0 in seg1, byte 1 in seg2
        SequenceSegment seg1 = new(new byte[] { 0x05 });
        SequenceSegment seg2 = seg1.Append(new byte[] { 0x00, 0x11, 0x22, 0x33, 0x44, 0x55 });
        ReadOnlySequence<byte> buffer = new(seg1, 0, seg2, seg2.Memory.Length);

        bool success = ChatSession.TryReadPacketFrame(
            ref buffer,
            out ReadOnlySequence<byte> extractedPayload,
            out bool framingViolation,
            out ushort declaredLength);

        success.Should().BeTrue();
        framingViolation.Should().BeFalse();
        declaredLength.Should().Be(5);
        extractedPayload.ToArray().Should().Equal([0x11, 0x22, 0x33, 0x44, 0x55]);
        buffer.IsEmpty.Should().BeTrue();
    }

    #endregion

    #region ChatBuffer String Perimeter Sanitization Tests

    [Fact]
    public void SanitizeIncomingString_StripsControlCharacters_ExceptTabAndNewlines()
    {
        string input = "Hello\u0000\u0001\u0002World\t\r\nTest\u001fDone";
        string sanitized = ChatBuffer.SanitizeIncomingString(input);

        sanitized.Should().Be("HelloWorld\t\r\nTestDone");
    }

    [Fact]
    public void SanitizeIncomingString_CleanString_ReturnsSameInstanceOrValue()
    {
        string input = "Normal Printable English Text 12345!@#$%^&*()";
        string sanitized = ChatBuffer.SanitizeIncomingString(input);

        sanitized.Should().Be(input);
    }

    [Theory]
    [InlineData("مرحبا بك في خادم المحادثة لـ Project Kongor 2026!")]
    [InlineData("プロジェクト・コンガー：新世代チャットサーバーテスト【２０２６】")]
    [InlineData("Привет мир! Проверка протокола чата Project Kongor")]
    [InlineData("Helló Világ! Árvíztűrő tükörfúrógép")]
    [InlineData("Kongor MOBA 🎮 🔥 🏆")]
    public void ChatFramingFirewall_SanitizeIncomingString_PreservesMultilingualAndEmojiText(string input)
    {
        string sanitized = ChatBuffer.SanitizeIncomingString(input);
        sanitized.Should().Be(input);
    }

    [Fact]
    public void ChatBuffer_ReadString_AutomaticallySanitizesControlChars()
    {
        ChatBuffer buffer = new();
        // Manually write string bytes including non-printable control chars and a null terminator
        byte[] raw = Encoding.UTF8.GetBytes("Bad\x07Packet\x1BPayload\0");
        buffer.Append(raw);

        string result = buffer.ReadString();
        result.Should().Be("BadPacketPayload");
    }

    [Fact]
    public void Whisper_Create_Exceeds250Chars_TruncatedTo250()
    {
        string longMessage = new('X', 500);
        Whisper whisper = Whisper.Create(longMessage);

        whisper.Message.Length.Should().Be(250);
        whisper.Message.Should().Be(new string('X', 250));
    }

    [Fact]
    public void SanitizeIncomingString_StripsBiDiOverridesAndLoneSurrogates()
    {
        string malicious = "Text\u202Ereversed\u202C\uD800broken\uDFFFend";
        string sanitized = ChatBuffer.SanitizeIncomingString(malicious);

        sanitized.Should().Be("Textreversedbrokenend");
    }

    #endregion

    #region Channel Sanitization Tests

    [Theory]
    [InlineData("../../etc/passwd", "")]
    [InlineData("..\\windows\\win.ini", "")]
    [InlineData("/general", "")]
    [InlineData("chan/room", "")]
    [InlineData("chan\\room", "")]
    [InlineData("<script>alert(1)</script>", "")]
    [InlineData("channel' OR '1'='1", "")]
    [InlineData("channel\"injection", "")]
    [InlineData("   ", "")]
    [InlineData(null, "")]
    public void SanitizeChannelName_MaliciousOrInvalidNames_ReturnsEmpty(string? rawName, string expected)
    {
        string result = JoinChannel.SanitizeChannelName(rawName);
        result.Should().Be(expected);
    }

    [Fact]
    public void SanitizeChannelName_ValidChannelName_ReturnsTrimmedName()
    {
        string result = JoinChannel.SanitizeChannelName("  ProjectKongor_Lobby  ");
        result.Should().Be("ProjectKongor_Lobby");
    }

    [Fact]
    public void SanitizeChannelName_Exceeds32Chars_TruncatedTo32Chars()
    {
        string longName = new('A', 50);
        string result = JoinChannel.SanitizeChannelName(longName);

        result.Length.Should().Be(32);
        result.Should().Be(new string('A', 32));
    }

    #endregion
}
