// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Text;
using ASPIRE.Common.Prebaking.Parsers;
using ASPIRE.Prebaking.Tests.Fixtures;
using FluentAssertions;
using NEXUS.Contracts.EntityCache;
using Xunit;

namespace ASPIRE.Prebaking.Tests.Parsers;

public sealed class HeroesXmlParserTests
{
    [Fact]
    public async Task ParseStreamAsync_WithValidHeroesXml_ReturnsPopulatedHeroList()
    {
        // Arrange
        HeroesXmlParser parser = new();
        using MemoryStream stream = new(Encoding.UTF8.GetBytes(SampleXmlData.ValidHeroesXml));

        // Act
        List<HeroDefinitionDto> heroes = await parser.ParseStreamAsync(stream);

        // Assert
        heroes.Should().NotBeNull();
        heroes.Should().HaveCount(2);

        HeroDefinitionDto accursed = heroes[0];
        accursed.Name.Should().Be("Hero_Accursed");
        accursed.DisplayName.Should().Be("Accursed");
        accursed.PrimaryAttribute.Should().Be("strength");
        accursed.Strength.Should().Be(23f);
        accursed.MoveSpeed.Should().Be(300f);
        accursed.Abilities.Should().HaveCount(2);
        accursed.AltAvatars.Should().HaveCount(2);

        HeroDefinitionDto pyromancer = heroes[1];
        pyromancer.Name.Should().Be("Hero_Pyromancer");
        pyromancer.PrimaryAttribute.Should().Be("intelligence");
        pyromancer.Abilities.Should().HaveCount(1);
        pyromancer.AltAvatars.Should().HaveCount(1);
    }

    [Fact]
    public async Task ParseStreamAsync_WithInvalidRoot_ThrowsInvalidDataException()
    {
        // Arrange
        HeroesXmlParser parser = new();
        using MemoryStream stream = new(Encoding.UTF8.GetBytes("<invalid_root></invalid_root>"));

        // Act & Assert
        Func<Task> act = async () => await parser.ParseStreamAsync(stream);
        await act.Should().ThrowAsync<InvalidDataException>();
    }

    [Fact]
    public async Task ParseStreamAsync_WithGwynplaineXml_ResolvesHellbourneAndAbilities()
    {
        // Arrange
        HeroesXmlParser parser = new();
        const string xml = """
            <heroes>
                <hero name="Hero_Gwynplaine" team="Hellbourne" path="/heroes/gwynplaine" entity="/hero.entity" defaultIcon="/heroes/gwynplaine/icon.tga" displayName="Gwynplaine" defaultModel="model.mdf" primaryattribute="intelligence" strength="19" agility="23" intelligence="25">
                    <ability slot="0" name="Ability_Gwynplaine1" icon="/heroes/gwynplaine/ability_01/icon.tga" maxLevel="4" actionType="4" manaCost="95" range="800" cooldown="12000" />
                    <ability slot="1" name="Ability_Gwynplaine2" icon="/heroes/gwynplaine/ability_02/icon.tga" maxLevel="4" actionType="4" manaCost="80" range="600" cooldown="14000" />
                    <ability slot="2" name="Ability_Gwynplaine3" icon="/heroes/gwynplaine/ability_03/icon.tga" maxLevel="4" actionType="1" manaCost="0" range="0" cooldown="0" />
                    <ability slot="3" name="Ability_Gwynplaine4" icon="/heroes/gwynplaine/ability_04/icon.tga" maxLevel="3" actionType="5" manaCost="125" range="400" cooldown="70000" />
                    <altavatar key="Hero_Gwynplaine.Alt" name="Crimson Grin" path="" iconPath="/heroes/gwynplaine/icon.tga" defaultModel="model.mdf" />
                </hero>
            </heroes>
            """;
        using MemoryStream stream = new(Encoding.UTF8.GetBytes(xml));

        // Act
        List<HeroDefinitionDto> heroes = await parser.ParseStreamAsync(stream);

        // Assert
        heroes.Should().NotBeNull();
        heroes.Should().HaveCount(1);
        HeroDefinitionDto gwyn = heroes[0];
        gwyn.Name.Should().Be("Hero_Gwynplaine");
        gwyn.DisplayName.Should().Be("Gwynplaine");
        gwyn.Team.Should().Be("Hellbourne");
        gwyn.Faction.Should().Be("Hellbourne");
        gwyn.PrimaryAttribute.Should().Be("intelligence");
        gwyn.Abilities.Should().HaveCount(4);
        gwyn.Abilities[0].Name.Should().Be("Ability_Gwynplaine1");
        gwyn.Abilities[3].Name.Should().Be("Ability_Gwynplaine4");
        gwyn.AltAvatars.Should().HaveCount(1);
        gwyn.AltAvatars[0].Key.Should().Be("Hero_Gwynplaine.Alt");
        gwyn.AltAvatars[0].DisplayName.Should().Be("Crimson Grin");
    }
}

