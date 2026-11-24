// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using ASPIRE.Tests.ASPIRE.Common.EntityPrebake.Contracts;
using ASPIRE.Tests.ASPIRE.Common.EntityPrebake.Harness;
using FluentAssertions;

namespace ASPIRE.Tests.ASPIRE.Common.EntityPrebake.Tier1_FeatureCoverage;

public sealed class LayeredVfsOverrideTests : IDisposable
{
    private readonly string _overrideDir;
    private readonly string _s2zPath;
    private readonly string _tempDir;

    public LayeredVfsOverrideTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "KongorVfsTest_" + Guid.NewGuid().ToString("N"));
        _overrideDir = Path.Combine(_tempDir, "override");
        _s2zPath = Path.Combine(_tempDir, "resources0.s2z");

        Directory.CreateDirectory(_overrideDir);

        Dictionary<string, byte[]> s2zEntries = new()
        {
            ["models/heroes/pyromancer/hero.glb"] = Encoding.UTF8.GetBytes("archive-pyromancer-glb"),
            ["textures/heroes/pyromancer/icon.tga"] = Encoding.UTF8.GetBytes("archive-icon-tga")
        };
        TestPrebakeHarness.CreateSampleS2zArchive(_s2zPath, s2zEntries);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            try
            {
                Directory.Delete(_tempDir, true);
            }
            catch
            {
                /* ignored */
            }
    }

    [Fact]
    public async Task ResolveAsset_InMemoryPrewarmed_ReturnsPreBakedMemorySourceTier()
    {
        // Arrange
        EntityCacheContainer container = TestPrebakeHarness.CreateSampleCatalog(5, 2);
        using TestPrebakedEntityRegistry registry = new(container, _overrideDir, _s2zPath);
        await registry.InitializeAsync();

        // Act
        EntityLookupResult result = registry.ResolveAsset("Hero_1");

        // Assert
        result.Found.Should().BeTrue();
        result.SourceTier.Should().Be("PreBakedMemory");
        result.ResolvedPath.Should().Be("/heroes/hero_1/model.mdf");
        result.LatencyMicroseconds.Should().BeLessThan(5000);
    }

    [Fact]
    public async Task ResolveAsset_LooseOverrideFileOnDisk_ReturnsPreBakedDiskSourceTier()
    {
        // Arrange
        string looseFile = Path.Combine(_overrideDir, "models", "heroes", "magmus", "hero.glb");
        Directory.CreateDirectory(Path.GetDirectoryName(looseFile)!);
        File.WriteAllText(looseFile, "override-magmus-glb");

        using TestPrebakedEntityRegistry registry = new(null, _overrideDir, _s2zPath);
        await registry.InitializeAsync();

        // Act
        EntityLookupResult result = registry.ResolveAsset("models/heroes/magmus/hero.glb");

        // Assert
        result.Found.Should().BeTrue();
        result.SourceTier.Should().Be("PreBakedDisk");
        result.ResolvedPath.Should().Be(looseFile);
    }

    [Fact]
    public async Task LayeredVfsOverride_ResolveAsset_LooseOverrideTakesPrecedenceOverS2zArchive()
    {
        // Arrange: Place file in both override directory and S2Z archive
        string looseFile = Path.Combine(_overrideDir, "models", "heroes", "pyromancer", "hero.glb");
        Directory.CreateDirectory(Path.GetDirectoryName(looseFile)!);
        File.WriteAllText(looseFile, "override-pyromancer-glb-HIGH-PRIORITY");

        using TestPrebakedEntityRegistry registry = new(null, _overrideDir, _s2zPath);
        await registry.InitializeAsync();

        // Act
        EntityLookupResult result = registry.ResolveAsset("models/heroes/pyromancer/hero.glb");

        // Assert
        result.Found.Should().BeTrue();
        result.SourceTier.Should().Be("PreBakedDisk", "Loose override must always take precedence over S2Z archive");
        result.ResolvedPath.Should().Be(looseFile);
    }

    [Trait("Category", "Manual")]
    [Fact]
    public async Task ResolveAsset_S2zArchiveFile_ReturnsVfsArchiveSourceTier()
    {
        if (!NexusTestEnvironment.IsManualTestEnabled) return;
        // Arrange
        using TestPrebakedEntityRegistry registry = new(null, _overrideDir, _s2zPath);
        await registry.InitializeAsync();

        // Act
        EntityLookupResult result = registry.ResolveAsset("textures/heroes/pyromancer/icon.tga");

        // Assert
        result.Found.Should().BeTrue();
        result.SourceTier.Should().Be("VfsArchive");
        result.ResolvedPath.Should().Contain("textures/heroes/pyromancer/icon.tga");
    }

    [Fact]
    public async Task ResolveAsset_MissingFile_ReturnsNotFound()
    {
        // Arrange
        using TestPrebakedEntityRegistry registry = new(null, _overrideDir, _s2zPath);
        await registry.InitializeAsync();

        // Act
        EntityLookupResult result = registry.ResolveAsset("non/existent/texture.dds");

        // Assert
        result.Found.Should().BeFalse();
        result.SourceTier.Should().Be("NotFound");
        result.ResolvedPath.Should().BeEmpty();
    }

    [Fact]
    public async Task ResolveAsset_PathNormalization_HandlesBackslashesAndLeadingSlashes()
    {
        // Arrange
        using TestPrebakedEntityRegistry registry = new(null, _overrideDir, _s2zPath);
        await registry.InitializeAsync();

        // Act
        EntityLookupResult resultBackslashes = registry.ResolveAsset(@"textures\heroes\pyromancer\icon.tga");
        EntityLookupResult resultLeadingSlash = registry.ResolveAsset(@"/textures/heroes/pyromancer/icon.tga");

        // Assert
        resultBackslashes.Found.Should().BeTrue();
        resultLeadingSlash.Found.Should().BeTrue();
        resultBackslashes.SourceTier.Should().Be("VfsArchive");
        resultLeadingSlash.SourceTier.Should().Be("VfsArchive");
    }
}
