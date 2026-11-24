// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using WILLOWMAKER.Core.Services;
using WILLOWMAKER.Core.ViewModels;
using Xunit;

namespace WILLOWMAKER.Tests;

public sealed class HardwareDetectionTests
{
    [Fact]
    public void DetectHardware_ReturnsSensibleMetrics()
    {
        // Act
        HardwareProfile profile = HardwareDetectionService.DetectHardware();

        // Assert
        Assert.NotNull(profile);
        Assert.False(string.IsNullOrWhiteSpace(profile.GpuName));
        Assert.True(profile.SystemRamBytes > 0);
        Assert.True(profile.RefreshRateHz > 0);
        Assert.False(string.IsNullOrWhiteSpace(profile.Summary));
    }

    [Theory]
    [InlineData("NVIDIA GeForce RTX 4090", 24L * 1024 * 1024 * 1024, 64L * 1024 * 1024 * 1024, HardwareTier.EnthusiastRtx)]
    [InlineData("NVIDIA GeForce RTX 3070", 8L * 1024 * 1024 * 1024, 32L * 1024 * 1024 * 1024, HardwareTier.HighPerformanceRtx)]
    [InlineData("AMD Radeon RX 6700 XT", 12L * 1024 * 1024 * 1024, 32L * 1024 * 1024 * 1024, HardwareTier.EnthusiastRtx)]
    [InlineData("NVIDIA GeForce GTX 1060 6GB", 6L * 1024 * 1024 * 1024, 16L * 1024 * 1024 * 1024, HardwareTier.CompetitiveMidRange)]
    [InlineData("Intel(R) UHD Graphics 630", 0L, 8L * 1024 * 1024 * 1024, HardwareTier.EntryIntegrated)]
    public void ClassifyTier_MatchesExpectedPerformanceTiers(
        string gpuName,
        long vramBytes,
        long ramBytes,
        HardwareTier expectedTier)
    {
        // Act
        HardwareTier actualTier = HardwareDetectionService.ClassifyTier(gpuName, vramBytes, ramBytes);

        // Assert
        Assert.Equal(expectedTier, actualTier);
    }

    [Fact]
    public void GetRecommendation_HighPerformanceRtx_RecommendsBalancedRtxAnd2kShadows()
    {
        // Arrange (Profile matching an RTX 3070 rig)
        HardwareProfile profile = new(
            GpuName: "NVIDIA GeForce RTX 3070",
            VramBytes: 8L * 1024 * 1024 * 1024,
            SystemRamBytes: 32L * 1024 * 1024 * 1024,
            RefreshRateHz: 144,
            Tier: HardwareTier.HighPerformanceRtx,
            Summary: "NVIDIA GeForce RTX 3070 (8.0 GB VRAM) | 32 GB RAM | 144 Hz"
        );

        // Act
        HardwareRecommendation rec = HardwareDetectionService.GetRecommendation(profile);

        // Assert
        Assert.True(rec.EnableRayTracing);
        Assert.Equal("Balanced RTX (RTAO)", rec.RayTracingPreset);
        Assert.Equal("High (2048)", rec.ShadowFidelity);
        Assert.Equal("144 Hz", rec.RefreshRate);
        Assert.True(rec.EnableDirectFlip);
        Assert.False(rec.EnableNvidiaReflex);
        Assert.False(rec.EnableTextureClarityBoost);
        Assert.Equal("DirectFlip (FLIP_DISCARD)", rec.PresentationMode);
    }

    [Fact]
    [Trait("Category", "Manual")]
    public void MainViewModel_ApplyRecommendedSettings_UpdatesViewModelState()
    {
        if (!NexusTestEnvironment.IsManualTestEnabled) return;

        // Arrange
        MainViewModel vm = new();

        // Act
        vm.ApplyRecommendedSettings();

        // Assert
        Assert.NotNull(vm.DetectedHardwareSummary);
        Assert.Contains("🎯", vm.RecommendedSettingsTooltip);
        Assert.Contains(vm.SelectedShadowFidelity, vm.AvailableShadowFidelities);
        Assert.Contains(vm.SelectedRayTracingPreset, vm.AvailableRayTracingPresets);
    }
}
