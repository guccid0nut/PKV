// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Collections.Specialized;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ASPIRE.Common.Vulkan;
using WILLOWMAKER.Core.Services.Serialization;

namespace WILLOWMAKER.Core.Services.Diagnostics;

public sealed class LaunchReceiptConfigSummaryDto
{
    public string Path { get; set; } = string.Empty;
    public bool Exists { get; set; }
    public string Sha256 { get; set; } = string.Empty;
    public Dictionary<string, string> Settings { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class LaunchReceiptModuleHashDto
{
    public string ModuleName { get; set; } = string.Empty;
    public string FilePath { get; set; } = string.Empty;
    public string Sha256 { get; set; } = string.Empty;
}

public sealed class LaunchReceiptPostBootAuditDto
{
    public string AuditedAtUtc { get; set; } = string.Empty;
    public int ProcessId { get; set; }
    public bool ProcessAlive { get; set; }
    public int TotalLoadedModules { get; set; }
    public bool VidVulkanLoaded { get; set; }
    public bool D3d9Loaded { get; set; }
    public bool VersionDllLoaded { get; set; }
    public bool VkLayerReplayCaptureLoaded { get; set; }
    public bool ExpectedVkLayerReplayCaptureLoaded { get; set; }
    public List<string> DetectedDeniedHooks { get; set; } = [];
    public List<LaunchReceiptModuleHashDto> KeyModules { get; set; } = [];
    public bool Passed { get; set; }
    public List<string> Findings { get; set; } = [];
}

public sealed class LaunchReceiptDto
{
    public int SchemaVersion { get; set; } = 1;
    public string TimestampUtc { get; set; } = string.Empty;
    public string WillowmakerExecutable { get; set; } = string.Empty;
    public string WillowmakerDllSha256 { get; set; } = string.Empty;
    public string ClientExecutable { get; set; } = string.Empty;
    public string WorkingDirectory { get; set; } = string.Empty;
    public int GamePid { get; set; }
    public bool IsSafeMode { get; set; }
    public string SelectedVulkanHudMode { get; set; } = string.Empty;
    public string SelectedRayTracingPreset { get; set; } = string.Empty;
    public string SelectedShadowFidelity { get; set; } = string.Empty;
    public string SelectedPresentationMode { get; set; } = string.Empty;
    public bool AllowThirdPartyVulkanOverlays { get; set; }
    public bool ExpectedVkLayerReplayCaptureLoaded { get; set; }
    public List<string> RedactedArguments { get; set; } = [];
    public Dictionary<string, string> AllowlistedEnvironment { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public LaunchReceiptConfigSummaryDto DxvkConfig { get; set; } = new();
    public LaunchReceiptConfigSummaryDto AutoexecConfig { get; set; } = new();
    public LaunchReceiptPostBootAuditDto? PostBootAudit { get; set; }
}

/// <summary>
///     Writes a secret-free, allowlisted launch receipt on every WILLOWMAKER game launch
///     and performs a non-blocking 10-second post-boot module audit to catch silent Vulkan layer,
///     overlay hook, or config drift during real player sessions.
/// </summary>
public static class LaunchReceiptService
{
    public const string ReceiptFileName = "willowmaker_last_launch_receipt.json";

    private static readonly HashSet<string> AllowedEnvironmentKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "DXVK_CONFIG_FILE",
        "DXVK_SHADER_CACHE",
        "DXVK_SHADER_CACHE_PATH",
        "DXVK_STATE_CACHE_PATH",
        "DXVK_HUD",
        "ALSOFT_CONF",
        "VK_INSTANCE_LAYERS",
        "VK_LOADER_LAYERS_DISABLE",
        "VK_LOADER_LAYERS_ENABLE",
        "VK_LAYER_PATH",
        "VK_ADD_LAYER_PATH",
        "HON_MODERN_HUD",
        "HON_CAPTURE_ENABLE",
        "HON_CAPTURE_DISABLE",
        "HON_CAPTURE_SLOTS",
        "HON_FSR_ENABLE",
        "HON_FSR_SHARPNESS",
        "HON_FORCE_NATIVE_D3D9",
        "KONGOR_SAFE_MODE",
        "KONGOR_TARGET_MONITOR",
        "KONGOR_MANUAL_FPS_OVERRIDE",
        "KONGOR_AUDIO_HRTF",
        "KONGOR_AUDIO_WASAPI",
        "KONGOR_HEADLESS"
    };

    private static readonly string[] DeniedOverlayHookModules =
    [
        "RTSSHooks64.dll",
        "DiscordHook64.dll",
        "graphics-hook64.dll",
        "obs-vulkan64.dll",
        "SteamOverlayVulkanLayer64.dll",
        "SteamFossilizeVulkanLayer64.dll",
        "renderdoc.dll",
        "MediaSDKVulkanLayer64.dll",
        "MedalHook64.dll"
    ];

    private static readonly HashSet<string> KeyAuditModules = new(StringComparer.OrdinalIgnoreCase)
    {
        "vid_vulkan_x64.dll",
        "d3d9.dll",
        "version.dll",
        "VkLayer_hon_replay_capture.dll"
    };

    private static readonly HashSet<string> TrackedDxvkKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "dxvk.hud",
        "dxgi.syncInterval",
        "d3d9.presentInterval",
        "d3d9.maxFrameRate",
        "d3d9.maxFrameLatency",
        "dxgi.maxFrameLatency",
        "dxvk.tearFree",
        "dxvk.latencySleep",
        "dxvk.enableLatencyReduction",
        "dxvk.nvLowLatency",
        "dxvk.disableNvLowLatency2",
        "dxvk.allowFse"
    };

    private static readonly HashSet<string> TrackedAutoexecCvars = new(StringComparer.OrdinalIgnoreCase)
    {
        "host_maxFPS",
        "vid_refreshRate",
        "vid_resolution",
        "vid_fullscreen",
        "vid_borderless",
        "d3d_exclusive",
        "cg_interfaceFPS",
        "vid_driver",
        "vid_shadowmapSize",
        "vid_shadowDrawDistance",
        "vid_antialiasing",
        "vid_reflectionMapSize",
        "cg_precacheResources",
        "vid_vsync"
    };

    private static readonly Regex SetSaveRegex = new(
        @"^\s*SetSave\s+""([^""]+)""\s+""([^""]*)""",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex SensitiveArgRegex = new(
        @"(?i)(login_password|password|cookie|token|auth_?key|secret)\s*[=\s]+(?:""[^""]*""|'[^']*'|\S+)",
        RegexOptions.Compiled);

    public static string SanitizeUserPath(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrWhiteSpace(userProfile) &&
            value.Contains(userProfile, StringComparison.OrdinalIgnoreCase))
        {
            return value.Replace(userProfile, "[USER_DIR]", StringComparison.OrdinalIgnoreCase);
        }

        return value;
    }

    public static string ResolveReceiptPath(string clientDir, string? explicitReceiptPath = null)
    {
        if (!string.IsNullOrWhiteSpace(explicitReceiptPath))
        {
            return explicitReceiptPath;
        }

        string fullClientDir = Path.GetFullPath(clientDir);
        DirectoryInfo? dir = new(fullClientDir);
        for (int depth = 0; depth < 4 && dir is not null; depth++)
        {
            if (File.Exists(Path.Combine(dir.FullName, "ProjectKongor.slnx")) ||
                File.Exists(Path.Combine(dir.FullName, "ProjectKongor.sln")))
            {
                return Path.Combine(dir.FullName, "logs", "shj", ReceiptFileName);
            }
            dir = dir.Parent;
        }

        return Path.Combine(fullClientDir, "logs", "shj", ReceiptFileName);
    }

    public static Dictionary<string, string> ExtractAllowlistedEnvironment(StringDictionary environmentVariables)
    {
        Dictionary<string, string> result = new(StringComparer.OrdinalIgnoreCase);
        foreach (string key in AllowedEnvironmentKeys)
        {
            if (!environmentVariables.ContainsKey(key))
            {
                continue;
            }

            string? val = environmentVariables[key];
            if (val is not null)
            {
                result[key] = SanitizeUserPath(val);
            }
        }

        foreach (System.Collections.DictionaryEntry entry in environmentVariables)
        {
            string? key = entry.Key as string;
            if (string.IsNullOrWhiteSpace(key) || result.ContainsKey(key))
            {
                continue;
            }

            if (VulkanImplicitLayerShield.IsVulkanShieldEnvironmentKey(key) && entry.Value is string val)
            {
                result[key] = SanitizeUserPath(val);
            }
        }

        return result;
    }

    public static List<string> RedactArguments(IEnumerable<string> arguments)
    {
        List<string> redacted = [];
        bool redactNext = false;
        foreach (string raw in arguments)
        {
            string arg = raw ?? string.Empty;
            if (redactNext)
            {
                redacted.Add("[REDACTED]");
                redactNext = false;
                continue;
            }

            if (arg.Equals("-password", StringComparison.OrdinalIgnoreCase) ||
                arg.Equals("--password", StringComparison.OrdinalIgnoreCase) ||
                arg.Equals("-pass", StringComparison.OrdinalIgnoreCase) ||
                arg.Equals("-cookie", StringComparison.OrdinalIgnoreCase) ||
                arg.Equals("-token", StringComparison.OrdinalIgnoreCase) ||
                arg.Equals("--token", StringComparison.OrdinalIgnoreCase) ||
                arg.Equals("-secret", StringComparison.OrdinalIgnoreCase) ||
                arg.Equals("login_password", StringComparison.OrdinalIgnoreCase))
            {
                redacted.Add(arg);
                redactNext = true;
                continue;
            }

            if (SensitiveArgRegex.IsMatch(arg))
            {
                redacted.Add(SensitiveArgRegex.Replace(arg, "$1 [REDACTED]"));
                continue;
            }

            redacted.Add(SanitizeUserPath(arg));
        }
        return redacted;
    }

    public static string ComputeFileSha256(string? path, Action<string>? log = null)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return string.Empty;
        }

        try
        {
            using FileStream fs = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            byte[] hash = SHA256.HashData(fs);
            return Convert.ToHexStringLower(hash);
        }
        catch (Exception ex)
        {
            log?.Invoke($"[LAUNCH_RECEIPT] SHA-256 read warning for '{path}': {ex.Message}");
            return string.Empty;
        }
    }

    public static LaunchReceiptConfigSummaryDto ParseDxvkConfigSummary(string dxvkConfPath, Action<string>? log = null)
    {
        LaunchReceiptConfigSummaryDto summary = new()
        {
            Path = SanitizeUserPath(dxvkConfPath),
            Exists = File.Exists(dxvkConfPath)
        };
        if (!summary.Exists)
        {
            return summary;
        }

        summary.Sha256 = ComputeFileSha256(dxvkConfPath, log);
        try
        {
            foreach (string line in File.ReadLines(dxvkConfPath))
            {
                string trimmed = line.Trim();
                if (string.IsNullOrEmpty(trimmed) || trimmed.StartsWith('#'))
                {
                    continue;
                }

                int eqIdx = trimmed.IndexOf('=');
                if (eqIdx <= 0)
                {
                    continue;
                }

                string key = trimmed[..eqIdx].Trim();
                string value = trimmed[(eqIdx + 1)..].Trim();
                int inlineComment = value.IndexOf('#');
                if (inlineComment >= 0)
                {
                    value = value[..inlineComment].Trim();
                }
                if (TrackedDxvkKeys.Contains(key))
                {
                    summary.Settings[key] = value;
                }
            }
        }
        catch (Exception ex)
        {
            log?.Invoke($"[LAUNCH_RECEIPT] Failed parsing dxvk.conf '{dxvkConfPath}': {ex.Message}");
        }

        return summary;
    }

    public static LaunchReceiptConfigSummaryDto ParseAutoexecSummary(string autoexecPath, Action<string>? log = null)
    {
        LaunchReceiptConfigSummaryDto summary = new()
        {
            Path = SanitizeUserPath(autoexecPath),
            Exists = File.Exists(autoexecPath)
        };
        if (!summary.Exists)
        {
            return summary;
        }

        summary.Sha256 = ComputeFileSha256(autoexecPath, log);
        try
        {
            foreach (string line in File.ReadLines(autoexecPath))
            {
                Match m = SetSaveRegex.Match(line);
                if (!m.Success)
                {
                    continue;
                }

                string key = m.Groups[1].Value.Trim();
                string value = m.Groups[2].Value.Trim();
                if (TrackedAutoexecCvars.Contains(key))
                {
                    summary.Settings[key] = value;
                }
            }
        }
        catch (Exception ex)
        {
            log?.Invoke($"[LAUNCH_RECEIPT] Failed parsing autoexec.cfg '{autoexecPath}': {ex.Message}");
        }

        return summary;
    }

    public static string WriteLaunchReceipt(
        string clientDir,
        string clientExecutablePath,
        int gamePid,
        IEnumerable<string> rawArguments,
        StringDictionary childEnvironment,
        string selectedVulkanHudMode,
        string selectedRayTracingPreset,
        string selectedShadowFidelity,
        string selectedPresentationMode,
        bool allowThirdPartyVulkanOverlays,
        bool expectedVkLayerReplayCaptureLoaded,
        bool isSafeMode,
        Action<string>? log = null,
        string? explicitReceiptPath = null)
    {
        string receiptPath = ResolveReceiptPath(clientDir, explicitReceiptPath);
        string? exePath = Environment.ProcessPath;
        string? dllPath = string.IsNullOrWhiteSpace(exePath)
            ? null
            : Path.ChangeExtension(exePath, ".dll");
        if (string.IsNullOrWhiteSpace(dllPath) || !File.Exists(dllPath))
        {
            dllPath = exePath;
        }

        string? envDxvkPath = childEnvironment.ContainsKey("DXVK_CONFIG_FILE") ? childEnvironment["DXVK_CONFIG_FILE"] : null;
        string dxvkPath = !string.IsNullOrWhiteSpace(envDxvkPath)
            ? envDxvkPath
            : Path.Combine(clientDir, "userdataKONGOR", "dxvk.conf");
        string autoexecPath = Path.Combine(clientDir, "userdataKONGOR", "configuration", "autoexec.cfg");

        LaunchReceiptDto dto = new()
        {
            SchemaVersion = 1,
            TimestampUtc = TimeProvider.System.GetUtcNow().ToString("O"),
            WillowmakerExecutable = SanitizeUserPath(exePath),
            WillowmakerDllSha256 = ComputeFileSha256(dllPath, log),
            ClientExecutable = SanitizeUserPath(clientExecutablePath),
            WorkingDirectory = SanitizeUserPath(clientDir),
            GamePid = gamePid,
            IsSafeMode = isSafeMode,
            SelectedVulkanHudMode = selectedVulkanHudMode,
            SelectedRayTracingPreset = selectedRayTracingPreset,
            SelectedShadowFidelity = selectedShadowFidelity,
            SelectedPresentationMode = selectedPresentationMode,
            AllowThirdPartyVulkanOverlays = allowThirdPartyVulkanOverlays,
            ExpectedVkLayerReplayCaptureLoaded = expectedVkLayerReplayCaptureLoaded,
            RedactedArguments = RedactArguments(rawArguments),
            AllowlistedEnvironment = ExtractAllowlistedEnvironment(childEnvironment),
            DxvkConfig = ParseDxvkConfigSummary(dxvkPath, log),
            AutoexecConfig = ParseAutoexecSummary(autoexecPath, log),
            PostBootAudit = null
        };

        WriteDtoAtomically(receiptPath, dto, log);
        log?.Invoke(
            $"[LAUNCH_RECEIPT] Wrote sanitized launch receipt to '{receiptPath}' (PID={gamePid}, ExpectedVkLayer={expectedVkLayerReplayCaptureLoaded}, EnvKeys={dto.AllowlistedEnvironment.Count}, DxvkMaxLat={dto.DxvkConfig.Settings.GetValueOrDefault("d3d9.maxFrameLatency", "unset")}, AutoexecMaxFps={dto.AutoexecConfig.Settings.GetValueOrDefault("host_maxFPS", "unset")}).");
        return receiptPath;
    }

    public static LaunchReceiptPostBootAuditDto EvaluateLoadedModules(
        int gamePid,
        IEnumerable<(string ModuleName, string FilePath)> loadedModules,
        bool expectedVkLayerReplayCaptureLoaded,
        bool allowThirdPartyVulkanOverlays)
    {
        List<(string ModuleName, string FilePath)> modulesList = loadedModules.ToList();
        HashSet<string> moduleSet = new(modulesList.Select(m => m.ModuleName), StringComparer.OrdinalIgnoreCase);

        bool vkLayerLoaded = moduleSet.Contains("VkLayer_hon_replay_capture.dll");
        bool vidVulkanLoaded = moduleSet.Contains("vid_vulkan_x64.dll");
        bool d3d9Loaded = moduleSet.Contains("d3d9.dll");
        bool versionLoaded = moduleSet.Contains("version.dll");

        List<string> deniedHits = [];
        if (!allowThirdPartyVulkanOverlays)
        {
            foreach (string denied in DeniedOverlayHookModules)
            {
                if (moduleSet.Contains(denied))
                {
                    deniedHits.Add(denied);
                }
            }
        }

        List<LaunchReceiptModuleHashDto> keyModuleHashes = [];
        foreach ((string modName, string filePath) in modulesList)
        {
            if (KeyAuditModules.Contains(modName))
            {
                keyModuleHashes.Add(new LaunchReceiptModuleHashDto
                {
                    ModuleName = modName,
                    FilePath = SanitizeUserPath(filePath),
                    Sha256 = ComputeFileSha256(filePath)
                });
            }
        }

        List<string> findings = [];
        if (vkLayerLoaded != expectedVkLayerReplayCaptureLoaded)
        {
            findings.Add(
                $"VkLayer_hon_replay_capture.dll drift: loaded={vkLayerLoaded}, expected={expectedVkLayerReplayCaptureLoaded}.");
        }
        if (deniedHits.Count > 0)
        {
            findings.Add($"Denied third-party overlay/Vulkan hook(s) loaded: {string.Join(", ", deniedHits)}.");
        }

        return new LaunchReceiptPostBootAuditDto
        {
            AuditedAtUtc = TimeProvider.System.GetUtcNow().ToString("O"),
            ProcessId = gamePid,
            ProcessAlive = true,
            TotalLoadedModules = modulesList.Count,
            VidVulkanLoaded = vidVulkanLoaded,
            D3d9Loaded = d3d9Loaded,
            VersionDllLoaded = versionLoaded,
            VkLayerReplayCaptureLoaded = vkLayerLoaded,
            ExpectedVkLayerReplayCaptureLoaded = expectedVkLayerReplayCaptureLoaded,
            DetectedDeniedHooks = deniedHits,
            KeyModules = keyModuleHashes,
            Passed = findings.Count == 0,
            Findings = findings
        };
    }

    public static async Task RunPostBootModuleAuditAsync(
        int gamePid,
        string receiptPath,
        bool expectedVkLayerReplayCaptureLoaded,
        bool allowThirdPartyVulkanOverlays,
        TimeSpan delay,
        Action<string>? log = null,
        CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows() || gamePid <= 0 || string.IsNullOrWhiteSpace(receiptPath))
        {
            return;
        }

        try
        {
            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        try
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            using Process proc = Process.GetProcessById(gamePid);
            if (proc.HasExited)
            {
                return;
            }

            // Guard against OS PID reuse if hon_x64 exited quickly and another process took its PID
            if (!proc.ProcessName.Contains("hon", StringComparison.OrdinalIgnoreCase))
            {
                log?.Invoke($"[LAUNCH_RECEIPT][POST_BOOT_AUDIT] Skipping PID {gamePid}: process name '{proc.ProcessName}' does not match HoN client (PID reused).");
                return;
            }

            List<(string ModuleName, string FilePath)> loaded = [];
            foreach (ProcessModule mod in proc.Modules)
            {
                loaded.Add((mod.ModuleName ?? string.Empty, mod.FileName ?? string.Empty));
            }

            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            LaunchReceiptPostBootAuditDto audit = EvaluateLoadedModules(
                gamePid,
                loaded,
                expectedVkLayerReplayCaptureLoaded,
                allowThirdPartyVulkanOverlays);

            if (!cancellationToken.IsCancellationRequested && File.Exists(receiptPath))
            {
                string existingJson = File.ReadAllText(receiptPath, Encoding.UTF8);
                LaunchReceiptDto? dto = JsonSerializer.Deserialize(existingJson, WillowmakerJsonSerializerContext.Default.LaunchReceiptDto);
                if (dto is not null && dto.GamePid == gamePid)
                {
                    dto.PostBootAudit = audit;
                    WriteDtoAtomically(receiptPath, dto, log);
                }
            }

            if (audit.Passed)
            {
                log?.Invoke(
                    $"[LAUNCH_RECEIPT][POST_BOOT_AUDIT] PASS (PID={gamePid}, Modules={audit.TotalLoadedModules}, VkLayerLoaded={audit.VkLayerReplayCaptureLoaded}, VidVulkan={audit.VidVulkanLoaded}, DeniedHooks=0).");
            }
            else
            {
                log?.Invoke(
                    $"[LAUNCH_RECEIPT][POST_BOOT_AUDIT][WARN] DRIFT DETECTED (PID={gamePid}): {string.Join(" | ", audit.Findings)}");
            }
        }
        catch (Exception ex)
        {
            log?.Invoke($"[LAUNCH_RECEIPT][POST_BOOT_AUDIT] Skipped for PID {gamePid}: {ex.Message}");
        }
    }

    private static void WriteDtoAtomically(string receiptPath, LaunchReceiptDto dto, Action<string>? log)
    {
        string? tempPath = null;
        try
        {
            string? dir = Path.GetDirectoryName(receiptPath);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            string json = JsonSerializer.Serialize(dto, WillowmakerJsonSerializerContext.Default.LaunchReceiptDto);
            tempPath = $"{receiptPath}.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp";
            File.WriteAllText(tempPath, json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            File.Move(tempPath, receiptPath, overwrite: true);
            tempPath = null;
        }
        catch (Exception ex)
        {
            log?.Invoke($"[LAUNCH_RECEIPT] Failed writing receipt '{receiptPath}': {ex.Message}");
            if (!string.IsNullOrEmpty(tempPath) && File.Exists(tempPath))
            {
                try
                {
                    File.Delete(tempPath);
                }
                catch (Exception cleanupEx)
                {
                    log?.Invoke($"[LAUNCH_RECEIPT] Failed cleaning up temp file '{tempPath}': {cleanupEx.Message}");
                }
            }
        }
    }
}
