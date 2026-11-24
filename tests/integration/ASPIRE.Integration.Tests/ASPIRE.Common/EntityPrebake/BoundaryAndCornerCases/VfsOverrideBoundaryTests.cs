// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using ASPIRE.Tests.ASPIRE.Common.EntityPrebake.Contracts;
using ASPIRE.Tests.ASPIRE.Common.EntityPrebake.Harness;
using FluentAssertions;

namespace ASPIRE.Tests.ASPIRE.Common.EntityPrebake.Tier2_BoundaryAndCornerCases;

public sealed class VfsOverrideBoundaryTests : IDisposable
{
    private readonly string _tempDir;

    public VfsOverrideBoundaryTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "KongorVfsBoundary_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
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

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task ResolveAsset_NullOrEmptyPath_ReturnsNotFound(string? invalidPath)
    {
        // Arrange
        using TestPrebakedEntityRegistry registry = new();
        await registry.InitializeAsync();

        // Act
        EntityLookupResult result = registry.ResolveAsset(invalidPath!);

        // Assert
        result.Found.Should().BeFalse();
        result.SourceTier.Should().Be("NotFound");
        result.ResolvedPath.Should().BeEmpty();
    }

    [Trait("Category", "Manual")]
    [Fact]
    public async Task ResolveAsset_NonExistentArchive_ReturnsNotFoundWithoutCrashing()
    {
        if (!NexusTestEnvironment.IsManualTestEnabled) return;
        // Arrange
        string nonExistentZip = Path.Combine(_tempDir, "does_not_exist.s2z");
        using TestPrebakedEntityRegistry registry = new(null, _tempDir, nonExistentZip);
        await registry.InitializeAsync();

        // Act
        EntityLookupResult result = registry.ResolveAsset("heroes/test/icon.tga");

        // Assert
        result.Found.Should().BeFalse();
        result.SourceTier.Should().Be("NotFound");
    }

    [Fact]
    public async Task ResolveAsset_DeeplyNestedPath_NormalizesAndResolves()
    {
        // Arrange
        string overrideSub = Path.Combine(_tempDir, "override", "a", "b", "c", "d");
        Directory.CreateDirectory(overrideSub);
        string deepFile = Path.Combine(overrideSub, "deep_model.mdf");
        File.WriteAllText(deepFile, "deep-model-data");

        using TestPrebakedEntityRegistry registry = new(null, Path.Combine(_tempDir, "override"));
        await registry.InitializeAsync();

        // Act
        EntityLookupResult result = registry.ResolveAsset(@"a\b\c\d\deep_model.mdf");

        // Assert
        result.Found.Should().BeTrue();
        result.SourceTier.Should().Be("PreBakedDisk");
        result.ResolvedPath.Should().Be(deepFile);
    }

    [Fact]
    public async Task ResolveAsset_CorruptedS2zArchive_HandlesGracefully()
    {
        // Arrange: Corrupt file written as .s2z
        string corruptZip = Path.Combine(_tempDir, "corrupt.s2z");
        File.WriteAllBytes(corruptZip, [0x00, 0x11, 0x22, 0x33, 0x44]);

        using TestPrebakedEntityRegistry registry = new(null, _tempDir, corruptZip);
        await registry.InitializeAsync();

        // Act: Should throw or handle gracefully
        Action act = () => registry.ResolveAsset("test.tga");

        // Assert: Throws InvalidDataException or handles cleanly
        act.Should().Throw<Exception>();
    }

    [Fact]
    public async Task ResolveAsset_SpecialCharactersInPath_ResolvesSuccessfully()
    {
        // Arrange
        string overrideDir = Path.Combine(_tempDir, "override");
        Directory.CreateDirectory(overrideDir);
        string specialFile = Path.Combine(overrideDir, "icon_hero#1(alt)[v2].tga");
        File.WriteAllText(specialFile, "special-icon-data");

        using TestPrebakedEntityRegistry registry = new(null, overrideDir);
        await registry.InitializeAsync();

        // Act
        EntityLookupResult result = registry.ResolveAsset("icon_hero#1(alt)[v2].tga");

        // Assert
        result.Found.Should().BeTrue();
        result.SourceTier.Should().Be("PreBakedDisk");
    }
}
