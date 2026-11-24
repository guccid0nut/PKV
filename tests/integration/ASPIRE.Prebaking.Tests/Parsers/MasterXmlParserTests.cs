// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using ASPIRE.Common.Prebaking.Parsers;
using ASPIRE.Prebaking.Tests.Fixtures;
using FluentAssertions;
using NEXUS.Contracts.EntityCache;
using Xunit;

namespace ASPIRE.Prebaking.Tests.Parsers;

public sealed class MasterXmlParserTests
{
    [Fact]
    public async Task ParseMasterDataAsync_WithMockGameDirectory_PopulatesEntityContainer()
    {
        // Arrange
        string tempDir = Path.Combine(Path.GetTempPath(), $"master_test_{Guid.NewGuid():N}");
        string gameDir = Path.Combine(tempDir, "game");
        string sharedDir = Path.Combine(tempDir, "shared");
        Directory.CreateDirectory(gameDir);
        Directory.CreateDirectory(sharedDir);

        try
        {
            await File.WriteAllTextAsync(Path.Combine(gameDir, "heroes.xml"), SampleXmlData.ValidHeroesXml);
            await File.WriteAllTextAsync(Path.Combine(gameDir, "heroes_preloadres.xml"),
                SampleXmlData.ValidPreloadResXml);
            await File.WriteAllTextAsync(Path.Combine(gameDir, "heroes_preview.xml"), SampleXmlData.ValidPreviewXml);
            await File.WriteAllTextAsync(Path.Combine(gameDir, "base.upgrades"), SampleXmlData.ValidUpgradesXml);
            await File.WriteAllTextAsync(Path.Combine(gameDir, "base.gamemechanics"), SampleXmlData.ValidMechanicsXml);

            await File.WriteAllTextAsync(Path.Combine(sharedDir, "ability_test.entity"),
                SampleXmlData.ValidAbilityEntity);
            await File.WriteAllTextAsync(Path.Combine(sharedDir, "state_test.entity"), SampleXmlData.ValidStateEntity);
            await File.WriteAllTextAsync(Path.Combine(sharedDir, "projectile_test.entity"),
                SampleXmlData.ValidProjectileEntity);
            await File.WriteAllTextAsync(Path.Combine(sharedDir, "item_test.entity"), SampleXmlData.ValidItemEntity);

            MasterXmlParser parser = new();

            // Act
            EntityCacheContainer container = await parser.ParseMasterDataAsync(tempDir, sharedDir);

            // Assert
            container.Should().NotBeNull();
            container.Heroes.Should().HaveCount(2);
            container.Heroes[0].PreloadResources.Should().HaveCount(4);
            container.Heroes[0].AltAvatars[0].PreviewPresets.Should().HaveCount(1);
            container.CosmeticUpgrades.Should().HaveCount(8);
            container.GameMechanics.Should().NotBeNull();
            container.Abilities.Should().Contain(a => a.Name == "Ability_TestStrike");
            container.States.Should().Contain(s => s.Name == "State_TestBuff");
            container.Projectiles.Should().Contain(p => p.Name == "Projectile_TestArrow");
            container.Items.Should().Contain(i => i.Name == "Item_TestBlade");
            container.TotalEntityCount.Should().BeGreaterThan(0);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }
}
