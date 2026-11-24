// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Buffers;

namespace ASPIRE.Tests.KONGOR.MasterServer.Tests.Serialisation.PHP;

public sealed class Utf8PhpWriterTests
{
    [Fact]
    public void WriteNull_Formats_Correctly()
    {
        ArrayBufferWriter<byte> bufferWriter = new();
        Utf8PhpWriter writer = new(bufferWriter);
        writer.WriteNull();
        writer.Flush();

        string result = Encoding.UTF8.GetString(bufferWriter.WrittenSpan);
        Assert.Equal("N;", result);
    }

    [Fact]
    public void WriteBool_Formats_True_And_False_Correctly()
    {
        ArrayBufferWriter<byte> bufferWriter = new();
        Utf8PhpWriter writer = new(bufferWriter);
        writer.WriteBool(true);
        writer.WriteBool(false);
        writer.Flush();

        string result = Encoding.UTF8.GetString(bufferWriter.WrittenSpan);
        Assert.Equal("b:1;b:0;", result);
    }

    [Fact]
    public void WriteInt_Formats_Positive_And_Negative_Integers()
    {
        ArrayBufferWriter<byte> bufferWriter = new();
        Utf8PhpWriter writer = new(bufferWriter);
        writer.WriteInt(42);
        writer.WriteInt(-100);
        writer.Flush();

        string result = Encoding.UTF8.GetString(bufferWriter.WrittenSpan);
        Assert.Equal("i:42;i:-100;", result);
    }

    [Fact]
    public void WriteString_Formats_Ascii_And_Multibyte_Utf8()
    {
        ArrayBufferWriter<byte> bufferWriter = new();
        Utf8PhpWriter writer = new(bufferWriter);
        writer.WriteString("hello");
        writer.WriteString("测试");
        writer.Flush();

        string result = Encoding.UTF8.GetString(bufferWriter.WrittenSpan);
        Assert.Equal("s:5:\"hello\";s:6:\"测试\";", result);
    }

    [Fact]
    public void WriteArrayHeaderAndFooter_Formats_Correctly()
    {
        ArrayBufferWriter<byte> bufferWriter = new();
        Utf8PhpWriter writer = new(bufferWriter);
        writer.WriteArrayHeader(2);
        writer.WriteKey("a");
        writer.WriteInt(1);
        writer.WriteKey("b");
        writer.WriteInt(2);
        writer.WriteArrayFooter();
        writer.Flush();

        string result = Encoding.UTF8.GetString(bufferWriter.WrittenSpan);
        Assert.Equal("a:2:{s:1:\"a\";i:1;s:1:\"b\";i:2;}", result);
    }
}
