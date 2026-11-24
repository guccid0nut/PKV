// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System;
using System.Collections.Generic;
using System.IO;
using WILLOWMAKER.Core.Services;
using WILLOWMAKER.Core.Services.Diagnostics;
using WILLOWMAKER.Core.ViewModels;
using Xunit;

namespace WILLOWMAKER.Tests;

public sealed class HighHzAndPresetConfigTests
{
    private static MonitorDisplayInfo Create200HzEdidMonitor()
    {
        return new MonitorDisplayInfo(
            Index: 1,
            DeviceName: @"\\.\DISPLAY1",
            FriendlyName: "Test 200Hz Ultrawide",
            IsPrimary: true,
            BoundsX: 0,
            BoundsY: 0,
            NativeWidth: 3440,
            NativeHeight: 1440,
            CurrentRefreshRateHz: 200,
            MaxRefreshRateHz: 200,
            SupportedResolutions:
            [
                new MonitorResolutionOption(
                    Width: 3440,
                    Height: 1440,
                    MaxRefreshRateHz: 200,
                    IsNative: true,
                    DisplayLabel: "3440x1440 (Native)",
                    SupportedRefreshRatesHz: [200, 144, 100, 60]),
                new MonitorResolutionOption(
                    Width: 2560,
                    Height: 1080,
                    MaxRefreshRateHz: 175,
                    IsNative: false,
                    DisplayLabel: "2560x1080",
                    SupportedRefreshRatesHz: [175, 120, 60])
            ]);
    }

    [Theory]
    [InlineData(200, "200 Hz")]
    [InlineData(180, "180 Hz")]
    [InlineData(280, "280 Hz")]
    [InlineData(300, "300 Hz")]
    [InlineData(480, "480 Hz")]
    [InlineData(144, "144 Hz")]
    [InlineData(30, "60 Hz")]
    [InlineData(0, "60 Hz")]
    [InlineData(600, "500 Hz")]
    public void ResolveClosestRefreshRateString_PreservesExactClampedRateWithoutSnapping(int detectedHz, string expectedLabel)
    {
        // protects: Pure OS/EDID refresh-rate preservation (no lossy snapping of 200 Hz to 165/240 Hz)
        // fails_when: ResolveClosestRefreshRateString snaps to the legacy hardcoded standardRates ladder
        // seam: HardwareDetectionService.ResolveClosestRefreshRateString + GetRecommendation
        string resolved = HardwareDetectionService.ResolveClosestRefreshRateString(detectedHz);
        Assert.Equal(expectedLabel, resolved);

        HardwareProfile profile = new(
            GpuName: "NVIDIA GeForce RTX 4080",
            VramBytes: 16L * 1024 * 1024 * 1024,
            SystemRamBytes: 32L * 1024 * 1024 * 1024,
            RefreshRateHz: detectedHz,
            Tier: HardwareTier.EnthusiastRtx,
            Summary: $"Test Rig @ {detectedHz} Hz");

        HardwareRecommendation rec = HardwareDetectionService.GetRecommendation(profile);
        Assert.Equal(expectedLabel, rec.RefreshRate);
    }

    [Fact]
    public void SyncSelectedRefreshRateFromMonitor_InjectsOnlyEdidReportedRatesWithoutArbitraryHardcodes()
    {
        // protects: Pure EDID refresh-rate discovery (no hardcoded 180/200/280/300/480 Hz pollution)
        // fails_when: Arbitrary rates are injected without EDID support or EDID-reported rates fail to inject in sorted order
        // seam: MainViewModel.SelectedMonitor -> SyncSelectedRefreshRateFromMonitor -> AvailableRefreshRates
        MainViewModel vm = new()
        {
            // Reset to baseline defaults so the assertion is deterministic regardless of the host workstation's real monitor EDID
            AvailableRefreshRates =
            [
                "60 Hz", "75 Hz", "120 Hz", "144 Hz", "165 Hz", "240 Hz", "360 Hz", "500 Hz", "Uncapped (Max FPS)"
            ]
        };

        Assert.DoesNotContain("100 Hz", vm.AvailableRefreshRates);
        Assert.DoesNotContain("180 Hz", vm.AvailableRefreshRates);
        Assert.DoesNotContain("200 Hz", vm.AvailableRefreshRates);
        Assert.DoesNotContain("280 Hz", vm.AvailableRefreshRates);
        Assert.DoesNotContain("300 Hz", vm.AvailableRefreshRates);
        Assert.DoesNotContain("480 Hz", vm.AvailableRefreshRates);

        MonitorDisplayInfo monitor200Hz = Create200HzEdidMonitor();
        vm.SelectedMonitor = monitor200Hz;

        Assert.Equal("200 Hz", vm.SelectedRefreshRate);
        Assert.Contains("100 Hz", vm.AvailableRefreshRates);
        Assert.Contains("200 Hz", vm.AvailableRefreshRates);
        Assert.DoesNotContain("180 Hz", vm.AvailableRefreshRates);
        Assert.DoesNotContain("280 Hz", vm.AvailableRefreshRates);
        Assert.DoesNotContain("300 Hz", vm.AvailableRefreshRates);
        Assert.DoesNotContain("480 Hz", vm.AvailableRefreshRates);

        int idx165 = vm.AvailableRefreshRates.IndexOf("165 Hz");
        int idx200 = vm.AvailableRefreshRates.IndexOf("200 Hz");
        int idx240 = vm.AvailableRefreshRates.IndexOf("240 Hz");
        int idxUncapped = vm.AvailableRefreshRates.IndexOf("Uncapped (Max FPS)");
        Assert.True(idx200 > idx165 && idx200 < idx240 && idx200 < idxUncapped,
            "EDID 200 Hz must be inserted sorted between 165 Hz and 240 Hz before Uncapped");

        // Switching resolution injects the newly selected resolution's EDID rates
        vm.SelectedResolution = "2560x1080";
        Assert.Equal("175 Hz", vm.SelectedRefreshRate);
        Assert.Contains("175 Hz", vm.AvailableRefreshRates);
        Assert.DoesNotContain("180 Hz", vm.AvailableRefreshRates);
    }

    [Fact]
    public void DynamicInterfaceFps_MatchesTargetHzAndClampsInAutoexecAndLaunchArguments()
    {
        // protects: Dynamic cg_interfaceFPS in both autoexec.cfg and CLI launch args (replaces hardcoded 144)
        // fails_when: cg_interfaceFPS remains hardcoded at 144 or exceeds K2's safe [60, 500] range
        // seam: MainViewModel.WriteCustomConfiguration + MainViewModel.BuildClientLaunchArgumentList
        string tempDir = Path.Combine(Path.GetTempPath(), "WILLOWMAKER_HighHzFps_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            PlaytestPolicyDto policy = new()
            {
                IsPlaytest = false,
                UseStableReleaseStageCvars = false
            };

            MainViewModel vm = new();
            vm.SelectedMonitor = Create200HzEdidMonitor();

            // 1. Auto 200 Hz monitor -> cg_interfaceFPS = 200 in both autoexec.cfg and CLI args
            vm.WriteCustomConfiguration(tempDir);
            string autoexecPath = Path.Combine(tempDir, "userdataKONGOR", "configuration", "autoexec.cfg");
            string autoexec200 = File.ReadAllText(autoexecPath);
            Assert.Contains("SetSave \"cg_interfaceFPS\" \"200\"", autoexec200);
            Assert.Contains("SetSave \"vid_maxFPS\" \"200\"", autoexec200);

            List<string> args200 = vm.BuildClientLaunchArgumentList(tempDir, "127.0.0.1:55555", policy);
            Assert.Contains("+Set cg_interfaceFPS 200", args200);
            Assert.DoesNotContain("+Set cg_interfaceFPS 144", args200);

            // 2. Manual Uncapped (0 -> 1000 maxFPS) -> cg_interfaceFPS clamped to 500
            vm.ManualRefreshRateOverrideEnabled = true;
            vm.ManualMaxFpsText = "0";
            vm.WriteCustomConfiguration(tempDir);
            string autoexecUncapped = File.ReadAllText(autoexecPath);
            Assert.Contains("SetSave \"cg_interfaceFPS\" \"500\"", autoexecUncapped);
            List<string> argsUncapped = vm.BuildClientLaunchArgumentList(tempDir, "127.0.0.1:55555", policy);
            Assert.Contains("+Set cg_interfaceFPS 500", argsUncapped);

            // 3. Manual sub-60 cap (30 FPS) -> cg_interfaceFPS floored to 60
            vm.ManualMaxFpsText = "30";
            vm.WriteCustomConfiguration(tempDir);
            string autoexecLow = File.ReadAllText(autoexecPath);
            Assert.Contains("SetSave \"cg_interfaceFPS\" \"60\"", autoexecLow);
            List<string> argsLow = vm.BuildClientLaunchArgumentList(tempDir, "127.0.0.1:55555", policy);
            Assert.Contains("+Set cg_interfaceFPS 60", argsLow);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    [Theory]
    [InlineData("Competitive (0% RT)", "Ultra (4096)", "0", "512", "4096", "3500.0000")]
    [InlineData("Balanced RTX (RTAO)", "Medium (1024)", "2", "1024", "1024", "2500.0000")]
    [InlineData("Ultra (Path-Tracing)", "High (2048)", "4", "2048", "2048", "2500.0000")]
    public void WriteCustomConfiguration_ScalesAaAndReflectionByPresetWhilePreservingAuthoritativeShadowFidelity(
        string rayTracingPreset,
        string shadowFidelity,
        string expectedAa,
        string expectedReflectionSize,
        string expectedShadowMapSize,
        string expectedShadowDrawDistance)
    {
        // protects: Preset-scaled vid_antialiasing & vid_reflectionMapSize without clobbering SelectedShadowFidelity
        // fails_when: AA/reflection remain hardcoded at 4/2048 or preset overrides user's Shadow Fidelity dropdown
        // seam: MainViewModel.WriteCustomConfiguration -> autoexec.cfg + dxvk.conf
        string tempDir = Path.Combine(Path.GetTempPath(), "WILLOWMAKER_PresetScale_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            MainViewModel vm = new()
            {
                SelectedRayTracingPreset = rayTracingPreset,
                SelectedShadowFidelity = shadowFidelity,
                EnableNvidiaReflex = false
            };

            vm.WriteCustomConfiguration(tempDir);

            string autoexecPath = Path.Combine(tempDir, "userdataKONGOR", "configuration", "autoexec.cfg");
            Assert.True(File.Exists(autoexecPath));
            string autoexecContent = File.ReadAllText(autoexecPath);

            Assert.Contains($"SetSave \"vid_antialiasing\" \"{expectedAa}\"", autoexecContent);
            Assert.Contains($"SetSave \"vid_reflectionMapSize\" \"{expectedReflectionSize}\"", autoexecContent);
            Assert.Contains($"SetSave \"vid_shadowmapSize\" \"{expectedShadowMapSize}\"", autoexecContent);
            Assert.Contains($"SetSave \"vid_shadowDrawDistance\" \"{expectedShadowDrawDistance}\"", autoexecContent);

            string dxvkPath = Path.Combine(tempDir, "userdataKONGOR", "dxvk.conf");
            Assert.True(File.Exists(dxvkPath));
            string dxvkContent = File.ReadAllText(dxvkPath);
            Assert.Contains("d3d9.maxFrameLatency = 2", dxvkContent);
            Assert.Contains("dxgi.maxFrameLatency = 2", dxvkContent);
            Assert.Contains("dxvk.latencySleep = False", dxvkContent);
            Assert.DoesNotContain("dxvk.latencyTolerance = 0", dxvkContent);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    [Theory]
    [InlineData("Off (Clean Screen)", false, false)]
    [InlineData("Minimal FPS", false, false)]
    [InlineData("FPS Only", false, false)]
    [InlineData("Pro Diagnostics", false, true)]
    [InlineData("Modern HUD", false, true)]
    [InlineData("Minimal FPS", true, true)]
    public void ShouldInjectVulkanCaptureLayer_GatesLayerToCaptureOrExplicitModernHud(
        string hudMode,
        bool captureEnabled,
        bool expectedInject)
    {
        // protects: HZ-02 Pure DirectFlip Vulkan path (omits VK_LAYER_HON_replay_capture during normal gameplay)
        // fails_when: Minimal FPS / Off injects VK_LAYER_HON_replay_capture or Pro Diagnostics/capture fails to inject it
        // seam: MainViewModel.ShouldInjectVulkanCaptureLayer + ConfigureChildClientEnvironment
        Assert.Equal(expectedInject, MainViewModel.ShouldInjectVulkanCaptureLayer(hudMode, captureEnabled));

        string tempDir = Path.Combine(Path.GetTempPath(), "WILLOWMAKER_VkLayerGate_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(tempDir, "userdataKONGOR"));
        try
        {
            if (!captureEnabled)
            {
                MainViewModel vm = new()
                {
                    SelectedVulkanHudMode = hudMode,
                    AllowThirdPartyVulkanOverlays = false
                };
                System.Diagnostics.ProcessStartInfo psi = new()
                {
                    FileName = Path.Combine(tempDir, "hon_x64.exe"),
                    UseShellExecute = false
                };
                string ephemeralIpcNonce = Guid.NewGuid().ToString("N");
                vm.ConfigureChildClientEnvironment(psi, tempDir, ephemeralIpcNonce, isWindowsOverride: true);

                if (expectedInject)
                {
                    Assert.Equal("VK_LAYER_HON_replay_capture", psi.EnvironmentVariables["VK_INSTANCE_LAYERS"]);
                    Assert.Equal("1", psi.EnvironmentVariables["HON_MODERN_HUD"]);
                    Assert.False(psi.EnvironmentVariables.ContainsKey("HON_CAPTURE_DISABLE"));
                }
                else
                {
                    Assert.False(psi.EnvironmentVariables.ContainsKey("VK_INSTANCE_LAYERS"));
                    Assert.False(psi.EnvironmentVariables.ContainsKey("HON_MODERN_HUD"));
                    Assert.Equal("1", psi.EnvironmentVariables["HON_CAPTURE_DISABLE"]);
                    Assert.Contains("VK_LAYER_HON_replay_capture", psi.EnvironmentVariables["VK_LOADER_LAYERS_DISABLE"], StringComparison.Ordinal);
                    Assert.Equal("0", psi.EnvironmentVariables["HON_FSR_ENABLE"]);
                }
                Assert.Contains("~implicit~", psi.EnvironmentVariables["VK_LOADER_LAYERS_DISABLE"], StringComparison.Ordinal);
            }
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    [Fact]
    public void ResolveEffectiveSafeSwapchainPacing_ProtectsHardRecordersWithoutDowngradingDirectFlipForBlockedCompanions()
    {
        // protects: HZ-04 launch conflict gating without breaking DirectFlip for Discord/USB auxiliary displays
        // fails_when: Discord with AllowThirdPartyVulkanOverlays=false forces SafeSwapchainPacing, or RTSS fails to trigger pacing
        // seam: MainViewModel.ResolveEffectiveSafeSwapchainPacing
        OverlayConflictReport companionOnly = new(
            HasActiveConflicts: false,
            HasVirtualDisplayAdapters: true,
            ShouldApplySafeSwapchainPacing: true,
            ConflictingProcesses: [],
            WarningBannerText: string.Empty,
            CompanionProcesses: ["Discord"]);

        Assert.False(MainViewModel.ResolveEffectiveSafeSwapchainPacing(companionOnly, allowThirdPartyVulkanOverlays: false));
        Assert.True(MainViewModel.ResolveEffectiveSafeSwapchainPacing(companionOnly, allowThirdPartyVulkanOverlays: true));

        OverlayConflictReport hardRecorder = new(
            HasActiveConflicts: true,
            HasVirtualDisplayAdapters: false,
            ShouldApplySafeSwapchainPacing: true,
            ConflictingProcesses: ["RTSSHooks64"],
            WarningBannerText: "Conflict",
            CompanionProcesses: []);

        Assert.True(MainViewModel.ResolveEffectiveSafeSwapchainPacing(hardRecorder, allowThirdPartyVulkanOverlays: false));
        Assert.True(MainViewModel.ResolveEffectiveSafeSwapchainPacing(hardRecorder, allowThirdPartyVulkanOverlays: true));
    }

    [Fact]
    public void PresetButtons_BatchMutationsIntoSingleConfigWriteWithoutIntermediateThrashing()
    {
        // protects: HZ-07 preset-application batching (prevents 6-8 redundant Process.GetProcesses() & config writes per click)
        // fails_when: ApplyBestFpsPreset / ApplyBestGraphicsPreset / ApplyRecommendedSettings fire WriteCustomConfiguration multiple times
        // seam: MainViewModel._isApplyingPreset -> OnSelected*Changed -> WriteCustomConfiguration
        MainViewModel vm = new()
        {
            SkipProcessConflictScanForTesting = true,
            EnableRayTracing = true,
            SelectedRayTracingPreset = "Ultra (Path-Tracing)",
            SelectedShadowFidelity = "Ultra (4096)",
            SelectedPresentationMode = "Legacy Blit (DWM Composited)",
            ManualRefreshRateOverrideEnabled = true,
            ManualMaxFpsText = "120"
        };

        int beforeFps = vm.ConfigWriteInvocationCountForTesting;
        vm.ApplyBestFpsPreset();
        Assert.Equal(1, vm.ConfigWriteInvocationCountForTesting - beforeFps);

        int beforeGfx = vm.ConfigWriteInvocationCountForTesting;
        vm.ApplyBestGraphicsPreset();
        Assert.Equal(1, vm.ConfigWriteInvocationCountForTesting - beforeGfx);

        int beforeRec = vm.ConfigWriteInvocationCountForTesting;
        vm.ApplyRecommendedSettings();
        Assert.Equal(1, vm.ConfigWriteInvocationCountForTesting - beforeRec);
    }

    [Fact]
    public void LauncherSettingsSchemaV2Migration_UpgradesLegacyDefaultsOnceAndPreservesPostV2Choices()
    {
        // protects: UX-01/UX-03 schema v2 migration for existing installs (Minimal FPS -> Off, legacy Ultra 4096 -> High 2048)
        // fails_when: Pre-v2 installs stay stuck on Minimal FPS/Ultra 4096 or post-v2 deliberate choices are overwritten
        // seam: LauncherSettingsService.Load -> LauncherSettingsModel.ApplySchemaMigrations
        string tempDir = Path.Combine(Path.GetTempPath(), "WILLOWMAKER_SchemaV2_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            // 1. Legacy pre-v2 file (no settingsSchemaVersion key)
            string settingsPath = LauncherSettingsService.GetSettingsFilePath(tempDir);
            File.WriteAllText(settingsPath, """
                {
                  "shadowFidelity": "Ultra (4096)",
                  "vulkanHudMode": "Minimal FPS",
                  "refreshRate": "200 Hz"
                }
                """);

            LauncherSettingsModel migrated = LauncherSettingsService.Load(tempDir);
            Assert.Equal(LauncherSettingsModel.CurrentSettingsSchemaVersion, migrated.SettingsSchemaVersion);
            Assert.Equal("High (2048)", migrated.ShadowFidelity);
            Assert.Equal("Off (Clean Screen)", migrated.VulkanHudMode);

            // 2. User deliberately chooses Ultra (4096) & Minimal FPS under v2 -> preserved verbatim on reload
            migrated.ShadowFidelity = "Ultra (4096)";
            migrated.VulkanHudMode = "Minimal FPS";
            LauncherSettingsService.Save(migrated, tempDir);

            LauncherSettingsModel reloaded = LauncherSettingsService.Load(tempDir);
            Assert.Equal(LauncherSettingsModel.CurrentSettingsSchemaVersion, reloaded.SettingsSchemaVersion);
            Assert.Equal("Ultra (4096)", reloaded.ShadowFidelity);
            Assert.Equal("Minimal FPS", reloaded.VulkanHudMode);

            // 3. Legacy file with explicit Pro Diagnostics preserves Pro Diagnostics during v2 migration
            File.WriteAllText(settingsPath, """
                {
                  "shadowFidelity": "Medium (1024)",
                  "vulkanHudMode": "Pro Diagnostics"
                }
                """);
            LauncherSettingsModel devMigrated = LauncherSettingsService.Load(tempDir);
            Assert.Equal("Medium (1024)", devMigrated.ShadowFidelity);
            Assert.Equal("Pro Diagnostics", devMigrated.VulkanHudMode);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void PresetTransparency_UpdatesActiveBadgeConcreteSummaryAndDiffBannerOnClicksAndManualEdits()
    {
        // protects: UX-02 preset transparency (distinct Recommended / Best FPS / Best Graphics / Custom states & concrete diffs)
        // fails_when: Clicking presets produces empty/misleading feedback or manual shadow edits fail to update badge/summary
        // seam: MainViewModel.ApplyBestFpsPreset / ApplyBestGraphicsPreset / OnSelectedShadowFidelityChanged -> Box 3 bindings
        MainViewModel vm = new() { SkipProcessConflictScanForTesting = true };

        vm.ApplyBestGraphicsPreset();
        Assert.Equal("BestGraphics", vm.ActiveGraphicsPresetKey);
        Assert.Equal("BEST GRAPHICS", vm.ActiveGraphicsPresetBadgeText);
        Assert.True(vm.IsBestGraphicsPresetActive);
        Assert.Equal("Ultra (4096)", vm.SelectedShadowFidelity);
        Assert.Equal("Off (Clean Screen)", vm.SelectedVulkanHudMode);
        Assert.Contains("Shadows: 4096", vm.ActiveGraphicsPipelineSummary);
        Assert.Contains("AA: 4x MSAA", vm.ActiveGraphicsPipelineSummary);

        vm.ApplyBestFpsPreset();
        Assert.Equal("BestFps", vm.ActiveGraphicsPresetKey);
        Assert.Equal("BEST FPS", vm.ActiveGraphicsPresetBadgeText);
        Assert.Equal("Medium (1024)", vm.SelectedShadowFidelity);
        Assert.Contains("Shadows 4096 → 1024", vm.LastPresetChangeSummary);
        Assert.Contains("AA 4x MSAA → Off", vm.LastPresetChangeSummary);
        Assert.Contains("Refl 2048 → 512", vm.LastPresetChangeSummary);
        Assert.Contains("SSAO On → Off", vm.LastPresetChangeSummary);

        // Clicking Best FPS a second time reports no changes needed
        vm.ApplyBestFpsPreset();
        Assert.Contains("already active", vm.LastPresetChangeSummary);

        // Manual Shadow Fidelity edit immediately updates badge to Custom & updates live summary
        vm.SelectedShadowFidelity = "High (2048)";
        Assert.Equal("Custom", vm.ActiveGraphicsPresetKey);
        Assert.Equal("CUSTOM", vm.ActiveGraphicsPresetBadgeText);
        Assert.Contains("Shadows: 2048", vm.ActiveGraphicsPipelineSummary);
        Assert.Contains("Shadow Fidelity → High (2048)", vm.LastPresetChangeSummary);
    }

    [Fact]
    public void LaunchReceiptService_RedactsSecretsAllowlistsEnvAndDetectsModuleDrift()
    {
        // protects: SHJ launch-parity receipt privacy (zero KONGOR_IPC_AUTH_KEY/password leakage) & post-boot module drift detection
        // fails_when: Sensitive env/args leak into willowmaker_last_launch_receipt.json or VkLayer/3rd-party hooks go undetected
        // seam: LaunchReceiptService.WriteLaunchReceipt + EvaluateLoadedModules
        string tempDir = Path.Combine(Path.GetTempPath(), $"kongor_launch_receipt_{Guid.NewGuid():N}");
        try
        {
            string userdataDir = Path.Combine(tempDir, "userdataKONGOR");
            string configDir = Path.Combine(userdataDir, "configuration");
            Directory.CreateDirectory(configDir);

            string dxvkPath = Path.Combine(userdataDir, "dxvk.conf");
            File.WriteAllText(dxvkPath, "d3d9.maxFrameLatency = 2 # allow 2 frames\ndxvk.latencySleep = False\ndxvk.enableLatencyReduction = false\n");

            string autoexecPath = Path.Combine(configDir, "autoexec.cfg");
            File.WriteAllText(autoexecPath, "SetSave \"host_maxFPS\" \"200\"\nSetSave \"cg_interfaceFPS\" \"200\"\nSetSave \"vid_antialiasing\" \"2\"\n");

            System.Diagnostics.ProcessStartInfo psi = new();
            psi.EnvironmentVariables["DXVK_CONFIG_FILE"] = dxvkPath;
            psi.EnvironmentVariables["HON_CAPTURE_DISABLE"] = "1";
            psi.EnvironmentVariables["VK_LOADER_LAYERS_DISABLE"] = "~implicit~,VK_LAYER_HON_replay_capture,VK_LAYER_OBS_*";
            psi.EnvironmentVariables["KONGOR_IPC_AUTH_KEY"] = "super-secret-ipc-key-must-never-leak";
            psi.EnvironmentVariables["UNRELATED_SECRET_TOKEN"] = "do-not-serialize";

            string receiptPath = Path.Combine(tempDir, "logs", "shj", "willowmaker_last_launch_receipt.json");
            string written = LaunchReceiptService.WriteLaunchReceipt(
                clientDir: tempDir,
                clientExecutablePath: Path.Combine(tempDir, "hon_x64.exe"),
                gamePid: 12345,
                rawArguments: ["-masterserver 127.0.0.1:55555", "+Set login_password \"quoted secret pw\"", "-password", "plaintextpw", "--token", "tok123", "+Set host_maxFPS 200"],
                childEnvironment: psi.EnvironmentVariables,
                selectedVulkanHudMode: "Minimal FPS",
                selectedRayTracingPreset: "Balanced RTX (RTAO)",
                selectedShadowFidelity: "High (2048)",
                selectedPresentationMode: "DirectFlip (FLIP_DISCARD)",
                allowThirdPartyVulkanOverlays: false,
                expectedVkLayerReplayCaptureLoaded: false,
                isSafeMode: false,
                explicitReceiptPath: receiptPath);

            Assert.Equal(receiptPath, written);
            Assert.True(File.Exists(receiptPath));

            string rawJson = File.ReadAllText(receiptPath);
            Assert.DoesNotContain("super-secret-ipc-key-must-never-leak", rawJson, StringComparison.Ordinal);
            Assert.DoesNotContain("KONGOR_IPC_AUTH_KEY", rawJson, StringComparison.Ordinal);
            Assert.DoesNotContain("UNRELATED_SECRET_TOKEN", rawJson, StringComparison.Ordinal);
            Assert.DoesNotContain("quoted secret pw", rawJson, StringComparison.Ordinal);
            Assert.DoesNotContain("plaintextpw", rawJson, StringComparison.Ordinal);
            Assert.DoesNotContain("tok123", rawJson, StringComparison.Ordinal);
            Assert.Contains("HON_CAPTURE_DISABLE", rawJson, StringComparison.Ordinal);
            Assert.Contains("VK_LOADER_LAYERS_DISABLE", rawJson, StringComparison.Ordinal);
            Assert.Contains("\"d3d9.maxFrameLatency\":\"2\"", rawJson, StringComparison.Ordinal);
            Assert.Contains("\"host_maxFPS\":\"200\"", rawJson, StringComparison.Ordinal);
            string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (!string.IsNullOrWhiteSpace(userProfile))
            {
                Assert.DoesNotContain(userProfile, rawJson, StringComparison.OrdinalIgnoreCase);
            }

            LaunchReceiptPostBootAuditDto cleanAudit =
                LaunchReceiptService.EvaluateLoadedModules(
                    gamePid: 12345,
                    loadedModules:
                    [
                        ("vid_vulkan_x64.dll", Path.Combine(tempDir, "vid_vulkan_x64.dll")),
                        ("d3d9.dll", Path.Combine(tempDir, "d3d9.dll")),
                        ("version.dll", Path.Combine(tempDir, "version.dll"))
                    ],
                    expectedVkLayerReplayCaptureLoaded: false,
                    allowThirdPartyVulkanOverlays: false);
            Assert.True(cleanAudit.Passed);
            Assert.Empty(cleanAudit.Findings);

            LaunchReceiptPostBootAuditDto driftedAudit =
                LaunchReceiptService.EvaluateLoadedModules(
                    gamePid: 12345,
                    loadedModules:
                    [
                        ("vid_vulkan_x64.dll", Path.Combine(tempDir, "vid_vulkan_x64.dll")),
                        ("VkLayer_hon_replay_capture.dll", Path.Combine(tempDir, "VkLayer_hon_replay_capture.dll")),
                        ("MediaSDKVulkanLayer64.dll", @"C:\Program Files\TikTok\MediaSDKVulkanLayer64.dll")
                    ],
                    expectedVkLayerReplayCaptureLoaded: false,
                    allowThirdPartyVulkanOverlays: false);
            Assert.False(driftedAudit.Passed);
            Assert.True(driftedAudit.VkLayerReplayCaptureLoaded);
            Assert.Contains("MediaSDKVulkanLayer64.dll", driftedAudit.DetectedDeniedHooks);
            Assert.Equal(2, driftedAudit.Findings.Count);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }
}
