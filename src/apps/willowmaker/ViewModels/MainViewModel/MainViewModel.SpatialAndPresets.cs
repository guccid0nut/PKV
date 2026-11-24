// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using Microsoft.Extensions.Logging.Abstractions;
using WILLOWMAKER.Core.Services;

namespace WILLOWMAKER.Core.ViewModels;

public partial class MainViewModel
{
    public SpatialAudioOptions GetCurrentSpatialAudioOptions()
    {
        int channels = int.TryParse(SelectedSpatialChannels.Split(' ')[0], out int parsedCh) ? parsedCh : 128;
        return new SpatialAudioOptions(
            HrtfProfile: SelectedSpatialHrtfProfile,
            Channels: channels,
            OcclusionDamping: EnableOcclusionDamping);
    }

    private void ApplyActiveSpatialSettingsAudition()
    {
        if (IsSuppressingAutoWrite) return;

        AudioModernizationService audioService = new(NullLogger<AudioModernizationService>.Instance);
        SpatialAudioOptions options = GetCurrentSpatialAudioOptions();

        string profileShort = SelectedSpatialHrtfProfile.Length > 24 ? SelectedSpatialHrtfProfile[..21] + "..." : SelectedSpatialHrtfProfile;
        string channelShort = SelectedSpatialChannels.Split(' ')[0] + "ch";
        string occlusionShort = EnableOcclusionDamping ? "Occl:ON" : "Occl:OFF";

        if (IsContinuousAudioPlaying)
        {
            audioService.UpdateSpatialAudioPosition(SpatialAzimuthAngle, SpatialDistance, options);
            SpatialAudioFeedbackText = $"▶ Switched: {profileShort} • {channelShort} • {occlusionShort}";
        }
        else
        {
            audioService.PlayAuditionTrack(
                SelectedSpatialAuditionTrack,
                SpatialAzimuthAngle,
                SpatialDistance,
                isContinuous: false,
                options: options);
            SpatialAudioFeedbackText = $"🎧 Auditioning: {profileShort} • {channelShort} • {occlusionShort}";
        }
        Log(LogCategory.Initialise, $"[Spatial Audio] Applied acoustic profile change -> Profile: {SelectedSpatialHrtfProfile}, Channels: {SelectedSpatialChannels}, Occlusion: {EnableOcclusionDamping}");
    }

    [ObservableProperty]
    public partial ObservableCollection<string> AvailableSpatialAuditionTracks { get; set; } = new()
    {
        "🎵 Can You Feel My Heart (Looping Song)",
        "🔥 Pyromancer Dragon Stun (Spell Cast)",
        "⚔️ Hammerstorm Cleave (Attack & Impact)",
        "🎙️ Announcer: Kongor Has Been Slain!",
        "🔔 Pristine HRTF Reference Chime"
    };

    [ObservableProperty]
    public partial string SelectedSpatialAuditionTrack { get; set; } = "🎵 Can You Feel My Heart (Looping Song)";

    [ObservableProperty] public partial float SpatialSourceX { get; set; } = 0.0f;
    [ObservableProperty] public partial float SpatialSourceY { get; set; } = -0.75f;
    [ObservableProperty] public partial float SpatialAzimuthAngle { get; set; } = 0.0f;
    [ObservableProperty] public partial float SpatialDistance { get; set; } = 0.75f;
    [ObservableProperty] public partial double SpatialRadarBeaconX { get; set; } = 52.0;
    [ObservableProperty] public partial double SpatialRadarBeaconY { get; set; } = 14.0;
    [ObservableProperty] public partial bool IsContinuousAudioPlaying { get; set; } = false;
    [ObservableProperty] public partial string ContinuousAudioPlayButtonText { get; set; } = "▶ Play Continuous Audio";

    [ObservableProperty]
    public partial string SpatialAudioFeedbackText { get; set; } = "Click or drag anywhere on the radar circle to position the 3D sound source";

    partial void OnSelectedSpatialAuditionTrackChanged(string value)
    {
        AudioModernizationService audioService = new(NullLogger<AudioModernizationService>.Instance);
        SpatialAudioOptions options = GetCurrentSpatialAudioOptions();
        if (IsContinuousAudioPlaying)
        {
            audioService.PlayAuditionTrack(value, SpatialAzimuthAngle, SpatialDistance, isContinuous: true, options: options);
            SpatialAudioFeedbackText = $"▶ Looping: {value} at {SpatialAzimuthAngle:F0}° ({GetAngleDirection(SpatialAzimuthAngle)})";
        }
        else
        {
            SpatialAudioFeedbackText = $"Selected track: {value}. Click anywhere on radar to audition.";
        }
        Log(LogCategory.Initialise, $"[Spatial Audio] Selected audition sound: {value}");
    }

    public void SetSpatialRadarCoordinates(float normX, float normY, float angleDeg, float dist, double pixelX, double pixelY)
    {
        SpatialSourceX = normX;
        SpatialSourceY = normY;
        SpatialAzimuthAngle = angleDeg;
        SpatialDistance = dist;
        SpatialRadarBeaconX = pixelX;
        SpatialRadarBeaconY = pixelY;

        string dirName = dist < 0.05f ? "Center (Headphones)" : GetAngleDirection(angleDeg);
        float itdMs = dist < 0.05f ? 0f : MathF.Abs(MathF.Sin(angleDeg * MathF.PI / 180f)) * 0.65f;
        SpatialAudioFeedbackText = $"🎧 {dirName} ({angleDeg:F0}°) • Dist: {(dist * 100):F0}% • ITD: {itdMs:F2}ms • Track: {SelectedSpatialAuditionTrack}";

        AudioModernizationService audioService = new(NullLogger<AudioModernizationService>.Instance);
        SpatialAudioOptions options = GetCurrentSpatialAudioOptions();
        if (IsContinuousAudioPlaying)
        {
            audioService.UpdateSpatialAudioPosition(angleDeg, dist, options);
        }
        else
        {
            audioService.PlayAuditionTrack(SelectedSpatialAuditionTrack, angleDeg, dist, isContinuous: false, options: options);
        }
        Log(LogCategory.Initialise, $"[Spatial Audio] 🎧 Position updated: {dirName} ({angleDeg:F0}°), Dist: {dist:F2}");
    }

    [RelayCommand]
    public void ToggleContinuousAudio()
    {
        AudioModernizationService audioService = new(NullLogger<AudioModernizationService>.Instance);
        SpatialAudioOptions options = GetCurrentSpatialAudioOptions();
        if (!IsContinuousAudioPlaying)
        {
            IsContinuousAudioPlaying = true;
            ContinuousAudioPlayButtonText = "⏸ Pause Continuous Audio";
            audioService.PlayAuditionTrack(SelectedSpatialAuditionTrack, SpatialAzimuthAngle, SpatialDistance, isContinuous: true, options: options);
            SpatialAudioFeedbackText = $"▶ Playing continuous loop: {SelectedSpatialAuditionTrack} (Click/drag to move sound)";
            Log(LogCategory.Initialise, $"[Spatial Audio] ▶ Started continuous playback for '{SelectedSpatialAuditionTrack}'");
        }
        else
        {
            IsContinuousAudioPlaying = false;
            ContinuousAudioPlayButtonText = "▶ Play Continuous Audio";
            audioService.StopAudition();
            SpatialAudioFeedbackText = "⏸ Audio paused. Click anywhere on radar to audition.";
            Log(LogCategory.Initialise, "[Spatial Audio] ⏸ Paused continuous audio playback");
        }
    }

    [RelayCommand]
    public void TriggerCurrentSpatialSound()
    {
        AudioModernizationService audioService = new(NullLogger<AudioModernizationService>.Instance);
        SpatialAudioOptions options = GetCurrentSpatialAudioOptions();
        audioService.PlayAuditionTrack(SelectedSpatialAuditionTrack, SpatialAzimuthAngle, SpatialDistance, isContinuous: false, options: options);
    }

    [RelayCommand]
    public async Task TestSpatialAudio()
    {
        if (IsContinuousAudioPlaying)
        {
            ToggleContinuousAudio();
        }

        AudioModernizationService audioService = new(NullLogger<AudioModernizationService>.Instance);
        SpatialAudioOptions options = GetCurrentSpatialAudioOptions();
        audioService.Play3DOrbitDemonstration(Environment.CurrentDirectory, options: options);
        Log(LogCategory.Initialise, $"[Spatial Audio] 🎧 3D Orbit Sound Test Triggered with profile '{SelectedSpatialHrtfProfile}'");

        // Smoothly animate radar beacon 360° around listener head over duration of sweep (1.4s)
        int steps = 28;
        int delayMs = 50;
        for (int i = 0; i <= steps; i++)
        {
            float angle = (i / (float)steps) * 360f;
            UpdateBeaconCoordinatesOnly(angle, 0.75f);
            await Task.Delay(delayMs);
        }

        SpatialAudioFeedbackText = "🎧 360° Orbit Sweep complete. Click anywhere on radar to audition.";
    }

    public void UpdateBeaconCoordinatesOnly(float angleDeg, float dist = 0.75f)
    {
        void Apply()
        {
            float rad = angleDeg * (MathF.PI / 180f);
            float normX = MathF.Sin(rad) * dist;
            float normY = -MathF.Cos(rad) * dist;
            double pixelX = 59.0 + (normX * 51.0) - 7.0;
            double pixelY = 59.0 + (normY * 51.0) - 7.0;

            SpatialSourceX = normX;
            SpatialSourceY = normY;
            SpatialAzimuthAngle = angleDeg;
            SpatialDistance = dist;
            SpatialRadarBeaconX = pixelX;
            SpatialRadarBeaconY = pixelY;
            string dirName = GetAngleDirection(angleDeg);
            SpatialAudioFeedbackText = $"🎧 360° Orbiting Sweep: {dirName} ({angleDeg:F0}°)";
        }

        try
        {
            if (Dispatcher.UIThread.CheckAccess())
            {
                Apply();
            }
            else
            {
                Dispatcher.UIThread.Post(Apply);
            }
        }
        catch
        {
            Apply();
        }
    }

    [RelayCommand]
    public void PlayDirectionalSpatialSound(string? direction)
    {
        if (string.Equals(direction, "center", StringComparison.OrdinalIgnoreCase))
        {
            SetSpatialRadarCoordinates(0.0f, 0.0f, 0.0f, 0.0f, 52.0, 52.0);
            return;
        }

        float angle = direction?.ToLowerInvariant() switch
        {
            "left" => -90f,
            "right" => 90f,
            "front" => 0f,
            "behind" or "back" => 180f,
            "front-left" => -45f,
            "front-right" => 45f,
            "behind-left" => -135f,
            "behind-right" => 135f,
            _ => float.TryParse(direction, out float parsed) ? parsed : 0f
        };

        // Convert angle to normalized coordinates on radius 0.75
        float rad = angle * (MathF.PI / 180f);
        float normX = MathF.Sin(rad) * 0.75f;
        float normY = -MathF.Cos(rad) * 0.75f;

        // Host width/height 118, center at 59, radius = 51, beacon size 14 (offset 7)
        double pixelX = 59.0 + (normX * 51.0) - 7.0;
        double pixelY = 59.0 + (normY * 51.0) - 7.0;

        SetSpatialRadarCoordinates(normX, normY, angle, 0.75f, pixelX, pixelY);
    }

    private static string GetAngleDirection(float angleDeg)
    {
        float norm = ((angleDeg % 360f) + 360f) % 360f;
        return norm switch
        {
            >= 337.5f or < 22.5f => "Front Center",
            >= 22.5f and < 67.5f => "Front-Right",
            >= 67.5f and < 112.5f => "Right Ear",
            >= 112.5f and < 157.5f => "Behind-Right",
            >= 157.5f and < 202.5f => "Directly Behind",
            >= 202.5f and < 247.5f => "Behind-Left",
            >= 247.5f and < 292.5f => "Left Ear",
            _ => "Front-Left"
        };
    }

    [RelayCommand]
    public void PurgeShaderCache()
    {
        RenderModernizationService renderService = new(NullLogger<RenderModernizationService>.Instance);
        string clientDir = Environment.CurrentDirectory;
        renderService.PurgeShaderCache(clientDir);
        UpdateShaderCacheStatus(renderService);
        Log(LogCategory.Initialise, "[Shader Cache] 🧹 Purged stale DXVK cache and re-seeded pristine base shader cache.");
    }

    public void UpdateShaderCacheStatus(RenderModernizationService? service = null)
    {
        RenderModernizationService renderService = service ?? new(NullLogger<RenderModernizationService>.Instance);
        ShaderCacheStats stats = renderService.GetShaderCacheStats();
        ShaderCacheStatusText = stats.FileCount > 0
            ? $"Shader Cache: {stats.FileCount} Files ({stats.FormattedSize})"
            : "Shader Cache: Empty (Will seed on launch)";
    }

    private HardwareProfile? _cachedHardwareProfile;
    private HardwareRecommendation? _cachedHardwareRecommendation;

    internal sealed record GraphicsPipelineSnapshot(
        string ShadowFidelity,
        string ShadowMapSize,
        string AntialiasingLabel,
        string ReflectionMapSize,
        bool AmbientOcclusionOn,
        string PresentationMode,
        string RefreshCapLabel,
        string VulkanHudLabel);

    private (HardwareProfile Profile, HardwareRecommendation Recommendation) GetCachedHardwareRecommendation(bool forceRefresh = false)
    {
        if (!forceRefresh && _cachedHardwareProfile is not null && _cachedHardwareRecommendation is not null)
        {
            return (_cachedHardwareProfile, _cachedHardwareRecommendation);
        }

        HardwareProfile profile = HardwareDetectionService.DetectHardware();
        HardwareRecommendation rec = HardwareDetectionService.GetRecommendation(profile);
        _cachedHardwareProfile = profile;
        _cachedHardwareRecommendation = rec;
        return (profile, rec);
    }

    private static string FormatAntialiasingLabel(string antialiasingCvar) => antialiasingCvar switch
    {
        "4" => "4x MSAA",
        "2" => "2x MSAA",
        _ => "Off"
    };

    private static string FormatShortHudLabel(string hudMode) => hudMode switch
    {
        "Off (Clean Screen)" => "Off",
        "Pro Diagnostics" => "Pro Diag",
        _ => "Minimal FPS"
    };

    private static string FormatShortPresentationLabel(string presentationMode) => presentationMode switch
    {
        "DXVK Mailbox (Low-Latency)" => "Mailbox",
        "Legacy Blit (DWM Composited)" => "Legacy Blit",
        _ => "DirectFlip"
    };

    internal GraphicsPipelineSnapshot CaptureGraphicsPipelineSnapshot()
    {
        var (shadowMapSize, _, _, _, _, ambientOcclusionSetting, _, refreshRateNumber, maxFpsNumber, _) = ResolveActiveGraphicsSettings();
        var (aaCvar, reflSize) = ResolvePresetQualityCvars(SelectedRayTracingPreset);
        string capLabel = ManualRefreshRateOverrideEnabled
            ? (maxFpsNumber == "1000" ? "Uncapped" : $"{maxFpsNumber} FPS (Custom)")
            : $"{refreshRateNumber} Hz";

        return new GraphicsPipelineSnapshot(
            ShadowFidelity: SelectedShadowFidelity,
            ShadowMapSize: shadowMapSize,
            AntialiasingLabel: FormatAntialiasingLabel(aaCvar),
            ReflectionMapSize: reflSize,
            AmbientOcclusionOn: string.Equals(ambientOcclusionSetting, "true", StringComparison.OrdinalIgnoreCase),
            PresentationMode: FormatShortPresentationLabel(SelectedPresentationMode),
            RefreshCapLabel: capLabel,
            VulkanHudLabel: FormatShortHudLabel(SelectedVulkanHudMode));
    }

    internal static string BuildGraphicsPipelineDiff(
        GraphicsPipelineSnapshot before,
        GraphicsPipelineSnapshot after,
        string presetDisplayName)
    {
        List<string> changes = [];
        if (!string.Equals(before.ShadowMapSize, after.ShadowMapSize, StringComparison.OrdinalIgnoreCase))
            changes.Add($"Shadows {before.ShadowMapSize} → {after.ShadowMapSize}");
        if (!string.Equals(before.AntialiasingLabel, after.AntialiasingLabel, StringComparison.OrdinalIgnoreCase))
            changes.Add($"AA {before.AntialiasingLabel} → {after.AntialiasingLabel}");
        if (!string.Equals(before.ReflectionMapSize, after.ReflectionMapSize, StringComparison.OrdinalIgnoreCase))
            changes.Add($"Refl {before.ReflectionMapSize} → {after.ReflectionMapSize}");
        if (before.AmbientOcclusionOn != after.AmbientOcclusionOn)
            changes.Add($"SSAO {(before.AmbientOcclusionOn ? "On" : "Off")} → {(after.AmbientOcclusionOn ? "On" : "Off")}");
        if (!string.Equals(before.PresentationMode, after.PresentationMode, StringComparison.OrdinalIgnoreCase))
            changes.Add($"Present {before.PresentationMode} → {after.PresentationMode}");
        if (!string.Equals(before.RefreshCapLabel, after.RefreshCapLabel, StringComparison.OrdinalIgnoreCase))
            changes.Add($"Cap {before.RefreshCapLabel} → {after.RefreshCapLabel}");
        if (!string.Equals(before.VulkanHudLabel, after.VulkanHudLabel, StringComparison.OrdinalIgnoreCase))
            changes.Add($"HUD {before.VulkanHudLabel} → {after.VulkanHudLabel}");

        return changes.Count > 0
            ? $"✓ Applied {presetDisplayName}: {string.Join(" • ", changes)}"
            : $"✓ {presetDisplayName} already active — no changes needed";
    }

    public void RefreshActiveGraphicsPresetState(string? changeBannerText = null, string? changeBannerColor = null)
    {
        GraphicsPipelineSnapshot snap = CaptureGraphicsPipelineSnapshot();
        string shadowTierLabel = snap.ShadowFidelity.Split(' ')[0];
        ActiveGraphicsPipelineSummary =
            $"Shadows: {snap.ShadowMapSize} ({shadowTierLabel}) • AA: {snap.AntialiasingLabel} • Refl: {snap.ReflectionMapSize} • SSAO: {(snap.AmbientOcclusionOn ? "On" : "Off")} • Cap: {snap.RefreshCapLabel} • HUD: {snap.VulkanHudLabel}";

        HardwareRecommendation? rec = null;
        try
        {
            (_, rec) = GetCachedHardwareRecommendation(forceRefresh: false);
        }
        catch
        {
            // Keep null if hardware detection unavailable
        }

        bool isCleanHud = string.Equals(SelectedVulkanHudMode, "Off (Clean Screen)", StringComparison.OrdinalIgnoreCase);
        bool isDirectFlip = EnableDirectFlip && string.Equals(SelectedPresentationMode, "DirectFlip (FLIP_DISCARD)", StringComparison.OrdinalIgnoreCase);
        bool isAutoHz = !ManualRefreshRateOverrideEnabled;

        bool matchesRecommended = rec is not null &&
                                  string.Equals(SelectedRayTracingPreset, rec.RayTracingPreset, StringComparison.OrdinalIgnoreCase) &&
                                  string.Equals(SelectedShadowFidelity, rec.ShadowFidelity, StringComparison.OrdinalIgnoreCase) &&
                                  string.Equals(SelectedPresentationMode, rec.PresentationMode, StringComparison.OrdinalIgnoreCase) &&
                                  isAutoHz &&
                                  isCleanHud;

        bool matchesBestFps = string.Equals(SelectedRayTracingPreset, "Competitive (0% RT)", StringComparison.OrdinalIgnoreCase) &&
                              string.Equals(SelectedShadowFidelity, "Medium (1024)", StringComparison.OrdinalIgnoreCase) &&
                              isDirectFlip &&
                              isAutoHz &&
                              isCleanHud;

        bool matchesBestGraphics = string.Equals(SelectedRayTracingPreset, "Ultra (Path-Tracing)", StringComparison.OrdinalIgnoreCase) &&
                                   string.Equals(SelectedShadowFidelity, "Ultra (4096)", StringComparison.OrdinalIgnoreCase) &&
                                   isDirectFlip &&
                                   isAutoHz &&
                                   isCleanHud;

        if (matchesRecommended)
        {
            ActiveGraphicsPresetKey = "Recommended";
            ActiveGraphicsPresetBadgeText = "RECOMMENDED";
            ActiveGraphicsPresetBadgeBackground = "#0C2036";
            ActiveGraphicsPresetBadgeBorder = "#0284C7";
            ActiveGraphicsPresetBadgeForeground = "#38BDF8";
        }
        else if (matchesBestFps)
        {
            ActiveGraphicsPresetKey = "BestFps";
            ActiveGraphicsPresetBadgeText = "BEST FPS";
            ActiveGraphicsPresetBadgeBackground = "#064E3B";
            ActiveGraphicsPresetBadgeBorder = "#10B981";
            ActiveGraphicsPresetBadgeForeground = "#34D399";
        }
        else if (matchesBestGraphics)
        {
            ActiveGraphicsPresetKey = "BestGraphics";
            ActiveGraphicsPresetBadgeText = "BEST GRAPHICS";
            ActiveGraphicsPresetBadgeBackground = "#2E1065";
            ActiveGraphicsPresetBadgeBorder = "#8B5CF6";
            ActiveGraphicsPresetBadgeForeground = "#C4B5FD";
        }
        else
        {
            ActiveGraphicsPresetKey = "Custom";
            ActiveGraphicsPresetBadgeText = "CUSTOM";
            ActiveGraphicsPresetBadgeBackground = "#1E293B";
            ActiveGraphicsPresetBadgeBorder = "#64748B";
            ActiveGraphicsPresetBadgeForeground = "#E2E8F0";
        }

        IsRecommendedPresetActive = matchesRecommended;
        IsBestFpsPresetActive = matchesBestFps && !matchesRecommended;
        IsBestGraphicsPresetActive = matchesBestGraphics && !matchesRecommended;

        if (!string.IsNullOrWhiteSpace(changeBannerText))
        {
            LastPresetChangeSummary = changeBannerText;
            LastPresetChangeForeground = changeBannerColor ?? "#34D399";
        }
    }

    public void RefreshDisplayVrrReadiness()
    {
        try
        {
            string activeDriver = LaunchConfigWriterService.ResolveEffectiveVideoDriver(
                clientDirectory: null,
                useD3D12: SelectedSwapchainBackend.Contains("D3D9On12", StringComparison.OrdinalIgnoreCase));

            DisplayVrrReadinessReport report = DisplayVrrDetectionService.InspectDisplayReadiness(
                SelectedMonitor,
                SelectedDisplayMode,
                SelectedPresentationMode,
                EnableDirectFlip,
                activeDriver);

            VrrReadinessPillText = report.PillText;
            VrrReadinessTooltip = report.DetailTooltip;
            VrrReadinessBackground = report.BadgeBackground;
            VrrReadinessBorder = report.BadgeBorder;
            VrrReadinessForeground = report.BadgeForeground;
            RecommendedVrrCapFps = report.RecommendedVrrCapFps;

            string levelTag = report.Level switch
            {
                DisplayVrrReadinessLevel.VrrReady => "VRR_READY",
                DisplayVrrReadinessLevel.Advisory => "ADVISORY",
                _ => "FIXED_REFRESH"
            };
            Log(LogCategory.Initialise,
                $"[DISPLAY_VRR] Monitor='{SelectedMonitor?.FriendlyName ?? "Unknown"}' | Level={levelTag} | VRR={(report.IsMonitorVrrCapable ? $"{report.VrrMinHz}-{report.VrrMaxHz}Hz" : "None")} | DesktopHz={report.ActiveDesktopHz} | MaxHz={report.PanelMaxHz} | Mode='{SelectedDisplayMode}' | Present='{SelectedPresentationMode}' | Pill='{report.PillText}'");
        }
        catch (Exception ex)
        {
            VrrReadinessPillText = "Display VRR status unavailable";
            VrrReadinessTooltip = $"Display readiness probe encountered an error: {ex.Message}";
            VrrReadinessBackground = "#101726";
            VrrReadinessBorder = "#26324A";
            VrrReadinessForeground = "#94A3B8";
            RecommendedVrrCapFps = 0;
            Log(LogCategory.Initialise, $"[DISPLAY_VRR] Probe notice: {ex.Message}");
        }
    }

    [RelayCommand]
    public void ApplyVrrLowLatencyCap()
    {
        int capFps = RecommendedVrrCapFps;
        if (capFps < 57)
        {
            int fallbackHz = SelectedMonitor is not null ? GetAutoMaxRefreshRateHz() : 0;
            capFps = DisplayVrrDetectionService.CalculateRecommendedVrrCapFps(fallbackHz);
        }

        if (capFps < 57)
        {
            return;
        }

        _isApplyingPreset = true;
        try
        {
            ManualRefreshRateOverrideEnabled = true;
            ManualMaxFpsText = capFps.ToString(System.Globalization.CultureInfo.InvariantCulture);
            IsManualRefreshRateExpanded = true;
        }
        finally
        {
            _isApplyingPreset = false;
        }

        UpdateEffectiveRefreshRateSummary();
        SaveLauncherSettings();
        WriteCustomConfiguration();
        RefreshActiveGraphicsPresetState(
            $"✓ Applied VRR Low-Latency Cap → {capFps} FPS (keeps frames inside VRR window)",
            "#34D399");
        Log(LogCategory.Initialise, $"[DISPLAY_VRR] Applied sub-refresh VRR low-latency cap: {capFps} FPS (Monitor max {GetAutoMaxRefreshRateHz()} Hz).");
    }

    [RelayCommand]
    public void ApplyBestFpsPreset()
    {
        GraphicsPipelineSnapshot before = CaptureGraphicsPipelineSnapshot();

        _isApplyingPreset = true;
        try
        {
            EnableRayTracing = false;
            SelectedRayTracingPreset = "Competitive (0% RT)";
            EnableAsyncPipelineCaching = true;
            EnableTextureClarityBoost = false;
            ManualRefreshRateOverrideEnabled = false;
            SyncSelectedRefreshRateFromMonitor();
            SelectedShadowFidelity = "Medium (1024)";
            SelectedVulkanHudMode = "Off (Clean Screen)";
            EnableNvidiaReflex = false;
            EnableDirectFlip = true;
            SelectedPresentationMode = "DirectFlip (FLIP_DISCARD)";
            SelectedSwapchainBackend = "DXVK (Vulkan WSI)";
            SelectedNetworkProfile = "Competitive 128-Tick (Esports / Fiber)";
        }
        finally
        {
            _isApplyingPreset = false;
        }

        UpdateEffectiveRefreshRateSummary();
        SaveLauncherSettings();
        WriteCustomConfiguration();
        GraphicsPipelineSnapshot after = CaptureGraphicsPipelineSnapshot();
        string diffSummary = BuildGraphicsPipelineDiff(before, after, "Best FPS");
        RefreshActiveGraphicsPresetState(diffSummary, diffSummary.Contains("already active", StringComparison.Ordinal) ? "#38BDF8" : "#34D399");
        RefreshDisplayVrrReadiness();
        Log(LogCategory.Initialise, $"[Preset Applied] ⚡ {diffSummary}");
    }

    [RelayCommand]
    public void ApplyBestGraphicsPreset()
    {
        GraphicsPipelineSnapshot before = CaptureGraphicsPipelineSnapshot();

        _isApplyingPreset = true;
        try
        {
            EnableRayTracing = true;
            SelectedRayTracingPreset = "Ultra (Path-Tracing)";
            EnableAsyncPipelineCaching = true;
            EnableTextureClarityBoost = false;
            ManualRefreshRateOverrideEnabled = false;
            SyncSelectedRefreshRateFromMonitor();
            SelectedShadowFidelity = "Ultra (4096)";
            SelectedVulkanHudMode = "Off (Clean Screen)";
            EnableNvidiaReflex = false;
            EnableDirectFlip = true;
            SelectedPresentationMode = "DirectFlip (FLIP_DISCARD)";
            SelectedSwapchainBackend = "DXVK (Vulkan WSI)";
            SelectedNetworkProfile = "Competitive 128-Tick (Esports / Fiber)";
        }
        finally
        {
            _isApplyingPreset = false;
        }

        UpdateEffectiveRefreshRateSummary();
        SaveLauncherSettings();
        WriteCustomConfiguration();
        GraphicsPipelineSnapshot after = CaptureGraphicsPipelineSnapshot();
        string diffSummary = BuildGraphicsPipelineDiff(before, after, "Best Graphics");
        RefreshActiveGraphicsPresetState(diffSummary, diffSummary.Contains("already active", StringComparison.Ordinal) ? "#38BDF8" : "#C4B5FD");
        RefreshDisplayVrrReadiness();
        Log(LogCategory.Initialise, $"[Preset Applied] ✨ {diffSummary}");
    }

    [RelayCommand]
    public void ApplyRecommendedSettings()
    {
        GraphicsPipelineSnapshot before = CaptureGraphicsPipelineSnapshot();
        (HardwareProfile profile, HardwareRecommendation rec) = GetCachedHardwareRecommendation(forceRefresh: true);
        int monitorAutoHz = SelectedMonitor is not null ? GetAutoMaxRefreshRateHz() : profile.RefreshRateHz;
        (string recAa, string recRefl) = ResolvePresetQualityCvars(rec.RayTracingPreset);

        DetectedHardwareSummary = $"{profile.GpuName} ({(profile.VramBytes > 0 ? $"{profile.VramBytes / (1024.0 * 1024.0 * 1024.0):F1} GB VRAM" : "Shared")}) • {profile.SystemRamBytes / (1024.0 * 1024.0 * 1024.0):F0} GB RAM • {monitorAutoHz} Hz";
        RecommendedSettingsTooltip = $"🎯 Recommended Settings for {profile.GpuName}\n" +
                                     $"• Shadows: {rec.ShadowFidelity} | AA: {FormatAntialiasingLabel(recAa)} | Reflections: {recRefl}\n" +
                                     $"• Cap: Auto {monitorAutoHz} Hz | Present: {FormatShortPresentationLabel(rec.PresentationMode)} | HUD: Off\n" +
                                     $"• Tier: {rec.TierLabel}\n\n" +
                                     $"Click to apply recommended settings for this PC.";

        _isApplyingPreset = true;
        try
        {
            EnableRayTracing = rec.EnableRayTracing;
            SelectedRayTracingPreset = rec.RayTracingPreset;
            SelectedShadowFidelity = rec.ShadowFidelity;
            SelectedVulkanHudMode = "Off (Clean Screen)";
            ManualRefreshRateOverrideEnabled = false;
            if (SelectedMonitor is not null)
            {
                SyncSelectedRefreshRateFromMonitor();
            }
            else
            {
                EnsureRefreshRateAvailable(rec.RefreshRate);
                SelectedRefreshRate = rec.RefreshRate;
            }
            EnableTextureClarityBoost = rec.EnableTextureClarityBoost;
            EnableNvidiaReflex = rec.EnableNvidiaReflex;
            EnableDirectFlip = rec.EnableDirectFlip;
            SelectedPresentationMode = rec.PresentationMode;
            SelectedSwapchainBackend = rec.SwapchainBackend;
            if (AvailableSpatialChannels.Contains(rec.SpatialChannels))
            {
                SelectedSpatialChannels = rec.SpatialChannels;
            }
            SelectedNetworkProfile = "Competitive 128-Tick (Esports / Fiber)";
        }
        finally
        {
            _isApplyingPreset = false;
        }

        UpdateEffectiveRefreshRateSummary();
        SaveLauncherSettings();
        WriteCustomConfiguration();
        GraphicsPipelineSnapshot after = CaptureGraphicsPipelineSnapshot();
        string diffSummary = BuildGraphicsPipelineDiff(before, after, "Recommended Settings");
        RefreshActiveGraphicsPresetState(diffSummary, "#38BDF8");
        RefreshDisplayVrrReadiness();

        Log(LogCategory.Initialise, $"[Hardware Detected] 🖥️ GPU: {profile.GpuName} ({(profile.VramBytes > 0 ? $"{profile.VramBytes / (1024.0 * 1024.0 * 1024.0):F1} GB VRAM" : "Shared Memory")}) | RAM: {profile.SystemRamBytes / (1024.0 * 1024.0 * 1024.0):F0} GB | Display Max: {monitorAutoHz} Hz");
        Log(LogCategory.Initialise, $"[Recommendation Applied] 🎯 {diffSummary} ({rec.TierLabel})");
    }

    private void RefreshHardwareDetectionTooltip()
    {
        try
        {
            (HardwareProfile profile, HardwareRecommendation rec) = GetCachedHardwareRecommendation(forceRefresh: false);
            int monitorAutoHz = SelectedMonitor is not null ? GetAutoMaxRefreshRateHz() : profile.RefreshRateHz;
            (string recAa, string recRefl) = ResolvePresetQualityCvars(rec.RayTracingPreset);
            DetectedHardwareSummary = $"{profile.GpuName} ({(profile.VramBytes > 0 ? $"{profile.VramBytes / (1024.0 * 1024.0 * 1024.0):F1} GB VRAM" : "Shared")}) • {profile.SystemRamBytes / (1024.0 * 1024.0 * 1024.0):F0} GB RAM • {monitorAutoHz} Hz";
            RecommendedSettingsTooltip = $"🎯 Auto-Detect & Apply Recommended Settings\n\n" +
                                         $"• Hardware: {DetectedHardwareSummary}\n" +
                                         $"• Applies: Shadows {rec.ShadowFidelity}, AA {FormatAntialiasingLabel(recAa)}, Refl {recRefl}, Auto {monitorAutoHz} Hz, DirectFlip, HUD Off\n" +
                                         $"• Tier: {rec.TierLabel}";
        }
        catch
        {
            RecommendedSettingsTooltip = "Auto-detect PC hardware and apply optimal graphics & FPS configuration";
        }
    }

    partial void OnEnableCdnSyncChanged(bool value)
    {
        SaveLauncherSettings();
    }

    partial void OnEnableRayTracingChanged(bool value)
    {
        if (!IsSuppressingAutoWrite)
        {
            if (value && SelectedRayTracingPreset.StartsWith("Competitive", StringComparison.OrdinalIgnoreCase))
                SelectedRayTracingPreset = "Balanced RTX (RTAO)";
            else if (!value) SelectedRayTracingPreset = "Competitive (0% RT)";
        }

        SaveLauncherSettings();
    }

    partial void OnSelectedRayTracingPresetChanged(string value)
    {
        if (!IsSuppressingAutoWrite)
        {
            EnableRayTracing = !value.StartsWith("Competitive", StringComparison.OrdinalIgnoreCase);
            SaveLauncherSettings();
            WriteCustomConfiguration();
            (string aa, string refl) = ResolvePresetQualityCvars(value);
            RefreshActiveGraphicsPresetState($"⚙ Updated Graphics Quality → {FormatAntialiasingLabel(aa)} • Refl {refl}", "#38BDF8");
            Log(LogCategory.Initialise, $"[Graphics Profile] Switched to '{value}'. Updated AA, reflections, and DXVK configuration.");
            return;
        }

        SaveLauncherSettings();
    }

    partial void OnEnableAsyncPipelineCachingChanged(bool value)
    {
        SaveLauncherSettings();
    }

    partial void OnEnableTextureClarityBoostChanged(bool value)
    {
        SaveLauncherSettings();
    }

    private bool _isSyncingMonitorRefreshRate;
    private bool _explicitLegacyRefreshRateOverride;

    public int GetAutoMaxRefreshRateHz()
    {
        if (SelectedMonitor is { } mon)
        {
            var (selW, selH) = HardwareDetectionService.ParseResolutionString(SelectedResolution);
            var matchedRes = mon.SupportedResolutions.FirstOrDefault(r => r.Width == selW && r.Height == selH);
            int maxHz = matchedRes?.MaxRefreshRateHz ?? mon.MaxRefreshRateHz;
            return maxHz >= 30 ? maxHz : 60;
        }

        return 144;
    }

    public void SyncSelectedRefreshRateFromMonitor()
    {
        if (SelectedMonitor is not { } mon) return;

        var (selW, selH) = HardwareDetectionService.ParseResolutionString(SelectedResolution);
        MonitorResolutionOption? matchedRes = mon.SupportedResolutions
            .FirstOrDefault(r => r.Width == selW && r.Height == selH)
            ?? mon.SupportedResolutions.FirstOrDefault(r => r.IsNative);

        int autoHz = GetAutoMaxRefreshRateHz();
        string autoLabel = $"{autoHz} Hz";
        _isSyncingMonitorRefreshRate = true;
        try
        {
            List<int> injectedEdidRates = [];
            if (matchedRes?.SupportedRefreshRatesHz is { Count: > 0 } edidRates)
            {
                foreach (int hz in edidRates.Distinct().OrderBy(r => r))
                {
                    if (hz < 30 || hz > 500) continue;
                    EnsureRefreshRateAvailable($"{hz} Hz");
                    injectedEdidRates.Add(hz);
                }
            }

            EnsureRefreshRateAvailable(autoLabel);
            SelectedRefreshRate = autoLabel;
            _explicitLegacyRefreshRateOverride = false;

            Log(LogCategory.Initialise,
                $"[MONITOR_EDID_SYNC] Monitor '{mon.FriendlyName}' ({selW}x{selH}) -> autoHz={autoHz} Hz, EDID SupportedRefreshRatesHz=[{string.Join(", ", injectedEdidRates)}]");
        }
        finally
        {
            _isSyncingMonitorRefreshRate = false;
        }
    }

    public void UpdateEffectiveRefreshRateSummary()
    {
        if (ManualRefreshRateOverrideEnabled && int.TryParse(ManualMaxFpsText, out int manualFps) && manualFps >= 0)
        {
            EffectiveRefreshRateSummary = manualFps == 0
                ? "Manual Override: Uncapped (Max FPS)"
                : $"Manual Override: {manualFps} FPS Cap";
        }
        else if (SelectedMonitor is { } mon)
        {
            int autoHz = GetAutoMaxRefreshRateHz();
            EffectiveRefreshRateSummary = $"Auto: {autoHz} Hz (Monitor #{mon.Index} — {mon.FriendlyName})";
        }
        else
        {
            EffectiveRefreshRateSummary = "Auto (Detected)";
        }
    }

    partial void OnSelectedMonitorChanged(MonitorDisplayInfo? value)
    {
        if (value is null) return;

        string currentSelected = SelectedResolution;
        AvailableResolutions.Clear();
        foreach (var res in value.SupportedResolutions)
        {
            AvailableResolutions.Add(res.DisplayLabel);
        }

        var native = value.SupportedResolutions.FirstOrDefault(r => r.IsNative);
        if (_isLoadingSettings)
        {
            string? match = AvailableResolutions.FirstOrDefault(r => string.Equals(r, currentSelected, StringComparison.OrdinalIgnoreCase));
            SelectedResolution = match ?? native?.DisplayLabel ?? AvailableResolutions.FirstOrDefault() ?? "1920x1080 (1080p FHD)";
        }
        else
        {
            SelectedResolution = native?.DisplayLabel ?? AvailableResolutions.FirstOrDefault() ?? "1920x1080 (1080p FHD)";
        }

        SyncSelectedRefreshRateFromMonitor();
        UpdateEffectiveRefreshRateSummary();
        SaveLauncherSettings();
        if (!_isLoadingSettings)
        {
            EnsureVulkanDxvkConfigured();
            WriteCustomConfiguration();
            RefreshHardwareDetectionTooltip();
            RefreshActiveGraphicsPresetState();
            RefreshDisplayVrrReadiness();
            Log(LogCategory.Initialise, $"[Target Monitor] Switched to '{value.DisplayLabel}' (token='{value.NormalizedDeviceToken}', autoMaxHz={GetAutoMaxRefreshRateHz()} Hz).");
        }
    }

    partial void OnManualRefreshRateOverrideEnabledChanged(bool value)
    {
        UpdateEffectiveRefreshRateSummary();
        SaveLauncherSettings();
        if (!IsSuppressingAutoWrite)
        {
            EnsureVulkanDxvkConfigured();
            WriteCustomConfiguration();
            RefreshActiveGraphicsPresetState($"⚙ Frame Cap → {EffectiveRefreshRateSummary}", "#38BDF8");
            Log(LogCategory.Initialise, $"[Refresh Rate Override] Manual override {(value ? "enabled" : "disabled")}.");
        }
    }

    partial void OnManualMaxFpsTextChanged(string value)
    {
        UpdateEffectiveRefreshRateSummary();
        SaveLauncherSettings();
        if (!IsSuppressingAutoWrite)
        {
            EnsureVulkanDxvkConfigured();
            WriteCustomConfiguration();
            RefreshActiveGraphicsPresetState();
        }
    }

    partial void OnSelectedRefreshRateChanged(string value)
    {
        if (!IsSuppressingAutoWrite && !_isSyncingMonitorRefreshRate)
        {
            _explicitLegacyRefreshRateOverride = true;
        }
        SaveLauncherSettings();
        if (!IsSuppressingAutoWrite && !_isSyncingMonitorRefreshRate)
        {
            EnsureVulkanDxvkConfigured();
            WriteCustomConfiguration();
            RefreshActiveGraphicsPresetState($"⚙ Refresh Cap → {value}", "#38BDF8");
            Log(LogCategory.Initialise, $"[Refresh Rate / FPS Cap] Switched to '{value}'. Updated DXVK frame rate cap and engine settings.");
        }
    }

    partial void OnSelectedResolutionChanged(string value)
    {
        if (!IsSuppressingAutoWrite && SelectedMonitor is not null)
        {
            SyncSelectedRefreshRateFromMonitor();
            UpdateEffectiveRefreshRateSummary();
        }
        SaveLauncherSettings();
        if (!IsSuppressingAutoWrite)
        {
            WriteCustomConfiguration();
            RefreshActiveGraphicsPresetState();
            Log(LogCategory.Initialise, $"[Resolution] Switched to '{value}' (Auto Max Hz: {GetAutoMaxRefreshRateHz()} Hz). Updated client display configuration.");
        }
    }

    partial void OnSelectedDisplayModeChanged(string value)
    {
        SaveLauncherSettings();
        if (!IsSuppressingAutoWrite)
        {
            if (OperatingSystem.IsWindows() && Process.GetProcessesByName("hon_x64").Length > 0)
            {
                Log(LogCategory.Executable, "[Display Mode] Notice: 'hon_x64.exe' is currently running. Exit the game to apply changes, as closing the game will overwrite startup.cfg with its active session settings.");
            }
            EnsureVulkanDxvkConfigured();
            WriteCustomConfiguration();
            RefreshActiveGraphicsPresetState();
            RefreshDisplayVrrReadiness();
            Log(LogCategory.Initialise, $"[Display Mode] Switched to '{value}'. Updated client configuration.");
        }
    }

    partial void OnSelectedShadowFidelityChanged(string value)
    {
        SaveLauncherSettings();
        if (!IsSuppressingAutoWrite)
        {
            WriteCustomConfiguration();
            RefreshActiveGraphicsPresetState($"⚙ Updated Shadow Fidelity → {value}", "#38BDF8");
            Log(LogCategory.Initialise, $"[Shadow Fidelity] Switched to '{value}'. Updated shadow map resolution and client configuration.");
        }
    }

    partial void OnSelectedVulkanHudModeChanged(string value)
    {
        SaveLauncherSettings();
        if (!IsSuppressingAutoWrite)
        {
            EnsureVulkanDxvkConfigured();
            RefreshActiveGraphicsPresetState($"⚙ Updated Vulkan HUD → {value}", "#38BDF8");
            Log(LogCategory.Initialise, $"[Vulkan HUD] Switched to '{value}'. Updated DXVK HUD configuration.");
        }
    }

    partial void OnEnableNvidiaReflexChanged(bool value)
    {
        SaveLauncherSettings();
        if (!IsSuppressingAutoWrite)
        {
            EnsureVulkanDxvkConfigured();
            RefreshActiveGraphicsPresetState();
            Log(LogCategory.Initialise, $"[NVIDIA Reflex] Low-latency mode {(value ? "enabled" : "disabled")}. Updated DXVK nvLowLatency setting.");
        }
    }

    partial void OnEnableFidelityFxRcasChanged(bool value)
    {
        SaveLauncherSettings();
        if (!IsSuppressingAutoWrite)
        {
            Log(LogCategory.Initialise, $"[FidelityFX RCAS] Sharpening {(value ? "enabled" : "disabled")}.");
        }
    }

    partial void OnFidelityFxRcasSharpnessChanged(float value)
    {
        FidelityFxRcasSharpnessText = $"{(int)Math.Round(value * 100)}%";
        SaveLauncherSettings();
    }

    partial void OnEnableDirectFlipChanged(bool value)
    {
        SaveLauncherSettings();
        if (!IsSuppressingAutoWrite)
        {
            EnsureVulkanDxvkConfigured();
            RefreshActiveGraphicsPresetState();
            RefreshDisplayVrrReadiness();
            Log(LogCategory.Initialise, $"[DirectFlip] DirectFlip {(value ? "enabled" : "disabled")}. Updated DXVK configuration.");
        }
    }

    partial void OnSelectedPresentationModeChanged(string value)
    {
        SaveLauncherSettings();
        if (!IsSuppressingAutoWrite)
        {
            EnsureVulkanDxvkConfigured();
            WriteCustomConfiguration();
            RefreshActiveGraphicsPresetState($"⚙ Updated Presentation → {FormatShortPresentationLabel(value)}", "#38BDF8");
            RefreshDisplayVrrReadiness();
            Log(LogCategory.Initialise, $"[Presentation Mode] Presentation mode switched to '{value}'. Synchronized DXVK and engine pacing configuration.");
        }
    }

    partial void OnSelectedSwapchainBackendChanged(string value)
    {
        SaveLauncherSettings();
        if (!IsSuppressingAutoWrite)
        {
            EnsureVulkanDxvkConfigured();
            RefreshDisplayVrrReadiness();
            Log(LogCategory.Initialise, $"[Swapchain Backend] Backend switched to '{value}'. Updated render configuration.");
        }
    }

    partial void OnSelectedNetworkProfileChanged(string value)
    {
        SaveLauncherSettings();
        if (!IsSuppressingAutoWrite)
        {
            WriteCustomConfiguration();
            Log(LogCategory.Initialise, $"[Network & Tickrate Profile] Switched to '{value}'. Updated high-tick esports client-server networking configuration.");
        }
    }
}
