// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Text;
using FluentAssertions;
using TRANSMUTANSTEIN.ChatServer.Domain.Core;
using Xunit;

namespace TRANSMUTANSTEIN.ChatServer.Tests.Domain;

public sealed class ChatBufferTests
{
    [Theory]
    [InlineData("Hello, World!")]
    [InlineData("Project Kongor Modernization 2026")]
    [InlineData("Standard ASCII test with 1234567890 and !@#$%^&*()-_=+[]{}|;:',.<>?/`~")]
    [InlineData("A quick brown fox jumps over the lazy dog.")]
    public void WriteString_ReadString_Ascii_RoundTripsAccurately(string input)
    {
        ChatBuffer buffer = new();
        buffer.WriteString(input);
        string result = buffer.ReadString();

        result.Should().Be(input);
        buffer.HasRemainingData().Should().BeFalse();
    }

    [Theory]
    [InlineData("Привет, мир!")]
    [InlineData("Тестирование кириллицы в Project Kongor")]
    [InlineData("Слава героям Newerth — победа близка!")]
    [InlineData("Юникод: абвгдеёжзийклмнопрстуфхцчшщъыьэюя АБВГДЕЁЖЗИЙКЛМНОПРСТУФХЦЧШЩЪЫЬЭЮЯ")]
    public void WriteString_ReadString_Cyrillic_RoundTripsAccurately(string input)
    {
        ChatBuffer buffer = new();
        buffer.WriteString(input);
        string result = buffer.ReadString();

        result.Should().Be(input);
        buffer.HasRemainingData().Should().BeFalse();
    }

    [Theory]
    [InlineData("你好，世界！")]
    [InlineData("超神英雄（HoN）聊天服务器测试")]
    [InlineData("繁體中文與簡體中文混合：這是一個測試用例 123")]
    [InlineData("日文カタカナ・ひらがな：プロジェクト・コンガー (Project Kongor) ⚔️")]
    public void WriteString_ReadString_ChineseAndAsianScripts_RoundTripsAccurately(string input)
    {
        ChatBuffer buffer = new();
        buffer.WriteString(input);
        string result = buffer.ReadString();

        result.Should().Be(input);
        buffer.HasRemainingData().Should().BeFalse();
    }

    [Theory]
    [InlineData("🎮🔥⚔️🚀👑🌟🎉")]
    [InlineData("Player1 🛡️👾 vs Player2 🎯🏆")]
    [InlineData("❤️‍🔥 Multi-byte surrogate pair emojis: 🦸‍♂️🦸‍♀️")]
    [InlineData("Complex sequence: 👨‍👩‍👧‍👦 🏳️‍🌈 🦄 ⚡ 💥 🕹️")]
    public void WriteString_ReadString_EmojisAndSurrogates_RoundTripsAccurately(string input)
    {
        ChatBuffer buffer = new();
        buffer.WriteString(input);
        string result = buffer.ReadString();

        result.Should().Be(input);
        buffer.HasRemainingData().Should().BeFalse();
    }

    [Fact]
    public void WriteString_ReadString_MixedMultilingual_RoundTripsAccurately()
    {
        string mixed = "English / Привет / 你好 / 🎮 Combo 2026 / العربية";
        ChatBuffer buffer = new();
        buffer.WriteString(mixed);
        string result = buffer.ReadString();

        result.Should().Be(mixed);
        buffer.HasRemainingData().Should().BeFalse();
    }

    [Fact]
    public void WriteString_NullAndEmpty_WritesSingleNullTerminator()
    {
        ChatBuffer buffer = new();
        buffer.WriteString(string.Empty);
        buffer.WriteString(null);
        buffer.WriteString([]);

        buffer.Size.Should().Be(3); // 3 null bytes (0x00, 0x00, 0x00)
        buffer.ReadString().Should().Be(string.Empty);
        buffer.ReadString().Should().Be(string.Empty);
        buffer.ReadString().Should().Be(string.Empty);
        buffer.HasRemainingData().Should().BeFalse();
    }

    [Fact]
    public void WriteString_ReadOnlySpan_SlicesDirectlyWithoutAllocation()
    {
        string text = "The quick brown fox jumps over the lazy dog";
        ReadOnlySpan<char> span = text.AsSpan(10, 5); // "brown"

        ChatBuffer buffer = new(256);

        // Warm up JIT and GC counters
        buffer.WriteString(span);
        buffer.Clear();

        long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        buffer.WriteString(span);
        long allocatedAfter = GC.GetAllocatedBytesForCurrentThread();

        long delta = allocatedAfter - allocatedBefore;
        delta.Should().Be(0, "pre-reserved ChatBuffer.WriteString(ReadOnlySpan<char>) must not allocate heap memory");

        buffer.ReadString().Should().Be("brown");
    }

    [Fact]
    public void ChatBuffer_ReadStringSpan_ReturnsExactUtf8SpanWithoutStringAllocation()
    {
        string text = "VectorizedScanTest2026";
        ChatBuffer buffer = new();
        buffer.WriteString(text);

        ReadOnlySpan<byte> span = buffer.ReadStringSpan();
        Encoding.UTF8.GetString(span).Should().Be(text);
        buffer.HasRemainingData().Should().BeFalse();
    }

    [Fact]
    public void ReadString_MissingNullTerminator_ReadsToEndGracefully()
    {
        // Construct raw buffer without trailing 0x00
        byte[] rawUtf8 = Encoding.UTF8.GetBytes("NoTerminatorHere");
        ChatBuffer buffer = new(rawUtf8);

        string result = buffer.ReadString();
        result.Should().Be("NoTerminatorHere");
        buffer.Offset.Should().Be(buffer.Size);
        buffer.HasRemainingData().Should().BeFalse();
    }

    [Fact]
    public void ReadStringSpan_MissingNullTerminator_ReadsToEndGracefully()
    {
        byte[] rawUtf8 = Encoding.UTF8.GetBytes("SpanWithoutTerminator");
        ChatBuffer buffer = new(rawUtf8);

        ReadOnlySpan<byte> span = buffer.ReadStringSpan();
        Encoding.UTF8.GetString(span).Should().Be("SpanWithoutTerminator");
        buffer.Offset.Should().Be(buffer.Size);
        buffer.HasRemainingData().Should().BeFalse();
    }

    [Fact]
    public void WritePrimitives_ZeroAllocations_RoundTripFidelity()
    {
        ChatBuffer buffer = new(512);

        buffer.WriteCommand(0x1234);
        buffer.WriteBool(true);
        buffer.WriteBool(false);
        buffer.WriteInt8(0xAB);
        buffer.WriteInt16(-12345);
        buffer.WriteInt16(short.MinValue);
        buffer.WriteInt16(short.MaxValue);
        buffer.WriteInt32(987654321);
        buffer.WriteInt32(int.MinValue);
        buffer.WriteInt32(int.MaxValue);
        buffer.WriteInt64(1234567890123456789L);
        buffer.WriteInt64(long.MinValue);
        buffer.WriteInt64(long.MaxValue);
        buffer.WriteFloat32(3.14159f);
        buffer.WriteFloat32(-0.00123f);
        buffer.WriteString("Project Kongor Modernized");

        buffer.ReadCommand().Should().Be(0x1234);
        buffer.ReadBool().Should().BeTrue();
        buffer.ReadBool().Should().BeFalse();
        buffer.ReadInt8().Should().Be(0xAB);
        buffer.ReadInt16().Should().Be(-12345);
        buffer.ReadInt16().Should().Be(short.MinValue);
        buffer.ReadInt16().Should().Be(short.MaxValue);
        buffer.ReadInt32().Should().Be(987654321);
        buffer.ReadInt32().Should().Be(int.MinValue);
        buffer.ReadInt32().Should().Be(int.MaxValue);
        buffer.ReadInt64().Should().Be(1234567890123456789L);
        buffer.ReadInt64().Should().Be(long.MinValue);
        buffer.ReadInt64().Should().Be(long.MaxValue);
        buffer.ReadFloat32().Should().BeApproximately(3.14159f, 0.00001f);
        buffer.ReadFloat32().Should().BeApproximately(-0.00123f, 0.00001f);
        buffer.ReadString().Should().Be("Project Kongor Modernized");
        buffer.HasRemainingData().Should().BeFalse();
    }

    [Fact]
    public void ReadPrimitives_ThrowsInvalidDataException_WhenBufferUnderflow()
    {
        ChatBuffer buffer = new();

        buffer.Invoking(b => b.ReadCommand()).Should().Throw<InvalidDataException>();
        buffer.Invoking(b => b.ReadInt8()).Should().Throw<InvalidDataException>();
        buffer.Invoking(b => b.ReadBool()).Should().Throw<InvalidDataException>();
        buffer.Invoking(b => b.ReadInt16()).Should().Throw<InvalidDataException>();
        buffer.Invoking(b => b.ReadInt32()).Should().Throw<InvalidDataException>();
        buffer.Invoking(b => b.ReadInt64()).Should().Throw<InvalidDataException>();
        buffer.Invoking(b => b.ReadFloat32()).Should().Throw<InvalidDataException>();
        buffer.Invoking(b => b.ReadCommandBytes()).Should().Throw<InvalidDataException>();
    }

    [Fact]
    public void ReadBool_InvalidByteValue_ThrowsInvalidDataException()
    {
        ChatBuffer buffer = new();
        buffer.WriteInt8(0x05); // Not 0 and not 1

        buffer.Invoking(b => b.ReadBool()).Should().Throw<InvalidDataException>()
            .WithMessage("*Unable To Read A Boolean Value*");
    }

    [Fact]
    public void ChatBuffer_PeekAndPeekSpan_InspectsDataWithoutAdvancingOffset()
    {
        ChatBuffer buffer = new();
        buffer.WriteInt32(0x12345678);
        buffer.WriteString("PeekTest");

        long initialOffset = buffer.Offset;

        ReadOnlySpan<byte> peekSpan = buffer.PeekSpan(4);
        peekSpan.Length.Should().Be(4);

        byte[] peekArray = buffer.Peek(4);
        peekArray.Should().Equal(peekSpan.ToArray());

        buffer.Offset.Should().Be(initialOffset);

        int intVal = buffer.ReadInt32();
        intVal.Should().Be(0x12345678);
        buffer.ReadString().Should().Be("PeekTest");
    }

    [Fact]
    public void ChatBuffer_PrependBufferSize_PrependsInt16SizeCorrectly()
    {
        ChatBuffer buffer = new();
        buffer.WriteString("DataPayload");
        long initialSize = buffer.Size;

        long prependedCount = buffer.PrependBufferSize();
        prependedCount.Should().Be(2);
        buffer.Size.Should().Be(initialSize + 2);

        short sizeHeader = buffer.ReadInt16();
        sizeHeader.Should().Be((short)initialSize);
        buffer.ReadString().Should().Be("DataPayload");
    }

    [Fact]
    public void TCPBuffer_GeometricGrowth_DoublesCapacityToPreventQuadraticRealloc()
    {
        TCPBuffer buffer = new();
        buffer.Capacity.Should().Be(0);

        // Appending 1 byte should grow capacity to at least 64
        buffer.Append(0xAA);
        buffer.Capacity.Should().BeGreaterOrEqualTo(64);

        long cap1 = buffer.Capacity;
        // Requesting cap1 + 1 should trigger doubling (cap1 * 2)
        buffer.Reserve(cap1 + 1);
        buffer.Capacity.Should().BeGreaterOrEqualTo(cap1 * 2);
    }
}
