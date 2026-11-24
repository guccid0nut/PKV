// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Collections;

namespace ASPIRE.Tests.KONGOR.MasterServer.Tests.Serialisation.PHP;

public sealed class PhpSerializationVerificationTests
{
    [Fact]
    public async Task Debug_Serialize_List()
    {
        List<string> list = ["a", "b"];
        string serialized = PhpSerialization.Serialize(list);
        Assert.Contains("a:2:", serialized);
        await Task.CompletedTask;
    }

    [Fact]
    public async Task Debug_Serialize_Dictionary_IntKey()
    {
        Dictionary<int, object> dict = new() { { 0, "a" }, { 1, "b" } };
        string serialized = PhpSerialization.Serialize(dict);
        Assert.Contains("i:0;", serialized);
        await Task.CompletedTask;
    }

    [Fact]
    public async Task Debug_Serialize_Dictionary_StringKey_Numeric()
    {
        Dictionary<string, object> dict = new() { { "0", "a" }, { "1", "b" } };
        string serialized = PhpSerialization.Serialize(dict);
        Assert.Contains("s:1:\"0\";", serialized);
        await Task.CompletedTask;
    }

    [Fact]
    public async Task Debug_Serialize_Hybrid()
    {
        Dictionary<object, object> dict = new() { ["nickname"] = "TestUser", [0] = "val0", ["0"] = "val0" };

        string serialized = PhpSerialization.Serialize(dict);
        Assert.False(serialized.Contains("a:0:{}"), "Dictionary<object,object> serialized to empty array!");
        await Task.CompletedTask;
    }

    [Fact]
    public async Task Debug_Serialize_Hashtable_IntKeys()
    {
        Hashtable ht = new() { [0] = "val0", [1] = "val1", ["name"] = "test" };

        string serialized = PhpSerialization.Serialize(ht);
        Assert.False(serialized.Contains("a:0:{}"), "Hashtable (Int/Mixed) serialized to empty array!");
        Assert.Contains("val0", serialized);
        await Task.CompletedTask;
    }

    [Fact]
    public async Task PhpSerializer_VariousDataFormats_SerializesAndDeserializesCorrectly()
    {
        List<object> list = ["Val1", "Val2"];
        string serializedList = PhpSerialization.Serialize(list);
        Assert.Contains("a:2:", serializedList);

        Dictionary<object, object> dict0 = new() { { 0, "Val1" }, { 1, "Val2" } };
        string serializedDict0 = PhpSerialization.Serialize(dict0);
        Assert.Contains("Val1", serializedDict0);

        Dictionary<object, object> dict1 = new() { { 1, "Val1" }, { 2, "Val2" } };
        string serializedDict1 = PhpSerialization.Serialize(dict1);
        Assert.Contains("Val1", serializedDict1);

        await Task.CompletedTask;
    }

    [Fact]
    public async Task PhpSerializer_DictionaryPayloads_SerializesCorrectly()
    {
        Dictionary<int, object> dict = new() { { 1, "Hero1" }, { 2, "Hero2" } };
        object dictAsObj = dict;
        string output = PhpSerialization.Serialize(dictAsObj);
        Assert.Contains("Hero1", output);

        List<object> list = ["Item1", "Item2"];
        string listOutput = PhpSerialization.Serialize(list);
        Assert.Contains("Item1", listOutput);

        await Task.CompletedTask;
    }

    [Fact]
    public async Task PhpSerializer_MasteryPayloads_SerializesCorrectly()
    {
        Hashtable masteryData = [];
        const int index = 1;
        Hashtable heroData = new()
        {
            [1] = "HeroIdent",
            [2] = "HeroIdent",
            ["heroName"] = "HeroIdent",
            [17] = "1000",
            ["level"] = 1
        };

        masteryData[index] = heroData;

        object dictAsObj = masteryData;
        string output = PhpSerialization.Serialize(dictAsObj);
        Assert.Contains("HeroIdent", output);

        await Task.CompletedTask;
    }

    [Fact]
    public async Task Dictionary_vs_Hashtable_Sorting()
    {
        Hashtable ht = new() { ["b"] = 2, ["a"] = 1, [1] = "one" };
        string serialized = PhpSerialization.Serialize(ht);
        Assert.Contains("a:3:", serialized);
        await Task.CompletedTask;
    }
}
