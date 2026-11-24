// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Text;
using FluentAssertions;
using TRANSMUTANSTEIN.ChatServer.Domain.Core;
using Xunit;

namespace TRANSMUTANSTEIN.ChatServer.Tests.Domain;

public sealed class ChatBufferAdversarialTests
{
    #region Multilingual Fuzzing & Complex UTF-8 Scripts

    [Theory]
    [InlineData("مرحبا بك في خادم المحادثة لـ Project Kongor 2026!")]
    [InlineData("اللغة العربية مع الأرقام 12345 والرموز #$%^&*()")]
    [InlineData("النص العربي ثنائي الاتجاه: Arabic (العربية) and English mixed.")]
    public void WriteString_ReadString_Arabic_RoundTripsWithPerfectFidelity(string input)
    {
        ChatBuffer buffer = new();
        buffer.WriteString(input);
        string result = buffer.ReadString();

        result.Should().Be(input);
        buffer.HasRemainingData().Should().BeFalse();
    }

    [Theory]
    [InlineData("שלום עולם! בדיקת פרוטוקול גרסה 2.5 בשרת")]
    [InlineData("עברית עם מספרים 12345 וטקסט אנגלי: Hebrew with English")]
    [InlineData("טקסט RTL מורכב עם סימני פיסוק: !@#$%^&*() - פרויקט קונגור")]
    public void WriteString_ReadString_HebrewRtl_RoundTripsWithPerfectFidelity(string input)
    {
        ChatBuffer buffer = new();
        buffer.WriteString(input);
        string result = buffer.ReadString();

        result.Should().Be(input);
        buffer.HasRemainingData().Should().BeFalse();
    }

    [Theory]
    [InlineData("नमस्ते दुनिया! परियोजना कोंगोर चैट सर्वर परीक्षण 2026")]
    [InlineData("हिन्दी देवनागरी संयुक्ताक्षर: क्ष, त्र, ज्ञ, श्र, द्ध, ष्ट्र")]
    [InlineData("देवनागरी मात्राएँ और स्वर: अ आ इ ई उ ऊ ऋ ए ऐ ओ औ अं अः क का कि की कु कू कृ")]
    public void WriteString_ReadString_HindiDevanagari_RoundTripsWithPerfectFidelity(string input)
    {
        ChatBuffer buffer = new();
        buffer.WriteString(input);
        string result = buffer.ReadString();

        result.Should().Be(input);
        buffer.HasRemainingData().Should().BeFalse();
    }

    [Theory]
    [InlineData("プロジェクト・コンガー：新世代チャットサーバーテスト【２０２６】")]
    [InlineData("漢字・ひらがな・カタカナ混合：神羅万象、初音ミク、戦闘開始！")]
    [InlineData("半角ｶﾀｶﾅ＆全角英数：ｱｲｳｴｵ ＡＢＣ １２３")]
    public void WriteString_ReadString_JapaneseKanjiKana_RoundTripsWithPerfectFidelity(string input)
    {
        ChatBuffer buffer = new();
        buffer.WriteString(input);
        string result = buffer.ReadString();

        result.Should().Be(input);
        buffer.HasRemainingData().Should().BeFalse();
    }

    [Theory]
    [InlineData("🦸‍♂️ Superhero Male ZWJ")]
    [InlineData("🦸‍♀️ Superhero Female ZWJ")]
    [InlineData("👨‍👩‍👧‍👦 Family ZWJ 4-person Sequence")]
    [InlineData("🏳️‍🌈 Rainbow Flag ZWJ Sequence")]
    [InlineData("🧑🏽‍💻 Technologist with Medium Skin Tone and ZWJ")]
    [InlineData("👩‍🔬 Woman Scientist ZWJ")]
    [InlineData("🔥⚡💥🛡️🎯🏆🎮👾 Multibyte Symbols")]
    public void WriteString_ReadString_SurrogatePairAndZwjEmojis_RoundTripsWithPerfectFidelity(string input)
    {
        ChatBuffer buffer = new();
        buffer.WriteString(input);
        string result = buffer.ReadString();

        result.Should().Be(input);
        buffer.HasRemainingData().Should().BeFalse();
    }

    [Fact]
    public void WriteString_ReadString_CombiningDiacriticsAndZalgo_RoundTripsWithPerfectFidelity()
    {
        // Decomposed combining characters and stacked diacritics
        string combining = "e\u0301\u0300\u0302\u0303\u0304 a\u0305\u0306\u0307\u0308 o\u0309\u030A\u030B\u030C";
        string zalgo = "T̶e̷s̶t̷ ̷Z̷a̷l̶g̷o̶ ̷S̷t̷r̷e̷s̶s̷ ̶2̶0̶2̶6̶";

        ChatBuffer buffer = new();
        buffer.WriteString(combining);
        buffer.WriteString(zalgo);

        buffer.ReadString().Should().Be(combining);
        buffer.ReadString().Should().Be(zalgo);
        buffer.HasRemainingData().Should().BeFalse();
    }

    [Fact]
    public void WriteString_ReadString_ComplexBidirectionalMixedMultilingual_RoundTripsWithPerfectFidelity()
    {
        string bidi =
            "EN: Hello | AR: مرحباً | HE: שלום | HI: नमस्ते | ZH: 你好 | JA: こんにちは | RU: Привет | EMOJI: 🦸‍♂️🔥";
        ChatBuffer buffer = new();
        buffer.WriteString(bidi);

        string result = buffer.ReadString();
        result.Should().Be(bidi);
        buffer.HasRemainingData().Should().BeFalse();
    }

    #endregion

    #region Boundary & Stress Conditions

    [Fact]
    public void WriteString_NullString_WritesSingleNullTerminator_AndReadsEmpty()
    {
        ChatBuffer buffer = new();
        buffer.WriteString(null);

        buffer.Size.Should().Be(1);
        buffer.Data[0].Should().Be(0);

        string result = buffer.ReadString();
        result.Should().BeEmpty();
        buffer.HasRemainingData().Should().BeFalse();
    }

    [Fact]
    public void WriteString_EmptySpan_WritesSingleNullTerminator_AndReadsEmpty()
    {
        ChatBuffer buffer = new();
        buffer.WriteString([]);

        buffer.Size.Should().Be(1);
        buffer.Data[0].Should().Be(0);

        string result = buffer.ReadString();
        result.Should().BeEmpty();
        buffer.HasRemainingData().Should().BeFalse();
    }

    [Fact]
    public void WriteString_MultipleSequentialEmptyStrings_WritesConsecutiveNulls()
    {
        ChatBuffer buffer = new();
        for (int i = 0; i < 10; i++) buffer.WriteString(string.Empty);

        buffer.Size.Should().Be(10);
        buffer.Data.Take(10).Should().AllBeEquivalentTo((byte)0);

        for (int i = 0; i < 10; i++) buffer.ReadString().Should().BeEmpty();

        buffer.HasRemainingData().Should().BeFalse();
    }

    [Fact]
    public void ReadString_OnEmptyBuffer_ReturnsEmptyStringWithoutAdvancingOffset()
    {
        ChatBuffer buffer = new();
        buffer.ReadString().Should().BeEmpty();
        buffer.ReadStringSpan().Length.Should().Be(0);
        buffer.Offset.Should().Be(0);
        buffer.HasRemainingData().Should().BeFalse();
    }

    [Fact]
    public void ZeroByteReads_PeekSpan_Peek_ExtractString_BehaveCorrectly()
    {
        ChatBuffer buffer = new();
        buffer.WriteString("Testing0ByteReads");

        buffer.PeekSpan(0).Length.Should().Be(0);
        buffer.Peek(0).Should().BeEmpty();
        buffer.ExtractString(0, 0).Should().BeEmpty();
        buffer.HasRemainingData(0).Should().BeTrue();

        buffer.ReadString().Should().Be("Testing0ByteReads");
        buffer.HasRemainingData().Should().BeFalse();
        buffer.HasRemainingData(0).Should().BeTrue();
    }

    [Fact]
    public void ReadString_MissingNullTerminator_ReadsEntireBufferGracefully()
    {
        byte[] unterminatedBytes = Encoding.UTF8.GetBytes("NoNullTerminatorAtTheEnd");
        ChatBuffer buffer = new(unterminatedBytes);

        buffer.Offset.Should().Be(0);
        buffer.Size.Should().Be(unterminatedBytes.Length);

        string result = buffer.ReadString();
        result.Should().Be("NoNullTerminatorAtTheEnd");
        buffer.Offset.Should().Be(buffer.Size);
        buffer.HasRemainingData().Should().BeFalse();

        // Subsequent read on exhausted buffer returns string.Empty
        buffer.ReadString().Should().BeEmpty();
    }

    [Fact]
    public void ReadStringSpan_MissingNullTerminator_ReadsEntireSpanGracefully()
    {
        byte[] unterminatedBytes = Encoding.UTF8.GetBytes("SpanPayloadWithoutZero");
        ChatBuffer buffer = new(unterminatedBytes);

        ReadOnlySpan<byte> span = buffer.ReadStringSpan();
        Encoding.UTF8.GetString(span).Should().Be("SpanPayloadWithoutZero");
        buffer.Offset.Should().Be(buffer.Size);
        buffer.HasRemainingData().Should().BeFalse();

        // Subsequent read returns empty span
        buffer.ReadStringSpan().Length.Should().Be(0);
    }

    [Fact]
    public void MassiveString_16KbAsciiPacket_RoundTripsAccurately()
    {
        // 16KB string (16384 characters)
        string massive = new('K', 16384);
        ChatBuffer buffer = new();
        buffer.WriteString(massive);

        buffer.Size.Should().Be(16385); // 16384 + 1 null terminator
        string result = buffer.ReadString();

        result.Length.Should().Be(16384);
        result.Should().Be(massive);
        buffer.HasRemainingData().Should().BeFalse();
    }

    [Fact]
    public void MassiveString_16KbMultibyteUtf8_RoundTripsAccurately()
    {
        // 4096 4-byte emojis = 16384 UTF-8 bytes
        StringBuilder sb = new();
        for (int i = 0; i < 4096; i++) sb.Append("🦸");
        string massiveEmoji = sb.ToString();

        ChatBuffer buffer = new();
        buffer.WriteString(massiveEmoji);

        buffer.Size.Should().Be(16384 + 1); // 16384 bytes + 1 null terminator
        string result = buffer.ReadString();

        result.Should().Be(massiveEmoji);
        buffer.HasRemainingData().Should().BeFalse();
    }

    [Fact]
    public void ConsecutiveWrites_TriggerGeometricGrowth_WithoutDataCorruption()
    {
        ChatBuffer buffer = new(); // capacity = 0
        buffer.Capacity.Should().Be(0);

        List<string> writtenStrings = [];

        // Write sequentially growing payloads to force multiple geometric reallocations
        int[] sizes = [1, 10, 50, 200, 1000, 4000, 16000];
        foreach (int size in sizes)
        {
            string payload = new('A', size);
            writtenStrings.Add(payload);
            buffer.WriteString(payload);
        }

        buffer.Capacity.Should().BeGreaterOrEqualTo(buffer.Size);

        // Read all strings back in FIFO order and verify integrity
        foreach (string expected in writtenStrings)
        {
            string read = buffer.ReadString();
            read.Should().Be(expected);
        }

        buffer.HasRemainingData().Should().BeFalse();
    }

    #endregion

    #region Zero-Allocation Verification

    [Fact]
    public void WriteString_ReadOnlySpan_PreAllocatedBuffer_HasZeroAllocations()
    {
        string source = "ZeroAllocationHotpathVerification2026";
        ReadOnlySpan<char> span = source.AsSpan();

        ChatBuffer buffer = new(4096);

        // Warm up JIT and thread local caches
        buffer.WriteString(span);
        buffer.Clear();

        long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        buffer.WriteString(span);
        long allocatedAfter = GC.GetAllocatedBytesForCurrentThread();

        long delta = allocatedAfter - allocatedBefore;
        delta.Should().Be(0,
            "ChatBuffer.WriteString(ReadOnlySpan<char>) with pre-allocated buffer MUST NOT allocate heap memory");

        buffer.ReadString().Should().Be(source);
    }

    [Fact]
    public void ReadStringSpan_PreAllocatedBuffer_HasZeroAllocations()
    {
        ChatBuffer buffer = new(4096);
        buffer.WriteString("ZeroAllocReadSpanTest");

        // Warm up JIT
        ReadOnlySpan<byte> warmSpan = buffer.ReadStringSpan();
        buffer.Shift(-buffer.Offset); // Reset offset to 0

        long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        ReadOnlySpan<byte> span = buffer.ReadStringSpan();
        long allocatedAfter = GC.GetAllocatedBytesForCurrentThread();

        long delta = allocatedAfter - allocatedBefore;
        delta.Should().Be(0, "ChatBuffer.ReadStringSpan() MUST NOT allocate heap memory");
        Encoding.UTF8.GetString(span).Should().Be("ZeroAllocReadSpanTest");
    }

    [Fact]
    public void TCPBuffer_AppendReadOnlySpanChar_PreAllocated_HasZeroAllocations()
    {
        TCPBuffer buffer = new(4096);
        string text = "TCPBufferZeroAllocAppend";
        ReadOnlySpan<char> span = text.AsSpan();

        // Warm up JIT
        buffer.Append(span);
        buffer.Clear();

        long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        buffer.Append(span);
        long allocatedAfter = GC.GetAllocatedBytesForCurrentThread();

        long delta = allocatedAfter - allocatedBefore;
        delta.Should().Be(0,
            "TCPBuffer.Append(ReadOnlySpan<char>) with pre-allocated buffer MUST NOT allocate heap memory");
        buffer.ExtractString(0, buffer.Size).Should().Be(text);
    }

    [Fact]
    public void TCPBuffer_AppendReadOnlySpanByte_PreAllocated_HasZeroAllocations()
    {
        TCPBuffer buffer = new(4096);
        byte[] rawBytes = [1, 2, 3, 4, 5, 6, 7, 8];
        ReadOnlySpan<byte> span = rawBytes.AsSpan();

        // Warm up JIT
        buffer.Append(span);
        buffer.Clear();

        long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        buffer.Append(span);
        long allocatedAfter = GC.GetAllocatedBytesForCurrentThread();

        long delta = allocatedAfter - allocatedBefore;
        delta.Should().Be(0,
            "TCPBuffer.Append(ReadOnlySpan<byte>) with pre-allocated buffer MUST NOT allocate heap memory");
    }

    [Fact]
    public void PrimitiveWrites_PreAllocatedBuffer_HasZeroAllocations()
    {
        ChatBuffer buffer = new(4096);

        // Warm up JIT
        buffer.WriteCommand(0x1000);
        buffer.WriteInt8(0x01);
        buffer.WriteInt16(123);
        buffer.WriteInt32(456);
        buffer.WriteInt64(789L);
        buffer.WriteFloat32(1.0f);
        buffer.WriteBool(true);
        buffer.Clear();

        long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        buffer.WriteCommand(0x1000);
        buffer.WriteInt8(0x01);
        buffer.WriteInt16(123);
        buffer.WriteInt32(456);
        buffer.WriteInt64(789L);
        buffer.WriteFloat32(1.0f);
        buffer.WriteBool(true);
        long allocatedAfter = GC.GetAllocatedBytesForCurrentThread();

        long delta = allocatedAfter - allocatedBefore;
        delta.Should().Be(0, "Binary primitive writes into pre-allocated ChatBuffer MUST NOT allocate heap memory");
    }

    #endregion
}
