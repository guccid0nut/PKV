// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.IO.Compression;
using System.Text;
using ASPIRE.Common.Prebaking.Parsers;
using ASPIRE.Prebaking.Tests.Fixtures;
using FluentAssertions;
using NEXUS.Contracts.EntityCache;
using Xunit;

namespace ASPIRE.Prebaking.Tests.Parsers;

public sealed class MasterXmlParserArchiveFallbackTests
{
    [Fact]
    public async Task ParseMasterDataAsync_WithoutSharedDir_HarvestsFromArchiveStream()
    {
        // Arrange: create mock game directory with ONLY a resources0.s2z archive and NO loose files or sharedDir
        string tempDir = Path.Combine(Path.GetTempPath(), $"master_archive_test_{Guid.NewGuid():N}");
        string gameDir = Path.Combine(tempDir, "game");
        Directory.CreateDirectory(gameDir);

        string r0Path = Path.Combine(gameDir, "resources0.s2z");
        using (FileStream fs = new(r0Path, FileMode.Create))
        using (ZipArchive zip = new(fs, ZipArchiveMode.Create))
        {
            void AddZipEntry(string name, string content)
            {
                ZipArchiveEntry entry = zip.CreateEntry(name);
                using StreamWriter sw = new(entry.Open(), Encoding.UTF8);
                sw.Write(content);
            }

            AddZipEntry("heroes.xml", SampleXmlData.ValidHeroesXml);
            AddZipEntry("heroes_preloadres.xml", SampleXmlData.ValidPreloadResXml);
            AddZipEntry("heroes_preview.xml", SampleXmlData.ValidPreviewXml);
            AddZipEntry("base.upgrades", SampleXmlData.ValidUpgradesXml);
            AddZipEntry("base.gamemechanics", SampleXmlData.ValidMechanicsXml);

            // Add entity definitions directly inside the archive
            AddZipEntry("heroes/test/ability_01.entity", SampleXmlData.ValidAbilityEntity);
            AddZipEntry("heroes/test/state_test.entity", SampleXmlData.ValidStateEntity);
            AddZipEntry("heroes/test/projectile_test.entity", SampleXmlData.ValidProjectileEntity);
            AddZipEntry("items/test/item_test.entity", SampleXmlData.ValidItemEntity);
        }

        try
        {
            MasterXmlParser parser = new();

            // Act: Pass sharedDir as null
            EntityCacheContainer container = await parser.ParseMasterDataAsync(tempDir, sharedDir: null);

            // Assert
            container.Should().NotBeNull();
            container.Heroes.Should().HaveCount(2);
            container.Heroes[0].PreloadResources.Should().HaveCount(4);
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

    [Trait("Category", "Manual")]
    [Fact]
    public async Task ParseMasterDataAsync_AgainstRealClientArchives_SucceedsWithoutSharedDir()
    {
        if (!NexusTestEnvironment.IsManualTestEnabled) return;

        string current = AppContext.BaseDirectory;
        DirectoryInfo? dir = new(current);
        string? repoRoot = null;
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Directory.Build.props")))
            {
                repoRoot = dir.FullName;
                break;
            }
            dir = dir.Parent;
        }

        if (repoRoot == null) return;
        string clientDir = Path.Combine(repoRoot, "games", "client");
        if (!Directory.Exists(clientDir) || !File.Exists(Path.Combine(clientDir, "game", "resources0.s2z")))
            return;

        MasterXmlParser parser = new();
        EntityCacheContainer container = await parser.ParseMasterDataAsync(clientDir, sharedDir: null);

        container.Should().NotBeNull();
        container.Heroes.Should().HaveCount(140);
        container.Abilities.Should().NotBeEmpty();
        container.States.Should().NotBeEmpty();
        container.Projectiles.Should().NotBeEmpty();
        container.Items.Should().NotBeEmpty();
    }
}
