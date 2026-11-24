// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using WILLOWMAKER.Core.Services;
using WILLOWMAKER.Core.Services.Diagnostics;
using WILLOWMAKER.Core.ViewModels;
using Xunit;
using Xunit.Abstractions;

namespace WILLOWMAKER.Tests;

/// <summary>
/// Clean-PC / fresh-profile first-run smoke tests verifying zero-config initialization,
/// v2 schema defaults, graceful 60 Hz / headless VRR fallback, zero false cold-boot crash banners,
/// and PII-redacted launch receipts on an empty client root.
/// </summary>
public sealed class CleanProfileFirstRunTests(ITestOutputHelper output)
{
    [Fact]
    public void FreshEmptyClientRoot_FirstBootSettingsV2_ZeroCrashBanner_AndRedactedLaunchReceipt()
    {
        // protects: First-time v5.0.2 player onboarding on a brand-new install without pre-existing userdataKONGOR/launcher_settings.json.
        // fails_when: Clean install defaults regress from schema v2 (High 2048 shadows, Clean HUD, overlays blocked), shows a false crash banner, or leaks user paths/secrets into launch receipts.
        // seam: LauncherSettingsService.Load/SaveToCanonicalPath + MainViewModel.CheckColdBootCrashSentinel + LaunchReceiptService.WriteLaunchReceipt.

        string clientRoot = Path.Combine(Path.GetTempPath(), $"Kongor_CleanProfile_{Guid.NewGuid():N}");
        Directory.CreateDirectory(clientRoot);

        try
        {
            string userdataDir = Path.Combine(clientRoot, "userdataKONGOR");
            Assert.False(Directory.Exists(userdataDir));

            // 1. Load defaults against non-existent userdataKONGOR directory
            LauncherSettingsModel defaults = LauncherSettingsService.Load(userdataDir);
            Assert.Equal(LauncherSettingsModel.CurrentSettingsSchemaVersion, defaults.SettingsSchemaVersion);
            Assert.Equal("High (2048)", defaults.ShadowFidelity);
            Assert.Equal("Off (Clean Screen)", defaults.VulkanHudMode);
            Assert.False(defaults.AllowThirdPartyVulkanOverlays);
            Assert.True(defaults.EnableDirectFlip);
            Assert.Equal("DirectFlip (FLIP_DISCARD)", defaults.PresentationMode);

            // 2. Persist atomically and verify round-trip
            LauncherSettingsService.SaveToCanonicalPath(defaults, userdataDir);
            string canonicalPath = LauncherSettingsService.GetSettingsFilePath(userdataDir);
            Assert.True(File.Exists(canonicalPath));

            LauncherSettingsModel reloaded = LauncherSettingsService.Load(userdataDir);
            Assert.Equal(2, reloaded.SettingsSchemaVersion);
            Assert.Equal("High (2048)", reloaded.ShadowFidelity);
            Assert.Equal("Off (Clean Screen)", reloaded.VulkanHudMode);
            Assert.False(reloaded.AllowThirdPartyVulkanOverlays);

            // 3. Cold-boot sentinel check on clean install must emit zero false crash banners
            MainViewModel vm = new();
            try
            {
                vm.CheckColdBootCrashSentinel(clientRoot);
                Assert.False(vm.HasCrashReportBanner);
                Assert.True(string.IsNullOrEmpty(vm.LastCrashFaultSummary));
            }
            finally
            {
                vm.DisposeApplicationBackgroundServices();
            }

            // 4. First launch receipt on clean profile (no dxvk.conf or autoexec.cfg yet) writes cleanly and redacts paths/secrets
            ProcessStartInfo psi = new();
            psi.EnvironmentVariables["HON_CAPTURE_DISABLE"] = "1";
            psi.EnvironmentVariables["VK_LOADER_LAYERS_DISABLE"] = "~implicit~,VK_LAYER_HON_replay_capture";
            psi.EnvironmentVariables["KONGOR_IPC_AUTH_KEY"] = "secret-first-boot-key";

            string receiptPath = Path.Combine(clientRoot, "logs", "shj", LaunchReceiptService.ReceiptFileName);
            string written = LaunchReceiptService.WriteLaunchReceipt(
                clientDir: clientRoot,
                clientExecutablePath: Path.Combine(clientRoot, "hon_x64.exe"),
                gamePid: 4242,
                rawArguments: ["-masterserver 127.0.0.1:55555", "-password", "clean-boot-secret"],
                childEnvironment: psi.EnvironmentVariables,
                selectedVulkanHudMode: reloaded.VulkanHudMode,
                selectedRayTracingPreset: reloaded.RayTracingPresetName,
                selectedShadowFidelity: reloaded.ShadowFidelity,
                selectedPresentationMode: reloaded.PresentationMode,
                allowThirdPartyVulkanOverlays: reloaded.AllowThirdPartyVulkanOverlays,
                expectedVkLayerReplayCaptureLoaded: false,
                isSafeMode: false,
                explicitReceiptPath: receiptPath);

            Assert.Equal(receiptPath, written);
            Assert.True(File.Exists(receiptPath));
            string rawJson = File.ReadAllText(receiptPath);
            Assert.DoesNotContain("secret-first-boot-key", rawJson, StringComparison.Ordinal);
            Assert.DoesNotContain("clean-boot-secret", rawJson, StringComparison.Ordinal);
            Assert.Contains("\"selectedShadowFidelity\":\"High (2048)\"", rawJson, StringComparison.Ordinal);
            Assert.Contains("\"selectedVulkanHudMode\":\"Off (Clean Screen)\"", rawJson, StringComparison.Ordinal);

            string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (!string.IsNullOrWhiteSpace(userProfile))
            {
                Assert.DoesNotContain(userProfile, rawJson, StringComparison.OrdinalIgnoreCase);
            }

            output.WriteLine($"[CleanProfile] Verified v{reloaded.SettingsSchemaVersion} defaults, 0 false crash banners, and redacted receipt ({rawJson.Length} bytes).");
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
    public void DisplayVrrDetection_DegradesGracefullyOnNullHeadlessFixed60HzAndMismatchedMonitors()
    {
        // protects: Zero-exception cross-PC VRR detection across 60 Hz non-VRR monitors, headless/RDP sessions, and misconfigured Windows desktop refresh rates.
        // fails_when: InspectDisplayReadiness throws on null monitor, flags fixed 60 Hz as VRR-Ready, or fails to warn when a 144 Hz panel runs at 60 Hz.
        // seam: DisplayVrrDetectionService.InspectDisplayReadiness + EvaluateDisplayVrrReadiness.

        // 1. Headless / null monitor fallback
        DisplayVrrReadinessReport nullReport = DisplayVrrDetectionService.InspectDisplayReadiness(
            monitor: null,
            selectedDisplayMode: "Borderless Windowed",
            selectedPresentationMode: "DirectFlip (FLIP_DISCARD)",
            enableDirectFlip: true,
            activeDriver: "vid_vulkan");
        Assert.NotNull(nullReport);
        Assert.False(string.IsNullOrWhiteSpace(nullReport.PillText));
        Assert.Equal(DisplayVrrReadinessLevel.FixedRefresh, nullReport.Level);

        // 2. Standard 60 Hz office monitor (non-VRR)
        MonitorDisplayInfo fixed60Monitor = new(
            Index: 1,
            DeviceName: @"\\.\DISPLAY1",
            FriendlyName: "Generic 60Hz Office Panel",
            IsPrimary: true,
            BoundsX: 0,
            BoundsY: 0,
            NativeWidth: 1920,
            NativeHeight: 1080,
            CurrentRefreshRateHz: 60,
            MaxRefreshRateHz: 60,
            SupportedResolutions:
            [
                new MonitorResolutionOption(1920, 1080, 60, true, "1920x1080 (1080p FHD) [Native]", [60])
            ]);

        DisplayVrrReadinessReport fixed60Report = DisplayVrrDetectionService.EvaluateDisplayVrrReadiness(
            monitor: fixed60Monitor,
            edidVrr: new EdidVrrCapability(false, 56, 76, "EDID Fixed Range"),
            osState: new WindowsVrrOsState(DxgiAllowTearingSupported: true, WindowsVrrOptimizeEnabled: true, SwapEffectUpgradeEnabled: true, IsWindows11DefaultFlipUpgrade: true),
            selectedDisplayMode: "Borderless Windowed",
            selectedPresentationMode: "DirectFlip (FLIP_DISCARD)",
            enableDirectFlip: true,
            activeDriver: "vid_vulkan");

        Assert.Equal(DisplayVrrReadinessLevel.FixedRefresh, fixed60Report.Level);
        Assert.False(fixed60Report.IsMonitorVrrCapable);
        Assert.Equal(60, fixed60Report.ActiveDesktopHz);

        // 3. 144 Hz VRR panel stuck at 60 Hz in Windows Display Settings -> Advisory warning
        MonitorDisplayInfo throttled144Monitor = new(
            Index: 1,
            DeviceName: @"\\.\DISPLAY1",
            FriendlyName: "144Hz Gaming Panel (Stuck @ 60Hz)",
            IsPrimary: true,
            BoundsX: 0,
            BoundsY: 0,
            NativeWidth: 2560,
            NativeHeight: 1440,
            CurrentRefreshRateHz: 60,
            MaxRefreshRateHz: 144,
            SupportedResolutions:
            [
                new MonitorResolutionOption(2560, 1440, 144, true, "2560x1440 (1440p QHD) [Native]", [144, 120, 60])
            ]);

        DisplayVrrReadinessReport advisoryReport = DisplayVrrDetectionService.EvaluateDisplayVrrReadiness(
            monitor: throttled144Monitor,
            edidVrr: new EdidVrrCapability(true, 48, 144, "EDID Adaptive-Sync"),
            osState: new WindowsVrrOsState(DxgiAllowTearingSupported: true, WindowsVrrOptimizeEnabled: true, SwapEffectUpgradeEnabled: true, IsWindows11DefaultFlipUpgrade: true),
            selectedDisplayMode: "Borderless Windowed",
            selectedPresentationMode: "DirectFlip (FLIP_DISCARD)",
            enableDirectFlip: true,
            activeDriver: "vid_vulkan");

        Assert.Equal(DisplayVrrReadinessLevel.Advisory, advisoryReport.Level);
        Assert.True(advisoryReport.IsMonitorVrrCapable);
        Assert.Equal(138, DisplayVrrDetectionService.CalculateRecommendedVrrCapFps(144));

        output.WriteLine($"[CleanProfile][VRR] null='{nullReport.PillText}', fixed60='{fixed60Report.PillText}', throttled144='{advisoryReport.PillText}'.");
    }
}
