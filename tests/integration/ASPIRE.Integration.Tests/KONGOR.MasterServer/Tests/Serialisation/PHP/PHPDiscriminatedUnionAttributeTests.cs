// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using OneOf;

namespace ASPIRE.Tests.KONGOR.MasterServer.Tests.Serialisation.PHP;

public class PHPDiscriminatedUnionAttributeTests
{
    [Fact]
    public void Serialize_WithNullValue_SerializesNull()
    {
        PHPDiscriminatedUnionAttribute attribute = new();
        string result = attribute.Serialize("testKey", null);

        Assert.Equal("s:7:\"testKey\";N;", result);
    }

    [Fact]
    public void Serialize_WithOneOfValue_UnwrapsAndSerializes()
    {
        PHPDiscriminatedUnionAttribute attribute = new();
        OneOf<int, string> val = "testValue";
        string result = attribute.Serialize("testKey", val);

        Assert.Equal("s:7:\"testKey\";s:9:\"testValue\";", result);
    }

    [Fact]
    public void Serialize_WithOneOfIntValue_UnwrapsAndSerializes()
    {
        PHPDiscriminatedUnionAttribute attribute = new();
        OneOf<int, string> val = 123;
        string result = attribute.Serialize("testKey", val);

        Assert.Equal("s:7:\"testKey\";i:123;", result);
    }
}
