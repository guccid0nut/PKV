// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using ASPIRE.Common.Prebaking.Serialization;
using FluentAssertions;
using NEXUS.Contracts.EntityCache;
using Xunit;

namespace ASPIRE.Prebaking.Tests.Serialization;

public sealed class EntityCacheSerializationTests
{
    private static EntityCacheContainer CreateTestContainer()
    {
        EntityCacheContainer container = new()
        {
            Version = 1,
            BuildTimestampUtc = TimeProvider.System.GetUtcNow().ToString("O"),
            BuildTimestamp = TimeProvider.System.GetUtcNow().ToString("O"),
            Heroes =
            [
                new HeroDefinitionDto
                {
                    Id = 1,
                    Name = "Hero_Accursed",
                    DisplayName = "Accursed",
                    PrimaryAttribute = "strength",
                    Strength = 23f,
                    Abilities = [new AbilityDefinitionDto { Name = "Ability_Accursed1", Slot = 0 }],
                    AltAvatars = [new AltAvatarDto { Key = "Hero_Accursed.Alt", HeroName = "Hero_Accursed" }]
                }
            ],
            AltAvatars =
            [
                new AltAvatarDto { Key = "Hero_Accursed.Alt", HeroName = "Hero_Accursed" }
            ],
            Abilities =
            [
                new AbilityDefinitionDto { Name = "Ability_Accursed1", Slot = 0 }
            ],
            States =
            [
                new StateDefinitionDto { Name = "State_Accursed1" }
            ],
            Projectiles =
            [
                new ProjectileDefinitionDto { Name = "Projectile_Accursed1", Speed = 1000f }
            ],
            Items =
            [
                new ItemDefinitionDto { Name = "Item_Bottle", Cost = 600 }
            ],
            CosmeticUpgrades =
            [
                new CosmeticUpgradeDto { Name = "afghanistan", Category = "ChatSymbol" }
            ]
        };

        return container;
    }

    [Fact]
    public void SerializeAndDeserialize_Roundtrip_RestoresExactObjectGraph()
    {
        // Arrange
        EntityCacheContainer original = CreateTestContainer();

        // Act
        byte[] serializedBytes = EntityCacheSerializer.Serialize(original, 3, true);
        EntityCacheContainer deserialized = EntityCacheDeserializer.Deserialize(serializedBytes);

        // Assert
        deserialized.Should().NotBeNull();
        deserialized.Version.Should().Be(original.Version);
        deserialized.Heroes.Should().HaveCount(1);
        deserialized.Heroes[0].Name.Should().Be("Hero_Accursed");
        deserialized.Heroes[0].Strength.Should().Be(23f);
        deserialized.AltAvatars.Should().HaveCount(1);
        deserialized.Abilities.Should().HaveCount(1);
        deserialized.States.Should().HaveCount(1);
        deserialized.Projectiles.Should().HaveCount(1);
        deserialized.Items.Should().HaveCount(1);
        deserialized.CosmeticUpgrades.Should().HaveCount(1);
    }

    [Fact]
    public void EntityCacheSerialization_Serialize_WritesValid32ByteHeader()
    {
        // Arrange
        EntityCacheContainer original = CreateTestContainer();

        // Act
        byte[] serializedBytes = EntityCacheSerializer.Serialize(original, 3, true);

        // Assert
        serializedBytes.Length.Should().BeGreaterThan(EntityCacheHeader.HeaderSize);
        EntityCacheHeader header = EntityCacheHeader.ReadFrom(serializedBytes);

        header.Magic.Should().Be(EntityCacheHeader.ExpectedMagic);
        header.FormatVersion.Should().Be(EntityCacheHeader.CurrentVersion);
        header.CompressionType.Should().Be(1); // Zstd
        header.EntityCount.Should().Be(7);
        header.UncompressedSize.Should().BeGreaterThan(0);
        header.XxHash3Checksum.Should().NotBe(0);
    }

    [Fact]
    public void Deserialize_WithCorruptedPayload_ThrowsInvalidDataException()
    {
        // Arrange
        EntityCacheContainer original = CreateTestContainer();
        byte[] serializedBytes = EntityCacheSerializer.Serialize(original, 3, true);

        // Corrupt a byte in the compressed payload (after the 32-byte header)
        serializedBytes[EntityCacheHeader.HeaderSize + 5] ^= 0xFF;

        // Act & Assert
        Action act = () => EntityCacheDeserializer.Deserialize(serializedBytes);
        act.Should().Throw<Exception>();
    }

    [Fact]
    public void Deserialize_WithInvalidMagic_ThrowsInvalidDataException()
    {
        // Arrange
        EntityCacheContainer original = CreateTestContainer();
        byte[] serializedBytes = EntityCacheSerializer.Serialize(original, 3, true);

        // Corrupt magic bytes
        serializedBytes[0] = 0x00;

        // Act & Assert
        Action act = () => EntityCacheDeserializer.Deserialize(serializedBytes);
        act.Should().Throw<InvalidDataException>().WithMessage("*magic*");
    }

    [Fact]
    public async Task SerializeToFileAsync_AndValidateFile_Succeeds()
    {
        // Arrange
        EntityCacheContainer original = CreateTestContainer();
        string tempFile = Path.Combine(Path.GetTempPath(), $"entity_cache_test_{Guid.NewGuid():N}.bin");

        try
        {
            // Act
            await EntityCacheSerializer.SerializeToFileAsync(original, tempFile, 3);
            ValidationResult valResult = EntityCacheDeserializer.ValidateFile(tempFile);
            EntityCacheContainer loaded = await EntityCacheDeserializer.DeserializeFromFileAsync(tempFile);

            // Assert
            valResult.IsValid.Should().BeTrue();
            valResult.Header.EntityCount.Should().Be(7);
            loaded.Heroes.Should().HaveCount(1);
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }
}
