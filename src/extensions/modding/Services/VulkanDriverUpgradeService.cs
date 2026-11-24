// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ============================================================================
// File: VulkanDriverUpgradeService.cs
// System: WILLOWMAKER Core Services - Client Vulkan & DXVK Driver Upgrader
// Policy: Strict Explicit Typing (NO 'var')
// ============================================================================

using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;

namespace WILLOWMAKER.Core.Services;

/// <summary>
///     Comprehensive diagnostic report detailing the graphics driver, Vulkan runtime,
///     and binary patch state of a game client installation.
/// </summary>
public sealed record ClientGraphicsDriverReport(
    bool HasSystemVulkanLoader,
    bool HasDxvkD3D9,
    bool HasDotLocalRedirection,
    bool IsVidDriverPatched,
    bool IsK2EnginePatched,
    bool HasDxvkConfig,
    bool HasBaseShaderCacheSeeded,
    bool HasCaptureLayerDeployed,
    bool RequiresUpgrade,
    string StatusSummary);

/// <summary>
///     Result of an upgrade operation to Vulkan 1.3 / DXVK.
/// </summary>
public sealed record VulkanUpgradeResult(
    bool Success,
    string Message,
    ClientGraphicsDriverReport Report);

/// <summary>
///     Service responsible for verifying client graphics driver health and performing
///     automated in-place upgrades to Vulkan 1.3, DXVK, 1000 FPS engine patches,
///     display mode bypass, shader cache seeding, and zero-copy replay capture layer deployment.
/// </summary>
public static class VulkanDriverUpgradeService
{
    private const long MinimumDxvkSize = 1_000_000; // DXVK d3d9.dll is ~7.2 MB, native D3D9 is not

    /// <summary>
    ///     Determines whether the given directory represents a dedicated game server installation.
    /// </summary>
    public static bool IsServerDirectory(string gameDirectory)
    {
        if (string.IsNullOrWhiteSpace(gameDirectory))
        {
            return false;
        }

        string normalized = gameDirectory.Replace('\\', '/').TrimEnd('/');
        string dirName = Path.GetFileName(normalized);
        if (dirName.Equals("server", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (normalized.EndsWith("/games/server", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("/games/server/", StringComparison.OrdinalIgnoreCase) ||
            normalized.EndsWith("/server/windows-x64", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (File.Exists(Path.Combine(gameDirectory, "COMPEL.exe")) ||
            File.Exists(Path.Combine(gameDirectory, "COMPEL.dll")) ||
            File.Exists(Path.Combine(gameDirectory, "windows-x64", "COMPEL.exe")) ||
            File.Exists(Path.Combine(gameDirectory, "windows-x64", "COMPEL.dll")))
        {
            return true;
        }

        return false;
    }

    /// <summary>
    ///     Determines whether the given directory represents a non-Windows (macOS or Linux) client installation.
    /// </summary>
    public static bool IsPosixClientDirectory(string gameDirectory)
    {
        if (!OperatingSystem.IsWindows())
        {
            return true;
        }

        if (string.IsNullOrWhiteSpace(gameDirectory))
        {
            return false;
        }

        string normalized = gameDirectory.Replace('\\', '/').TrimEnd('/');
        if (normalized.EndsWith("/macos-x64", StringComparison.OrdinalIgnoreCase) ||
            normalized.EndsWith("/linux-x64", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        bool hasWinExe = File.Exists(Path.Combine(gameDirectory, "hon_x64.exe")) ||
                         File.Exists(Path.Combine(gameDirectory, "windows-x64", "hon_x64.exe"));
        bool hasPosixBin = File.Exists(Path.Combine(gameDirectory, "HoN64")) ||
                           File.Exists(Path.Combine(gameDirectory, "libk2.dylib")) ||
                           File.Exists(Path.Combine(gameDirectory, "hon-x86_64"));
        return !hasWinExe && hasPosixBin;
    }

    /// <summary>
    ///     Inspects the specified game client or dedicated server directory and evaluates its Vulkan readiness.
    /// </summary>
    public static ClientGraphicsDriverReport InspectClient(string gameDirectory, bool? isServer = null)
    {
        bool server = isServer ?? IsServerDirectory(gameDirectory);
        if (IsPosixClientDirectory(gameDirectory))
        {
            string normalizedDir = (gameDirectory ?? string.Empty).Replace('\\', '/').TrimEnd('/');
            bool isMacTarget = OperatingSystem.IsMacOS() ||
                               normalizedDir.EndsWith("/macos-x64", StringComparison.OrdinalIgnoreCase) ||
                               (!string.IsNullOrWhiteSpace(gameDirectory) && File.Exists(Path.Combine(gameDirectory, "HoN64")));
            return new ClientGraphicsDriverReport(
                HasSystemVulkanLoader: true,
                HasDxvkD3D9: true,
                HasDotLocalRedirection: true,
                IsVidDriverPatched: true,
                IsK2EnginePatched: true,
                HasDxvkConfig: true,
                HasBaseShaderCacheSeeded: true,
                HasCaptureLayerDeployed: true,
                RequiresUpgrade: false,
                StatusSummary: isMacTarget
                    ? "macOS Native OpenGL / Metal Engine Active (vid_gl2)"
                    : "Linux Native OpenGL Engine Active (vid_gl2 + glthread)");
        }

        bool hasSystemVulkan = CheckSystemVulkanLoader();
        bool hasDxvk = CheckDxvkD3D9(gameDirectory);
        bool hasDotLocal = CheckDotLocalRedirection(gameDirectory);
        bool isVidPatched = CheckVidDriverPatched(gameDirectory);
        bool isK2Patched = CheckK2EnginePatched(gameDirectory);
        bool hasDxvkConfig = CheckDxvkConfig(gameDirectory);

        RenderModernizationService renderService = new(NullLogger<RenderModernizationService>.Instance);
        ShaderCacheStats cacheStats = renderService.GetShaderCacheStats();
        bool hasShadersSeeded = cacheStats.HasBaseCacheSeeded;

        bool hasCaptureLayer = server || CheckCaptureLayerDeployed(gameDirectory);

        bool requiresUpgrade = !hasDxvk ||
                               !hasDotLocal ||
                               !isVidPatched ||
                               !isK2Patched ||
                               !hasDxvkConfig ||
                               !hasShadersSeeded;

        string summary;
        if (!hasSystemVulkan)
        {
            summary = "System Vulkan Loader Missing (Update GPU Driver / VulkanRT)";
        }
        else if (requiresUpgrade)
        {
            summary = server
                ? "Dedicated Server Direct3D 9 Baseline (Vulkan Upgrade Available)"
                : "Legacy Direct3D 9 Detected (Vulkan Upgrade Available)";
        }
        else
        {
            summary = server
                ? "Dedicated Server Vulkan 1.3 Active (Headless / IPC Optimized)"
                : "Vulkan 1.3 Active (1000 FPS Uncapped)";
        }

        return new ClientGraphicsDriverReport(
            HasSystemVulkanLoader: hasSystemVulkan,
            HasDxvkD3D9: hasDxvk,
            HasDotLocalRedirection: hasDotLocal,
            IsVidDriverPatched: isVidPatched,
            IsK2EnginePatched: isK2Patched,
            HasDxvkConfig: hasDxvkConfig,
            HasBaseShaderCacheSeeded: hasShadersSeeded,
            HasCaptureLayerDeployed: hasCaptureLayer,
            RequiresUpgrade: requiresUpgrade,
            StatusSummary: summary);
    }

    /// <summary>
    ///     Performs a comprehensive automated upgrade of the target client or server directory to Vulkan 1.3 / DXVK.
    /// </summary>
    public static async Task<VulkanUpgradeResult> UpgradeToVulkanAsync(
        string gameDirectory,
        RayTracingPreset preset = RayTracingPreset.Competitive,
        Action<string>? logAction = null,
        bool? isServer = null)
    {
        return await Task.Run(() =>
        {
            bool server = isServer ?? IsServerDirectory(gameDirectory);
            string targetType = server ? "dedicated server" : "game client";
            logAction?.Invoke($"[VULKAN_UPGRADE] Initiating Vulkan 1.3 modernization for {targetType}: '{gameDirectory}'...");

            if (!Directory.Exists(gameDirectory))
            {
                string errMsg = $"Target directory does not exist: '{gameDirectory}'";
                logAction?.Invoke($"[VULKAN_UPGRADE] ERROR: {errMsg}");
                return new VulkanUpgradeResult(false, errMsg, InspectClient(gameDirectory, server));
            }

            if (IsPosixClientDirectory(gameDirectory))
            {
                logAction?.Invoke("[VULKAN_UPGRADE] POSIX client target detected; skipping Windows PE DXVK (d3d9.dll / hon_x64.exe.local) deployment.");
                EnginePatchService.EnsureEnginePatched(gameDirectory, logAction);
                ClientGraphicsDriverReport posixReport = InspectClient(gameDirectory, server);
                return new VulkanUpgradeResult(true, posixReport.StatusSummary, posixReport);
            }

            // Step 1: Check System Vulkan Loader
            bool hasSystemVulkan = CheckSystemVulkanLoader();
            if (!hasSystemVulkan)
            {
                logAction?.Invoke(
                    "[VULKAN_UPGRADE] WARNING: System Vulkan loader (vulkan-1.dll) was not found in System32 or PATH. " +
                    "Please install modern NVIDIA/AMD/Intel GPU drivers or the LunarG Vulkan Runtime (https://vulkan.lunarg.com/sdk/home).");
            }
            else
            {
                logAction?.Invoke("[VULKAN_UPGRADE] System Vulkan 1.3+ Loader verified.");
            }

            // Step 2: Ensure hon_x64.exe.local exists for PE DotLocal DLL Redirection
            EnsureDotLocalRedirection(gameDirectory, logAction);

            // Step 3: Deploy DXVK d3d9.dll
            DeployDxvkBinaries(gameDirectory, logAction);

            // Step 4: Run EnginePatchService (vid_d3d9_x64.dll display mode bypass, 1000 FPS clamp, throttle jump, legacy URLs, userdata redirection)
            logAction?.Invoke("[VULKAN_UPGRADE] Applying high-performance binary tuning & display mode failure bypass...");
            bool patchesApplied = EnginePatchService.EnsureEnginePatched(gameDirectory, logAction);
            if (!patchesApplied)
            {
                logAction?.Invoke("[VULKAN_UPGRADE] WARNING: One or more binary patch steps encountered warnings or skipped.");
            }

            // Step 5: Render Modernization (write runtime config to userdataKONGOR/dxvk.conf so git-tracked client root stays clean;
            // only seed portable baseline dxvk.conf in gameDirectory when absent on fresh installs)
            try
            {
                logAction?.Invoke("[VULKAN_UPGRADE] Generating optimized dxvk.conf & seeding base shader cache...");
                RenderModernizationService renderService = new(NullLogger<RenderModernizationService>.Instance);
                string runtimeConfigDir = Path.Combine(gameDirectory, "userdataKONGOR");
                renderService.ApplyRenderModernization(runtimeConfigDir, preset);

                string rootDxvkPath = Path.Combine(gameDirectory, "dxvk.conf");
                string win64Dir = Path.Combine(gameDirectory, "windows-x64");
                string win64DxvkPath = Path.Combine(win64Dir, "dxvk.conf");
                if (!File.Exists(rootDxvkPath) || (Directory.Exists(win64Dir) && !File.Exists(win64DxvkPath)))
                {
                    renderService.ApplyRenderModernization(gameDirectory, preset, customStateCacheDir: ".");
                    logAction?.Invoke("[VULKAN_UPGRADE] Seeded portable baseline dxvk.conf (stateCachePath = .) for fresh client directory.");
                }

                logAction?.Invoke($"[VULKAN_UPGRADE] DXVK runtime configuration verified in {runtimeConfigDir}.");
            }
            catch (Exception ex)
            {
                logAction?.Invoke($"[VULKAN_UPGRADE] WARNING: Failed to configure render modernization: {ex.Message}");
            }

            // Step 6: Deploy Vulkan Capture Layer (VkLayer_hon_replay_capture) - skipped for dedicated server
            if (!server)
            {
                DeployCaptureLayer(gameDirectory, logAction);
            }
            else
            {
                logAction?.Invoke("[VULKAN_UPGRADE] Dedicated server target detected; skipping client replay capture layer deployment.");
            }

            // Step 7: Final verification inspection
            ClientGraphicsDriverReport finalReport = InspectClient(gameDirectory, server);
            bool success = !finalReport.RequiresUpgrade;
            string message = success
                ? (server ? "Dedicated server successfully upgraded to Vulkan 1.3 / DXVK (DotLocal Redirection Active)." : "Game client successfully upgraded to Vulkan 1.3 / DXVK (1000 FPS Uncapped).")
                : $"Vulkan upgrade completed with warnings: {finalReport.StatusSummary}";

            logAction?.Invoke($"[VULKAN_UPGRADE] Completed. Status: {finalReport.StatusSummary}");

            return new VulkanUpgradeResult(success, message, finalReport);
        });
    }

    /// <summary>
    ///     Checks if the system Vulkan Loader (vulkan-1.dll) is available and functional.
    /// </summary>
    public static bool CheckSystemVulkanLoader()
    {
        if (!OperatingSystem.IsWindows())
        {
            return true;
        }

        try
        {
            if (NativeLibrary.TryLoad("vulkan-1.dll", out IntPtr handle))
            {
                NativeLibrary.Free(handle);
                return true;
            }
        }
        catch
        {
            // Fallback to direct file inspection
        }

        string systemDir = Environment.GetFolderPath(Environment.SpecialFolder.System);
        string vulkanPath = Path.Combine(systemDir, "vulkan-1.dll");
        return File.Exists(vulkanPath);
    }

    /// <summary>
    ///     Checks if DXVK d3d9.dll is present in the game client or server directory.
    /// </summary>
    public static bool CheckDxvkD3D9(string gameDirectory)
    {
        string primaryPath = Path.Combine(gameDirectory, "d3d9.dll");
        bool hasPrimary = File.Exists(primaryPath) && new FileInfo(primaryPath).Length >= MinimumDxvkSize;

        string win64Dir = Path.Combine(gameDirectory, "windows-x64");
        if (Directory.Exists(win64Dir))
        {
            string win64Path = Path.Combine(win64Dir, "d3d9.dll");
            bool hasWin64 = File.Exists(win64Path) && new FileInfo(win64Path).Length >= MinimumDxvkSize;
            return hasPrimary && hasWin64;
        }

        if (Path.GetFileName(gameDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
            .Equals("windows-x64", StringComparison.OrdinalIgnoreCase))
        {
            string? parentDir = Directory.GetParent(gameDirectory)?.FullName;
            if (parentDir != null && Directory.Exists(parentDir))
            {
                string parentD3d9 = Path.Combine(parentDir, "d3d9.dll");
                bool hasParent = File.Exists(parentD3d9) && new FileInfo(parentD3d9).Length >= MinimumDxvkSize;
                return hasPrimary && hasParent;
            }
        }

        return hasPrimary;
    }

    /// <summary>
    ///     Checks if hon_x64.exe.local exists to enforce Windows PE DotLocal DLL Redirection.
    /// </summary>
    public static bool CheckDotLocalRedirection(string gameDirectory)
    {
        string rootLocal = Path.Combine(gameDirectory, "hon_x64.exe.local");
        bool hasRoot = File.Exists(rootLocal);

        string win64Dir = Path.Combine(gameDirectory, "windows-x64");
        if (Directory.Exists(win64Dir))
        {
            string win64Local = Path.Combine(win64Dir, "hon_x64.exe.local");
            return hasRoot && File.Exists(win64Local);
        }

        if (Path.GetFileName(gameDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
            .Equals("windows-x64", StringComparison.OrdinalIgnoreCase))
        {
            string? parentDir = Directory.GetParent(gameDirectory)?.FullName;
            if (parentDir != null && Directory.Exists(parentDir))
            {
                return File.Exists(Path.Combine(parentDir, "hon_x64.exe.local")) && File.Exists(Path.Combine(gameDirectory, "hon_x64.exe.local"));
            }
        }

        return hasRoot;
    }

    /// <summary>
    ///     Checks if dxvk.conf is present in the game client or server directory (and windows-x64 if present).
    /// </summary>
    public static bool CheckDxvkConfig(string gameDirectory)
    {
        string rootConfig = Path.Combine(gameDirectory, "dxvk.conf");
        bool hasRoot = File.Exists(rootConfig);

        string win64Dir = Path.Combine(gameDirectory, "windows-x64");
        if (Directory.Exists(win64Dir))
        {
            string win64Config = Path.Combine(win64Dir, "dxvk.conf");
            return hasRoot && File.Exists(win64Config);
        }

        if (Path.GetFileName(gameDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
            .Equals("windows-x64", StringComparison.OrdinalIgnoreCase))
        {
            string? parentDir = Directory.GetParent(gameDirectory)?.FullName;
            if (parentDir != null && Directory.Exists(parentDir))
            {
                return File.Exists(Path.Combine(parentDir, "dxvk.conf")) && File.Exists(Path.Combine(gameDirectory, "dxvk.conf"));
            }
        }

        return hasRoot;
    }

    /// <summary>
    ///     Checks whether vid_d3d9_x64.dll has been patched.
    /// </summary>
    public static bool CheckVidDriverPatched(string gameDirectory)
    {
        string[] candidates =
        [
            Path.Combine(gameDirectory, "vid_d3d9_x64.dll"),
            Path.Combine(gameDirectory, "windows-x64", "vid_d3d9_x64.dll")
        ];

        foreach (string candidate in candidates)
        {
            if (File.Exists(candidate))
            {
                byte[] bytes = File.ReadAllBytes(candidate);
                return EnginePatchService.IsVidDriverPatched(bytes);
            }
        }

        // If vid_d3d9_x64.dll does not exist at all, not unpatched
        return true;
    }

    /// <summary>
    ///     Checks whether k2_x64.dll has been patched for 1000 FPS and frame unthrottling.
    /// </summary>
    public static bool CheckK2EnginePatched(string gameDirectory)
    {
        string[] candidates =
        [
            Path.Combine(gameDirectory, "k2_x64.dll"),
            Path.Combine(gameDirectory, "windows-x64", "k2_x64.dll")
        ];

        foreach (string candidate in candidates)
        {
            if (File.Exists(candidate))
            {
                byte[] bytes = File.ReadAllBytes(candidate);
                return EnginePatchService.IsK2Patched(bytes);
            }
        }

        return true;
    }

    /// <summary>
    ///     Checks whether VkLayer_hon_replay_capture is deployed.
    /// </summary>
    public static bool CheckCaptureLayerDeployed(string gameDirectory)
    {
        string[] dllCandidates =
        [
            Path.Combine(gameDirectory, "VkLayer_hon_replay_capture.dll"),
            Path.Combine(gameDirectory, "windows-x64", "VkLayer_hon_replay_capture.dll")
        ];

        string[] jsonCandidates =
        [
            Path.Combine(gameDirectory, "VkLayer_hon_replay_capture.json"),
            Path.Combine(gameDirectory, "windows-x64", "VkLayer_hon_replay_capture.json")
        ];

        bool hasDll = false;
        foreach (string path in dllCandidates)
        {
            if (File.Exists(path)) { hasDll = true; break; }
        }

        bool hasJson = false;
        foreach (string path in jsonCandidates)
        {
            if (File.Exists(path)) { hasJson = true; break; }
        }

        return hasDll && hasJson;
    }

    public static void EnsureDotLocalRedirection(string gameDirectory, Action<string>? logAction = null)
    {
        string rootDir = gameDirectory;
        string? win64Dir = null;

        if (Path.GetFileName(gameDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
            .Equals("windows-x64", StringComparison.OrdinalIgnoreCase))
        {
            win64Dir = gameDirectory;
            rootDir = Directory.GetParent(gameDirectory)?.FullName ?? gameDirectory;
        }
        else
        {
            string candidateWin64 = Path.Combine(gameDirectory, "windows-x64");
            if (Directory.Exists(candidateWin64))
            {
                win64Dir = candidateWin64;
            }
        }

        string rootLocal = Path.Combine(rootDir, "hon_x64.exe.local");
        if (!File.Exists(rootLocal))
        {
            File.WriteAllBytes(rootLocal, []);
            logAction?.Invoke($"[VULKAN_UPGRADE] Created PE DotLocal redirection marker: '{rootLocal}'.");
        }

        if (win64Dir != null)
        {
            string win64Local = Path.Combine(win64Dir, "hon_x64.exe.local");
            if (!File.Exists(win64Local))
            {
                File.WriteAllBytes(win64Local, []);
                logAction?.Invoke($"[VULKAN_UPGRADE] Created PE DotLocal redirection marker: '{win64Local}'.");
            }
        }
    }

    private static void DeployDxvkBinaries(string gameDirectory, Action<string>? logAction)
    {
        string rootDir = gameDirectory;
        string win64Dir = Path.Combine(gameDirectory, "windows-x64");

        if (Path.GetFileName(gameDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
            .Equals("windows-x64", StringComparison.OrdinalIgnoreCase))
        {
            win64Dir = gameDirectory;
            rootDir = Directory.GetParent(gameDirectory)?.FullName ?? gameDirectory;
        }

        string targetRootD3D9 = Path.Combine(rootDir, "d3d9.dll");
        string targetWin64D3D9 = Path.Combine(win64Dir, "d3d9.dll");

        bool rootValid = File.Exists(targetRootD3D9) && new FileInfo(targetRootD3D9).Length >= MinimumDxvkSize;
        bool win64Valid = File.Exists(targetWin64D3D9) && new FileInfo(targetWin64D3D9).Length >= MinimumDxvkSize;

        if (rootValid && (win64Valid || !Directory.Exists(win64Dir)))
        {
            logAction?.Invoke("[VULKAN_UPGRADE] DXVK d3d9.dll is already deployed and verified.");
            return;
        }

        // If windows-x64 has valid d3d9.dll and root is missing, copy from windows-x64
        string? sourceD3D9 = null;
        if (win64Valid)
        {
            sourceD3D9 = targetWin64D3D9;
        }
        else if (rootValid)
        {
            sourceD3D9 = targetRootD3D9;
        }
        else
        {
            sourceD3D9 = FindSourceFile("d3d9.dll", MinimumDxvkSize, rootDir);
        }

        if (sourceD3D9 == null)
        {
            logAction?.Invoke("[VULKAN_UPGRADE] WARNING: Could not find bundled DXVK d3d9.dll to deploy.");
            return;
        }

        try
        {
            if (!rootValid)
            {
                File.Copy(sourceD3D9, targetRootD3D9, overwrite: true);
                logAction?.Invoke($"[VULKAN_UPGRADE] Deployed DXVK d3d9.dll to '{targetRootD3D9}' from '{sourceD3D9}'.");
            }

            if (Directory.Exists(win64Dir) && !win64Valid)
            {
                File.Copy(sourceD3D9, targetWin64D3D9, overwrite: true);
                logAction?.Invoke($"[VULKAN_UPGRADE] Deployed DXVK d3d9.dll to '{targetWin64D3D9}'.");
            }
        }
        catch (Exception ex)
        {
            logAction?.Invoke($"[VULKAN_UPGRADE] ERROR deploying DXVK d3d9.dll: {ex.Message}");
        }
    }

    private static void DeployCaptureLayer(string gameDirectory, Action<string>? logAction)
    {
        string? sourceDll = FindSourceFile("VkLayer_hon_replay_capture.dll", minSize: 10_000, gameDirectory);
        string? sourceJson = FindSourceFile("VkLayer_hon_replay_capture.json", minSize: 100, gameDirectory);

        if (sourceDll == null || sourceJson == null)
        {
            logAction?.Invoke("[VULKAN_UPGRADE] Vulkan replay capture layer source binaries not found; skipping layer deployment.");
            return;
        }

        string win64Dir = Path.Combine(gameDirectory, "windows-x64");
        string targetDll = Directory.Exists(win64Dir)
            ? Path.Combine(win64Dir, "VkLayer_hon_replay_capture.dll")
            : Path.Combine(gameDirectory, "VkLayer_hon_replay_capture.dll");

        string targetJson = Directory.Exists(win64Dir)
            ? Path.Combine(win64Dir, "VkLayer_hon_replay_capture.json")
            : Path.Combine(gameDirectory, "VkLayer_hon_replay_capture.json");

        try
        {
            if (!File.Exists(targetDll))
            {
                File.Copy(sourceDll, targetDll, overwrite: true);
                logAction?.Invoke($"[VULKAN_UPGRADE] Deployed capture layer DLL to '{targetDll}'.");
            }

            if (!File.Exists(targetJson))
            {
                File.Copy(sourceJson, targetJson, overwrite: true);
                logAction?.Invoke($"[VULKAN_UPGRADE] Deployed capture layer JSON to '{targetJson}'.");
            }
        }
        catch (Exception ex)
        {
            logAction?.Invoke($"[VULKAN_UPGRADE] WARNING: Failed to deploy capture layer: {ex.Message}");
        }
    }

    private static string? FindSourceFile(string fileName, long minSize, string preferredClientDirectory)
    {
        // 1. Check within client directory itself
        string[] localCandidates =
        [
            Path.Combine(preferredClientDirectory, fileName),
            Path.Combine(preferredClientDirectory, "windows-x64", fileName)
        ];

        foreach (string candidate in localCandidates)
        {
            if (File.Exists(candidate) && new FileInfo(candidate).Length >= minSize)
            {
                return candidate;
            }
        }

        // 2. Check application base directory
        string appBase = AppContext.BaseDirectory;
        string[] appCandidates =
        [
            Path.Combine(appBase, fileName),
            Path.Combine(appBase, "windows-x64", fileName),
            Path.Combine(appBase, "native", "vulkan-layer", fileName),
            Path.Combine(appBase, "native", "vulkan-layer", "build", "x64", "Release", fileName)
        ];

        foreach (string candidate in appCandidates)
        {
            if (File.Exists(candidate) && new FileInfo(candidate).Length >= minSize)
            {
                return candidate;
            }
        }

        // 3. Search up parent directories from preferredClientDirectory, application base, and current directory
        string[] searchRoots = [preferredClientDirectory, appBase, Directory.GetCurrentDirectory()];
        foreach (string root in searchRoots)
        {
            if (string.IsNullOrWhiteSpace(root))
            {
                continue;
            }

            string? current = root;
            for (int i = 0; i < 10 && current != null; i++)
            {
                string[] searchPaths =
                [
                    Path.Combine(current, fileName),
                    Path.Combine(current, "windows-x64", fileName),
                    Path.Combine(current, "games", "client", fileName),
                    Path.Combine(current, "games", "client", "windows-x64", fileName),
                    Path.Combine(current, "games", "server", fileName),
                    Path.Combine(current, "games", "server", "windows-x64", fileName),
                    Path.Combine(current, "native", "vulkan-layer", "build", "x64", "Release", fileName),
                    Path.Combine(current, "native", "vulkan-layer", fileName)
                ];

                foreach (string searchPath in searchPaths)
                {
                    if (File.Exists(searchPath) && new FileInfo(searchPath).Length >= minSize)
                    {
                        return searchPath;
                    }
                }

                DirectoryInfo? parent = Directory.GetParent(current);
                current = parent?.FullName;
            }
        }

        return null;
    }
}
