// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2022-2026 [K]ONGOR and Contributors
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV) -- Upstream lineage: https://github.com/Project-KONGOR-Open-Source

using System.Collections;

namespace ASPIRE.Tests.KONGOR.MasterServer.Tests.Serialisation.PHP;

public sealed partial class PHPPropertyAttributeParityTests
{
    [Fact]
    public void Serialisation_And_Deserialisation_With_Standard_Attribute_Produces_Expected_Results()
    {
        TestClassWithPhpProperty @object = new()
        {
            PropertyOne = "one",
            PropertyTwo = 2,
            PropertyThree = "three",
            PropertyFour = 4
        };

        string serialisedData = PhpSerializer.SerializeToString(@object);

        const string expectedSerialisationOutput =
            @"a:4:{s:3:""one"";s:3:""one"";s:3:""two"";i:2;i:3;s:5:""three"";i:4;i:4;}";

        Assert.Equal(expectedSerialisationOutput, serialisedData);

        if (PhpSerializer.Deserialize(serialisedData) is not IDictionary deserialisedData)
        {
            Assert.Fail("Deserialised Data Is NULL");
        }
        else
        {
            Assert.NotNull(deserialisedData);
            Assert.Equal("one", deserialisedData["one"]);
            Assert.Equal(2, deserialisedData["two"]);
            Assert.Equal("three", deserialisedData[3]);
            Assert.Equal(4, deserialisedData[4]);
        }
    }

    [Fact]
    public void Serialisation_And_Deserialisation_With_Custom_Attribute_Produces_Expected_Results()
    {
        TestClassWithCustomPHPPropertyAttribute @object = new()
        {
            PropertyOne = "one",
            PropertyTwo = 2,
            PropertyThree = "three",
            PropertyFour = 4
        };

        string serialisedData = PhpSerialization.Serialize(@object);

        const string expectedSerialisationOutput =
            @"a:4:{s:3:""one"";s:3:""one"";s:3:""two"";i:2;i:3;s:5:""three"";i:4;i:4;}";

        Assert.Equal(expectedSerialisationOutput, serialisedData);

        if (PhpSerialization.Deserialize(serialisedData) is not IDictionary deserialisedData)
        {
            Assert.Fail("Deserialised Data Is NULL");
        }
        else
        {
            Assert.NotNull(deserialisedData);
            Assert.Equal("one", deserialisedData["one"]);
            Assert.Equal(2, deserialisedData["two"]);
            Assert.Equal("three", deserialisedData[3]);
            Assert.Equal(4, deserialisedData[4]);
        }
    }

    [Fact]
    public void PHP_Serialisation_Output_Is_Identical_Between_Standard_And_Custom_Attribute()
    {
        TestClassWithPhpProperty standardObject = new()
        {
            PropertyOne = "one",
            PropertyTwo = 2,
            PropertyThree = "three",
            PropertyFour = 4
        };

        TestClassWithCustomPHPPropertyAttribute customObject = new()
        {
            PropertyOne = "one",
            PropertyTwo = 2,
            PropertyThree = "three",
            PropertyFour = 4
        };

        string standardSerialised = PhpSerializer.SerializeToString(standardObject);
        string customSerialised = PhpSerialization.Serialize(customObject);

        const string expectedSerialisationOutput =
            @"a:4:{s:3:""one"";s:3:""one"";s:3:""two"";i:2;i:3;s:5:""three"";i:4;i:4;}";

        Assert.Equal(expectedSerialisationOutput, standardSerialised);
        Assert.Equal(expectedSerialisationOutput, customSerialised);
    }

    [Fact]
    public void NULL_Values_Are_Correctly_Serialised_To_PHP_For_Both_Standard_And_Custom_Attribute()
    {
        TestClassWithPhpProperty standardObject = new()
        {
            PropertyOne = null!,
            PropertyTwo = 2,
            PropertyThree = null!,
            PropertyFour = 4
        };

        TestClassWithCustomPHPPropertyAttribute customObject = new()
        {
            PropertyOne = null!,
            PropertyTwo = 2,
            PropertyThree = null!,
            PropertyFour = 4
        };

        string standardSerialised = PhpSerializer.SerializeToString(standardObject);
        string customSerialised = PhpSerialization.Serialize(customObject);

        const string expectedSerialisationOutput = @"a:4:{s:3:""one"";N;s:3:""two"";i:2;i:3;N;i:4;i:4;}";

        Assert.Equal(expectedSerialisationOutput, standardSerialised);
        Assert.Equal(expectedSerialisationOutput, customSerialised);
    }

    public partial class TestClassWithPhpProperty
    {
        [PhpProperty("one")] public required string PropertyOne { get; init; }

        [PhpProperty("two")] public required int PropertyTwo { get; init; }

        [PhpProperty(3)] public required string PropertyThree { get; init; }

        [PhpProperty(4)] public required int PropertyFour { get; init; }
    }

    public partial class TestClassWithCustomPHPPropertyAttribute
    {
        [PHPProperty("one")] public required string PropertyOne { get; init; }

        [PHPProperty("two")] public required int PropertyTwo { get; init; }

        [PHPProperty(3)] public required string PropertyThree { get; init; }

        [PHPProperty(4)] public required int PropertyFour { get; init; }
    }
}
