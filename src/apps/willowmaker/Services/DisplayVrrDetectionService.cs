// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ============================================================================
// File: DisplayVrrDetectionService.cs
// System: WILLOWMAKER - Native C++ Modernization & Engine Hardening
// Subsystem: Phase-1 Vendor-Neutral Display & VRR Readiness Detection
// ============================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace WILLOWMAKER.Core.Services;

public enum DisplayVrrReadinessLevel
{
    VrrReady,
    Advisory,
    FixedRefresh
}

internal sealed record EdidVrrCapability(
    bool IsVrrCapable,
    int MinHz,
    int MaxHz,
    string Source);

internal sealed record WindowsVrrOsState(
    bool DxgiAllowTearingSupported,
    bool? WindowsVrrOptimizeEnabled,
    bool? SwapEffectUpgradeEnabled,
    bool IsWindows11DefaultFlipUpgrade);

public sealed record DisplayVrrReadinessReport(
    DisplayVrrReadinessLevel Level,
    bool IsMonitorVrrCapable,
    int VrrMinHz,
    int VrrMaxHz,
    int ActiveDesktopHz,
    int PanelMaxHz,
    int RecommendedVrrCapFps,
    string PillText,
    string DetailTooltip,
    string BadgeBackground,
    string BadgeBorder,
    string BadgeForeground);

/// <summary>
///     Phase-1 vendor-neutral display and Variable Refresh Rate (G-SYNC / FreeSync / Adaptive-Sync) readiness inspector.
///     Uses only documented OS/EDID/DXGI/DRM APIs (zero in-process vendor driver DLL loading) to detect:
///     (1) Monitor EDID VRR range (0xFD Monitor Range Limits + CTA-861 HDMI 2.1 Forum / FreeSync VSDBs),
///     (2) Active desktop Hz vs. panel max Hz mismatches (e.g. Windows stuck at 60 Hz on a 200 Hz display),
///     (3) DXGI_FEATURE_PRESENT_ALLOW_TEARING and Windows UserGpuPreferences VRR/flip settings, and
///     (4) Effective resolved display mode compatibility under vid_vulkan.
/// </summary>
public static class DisplayVrrDetectionService
{
    private static readonly Guid IidDxgiFactory5 = new("7632e1f5-ee65-4dca-87fd-84cd75f8838d");
    private const uint DxgiFeaturePresentAllowTearing = 0;

    [DllImport("dxgi.dll", ExactSpelling = true)]
    private static extern int CreateDXGIFactory1(ref Guid riid, out IntPtr ppFactory);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate uint ReleaseDelegate(IntPtr instance);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int CheckFeatureSupportDelegate(IntPtr instance, uint feature, out int featureSupportData, uint featureSupportDataSize);

    private static readonly Lock OsStateCacheLock = new();
    private static bool? s_cachedDxgiAllowTearing;

    /// <summary>
    ///     Calculates the sub-refresh low-latency VRR frame cap (<c>round(Hz - Hz^2 / 3600)</c>, clamped to <c>[57, Hz - 3]</c>)
    ///     to keep frame delivery comfortably inside the monitor's G-SYNC / FreeSync window (e.g., 200 Hz -> 189 FPS, 144 Hz -> 138 FPS).
    /// </summary>
    public static int CalculateRecommendedVrrCapFps(int refreshRateHz)
    {
        if (refreshRateHz < 60)
        {
            return 0;
        }

        int clampedHz = Math.Clamp(refreshRateHz, 60, 500);
        int rawCap = (int)Math.Round(clampedHz - (clampedHz * (double)clampedHz / 3600.0));
        return Math.Clamp(rawCap, 57, clampedHz - 3);
    }

    /// <summary>
    ///     Parses standard VESA E-EDID 1.4 Monitor Range Limits (<c>0xFD</c>) and CTA-861 Extension Blocks (<c>0x02</c>)
    ///     for HDMI 2.1 Forum VRR (<c>IEEE 0xC45DD8</c>) and AMD FreeSync (<c>IEEE 0x00001A</c>) capabilities.
    /// </summary>
    internal static EdidVrrCapability ParseEdidVrrCapability(ReadOnlySpan<byte> edid)
    {
        if (edid.Length < 128)
        {
            return new EdidVrrCapability(false, 0, 0, "EDID Unavailable");
        }

        // Validate 8-byte EDID magic header (00 FF FF FF FF FF FF 00)
        if (edid[0] != 0x00 || edid[1] != 0xFF || edid[2] != 0xFF || edid[3] != 0xFF ||
            edid[4] != 0xFF || edid[5] != 0xFF || edid[6] != 0xFF || edid[7] != 0x00)
        {
            return new EdidVrrCapability(false, 0, 0, "Invalid EDID Header");
        }

        // 1. Inspect CTA-861 extension blocks first for explicit HDMI 2.1 VRR or FreeSync VSDBs
        int numBlocks = edid.Length / 128;
        for (int b = 1; b < numBlocks; b++)
        {
            ReadOnlySpan<byte> ext = edid.Slice(b * 128, 128);
            if (ext[0] != 0x02) // CTA-861 Extension tag
            {
                continue;
            }

            int dtdOffset = ext[2];
            if (dtdOffset <= 4 || dtdOffset > 127)
            {
                continue;
            }

            int pos = 4;
            while (pos < dtdOffset)
            {
                byte header = ext[pos];
                int tagCode = (header >> 5) & 0x07;
                int payloadLen = header & 0x1F;
                pos++;

                if (pos + payloadLen > dtdOffset)
                {
                    break;
                }

                if (tagCode == 3 && payloadLen >= 3) // Vendor-Specific Data Block
                {
                    int oui = ext[pos] | (ext[pos + 1] << 8) | (ext[pos + 2] << 16);

                    // HDMI Forum VSDB (IEEE OUI 0xC45DD8 -> bytes D8 5D C4)
                    if (oui == 0xC45DD8 && payloadLen >= 10)
                    {
                        int vrrMin = ext[pos + 8] & 0x3F;
                        int vrrMaxHigh = (ext[pos + 8] >> 6) & 0x03;
                        int vrrMax = ext[pos + 9] | (vrrMaxHigh << 8);
                        if (vrrMin >= 20 && vrrMax > vrrMin && vrrMax <= 1000)
                        {
                            return new EdidVrrCapability(true, vrrMin, vrrMax, "HDMI 2.1 VRR");
                        }
                    }

                    // AMD FreeSync VSDB (IEEE OUI 0x00001A -> bytes 1A 00 00)
                    if (oui == 0x00001A && payloadLen >= 7)
                    {
                        int minV = ext[pos + 5];
                        int maxV = ext[pos + 6];
                        if (minV >= 20 && maxV > minV && maxV <= 500)
                        {
                            return new EdidVrrCapability(true, minV, maxV, "FreeSync VSDB");
                        }
                    }
                }

                pos += payloadLen;
            }
        }

        // 2. Inspect 4 base 18-byte EDID descriptor slots (offsets 54, 72, 90, 108) for 0xFD Monitor Range Limits
        for (int off = 54; off <= 108; off += 18)
        {
            if (edid[off] == 0x00 && edid[off + 1] == 0x00 && edid[off + 2] == 0x00 && edid[off + 3] == 0xFD)
            {
                byte rangeOffsets = edid[off + 4];
                int minV = edid[off + 5] + ((rangeOffsets & 0x01) != 0 ? 255 : 0);
                int maxV = edid[off + 6] + ((rangeOffsets & 0x02) != 0 ? 255 : 0);

                if (minV >= 20 && maxV > minV && maxV <= 1000)
                {
                    bool isAdaptiveSyncRange = maxV >= 75 && minV <= 60 && (maxV - minV) >= 24;
                    return new EdidVrrCapability(
                        isAdaptiveSyncRange,
                        minV,
                        maxV,
                        isAdaptiveSyncRange ? "EDID Adaptive-Sync" : "EDID Fixed Range");
                }
            }
        }

        return new EdidVrrCapability(false, 0, 0, "EDID Range Not Reported");
    }

    /// <summary>
    ///     Extracts registry subpath <c>DISPLAY\&lt;ModelId&gt;\&lt;InstanceId&gt;</c> from a Windows
    ///     <c>DISPLAYCONFIG_TARGET_DEVICE_NAME.monitorDevicePath</c> string
    ///     (e.g. <c>\\?\DISPLAY#AUS2718#5&amp;382a3a70&amp;0&amp;UID4353#{e6f07b5f-...}</c>).
    /// </summary>
    public static bool TryParseMonitorRegistrySubPath(string? monitorDevicePath, out string registrySubPath)
    {
        registrySubPath = string.Empty;
        if (string.IsNullOrWhiteSpace(monitorDevicePath))
        {
            return false;
        }

        string trimmed = monitorDevicePath.Trim();
        if (trimmed.StartsWith(@"\\?\", StringComparison.Ordinal))
        {
            trimmed = trimmed[4..];
        }

        string[] parts = trimmed.Split('#', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 3 || !parts[0].Equals("DISPLAY", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        registrySubPath = $@"SYSTEM\CurrentControlSet\Enum\DISPLAY\{parts[1]}\{parts[2]}\Device Parameters";
        return true;
    }

    /// <summary>
    ///     Parses <c>DirectXUserGlobalSettings</c> key-value pairs (<c>VRROptimizeEnable</c>, <c>SwapEffectUpgradeEnable</c>).
    /// </summary>
    public static (bool? VrrOptimizeEnabled, bool? SwapEffectUpgradeEnabled) ParseDirectXUserGlobalSettings(string? rawSettings)
    {
        if (string.IsNullOrWhiteSpace(rawSettings))
        {
            return (null, null);
        }

        bool? vrrOpt = null;
        bool? swapUpgrade = null;
        foreach (string rawToken in rawSettings.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            int eq = rawToken.IndexOf('=');
            if (eq <= 0 || eq >= rawToken.Length - 1)
                continue;

            string key = rawToken[..eq].Trim();
            string val = rawToken[(eq + 1)..].Trim();
            if (key.Equals("VRROptimizeEnable", StringComparison.OrdinalIgnoreCase))
            {
                if (val == "1") vrrOpt = true;
                else if (val == "0") vrrOpt = false;
            }
            else if (key.Equals("SwapEffectUpgradeEnable", StringComparison.OrdinalIgnoreCase))
            {
                if (val == "1") swapUpgrade = true;
                else if (val == "0") swapUpgrade = false;
            }
        }

        return (vrrOpt, swapUpgrade);
    }

    public static bool QueryDxgiAllowTearingSupported()
    {
        if (!OperatingSystem.IsWindows())
        {
            return true;
        }

        lock (OsStateCacheLock)
        {
            if (s_cachedDxgiAllowTearing.HasValue)
            {
                return s_cachedDxgiAllowTearing.Value;
            }

            bool supported = false;
            IntPtr factoryPtr = IntPtr.Zero;
            try
            {
                Guid iid = IidDxgiFactory5;
                int hr = CreateDXGIFactory1(ref iid, out factoryPtr);
                if (hr >= 0 && factoryPtr != IntPtr.Zero)
                {
                    IntPtr vtable = Marshal.ReadIntPtr(factoryPtr);
                    IntPtr checkFnPtr = Marshal.ReadIntPtr(vtable, 28 * IntPtr.Size);
                    CheckFeatureSupportDelegate checkFeature = Marshal.GetDelegateForFunctionPointer<CheckFeatureSupportDelegate>(checkFnPtr);
                    if (checkFeature(factoryPtr, DxgiFeaturePresentAllowTearing, out int allowTearing, sizeof(int)) >= 0)
                    {
                        supported = allowTearing != 0;
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.WriteLine($"[WILLOWMAKER][VRR] DXGI CheckFeatureSupport probe notice: {ex.Message}");
            }
            finally
            {
                if (factoryPtr != IntPtr.Zero)
                {
                    try
                    {
                        IntPtr vtable = Marshal.ReadIntPtr(factoryPtr);
                        IntPtr releaseFnPtr = Marshal.ReadIntPtr(vtable, 2 * IntPtr.Size);
                        ReleaseDelegate release = Marshal.GetDelegateForFunctionPointer<ReleaseDelegate>(releaseFnPtr);
                        release(factoryPtr);
                    }
                    catch
                    {
                        // Best-effort COM release
                    }
                }
            }

            s_cachedDxgiAllowTearing = supported;
            return supported;
        }
    }

    internal static WindowsVrrOsState QueryWindowsVrrOsState()
    {
        if (!OperatingSystem.IsWindows())
        {
            return new WindowsVrrOsState(true, null, null, false);
        }

        bool allowTearing = QueryDxgiAllowTearingSupported();
        bool isWin11DefaultFlip = Environment.OSVersion.Version.Build >= 22621;
        bool? vrrOpt = null;
        bool? swapUpgrade = null;

        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\DirectX\UserGpuPreferences");
            string? globalSettings = key?.GetValue("DirectXUserGlobalSettings") as string;
            (vrrOpt, swapUpgrade) = ParseDirectXUserGlobalSettings(globalSettings);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine($"[WILLOWMAKER][VRR] UserGpuPreferences read notice: {ex.Message}");
        }

        return new WindowsVrrOsState(allowTearing, vrrOpt, swapUpgrade, isWin11DefaultFlip);
    }

    internal static EdidVrrCapability QueryMonitorVrrCapability(MonitorDisplayInfo? monitor)
    {
        if (monitor is null)
        {
            return new EdidVrrCapability(false, 0, 0, "No Monitor Selected");
        }

        if (OperatingSystem.IsWindows())
        {
            try
            {
                Dictionary<string, string> devicePaths = HardwareDetectionService.QueryMonitorDevicePathsByGdiDevice();
                if (devicePaths.TryGetValue(monitor.NormalizedDeviceToken, out string? devPath) &&
                    TryParseMonitorRegistrySubPath(devPath, out string regSubPath))
                {
                    using RegistryKey? devParamKey = Registry.LocalMachine.OpenSubKey(regSubPath);
                    if (devParamKey?.GetValue("EDID") is byte[] edidBytes && edidBytes.Length >= 128)
                    {
                        return ParseEdidVrrCapability(edidBytes);
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.WriteLine($"[WILLOWMAKER][VRR] Windows EDID probe notice for '{monitor.DeviceName}': {ex.Message}");
            }

            return new EdidVrrCapability(false, 0, monitor.MaxRefreshRateHz, "EDID Unavailable");
        }

        if (OperatingSystem.IsLinux())
        {
            return QueryLinuxDrmVrrCapability(monitor);
        }

        if (OperatingSystem.IsMacOS() && monitor.MaxRefreshRateHz >= 120)
        {
            return new EdidVrrCapability(true, 24, monitor.MaxRefreshRateHz, "macOS ProMotion");
        }

        return new EdidVrrCapability(false, 0, monitor.MaxRefreshRateHz, "Fixed Refresh");
    }

    private static EdidVrrCapability QueryLinuxDrmVrrCapability(MonitorDisplayInfo monitor)
    {
        try
        {
            const string drmRoot = "/sys/class/drm";
            if (Directory.Exists(drmRoot))
            {
                foreach (string connectorDir in Directory.EnumerateDirectories(drmRoot, "card*-*"))
                {
                    string statusPath = Path.Combine(connectorDir, "status");
                    if (!File.Exists(statusPath) ||
                        !File.ReadAllText(statusPath).Trim().Equals("connected", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    string vrrPath = Path.Combine(connectorDir, "vrr_capable");
                    bool drmVrrCapable = File.Exists(vrrPath) && File.ReadAllText(vrrPath).Trim() == "1";

                    string edidPath = Path.Combine(connectorDir, "edid");
                    if (File.Exists(edidPath))
                    {
                        byte[] edidBytes = File.ReadAllBytes(edidPath);
                        EdidVrrCapability parsed = ParseEdidVrrCapability(edidBytes);
                        if (parsed.IsVrrCapable || drmVrrCapable)
                        {
                            return new EdidVrrCapability(
                                true,
                                parsed.MinHz > 0 ? parsed.MinHz : 48,
                                parsed.MaxHz > 0 ? parsed.MaxHz : monitor.MaxRefreshRateHz,
                                drmVrrCapable ? "Linux DRM VRR" : parsed.Source);
                        }
                    }
                    else if (drmVrrCapable)
                    {
                        return new EdidVrrCapability(true, 48, monitor.MaxRefreshRateHz, "Linux DRM VRR");
                    }
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine($"[WILLOWMAKER][VRR] Linux DRM probe notice: {ex.Message}");
        }

        return new EdidVrrCapability(false, 0, monitor.MaxRefreshRateHz, "Linux Fixed Refresh");
    }

    /// <summary>
    ///     Runs full Phase-1 display &amp; VRR readiness detection for the active monitor and launcher display configuration.
    /// </summary>
    public static DisplayVrrReadinessReport InspectDisplayReadiness(
        MonitorDisplayInfo? monitor,
        string selectedDisplayMode,
        string selectedPresentationMode,
        bool enableDirectFlip,
        string activeDriver)
    {
        EdidVrrCapability edidVrr = QueryMonitorVrrCapability(monitor);
        WindowsVrrOsState osState = QueryWindowsVrrOsState();
        return EvaluateDisplayVrrReadiness(
            monitor,
            edidVrr,
            osState,
            selectedDisplayMode,
            selectedPresentationMode,
            enableDirectFlip,
            activeDriver);
    }

    /// <summary>
    ///     Pure deterministic evaluator for display &amp; VRR readiness.
    ///     Never reports a false green for vendor driver G-SYNC/FreeSync state that Phase 1 does not inspect;
    ///     uses honest <c>"VRR-Ready"</c> wording when EDID + OS DXGI tearing + desktop Hz + DirectFlip all align,
    ///     and amber advisories strictly for proven configuration issues.
    /// </summary>
    internal static DisplayVrrReadinessReport EvaluateDisplayVrrReadiness(
        MonitorDisplayInfo? monitor,
        EdidVrrCapability edidVrr,
        WindowsVrrOsState osState,
        string selectedDisplayMode,
        string selectedPresentationMode,
        bool enableDirectFlip,
        string activeDriver)
    {
        int activeDesktopHz = monitor?.CurrentRefreshRateHz ?? 0;
        // Compare desktop Hz against the OS-enumerated mode max Hz (not raw 0xFD legacy scan ceiling on fixed 60 Hz panels)
        int panelMaxHz = monitor is { MaxRefreshRateHz: > 0 }
            ? monitor.MaxRefreshRateHz
            : (edidVrr.IsVrrCapable && edidVrr.MaxHz > 0 ? edidVrr.MaxHz : (activeDesktopHz > 0 ? activeDesktopHz : 60));

        int recommendedVrrCap = edidVrr.IsVrrCapable ? CalculateRecommendedVrrCapFps(panelMaxHz) : 0;

        (string fullscreenCvar, _, _) = LaunchConfigWriterService.ResolveDisplayModeCvars(selectedDisplayMode, activeDriver);
        bool isPlainWindowed = string.Equals(fullscreenCvar, "false", StringComparison.OrdinalIgnoreCase);
        bool isLegacyBlit = !enableDirectFlip ||
                            selectedPresentationMode.Contains("Legacy", StringComparison.OrdinalIgnoreCase);

        // Priority 1 (Proven #1 Player Misconfiguration): Windows Desktop Hz is significantly below panel's native max Hz
        if (activeDesktopHz > 0 && panelMaxHz > activeDesktopHz + 5)
        {
            string vrrRangeNote = edidVrr.IsVrrCapable ? $" (VRR {edidVrr.MinHz}–{edidVrr.MaxHz} Hz)" : string.Empty;
            return new DisplayVrrReadinessReport(
                Level: DisplayVrrReadinessLevel.Advisory,
                IsMonitorVrrCapable: edidVrr.IsVrrCapable,
                VrrMinHz: edidVrr.MinHz,
                VrrMaxHz: edidVrr.MaxHz,
                ActiveDesktopHz: activeDesktopHz,
                PanelMaxHz: panelMaxHz,
                RecommendedVrrCapFps: recommendedVrrCap,
                PillText: $"⚠ Windows Display at {activeDesktopHz} Hz (Monitor supports {panelMaxHz} Hz)",
                DetailTooltip: $"Your monitor supports up to {panelMaxHz} Hz{vrrRangeNote}, but Windows is currently driving it at {activeDesktopHz} Hz.\n" +
                               $"Open Windows Settings → System → Display → Advanced display and set refresh rate to {panelMaxHz} Hz for smooth high-Hz gameplay.",
                BadgeBackground: "#2A1F0A",
                BadgeBorder: "#D97706",
                BadgeForeground: "#FBBF24");
        }

        // Priority 2 (Proven VRR Bypass): VRR monitor with Windowed mode or Legacy Blit selected
        if (edidVrr.IsVrrCapable && (isPlainWindowed || isLegacyBlit))
        {
            string blocker = isLegacyBlit ? "Legacy Blit" : "Windowed Mode";
            return new DisplayVrrReadinessReport(
                Level: DisplayVrrReadinessLevel.Advisory,
                IsMonitorVrrCapable: true,
                VrrMinHz: edidVrr.MinHz,
                VrrMaxHz: edidVrr.MaxHz,
                ActiveDesktopHz: activeDesktopHz,
                PanelMaxHz: panelMaxHz,
                RecommendedVrrCapFps: recommendedVrrCap,
                PillText: $"⚠ {blocker} bypasses VRR ({edidVrr.MinHz}–{edidVrr.MaxHz} Hz)",
                DetailTooltip: $"Your display supports {edidVrr.Source} ({edidVrr.MinHz}–{edidVrr.MaxHz} Hz), but {blocker} prevents Hardware Independent Flip.\n" +
                               $"Use Borderless Windowed or Exclusive Fullscreen with DirectFlip (FLIP_DISCARD) so G-SYNC / FreeSync can engage cleanly.",
                BadgeBackground: "#2A1F0A",
                BadgeBorder: "#D97706",
                BadgeForeground: "#FBBF24");
        }

        // Priority 3 (Proven OS Setting Disabled): Windows VRR or Windowed Optimizations explicitly set to 0
        if (edidVrr.IsVrrCapable &&
            (osState.WindowsVrrOptimizeEnabled == false || osState.SwapEffectUpgradeEnabled == false))
        {
            string disabledName = osState.WindowsVrrOptimizeEnabled == false
                ? "Windows Variable Refresh Rate"
                : "Windows Optimizations for Windowed Games";
            return new DisplayVrrReadinessReport(
                Level: DisplayVrrReadinessLevel.Advisory,
                IsMonitorVrrCapable: true,
                VrrMinHz: edidVrr.MinHz,
                VrrMaxHz: edidVrr.MaxHz,
                ActiveDesktopHz: activeDesktopHz,
                PanelMaxHz: panelMaxHz,
                RecommendedVrrCapFps: recommendedVrrCap,
                PillText: $"⚠ {disabledName} disabled ({edidVrr.MinHz}–{edidVrr.MaxHz} Hz)",
                DetailTooltip: $"Your monitor supports {edidVrr.Source} ({edidVrr.MinHz}–{edidVrr.MaxHz} Hz), but {disabledName} is explicitly turned off in Windows Graphics Settings.\n" +
                               $"Enable it under Windows Settings → System → Display → Graphics → Default graphics settings.",
                BadgeBackground: "#2A1F0A",
                BadgeBorder: "#D97706",
                BadgeForeground: "#FBBF24");
        }

        // Priority 4 (Honest Green): Monitor EDID VRR + DXGI AllowTearing + Desktop Hz matches + DirectFlip/Mailbox active
        if (edidVrr.IsVrrCapable && osState.DxgiAllowTearingSupported)
        {
            string presentShort = selectedPresentationMode.Contains("Mailbox", StringComparison.OrdinalIgnoreCase)
                ? "Mailbox"
                : "DirectFlip";
            string capTip = recommendedVrrCap > 0
                ? $"\n• Optional VRR Low-Latency Cap: {recommendedVrrCap} FPS (available under ⚙ Custom Cap to keep pacing strictly inside the VRR window)."
                : string.Empty;
            return new DisplayVrrReadinessReport(
                Level: DisplayVrrReadinessLevel.VrrReady,
                IsMonitorVrrCapable: true,
                VrrMinHz: edidVrr.MinHz,
                VrrMaxHz: edidVrr.MaxHz,
                ActiveDesktopHz: activeDesktopHz,
                PanelMaxHz: panelMaxHz,
                RecommendedVrrCapFps: recommendedVrrCap,
                PillText: $"✓ VRR-Ready ({edidVrr.MinHz}–{edidVrr.MaxHz} Hz • {presentShort})",
                DetailTooltip: $"Monitor reports {edidVrr.Source} range {edidVrr.MinHz}–{edidVrr.MaxHz} Hz, Windows desktop is running at {panelMaxHz} Hz, and DXGI Independent Flip tearing is supported.\n" +
                               $"• Confirm G-SYNC / FreeSync is enabled in your NVIDIA/AMD Control Panel if you haven't already.{capTip}",
                BadgeBackground: "#07261E",
                BadgeBorder: "#059669",
                BadgeForeground: "#34D399");
        }

        // Priority 5 (Neutral): Fixed-Refresh Panel or EDID VRR not advertised
        string fixedPresent = isLegacyBlit ? "Legacy VSync" : "DirectFlip Active";
        return new DisplayVrrReadinessReport(
            Level: DisplayVrrReadinessLevel.FixedRefresh,
            IsMonitorVrrCapable: false,
            VrrMinHz: 0,
            VrrMaxHz: panelMaxHz,
            ActiveDesktopHz: activeDesktopHz,
            PanelMaxHz: panelMaxHz,
            RecommendedVrrCapFps: 0,
            PillText: $"Fixed-Refresh Display ({panelMaxHz} Hz • {fixedPresent})",
            DetailTooltip: $"No Adaptive-Sync / VRR range was advertised by this monitor's EDID ({edidVrr.Source}). DirectFlip / Mailbox pacing is active for {panelMaxHz} Hz.",
            BadgeBackground: "#101726",
            BadgeBorder: "#26324A",
            BadgeForeground: "#94A3B8");
    }
}
