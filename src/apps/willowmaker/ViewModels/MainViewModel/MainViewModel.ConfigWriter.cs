// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using Microsoft.Extensions.Logging.Abstractions;
using WILLOWMAKER.Core.Models;
using WILLOWMAKER.Core.Services;

namespace WILLOWMAKER.Core.ViewModels;

public partial class MainViewModel
{
    public void LoadLauncherSettings(string? baseDirectory = null)
    {
        _isLoadingSettings = true;
        try
        {
            LauncherSettingsModel settings = LauncherSettingsService.Load(baseDirectory);
            EnableCdnSync = settings.EnableCdnSync;
            EnableRayTracing = settings.EnableRayTracing;
            EnableAsyncPipelineCaching = settings.EnableAsyncPipelineCaching;
            EnableTextureClarityBoost = settings.EnableTextureClarityBoost;

            if (EnableRayTracing)
                SelectedRayTracingPreset = settings.RayTracingPresetName switch
                {
                    "Ultra" => "Ultra (Path-Tracing)",
                    _ => "Balanced RTX (RTAO)"
                };
            else
                SelectedRayTracingPreset = "Competitive (0% RT)";

            AvailableMonitors.Clear();
            var detectedMonitors = HardwareDetectionService.DetectAvailableMonitors();
            foreach (var m in detectedMonitors)
            {
                AvailableMonitors.Add(m);
            }

            MonitorDisplayInfo? matchedMonitor = null;
            if (!string.IsNullOrWhiteSpace(settings.SelectedMonitorDeviceName))
            {
                matchedMonitor = AvailableMonitors.FirstOrDefault(m =>
                    HardwareDetectionService.AreSameMonitorDevice(m.DeviceName, settings.SelectedMonitorDeviceName));
            }
            matchedMonitor ??= AvailableMonitors.FirstOrDefault(m => m.IsPrimary) ?? AvailableMonitors.FirstOrDefault();
            SelectedMonitor = matchedMonitor;

            if (SelectedMonitor is not null)
            {
                AvailableResolutions.Clear();
                foreach (var res in SelectedMonitor.SupportedResolutions)
                {
                    AvailableResolutions.Add(res.DisplayLabel);
                }
            }

            if (!string.IsNullOrWhiteSpace(settings.Resolution))
            {
                var (savedW, savedH) = HardwareDetectionService.ParseResolutionString(settings.Resolution);
                var matchedResOption = SelectedMonitor?.SupportedResolutions.FirstOrDefault(r => r.Width == savedW && r.Height == savedH);
                if (matchedResOption is not null)
                {
                    SelectedResolution = matchedResOption.DisplayLabel;
                }
                else if (AvailableResolutions.Contains(settings.Resolution))
                {
                    SelectedResolution = settings.Resolution;
                }
                else if (SelectedMonitor is not null)
                {
                    var native = SelectedMonitor.SupportedResolutions.FirstOrDefault(r => r.IsNative);
                    if (native is not null)
                        SelectedResolution = native.DisplayLabel;
                }
            }
            else if (SelectedMonitor is not null)
            {
                var native = SelectedMonitor.SupportedResolutions.FirstOrDefault(r => r.IsNative);
                if (native is not null)
                    SelectedResolution = native.DisplayLabel;
            }

            ManualRefreshRateOverrideEnabled = settings.ManualRefreshRateOverrideEnabled;
            ManualMaxFpsText = settings.ManualMaxFpsOverride is >= 0 && settings.ManualRefreshRateOverrideEnabled
                ? settings.ManualMaxFpsOverride.Value.ToString()
                : (settings.ManualMaxFpsOverride is > 0 ? settings.ManualMaxFpsOverride.Value.ToString() : "");
            IsManualRefreshRateExpanded = settings.ManualRefreshRateOverrideEnabled;

            if (SelectedMonitor is not null)
            {
                SyncSelectedRefreshRateFromMonitor();
            }
            else if (!string.IsNullOrWhiteSpace(settings.RefreshRate))
            {
                EnsureRefreshRateAvailable(settings.RefreshRate);
                SelectedRefreshRate = settings.RefreshRate;
            }

            UpdateEffectiveRefreshRateSummary();
            if (!string.IsNullOrWhiteSpace(settings.DisplayMode) && AvailableDisplayModes.Contains(settings.DisplayMode))
                SelectedDisplayMode = settings.DisplayMode;

            System.Diagnostics.Trace.WriteLine($"[WILLOWMAKER] Configured display settings: DisplayMode={SelectedDisplayMode}, RefreshRate={SelectedRefreshRate}");
            if (AvailableShadowFidelities.Contains(settings.ShadowFidelity))
                SelectedShadowFidelity = settings.ShadowFidelity;
            if (AvailableVulkanHudModes.Contains(settings.VulkanHudMode))
                SelectedVulkanHudMode = settings.VulkanHudMode;
            EnableNvidiaReflex = settings.EnableNvidiaReflex;
            EnableFidelityFxRcas = settings.EnableFidelityFxRcas;
            FidelityFxRcasSharpness = settings.FidelityFxRcasSharpness;
            FidelityFxRcasSharpnessText = $"{(int)Math.Round(FidelityFxRcasSharpness * 100)}%";
            EnableSpatialAudio = settings.EnableSpatialAudio;
            AllowThirdPartyVulkanOverlays = settings.AllowThirdPartyVulkanOverlays;
            if (!string.IsNullOrWhiteSpace(settings.SpatialHrtfProfile))
                SelectedSpatialHrtfProfile = settings.SpatialHrtfProfile;
            EnableOcclusionDamping = settings.EnableOcclusionDamping;
            foreach (string ch in AvailableSpatialChannels)
            {
                if (ch.StartsWith(settings.SpatialAudioChannels.ToString()))
                {
                    SelectedSpatialChannels = ch;
                    break;
                }
            }
            EnableQuicHttp3 = settings.EnableQuicHttp3;
            EnableDirectFlip = settings.EnableDirectFlip;
            if (!string.IsNullOrWhiteSpace(settings.PresentationMode) && AvailablePresentationModes.Contains(settings.PresentationMode))
                SelectedPresentationMode = settings.PresentationMode;
            if (!string.IsNullOrWhiteSpace(settings.SwapchainBackend) && AvailableSwapchainBackends.Contains(settings.SwapchainBackend))
                SelectedSwapchainBackend = settings.SwapchainBackend;
            if (!string.IsNullOrWhiteSpace(settings.NetworkProfile) && AvailableNetworkProfiles.Contains(settings.NetworkProfile))
                SelectedNetworkProfile = settings.NetworkProfile;
            if (!string.IsNullOrWhiteSpace(settings.MasterServerAddress))
                LoadedMasterServerAddress = settings.MasterServerAddress.Trim();
            if (!string.IsNullOrEmpty(settings.CustomMasterServerAddress))
                CustomMasterServerAddress = settings.CustomMasterServerAddress.Trim();

            if (!EndpointPairRegistry.IsKongorNetPkvEnabled &&
                !string.IsNullOrWhiteSpace(LoadedMasterServerAddress) &&
                LoadedMasterServerAddress.Contains("kongor.net", StringComparison.OrdinalIgnoreCase))
            {
                Log(LogCategory.General, $"[PKV-GUARD] Auto-migrating legacy kongor.net selection '{LoadedMasterServerAddress}' to '{EndpointPairRegistry.MasterServerNewbsIo}' (kongor.net disabled pending PKV v5).");
                LoadedMasterServerAddress = EndpointPairRegistry.MasterServerNewbsIo;
            }

            string? cliMasterOverride = TryResolveCommandLineMasterServerOverride(Environment.GetCommandLineArgs());
            if (!string.IsNullOrWhiteSpace(cliMasterOverride))
            {
                LoadedMasterServerAddress = cliMasterOverride;
                HasActiveCliMasterServerOverride = true;
                Log(LogCategory.General, $"[ControlPanelOverride] Applied --masterserver CLI override -> '{cliMasterOverride}'");
            }

            PopulateCDNOptions(LoadedMasterServerAddress, forcePairedSelection: true);
            if (string.IsNullOrWhiteSpace(cliMasterOverride) && !string.IsNullOrWhiteSpace(settings.SelectedCDNAddress))
            {
                string savedCdn = settings.SelectedCDNAddress.Trim();
                if (EndpointPairRegistry.IsCrossEcosystemMismatch(LoadedMasterServerAddress, savedCdn))
                {
                    Log(LogCategory.General, $"[EndpointPair] Migrated legacy cross-ecosystem CDN setting '{savedCdn}' -> '{SelectedCDNAddressItem?.DisplayText}' for Master='{LoadedMasterServerAddress}'");
                }
                else
                {
                    AddressSelectItem? match = AvailableCDNOptions.FirstOrDefault(o =>
                        o.DisplayText.Equals(savedCdn, StringComparison.OrdinalIgnoreCase) ||
                        (o.IsCustom && savedCdn.Equals("Custom", StringComparison.OrdinalIgnoreCase)));
                    if (match is not null)
                        SelectedCDNAddressItem = match;
                }
            }
            if (!string.IsNullOrEmpty(settings.CustomCDNAddress))
                CustomCDNAddress = settings.CustomCDNAddress.Trim();

            VoiceMicVolume = settings.VoiceMicVolume;
            VoiceMicGain = settings.VoiceMicGain;
            EnableVoiceAiNoiseSuppression = settings.EnableVoiceAiNoiseSuppression;
            PureCompatibilityMode = settings.PureCompatibilityMode;
            AutoRestartMidMatchCrash = settings.AutoRestartMidMatchCrash;
            DiscordStreamerPrivacyMode = settings.DiscordStreamerPrivacyMode;
            VoiceMicVolumeText = $"{VoiceMicVolume}%";
            VoiceMicGainText = VoiceMicGain == 0 ? "Off (+0 dB)" : $"+{VoiceMicGain} dB";

            AudioModernizationService audioDiscovery = new(NullLogger<AudioModernizationService>.Instance);
            IReadOnlyList<string> discovered = audioDiscovery.DiscoverAvailableProfiles(baseDirectory ?? Environment.CurrentDirectory);
            foreach (string prof in discovered)
            {
                if (!AvailableSpatialHrtfProfiles.Contains(prof))
                    AvailableSpatialHrtfProfiles.Add(prof);
            }

            UpdateShaderCacheStatus();
            RefreshHardwareDetectionTooltip();
            RefreshActiveGraphicsPresetState();
            RefreshDisplayVrrReadiness();
        }
        finally
        {
            _isLoadingSettings = false;
        }

        WriteCustomConfiguration(baseDirectory);
    }

    public void EnsureRefreshRateAvailable(string? refreshRate)
    {
        if (string.IsNullOrWhiteSpace(refreshRate) || AvailableRefreshRates.Contains(refreshRate))
        {
            return;
        }

        string trimmed = refreshRate.Trim();
        string[] parts = trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length > 0 && int.TryParse(parts[0], out int targetHz))
        {
            int insertIdx = -1;
            for (int i = 0; i < AvailableRefreshRates.Count; i++)
            {
                string item = AvailableRefreshRates[i];
                if (item.Contains("Uncapped", StringComparison.OrdinalIgnoreCase))
                {
                    insertIdx = i;
                    break;
                }
                string[] itemParts = item.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (itemParts.Length > 0 && int.TryParse(itemParts[0], out int itemHz) && itemHz > targetHz)
                {
                    insertIdx = i;
                    break;
                }
            }

            if (insertIdx >= 0)
            {
                AvailableRefreshRates.Insert(insertIdx, trimmed);
            }
            else
            {
                AvailableRefreshRates.Add(trimmed);
            }
        }
        else
        {
            AvailableRefreshRates.Add(trimmed);
        }
    }

    public void SaveLauncherSettings(string? baseDirectory = null)
    {
        if (IsSuppressingAutoWrite) return;

        string presetName = SelectedRayTracingPreset.StartsWith("Ultra", StringComparison.OrdinalIgnoreCase)
            ? "Ultra"
            : SelectedRayTracingPreset.StartsWith("Balanced", StringComparison.OrdinalIgnoreCase)
                ? "Balanced"
                : "Competitive";

        LauncherSettingsModel existing = LauncherSettingsService.Load(baseDirectory);

        string savedMasterServerAddress = HasActiveCliMasterServerOverride && !string.IsNullOrWhiteSpace(existing.MasterServerAddress)
            ? existing.MasterServerAddress
            : (TryGetMasterServerComboText()
                ?? (!string.IsNullOrWhiteSpace(LoadedMasterServerAddress)
                    ? LoadedMasterServerAddress
                    : LauncherSettingsModel.DefaultLocalOfflineAutoMasterServer));

        string savedCustomMasterServerAddress = HasActiveCliMasterServerOverride
            ? existing.CustomMasterServerAddress
            : (CustomMasterServerAddress ?? string.Empty);

        string savedSelectedCdn = HasActiveCliMasterServerOverride && !string.IsNullOrWhiteSpace(existing.SelectedCDNAddress)
            ? existing.SelectedCDNAddress
            : (SelectedCDNAddressItem?.DisplayText ?? string.Empty);

        string savedCustomCdn = HasActiveCliMasterServerOverride
            ? existing.CustomCDNAddress
            : (CustomCDNAddress ?? string.Empty);

        int savedFpsCap = 240;
        if (!string.IsNullOrWhiteSpace(SelectedRefreshRate))
        {
            if (SelectedRefreshRate.Contains("Uncapped", StringComparison.OrdinalIgnoreCase))
            {
                savedFpsCap = 1000;
            }
            else
            {
                string[] hzParts = SelectedRefreshRate.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (hzParts.Length > 0 && int.TryParse(hzParts[0], out int parsedHz))
                {
                    savedFpsCap = string.Equals(existing.RefreshRate, SelectedRefreshRate, StringComparison.OrdinalIgnoreCase) &&
                                  existing.FpsCap > parsedHz
                        ? existing.FpsCap
                        : parsedHz;
                }
            }
        }

        int parsedManualFps = 0;
        int mf = 0;
        bool hasValidManualOverride = ManualRefreshRateOverrideEnabled && int.TryParse(ManualMaxFpsText, out mf) && mf >= 0;
        if (hasValidManualOverride)
        {
            parsedManualFps = mf;
        }

        int effectiveCap = hasValidManualOverride
            ? (parsedManualFps == 0 ? 1000 : parsedManualFps)
            : (!_explicitLegacyRefreshRateOverride && SelectedMonitor is not null
                ? GetAutoMaxRefreshRateHz()
                : savedFpsCap);

        LauncherSettingsModel settings = new()
        {
            SettingsSchemaVersion = LauncherSettingsModel.CurrentSettingsSchemaVersion,
            EnableCdnSync = EnableCdnSync,
            EnableRayTracing = EnableRayTracing,
            RayTracingPresetName = presetName,
            EnableAsyncPipelineCaching = EnableAsyncPipelineCaching,
            EnableTextureClarityBoost = EnableTextureClarityBoost,
            SelectedMonitorDeviceName = SelectedMonitor?.DeviceName,
            ManualRefreshRateOverrideEnabled = ManualRefreshRateOverrideEnabled,
            ManualMaxFpsOverride = hasValidManualOverride ? parsedManualFps : null,
            RefreshRate = SelectedRefreshRate,
            FpsCap = effectiveCap,
            Resolution = SelectedResolution,
            DisplayMode = SelectedDisplayMode,
            ShadowFidelity = SelectedShadowFidelity,
            VulkanHudMode = SelectedVulkanHudMode,
            EnableNvidiaReflex = EnableNvidiaReflex,
            EnableFidelityFxRcas = EnableFidelityFxRcas,
            FidelityFxRcasSharpness = FidelityFxRcasSharpness,
            EnableSpatialAudio = EnableSpatialAudio,
            AllowThirdPartyVulkanOverlays = AllowThirdPartyVulkanOverlays,
            SpatialHrtfProfile = SelectedSpatialHrtfProfile,
            EnableOcclusionDamping = EnableOcclusionDamping,
            SpatialAudioChannels = int.TryParse(SelectedSpatialChannels.Split(' ')[0], out int parsedCh) ? parsedCh : 128,
            EnableQuicHttp3 = EnableQuicHttp3,
            EnableDirectFlip = EnableDirectFlip,
            PresentationMode = SelectedPresentationMode,
            SwapchainBackend = SelectedSwapchainBackend,
            MasterServerAddress = savedMasterServerAddress,
            CustomMasterServerAddress = savedCustomMasterServerAddress,
            SelectedCDNAddress = savedSelectedCdn,
            CustomCDNAddress = savedCustomCdn,
            FavoriteModIds = ModManager != null && ModManager.Mods.Count > 0
                ? ModManager.FavoriteMods.Select(m => m.Id).Distinct(StringComparer.OrdinalIgnoreCase).ToList()
                : existing.FavoriteModIds,
            EnabledModIds = ModManager != null && ModManager.Mods.Count > 0
                ? ModManager.Mods.Where(m => m.IsEnabled).Select(m => m.Id).Distinct(StringComparer.OrdinalIgnoreCase).ToList()
                : existing.EnabledModIds,
            VoiceMicVolume = VoiceMicVolume,
            VoiceMicGain = VoiceMicGain,
            EnableVoiceAiNoiseSuppression = EnableVoiceAiNoiseSuppression,
            NetworkProfile = SelectedNetworkProfile,
            LastPlayerNickname = existing.LastPlayerNickname,
            LastAcknowledgedCrashUtc = existing.LastAcknowledgedCrashUtc,
            LastCrashFaultSummary = existing.LastCrashFaultSummary,
            LastCrashTimeUtc = existing.LastCrashTimeUtc,
            ConsecutiveCrashCount = existing.ConsecutiveCrashCount,
            AutoRestartMidMatchCrash = AutoRestartMidMatchCrash,
            PureCompatibilityMode = PureCompatibilityMode,
            DiscordStreamerPrivacyMode = DiscordStreamerPrivacyMode
        };

        LauncherSettingsService.Save(settings, baseDirectory);
    }

    partial void OnDiscordStreamerPrivacyModeChanged(bool value)
    {
        SaveLauncherSettings();
        if (!_isLoadingSettings)
        {
            _activeDiscordPresence?.SetStreamerPrivacyMode(value);
            Log(LogCategory.General, $"[Discord RPC] Streamer Privacy Mode {(value ? "enabled (hiding handle, clan tag, KDA & party)" : "disabled (showing full profile details)")}.");
        }
    }

    partial void OnPureCompatibilityModeChanged(bool value)
    {
        SaveLauncherSettings();
        if (!_isLoadingSettings)
        {
            WriteCustomConfiguration();
        }
    }

    partial void OnAutoRestartMidMatchCrashChanged(bool value)
    {
        SaveLauncherSettings();
    }

    partial void OnEnableSpatialAudioChanged(bool value)
    {
        SaveLauncherSettings();
        if (!_isLoadingSettings)
        {
            EnsureSpatialAudioConfigured();
            WriteCustomConfiguration();
            RefreshVoiceDevices();
            Log(LogCategory.Initialise, $"[Audio] Spatial HRTF {(value ? "enabled (OpenAL Soft 256ms)" : "disabled (Native FMOD stereo 100ms)")}.");
        }
    }

    partial void OnAllowThirdPartyVulkanOverlaysChanged(bool value)
    {
        SaveLauncherSettings();
        if (!_isLoadingSettings)
        {
            Log(LogCategory.Initialise, $"[Vulkan Overlays] Third-party Vulkan overlay hooks {(value ? "allowed" : "blocked via VK_LOADER_LAYERS_DISABLE")}.");
        }
    }

    partial void OnSelectedSpatialHrtfProfileChanged(string value)
    {
        SaveLauncherSettings();
        ApplyActiveSpatialSettingsAudition();
    }

    partial void OnEnableOcclusionDampingChanged(bool value)
    {
        SaveLauncherSettings();
        ApplyActiveSpatialSettingsAudition();
    }

    partial void OnSelectedSpatialChannelsChanged(string value)
    {
        SaveLauncherSettings();
        ApplyActiveSpatialSettingsAudition();
    }

    partial void OnEnableQuicHttp3Changed(bool value)
    {
        SaveLauncherSettings();
    }

    private int? _lastAppliedDxvkMaxFrameLatency;

    /// <summary>
    ///     Test-only opt-out for live OS process enumeration inside <see cref="EnsureVulkanDxvkConfigured" />.
    ///     Production launches ALWAYS scan for intrusive swapchain recorders even when <c>baseDirectory</c> is supplied.
    /// </summary>
    public bool SkipProcessConflictScanForTesting { get; set; }

    /// <summary>
    ///     Determines whether Safe Swapchain Pacing must override DirectFlip settings.
    ///     Always active for high-risk D3D9/Vulkan hook recorders (RTSS, Overwolf, Action!, Medal, Fraps).
    ///     Benign companion apps (Discord, OBS) only trigger pacing when third-party Vulkan overlays are explicitly allowed,
    ///     because <c>VK_LOADER_LAYERS_DISABLE</c> already blocks their Vulkan layers by default.
    /// </summary>
    public static bool ResolveEffectiveSafeSwapchainPacing(
        OverlayConflictReport conflictReport,
        bool allowThirdPartyVulkanOverlays)
    {
        if (conflictReport.HasActiveConflicts)
            return true;

        return allowThirdPartyVulkanOverlays && conflictReport.ShouldApplySafeSwapchainPacing;
    }

    public void EnsureVulkanDxvkConfigured(string? baseDirectory = null, bool forceConflictScan = false)
    {
        try
        {
            string? rootDir = ResolveClientConfigRoot(baseDirectory, nameof(EnsureVulkanDxvkConfigured));
            if (rootDir is null)
                return;
            string targetDir = rootDir;
            if (Directory.Exists(Path.Combine(rootDir, "games", "client")))
                targetDir = Path.Combine(rootDir, "games", "client");

            if (VulkanDriverUpgradeService.IsPosixClientDirectory(targetDir))
            {
                Log(LogCategory.Initialise, $"[POSIX Render] Skipping Windows DXVK / DirectFlip configuration on POSIX target '{targetDir}'.");
                return;
            }

            RayTracingPreset preset = RayTracingPreset.Competitive;
            if (EnableRayTracing)
                preset = SelectedRayTracingPreset.StartsWith("Ultra", StringComparison.OrdinalIgnoreCase)
                    ? RayTracingPreset.Ultra
                    : RayTracingPreset.Balanced;

            RenderModernizationService renderService = new(
                NullLogger<RenderModernizationService>.Instance);

            DirectFlipPresentationMode presentationMode = SelectedPresentationMode switch
            {
                "DXVK Mailbox (Low-Latency)" => DirectFlipPresentationMode.DxvkMailbox,
                "Legacy Blit (DWM Composited)" => DirectFlipPresentationMode.LegacyComposited,
                _ => DirectFlipPresentationMode.DirectFlipDiscard
            };

            SwapchainBackend swapchainBackend =
                SelectedSwapchainBackend.Contains("D3D9On12", StringComparison.OrdinalIgnoreCase) ||
                SelectedSwapchainBackend.Contains("Compatibility", StringComparison.OrdinalIgnoreCase)
                    ? SwapchainBackend.D3D9On12
                    : SwapchainBackend.DxvkVulkan;

            int maxFrameRate = 0;
            if (ManualRefreshRateOverrideEnabled && int.TryParse(ManualMaxFpsText, out int manualFps) && manualFps > 0)
            {
                maxFrameRate = manualFps;
            }
            // In Auto mode, maxFrameRate remains 0 in dxvk.conf so the K2 engine CVars can dynamically adapt mid-game across monitors without restart.

            string dxvkDriver = LaunchConfigWriterService.ResolveEffectiveVideoDriver(
                targetDir,
                SelectedSwapchainBackend.Contains("D3D9On12", StringComparison.OrdinalIgnoreCase));
            (string dxvkFullscreenCvar, _, _) = LaunchConfigWriterService.ResolveDisplayModeCvars(SelectedDisplayMode, dxvkDriver);
            bool isFullscreen = string.Equals(dxvkFullscreenCvar, "true", StringComparison.OrdinalIgnoreCase) &&
                                !SelectedDisplayMode.Contains("Borderless", StringComparison.OrdinalIgnoreCase);

            string userRenderDir = Path.Combine(targetDir, "userdataKONGOR");
            string userDxvkCacheDir = Path.Combine(userRenderDir, "dxvk_cache");
            Directory.CreateDirectory(userRenderDir);
            Directory.CreateDirectory(userDxvkCacheDir);

            OverlayConflictDetectorService conflictDetector = new OverlayConflictDetectorService();
            bool hasUsbDisplays = HardwareDetectionService.LastDetectionIgnoredAuxiliaryDisplay ||
                AvailableMonitors.Any(m =>
                    m.FriendlyName.Contains("DisplayLink", StringComparison.OrdinalIgnoreCase) ||
                    m.FriendlyName.Contains("Virtual", StringComparison.OrdinalIgnoreCase) ||
                    m.FriendlyName.Contains("USB", StringComparison.OrdinalIgnoreCase) ||
                    m.FriendlyName.Contains("StreamDeck", StringComparison.OrdinalIgnoreCase) ||
                    m.FriendlyName.Contains("Stream Deck", StringComparison.OrdinalIgnoreCase));
            OverlayConflictReport conflictReport = SkipProcessConflictScanForTesting
                ? new OverlayConflictReport(false, hasUsbDisplays, false, [], string.Empty)
                : conflictDetector.ScanForConflicts(hasUsbDisplays, forceRefresh: forceConflictScan);
            bool effectiveSafeSwapchainPacing = ResolveEffectiveSafeSwapchainPacing(
                conflictReport,
                AllowThirdPartyVulkanOverlays);

            if (conflictReport.HasActiveConflicts)
            {
                Log(LogCategory.Executable, conflictReport.WarningBannerText);
            }
            else if (conflictReport.HasVirtualDisplayAdapters)
            {
                Log(LogCategory.Initialise,
                    $"[Graphics Notice] Secondary virtual / USB auxiliary display detected (ignored as gaming target). DirectFlip preserved (SafeSwapchainPacing={effectiveSafeSwapchainPacing}, AllowThirdPartyOverlays={AllowThirdPartyVulkanOverlays}).");
            }

            if (conflictReport.ImplicitVulkanLayers is { Count: > 0 } implicitLayers)
            {
                string shieldAction = AllowThirdPartyVulkanOverlays
                    ? "allowed by user setting"
                    : "auto-neutralized via manifest disable_environment + VK_LOADER_LAYERS_DISABLE";
                Log(LogCategory.Initialise,
                    $"[Vulkan Implicit Layer Shield] Detected {implicitLayers.Count} host Khronos implicit layer(s) ({string.Join(", ", implicitLayers)}) -> {shieldAction}.");
            }

            renderService.ApplyRenderModernization(
                userRenderDir,
                preset,
                SelectedVulkanHudMode,
                EnableNvidiaReflex,
                EnableAsyncPipelineCaching,
                EnableTextureClarityBoost,
                EnableDirectFlip,
                presentationMode,
                swapchainBackend,
                maxFrameRate: maxFrameRate,
                isFullscreen: isFullscreen,
                customStateCacheDir: userDxvkCacheDir,
                safeSwapchainPacing: effectiveSafeSwapchainPacing);

            _lastAppliedDxvkMaxFrameLatency = RenderModernizationService.ResolveEffectiveMaxFrameLatency(
                EnableNvidiaReflex,
                EnableDirectFlip,
                presentationMode,
                effectiveSafeSwapchainPacing);

            if (effectiveSafeSwapchainPacing)
            {
                string companionDetail = conflictReport.CompanionProcesses is { Count: > 0 }
                    ? $" (companion overlays: {string.Join(", ", conflictReport.CompanionProcesses)})"
                    : string.Empty;
                Log(LogCategory.Initialise,
                    $"[Safe Swapchain Pacing] Active (tearFree=true, maxLatency=2, latencyTolerance=1000){companionDetail} to protect DXVK against swapchain recreation timeouts.");
            }

            if (preset != RayTracingPreset.Competitive)
                Log(LogCategory.Initialise,
                    $"[RTX Mode ENABLED - {preset}] Configured DXVK & RTX Remix in {userRenderDir}");
            else
                Log(LogCategory.Initialise, $"[Competitive Mode] Configured DXVK in {userRenderDir}");

            Log(LogCategory.Initialise,
                $"[DirectFlip Pipeline] Status: {(EnableDirectFlip ? "ACTIVE (DWM Bypass)" : "DISABLED")}, Mode: '{SelectedPresentationMode}', Backend: '{SelectedSwapchainBackend}'");

            UpdateShaderCacheStatus(renderService);
        }
        catch (Exception exception)
        {
            Log(LogCategory.Executable, $"Failed to configure DXVK / RTX Render Modernization: {exception.Message}");
        }
    }

    public void EnsureSpatialAudioConfigured(string? baseDirectory = null)
    {
        try
        {
            string? rootDir = ResolveClientConfigRoot(baseDirectory, nameof(EnsureSpatialAudioConfigured));
            if (rootDir is null)
                return;
            string targetDir = rootDir;
            if (Directory.Exists(Path.Combine(rootDir, "games", "client")))
                targetDir = Path.Combine(rootDir, "games", "client");

            string userAudioDir = Path.Combine(targetDir, "userdataKONGOR");
            Directory.CreateDirectory(userAudioDir);

            AudioModernizationService audioService = new(NullLogger<AudioModernizationService>.Instance);
            int channels = int.TryParse(SelectedSpatialChannels.Split(' ')[0], out int parsedCh) ? parsedCh : 128;
            audioService.ApplyAudioModernization(
                userAudioDir,
                EnableSpatialAudio,
                SelectedSpatialHrtfProfile,
                EnableOcclusionDamping,
                channels);

            if (EnableSpatialAudio)
                Log(LogCategory.Initialise,
                    $"[Spatial Audio ENABLED] OpenAL Soft HRTF Profile: '{SelectedSpatialHrtfProfile}' ({channels} voices) configured in {userAudioDir}");
            else
                Log(LogCategory.Initialise, $"[Legacy Audio Mode] Configured standard stereo in {userAudioDir}");
        }
        catch (Exception ex)
        {
            Log(LogCategory.Executable, $"Failed to configure Spatial Audio Modernization: {ex.Message}");
        }
    }

    public (string shadowMapSize, string shadowFilterWidth, string shadowDrawDistance, string foliageDensity, string cullGroundSprites, string ambientOcclusionSetting, string textureCompressionSetting, string refreshRateNumber, string maxFpsNumber, string resParam) ResolveActiveGraphicsSettings()
    {
        string refreshRateNumber = "144";
        string maxFpsNumber = "144";

        if (ManualRefreshRateOverrideEnabled && int.TryParse(ManualMaxFpsText, out int manualFps) && manualFps >= 0)
        {
            int monitorAutoHz = SelectedMonitor is not null ? GetAutoMaxRefreshRateHz() : (manualFps > 0 ? manualFps : 144);
            refreshRateNumber = monitorAutoHz.ToString();
            maxFpsNumber = manualFps == 0 ? "1000" : manualFps.ToString();
        }
        else if (!_explicitLegacyRefreshRateOverride && SelectedMonitor is not null)
        {
            int autoHz = GetAutoMaxRefreshRateHz();
            refreshRateNumber = autoHz.ToString();
            maxFpsNumber = autoHz.ToString();
        }
        else if (!string.IsNullOrEmpty(SelectedRefreshRate))
        {
            if (SelectedRefreshRate.Contains("Uncapped", StringComparison.OrdinalIgnoreCase))
            {
                refreshRateNumber = "0";
                maxFpsNumber = "1000";
            }
            else
            {
                string[] parts = SelectedRefreshRate.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length > 0 && int.TryParse(parts[0], out int hz))
                {
                    refreshRateNumber = hz.ToString();
                    maxFpsNumber = hz.ToString();
                }
            }
        }

        string resParam = "1920,1080";
        if (!string.IsNullOrWhiteSpace(SelectedResolution))
        {
            string firstPart = SelectedResolution.Split(' ')[0];
            string[] dims = firstPart.Split('x', 'X');
            if (dims.Length == 2 && int.TryParse(dims[0], out int w) && int.TryParse(dims[1], out int h))
            {
                resParam = $"{w},{h}";
            }
        }

        string shadowMapSize;
        string shadowFilterWidth;
        if (SelectedShadowFidelity.Contains("1024", StringComparison.OrdinalIgnoreCase))
        {
            shadowMapSize = "1024";
            shadowFilterWidth = "1";
        }
        else if (SelectedShadowFidelity.Contains("2048", StringComparison.OrdinalIgnoreCase))
        {
            shadowMapSize = "2048";
            shadowFilterWidth = "2";
        }
        else
        {
            shadowMapSize = "4096";
            shadowFilterWidth = "2";
        }

        string foliageDensity = "1.0000";
        string cullGroundSprites = "false";
        string shadowDrawDistance = "2500.0000";
        string ambientOcclusionSetting = "true";

        if (SelectedShadowFidelity.Contains("1024", StringComparison.OrdinalIgnoreCase) && !EnableRayTracing)
        {
            foliageDensity = "0.0000";
            cullGroundSprites = "true";
            shadowDrawDistance = "1800.0000";
            ambientOcclusionSetting = "false";
        }
        else if (SelectedShadowFidelity.Contains("2048", StringComparison.OrdinalIgnoreCase))
        {
            foliageDensity = "1.0000";
            cullGroundSprites = "false";
            shadowDrawDistance = "2500.0000";
            ambientOcclusionSetting = "true";
        }
        else if (SelectedShadowFidelity.Contains("4096", StringComparison.OrdinalIgnoreCase))
        {
            foliageDensity = "1.0000";
            cullGroundSprites = "false";
            shadowDrawDistance = "3500.0000";
            ambientOcclusionSetting = "true";
        }

        // satisfies: G1 option 1 - compression OFF leaves 19,213 retail textures with no raw source (plan v4 sections 3.3, 3.5)
        string textureCompressionSetting = "true";
        if (EnableTextureClarityBoost) { Log(LogCategory.Parameters, "[Launch] EnableTextureClarityBoost=true no longer disables vid_textureCompression (plan v4 G1); it only adds the DXVK LOD-bias block."); }
        Log(LogCategory.Parameters, $"[Launch] vid_textureCompression={textureCompressionSetting} (ClarityBoost={EnableTextureClarityBoost})");

        return (shadowMapSize, shadowFilterWidth, shadowDrawDistance, foliageDensity, cullGroundSprites, ambientOcclusionSetting, textureCompressionSetting, refreshRateNumber, maxFpsNumber, resParam);
    }

    /// <summary>
    ///     Resolves preset-scaled antialiasing and reflection map size CVars without overriding the user's
    ///     authoritative <see cref="SelectedShadowFidelity"/> selection.
    ///     Competitive (0% RT): AA=0, reflection=512; Balanced RTX: AA=2, reflection=1024; Ultra: AA=4, reflection=2048.
    /// </summary>
    public static (string Antialiasing, string ReflectionMapSize) ResolvePresetQualityCvars(string? rayTracingPreset)
    {
        if (!string.IsNullOrWhiteSpace(rayTracingPreset))
        {
            if (rayTracingPreset.StartsWith("Ultra", StringComparison.OrdinalIgnoreCase))
            {
                return ("4", "2048");
            }

            if (rayTracingPreset.StartsWith("Balanced", StringComparison.OrdinalIgnoreCase))
            {
                return ("2", "1024");
            }
        }

        return ("0", "512");
    }

    /// <summary>
    ///     Resolves dynamic <c>cg_interfaceFPS</c> matching the active FPS cap / monitor refresh rate,
    ///     strictly clamped to K2's safe [60, 500] Hz envelope.
    /// </summary>
    public static string ResolveEffectiveInterfaceFps(string? effectiveMaxFpsNumber, string? refreshRateNumber)
    {
        if (int.TryParse(effectiveMaxFpsNumber, out int parsedMaxFps) && parsedMaxFps > 0)
        {
            return Math.Clamp(parsedMaxFps, 60, 500).ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        if (int.TryParse(refreshRateNumber, out int parsedRefreshRate) && parsedRefreshRate > 0)
        {
            return Math.Clamp(parsedRefreshRate, 60, 500).ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        return "144";
    }

    public string? ResolveClientConfigRoot(string? baseDirectory, string caller)
        => LaunchConfigWriterService.ResolveClientConfigRoot(baseDirectory, caller, Log);

    private void WriteConfigFileAtomically(string path, string content)
        => LaunchConfigWriterService.WriteConfigFileAtomically(path, content, Log);

    private static string UpsertSetSave(string content, string key, string value, bool allowAppend, out bool modified)
        => LaunchConfigWriterService.UpsertSetSave(content, key, value, allowAppend, out modified);

    public void WriteCustomConfiguration(string? baseDirectory = null, bool forceConflictScan = false)
    {
        ConfigWriteInvocationCountForTesting++;
        string? rootDir = ResolveClientConfigRoot(baseDirectory, nameof(WriteCustomConfiguration));
        if (rootDir is null)
            return;

        EnsureVulkanDxvkConfigured(rootDir, forceConflictScan: forceConflictScan);
        EnsureSpatialAudioConfigured(rootDir);

        string configDir = Path.Combine(rootDir, "userdataKONGOR", "configuration");
        Directory.CreateDirectory(configDir);
        string customConfigurationFilePath = Path.Combine(configDir, "autoexec.cfg");

        var (shadowMapSize, shadowFilterWidth, shadowDrawDistance, foliageDensity, cullGroundSprites, ambientOcclusionSetting, textureCompressionSetting, refreshRateNumber, maxFpsNumber, resParam) = ResolveActiveGraphicsSettings();
        string audioChannels = int.TryParse(SelectedSpatialChannels.Split(' ')[0], out int ch) ? ch.ToString() : "128";
        string occlusionSetting = EnableOcclusionDamping ? "true" : "false";
        (string audioMixRate, string audioBufferLength) = AudioModernizationService.ResolveAudioCvars(EnableSpatialAudio);

        bool is128Tick = SelectedNetworkProfile.Contains("128", StringComparison.OrdinalIgnoreCase);
        // K2 engine clamps cl_packetSendFPS to [1, 85]; passing > 85 fails CVar validation and falls back to 30 Hz.
        // Competitive mode applies the maximum 85 Hz client send ceiling alongside 128 net_maxPacketRate.
        string packetSendFps = is128Tick ? "85" : "60";
        string netFps = is128Tick ? "128" : "60";
        string packetRate = is128Tick ? "128" : "60";
        string maxBps = is128Tick ? "262144" : "131072";
        string packetEpsilon = is128Tick ? "0.0005" : "0.001";
        string packetResendTime = is128Tick ? "30" : "50";
        Log(LogCategory.Initialise,
            $"[CONFIG_SYNC] Network profile '{SelectedNetworkProfile}' -> cl_packetSendFPS={packetSendFps} (K2 [1, 85] ceiling), net_FPS={netFps}, net_maxPacketRate={packetRate}, net_maxBPS={maxBps}.");

        LauncherSettingsModel settings = LauncherSettingsService.Load(baseDirectory);
        string targetDisplayDevice = !string.IsNullOrWhiteSpace(SelectedMonitor?.NormalizedDeviceToken)
            ? SelectedMonitor!.NormalizedDeviceToken
            : "DISPLAY1";
        int manualFpsValue = (ManualRefreshRateOverrideEnabled && int.TryParse(ManualMaxFpsText, out int mOvr) && mOvr >= 0)
            ? (mOvr == 0 ? 1000 : mOvr)
            : 0;
        string manualFpsEnabledFlag = manualFpsValue > 0 ? "1" : "0";
        string detectedMonitorsCvar = HardwareDetectionService.FormatKongorDetectedMonitorsCvar(AvailableMonitors);
        string detectedMonitorsConfigBlock = string.IsNullOrWhiteSpace(detectedMonitorsCvar)
            ? string.Empty
            : $"SetSave \"kongor_detected_monitors\" \"{detectedMonitorsCvar}\"";
        string effectiveMaxFpsNumber = maxFpsNumber;
        string effectiveInterfaceFpsNumber = ResolveEffectiveInterfaceFps(effectiveMaxFpsNumber, refreshRateNumber);
        (string antialiasingSetting, string reflectionMapSize) = ResolvePresetQualityCvars(SelectedRayTracingPreset);
        DirectFlipPresentationMode currentPresentationMode = SelectedPresentationMode switch
        {
            "DXVK Mailbox (Low-Latency)" => DirectFlipPresentationMode.DxvkMailbox,
            "Legacy Blit (DWM Composited)" => DirectFlipPresentationMode.LegacyComposited,
            _ => DirectFlipPresentationMode.DirectFlipDiscard
        };
        int effectiveDxvkMaxFrameLatency = _lastAppliedDxvkMaxFrameLatency
            ?? RenderModernizationService.ResolveEffectiveMaxFrameLatency(
                EnableNvidiaReflex,
                EnableDirectFlip,
                currentPresentationMode);
        Log(LogCategory.Initialise,
            $"[CONFIG_SYNC][HIGH_HZ] Preset='{SelectedRayTracingPreset}' | ShadowFidelity='{SelectedShadowFidelity}' (vid_shadowmapSize={shadowMapSize}, vid_shadowDrawDistance={shadowDrawDistance}) | vid_antialiasing={antialiasingSetting} | vid_reflectionMapSize={reflectionMapSize} | TargetHz={refreshRateNumber} | MaxFPS={effectiveMaxFpsNumber} | cg_interfaceFPS={effectiveInterfaceFpsNumber} | DXVK maxFrameLatency={effectiveDxvkMaxFrameLatency} (Reflex={EnableNvidiaReflex})");

        string rawSavedNickname = !string.IsNullOrWhiteSpace(settings.LastPlayerNickname)
            ? settings.LastPlayerNickname.Trim()
            : string.Empty;
        string savedNickname = DiscordPresenceService.StripClanTag(rawSavedNickname);
        if (!string.IsNullOrWhiteSpace(rawSavedNickname) &&
            !string.Equals(rawSavedNickname, savedNickname, StringComparison.Ordinal))
        {
            Log(LogCategory.Initialise, $"[CONFIG_SYNC] Stripped clan tag from login_name '{rawSavedNickname}' -> '{savedNickname}'.");
        }

        string activeDriver = LaunchConfigWriterService.ResolveEffectiveVideoDriver(
            rootDir,
            SelectedSwapchainBackend.Contains("D3D9On12", StringComparison.OrdinalIgnoreCase));
        (string fullscreenSetting, string borderlessSetting, string exclusiveSetting) =
            LaunchConfigWriterService.ResolveDisplayModeCvars(SelectedDisplayMode, activeDriver);
        PlaytestPolicyDto playtestPolicy = PlaytestPolicyService.Resolve(
            rootDir,
            msg => Log(LogCategory.Initialise, msg));
        string devFlag = playtestPolicy.UseStableReleaseStageCvars ? "false" : "true";
        string stableFlag = playtestPolicy.UseStableReleaseStageCvars ? "true" : "false";

        bool isLegacyComposited = !EnableDirectFlip ||
                                  SelectedPresentationMode.Contains("Legacy", StringComparison.OrdinalIgnoreCase);
        bool isDxvkMailbox = EnableDirectFlip &&
                             SelectedPresentationMode.Contains("Mailbox", StringComparison.OrdinalIgnoreCase);
        string presentationPacingAutoexecBlock = isLegacyComposited
            ? """
              SetSave "d3d_presentInterval" "1"
              SetSave "gl_swapInterval" "1"
              SetSave "d3d_tripleBuffering" "true"
              """
            : isDxvkMailbox
                ? """
                  SetSave "d3d_presentInterval" "0"
                  SetSave "gl_swapInterval" "0"
                  SetSave "d3d_tripleBuffering" "false"
                  """
                : "// DirectFlip (App-Controlled VSync): omits d3d_presentInterval override so HoN's in-game VSync checkbox persists across restarts";

        string customConfigurationFileContent =
            $"""
             // autoexec.cfg auto-generated by WILLOWMAKER
             // load order: 1) startup.cfg, 2) login.cfg, 3) init.cfg, 4) components, 5) autoexec.cfg

             // Next-Gen High-Performance Engine & Latency Tuning
             SetSave "host_affinity" "-1"
             SetSave "vid_maxFPS" "{effectiveMaxFpsNumber}"
             SetSave "cl_maxFPS" "{effectiveMaxFpsNumber}"
             SetSave "host_maxFPS" "{effectiveMaxFpsNumber}"
             SetSave "host_maximumFPS" "{effectiveMaxFpsNumber}"
             SetSave "vid_refreshRate" "{refreshRateNumber}"
             SetSave "options_refreshrate" "{refreshRateNumber}"
             SetSave "vid_resolution" "{resParam}"
             SetSave "options_resolution" "{resParam}"
             SetSave "vid_display" "{targetDisplayDevice}"
             SetSave "options_display" "{targetDisplayDevice}"
             SetSave "cg_manualMaxFpsOverride" "{manualFpsValue}"
             SetSave "kongor_manual_fps_override" "{manualFpsEnabledFlag}"
             SetSave "kongor_manual_max_fps" "{manualFpsValue}"
             {detectedMonitorsConfigBlock}
             SetSave "vid_borderless" "{borderlessSetting}"
             SetSave "vid_fullscreen" "{fullscreenSetting}"
             SetSave "sys_sleepTime" "0"
             SetSave "mem_maxHeapSize" "4096"
             {presentationPacingAutoexecBlock}
             SetSave "d3d_pure" "true"
             SetSave "d3d_exclusive" "{exclusiveSetting}"
             SetSave "options_exclusive" "{exclusiveSetting}"
             SetSave "options_fullscreen" "{fullscreenSetting}"
             SetSave "cg_interfaceFPS" "{effectiveInterfaceFpsNumber}"
             SetSave "cg_minimapFPS" "60"
             SetSave "host_sleep" "false"
             SetSave "host_sleepMS" "0"
             SetSave "host_dynamicResReload" "false"
             SetSave "host_vidDriver" "{activeDriver}"

             SetSave "vid_driver" "{activeDriver}"
             SetSave "vid_textureCompression" "{textureCompressionSetting}"
             SetSave "vid_checkOpenGLVersion" "false"

             // Dynamic Soft Shadows & Foliage
             SetSave "vid_shadows" "true"
             SetSave "vid_terrainShadows" "true"
             SetSave "vid_shadowmapSize" "{shadowMapSize}"
             SetSave "vid_shadowmapFilterWidth" "{shadowFilterWidth}"
             SetSave "vid_shadowmapType" "1"
             SetSave "vid_shadowDrawDistance" "{shadowDrawDistance}"
             SetSave "vid_shadowFalloffDistance" "1000.0000"
             SetSave "vid_shadowLeak" "0.1000"
             SetSave "vid_foliageDensity" "{foliageDensity}"
             SetSave "vid_foliageFalloffDistance" "1200.0000"
             SetSave "vid_cullGroundSprites" "{cullGroundSprites}"
             // Engine Shader Baseline Resets (Prevents Fog/Terrain Glitches & Black Meshes)
             SetSave "vid_shaderFalloffQuality" "0"
             SetSave "vid_shaderFogQuality" "0"
             SetSave "vid_shaderLightingQuality" "0"
             SetSave "vid_shaderRimLighting" "true"
             SetSave "vid_shaderWaterQuality" "0"
             SetSave "vid_shaderAmbientOcclusion" "{ambientOcclusionSetting}"
             SetSave "vid_reflectionMapSize" "{reflectionMapSize}"
             SetSave "vid_treeSmoothNormals" "true"
             SetSave "vid_specularLookup" "false"
             SetSave "vid_meshQuality" "0"
             SetSave "vid_modelLOD" "0"
             SetSave "vid_textureFiltering" "3"
             SetSave "vid_antialiasing" "{antialiasingSetting}"
             SetSave "cg_enableLoginAnimation" "false"
             SetSave "ui_disable3DBackground" "false"
             SetSave "ui_background" "0"

             // Low-Latency Spatial Audio Modernization & HRTF Tuning
             SetSave "sound_numChannels" "{audioChannels}"
             SetSave "sound_bufferLength" "{audioBufferLength}"
             SetSave "sound_mixRate" "{audioMixRate}"
             SetSave "sound_recording_mixRate" "{audioMixRate}"
             SetSave "sound_disableRecording" "true"
             SetSave "sound_distanceFactor" "1.0000"
             SetSave "sound_dopplerFactor" "1.0000"
             SetSave "sound_geometryOcclusion" "{occlusionSetting}"
             SetSave "sound_customRolloff" "true"
             SetSave "sound_rolloffScale" "1.0000"
             SetSave "sound_speakerModeStereo" "true"
             SetSave "sound_speakerModeMono" "false"
             SetSave "sound_prologic" "false"
             SetSave "sound_resampler" "2"
             SetSave "sound_useCompressedSamples" "true"

             // Ultra-Smooth 500 FPS Skeletal Animation Interpolation & Scene Prediction
             SetSave "skel_interpolate" "true"
             SetSave "skel_blendAnims" "true"
             SetSave "skel_skipKeyFrames" "false"
             SetSave "scene_interpolate" "true"

             // Competitive High-Tick Network Protocol & Client-Server Synergy (cl_packetSendFPS capped at K2's verified 85 Hz ceiling alongside 128 net_maxPacketRate)
             SetSave "cl_packetSendFPS" "{packetSendFps}"
             SetSave "net_FPS" "{netFps}"
             SetSave "net_maxPacketRate" "{packetRate}"
             SetSave "net_maxBPS" "{maxBps}"
             SetSave "net_maxPacketSize" "1400"
             SetSave "cl_packetEpsilon" "{packetEpsilon}"
             SetSave "net_smoothInterpolation" "1"
             SetSave "cl_smoothGameTime" "true"
             SetSave "cl_lerp" "true"
             SetSave "cl_delta" "true"
             SetSave "cl_packetResendTime" "{packetResendTime}"
             SetSave "cl_splitLargePacket" "true"
             SetSave "cl_dropBadSnapshots" "true"
             SetSave "cl_ignoreBadPackets" "false"
             SetSave "cg_precacheResources" "false"

             // Canonical 20 Hz (50ms) K2 Simulation Tickrate & Anti-Stall Headroom (Required for IEntityState & <onframe> parity)
             SetSave "svr_gameFPS" "20"
             SetSave "svr_maxFramesPerHostFrame" "5"

             // Asynchronous Non-Blocking Logging (Eliminates In-Game Disk Flush Micro-Stutter)
             SetSave "con_verbose" "false"
             SetSave "con_writeLog" "true"
             SetSave "con_appendLog" "true"
             SetSave "con_flushLog" "false"
             SetSave "sys_flushLogs" "false"
             SetSave "log_flush" "false"
             SetSave "con_developer" "false"
             SetSave "host_dev" "false"
             SetSave "con_showNet" "false"
             SetSave "con_showDev" "false"
             SetSave "http_printDebugInfo" "false"
             SetSave "php_printDebugInfo" "false"
             SetSave "http_connectTimeout" "1"
             SetSave "http_timeout" "5"
             SetSave "upd_checkForUpdates" "false"
             SetSave "upd_checkAtStartup" "false"
             SetSave "chat_connectTimeout" "5"
             SetSave "chat_maxReconnectAttempts" "5"

             // Deterministic Game Simulation & Input Responsiveness
             SetSave "m_smooth" "0"
             SetSave "m_filter" "false"
             SetSave "in_mouseRaw" "true"
             SetSave "cg_smoothCamera" "false"
             SetSave "cg_cameraSmoothTime" "0"
             SetSave "sys_dumpOnFatal" "true"
             SetSave "sys_autoSaveDump" "true"
             SetSave "sys_keepOldDumpsOnStartup" "true"
             SetSave "sys_keepDumpAfterUpload" "true"
             Set "sys_disableCrashReporter" "true"
             SetSave "cef_crash_reporting_enabled" "false"
             SetSave "cloud_autoupload" "false"
             SetSave "host_cloudAutoUpload" "false"
             SetSave "host_autoUploadOnLogout" "false"

             // Real-Time Debugging & Notifications
             SetSave "con_notify" "false"
             SetSave "con_notifyLines" "0"
             SetSave "con_notifyTime" "0"

             // Accelerated Hero Mastery & Entity Database Loading
             SetSave "cg_heroLoadingChunkDurationMS" "20"
             SetSave "svr_port" "11235"
             SetSave "svr_voicePort" "11435"
             SetSave "res_unloadHeroResources" "true"
             SetSave "res_unloadMapResources" "true"
             SetSave "cg_disableBackgroundEffects" "true"
             SetSave "cg_forceDefinitionReloadAll" "false"
             SetSave "fs_trackCustomFiles" "false"

             // console (CTRL+F8)
             SetSave "con_height" "0.85"
             SetSave "con_alpha" "0.85"
             SetSave "con_showNet" "false"

             // interface & developer mode
             SetSave "releaseStage_dev" "{devFlag}"
             SetSave "releaseStage_test" "{devFlag}"
             SetSave "releaseStage_stable" "{stableFlag}"
             SetSave "ui_dev" "{devFlag}"
             SetSave "ui_dev_button" "{devFlag}"
             SetSave "ui_showQuickStart" "true"
             SetSave "ui_canCreateGame" "true"
             SetSave "cg_newMainInterface" "true"
             SetSave "cg_24hourClock" "true"

             // Native Low-Health Heartbeat Feature Defaults
             SetSave "cg_heartbeat_enabled" "true"
             SetSave "cg_heartbeat_threshold" "0.2000"
             SetSave "cg_heartbeat_sound" "true"
             SetSave "cg_heartbeat_freq" "80.0000"
             SetSave "cg_heartbeat_show_border" "true"
             SetSave "cg_heartbeat_show_circle" "true"

             // Account Authentication (Zero-Knowledge: plaintext credentials never stored in config)
             {(string.IsNullOrWhiteSpace(savedNickname) ? string.Empty : $"SetSave \"login_name\" \"{savedNickname}\"\nSetSave \"login_rememberName\" \"true\"")}
             """;

        WriteConfigFileAtomically(customConfigurationFilePath, customConfigurationFileContent);
        Log(LogCategory.Initialise, $"[CONFIG_SYNC] Wrote primary configuration: {customConfigurationFilePath}");

        LaunchConfigWriterService.PurgeStaleReplayAutoexecs(rootDir, Log);
        LaunchConfigWriterService.MirrorAutoexecToCanonicalLocations(rootDir, customConfigurationFilePath, customConfigurationFileContent, Log);
        LaunchConfigWriterService.SynchronizeStartupAndAccountProfiles(
            rootDir,
            activeDriver,
            fullscreenSetting,
            borderlessSetting,
            resParam,
            refreshRateNumber,
            shadowMapSize,
            shadowDrawDistance,
            foliageDensity,
            cullGroundSprites,
            textureCompressionSetting,
            ambientOcclusionSetting,
            Log,
            d3dExclusiveSetting: exclusiveSetting,
            targetDisplay: targetDisplayDevice,
            maxFpsNumber: effectiveMaxFpsNumber,
            manualFpsOverride: manualFpsValue);
    }

    public static string? TryResolveCommandLineMasterServerOverride(IReadOnlyList<string>? args)
    {
        if (args is null || args.Count == 0)
            return null;

        for (int i = 0; i < args.Count; i++)
        {
            string arg = args[i]?.Trim() ?? string.Empty;
            string? rawValue = null;

            if (arg.Equals("--masterserver", StringComparison.OrdinalIgnoreCase) ||
                arg.Equals("-masterserver", StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 < args.Count)
                    rawValue = args[i + 1]?.Trim();
            }
            else if (arg.StartsWith("--masterserver=", StringComparison.OrdinalIgnoreCase))
            {
                rawValue = arg["--masterserver=".Length..].Trim();
            }

            if (string.IsNullOrWhiteSpace(rawValue))
                continue;

            rawValue = rawValue.Trim('"', '\'');
            if (rawValue.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
                rawValue = rawValue[7..];
            else if (rawValue.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                rawValue = rawValue[8..];
            rawValue = rawValue.TrimEnd('/');

            if (rawValue.Equals("127.0.0.1:55555", StringComparison.OrdinalIgnoreCase) ||
                rawValue.Equals("localhost:55555", StringComparison.OrdinalIgnoreCase))
            {
                return EndpointPairRegistry.MasterServerLocalhost55555;
            }

            return rawValue;
        }

        return null;
    }

    public static void EnsureUserDirectoryInitialized(string? baseDirectory = null)
        => LaunchConfigWriterService.EnsureUserDirectoryInitialized(baseDirectory);

    private void EnsureLinuxModesettingCompatibility(string workingDirectory)
        => LaunchConfigWriterService.EnsureLinuxModesettingCompatibility(workingDirectory, Log);
}
