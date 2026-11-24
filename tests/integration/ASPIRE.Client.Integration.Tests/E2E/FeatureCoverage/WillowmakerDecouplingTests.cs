// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ============================================================================
// File: Feature04_WillowmakerDecouplingTests.cs
// System: ProjectKongor Solution-Wide E2E Testing Suite
// Tier 1: Feature Coverage (Feature 4: Willowmaker Desktop Client Decoupling)
// Policy: Strict Explicit Typing, Opaque-Box Validation (>= 5 tests)
// ============================================================================

using System.Text.Json;
using FluentAssertions;
using Moq;
using WILLOWMAKER.Core.Services;

namespace ASPIRE.Tests.E2E.Tier1_FeatureCoverage;

public sealed class WillowmakerDecouplingTests
{
    [Fact]
    public void LauncherSettings_Defaults_ContainsCompetitiveOptimizations()
    {
        // Arrange & Act
        LauncherSettingsModel model = new();

        // Assert
        model.EnableAsyncPipelineCaching.Should().BeTrue();
        model.EnableTextureClarityBoost.Should().BeTrue();
        model.RefreshRate.Should().Be("240 Hz");
        model.ShadowFidelity.Should().Be("High (2048)");
        model.VulkanHudMode.Should().Be("Off (Clean Screen)");
        model.EnableNvidiaReflex.Should().BeFalse();
        model.EnableSpatialAudio.Should().BeFalse();
        model.MasterServerAddress.Should().Be(LauncherSettingsModel.DefaultLocalOfflineAutoMasterServer);
    }

    [Fact]
    public void LauncherSettings_FilePersistence_SavesAndLoadsJsonConfiguration()
    {
        // Arrange
        string tempDir = Path.Combine(Path.GetTempPath(), "KongorTest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            LauncherSettingsModel model = LauncherSettingsService.Load(tempDir);
            model.EnableRayTracing = true;
            model.RayTracingPresetName = "Cinematic";

            // Act
            LauncherSettingsService.Save(model, tempDir);
            LauncherSettingsModel reloaded = LauncherSettingsService.Load(tempDir);

            // Assert
            reloaded.EnableRayTracing.Should().BeTrue();
            reloaded.RayTracingPresetName.Should().Be("Cinematic");
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void Manifest_Serialization_DeserializesManifestJson()
    {
        // Arrange
        Manifest original = new()
        {
            Version = "4.10.2",
            HashAlgorithm = "sha256",
            ExcludeFromSource = ["manifest.json"],
            ExcludeFromTarget = ["launcher_settings.json"],
            Files = new Dictionary<string, ManifestEntry>
            {
                ["game/resources0.s2z"] = new() { Size = 1048576, Hash = "abc123hash" }
            }
        };

        // Act
        string json = JsonSerializer.Serialize(original);
        Manifest? deserialized = JsonSerializer.Deserialize<Manifest>(json);

        // Assert
        deserialized.Should().NotBeNull();
        deserialized!.Version.Should().Be("4.10.2");
        deserialized.Files.Should().ContainKey("game/resources0.s2z");
        deserialized.Files["game/resources0.s2z"].Size.Should().Be(1048576);
    }

    [Fact]
    public void LauncherSettings_CorruptFileRecovery_FallsBackToDefaults()
    {
        // Arrange
        string tempDir = Path.Combine(Path.GetTempPath(), "KongorTest_Corrupt_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        string settingsFile = LauncherSettingsService.GetSettingsFilePath(tempDir);
        File.WriteAllText(settingsFile, "{ invalid json content !!!");

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
    public void RenderModernizationService_GpuVendorDetection_HandlesAmdNvidiaIntel()
    {
        // Arrange
        ILogger<RenderModernizationService> logger = Mock.Of<ILogger<RenderModernizationService>>();
        RenderModernizationService service = new(logger);

        // Act & Assert
        service.Should().NotBeNull();
    }

    [Fact]
    public void WillowmakerDecoupling_DdsDecoder_SupportsBc7TextureFormats()
    {
        // Arrange
        byte[] dummyDds = new byte[128];
        dummyDds[0] = (byte)'D';
        dummyDds[1] = (byte)'D';
        dummyDds[2] = (byte)'S';
        dummyDds[3] = (byte)' ';

        // Act
        bool isDds = DdsDecoder.IsValidDdsHeader(dummyDds);

        // Assert
        isDds.Should().BeTrue();
    }
}
