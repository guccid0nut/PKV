// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

namespace ASPIRE.Tests.Serialization;

public class PhpDeserializerSecurityTests
{
    private static string GenerateNestedArrayPayload(int depth)
    {
        // Generates nested arrays: a:1:{i:0;a:1:{i:0;...i:42;...}}}
        StringBuilder sb = new();
        for (int i = 0; i < depth; i++) sb.Append("a:1:{i:0;");
        sb.Append("i:42;");
        for (int i = 0; i < depth; i++) sb.Append('}');
        return sb.ToString();
    }

    [Fact]
    public void Deserialize_RecursionDepth32_String_Succeeds()
    {
        string payload = GenerateNestedArrayPayload(32);
        object? result = PhpDeserializer.Deserialize(payload);

        Assert.NotNull(result);
        Assert.IsType<PhpHashtable>(result);
    }

    [Fact]
    public void Deserialize_RecursionDepth32_Bytes_Succeeds()
    {
        string payload = GenerateNestedArrayPayload(32);
        byte[] bytes = Encoding.UTF8.GetBytes(payload);
        object? result = PhpDeserializer.Deserialize((ReadOnlySpan<byte>)bytes);

        Assert.NotNull(result);
        Assert.IsType<PhpHashtable>(result);
    }

    [Fact]
    public void Deserialize_RecursionDepth33_String_ThrowsFormatException()
    {
        string payload = GenerateNestedArrayPayload(33);
        FormatException ex = Assert.Throws<FormatException>(() => { PhpDeserializer.Deserialize(payload); });

        Assert.Contains("Exceeded maximum recursion depth of 32", ex.Message);
    }

    [Fact]
    public void Deserialize_RecursionDepth33_Bytes_ThrowsFormatException()
    {
        string payload = GenerateNestedArrayPayload(33);
        byte[] bytes = Encoding.UTF8.GetBytes(payload);
        FormatException ex = Assert.Throws<FormatException>(() =>
        {
            PhpDeserializer.Deserialize((ReadOnlySpan<byte>)bytes);
        });

        Assert.Contains("Exceeded maximum recursion depth of 32", ex.Message);
    }

    [Fact]
    public void Deserialize_DeeplyNestedPayload_String_ThrowsFormatExceptionWithoutStackOverflow()
    {
        // 500 levels of nested arrays
        string payload = GenerateNestedArrayPayload(500);
        Assert.Throws<FormatException>(() => { PhpDeserializer.Deserialize(payload); });
    }

    [Fact]
    public void Deserialize_DeeplyNestedPayload_Bytes_ThrowsFormatExceptionWithoutStackOverflow()
    {
        string payload = GenerateNestedArrayPayload(500);
        byte[] bytes = Encoding.UTF8.GetBytes(payload);
        Assert.Throws<FormatException>(() => { PhpDeserializer.Deserialize((ReadOnlySpan<byte>)bytes); });
    }

    [Theory]
    [InlineData("s:-5:\"hello\";")]
    [InlineData("s:-1:\"\";")]
    [InlineData("s:-99999999:\"test\";")]
    public void Deserialize_NegativeStringLength_ReturnsNullSafely(string payload)
    {
        object? resultChar = PhpDeserializer.Deserialize(payload);
        Assert.Null(resultChar);

        byte[] bytes = Encoding.UTF8.GetBytes(payload);
        object? resultByte = PhpDeserializer.Deserialize((ReadOnlySpan<byte>)bytes);
        Assert.Null(resultByte);
    }

    [Fact]
    public void Deserialize_MassiveStringLength_ReturnsNullSafely()
    {
        string payload = "s:2000000000:\"abc\";";
        object? resultChar = PhpDeserializer.Deserialize(payload);
        Assert.Null(resultChar);

        byte[] bytes = Encoding.UTF8.GetBytes(payload);
        object? resultByte = PhpDeserializer.Deserialize((ReadOnlySpan<byte>)bytes);
        Assert.Null(resultByte);
    }

    [Theory]
    [InlineData("a:-5:{}")]
    [InlineData("a:-1:{}")]
    [InlineData("a:-2147483648:{}")]
    public void Deserialize_NegativeArrayCount_ReturnsEmptyTableSafely(string payload)
    {
        object? resultChar = PhpDeserializer.Deserialize(payload);
        Assert.NotNull(resultChar);
        PhpHashtable tableChar = Assert.IsType<PhpHashtable>(resultChar);
        Assert.Empty(tableChar);

        byte[] bytes = Encoding.UTF8.GetBytes(payload);
        object? resultByte = PhpDeserializer.Deserialize((ReadOnlySpan<byte>)bytes);
        Assert.NotNull(resultByte);
        PhpHashtable tableByte = Assert.IsType<PhpHashtable>(resultByte);
        Assert.Empty(tableByte);
    }

    [Fact]
    public void Deserialize_MassiveArrayCount_ClampsCapacityWithoutOom()
    {
        string payload = "a:2000000000:{}";
        object? resultChar = PhpDeserializer.Deserialize(payload);
        Assert.NotNull(resultChar);
        PhpHashtable tableChar = Assert.IsType<PhpHashtable>(resultChar);
        Assert.Empty(tableChar);

        byte[] bytes = Encoding.UTF8.GetBytes(payload);
        object? resultByte = PhpDeserializer.Deserialize((ReadOnlySpan<byte>)bytes);
        Assert.NotNull(resultByte);
        PhpHashtable tableByte = Assert.IsType<PhpHashtable>(resultByte);
        Assert.Empty(tableByte);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("b")]
    [InlineData("b:")]
    [InlineData("b:1")]
    [InlineData("b:0")]
    [InlineData("i")]
    [InlineData("i:")]
    [InlineData("i:notanumber;")]
    [InlineData("d")]
    [InlineData("d:")]
    [InlineData("d:invalid;")]
    [InlineData("s")]
    [InlineData("s:")]
    [InlineData("s:5")]
    [InlineData("s:5:")]
    [InlineData("s:5:\"")]
    [InlineData("s:10:\"short\"")]
    [InlineData("a")]
    [InlineData("a:")]
    [InlineData("a:2")]
    [InlineData("a:2:")]
    [InlineData("a:2:{")]
    [InlineData("a:2:{s:1:\"k\"")]
    [InlineData("N")]
    [InlineData("N;")]
    [InlineData("?unknown")]
    public void Deserialize_TruncatedAndMalformedInputs_DoesNotThrowUnhandledExceptions(string payload)
    {
        // Must not throw unhandled exceptions like IndexOutOfRangeException, ArgumentOutOfRangeException, etc.
        object? resultChar = PhpDeserializer.Deserialize(payload);

        byte[] bytes = Encoding.UTF8.GetBytes(payload);
        object? resultByte = PhpDeserializer.Deserialize((ReadOnlySpan<byte>)bytes);
    }
}
