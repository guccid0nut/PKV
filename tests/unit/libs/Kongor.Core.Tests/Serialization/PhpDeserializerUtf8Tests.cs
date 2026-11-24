// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Text;
using ASPIRE.Common.Serialization.PHP;
using FluentAssertions;
using Xunit;

namespace Kongor.Core.Tests.Serialization;

public sealed class PhpDeserializerUtf8Tests
{
    [Fact]
    public void Deserialize_CyrillicMultiByteString_ParsesExactCharacters()
    {
        string text = "Привет";
        int byteCount = Encoding.UTF8.GetByteCount(text);
        byteCount.Should().Be(12);

        string phpPayload = $"s:{byteCount}:\"{text}\";";
        object? result = PhpDeserializer.Deserialize(phpPayload);

        result.Should().BeOfType<string>().Which.Should().Be(text);
    }

    [Fact]
    public void Deserialize_ChineseMultiByteString_ParsesExactCharacters()
    {
        string text = "世界";
        int byteCount = Encoding.UTF8.GetByteCount(text);
        byteCount.Should().Be(6);

        string phpPayload = $"s:{byteCount}:\"{text}\";";
        object? result = PhpDeserializer.Deserialize(phpPayload);

        result.Should().BeOfType<string>().Which.Should().Be(text);
    }

    [Fact]
    public void Deserialize_EmojiSurrogatePairString_ParsesExactCharacters()
    {
        string text = "Hi 🎮";
        int byteCount = Encoding.UTF8.GetByteCount(text);
        byteCount.Should().Be(7); // 'H'(1) + 'i'(1) + ' '(1) + 🎮(4) = 7

        string phpPayload = $"s:{byteCount}:\"{text}\";";
        object? result = PhpDeserializer.Deserialize(phpPayload);

        result.Should().BeOfType<string>().Which.Should().Be(text);
    }

    [Fact]
    public void Deserialize_AssociativeArrayWithMultiByteKeysAndValues_MaintainsTokenAlignment()
    {
        string key1 = "世界";
        string val1 = "Привет";
        string key2 = "status";
        string val2 = "Online 🎮";

        string phpPayload = $"a:2:{{s:{Encoding.UTF8.GetByteCount(key1)}:\"{key1}\";s:{Encoding.UTF8.GetByteCount(val1)}:\"{val1}\";s:{Encoding.UTF8.GetByteCount(key2)}:\"{key2}\";s:{Encoding.UTF8.GetByteCount(val2)}:\"{val2}\";}}";

        object? result = PhpDeserializer.Deserialize(phpPayload);

        result.Should().BeOfType<PhpHashtable>();
        var dict = (PhpHashtable)result!;

        dict[key1].Should().Be(val1);
        dict[key2].Should().Be(val2);
    }

    [Fact]
    public void Deserialize_Utf8ByteSpan_MatchesStringOverloadForMultiBytePayload()
    {
        string text = "Newerth 英雄";
        string phpPayload = $"s:{Encoding.UTF8.GetByteCount(text)}:\"{text}\";";

        byte[] utf8Bytes = Encoding.UTF8.GetBytes(phpPayload);

        object? fromString = PhpDeserializer.Deserialize(phpPayload);
        object? fromBytes = PhpDeserializer.Deserialize(utf8Bytes.AsSpan());

        fromString.Should().Be(text);
        fromBytes.Should().Be(text);
    }
}
