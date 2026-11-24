// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using ASPIRE.Common.Prebaking;
using ASPIRE.Common.Prebaking.Generators;
using ASPIRE.Common.Prebaking.Serialization;
using ASPIRE.Prebaking.Tests.Infrastructure;
using FluentAssertions;
using NEXUS.Contracts.EntityCache;
using Xunit;

namespace ASPIRE.Prebaking.Tests.Integration;

public sealed class PrebakeIntegrationTests
{
    private static string FindRepoRoot()
    {
        string current = AppContext.BaseDirectory;
        while (!string.IsNullOrEmpty(current))
        {
            if (File.Exists(Path.Combine(current, "ProjectKongor.slnx")) ||
                Directory.Exists(Path.Combine(current, "games", "client")))
                return current;
            string? parent = Path.GetDirectoryName(current);
            if (string.IsNullOrEmpty(parent) || parent == current) break;
            current = parent;
        }

        return Directory.GetCurrentDirectory();
    }

    [Trait("Category", "Manual")]
    [Fact]
    public async Task EntityHarvestEngine_AgainstRepoAssets_ExtractsFullDataset()
    {
        if (!NexusTestEnvironment.IsManualTestEnabled) return;

        // Arrange
        string repoRoot = FindRepoRoot();
        string clientDir = Path.Combine(repoRoot, "games", "client");
        string sharedDir = Path.Combine(repoRoot, "games", "shared");

        if (!Directory.Exists(clientDir) || !File.Exists(Path.Combine(clientDir, "game", "heroes.xml")))
            // Skip if not in full repo environment
            return;

        EntityHarvestEngine engine = new();

        // Act
        EntityCacheContainer container = await engine.HarvestAllAsync(clientDir, sharedDir);

        // Assert
        container.Should().NotBeNull();
        container.Heroes.Should().HaveCount(140);
        container.AltAvatars.Should().HaveCount(1499);
        container.AllPreloadResources.Should().HaveCountGreaterThan(10000);
        container.CosmeticUpgrades.Should().HaveCount(2858);
        container.GameMechanics.Should().NotBeNull();
        container.GameMechanics!.CombatTables.Should().NotBeEmpty();

        // Test Serialization
        byte[] binary = EntityCacheSerializer.Serialize(container, 3);
        binary.Length.Should().BeGreaterThan(EntityCacheHeader.HeaderSize);

        // Test Deserialization & Verification
        EntityCacheContainer restored = EntityCacheDeserializer.Deserialize(binary);
        restored.Heroes.Should().HaveCount(140);
        restored.AltAvatars.Should().HaveCount(1499);
        restored.CosmeticUpgrades.Should().HaveCount(2858);

        // Test Lua Generation
        string lua = LuaCacheGenerator.GenerateLuaCache(container);
        lua.Should().Contain("HeroDefinitionsCache.Heroes");
        lua.Should().Contain("Hero_Accursed");

        // Test FlatBuffers Generation & Loading
        byte[] flatBufferBytes = HeroFlatBufferGenerator.Generate(container);
        flatBufferBytes.Length.Should().BeGreaterThan(32);

        HeroFlatBufferLoader fbLoader = HeroFlatBufferLoader.Load(flatBufferBytes);
        fbLoader.HeroCount.Should().Be(140);
        fbLoader.Manifest.Version.Should().Be(1);
        fbLoader.Manifest.ChecksumXxHash3.Should().BeGreaterThan(0);

        HeroDescriptorRecord? accursed = fbLoader.GetHero("Hero_Accursed");
        accursed.Should().NotBeNull();
        accursed!.DisplayName.Should().Be("Accursed");
        accursed.PrimaryAttribute.Should().Be(FlatBufferAttributeType.Strength);
        accursed.Abilities.Should().NotBeEmpty();
    }
}

