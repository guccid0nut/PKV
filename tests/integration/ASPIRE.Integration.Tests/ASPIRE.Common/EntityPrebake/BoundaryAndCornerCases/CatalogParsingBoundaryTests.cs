// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Xml;
using ASPIRE.Tests.ASPIRE.Common.EntityPrebake.Contracts;
using ASPIRE.Tests.ASPIRE.Common.EntityPrebake.Harness;
using FluentAssertions;

namespace ASPIRE.Tests.ASPIRE.Common.EntityPrebake.Tier2_BoundaryAndCornerCases;

public sealed class CatalogParsingBoundaryTests
{
    private readonly TestEntityCatalogParser _parser = new();

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t\n\r")]
    public void ParseFromXmlContent_EmptyOrWhitespace_ThrowsArgumentException(string emptyXml)
    {
        // Act
        Action act = () => _parser.ParseFromXmlContent(emptyXml);

        // Assert
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void ParseFromXmlContent_MalformedXmlSyntax_ThrowsXmlException()
    {
        // Arrange
        string malformed = "<heroes><hero id=\"1\" name=\"Unclosed\"></heroes>";

        // Act
        Action act = () => _parser.ParseFromXmlContent(malformed);

        // Assert
        act.Should().Throw<XmlException>();
    }

    [Fact]
    public void ParseFromXmlContent_HeroWithZeroId_ParsesWithDefaultZeroId()
    {
        // Arrange
        string xml = "<heroes><hero id=\"0\" name=\"Hero_Zero\" displayname=\"Zero Hero\" /></heroes>";

        // Act
        EntityCacheContainer container = _parser.ParseFromXmlContent(xml);

        // Assert
        container.Heroes.Should().HaveCount(1);
        container.Heroes[0].Id.Should().Be(0);
        container.Heroes[0].Name.Should().Be("Hero_Zero");
    }

    [Fact]
    public void ParseFromXmlContent_HeroWithMaxUintId_ParsesCorrectly()
    {
        // Arrange
        string xml = $"<heroes><hero id=\"{uint.MaxValue}\" name=\"Hero_Max\" displayname=\"Max Hero\" /></heroes>";

        // Act
        EntityCacheContainer container = _parser.ParseFromXmlContent(xml);

        // Assert
        container.Heroes.Should().HaveCount(1);
        container.Heroes[0].Id.Should().Be(uint.MaxValue);
    }

    [Fact]
    public void ParseFromXmlContent_SpecialUnicodeCharactersInHeroName_PreservesCharacters()
    {
        // Arrange
        string unicodeHeroName = "Hero_???_??_öäü_???????";
        string xml = $"<heroes><hero id=\"100\" name=\"{unicodeHeroName}\" displayname=\"Sun Wukong ???\" /></heroes>";

        // Act
        EntityCacheContainer container = _parser.ParseFromXmlContent(xml);

        // Assert
        container.Heroes.Should().HaveCount(1);
        container.Heroes[0].Name.Should().Be(unicodeHeroName);
        container.Heroes[0].DisplayName.Should().Be("Sun Wukong ???");
    }

    [Fact]
    public void ParseLooseEntity_EmptyFile_ThrowsArgumentException()
    {
        // Act
        Action act = () => _parser.ParseLooseEntityFile("test.entity", "");

        // Assert
        act.Should().Throw<ArgumentException>();
    }
}
