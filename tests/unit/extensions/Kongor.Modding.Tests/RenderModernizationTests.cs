// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ============================================================================
// File: RenderModernizationTests.cs
// System: Kongor.Modding.Tests - Render Modernization & DXVK Profile Governance
// Policy: Strict Explicit Typing (NO 'var')
// ============================================================================

using System;
using System.IO;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using WILLOWMAKER.Core.Services;
using Xunit;

namespace Kongor.Modding.Tests;

/// <summary>
///     Comprehensive unit test suite for DXVK 3.0.2 modernization profiles,
///     NVIDIA Reflex low-latency frame pacing, two-tier config rollback, and status inspection.
/// </summary>
public sealed class RenderModernizationTests
{
    private static string CreateIsolatedTestDirectory()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "kongor_render_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        return tempDir;
    }

    [Fact]
    public void DxvkProfile_CompetitiveLowLatency_EnablesReflexAndZeroTolerance()
    {
        string testDir = CreateIsolatedTestDirectory();
        try
        {
            RenderModernizationService service = new(NullLogger<RenderModernizationService>.Instance);

            service.ApplyRenderModernization(testDir, Dxvk24Profile.CompetitiveLowLatency);

            string configPath = Path.Combine(testDir, "dxvk.conf");
            File.Exists(configPath).Should().BeTrue("dxvk.conf must be created by ApplyRenderModernization");

            string configContent = File.ReadAllText(configPath);
            configContent.Should().Contain("dxvk.nvLowLatency = true", "Reflex low-latency pacing must be enabled");
            configContent.Should().Contain("d3d9.latencyTolerance = 0", "Zero-tolerance frame pacing must be enforced");
            configContent.Should().Contain("d3d9.maxFrameLatency = 1", "Queue depth must be clamped to 1");
            configContent.Should().Contain("dxvk.enableLatencyReduction = true", "Upstream latency reduction must be enabled");

            DxvkStatusReport status = service.GetDxvkStatus(testDir);
            status.HasDxvkConfig.Should().BeTrue();
            status.EnableReflex.Should().BeTrue();
            status.EnableLatencyReduction.Should().BeTrue();
            status.LatencyTolerance.Should().Be(0);
            status.MaxFrameLatency.Should().Be(1);
        }
        finally
        {
            if (Directory.Exists(testDir))
            {
                try { Directory.Delete(testDir, recursive: true); } catch { }
            }
        }
    }

    [Fact]
    public void DxvkConfig_RollbackTier_RestoresBackupCorrectly()
    {
        string testDir = CreateIsolatedTestDirectory();
        try
        {
            RenderModernizationService service = new(NullLogger<RenderModernizationService>.Instance);

            // 1. Initial Standard Profile
            service.ApplyRenderModernization(testDir, Dxvk24Profile.Standard);
            string standardPath = Path.Combine(testDir, "dxvk.conf");
            string standardContent = File.ReadAllText(standardPath);

            // 2. Switch to CompetitiveLowLatency (rotates standard -> .bak)
            service.ApplyRenderModernization(testDir, Dxvk24Profile.CompetitiveLowLatency);
            string competitiveContent = File.ReadAllText(standardPath);

            string bakPath = Path.Combine(testDir, "dxvk.conf.bak");
            File.Exists(bakPath).Should().BeTrue("First overwrite must create dxvk.conf.bak");
            File.ReadAllText(bakPath).Should().Be(standardContent);

            // 3. Switch to UltraHDRVisual (rotates standard -> .bak.old, competitive -> .bak)
            service.ApplyRenderModernization(testDir, Dxvk24Profile.UltraHDRVisual);
            string bakOldPath = Path.Combine(testDir, "dxvk.conf.bak.old");
            File.Exists(bakOldPath).Should().BeTrue("Second overwrite must create dxvk.conf.bak.old");
            File.ReadAllText(bakOldPath).Should().Be(standardContent);
            File.ReadAllText(bakPath).Should().Be(competitiveContent);

            // 4. Rollback to 'previous' (should restore Competitive)
            bool rollbackPrevSuccess = service.RollbackDxvkConfig(testDir, "previous");
            rollbackPrevSuccess.Should().BeTrue();
            File.ReadAllText(standardPath).Should().Be(competitiveContent);

            // 5. Rollback to 'baseline' / 'old' (should restore Standard)
            bool rollbackOldSuccess = service.RollbackDxvkConfig(testDir, "baseline");
            rollbackOldSuccess.Should().BeTrue();
            File.ReadAllText(standardPath).Should().Be(standardContent);

            // 6. Rollback in directory with no backup should fail gracefully
            string emptyDir = CreateIsolatedTestDirectory();
            try
            {
                bool failureExpected = service.RollbackDxvkConfig(emptyDir, "previous");
                failureExpected.Should().BeFalse();
            }
            finally
            {
                if (Directory.Exists(emptyDir))
                {
                    try { Directory.Delete(emptyDir, recursive: true); } catch { }
                }
            }
        }
        finally
        {
            if (Directory.Exists(testDir))
            {
                try { Directory.Delete(testDir, recursive: true); } catch { }
            }
        }
    }

    [Fact]
    public void DxvkStatusReport_InspectsActiveConfigurationAndLogTelemetry()
    {
        string testDir = CreateIsolatedTestDirectory();
        try
        {
            RenderModernizationService service = new(NullLogger<RenderModernizationService>.Instance);

            // Create simulated d3d9.dll
            string d3d9Path = Path.Combine(testDir, "d3d9.dll");
            byte[] dummyBytes = new byte[1024];
            new Random(42).NextBytes(dummyBytes);
            File.WriteAllBytes(d3d9Path, dummyBytes);

            // Create simulated hon_x64_d3d9.log with DXVK 3.0.2 and VK_NV_low_latency2
            string logPath = Path.Combine(testDir, "hon_x64_d3d9.log");
            string logContent = """
                                info:  DXVK: v3.0.2
                                info:  Vulkan: using VK_NV_low_latency2 frame pacing extension
                                info:  nvLowLatency2 : 1
                                """;
            File.WriteAllText(logPath, logContent);

            // Apply Competitive profile
            service.ApplyRenderModernization(testDir, Dxvk24Profile.CompetitiveLowLatency);

            DxvkStatusReport report = service.GetDxvkStatus(testDir);
            report.HasD3D9.Should().BeTrue();
            report.D3D9SizeBytes.Should().Be(1024);
            report.D3D9Sha256.Should().NotBeNullOrWhiteSpace();
            report.DetectedDxvkVersion.Should().Be("3.0.2");
            report.HasNvLowLatency2Support.Should().BeTrue();
            report.EnableReflex.Should().BeTrue();
            report.MaxFrameLatency.Should().Be(1);
            report.LatencyTolerance.Should().Be(0);
            report.Diagnostics.Should().Contain(d => d.Contains("VK_NV_low_latency2"));
        }
        finally
        {
            if (Directory.Exists(testDir))
            {
                try { Directory.Delete(testDir, recursive: true); } catch { }
            }
        }
    }

    [Fact]
    public void CheckDirectFlipReadiness_EvaluatesFullscreenAndBorderlessScenarios()
    {
        RenderModernizationService service = new(NullLogger<RenderModernizationService>.Instance);

        // 1. Optimal 1:1 borderless window
        DirectFlipReadinessReport borderlessReport = service.CheckDirectFlipReadiness(
            targetWidth: 1920,
            targetHeight: 1080,
            screenWidth: 1920,
            screenHeight: 1080,
            isBorderless: true,
            isFullscreen: false);

        borderlessReport.IsEligible.Should().BeTrue();
        borderlessReport.IsBorderless.Should().BeTrue();
        borderlessReport.IsFullscreen.Should().BeFalse();

        // 2. Windowed without borderless (DWM compositing overhead)
        DirectFlipReadinessReport windowedReport = service.CheckDirectFlipReadiness(
            targetWidth: 1920,
            targetHeight: 1080,
            screenWidth: 1920,
            screenHeight: 1080,
            isBorderless: false,
            isFullscreen: false);

        windowedReport.IsEligible.Should().BeFalse();
        windowedReport.Diagnostics.Should().Contain(d => d.Contains("vid_borderless = false"));

        // 3. Resolution mismatch borderless
        DirectFlipReadinessReport mismatchReport = service.CheckDirectFlipReadiness(
            targetWidth: 1280,
            targetHeight: 720,
            screenWidth: 1920,
            screenHeight: 1080,
            isBorderless: true,
            isFullscreen: false);

        mismatchReport.IsEligible.Should().BeFalse();
        mismatchReport.Diagnostics.Should().Contain(d => d.Contains("Resolution mismatch"));

        // 4. Fullscreen mode
        DirectFlipReadinessReport fullscreenReport = service.CheckDirectFlipReadiness(
            targetWidth: 1920,
            targetHeight: 1080,
            screenWidth: 1920,
            screenHeight: 1080,
            isBorderless: false,
            isFullscreen: true);

        fullscreenReport.IsEligible.Should().BeTrue();
        fullscreenReport.IsFullscreen.Should().BeTrue();
    }

    [Fact]
    public void SeedBaseShaderCache_ValidatesDxvkLutMagicAndAtomicallyUpgradesParentCacheIntoUserdataKongor()
    {
        // protects: Atomic DXVK 3.0.2 .dxvk.bin + .dxvk.lut seeding from parent dxvk_cache/ into userdataKONGOR/dxvk_cache
        // fails_when: Parent directory lookup is skipped, corrupt .lut is copied, or stale .lut is left mismatched after .bin upgrade
        // seam: Real filesystem temp directories + binary magic inspection via RenderModernizationService.SeedBaseShaderCache
        string clientRoot = CreateIsolatedTestDirectory();
        try
        {
            string parentDxvkCache = Path.Combine(clientRoot, "dxvk_cache");
            string userdataKongor = Path.Combine(clientRoot, "userdataKONGOR");
            string customTargetCache = Path.Combine(userdataKongor, "dxvk_cache");
            Directory.CreateDirectory(parentDxvkCache);
            Directory.CreateDirectory(userdataKongor);
            Directory.CreateDirectory(customTargetCache);

            string srcBin = Path.Combine(parentDxvkCache, $"{RenderModernizationService.DefaultDxvkAppHash}.dxvk.bin");
            string srcLut = Path.Combine(parentDxvkCache, $"{RenderModernizationService.DefaultDxvkAppHash}.dxvk.lut");

            RenderModernizationService service = new(NullLogger<RenderModernizationService>.Instance);

            // 1. Corrupt .lut lacking "DXVK" magic must be rejected cleanly
            byte[] validBinPayload = new byte[4096];
            new Random(7).NextBytes(validBinPayload);
            File.WriteAllBytes(srcBin, validBinPayload);
            File.WriteAllBytes(srcLut, [0x00, 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08, 0x09, 0x0A, 0x0B, 0x0C, 0x0D, 0x0E, 0x0F]);

            bool rejectedCorrupt = service.SeedBaseShaderCache(
                userdataKongor,
                customStateCacheDir: customTargetCache,
                seedSystemLocalAppData: false);
            rejectedCorrupt.Should().BeFalse("corrupt .lut without 'DXVK' magic header must be rejected");
            Directory.GetFiles(customTargetCache).Should().BeEmpty();

            // 2. Seed pre-existing stale smaller .bin + stale .lut in customTargetCache to prove atomic pair upgrade
            string dstHashBin = Path.Combine(customTargetCache, $"{RenderModernizationService.DefaultDxvkAppHash}.dxvk.bin");
            string dstHashLut = Path.Combine(customTargetCache, $"{RenderModernizationService.DefaultDxvkAppHash}.dxvk.lut");
            File.WriteAllBytes(dstHashBin, new byte[512]);
            byte[] staleLutBytes = "DXVK\x06\x00v3.0.2#stale"u8.ToArray();
            File.WriteAllBytes(dstHashLut, staleLutBytes);

            // Write valid upgraded "DXVK" v3.0.2 .lut in parent dxvk_cache/
            byte[] upgradedLutBytes = "DXVK\x06\x00v3.0.2#\x00fs.upgraded_2272_shaders"u8.ToArray();
            File.WriteAllBytes(srcLut, upgradedLutBytes);

            bool seededUpgrade = service.SeedBaseShaderCache(
                userdataKongor,
                customStateCacheDir: customTargetCache,
                seedSystemLocalAppData: false);

            seededUpgrade.Should().BeTrue("child userdataKONGOR call must discover parent dxvk_cache and upgrade both .bin and .lut");
            new FileInfo(dstHashBin).Length.Should().Be(4096);
            File.ReadAllBytes(dstHashLut).Should().Equal(upgradedLutBytes, "companion .dxvk.lut must upgrade atomically alongside .dxvk.bin");
            Directory.GetFiles(customTargetCache, "*.tmp").Should().BeEmpty("atomic temp files must be cleaned/moved");
        }
        finally
        {
            if (Directory.Exists(clientRoot))
            {
                Directory.Delete(clientRoot, recursive: true);
            }
        }
    }

    [Fact]
    public void GenerateDxvkConfig_MapsPresentationModesToDxvk302SyncAndLatencyDirectives()
    {
        // protects: Presentation Mode frame-pacing matrix (-1 app-controlled DirectFlip, 0 Mailbox/Competitive, 1 Legacy) + DXVK 3.0.2 keys
        // fails_when: Hardcoded presentInterval = 1 forces VSync queue lag on DirectFlip or Competitive profiles
        // seam: RenderModernizationService.GenerateDxvkConfig & GenerateDxvk24Profile output verification
        RenderModernizationService service = new(NullLogger<RenderModernizationService>.Instance);

        string directFlipCfg = service.GenerateDxvkConfig(
            RayTracingPreset.Competitive,
            enableDirectFlip: true,
            presentationMode: DirectFlipPresentationMode.DirectFlipDiscard);
        directFlipCfg.Should().Contain("d3d9.presentInterval = -1");
        directFlipCfg.Should().Contain("dxgi.syncInterval = -1");
        directFlipCfg.Should().Contain("dxvk.tearFree = false");
        directFlipCfg.Should().Contain("dxvk.latencySleep = Auto");
        directFlipCfg.Should().Contain("dxvk.latencyTolerance = 0");
        directFlipCfg.Should().Contain("dxvk.disableNvLowLatency2 = false");

        string competitiveProfileCfg = service.GenerateDxvk24Profile(Dxvk24Profile.CompetitiveLowLatency);
        competitiveProfileCfg.Should().Contain("d3d9.presentInterval = 0");
        competitiveProfileCfg.Should().Contain("dxgi.syncInterval = 0");

        string mailboxCfg = service.GenerateDxvkConfig(
            RayTracingPreset.Competitive,
            enableDirectFlip: true,
            presentationMode: DirectFlipPresentationMode.DxvkMailbox);
        mailboxCfg.Should().Contain("d3d9.presentInterval = 0");
        mailboxCfg.Should().Contain("dxgi.syncInterval = 0");
        mailboxCfg.Should().Contain("dxvk.tearFree = true");
        mailboxCfg.Should().Contain("d3d9.tearFree = true");

        string legacyCfg = service.GenerateDxvkConfig(
            RayTracingPreset.Balanced,
            enableDirectFlip: false,
            presentationMode: DirectFlipPresentationMode.LegacyComposited);
        legacyCfg.Should().Contain("d3d9.presentInterval = 1");
        legacyCfg.Should().Contain("dxgi.syncInterval = 1");
        legacyCfg.Should().Contain("dxvk.tearFree = true");
    }

    [Fact]
    public void GenerateDxvkConfig_ReflexDisabled_UsesPipelinedMaxFrameLatency2AndOmitsZeroLatencyTolerance()
    {
        // protects: High-Hz CPU/GPU frame pipelining when NVIDIA Reflex is disabled vs strict 1-frame queue when Reflex is enabled
        // fails_when: Reflex-off config stalls at maxFrameLatency=1 or latencyTolerance=0, or Reflex-on loses maxFrameLatency=1 / latencyTolerance=0
        // seam: RenderModernizationService.GenerateDxvkConfig + ApplyRenderModernization + GetDxvkStatus
        string testDir = CreateIsolatedTestDirectory();
        try
        {
            RenderModernizationService service = new(NullLogger<RenderModernizationService>.Instance);

            string reflexOffDiscard = service.GenerateDxvkConfig(
                RayTracingPreset.Competitive,
                enableReflex: false,
                enableDirectFlip: true,
                presentationMode: DirectFlipPresentationMode.DirectFlipDiscard,
                enableLatencyReduction: true);

            reflexOffDiscard.Should().Contain("d3d9.maxFrameLatency = 2", "Reflex OFF must allow 2-frame CPU/GPU pipelining");
            reflexOffDiscard.Should().Contain("dxgi.maxFrameLatency = 2", "Reflex OFF must emit dxgi.maxFrameLatency = 2");
            reflexOffDiscard.Should().Contain("dxvk.latencySleep = False", "Reflex OFF must avoid DXVK CPU sleep stalls at 200+ Hz");
            reflexOffDiscard.Should().NotContain("dxvk.latencyTolerance = 0", "Reflex OFF must omit 0us latencyTolerance stall");
            reflexOffDiscard.Should().NotContain("d3d9.latencyTolerance = 0", "Reflex OFF must omit d3d9 0us latencyTolerance stall");
            reflexOffDiscard.Should().Contain("dxvk.nvLowLatency = false");
            reflexOffDiscard.Should().Contain("dxvk.disableNvLowLatency2 = true");

            string safePacingDiscard = service.GenerateDxvkConfig(
                RayTracingPreset.Competitive,
                enableReflex: false,
                enableDirectFlip: true,
                presentationMode: DirectFlipPresentationMode.DirectFlipDiscard,
                safeSwapchainPacing: true);
            safePacingDiscard.Should().Contain("d3d9.presentInterval = -1", "Safe swapchain pacing must preserve app-controlled VSync instead of forcing syncInterval=1");
            safePacingDiscard.Should().Contain("dxvk.tearFree = true");
            safePacingDiscard.Should().Contain("d3d9.maxFrameLatency = 2");

            string reflexOffMailbox = service.GenerateDxvkConfig(
                RayTracingPreset.Competitive,
                enableReflex: false,
                enableDirectFlip: true,
                presentationMode: DirectFlipPresentationMode.DxvkMailbox,
                enableLatencyReduction: true);

            reflexOffMailbox.Should().Contain("d3d9.maxFrameLatency = 2");
            reflexOffMailbox.Should().Contain("dxgi.maxFrameLatency = 2");
            reflexOffMailbox.Should().NotContain("dxvk.latencyTolerance = 0");

            string reflexOnDiscard = service.GenerateDxvkConfig(
                RayTracingPreset.Competitive,
                enableReflex: true,
                enableDirectFlip: true,
                presentationMode: DirectFlipPresentationMode.DirectFlipDiscard,
                enableLatencyReduction: true);

            reflexOnDiscard.Should().Contain("d3d9.maxFrameLatency = 1", "Reflex ON must preserve maxFrameLatency = 1");
            reflexOnDiscard.Should().Contain("dxgi.maxFrameLatency = 1", "Reflex ON must preserve dxgi.maxFrameLatency = 1");
            reflexOnDiscard.Should().Contain("dxvk.latencyTolerance = 0", "Reflex ON must preserve dxvk.latencyTolerance = 0");
            reflexOnDiscard.Should().Contain("d3d9.latencyTolerance = 0", "Reflex ON must preserve d3d9.latencyTolerance = 0");
            reflexOnDiscard.Should().Contain("dxvk.nvLowLatency = true");
            reflexOnDiscard.Should().Contain("dxvk.disableNvLowLatency2 = false");

            service.ApplyRenderModernization(
                testDir,
                RayTracingPreset.Competitive,
                enableReflex: false,
                enableDirectFlip: true,
                presentationMode: DirectFlipPresentationMode.DirectFlipDiscard);

            DxvkStatusReport status = service.GetDxvkStatus(testDir);
            status.HasDxvkConfig.Should().BeTrue();
            status.EnableReflex.Should().BeFalse();
            status.MaxFrameLatency.Should().Be(2);
            status.LatencyTolerance.Should().Be(-1);
        }
        finally
        {
            if (Directory.Exists(testDir))
            {
                try { Directory.Delete(testDir, recursive: true); } catch { }
            }
        }
    }
}
