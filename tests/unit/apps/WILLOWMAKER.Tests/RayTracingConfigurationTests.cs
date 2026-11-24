// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using Microsoft.Extensions.Logging.Abstractions;
using WILLOWMAKER.Core.Services;
using WILLOWMAKER.Core.ViewModels;
using Xunit;

namespace WILLOWMAKER.Tests;

[Trait("Category", "Manual")]
public sealed class RayTracingConfigurationTests
{
    [Fact]
    public void EnsureVulkanDxvkConfigured_WhenRayTracingDisabled_WritesCompetitivePreset()
    {
        // Arrange
        string tempDir = Path.Combine(Path.GetTempPath(), "WILLOWMAKER_RT_Test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            MainViewModel viewModel = new()
            {
                EnableRayTracing = false,
                SelectedVulkanHudMode = "Minimal FPS",
                EnableNvidiaReflex = true
            };

            // Act
            viewModel.EnsureVulkanDxvkConfigured(tempDir);

            // Assert
            string dxvkPath = Path.Combine(tempDir, "userdataKONGOR", "dxvk.conf");
            Assert.True(File.Exists(dxvkPath));
            string dxvkContent = File.ReadAllText(dxvkPath);
            Assert.Contains("Competitive Mode", dxvkContent);
            Assert.Contains("dxvk.hud = fps", dxvkContent);
            Assert.Contains("dxvk.nvLowLatency = true", dxvkContent);
            Assert.DoesNotContain("d3d9.enableDialogMode", dxvkContent);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void EnsureVulkanDxvkConfigured_WhenRayTracingEnabled_WritesRtxPreset()
    {
        // Arrange
        string tempDir = Path.Combine(Path.GetTempPath(), "WILLOWMAKER_RT_Test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            MainViewModel viewModel = new()
            {
                EnableRayTracing = true,
                SelectedRayTracingPreset = "Balanced RTX (RTAO)",
                EnableNvidiaReflex = true
            };

            // Act
            viewModel.EnsureVulkanDxvkConfigured(tempDir);

            // Assert
            string dxvkPath = Path.Combine(tempDir, "userdataKONGOR", "dxvk.conf");
            string rtxPath = Path.Combine(tempDir, "userdataKONGOR", "rtx.conf");

            Assert.True(File.Exists(dxvkPath));
            Assert.True(File.Exists(rtxPath));

            string dxvkContent = File.ReadAllText(dxvkPath);
            Assert.Contains("Balanced RTX Mode", dxvkContent);
            Assert.Contains("dxvk.nvLowLatency = true", dxvkContent);

            string rtxContent = File.ReadAllText(rtxPath);
            Assert.Contains("rtx.enable = True", rtxContent);
            Assert.Contains("rtx.ambientOcclusion = True", rtxContent);
            Assert.Contains("rtx.reflections = True", rtxContent);
            Assert.Contains("rtx.skyCutoffDistance = 12000.0", rtxContent);
            Assert.Contains("rtx.denoiser.temporalWeight = 0.85", rtxContent);
            Assert.Contains("rtx.denoiser.fireflyFilter = True", rtxContent);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void EnsureVulkanDxvkConfigured_WithCustomHudAndReflexDisabled_ReflectsSettings()
    {
        // Arrange
        string tempDir = Path.Combine(Path.GetTempPath(), "WILLOWMAKER_RT_Test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            MainViewModel viewModel = new()
            {
                EnableRayTracing = false,
                SelectedVulkanHudMode = "Off (Clean Screen)",
                EnableNvidiaReflex = false
            };

            // Act
            viewModel.EnsureVulkanDxvkConfigured(tempDir);

            // Assert
            string dxvkPath = Path.Combine(tempDir, "userdataKONGOR", "dxvk.conf");
            Assert.True(File.Exists(dxvkPath));
            string dxvkContent = File.ReadAllText(dxvkPath);
            Assert.Contains("dxvk.hud = 0", dxvkContent);
            Assert.Contains("dxvk.nvLowLatency = false", dxvkContent);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void EnsureVulkanDxvkConfigured_WhenDirectFlipSelected_WritesDirectFlipAndTearFreeFalse()
    {
        // Arrange
        string tempDir = Path.Combine(Path.GetTempPath(), "WILLOWMAKER_DF_Test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            MainViewModel viewModel = new()
            {
                EnableDirectFlip = true,
                SelectedPresentationMode = "DirectFlip (FLIP_DISCARD)"
            };

            // Act
            viewModel.EnsureVulkanDxvkConfigured(tempDir);

            // Assert
            string dxvkPath = Path.Combine(tempDir, "userdataKONGOR", "dxvk.conf");
            Assert.True(File.Exists(dxvkPath));
            string dxvkContent = File.ReadAllText(dxvkPath);
            Assert.Contains("DirectFlip FLIP_DISCARD", dxvkContent);
            Assert.Contains("d3d9.tearFree = false", dxvkContent);
            Assert.Contains("dxgi.tearFree = false", dxvkContent);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void EnsureVulkanDxvkConfigured_WhenMailboxSelected_WritesMailboxAndTearFreeTrue()
    {
        // Arrange
        string tempDir = Path.Combine(Path.GetTempPath(), "WILLOWMAKER_MB_Test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            MainViewModel viewModel = new()
            {
                EnableDirectFlip = true,
                SelectedPresentationMode = "DXVK Mailbox (Low-Latency)"
            };

            // Act
            viewModel.EnsureVulkanDxvkConfigured(tempDir);

            // Assert
            string dxvkPath = Path.Combine(tempDir, "userdataKONGOR", "dxvk.conf");
            Assert.True(File.Exists(dxvkPath));
            string dxvkContent = File.ReadAllText(dxvkPath);
            Assert.Contains("DirectFlip DXVK Mailbox", dxvkContent);
            Assert.Contains("d3d9.tearFree = true", dxvkContent);
            Assert.Contains("dxgi.tearFree = true", dxvkContent);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void GenerateDxvkConfig_WhenSafeSwapchainPacingEnabled_WritesSafePacingAndTearFreeTrue()
    {
        // Arrange
        RenderModernizationService renderService = new(NullLogger<RenderModernizationService>.Instance);

        // Act
        string dxvkContent = renderService.GenerateDxvkConfig(
            preset: RayTracingPreset.Competitive,
            enableReflex: true,
            enableDirectFlip: true,
            safeSwapchainPacing: true);

        // Assert
        Assert.Contains("dxvk.tearFree = true", dxvkContent);
        Assert.Contains("d3d9.maxFrameLatency = 2", dxvkContent);
        Assert.Contains("dxvk.latencyTolerance = 1000", dxvkContent);
        Assert.Contains("dxvk.latencySleep = False", dxvkContent);
        Assert.Contains("dxvk.nvLowLatency = false", dxvkContent);
    }
}
