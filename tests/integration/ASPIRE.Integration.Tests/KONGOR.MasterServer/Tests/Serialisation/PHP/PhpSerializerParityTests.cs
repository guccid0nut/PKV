// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Collections;

namespace ASPIRE.Tests.KONGOR.MasterServer.Tests.Serialisation.PHP;

public sealed class PhpSerializerParityTests
{
    [Fact]
    public void Primitive_Values_Match_Legacy_Output()
    {
        Assert.Equal(PhpSerialization.Serialize(123), PhpSerializer.SerializeToString(123));
        Assert.Equal(PhpSerialization.Serialize("test"), PhpSerializer.SerializeToString("test"));
        Assert.Equal(PhpSerialization.Serialize(true), PhpSerializer.SerializeToString(true));
        Assert.Equal(PhpSerialization.Serialize(null), PhpSerializer.SerializeToString<object>(null));
    }

    [Fact]
    public void Dictionary_IntKeys_Match_Legacy_Output()
    {
        Dictionary<int, object> dict = new() { { 0, "first" }, { 1, "second" } };

        string legacy = PhpSerialization.Serialize(dict);
        string newSerializer = PhpSerializer.SerializeToString(dict);

        Assert.Equal(legacy, newSerializer);
    }

    [Fact]
    public void List_Matches_Legacy_Output()
    {
        List<string> list = ["one", "two", "three"];

        string legacy = PhpSerialization.Serialize(list);
        string newSerializer = PhpSerializer.SerializeToString(list);

        Assert.Equal(legacy, newSerializer);
    }

    [Fact]
    public void Hashtable_Matches_Legacy_Output()
    {
        Hashtable ht = new() { ["key1"] = "val1", ["key2"] = 100 };

        string legacy = PhpSerialization.Serialize(ht);
        string newSerializer = PhpSerializer.SerializeToString(ht);

        Assert.Equal(legacy, newSerializer);
    }
}
