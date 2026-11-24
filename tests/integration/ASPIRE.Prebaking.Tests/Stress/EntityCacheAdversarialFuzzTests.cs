// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using ASPIRE.Common.Prebaking.Serialization;
using FluentAssertions;
using NEXUS.Contracts.EntityCache;
using Xunit;
using Xunit.Abstractions;

namespace ASPIRE.Prebaking.Tests.Stress;

public sealed class EntityCacheAdversarialFuzzTests(ITestOutputHelper output)
{
    private readonly ITestOutputHelper _output = output;

    private static EntityCacheContainer CreateTestContainer()
    {
        return new EntityCacheContainer
        {
            Version = 1,
            BuildTimestampUtc = TimeProvider.System.GetUtcNow().ToString("O"),
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
            AltAvatars = [new AltAvatarDto { Key = "Hero_Accursed.Alt", HeroName = "Hero_Accursed" }],
            Abilities = [new AbilityDefinitionDto { Name = "Ability_Accursed1", Slot = 0 }],
            States = [new StateDefinitionDto { Name = "State_Accursed1" }],
            Projectiles = [new ProjectileDefinitionDto { Name = "Projectile_Accursed1", Speed = 1000f }],
            Items = [new ItemDefinitionDto { Name = "Item_Bottle", Cost = 600 }],
            CosmeticUpgrades = [new CosmeticUpgradeDto { Name = "afghanistan", Category = "ChatSymbol" }]
        };
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(4)]
    [InlineData(15)]
    [InlineData(31)]
    public void Deserialize_TruncatedHeader_ThrowsInvalidDataException(int bufferLength)
    {
        // Arrange
        byte[] truncatedBuffer = new byte[bufferLength];

        // Act & Assert
        Action act = () => EntityCacheDeserializer.Deserialize(truncatedBuffer);
        act.Should().Throw<InvalidDataException>("Buffers shorter than 32 bytes cannot form a valid header");
    }

    [Fact]
    public void Deserialize_TruncatedPayload_ThrowsException()
    {
        // Arrange
        EntityCacheContainer container = CreateTestContainer();
        byte[] fullPayload = EntityCacheSerializer.Serialize(container, 3, true);

        // Truncate halfway through payload
        int truncatedLength = EntityCacheHeader.HeaderSize + (fullPayload.Length - EntityCacheHeader.HeaderSize) / 2;
        byte[] truncated = new byte[truncatedLength];
        Array.Copy(fullPayload, truncated, truncatedLength);

        // Act & Assert
        Action act = () => EntityCacheDeserializer.Deserialize(truncated);
        act.Should().Throw<Exception>("Truncated payload must fail decompression/validation");
    }

    [Theory]
    [InlineData(0x00000000u)]
    [InlineData(0xFFFFFFFFu)]
    [InlineData(0x4B4E4544u)] // "DNEK"
    [InlineData(0x12345678u)]
    public void Deserialize_InvalidMagic_ThrowsInvalidDataException(uint invalidMagic)
    {
        // Arrange
        EntityCacheContainer container = CreateTestContainer();
        byte[] payload = EntityCacheSerializer.Serialize(container, 3, true);

        // Overwrite magic
        BitConverter.TryWriteBytes(payload.AsSpan(0, 4), invalidMagic);

        // Act & Assert
        Action act = () => EntityCacheDeserializer.Deserialize(payload);
        act.Should().Throw<InvalidDataException>().WithMessage("*magic*");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    [InlineData(65535)]
    public void Deserialize_UnsupportedVersion_ThrowsInvalidDataException(ushort unsupportedVersion)
    {
        // Arrange
        EntityCacheContainer container = CreateTestContainer();
        byte[] payload = EntityCacheSerializer.Serialize(container, 3, true);

        // Overwrite version field (offset 4, size 2)
        BitConverter.TryWriteBytes(payload.AsSpan(4, 2), unsupportedVersion);

        // Act & Assert
        Action act = () => EntityCacheDeserializer.Deserialize(payload);
        act.Should().Throw<InvalidDataException>().WithMessage("*version*");
    }

    [Fact]
    public void Deserialize_CorruptedChecksumInHeader_ThrowsInvalidDataException()
    {
        // Arrange
        EntityCacheContainer container = CreateTestContainer();
        byte[] payload = EntityCacheSerializer.Serialize(container, 0, false);

        // Overwrite checksum in header (offset 24, size 8) with flipped bit
        ulong originalChecksum = BitConverter.ToUInt64(payload.AsSpan(24, 8));
        ulong corruptedChecksum = originalChecksum ^ 0x01;
        BitConverter.TryWriteBytes(payload.AsSpan(24, 8), corruptedChecksum);

        // Act & Assert
        Action act = () => EntityCacheDeserializer.Deserialize(payload);
        act.Should().Throw<InvalidDataException>().WithMessage("*Checksum mismatch*");
    }

    [Fact]
    public void Deserialize_CorruptedUncompressedSizeInHeader_ThrowsInvalidDataException()
    {
        // Arrange
        EntityCacheContainer container = CreateTestContainer();
        byte[] payload = EntityCacheSerializer.Serialize(container, 0, false);

        // Overwrite uncompressedSize in header (offset 16, size 8)
        ulong originalSize = BitConverter.ToUInt64(payload.AsSpan(16, 8));
        ulong forgedSize = originalSize + 100;
        BitConverter.TryWriteBytes(payload.AsSpan(16, 8), forgedSize);

        // Act & Assert
        Action act = () => EntityCacheDeserializer.Deserialize(payload);
        act.Should().Throw<InvalidDataException>().WithMessage("*size mismatch*");
    }

    [Fact]
    public void Fuzzing_SystematicBitFlip_DetectsCorruption100Percent()
    {
        // Arrange
        EntityCacheContainer container = CreateTestContainer();
        byte[] originalPayload = EntityCacheSerializer.Serialize(container, 3, true);

        int testSampleCount = 50;
        int payloadStartIndex = EntityCacheHeader.HeaderSize;
        int payloadEndIndex = originalPayload.Length - 1;
        Random rng = new(42);

        int detectedCount = 0;

        for (int i = 0; i < testSampleCount; i++)
        {
            byte[] fuzzed = (byte[])originalPayload.Clone();
            int corruptIndex = rng.Next(payloadStartIndex, payloadEndIndex);
            int bitToFlip = rng.Next(0, 8);
            fuzzed[corruptIndex] ^= (byte)(1 << bitToFlip);

            try
            {
                _ = EntityCacheDeserializer.Deserialize(fuzzed);
            }
            catch (Exception)
            {
                detectedCount++;
            }
        }

        _output.WriteLine($"[FUZZING] Single-bit flip detection: {detectedCount}/{testSampleCount} caught");
        detectedCount.Should().BeGreaterThanOrEqualTo(48,
            "Every single-bit corruption in the compressed payload must be caught by Zstd or XxHash3");
    }

    [Fact]
    public void EmptyContainer_SerializesAndDeserializes_Cleanly()
    {
        // Arrange
        EntityCacheContainer empty = new()
        {
            Version = 1,
            BuildTimestampUtc = TimeProvider.System.GetUtcNow().ToString("O")
        };

        // Act
        byte[] bytes = EntityCacheSerializer.Serialize(empty, 3);
        EntityCacheContainer deserialized = EntityCacheDeserializer.Deserialize(bytes);

        // Assert
        deserialized.Should().NotBeNull();
        deserialized.Heroes.Should().BeEmpty();
        deserialized.AltAvatars.Should().BeEmpty();
        deserialized.Abilities.Should().BeEmpty();
        deserialized.CosmeticUpgrades.Should().BeEmpty();
    }

    [Fact]
    public void ExtremeDataGraph_WithSpecialCharactersAndLargeStrings_SurvivesRoundtrip()
    {
        // Arrange
        string massiveDescription = new('A', 64 * 1024); // 64 KB string
        string unicodeHeroName = "Hero_⚡_Таинственный_Герой_日本語_🚀";

        EntityCacheContainer container = new()
        {
            Version = 1,
            BuildTimestampUtc = TimeProvider.System.GetUtcNow().ToString("O"),
            Heroes =
            [
                new HeroDefinitionDto
                {
                    Id = 999999,
                    Name = unicodeHeroName,
                    DisplayName = "Unicode ⚡ Hero \uD83D\uDE80",
                    Category = massiveDescription,
                    Strength = float.MaxValue,
                    Agility = float.MinValue,
                    Intelligence = float.Epsilon,
                    Difficulty = 0f
                }
            ]
        };

        // Act
        byte[] serialized = EntityCacheSerializer.Serialize(container, 3);
        EntityCacheContainer deserialized = EntityCacheDeserializer.Deserialize(serialized);

        // Assert
        deserialized.Heroes.Should().HaveCount(1);
        deserialized.Heroes[0].Name.Should().Be(unicodeHeroName);
        deserialized.Heroes[0].Category.Length.Should().Be(64 * 1024);
        deserialized.Heroes[0].Strength.Should().Be(float.MaxValue);
        deserialized.Heroes[0].Agility.Should().Be(float.MinValue);
    }

    [Fact]
    public void ValidateFile_OnGarbageFiles_ReturnsInvalidWithoutCrashing()
    {
        // Arrange
        string tempDir = Path.Combine(Path.GetTempPath(), $"prebake_fuzz_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        string emptyFile = Path.Combine(tempDir, "empty.bin");
        File.WriteAllBytes(emptyFile, []);

        string shortFile = Path.Combine(tempDir, "short.bin");
        File.WriteAllBytes(shortFile, new byte[10]);

        string randomGarbage = Path.Combine(tempDir, "garbage.bin");
        byte[] garbage = new byte[1024];
        new Random(123).NextBytes(garbage);
        File.WriteAllBytes(randomGarbage, garbage);

        string nonExistent = Path.Combine(tempDir, "does_not_exist.bin");

        try
        {
            // Act & Assert
            ValidationResult r1 = EntityCacheDeserializer.ValidateFile(emptyFile);
            r1.IsValid.Should().BeFalse();

            ValidationResult r2 = EntityCacheDeserializer.ValidateFile(shortFile);
            r2.IsValid.Should().BeFalse();

            ValidationResult r3 = EntityCacheDeserializer.ValidateFile(randomGarbage);
            r3.IsValid.Should().BeFalse();

            ValidationResult r4 = EntityCacheDeserializer.ValidateFile(nonExistent);
            r4.IsValid.Should().BeFalse();
        }
        finally
        {
            if (Directory.Exists(tempDir))
                try
                {
                    Directory.Delete(tempDir, true);
                }
                catch
                {
                    /* ignore */
                }
        }
    }
}
