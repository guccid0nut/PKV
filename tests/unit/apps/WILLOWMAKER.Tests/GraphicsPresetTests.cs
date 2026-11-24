// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using WILLOWMAKER.Core.ViewModels;
using Xunit;

namespace WILLOWMAKER.Tests;

[Trait("Category", "Manual")]
public sealed class GraphicsPresetTests
{
    [Fact]
    public void GraphicsPreset_ApplyBestFpsPreset_SetsFastLoadingAndCompetitiveProperties()
    {
        // Arrange
        MainViewModel viewModel = new()
        {
            EnableRayTracing = true,
            SelectedRefreshRate = "60 Hz",
            SelectedShadowFidelity = "Ultra (4096)"
        };

        // Act
        viewModel.ApplyBestFpsPreset();

        // Assert
        int expectedAutoHz = viewModel.GetAutoMaxRefreshRateHz();
        Assert.False(viewModel.EnableRayTracing);
        Assert.False(viewModel.EnableTextureClarityBoost);
        Assert.False(viewModel.ManualRefreshRateOverrideEnabled);
        Assert.Equal($"{expectedAutoHz} Hz", viewModel.SelectedRefreshRate);
        Assert.Equal("Medium (1024)", viewModel.SelectedShadowFidelity);
        Assert.True(viewModel.EnableDirectFlip);
        Assert.Equal("DirectFlip (FLIP_DISCARD)", viewModel.SelectedPresentationMode);
        Assert.False(viewModel.EnableNvidiaReflex);
        Assert.Equal("Off (Clean Screen)", viewModel.SelectedVulkanHudMode);
        Assert.Contains("Shadows: 1024", viewModel.ActiveGraphicsPipelineSummary);
        Assert.Contains("Applied Best FPS", viewModel.LastPresetChangeSummary);
    }

    [Fact]
    public void GraphicsPreset_ApplyBestGraphicsPreset_SetsHighRtxProperties()
    {
        // Arrange
        MainViewModel viewModel = new()
        {
            EnableRayTracing = false,
            SelectedRefreshRate = "60 Hz",
            SelectedShadowFidelity = "Medium (1024)"
        };

        // Act
        viewModel.ApplyBestGraphicsPreset();

        // Assert
        int expectedAutoHz = viewModel.GetAutoMaxRefreshRateHz();
        Assert.True(viewModel.EnableRayTracing);
        Assert.Equal("Ultra (Path-Tracing)", viewModel.SelectedRayTracingPreset);
        Assert.False(viewModel.ManualRefreshRateOverrideEnabled);
        Assert.Equal($"{expectedAutoHz} Hz", viewModel.SelectedRefreshRate);
        Assert.Equal("Ultra (4096)", viewModel.SelectedShadowFidelity);
        Assert.False(viewModel.EnableTextureClarityBoost);
        Assert.False(viewModel.EnableNvidiaReflex);
        Assert.Equal("Off (Clean Screen)", viewModel.SelectedVulkanHudMode);
        Assert.Equal("BestGraphics", viewModel.ActiveGraphicsPresetKey);
        Assert.Contains("4x MSAA", viewModel.ActiveGraphicsPipelineSummary);
        Assert.Contains("Applied Best Graphics", viewModel.LastPresetChangeSummary);
    }

    [Fact]
    public void WriteCustomConfiguration_WhenBestFpsActive_GeneratesFastLoadingConfig()
    {
        // Arrange
        string tempDir = Path.Combine(Path.GetTempPath(), "WILLOWMAKER_FpsTest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            MainViewModel viewModel = new();
            viewModel.ApplyBestFpsPreset();
            int expectedAutoHz = viewModel.GetAutoMaxRefreshRateHz();

            // Act
            viewModel.WriteCustomConfiguration(tempDir);

            // Assert
            string autoexecPath = Path.Combine(tempDir, "userdataKONGOR", "configuration", "autoexec.cfg");
            Assert.True(File.Exists(autoexecPath));

            string mirrorPath = Path.Combine(tempDir, "userdataKONGOR", "extensions", "autoexec.cfg");
            Assert.True(File.Exists(mirrorPath));

            string content = File.ReadAllText(autoexecPath);
            Assert.Contains($"SetSave \"vid_refreshRate\" \"{expectedAutoHz}\"", content);
            Assert.Contains("SetSave \"vid_shadowmapSize\" \"1024\"", content);
            Assert.Contains("SetSave \"vid_foliageDensity\" \"0.0000\"", content);
            Assert.Contains("SetSave \"vid_cullGroundSprites\" \"true\"", content);
            Assert.Contains("SetSave \"vid_textureCompression\" \"true\"", content);
            Assert.Contains("SetSave \"vid_shaderAmbientOcclusion\" \"false\"", content);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void WriteCustomConfiguration_SynchronizesStartupCfgAndMirrorAutoexecs()
    {
        // Arrange
        string tempDir = Path.Combine(Path.GetTempPath(), "WILLOWMAKER_SyncTest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        string extDir = Path.Combine(tempDir, "userdataKONGOR", "extensions");
        Directory.CreateDirectory(extDir);

        string userGameDir = Path.Combine(tempDir, "userdata", "Heroes of Newerth x64", "game");
        Directory.CreateDirectory(userGameDir);

        string startupPath = Path.Combine(userGameDir, "startup.cfg");
        File.WriteAllText(startupPath, "SetSave \"vid_shadowmapSize\" \"4096\" \"0\"\nSetSave \"vid_shadowDrawDistance\" \"3500.0000\" \"0\"");

        try
        {
            MainViewModel viewModel = new();
            viewModel.ApplyBestFpsPreset();

            // Act
            viewModel.WriteCustomConfiguration(tempDir);

            // Assert
            string updatedStartup = File.ReadAllText(startupPath);
            Assert.Contains("SetSave \"vid_shadowmapSize\" \"1024\" \"0\"", updatedStartup);
            Assert.Contains("SetSave \"vid_shadowDrawDistance\" \"1800.0000\" \"0\"", updatedStartup);

            string extAutoexec = Path.Combine(extDir, "autoexec.cfg");
            Assert.True(File.Exists(extAutoexec));
            Assert.Contains("SetSave \"vid_shadowmapSize\" \"1024\"", File.ReadAllText(extAutoexec));
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }
}
