// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using ASPIRE.Common.Netcode;
using FluentAssertions;
using Xunit;

namespace Kongor.Core.Tests.Netcode;

[Collection("ZstdNetcode")]
public sealed class ZstdNetcodeDictionaryTests
{
    [Fact]
    public void ZstdNetcodeDictionary_Instance_ShouldLoadValid64KBDictionary()
    {
        // Arrange & Act
        ZstdNetcodeDictionary dict = ZstdNetcodeDictionary.Instance;

        // Assert
        dict.Should().NotBeNull();
        dict.IsLoaded.Should().BeTrue();
        dict.DictionaryBytes.Length.Should().BeGreaterThan(10000);
        dict.DictionarySpan.Length.Should().BeGreaterThan(10000);
        dict.DictionaryId.Should().Be(ZstdNetcodeDictionary.DefaultDictionaryId);
    }

    [Fact]
    public void IsValidDictionary_WithValidHeader_ShouldReturnTrue()
    {
        // Arrange
        byte[] valid = new byte[65536];
        valid[0] = 0x37; valid[1] = 0xA4; valid[2] = 0x30; valid[3] = 0xEC; // Magic 0xEC30A437
        valid[4] = 0x31; valid[5] = 0x4E; valid[6] = 0x4F; valid[7] = 0x48; // Dict ID "HON1"

        // Act
        bool isValid = ZstdNetcodeDictionary.IsValidDictionary(valid);

        // Assert
        isValid.Should().BeTrue();
    }

    [Fact]
    public void IsValidDictionary_WithInvalidMagic_ShouldReturnFalse()
    {
        // Arrange
        byte[] invalid = new byte[64];
        invalid[0] = 0x00; invalid[1] = 0x00; invalid[2] = 0x00; invalid[3] = 0x00;

        // Act
        bool isValid = ZstdNetcodeDictionary.IsValidDictionary(invalid);

        // Assert
        isValid.Should().BeFalse();
    }

    [Fact]
    public void IsValidDictionary_WithShortBuffer_ShouldReturnFalse()
    {
        // Arrange
        byte[] tooShort = [0x37, 0xA4];

        // Act
        bool isValid = ZstdNetcodeDictionary.IsValidDictionary(tooShort);

        // Assert
        isValid.Should().BeFalse();
    }

    [Fact]
    public void LoadCustomDictionary_WithValidBuffer_ShouldUpdateDictionary()
    {
        // Arrange - Isolated instance prevents mutating global singleton during concurrent test runs
        using ZstdNetcodeDictionary isolated = ZstdNetcodeDictionary.CreateIsolatedForTesting();
        var corpus = ZstdDictionaryTrainer.GenerateTrainingCorpus(30);
        byte[] custom = ZstdDictionaryTrainer.BuildStaticDictionary(corpus);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(custom.AsSpan(4, 4), 0x484F4E32); // "HON2"

        // Act
        isolated.LoadCustomDictionary(custom);

        // Assert
        isolated.DictionaryId.Should().Be(0x484F4E32);
        isolated.DictionaryBytes.Length.Should().Be(custom.Length);
        isolated.CompiledDictionary.Should().NotBeNull();
        ZstdNetcodeDictionary.Instance.DictionaryId.Should().Be(ZstdNetcodeDictionary.DefaultDictionaryId);
    }

    [Fact]
    public void ValidateAndSet_WithInvalidBuffer_ShouldPreserveExistingCompiledDictionary()
    {
        // Arrange
        using ZstdNetcodeDictionary isolated = ZstdNetcodeDictionary.CreateIsolatedForTesting();
        var originalCompiled = isolated.CompiledDictionary;
        byte[] badPayload = new byte[64];

        // Act
        bool updated = isolated.ValidateAndSet(badPayload);

        // Assert
        updated.Should().BeFalse();
        isolated.IsLoaded.Should().BeTrue();
        isolated.CompiledDictionary.Should().BeSameAs(originalCompiled);
        isolated.DictionaryId.Should().Be(ZstdNetcodeDictionary.DefaultDictionaryId);
    }

    [Fact]
    public void ZstdNetcodeDictionary_Instance_ShouldCompileValidZstandardDictionary()
    {
        // 1. Ensure trained static dictionary is baked to resource path
        string resourcePath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../../../src/libs/protocol-core/Resources/hon_netcode.zstd.dict"));
        if (!File.Exists(resourcePath) || new FileInfo(resourcePath).Length == 0)
        {
            ZstdDictionaryTrainer.GenerateAndSaveDictionary(resourcePath);
        }

        // 2. Arrange & Act
        ZstdNetcodeDictionary dict = ZstdNetcodeDictionary.Instance;
        dict.EnsureLoaded();

        // 3. Assert
        dict.Should().NotBeNull();
        dict.IsLoaded.Should().BeTrue();
        dict.CompiledDictionary.Should().NotBeNull();
        dict.DecompressionOptions.Should().NotBeNull();
        dict.DecompressionOptions!.Dictionary.Should().BeSameAs(dict.CompiledDictionary);

        var compOpt1 = dict.GetCompressionOptions(1);
        compOpt1.Should().NotBeNull();
        compOpt1!.Dictionary.Should().BeSameAs(dict.CompiledDictionary);
        compOpt1.Quality.Should().Be(1);

        var compOpt3 = dict.GetCompressionOptions(3);
        compOpt3.Should().NotBeNull();
        compOpt3!.Quality.Should().Be(3);
    }

    [Fact]
    public void ZstdNetcodeDictionary_EnsureLoaded_PreWarmsOptionsWithoutExceptions()
    {
        // Arrange & Act & Assert
        ZstdNetcodeDictionary.Instance.EnsureLoaded();
        ZstdNetcodeDictionary.Instance.IsLoaded.Should().BeTrue();
        ZstdNetcodeDictionary.Instance.CompiledDictionary.Should().NotBeNull();
    }

    [Fact]
    public void EmbeddedDictionaryResource_ShouldCompileDirectlyViaZstandardDictionaryCreate()
    {
        // Arrange
        var assembly = typeof(ZstdNetcodeDictionary).Assembly;
        string resourceName = "ASPIRE.Common.Resources.hon_netcode.zstd.dict";
        using Stream? stream = assembly.GetManifestResourceStream(resourceName);
        stream.Should().NotBeNull($"Embedded resource '{resourceName}' must exist in {assembly.FullName}");

        using MemoryStream ms = new();
        stream!.CopyTo(ms);
        byte[] dictBytes = ms.ToArray();

        // Assert size and header
        dictBytes.Length.Should().Be(65536);
        ZstdNetcodeDictionary.IsValidDictionary(dictBytes).Should().BeTrue();

        // Act
        var dict = System.IO.Compression.ZstandardDictionary.Create(dictBytes);

        // Assert
        dict.Should().NotBeNull();
        ZstdNetcodeDictionary.Instance.LoadSource.Should().BeOneOf(
            ZstdDictionaryLoadSource.EmbeddedResource,
            ZstdDictionaryLoadSource.EnvironmentOverride);
    }
}
