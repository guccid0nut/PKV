// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using ASPIRE.Common.Prebaking.Serialization;
using FluentAssertions;
using NEXUS.Contracts.EntityCache;
using Xunit;

namespace ASPIRE.Prebaking.Tests.Challenger;

public sealed class AdversarialCorruptionTests
{
    private static EntityCacheContainer CreateTestContainer()
    {
        return new EntityCacheContainer
        {
            Version = 1,
            BuildTimestampUtc = TimeProvider.System.GetUtcNow().ToString("O"),
            BuildTimestamp = TimeProvider.System.GetUtcNow().ToString("O"),
            Heroes =
            [
                new HeroDefinitionDto
                {
                    Id = 1,
                    Name = "Hero_Adversarial",
                    DisplayName = "Adversarial Hero",
                    Strength = 42.5f,
                    Agility = 18.0f,
                    Intelligence = 30.2f,
                    PrimaryAttribute = "strength"
                }
            ],
            AltAvatars =
            [
                new AltAvatarDto { Key = "Hero_Adversarial.Alt1", HeroName = "Hero_Adversarial" }
            ],
            Abilities =
            [
                new AbilityDefinitionDto { Name = "Ability_Adversarial_Nuke", Slot = 0, MaxLevel = 4 }
            ],
            States =
            [
                new StateDefinitionDto { Name = "State_Adversarial_Stun", Duration = 2000, Stunned = true }
            ],
            Projectiles =
            [
                new ProjectileDefinitionDto { Name = "Projectile_Adversarial_Bolt", Speed = 1500f }
            ],
            Items =
            [
                new ItemDefinitionDto { Name = "Item_Adversarial_Sword", Cost = 5000 }
            ],
            CosmeticUpgrades =
            [
                new CosmeticUpgradeDto { Name = "adver_icon", Category = "AccountIcon" }
            ]
        };
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void Deserialize_MagicByteCorruption_ThrowsInvalidDataException(int byteOffset)
    {
        EntityCacheContainer original = CreateTestContainer();
        byte[] serializedBytes = EntityCacheSerializer.Serialize(original, 3, true);

        // Tamper with magic byte at specific offset
        serializedBytes[byteOffset] ^= 0xAA;

        Action act = () => EntityCacheDeserializer.Deserialize(serializedBytes);
        act.Should().Throw<InvalidDataException>()
            .WithMessage("*magic*");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    [InlineData(99)]
    [InlineData(65535)]
    public void Deserialize_FormatVersionMismatch_ThrowsInvalidDataException(ushort badVersion)
    {
        EntityCacheContainer original = CreateTestContainer();
        byte[] serializedBytes = EntityCacheSerializer.Serialize(original, 3, true);

        // Write bad version at offset 4..5
        serializedBytes[4] = (byte)(badVersion & 0xFF);
        serializedBytes[5] = (byte)((badVersion >> 8) & 0xFF);

        Action act = () => EntityCacheDeserializer.Deserialize(serializedBytes);
        act.Should().Throw<InvalidDataException>()
            .WithMessage("*version*");
    }

    [Fact]
    public void Deserialize_UncompressedSizeMismatch_ThrowsInvalidDataException()
    {
        EntityCacheContainer original = CreateTestContainer();
        byte[] serializedBytes = EntityCacheSerializer.Serialize(original, 3, true);

        // Corrupt uncompressed size at offset 16
        serializedBytes[16] ^= 0xFF;

        Action act = () => EntityCacheDeserializer.Deserialize(serializedBytes);
        act.Should().Throw<Exception>();
    }

    [Fact]
    public void Deserialize_XxHash3ChecksumFieldTampered_ThrowsInvalidDataException()
    {
        EntityCacheContainer original = CreateTestContainer();
        byte[] serializedBytes = EntityCacheSerializer.Serialize(original, 3, true);

        // Corrupt checksum in header at offset 24..31
        serializedBytes[24] ^= 0x01;

        Action act = () => EntityCacheDeserializer.Deserialize(serializedBytes);
        act.Should().Throw<InvalidDataException>()
            .WithMessage("*Checksum mismatch*");
    }

    [Theory]
    [InlineData(0)] // Start of compressed payload
    [InlineData(10)] // Early payload block
    [InlineData(50)] // Middle payload
    public void Deserialize_ZstdPayloadByteFlip_ThrowsException(int payloadOffset)
    {
        EntityCacheContainer original = CreateTestContainer();
        byte[] serializedBytes = EntityCacheSerializer.Serialize(original, 3, true);

        int targetIndex = EntityCacheHeader.HeaderSize + payloadOffset;
        if (targetIndex < serializedBytes.Length) serializedBytes[targetIndex] ^= 0x55;

        Action act = () => EntityCacheDeserializer.Deserialize(serializedBytes);
        act.Should().Throw<Exception>();
    }

    [Fact]
    public void Deserialize_RawUncompressedPayloadByteFlip_DetectsXxHash3ChecksumMismatch()
    {
        EntityCacheContainer original = CreateTestContainer();
        // Serialize in RAW (uncompressed) mode
        byte[] rawBytes = EntityCacheSerializer.Serialize(original, compress: false);

        // Flip a byte in the raw MemoryPack payload
        rawBytes[EntityCacheHeader.HeaderSize + 10] ^= 0xFF;

        Action act = () => EntityCacheDeserializer.Deserialize(rawBytes);
        act.Should().Throw<InvalidDataException>()
            .WithMessage("*Checksum mismatch*");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(10)]
    [InlineData(31)]
    public void Deserialize_TruncatedBuffer_ThrowsInvalidDataException(int bufferSize)
    {
        byte[] truncated = new byte[bufferSize];
        Action act = () => EntityCacheDeserializer.Deserialize(truncated);
        act.Should().Throw<InvalidDataException>();
    }

    [Fact]
    public async Task ValidateFile_And_DeserializeFromFileAsync_WithDiskCorruption_DetectsMismatch()
    {
        EntityCacheContainer original = CreateTestContainer();
        string tempFile = Path.Combine(Path.GetTempPath(), $"adversarial_corr_{Guid.NewGuid():N}.bin");

        try
        {
            await EntityCacheSerializer.SerializeToFileAsync(original, tempFile, 3);

            // Verify initial file is valid
            ValidationResult initialVal = EntityCacheDeserializer.ValidateFile(tempFile);
            initialVal.IsValid.Should().BeTrue();

            // Tamper file on disk: flip byte at offset 35
            byte[] fileBytes = await File.ReadAllBytesAsync(tempFile);
            fileBytes[35] ^= 0xFF;
            await File.WriteAllBytesAsync(tempFile, fileBytes);

            // Verify ValidateFile returns false
            ValidationResult tamperedVal = EntityCacheDeserializer.ValidateFile(tempFile);
            tamperedVal.IsValid.Should().BeFalse();
            tamperedVal.Message.Should().NotBeNullOrWhiteSpace();

            // Verify DeserializeFromFileAsync throws
            Func<Task> act = async () => await EntityCacheDeserializer.DeserializeFromFileAsync(tempFile);
            await act.Should().ThrowAsync<Exception>();
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    [Fact]
    public void ValidateFile_NonExistentFile_ReturnsInvalid()
    {
        string nonExistentPath = Path.Combine(Path.GetTempPath(), $"does_not_exist_{Guid.NewGuid():N}.bin");
        ValidationResult val = EntityCacheDeserializer.ValidateFile(nonExistentPath);

        val.IsValid.Should().BeFalse();
        val.Message.Should().Contain("does not exist");
    }

    [Fact]
    public async Task DeserializeFromFileAsync_NonExistentFile_ThrowsFileNotFoundException()
    {
        string nonExistentPath = Path.Combine(Path.GetTempPath(), $"does_not_exist_{Guid.NewGuid():N}.bin");
        Func<Task> act = async () => await EntityCacheDeserializer.DeserializeFromFileAsync(nonExistentPath);

        await act.Should().ThrowAsync<FileNotFoundException>();
    }
}
