// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ============================================================================
// File: CdnBrokerBoundaryTests.cs
// System: ProjectKongor Solution-Wide E2E Testing Suite
// Tier 2: Boundary & Corner Cases (CDN Broker & Willowmaker Client Boundaries)
// Policy: Strict Explicit Typing, Adversarial Boundary Verification (>= 5 tests)
// ============================================================================

using System.Text.Json;
using FluentAssertions;
using WILLOWMAKER.Core.Services;

namespace ASPIRE.Tests.E2E.Tier2_BoundaryAndCornerCases;

public sealed class CdnBrokerBoundaryTests
{
    [Fact]
    public void Boundary_Manifest_EmptyFilesDictionary_DeserializesSuccessfully()
    {
        // Arrange
        Manifest original = new()
        {
            Version = "1.0.0",
            HashAlgorithm = "sha256",
            ExcludeFromSource = [],
            ExcludeFromTarget = [],
            Files = new Dictionary<string, ManifestEntry>()
        };

        // Act
        string json = JsonSerializer.Serialize(original);
        Manifest? deserialized = JsonSerializer.Deserialize<Manifest>(json);

        // Assert
        deserialized.Should().NotBeNull();
        deserialized!.Files.Should().BeEmpty();
    }

    [Fact]
    public void Boundary_Manifest_ExtremeNumberOfEntries_Parses1000Files()
    {
        // Arrange - Generate 1000 files dictionary
        Dictionary<string, ManifestEntry> files = [];
        for (int i = 0; i < 1000; i++)
            files[$"game/file_{i}.s2z"] = new ManifestEntry { Size = 1024, Hash = $"hash_{i:X8}" };

        Manifest original = new()
        {
            Version = "1.0.0",
            HashAlgorithm = "sha256",
            ExcludeFromSource = [],
            ExcludeFromTarget = [],
            Files = files
        };

        // Act
        string json = JsonSerializer.Serialize(original);
        Manifest? deserialized = JsonSerializer.Deserialize<Manifest>(json);

        // Assert
        deserialized.Should().NotBeNull();
        deserialized!.Files.Should().HaveCount(1000);
    }

    [Fact]
    public void Boundary_Manifest_CorruptJson_ThrowsJsonException()
    {
        // Arrange
        string malformedJson = "{ unclosed json object ...";

        // Act & Assert
        Action act = () => JsonSerializer.Deserialize<Manifest>(malformedJson);
        act.Should().Throw<JsonException>();
    }

    [Fact]
    public void Boundary_DeploymentManifest_IgnoredFiles_ContainsExpectedLogExtensions()
    {
        // Arrange
        string[] ignored = DeploymentManifest.IgnoredFileNames;

        // Assert
        ignored.Should().Contain("WILLOWMAKER.log");
        ignored.Should().Contain("dxvk.log");
        ignored.Should().Contain("d3d9.log");
    }

    [Fact]
    public void Boundary_LauncherSettings_InvalidJsonInFile_RecreatesDefaults()
    {
        // Arrange
        string tempDir = Path.Combine(Path.GetTempPath(), "KongorTest_Brd_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        string settingsFile = LauncherSettingsService.GetSettingsFilePath(tempDir);
        File.WriteAllText(settingsFile, "{ this is not valid json }");

        try
        {
            // Act
            LauncherSettingsModel model = LauncherSettingsService.Load(tempDir);

            // Assert
            model.Should().NotBeNull();
            model.EnableAsyncPipelineCaching.Should().BeTrue();
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void Boundary_DdsDecoder_OneByOneTexture_HandlesBoundaryDimensions()
    {
        // Arrange - 1x1 DXT1 DDS texture
        byte[] ddsHeader = new byte[128 + 8]; // 128 byte header + 8 bytes for 1 4x4 block
        Encoding.ASCII.GetBytes("DDS ").CopyTo(ddsHeader, 0);

        // Act
        bool isValid = DdsDecoder.IsValidDdsHeader(ddsHeader);

        // Assert
        isValid.Should().BeTrue();
    }
}
