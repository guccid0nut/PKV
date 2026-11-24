// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using ASPIRE.Tests.ASPIRE.Common.EntityPrebake.Contracts;
using ASPIRE.Tests.ASPIRE.Common.EntityPrebake.Harness;
using FluentAssertions;

namespace ASPIRE.Tests.ASPIRE.Common.EntityPrebake.Tier1_FeatureCoverage;

public sealed class MasterCatalogParsingTests
{
    private readonly TestEntityCatalogParser _parser = new();

    [Fact]
    public void ParseHeroesXml_StandardPayload_ExtractsAllHeroesAccurately()
    {
        // Arrange
        string xml = TestPrebakeHarness.CreateSampleHeroesXml(5);

        // Act
        EntityCacheContainer container = _parser.ParseFromXmlContent(xml);

        // Assert
        container.Should().NotBeNull();
        container.Heroes.Should().HaveCount(5);
        HeroDefinitionDto firstHero = container.Heroes[0];
        firstHero.Id.Should().Be(1);
        firstHero.Name.Should().Be("Hero_Test_1");
        firstHero.DisplayName.Should().Be("Test Hero 1");
        firstHero.PrimaryAttribute.Should().Be("Strength");
        firstHero.IconPath.Should().Be("/heroes/test_1/icon.tga");
        firstHero.ModelPath.Should().Be("/heroes/test_1/model.mdf");
    }

    [Fact]
    public void ParseAltAvatars_WithinHeroDefinitions_PreservesAvatarHierarchyAndPriority()
    {
        // Arrange
        string xml = TestPrebakeHarness.CreateSampleHeroesXml();

        // Act
        EntityCacheContainer container = _parser.ParseFromXmlContent(xml);

        // Assert
        container.AltAvatars.Should().HaveCount(6); // 3 heroes * 2 altavatars each
        AltAvatarDto avatar = container.AltAvatars[0];
        avatar.HeroName.Should().Be("Hero_Test_1");
        avatar.Key.Should().Be("alt1");
        avatar.Priority.Should().Be(10);
        avatar.DisplayName.Should().Be("Alt Avatar 1");
    }

    [Fact]
    public void MasterCatalogParsing_ParseAbilities_ExtractsManaCostCooldownAndActionType()
    {
        // Arrange
        string xml = TestPrebakeHarness.CreateSampleHeroesXml(2);

        // Act
        EntityCacheContainer container = _parser.ParseFromXmlContent(xml);

        // Assert
        container.Abilities.Should().HaveCount(4); // 2 heroes * 2 abilities
        AbilityDefinitionDto ability = container.Abilities[0];
        ability.Name.Should().Be("Hero_Test_1_Ability1");
        ability.ActionType.Should().Be("target_entity");
    }

    [Fact]
    public void MasterCatalogParsing_ParsePreloadResources_BindsResourceReferencesToCorrectHero()
    {
        // Arrange
        string heroesXml = TestPrebakeHarness.CreateSampleHeroesXml(2);
        string preloadXml = TestPrebakeHarness.CreateSamplePreloadXml();

        // Act
        EntityCacheContainer container = _parser.ParseFromXmlContent(heroesXml, preloadXml);

        // Assert
        HeroDefinitionDto hero1 = container.Heroes[0];
        hero1.PreloadResources.Should().HaveCount(2);
        hero1.PreloadResources.Should().Contain("/heroes/test_1/sounds/ready.wav");
        hero1.PreloadResources.Should().Contain("/heroes/test_1/textures/diffuse.dds");

        HeroDefinitionDto hero2 = container.Heroes[1];
        hero2.PreloadResources.Should().BeEmpty();
    }

    [Fact]
    public void MasterCatalogParsing_ParseLooseEntityFile_ExtractsEntityPropertiesAndAttributes()
    {
        // Arrange
        string entityContent =
            @"<projectile name=""Projectile_Pyromancer_Dragon"" speed=""1400"" lifetime=""2.5"" model=""/heroes/pyromancer/dragon.mdf"" />";

        // Act
        EntityDefinitionDto entity = _parser.ParseLooseEntityFile("heroes/pyromancer/dragon.entity", entityContent);

        // Assert
        entity.Should().NotBeNull();
        entity.Name.Should().Be("Projectile_Pyromancer_Dragon");
        entity.EntityType.Should().Be("projectile");
        entity.Properties.Should().ContainKey("speed").WhoseValue.Should().Be("1400");
        entity.Properties.Should().ContainKey("lifetime").WhoseValue.Should().Be("2.5");
        entity.Properties.Should().ContainKey("model").WhoseValue.Should().Be("/heroes/pyromancer/dragon.mdf");
    }

    [Fact]
    public void ParseFullCatalog_WithAllEntityTypes_BuildsComprehensiveContainer()
    {
        // Arrange
        EntityCacheContainer sample = TestPrebakeHarness.CreateSampleCatalog(10, 2);

        // Assert
        sample.Heroes.Should().HaveCount(10);
        sample.AltAvatars.Should().HaveCount(20);
        sample.Abilities.Should().HaveCount(30);
        sample.States.Should().HaveCount(10);
        sample.Projectiles.Should().HaveCount(10);
        sample.Items.Should().HaveCount(1);
        sample.Version.Should().Be(1);
    }
}
