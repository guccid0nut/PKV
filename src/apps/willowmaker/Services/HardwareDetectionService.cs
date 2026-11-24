// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ============================================================================
// File: HardwareDetectionService.cs
// System: WILLOWMAKER - Native C++ Modernization & Engine Hardening
// Subsystem: Hardware Auto-Detection & Graphics Recommendation Engine
// ============================================================================

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace WILLOWMAKER.Core.Services;

public enum HardwareTier
{
    EntryIntegrated,
    CompetitiveMidRange,
    HighPerformanceRtx,
    EnthusiastRtx
}

public sealed record MonitorResolutionOption(
    int Width,
    int Height,
    int MaxRefreshRateHz,
    bool IsNative,
    string DisplayLabel,
    IReadOnlyList<int>? SupportedRefreshRatesHz = null
);

public sealed record MonitorDisplayInfo(
    int Index,
    string DeviceName,
    string FriendlyName,
    bool IsPrimary,
    int BoundsX,
    int BoundsY,
    int NativeWidth,
    int NativeHeight,
    int CurrentRefreshRateHz,
    int MaxRefreshRateHz,
    IReadOnlyList<MonitorResolutionOption> SupportedResolutions
)
{
    public string NormalizedDeviceToken => HardwareDetectionService.NormalizeMonitorDeviceToken(DeviceName);

    public string DisplayLabel =>
        $"{Index}: {FriendlyName}{(IsPrimary ? " (Primary)" : "")} — {NativeWidth}x{NativeHeight} @ {MaxRefreshRateHz} Hz";
}

public sealed record HardwareProfile(
    string GpuName,
    long VramBytes,
    long SystemRamBytes,
    int RefreshRateHz,
    HardwareTier Tier,
    string Summary
);

public sealed record HardwareRecommendation(
    bool EnableRayTracing,
    string RayTracingPreset,
    string ShadowFidelity,
    string RefreshRate,
    bool EnableTextureClarityBoost,
    bool EnableNvidiaReflex,
    bool EnableDirectFlip,
    string PresentationMode,
    string SwapchainBackend,
    string SpatialChannels,
    string TierLabel,
    string Rationale
);

/// <summary>
///     High-speed, zero-dependency hardware detection and recommendation service.
///     Inspects GPU model, dedicated VRAM, total physical system RAM, and display refresh rate
///     to deterministically tailor graphics presets for optimal FPS and visual fidelity.
/// </summary>
public static class HardwareDetectionService
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private sealed class MEMORYSTATUSEX
    {
        public uint dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>();
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx([In, Out] MEMORYSTATUSEX lpBuffer);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DEVMODE
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string dmDeviceName;
        public short dmSpecVersion;
        public short dmDriverVersion;
        public short dmSize;
        public short dmDriverExtra;
        public int dmFields;
        public int dmPositionX;
        public int dmPositionY;
        public int dmDisplayOrientation;
        public int dmDisplayFixedOutput;
        public short dmColor;
        public short dmDuplex;
        public short dmYResolution;
        public short dmTTOption;
        public short dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string dmFormName;
        public short dmLogPixels;
        public short dmBitsPerPel;
        public int dmPelsWidth;
        public int dmPelsHeight;
        public int dmDisplayFlags;
        public int dmDisplayFrequency;
        public int dmICMMethod;
        public int dmICMIntent;
        public int dmMediaType;
        public int dmDitherType;
        public int dmReserved1;
        public int dmReserved2;
        public int dmPanningWidth;
        public int dmPanningHeight;
    }

    private const int ENUM_CURRENT_SETTINGS = -1;

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "EnumDisplaySettingsW")]
    private static extern bool EnumDisplaySettings([MarshalAs(UnmanagedType.LPWStr)] string? deviceName, int modeNum, ref DEVMODE devMode);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DISPLAY_DEVICEW
    {
        public int cb;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string DeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string DeviceString;
        public int StateFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string DeviceID;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string DeviceKey;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "EnumDisplayDevicesW")]
    private static extern bool EnumDisplayDevices([MarshalAs(UnmanagedType.LPWStr)] string? lpDevice, uint iDevNum, ref DISPLAY_DEVICEW lpDisplayDevice, uint dwFlags);

    private const uint QDC_ONLY_ACTIVE_PATHS = 0x00000002;
    private const uint DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME = 1;
    private const uint DISPLAYCONFIG_DEVICE_INFO_GET_TARGET_NAME = 2;

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct LUID
    {
        public uint LowPart;
        public int HighPart;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct DISPLAYCONFIG_RATIONAL
    {
        public uint Numerator;
        public uint Denominator;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct DISPLAYCONFIG_PATH_SOURCE_INFO
    {
        public LUID adapterId;
        public uint id;
        public uint modeInfoIdx;
        public uint statusFlags;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct DISPLAYCONFIG_PATH_TARGET_INFO
    {
        public LUID adapterId;
        public uint id;
        public uint modeInfoIdx;
        public uint outputTechnology;
        public uint rotation;
        public uint scaling;
        public DISPLAYCONFIG_RATIONAL refreshRate;
        public uint scanLineOrdering;
        public int targetAvailable;
        public uint statusFlags;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct DISPLAYCONFIG_PATH_INFO
    {
        public DISPLAYCONFIG_PATH_SOURCE_INFO sourceInfo;
        public DISPLAYCONFIG_PATH_TARGET_INFO targetInfo;
        public uint flags;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct DISPLAYCONFIG_MODE_INFO
    {
        public uint infoType;
        public uint id;
        public LUID adapterId;
        public ulong modeData0;
        public ulong modeData1;
        public ulong modeData2;
        public ulong modeData3;
        public ulong modeData4;
        public ulong modeData5;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct DISPLAYCONFIG_DEVICE_INFO_HEADER
    {
        public uint type;
        public uint size;
        public LUID adapterId;
        public uint id;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode, Pack = 4)]
    private struct DISPLAYCONFIG_SOURCE_DEVICE_NAME
    {
        public DISPLAYCONFIG_DEVICE_INFO_HEADER header;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string viewGdiDeviceName;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode, Pack = 4)]
    private struct DISPLAYCONFIG_TARGET_DEVICE_NAME
    {
        public DISPLAYCONFIG_DEVICE_INFO_HEADER header;
        public uint flags;
        public uint outputTechnology;
        public ushort edidManufactureId;
        public ushort edidProductCodeId;
        public uint connectorInstance;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string monitorFriendlyDeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string monitorDevicePath;
    }

    [DllImport("user32.dll")]
    private static extern int GetDisplayConfigBufferSizes(uint flags, out uint numPathArrayElements, out uint numModeInfoArrayElements);

    [DllImport("user32.dll")]
    private static extern int QueryDisplayConfig(
        uint flags,
        ref uint numPathArrayElements,
        [Out] DISPLAYCONFIG_PATH_INFO[] pathArray,
        ref uint numModeInfoArrayElements,
        [Out] DISPLAYCONFIG_MODE_INFO[] modeInfoArray,
        IntPtr currentTopologyId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int DisplayConfigGetDeviceInfo(ref DISPLAYCONFIG_SOURCE_DEVICE_NAME requestPacket);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int DisplayConfigGetDeviceInfo(ref DISPLAYCONFIG_TARGET_DEVICE_NAME requestPacket);

    /// <summary>
    ///     Detects the active hardware profile including discrete GPU, VRAM, system RAM, and display Hz.
    /// </summary>
    public static HardwareProfile DetectHardware()
    {
        (string gpuName, long vramBytes) = QueryPrimaryGpu();
        long systemRamBytes = QuerySystemRam();
        int refreshRateHz = QueryDisplayRefreshRate();

        HardwareTier tier = ClassifyTier(gpuName, vramBytes, systemRamBytes);
        double vramGb = vramBytes / (1024.0 * 1024.0 * 1024.0);
        double ramGb = systemRamBytes / (1024.0 * 1024.0 * 1024.0);

        string summary = $"{gpuName} ({(vramGb > 0 ? $"{vramGb:F1} GB VRAM" : "Shared Memory")}) | {ramGb:F0} GB RAM | {refreshRateHz} Hz";

        return new HardwareProfile(
            gpuName,
            vramBytes,
            systemRamBytes,
            refreshRateHz,
            tier,
            summary
        );
    }

    /// <summary>
    ///     Generates an optimal configuration recommendation tailored to the given hardware profile.
    /// </summary>
    public static HardwareRecommendation GetRecommendation(HardwareProfile profile)
    {
        string targetHz = ResolveClosestRefreshRateString(profile.RefreshRateHz);

        return profile.Tier switch
        {
            HardwareTier.EnthusiastRtx => new HardwareRecommendation(
                EnableRayTracing: true,
                RayTracingPreset: "Ultra (Path-Tracing)",
                ShadowFidelity: "High (2048)",
                RefreshRate: targetHz,
                EnableTextureClarityBoost: false,
                EnableNvidiaReflex: false,
                EnableDirectFlip: true,
                PresentationMode: "DirectFlip (FLIP_DISCARD)",
                SwapchainBackend: "DXVK (Vulkan WSI)",
                SpatialChannels: "256 Channels (Ultra / Polyphony)",
                TierLabel: "Enthusiast RTX (Full Path-Tracing + 2K Shadows)",
                Rationale: $"High-end GPU detected ({profile.GpuName} with {profile.VramBytes / (1024 * 1024 * 1024):F0} GB VRAM). Full RTX Path-Tracing enabled with balanced 2048 soft shadows for maximum visual fidelity."
            ),

            HardwareTier.HighPerformanceRtx => new HardwareRecommendation(
                EnableRayTracing: true,
                RayTracingPreset: "Balanced RTX (RTAO)",
                ShadowFidelity: "High (2048)",
                RefreshRate: targetHz,
                EnableTextureClarityBoost: false,
                EnableNvidiaReflex: false,
                EnableDirectFlip: true,
                PresentationMode: "DirectFlip (FLIP_DISCARD)",
                SwapchainBackend: "DXVK (Vulkan WSI)",
                SpatialChannels: "128 Channels (Studio Balanced)",
                TierLabel: "High-Performance RTX (Ray-Traced Ambient Occlusion + 2K Shadows)",
                Rationale: $"Modern RTX/Radeon GPU detected ({profile.GpuName}, {profile.VramBytes / (1024 * 1024 * 1024):F0} GB VRAM, {profile.SystemRamBytes / (1024 * 1024 * 1024):F0} GB RAM). Balanced RTX and 2048 soft shadows selected for crisp visuals and 300+ FPS."
            ),

            HardwareTier.CompetitiveMidRange => new HardwareRecommendation(
                EnableRayTracing: false,
                RayTracingPreset: "Competitive (0% RT)",
                ShadowFidelity: "Medium (1024)",
                RefreshRate: targetHz,
                EnableTextureClarityBoost: false,
                EnableNvidiaReflex: false,
                EnableDirectFlip: true,
                PresentationMode: "DirectFlip (FLIP_DISCARD)",
                SwapchainBackend: "DXVK (Vulkan WSI)",
                SpatialChannels: "128 Channels (Studio Balanced)",
                TierLabel: "Competitive Mid-Range (High FPS + 1024 Shadows)",
                Rationale: $"Mid-range GPU detected ({profile.GpuName}). Ray-tracing disabled and medium 1024 shadows configured to ensure maximum frame rate stability and fast scene loading."
            ),

            _ => new HardwareRecommendation(
                EnableRayTracing: false,
                RayTracingPreset: "Competitive (0% RT)",
                ShadowFidelity: "Medium (1024)",
                RefreshRate: targetHz,
                EnableTextureClarityBoost: false,
                EnableNvidiaReflex: false,
                EnableDirectFlip: false,
                PresentationMode: "Legacy Blit (Composited)",
                SwapchainBackend: "DXVK (Vulkan WSI)",
                SpatialChannels: "64 Channels (Fast / Esports Focus)",
                TierLabel: "Entry / Integrated Graphics (Maximum Frame Rate & Low Power)",
                Rationale: $"Entry-level or integrated GPU detected ({profile.GpuName}). Low-overhead pipeline configured with 0% ray tracing and lightweight audio to maximize FPS."
            )
        };
    }

    /// <summary>
    ///     Classifies hardware into one of four performance tiers.
    /// </summary>
    public static HardwareTier ClassifyTier(string gpuName, long vramBytes, long systemRamBytes)
    {
        string gpu = gpuName.ToUpperInvariant();
        double vramGb = vramBytes / (1024.0 * 1024.0 * 1024.0);
        double ramGb = systemRamBytes / (1024.0 * 1024.0 * 1024.0);

        // Explicit integrated or low-power display identification
        bool isIntegratedOrVirtual =
            gpu.Contains("UHD") ||
            gpu.Contains("IRIS") ||
            gpu.Contains("HD GRAPHICS") ||
            gpu.Contains("BASIC DISPLAY") ||
            gpu.Contains("STANDARD GRAPHICS") ||
            (gpu.Contains("INTEL") && !gpu.Contains("ARC")) ||
            vramGb < 1.0;

        if (isIntegratedOrVirtual)
        {
            return HardwareTier.EntryIntegrated;
        }

        // Tier 1: Enthusiast RTX / Flagship
        if (vramGb >= 11.5 ||
            gpu.Contains("4090") || gpu.Contains("4080") || gpu.Contains("3090") || gpu.Contains("3080") ||
            gpu.Contains("7900"))
        {
            return HardwareTier.EnthusiastRtx;
        }

        // Tier 2: High-Performance Modern GPU (e.g. RTX 4060, 4060 Ti, 4070, 3060, 3070, 2070+, RX 6700+, RX 7700+, Arc A770)
        if ((vramGb >= 7.0 ||
             gpu.Contains("RTX") || gpu.Contains("4060") || gpu.Contains("4070") ||
             gpu.Contains("3060") || gpu.Contains("3070") || gpu.Contains("2070") || gpu.Contains("2080") ||
             gpu.Contains("RX 6") || gpu.Contains("RX 7") || gpu.Contains("A770")) &&
            ramGb >= 15.0)
        {
            return HardwareTier.HighPerformanceRtx;
        }

        // Tier 3: Competitive Mid-Range (GTX 1060/1660, RTX 2060, RX 580/5600, Arc A580)
        if (vramGb >= 3.5 ||
            gpu.Contains("GTX") || gpu.Contains("RADEON") || gpu.Contains("ARC") ||
            ramGb >= 7.5)
        {
            return HardwareTier.CompetitiveMidRange;
        }

        // Tier 4: Entry / Integrated
        return HardwareTier.EntryIntegrated;
    }

    private static (string GpuName, long VramBytes) QueryPrimaryGpu()
    {
        if (!OperatingSystem.IsWindows())
        {
            return ("Standard Graphics Adapter", 0);
        }

        try
        {
            using RegistryKey? classKey = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}");
            if (classKey != null)
            {
                string bestName = "Standard Graphics Adapter";
                long maxVram = 0;

                foreach (string subKeyName in classKey.GetSubKeyNames())
                {
                    if (!int.TryParse(subKeyName, out _)) continue;

                    using RegistryKey? subKey = classKey.OpenSubKey(subKeyName);
                    if (subKey == null) continue;

                    string? desc = subKey.GetValue("DriverDesc") as string
                                ?? subKey.GetValue("HardwareInformation.AdapterString") as string;

                    if (string.IsNullOrWhiteSpace(desc)) continue;

                    // Exclude virtual display adapters (e.g. DisplayLink USB, VirtualBox, Remote Desktop)
                    if (desc.Contains("DisplayLink", StringComparison.OrdinalIgnoreCase) ||
                        desc.Contains("Virtual", StringComparison.OrdinalIgnoreCase) ||
                        desc.Contains("Remote", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    long vram = 0;
                    object? qwMem = subKey.GetValue("HardwareInformation.qwMemorySize");
                    if (qwMem is long l)
                    {
                        vram = l;
                    }
                    else if (qwMem is byte[] b && b.Length >= 8)
                    {
                        vram = BitConverter.ToInt64(b, 0);
                    }
                    else if (subKey.GetValue("HardwareInformation.MemorySize") is int dw)
                    {
                        vram = (long)(uint)dw;
                    }

                    if (vram > maxVram || (maxVram == 0 && bestName == "Standard Graphics Adapter"))
                    {
                        maxVram = vram;
                        bestName = desc;
                    }
                }

                if (!string.IsNullOrEmpty(bestName) && bestName != "Standard Graphics Adapter")
                {
                    return (bestName, maxVram);
                }
            }
        }
        catch
        {
            // Registry read failed, fall through to fallback
        }

        return ("Standard Graphics Adapter", 0);
    }

    private static long QuerySystemRam()
    {
        if (OperatingSystem.IsWindows())
        {
            try
            {
                MEMORYSTATUSEX memStatus = new();
                if (GlobalMemoryStatusEx(memStatus))
                {
                    return (long)memStatus.ullTotalPhys;
                }
            }
            catch
            {
                // Fall back to GC memory info
            }
        }

        try
        {
            return GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
        }
        catch
        {
            return 16L * 1024 * 1024 * 1024; // Safe 16 GB default
        }
    }

    public static int QueryDisplayRefreshRate()
    {
        if (OperatingSystem.IsWindows())
        {
            try
            {
                DEVMODE devMode = new() { dmSize = (short)Marshal.SizeOf<DEVMODE>() };
                if (EnumDisplaySettings(null, ENUM_CURRENT_SETTINGS, ref devMode) && devMode.dmDisplayFrequency > 0)
                {
                    return devMode.dmDisplayFrequency;
                }
            }
            catch
            {
                // Fall back
            }
        }
        else if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
        {
            try
            {
                IReadOnlyList<MonitorDisplayInfo> monitors = DetectAvailableMonitors();
                MonitorDisplayInfo? primary = monitors.FirstOrDefault(m => m.IsPrimary) ?? monitors.FirstOrDefault();
                if (primary is not null && primary.MaxRefreshRateHz > 0)
                {
                    return primary.MaxRefreshRateHz;
                }
            }
            catch
            {
                // Fall back
            }
        }

        return 60;
    }

    /// <summary>
    ///     Resolves the safe first-boot refresh rate clamped between 60 Hz and 500 Hz,
    ///     preserving exact non-standard panel refresh rates (e.g. 180 Hz, 300 Hz) without lossy snapping.
    ///     When detectedHzOverride is null and not running on Windows, returns (1000, "Uncapped (Max FPS)")
    ///     so 120 Hz ProMotion Macs and 144 Hz+ Linux monitors aren't capped at 60 FPS on first boot.
    /// </summary>
    public static (int Hz, string DisplayLabel) GetFirstBootRefreshRate(int? detectedHzOverride = null, bool? isWindowsOverride = null)
    {
        if (detectedHzOverride is null && !(isWindowsOverride ?? OperatingSystem.IsWindows()))
        {
            return (1000, "Uncapped (Max FPS)");
        }

        int rawHz = detectedHzOverride ?? QueryDisplayRefreshRate();
        int hz = Math.Clamp(rawHz, 60, 500);
        return (hz, $"{hz} Hz");
    }

    /// <summary>
    ///     Preserves the exact OS/EDID-reported monitor refresh rate clamped to [60, 500] Hz
    ///     (matching <see cref="GetFirstBootRefreshRate"/>), without snapping against a legacy hardcoded rate list.
    /// </summary>
    public static string ResolveClosestRefreshRateString(int hz)
    {
        int clamped = Math.Clamp(hz, 60, 500);
        return $"{clamped} Hz";
    }

    /// <summary>
    ///     Normalizes a Windows GDI monitor device string (e.g. @"\\.\DISPLAY2", @"\.DISPLAY2", "DISPLAY2")
    ///     into a canonical backslash-free token ("DISPLAY2") safe for K2 console commands and CVar storage.
    /// </summary>
    public static string NormalizeMonitorDeviceToken(string? deviceName)
    {
        if (string.IsNullOrWhiteSpace(deviceName))
            return string.Empty;

        string trimmed = deviceName.Trim().Trim('"', '\'');
        while (trimmed.Length > 0 && (trimmed[0] == '\\' || trimmed[0] == '/' || trimmed[0] == '.'))
        {
            trimmed = trimmed[1..];
        }

        return trimmed.ToUpperInvariant();
    }

    public static bool AreSameMonitorDevice(string? left, string? right)
    {
        string normLeft = NormalizeMonitorDeviceToken(left);
        string normRight = NormalizeMonitorDeviceToken(right);
        return normLeft.Length > 0 && string.Equals(normLeft, normRight, StringComparison.OrdinalIgnoreCase);
    }

    public static int ExtractMonitorDeviceNumber(string? deviceName)
    {
        string norm = NormalizeMonitorDeviceToken(deviceName);
        int digitStart = -1;
        for (int i = 0; i < norm.Length; i++)
        {
            if (char.IsDigit(norm[i]))
            {
                digitStart = i;
                break;
            }
        }

        if (digitStart >= 0 && int.TryParse(norm[digitStart..], out int num) && num > 0)
            return num;

        return int.MaxValue;
    }

    /// <summary>
    ///     Queries Windows CCD (<c>QueryDisplayConfig</c>) to map normalized GDI monitor tokens
    ///     (e.g. <c>"DISPLAY1"</c>) to their active PnP <c>monitorDevicePath</c> strings
    ///     (e.g. <c>"\\?\DISPLAY#AUS2718#5&amp;..."</c>) so EDID blobs can be read deterministically.
    /// </summary>
    public static Dictionary<string, string> QueryMonitorDevicePathsByGdiDevice()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!OperatingSystem.IsWindows())
            return map;

        try
        {
            if (GetDisplayConfigBufferSizes(QDC_ONLY_ACTIVE_PATHS, out uint pathCount, out uint modeCount) != 0 || pathCount == 0)
                return map;

            var paths = new DISPLAYCONFIG_PATH_INFO[pathCount];
            var modes = new DISPLAYCONFIG_MODE_INFO[modeCount];
            if (QueryDisplayConfig(QDC_ONLY_ACTIVE_PATHS, ref pathCount, paths, ref modeCount, modes, IntPtr.Zero) != 0)
                return map;

            for (int i = 0; i < pathCount; i++)
            {
                var path = paths[i];
                var srcName = new DISPLAYCONFIG_SOURCE_DEVICE_NAME
                {
                    header = new DISPLAYCONFIG_DEVICE_INFO_HEADER
                    {
                        type = DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME,
                        size = (uint)Marshal.SizeOf<DISPLAYCONFIG_SOURCE_DEVICE_NAME>(),
                        adapterId = path.sourceInfo.adapterId,
                        id = path.sourceInfo.id
                    },
                    viewGdiDeviceName = string.Empty
                };

                var dstName = new DISPLAYCONFIG_TARGET_DEVICE_NAME
                {
                    header = new DISPLAYCONFIG_DEVICE_INFO_HEADER
                    {
                        type = DISPLAYCONFIG_DEVICE_INFO_GET_TARGET_NAME,
                        size = (uint)Marshal.SizeOf<DISPLAYCONFIG_TARGET_DEVICE_NAME>(),
                        adapterId = path.targetInfo.adapterId,
                        id = path.targetInfo.id
                    },
                    monitorFriendlyDeviceName = string.Empty,
                    monitorDevicePath = string.Empty
                };

                if (DisplayConfigGetDeviceInfo(ref srcName) == 0 &&
                    DisplayConfigGetDeviceInfo(ref dstName) == 0 &&
                    !string.IsNullOrWhiteSpace(srcName.viewGdiDeviceName) &&
                    !string.IsNullOrWhiteSpace(dstName.monitorDevicePath))
                {
                    string token = NormalizeMonitorDeviceToken(srcName.viewGdiDeviceName);
                    string devicePath = dstName.monitorDevicePath.Trim();
                    if (!string.IsNullOrEmpty(token) && !string.IsNullOrEmpty(devicePath))
                    {
                        map[token] = devicePath;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine($"[WILLOWMAKER] QueryDisplayConfig device path lookup warning: {ex.Message}");
        }

        return map;
    }

    private static Dictionary<string, string> QueryMonitorFriendlyNamesByGdiDevice()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!OperatingSystem.IsWindows())
            return map;

        try
        {
            if (GetDisplayConfigBufferSizes(QDC_ONLY_ACTIVE_PATHS, out uint pathCount, out uint modeCount) != 0 || pathCount == 0)
                return map;

            var paths = new DISPLAYCONFIG_PATH_INFO[pathCount];
            var modes = new DISPLAYCONFIG_MODE_INFO[modeCount];
            if (QueryDisplayConfig(QDC_ONLY_ACTIVE_PATHS, ref pathCount, paths, ref modeCount, modes, IntPtr.Zero) != 0)
                return map;

            for (int i = 0; i < pathCount; i++)
            {
                var path = paths[i];
                var srcName = new DISPLAYCONFIG_SOURCE_DEVICE_NAME
                {
                    header = new DISPLAYCONFIG_DEVICE_INFO_HEADER
                    {
                        type = DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME,
                        size = (uint)Marshal.SizeOf<DISPLAYCONFIG_SOURCE_DEVICE_NAME>(),
                        adapterId = path.sourceInfo.adapterId,
                        id = path.sourceInfo.id
                    },
                    viewGdiDeviceName = string.Empty
                };

                var dstName = new DISPLAYCONFIG_TARGET_DEVICE_NAME
                {
                    header = new DISPLAYCONFIG_DEVICE_INFO_HEADER
                    {
                        type = DISPLAYCONFIG_DEVICE_INFO_GET_TARGET_NAME,
                        size = (uint)Marshal.SizeOf<DISPLAYCONFIG_TARGET_DEVICE_NAME>(),
                        adapterId = path.targetInfo.adapterId,
                        id = path.targetInfo.id
                    },
                    monitorFriendlyDeviceName = string.Empty,
                    monitorDevicePath = string.Empty
                };

                if (DisplayConfigGetDeviceInfo(ref srcName) == 0 &&
                    DisplayConfigGetDeviceInfo(ref dstName) == 0 &&
                    !string.IsNullOrWhiteSpace(srcName.viewGdiDeviceName) &&
                    !string.IsNullOrWhiteSpace(dstName.monitorFriendlyDeviceName))
                {
                    string token = NormalizeMonitorDeviceToken(srcName.viewGdiDeviceName);
                    string friendly = dstName.monitorFriendlyDeviceName.Trim();
                    if (!string.IsNullOrEmpty(token) && !string.IsNullOrEmpty(friendly))
                    {
                        map[token] = friendly;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine($"[WILLOWMAKER] QueryDisplayConfig friendly name lookup warning: {ex.Message}");
        }

        return map;
    }

    private static readonly Lock MonitorCacheLock = new();
    private static IReadOnlyList<MonitorDisplayInfo>? s_cachedMonitors;
    private static long s_cachedMonitorsTimestampTicks;
    private static readonly long MonitorCacheTtlTicks = TimeSpan.FromSeconds(5).Ticks;
    private static int s_lastDetectionIgnoredAuxDisplay;

    /// <summary>
    ///     Indicates whether the most recent <see cref="DetectAvailableMonitors"/> scan excluded one or more
    ///     non-gaming auxiliary / StreamDeck / mini-USB displays.
    /// </summary>
    public static bool LastDetectionIgnoredAuxiliaryDisplay => Volatile.Read(ref s_lastDetectionIgnoredAuxDisplay) != 0;

    private static readonly string[] KnownAuxiliaryDisplayTokens =
    [
        "Stream Deck",
        "StreamDeck",
        "Elgato",
        "VID_0FD9",
        "Loupedeck",
        "Razer Stream Controller",
        "VoCore",
        "Turing Smart",
        "BeadaPanel",
        "USBDMonitor",
        "DisplayLink",
        "UsbMobileMonitor",
        "Spacedesk",
        "IddSampleDriver"
    ];

    /// <summary>
    ///     Determines whether an enumerated desktop display is an auxiliary / StreamDeck / mini-USB panel
    ///     rather than a gaming monitor. Never excludes the OS primary display.
    /// </summary>
    public static bool IsAuxiliaryOrStreamDeckDisplay(
        string? deviceString,
        string? friendlyName,
        string? deviceId,
        int nativeWidth,
        int nativeHeight,
        bool isPrimary)
    {
        if (isPrimary)
        {
            return false;
        }

        if (nativeWidth > 0 && nativeHeight > 0 && (nativeWidth < 1024 || nativeHeight < 720))
        {
            return true;
        }

        foreach (string token in KnownAuxiliaryDisplayTokens)
        {
            if ((!string.IsNullOrWhiteSpace(deviceString) && deviceString.Contains(token, StringComparison.OrdinalIgnoreCase)) ||
                (!string.IsNullOrWhiteSpace(friendlyName) && friendlyName.Contains(token, StringComparison.OrdinalIgnoreCase)) ||
                (!string.IsNullOrWhiteSpace(deviceId) && deviceId.Contains(token, StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    ///     Enumerates all connected monitors, their EDID friendly model names, desktop bounds, native modes,
    ///     current refresh rates, and all supported display resolutions with their respective refresh rates.
    ///     Uses a short 5-second TTL cache so concurrent ViewModel/settings initialization avoids win32k/DisplayLink driver contention.
    /// </summary>
    public static IReadOnlyList<MonitorDisplayInfo> DetectAvailableMonitors(bool forceRefresh = false)
    {
        long nowTicks = Environment.TickCount64 * TimeSpan.TicksPerMillisecond;
        var existing = Volatile.Read(ref s_cachedMonitors);
        if (!forceRefresh && existing is not null && (nowTicks - Interlocked.Read(ref s_cachedMonitorsTimestampTicks)) < MonitorCacheTtlTicks)
        {
            return existing;
        }

        lock (MonitorCacheLock)
        {
            nowTicks = Environment.TickCount64 * TimeSpan.TicksPerMillisecond;
            if (!forceRefresh && s_cachedMonitors is not null && (nowTicks - s_cachedMonitorsTimestampTicks) < MonitorCacheTtlTicks)
            {
                return s_cachedMonitors;
            }

            IReadOnlyList<MonitorDisplayInfo> detected = OperatingSystem.IsWindows()
                ? DetectAvailableMonitorsWindows()
                : OperatingSystem.IsLinux()
                    ? DetectAvailableMonitorsLinux()
                    : OperatingSystem.IsMacOS()
                        ? DetectAvailableMonitorsMacOs()
                        : CreateFallbackMonitors();
            Volatile.Write(ref s_cachedMonitors, detected);
            Interlocked.Exchange(ref s_cachedMonitorsTimestampTicks, Environment.TickCount64 * TimeSpan.TicksPerMillisecond);
            return detected;
        }
    }

    private static IReadOnlyList<MonitorDisplayInfo> DetectAvailableMonitorsWindows()
    {
        var rawMonitors = new List<MonitorDisplayInfo>();
        var allEnumeratedMonitors = new List<MonitorDisplayInfo>();
        int ignoredAuxCount = 0;

        try
        {
            Dictionary<string, string> ccdFriendlyNames = QueryMonitorFriendlyNamesByGdiDevice();
            uint devNum = 0;
            DISPLAY_DEVICEW d = new() { cb = Marshal.SizeOf<DISPLAY_DEVICEW>() };

            while (EnumDisplayDevices(null, devNum, ref d, 0))
            {
                // Only consider adapters that are currently active on the desktop
                if ((d.StateFlags & 1) != 0) // DISPLAY_DEVICE_ATTACHED_TO_DESKTOP
                {
                    bool isPrimary = (d.StateFlags & 4) != 0; // DISPLAY_DEVICE_PRIMARY_DEVICE
                    string normToken = NormalizeMonitorDeviceToken(d.DeviceName);
                    string friendlyName = ccdFriendlyNames.TryGetValue(normToken, out string? ccdName) && !string.IsNullOrWhiteSpace(ccdName)
                        ? ccdName
                        : ResolveMonitorFriendlyName(d.DeviceName, d.DeviceString);

                    string? monitorHardwareId = null;
                    DISPLAY_DEVICEW monInterface = new() { cb = Marshal.SizeOf<DISPLAY_DEVICEW>() };
                    if (EnumDisplayDevices(d.DeviceName, 0, ref monInterface, 1) && !string.IsNullOrWhiteSpace(monInterface.DeviceID))
                    {
                        monitorHardwareId = monInterface.DeviceID;
                    }

                    DEVMODE dm = new() { dmSize = (short)Marshal.SizeOf<DEVMODE>() };
                    int nativeW = 1920;
                    int nativeH = 1080;
                    int currentHz = 60;
                    int posX = 0;
                    int posY = 0;

                    if (EnumDisplaySettings(d.DeviceName, ENUM_CURRENT_SETTINGS, ref dm) && dm.dmPelsWidth > 0 && dm.dmPelsHeight > 0)
                    {
                        nativeW = dm.dmPelsWidth;
                        nativeH = dm.dmPelsHeight;
                        currentHz = dm.dmDisplayFrequency > 1 ? dm.dmDisplayFrequency : 60;
                        posX = dm.dmPositionX;
                        posY = dm.dmPositionY;
                    }

                    // Enumerate all display modes to find all supported refresh rates and max Hz per resolution
                    var resRatesMap = new Dictionary<(int Width, int Height), HashSet<int>>();
                    int modeNum = 0;
                    DEVMODE modeDm = new() { dmSize = (short)Marshal.SizeOf<DEVMODE>() };

                    while (EnumDisplaySettings(d.DeviceName, modeNum, ref modeDm))
                    {
                        if (modeDm.dmBitsPerPel == 32 && modeDm.dmPelsWidth >= 640 && modeDm.dmPelsHeight >= 480)
                        {
                            var key = (modeDm.dmPelsWidth, modeDm.dmPelsHeight);
                            int modeHz = modeDm.dmDisplayFrequency > 1 ? modeDm.dmDisplayFrequency : 60;
                            if (!resRatesMap.TryGetValue(key, out HashSet<int>? rates))
                            {
                                rates = new HashSet<int>();
                                resRatesMap[key] = rates;
                            }
                            rates.Add(modeHz);
                        }
                        modeNum++;
                    }

                    // Ensure native desktop resolution and active refresh rate are always present
                    if (!resRatesMap.TryGetValue((nativeW, nativeH), out HashSet<int>? nativeRates))
                    {
                        nativeRates = new HashSet<int>();
                        resRatesMap[(nativeW, nativeH)] = nativeRates;
                    }
                    nativeRates.Add(currentHz);

                    int maxHzAtNative = nativeRates.Count > 0 ? nativeRates.Max() : currentHz;

                    // Sort resolutions descending by pixel area, prioritizing native resolution at index 0
                    var supportedResolutions = resRatesMap
                        .OrderByDescending(r => r.Key.Width == nativeW && r.Key.Height == nativeH)
                        .ThenByDescending(r => (long)r.Key.Width * r.Key.Height)
                        .Select(r =>
                        {
                            bool isNative = r.Key.Width == nativeW && r.Key.Height == nativeH;
                            var sortedRates = r.Value.OrderByDescending(hz => hz).ToList();
                            int maxHz = sortedRates.Count > 0 ? sortedRates[0] : currentHz;
                            return new MonitorResolutionOption(
                                Width: r.Key.Width,
                                Height: r.Key.Height,
                                MaxRefreshRateHz: maxHz,
                                IsNative: isNative,
                                DisplayLabel: FormatResolutionOptionLabel(r.Key.Width, r.Key.Height, isNative),
                                SupportedRefreshRatesHz: sortedRates
                            );
                        })
                        .ToList();

                    var candidate = new MonitorDisplayInfo(
                        Index: 0,
                        DeviceName: d.DeviceName,
                        FriendlyName: friendlyName,
                        IsPrimary: isPrimary,
                        BoundsX: posX,
                        BoundsY: posY,
                        NativeWidth: nativeW,
                        NativeHeight: nativeH,
                        CurrentRefreshRateHz: currentHz,
                        MaxRefreshRateHz: maxHzAtNative,
                        SupportedResolutions: supportedResolutions
                    );

                    allEnumeratedMonitors.Add(candidate);

                    if (IsAuxiliaryOrStreamDeckDisplay(d.DeviceString, friendlyName, monitorHardwareId, nativeW, nativeH, isPrimary))
                    {
                        ignoredAuxCount++;
                        System.Diagnostics.Trace.WriteLine(
                            $"[WILLOWMAKER-MONITORS] Ignored auxiliary/StreamDeck display device='{d.DeviceName}' name='{friendlyName}' adapter='{d.DeviceString}' hwid='{monitorHardwareId ?? "none"}' native={nativeW}x{nativeH}");
                    }
                    else
                    {
                        rawMonitors.Add(candidate);
                    }
                }
                devNum++;
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine($"[WILLOWMAKER] Error enumerating display devices: {ex.Message}");
        }

        Interlocked.Exchange(ref s_lastDetectionIgnoredAuxDisplay, ignoredAuxCount > 0 ? 1 : 0);

        if (rawMonitors.Count == 0 && allEnumeratedMonitors.Count > 0)
        {
            rawMonitors.AddRange(allEnumeratedMonitors);
        }

        if (rawMonitors.Count == 0)
        {
            return CreateFallbackMonitors();
        }

        var ordered = rawMonitors
            .OrderByDescending(m => m.IsPrimary)
            .ThenBy(m => ExtractMonitorDeviceNumber(m.DeviceName))
            .ThenBy(m => m.DeviceName, StringComparer.OrdinalIgnoreCase)
            .Select((m, idx) => m with { Index = idx + 1 })
            .ToList();

        foreach (var mon in ordered)
        {
            System.Diagnostics.Trace.WriteLine(
                $"[WILLOWMAKER-MONITORS] #{mon.Index} token='{mon.NormalizedDeviceToken}' name='{mon.FriendlyName}' primary={mon.IsPrimary} native={mon.NativeWidth}x{mon.NativeHeight} currentHz={mon.CurrentRefreshRateHz} maxHz={mon.MaxRefreshRateHz} modes={mon.SupportedResolutions.Count}");
        }

        return ordered;
    }

    private static string ResolveMonitorFriendlyName(string deviceName, string fallbackDeviceString)
    {
        if (!OperatingSystem.IsWindows()) return fallbackDeviceString;

        try
        {
            DISPLAY_DEVICEW mon = new() { cb = Marshal.SizeOf<DISPLAY_DEVICEW>() };
            // EDD_GET_DEVICE_INTERFACE_NAME = 0x00000001 returns the DISPLAY#HWID#INSTANCE interface path
            if (EnumDisplayDevices(deviceName, 0, ref mon, 1) && !string.IsNullOrWhiteSpace(mon.DeviceID))
            {
                string? edidName = TryGetMonitorNameFromEdidRegistry(mon.DeviceID);
                if (!string.IsNullOrWhiteSpace(edidName))
                {
                    return edidName;
                }
            }

            mon = new DISPLAY_DEVICEW { cb = Marshal.SizeOf<DISPLAY_DEVICEW>() };
            if (EnumDisplayDevices(deviceName, 0, ref mon, 0))
            {
                if (!string.IsNullOrWhiteSpace(mon.DeviceString) &&
                    !mon.DeviceString.Equals("Generic PnP Monitor", StringComparison.OrdinalIgnoreCase))
                {
                    return mon.DeviceString;
                }
            }
        }
        catch
        {
            // Fall back
        }

        string norm = NormalizeMonitorDeviceToken(deviceName);
        if (!string.IsNullOrWhiteSpace(norm))
            return norm;

        return !string.IsNullOrWhiteSpace(fallbackDeviceString) ? fallbackDeviceString : "Generic Display";
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static string? TryGetMonitorNameFromEdidRegistry(string deviceId)
    {
        if (!OperatingSystem.IsWindows()) return null;
        try
        {
            // Supports both interface path (\\?\DISPLAY#HWID#INSTANCE#{guid}) and PnP ID (DISPLAY\HWID\INSTANCE)
            string cleaned = deviceId.Trim();
            if (cleaned.StartsWith(@"\\?\", StringComparison.Ordinal))
                cleaned = cleaned[4..];

            string[] parts = cleaned.Contains('#') ? cleaned.Split('#') : cleaned.Split('\\');
            if (parts.Length < 3) return null;

            string hardwareId = parts[1];
            string instanceId = parts[2];

            string regPath = $@"SYSTEM\CurrentControlSet\Enum\DISPLAY\{hardwareId}\{instanceId}\Device Parameters";
            using RegistryKey? key = Registry.LocalMachine.OpenSubKey(regPath);
            if (key?.GetValue("EDID") is byte[] edid && edid.Length >= 128)
            {
                string? parsed = ParseEdidModelName(edid);
                if (!string.IsNullOrWhiteSpace(parsed))
                {
                    return parsed;
                }
            }
        }
        catch
        {
            // Ignored
        }

        return null;
    }

    /// <summary>
    ///     Extracts the human-readable monitor model name from a raw 128+ byte VESA EDID block
    ///     (descriptor tag 0xFC Monitor Name, falling back to 0xFE Unspecified Text).
    /// </summary>
    public static string? ParseEdidModelName(ReadOnlySpan<byte> edid)
    {
        if (edid.Length < 128)
        {
            return null;
        }

        Span<char> buf = stackalloc char[13];
        string? fallbackAsciiText = null;
        for (int i = 54; i <= 108; i += 18)
        {
            if (edid[i] == 0 && edid[i + 1] == 0 && edid[i + 2] == 0 && (edid[i + 3] == 0xFC || edid[i + 3] == 0xFE))
            {
                int len = 0;
                for (int j = i + 5; j <= i + 17; j++)
                {
                    byte b = edid[j];
                    if (b == 0x0A || b == 0x00) break;
                    if (b >= 32 && b <= 126) buf[len++] = (char)b;
                }

                string parsed = new string(buf[..len]).Trim();
                if (!string.IsNullOrWhiteSpace(parsed))
                {
                    if (edid[i + 3] == 0xFC)
                    {
                        return parsed;
                    }

                    fallbackAsciiText ??= parsed;
                }
            }
        }

        return fallbackAsciiText;
    }

    /// <summary>
    ///     Extracts Detailed Timing Descriptor (DTD) refresh rates from a 128+ byte VESA EDID block,
    ///     keyed by (Width, Height). Useful on Linux Wayland/DRM sysfs where /sys/class/drm/*/modes omits Hz.
    /// </summary>
    public static Dictionary<(int Width, int Height), List<int>> ParseEdidDetailedTimingRefreshRates(ReadOnlySpan<byte> edid)
    {
        var result = new Dictionary<(int Width, int Height), List<int>>();
        if (edid.Length < 128)
        {
            return result;
        }

        int totalBlocks = edid.Length / 128;
        for (int blockIdx = 0; blockIdx < totalBlocks; blockIdx++)
        {
            int blockBase = blockIdx * 128;
            if (blockIdx > 0 && edid[blockBase] == 0x70)
            {
                // VESA DisplayID 1.3 / 2.0 Extension Block (Type I tag 0x03 / Type VII tag 0x22: 24-bit pixel clock > 655.35 MHz)
                int sectionBytes = Math.Min((int)edid[blockBase + 2], 121);
                int cursor = blockBase + 5;
                int sectionEnd = cursor + sectionBytes;
                while (cursor + 3 <= sectionEnd)
                {
                    byte dataTag = edid[cursor];
                    int payloadLen = edid[cursor + 2];
                    int payloadStart = cursor + 3;
                    if (payloadLen <= 0 || payloadStart + payloadLen > sectionEnd)
                    {
                        break;
                    }

                    if (dataTag is 0x03 or 0x22)
                    {
                        for (int d = payloadStart; d + 20 <= payloadStart + payloadLen; d += 20)
                        {
                            int pixelClock10kHz = (edid[d] | (edid[d + 1] << 8) | (edid[d + 2] << 16)) + 1;
                            bool interlaced = (edid[d + 3] & 0x10) != 0;
                            int hActive = (edid[d + 4] | (edid[d + 5] << 8)) + 1;
                            int hBlank = (edid[d + 6] | (edid[d + 7] << 8)) + 1;
                            int vActive = (edid[d + 12] | (edid[d + 13] << 8)) + 1;
                            int vBlank = (edid[d + 14] | (edid[d + 15] << 8)) + 1;
                            int hTotal = hActive + hBlank;
                            int vTotal = vActive + vBlank;
                            if (!interlaced && hActive >= 640 && vActive >= 480 && hTotal > 0 && vTotal > 0)
                            {
                                int hz = (int)Math.Round((pixelClock10kHz * 10_000.0) / ((double)hTotal * vTotal));
                                if (hz >= 24 && hz <= 1000)
                                {
                                    var key = (hActive, vActive);
                                    if (!result.TryGetValue(key, out List<int>? list))
                                    {
                                        list = [];
                                        result[key] = list;
                                    }
                                    if (!list.Contains(hz))
                                    {
                                        list.Add(hz);
                                    }
                                }
                            }
                        }
                    }

                    cursor = payloadStart + payloadLen;
                }
                continue;
            }

            int startOffset = blockIdx == 0 ? 54 : (edid[blockBase] == 0x02 && edid[blockBase + 2] >= 4 ? blockBase + edid[blockBase + 2] : -1);
            if (startOffset < 0) continue;

            for (int pos = startOffset; pos + 18 <= blockBase + 127; pos += 18)
            {
                int pixelClock10kHz = edid[pos] | (edid[pos + 1] << 8);
                if (pixelClock10kHz == 0) continue; // Display descriptor, not a DTD

                int hActive = edid[pos + 2] | ((edid[pos + 4] & 0xF0) << 4);
                int hBlank = edid[pos + 3] | ((edid[pos + 4] & 0x0F) << 8);
                int vActive = edid[pos + 5] | ((edid[pos + 7] & 0xF0) << 4);
                int vBlank = edid[pos + 6] | ((edid[pos + 7] & 0x0F) << 8);
                bool interlaced = (edid[pos + 17] & 0x80) != 0;

                int hTotal = hActive + hBlank;
                int vTotal = vActive + vBlank;
                if (!interlaced && hActive >= 640 && vActive >= 480 && hTotal > 0 && vTotal > 0)
                {
                    double hzDouble = (pixelClock10kHz * 10_000.0) / ((double)hTotal * vTotal);
                    int hz = (int)Math.Round(hzDouble);
                    if (hz >= 24 && hz <= 1000)
                    {
                        var key = (hActive, vActive);
                        if (!result.TryGetValue(key, out List<int>? list))
                        {
                            list = [];
                            result[key] = list;
                        }
                        if (!list.Contains(hz))
                        {
                            list.Add(hz);
                        }
                    }
                }
            }
        }

        foreach (List<int> rates in result.Values)
        {
            rates.Sort((a, b) => b.CompareTo(a));
        }

        return result;
    }

    /// <summary>
    ///     Serializes detected monitors into the K2-console-safe <c>kongor_detected_monitors</c> CVar payload
    ///     (<c>TOKEN|Index|Name|Primary|W,H|MaxHz|WxH@r1,r2+...</c> separated by <c>/</c>).
    /// </summary>
    public static string FormatKongorDetectedMonitorsCvar(IReadOnlyList<MonitorDisplayInfo>? monitors)
    {
        if (monitors is null || monitors.Count == 0)
        {
            return string.Empty;
        }

        StringBuilder sb = new();
        for (int i = 0; i < monitors.Count; i++)
        {
            MonitorDisplayInfo mon = monitors[i];
            if (i > 0) sb.Append('/');

            string token = NormalizeMonitorDeviceToken(mon.DeviceName);
            if (string.IsNullOrWhiteSpace(token))
            {
                token = $"DISPLAY{mon.Index}";
            }

            string cleanName = SanitizeKongorMonitorName(mon.FriendlyName, token);
            int primaryFlag = mon.IsPrimary ? 1 : 0;
            int maxHz = mon.MaxRefreshRateHz > 0 ? mon.MaxRefreshRateHz : 60;

            sb.Append(token)
              .Append('|')
              .Append(mon.Index > 0 ? mon.Index : i + 1)
              .Append('|')
              .Append(cleanName)
              .Append('|')
              .Append(primaryFlag)
              .Append('|')
              .Append(mon.NativeWidth)
              .Append(',')
              .Append(mon.NativeHeight)
              .Append('|')
              .Append(maxHz)
              .Append('|');

            // Cap modes/rates per monitor so multi-monitor payloads stay strictly below K2 CConsole MAX_CMD_LENGTH (1024 chars)
            int modeLimit = Math.Min(mon.SupportedResolutions.Count, 8);
            for (int m = 0; m < modeLimit; m++)
            {
                MonitorResolutionOption mode = mon.SupportedResolutions[m];
                if (m > 0) sb.Append('+');
                sb.Append(mode.Width).Append('x').Append(mode.Height).Append('@');

                IReadOnlyList<int> rates = mode.SupportedRefreshRatesHz is { Count: > 0 }
                    ? mode.SupportedRefreshRatesHz
                    : [mode.MaxRefreshRateHz > 0 ? mode.MaxRefreshRateHz : maxHz];

                int rateLimit = Math.Min(rates.Count, 6);
                for (int r = 0; r < rateLimit; r++)
                {
                    if (r > 0) sb.Append(',');
                    sb.Append(rates[r]);
                }
            }
        }

        string built = sb.ToString();
        return built.Length <= 940 ? built : built[..940];
    }

    private static string SanitizeKongorMonitorName(string? name, string fallback)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return fallback;
        }

        StringBuilder sb = new(name.Length);
        foreach (char ch in name.Trim())
        {
            if (ch is '|' or '/' or ';' or '"' or '\'' or '\\' or '\r' or '\n')
            {
                sb.Append(' ');
            }
            else if (ch >= 32 && ch <= 126)
            {
                sb.Append(ch);
            }
        }

        string cleaned = sb.ToString().Trim();
        return string.IsNullOrWhiteSpace(cleaned) ? fallback : cleaned;
    }

    private static readonly Regex XrandrConnectedHeaderRegex = new(
        @"^(?<name>\S+)\s+connected\s+(?:(?<primary>primary)\s+)?(?<w>\d+)x(?<h>\d+)(?<x>[+-]\d+)(?<y>[+-]\d+)\b",
        RegexOptions.Compiled);

    private static readonly Regex XrandrStatusLineRegex = new(
        @"^(?<name>\S+)\s+(?:connected|disconnected)\b",
        RegexOptions.Compiled);

    private static readonly Regex XrandrModeHeaderRegex = new(
        @"^\s+(?<w>\d+)x(?<h>\d+)(?<suffix>\S*)\s+(?<rates>.+)$",
        RegexOptions.Compiled);

    private static readonly Regex XrandrRateTokenRegex = new(
        @"(?<hz>\d+(?:\.\d+)?)(?<flags>[*+]*)",
        RegexOptions.Compiled);

    /// <summary>
    ///     Parses <c>xrandr --props</c> output on Linux/X11/XWayland into structured <see cref="MonitorDisplayInfo"/> entries,
    ///     extracting 128-byte hex EDID model names, desktop coordinates, native resolutions, and all progressive refresh rates.
    /// </summary>
    public static IReadOnlyList<MonitorDisplayInfo> ParseXrandrPropsMonitors(string? xrandrOutput)
    {
        if (string.IsNullOrWhiteSpace(xrandrOutput))
        {
            return [];
        }

        var rawMonitors = new List<MonitorDisplayInfo>();
        var allEnumerated = new List<MonitorDisplayInfo>();
        int ignoredAuxCount = 0;

        string? curOutputName = null;
        bool curIsPrimary = false;
        int curPosX = 0, curPosY = 0, curNativeW = 0, curNativeH = 0, curActiveHz = 0;
        bool inEdidHexBlock = false;
        StringBuilder edidHex = new();
        var curResRates = new Dictionary<(int Width, int Height), HashSet<int>>();
        (int Width, int Height)? lastModeKey = null;

        void FinalizeCurrentOutput()
        {
            if (string.IsNullOrWhiteSpace(curOutputName) || curNativeW <= 0 || curNativeH <= 0)
            {
                return;
            }

            string friendlyName = curOutputName;
            if (edidHex.Length >= 256)
            {
                byte[]? edidBytes = TryDecodeHexBytes(edidHex.ToString());
                if (edidBytes is not null)
                {
                    string? parsedEdid = ParseEdidModelName(edidBytes);
                    if (!string.IsNullOrWhiteSpace(parsedEdid))
                    {
                        friendlyName = parsedEdid;
                    }
                }
            }

            int activeHz = curActiveHz > 1 ? curActiveHz : 60;
            if (!curResRates.TryGetValue((curNativeW, curNativeH), out HashSet<int>? nativeRates))
            {
                nativeRates = new HashSet<int>();
                curResRates[(curNativeW, curNativeH)] = nativeRates;
            }
            if (nativeRates.Count == 0 || curActiveHz > 1)
            {
                nativeRates.Add(activeHz);
            }

            int maxHzAtNative = nativeRates.Count > 0 ? nativeRates.Max() : activeHz;
            var supportedResolutions = curResRates
                .OrderByDescending(r => r.Key.Width == curNativeW && r.Key.Height == curNativeH)
                .ThenByDescending(r => (long)r.Key.Width * r.Key.Height)
                .Select(r =>
                {
                    bool isNative = r.Key.Width == curNativeW && r.Key.Height == curNativeH;
                    var sortedRates = r.Value.OrderByDescending(hz => hz).ToList();
                    int maxHz = sortedRates.Count > 0 ? sortedRates[0] : activeHz;
                    return new MonitorResolutionOption(
                        Width: r.Key.Width,
                        Height: r.Key.Height,
                        MaxRefreshRateHz: maxHz,
                        IsNative: isNative,
                        DisplayLabel: FormatResolutionOptionLabel(r.Key.Width, r.Key.Height, isNative),
                        SupportedRefreshRatesHz: sortedRates);
                })
                .ToList();

            var candidate = new MonitorDisplayInfo(
                Index: 0,
                DeviceName: NormalizeMonitorDeviceToken(curOutputName),
                FriendlyName: friendlyName,
                IsPrimary: curIsPrimary,
                BoundsX: curPosX,
                BoundsY: curPosY,
                NativeWidth: curNativeW,
                NativeHeight: curNativeH,
                CurrentRefreshRateHz: activeHz,
                MaxRefreshRateHz: maxHzAtNative,
                SupportedResolutions: supportedResolutions);

            allEnumerated.Add(candidate);
            if (IsAuxiliaryOrStreamDeckDisplay(curOutputName, friendlyName, null, curNativeW, curNativeH, curIsPrimary))
            {
                ignoredAuxCount++;
            }
            else
            {
                rawMonitors.Add(candidate);
            }
        }

        string[] lines = xrandrOutput.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        foreach (string line in lines)
        {
            if (line.StartsWith("Screen ", StringComparison.Ordinal))
            {
                continue;
            }

            if (XrandrStatusLineRegex.IsMatch(line))
            {
                FinalizeCurrentOutput();
                curOutputName = null;
                inEdidHexBlock = false;
                edidHex.Clear();
                curResRates = new Dictionary<(int Width, int Height), HashSet<int>>();
                lastModeKey = null;

                Match connMatch = XrandrConnectedHeaderRegex.Match(line);
                if (connMatch.Success &&
                    int.TryParse(connMatch.Groups["w"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int w) &&
                    int.TryParse(connMatch.Groups["h"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int h) &&
                    int.TryParse(connMatch.Groups["x"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int x) &&
                    int.TryParse(connMatch.Groups["y"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int y) &&
                    w > 0 && h > 0)
                {
                    curOutputName = connMatch.Groups["name"].Value;
                    curIsPrimary = connMatch.Groups["primary"].Success;
                    curNativeW = w;
                    curNativeH = h;
                    curPosX = x;
                    curPosY = y;
                    curActiveHz = 0;
                }
                continue;
            }

            if (curOutputName is null)
            {
                continue;
            }

            string trimmed = line.Trim();
            if (trimmed.Equals("EDID:", StringComparison.OrdinalIgnoreCase))
            {
                inEdidHexBlock = true;
                edidHex.Clear();
                lastModeKey = null;
                continue;
            }

            if (inEdidHexBlock)
            {
                if (IsHexOnlyLine(trimmed))
                {
                    edidHex.Append(trimmed);
                    continue;
                }

                inEdidHexBlock = false;
            }

            Match modeMatch = XrandrModeHeaderRegex.Match(line);
            if (modeMatch.Success && !line.Contains(':', StringComparison.Ordinal))
            {
                string suffix = modeMatch.Groups["suffix"].Value;
                if (suffix.EndsWith("i", StringComparison.OrdinalIgnoreCase))
                {
                    lastModeKey = null;
                    continue; // Skip interlaced modes
                }

                if (int.TryParse(modeMatch.Groups["w"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int mw) &&
                    int.TryParse(modeMatch.Groups["h"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int mh) &&
                    mw >= 640 && mh >= 480)
                {
                    lastModeKey = (mw, mh);
                    if (!curResRates.TryGetValue((mw, mh), out HashSet<int>? set))
                    {
                        set = new HashSet<int>();
                        curResRates[(mw, mh)] = set;
                    }

                    foreach (Match rm in XrandrRateTokenRegex.Matches(modeMatch.Groups["rates"].Value))
                    {
                        if (double.TryParse(rm.Groups["hz"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double hzD))
                        {
                            int hzInt = (int)Math.Round(hzD);
                            if (hzInt > 1 && hzInt <= 1000)
                            {
                                set.Add(hzInt);
                                if (rm.Groups["flags"].Value.Contains('*'))
                                {
                                    curActiveHz = hzInt;
                                }
                            }
                        }
                    }
                }
                continue;
            }

            // Handle continuation lines that list additional refresh rates for the previous mode
            if (lastModeKey.HasValue && !line.Contains(':', StringComparison.Ordinal) && trimmed.Length > 0 && char.IsDigit(trimmed[0]))
            {
                HashSet<int> set = curResRates[lastModeKey.Value];
                foreach (Match rm in XrandrRateTokenRegex.Matches(trimmed))
                {
                    if (double.TryParse(rm.Groups["hz"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double hzD))
                    {
                        int hzInt = (int)Math.Round(hzD);
                        if (hzInt > 1 && hzInt <= 1000)
                        {
                            set.Add(hzInt);
                            if (rm.Groups["flags"].Value.Contains('*'))
                            {
                                curActiveHz = hzInt;
                            }
                        }
                    }
                }
            }
            else
            {
                lastModeKey = null;
            }
        }

        FinalizeCurrentOutput();

        Interlocked.Exchange(ref s_lastDetectionIgnoredAuxDisplay, ignoredAuxCount > 0 ? 1 : 0);
        if (rawMonitors.Count == 0 && allEnumerated.Count > 0)
        {
            rawMonitors.AddRange(allEnumerated);
        }

        if (rawMonitors.Count == 0)
        {
            return [];
        }

        // Preserve primary first, then desktop left-to-right / top-to-bottom ordering
        bool anyMarkedPrimary = rawMonitors.Any(m => m.IsPrimary);
        if (!anyMarkedPrimary && rawMonitors.Count > 0)
        {
            int primaryIdx = rawMonitors.FindIndex(m => m.BoundsX == 0 && m.BoundsY == 0);
            if (primaryIdx < 0) primaryIdx = 0;
            rawMonitors[primaryIdx] = rawMonitors[primaryIdx] with { IsPrimary = true };
        }

        return rawMonitors
            .OrderByDescending(m => m.IsPrimary)
            .ThenBy(m => m.BoundsX)
            .ThenBy(m => m.BoundsY)
            .ThenBy(m => m.DeviceName, StringComparer.OrdinalIgnoreCase)
            .Select((m, idx) => m with { Index = idx + 1 })
            .ToList();
    }

    /// <summary>
    ///     Enumerates connected Linux displays directly from kernel DRM sysfs (<c>/sys/class/drm/card*-*</c>)
    ///     when running on pure Wayland without XWayland/<c>xrandr</c>.
    /// </summary>
    public static IReadOnlyList<MonitorDisplayInfo> ParseLinuxSysfsDrmMonitors(string drmRoot = "/sys/class/drm")
    {
        if (string.IsNullOrWhiteSpace(drmRoot) || !Directory.Exists(drmRoot))
        {
            return [];
        }

        var rawMonitors = new List<MonitorDisplayInfo>();
        try
        {
            string[] connectorDirs = Directory.GetDirectories(drmRoot, "card*-*")
                .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            int offsetX = 0;
            foreach (string dir in connectorDirs)
            {
                string statusPath = Path.Combine(dir, "status");
                if (!File.Exists(statusPath)) continue;
                string status = File.ReadAllText(statusPath).Trim();
                if (!status.Equals("connected", StringComparison.OrdinalIgnoreCase)) continue;

                string dirName = Path.GetFileName(dir); // e.g. card0-DP-1
                int dashIdx = dirName.IndexOf('-');
                string connectorToken = dashIdx > 0 && dashIdx + 1 < dirName.Length
                    ? dirName[(dashIdx + 1)..]
                    : dirName;

                string friendlyName = connectorToken;
                Dictionary<(int Width, int Height), List<int>> dtdRates = new();
                string edidPath = Path.Combine(dir, "edid");
                if (File.Exists(edidPath))
                {
                    byte[] edidBytes = File.ReadAllBytes(edidPath);
                    if (edidBytes.Length >= 128)
                    {
                        friendlyName = ParseEdidModelName(edidBytes) ?? connectorToken;
                        dtdRates = ParseEdidDetailedTimingRefreshRates(edidBytes);
                    }
                }

                var resRates = new Dictionary<(int Width, int Height), HashSet<int>>();
                foreach (var kvp in dtdRates)
                {
                    resRates[kvp.Key] = new HashSet<int>(kvp.Value);
                }

                int nativeW = 0, nativeH = 0;
                string modesPath = Path.Combine(dir, "modes");
                if (File.Exists(modesPath))
                {
                    foreach (string modeLine in File.ReadAllLines(modesPath))
                    {
                        string clean = modeLine.Trim();
                        if (clean.EndsWith("i", StringComparison.OrdinalIgnoreCase)) continue;
                        string[] parts = clean.Split('x');
                        if (parts.Length == 2 &&
                            int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int mw) &&
                            int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int mh) &&
                            mw >= 640 && mh >= 480)
                        {
                            if (nativeW == 0)
                            {
                                nativeW = mw;
                                nativeH = mh;
                            }
                            if (!resRates.TryGetValue((mw, mh), out HashSet<int>? set))
                            {
                                set = new HashSet<int> { 60 };
                                resRates[(mw, mh)] = set;
                            }
                        }
                    }
                }

                if (nativeW == 0 && dtdRates.Count > 0)
                {
                    var first = dtdRates.Keys.First();
                    nativeW = first.Width;
                    nativeH = first.Height;
                }
                if (nativeW == 0)
                {
                    nativeW = 1920;
                    nativeH = 1080;
                    resRates[(1920, 1080)] = new HashSet<int> { 60 };
                }

                bool isPrimary = rawMonitors.Count == 0;
                int maxHzAtNative = resRates.TryGetValue((nativeW, nativeH), out HashSet<int>? natSet) && natSet.Count > 0
                    ? natSet.Max()
                    : 60;

                var supportedResolutions = resRates
                    .OrderByDescending(r => r.Key.Width == nativeW && r.Key.Height == nativeH)
                    .ThenByDescending(r => (long)r.Key.Width * r.Key.Height)
                    .Select(r =>
                    {
                        bool isNat = r.Key.Width == nativeW && r.Key.Height == nativeH;
                        var sorted = r.Value.OrderByDescending(x => x).ToList();
                        return new MonitorResolutionOption(
                            r.Key.Width,
                            r.Key.Height,
                            sorted[0],
                            isNat,
                            FormatResolutionOptionLabel(r.Key.Width, r.Key.Height, isNat),
                            sorted);
                    })
                    .ToList();

                rawMonitors.Add(new MonitorDisplayInfo(
                    Index: rawMonitors.Count + 1,
                    DeviceName: NormalizeMonitorDeviceToken(connectorToken),
                    FriendlyName: friendlyName,
                    IsPrimary: isPrimary,
                    BoundsX: offsetX,
                    BoundsY: 0,
                    NativeWidth: nativeW,
                    NativeHeight: nativeH,
                    CurrentRefreshRateHz: maxHzAtNative,
                    MaxRefreshRateHz: maxHzAtNative,
                    SupportedResolutions: supportedResolutions));

                offsetX += nativeW;
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine($"[WILLOWMAKER-MONITORS:LinuxDRM] Notice reading {drmRoot}: {ex.Message}");
        }

        return rawMonitors;
    }

    private static IReadOnlyList<MonitorDisplayInfo> DetectAvailableMonitorsLinux()
    {
        try
        {
            string[] xrandrCandidates = ["/usr/bin/xrandr", "/bin/xrandr", "/usr/local/bin/xrandr"];
            string? xrandrBin = xrandrCandidates.FirstOrDefault(File.Exists);
            if (xrandrBin is not null)
            {
                int exit = LaunchConfigWriterService.RunMacOsTool(xrandrBin, ["--props"], out string stdout, out _, timeoutMs: 2_500);
                if (exit == 0 && !string.IsNullOrWhiteSpace(stdout))
                {
                    IReadOnlyList<MonitorDisplayInfo> parsed = ParseXrandrPropsMonitors(stdout);
                    if (parsed.Count > 0)
                    {
                        foreach (MonitorDisplayInfo mon in parsed)
                        {
                            System.Diagnostics.Trace.WriteLine(
                                $"[WILLOWMAKER-MONITORS:LinuxXRandR] #{mon.Index} token='{mon.NormalizedDeviceToken}' name='{mon.FriendlyName}' primary={mon.IsPrimary} native={mon.NativeWidth}x{mon.NativeHeight} maxHz={mon.MaxRefreshRateHz}");
                        }
                        return parsed;
                    }
                }
            }

            IReadOnlyList<MonitorDisplayInfo> drmMonitors = ParseLinuxSysfsDrmMonitors();
            if (drmMonitors.Count > 0)
            {
                return drmMonitors;
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine($"[WILLOWMAKER-MONITORS:Linux] Error detecting Linux displays: {ex.Message}");
        }

        return CreateFallbackMonitors();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MacOsCGRect64
    {
        public double X;
        public double Y;
        public double Width;
        public double Height;
    }

    private const string CoreGraphicsLib = "/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics";
    private const string CoreFoundationLib = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";

    [DllImport(CoreGraphicsLib)]
    private static extern int CGGetOnlineDisplayList(uint maxDisplays, [Out] uint[] onlineDisplays, out uint displayCount);

    [DllImport(CoreGraphicsLib)]
    private static extern int CGDisplayIsMain(uint display);

    [DllImport(CoreGraphicsLib)]
    private static extern MacOsCGRect64 CGDisplayBounds(uint display);

    [DllImport(CoreGraphicsLib)]
    private static extern IntPtr CGDisplayCopyDisplayMode(uint display);

    [DllImport(CoreGraphicsLib)]
    private static extern IntPtr CGDisplayCopyAllDisplayModes(uint display, IntPtr options);

    [DllImport(CoreGraphicsLib)]
    private static extern nuint CGDisplayModeGetWidth(IntPtr mode);

    [DllImport(CoreGraphicsLib)]
    private static extern nuint CGDisplayModeGetHeight(IntPtr mode);

    [DllImport(CoreGraphicsLib)]
    private static extern nuint CGDisplayModeGetPixelWidth(IntPtr mode);

    [DllImport(CoreGraphicsLib)]
    private static extern nuint CGDisplayModeGetPixelHeight(IntPtr mode);

    [DllImport(CoreGraphicsLib)]
    private static extern double CGDisplayModeGetRefreshRate(IntPtr mode);

    [DllImport(CoreFoundationLib)]
    private static extern nint CFArrayGetCount(IntPtr theArray);

    [DllImport(CoreFoundationLib)]
    private static extern IntPtr CFArrayGetValueAtIndex(IntPtr theArray, nint idx);

    [DllImport(CoreFoundationLib)]
    private static extern void CFRelease(IntPtr cf);

    /// <summary>
    ///     Extracts ordered display friendly names from macOS <c>system_profiler SPDisplaysDataType -json</c>.
    /// </summary>
    public static List<string> ParseMacOsSystemProfilerDisplaysJson(string? json)
    {
        var names = new List<string>();
        if (string.IsNullOrWhiteSpace(json))
        {
            return names;
        }

        try
        {
            using JsonDocument doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                doc.RootElement.TryGetProperty("SPDisplaysDataType", out JsonElement gpus) &&
                gpus.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement gpu in gpus.EnumerateArray())
                {
                    if (gpu.ValueKind == JsonValueKind.Object &&
                        gpu.TryGetProperty("spdisplays_ndrvs", out JsonElement displays) &&
                        displays.ValueKind == JsonValueKind.Array)
                    {
                        foreach (JsonElement disp in displays.EnumerateArray())
                        {
                            if (disp.ValueKind == JsonValueKind.Object &&
                                disp.TryGetProperty("_name", out JsonElement nameEl) &&
                                nameEl.ValueKind == JsonValueKind.String)
                            {
                                string? name = nameEl.GetString()?.Trim();
                                if (!string.IsNullOrWhiteSpace(name) &&
                                    !name.Equals("spdisplays_display_connector", StringComparison.OrdinalIgnoreCase))
                                {
                                    names.Add(name);
                                }
                            }
                        }
                    }
                }
            }
        }
        catch
        {
            // Ignore malformed JSON
        }

        return names;
    }

    private static IReadOnlyList<MonitorDisplayInfo> DetectAvailableMonitorsMacOs()
    {
        try
        {
            List<string> profilerNames = [];
            if (File.Exists("/usr/sbin/system_profiler"))
            {
                int spExit = LaunchConfigWriterService.RunMacOsTool(
                    "/usr/sbin/system_profiler",
                    ["SPDisplaysDataType", "-json"],
                    out string spJson,
                    out _,
                    timeoutMs: 3_500);
                if (spExit == 0 && !string.IsNullOrWhiteSpace(spJson))
                {
                    profilerNames = ParseMacOsSystemProfilerDisplaysJson(spJson);
                }
            }

            uint[] displayIds = new uint[16];
            if (CGGetOnlineDisplayList((uint)displayIds.Length, displayIds, out uint count) != 0 || count == 0)
            {
                return CreateFallbackMonitors();
            }

            var raw = new List<MonitorDisplayInfo>();
            for (int i = 0; i < count; i++)
            {
                uint did = displayIds[i];
                bool isMain = CGDisplayIsMain(did) != 0;
                MacOsCGRect64 bounds = CGDisplayBounds(did);

                int nativeW = bounds.Width > 0 ? (int)Math.Round(bounds.Width) : 1920;
                int nativeH = bounds.Height > 0 ? (int)Math.Round(bounds.Height) : 1080;
                int currentHz = 60;

                IntPtr curMode = CGDisplayCopyDisplayMode(did);
                if (curMode != IntPtr.Zero)
                {
                    try
                    {
                        int pw = (int)CGDisplayModeGetPixelWidth(curMode);
                        int ph = (int)CGDisplayModeGetPixelHeight(curMode);
                        int lw = (int)CGDisplayModeGetWidth(curMode);
                        int lh = (int)CGDisplayModeGetHeight(curMode);
                        if (pw >= 640 && ph >= 480)
                        {
                            nativeW = pw;
                            nativeH = ph;
                        }
                        else if (lw >= 640 && lh >= 480)
                        {
                            nativeW = lw;
                            nativeH = lh;
                        }

                        double r = CGDisplayModeGetRefreshRate(curMode);
                        if (r > 1.0) currentHz = (int)Math.Round(r);
                    }
                    finally
                    {
                        CFRelease(curMode);
                    }
                }

                var resRates = new Dictionary<(int Width, int Height), HashSet<int>>();
                IntPtr allModes = CGDisplayCopyAllDisplayModes(did, IntPtr.Zero);
                if (allModes != IntPtr.Zero)
                {
                    try
                    {
                        nint modeCount = CFArrayGetCount(allModes);
                        for (nint mIdx = 0; mIdx < modeCount; mIdx++)
                        {
                            IntPtr modeRef = CFArrayGetValueAtIndex(allModes, mIdx);
                            if (modeRef == IntPtr.Zero) continue;

                            int mw = (int)CGDisplayModeGetPixelWidth(modeRef);
                            int mh = (int)CGDisplayModeGetPixelHeight(modeRef);
                            if (mw < 640 || mh < 480)
                            {
                                mw = (int)CGDisplayModeGetWidth(modeRef);
                                mh = (int)CGDisplayModeGetHeight(modeRef);
                            }

                            if (mw >= 640 && mh >= 480)
                            {
                                double r = CGDisplayModeGetRefreshRate(modeRef);
                                int hz = r > 1.0 ? (int)Math.Round(r) : 60;
                                if (!resRates.TryGetValue((mw, mh), out HashSet<int>? set))
                                {
                                    set = new HashSet<int>();
                                    resRates[(mw, mh)] = set;
                                }
                                set.Add(hz);
                            }
                        }
                    }
                    finally
                    {
                        CFRelease(allModes);
                    }
                }

                if (!resRates.TryGetValue((nativeW, nativeH), out HashSet<int>? natSet))
                {
                    natSet = new HashSet<int>();
                    resRates[(nativeW, nativeH)] = natSet;
                }
                natSet.Add(currentHz);

                int maxHzAtNative = natSet.Max();
                var supportedRes = resRates
                    .OrderByDescending(r => r.Key.Width == nativeW && r.Key.Height == nativeH)
                    .ThenByDescending(r => (long)r.Key.Width * r.Key.Height)
                    .Select(r =>
                    {
                        bool isNat = r.Key.Width == nativeW && r.Key.Height == nativeH;
                        var sorted = r.Value.OrderByDescending(x => x).ToList();
                        return new MonitorResolutionOption(
                            r.Key.Width,
                            r.Key.Height,
                            sorted[0],
                            isNat,
                            FormatResolutionOptionLabel(r.Key.Width, r.Key.Height, isNat),
                            sorted);
                    })
                    .ToList();

                string friendly = i < profilerNames.Count && !string.IsNullOrWhiteSpace(profilerNames[i])
                    ? profilerNames[i]
                    : (isMain ? "Main Display" : $"External Display #{i + 1}");

                raw.Add(new MonitorDisplayInfo(
                    Index: 0,
                    DeviceName: string.Empty,
                    FriendlyName: friendly,
                    IsPrimary: isMain,
                    BoundsX: (int)Math.Round(bounds.X),
                    BoundsY: (int)Math.Round(bounds.Y),
                    NativeWidth: nativeW,
                    NativeHeight: nativeH,
                    CurrentRefreshRateHz: currentHz,
                    MaxRefreshRateHz: maxHzAtNative,
                    SupportedResolutions: supportedRes));
            }

            if (raw.Count == 0)
            {
                return CreateFallbackMonitors();
            }

            return raw
                .OrderByDescending(m => m.IsPrimary)
                .ThenBy(m => m.BoundsX)
                .ThenBy(m => m.BoundsY)
                .Select((m, idx) => m with
                {
                    Index = idx + 1,
                    DeviceName = $"DISPLAY{idx + 1}"
                })
                .ToList();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine($"[WILLOWMAKER-MONITORS:macOS] Error detecting macOS displays: {ex.Message}");
            return CreateFallbackMonitors();
        }
    }

    private static bool IsHexOnlyLine(string s)
    {
        if (s.Length < 16 || (s.Length % 2) != 0)
        {
            return false;
        }

        foreach (char c in s)
        {
            bool isHex = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
            if (!isHex) return false;
        }

        return true;
    }

    private static byte[]? TryDecodeHexBytes(string hex)
    {
        try
        {
            return Convert.FromHexString(hex);
        }
        catch
        {
            return null;
        }
    }

    public static string FormatResolutionOptionLabel(int width, int height, bool isNative)
    {
        string tag = (width, height) switch
        {
            (3840, 2160) => " (4K UHD)",
            (2560, 1440) => " (1440p QHD)",
            (1920, 1200) => " (WUXGA)",
            (1920, 1080) => " (1080p FHD)",
            (1600, 900)  => " (900p HD+)",
            (1440, 900)  => " (WXGA+)",
            (1280, 1024) => " (SXGA)",
            (1280, 720)  => " (720p HD)",
            (1024, 768)  => " (XGA)",
            (1024, 600)  => " (WSVGA)",
            _ => ""
        };
        string nativeTag = isNative ? " [Native]" : "";
        return $"{width}x{height}{tag}{nativeTag}";
    }

    public static (int Width, int Height) ParseResolutionString(string? resStr)
    {
        if (string.IsNullOrWhiteSpace(resStr)) return (1920, 1080);

        int spaceIdx = resStr.IndexOf(' ');
        string token = spaceIdx > 0 ? resStr[..spaceIdx] : resStr;
        string[] dims = token.Split('x', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (dims.Length == 2 && int.TryParse(dims[0], out int w) && int.TryParse(dims[1], out int h) && w > 0 && h > 0)
        {
            return (w, h);
        }

        return (1920, 1080);
    }

    private static readonly int[] Fallback60HzRates = [60];

    private static IReadOnlyList<MonitorDisplayInfo> CreateFallbackMonitors()
    {
        return new[]
        {
            new MonitorDisplayInfo(
                Index: 1,
                DeviceName: "DISPLAY1",
                FriendlyName: "Default Display",
                IsPrimary: true,
                BoundsX: 0,
                BoundsY: 0,
                NativeWidth: 1920,
                NativeHeight: 1080,
                CurrentRefreshRateHz: 60,
                MaxRefreshRateHz: 60,
                SupportedResolutions: new[]
                {
                    new MonitorResolutionOption(1920, 1080, 60, true, "1920x1080 (1080p FHD) [Native]", Fallback60HzRates),
                    new MonitorResolutionOption(2560, 1440, 60, false, "2560x1440 (1440p QHD)", Fallback60HzRates),
                    new MonitorResolutionOption(1280, 720, 60, false, "1280x720 (720p HD)", Fallback60HzRates)
                }
            )
        };
    }
}
