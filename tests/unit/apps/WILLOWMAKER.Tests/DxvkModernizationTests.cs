// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ============================================================================
// File: DxvkModernizationTests.cs
// System: WILLOWMAKER Unit Tests - DXVK 2.4 Vulkan Modernization
// ============================================================================

using System.IO;
using Microsoft.Extensions.Logging.Abstractions;
using WILLOWMAKER.Core.Services;
using Xunit;

namespace WILLOWMAKER.Tests;

public sealed class DxvkModernizationTests
{
    [Fact]
    public void GenerateDxvkConfig_DefaultSettings_IncludesLatencyReductionAndNoHdrNoFsr()
    {
        // Arrange
        RenderModernizationService service = new(NullLogger<RenderModernizationService>.Instance);

        // Act
        string config = service.GenerateDxvkConfig(RayTracingPreset.Competitive);

        // Assert
        Assert.Contains("dxvk.enableLatencyReduction = true", config);
        Assert.Contains("d3d9.latencyTolerance = 0", config);
        Assert.DoesNotContain("dxvk.enableHDR", config);
        Assert.DoesNotContain("dxvk.fsrEffect", config);
    }

    [Fact]
    public void GenerateDxvkConfig_WithHdr_OutputsNativeScRgbHdrSettings()
    {
        // Arrange
        RenderModernizationService service = new(NullLogger<RenderModernizationService>.Instance);

        // Act
        string config = service.GenerateDxvkConfig(
            RayTracingPreset.Ultra,
            enableHdr: true);

        // Assert
        Assert.Contains("dxvk.enableHDR = true", config);
        Assert.Contains("dxgi.enableHDR = true", config);
        Assert.Contains("d3d9.enableHDR = true", config);
    }

    [Fact]
    public void GenerateDxvkConfig_WithFsr_OutputsSpatialUpscalingSettings()
    {
        // Arrange
        RenderModernizationService service = new(NullLogger<RenderModernizationService>.Instance);

        // Act
        string config = service.GenerateDxvkConfig(
            RayTracingPreset.Competitive,
            enableFsr: true,
            fsrSharpness: 0.8f);

        // Assert
        Assert.Contains("dxvk.fsrEffect = spatial", config);
        Assert.Contains("dxvk.fsrSharpness = 0.8", config);
    }

    [Fact]
    public void GenerateDxvk24Profile_UltraHDRVisual_HasExpectedConfiguration()
    {
        // Arrange
        RenderModernizationService service = new(NullLogger<RenderModernizationService>.Instance);

        // Act
        string config = service.GenerateDxvk24Profile(Dxvk24Profile.UltraHDRVisual);

        // Assert
        Assert.Contains("Ultra Path-Tracing Mode", config);
        Assert.Contains("dxvk.enableHDR = true", config);
        Assert.Contains("dxgi.enableHDR = true", config);
        Assert.Contains("d3d9.enableHDR = true", config);
        Assert.Contains("dxvk.hud = fps,frametimes,gpuload,memory", config);
        Assert.Contains("dxvk.enableLatencyReduction = true", config);
        Assert.DoesNotContain("dxvk.fsrEffect", config);
    }

    [Fact]
    public void GenerateDxvk24Profile_BudgetFsrUpscaled_HasExpectedConfiguration()
    {
        // Arrange
        RenderModernizationService service = new(NullLogger<RenderModernizationService>.Instance);

        // Act
        string config = service.GenerateDxvk24Profile(Dxvk24Profile.BudgetFsrUpscaled);

        // Assert
        Assert.Contains("Competitive Mode", config);
        Assert.Contains("dxvk.fsrEffect = spatial", config);
        Assert.Contains("dxvk.fsrSharpness = 0.7", config);
        Assert.Contains("dxvk.hud = fps", config);
        Assert.DoesNotContain("dxvk.enableHDR", config);
    }

    [Fact]
    public void GenerateDxvk24Profile_CompetitiveLowLatency_HasCleanScreenAndLowLatency()
    {
        // Arrange
        RenderModernizationService service = new(NullLogger<RenderModernizationService>.Instance);

        // Act
        string config = service.GenerateDxvk24Profile(Dxvk24Profile.CompetitiveLowLatency);

        // Assert
        Assert.Contains("Competitive Mode", config);
        Assert.Contains("dxvk.hud = 0", config);
        Assert.Contains("dxvk.enableLatencyReduction = true", config);
        Assert.Contains("d3d9.latencyTolerance = 0", config);
        Assert.DoesNotContain("dxvk.enableHDR", config);
        Assert.DoesNotContain("dxvk.fsrEffect", config);
        Assert.DoesNotContain("d3d9.samplerLodBias", config);
    }

    [Fact]
    public void GenerateDxvk24Profile_Standard_HasBalancedSettings()
    {
        // Arrange
        RenderModernizationService service = new(NullLogger<RenderModernizationService>.Instance);

        // Act
        string config = service.GenerateDxvk24Profile(Dxvk24Profile.Standard);

        // Assert
        Assert.Contains("Balanced RTX Mode", config);
        Assert.Contains("dxvk.hud = fps", config);
        Assert.Contains("d3d9.samplerLodBias = -0.5", config);
        Assert.DoesNotContain("dxvk.enableHDR", config);
        Assert.DoesNotContain("dxvk.fsrEffect", config);
    }

    [Fact]
    public void ApplyRenderModernization_WithProfile_WritesConfigFileCorrectly()
    {
        // Arrange
        RenderModernizationService service = new(NullLogger<RenderModernizationService>.Instance);
        string tempDir = Path.Combine(Path.GetTempPath(), "DxvkModernization_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            // Act
            service.ApplyRenderModernization(tempDir, Dxvk24Profile.UltraHDRVisual);

            // Assert
            string dxvkConf = Path.Combine(tempDir, "dxvk.conf");
            Assert.True(File.Exists(dxvkConf));
            string content = File.ReadAllText(dxvkConf);
            Assert.Contains("dxvk.enableHDR = true", content);
            Assert.Contains("Ultra Path-Tracing Mode", content);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }
}
