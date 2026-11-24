// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ============================================================================
// File: ModdingAndClientDistributionJourneyE2ETests.cs
// System: ProjectKongor Solution-Wide E2E Testing Suite
// Tier 4: Real-World Application Scenarios (Modding & Client Distribution Journey)
// Policy: Strict Explicit Typing, Multi-Stage Pipeline Verification
// ============================================================================

using System.Text.Json;
using FluentAssertions;
using WILLOWMAKER.Core.Services;

namespace ASPIRE.Tests.E2E.Tier4_RealWorldScenarios;

public sealed class ModdingAndClientDistributionJourneyE2ETests
{
    [Fact]
    public void Journey_FullClientUpdateAndModPipeline_CompletesSuccessfully()
    {
        // ---------------------------------------------------------------------
        // Stage 1: CDN Manifest Parsing & File Verification
        // ---------------------------------------------------------------------
        Manifest manifest = new()
        {
            Version = "4.10.2",
            HashAlgorithm = "SHA256",
            ExcludeFromSource = [],
            ExcludeFromTarget = [],
            Files = new Dictionary<string, ManifestEntry>
            {
                ["game/resources0.s2z"] = new() { Size = 1024 * 1024, Hash = "abc123hash" },
                ["k2_x64.dll"] = new() { Size = 500 * 1024, Hash = "def456hash" }
            }
        };

        string json = JsonSerializer.Serialize(manifest);
        Manifest? deserialized = JsonSerializer.Deserialize<Manifest>(json);

        deserialized.Should().NotBeNull();
        deserialized!.Files.Should().ContainKey("game/resources0.s2z");
        deserialized.Version.Should().Be("4.10.2");

        // ---------------------------------------------------------------------
        // Stage 2: Willowmaker Launcher Settings Modernization
        // ---------------------------------------------------------------------
        string tempDir = Path.Combine(Path.GetTempPath(), "KongorTest_Journey_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            LauncherSettingsModel model = LauncherSettingsService.Load(tempDir);
            model.EnableRayTracing = true;
            model.RayTracingPresetName = "Ultra";
            LauncherSettingsService.Save(model, tempDir);

            LauncherSettingsModel verifiedSettings = LauncherSettingsService.Load(tempDir);
            verifiedSettings.EnableRayTracing.Should().BeTrue();
            verifiedSettings.RayTracingPresetName.Should().Be("Ultra");
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }

        // ---------------------------------------------------------------------
        // Stage 3: Game Engine Patching & Legacy Host Redirection
        // ---------------------------------------------------------------------
        byte[] mockBinary = Encoding.ASCII.GetBytes("head_masterserver.naeu.heroesofnewerth.com_tail");
        int patched = EnginePatchService.SuppressLegacyDomains(mockBinary);

        patched.Should().Be(1);
        string modifiedString = Encoding.ASCII.GetString(mockBinary);
        modifiedString.Should().NotContain("masterserver.naeu.heroesofnewerth.com");

        // ---------------------------------------------------------------------
        // Stage 4: Asset Texture Decoding for UI Modding
        // ---------------------------------------------------------------------
        byte[] ddsHeader = new byte[128 + 8];
        Encoding.ASCII.GetBytes("DDS ").CopyTo(ddsHeader, 0);
        bool isDds = DdsDecoder.IsValidDdsHeader(ddsHeader);
        isDds.Should().BeTrue();
    }

    [Fact]
    public void Journey_LauncherSettings_CustomArgs_StoresAndLoadsCorrectly()
    {
        // Arrange
        string tempDir = Path.Combine(Path.GetTempPath(), "KongorTest_Args_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            LauncherSettingsModel model = LauncherSettingsService.Load(tempDir);
            model.CustomMasterServerAddress = "custom.masterserver.org";
            LauncherSettingsService.Save(model, tempDir);

            LauncherSettingsModel reloaded = LauncherSettingsService.Load(tempDir);
            reloaded.CustomMasterServerAddress.Should().Be("custom.masterserver.org");
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void Journey_PlatformAgnosticDeployment_ReturnsConsistentNativeLibraries()
    {
        // Act
        string[] libs = DeploymentManifest.NativeLibrariesFileNames;

        // Assert
        libs.Should().Contain("d3d9.dll");
        libs.Should().Contain("dxgi.dll");
    }

    [Fact]
    public void Journey_EnginePatching_ReplacesMultipleLegacyEndpoints()
    {
        // Arrange
        string target =
            "http://masterserver.naeu.heroesofnewerth.com/api and https://masterserver.naeu.heroesofnewerth.com/login";
        byte[] buffer = Encoding.ASCII.GetBytes(target);

        // Act
        int replacementCount = EnginePatchService.SuppressLegacyDomains(buffer);

        // Assert
        replacementCount.Should().Be(2);
        Encoding.ASCII.GetString(buffer).Should().NotContain("masterserver.naeu.heroesofnewerth.com");
    }
}
