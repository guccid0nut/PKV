// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

namespace ASPIRE.Tests.Serialization;

public class PhpDeserializerBytesTests
{
    [Fact]
    public void Deserialize_Null_ReturnsNull()
    {
        ReadOnlySpan<byte> bytes = "N;"u8;
        object? result = PhpDeserializer.Deserialize(bytes);
        Assert.Null(result);
    }

    [Fact]
    public void Deserialize_Bool_ReturnsBool()
    {
        ReadOnlySpan<byte> trueBytes = "b:1;"u8;
        object? trueResult = PhpDeserializer.Deserialize(trueBytes);
        Assert.Equal(true, trueResult);

        ReadOnlySpan<byte> falseBytes = "b:0;"u8;
        object? falseResult = PhpDeserializer.Deserialize(falseBytes);
        Assert.Equal(false, falseResult);
    }

    [Fact]
    public void Deserialize_Integer_ReturnsInt()
    {
        ReadOnlySpan<byte> bytes = "i:42;"u8;
        object? result = PhpDeserializer.Deserialize(bytes);
        Assert.Equal(42, result);
    }

    [Fact]
    public void Deserialize_Double_ReturnsDouble()
    {
        ReadOnlySpan<byte> bytes = "d:3.14159;"u8;
        object? result = PhpDeserializer.Deserialize(bytes);
        Assert.Equal(3.14159, result);
    }

    [Fact]
    public void Deserialize_String_ReturnsString()
    {
        ReadOnlySpan<byte> bytes = "s:11:\"Hello World\";"u8;
        object? result = PhpDeserializer.Deserialize(bytes);
        Assert.Equal("Hello World", result);
    }

    [Fact]
    public void Deserialize_Array_ReturnsPhpHashtable()
    {
        ReadOnlySpan<byte> bytes = "a:2:{s:4:\"name\";s:4:\"Test\";s:2:\"id\";i:100;}"u8;
        object? result = PhpDeserializer.Deserialize(bytes);
        Assert.IsType<PhpHashtable>(result);

        PhpHashtable table = (PhpHashtable)result;
        Assert.Equal("Test", table["name"]);
        Assert.Equal(100, table["id"]);
    }
}
