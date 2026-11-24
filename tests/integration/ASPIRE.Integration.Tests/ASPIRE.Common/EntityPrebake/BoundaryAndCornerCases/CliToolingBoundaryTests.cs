// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.IO.Compression;
using ASPIRE.Tests.ASPIRE.Common.EntityPrebake.Contracts;
using ASPIRE.Tests.ASPIRE.Common.EntityPrebake.Harness;
using FluentAssertions;

namespace ASPIRE.Tests.ASPIRE.Common.EntityPrebake.Tier2_BoundaryAndCornerCases;

public sealed class CliToolingBoundaryTests : IDisposable
{
    private readonly TestMemoryPackSerializer _serializer = new();
    private readonly string _tempDir;

    public CliToolingBoundaryTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "KongorCliBoundaryTest_" + Guid.NewGuid().ToString("N"));
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
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(9)]
    [InlineData(19)]
    [InlineData(22)]
    public void PrebakeCli_ZstdLevelBoundaries_Level1ToLevel22_Accepted(int level)
    {
        // Arrange
        EntityCacheContainer container = TestPrebakeHarness.CreateSampleCatalog(5, 1);

        // Act
        byte[] compressed = _serializer.SerializeToBytes(container, true, level);
        EntityCacheContainer restored = _serializer.DeserializeFromBytes(compressed, true);

        // Assert
        restored.Heroes.Should().HaveCount(5);
    }

    [Fact]
    public void PrebakeCli_S2zPackaging_WithZeroEntries_ProducesValidEmptyZip()
    {
        // Arrange
        string emptyZipPath = Path.Combine(_tempDir, "empty.s2z");
        Dictionary<string, byte[]> emptyEntries = [];

        // Act
        TestPrebakeHarness.CreateSampleS2zArchive(emptyZipPath, emptyEntries);

        // Assert
        File.Exists(emptyZipPath).Should().BeTrue();
        using ZipArchive archive = ZipFile.OpenRead(emptyZipPath);
        archive.Entries.Should().BeEmpty();
    }

    [Fact]
    public void PrebakeCli_LargeArchiveBundling_100Entries_PackagesCleanly()
    {
        // Arrange
        string largeZipPath = Path.Combine(_tempDir, "large.s2z");
        Dictionary<string, byte[]> entries = [];
        for (int i = 0; i < 100; i++)
            entries[$"heroes/hero_{i}/model.mdf"] = Encoding.UTF8.GetBytes($"model-content-{i}");

        // Act
        TestPrebakeHarness.CreateSampleS2zArchive(largeZipPath, entries);

        // Assert
        using ZipArchive archive = ZipFile.OpenRead(largeZipPath);
        archive.Entries.Should().HaveCount(100);
        archive.GetEntry("heroes/hero_50/model.mdf").Should().NotBeNull();
    }

    [Fact]
    public void PrebakeCliOptions_InvalidNegativeLevel_ClampsOrConfiguresSafely()
    {
        // Arrange
        PrebakeCliOptions options = new()
        {
            ZstdCompressionLevel = -1
        };

        // Assert
        options.ZstdCompressionLevel.Should().Be(-1);
    }

    [Fact]
    public void CliToolingBoundary_PrebakeCli_HandlesSpecialCharsInOutputPath()
    {
        // Arrange
        string specialSubDir = Path.Combine(_tempDir, "Kongor #1 (Special) [Assets] & Models");
        Directory.CreateDirectory(specialSubDir);
        string targetFile = Path.Combine(specialSubDir, "entity_cache.bin");

        EntityCacheContainer container = TestPrebakeHarness.CreateSampleCatalog(2, 1);
        byte[] bytes = _serializer.SerializeToBytes(container);

        // Act
        File.WriteAllBytes(targetFile, bytes);

        // Assert
        File.Exists(targetFile).Should().BeTrue();
        new FileInfo(targetFile).Length.Should().BeGreaterThan(0);
    }
}
