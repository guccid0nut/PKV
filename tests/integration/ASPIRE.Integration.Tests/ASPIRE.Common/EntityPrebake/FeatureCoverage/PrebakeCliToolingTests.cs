// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Globalization;
using System.IO.Compression;
using ASPIRE.Common.Utilities;
using ASPIRE.Tests.ASPIRE.Common.EntityPrebake.Contracts;
using ASPIRE.Tests.ASPIRE.Common.EntityPrebake.Harness;
using FluentAssertions;

namespace ASPIRE.Tests.ASPIRE.Common.EntityPrebake.Tier1_FeatureCoverage;

public sealed class PrebakeCliToolingTests : IDisposable
{
    private readonly TestLuaManifestGenerator _luaGen = new();
    private readonly TestEntityCatalogParser _parser = new();
    private readonly TestMemoryPackSerializer _serializer = new();
    private readonly string _tempOutputDir;

    public PrebakeCliToolingTests()
    {
        _tempOutputDir = Path.Combine(Path.GetTempPath(), "KongorCliTest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempOutputDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempOutputDir))
            try
            {
                Directory.Delete(_tempOutputDir, true);
            }
            catch
            {
                /* ignored */
            }
    }

    [Fact]
    public void PrebakeCliOptions_DefaultSettings_ConfiguredCorrectly()
    {
        // Arrange & Act
        PrebakeCliOptions options = new();

        // Assert
        options.CompressWithZstd.Should().BeTrue();
        options.ZstdCompressionLevel.Should().Be(3);
        options.EmitLuaManifest.Should().BeTrue();
        options.CreateS2zArchive.Should().BeTrue();
    }

    [Fact]
    public void PrebakeCliTooling_PrebakePipeline_GeneratesBinaryAndLuaArtifacts()
    {
        // Arrange
        string heroesXml = TestPrebakeHarness.CreateSampleHeroesXml(5);
        EntityCacheContainer container = _parser.ParseFromXmlContent(heroesXml);

        string binPath = Path.Combine(_tempOutputDir, "entity_cache.bin");
        string luaPath = Path.Combine(_tempOutputDir, "hero_definitions_cache.lua");

        // Act
        byte[] binaryBytes = _serializer.SerializeToBytes(container, true);
        File.WriteAllBytes(binPath, binaryBytes);

        string luaContent = _luaGen.GenerateLuaManifest(container);
        File.WriteAllText(luaPath, luaContent, Encoding.UTF8);

        // Assert
        File.Exists(binPath).Should().BeTrue();
        File.Exists(luaPath).Should().BeTrue();

        new FileInfo(binPath).Length.Should().BeGreaterThan(0);
        new FileInfo(luaPath).Length.Should().BeGreaterThan(0);

        bool isValidZstd = ZstdCompressionExtensions.IsZstdCompressed(File.ReadAllBytes(binPath));
        isValidZstd.Should().BeTrue();
    }

    [Fact]
    public void PrebakePipeline_WithS2zOption_PackagesArchiveCleanly()
    {
        // Arrange
        string s2zPath = Path.Combine(_tempOutputDir, "resources0_prebaked.s2z");
        Dictionary<string, byte[]> entries = new()
        {
            ["ui/scripts/newui/hero_definitions_cache.lua"] = Encoding.UTF8.GetBytes("-- prebaked lua"),
            ["models/heroes/test/hero.glb"] = [0x67, 0x6C, 0x54, 0x46]
        };

        // Act
        TestPrebakeHarness.CreateSampleS2zArchive(s2zPath, entries);

        // Assert
        File.Exists(s2zPath).Should().BeTrue();
        using ZipArchive zip = ZipFile.OpenRead(s2zPath);
        zip.Entries.Should().HaveCount(2);
        zip.GetEntry("ui/scripts/newui/hero_definitions_cache.lua").Should().NotBeNull();
        zip.GetEntry("models/heroes/test/hero.glb").Should().NotBeNull();
    }

    [Fact]
    public void PrebakePipeline_WithZstdDisabled_OutputsRawBinary()
    {
        // Arrange
        EntityCacheContainer container = TestPrebakeHarness.CreateSampleCatalog(3, 1);
        string binPath = Path.Combine(_tempOutputDir, "uncompressed_cache.bin");

        // Act
        byte[] rawBytes = _serializer.SerializeToBytes(container, false);
        File.WriteAllBytes(binPath, rawBytes);

        // Assert
        bool isZstd = ZstdCompressionExtensions.IsZstdCompressed(rawBytes);
        isZstd.Should().BeFalse();

        EntityCacheContainer restored = _serializer.DeserializeFromBytes(File.ReadAllBytes(binPath), false);
        restored.Heroes.Should().HaveCount(3);
    }

    [Fact]
    public void PrebakePipeline_IntegrityValidation_MatchesXxHashChecksum()
    {
        // Arrange
        EntityCacheContainer container = TestPrebakeHarness.CreateSampleCatalog(4, 2);
        byte[] payload = _serializer.SerializeToBytes(container, true);
        ulong hash = _serializer.ComputeXxHash64(payload);

        string hashFile = Path.Combine(_tempOutputDir, "entity_cache.hash");
        File.WriteAllText(hashFile, hash.ToString("X16"), Encoding.UTF8);

        // Act
        string readHashText = File.ReadAllText(hashFile).Trim();
        ulong readHash = ulong.Parse(readHashText, NumberStyles.HexNumber);
        bool matches = _serializer.VerifyHash(payload, readHash);

        // Assert
        matches.Should().BeTrue();
    }

    [Fact]
    public void PrebakePipeline_NonExistentDirectory_CreatesDirectoriesOnDemand()
    {
        // Arrange
        string nestedDir = Path.Combine(_tempOutputDir, "nested", "level1", "level2");
        string binPath = Path.Combine(nestedDir, "entity_cache.bin");
        Directory.CreateDirectory(nestedDir);

        EntityCacheContainer container = TestPrebakeHarness.CreateSampleCatalog(1, 1);
        byte[] data = _serializer.SerializeToBytes(container);

        // Act
        File.WriteAllBytes(binPath, data);

        // Assert
        File.Exists(binPath).Should().BeTrue();
    }
}
