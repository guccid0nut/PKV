// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using ASPIRE.Tests.ASPIRE.Common.EntityPrebake.Contracts;
using ASPIRE.Tests.ASPIRE.Common.EntityPrebake.Harness;
using FluentAssertions;

namespace ASPIRE.Tests.ASPIRE.Common.EntityPrebake.Tier2_BoundaryAndCornerCases;

public sealed class SerializationBoundaryTests
{
    private readonly TestMemoryPackSerializer _serializer = new();

    [Fact]
    public void Deserialize_EmptyByteArray_ThrowsArgumentException()
    {
        // Act
        Action act = () => _serializer.DeserializeFromBytes([]);

        // Assert
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Deserialize_CorruptedZstdStream_ThrowsException()
    {
        // Arrange: Byte array starting with Zstd magic number but with corrupted payload
        byte[] corruptedZstd = [0x28, 0xB5, 0x2F, 0xFD, 0xFF, 0xFE, 0xFD, 0xFC, 0x00, 0x11, 0x22];

        // Act
        Action act = () => _serializer.DeserializeFromBytes(corruptedZstd, true);

        // Assert
        act.Should().Throw<Exception>();
    }

    [Fact]
    public void Deserialize_CorruptedPayloadBytes_ThrowsOrRejects()
    {
        // Arrange: Random junk bytes
        byte[] randomBytes = new byte[64];
        new Random(42).NextBytes(randomBytes);

        // Act
        Action act = () => _serializer.DeserializeFromBytes(randomBytes, false);

        // Assert
        act.Should().Throw<Exception>();
    }

    [Fact]
    public void Serialize_ContainerWithZeroEntities_SerializesAndRestoresCleanly()
    {
        // Arrange
        EntityCacheContainer emptyContainer = new()
        {
            Version = 1,
            BuildTimestamp = "2026-08-28T00:00:00Z"
        };

        // Act
        byte[] bytes = _serializer.SerializeToBytes(emptyContainer);
        EntityCacheContainer restored = _serializer.DeserializeFromBytes(bytes);

        // Assert
        restored.Should().NotBeNull();
        restored.Heroes.Should().BeEmpty();
        restored.AltAvatars.Should().BeEmpty();
        restored.Abilities.Should().BeEmpty();
        restored.States.Should().BeEmpty();
        restored.Projectiles.Should().BeEmpty();
        restored.Items.Should().BeEmpty();
    }

    [Fact]
    public void Serialize_ContainerWithExtremelyLargeEntityGraph_SerializesSuccessfully()
    {
        // Arrange: Large catalog with 300 heroes, 3000 alt avatars, 1500 abilities
        EntityCacheContainer largeContainer = TestPrebakeHarness.CreateSampleCatalog(300, 10);

        // Act
        byte[] bytes = _serializer.SerializeToBytes(largeContainer, true);
        EntityCacheContainer restored = _serializer.DeserializeFromBytes(bytes, true);

        // Assert
        restored.Heroes.Should().HaveCount(300);
        restored.AltAvatars.Should().HaveCount(3000);
        restored.Abilities.Should().HaveCount(900);
    }

    [Fact]
    public void VerifyHash_CorruptedData_ReturnsFalse()
    {
        // Arrange
        EntityCacheContainer container = TestPrebakeHarness.CreateSampleCatalog(5, 1);
        byte[] data = _serializer.SerializeToBytes(container);
        ulong originalHash = _serializer.ComputeXxHash64(data);

        // Act: Corrupt one byte
        data[data.Length / 2] ^= 0xFF;
        bool isValid = _serializer.VerifyHash(data, originalHash);

        // Assert
        isValid.Should().BeFalse();
    }
}
