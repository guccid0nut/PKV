// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Diagnostics;
using ASPIRE.Common.Utilities;
using ASPIRE.Tests.ASPIRE.Common.EntityPrebake.Contracts;
using ASPIRE.Tests.ASPIRE.Common.EntityPrebake.Harness;
using FluentAssertions;

namespace ASPIRE.Tests.ASPIRE.Common.EntityPrebake.Tier1_FeatureCoverage;

public sealed class MemoryPackSerializationTests
{
    private readonly TestMemoryPackSerializer _serializer = new();

    [Fact]
    public void SerializeAndDeserialize_Roundtrip_RestoresAllCatalogEntities()
    {
        // Arrange
        EntityCacheContainer original = TestPrebakeHarness.CreateSampleCatalog(15);

        // Act
        byte[] serialized = _serializer.SerializeToBytes(original);
        EntityCacheContainer deserialized = _serializer.DeserializeFromBytes(serialized);

        // Assert
        deserialized.Should().NotBeNull();
        deserialized.Version.Should().Be(original.Version);
        deserialized.Heroes.Should().HaveCount(original.Heroes.Count);
        deserialized.AltAvatars.Should().HaveCount(original.AltAvatars.Count);
        deserialized.Abilities.Should().HaveCount(original.Abilities.Count);
        deserialized.States.Should().HaveCount(original.States.Count);
        deserialized.Projectiles.Should().HaveCount(original.Projectiles.Count);
        deserialized.Items.Should().HaveCount(original.Items.Count);

        deserialized.Heroes[0].DisplayName.Should().Be(original.Heroes[0].DisplayName);
        deserialized.Heroes[0].AbilityNames.Should().Equal(original.Heroes[0].AbilityNames);
    }

    [Fact]
    public void MemoryPackSerialization_SerializeWithZstd_ReducesPayloadSizeSignificantly()
    {
        // Arrange
        EntityCacheContainer original = TestPrebakeHarness.CreateSampleCatalog(50, 4);

        // Act
        byte[] uncompressed = _serializer.SerializeToBytes(original);
        byte[] compressed = _serializer.SerializeToBytes(original, true);

        // Assert
        compressed.Length.Should().BeLessThan(uncompressed.Length);
        double ratio = (double)compressed.Length / uncompressed.Length;
        ratio.Should().BeLessThan(0.7, "Zstd compression should reduce catalog size by at least 30%");
    }

    [Fact]
    public void DecompressZstd_CompressedStream_MatchesOriginalDecompressedData()
    {
        // Arrange
        EntityCacheContainer original = TestPrebakeHarness.CreateSampleCatalog(20, 2);
        byte[] compressed = _serializer.SerializeToBytes(original, true);

        // Act
        bool isZstd = ZstdCompressionExtensions.IsZstdCompressed(compressed);
        EntityCacheContainer deserialized = _serializer.DeserializeFromBytes(compressed, true);

        // Assert
        isZstd.Should().BeTrue();
        deserialized.Heroes.Should().HaveCount(20);
        deserialized.AltAvatars.Should().HaveCount(40);
    }

    [Fact]
    public void MemoryPackSerialization_XxHash3Validation_EnsuresBinaryIntegrity()
    {
        // Arrange
        EntityCacheContainer container = TestPrebakeHarness.CreateSampleCatalog(10, 2);
        byte[] serialized = _serializer.SerializeToBytes(container, true);

        // Act
        ulong checksum = _serializer.ComputeXxHash64(serialized);
        bool isValid = _serializer.VerifyHash(serialized, checksum);

        // Assert
        checksum.Should().NotBe(0UL);
        isValid.Should().BeTrue();
    }

    [Fact]
    public void MemoryPackSerialization_VersionHeader_PreservesContainerVersionAndTimestamp()
    {
        // Arrange
        string timestamp = TimeProvider.System.GetUtcNow().UtcDateTime.ToString("o");
        EntityCacheContainer container = new()
        {
            Version = 42,
            BuildTimestamp = timestamp
        };

        // Act
        byte[] bytes = _serializer.SerializeToBytes(container);
        EntityCacheContainer result = _serializer.DeserializeFromBytes(bytes);

        // Assert
        result.Version.Should().Be(42);
        result.BuildTimestamp.Should().Be(timestamp);
    }

    [Fact]
    public void MemoryPackSerialization_HighThroughputDeserialization_ExecutesRapidly()
    {
        // Arrange
        EntityCacheContainer container = TestPrebakeHarness.CreateSampleCatalog(140, 5);
        byte[] bytes = _serializer.SerializeToBytes(container);

        // Act
        Stopwatch sw = Stopwatch.StartNew();
        EntityCacheContainer result = _serializer.DeserializeFromBytes(bytes);
        sw.Stop();

        // Assert
        result.Heroes.Should().HaveCount(140);
        sw.ElapsedMilliseconds.Should().BeLessThan(500, "In-memory deserialization should be ultra-fast");
    }
}
