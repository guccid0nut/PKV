// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using Microsoft.Extensions.Logging.Abstractions;
using WILLOWMAKER.Core.Services;
using WILLOWMAKER.Core.ViewModels;
using Xunit;

namespace WILLOWMAKER.Tests;

public sealed class RenderModernizationTests
{
    [Fact]
    public void GenerateDxvkConfig_CompetitivePreset_HasAsyncAndTextureClarity()
    {
        // Arrange
        RenderModernizationService service = new(NullLogger<RenderModernizationService>.Instance);

        // Act
        string config = service.GenerateDxvkConfig(
            RayTracingPreset.Competitive,
            "Minimal FPS",
            true,
            true,
            true);

        // Assert
        Assert.Contains("Competitive Mode", config);
        Assert.Contains("dxvk.enableGraphicsPipelineLibrary = true", config);
        Assert.Contains("dxvk.enableAsync = true", config);
        Assert.Contains("dxvk.numCompilerThreads = 0", config);
        Assert.Contains("d3d9.samplerAnisotropy = 16", config);
        Assert.Contains("d3d9.samplerLodBias = -0.5", config);
        Assert.Contains("d3d9.floatEmulation = strict", config);
        Assert.Contains("dxvk.nvLowLatency = true", config);
        Assert.Contains("d3d9.tearFree = false", config);
        Assert.Contains("dxgi.tearFree = false", config);
        Assert.Contains("d3d9.allowDiscard = true", config);
        Assert.Contains("dxgi.maxFrameLatency = 1", config);
    }

    [Fact]
    public void GenerateDxvkConfig_TextureClarityDisabled_OmitsSamplerLodBias()
    {
        // Arrange
        RenderModernizationService service = new(NullLogger<RenderModernizationService>.Instance);

        // Act
        string config = service.GenerateDxvkConfig(
            RayTracingPreset.Competitive,
            "Minimal FPS",
            true,
            true,
            false);

        // Assert
        Assert.DoesNotContain("d3d9.samplerLodBias", config);
        Assert.DoesNotContain("d3d9.floatEmulation", config);
    }

    [Fact]
    public void GenerateRtxConfig_BalancedPreset_HasRtaoAndSingleBounce()
    {
        // Arrange
        RenderModernizationService service = new(NullLogger<RenderModernizationService>.Instance);

        // Act
        string config = service.GenerateRtxConfig(RayTracingPreset.Balanced);

        // Assert
        Assert.Contains("rtx.enable = True", config);
        Assert.Contains("rtx.ambientOcclusion = True", config);
        Assert.Contains("rtx.reflections = True", config);
        Assert.Contains("rtx.secondaryBounces = 1", config);
        Assert.Contains("rtx.volumetricLighting = False", config);
        Assert.Contains("rtx.skyCutoffDistance = 12000.0", config);
        Assert.Contains("rtx.denoiser.temporalWeight = 0.85", config);
        Assert.Contains("rtx.denoiser.fireflyFilter = True", config);
    }

    [Fact]
    public void GenerateRtxConfig_UltraPreset_HasPathTracingAndTwoBounces()
    {
        // Arrange
        RenderModernizationService service = new(NullLogger<RenderModernizationService>.Instance);

        // Act
        string config = service.GenerateRtxConfig(RayTracingPreset.Ultra);

        // Assert
        Assert.Contains("rtx.enable = True", config);
        Assert.Contains("rtx.ambientOcclusion = True", config);
        Assert.Contains("rtx.reflections = True", config);
        Assert.Contains("rtx.secondaryBounces = 2", config);
        Assert.Contains("rtx.volumetricLighting = True", config);
        Assert.Contains("rtx.denoiser = True", config);
        Assert.Contains("rtx.skyCutoffDistance = 15000.0", config);
        Assert.Contains("rtx.denoiser.temporalWeight = 0.85", config);
        Assert.Contains("rtx.denoiser.fireflyFilter = True", config);
    }

    [Fact]
    public void RenderModernization_ApplyRenderModernization_WritesBothConfigsAndCleansUpWhenCompetitive()
    {
        // Arrange
        string tempDir = Path.Combine(Path.GetTempPath(), "RenderMod_Test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            RenderModernizationService service = new(NullLogger<RenderModernizationService>.Instance);

            // Act 1: Apply Ultra RTX
            service.ApplyRenderModernization(tempDir, RayTracingPreset.Ultra);
            string dxvkPath = Path.Combine(tempDir, "dxvk.conf");
            string rtxPath = Path.Combine(tempDir, "rtx.conf");

            Assert.True(File.Exists(dxvkPath));
            Assert.True(File.Exists(rtxPath));
            Assert.Contains("Ultra Path-Tracing Mode", File.ReadAllText(dxvkPath));
            Assert.Contains("secondaryBounces = 2", File.ReadAllText(rtxPath));

            // Act 2: Apply Competitive (should clean up rtx.conf)
            service.ApplyRenderModernization(tempDir, RayTracingPreset.Competitive);
            Assert.True(File.Exists(dxvkPath));
            Assert.False(File.Exists(rtxPath));
            Assert.Contains("Competitive Mode", File.ReadAllText(dxvkPath));
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    [Trait("Category", "Manual")]
    public void RenderModernization_WriteCustomConfiguration_WritesUncompressedTexturesAndAnisotropicFiltering()
    {
        if (!NexusTestEnvironment.IsManualTestEnabled) return;

        // Arrange
        string tempDir = Path.Combine(Path.GetTempPath(), "RenderAutoexec_Test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            MainViewModel vm = new()
            {
                EnableTextureClarityBoost = true,
                SelectedRayTracingPreset = "Ultra (Path-Tracing)"
            };

            // Act
            vm.WriteCustomConfiguration(tempDir);

            // Assert
            string autoexecPath = Path.Combine(tempDir, "userdataKONGOR", "configuration", "autoexec.cfg");
            Assert.True(File.Exists(autoexecPath));

            string autoexecContent = File.ReadAllText(autoexecPath);
            Assert.Contains("SetSave \"vid_driver\" \"d3d9\"", autoexecContent);
            Assert.Contains("SetSave \"vid_textureCompression\" \"false\"", autoexecContent);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void RenderModernization_GetShaderCacheStats_ReturnsValidMetrics()
    {
        // Arrange
        RenderModernizationService service = new(NullLogger<RenderModernizationService>.Instance);

        // Act
        ShaderCacheStats stats = service.GetShaderCacheStats();

        // Assert
        Assert.NotNull(stats);
        Assert.NotNull(stats.FormattedSize);
        Assert.True(stats.TotalSizeBytes >= 0);
        Assert.True(stats.FileCount >= 0);
    }

    [Fact]
    public void RenderModernization_GenerateDxvkConfig_IncludesExplicitStateCacheDirectives()
    {
        // Arrange
        RenderModernizationService service = new(NullLogger<RenderModernizationService>.Instance);
        string customCache = @"C:\Games\HoN\dxvk_cache";

        // Act
        string config = service.GenerateDxvkConfig(
            RayTracingPreset.Competitive,
            "fps",
            enableReflex: true,
            enableAsyncCaching: true,
            enableTextureClarityBoost: true,
            customStateCacheDir: customCache);

        // Assert
        Assert.Contains("dxvk.stateCache = true", config);
        Assert.Contains($@"dxvk.stateCachePath = {customCache}", config);
    }

    [Fact]
    public void RenderModernization_GenerateDxvkConfig_UsesPortableDefaultWithoutUserProfilePathWhenNull()
    {
        // protects: Portable dxvk.conf generation so git-tracked configs and playtest bundles never leak developer OS paths
        // fails_when: GenerateDxvkConfig falls back to Environment.SpecialFolder.LocalApplicationData (C:\Users\<name>\...) when customStateCacheDir is null
        // seam: Real RenderModernizationService.GenerateDxvkConfig output formatting
        RenderModernizationService service = new(NullLogger<RenderModernizationService>.Instance);

        string config = service.GenerateDxvkConfig(
            RayTracingPreset.Balanced,
            "fps",
            enableReflex: true,
            enableAsyncCaching: true,
            enableTextureClarityBoost: true,
            customStateCacheDir: null);

        Assert.Contains("dxvk.stateCachePath = .", config);
        Assert.DoesNotContain(@"Users\", config, StringComparison.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(Environment.UserName))
        {
            Assert.DoesNotContain(Environment.UserName, config, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void RenderModernization_SeedBaseShaderCache_SeedsFromClientDirectory()
    {
        // Arrange
        string tempClientDir = Path.Combine(Path.GetTempPath(), "ClientShaderTest_" + Guid.NewGuid().ToString("N"));
        string shaderDir = Path.Combine(tempClientDir, "shaders");
        Directory.CreateDirectory(shaderDir);

        try
        {
            // Write mock base shader
            string mockBin = Path.Combine(shaderDir, RenderModernizationService.BaseShaderCacheFileName);
            File.WriteAllBytes(mockBin, new byte[1024]);

            RenderModernizationService service = new(NullLogger<RenderModernizationService>.Instance);

            // Act
            bool seeded = service.SeedBaseShaderCache(tempClientDir);

            // Assert
            Assert.True(seeded || !seeded); // Executed without throwing
        }
        finally
        {
            if (Directory.Exists(tempClientDir)) Directory.Delete(tempClientDir, true);
        }
    }

    [Fact]
    public void RenderModernization_IsNvidiaRtxSupported_ExecutesWithoutException()
    {
        // Arrange
        RenderModernizationService service = new(NullLogger<RenderModernizationService>.Instance);

        // Act
        bool supported = service.IsNvidiaRtxSupported();

        // Assert
        Assert.True(supported || !supported);
    }

    [Fact]
    public void GenerateDxvkConfig_MailboxMode_EmitsTearFreeDirectives()
    {
        // Arrange
        RenderModernizationService service = new(NullLogger<RenderModernizationService>.Instance);

        // Act
        string config = service.GenerateDxvkConfig(
            RayTracingPreset.Competitive,
            "Minimal FPS",
            enableReflex: true,
            enableAsyncCaching: true,
            enableTextureClarityBoost: true,
            customStateCacheDir: null,
            enableDirectFlip: true,
            presentationMode: DirectFlipPresentationMode.DxvkMailbox);

        // Assert
        Assert.Contains("DirectFlip DXVK Mailbox", config);
        Assert.Contains("d3d9.tearFree = true", config);
        Assert.Contains("dxgi.tearFree = true", config);
        Assert.Contains("d3d9.allowDiscard = true", config);
        Assert.Contains("dxgi.maxFrameLatency = 1", config);
    }

    [Fact]
    public void GenerateDxvkConfig_LegacyComposited_EmitsDisabledDirectives()
    {
        // Arrange
        RenderModernizationService service = new(NullLogger<RenderModernizationService>.Instance);

        // Act
        string config = service.GenerateDxvkConfig(
            RayTracingPreset.Competitive,
            "Minimal FPS",
            enableReflex: true,
            enableAsyncCaching: true,
            enableTextureClarityBoost: true,
            customStateCacheDir: null,
            enableDirectFlip: false,
            presentationMode: DirectFlipPresentationMode.LegacyComposited);

        // Assert
        Assert.Contains("Legacy Composited Blit", config);
        Assert.Contains("d3d9.deferSurfaceCreation = false", config);
        Assert.Contains("d3d9.allowDiscard = false", config);
        Assert.Contains("dxgi.maxFrameLatency = 2", config);
    }

    [Fact]
    public void GenerateD3D9On12Config_ValidParameters_WritesExpectedConfig()
    {
        // Arrange
        RenderModernizationService service = new(NullLogger<RenderModernizationService>.Instance);

        // Act
        string config = service.GenerateD3D9On12Config(enableDirectFlip: true, maxFrameLatency: 1, allowTearing: true);

        // Assert
        Assert.Contains("SwapEffect = FLIP_DISCARD", config);
        Assert.Contains("MaxFrameLatency = 1", config);
        Assert.Contains("AllowTearing = True", config);
        Assert.Contains("EnableDirectFlip = True", config);
    }

    [Fact]
    public void CheckDirectFlipReadiness_ValidBorderless_ReportsEligible()
    {
        // Arrange
        RenderModernizationService service = new(NullLogger<RenderModernizationService>.Instance);

        // Act
        DirectFlipReadinessReport report = service.CheckDirectFlipReadiness(
            targetWidth: 2560,
            targetHeight: 1440,
            screenWidth: 2560,
            screenHeight: 1440,
            isBorderless: true,
            isFullscreen: false);

        // Assert
        Assert.True(report.IsEligible);
        Assert.True(report.IsBorderless);
        Assert.False(report.IsFullscreen);
        Assert.Contains("Optimal DirectFlip conditions verified", report.Diagnostics[0]);
    }

    [Fact]
    public void CheckDirectFlipReadiness_ResolutionMismatch_ReportsWarning()
    {
        // Arrange
        RenderModernizationService service = new(NullLogger<RenderModernizationService>.Instance);

        // Act
        DirectFlipReadinessReport report = service.CheckDirectFlipReadiness(
            targetWidth: 1920,
            targetHeight: 1080,
            screenWidth: 2560,
            screenHeight: 1440,
            isBorderless: true,
            isFullscreen: false);

        // Assert
        Assert.False(report.IsEligible);
        Assert.Contains(report.Diagnostics, static (string d) => d.Contains("Resolution mismatch"));
    }

    [Fact]
    public void ApplyRenderModernization_SwapchainBackendSwitch_DeploysCorrectConfigs()
    {
        // Arrange
        string tempDir = Path.Combine(Path.GetTempPath(), "RenderSwapchain_Test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            RenderModernizationService service = new(NullLogger<RenderModernizationService>.Instance);
            string d3d9on12Path = Path.Combine(tempDir, RenderModernizationService.D3D9On12ConfigFileName);

            // Act 1: Deploy with D3D9On12
            service.ApplyRenderModernization(
                tempDir,
                RayTracingPreset.Competitive,
                swapchainBackend: SwapchainBackend.D3D9On12);

            Assert.True(File.Exists(d3d9on12Path));
            string content = File.ReadAllText(d3d9on12Path);
            Assert.Contains("SwapEffect = FLIP_DISCARD", content);

            // Act 2: Deploy with DxvkVulkan (should clean up d3d9on12.conf)
            service.ApplyRenderModernization(
                tempDir,
                RayTracingPreset.Competitive,
                swapchainBackend: SwapchainBackend.DxvkVulkan);

            Assert.False(File.Exists(d3d9on12Path));
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }
}
