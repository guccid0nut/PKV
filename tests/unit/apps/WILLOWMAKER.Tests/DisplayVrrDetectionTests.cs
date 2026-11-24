// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System;
using System.Collections.Generic;
using WILLOWMAKER.Core.Services;
using WILLOWMAKER.Core.ViewModels;
using Xunit;
using Xunit.Abstractions;

namespace WILLOWMAKER.Tests;

public sealed class DisplayVrrDetectionTests(ITestOutputHelper output)
{
    private static byte[] BuildSyntheticEdidWithRangeLimits(byte minV, byte maxV, byte rangeOffsetFlags = 0)
    {
        byte[] edid = new byte[128];
        // Standard 8-byte EDID magic header
        edid[0] = 0x00; edid[1] = 0xFF; edid[2] = 0xFF; edid[3] = 0xFF;
        edid[4] = 0xFF; edid[5] = 0xFF; edid[6] = 0xFF; edid[7] = 0x00;

        // Place 0xFD Monitor Range Limits in descriptor slot 2 (offset 72)
        int off = 72;
        edid[off] = 0x00;
        edid[off + 1] = 0x00;
        edid[off + 2] = 0x00;
        edid[off + 3] = 0xFD;
        edid[off + 4] = rangeOffsetFlags;
        edid[off + 5] = minV;
        edid[off + 6] = maxV;
        return edid;
    }

    private static byte[] BuildSyntheticEdidWithHdmi21Vrr(int vrrMin, int vrrMax)
    {
        byte[] edid = new byte[256];
        edid[0] = 0x00; edid[1] = 0xFF; edid[2] = 0xFF; edid[3] = 0xFF;
        edid[4] = 0xFF; edid[5] = 0xFF; edid[6] = 0xFF; edid[7] = 0x00;
        edid[126] = 1; // 1 extension block

        // CTA-861 Extension Block at offset 128
        edid[128] = 0x02; // CTA tag
        edid[129] = 0x03; // revision 3
        edid[130] = 20;   // DTD offset (> 4 + 11)

        // Vendor-Specific Data Block: tag=3 (bits 7:5), length=10 (bits 4:0)
        edid[132] = (3 << 5) | 10;
        // HDMI Forum IEEE OUI 0xC45DD8 -> D8 5D C4
        edid[133] = 0xD8;
        edid[134] = 0x5D;
        edid[135] = 0xC4;
        // Byte +8: VRRmin (bits 5:0) | VRRmax[9:8] (bits 7:6)
        edid[133 + 8] = (byte)((vrrMin & 0x3F) | (((vrrMax >> 8) & 0x03) << 6));
        // Byte +9: VRRmax[7:0]
        edid[133 + 9] = (byte)(vrrMax & 0xFF);
        return edid;
    }

    private static MonitorDisplayInfo CreateMonitor(int currentHz, int maxHz, string friendlyName = "VG27AQ5A")
    {
        return new MonitorDisplayInfo(
            Index: 1,
            DeviceName: @"\\.\DISPLAY1",
            FriendlyName: friendlyName,
            IsPrimary: true,
            BoundsX: 0,
            BoundsY: 0,
            NativeWidth: 2560,
            NativeHeight: 1440,
            CurrentRefreshRateHz: currentHz,
            MaxRefreshRateHz: maxHz,
            SupportedResolutions:
            [
                new MonitorResolutionOption(2560, 1440, maxHz, true, $"2560x1440 (1440p QHD) [Native]", [maxHz, 144, 60])
            ]);
    }

    [Fact]
    public void ParseEdidVrrCapability_Parses200HzAdaptiveSyncAnd360HzOffsetAndFixed60Hz()
    {
        // protects: UX-04 EDID 0xFD range limits parsing including >255Hz EDID 1.4 offset bits
        // fails_when: VRR monitor range is missed or fixed 60 Hz monitor is falsely flagged as VRR
        // seam: DisplayVrrDetectionService.ParseEdidVrrCapability
        byte[] vg27aq5a = BuildSyntheticEdidWithRangeLimits(minV: 48, maxV: 200);
        EdidVrrCapability vrr200 = DisplayVrrDetectionService.ParseEdidVrrCapability(vg27aq5a);
        Assert.True(vrr200.IsVrrCapable);
        Assert.Equal(48, vrr200.MinHz);
        Assert.Equal(200, vrr200.MaxHz);

        // 360 Hz monitor uses EDID 1.4 rangeOffset bit 1 (+255 to maxV: 105 + 255 = 360)
        byte[] edid360 = BuildSyntheticEdidWithRangeLimits(minV: 48, maxV: 105, rangeOffsetFlags: 0x02);
        EdidVrrCapability vrr360 = DisplayVrrDetectionService.ParseEdidVrrCapability(edid360);
        Assert.True(vrr360.IsVrrCapable);
        Assert.Equal(48, vrr360.MinHz);
        Assert.Equal(360, vrr360.MaxHz);

        // Fixed 60 Hz panel (56-76 Hz) must NOT be treated as VRR
        byte[] fixed60 = BuildSyntheticEdidWithRangeLimits(minV: 56, maxV: 76);
        EdidVrrCapability noVrr = DisplayVrrDetectionService.ParseEdidVrrCapability(fixed60);
        Assert.False(noVrr.IsVrrCapable);

        // HDMI 2.1 Forum VSDB (40-120 Hz)
        byte[] hdmi21 = BuildSyntheticEdidWithHdmi21Vrr(40, 120);
        EdidVrrCapability vrrHdmi = DisplayVrrDetectionService.ParseEdidVrrCapability(hdmi21);
        Assert.True(vrrHdmi.IsVrrCapable);
        Assert.Equal(40, vrrHdmi.MinHz);
        Assert.Equal(120, vrrHdmi.MaxHz);
        Assert.Equal("HDMI 2.1 VRR", vrrHdmi.Source);
    }

    [Theory]
    [InlineData(200, 189)]
    [InlineData(144, 138)]
    [InlineData(165, 157)]
    [InlineData(240, 224)]
    [InlineData(120, 116)]
    [InlineData(60, 57)]
    public void CalculateRecommendedVrrCapFps_FollowsSubRefreshFormula(int panelHz, int expectedCapFps)
    {
        // protects: UX-04 VRR low-latency sub-refresh cap math (Hz - Hz^2/3600)
        // fails_when: Recommended cap equals/exceeds refresh rate or diverges from formula
        // seam: DisplayVrrDetectionService.CalculateRecommendedVrrCapFps
        Assert.Equal(expectedCapFps, DisplayVrrDetectionService.CalculateRecommendedVrrCapFps(panelHz));
    }

    [Fact]
    public void TryParseMonitorRegistrySubPath_ExtractsDisplayEnumPathFromDevicePath()
    {
        // protects: UX-04 Windows CCD monitorDevicePath -> HKLM DISPLAY EDID mapping
        // fails_when: Valid CCD device path fails to resolve registry subpath
        // seam: DisplayVrrDetectionService.TryParseMonitorRegistrySubPath
        bool ok = DisplayVrrDetectionService.TryParseMonitorRegistrySubPath(
            @"\\?\DISPLAY#AUS2718#5&382a3a70&0&UID4353#{e6f07b5f-ee97-4a90-b076-33f57bf4eaa7}",
            out string regPath);

        Assert.True(ok);
        Assert.Equal(@"SYSTEM\CurrentControlSet\Enum\DISPLAY\AUS2718\5&382a3a70&0&UID4353\Device Parameters", regPath);
    }

    [Fact]
    public void MergeUserGpuPreferenceValue_PreservesExistingWindowedOptimizationTokens()
    {
        // protects: UX-04 preservation of existing Windows UserGpuPreferences flags when setting GpuPreference=2
        // fails_when: Writing GpuPreference=2 overwrites SwapEffectUpgradeEnable or AutoHDREnable
        // seam: LaunchConfigWriterService.MergeUserGpuPreferenceValue
        string merged = LaunchConfigWriterService.MergeUserGpuPreferenceValue(
            "SwapEffectUpgradeEnable=1;AutoHDREnable=1;",
            "GpuPreference",
            "2");

        Assert.Contains("SwapEffectUpgradeEnable=1", merged);
        Assert.Contains("AutoHDREnable=1", merged);
        Assert.Contains("GpuPreference=2", merged);
    }

    [Fact]
    public void EvaluateDisplayVrrReadiness_ReportsHonestVerdictsAcrossAllScenarios()
    {
        // protects: UX-04 honest Phase-1 VRR readiness verdicts (no false greens, catches desktop Hz mismatch & VRR bypasses)
        // fails_when: Desktop stuck at 60 Hz isn't flagged, Windowed/Legacy isn't flagged, or fixed-refresh gets a false warning
        // seam: DisplayVrrDetectionService.EvaluateDisplayVrrReadiness
        EdidVrrCapability vrr48To200 = new(true, 48, 200, "EDID Adaptive-Sync");
        WindowsVrrOsState healthyWin11 = new(true, null, null, true);

        // 1. Healthy 200 Hz VRR monitor + DirectFlip Borderless/Exclusive -> Green "VRR-Ready" (never false "G-SYNC Active")
        DisplayVrrReadinessReport ready = DisplayVrrDetectionService.EvaluateDisplayVrrReadiness(
            CreateMonitor(currentHz: 200, maxHz: 200),
            vrr48To200,
            healthyWin11,
            selectedDisplayMode: "Exclusive Fullscreen",
            selectedPresentationMode: "DirectFlip (FLIP_DISCARD)",
            enableDirectFlip: true,
            activeDriver: "vid_vulkan");
        Assert.Equal(DisplayVrrReadinessLevel.VrrReady, ready.Level);
        Assert.Contains("VRR-Ready (48–200 Hz", ready.PillText);
        Assert.DoesNotContain("G-SYNC Active", ready.PillText);
        Assert.Equal(189, ready.RecommendedVrrCapFps);

        // 2. Windows desktop stuck at 60 Hz on a 200 Hz panel -> Amber Advisory
        DisplayVrrReadinessReport hzMismatch = DisplayVrrDetectionService.EvaluateDisplayVrrReadiness(
            CreateMonitor(currentHz: 60, maxHz: 200),
            vrr48To200,
            healthyWin11,
            selectedDisplayMode: "Borderless Windowed",
            selectedPresentationMode: "DirectFlip (FLIP_DISCARD)",
            enableDirectFlip: true,
            activeDriver: "vid_vulkan");
        Assert.Equal(DisplayVrrReadinessLevel.Advisory, hzMismatch.Level);
        Assert.Contains("60 Hz", hzMismatch.PillText);
        Assert.Contains("200 Hz", hzMismatch.PillText);

        // 3. Plain Windowed mode on a VRR monitor -> Amber Advisory
        DisplayVrrReadinessReport windowedBypass = DisplayVrrDetectionService.EvaluateDisplayVrrReadiness(
            CreateMonitor(currentHz: 200, maxHz: 200),
            vrr48To200,
            healthyWin11,
            selectedDisplayMode: "Windowed",
            selectedPresentationMode: "DirectFlip (FLIP_DISCARD)",
            enableDirectFlip: true,
            activeDriver: "vid_vulkan");
        Assert.Equal(DisplayVrrReadinessLevel.Advisory, windowedBypass.Level);
        Assert.Contains("Windowed Mode bypasses VRR", windowedBypass.PillText);

        // 4. Windows VRR explicitly disabled (VRROptimizeEnable=0) -> Amber Advisory
        DisplayVrrReadinessReport osDisabled = DisplayVrrDetectionService.EvaluateDisplayVrrReadiness(
            CreateMonitor(currentHz: 200, maxHz: 200),
            vrr48To200,
            new WindowsVrrOsState(true, WindowsVrrOptimizeEnabled: false, SwapEffectUpgradeEnabled: true, true),
            selectedDisplayMode: "Borderless Windowed",
            selectedPresentationMode: "DirectFlip (FLIP_DISCARD)",
            enableDirectFlip: true,
            activeDriver: "vid_vulkan");
        Assert.Equal(DisplayVrrReadinessLevel.Advisory, osDisabled.Level);
        Assert.Contains("disabled", osDisabled.PillText);

        // 5. Fixed 60 Hz non-VRR monitor -> Neutral FixedRefresh (zero false warnings)
        DisplayVrrReadinessReport fixed60 = DisplayVrrDetectionService.EvaluateDisplayVrrReadiness(
            CreateMonitor(currentHz: 60, maxHz: 60, friendlyName: "DELL P2719H"),
            new EdidVrrCapability(false, 56, 76, "EDID Fixed Range"),
            healthyWin11,
            selectedDisplayMode: "Borderless Windowed",
            selectedPresentationMode: "DirectFlip (FLIP_DISCARD)",
            enableDirectFlip: true,
            activeDriver: "vid_vulkan");
        Assert.Equal(DisplayVrrReadinessLevel.FixedRefresh, fixed60.Level);
        Assert.Contains("Fixed-Refresh Display (60 Hz", fixed60.PillText);
        Assert.Equal(0, fixed60.RecommendedVrrCapFps);
    }

    [Fact]
    public void MainViewModel_ApplyVrrLowLatencyCap_AppliesSubRefreshOverrideInSingleWrite()
    {
        // protects: UX-04 1-click VRR sub-refresh cap helper inside Custom Cap drawer
        // fails_when: ApplyVrrLowLatencyCap fails to set manual cap or fires multiple writes
        // seam: MainViewModel.ApplyVrrLowLatencyCapCommand
        MainViewModel vm = new()
        {
            SkipProcessConflictScanForTesting = true,
            RecommendedVrrCapFps = 189
        };

        int writesBefore = vm.ConfigWriteInvocationCountForTesting;
        vm.ApplyVrrLowLatencyCap();

        Assert.True(vm.ManualRefreshRateOverrideEnabled);
        Assert.Equal("189", vm.ManualMaxFpsText);
        Assert.Equal(1, vm.ConfigWriteInvocationCountForTesting - writesBefore);
        Assert.Contains("189 FPS", vm.LastPresetChangeSummary);
    }

    [Fact]
    public void LiveHostDisplayProbe_ExecutesSafelyAndProducesHonestReadinessReport()
    {
        // protects: UX-04 real OS COM/DXGI/CCD/EDID probe safety on live Windows/Linux/macOS workstations
        // fails_when: CreateDXGIFactory1 / QueryDisplayConfig / EDID registry lookup throws or produces empty status
        // seam: DisplayVrrDetectionService.InspectDisplayReadiness + QueryDxgiAllowTearingSupported
        IReadOnlyList<MonitorDisplayInfo> monitors = HardwareDetectionService.DetectAvailableMonitors(forceRefresh: true);
        MonitorDisplayInfo? primary = monitors.Count > 0 ? monitors[0] : null;
        WindowsVrrOsState osState = DisplayVrrDetectionService.QueryWindowsVrrOsState();
        EdidVrrCapability edid = DisplayVrrDetectionService.QueryMonitorVrrCapability(primary);
        DisplayVrrReadinessReport report = DisplayVrrDetectionService.InspectDisplayReadiness(
            primary,
            selectedDisplayMode: "Exclusive Fullscreen",
            selectedPresentationMode: "DirectFlip (FLIP_DISCARD)",
            enableDirectFlip: true,
            activeDriver: "vid_vulkan");

        output.WriteLine($"Monitor: {primary?.DisplayLabel ?? "None"}");
        output.WriteLine($"EDID: IsVrrCapable={edid.IsVrrCapable}, Range={edid.MinHz}-{edid.MaxHz}Hz, Source={edid.Source}");
        output.WriteLine($"OS: DxgiAllowTearing={osState.DxgiAllowTearingSupported}, VrrOpt={osState.WindowsVrrOptimizeEnabled}, SwapUpgrade={osState.SwapEffectUpgradeEnabled}, Win11Default={osState.IsWindows11DefaultFlipUpgrade}");
        output.WriteLine($"Report: Level={report.Level}, Pill='{report.PillText}', RecVrrCap={report.RecommendedVrrCapFps}");

        Assert.False(string.IsNullOrWhiteSpace(report.PillText));
        Assert.False(string.IsNullOrWhiteSpace(report.DetailTooltip));
        Assert.True(report.PanelMaxHz >= 30);
    }
}
