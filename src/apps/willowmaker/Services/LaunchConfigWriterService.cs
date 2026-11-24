// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Collections.Specialized;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace WILLOWMAKER.Core.Services;

/// <summary>
///     Dedicated service for atomic client configuration writing, SetSave CVar upserting,
///     isolated portable userdata directory initialization, and Linux/XWayland startup.cfg compatibility.
/// </summary>
public static class LaunchConfigWriterService
{
    private static readonly Lock ConfigFileWriteLock = new();
    private static readonly Regex GuidProfileRegex = new(@"^[0-9a-fA-F-]{36}\.cfg$", RegexOptions.Compiled);

    /// <summary>
    ///     Resolves the effective K2 video driver for the target platform and client directory.
    ///     Windows uses <c>vid_vulkan</c> (or <c>vid_d3d12</c>); Linux and macOS default to <c>vid_gl2</c>
    ///     unless a platform-native <c>vid_vulkan</c> module exporting <c>InitAPIs</c> is present.
    /// </summary>
    // satisfies: OBL-03
    public static string ResolveEffectiveVideoDriver(
        string? clientDirectory,
        bool useD3D12 = false,
        bool? forcePosix = null)
    {
        bool isPosix = forcePosix ?? (!OperatingSystem.IsWindows() ||
                                      (!string.IsNullOrWhiteSpace(clientDirectory) &&
                                       VulkanDriverUpgradeService.IsPosixClientDirectory(clientDirectory)));

        if (!isPosix)
        {
            if (useD3D12)
            {
                if (!string.IsNullOrWhiteSpace(clientDirectory))
                {
                    string d3d12Path = Path.Combine(clientDirectory, "vid_d3d12_x64.dll");
                    string vulkanPath = Path.Combine(clientDirectory, "vid_vulkan_x64.dll");
                    if (!File.Exists(d3d12Path) && File.Exists(vulkanPath))
                    {
                        return "vid_vulkan";
                    }
                }

                return "vid_d3d12";
            }

            return "vid_vulkan";
        }

        if (!string.IsNullOrWhiteSpace(clientDirectory))
        {
            string[] candidates =
            [
                Path.Combine(clientDirectory, "vid_vulkan.dylib"),
                Path.Combine(clientDirectory, "macos-x64", "vid_vulkan.dylib"),
                Path.Combine(clientDirectory, "vid_vulkan-x86_64.so"),
                Path.Combine(clientDirectory, "vid_vulkan.so"),
                Path.Combine(clientDirectory, "linux-x64", "vid_vulkan-x86_64.so"),
                Path.Combine(clientDirectory, "linux-x64", "vid_vulkan.so")
            ];

            foreach (string candidate in candidates)
            {
                if (!File.Exists(candidate))
                {
                    continue;
                }

                try
                {
                    if (NativeLibrary.TryLoad(candidate, out IntPtr handle))
                    {
                        try
                        {
                            if (NativeLibrary.TryGetExport(handle, "InitAPIs", out _))
                            {
                                return "vid_vulkan";
                            }
                        }
                        finally
                        {
                            NativeLibrary.Free(handle);
                        }
                    }
                    else if (string.Equals(Environment.GetEnvironmentVariable("KONGOR_POSIX_VID_VULKAN"), "1", StringComparison.Ordinal))
                    {
                        byte[] bytes = File.ReadAllBytes(candidate);
                        if (System.Text.Encoding.ASCII.GetString(bytes).Contains("InitAPIs", StringComparison.Ordinal))
                        {
                            return "vid_vulkan";
                        }
                    }
                }
                catch
                {
                    // Fall back to vid_gl2 if candidate cannot be loaded or validated
                }
            }
        }

        return "vid_gl2";
    }

    /// <summary>
    ///     Maps a user-selected launcher display mode (<c>"Borderless Windowed"</c>, <c>"Windowed"</c>,
    ///     <c>"Exclusive Fullscreen"</c>) to K2 engine CVars (<c>vid_fullscreen</c>, <c>vid_borderless</c>,
    ///     and <c>d3d_exclusive</c>). Under <c>vid_d3d9_x64.dll</c> / <c>vid_vulkan_x64.dll</c>, true borderless
    ///     DirectFlip fullscreen requires <c>vid_fullscreen = true</c> and <c>d3d_exclusive = false</c>.
    /// </summary>
    public static (string Fullscreen, string Borderless, string D3dExclusive) ResolveDisplayModeCvars(
        string? selectedDisplayMode,
        string? activeDriver)
    {
        string mode = (selectedDisplayMode ?? "Borderless Windowed").Trim();
        string driver = (activeDriver ?? "vid_vulkan").Trim();

        if (mode.Contains("Borderless", StringComparison.OrdinalIgnoreCase))
        {
            return ("true", "true", "false");
        }

        if (mode.Contains("Windowed", StringComparison.OrdinalIgnoreCase))
        {
            return ("false", "false", "false");
        }

        bool allowNativeExclusive = !driver.Equals("vid_vulkan", StringComparison.OrdinalIgnoreCase);
        return ("true", "false", allowNativeExclusive ? "true" : "false");
    }

    /// <summary>
    ///     Idempotently registers <paramref name="gameExecutablePath" /> as a High-Performance Discrete GPU
    ///     application (<c>GpuPreference=2;</c>) in <c>HKCU\Software\Microsoft\DirectX\UserGpuPreferences</c>
    ///     so Windows Hybrid / NVIDIA Optimus / AMD Switchable laptops never route <c>hon_x64.exe</c> to a
    ///     pre-Vulkan-1.3 integrated GPU. Requires zero administrator privileges.
    /// </summary>
    public static bool EnsureWindowsHighPerformanceGpuPreference(
        string? gameExecutablePath,
        Action<string, string>? log = null,
        Action<string, string>? registryWriterOverride = null)
    {
        if (string.IsNullOrWhiteSpace(gameExecutablePath))
        {
            return false;
        }

        const string highPerformanceValue = "GpuPreference=2;";
        try
        {
            string fullExePath = Path.GetFullPath(gameExecutablePath);
            if (registryWriterOverride is not null)
            {
                registryWriterOverride(fullExePath, highPerformanceValue);
                log?.Invoke(LogCategory.Initialise,
                    $"[GPU_ROUTING] Registered High-Performance Discrete GPU preference ({highPerformanceValue}) for '{fullExePath}'.");
                return true;
            }

            if (!OperatingSystem.IsWindows())
            {
                return false;
            }

            using Microsoft.Win32.RegistryKey? key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(
                @"Software\Microsoft\DirectX\UserGpuPreferences",
                writable: true);
            if (key is null)
            {
                return false;
            }

            string? existing = key.GetValue(fullExePath) as string;
            if (!string.IsNullOrWhiteSpace(existing) &&
                existing.Contains("GpuPreference=2", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            string mergedValue = MergeUserGpuPreferenceValue(existing, "GpuPreference", "2");
            key.SetValue(fullExePath, mergedValue, Microsoft.Win32.RegistryValueKind.String);
            log?.Invoke(LogCategory.Initialise,
                $"[GPU_ROUTING] Registered High-Performance Discrete GPU preference ({mergedValue}) for '{fullExePath}'.");
            return true;
        }
        catch (Exception ex)
        {
            log?.Invoke(LogCategory.Executable,
                $"[GPU_ROUTING_WARN] Could not register DirectX High-Performance GPU preference for '{gameExecutablePath}': {ex.Message}");
            return false;
        }
    }

    /// <summary>
    ///     Upserts a <c>Key=Value;</c> token inside a Windows <c>UserGpuPreferences</c> string
    ///     while preserving any existing user tokens (such as <c>SwapEffectUpgradeEnable=1</c> or <c>AutoHDREnable=1</c>).
    /// </summary>
    public static string MergeUserGpuPreferenceValue(string? existing, string key, string value)
    {
        if (string.IsNullOrWhiteSpace(existing))
        {
            return $"{key}={value};";
        }

        List<string> tokens = [];
        bool replaced = false;
        foreach (string rawPart in existing.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            string part = rawPart.Trim();
            if (string.IsNullOrEmpty(part))
                continue;

            int eq = part.IndexOf('=');
            if (eq > 0 && part[..eq].Trim().Equals(key, StringComparison.OrdinalIgnoreCase))
            {
                tokens.Add($"{key}={value}");
                replaced = true;
            }
            else
            {
                tokens.Add(part);
            }
        }

        if (!replaced)
        {
            tokens.Add($"{key}={value}");
        }

        return string.Join(";", tokens) + ";";
    }

    /// <summary>
    ///     Resolves the directory client configuration files are written to.
    ///     Order: explicit <paramref name="baseDirectory"/> → game client directory discovered by
    ///     <see cref="LocationGuard.ResolveGameClientDirectory"/> (env vars, CWD, app base, parent "games/client").
    ///     Deliberately has NO <see cref="Environment.CurrentDirectory"/> fallback: settings-change handlers call the
    ///     writers without a directory, and when WillowMaker was started from the repository root (terminal, MCP,
    ///     dotnet run) that fallback scattered autoexec.cfg, game/, KONGOR/ and userdataKONGOR/ into the repo.
    /// </summary>
    /// <returns>The directory to write to, or <see langword="null"/> when no game client could be located (caller must skip).</returns>
    public static string? ResolveClientConfigRoot(
        string? baseDirectory,
        string caller,
        Action<string, string>? log = null)
    {
        if (!string.IsNullOrWhiteSpace(baseDirectory))
            return baseDirectory;

        string? resolved = LocationGuard.ResolveGameClientDirectory(Environment.CurrentDirectory) ??
                           LocationGuard.ResolveGameClientDirectory(AppContext.BaseDirectory);

        if (resolved is not null)
        {
            log?.Invoke(LogCategory.Initialise, $"[CONFIG_ROOT] {caller}: no directory supplied, resolved game client directory '{resolved}'.");
            return resolved;
        }

        log?.Invoke(LogCategory.Executable,
            $"[CONFIG_ROOT_WARN] {caller}: no game client directory could be located (CWD='{Environment.CurrentDirectory}', " +
            $"AppBase='{AppContext.BaseDirectory}'). Skipping config write instead of writing into the current directory. " +
            "Set HON_GAME_PATH or launch once via the Play button to resolve it.");
        return null;
    }

    /// <summary>
    ///     Writes a config file atomically (temp file in the same directory, then replace).
    ///     In-process writers are serialized by <see cref="ConfigFileWriteLock"/> — several code paths write the same
    ///     autoexec.cfg concurrently (constructor-time LoadLauncherSettings, settings-change handlers, parallel unit
    ///     tests). Cross-process sharing violations (e.g. the game reading the file) get a short bounded SpinWait retry.
    /// </summary>
    public static void WriteConfigFileAtomically(
        string path,
        string content,
        Action<string, string>? log = null)
    {
        const int maxAttempts = 20;
        string tempPath = $"{path}.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp";

        lock (ConfigFileWriteLock)
        {
            string? parentDir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(parentDir) && !Directory.Exists(parentDir))
            {
                Directory.CreateDirectory(parentDir);
            }

            System.Text.Encoding targetEncoding = new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
            if (File.Exists(path))
            {
                try
                {
                    Span<byte> bom = stackalloc byte[2];
                    using FileStream fs = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    if (fs.Read(bom) == 2 && bom[0] == 0xFF && bom[1] == 0xFE)
                    {
                        targetEncoding = System.Text.Encoding.Unicode;
                    }
                }
                catch
                {
                    // Fall back to UTF-8 if target cannot be probed
                }
            }

            File.WriteAllText(tempPath, content, targetEncoding);
            try
            {
                SpinWait backoff = new();
                for (int attempt = 1; ; attempt++)
                {
                    try
                    {
                        File.Move(tempPath, path, overwrite: true);
                        if (attempt > 1)
                            log?.Invoke(LogCategory.Initialise, $"[CONFIG_SYNC] Replaced '{path}' on attempt {attempt}/{maxAttempts}.");
                        return;
                    }
                    catch (Exception ex) when ((ex is IOException or UnauthorizedAccessException) && attempt < maxAttempts)
                    {
                        log?.Invoke(LogCategory.Initialise, $"[CONFIG_SYNC_RETRY] '{path}' busy (attempt {attempt}/{maxAttempts}): {ex.Message}");
                        backoff.SpinOnce(sleep1Threshold: 10);
                    }
                }
            }
            finally
            {
                if (File.Exists(tempPath))
                {
                    try { File.Delete(tempPath); }
                    catch (Exception ex) { log?.Invoke(LogCategory.Executable, $"[CONFIG_SYNC_WARN] Could not delete temp file '{tempPath}': {ex.Message}"); }
                }
            }
        }
    }

    /// <summary>
    ///     Upserts a <c>SetSave "key" "value"</c> directive inside a K2 configuration file content string.
    /// </summary>
    public static string UpsertSetSave(string content, string key, string value, bool allowAppend, out bool modified)
    {
        string pattern = $@"(?m)^(\s*SetSave\s+""{Regex.Escape(key)}""\s+)""[^""]*""(\s+""[^""]*"")?";
        if (Regex.IsMatch(content, pattern))
        {
            modified = true;
            return Regex.Replace(content, pattern, $"$1\"{value}\"$2");
        }
        if (allowAppend)
        {
            modified = true;
            return content.TrimEnd() + $"\nSetSave \"{key}\" \"{value}\" \"0\"\n";
        }
        modified = false;
        return content;
    }

    /// <summary>
    ///     Proactively purges any stale replay hijacking autoexec files (e.g. from aborted NEXUS Replay Renderer jobs).
    /// </summary>
    public static void PurgeStaleReplayAutoexecs(string rootDir, Action<string, string>? log = null)
    {
        string[] staleReplayDirs =
        [
            Path.Combine(rootDir, "userdataKONGOR", "configuration"),
            Path.Combine(rootDir, "userdataKONGOR", "extensions"),
            Path.Combine(rootDir, "userdata", "Heroes of Newerth x64", "game"),
            Path.Combine(rootDir, "userdata", "Heroes of Newerth x64", "KONGOR", "configuration"),
            Path.Combine(rootDir, "userdata", "Heroes of Newerth x64", "KONGOR", "extensions")
        ];

        foreach (string dir in staleReplayDirs)
        {
            try
            {
                string path = Path.Combine(dir, "autoexec.cfg");
                if (File.Exists(path))
                {
                    string text = File.ReadAllText(path);
                    if (text.Contains("StartReplay") || text.Contains("NEXUS Replay Renderer") || text.Contains("host_autoexec"))
                    {
                        File.Delete(path);
                        log?.Invoke(LogCategory.Initialise, $"[CONFIG_SYNC] Purged stale replay autoexec: {path}");
                    }
                }
            }
            catch (Exception ex)
            {
                log?.Invoke(LogCategory.Executable, $"[CONFIG_SYNC_WARN] Failed to purge stale autoexec in {dir}: {ex.Message}");
            }
        }
    }

    /// <summary>
    ///     Mirrors generated <c>autoexec.cfg</c> content to canonical and userdataKONGOR locations.
    /// </summary>
    public static void MirrorAutoexecToCanonicalLocations(
        string rootDir,
        string customConfigurationFilePath,
        string customConfigurationFileContent,
        Action<string, string>? log = null)
    {
        List<string> mirrorAutoexecPaths =
        [
            customConfigurationFilePath,
            Path.Combine(rootDir, "userdataKONGOR", "extensions", "autoexec.cfg"),
            Path.Combine(rootDir, "userdata", "Heroes of Newerth x64", "KONGOR", "configuration", "autoexec.cfg"),
            Path.Combine(rootDir, "userdata", "Heroes of Newerth x64", "KONGOR", "extensions", "autoexec.cfg"),
            Path.Combine(rootDir, "userdata", "Heroes of Newerth x64", "game", "autoexec.cfg"),
            Path.Combine(rootDir, "userdataKONGOR", "configuration", "autoexec.cfg")
        ];

        string[] posixRoots = [rootDir, Path.Combine(rootDir, "linux-x64"), Path.Combine(rootDir, "macos-x64")];
        foreach (string pRoot in posixRoots)
        {
            if (!Directory.Exists(pRoot))
            {
                continue;
            }

            if (OperatingSystem.IsLinux() || pRoot.EndsWith("linux-x64", StringComparison.OrdinalIgnoreCase))
            {
                mirrorAutoexecPaths.Add(Path.Combine(pRoot, "userdata", ".Heroes of Newerth", "game", "autoexec.cfg"));
                mirrorAutoexecPaths.Add(Path.Combine(pRoot, "userdata", ".Heroes of Newerth", "KONGOR", "extensions", "autoexec.cfg"));
                mirrorAutoexecPaths.Add(Path.Combine(pRoot, "userdata", ".Heroes of Newerth x64", "game", "autoexec.cfg"));
                mirrorAutoexecPaths.Add(Path.Combine(pRoot, "userdata", ".Heroes of Newerth x64", "KONGOR", "extensions", "autoexec.cfg"));
            }

            if (OperatingSystem.IsMacOS() || pRoot.EndsWith("macos-x64", StringComparison.OrdinalIgnoreCase))
            {
                mirrorAutoexecPaths.Add(Path.Combine(pRoot, "userdata", "Library", "Application Support", "Heroes of Newerth", "game", "autoexec.cfg"));
                mirrorAutoexecPaths.Add(Path.Combine(pRoot, "userdata", "Library", "Application Support", "Heroes of Newerth", "KONGOR", "extensions", "autoexec.cfg"));
            }
        }

        foreach (string mirrorPath in mirrorAutoexecPaths)
        {
            try
            {
                string? parent = Path.GetDirectoryName(mirrorPath);
                if (!string.IsNullOrEmpty(parent) && !Directory.Exists(parent))
                    Directory.CreateDirectory(parent);
                WriteConfigFileAtomically(mirrorPath, customConfigurationFileContent, log);
                log?.Invoke(LogCategory.Initialise, $"[CONFIG_SYNC] Mirrored to {mirrorPath}");
            }
            catch (Exception ex)
            {
                log?.Invoke(LogCategory.Executable, $"[CONFIG_SYNC_WARN] Failed to mirror {mirrorPath}: {ex.Message}");
            }
        }
    }

    /// <summary>
    ///     Patches canonical <c>startup.cfg</c> and user account profile GUID configs so the selected video driver,
    ///     display mode, and graphics settings are deterministically enforced without erasing keybindings.
    /// </summary>
    public static void SynchronizeStartupAndAccountProfiles(
        string rootDir,
        string activeDriver,
        string fullscreenSetting,
        string borderlessSetting,
        string resParam,
        string refreshRateNumber,
        string shadowMapSize,
        string shadowDrawDistance,
        string foliageDensity,
        string cullGroundSprites,
        string textureCompressionSetting,
        string ambientOcclusionSetting,
        Action<string, string>? log = null,
        string d3dExclusiveSetting = "false",
        string? explicitPresentInterval = null,
        string? explicitTripleBuffering = null,
        string? targetDisplay = null,
        string? maxFpsNumber = null,
        int manualFpsOverride = 0)
    {
        try
        {
            List<string> configPaths = new();
            string canonicalStartup = OperatingSystem.IsLinux()
                ? Path.Combine(rootDir, "userdata", ".Heroes of Newerth", "game", "startup.cfg")
                : Path.Combine(rootDir, "userdata", "Heroes of Newerth x64", "game", "startup.cfg");

            string? canonicalDir = Path.GetDirectoryName(canonicalStartup);
            if (!string.IsNullOrEmpty(canonicalDir) && !Directory.Exists(canonicalDir))
            {
                try
                {
                    Directory.CreateDirectory(canonicalDir);
                }
                catch (Exception ex)
                {
                    log?.Invoke(LogCategory.Executable, $"[CONFIG_SYNC_WARN] Notice: could not create directory '{canonicalDir}': {ex.Message}");
                }
            }

            if (OperatingSystem.IsLinux() && !File.Exists(canonicalStartup))
            {
                try
                {
                    File.WriteAllText(canonicalStartup, "SetSave \"gl_modesetting\" \"\"\n");
                    log?.Invoke(LogCategory.Initialise, $"[LinuxCompat] Created baseline internal startup.cfg at '{canonicalStartup}' with gl_modesetting \"\" for XWayland safety.");
                }
                catch (Exception ex)
                {
                    log?.Invoke(LogCategory.Executable, $"[LinuxCompat] Notice: could not initialize '{canonicalStartup}': {ex.Message}");
                }
            }
            else if (!File.Exists(canonicalStartup))
            {
                try
                {
                    File.WriteAllText(canonicalStartup, "// Project Kongor Canonical Startup Configuration\n");
                    log?.Invoke(LogCategory.Initialise, $"[CONFIG_SYNC] Initialized baseline canonical startup.cfg at '{canonicalStartup}'.");
                }
                catch (Exception ex)
                {
                    log?.Invoke(LogCategory.Executable, $"[CONFIG_SYNC_WARN] Notice: could not initialize '{canonicalStartup}': {ex.Message}");
                }
            }

            if (File.Exists(canonicalStartup))
            {
                configPaths.Add(canonicalStartup);
            }

            string normalizedRoot = rootDir.Replace('\\', '/').TrimEnd('/');
            bool isLinuxTarget = OperatingSystem.IsLinux() ||
                                 normalizedRoot.EndsWith("/linux-x64", StringComparison.OrdinalIgnoreCase) ||
                                 File.Exists(Path.Combine(rootDir, "hon-x86_64"));
            if (isLinuxTarget)
            {
                List<string> linuxRoots = [rootDir];
                string nestedLinuxDir = Path.Combine(rootDir, "linux-x64");
                if (Directory.Exists(nestedLinuxDir))
                {
                    linuxRoots.Add(nestedLinuxDir);
                }

                string[] linuxDotProfiles = [".Heroes of Newerth", ".Heroes of Newerth x64"];
                string[] linuxSubmods = ["game", "override"];
                foreach (string lRoot in linuxRoots)
                {
                    foreach (string dotProfile in linuxDotProfiles)
                    {
                        foreach (string submod in linuxSubmods)
                        {
                            string linuxStartup = Path.Combine(lRoot, "userdata", dotProfile, submod, "startup.cfg");
                            string? linuxDir = Path.GetDirectoryName(linuxStartup);
                            if (!string.IsNullOrEmpty(linuxDir) && !Directory.Exists(linuxDir))
                            {
                                Directory.CreateDirectory(linuxDir);
                            }
                            if (!File.Exists(linuxStartup))
                            {
                                File.WriteAllText(linuxStartup, "SetSave \"gl_modesetting\" \"\"\n");
                            }
                            if (!configPaths.Contains(linuxStartup))
                            {
                                configPaths.Add(linuxStartup);
                            }
                        }
                    }
                }
            }

            bool isMacTarget = OperatingSystem.IsMacOS() ||
                               normalizedRoot.EndsWith("/macos-x64", StringComparison.OrdinalIgnoreCase) ||
                               File.Exists(Path.Combine(rootDir, "HoN64"));
            if (isMacTarget)
            {
                List<string> macRoots = [rootDir];
                string nestedMacDir = Path.Combine(rootDir, "macos-x64");
                if (Directory.Exists(nestedMacDir))
                {
                    macRoots.Add(nestedMacDir);
                }

                string[] macSubmods = ["game", "override"];
                foreach (string mRoot in macRoots)
                {
                    foreach (string submod in macSubmods)
                    {
                        string macInternalStartup = Path.Combine(mRoot, "userdata", "Library", "Application Support", "Heroes of Newerth", submod, "startup.cfg");
                        string? macInternalDir = Path.GetDirectoryName(macInternalStartup);
                        if (!string.IsNullOrEmpty(macInternalDir) && !Directory.Exists(macInternalDir))
                        {
                            Directory.CreateDirectory(macInternalDir);
                        }
                        if (!File.Exists(macInternalStartup))
                        {
                            File.WriteAllText(macInternalStartup, "// Project Kongor macOS Canonical Startup Configuration\n");
                        }
                        if (!configPaths.Contains(macInternalStartup))
                        {
                            configPaths.Add(macInternalStartup);
                        }
                    }
                }

                if (OperatingSystem.IsMacOS())
                {
                    foreach (string submod in macSubmods)
                    {
                        string userHome = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                        if (!string.IsNullOrWhiteSpace(userHome))
                        {
                            string macHomeStartup = Path.Combine(userHome, "Library", "Application Support", "Heroes of Newerth", submod, "startup.cfg");
                            string? macHomeDir = Path.GetDirectoryName(macHomeStartup);
                            if (!string.IsNullOrEmpty(macHomeDir) && !Directory.Exists(macHomeDir))
                            {
                                Directory.CreateDirectory(macHomeDir);
                            }
                            if (!File.Exists(macHomeStartup))
                            {
                                File.WriteAllText(macHomeStartup, "// Project Kongor macOS Canonical Startup Configuration\n");
                            }
                            if (!configPaths.Contains(macHomeStartup))
                            {
                                configPaths.Add(macHomeStartup);
                            }
                        }
                    }
                }
            }

            if (!OperatingSystem.IsLinux())
            {
                string overrideStartup = Path.Combine(rootDir, "userdata", "Heroes of Newerth x64", "override", "startup.cfg");
                if (File.Exists(overrideStartup)) configPaths.Add(overrideStartup);

                string baseStartup = Path.Combine(rootDir, "userdata", "Heroes of Newerth x64", "base", "startup.cfg");
                if (File.Exists(baseStartup)) configPaths.Add(baseStartup);
            }

            foreach (string startupCfgPath in configPaths)
            {
                if (File.Exists(startupCfgPath))
                {
                    string bakPath = $"{startupCfgPath}.bak";
                    if (!File.Exists(bakPath))
                    {
                        try { File.Copy(startupCfgPath, bakPath, overwrite: false); }
                        catch (Exception ex) { log?.Invoke(LogCategory.Executable, $"[BACKUP_WARN] Could not backup {startupCfgPath}: {ex.Message}"); }
                    }

                    string content = File.ReadAllText(startupCfgPath);
                    content = UpsertSetSave(content, "cg_newMainInterface", "true", allowAppend: true, out _);
                    content = UpsertSetSave(content, "vid_fullscreen", fullscreenSetting, allowAppend: true, out _);
                    content = UpsertSetSave(content, "vid_borderless", borderlessSetting, allowAppend: true, out _);
                    content = UpsertSetSave(content, "d3d_exclusive", d3dExclusiveSetting, allowAppend: true, out _);
                    content = UpsertSetSave(content, "options_exclusive", d3dExclusiveSetting, allowAppend: true, out _);
                    content = UpsertSetSave(content, "options_fullscreen", fullscreenSetting, allowAppend: true, out _);
                    if (!Regex.IsMatch(content, @"(?mi)^\s*setsave\s+""?cg_constrainCursor""?\b"))
                    {
                        content = UpsertSetSave(content, "cg_constrainCursor", "true", allowAppend: true, out _);
                    }

                    if (!string.IsNullOrWhiteSpace(explicitPresentInterval))
                    {
                        string vsyncBool = explicitPresentInterval == "0" ? "false" : "true";
                        string tripleBuf = !string.IsNullOrWhiteSpace(explicitTripleBuffering)
                            ? explicitTripleBuffering
                            : (explicitPresentInterval == "0" ? "false" : "true");
                        content = UpsertSetSave(content, "d3d_presentInterval", explicitPresentInterval, allowAppend: true, out _);
                        content = UpsertSetSave(content, "gl_swapInterval", explicitPresentInterval, allowAppend: true, out _);
                        content = UpsertSetSave(content, "vid_vsync", vsyncBool, allowAppend: true, out _);
                        content = UpsertSetSave(content, "d3d_tripleBuffering", tripleBuf, allowAppend: true, out _);
                    }
                    else
                    {
                        // First-run seeding only: default new players to zero-lag (0 / false), while preserving
                        // any in-game Video -> VSync toggle saved by the player across restarts.
                        if (!Regex.IsMatch(content, @"(?mi)^\s*setsave\s+""?d3d_presentInterval""?\b"))
                        {
                            content = UpsertSetSave(content, "d3d_presentInterval", "0", allowAppend: true, out _);
                        }
                        if (!Regex.IsMatch(content, @"(?mi)^\s*setsave\s+""?gl_swapInterval""?\b"))
                        {
                            content = UpsertSetSave(content, "gl_swapInterval", "0", allowAppend: true, out _);
                        }
                        if (!Regex.IsMatch(content, @"(?mi)^\s*setsave\s+""?vid_vsync""?\b"))
                        {
                            content = UpsertSetSave(content, "vid_vsync", "false", allowAppend: true, out _);
                        }
                        if (!Regex.IsMatch(content, @"(?mi)^\s*setsave\s+""?d3d_tripleBuffering""?\b"))
                        {
                            content = UpsertSetSave(content, "d3d_tripleBuffering", "false", allowAppend: true, out _);
                        }
                    }
                    content = UpsertSetSave(content, "vid_resolution", resParam, allowAppend: true, out _);
                    content = UpsertSetSave(content, "options_resolution", resParam, allowAppend: true, out _);
                    content = UpsertSetSave(content, "vid_refreshRate", refreshRateNumber, allowAppend: true, out _);
                    content = UpsertSetSave(content, "options_refreshrate", refreshRateNumber, allowAppend: true, out _);
                    if (!string.IsNullOrWhiteSpace(targetDisplay))
                    {
                        string normTarget = HardwareDetectionService.NormalizeMonitorDeviceToken(targetDisplay);
                        if (string.IsNullOrWhiteSpace(normTarget)) normTarget = targetDisplay;
                        content = UpsertSetSave(content, "vid_display", normTarget, allowAppend: true, out _);
                        content = UpsertSetSave(content, "options_display", normTarget, allowAppend: true, out _);
                    }
                    if (!string.IsNullOrWhiteSpace(maxFpsNumber))
                    {
                        content = UpsertSetSave(content, "vid_maxFPS", maxFpsNumber, allowAppend: true, out _);
                        content = UpsertSetSave(content, "host_maxFPS", maxFpsNumber, allowAppend: true, out _);
                        content = UpsertSetSave(content, "cl_maxFPS", maxFpsNumber, allowAppend: true, out _);
                    }
                    content = UpsertSetSave(content, "cg_manualMaxFpsOverride", manualFpsOverride.ToString(), allowAppend: true, out _);
                    content = UpsertSetSave(content, "vid_shadowmapSize", shadowMapSize, allowAppend: true, out _);
                    content = UpsertSetSave(content, "vid_shadowDrawDistance", shadowDrawDistance, allowAppend: true, out _);
                    content = UpsertSetSave(content, "vid_foliageDensity", foliageDensity, allowAppend: true, out _);
                    content = UpsertSetSave(content, "vid_cullGroundSprites", cullGroundSprites, allowAppend: true, out _);
                    content = UpsertSetSave(content, "vid_textureCompression", textureCompressionSetting, allowAppend: true, out _);
                    content = UpsertSetSave(content, "vid_shaderAmbientOcclusion", ambientOcclusionSetting, allowAppend: true, out _);
                    string normalizedCfgPath = startupCfgPath.Replace('\\', '/');
                    bool isPosixStartupPath =
                        OperatingSystem.IsLinux() ||
                        OperatingSystem.IsMacOS() ||
                        normalizedCfgPath.Contains("/linux-x64/", StringComparison.OrdinalIgnoreCase) ||
                        normalizedCfgPath.Contains("/macos-x64/", StringComparison.OrdinalIgnoreCase) ||
                        normalizedCfgPath.Contains("/.Heroes of Newerth", StringComparison.OrdinalIgnoreCase) ||
                        normalizedCfgPath.Contains("/Library/Application Support/Heroes of Newerth/", StringComparison.OrdinalIgnoreCase);
                    string effectiveDriver = isPosixStartupPath ? "vid_gl2" : activeDriver;

                    content = UpsertSetSave(content, "vid_driver", effectiveDriver, allowAppend: true, out _);
                    content = UpsertSetSave(content, "host_vidDriver", effectiveDriver, allowAppend: true, out _);
                    content = UpsertSetSave(content, "con_showNet", "false", allowAppend: true, out _);
                    content = UpsertSetSave(content, "http_printDebugInfo", "false", allowAppend: true, out _);
                    content = UpsertSetSave(content, "php_printDebugInfo", "false", allowAppend: true, out _);
                    content = UpsertSetSave(content, "sys_dumpOnFatal", "true", allowAppend: true, out _);
                    content = UpsertSetSave(content, "sys_autoSaveDump", "true", allowAppend: true, out _);
                    content = UpsertSetSave(content, "sys_keepOldDumpsOnStartup", "true", allowAppend: true, out _);
                    content = UpsertSetSave(content, "sys_keepDumpAfterUpload", "true", allowAppend: true, out _);
                    content = UpsertSetSave(content, "sys_disableCrashReporter", "true", allowAppend: true, out _);
                    content = UpsertSetSave(content, "cef_crash_reporting_enabled", "false", allowAppend: true, out _);
                    content = UpsertSetSave(content, "cloud_autoupload", "false", allowAppend: true, out _);
                    content = UpsertSetSave(content, "host_cloudAutoUpload", "false", allowAppend: true, out _);
                    content = UpsertSetSave(content, "host_autoUploadOnLogout", "false", allowAppend: true, out _);

                    if (OperatingSystem.IsLinux())
                    {
                        content = UpsertSetSave(content, "gl_modesetting", string.Empty, allowAppend: true, out _);
                    }

                    WriteConfigFileAtomically(startupCfgPath, content, log);
                    log?.Invoke(LogCategory.Initialise, $"[CONFIG_SYNC] Successfully synchronized {startupCfgPath} (driver: {effectiveDriver}, requestedDriver: {activeDriver}, posixClamp: {isPosixStartupPath}, fullscreen: {fullscreenSetting}, borderless: {borderlessSetting}, res: {resParam}@{refreshRateNumber}Hz).");
                }
            }

            string[] userRoots =
            [
                Path.Combine(rootDir, "userdata"),
                Path.Combine(rootDir, "userdataKONGOR"),
                Path.Combine(rootDir, "linux-x64", "userdata"),
                Path.Combine(rootDir, "macos-x64", "userdata")
            ];
            EnumerationOptions profileEnumOpts = new()
            {
                IgnoreInaccessible = true,
                RecurseSubdirectories = true,
                MaxRecursionDepth = 5
            };
            foreach (string uRoot in userRoots)
            {
                if (Directory.Exists(uRoot))
                {
                    foreach (string cfgFile in Directory.EnumerateFiles(uRoot, "*.cfg", profileEnumOpts))
                    {
                        string fileName = Path.GetFileName(cfgFile);
                        bool isAccountCloudCfg = fileName.Equals("account_options_cloud.cfg", StringComparison.OrdinalIgnoreCase);
                        if (GuidProfileRegex.IsMatch(fileName) || isAccountCloudCfg)
                        {
                            try
                            {
                                string bakPath = $"{cfgFile}.bak";
                                if (!File.Exists(bakPath))
                                {
                                    try { File.Copy(cfgFile, bakPath, overwrite: false); }
                                    catch (Exception ex) { log?.Invoke(LogCategory.Executable, $"[BACKUP_WARN] Could not backup GUID profile {cfgFile}: {ex.Message}"); }
                                }

                                string profileContent = File.ReadAllText(cfgFile);
                                bool profileModified = false;
                                profileContent = UpsertSetSave(profileContent, "cg_newMainInterface", "true", allowAppend: false, out bool mUi);
                                profileModified |= mUi;
                                profileContent = UpsertSetSave(profileContent, "vid_fullscreen", fullscreenSetting, allowAppend: false, out bool m1);
                                profileModified |= m1;
                                profileContent = UpsertSetSave(profileContent, "vid_borderless", borderlessSetting, allowAppend: false, out bool m2);
                                profileModified |= m2;
                                profileContent = UpsertSetSave(profileContent, "d3d_exclusive", d3dExclusiveSetting, allowAppend: false, out bool mEx);
                                profileModified |= mEx;
                                profileContent = UpsertSetSave(profileContent, "options_exclusive", d3dExclusiveSetting, allowAppend: false, out bool mOptEx);
                                profileModified |= mOptEx;
                                profileContent = UpsertSetSave(profileContent, "options_fullscreen", fullscreenSetting, allowAppend: false, out bool mOpt);
                                profileModified |= mOpt;
                                profileContent = UpsertSetSave(profileContent, "vid_resolution", resParam, allowAppend: false, out bool m3);
                                profileModified |= m3;
                                profileContent = UpsertSetSave(profileContent, "vid_refreshRate", refreshRateNumber, allowAppend: false, out bool m4);
                                profileModified |= m4;
                                if (!string.IsNullOrWhiteSpace(targetDisplay))
                                {
                                    profileContent = UpsertSetSave(profileContent, "vid_display", targetDisplay, allowAppend: false, out bool mDisp);
                                    profileModified |= mDisp;
                                    profileContent = UpsertSetSave(profileContent, "options_display", targetDisplay, allowAppend: false, out bool mOptDisp);
                                    profileModified |= mOptDisp;
                                }
                                if (!string.IsNullOrWhiteSpace(maxFpsNumber))
                                {
                                    profileContent = UpsertSetSave(profileContent, "vid_maxFPS", maxFpsNumber, allowAppend: false, out bool mFps);
                                    profileModified |= mFps;
                                    profileContent = UpsertSetSave(profileContent, "host_maxFPS", maxFpsNumber, allowAppend: false, out bool mHostFps);
                                    profileModified |= mHostFps;
                                    profileContent = UpsertSetSave(profileContent, "cl_maxFPS", maxFpsNumber, allowAppend: false, out bool mClFps);
                                    profileModified |= mClFps;
                                }
                                profileContent = UpsertSetSave(profileContent, "cg_manualMaxFpsOverride", manualFpsOverride.ToString(), allowAppend: false, out bool mOvr);
                                profileModified |= mOvr;
                                profileContent = UpsertSetSave(profileContent, "con_showNet", "false", allowAppend: false, out bool m5);
                                profileModified |= m5;
                                profileContent = UpsertSetSave(profileContent, "http_printDebugInfo", "false", allowAppend: false, out bool m6);
                                profileModified |= m6;
                                profileContent = UpsertSetSave(profileContent, "php_printDebugInfo", "false", allowAppend: false, out bool m7);
                                profileModified |= m7;

                                if (profileModified)
                                {
                                    WriteConfigFileAtomically(cfgFile, profileContent, log);
                                    log?.Invoke(LogCategory.Initialise, $"[CONFIG_SYNC] Synchronized display settings in account profile {fileName} (fullscreen: {fullscreenSetting}, borderless: {borderlessSetting}).");
                                }
                            }
                            catch (Exception ex)
                            {
                                log?.Invoke(LogCategory.Executable, $"[CONFIG_SYNC_WARN] Failed to patch account profile {cfgFile}: {ex.Message}");
                            }
                        }
                    }
                }
            }

            string[] userInitCfgPaths =
            [
                Path.Combine(rootDir, "userdataKONGOR", "configuration", "init.cfg"),
                Path.Combine(rootDir, "userdata", "Heroes of Newerth x64", "KONGOR", "configuration", "init.cfg")
            ];
            foreach (string initCfgPath in userInitCfgPaths)
            {
                if (File.Exists(initCfgPath))
                {
                    try
                    {
                        string initContent = File.ReadAllText(initCfgPath);
                        string sanitized = Regex.Replace(
                            initContent,
                            @"(?m)^\s*setsave\s+(vid_fullscreen|vid_borderless|d3d_exclusive|options_fullscreen)\s+.*$[\r\n]*",
                            string.Empty);
                        if (sanitized != initContent)
                        {
                            WriteConfigFileAtomically(initCfgPath, sanitized, log);
                            log?.Invoke(LogCategory.Initialise, $"[CONFIG_SYNC] Cleaned stale display overrides from {initCfgPath}.");
                        }
                    }
                    catch (Exception ex)
                    {
                        log?.Invoke(LogCategory.Executable, $"[CONFIG_SYNC_WARN] Failed to sanitize {initCfgPath}: {ex.Message}");
                    }
                }
            }
        }
        catch (Exception ex)
        {
            log?.Invoke(LogCategory.Executable, $"[CONFIG_SYNC_WARN] Failed to patch startup.cfg: {ex.Message}");
        }
    }

    /// <summary>
    ///     Ensures the isolated local user directory structure (userdata/game/, userdata/editor/, etc.)
    ///     exists prior to launch, migrating all runtime assets from Documents/OneDrive to local storage.
    /// </summary>
    public static void EnsureUserDirectoryInitialized(string? baseDirectory = null)
    {
        string rootDir = baseDirectory ?? Environment.CurrentDirectory;
        string userdataRoot = Path.Combine(rootDir, "userdata");

        string[] requiredSubdirs =
        [
            userdataRoot,
            Path.Combine(userdataRoot, "game"),
            Path.Combine(userdataRoot, "game", "logs"),
            Path.Combine(userdataRoot, "game", "replays"),
            Path.Combine(userdataRoot, "game", "shaders"),
            Path.Combine(userdataRoot, "editor"),
            Path.Combine(userdataRoot, "Heroes of Newerth x64"),
            Path.Combine(userdataRoot, "Heroes of Newerth x64", "game"),
            Path.Combine(userdataRoot, "Heroes of Newerth x64", "game", "logs"),
            Path.Combine(userdataRoot, "Heroes of Newerth x64", "game", "replays"),
            Path.Combine(userdataRoot, "Heroes of Newerth x64", "replays"),
            Path.Combine(userdataRoot, ".Heroes of Newerth", "game"),
            Path.Combine(userdataRoot, ".Heroes of Newerth", "override"),
            Path.Combine(userdataRoot, ".Heroes of Newerth x64", "game"),
            Path.Combine(userdataRoot, ".Heroes of Newerth x64", "override"),
            Path.Combine(userdataRoot, "Library", "Application Support", "Heroes of Newerth", "game"),
            Path.Combine(userdataRoot, "Library", "Application Support", "Heroes of Newerth", "override"),
            Path.Combine(rootDir, "userdataKONGOR"),
            Path.Combine(rootDir, "userdataKONGOR", "extensions"),
            Path.Combine(rootDir, "userdataKONGOR", "configuration"),
            Path.Combine(rootDir, "userdataKONGOR", "updates")
        ];

        foreach (string dir in requiredSubdirs)
            if (!Directory.Exists(dir))
                Directory.CreateDirectory(dir);

        // Bi-directional replay synchronization between isolated userdata folders and the engine's canonical path
        try
        {
            string canonicalReplaysDir = Path.Combine(userdataRoot, "Heroes of Newerth x64", "game", "replays");
            if (!Directory.Exists(canonicalReplaysDir))
            {
                Directory.CreateDirectory(canonicalReplaysDir);
            }

            string[] sourceReplayDirs =
            [
                Path.Combine(userdataRoot, "replays"),
                Path.Combine(userdataRoot, "game", "replays"),
                Path.Combine(rootDir, "replays"),
                Path.Combine(rootDir, "game", "replays")
            ];

            foreach (string sDir in sourceReplayDirs)
            {
                if (Directory.Exists(sDir))
                {
                    string[] replayFiles = Directory.GetFiles(sDir, "*.honreplay");
                    foreach (string replayFile in replayFiles)
                    {
                        string fileName = Path.GetFileName(replayFile);
                        string targetPath = Path.Combine(canonicalReplaysDir, fileName);
                        if (!File.Exists(targetPath) || new FileInfo(targetPath).Length != new FileInfo(replayFile).Length)
                        {
                            File.Copy(replayFile, targetPath, true);
                            Console.WriteLine($"[Willowmaker:ReplaySync] Synchronized replay '{fileName}' from '{sDir}' to canonical engine path: '{targetPath}'");
                        }
                    }
                }
            }

            // Mirror canonical replays back to local userdata folders so external tools find them
            string fallbackUserdataReplays = Path.Combine(userdataRoot, "game", "replays");
            string rootUserdataReplays = Path.Combine(userdataRoot, "replays");
            string[] canonicalFiles = Directory.GetFiles(canonicalReplaysDir, "*.honreplay");
            foreach (string cFile in canonicalFiles)
            {
                string fileName = Path.GetFileName(cFile);
                string dest1 = Path.Combine(fallbackUserdataReplays, fileName);
                if (!File.Exists(dest1) || new FileInfo(dest1).Length != new FileInfo(cFile).Length)
                {
                    File.Copy(cFile, dest1, true);
                    Console.WriteLine($"[Willowmaker:ReplaySync] Mirrored replay '{fileName}' to '{dest1}'");
                }
                string dest2 = Path.Combine(rootUserdataReplays, fileName);
                if (!File.Exists(dest2) || new FileInfo(dest2).Length != new FileInfo(cFile).Length)
                {
                    File.Copy(cFile, dest2, true);
                    Console.WriteLine($"[Willowmaker:ReplaySync] Mirrored replay '{fileName}' to '{dest2}'");
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Willowmaker:ReplaySync] Warning synchronizing replays: {ex.Message}");
        }

        // Self-healing shader cache validation: Clean legacy/corrupted .shd shader binaries from userdata/game/shaders
        // so fresh DXVK / K2 shaders compile cleanly without texture clashing or black silhouette artifacts
        try
        {
            string shadersDir = Path.Combine(userdataRoot, "game", "shaders");
            if (Directory.Exists(shadersDir))
            {
                string cacheMarker = Path.Combine(shadersDir, ".cache_version_v3_dxvk_clean");
                if (!File.Exists(cacheMarker))
                {
                    foreach (string file in Directory.GetFiles(shadersDir, "*.*"))
                        try
                        {
                            File.Delete(file);
                        }
                        catch
                        {
                            /* best effort */
                        }

                    File.WriteAllText(cacheMarker, "v3_clean_shader_cache");
                }
            }
        }
        catch
        {
            /* Non-critical cache cleanup error */
        }
    }

    /// <summary>
    ///     Ensures modern Linux display servers (Wayland / XWayland / Mesa) do not crash when the K2 engine initializes OpenGL.
    ///     Legacy K2 engine attempts hardware display modesetting which fails under XWayland unless 'gl_modesetting ""' is saved.
    /// </summary>
    public static void EnsureLinuxModesettingCompatibility(
        string workingDirectory,
        Action<string, string>? log = null)
    {
        try
        {
            log?.Invoke(LogCategory.Executable, "[LinuxCompat] Verifying gl_modesetting compatibility for K2 OpenGL driver on Linux...");

            List<string> candidateDirs =
            [
                Path.Combine(workingDirectory, "userdata", ".Heroes of Newerth", "game"),
                Path.Combine(workingDirectory, "userdata", ".Heroes of Newerth", "override"),
                Path.Combine(workingDirectory, "userdata", ".Heroes of Newerth x64", "game"),
                Path.Combine(workingDirectory, "userdata", ".Heroes of Newerth x64", "override"),
                Path.Combine(workingDirectory, "userdata", "game"),
                Path.Combine(workingDirectory, "userdata", "override"),
                Path.Combine(workingDirectory, "userdata", "Heroes of Newerth x64", "game"),
                Path.Combine(workingDirectory, "userdata", "Heroes of Newerth x64", "override")
            ];
            string nestedLinux = Path.Combine(workingDirectory, "linux-x64");
            if (Directory.Exists(nestedLinux))
            {
                candidateDirs.Add(Path.Combine(nestedLinux, "userdata", ".Heroes of Newerth", "game"));
                candidateDirs.Add(Path.Combine(nestedLinux, "userdata", ".Heroes of Newerth", "override"));
                candidateDirs.Add(Path.Combine(nestedLinux, "userdata", ".Heroes of Newerth x64", "game"));
                candidateDirs.Add(Path.Combine(nestedLinux, "userdata", ".Heroes of Newerth x64", "override"));
            }

            foreach (string dir in candidateDirs)
            {
                try
                {
                    if (!Directory.Exists(dir))
                    {
                        Directory.CreateDirectory(dir);
                    }

                    string cfgFile = Path.Combine(dir, "startup.cfg");
                    const string targetSetting = "SetSave \"gl_modesetting\" \"\"";

                    if (!File.Exists(cfgFile))
                    {
                        File.WriteAllText(cfgFile, targetSetting + Environment.NewLine);
                        log?.Invoke(LogCategory.Executable, $"[LinuxCompat] Created '{cfgFile}' with '{targetSetting}' (XWayland crash prevention).");
                        continue;
                    }

                    string text = File.ReadAllText(cfgFile);
                    if (!text.Contains("gl_modesetting", StringComparison.OrdinalIgnoreCase))
                    {
                        File.AppendAllText(cfgFile, Environment.NewLine + targetSetting + Environment.NewLine);
                        log?.Invoke(LogCategory.Executable, $"[LinuxCompat] Appended '{targetSetting}' to '{cfgFile}'.");
                    }
                    else if (!text.Contains(targetSetting, StringComparison.OrdinalIgnoreCase))
                    {
                        string updated = Regex.Replace(
                            text,
                            @"(?m)^(\s*SetSave\s+""gl_modesetting"")\s+.*",
                            targetSetting);
                        File.WriteAllText(cfgFile, updated);
                        log?.Invoke(LogCategory.Executable, $"[LinuxCompat] Updated existing gl_modesetting to empty in '{cfgFile}'.");
                    }
                    else
                    {
                        log?.Invoke(LogCategory.Executable, $"[LinuxCompat] Verified '{cfgFile}' already has '{targetSetting}'.");
                    }
                }
                catch (Exception dirEx)
                {
                    log?.Invoke(LogCategory.Executable, $"[LinuxCompat] Notice: skipping modesetting check for '{dir}': {dirEx.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            log?.Invoke(LogCategory.Executable, $"[LinuxCompat] Warning verifying Linux display modesetting: {ex.Message}");
        }
    }

    private static bool PathOrSymlinkExists(string path)
    {
        if (Directory.Exists(path) || File.Exists(path))
        {
            return true;
        }

        try
        {
            FileInfo fi = new(path);
            DirectoryInfo di = new(path);
            return fi.LinkTarget is not null || di.LinkTarget is not null;
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return false;
        }
    }

    /// <summary>
    ///     Deletes an empty, non-symlink POSIX shadow directory (for example <c>linux-x64/override</c> created
    ///     accidentally on Windows) so a clean relative VFS symlink can take its place. Never deletes symlinks or
    ///     directories containing any files or subdirectories.
    /// </summary>
    public static bool TrySelfHealEmptyShadowDirectory(
        string path,
        bool allowPosixHeal,
        Action<string, string>? log = null)
    {
        if (!allowPosixHeal || string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        try
        {
            if (!Directory.Exists(path))
            {
                return false;
            }

            DirectoryInfo di = new(path);
            if (Directory.ResolveLinkTarget(path, returnFinalTarget: false) is not null ||
                di.LinkTarget is not null ||
                di.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                return false;
            }

            if (Directory.EnumerateFileSystemEntries(path).Any())
            {
                log?.Invoke(
                    LogCategory.Executable,
                    $"[POSIX_VFS_WARN] Preserving non-empty non-symlink directory '{path}'; refusing to overwrite with VFS symlink.");
                return false;
            }

            Directory.Delete(path, recursive: false);
            log?.Invoke(
                LogCategory.Initialise,
                $"[POSIX_VFS] Self-healed empty shadow directory '{path}' before symlink creation.");
            return true;
        }
        catch (Exception ex)
        {
            log?.Invoke(
                LogCategory.Executable,
                $"[POSIX_VFS_WARN] Failed to inspect or remove empty shadow directory '{path}': {ex.Message}");
            return false;
        }
    }

    /// <summary>
    ///     Ensures POSIX client subdirectories (<c>linux-x64</c>, <c>macos-x64</c>) have idempotent symlinks
    ///     to the parent client's shared asset directories (<c>base</c>, <c>KONGOR</c>, <c>ClientPackageFiles</c>, <c>override</c>),
    ///     root legal/icon assets (<c>tos*.txt</c>, <c>privacy*.txt</c>, <c>honicon_*.png</c>),
    ///     and <c>game/*.s2z</c> archives so <c>GetProcessExecutablePath()</c> / <c>_NSGetExecutablePath</c> / <c>/proc/self/exe</c>
    ///     resolves all VFS mounts without duplicating ~4 GB of archives.
    /// </summary>
    // satisfies: OBL-02
    public static void EnsurePosixClientRootPrepared(
        string? workingDirectory,
        Action<string, string>? log = null,
        bool? isPosixOverride = null)
    {
        if (string.IsNullOrWhiteSpace(workingDirectory) || !Directory.Exists(workingDirectory))
        {
            return;
        }

        try
        {
            QuarantineLegacyManifestFiles(workingDirectory, log);

            bool isPosix = isPosixOverride ?? !OperatingSystem.IsWindows();
            string normalized = workingDirectory.Replace('\\', '/').TrimEnd('/');
            string dirName = Path.GetFileName(normalized);
            string posixRoot;
            string sharedClientRoot;

            if (dirName.Equals("macos-x64", StringComparison.OrdinalIgnoreCase) ||
                dirName.Equals("linux-x64", StringComparison.OrdinalIgnoreCase))
            {
                if (!isPosix)
                {
                    log?.Invoke(
                        LogCategory.Initialise,
                        $"[POSIX_VFS] Skipping POSIX VFS symlink preparation on Windows for '{workingDirectory}'.");
                    return;
                }

                posixRoot = workingDirectory;
                string? parent = Directory.GetParent(workingDirectory)?.FullName;
                if (string.IsNullOrWhiteSpace(parent) || !Directory.Exists(parent))
                {
                    EnsureLinuxRuntimeLibrariesPrepared(posixRoot, log);
                    return;
                }
                sharedClientRoot = parent;
            }
            else if (isPosix &&
                     Directory.Exists(Path.Combine(workingDirectory, "macos-x64")) &&
                     (OperatingSystem.IsMacOS() || !Directory.Exists(Path.Combine(workingDirectory, "linux-x64"))))
            {
                sharedClientRoot = workingDirectory;
                posixRoot = Path.Combine(workingDirectory, "macos-x64");
            }
            else if (isPosix &&
                     Directory.Exists(Path.Combine(workingDirectory, "linux-x64")))
            {
                sharedClientRoot = workingDirectory;
                posixRoot = Path.Combine(workingDirectory, "linux-x64");
            }
            else
            {
                return;
            }

            QuarantineLegacyManifestFiles(sharedClientRoot, log);
            QuarantineLegacyManifestFiles(posixRoot, log);

            string[] sharedFolders = ["base", "KONGOR", "ClientPackageFiles", "override"];
            foreach (string folder in sharedFolders)
            {
                string sourceDir = Path.Combine(sharedClientRoot, folder);
                string linkPath = Path.Combine(posixRoot, folder);
                if (!Directory.Exists(sourceDir))
                {
                    continue;
                }

                TrySelfHealEmptyShadowDirectory(linkPath, allowPosixHeal: isPosix, log);

                if (PathOrSymlinkExists(linkPath))
                {
                    continue;
                }

                try
                {
                    Directory.CreateSymbolicLink(linkPath, Path.Combine("..", folder));
                    log?.Invoke(LogCategory.Initialise, $"[POSIX_VFS] Linked directory '{linkPath}' -> '../{folder}'.");
                }
                catch (Exception relEx)
                {
                    log?.Invoke(LogCategory.Initialise, $"[POSIX_VFS] Relative symlink fallback for '{linkPath}': {relEx.Message}");
                    try
                    {
                        Directory.CreateSymbolicLink(linkPath, sourceDir);
                        log?.Invoke(LogCategory.Initialise, $"[POSIX_VFS] Linked directory '{linkPath}' -> '{sourceDir}'.");
                    }
                    catch (Exception ex)
                    {
                        log?.Invoke(LogCategory.Executable, $"[POSIX_VFS_WARN] Could not link '{linkPath}' -> '../{folder}': {ex.Message}. Fix: Run ln -s ../{folder} \"{linkPath}\"");
                    }
                }
            }

            string? repoGamesDir = Directory.GetParent(sharedClientRoot)?.FullName;
            if (!string.IsNullOrWhiteSpace(repoGamesDir))
            {
                string repoSharedDir = Path.Combine(repoGamesDir, "shared");
                string parentSharedLink = Path.Combine(sharedClientRoot, "shared");
                if (Directory.Exists(repoSharedDir))
                {
                    TrySelfHealEmptyShadowDirectory(parentSharedLink, allowPosixHeal: isPosix, log);

                    if (!PathOrSymlinkExists(parentSharedLink))
                    {
                        try
                        {
                            Directory.CreateSymbolicLink(parentSharedLink, Path.Combine("..", "shared"));
                            log?.Invoke(LogCategory.Initialise, $"[POSIX_VFS] Linked parent shared directory '{parentSharedLink}' -> '../shared'.");
                        }
                        catch (Exception relEx)
                        {
                            log?.Invoke(LogCategory.Initialise, $"[POSIX_VFS] Relative parent shared symlink fallback for '{parentSharedLink}': {relEx.Message}");
                            try
                            {
                                Directory.CreateSymbolicLink(parentSharedLink, repoSharedDir);
                                log?.Invoke(LogCategory.Initialise, $"[POSIX_VFS] Linked parent shared directory '{parentSharedLink}' -> '{repoSharedDir}'.");
                            }
                            catch (Exception ex)
                            {
                                log?.Invoke(LogCategory.Executable, $"[POSIX_VFS_WARN] Could not link '{parentSharedLink}' -> '../shared': {ex.Message}");
                            }
                        }
                    }
                }
            }

            string[] rootAssetPatterns = ["tos*.txt", "privacy*.txt", "honicon_*.png"];
            foreach (string pattern in rootAssetPatterns)
            {
                IEnumerable<string> matchedFiles;
                try
                {
                    matchedFiles = Directory.EnumerateFiles(sharedClientRoot, pattern, SearchOption.TopDirectoryOnly);
                }
                catch (Exception ex)
                {
                    log?.Invoke(LogCategory.Executable, $"[POSIX_VFS_WARN] Could not enumerate root asset pattern '{pattern}' in '{sharedClientRoot}': {ex.Message}");
                    continue;
                }

                foreach (string srcAsset in matchedFiles)
                {
                    string assetName = Path.GetFileName(srcAsset);
                    string assetLinkPath = Path.Combine(posixRoot, assetName);
                    if (PathOrSymlinkExists(assetLinkPath))
                    {
                        continue;
                    }

                    try
                    {
                        File.CreateSymbolicLink(assetLinkPath, Path.Combine("..", assetName));
                        log?.Invoke(LogCategory.Initialise, $"[POSIX_VFS] Linked root asset '{assetLinkPath}' -> '../{assetName}'.");
                    }
                    catch (Exception relEx)
                    {
                        log?.Invoke(LogCategory.Initialise, $"[POSIX_VFS] Relative root asset symlink fallback for '{assetName}': {relEx.Message}");
                        try
                        {
                            File.CreateSymbolicLink(assetLinkPath, srcAsset);
                            log?.Invoke(LogCategory.Initialise, $"[POSIX_VFS] Linked root asset '{assetLinkPath}' -> '{srcAsset}'.");
                        }
                        catch (Exception absEx)
                        {
                            log?.Invoke(LogCategory.Executable, $"[POSIX_VFS_WARN] Could not link root asset '{assetLinkPath}' -> '../{assetName}': {absEx.Message}");
                        }
                    }
                }
            }

            string sharedGameDir = Path.Combine(sharedClientRoot, "game");
            string posixGameDir = Path.Combine(posixRoot, "game");
            if (Directory.Exists(sharedGameDir))
            {
                if (!Directory.Exists(posixGameDir))
                {
                    Directory.CreateDirectory(posixGameDir);
                }

                foreach (string s2zPath in Directory.EnumerateFiles(sharedGameDir, "*.s2z"))
                {
                    string fileName = Path.GetFileName(s2zPath);
                    string linkPath = Path.Combine(posixGameDir, fileName);
                    if (PathOrSymlinkExists(linkPath))
                    {
                        continue;
                    }

                    try
                    {
                        File.CreateSymbolicLink(linkPath, Path.Combine("..", "..", "game", fileName));
                        log?.Invoke(LogCategory.Initialise, $"[POSIX_VFS] Linked archive '{linkPath}' -> '../../game/{fileName}'.");
                    }
                    catch (Exception relEx)
                    {
                        log?.Invoke(LogCategory.Initialise, $"[POSIX_VFS] Relative archive symlink fallback for '{fileName}': {relEx.Message}");
                        try
                        {
                            File.CreateSymbolicLink(linkPath, s2zPath);
                            log?.Invoke(LogCategory.Initialise, $"[POSIX_VFS] Linked archive '{linkPath}' -> '{s2zPath}'.");
                        }
                        catch (Exception ex)
                        {
                            log?.Invoke(LogCategory.Executable, $"[POSIX_VFS_WARN] Could not link '{linkPath}' -> '../../game/{fileName}': {ex.Message}. Fix: Run ln -s ../../game/{fileName} \"{linkPath}\"");
                        }
                    }
                }

                foreach (string subDir in Directory.EnumerateDirectories(sharedGameDir))
                {
                    string subDirName = Path.GetFileName(subDir);
                    string subLinkPath = Path.Combine(posixGameDir, subDirName);
                    TrySelfHealEmptyShadowDirectory(subLinkPath, allowPosixHeal: isPosix, log);
                    if (PathOrSymlinkExists(subLinkPath))
                    {
                        continue;
                    }

                    try
                    {
                        Directory.CreateSymbolicLink(subLinkPath, Path.Combine("..", "..", "game", subDirName));
                        log?.Invoke(LogCategory.Initialise, $"[POSIX_VFS] Linked game subdirectory '{subLinkPath}' -> '../../game/{subDirName}'.");
                    }
                    catch (Exception relEx)
                    {
                        System.Diagnostics.Trace.WriteLine($"[POSIX_VFS] Relative subdir symlink fallback for '{subDirName}': {relEx.Message}");
                        try
                        {
                            Directory.CreateSymbolicLink(subLinkPath, subDir);
                            log?.Invoke(LogCategory.Initialise, $"[POSIX_VFS] Linked game subdirectory '{subLinkPath}' -> '{subDir}'.");
                        }
                        catch (Exception absEx)
                        {
                            log?.Invoke(LogCategory.Executable, $"[POSIX_VFS_WARN] Could not link '{subLinkPath}' -> '../../game/{subDirName}': {absEx.Message}");
                        }
                    }
                }
            }

            EnsureLinuxRuntimeLibrariesPrepared(posixRoot, log);
        }
        catch (Exception ex)
        {
            log?.Invoke(LogCategory.Executable, $"[POSIX_VFS_WARN] Failed to prepare POSIX client root '{workingDirectory}': {ex.Message}");
        }
    }

    /// <summary>
    ///     Quarantines legacy S2 <c>manifest.xml</c> and <c>*.manifest.xml.zip</c> checksum archives into
    ///     <c>*.disabled</c> so POSIX K2 (<c>libk2-x86_64.so</c> / <c>libk2.dylib</c>) never rejects modernized
    ///     <c>override/</c> or <c>resources*.s2z</c> entries during VFS initialization.
    /// </summary>
    public static int QuarantineLegacyManifestFiles(
        string? directory,
        Action<string, string>? log = null)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
        {
            return 0;
        }

        int quarantined = 0;
        List<string> candidates = [];
        string directManifest = Path.Combine(directory, "manifest.xml");
        if (File.Exists(directManifest))
        {
            candidates.Add(directManifest);
        }

        try
        {
            foreach (string zipManifest in Directory.EnumerateFiles(directory, "*.manifest.xml.zip", SearchOption.TopDirectoryOnly))
            {
                candidates.Add(zipManifest);
            }
        }
        catch (Exception ex)
        {
            log?.Invoke(LogCategory.Executable, $"[POSIX_MANIFEST_WARN] Could not scan manifests in '{directory}': {ex.Message}");
        }

        foreach (string manifestPath in candidates)
        {
            if (manifestPath.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string disabledPath = manifestPath + ".disabled";
            try
            {
                File.Move(manifestPath, disabledPath, overwrite: true);
                quarantined++;
                log?.Invoke(LogCategory.Initialise,
                    $"[POSIX_MANIFEST] Quarantined legacy S2 manifest '{manifestPath}' -> '{disabledPath}'.");
            }
            catch (Exception ex)
            {
                log?.Invoke(LogCategory.Executable,
                    $"[POSIX_MANIFEST_WARN] Failed to quarantine '{manifestPath}' -> '{disabledPath}': {ex.Message}");
            }
        }

        return quarantined;
    }

    private static readonly HashSet<string> ExcludedArchaicLinuxBundledLibraries = new(StringComparer.Ordinal)
    {
        "libstdc++.so.6",
        "libgcc_s.so.1",
        "libfreetype.so.6",
        "libc++.so.1",
        "libc++abi.so.1"
    };

    private static readonly HashSet<string> LinuxAwesomiumStubLibraries = new(StringComparer.Ordinal)
    {
        "libawesomium-1-7.so.5",
        "libawesomium-1.6.5.so.0"
    };

    private static readonly string[] DefaultLinuxSystemLibDirs =
    [
        "/usr/lib64",
        "/lib64",
        "/usr/lib/x86_64-linux-gnu",
        "/lib/x86_64-linux-gnu",
        "/usr/lib",
        "/lib",
        "/app/lib",
        "/app/lib64",
        "/run/current-system/sw/lib"
    ];

    /// <summary>
    ///     Populates <c>linux-x64/libs-runtime/</c> with symlinks to bundled <c>libs-x86_64/</c> libraries while:
    ///     <list type="number">
    ///         <item>Routing <c>libawesomium-*</c> to <c>libs-stub/</c> (eliminating 37 dead GNOME2/GTK2 deps and 2012 tcmalloc).</item>
    ///         <item>Shadowing archaic 2012-2015 C++/FreeType shared objects (<c>libstdc++.so.6</c>, <c>libgcc_s.so.1</c>,
    ///               <c>libfreetype.so.6</c>, <c>libc++.so.1</c>, <c>libc++abi.so.1</c>) with modern 64-bit host copies in
    ///               <c>libs-runtime/</c> so <c>LD_LIBRARY_PATH</c> beats <c>DT_RUNPATH</c> (<c>$ORIGIN/libs-x86_64</c>),
    ///               while leaving <c>libc++.so.1</c>/<c>libc++abi.so.1</c> unshadowed when absent on host so bundled copies act as fallback.</item>
    ///         <item>Bridging <c>libudev.so.0</c> -> <c>libudev.so.1</c> and searching host/Steam/Flatpak fallbacks if <c>libxml2.so.2</c> or <c>libGLU.so.1</c> is missing.</item>
    ///     </list>
    /// </summary>
    public static void EnsureLinuxRuntimeLibrariesPrepared(
        string? posixRoot,
        Action<string, string>? log = null,
        IReadOnlyList<string>? hostSearchDirsOverride = null,
        string? ldconfigOutputOverride = null)
    {
        if (string.IsNullOrWhiteSpace(posixRoot) || !Directory.Exists(posixRoot))
        {
            return;
        }

        string bundledLibsDir = Path.Combine(posixRoot, "libs-x86_64");
        if (!Directory.Exists(bundledLibsDir))
        {
            return;
        }

        string stubLibsDir = Path.Combine(posixRoot, "libs-stub");
        string runtimeLibsDir = Path.Combine(posixRoot, "libs-runtime");
        if (!Directory.Exists(runtimeLibsDir))
        {
            Directory.CreateDirectory(runtimeLibsDir);
        }

        string ldconfigCache = ldconfigOutputOverride ?? CaptureLinuxLdconfigCache(hostSearchDirsOverride is not null);

        foreach (string stubName in LinuxAwesomiumStubLibraries)
        {
            string stubSource = Path.Combine(stubLibsDir, stubName);
            if (!File.Exists(stubSource))
            {
                continue;
            }

            string linkPath = Path.Combine(runtimeLibsDir, stubName);
            FileInfo linkInfo = new(linkPath);
            string? currentTarget = linkInfo.LinkTarget;
            bool alreadyPointsToStub = (File.Exists(linkPath) || currentTarget is not null) &&
                                       currentTarget is not null &&
                                       currentTarget.Replace('\\', '/').Contains("libs-stub/", StringComparison.Ordinal);
            if (alreadyPointsToStub)
            {
                continue;
            }

            TryDeleteFileOrBrokenSymlink(linkPath, log);
            CreateRuntimeLibraryLinkOrCopy(linkPath, Path.Combine("..", "libs-stub", stubName), stubSource, log,
                $"[LINUX_LIBS] Routed '{stubName}' -> '../libs-stub/{stubName}' (clean 0-dep Awesomium stub).");
        }

        foreach (string bundledFile in Directory.EnumerateFiles(bundledLibsDir))
        {
            string fileName = Path.GetFileName(bundledFile);
            string linkPath = Path.Combine(runtimeLibsDir, fileName);

            if (LinuxAwesomiumStubLibraries.Contains(fileName) && File.Exists(Path.Combine(stubLibsDir, fileName)))
            {
                continue;
            }

            if (ExcludedArchaicLinuxBundledLibraries.Contains(fileName))
            {
                TryDeleteFileOrBrokenSymlink(linkPath, log);
                string? hostLibPath = ResolveHostLinuxSharedLibrary(fileName, hostSearchDirsOverride, ldconfigCache);
                if (!string.IsNullOrWhiteSpace(hostLibPath))
                {
                    CreateRuntimeLibraryLinkOrCopy(linkPath, hostLibPath, hostLibPath, log,
                        $"[LINUX_LIBS] Shadowed archaic bundled '{fileName}' with host '{hostLibPath}'.");
                }
                else
                {
                    log?.Invoke(LogCategory.Initialise,
                        $"[LINUX_LIBS] Host copy of '{fileName}' not found; leaving unshadowed so DT_RUNPATH bundled copy serves as fallback.");
                }
                continue;
            }

            FileInfo existingInfo = new(linkPath);
            if (File.Exists(linkPath) || existingInfo.LinkTarget is not null)
            {
                if (File.Exists(linkPath))
                {
                    continue;
                }

                TryDeleteFileOrBrokenSymlink(linkPath, log);
            }

            CreateRuntimeLibraryLinkOrCopy(linkPath, Path.Combine("..", "libs-x86_64", fileName), bundledFile, log,
                $"[LINUX_LIBS] Linked runtime library '{linkPath}' -> '../libs-x86_64/{fileName}'.");
        }

        string[] criticalClientLibs = ["libxml2.so.2", "libGLU.so.1"];
        foreach (string reqLib in criticalClientLibs)
        {
            string runtimeReqPath = Path.Combine(runtimeLibsDir, reqLib);
            if (File.Exists(runtimeReqPath))
            {
                continue;
            }

            string? fallbackHostLib = ResolveHostOrRuntimeFallbackLibrary(reqLib, hostSearchDirsOverride, ldconfigCache);
            if (!string.IsNullOrWhiteSpace(fallbackHostLib))
            {
                TryDeleteFileOrBrokenSymlink(runtimeReqPath, log);
                CreateRuntimeLibraryLinkOrCopy(runtimeReqPath, fallbackHostLib, fallbackHostLib, log,
                    $"[LINUX_LIBS] Linked fallback '{reqLib}' -> '{fallbackHostLib}'.");
            }
        }

        string udev0Link = Path.Combine(runtimeLibsDir, "libudev.so.0");
        if (!File.Exists(udev0Link))
        {
            string? udev1Path = ResolveHostLinuxSharedLibrary("libudev.so.1", hostSearchDirsOverride, ldconfigCache);
            if (!string.IsNullOrWhiteSpace(udev1Path))
            {
                CreateRuntimeLibraryLinkOrCopy(udev0Link, udev1Path, udev1Path, log,
                    $"[LINUX_LIBS] Linked '{udev0Link}' -> '{udev1Path}' for libcef compatibility.");
            }
        }
    }

    private static void TryDeleteFileOrBrokenSymlink(string path, Action<string, string>? log)
    {
        try
        {
            FileInfo fi = new(path);
            if (fi.Exists || fi.LinkTarget is not null || (fi.Attributes != (FileAttributes)(-1)))
            {
                File.Delete(path);
            }
        }
        catch (FileNotFoundException)
        {
        }
        catch (DirectoryNotFoundException)
        {
        }
        catch (Exception ex)
        {
            log?.Invoke(LogCategory.Executable, $"[LINUX_LIBS_WARN] Could not remove stale runtime link '{path}': {ex.Message}");
        }
    }

    private static void CreateRuntimeLibraryLinkOrCopy(
        string linkPath,
        string preferredLinkTarget,
        string fallbackSourceFile,
        Action<string, string>? log,
        string successMessage)
    {
        try
        {
            File.CreateSymbolicLink(linkPath, preferredLinkTarget);
            log?.Invoke(LogCategory.Initialise, successMessage);
        }
        catch
        {
            try
            {
                if (!string.Equals(preferredLinkTarget, fallbackSourceFile, StringComparison.Ordinal))
                {
                    File.CreateSymbolicLink(linkPath, fallbackSourceFile);
                    log?.Invoke(LogCategory.Initialise, successMessage);
                    return;
                }
            }
            catch
            {
            }

            try
            {
                if (OperatingSystem.IsWindows() && File.Exists(fallbackSourceFile) && new FileInfo(fallbackSourceFile).Length <= 10 * 1024 * 1024)
                {
                    File.Copy(fallbackSourceFile, linkPath, overwrite: true);
                    log?.Invoke(LogCategory.Initialise, successMessage);
                }
                else
                {
                    log?.Invoke(LogCategory.Executable,
                        $"[LINUX_LIBS_WARN] Could not link '{linkPath}' -> '{preferredLinkTarget}'. Fix: Run ln -sf \"{preferredLinkTarget}\" \"{linkPath}\"");
                }
            }
            catch (Exception ex)
            {
                log?.Invoke(LogCategory.Executable,
                    $"[LINUX_LIBS_WARN] Could not link '{linkPath}' -> '{preferredLinkTarget}': {ex.Message}. Fix: Run ln -sf \"{preferredLinkTarget}\" \"{linkPath}\"");
            }
        }
    }

    private static string CaptureLinuxLdconfigCache(bool skipSystemProbe)
    {
        if (skipSystemProbe || !OperatingSystem.IsLinux())
        {
            return string.Empty;
        }

        string[] ldconfigCandidates = ["/sbin/ldconfig", "/usr/sbin/ldconfig", "/bin/ldconfig", "/usr/bin/ldconfig"];
        foreach (string bin in ldconfigCandidates)
        {
            if (!File.Exists(bin))
            {
                continue;
            }

            if (RunMacOsTool(bin, ["-p"], out string stdout, out _, timeoutMs: 2_500) == 0 && !string.IsNullOrWhiteSpace(stdout))
            {
                return stdout;
            }
        }

        return string.Empty;
    }

    private static string? ResolveHostLinuxSharedLibrary(
        string fileName,
        IReadOnlyList<string>? hostSearchDirsOverride,
        string ldconfigCache)
    {
        if (!string.IsNullOrWhiteSpace(ldconfigCache))
        {
            string[] lines = ldconfigCache.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
            foreach (string rawLine in lines)
            {
                string line = rawLine.Trim();
                if (!line.StartsWith(fileName, StringComparison.Ordinal))
                {
                    continue;
                }

                if (line.Length > fileName.Length && !char.IsWhiteSpace(line[fileName.Length]))
                {
                    continue;
                }

                if (!line.Contains("x86-64", StringComparison.OrdinalIgnoreCase) &&
                    !line.Contains("x86_64", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                int arrowIdx = line.IndexOf("=>", StringComparison.Ordinal);
                if (arrowIdx < 0)
                {
                    continue;
                }

                string resolvedPath = line[(arrowIdx + 2)..].Trim();
                if (IsAcceptableElf64Candidate(resolvedPath))
                {
                    return resolvedPath;
                }
            }
        }

        List<string> searchDirs = [];
        if (hostSearchDirsOverride is not null)
        {
            searchDirs.AddRange(hostSearchDirsOverride);
        }
        else if (OperatingSystem.IsLinux())
        {
            try
            {
                if (Directory.Exists("/usr/lib/gcc"))
                {
                    foreach (string archDir in Directory.EnumerateDirectories("/usr/lib/gcc", "x86_64*"))
                    {
                        foreach (string verDir in Directory.EnumerateDirectories(archDir).OrderByDescending(d => d, StringComparer.Ordinal))
                        {
                            searchDirs.Add(verDir);
                        }
                    }
                }
            }
            catch
            {
            }

            searchDirs.AddRange(DefaultLinuxSystemLibDirs);
        }

        foreach (string dir in searchDirs)
        {
            if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir))
            {
                continue;
            }

            string candidate = Path.Combine(dir, fileName);
            if (IsAcceptableElf64Candidate(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private static string? ResolveHostOrRuntimeFallbackLibrary(
        string fileName,
        IReadOnlyList<string>? hostSearchDirsOverride,
        string ldconfigCache)
    {
        string? direct = ResolveHostLinuxSharedLibrary(fileName, hostSearchDirsOverride, ldconfigCache);
        if (!string.IsNullOrWhiteSpace(direct))
        {
            return direct;
        }

        if (hostSearchDirsOverride is not null || !OperatingSystem.IsLinux())
        {
            return null;
        }

        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        List<string> extraRuntimeDirs = [];
        if (!string.IsNullOrWhiteSpace(home))
        {
            string[] steamRoots =
            [
                Path.Combine(home, ".local", "share", "Steam"),
                Path.Combine(home, ".steam", "steam"),
                Path.Combine(home, ".steam", "root")
            ];
            foreach (string sRoot in steamRoots)
            {
                extraRuntimeDirs.Add(Path.Combine(sRoot, "ubuntu12_32", "steam-runtime", "usr", "lib", "x86_64-linux-gnu"));
                extraRuntimeDirs.Add(Path.Combine(sRoot, "ubuntu12_32", "steam-runtime", "lib", "x86_64-linux-gnu"));
                extraRuntimeDirs.Add(Path.Combine(sRoot, "steamapps", "common", "SteamLinuxRuntime_sniper", "sniper_platform_0.20240125.75305", "files", "lib", "x86_64-linux-gnu"));
                extraRuntimeDirs.Add(Path.Combine(sRoot, "steamapps", "common", "SteamLinuxRuntime_soldier", "soldier_platform_0.20240125.75304", "files", "lib", "x86_64-linux-gnu"));
            }
        }

        foreach (string dir in extraRuntimeDirs)
        {
            string candidate = Path.Combine(dir, fileName);
            if (IsAcceptableElf64Candidate(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private static bool IsAcceptableElf64Candidate(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return false;
        }

        try
        {
            Span<byte> header = stackalloc byte[5];
            using FileStream fs = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            int read = fs.Read(header);
            if (read >= 5 && header[0] == 0x7F && header[1] == (byte)'E' && header[2] == (byte)'L' && header[3] == (byte)'F')
            {
                return header[4] == 2; // ELFCLASS64 only (never 32-bit ELFCLASS32)
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    ///     Enumerates all 64-bit Mach-O binaries (<c>.dylib</c> modules and <c>HoN64</c>) under <paramref name="macosRoot" />
    ///     that require quarantine removal and code-signature verification on macOS, excluding legacy 32-bit
    ///     <c>Awesomium.framework</c> binaries. Orders <c>.dylib</c> dependencies before <c>HoN64</c>.
    /// </summary>
    public static IReadOnlyList<string> EnumerateMacOsSignableTargets(string macosRoot)
    {
        if (string.IsNullOrWhiteSpace(macosRoot) || !Directory.Exists(macosRoot))
        {
            return [];
        }

        List<string> targets = [];
        foreach (string dylibPath in Directory.EnumerateFiles(macosRoot, "*.dylib", SearchOption.AllDirectories))
        {
            if (dylibPath.Contains("Awesomium.framework", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            targets.Add(dylibPath);
        }

        targets.Sort(StringComparer.Ordinal);

        string hon64Path = Path.Combine(macosRoot, "HoN64");
        if (File.Exists(hon64Path))
        {
            targets.Add(hon64Path);
        }

        return targets;
    }

    /// <summary>
    ///     Clears macOS <c>com.apple.quarantine</c> extended attributes and verifies Mach-O code signatures on
    ///     all 64-bit <c>.dylib</c> modules and <c>HoN64</c> prior to launch. Only ad-hoc re-signs a binary if
    ///     <c>/usr/bin/codesign --verify --strict</c> fails on that binary.
    /// </summary>
    public static void EnsureMacOsBundlePrepared(
        string? workingDirectory,
        Action<string, string>? log = null)
    {
        if (!OperatingSystem.IsMacOS() || string.IsNullOrWhiteSpace(workingDirectory) || !Directory.Exists(workingDirectory))
        {
            return;
        }

        string macosRoot = Directory.Exists(Path.Combine(workingDirectory, "macos-x64"))
            ? Path.Combine(workingDirectory, "macos-x64")
            : workingDirectory;

        try
        {
            int xattrExit = RunMacOsTool("/usr/bin/xattr", ["-cr", macosRoot], out _, out string xattrErr, timeoutMs: 30_000);
            if (xattrExit != 0)
            {
                log?.Invoke(LogCategory.Executable,
                    $"[MACOS_XATTR_WARN] /usr/bin/xattr -cr '{macosRoot}' exited with {xattrExit}: {xattrErr.Trim()}. Fix: Run /usr/bin/xattr -cr \"{macosRoot}\"");
            }
        }
        catch (Exception ex)
        {
            log?.Invoke(LogCategory.Executable, $"[MACOS_BUNDLE_WARN] Notice clearing xattr on '{macosRoot}': {ex.Message}");
        }

        IReadOnlyList<string> signableTargets = EnumerateMacOsSignableTargets(macosRoot);
        foreach (string target in signableTargets)
        {
            try
            {
                if (!File.Exists(target))
                {
                    continue;
                }

                int verifyExit = RunMacOsTool("/usr/bin/codesign", ["--verify", "--strict", target], out _, out string verifyErr, timeoutMs: 30_000);
                if (verifyExit != 0)
                {
                    log?.Invoke(LogCategory.Executable,
                        $"[MACOS_CODESIGN] Signature verification failed for '{target}' ({verifyErr.Trim()}); applying targeted ad-hoc signature (--force --sign -).");
                    int signExit = RunMacOsTool("/usr/bin/codesign", ["--force", "--sign", "-", target], out _, out string signErr, timeoutMs: 30_000);
                    if (signExit != 0)
                    {
                        log?.Invoke(LogCategory.Executable,
                            $"[MACOS_CODESIGN_ERR] Failed to ad-hoc sign '{target}' (exit {signExit}): {signErr.Trim()}. Fix: Install Xcode Command Line Tools (`xcode-select --install`) and run /usr/bin/codesign --force --sign - \"{target}\"");
                    }
                }
            }
            catch (Exception targetEx)
            {
                log?.Invoke(LogCategory.Executable,
                    $"[MACOS_BUNDLE_WARN] Notice verifying/signing macOS binary '{target}': {targetEx.Message}");
            }
        }
    }

    public static int RunMacOsTool(
        string fileName,
        string[] args,
        out string stdout,
        out string stderr,
        int timeoutMs = 30_000)
    {
        stdout = string.Empty;
        stderr = string.Empty;
        if (!File.Exists(fileName))
        {
            return -1;
        }

        ProcessStartInfo psi = new(fileName)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        psi.Environment["LC_ALL"] = "C";
        psi.Environment["LANG"] = "C";
        foreach (string arg in args)
        {
            psi.ArgumentList.Add(arg);
        }

        using Process? proc = Process.Start(psi);
        if (proc is null)
        {
            return -1;
        }

        StringBuilder outBuilder = new();
        StringBuilder errBuilder = new();
        proc.OutputDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                outBuilder.AppendLine(e.Data);
            }
        };
        proc.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                errBuilder.AppendLine(e.Data);
            }
        };

        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();

        bool exited = proc.WaitForExit(timeoutMs);
        if (!exited)
        {
            try
            {
                proc.Kill(entireProcessTree: true);
            }
            catch (Exception killEx)
            {
                stderr = $"Process '{fileName}' timed out after {timeoutMs}ms and kill threw: {killEx.Message}";
                return -2;
            }

            stderr = $"Process '{fileName}' timed out after {timeoutMs}ms.";
            return -2;
        }

        proc.WaitForExit();
        stdout = outBuilder.ToString();
        stderr = errBuilder.ToString();
        return proc.ExitCode;
    }

    /// <summary>
    ///     Resolves the process launch command for <c>hon-x86_64</c> on <c>aarch64</c> / <c>arm64</c> Linux hosts.
    ///     When kernel <c>binfmt_misc</c> is already registered for x86_64 (e.g. SteamOS on ARM / FEX-Emu), returns
    ///     the executable directly unless <c>KONGOR_X86_EMULATOR</c> is explicitly set. Otherwise searches <c>PATH</c>
    ///     for <c>FEXInterpreter</c> or <c>box64</c> and wraps the launch arguments.
    /// </summary>
    public static (string FileName, string Arguments) ResolveLinuxArm64TranslationCommand(
        string executableFullPath,
        string arguments,
        bool? forceArm64Linux = null,
        bool? hasBinfmtRegistration = null,
        string? searchPathOverride = null,
        string? explicitEmulatorOverride = null)
    {
        bool isArm64Linux = forceArm64Linux ??
                            (OperatingSystem.IsLinux() && RuntimeInformation.ProcessArchitecture == Architecture.Arm64);
        if (!isArm64Linux)
        {
            return (executableFullPath, arguments);
        }

        string? explicitEmulator = explicitEmulatorOverride ?? Environment.GetEnvironmentVariable("KONGOR_X86_EMULATOR");
        if (!string.IsNullOrWhiteSpace(explicitEmulator))
        {
            string wrappedArgs = string.IsNullOrWhiteSpace(arguments)
                ? $"\"{executableFullPath}\""
                : $"\"{executableFullPath}\" {arguments}";
            return (explicitEmulator.Trim(), wrappedArgs);
        }

        bool binfmtActive = hasBinfmtRegistration ?? HasLinuxX86BinfmtRegistration();
        if (binfmtActive)
        {
            return (executableFullPath, arguments);
        }

        string pathEnv = searchPathOverride ?? Environment.GetEnvironmentVariable("PATH") ?? "/usr/local/bin:/usr/bin:/bin";
        char separator = searchPathOverride is not null && searchPathOverride.Contains(';') && !searchPathOverride.Contains(':')
            ? ';'
            : Path.PathSeparator;
        string[] searchDirs = pathEnv.Split(separator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        string[] candidateTranslators = ["FEXInterpreter", "box64", "qemu-x86_64-static", "qemu-x86_64"];

        foreach (string translatorName in candidateTranslators)
        {
            foreach (string dir in searchDirs)
            {
                string candidatePath = Path.Combine(dir, translatorName);
                if (File.Exists(candidatePath))
                {
                    string wrappedArgs = string.IsNullOrWhiteSpace(arguments)
                        ? $"\"{executableFullPath}\""
                        : $"\"{executableFullPath}\" {arguments}";
                    return (candidatePath, wrappedArgs);
                }
            }
        }

        return (executableFullPath, arguments);
    }

    private static bool HasLinuxX86BinfmtRegistration()
    {
        string[] binfmtEntries =
        [
            "/proc/sys/fs/binfmt_misc/FEX-x86_64",
            "/proc/sys/fs/binfmt_misc/box64",
            "/proc/sys/fs/binfmt_misc/qemu-x86_64"
        ];

        foreach (string entry in binfmtEntries)
        {
            if (File.Exists(entry))
            {
                return true;
            }
        }

        return false;
    }

    private static string? _cachedLinuxGraphicsStatusSummary;

    /// <summary>
    ///     Configures Linux OpenGL/Mesa/NVIDIA environment variables for <c>hon-x86_64</c>:
    ///     <list type="bullet">
    ///         <item>Enables background OpenGL command dispatch (<c>mesa_glthread=true</c> and <c>__GL_THREADED_OPTIMIZATIONS=1</c>) by default unless disabled via <c>KONGOR_GLTHREAD=0</c>.</item>
    ///         <item>Points persistent shader disk caches (<c>MESA_SHADER_CACHE_DIR</c>, <c>MESA_DISK_CACHE_SINGLE_FILE=1</c>, <c>__GL_SHADER_DISK_CACHE_PATH</c>) at <c>&lt;workingDirectory&gt;/userdata/shadercache</c>.</item>
    ///         <item>Preserves native OpenGL on NVIDIA and hardware Mesa OpenGL, promoting Mesa Zink (<c>MESA_LOADER_DRIVER_OVERRIDE=zink</c>) ONLY when <c>KONGOR_USE_ZINK=1</c> OR (<c>KONGOR_USE_ZINK</c> is unset AND <c>glxinfo -B</c> explicitly reports software rasterization <c>llvmpipe</c>/<c>softpipe</c>/<c>swrast</c> alongside a non-lavapipe hardware Vulkan GPU/ICD).</item>
    ///     </list>
    /// </summary>
    public static void ConfigureLinuxGraphicsEnvironment(
        StringDictionary environmentVariables,
        string workingDirectory,
        Action<string, string>? log = null,
        string? useZinkOverride = null,
        string? forceMesaGlxOverride = null,
        string? glThreadOverride = null,
        string? glxinfoOutputOverride = null,
        string? vulkaninfoOutputOverride = null,
        IReadOnlyList<string>? vulkanIcdDirsOverride = null)
    {
        ArgumentNullException.ThrowIfNull(environmentVariables);

        string baseDir = string.IsNullOrWhiteSpace(workingDirectory)
            ? Environment.CurrentDirectory
            : workingDirectory;
        string shaderCacheDir = Path.Combine(baseDir, "userdata", "shadercache");
        try
        {
            if (!Directory.Exists(shaderCacheDir))
            {
                Directory.CreateDirectory(shaderCacheDir);
            }
        }
        catch (Exception ex)
        {
            log?.Invoke(LogCategory.Executable, $"[LinuxGL_WARN] Could not create shader cache directory '{shaderCacheDir}': {ex.Message}");
        }

        environmentVariables["MESA_SHADER_CACHE_DIR"] = shaderCacheDir;
        environmentVariables["MESA_SHADER_CACHE_MAX_SIZE"] = "2G";
        environmentVariables["MESA_DISK_CACHE_SINGLE_FILE"] = "1";
        environmentVariables["__GL_SHADER_DISK_CACHE"] = "1";
        environmentVariables["__GL_SHADER_DISK_CACHE_PATH"] = shaderCacheDir;
        environmentVariables["__GL_SHADER_DISK_CACHE_SKIP_CLEANUP"] = "1";
        log?.Invoke(LogCategory.Executable,
            $"[LinuxGL] Configured persistent shader cache at '{shaderCacheDir}' (MESA_DISK_CACHE_SINGLE_FILE=1, __GL_SHADER_DISK_CACHE=1).");

        string glThreadValue = glThreadOverride ?? Environment.GetEnvironmentVariable("KONGOR_GLTHREAD") ?? "1";
        bool enableGlThread = !string.Equals(glThreadValue, "0", StringComparison.Ordinal) &&
                              !string.Equals(glThreadValue, "false", StringComparison.OrdinalIgnoreCase);
        if (enableGlThread)
        {
            environmentVariables["mesa_glthread"] = "true";
            environmentVariables["__GL_THREADED_OPTIMIZATIONS"] = "1";
            log?.Invoke(LogCategory.Executable,
                "[LinuxGL] Enabled multi-threaded OpenGL command submission (mesa_glthread=true, __GL_THREADED_OPTIMIZATIONS=1).");
        }
        else
        {
            environmentVariables.Remove("mesa_glthread");
            environmentVariables.Remove("__GL_THREADED_OPTIMIZATIONS");
            log?.Invoke(LogCategory.Executable,
                "[LinuxGL] Multi-threaded OpenGL command submission disabled via KONGOR_GLTHREAD=0.");
        }

        string? rawUseZink = useZinkOverride ?? Environment.GetEnvironmentVariable("KONGOR_USE_ZINK");
        string normalizedUseZink = (rawUseZink ?? string.Empty).Trim();
        string forceMesaGlx = forceMesaGlxOverride ?? Environment.GetEnvironmentVariable("KONGOR_FORCE_MESA_GLX") ?? "0";

        if (string.Equals(normalizedUseZink, "1", StringComparison.Ordinal) ||
            string.Equals(normalizedUseZink, "true", StringComparison.OrdinalIgnoreCase))
        {
            environmentVariables["MESA_LOADER_DRIVER_OVERRIDE"] = "zink";
            if (string.Equals(forceMesaGlx, "1", StringComparison.Ordinal))
            {
                environmentVariables["__GLX_VENDOR_LIBRARY_NAME"] = "mesa";
            }

            log?.Invoke(LogCategory.Executable,
                $"[LinuxCompat] Enabled opt-in Mesa Zink OpenGL-over-Vulkan translation (MESA_LOADER_DRIVER_OVERRIDE=zink, forceMesaGlx={forceMesaGlx}).");
        }
        else if (string.Equals(normalizedUseZink, "0", StringComparison.Ordinal) ||
                 string.Equals(normalizedUseZink, "false", StringComparison.OrdinalIgnoreCase))
        {
            environmentVariables.Remove("MESA_LOADER_DRIVER_OVERRIDE");
            log?.Invoke(LogCategory.Executable,
                $"[LinuxCompat] Mesa Zink disabled via KONGOR_USE_ZINK={normalizedUseZink}; preserving native OpenGL (vid_gl2).");
        }
        else
        {
            (string openGlRenderer, string vulkanDeviceName, string vulkanDeviceType, bool hasHardwareVulkanGpu, _) =
                ProbeLinuxGraphicsTelemetry(
                    log,
                    glxinfoOutputOverride: glxinfoOutputOverride,
                    vulkaninfoOutputOverride: vulkaninfoOutputOverride,
                    vulkanIcdDirsOverride: vulkanIcdDirsOverride);

            bool isSoftwareGl = IsSoftwareOpenGlRenderer(openGlRenderer);
            if (isSoftwareGl && hasHardwareVulkanGpu)
            {
                environmentVariables["MESA_LOADER_DRIVER_OVERRIDE"] = "zink";
                if (string.Equals(forceMesaGlx, "1", StringComparison.Ordinal))
                {
                    environmentVariables["__GLX_VENDOR_LIBRARY_NAME"] = "mesa";
                }

                log?.Invoke(LogCategory.Executable,
                    $"[LinuxCompat] Auto-Rescue Zink: Software OpenGL ('{openGlRenderer}') detected alongside hardware Vulkan ('{vulkanDeviceName}', '{vulkanDeviceType}'); promoting MESA_LOADER_DRIVER_OVERRIDE=zink.");
            }
            else if (isSoftwareGl)
            {
                log?.Invoke(LogCategory.Executable,
                    $"[LinuxCompat] Software OpenGL ('{openGlRenderer}') detected without a non-lavapipe hardware Vulkan GPU/ICD (device='{vulkanDeviceName}', type='{vulkanDeviceType}'); keeping native vid_gl2.");
            }
            else
            {
                string rendererLabel = string.IsNullOrWhiteSpace(openGlRenderer) ? "unprobed" : openGlRenderer;
                log?.Invoke(LogCategory.Executable,
                    $"[LinuxGL] Preserving native OpenGL renderer ('{rendererLabel}') with vid_gl2 + glthread (Mesa Zink not promoted).");
            }
        }
    }

    public static bool IsSoftwareOpenGlRenderer(string? openGlRenderer)
    {
        if (string.IsNullOrWhiteSpace(openGlRenderer))
        {
            return false;
        }

        return openGlRenderer.Contains("llvmpipe", StringComparison.OrdinalIgnoreCase) ||
               openGlRenderer.Contains("softpipe", StringComparison.OrdinalIgnoreCase) ||
               openGlRenderer.Contains("swrast", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsHardwareVulkanBlock(string deviceName, string deviceType, string driverName)
    {
        if (string.IsNullOrWhiteSpace(deviceType))
        {
            return false;
        }

        bool hasHardwareType =
            deviceType.Contains("DISCRETE_GPU", StringComparison.OrdinalIgnoreCase) ||
            deviceType.Contains("INTEGRATED_GPU", StringComparison.OrdinalIgnoreCase) ||
            deviceType.Contains("VIRTUAL_GPU", StringComparison.OrdinalIgnoreCase);

        if (!hasHardwareType)
        {
            return false;
        }

        return !deviceName.Contains("llvmpipe", StringComparison.OrdinalIgnoreCase) &&
               !deviceName.Contains("lavapipe", StringComparison.OrdinalIgnoreCase) &&
               !driverName.Contains("llvmpipe", StringComparison.OrdinalIgnoreCase) &&
               !driverName.Contains("lavapipe", StringComparison.OrdinalIgnoreCase) &&
               !string.Equals(driverName.Trim(), "lvp", StringComparison.OrdinalIgnoreCase);
    }

    private static (string DeviceName, string DeviceType, string DriverName, bool IsHardwareVulkan)
        ParseVulkanSummaryDevices(string vkOutput)
    {
        if (string.IsNullOrWhiteSpace(vkOutput))
        {
            return (string.Empty, string.Empty, string.Empty, false);
        }

        List<(string DeviceName, string DeviceType, string DriverName)> blocks = [];
        string curName = string.Empty;
        string curType = string.Empty;
        string curDriver = string.Empty;

        void FlushBlock()
        {
            if (!string.IsNullOrWhiteSpace(curName) ||
                !string.IsNullOrWhiteSpace(curType) ||
                !string.IsNullOrWhiteSpace(curDriver))
            {
                blocks.Add((curName, curType, curDriver));
            }
            curName = string.Empty;
            curType = string.Empty;
            curDriver = string.Empty;
        }

        string[] lines = vkOutput.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        foreach (string rawLine in lines)
        {
            string line = rawLine.Trim();
            if (Regex.IsMatch(line, @"^GPU\d+\s*:", RegexOptions.IgnoreCase))
            {
                FlushBlock();
                continue;
            }

            if (line.StartsWith("deviceName", StringComparison.OrdinalIgnoreCase))
            {
                curName = ExtractKeyValueRemainder(line, "deviceName");
            }
            else if (line.StartsWith("deviceType", StringComparison.OrdinalIgnoreCase))
            {
                curType = ExtractKeyValueRemainder(line, "deviceType");
            }
            else if (line.StartsWith("driverName", StringComparison.OrdinalIgnoreCase))
            {
                curDriver = ExtractKeyValueRemainder(line, "driverName");
            }
        }
        FlushBlock();

        foreach ((string devName, string devType, string drvName) in blocks)
        {
            if (IsHardwareVulkanBlock(devName, devType, drvName))
            {
                return (devName, devType, drvName, true);
            }
        }

        if (blocks.Count > 0)
        {
            return (blocks[0].DeviceName, blocks[0].DeviceType, blocks[0].DriverName, false);
        }

        return (string.Empty, string.Empty, string.Empty, false);
    }

    /// <summary>
    ///     Runs out-of-process <c>glxinfo -B</c> and <c>vulkaninfo --summary</c> probes (never <c>dlopen</c>ing Vulkan
    ///     inside the launcher process) to inspect the active OpenGL renderer and Vulkan physical device type
    ///     (<c>DISCRETE_GPU</c> / <c>INTEGRATED_GPU</c> vs <c>CPU</c> <c>lavapipe</c>/<c>llvmpipe</c>).
    /// </summary>
    public static (string OpenGlRenderer, string VulkanDeviceName, string VulkanDeviceType, bool HasHardwareVulkanGpu, string StatusSummary)
        ProbeLinuxGraphicsTelemetry(
            Action<string, string>? log = null,
            string? glxinfoOutputOverride = null,
            string? vulkaninfoOutputOverride = null,
            IReadOnlyList<string>? vulkanIcdDirsOverride = null)
    {
        string glxOutput = glxinfoOutputOverride ?? string.Empty;
        if (glxinfoOutputOverride is null && OperatingSystem.IsLinux())
        {
            string? glxinfoBin = ResolvePosixToolInPath("glxinfo");
            if (glxinfoBin is not null)
            {
                RunMacOsTool(glxinfoBin, ["-B"], out glxOutput, out _, timeoutMs: 3_000);
            }
        }

        string vkOutput = vulkaninfoOutputOverride ?? string.Empty;
        if (vulkaninfoOutputOverride is null && OperatingSystem.IsLinux())
        {
            string? vulkaninfoBin = ResolvePosixToolInPath("vulkaninfo");
            if (vulkaninfoBin is not null)
            {
                RunMacOsTool(vulkaninfoBin, ["--summary"], out vkOutput, out _, timeoutMs: 3_000);
            }
        }

        string openGlRenderer = ExtractKeyValueLine(glxOutput, "OpenGL renderer string:");
        string openGlVersion = ExtractKeyValueLine(glxOutput, "OpenGL version string:");
        (string vulkanDeviceName, string vulkanDeviceType, string vulkanDriverName, bool isHardwareVulkan) =
            ParseVulkanSummaryDevices(vkOutput);

        if (string.IsNullOrWhiteSpace(vulkanDeviceName) && (OperatingSystem.IsLinux() || vulkanIcdDirsOverride is not null))
        {
            IReadOnlyList<string> icdDirs = vulkanIcdDirsOverride ?? ["/usr/share/vulkan/icd.d", "/etc/vulkan/icd.d"];
            List<string> hardwareIcds = [];
            foreach (string icdDir in icdDirs)
            {
                if (string.IsNullOrWhiteSpace(icdDir) || !Directory.Exists(icdDir))
                {
                    continue;
                }

                try
                {
                    foreach (string icdJson in Directory.EnumerateFiles(icdDir, "*.json"))
                    {
                        string icdName = Path.GetFileName(icdJson);
                        if (icdName.StartsWith("lvp_", StringComparison.OrdinalIgnoreCase) ||
                            icdName.Contains("lavapipe", StringComparison.OrdinalIgnoreCase) ||
                            icdName.Contains("llvmpipe", StringComparison.OrdinalIgnoreCase) ||
                            icdName.Contains("softpipe", StringComparison.OrdinalIgnoreCase) ||
                            icdName.Contains("swrast", StringComparison.OrdinalIgnoreCase) ||
                            icdName.Contains(".i686.", StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        if (!hardwareIcds.Contains(icdName, StringComparer.OrdinalIgnoreCase))
                        {
                            hardwareIcds.Add(icdName);
                        }
                    }
                }
                catch (Exception ex)
                {
                    log?.Invoke(LogCategory.Executable,
                        $"[LinuxGPUProbe_WARN] Could not scan Vulkan ICD directory '{icdDir}': {ex.Message}");
                }
            }

            if (hardwareIcds.Count > 0)
            {
                vulkanDeviceName = $"ICD:{string.Join(",", hardwareIcds)}";
                vulkanDeviceType = "HARDWARE_ICD_MANIFEST";
                isHardwareVulkan = true;
            }
        }

        bool isSoftwareGl = IsSoftwareOpenGlRenderer(openGlRenderer);

        string statusSummary;
        if (!string.IsNullOrWhiteSpace(openGlRenderer))
        {
            statusSummary = isSoftwareGl
                ? $"Linux Software OpenGL Active (vid_gl2 | {openGlRenderer})"
                : $"Linux Native OpenGL Active (vid_gl2 + glthread | {openGlRenderer})";
        }
        else
        {
            statusSummary = "Linux Native OpenGL Engine Active (vid_gl2 + glthread)";
        }

        log?.Invoke(LogCategory.Executable,
            $"[LinuxGPUProbe] OpenGL Renderer='{openGlRenderer}' (Version='{openGlVersion}'), " +
            $"Vulkan Device='{vulkanDeviceName}' (Type='{vulkanDeviceType}', Driver='{vulkanDriverName}', HardwareVk={isHardwareVulkan}).");

        return (openGlRenderer, vulkanDeviceName, vulkanDeviceType, isHardwareVulkan, statusSummary);
    }

    public static string GetCachedLinuxGraphicsStatusSummary()
    {
        if (!string.IsNullOrWhiteSpace(_cachedLinuxGraphicsStatusSummary))
        {
            return _cachedLinuxGraphicsStatusSummary;
        }

        if (!OperatingSystem.IsLinux())
        {
            return "Linux Native OpenGL Engine Active (vid_gl2 + glthread)";
        }

        (_, _, _, _, string summary) = ProbeLinuxGraphicsTelemetry();
        _cachedLinuxGraphicsStatusSummary = summary;
        return summary;
    }

    private static string ExtractKeyValueRemainder(string trimmedLine, string keyPrefix)
    {
        string remainder = trimmedLine[keyPrefix.Length..].Trim();
        if (remainder.StartsWith('='))
        {
            remainder = remainder[1..].Trim();
        }

        return remainder;
    }

    private static string ExtractKeyValueLine(string text, string keyPrefix)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        string[] lines = text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        foreach (string rawLine in lines)
        {
            string line = rawLine.Trim();
            if (!line.StartsWith(keyPrefix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            return ExtractKeyValueRemainder(line, keyPrefix);
        }

        return string.Empty;
    }

    private static string? ResolvePosixToolInPath(string binaryName)
    {
        List<string> candidateDirs = [];
        string? pathEnv = Environment.GetEnvironmentVariable("PATH");
        if (!string.IsNullOrWhiteSpace(pathEnv))
        {
            candidateDirs.AddRange(pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        }

        candidateDirs.AddRange(["/usr/bin", "/usr/local/bin", "/bin", "/sbin", "/usr/sbin"]);

        foreach (string dir in candidateDirs)
        {
            try
            {
                string fullPath = Path.Combine(dir, binaryName);
                if (File.Exists(fullPath))
                {
                    return fullPath;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.WriteLine($"[LinuxGPUProbe] Ignoring invalid PATH directory '{dir}': {ex.Message}");
            }
        }

        return null;
    }
}
