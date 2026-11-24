// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using WILLOWMAKER.Core.Services.Serialization;

namespace WILLOWMAKER.Core.Services;

public sealed record RuntimePrerequisiteResult(
    bool RequiresNativeD3D9Fallback,
    IReadOnlyList<string> MissingLinuxLibraries,
    IReadOnlyList<string> Warnings);

/// <summary>
/// Runtime &amp; compile-time policy governing standalone playtest exports so external testers never
/// trigger developer-only behaviors (destructive CDN deletions, parent folder renames, localhost port scans,
/// runtime PE binary patching, or remote-thread DLL injection).
/// </summary>
public sealed class PlaytestPolicyDto
{
    public bool IsPlaytest { get; set; } = true;
    public bool DisableCdnDeletions { get; set; } = true;
    public bool DisableFolderNormalizer { get; set; } = true;
    public bool DisableLocalClusterPolling { get; set; } = true;
    public bool EnforceStrictTlsValidation { get; set; } = true;
    public bool BinariesPrePatched { get; set; } = true;
    public bool PreferStandardProcessStart { get; set; } = true;
    public bool DisableDevWatchers { get; set; } = true;
    public bool UseStableReleaseStageCvars { get; set; } = true;

    public static PlaytestPolicyDto CreateDeveloperDefault() => new()
    {
        IsPlaytest = false,
        DisableCdnDeletions = false,
        DisableFolderNormalizer = false,
        DisableLocalClusterPolling = false,
        EnforceStrictTlsValidation = false,
        BinariesPrePatched = false,
        PreferStandardProcessStart = false,
        DisableDevWatchers = false,
        UseStableReleaseStageCvars = false
    };

    public static PlaytestPolicyDto CreatePlaytestDefault() => new();
}

/// <summary>
/// Build metadata stamped into exported playtest bundles (<c>build_manifest.json</c>).
/// </summary>
public sealed class BuildManifestDto
{
    public string BuildId { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public string GitCommit { get; set; } = string.Empty;
    public string Platform { get; set; } = string.Empty;
    public string BuiltAtUtc { get; set; } = string.Empty;
    public string Channel { get; set; } = "playtest";
    public Dictionary<string, string> ArchiveHashes { get; set; } = [];
}

public static class PlaytestPolicyService
{
    public const string PolicyFileName = "playtest_policy.json";
    public const string BuildManifestFileName = "build_manifest.json";

    public static PlaytestPolicyDto Resolve(string? candidateDirectory = null, Action<string>? logAction = null)
    {
        foreach (string policyPath in EnumerateCandidatePaths(PolicyFileName, candidateDirectory))
        {
            if (!File.Exists(policyPath))
            {
                continue;
            }

            try
            {
                string json = File.ReadAllText(policyPath);
                PlaytestPolicyDto? parsed = JsonSerializer.Deserialize(
                    json,
                    WillowmakerJsonSerializerContext.Default.PlaytestPolicyDto);
                if (parsed is not null)
                {
                    Trace.WriteLine($"[PlaytestPolicy] Loaded policy from '{policyPath}' (IsPlaytest={parsed.IsPlaytest}).");
                    logAction?.Invoke($"[PlaytestPolicy] Active policy loaded from '{policyPath}' (IsPlaytest={parsed.IsPlaytest}).");
                    return parsed;
                }
            }
            catch (Exception ex)
            {
                string warning = $"[PlaytestPolicy] Failed reading '{policyPath}', falling back to safe playtest defaults: {ex.Message}";
                Trace.WriteLine(warning);
                logAction?.Invoke(warning);
                return PlaytestPolicyDto.CreatePlaytestDefault();
            }
        }

        string? envOverride = Environment.GetEnvironmentVariable("KONGOR_PLAYTEST_MODE");
        if (!string.IsNullOrWhiteSpace(envOverride))
        {
            if (string.Equals(envOverride.Trim(), "1", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(envOverride.Trim(), "true", StringComparison.OrdinalIgnoreCase))
            {
                logAction?.Invoke("[PlaytestPolicy] Activated via KONGOR_PLAYTEST_MODE environment override.");
                return PlaytestPolicyDto.CreatePlaytestDefault();
            }

            if (string.Equals(envOverride.Trim(), "0", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(envOverride.Trim(), "false", StringComparison.OrdinalIgnoreCase))
            {
                return PlaytestPolicyDto.CreateDeveloperDefault();
            }
        }

#if PLAYTEST
        logAction?.Invoke("[PlaytestPolicy] Activated via compile-time PLAYTEST profile.");
        return PlaytestPolicyDto.CreatePlaytestDefault();
#else
        return PlaytestPolicyDto.CreateDeveloperDefault();
#endif
    }

    public static BuildManifestDto? TryReadBuildManifest(string? candidateDirectory = null, Action<string>? logAction = null)
    {
        foreach (string manifestPath in EnumerateCandidatePaths(BuildManifestFileName, candidateDirectory))
        {
            if (!File.Exists(manifestPath))
            {
                continue;
            }

            try
            {
                string json = File.ReadAllText(manifestPath);
                BuildManifestDto? manifest = JsonSerializer.Deserialize(
                    json,
                    WillowmakerJsonSerializerContext.Default.BuildManifestDto);
                if (manifest is not null && !string.IsNullOrWhiteSpace(manifest.BuildId))
                {
                    logAction?.Invoke($"[BuildManifest] Loaded build '{manifest.BuildId}' ({manifest.Platform}) from '{manifestPath}'.");
                    return manifest;
                }
            }
            catch (Exception ex)
            {
                string warning = $"[BuildManifest] Could not parse '{manifestPath}': {ex.Message}";
                Trace.WriteLine(warning);
                logAction?.Invoke(warning);
            }
        }

        return null;
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int PfnVkCreateInstance(IntPtr pCreateInfo, IntPtr pAllocator, out IntPtr pInstance);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate void PfnVkDestroyInstance(IntPtr instance, IntPtr pAllocator);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int PfnVkEnumeratePhysicalDevices(IntPtr instance, ref uint pPhysicalDeviceCount, IntPtr pPhysicalDevices);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate void PfnVkGetPhysicalDeviceProperties(IntPtr physicalDevice, IntPtr pProperties);

    [StructLayout(LayoutKind.Sequential)]
    private struct VkApplicationInfoNative
    {
        public int sType; // VK_STRUCTURE_TYPE_APPLICATION_INFO = 0
        public IntPtr pNext;
        public IntPtr pApplicationName;
        public uint applicationVersion;
        public IntPtr pEngineName;
        public uint engineVersion;
        public uint apiVersion;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct VkInstanceCreateInfoNative
    {
        public int sType; // VK_STRUCTURE_TYPE_INSTANCE_CREATE_INFO = 1
        public IntPtr pNext;
        public uint flags;
        public IntPtr pApplicationInfo;
        public uint enabledLayerCount;
        public IntPtr ppEnabledLayerNames;
        public uint enabledExtensionCount;
        public IntPtr ppEnabledExtensionNames;
    }

    public static RuntimePrerequisiteResult CheckRuntimePrerequisites(
        string clientRoot,
        Action<string>? logAction = null,
        string? lddOutputOverride = null,
        IReadOnlyList<(string DeviceName, uint ApiVersion)>? vulkanAdaptersOverride = null,
        IReadOnlyList<string>? windowsDisplayAdaptersOverride = null,
        bool? isLinuxOverride = null,
        bool? isWindowsOverride = null)
    {
        bool isLinux = isLinuxOverride ?? OperatingSystem.IsLinux();
        bool isWindows = isWindowsOverride ?? (!isLinux && OperatingSystem.IsWindows());

        List<string> missingLinuxLibs = [];
        List<string> warnings = [];
        bool requiresNativeD3D9Fallback = false;

        if (isLinux)
        {
            try
            {
                string linuxRoot = ResolveLinuxClientRoot(clientRoot);
                string combinedLdd = lddOutputOverride ?? RunLinuxLddPreflight(linuxRoot);
                if (!string.IsNullOrWhiteSpace(combinedLdd))
                {
                    foreach (Match match in Regex.Matches(combinedLdd, @"(?m)^\s*([^\s=>]+)\s*=>\s*not found\b"))
                    {
                        string missingLib = match.Groups[1].Value.Trim();
                        if (!string.IsNullOrWhiteSpace(missingLib) &&
                            !missingLinuxLibs.Contains(missingLib, StringComparer.Ordinal))
                        {
                            missingLinuxLibs.Add(missingLib);
                        }
                    }

                    foreach (string lib in missingLinuxLibs)
                    {
                        string pkgHint = GetLinuxPackageInstallHint(lib);
                        string msg = $"[PREREQ_ERR] Missing Linux shared library '{lib}' required by hon-x86_64 / vid_gl2-x86_64.so. Fix: {pkgHint}";
                        warnings.Add(msg);
                        logAction?.Invoke(msg);
                    }

                    if (missingLinuxLibs.Count == 0)
                    {
                        logAction?.Invoke("[PREREQ_CHECK] Verified Linux ELF dynamic library closure (0 missing libraries via ldd).");
                    }
                }
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"[PlaytestPolicy] Linux ldd prerequisite check notice: {ex.Message}");
                logAction?.Invoke($"[PREREQ_CHECK] Linux runtime prerequisite probe skipped: {ex.Message}");
            }

            return new RuntimePrerequisiteResult(false, missingLinuxLibs, warnings);
        }

        if (!isWindows)
        {
            return new RuntimePrerequisiteResult(false, missingLinuxLibs, warnings);
        }

        try
        {
            bool usingOverrides = vulkanAdaptersOverride is not null || windowsDisplayAdaptersOverride is not null;
            string systemDir = Environment.SystemDirectory;
            bool hasVcRuntime = usingOverrides ||
                                File.Exists(Path.Combine(systemDir, "vcruntime140.dll")) ||
                                File.Exists(Path.Combine(clientRoot, "vcruntime140.dll"));
            bool hasVulkanLoader = vulkanAdaptersOverride is not null ||
                                   File.Exists(Path.Combine(systemDir, "vulkan-1.dll")) ||
                                   File.Exists(Path.Combine(clientRoot, "vulkan-1.dll"));

            if (!hasVcRuntime)
            {
                string vcWarn =
                    "[PREREQ_WARN] Missing Microsoft Visual C++ x64 Runtime (vcruntime140.dll). Install from https://aka.ms/vs/17/release/vc_redist.x64.exe if the game client fails to start.";
                warnings.Add(vcWarn);
                logAction?.Invoke(vcWarn);
            }

            if (!hasVulkanLoader)
            {
                requiresNativeD3D9Fallback = true;
                string vkWarn =
                    "[PREREQ_WARN] Missing Vulkan 1.3 Runtime Loader (vulkan-1.dll). Automatically enabling Compatibility Native D3D9 fallback (HON_FORCE_NATIVE_D3D9=1). Update your NVIDIA/AMD/Intel GPU driver to enable Vulkan 1.3 DXVK.";
                warnings.Add(vkWarn);
                logAction?.Invoke(vkWarn);
                return new RuntimePrerequisiteResult(requiresNativeD3D9Fallback, missingLinuxLibs, warnings);
            }

            IReadOnlyList<(string DeviceName, uint ApiVersion)>? vkAdapters =
                vulkanAdaptersOverride ?? TryProbeWindowsVulkanAdapters(clientRoot);

            if (vkAdapters is not null)
            {
                bool hasHardwareVulkan13 = vkAdapters.Any(a =>
                    !IsSoftwareVulkanAdapter(a.DeviceName) &&
                    SupportsVulkan13OrNewer(a.ApiVersion));

                if (!hasHardwareVulkan13)
                {
                    requiresNativeD3D9Fallback = true;
                    IReadOnlyList<string> winDisplayAdapters = windowsDisplayAdaptersOverride ?? EnumerateWindowsDisplayAdapterNames();
                    string vkListText = vkAdapters.Count == 0
                        ? "none"
                        : string.Join(", ", vkAdapters.Select(a => $"{a.DeviceName} (Vulkan {FormatVulkanVersion(a.ApiVersion)})"));

                    string? missingDiscreteGpu = FindDiscreteGpuMissingFromVulkan(winDisplayAdapters, vkAdapters.Select(a => a.DeviceName));
                    string fallbackWarn = !string.IsNullOrWhiteSpace(missingDiscreteGpu)
                        ? $"[PREREQ_WARN] Discrete GPU '{missingDiscreteGpu}' is registered in Windows Display Adapters, but Vulkan only enumerated [{vkListText}] without Vulkan 1.3 support (pending Windows Restart after driver update or Optimus iGPU binding). Automatically enabling Compatibility Native D3D9 fallback (HON_FORCE_NATIVE_D3D9=1). Fix: Reboot Windows or bind hon_x64.exe to High-Performance GPU in Windows Graphics Settings."
                        : $"[PREREQ_WARN] Enumerated GPU adapter(s) [{vkListText}] do not support Vulkan 1.3 required by DXVK v3.0.2. Automatically enabling Compatibility Native D3D9 fallback (HON_FORCE_NATIVE_D3D9=1). Fix: Update your NVIDIA/AMD/Intel graphics driver if your hardware supports Vulkan 1.3.";

                    warnings.Add(fallbackWarn);
                    logAction?.Invoke(fallbackWarn);
                }
                else if (hasVcRuntime)
                {
                    logAction?.Invoke("[PREREQ_CHECK] Verified Windows runtime prerequisites (vcruntime140.dll + Vulkan 1.3 hardware adapter ready).");
                }
            }
            else if (hasVcRuntime)
            {
                logAction?.Invoke("[PREREQ_CHECK] Verified Windows runtime prerequisites (vcruntime140.dll + vulkan-1.dll present).");
            }
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[PlaytestPolicy] Runtime prerequisite check warning: {ex.Message}");
            logAction?.Invoke($"[PREREQ_CHECK] Runtime prerequisite probe skipped: {ex.Message}");
        }

        return new RuntimePrerequisiteResult(requiresNativeD3D9Fallback, missingLinuxLibs, warnings);
    }

    public static uint MakeVulkanApiVersion(uint major, uint minor, uint patch = 0) =>
        (major << 22) | (minor << 12) | patch;

    public static bool SupportsVulkan13OrNewer(uint apiVersion)
    {
        uint major = (apiVersion >> 22) & 0x7FU;
        uint minor = (apiVersion >> 12) & 0x3FFU;
        return major > 1 || (major == 1 && minor >= 3);
    }

    public static string FormatVulkanVersion(uint apiVersion)
    {
        uint major = (apiVersion >> 22) & 0x7FU;
        uint minor = (apiVersion >> 12) & 0x3FFU;
        uint patch = apiVersion & 0xFFFU;
        return $"{major}.{minor}.{patch}";
    }

    public static string? FindDiscreteGpuMissingFromVulkan(
        IEnumerable<string> windowsDisplayAdapters,
        IEnumerable<string> vulkanAdapterNames)
    {
        List<string> vkNames = vulkanAdapterNames.Where(n => !string.IsNullOrWhiteSpace(n)).ToList();
        foreach (string winGpu in windowsDisplayAdapters)
        {
            if (string.IsNullOrWhiteSpace(winGpu) || !IsDiscreteNvidiaOrAmdAdapterName(winGpu))
            {
                continue;
            }

            bool presentInVulkan = vkNames.Any(vk =>
                vk.Contains(winGpu, StringComparison.OrdinalIgnoreCase) ||
                winGpu.Contains(vk, StringComparison.OrdinalIgnoreCase) ||
                (winGpu.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase) && vk.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase)) ||
                (winGpu.Contains("GeForce", StringComparison.OrdinalIgnoreCase) && vk.Contains("GeForce", StringComparison.OrdinalIgnoreCase)) ||
                (winGpu.Contains("Radeon", StringComparison.OrdinalIgnoreCase) && vk.Contains("Radeon", StringComparison.OrdinalIgnoreCase)));

            if (!presentInVulkan)
            {
                return winGpu.Trim();
            }
        }

        return null;
    }

    public static bool IsDiscreteNvidiaOrAmdAdapterName(string adapterName)
    {
        if (string.IsNullOrWhiteSpace(adapterName))
        {
            return false;
        }

        if (adapterName.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase) ||
            adapterName.Contains("GeForce", StringComparison.OrdinalIgnoreCase) ||
            adapterName.Contains("RTX ", StringComparison.OrdinalIgnoreCase) ||
            adapterName.Contains("GTX ", StringComparison.OrdinalIgnoreCase) ||
            adapterName.Contains("Quadro", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if ((adapterName.Contains("Radeon", StringComparison.OrdinalIgnoreCase) ||
             adapterName.Contains("AMD ", StringComparison.OrdinalIgnoreCase)) &&
            !adapterName.Contains("TM) Graphics", StringComparison.OrdinalIgnoreCase) &&
            !adapterName.Contains("Radeon(TM) Graphics", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return false;
    }

    public static IReadOnlyList<string> EnumerateWindowsDisplayAdapterNames()
    {
        List<string> adapters = [];
        if (!OperatingSystem.IsWindows())
        {
            return adapters;
        }

        try
        {
            const string displayClassKey = @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}";
            using RegistryKey? baseKey = Registry.LocalMachine.OpenSubKey(displayClassKey, writable: false);
            if (baseKey is null)
            {
                return adapters;
            }

            foreach (string subKeyName in baseKey.GetSubKeyNames())
            {
                if (subKeyName.Length != 4 || !subKeyName.All(char.IsDigit))
                {
                    continue;
                }

                using RegistryKey? subKey = baseKey.OpenSubKey(subKeyName, writable: false);
                string? desc = subKey?.GetValue("DriverDesc") as string;
                if (!string.IsNullOrWhiteSpace(desc) && !adapters.Contains(desc, StringComparer.OrdinalIgnoreCase))
                {
                    adapters.Add(desc.Trim());
                }
            }
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[PlaytestPolicy] Windows Display Adapter registry enumeration notice: {ex.Message}");
        }

        return adapters;
    }

    public static string GetLinuxPackageInstallHint(string missingLib)
    {
        string lower = missingLib.ToLowerInvariant();
        if (lower.StartsWith("libxml2.so", StringComparison.Ordinal))
        {
            return "sudo apt install libxml2 | sudo dnf install libxml2 | sudo pacman -S libxml2-legacy | sudo emerge dev-libs/libxml2 | sudo xbps-install -S libxml2";
        }
        if (lower.StartsWith("libglu.so", StringComparison.Ordinal))
        {
            return "sudo apt install libglu1-mesa | sudo dnf install mesa-libGLU | sudo pacman -S glu | sudo emerge media-libs/glu | sudo xbps-install -S glu";
        }
        if (lower.StartsWith("libgl.so", StringComparison.Ordinal))
        {
            return "sudo apt install libgl1 | sudo dnf install mesa-libGL | sudo pacman -S libglvnd | sudo emerge media-libs/mesa | sudo xbps-install -S libglvnd";
        }
        if (lower.StartsWith("libasound.so", StringComparison.Ordinal))
        {
            return "sudo apt install libasound2 | sudo dnf install alsa-lib | sudo pacman -S alsa-lib | sudo emerge media-libs/alsa-lib | sudo xbps-install -S alsa-lib";
        }
        if (lower.StartsWith("libncurses.so", StringComparison.Ordinal) || lower.StartsWith("libtinfo.so", StringComparison.Ordinal))
        {
            return "sudo apt install libncurses5 | sudo dnf install ncurses-compat-libs | sudo pacman -S ncurses5-compat-libs | sudo emerge sys-libs/ncurses-compat | sudo xbps-install -S ncurses-libs";
        }
        if (lower.StartsWith("libxrandr.so", StringComparison.Ordinal) || lower.StartsWith("libx11.so", StringComparison.Ordinal) || lower.StartsWith("libxi.so", StringComparison.Ordinal))
        {
            return "sudo apt install libx11-6 libxrandr2 libxi6 | sudo dnf install libX11 libXrandr libXi | sudo pacman -S libx11 libxrandr libxi";
        }

        return $"Install the 64-bit x86_64 system package providing '{missingLib}' via your distro package manager (apt / dnf / pacman / emerge / xbps-install).";
    }

    private static bool IsSoftwareVulkanAdapter(string name) =>
        name.Contains("llvmpipe", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("lavapipe", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("Microsoft Direct3D12", StringComparison.OrdinalIgnoreCase);

    private static string ResolveLinuxClientRoot(string clientRoot)
    {
        if (File.Exists(Path.Combine(clientRoot, "hon-x86_64")))
        {
            return clientRoot;
        }

        string sub = Path.Combine(clientRoot, "linux-x64");
        return Directory.Exists(sub) ? sub : clientRoot;
    }

    private static string RunLinuxLddPreflight(string linuxRoot)
    {
        string lddBin = File.Exists("/usr/bin/ldd") ? "/usr/bin/ldd" : "/bin/ldd";
        if (!File.Exists(lddBin))
        {
            return string.Empty;
        }

        string[] targets =
        [
            Path.Combine(linuxRoot, "hon-x86_64"),
            Path.Combine(linuxRoot, "vid_gl2-x86_64.so")
        ];

        string ldLibraryPath = $"{Path.Combine(linuxRoot, "libs-runtime")}:{linuxRoot}";
        StringBuilder sb = new();

        foreach (string target in targets)
        {
            if (!File.Exists(target))
            {
                continue;
            }

            ProcessStartInfo psi = new(lddBin)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            psi.Environment["LC_ALL"] = "C";
            psi.Environment["LANG"] = "C";
            psi.Environment["LD_LIBRARY_PATH"] = ldLibraryPath;
            psi.ArgumentList.Add(target);

            using Process? proc = Process.Start(psi);
            if (proc is null)
            {
                continue;
            }

            string output = proc.StandardOutput.ReadToEnd();
            if (!proc.WaitForExit(2_000))
            {
                try
                {
                    proc.Kill(entireProcessTree: true);
                }
                catch (Exception killEx)
                {
                    Trace.WriteLine($"[PlaytestPolicy] ldd timeout kill notice: {killEx.Message}");
                }
            }

            if (!string.IsNullOrWhiteSpace(output))
            {
                sb.AppendLine(output);
            }
        }

        return sb.ToString();
    }

    private static IReadOnlyList<(string DeviceName, uint ApiVersion)>? TryProbeWindowsVulkanAdapters(string clientRoot)
    {
        IntPtr vkLib = IntPtr.Zero;
        IntPtr instance = IntPtr.Zero;
        IntPtr appInfoPtr = IntPtr.Zero;
        IntPtr createInfoPtr = IntPtr.Zero;
        IntPtr devicesBuf = IntPtr.Zero;
        IntPtr propsBuf = IntPtr.Zero;

        try
        {
            string localVulkan = Path.Combine(clientRoot, "vulkan-1.dll");
            if (File.Exists(localVulkan))
            {
                NativeLibrary.TryLoad(localVulkan, out vkLib);
            }

            if (vkLib == IntPtr.Zero && !NativeLibrary.TryLoad("vulkan-1.dll", out vkLib))
            {
                return null;
            }

            if (!NativeLibrary.TryGetExport(vkLib, "vkCreateInstance", out IntPtr createPtr) ||
                !NativeLibrary.TryGetExport(vkLib, "vkDestroyInstance", out IntPtr destroyPtr) ||
                !NativeLibrary.TryGetExport(vkLib, "vkEnumeratePhysicalDevices", out IntPtr enumPtr) ||
                !NativeLibrary.TryGetExport(vkLib, "vkGetPhysicalDeviceProperties", out IntPtr propsPtr))
            {
                return null;
            }

            PfnVkCreateInstance vkCreateInstance = Marshal.GetDelegateForFunctionPointer<PfnVkCreateInstance>(createPtr);
            PfnVkDestroyInstance vkDestroyInstance = Marshal.GetDelegateForFunctionPointer<PfnVkDestroyInstance>(destroyPtr);
            PfnVkEnumeratePhysicalDevices vkEnumeratePhysicalDevices = Marshal.GetDelegateForFunctionPointer<PfnVkEnumeratePhysicalDevices>(enumPtr);
            PfnVkGetPhysicalDeviceProperties vkGetPhysicalDeviceProperties = Marshal.GetDelegateForFunctionPointer<PfnVkGetPhysicalDeviceProperties>(propsPtr);

            VkApplicationInfoNative appInfo = new()
            {
                sType = 0, // VK_STRUCTURE_TYPE_APPLICATION_INFO
                apiVersion = MakeVulkanApiVersion(1, 0, 0)
            };
            appInfoPtr = Marshal.AllocHGlobal(Marshal.SizeOf<VkApplicationInfoNative>());
            Marshal.StructureToPtr(appInfo, appInfoPtr, fDeleteOld: false);

            VkInstanceCreateInfoNative createInfo = new()
            {
                sType = 1, // VK_STRUCTURE_TYPE_INSTANCE_CREATE_INFO
                pApplicationInfo = appInfoPtr
            };
            createInfoPtr = Marshal.AllocHGlobal(Marshal.SizeOf<VkInstanceCreateInfoNative>());
            Marshal.StructureToPtr(createInfo, createInfoPtr, fDeleteOld: false);

            int createRes = vkCreateInstance(createInfoPtr, IntPtr.Zero, out instance);
            if (createRes != 0 || instance == IntPtr.Zero)
            {
                return [];
            }

            try
            {
                uint count = 0;
                if (vkEnumeratePhysicalDevices(instance, ref count, IntPtr.Zero) != 0 || count == 0)
                {
                    return [];
                }

                count = Math.Min(count, 16U);
                devicesBuf = Marshal.AllocHGlobal((int)(count * (uint)IntPtr.Size));
                if (vkEnumeratePhysicalDevices(instance, ref count, devicesBuf) != 0 || count == 0)
                {
                    return [];
                }

                propsBuf = Marshal.AllocHGlobal(1024);
                List<(string DeviceName, uint ApiVersion)> results = new((int)count);
                byte[] nameBytes = new byte[256];

                for (int i = 0; i < count; i++)
                {
                    IntPtr physDev = Marshal.ReadIntPtr(devicesBuf, i * IntPtr.Size);
                    if (physDev == IntPtr.Zero)
                    {
                        continue;
                    }

                    vkGetPhysicalDeviceProperties(physDev, propsBuf);
                    uint apiVer = (uint)Marshal.ReadInt32(propsBuf, 0);
                    Marshal.Copy(IntPtr.Add(propsBuf, 20), nameBytes, 0, 256);
                    int nulIdx = Array.IndexOf(nameBytes, (byte)0);
                    string devName = Encoding.UTF8.GetString(nameBytes, 0, nulIdx >= 0 ? nulIdx : 256).Trim();
                    results.Add((devName, apiVer));
                }

                return results;
            }
            finally
            {
                vkDestroyInstance(instance, IntPtr.Zero);
            }
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[PlaytestPolicy] Vulkan adapter probe notice: {ex.Message}");
            return null;
        }
        finally
        {
            if (propsBuf != IntPtr.Zero) Marshal.FreeHGlobal(propsBuf);
            if (devicesBuf != IntPtr.Zero) Marshal.FreeHGlobal(devicesBuf);
            if (createInfoPtr != IntPtr.Zero) Marshal.FreeHGlobal(createInfoPtr);
            if (appInfoPtr != IntPtr.Zero) Marshal.FreeHGlobal(appInfoPtr);
            if (vkLib != IntPtr.Zero) NativeLibrary.Free(vkLib);
        }
    }

    private static IEnumerable<string> EnumerateCandidatePaths(string fileName, string? candidateDirectory)
    {
        HashSet<string> yielded = new(StringComparer.OrdinalIgnoreCase);

        void CollectDirectoryChain(string? dir, int maxParents)
        {
            if (string.IsNullOrWhiteSpace(dir))
            {
                return;
            }

            string? current = dir;
            for (int depth = 0; depth <= maxParents && !string.IsNullOrWhiteSpace(current); depth++)
            {
                string direct = Path.Combine(current, fileName);
                yielded.Add(direct);

                string nestedClient = Path.Combine(current, "games", "client", fileName);
                yielded.Add(nestedClient);

                DirectoryInfo? parent = Directory.GetParent(current);
                if (parent is null || string.Equals(parent.FullName, current, StringComparison.OrdinalIgnoreCase))
                {
                    break;
                }

                current = parent.FullName;
            }
        }

        if (!string.IsNullOrWhiteSpace(candidateDirectory))
        {
            CollectDirectoryChain(candidateDirectory, maxParents: 2);
        }

        CollectDirectoryChain(AppContext.BaseDirectory, maxParents: 1);
        CollectDirectoryChain(Environment.CurrentDirectory, maxParents: 1);

        return yielded;
    }
}
