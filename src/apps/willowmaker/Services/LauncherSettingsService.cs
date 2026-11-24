// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using WILLOWMAKER.Core.Models;
using WILLOWMAKER.Core.Services.Serialization;

namespace WILLOWMAKER.Core.Services;

public sealed class LauncherSettingsModel
{
    public const string DefaultLocalOfflineAutoMasterServer = "Local / Offline Auto (localhost:55555)";
    public const string OfflineModeSelection = "Offline Mode";
    public const int CurrentSettingsSchemaVersion = 2;

    public int SettingsSchemaVersion { get; set; } = 0;
    public bool EnableRayTracing { get; set; }
    public string RayTracingPresetName { get; set; } = "Competitive";
    public bool EnableAsyncPipelineCaching { get; set; } = true;
    public bool EnableTextureClarityBoost { get; set; } = true;
    public string RefreshRate { get; set; } = "240 Hz";
    public string DisplayMode { get; set; } = "Borderless Windowed";
    public string Resolution { get; set; } = "1920x1080 (1080p FHD)";
    public string? SelectedMonitorDeviceName { get; set; }
    public bool ManualRefreshRateOverrideEnabled { get; set; } = false;
    public int? ManualMaxFpsOverride { get; set; }
    public string ShadowFidelity { get; set; } = "High (2048)";
    public string VulkanHudMode { get; set; } = "Off (Clean Screen)";
    public bool EnableNvidiaReflex { get; set; } = false;
    public bool EnableFidelityFxRcas { get; set; } = true;
    public float FidelityFxRcasSharpness { get; set; } = 0.25f;
    public string MasterServerAddress { get; set; } = DefaultLocalOfflineAutoMasterServer;
    public string CustomMasterServerAddress { get; set; } = string.Empty;
    public string SelectedCDNAddress { get; set; } = string.Empty;
    public string CustomCDNAddress { get; set; } = string.Empty;
    public int FpsCap { get; set; } = 240;
    public bool EnableSpatialAudio { get; set; } = false;
    public bool AllowThirdPartyVulkanOverlays { get; set; } = false;
    public string SpatialHrtfProfile { get; set; } = "Built-In 48000hz";
    public int SpatialAudioChannels { get; set; } = 128;
    public bool EnableOcclusionDamping { get; set; } = true;
    public bool EnableQuicHttp3 { get; set; } = true;
    public bool EnableDirectFlip { get; set; } = true;
    public string PresentationMode { get; set; } = "DirectFlip (FLIP_DISCARD)";
    public string SwapchainBackend { get; set; } = "DXVK (Vulkan WSI)";
    public bool EnableCdnSync { get; set; } = true;
    public bool EnableAutoUpdates { get; set; } = false;
    public string LastPlayerNickname { get; set; } = string.Empty;
    public List<string> FavoriteModIds { get; set; } = new();
    public List<string> EnabledModIds { get; set; } = new();
    public int VoiceMicVolume { get; set; } = 100;
    public int VoiceMicGain { get; set; } = 0;
    public bool EnableVoiceAiNoiseSuppression { get; set; } = true;
    public string NetworkProfile { get; set; } = "Competitive 128-Tick (Esports / Fiber)";
    public string LastAcknowledgedCrashUtc { get; set; } = string.Empty;
    public string LastCrashFaultSummary { get; set; } = string.Empty;
    public string LastCrashTimeUtc { get; set; } = string.Empty;
    public int ConsecutiveCrashCount { get; set; } = 0;
    public bool AutoRestartMidMatchCrash { get; set; } = false;
    public bool PureCompatibilityMode { get; set; } = false;
    public bool EnableClientZstdRelay { get; set; } = false;
    public bool DiscordStreamerPrivacyMode { get; set; } = false;

    public static LauncherSettingsModel CreateFirstBootDefaults(int? detectedHzOverride = null, bool? isWindowsOverride = null)
    {
        (int nativeHz, string nativeHzLabel) = HardwareDetectionService.GetFirstBootRefreshRate(detectedHzOverride, isWindowsOverride);
        var monitors = HardwareDetectionService.DetectAvailableMonitors();
        var primaryMon = monitors.FirstOrDefault(m => m.IsPrimary) ?? monitors.FirstOrDefault();

        string initialRes = primaryMon?.SupportedResolutions.FirstOrDefault(r => r.IsNative)?.DisplayLabel
            ?? primaryMon?.SupportedResolutions.FirstOrDefault()?.DisplayLabel
            ?? "1920x1080 (1080p FHD)";

        int effectiveFpsCap = detectedHzOverride is not null
            ? detectedHzOverride.Value
            : (isWindowsOverride is not null && !isWindowsOverride.Value)
                ? nativeHz
                : (primaryMon?.MaxRefreshRateHz ?? nativeHz);

        string effectiveHzLabel = (detectedHzOverride is null && (isWindowsOverride ?? OperatingSystem.IsWindows()) && primaryMon is not null)
            ? $"{effectiveFpsCap} Hz"
            : nativeHzLabel;

        return new LauncherSettingsModel
        {
            SettingsSchemaVersion = CurrentSettingsSchemaVersion,
            DisplayMode = "Borderless Windowed",
            RefreshRate = effectiveHzLabel,
            FpsCap = effectiveFpsCap,
            SelectedMonitorDeviceName = primaryMon?.NormalizedDeviceToken ?? primaryMon?.DeviceName,
            Resolution = initialRes,
            ShadowFidelity = "High (2048)",
            VulkanHudMode = "Off (Clean Screen)",
            ManualRefreshRateOverrideEnabled = false,
            ManualMaxFpsOverride = null
        };
    }

    /// <summary>
    ///     Applies one-time idempotent schema migrations to legacy <c>launcher_settings.json</c> files.
    ///     Upgrading from schema &lt; 2 migrates legacy default <c>"Minimal FPS"</c> Vulkan HUD to
    ///     <c>"Off (Clean Screen)"</c> (preserving <c>"Pro Diagnostics"</c>) and legacy default
    ///     <c>"Ultra (4096)"</c> shadows to <c>"High (2048)"</c>. Any subsequent user selection saved
    ///     under schema &gt;= 2 is preserved verbatim.
    /// </summary>
    public static bool ApplySchemaMigrations(LauncherSettingsModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        if (model.SettingsSchemaVersion >= CurrentSettingsSchemaVersion)
        {
            return false;
        }

        List<string> changes = [];
        if (string.Equals(model.VulkanHudMode, "Minimal FPS", StringComparison.OrdinalIgnoreCase))
        {
            model.VulkanHudMode = "Off (Clean Screen)";
            changes.Add("VulkanHudMode: Minimal FPS -> Off (Clean Screen)");
        }

        if (string.Equals(model.ShadowFidelity, "Ultra (4096)", StringComparison.OrdinalIgnoreCase))
        {
            model.ShadowFidelity = "High (2048)";
            changes.Add("ShadowFidelity: Ultra (4096) -> High (2048)");
        }

        int previousVersion = model.SettingsSchemaVersion;
        model.SettingsSchemaVersion = CurrentSettingsSchemaVersion;
        System.Diagnostics.Trace.WriteLine(
            $"[WILLOWMAKER] Migrated launcher_settings.json schema v{previousVersion} -> v{CurrentSettingsSchemaVersion} ({(changes.Count > 0 ? string.Join("; ", changes) : "no field overrides needed")}).");
        return true;
    }
}

public sealed class LauncherSettingsService
{
    private const string SettingsFileName = "launcher_settings.json";

    public LauncherSettingsService() : this(null)
    {
    }

    public LauncherSettingsService(string? explicitFilePath)
    {
        if (!string.IsNullOrWhiteSpace(explicitFilePath) && File.Exists(explicitFilePath))
            try
            {
                string json = File.ReadAllText(explicitFilePath);
                LauncherSettingsModel? parsed = JsonSerializer.Deserialize(json, WillowmakerJsonSerializerContext.Default.LauncherSettingsModel);
                if (parsed is not null)
                {
                    LauncherSettingsModel.ApplySchemaMigrations(parsed);
                }
                Settings = parsed ?? LauncherSettingsModel.CreateFirstBootDefaults();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.WriteLine($"[WILLOWMAKER] Failed to deserialize explicit settings file '{explicitFilePath}': {ex.Message}");
                Settings = LauncherSettingsModel.CreateFirstBootDefaults();
            }
        else
            Settings = Load(explicitFilePath != null && Directory.Exists(explicitFilePath) ? explicitFilePath : null);
    }

    public LauncherSettingsModel Settings { get; private set; }

    public static string GetSettingsFilePath(string? baseDirectory = null)
    {
        if (!string.IsNullOrWhiteSpace(baseDirectory))
        {
            return Path.Combine(baseDirectory, SettingsFileName);
        }

        string? resolvedClient = LocationGuard.ResolveGameClientDirectory();
        if (!string.IsNullOrWhiteSpace(resolvedClient))
        {
            return Path.Combine(resolvedClient, "userdataKONGOR", SettingsFileName);
        }

        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrWhiteSpace(localAppData))
        {
            return Path.Combine(localAppData, "ProjectKongor", SettingsFileName);
        }

        return Path.Combine(AppContext.BaseDirectory, SettingsFileName);
    }

    public static LauncherSettingsModel Load(string? baseDirectory = null)
    {
        string filePath = GetSettingsFilePath(baseDirectory);
        if (!File.Exists(filePath) && string.IsNullOrWhiteSpace(baseDirectory))
        {
            string? resolvedClient = LocationGuard.ResolveGameClientDirectory();
            if (!string.IsNullOrWhiteSpace(resolvedClient))
            {
                string legacyClientPath = Path.Combine(resolvedClient, SettingsFileName);
                if (File.Exists(legacyClientPath))
                {
                    filePath = legacyClientPath;
                }
            }

            if (!File.Exists(filePath))
            {
                string legacyAppBasePath = Path.Combine(AppContext.BaseDirectory, SettingsFileName);
                if (File.Exists(legacyAppBasePath))
                {
                    filePath = legacyAppBasePath;
                }
            }
        }

        if (!File.Exists(filePath))
        {
            LauncherSettingsModel firstBoot = LauncherSettingsModel.CreateFirstBootDefaults();
            System.Diagnostics.Trace.WriteLine($"[WILLOWMAKER] First-boot settings initialized: DisplayMode={firstBoot.DisplayMode}, RefreshRate={firstBoot.RefreshRate}, FpsCap={firstBoot.FpsCap}");
            return firstBoot;
        }

        try
        {
            string json = File.ReadAllText(filePath);
            LauncherSettingsModel? model = JsonSerializer.Deserialize(json, WillowmakerJsonSerializerContext.Default.LauncherSettingsModel);
            if (model is null)
            {
                return LauncherSettingsModel.CreateFirstBootDefaults();
            }

            if (LauncherSettingsModel.ApplySchemaMigrations(model))
            {
                // Persist upgraded schema version cleanly when an explicit directory is targeted or outside unit tests
                if (!string.IsNullOrWhiteSpace(baseDirectory) || !IsTestEnvironment())
                {
                    SaveToCanonicalPath(model, baseDirectory);
                }
            }

            return model;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine($"[WILLOWMAKER] Failed to load launcher settings from '{filePath}': {ex.Message}");
            return LauncherSettingsModel.CreateFirstBootDefaults();
        }
    }

    public static void Save(LauncherSettingsModel model, string? baseDirectory = null)
    {
        if (string.IsNullOrWhiteSpace(baseDirectory) && IsTestEnvironment())
        {
            return;
        }

        SaveToCanonicalPath(model, baseDirectory);
    }

    public static void SaveToCanonicalPath(LauncherSettingsModel model, string? baseDirectory = null)
    {
        ArgumentNullException.ThrowIfNull(model);
        if (model.SettingsSchemaVersion < LauncherSettingsModel.CurrentSettingsSchemaVersion)
        {
            model.SettingsSchemaVersion = LauncherSettingsModel.CurrentSettingsSchemaVersion;
        }
        string filePath = GetSettingsFilePath(baseDirectory);
        try
        {
            string? parentDir = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrWhiteSpace(parentDir))
            {
                Directory.CreateDirectory(parentDir);
            }

            string json = JsonSerializer.Serialize(model, WillowmakerJsonSerializerContext.Default.LauncherSettingsModel);
            string tempPath = $"{filePath}.{Guid.NewGuid():N}.tmp";
            File.WriteAllText(tempPath, json);
            File.Move(tempPath, filePath, overwrite: true);
            System.Diagnostics.Trace.WriteLine($"[WILLOWMAKER] Saved launcher settings atomically to '{filePath}'.");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine($"[WILLOWMAKER] Failed to save launcher settings to '{filePath}': {ex.Message}");
        }
    }

    private static bool IsTestEnvironment()
    {
        return !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("TESTHOST_PID")) ||
               AppDomain.CurrentDomain.GetAssemblies().Any(a =>
                   a.GetName().Name?.StartsWith("testhost", StringComparison.OrdinalIgnoreCase) == true ||
                   a.GetName().Name?.StartsWith("xunit", StringComparison.OrdinalIgnoreCase) == true);
    }
}
