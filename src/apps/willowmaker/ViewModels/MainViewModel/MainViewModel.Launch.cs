// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2022-2026 [K]ONGOR and Contributors
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV) -- Upstream lineage: https://github.com/Project-KONGOR-Open-Source

using System.Net.Sockets;
using ASPIRE.Common.Vulkan;
using Microsoft.Extensions.Logging.Abstractions;
using WILLOWMAKER.Core.Services;
using WILLOWMAKER.Core.Services.Diagnostics;
using WILLOWMAKER.Services.P2P;
using Kongor.Core.Vfs;
using SuspendedProcessWithStreamsResult = Kongor.Core.Services.Native.SuspendedProcessWithStreamsResult;

namespace WILLOWMAKER.Core.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private DiscordPresenceService? _activeDiscordPresence;
    private WillowmakerIpcService? _activeIpcService;
    private string? _activeIpcAuthKey;
    private HotReloadWatcherService? _activeHotReloadService;
    private TelemetryRingConsumerService? _activeTelemetryConsumer;
    private CancellationTokenSource? _activePostBootAuditCts;

    private bool _isLoadingSettings;
    private bool _isApplyingPreset;
    private bool IsSuppressingAutoWrite => _isLoadingSettings || _isApplyingPreset;
    public int ConfigWriteInvocationCountForTesting { get; private set; }
    private bool _requiresNativeD3D9Fallback;
    private bool _isSyncingEndpointPair;
    private bool _isRestoringInitialViewSelection;
    private long _telemetrySelectionGeneration;
    private long _masterServerScheduleGeneration;
    private readonly List<ComboBoxItem> _registeredMasterServerItems = [];

    public Func<int, int, Task<bool>> LocalMasterServerPortProbe { get; set; } = ProbeLocalMasterServerAsync;
    public Func<FileInfo, string[], Task>? ProcessLauncherOverride { get; set; }

    public void RegisterMasterServerComboItems(IEnumerable<ComboBoxItem> items)
    {
        _registeredMasterServerItems.Clear();
        _registeredMasterServerItems.AddRange(items);
    }

    public void BeginInitialViewRestore() => _isRestoringInitialViewSelection = true;
    public void EndInitialViewRestore() => _isRestoringInitialViewSelection = false;

    public ComboBoxItem GetOrCreateMasterServerComboItem(string selectionText)
    {
        ComboBoxItem? matched = _registeredMasterServerItems.FirstOrDefault(item =>
            string.Equals(item.Content?.ToString()?.Trim(), selectionText.Trim(), StringComparison.OrdinalIgnoreCase));
        if (matched is not null)
            return matched;

        return new ComboBoxItem { Content = selectionText };
    }

    public string? TryGetMasterServerComboText()
    {
        if (MasterServerAddress is { } item)
        {
            return item.CheckAccess() ? item.Content?.ToString() : LoadedMasterServerAddress;
        }

        return null;
    }

    private void EnsureLiveOnlinePlayerAtLeastOne()
    {
        if (LiveOnlinePlayers != 0)
            return;

        if (Application.Current is not null && !Avalonia.Threading.Dispatcher.UIThread.CheckAccess())
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                if (LiveOnlinePlayers == 0)
                    LiveOnlinePlayers = 1;
            });
            return;
        }

        LiveOnlinePlayers = 1;
    }

    [ObservableProperty] public partial ComboBoxItem? MasterServerAddress { get; set; }

    [ObservableProperty] public partial string? LoadedMasterServerAddress { get; set; }

    [ObservableProperty] public partial bool HasActiveCliMasterServerOverride { get; set; } = false;

    [ObservableProperty] public partial string? CustomMasterServerAddress { get; set; }

    [ObservableProperty] public partial bool CanShowCustomMasterServerAddressField { get; set; } = false;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanLaunchGameClient))]
    [NotifyPropertyChangedFor(nameof(CanCheckForCdnUpdates))]
    public partial bool MasterServerAddressIsValid { get; set; } = true;

    public ObservableCollection<AddressSelectItem> AvailableCDNOptions { get; } = [];

    [ObservableProperty]
    public partial AddressSelectItem? SelectedCDNAddressItem { get; set; }

    [ObservableProperty]
    public partial string? CustomCDNAddress { get; set; }

    [ObservableProperty]
    public partial bool CanShowCustomCDNAddressField { get; set; } = false;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanLaunchGameClient))]
    public partial bool CDNAddressIsValid { get; set; } = true;

    public bool CDNInputIsEnabled => MasterServerInputIsEnabled;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanLaunchMapEditor))]
    [NotifyPropertyChangedFor(nameof(CanLaunchGameClient))]
    [NotifyPropertyChangedFor(nameof(CanCheckForCdnUpdates))]
    public partial bool LaunchIsInProgress { get; set; } = false;

    [ObservableProperty] public partial int LiveOnlinePlayers { get; set; } = 0;

    [ObservableProperty] public partial int LiveMatchesCount { get; set; } = 0;

    [ObservableProperty] public partial string ServerStatusText { get; set; } = "Connecting...";

    [ObservableProperty] public partial string ServerStatusColor { get; set; } = "#fbbf24";

    [ObservableProperty] public partial bool HasTopWinrateTicker { get; set; } = false;

    [ObservableProperty] public partial string LeaderboardTickerText { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanLaunchGameClient))]
    public partial bool EnableCdnSync { get; set; } = true;

    [ObservableProperty] public partial bool EnableRayTracing { get; set; } = false;

    [ObservableProperty]
    public partial ObservableCollection<string> AvailableRayTracingPresets { get; set; } =
        new() { "Competitive (0% RT)", "Balanced RTX (RTAO)", "Ultra (Path-Tracing)" };

    [ObservableProperty] public partial string SelectedRayTracingPreset { get; set; } = "Competitive (0% RT)";

    [ObservableProperty] public partial bool EnableAsyncPipelineCaching { get; set; } = true;

    [ObservableProperty] public partial bool EnableTextureClarityBoost { get; set; } = true;

    public ObservableCollection<MonitorDisplayInfo> AvailableMonitors { get; } = [];

    [ObservableProperty] public partial MonitorDisplayInfo? SelectedMonitor { get; set; }

    [ObservableProperty] public partial bool IsManualRefreshRateExpanded { get; set; } = false;

    [ObservableProperty] public partial bool ManualRefreshRateOverrideEnabled { get; set; } = false;

    [ObservableProperty] public partial string ManualMaxFpsText { get; set; } = "";

    [ObservableProperty] public partial string EffectiveRefreshRateSummary { get; set; } = "Auto (Detected)";

    [ObservableProperty]
    public partial ObservableCollection<string> AvailableRefreshRates { get; set; } =
        new() { "60 Hz", "75 Hz", "120 Hz", "144 Hz", "165 Hz", "240 Hz", "360 Hz", "500 Hz", "Uncapped (Max FPS)" };

    [ObservableProperty] public partial string SelectedRefreshRate { get; set; } = "144 Hz";

    [ObservableProperty]
    public partial ObservableCollection<string> AvailableResolutions { get; set; } =
        new() { "1920x1080 (1080p FHD)", "2560x1440 (1440p QHD)", "3840x2160 (4K UHD)", "1280x720 (720p HD)" };

    [ObservableProperty] public partial string SelectedResolution { get; set; } = "1920x1080 (1080p FHD)";

    [ObservableProperty]
    public partial ObservableCollection<string> AvailableDisplayModes { get; set; } =
        new() { "Borderless Windowed", "Windowed", "Exclusive Fullscreen" };

    [ObservableProperty] public partial string SelectedDisplayMode { get; set; } = "Borderless Windowed";

    [ObservableProperty]
    public partial ObservableCollection<string> AvailableShadowFidelities { get; set; } =
        new() { "Ultra (4096)", "High (2048)", "Medium (1024)" };

    [ObservableProperty] public partial string SelectedShadowFidelity { get; set; } = "High (2048)";

    [ObservableProperty]
    public partial ObservableCollection<string> AvailableVulkanHudModes { get; set; } =
        new() { "Off (Clean Screen)", "Minimal FPS", "Pro Diagnostics" };

    [ObservableProperty] public partial string SelectedVulkanHudMode { get; set; } = "Off (Clean Screen)";

    [ObservableProperty] public partial bool EnableNvidiaReflex { get; set; } = false;

    [ObservableProperty] public partial bool EnableFidelityFxRcas { get; set; } = true;

    [ObservableProperty] public partial float FidelityFxRcasSharpness { get; set; } = 0.25f;

    [ObservableProperty] public partial string FidelityFxRcasSharpnessText { get; set; } = "25%";

    [ObservableProperty] public partial bool EnableSpatialAudio { get; set; } = false;

    [ObservableProperty] public partial bool AllowThirdPartyVulkanOverlays { get; set; } = false;

    [ObservableProperty]
    public partial ObservableCollection<string> AvailableSpatialHrtfProfiles { get; set; } =
        new() { "Built-In 48000hz", "Competitive Pinpoint 48kHz", "Cinematic Wide Stage 48kHz" };

    [ObservableProperty] public partial string SelectedSpatialHrtfProfile { get; set; } = "Built-In 48000hz";

    [ObservableProperty] public partial bool EnableOcclusionDamping { get; set; } = true;

    [ObservableProperty]
    public partial ObservableCollection<string> AvailableSpatialChannels { get; set; } =
        new() { "64 Channels (Fast / Esports Focus)", "128 Channels (Studio Balanced)", "256 Channels (Ultra / Polyphony)" };

    [ObservableProperty] public partial string SelectedSpatialChannels { get; set; } = "128 Channels (Studio Balanced)";

    [ObservableProperty] public partial bool EnableQuicHttp3 { get; set; } = true;

    [ObservableProperty] public partial string ShaderCacheStatusText { get; set; } = "Shader Cache: 320 Shaders (2.0 MB)";

    [ObservableProperty] public partial bool EnableDirectFlip { get; set; } = true;

    [ObservableProperty]
    public partial ObservableCollection<string> AvailablePresentationModes { get; set; } =
        new() { "DirectFlip (FLIP_DISCARD)", "DXVK Mailbox (Low-Latency)", "Legacy Blit (DWM Composited)" };

    [ObservableProperty] public partial string SelectedPresentationMode { get; set; } = "DirectFlip (FLIP_DISCARD)";

    [ObservableProperty]
    public partial ObservableCollection<string> AvailableSwapchainBackends { get; set; } =
        IsExperimentalD3D12Enabled() ? new() { "DXVK (Vulkan WSI)", "Compatibility (Native D3D9)", "D3D9On12 (DirectX 12) [Experimental]" } : new() { "DXVK (Vulkan WSI)", "Compatibility (Native D3D9)" };

    public static bool IsExperimentalD3D12Enabled() => string.Equals(Environment.GetEnvironmentVariable("KONGOR_EXPERIMENTAL_D3D12"), "1", StringComparison.OrdinalIgnoreCase);

    [ObservableProperty] public partial string SelectedSwapchainBackend { get; set; } = "DXVK (Vulkan WSI)";

    [ObservableProperty]
    public partial ObservableCollection<string> AvailableNetworkProfiles { get; set; } =
        new() { "Competitive 128-Tick (Esports / Fiber)", "Optimized 60-Tick (Broadband / Wi-Fi)" };

    [ObservableProperty] public partial string SelectedNetworkProfile { get; set; } = "Competitive 128-Tick (Esports / Fiber)";

    [ObservableProperty] public partial string RecommendedSettingsTooltip { get; set; } = "Auto-detect PC hardware and apply optimal graphics & FPS configuration";

    [ObservableProperty] public partial string DetectedHardwareSummary { get; set; } = string.Empty;

    [ObservableProperty] public partial string ActiveGraphicsPresetKey { get; set; } = "Custom";
    [ObservableProperty] public partial string ActiveGraphicsPresetBadgeText { get; set; } = "CUSTOM";
    [ObservableProperty] public partial string ActiveGraphicsPresetBadgeBackground { get; set; } = "#1E293B";
    [ObservableProperty] public partial string ActiveGraphicsPresetBadgeBorder { get; set; } = "#475569";
    [ObservableProperty] public partial string ActiveGraphicsPresetBadgeForeground { get; set; } = "#CBD5E1";
    [ObservableProperty] public partial bool IsRecommendedPresetActive { get; set; } = false;
    [ObservableProperty] public partial bool IsBestFpsPresetActive { get; set; } = false;
    [ObservableProperty] public partial bool IsBestGraphicsPresetActive { get; set; } = false;
    [ObservableProperty] public partial string ActiveGraphicsPipelineSummary { get; set; } = "Shadows: 2048 (High) • AA: Off • Refl: 512 • HUD: Off";
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasLastPresetChangeSummary))]
    public partial string LastPresetChangeSummary { get; set; } = string.Empty;
    [ObservableProperty] public partial string LastPresetChangeForeground { get; set; } = "#34D399";
    public bool HasLastPresetChangeSummary => !string.IsNullOrWhiteSpace(LastPresetChangeSummary);

    // Phase-1 Vendor-Neutral Display & VRR Readiness Pill
    [ObservableProperty] public partial string VrrReadinessPillText { get; set; } = "Checking display VRR readiness...";
    [ObservableProperty] public partial string VrrReadinessTooltip { get; set; } = "Inspects monitor EDID Adaptive-Sync/VRR range, desktop refresh rate alignment, DXGI tearing support, and display mode.";
    [ObservableProperty] public partial string VrrReadinessBackground { get; set; } = "#101726";
    [ObservableProperty] public partial string VrrReadinessBorder { get; set; } = "#26324A";
    [ObservableProperty] public partial string VrrReadinessForeground { get; set; } = "#94A3B8";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSuggestVrrLowLatencyCap))]
    [NotifyPropertyChangedFor(nameof(ApplyVrrLowLatencyCapButtonText))]
    public partial int RecommendedVrrCapFps { get; set; } = 0;

    public bool CanSuggestVrrLowLatencyCap => RecommendedVrrCapFps >= 57;
    public string ApplyVrrLowLatencyCapButtonText => $"⚡ Apply VRR Low-Latency Cap ({RecommendedVrrCapFps} FPS)";

    public bool MasterServerInputIsEnabled => UpdateCheckIsIdle && UpdateIsInstalling is false && SynchronisationIsIdle;

    public bool CanLaunchMapEditor => UpdateCheckIsIdle && UpdateIsInstalling is false && SynchronisationIsIdle &&
                                      LaunchIsInProgress is false;

    public bool CanLaunchGameClient => UpdateCheckIsIdle && UpdateIsInstalling is false &&
                                       MasterServerAddressIsValid && (!EnableCdnSync || CDNAddressIsValid) &&
                                       SynchronisationIsIdle && LaunchIsInProgress is false;

    public string LaunchMapEditorButtonText => "Open Map Editor";

    public string LaunchGameClientButtonText => "Play Project KONGOR";

    public static bool IsOfflineModeSelection(string? selection)
    {
        if (string.IsNullOrWhiteSpace(selection))
            return false;

        string trimmed = selection.Trim();
        return trimmed.Equals(LauncherSettingsModel.OfflineModeSelection, StringComparison.OrdinalIgnoreCase) ||
               trimmed.StartsWith("Offline Mode", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsLocalOfflineAutoSelection(string? selection)
    {
        if (string.IsNullOrWhiteSpace(selection))
            return false;

        string trimmed = selection.Trim();
        return trimmed.Equals(LauncherSettingsModel.DefaultLocalOfflineAutoMasterServer, StringComparison.OrdinalIgnoreCase) ||
               trimmed.Contains("Offline Auto", StringComparison.OrdinalIgnoreCase);
    }

    public static async Task<bool> ProbeLocalMasterServerAsync(int port = 55555, int timeoutMs = 250)
    {
        try
        {
            using TcpClient tcpClient = new();
            using CancellationTokenSource cts = new(Math.Max(50, timeoutMs));
            await tcpClient.ConnectAsync(IPAddress.Loopback, port, cts.Token).ConfigureAwait(false);
            return tcpClient.Connected;
        }
        catch
        {
            return false;
        }
    }

    public static string ResolveSanitizedOfflineNickname(string? gameDir = null)
        => P2pConnectionService.ResolveDefaultNickname(gameDir);

    public static bool ShouldEnableWindowsHighDpi(string? disableEnv = null)
    {
        string env = disableEnv ?? Environment.GetEnvironmentVariable("KONGOR_DISABLE_HIGHDPI") ?? string.Empty;
        return !env.Equals("1", StringComparison.OrdinalIgnoreCase) &&
               !env.Equals("true", StringComparison.OrdinalIgnoreCase) &&
               !env.Equals("y", StringComparison.OrdinalIgnoreCase);
    }

    public static void ApplyWindowsHighDpiEnvironment(
        ProcessStartInfo startInfo,
        Action<string, string?>? setProcessEnv = null,
        Action<string>? log = null,
        string? disableEnvOverride = null)
    {
        if (ShouldEnableWindowsHighDpi(disableEnvOverride))
        {
            startInfo.EnvironmentVariables["__COMPAT_LAYER"] = "HighDpiAware";
            setProcessEnv?.Invoke("__COMPAT_LAYER", "HighDpiAware");
            log?.Invoke("[WILLOWMAKER] Enabled Windows HighDpiAware compatibility layer for client launch.");
        }
    }

    public void RefreshTelemetryPollingForSelection()
    {
        long telemetryGen = Interlocked.Increment(ref _telemetrySelectionGeneration);
        string rawSelection = TryGetMasterServerComboText()
            ?? LoadedMasterServerAddress
            ?? LauncherSettingsModel.DefaultLocalOfflineAutoMasterServer;

        if (IsOfflineModeSelection(rawSelection))
        {
            _liveTelemetryService?.StopPolling();
            ServerStatusText = "Offline Hub";
            ServerStatusColor = "#38bdf8";
            MasterServerProbe.SetReadyStatus("Offline Mode Ready (Bots, Practice & P2P)");
            return;
        }

        if (IsLocalOfflineAutoSelection(rawSelection))
        {
            var callerSyncCtx = System.Threading.SynchronizationContext.Current;
            _ = Task.Run(async () =>
            {
                bool localListening = await LocalMasterServerPortProbe(55555, 250).ConfigureAwait(false);
                void ApplyAutoStatus()
                {
                    if (LaunchIsInProgress || Volatile.Read(ref _telemetrySelectionGeneration) != telemetryGen)
                    {
                        Log(LogCategory.General, $"[TelemetryProbe] Discarded stale Local/Offline Auto probe result (gen={telemetryGen}).");
                        return;
                    }

                    if (localListening)
                    {
                        MasterServerProbe.Start(AddressValidation.NormaliseCDNURL("127.0.0.1:55555"), TimeSpan.Zero);
                        _liveTelemetryService?.StartPolling("127.0.0.1:55555");
                    }
                    else
                    {
                        _liveTelemetryService?.StopPolling();
                        ServerStatusText = "Offline Hub Ready";
                        ServerStatusColor = "#38bdf8";
                        MasterServerProbe.SetReadyStatus("Offline Hub Ready (Local Server Not Running — Auto-Offline Active)");
                    }
                }

                if (callerSyncCtx is not null && System.Threading.SynchronizationContext.Current != callerSyncCtx)
                    callerSyncCtx.Post(_ => ApplyAutoStatus(), null);
                else if (Application.Current is not null && !Avalonia.Threading.Dispatcher.UIThread.CheckAccess())
                    Avalonia.Threading.Dispatcher.UIThread.Post(ApplyAutoStatus);
                else
                    ApplyAutoStatus();
            });
            return;
        }

        string address = BuildMasterServerAddress();
        _liveTelemetryService?.StartPolling(address);
    }

    partial void OnMasterServerAddressChanged(ComboBoxItem? oldValue, ComboBoxItem? newValue)
    {
        if (newValue is not null)
        {
            if (!_isRestoringInitialViewSelection)
            {
                HasActiveCliMasterServerOverride = false;
            }

            string? content = newValue.Content?.ToString();
            if (!string.IsNullOrWhiteSpace(content))
            {
                LoadedMasterServerAddress = content.Trim();
            }

            CanShowCustomMasterServerAddressField =
                content?.Contains("CUSTOM", StringComparison.OrdinalIgnoreCase) ?? false;

            if (CanShowCustomMasterServerAddressField is false)
            {
                MasterServerAddressIsValid = true;
                LogLaunchParameters();
                ScheduleMasterServerProbe(content, ProbeDebounceDelay);
            }
            else
            {
                ValidateAndProbeCustomMasterServerAddress();
            }

            if (!_isSyncingEndpointPair)
            {
                _isSyncingEndpointPair = true;
                try
                {
                    PopulateCDNOptions(content, forcePairedSelection: !_isRestoringInitialViewSelection);
                }
                finally
                {
                    _isSyncingEndpointPair = false;
                }
            }

            OnPropertyChanged(nameof(LaunchGameClientButtonText));
            OnPropertyChanged(nameof(CanCheckForCdnUpdates));

            if (MasterServerAddressIsValid)
            {
                RefreshTelemetryPollingForSelection();
                if (!_isRestoringInitialViewSelection)
                {
                    SaveLauncherSettings();
                }
            }
        }
    }

    partial void OnCustomMasterServerAddressChanged(string? oldValue, string? newValue)
    {
        if (CanShowCustomMasterServerAddressField is false)
            return;

        if (!_isRestoringInitialViewSelection)
        {
            HasActiveCliMasterServerOverride = false;
        }

        ValidateAndProbeCustomMasterServerAddress();
        if (!_isSyncingEndpointPair)
        {
            _isSyncingEndpointPair = true;
            try
            {
                PopulateCDNOptions(newValue, forcePairedSelection: !_isRestoringInitialViewSelection);
            }
            finally
            {
                _isSyncingEndpointPair = false;
            }
        }
        OnPropertyChanged(nameof(LaunchGameClientButtonText));
        OnPropertyChanged(nameof(CanCheckForCdnUpdates));

        if (MasterServerAddressIsValid)
        {
            Interlocked.Increment(ref _telemetrySelectionGeneration);
            string address = BuildMasterServerAddress();
            _liveTelemetryService?.StartPolling(address);
            if (!_isRestoringInitialViewSelection)
            {
                SaveLauncherSettings();
            }
        }
    }

    private void ValidateAndProbeCustomMasterServerAddress()
    {
        MasterServerAddressIsValid = AddressValidation.IsValidAddress(CustomMasterServerAddress);
        if (MasterServerAddressIsValid)
        {
            ScheduleMasterServerProbe(CustomMasterServerAddress, ProbeDebounceDelay);
        }
        else
        {
            Interlocked.Increment(ref _masterServerScheduleGeneration);
            MasterServerProbe.ReportInvalidAddress(CustomMasterServerAddress);
        }
    }

    partial void OnSelectedCDNAddressItemChanged(AddressSelectItem? oldValue, AddressSelectItem? newValue)
    {
        if (newValue is null)
            return;

        SynchronisationIsFailed = false;
        SynchronisationStatusMessage = string.Empty;

        if (!_isSyncingEndpointPair && !_isLoadingSettings)
        {
            _isSyncingEndpointPair = true;
            try
            {
                string pairedMs = EndpointPairRegistry.ResolvePairedMasterServerSelection(newValue);
                string currentMs = TryGetMasterServerComboText()?.Trim()
                    ?? LoadedMasterServerAddress
                    ?? string.Empty;

                if (!string.Equals(currentMs, pairedMs, StringComparison.OrdinalIgnoreCase))
                {
                    MasterServerAddress = GetOrCreateMasterServerComboItem(pairedMs);
                    Log(LogCategory.General, $"[EndpointPair] Reverse sync: CDN='{newValue.DisplayText}' -> Master='{pairedMs}'");
                }
            }
            finally
            {
                _isSyncingEndpointPair = false;
            }
        }

        if (newValue.IsCustom)
        {
            CanShowCustomCDNAddressField = true;
            ValidateAndProbeCustomCDNAddress();
        }
        else
        {
            CanShowCustomCDNAddressField = false;
            CustomCDNAddress = null;
            CDNAddressIsValid = true;
            ScheduleCDNProbe(newValue.TargetURL, ProbeDebounceDelay);
        }

        OnPropertyChanged(nameof(CanCheckForCdnUpdates));
        SaveLauncherSettings();
    }

    partial void OnCustomCDNAddressChanged(string? oldValue, string? newValue)
    {
        if (CanShowCustomCDNAddressField is false)
            return;

        ValidateAndProbeCustomCDNAddress();
        SaveLauncherSettings();
    }

    private void ValidateAndProbeCustomCDNAddress()
    {
        bool hasAddress = !string.IsNullOrWhiteSpace(CustomCDNAddress);
        CDNAddressIsValid = !EnableCdnSync || (hasAddress && AddressValidation.IsValidAddress(CustomCDNAddress));
        if (hasAddress)
        {
            if (AddressValidation.IsValidAddress(CustomCDNAddress))
                ScheduleCDNProbe(CustomCDNAddress, ProbeDebounceDelay);
            else
                CDNProbe.ReportInvalidAddress(CustomCDNAddress);
        }
        else
        {
            CDNProbe.Reset();
        }
    }

    public void PopulateCDNOptions(string? masterServerAddress, bool forcePairedSelection = true)
    {
        string effectiveMaster = !string.IsNullOrWhiteSpace(CustomMasterServerAddress) && CanShowCustomMasterServerAddressField
            ? CustomMasterServerAddress
            : (masterServerAddress ?? string.Empty);

        List<AddressSelectItem> options = EndpointPairRegistry.BuildAvailableCdnOptions(
            effectiveMaster,
            CustomMasterServerAddress,
            CanShowCustomMasterServerAddressField);

        AvailableCDNOptions.Clear();
        foreach (AddressSelectItem option in options)
            AvailableCDNOptions.Add(option);

        if (forcePairedSelection)
        {
            SelectedCDNAddressItem = EndpointPairRegistry.ResolvePairedCdnOption(
                options,
                effectiveMaster,
                CustomMasterServerAddress,
                CanShowCustomMasterServerAddressField);
            Log(LogCategory.General, $"[EndpointPair] Forward sync: Master='{effectiveMaster}' -> CDN='{SelectedCDNAddressItem?.DisplayText}'");
        }
        else
        {
            if (SelectedCDNAddressItem is not null &&
                options.Any(o => o.DisplayText == SelectedCDNAddressItem.DisplayText) &&
                !EndpointPairRegistry.IsCrossEcosystemMismatch(effectiveMaster, SelectedCDNAddressItem.DisplayText))
            {
                SelectedCDNAddressItem = options.First(o => o.DisplayText == SelectedCDNAddressItem.DisplayText);
            }
            else
            {
                SelectedCDNAddressItem = EndpointPairRegistry.ResolvePairedCdnOption(
                    options,
                    effectiveMaster,
                    CustomMasterServerAddress,
                    CanShowCustomMasterServerAddressField);
            }
        }
    }

    private static TimeSpan ProbeDebounceDelay { get; } = TimeSpan.FromMilliseconds(350);

    private void ScheduleMasterServerProbe(string? address, TimeSpan delay)
    {
        if (LaunchIsInProgress)
            return;

        long scheduleGen = Interlocked.Increment(ref _masterServerScheduleGeneration);

        if (string.IsNullOrWhiteSpace(address))
        {
            MasterServerProbe.Reset();
            return;
        }

        if (IsOfflineModeSelection(address))
        {
            MasterServerProbe.SetReadyStatus("Offline Mode Ready (Bots, Practice & P2P)");
            return;
        }

        if (IsLocalOfflineAutoSelection(address))
        {
            MasterServerProbe.CancelPending();
            SynchronizationContext? callerSyncCtx = SynchronizationContext.Current ?? _uiSyncContext;
            _ = Task.Run(async () =>
            {
                bool localListening = await LocalMasterServerPortProbe(55555, 250).ConfigureAwait(false);
                void ApplyProbeState()
                {
                    if (LaunchIsInProgress || Volatile.Read(ref _masterServerScheduleGeneration) != scheduleGen)
                    {
                        Log(LogCategory.General, $"[MasterServerProbe] Discarded stale Local/Offline Auto port probe result (gen={scheduleGen}).");
                        return;
                    }

                    if (localListening)
                        MasterServerProbe.Start(AddressValidation.NormaliseCDNURL("127.0.0.1:55555"), delay);
                    else
                        MasterServerProbe.SetReadyStatus("Offline Hub Ready (Local Server Not Running — Auto-Offline Active)");
                }

                RunOnUiThread(ApplyProbeState, callerSyncCtx);
            });
            return;
        }

        string probeUrl = AddressValidation.NormaliseCDNURL(address);
        MasterServerProbe.Start(probeUrl, delay);
    }

    private void ScheduleCDNProbe(string? address, TimeSpan delay)
    {
        if (LaunchIsInProgress)
            return;

        string? targetUrl = address ?? SelectedCDNAddressItem?.TargetURL;
        string? displayText = SelectedCDNAddressItem?.DisplayText;
        bool hasExplicitCdnTarget = !string.IsNullOrWhiteSpace(targetUrl) || SelectedCDNAddressItem is not null;

        string currentMs = TryGetMasterServerComboText() ?? LoadedMasterServerAddress ?? string.Empty;

        bool isOfflineSelection = string.Equals(targetUrl, "offline", StringComparison.OrdinalIgnoreCase) ||
            (displayText?.Contains("Offline Mode", StringComparison.OrdinalIgnoreCase) ?? false) ||
            (!hasExplicitCdnTarget && IsOfflineModeSelection(currentMs));

        if (isOfflineSelection)
        {
            CDNProbe.SetReadyStatus("Offline Mode — Local Files");
            ResetCdnUpdateStateToOffline();
            return;
        }

        bool isLocalAuto = (displayText?.Contains("Offline Auto", StringComparison.OrdinalIgnoreCase) ?? false) ||
            (!hasExplicitCdnTarget && IsLocalOfflineAutoSelection(currentMs));

        if (isLocalAuto)
        {
            long probeGen = CDNProbe.BeginPendingScope();
            SynchronizationContext? callerSyncCtx = SynchronizationContext.Current ?? _uiSyncContext;
            _ = Task.Run(async () =>
            {
                bool localListening = await LocalMasterServerPortProbe(55555, 250).ConfigureAwait(false);
                void ApplyCdnAutoStatus()
                {
                    if (LaunchIsInProgress || !CDNProbe.IsCurrentGeneration(probeGen))
                    {
                        Log(LogCategory.Synchronise, $"[CDNProbe] Discarded stale Local/Offline Auto port probe result (gen={probeGen}).");
                        return;
                    }

                    if (localListening)
                    {
                        CDNProbe.Start(AddressValidation.NormaliseCDNURL("127.0.0.1:55555/cdn"), delay);
                    }
                    else
                    {
                        CDNProbe.SetReadyStatus("Auto-Offline Active — Local Files");
                        ResetCdnUpdateStateToOffline();
                    }
                }

                RunOnUiThread(ApplyCdnAutoStatus, callerSyncCtx);
            });
            return;
        }

        if (string.IsNullOrWhiteSpace(address))
        {
            CDNProbe.Reset();
            ResetCdnUpdateStateToOffline();
            return;
        }

        string probeUrl = AddressValidation.NormaliseCDNURL(address);
        CDNProbe.Start(probeUrl, delay);
    }

    public string BuildCDNAddress()
    {
        string? target = SelectedCDNAddressItem?.IsCustom is true
            ? CustomCDNAddress
            : SelectedCDNAddressItem?.TargetURL;

        if (string.Equals(target, "offline", StringComparison.OrdinalIgnoreCase))
            return string.Empty;

        if (string.IsNullOrWhiteSpace(target))
        {
            target = EndpointPairRegistry.ResolveDefaultCdnUrlForMasterServer(
                TryGetMasterServerComboText() ?? LoadedMasterServerAddress,
                BuildMasterServerAddress());
        }

        return AddressValidation.NormaliseCDNURL(target);
    }

    private void LogLaunchParameters()
    {
        string rawSelection = TryGetMasterServerComboText()
            ?? LoadedMasterServerAddress
            ?? LauncherSettingsModel.DefaultLocalOfflineAutoMasterServer;

        if (IsOfflineModeSelection(rawSelection))
        {
            Log(LogCategory.Parameters, "[OfflineHub] +Set ui_suppressAutoLogin true +Set kongor_offline_hub true +Set cg_newMainInterface true");
            return;
        }

        if (IsLocalOfflineAutoSelection(rawSelection))
        {
            Log(LogCategory.Parameters, "[Local / Offline Auto] Probes 127.0.0.1:55555 (<=250ms); connects locally if running or boots Offline Main Menu Hub automatically.");
            return;
        }

        string address = BuildMasterServerAddress();
        Log(LogCategory.Parameters, $"-masterserver {address} -webserver {address} -messageserver {address}");
    }

    [RelayCommand]
    private Task LaunchGameClient()
    {
        return LaunchGameClientCore(skipSynchronisation: true);
    }

    [RelayCommand]
    private async Task LaunchGameClientWithoutSynchronisation()
    {
        if (await ConfirmLaunchWithoutSynchronisation() is false)
            return;

        await LaunchGameClientCore(true);
    }

    public async Task LaunchGameClientCore(
        bool skipSynchronisation,
        IEnumerable<string>? extraArguments = null,
        bool isSafeMode = false)
    {
        if (LaunchIsInProgress)
        {
            Log(LogCategory.Executable, "Launch is already in progress, ignoring duplicate launch request.");
            return;
        }

        LaunchIsInProgress = true;
        CDNProbe.CancelPending();
        MasterServerProbe.CancelPending();
        SaveLauncherSettings();

        string? parkedResources999Path = null;
        string? activeResources999Path = null;
        bool didParkResources999 = false;

        try
        {
            Stopwatch launchTimer = Stopwatch.StartNew();
            Log(LogCategory.Executable, isSafeMode ? "Game Launch Initiated (Safe Mode)" : "Game Launch Initiated");

            if (TryResolveGameExecutable(out FileInfo? executable) is false)
                return;

            string gameDir = executable.DirectoryName ?? Environment.CurrentDirectory;

            if (isSafeMode)
            {
                activeResources999Path = Path.Combine(gameDir, "game", "resources999.s2z");
                parkedResources999Path = Path.Combine(gameDir, "game", "resources999.s2z.safemode_parked");
                if (File.Exists(activeResources999Path))
                {
                    try
                    {
                        File.Move(activeResources999Path, parkedResources999Path, overwrite: true);
                        didParkResources999 = true;
                        Log(LogCategory.Executable,
                            "[SAFE_MODE] Temporarily parked user mod archive game/resources999.s2z -> game/resources999.s2z.safemode_parked.");
                    }
                    catch (Exception parkEx)
                    {
                        Log(LogCategory.Executable, $"[SAFE_MODE] Notice: could not park resources999.s2z: {parkEx.Message}");
                    }
                }
            }
            LaunchConfigWriterService.EnsurePosixClientRootPrepared(gameDir, Log);
            LaunchConfigWriterService.EnsureMacOsBundlePrepared(gameDir, Log);
            EnsureUserDirectoryInitialized(gameDir);
            Log(LogCategory.Executable,
                $"[PORTABLE_STORAGE] Initialized self-contained user directory at '{Path.Combine(gameDir, "userdata")}'.");

            PlaytestPolicyDto playtestPolicy = PlaytestPolicyService.Resolve(
                gameDir,
                message => Log(LogCategory.Executable, message));
            RuntimePrerequisiteResult prereqResult = PlaytestPolicyService.CheckRuntimePrerequisites(
                gameDir,
                message => Log(LogCategory.Executable, message));
            _requiresNativeD3D9Fallback = prereqResult.RequiresNativeD3D9Fallback;

            // Perform automated Vulkan 1.3 / DXVK modernization, 1000 FPS binary patches, and driver optimization
            if (playtestPolicy.BinariesPrePatched)
            {
                Log(LogCategory.Executable,
                    "[PlaytestPolicy] Binaries marked pre-patched in playtest_policy.json; skipping runtime PE/Mach-O/ELF binary modification.");
            }
            else
            {
                RayTracingPreset preset = RayTracingPreset.Competitive;
                if (EnableRayTracing)
                    preset = SelectedRayTracingPreset.StartsWith("Ultra", StringComparison.OrdinalIgnoreCase)
                        ? RayTracingPreset.Ultra
                        : RayTracingPreset.Balanced;

                await VulkanDriverUpgradeService.UpgradeToVulkanAsync(gameDir, preset, message => Log(LogCategory.Executable, message));
            }
            RefreshGraphicsDriverStatus(gameDir);

            string address = BuildMasterServerAddress();

            WriteCustomConfiguration(gameDir, forceConflictScan: true);

            if (ModManager != null)
            {
                Log(LogCategory.Executable, $"[MODS] Pre-launch mod synchronization (Active count: {ModManager.ActiveModsCount})...");
                await ModManager.EnsureSynchronizedForLaunchAsync();
            }

            Log(LogCategory.Executable,
                $"[LAUNCH_PERF] Pre-launch preparation completed in {launchTimer.ElapsedMilliseconds}ms.");

            bool hasMasterServerOverride = extraArguments?.Any(a => a.StartsWith("-masterserver", StringComparison.OrdinalIgnoreCase)) == true;
            bool isP2pLaunch = extraArguments?.Any(a => a.Contains("p2p_", StringComparison.OrdinalIgnoreCase)) == true;
            string activeSelection = TryGetMasterServerComboText()
                ?? LoadedMasterServerAddress
                ?? LauncherSettingsModel.DefaultLocalOfflineAutoMasterServer;

            bool launchOfflineHub = false;
            if (!hasMasterServerOverride && !isP2pLaunch)
            {
                if (IsOfflineModeSelection(activeSelection))
                {
                    launchOfflineHub = true;
                    Log(LogCategory.Executable, "[OfflineHub] Offline Mode selected; booting directly into Offline Main Menu Hub without master-server auto-login.");
                }
                else if (IsLocalOfflineAutoSelection(activeSelection))
                {
                    bool localListening = await LocalMasterServerPortProbe(55555, 250);
                    launchOfflineHub = !localListening;
                    Log(LogCategory.Executable, localListening
                        ? "[AutoOffline] Local Master Server detected on 127.0.0.1:55555; launching in local online mode."
                        : "[AutoOffline] No local Master Server listening on 127.0.0.1:55555; launching directly into Offline Main Menu Hub.");
                }
            }

            List<string> arguments = BuildClientLaunchArgumentList(gameDir, address, playtestPolicy, extraArguments, launchOfflineHub, isSafeMode: isSafeMode);

            // Initialize Named Pipe IPC bridge when launching game
            try
            {
                Log(LogCategory.Executable, "Initializing Named Pipe IPC Service (\\\\.\\pipe\\willowmaker-ipc)...");
                string ipcAuthKey = Convert.ToHexStringLower(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
                _activeIpcAuthKey = ipcAuthKey;
                if (_activeIpcService is { IsRunning: true } existingIpc)
                {
                    existingIpc.AuthKey = ipcAuthKey;
                    BindCockpitIpcCallbacks(existingIpc);
                    Log(LogCategory.Executable, $"Reusing persistent Named Pipe IPC Service on '{existingIpc.PipeName}' with fresh session AuthKey.");
                }
                else
                {
                    if (_activeIpcService != null) await _activeIpcService.DisposeAsync();
                    _boundStreamEventsService = null;
                    _activeIpcService = new WillowmakerIpcService(
                        WillowmakerIpcService.DefaultPipeName,
                        authKey: ipcAuthKey,
                        sandboxMode: NEXUS.Contracts.Ipc.IpcSandboxMode.Enforced,
                        logAction: msg => Log(LogCategory.Executable, msg));
                    BindCockpitIpcCallbacks(_activeIpcService);
                    await _activeIpcService.StartServerAsync();
                    Log(LogCategory.Executable, "Named Pipe IPC Service Started Successfully!");
                }
            }
            catch (Exception exception)
            {
                Log(LogCategory.Executable, $"Named Pipe IPC Service Exception: {exception.Message}");
            }

            if (isP2pLaunch)
            {
                string? p2pNick = extraArguments?
                    .FirstOrDefault(a => a.StartsWith("+Set p2p_nickname", StringComparison.OrdinalIgnoreCase))?
                    .Replace("+Set p2p_nickname", string.Empty)
                    .Trim(' ', '"', '\'');

                if (string.IsNullOrWhiteSpace(p2pNick))
                {
                    p2pNick = P2pConnectionService.ResolveDefaultNickname(gameDir);
                }

                P2pConnectionService.EnsureClientDefaultNameConfigured(gameDir, p2pNick);
                Log(LogCategory.Executable, $"[P2P Configuration] Configured client_default_name stringtables for player '{p2pNick}' in '{gameDir}'.");
            }
            else if (launchOfflineHub && !PureCompatibilityMode)
            {
                string offlineNick = ResolveSanitizedOfflineNickname(gameDir);
                bool isDevRepoWorkspace = File.Exists(Path.Combine(gameDir, "..", "..", "ProjectKongor.slnx")) ||
                                          File.Exists(Path.Combine(gameDir, "..", "..", "ProjectKongor.sln"));
                if (!isDevRepoWorkspace || !string.Equals(offlineNick, "Player", StringComparison.Ordinal))
                {
                    P2pConnectionService.EnsureClientDefaultNameConfigured(gameDir, offlineNick);
                }
                Log(LogCategory.Executable, $"[OfflineHub] Configured offline player nickname '{offlineNick}'.");
            }

            if (!hasMasterServerOverride)
            try
            {
                string masterServerUrl = (launchOfflineHub || isP2pLaunch)
                    ? string.Empty
                    : address.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                      address.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
                        ? address
                        : address.Contains("localhost", StringComparison.OrdinalIgnoreCase) ||
                          address.Contains("127.0.0.1", StringComparison.OrdinalIgnoreCase)
                            ? $"http://{address}"
                            : $"https://{address}";

                Log(LogCategory.Executable, string.IsNullOrEmpty(masterServerUrl)
                    ? "Initializing local-only Discord RPC bridge for Offline Hub / P2P session..."
                    : $"Initializing Discord RPC bridge for master server '{masterServerUrl}'...");
                _activeDiscordPresence?.Dispose();
                _activeDiscordPresence = new DiscordPresenceService();
                _activeDiscordPresence.SetCustomDiscordAppId(_cockpitDiscordAppId);
                _activeDiscordPresence.SetStreamerPrivacyMode(DiscordStreamerPrivacyMode);
                WireDiscordPresenceEvents(_activeDiscordPresence);
                await _activeDiscordPresence.InitializeAsync(masterServerUrl);
                Log(LogCategory.Executable, "Discord RPC Bridge Initialized Successfully!");
            }
            catch (Exception exception)
            {
                Log(LogCategory.Executable, $"Discord RPC Bridge Exception: {exception.Message}");
            }

            await LaunchProcessAndExit(executable, arguments.ToArray(), isSafeMode);
        }

        finally
        {
            if (didParkResources999 && !string.IsNullOrEmpty(parkedResources999Path) && !string.IsNullOrEmpty(activeResources999Path) && File.Exists(parkedResources999Path))
            {
                try
                {
                    File.Move(parkedResources999Path, activeResources999Path, overwrite: true);
                    Log(LogCategory.Executable,
                        "[SAFE_MODE] Restored user mod archive game/resources999.s2z.safemode_parked -> game/resources999.s2z.");
                }
                catch (Exception restoreEx)
                {
                    Log(LogCategory.Executable, $"[SAFE_MODE] Notice: could not restore resources999.s2z: {restoreEx.Message}");
                }
            }

            RunOnUiThread(() => LaunchIsInProgress = false);
        }
    }

    public List<string> BuildClientLaunchArgumentList(
        string gameDir,
        string address,
        PlaytestPolicyDto playtestPolicy,
        IEnumerable<string>? extraArguments = null,
        bool? launchOfflineHubOverride = null,
        bool isSafeMode = false)
    {
        string modParam = ClientModMountSpec.GetCanonicalModString(includeOverride: !PureCompatibilityMode, isSafeMode: isSafeMode);
        if (PureCompatibilityMode)
        {
            Log(LogCategory.Parameters, "[Pure 4.10 Mode] Omitting ';override' tier from -mod for external vanilla master server compatibility.");
        }
        if (isSafeMode)
        {
            Log(LogCategory.Parameters, "[SAFE_MODE] Launching with Safe Mode: omitting 'KONGOR/extensions', keeping 'override' for core entity/UI stability.");
        }
        Log(LogCategory.Parameters, modParam);

        string[]? extraArgArray = extraArguments as string[] ?? extraArguments?.ToArray();
        bool hasMasterServerOverride = extraArgArray?.Any(a => a.StartsWith("-masterserver", StringComparison.OrdinalIgnoreCase)) == true;
        bool isP2pLaunch = extraArgArray?.Any(a => a.Contains("p2p_", StringComparison.OrdinalIgnoreCase)) == true;
        string rawSelection = TryGetMasterServerComboText() ?? LoadedMasterServerAddress ?? string.Empty;
        bool launchOfflineHub = launchOfflineHubOverride
            ?? (!hasMasterServerOverride && !isP2pLaunch && IsOfflineModeSelection(rawSelection));

        List<string> arguments = [];

        if (!hasMasterServerOverride)
        {
            string effectiveAddress = launchOfflineHub ? "127.0.0.1:55555" : address;
            arguments.Add($"-masterserver {effectiveAddress}");
            if (ClientModMountSpec.IsLocalhostEndpoint(effectiveAddress))
            {
                arguments.Add("-chatserver 127.0.0.1:11031");
                Log(LogCategory.Parameters, $"[ChatRouting] Localhost master server detected ('{effectiveAddress}'); routing chatserver to 127.0.0.1:11031.");
            }
            else
            {
                Log(LogCategory.Parameters, $"[ChatRouting] Remote master server ('{effectiveAddress}'); delegating chatserver endpoint discovery to auth response.");
            }
            arguments.Add($"-webserver {effectiveAddress}");
            arguments.Add($"-messageserver {effectiveAddress}");
        }

        var (shadowMapSize, shadowFilterWidth, shadowDrawDistance, foliageDensity, cullGroundSprites, ambientOcclusionSetting, textureCompressionSetting, refreshRateNumber, effectiveMaxFpsNumber, resParam) = ResolveActiveGraphicsSettings();
        string effectiveInterfaceFpsNumber = ResolveEffectiveInterfaceFps(effectiveMaxFpsNumber, refreshRateNumber);
        Log(LogCategory.Parameters,
            $"[Launch][HIGH_HZ] targetHz={refreshRateNumber}, effectiveMaxFps={effectiveMaxFpsNumber}, cg_interfaceFPS={effectiveInterfaceFpsNumber}, shadowMapSize={shadowMapSize}, shadowDrawDistance={shadowDrawDistance}");
        string effectiveVidDriver = LaunchConfigWriterService.ResolveEffectiveVideoDriver(
            gameDir,
            SelectedSwapchainBackend.Contains("D3D9On12", StringComparison.OrdinalIgnoreCase) && IsExperimentalD3D12Enabled());
        var (vidFullscreen, vidBorderless, d3dExclusive) =
            LaunchConfigWriterService.ResolveDisplayModeCvars(SelectedDisplayMode, effectiveVidDriver);
        string optionsExclusive = d3dExclusive;
        string optionsFullscreen = vidFullscreen;

        arguments.AddRange([
            // resources
            modParam,

            // Real-time non-blocking asynchronous logging (Eliminates main-thread disk flush stalls)
            "-writeconsole",
            "-logfile console.log",
            "+Set log_flush false",
            "+Set con_flushLog false",
            "+Set sys_flushLogs false",
            "+Set con_log 1",
            "+Set host_writeConsole false",

            // Named Pipe IPC Protocol
            "-pipe willowmaker-ipc",

            // Performance & FPS Pacing (Uncapped K2 clocks; VSync/presentInterval is omitted from CLI flags so HoN's in-game VSync toggle persists)
            $"+Set host_maxFPS {effectiveMaxFpsNumber}",
            $"+Set host_maximumFPS {effectiveMaxFpsNumber}",
            $"+Set cl_maxFPS {effectiveMaxFpsNumber}",
            $"+Set vid_maxFPS {effectiveMaxFpsNumber}",
            "+Set host_sleep 0",
            "+Set host_sleepMS 0",
            $"+Set vid_refreshRate {refreshRateNumber}",
            $"+Set vid_resolution {resParam}",
            $"+Set vid_display {(!string.IsNullOrWhiteSpace(SelectedMonitor?.NormalizedDeviceToken) ? SelectedMonitor!.NormalizedDeviceToken : "DISPLAY1")}",
            $"+Set options_display {(!string.IsNullOrWhiteSpace(SelectedMonitor?.NormalizedDeviceToken) ? SelectedMonitor!.NormalizedDeviceToken : "DISPLAY1")}",
            "+Set sys_sleepTime 0",
            "+Set d3d_pure true",
            $"+Set cg_interfaceFPS {effectiveInterfaceFpsNumber}",
            "+Set cg_minimapFPS 60",
            "+Set host_dynamicResReload false",

            // K2 Native Video Driver (vid_vulkan_x64.dll or vid_d3d12_x64.dll)
            $"-vid {effectiveVidDriver}",
            $"+Set host_vidDriver {effectiveVidDriver}",
            $"+Set vid_driver {effectiveVidDriver}",

            // Deterministic Graphics Fidelity & Shadow Cvars (Direct Command-Line Priority)
            $"+Set vid_shadowmapSize {shadowMapSize}",
            $"+Set vid_shadowmapFilterWidth {shadowFilterWidth}",
            $"+Set vid_shadowDrawDistance {shadowDrawDistance}",
            $"+Set vid_foliageDensity {foliageDensity}",
            $"+Set vid_cullGroundSprites {cullGroundSprites}",
            $"+Set vid_shaderAmbientOcclusion {ambientOcclusionSetting}",
            $"+Set vid_textureCompression {textureCompressionSetting}",

            // Display & Fullscreen (DirectFlip non-exclusive scanout; NativeGuard enforces exact client-rect cursor lock)
            $"+Set vid_fullscreen {vidFullscreen}",
            $"+Set vid_borderless {vidBorderless}",
            $"+Set d3d_exclusive {d3dExclusive}",
            $"+Set options_exclusive {optionsExclusive}",
            $"+Set options_fullscreen {optionsFullscreen}",

            // Instant Entity Loading & Clean UI
            "+Set con_notify false",
            "+Set cg_heroLoadingChunkDurationMS 20",
            "+Set svr_port 11235",
            "+Set svr_voicePort 11435",
            "+Set sound_disableRecording true",
            "+Set res_unloadHeroResources true",
            "+Set res_unloadMapResources true",
            "+Set cg_disableBackgroundEffects true",
            "+Set cg_precacheResources false",
            "+Set http_connectTimeout 1",
            "+Set http_timeout 5",
            "+Set upd_checkForUpdates false",
            "+Set upd_checkAtStartup false",
            $"+Set chat_connectTimeout {(launchOfflineHub ? "1" : "5")}",
            $"+Set chat_maxReconnectAttempts {(launchOfflineHub ? "0" : "5")}",
            "+Set http_printDebugInfo false",
            "+Set php_printDebugInfo false",
            "+Set cg_enableLoginAnimation false",
            "+Set ui_disable3DBackground false",
            "+Set ui_background 0",
            "+Set con_height 0.85",
            "+Set con_alpha 0.85",

            // UI & Release Stage Policy (preserves Practice Mode, Bot Matches, Local Lobbies, and Ctrl+Shift+D)
            $"+Set releaseStage_dev {(playtestPolicy.UseStableReleaseStageCvars ? "false" : "true")}",
            $"+Set releaseStage_test {(playtestPolicy.UseStableReleaseStageCvars ? "false" : "true")}",
            $"+Set releaseStage_stable {(playtestPolicy.UseStableReleaseStageCvars ? "true" : "false")}",
            $"+Set ui_dev {(playtestPolicy.UseStableReleaseStageCvars ? "false" : "true")}",
            $"+Set ui_dev_button {(playtestPolicy.UseStableReleaseStageCvars ? "false" : "true")}",
            "+Set ui_showQuickStart true",
            "+Set ui_canCreateGame true",
            "+Set cg_24hourClock true",

            // Local-Only Crash Storage & Privacy Hardening (Zero Network Uploads)
            "+Set sys_disableCrashReporter true",
            "+Set sys_keepOldDumpsOnStartup true",
            "+Set sys_dumpOnFatal true",
            "+Set sys_autoSaveDump true",
            "+Set sys_keepDumpAfterUpload true",
            "+Set cef_crash_reporting_enabled false",
            "+Set cloud_autoupload false",
            "+Set host_cloudAutoUpload false",
            "+Set host_autoUploadOnLogout false"
        ]);

        if (!PureCompatibilityMode)
        {
            arguments.Add("+Set cg_newMainInterface true");
        }

        if (launchOfflineHub)
        {
            string offlineNick = ResolveSanitizedOfflineNickname(gameDir);
            arguments.Add("+Set ui_suppressAutoLogin true");
            arguments.Add("+Set kongor_offline_hub true");
            arguments.Add($"+Set login_name {offlineNick}");
            arguments.Add($"+Set p2p_nickname {offlineNick}");
            Log(LogCategory.Parameters, $"[OfflineHub] Added '+Set ui_suppressAutoLogin true +Set kongor_offline_hub true +Set login_name {offlineNick}'.");
        }

        if (!OperatingSystem.IsWindows())
        {
            arguments.Add("+Set net_enableWatermark false");
            Log(LogCategory.Parameters, "[POSIX Networking] Added '+Set net_enableWatermark false' for Linux/macOS P2P and bot-match parity.");
        }

        if (isSafeMode)
        {
            arguments.Add("+Set kongor_safe_mode 1");
            bool graphicsCrash = !string.IsNullOrEmpty(_activeTriageResult?.FaultingModule) &&
                (_activeTriageResult.FaultingModule.Contains("vid_vulkan", StringComparison.OrdinalIgnoreCase) ||
                 _activeTriageResult.FaultingModule.Contains("vulkan", StringComparison.OrdinalIgnoreCase) ||
                 _activeTriageResult.FaultingModule.Contains("d3d9", StringComparison.OrdinalIgnoreCase) ||
                 _activeTriageResult.FaultingModule.Contains("dxvk", StringComparison.OrdinalIgnoreCase));

            if (graphicsCrash || _requiresNativeD3D9Fallback)
            {
                arguments.Add("+Set vid_driver vid_d3d9");
                arguments.Add("+Set host_vidDriver vid_d3d9");
                Log(LogCategory.Parameters, "[SAFE_MODE] Enforced +Set vid_driver vid_d3d9 due to detected graphics crash / fallback.");
            }
        }

        if (extraArgArray != null)
        {
            arguments.AddRange(extraArgArray);
        }

        Log(LogCategory.Parameters, $"[LaunchArgs] Prepared {arguments.Count} K2 client launch arguments (UseStableReleaseStageCvars={playtestPolicy.UseStableReleaseStageCvars}, OfflineHub={launchOfflineHub}).");
        return arguments;
    }

    private async Task<bool> ConfirmLaunchWithoutSynchronisation()
    {
        if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop ||
            desktop.MainWindow is null)
            return false;

        string message = new StringBuilder()
            .Append(
                $"{DeploymentManifest.ApplicationName} will launch Heroes Of Newerth without synchronising content from the Content Delivery Network." +
                " ")
            .Append("This is only intended for development purposes." + " ")
            .Append(
                "Running an out-of-date distribution can have negative consequences, such as not being able to connect to match servers.")
            .AppendLine().AppendLine()
            .Append("Continue?")
            .ToString();

        SynchronisationBypassDialog dialog = new(message);

        bool shouldBypass = await dialog.ShowDialog<bool>(desktop.MainWindow);

        if (shouldBypass is false)
            Log(LogCategory.Synchronise, "SKIP: Launch Without Synchronisation Cancelled By User");

        return shouldBypass;
    }

    [RelayCommand]
    private async Task LaunchMapEditor()
    {
        LaunchIsInProgress = true;
        CDNProbe.CancelPending();
        MasterServerProbe.CancelPending();

        try
        {
            Log(LogCategory.Executable, "Map Editor Launch Initiated");

            if (TryResolveGameExecutable(out FileInfo? executable) is false)
                return;

            string editorDir = executable.DirectoryName ?? Environment.CurrentDirectory;
            LaunchConfigWriterService.EnsurePosixClientRootPrepared(editorDir, Log);
            LaunchConfigWriterService.EnsureMacOsBundlePrepared(editorDir, Log);
            EnsureUserDirectoryInitialized(editorDir);
            Log(LogCategory.Executable,
                $"[PORTABLE_STORAGE] Initialized self-contained user directory at '{Path.Combine(editorDir, "userdata")}'.");
            PlaytestPolicyDto editorPolicy = PlaytestPolicyService.Resolve(
                editorDir,
                message => Log(LogCategory.Executable, message));
            if (!editorPolicy.BinariesPrePatched)
            {
                EnginePatchService.EnsureEnginePatched(editorDir, message => Log(LogCategory.Executable, message));
            }
            else
            {
                Log(LogCategory.Executable, "[PlaytestPolicy] Editor binaries pre-patched; skipping runtime binary patch.");
            }

            string editorModParam = $"{ClientModMountSpec.GetCanonicalModString(includeOverride: !PureCompatibilityMode)};editor";
            Log(LogCategory.Parameters, editorModParam);

            string[] arguments =
            [
                // resources only; the editor has no use for the service endpoints
                editorModParam
            ];

            try
            {
                Log(LogCategory.Executable, "Initializing Discord RPC bridge for K2 World Editor session...");
                _activeDiscordPresence?.Dispose();
                _activeDiscordPresence = new DiscordPresenceService();
                _activeDiscordPresence.SetCustomDiscordAppId(_cockpitDiscordAppId);
                _activeDiscordPresence.SetStreamerPrivacyMode(DiscordStreamerPrivacyMode);
                WireDiscordPresenceEvents(_activeDiscordPresence);
                await _activeDiscordPresence.InitializeAsync(masterServerUrl: null, isMapEditor: true);
                Log(LogCategory.Executable, "Discord RPC Bridge initialized for K2 World Editor.");
            }
            catch (Exception exception)
            {
                Log(LogCategory.Executable, $"Discord RPC Bridge (Map Editor) exception: {exception.Message}");
            }

            await LaunchProcessAndExit(executable, arguments);
        }

        finally
        {
            RunOnUiThread(() => LaunchIsInProgress = false);
        }
    }

    private bool TryResolveGameExecutable([NotNullWhen(true)] out FileInfo? executable) =>
        TryResolveGameExecutable(out executable, Environment.CurrentDirectory, AppContext.BaseDirectory, targetPlatformOverride: null);

    public bool TryResolveGameExecutable(
        [NotNullWhen(true)] out FileInfo? executable,
        string? currentDirOverride,
        string? baseDirOverride,
        string? targetPlatformOverride = null)
    {
        string normalizedPlatform = string.IsNullOrWhiteSpace(targetPlatformOverride)
            ? (OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsLinux() ? "linux" : OperatingSystem.IsMacOS() ? "macos" : "unknown")
            : targetPlatformOverride.Trim().ToLowerInvariant();

        string targetExeName = normalizedPlatform switch
        {
            "windows" or "win-x64" => "hon_x64.exe",
            "linux" or "linux-x64" => "hon-x86_64",
            "macos" or "macos-x64" or "osx" or "osx-universal" => "HoN64",
            _ => DeploymentManifest.HeroesOfNewerthExecutableFileName
        };

        string platformSubdir = normalizedPlatform switch
        {
            "windows" or "win-x64" => "windows-x64",
            "linux" or "linux-x64" => "linux-x64",
            "macos" or "macos-x64" or "osx" or "osx-universal" => "macos-x64",
            _ => string.Empty
        };

        bool isMacTarget = string.Equals(platformSubdir, "macos-x64", StringComparison.Ordinal);
        string currentDir = string.IsNullOrWhiteSpace(currentDirOverride) ? Environment.CurrentDirectory : currentDirOverride;
        string baseDir = string.IsNullOrWhiteSpace(baseDirOverride) ? AppContext.BaseDirectory : baseDirOverride;

        static bool TryCheckCandidate(string candidatePath, out FileInfo? found)
        {
            if (!string.IsNullOrWhiteSpace(candidatePath))
            {
                FileInfo info = new(candidatePath);
                if (info.Exists)
                {
                    found = info;
                    return true;
                }
            }

            found = null;
            return false;
        }

        // 1. Direct check in current directory
        if (TryCheckCandidate(Path.Combine(currentDir, targetExeName), out executable))
        {
            Log(LogCategory.Executable, $@"Resolved Game Executable: ""{executable!.FullName}""");
            return true;
        }

        // 2. Direct check in AppContext.BaseDirectory
        if (TryCheckCandidate(Path.Combine(baseDir, targetExeName), out executable))
        {
            Log(LogCategory.Executable, $@"Resolved Game Executable in BaseDirectory: ""{executable!.FullName}""");
            return true;
        }

        // 2b. macOS .app bundle direct check in current directory / BaseDirectory
        if (isMacTarget)
        {
            string[] macDirectBundles =
            [
                Path.Combine(currentDir, "Heroes of Newerth.app", "Contents", "MacOS", "HoN64"),
                Path.Combine(baseDir, "Heroes of Newerth.app", "Contents", "MacOS", "HoN64")
            ];
            foreach (string macCandidate in macDirectBundles)
            {
                if (TryCheckCandidate(macCandidate, out executable))
                {
                    Log(LogCategory.Executable, $@"Resolved macOS .app Game Executable: ""{executable!.FullName}""");
                    return true;
                }
            }
        }

        // 3. Search for games/client in directory tree
        string? resolvedClientDir = LocationGuard.ResolveGameClientDirectory(currentDir) ??
                                    LocationGuard.ResolveGameClientDirectory(baseDir);
        if (!string.IsNullOrEmpty(resolvedClientDir))
        {
            if (TryCheckCandidate(Path.Combine(resolvedClientDir, targetExeName), out executable))
            {
                Log(LogCategory.Executable, $@"Resolved Game Executable in games/client: ""{executable!.FullName}""");
                return true;
            }

            if (!string.IsNullOrEmpty(platformSubdir) &&
                TryCheckCandidate(Path.Combine(resolvedClientDir, platformSubdir, targetExeName), out executable))
            {
                Log(LogCategory.Executable,
                    $@"Resolved Game Executable in games/client/{platformSubdir}: ""{executable!.FullName}""");
                return true;
            }

            if (isMacTarget)
            {
                string[] macClientBundles =
                [
                    Path.Combine(resolvedClientDir, "Heroes of Newerth.app", "Contents", "MacOS", "HoN64"),
                    Path.Combine(resolvedClientDir, "macos-x64", "Heroes of Newerth.app", "Contents", "MacOS", "HoN64")
                ];
                foreach (string macClientBundle in macClientBundles)
                {
                    if (TryCheckCandidate(macClientBundle, out executable))
                    {
                        Log(LogCategory.Executable,
                            $@"Resolved macOS .app Game Executable in games/client: ""{executable!.FullName}""");
                        return true;
                    }
                }
            }
        }

        // 4. OS-specific platform subdirectory check in BaseDirectory & CurrentDirectory
        if (!string.IsNullOrEmpty(platformSubdir))
        {
            if (TryCheckCandidate(Path.Combine(baseDir, platformSubdir, targetExeName), out executable))
            {
                Log(LogCategory.Executable,
                    $@"Resolved Game Executable in BaseDirectory/{platformSubdir}: ""{executable!.FullName}""");
                return true;
            }

            if (TryCheckCandidate(Path.Combine(currentDir, platformSubdir, targetExeName), out executable))
            {
                Log(LogCategory.Executable,
                    $@"Resolved Game Executable in platform directory: ""{executable!.FullName}""");
                return true;
            }
        }

        // 5. Safe bounded fallback search (IgnoreInaccessible prevents POSIX $HOME / macOS TCC permission crashes)
        EnumerationOptions safeEnumOpts = new()
        {
            IgnoreInaccessible = true,
            RecurseSubdirectories = true,
            MaxRecursionDepth = 4
        };

        string userHome = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        foreach (string searchRoot in new[] { baseDir, currentDir }.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(searchRoot) || !Directory.Exists(searchRoot))
            {
                continue;
            }

            string trimmedRoot = searchRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (string.IsNullOrEmpty(trimmedRoot) ||
                (!string.IsNullOrWhiteSpace(userHome) &&
                 string.Equals(trimmedRoot, userHome.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), StringComparison.OrdinalIgnoreCase)))
            {
                Log(LogCategory.Executable,
                    $"[LaunchResolve] Skipping recursive fallback walk on unrestricted root/home directory '{searchRoot}'.");
                continue;
            }

            try
            {
                FileInfo? match = new DirectoryInfo(searchRoot)
                    .EnumerateFiles(targetExeName, safeEnumOpts)
                    .FirstOrDefault();
                if (match is not null)
                {
                    executable = match;
                    Log(LogCategory.Executable, $@"Resolved Game Executable via bounded scan: ""{executable.FullName}""");
                    return true;
                }
            }
            catch (Exception ex)
            {
                Log(LogCategory.Executable,
                    $"[LaunchResolve] Bounded fallback scan notice in '{searchRoot}': {ex.Message}");
            }
        }

        Log(LogCategory.Executable, $"Unable To Locate The Game Executable ({targetExeName}) In {currentDir} (BaseDir={baseDir}, Platform={normalizedPlatform})");
        executable = null;
        return false;
    }

    private string BuildMasterServerAddress()
    {
        string? rawSelection = TryGetMasterServerComboText();

        if (string.IsNullOrWhiteSpace(rawSelection))
        {
            rawSelection = !string.IsNullOrWhiteSpace(LoadedMasterServerAddress)
                ? LoadedMasterServerAddress
                : LauncherSettingsModel.DefaultLocalOfflineAutoMasterServer;
        }

        string address;
        if (IsOfflineModeSelection(rawSelection) || IsLocalOfflineAutoSelection(rawSelection))
        {
            address = "127.0.0.1:55555";
        }
        else if (rawSelection.Contains("CUSTOM", StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(CustomMasterServerAddress))
                address = "127.0.0.1:55555";
            else
                address = CustomMasterServerAddress;
        }
        else
        {
            address = rawSelection;
        }

        if (address.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
            address = address[7..];
        else if (address.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) address = address[8..];

        address = address.TrimEnd('/');

        // The Game Client Does Not Understand "localhost" As A Valid Address, So We Need To Replace It With The Loopback Address "127.0.0.1" For Locally Hosted Master Servers
        string resolvedAddress = address.Replace("localhost", IPAddress.Loopback.MapToIPv4().ToString());

        return resolvedAddress;
    }

    private async Task LaunchProcessAndExit(FileInfo executable, string[] arguments, bool isSafeMode = false)
    {
        if (ProcessLauncherOverride is not null)
        {
            await ProcessLauncherOverride(executable, arguments);
            return;
        }

        ClearClientStderrBuffer();
        DateTimeOffset launchStartUtc = TimeProvider.System.GetUtcNow();
        using WindowsTimerResolution timerResolution =
            WindowsTimerResolution.Enable(msg => Log(LogCategory.Executable, msg));

        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
            try
            {
                UnixFileMode currentMode = File.GetUnixFileMode(executable.FullName);

                // POSIX Systems Require The Execute Permission Bit To Be Explicitly Set For Downloaded Binaries To Run
                File.SetUnixFileMode(executable.FullName,
                    currentMode | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute);
            }

            catch (Exception exception)
            {
                Log(LogCategory.Executable, $@"Failed To Set POSIX Execution Permissions: {exception.Message}");
            }

        Log(LogCategory.Command, $@"""{executable.FullName}"" {string.Join(" ", arguments)}");

        // Background VFS toolchain audit and hot-reload watcher (skipped in Playtest mode)
        string clientRoot = executable.DirectoryName ?? Directory.GetCurrentDirectory();
        PlaytestPolicyDto launchPolicy = PlaytestPolicyService.Resolve(
            clientRoot,
            message => Log(LogCategory.Executable, message));

        if (launchPolicy.DisableDevWatchers)
        {
            Log(LogCategory.Executable,
                "[PlaytestPolicy] Skipping developer HotReloadWatcherService and launch-time VfsAuditService scan.");
        }
        else
        {
            _ = Task.Run(() =>
            {
                try
                {
                    _activeHotReloadService?.Dispose();
                    _activeHotReloadService = new HotReloadWatcherService(NullLogger<HotReloadWatcherService>.Instance, _activeIpcService);
                    _ = _activeHotReloadService.StartAsync(CancellationToken.None);

                    VfsAuditService vfsAudit = new(NullLogger<VfsAuditService>.Instance);
                    VfsAuditReport report = vfsAudit.PerformAudit(clientRoot);
                    Log(LogCategory.General,
                        $"[VFS Toolchain] Audited {report.TotalLooseFiles} loose files across {report.ScannedArchives.Count} archives ({report.TotalShadowedFiles} active, {report.TotalWarnings} warnings).");
                }
                catch (Exception ex)
                {
                    Log(LogCategory.General, $"[VFS Toolchain Init Error] {ex.Message}");
                }
            });
        }

        (string resolvedFileName, string resolvedArguments) = LaunchConfigWriterService.ResolveLinuxArm64TranslationCommand(
            executable.FullName,
            string.Join(" ", arguments));

        ProcessStartInfo startInfo = new()
        {
            FileName = resolvedFileName,
            WorkingDirectory = executable.DirectoryName ?? Environment.CurrentDirectory,
            Arguments = resolvedArguments,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        string resolvedClientDir = executable.DirectoryName ?? clientRoot;
        ConfigureChildClientEnvironment(startInfo, resolvedClientDir, _activeIpcAuthKey, isSafeMode: isSafeMode);

        if (OperatingSystem.IsLinux())
        {
            if (!string.Equals(resolvedFileName, executable.FullName, StringComparison.Ordinal))
            {
                Log(LogCategory.Executable,
                    $"[LinuxArm64] Wrapping x86_64 client launch with user-mode translator '{resolvedFileName}'.");
            }

            string runtimeLibsDir = Path.Combine(startInfo.WorkingDirectory, "libs-runtime");
            string fallbackLibsDir = Path.Combine(startInfo.WorkingDirectory, "libs-x86_64");
            string activeLibsDir = Directory.Exists(runtimeLibsDir) ? runtimeLibsDir : fallbackLibsDir;
            if (Directory.Exists(activeLibsDir))
            {
                string existingLd = Environment.GetEnvironmentVariable("LD_LIBRARY_PATH") ?? string.Empty;
                string ldPrefix = $"{startInfo.WorkingDirectory}:{activeLibsDir}";
                string mergedLd = string.IsNullOrEmpty(existingLd) ? ldPrefix : $"{ldPrefix}:{existingLd}";
                startInfo.EnvironmentVariables["LD_LIBRARY_PATH"] = mergedLd;
                startInfo.EnvironmentVariables["BOX64_LD_LIBRARY_PATH"] = mergedLd;
            }

            // Isolate K2 engine saves directly to internal project userdata folder (no symlinks, no host pollution)
            string internalUserdata = Path.Combine(startInfo.WorkingDirectory, "userdata");
            if (!Directory.Exists(internalUserdata))
            {
                Directory.CreateDirectory(internalUserdata);
            }
            startInfo.EnvironmentVariables["HOME"] = internalUserdata;
            startInfo.EnvironmentVariables["LC_NUMERIC"] = "C";
            startInfo.EnvironmentVariables["LC_MESSAGES"] = "C";

            // Preserve active XAUTHORITY so X11/XWayland display server authentication is retained
            string? existingXauth = Environment.GetEnvironmentVariable("XAUTHORITY");
            if (!string.IsNullOrEmpty(existingXauth))
            {
                startInfo.EnvironmentVariables["XAUTHORITY"] = existingXauth;
            }

            Log(LogCategory.Executable, $"[PORTABLE_STORAGE] Directed Linux engine HOME to internal userdata: '{internalUserdata}'");
            LaunchConfigWriterService.ConfigureLinuxGraphicsEnvironment(
                startInfo.EnvironmentVariables,
                startInfo.WorkingDirectory,
                Log);
            LaunchConfigWriterService.ProbeLinuxGraphicsTelemetry(Log);

            EnsureLinuxModesettingCompatibility(startInfo.WorkingDirectory);
        }
        else if (OperatingSystem.IsMacOS())
        {
            string internalUserdata = Path.Combine(startInfo.WorkingDirectory, "userdata");
            if (!Directory.Exists(internalUserdata))
            {
                Directory.CreateDirectory(internalUserdata);
            }
            startInfo.EnvironmentVariables["HOME"] = internalUserdata;
            startInfo.EnvironmentVariables["LC_NUMERIC"] = "C";
            startInfo.EnvironmentVariables["LC_MESSAGES"] = "C";
            Log(LogCategory.Executable, $"[PORTABLE_STORAGE] Directed macOS engine HOME to internal userdata: '{internalUserdata}'");

            string frameworksDir = Path.Combine(startInfo.WorkingDirectory, "Contents", "Frameworks", "universal64");
            if (Directory.Exists(frameworksDir))
            {
                string existingDyld = Environment.GetEnvironmentVariable("DYLD_LIBRARY_PATH") ?? string.Empty;
                startInfo.EnvironmentVariables["DYLD_LIBRARY_PATH"] = string.IsNullOrEmpty(existingDyld)
                    ? frameworksDir
                    : $"{frameworksDir}:{existingDyld}";
            }
        }
        else if (OperatingSystem.IsWindows())
        {
            LaunchConfigWriterService.EnsureWindowsHighPerformanceGpuPreference(executable.FullName, Log);
        }

        Process process = new() { StartInfo = startInfo, EnableRaisingEvents = true };
        process.OutputDataReceived += (_, eventArgs) =>
        {
            if (string.IsNullOrWhiteSpace(eventArgs.Data) is false)
            {
                if (eventArgs.Data.Contains("The VAD has been replaced by a hack", StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                Log(LogCategory.GameClient, eventArgs.Data);
                _activeDiscordPresence?.ProcessConsoleLogLine(eventArgs.Data);
                EnsureLiveOnlinePlayerAtLeastOne();

                if (eventArgs.Data.Contains("[LAUNCH_URL]"))
                {
                    int index = eventArgs.Data.IndexOf("[LAUNCH_URL]", StringComparison.Ordinal);
                    string targetUrl = eventArgs.Data[(index + 12)..].Trim();
                    if (!string.IsNullOrEmpty(targetUrl) &&
                        (targetUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                         targetUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase)))
                    {
                        OpenExternalUrl(targetUrl);
                        Log(LogCategory.Executable,
                            $"[WILLOWMAKER URL Bridge] Launched external browser for: {targetUrl}");
                    }
                }
            }
        };
        process.ErrorDataReceived += (_, eventArgs) =>
        {
            if (string.IsNullOrWhiteSpace(eventArgs.Data) is false)
            {
                if (eventArgs.Data.Contains("The VAD has been replaced by a hack", StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                RecordClientStderrLine(eventArgs.Data);
                Log(LogCategory.GameClient, eventArgs.Data);
                _activeDiscordPresence?.ProcessConsoleLogLine(eventArgs.Data);
            }
        };

        bool versionProxyOnDisk = OperatingSystem.IsWindows() &&
                                  (File.Exists(Path.Combine(clientRoot, "version.dll")) ||
                                   File.Exists(Path.Combine(clientRoot, "windows-x64", "version.dll")));
        bool skipSuspendedInjection = OperatingSystem.IsWindows() &&
                                      launchPolicy.PreferStandardProcessStart &&
                                      versionProxyOnDisk;
        if (skipSuspendedInjection)
        {
            Log(LogCategory.Executable,
                "[PlaytestPolicy] Unified version.dll present on disk; launching via AV-clean standard Process.Start without CREATE_SUSPENDED or CreateRemoteThread.");
        }

        if (OperatingSystem.IsWindows() && !skipSuspendedInjection)
        {
            // 1. Resolve pre-existing fallback Kongor.NativeGuard.dll only if unified version.dll proxy is absent
            string guardPath = Path.Combine(clientRoot, "Kongor.NativeGuard.dll");
            if (!File.Exists(guardPath))
                guardPath = Path.Combine(AppContext.BaseDirectory, "Kongor.NativeGuard.dll");
            if (!File.Exists(guardPath))
                guardPath = Path.Combine(AppContext.BaseDirectory, "windows-x64", "Kongor.NativeGuard.dll");
            if (!File.Exists(guardPath))
                guardPath = Path.Combine(clientRoot, "windows-x64", "Kongor.NativeGuard.dll");

            SuspendedProcessWithStreamsResult? suspendedResult = null;
            try
            {
                Log(LogCategory.Executable,
                    $"[NativeProcessInjection] Passing {startInfo.EnvironmentVariables.Count} child environment variable(s) via CREATE_UNICODE_ENVIRONMENT block to CreateSuspendedProcessWithStreams (HON_MODERN_HUD='{startInfo.EnvironmentVariables["HON_MODERN_HUD"]}', HON_FSR_ENABLE='{startInfo.EnvironmentVariables["HON_FSR_ENABLE"]}', DXVK_CONFIG_FILE='{startInfo.EnvironmentVariables["DXVK_CONFIG_FILE"]}').");
                suspendedResult = NativeProcessInjectionService.CreateSuspendedProcessWithStreams(
                    executable.FullName,
                    string.Join(" ", arguments),
                    startInfo.WorkingDirectory,
                    createNoWindow: false,
                    environmentVariables: startInfo.EnvironmentVariables);
            }
            catch (Exception ex)
            {
                Log(LogCategory.Executable, $"[NativeProcessInjection] CreateSuspendedProcess failed: {ex.Message}. Falling back to standard launch.");
            }

            if (suspendedResult != null)
            {
                // Synchronously inject Kongor.NativeGuard.dll only if version.dll proxying is not already present
                bool versionProxyPresent = File.Exists(Path.Combine(clientRoot, "version.dll")) ||
                                           File.Exists(Path.Combine(clientRoot, "windows-x64", "version.dll"));

                if (versionProxyPresent)
                {
                    Log(LogCategory.Executable,
                        $"[NativeProcessInjection] Unified version.dll supervisor active for suspended hon_x64.exe PID {suspendedResult.ProcessInfo.ProcessId}; skipping legacy Kongor.NativeGuard.dll injection.");
                }
                else if (File.Exists(guardPath))
                {
                    var injectResult = NativeProcessInjectionService.InjectDll(suspendedResult.ProcessInfo.ProcessHandle, guardPath);
                    if (injectResult.Success)
                    {
                        Log(LogCategory.Executable,
                            $"[NativeProcessInjection] Injected Kongor.NativeGuard.dll into suspended hon_x64.exe PID {suspendedResult.ProcessInfo.ProcessId} (Base: 0x{injectResult.ModuleBase:X16}) before main thread start.");
                    }
                    else
                    {
                        Log(LogCategory.Executable,
                            $"[NativeProcessInjection] Injection warning for PID {suspendedResult.ProcessInfo.ProcessId}: {injectResult.ErrorMessage}");
                    }
                }

                // Attach Fast Bus shared memory telemetry consumer before resuming so tick 0 is monitored
                try
                {
                    _activeTelemetryConsumer?.Dispose();
                    _activeTelemetryConsumer = new TelemetryRingConsumerService();
                    long lastTelemetryTicks = 0;
                    _activeTelemetryConsumer.OnSnapshotUpdated += snapshot =>
                    {
                        if (snapshot.IsConnected && snapshot.TotalFramesCaptured > 0)
                        {
                            long nowTicks = System.Diagnostics.Stopwatch.GetTimestamp();
                            if (lastTelemetryTicks == 0 || System.Diagnostics.Stopwatch.GetElapsedTime(lastTelemetryTicks).TotalMilliseconds >= 1000)
                            {
                                lastTelemetryTicks = nowTicks;
                                Log(LogCategory.GameClient,
                                    $"[FAST-BUS TELEMETRY] FPS: {snapshot.CurrentFps:F1} (1% Low: {snapshot.Low1PercentFps:F1}) | Frametime: {snapshot.AverageFrametimeMs:F2}ms | Entities: {snapshot.ActiveEntities} | Draws: {snapshot.DrawCalls} | Ping: {snapshot.PingMs}ms");
                            }
                        }
                    };
                    _activeTelemetryConsumer.OnPttFlagsChanged += (ulong pttFlags) =>
                    {
                        HandlePttFlagsUpdated(pttFlags);
                    };
                    _activeTelemetryConsumer.OnEngineStateChanged += (EngineHeaderStateSnapshot state) =>
                    {
                        _activeDiscordPresence?.ProcessFastBusState(
                            state.GamePhase,
                            state.ActiveMatchId,
                            state.CurrentMap,
                            state.PlayerName);
                    };
                    string telemetryMapName = TelemetryRingConsumerService.GetClientMapName(suspendedResult.ProcessInfo.ProcessId);
                    _activeTelemetryConsumer.StartMonitoring(telemetryMapName, TimeSpan.FromMilliseconds(16));
                    Log(LogCategory.Executable, $"[FAST-BUS TELEMETRY] Attached telemetry consumer to '{telemetryMapName}'.");

                    uint localAccId = _activeDiscordPresence?.AccountId > 0
                        ? (uint)_activeDiscordPresence.AccountId
                        : 1u;
                    InitializeVoiceClient(localAccId);
                }
                catch (Exception ex)
                {
                    Log(LogCategory.Executable, $"[FAST-BUS TELEMETRY] Consumer initialization warning: {ex.Message}");
                }

                // Resume main thread - engine starts cleanly with hooks and 1.0ms timer active from tick 0
                NativeProcessInjectionService.ResumeProcess(suspendedResult.ProcessInfo.ThreadHandle);
                Log(LogCategory.Executable,
                    $"[NativeProcessInjection] Resumed main thread of hon_x64.exe PID {suspendedResult.ProcessInfo.ProcessId}. Engine running cleanly.");

                int suspendedPid = suspendedResult.ProcessInfo.ProcessId;
                EmitLaunchReceiptAndSchedulePostBootAudit(resolvedClientDir, executable.FullName, suspendedPid, arguments, startInfo.EnvironmentVariables, isSafeMode);
                using Process suspendedProcess = Process.GetProcessById(suspendedPid);

                void HandleConsoleOutputLine(string data)
                {
                    if (string.IsNullOrWhiteSpace(data)) return;
                    if (data.Contains("The VAD has been replaced by a hack", StringComparison.OrdinalIgnoreCase)) return;

                    Log(LogCategory.GameClient, data);
                    _activeDiscordPresence?.ProcessConsoleLogLine(data);
                    EnsureLiveOnlinePlayerAtLeastOne();

                    if (data.Contains("[LAUNCH_URL]"))
                    {
                        int index = data.IndexOf("[LAUNCH_URL]", StringComparison.Ordinal);
                        string targetUrl = data[(index + 12)..].Trim();
                        if (!string.IsNullOrEmpty(targetUrl) &&
                            (targetUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                             targetUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase)))
                        {
                            OpenExternalUrl(targetUrl);
                            Log(LogCategory.Executable,
                                $"[WILLOWMAKER URL Bridge] Launched external browser for: {targetUrl}");
                        }
                    }
                }

                if (suspendedResult.StandardOutput != null)
                {
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            while (await suspendedResult.StandardOutput.ReadLineAsync() is { } line)
                            {
                                HandleConsoleOutputLine(line);
                            }
                        }
                        catch (Exception ex)
                        {
                            Log(LogCategory.Executable, $"StdOut stream closed on process exit: {ex.Message}");
                        }
                    });
                }

                if (suspendedResult.StandardError != null)
                {
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            while (await suspendedResult.StandardError.ReadLineAsync() is { } line)
                            {
                                RecordClientStderrLine(line);
                                HandleConsoleOutputLine(line);
                            }
                        }
                        catch (Exception ex)
                        {
                            Log(LogCategory.Executable, $"StdErr stream closed on process exit: {ex.Message}");
                        }
                    });
                }

                int suspendedWaitTimeMs = 0;
                const int suspendedMaxWaitMs = 15000;
                while (!suspendedProcess.HasExited && suspendedProcess.MainWindowHandle == IntPtr.Zero && suspendedWaitTimeMs < suspendedMaxWaitMs)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(250));
                    suspendedWaitTimeMs += 250;
                    try
                    {
                        suspendedProcess.Refresh();
                    }
                    catch (Exception ex)
                    {
                        Log(LogCategory.Executable, $"Process refresh notice during startup wait: {ex.Message}");
                    }
                }

                if (suspendedProcess.HasExited)
                {
                    uint exitCode = 0;
                    if (!NativeProcessInjectionService.GetExitCode(suspendedResult.ProcessInfo.ProcessHandle, out exitCode))
                    {
                        try
                        {
                            exitCode = (uint)suspendedProcess.ExitCode;
                        }
                        catch (Exception ex)
                        {
                            Log(LogCategory.Executable, $"Suspended premature exit code notice: {ex.Message}");
                            exitCode = 0;
                        }
                    }

                    suspendedResult.Dispose();
                    await DisposeActiveGameSessionAsync("suspended-premature-exit");
                    Log(LogCategory.Executable,
                        $"Process Exited prematurely with code {exitCode} before main window handle was established.");
                    await HandleGameProcessTerminationAsync(exitCode, launchStartUtc, clientRoot, gameProcessId: suspendedPid);
                    return;
                }

                Log(LogCategory.Executable,
                    $"Game process main window established (Handle: {suspendedProcess.MainWindowHandle}). Launcher waiting for process exit...");

                bool suspendedKilledByShutdownWatchdog = await AwaitGameExitWithShutdownWatchdogAsync(
                    suspendedProcess,
                    clientRoot,
                    suspendedPid).ConfigureAwait(false);
                uint finalSuspendedExit = 0;
                if (suspendedKilledByShutdownWatchdog)
                {
                    finalSuspendedExit = 0xCFFFFFFFu;
                }
                else if (!NativeProcessInjectionService.GetExitCode(suspendedResult.ProcessInfo.ProcessHandle, out finalSuspendedExit))
                {
                    try { finalSuspendedExit = (uint)suspendedProcess.ExitCode; } catch (Exception ex) { Log(LogCategory.Executable, $"Suspended exit code notice: {ex.Message}"); finalSuspendedExit = 0; }
                }
                Log(LogCategory.Executable, $"Game process exited with code {finalSuspendedExit} (0x{finalSuspendedExit:X8}). Disposing launcher session...");

                await RunBoundedTeardownStepAsync(
                    "SuspendedProcessHandles",
                    () => suspendedResult.Dispose(),
                    TimeSpan.FromMilliseconds(1000),
                    msg => Log(LogCategory.Executable, msg)).ConfigureAwait(false);
                await DisposeActiveGameSessionAsync("suspended-exit").ConfigureAwait(false);

                await HandleGameProcessTerminationAsync(finalSuspendedExit, launchStartUtc, clientRoot, gameProcessId: suspendedPid).ConfigureAwait(false);
                return;
            }
        }

        if (process.Start() is false)
        {
            Log(LogCategory.Executable, $@"Process Failed To Start: ""{executable.FullName}""");

            return;
        }

        // Runtime supervision: Unified in version.dll; only inject Kongor.NativeGuard.dll as fallback if version.dll proxy is absent
        if (OperatingSystem.IsWindows())
        {
            try
            {
                bool versionProxyPresent = File.Exists(Path.Combine(clientRoot, "version.dll")) ||
                                           File.Exists(Path.Combine(clientRoot, "windows-x64", "version.dll"));
                if (versionProxyPresent)
                {
                    Log(LogCategory.Executable,
                        $"[NativeProcessInjection] Unified version.dll supervisor active for hon_x64.exe PID {process.Id}; skipping redundant Kongor.NativeGuard.dll injection.");
                }
                else
                {
                    string guardPath = Path.Combine(clientRoot, "Kongor.NativeGuard.dll");
                    if (!File.Exists(guardPath))
                        guardPath = Path.Combine(AppContext.BaseDirectory, "Kongor.NativeGuard.dll");
                    if (!File.Exists(guardPath))
                        guardPath = Path.Combine(AppContext.BaseDirectory, "windows-x64", "Kongor.NativeGuard.dll");
                    if (!File.Exists(guardPath))
                        guardPath = Path.Combine(clientRoot, "windows-x64", "Kongor.NativeGuard.dll");

                    if (File.Exists(guardPath))
                    {
                        var injectResult = NativeProcessInjectionService.InjectDll(process.Handle, guardPath);
                        if (injectResult.Success)
                        {
                            Log(LogCategory.Executable,
                                $"[NativeProcessInjection] Injected fallback Kongor.NativeGuard.dll into hon_x64.exe PID {process.Id} (Base: 0x{injectResult.ModuleBase:X8}) for persistent 1.0ms timer pacing.");
                        }
                        else
                        {
                            Log(LogCategory.Executable,
                                $"[NativeProcessInjection] Injection warning for PID {process.Id}: {injectResult.ErrorMessage}");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Log(LogCategory.Executable, $"[NativeProcessInjection] Injection attempt skipped: {ex.Message}");
            }
        }

        // Attach Fast Bus shared memory telemetry consumer for sub-millisecond game loop monitoring
        if (OperatingSystem.IsWindows())
        {
            try
            {
                _activeTelemetryConsumer?.Dispose();
                _activeTelemetryConsumer = new TelemetryRingConsumerService();
                long lastTelemetryTicks = 0;
                _activeTelemetryConsumer.OnSnapshotUpdated += snapshot =>
                {
                    if (snapshot.IsConnected && snapshot.TotalFramesCaptured > 0)
                    {
                        long nowTicks = System.Diagnostics.Stopwatch.GetTimestamp();
                        if (lastTelemetryTicks == 0 || System.Diagnostics.Stopwatch.GetElapsedTime(lastTelemetryTicks).TotalMilliseconds >= 1000)
                        {
                            lastTelemetryTicks = nowTicks;
                            Log(LogCategory.GameClient,
                                $"[FAST-BUS TELEMETRY] FPS: {snapshot.CurrentFps:F1} (1% Low: {snapshot.Low1PercentFps:F1}) | Frametime: {snapshot.AverageFrametimeMs:F2}ms | Entities: {snapshot.ActiveEntities} | Draws: {snapshot.DrawCalls} | Ping: {snapshot.PingMs}ms");
                        }
                    }
                };
                _activeTelemetryConsumer.OnPttFlagsChanged += (ulong pttFlags) =>
                {
                    HandlePttFlagsUpdated(pttFlags);
                };
                _activeTelemetryConsumer.OnEngineStateChanged += (EngineHeaderStateSnapshot state) =>
                {
                    _activeDiscordPresence?.ProcessFastBusState(
                        state.GamePhase,
                        state.ActiveMatchId,
                        state.CurrentMap,
                        state.PlayerName);
                };
                string telemetryMapName = TelemetryRingConsumerService.GetClientMapName(process.Id);
                _activeTelemetryConsumer.StartMonitoring(telemetryMapName, TimeSpan.FromMilliseconds(16));
                Log(LogCategory.Executable, $"[FAST-BUS TELEMETRY] Attached telemetry consumer to '{telemetryMapName}'.");

                uint localAccId = _activeDiscordPresence?.AccountId > 0
                    ? (uint)_activeDiscordPresence.AccountId
                    : 1u;
                InitializeVoiceClient(localAccId);
            }
            catch (Exception ex)
            {
                Log(LogCategory.Executable, $"[FAST-BUS TELEMETRY] Consumer initialization warning: {ex.Message}");
            }
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        EmitLaunchReceiptAndSchedulePostBootAudit(resolvedClientDir, executable.FullName, process.Id, arguments, startInfo.EnvironmentVariables, isSafeMode);

        if (OperatingSystem.IsWindows())
        {
            int waitTimeMs = 0;
            const int maxWaitMs = 15000;
            while (!process.HasExited && process.MainWindowHandle == IntPtr.Zero && waitTimeMs < maxWaitMs)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(250));
                waitTimeMs += 250;
                try
                {
                    process.Refresh();
                }
                catch (InvalidOperationException refreshEx)
                {
                    Log(LogCategory.Executable, $"Window handle poll refresh notice: {refreshEx.Message}");
                }
            }
        }
        else
        {
            // On Linux (X11/Wayland) and macOS (AppKit), .NET Process.MainWindowHandle is always IntPtr.Zero.
            // Perform a bounded 1500ms startup liveness check instead of stalling 15s or misreporting <15s POSIX exits.
            int posixStartupCheckMs = 0;
            const int maxPosixStartupMs = 1500;
            while (!process.HasExited && posixStartupCheckMs < maxPosixStartupMs)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(250));
                posixStartupCheckMs += 250;
            }
        }

        if (process.HasExited)
        {
            await DisposeActiveGameSessionAsync("premature-exit");
            int exitCode = -1;
            try { exitCode = process.ExitCode; } catch (Exception ex) { Log(LogCategory.Executable, $"Premature exit code notice: {ex.Message}"); }
            int prematurePid = 0;
            try { prematurePid = process.Id; } catch (Exception ex) { Log(LogCategory.Executable, $"Premature PID notice: {ex.Message}"); }
            Log(LogCategory.Executable,
                $"Process Exited prematurely with code {exitCode} during startup window/liveness check.");
            await HandleGameProcessTerminationAsync((uint)exitCode, launchStartUtc, clientRoot, gameProcessId: prematurePid > 0 ? prematurePid : null);
            return;
        }

        int activeGamePid = 0;
        try { activeGamePid = process.Id; } catch (Exception ex) { Log(LogCategory.Executable, $"Active PID notice: {ex.Message}"); }
        if (OperatingSystem.IsWindows())
        {
            Log(LogCategory.Executable,
                $"Game process main window established (Handle: {process.MainWindowHandle}, PID: {activeGamePid}). Launcher waiting for process exit...");
        }
        else
        {
            Log(LogCategory.Executable,
                $"[POSIX Launch] Verified K2 client process alive on {RuntimeInformation.OSDescription} (PID: {activeGamePid}; bypassed Win32 MainWindowHandle poll). Launcher waiting for process exit...");
        }

        // Wait for the game process to exit with out-of-process shutdown-hang supervision so Discord RPC stays alive
        // and any LdrShutdownProcess / GPU driver deadlock after quit is captured and terminated cleanly.
        bool killedByShutdownWatchdog = await AwaitGameExitWithShutdownWatchdogAsync(
            process,
            clientRoot,
            activeGamePid).ConfigureAwait(false);
        uint finalProcessExit = 0;
        if (killedByShutdownWatchdog)
        {
            finalProcessExit = 0xCFFFFFFFu;
        }
        else
        {
            try { finalProcessExit = (uint)process.ExitCode; } catch (Exception ex) { Log(LogCategory.Executable, $"Final exit code notice: {ex.Message}"); }
        }
        Log(LogCategory.Executable, $"Game process exited with code {finalProcessExit} (0x{finalProcessExit:X8}, PID: {activeGamePid}). Disposing launcher session...");

        await DisposeActiveGameSessionAsync("process-exit").ConfigureAwait(false);

        await HandleGameProcessTerminationAsync(
            finalProcessExit,
            launchStartUtc,
            clientRoot,
            gameProcessId: activeGamePid > 0 ? activeGamePid : null).ConfigureAwait(false);
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SendMessageTimeoutW(
        IntPtr hWnd,
        uint msg,
        UIntPtr wParam,
        IntPtr lParam,
        uint fuFlags,
        uint uTimeout,
        out UIntPtr lpdwResult);

    private static bool IsWindowResponsiveWin32(IntPtr hwnd, uint timeoutMs = 500)
    {
        if (!OperatingSystem.IsWindows() || hwnd == IntPtr.Zero)
        {
            return false;
        }

        const uint WM_NULL = 0x0000u;
        const uint SMTO_ABORTIFHUNG = 0x0002u;
        IntPtr res = SendMessageTimeoutW(hwnd, WM_NULL, UIntPtr.Zero, IntPtr.Zero, SMTO_ABORTIFHUNG, timeoutMs, out _);
        return res != IntPtr.Zero;
    }

    public static TimeSpan ResolveShutdownHangTimeout()
    {
        const int defaultMs = 10_000;
        const int minMs = 3_000;
        const int maxMs = 60_000;
        string? raw = Environment.GetEnvironmentVariable("KONGOR_SHUTDOWN_HANG_TIMEOUT_MS");
        if (!string.IsNullOrWhiteSpace(raw) && int.TryParse(raw.Trim(), out int parsed))
        {
            return TimeSpan.FromMilliseconds(Math.Clamp(parsed, minMs, maxMs));
        }

        return TimeSpan.FromMilliseconds(defaultMs);
    }

    /// <summary>
    /// Awaits <paramref name="process"/> exit while supervising post-quit shutdown progress out-of-process.
    /// If NativeGuard writes <c>quit-initiated.&lt;pid&gt;.sentinel</c> (or <c>clean-exit.&lt;pid&gt;.sentinel</c>)
    /// and the client remains wedged past the timeout (default 10s), captures a PID-targeted Tier-1 minidump,
    /// revokes <c>clean-exit.&lt;pid&gt;.sentinel</c>, writes <c>last-crashed.&lt;pid&gt;.sentinel</c>, and kills
    /// the hung process so WILLOWMAKER auto-exports <c>Kongor_Diag_*_shutdown_hang.zip</c>.
    /// </summary>
    private async Task<bool> AwaitGameExitWithShutdownWatchdogAsync(
        Process process,
        string clientRoot,
        int processId)
    {
        Task waitExitTask = process.WaitForExitAsync();
        if (!OperatingSystem.IsWindows() || processId <= 0 || string.IsNullOrWhiteSpace(clientRoot))
        {
            await waitExitTask.ConfigureAwait(false);
            return false;
        }

        string crashDir = Path.Combine(clientRoot, "logs", "crash");
        string quitSentinelPath = Path.Combine(crashDir, $"quit-initiated.{processId}.sentinel");
        string cleanSentinelPath = Path.Combine(crashDir, $"clean-exit.{processId}.sentinel");
        string crashSentinelPath = Path.Combine(crashDir, $"last-crashed.{processId}.sentinel");
        TimeSpan hangTimeout = ResolveShutdownHangTimeout();
        DateTimeOffset? quitObservedUtc = null;

        while (!waitExitTask.IsCompleted)
        {
            try
            {
                await waitExitTask.WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
                return false;
            }
            catch (TimeoutException)
            {
                // Normal 1-second watchdog polling slice while game process is still running.
            }

            if (process.HasExited)
            {
                await waitExitTask.ConfigureAwait(false);
                return false;
            }

            if (!quitObservedUtc.HasValue)
            {
                if (File.Exists(quitSentinelPath))
                {
                    quitObservedUtc = new DateTimeOffset(File.GetLastWriteTimeUtc(quitSentinelPath), TimeSpan.Zero);
                    Log(LogCategory.Executable,
                        $"[SHUTDOWN_WATCHDOG] Detected quit-initiated.{processId}.sentinel (timeout={hangTimeout.TotalSeconds:F1}s); monitoring clean shutdown...");
                }
                else if (File.Exists(cleanSentinelPath))
                {
                    quitObservedUtc = new DateTimeOffset(File.GetLastWriteTimeUtc(cleanSentinelPath), TimeSpan.Zero);
                    Log(LogCategory.Executable,
                        $"[SHUTDOWN_WATCHDOG] Detected clean-exit.{processId}.sentinel while process still alive (timeout={hangTimeout.TotalSeconds:F1}s); monitoring LdrShutdownProcess...");
                }
            }

            if (quitObservedUtc.HasValue)
            {
                TimeSpan elapsed = TimeProvider.System.GetUtcNow() - quitObservedUtc.Value;
                if (elapsed >= hangTimeout)
                {
                    IntPtr hwnd = IntPtr.Zero;
                    try
                    {
                        process.Refresh();
                        hwnd = process.MainWindowHandle;
                    }
                    catch (Exception ex)
                    {
                        Log(LogCategory.Executable, $"[SHUTDOWN_WATCHDOG] Process refresh notice (PID {processId}): {ex.Message}");
                    }

                    bool windowHung = hwnd == IntPtr.Zero || !IsWindowResponsiveWin32(hwnd, 500);
                    bool hardCapExceeded = elapsed >= hangTimeout + TimeSpan.FromSeconds(5);
                    if (!windowHung && !hardCapExceeded)
                    {
                        continue;
                    }

                    Log(LogCategory.Executable,
                        $"[SHUTDOWN_WATCHDOG] Client PID {processId} hung for {elapsed.TotalSeconds:F1}s during shutdown (hwnd=0x{hwnd.ToInt64():X}, windowHung={windowHung}); capturing out-of-process Tier-1 minidump...");

                    string? capturedDumpPath = DiagnosticBundleService.TryCaptureProcessMinidumpToFile(processId, crashDir, "hang");
                    try
                    {
                        if (File.Exists(cleanSentinelPath))
                        {
                            File.Delete(cleanSentinelPath);
                        }
                    }
                    catch (Exception ex)
                    {
                        Log(LogCategory.Executable, $"[SHUTDOWN_WATCHDOG] Clean-exit sentinel revoke notice: {ex.Message}");
                    }

                    try
                    {
                        Directory.CreateDirectory(crashDir);
                        string dumpName = string.IsNullOrWhiteSpace(capturedDumpPath) ? "none" : Path.GetFileName(capturedDumpPath);
                        string sentinelBody =
                            $"PID={processId}\n" +
                            $"TimestampUtc={TimeProvider.System.GetUtcNow():O}\n" +
                            "Reason=ShutdownHangTimeout\n" +
                            "ExceptionCode=0xCFFFFFFF\n" +
                            "Location=ShutdownWatchdog\n" +
                            $"HangElapsedSeconds={elapsed.TotalSeconds:F1}\n" +
                            $"DumpFile={dumpName}\n";
                        File.WriteAllText(crashSentinelPath, sentinelBody);
                    }
                    catch (Exception ex)
                    {
                        Log(LogCategory.Executable, $"[SHUTDOWN_WATCHDOG] Failed writing last-crashed.{processId}.sentinel: {ex.Message}");
                    }

                    Log(LogCategory.Executable,
                        $"[SHUTDOWN_WATCHDOG] Captured dump='{capturedDumpPath ?? "none"}'. Terminating wedged client PID {processId}...");
                    try
                    {
                        if (!process.HasExited)
                        {
                            process.Kill(entireProcessTree: false);
                        }
                    }
                    catch (Exception ex)
                    {
                        Log(LogCategory.Executable, $"[SHUTDOWN_WATCHDOG] Process.Kill notice for PID {processId}: {ex.Message}");
                    }

                    try
                    {
                        await waitExitTask.WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
                    }
                    catch (TimeoutException)
                    {
                        Log(LogCategory.Executable, $"[SHUTDOWN_WATCHDOG] Bounded wait after Kill timed out for PID {processId}; proceeding to crash bundle export.");
                    }

                    return true;
                }
            }
        }

        await waitExitTask.ConfigureAwait(false);
        return false;
    }

    public static async Task RunBoundedTeardownStepAsync(
        string stepName,
        Func<Task> asyncAction,
        TimeSpan timeout,
        Action<string> log)
    {
        long startTicks = Stopwatch.GetTimestamp();
        log($"[SESSION_TEARDOWN] Starting step '{stepName}' (budget={timeout.TotalMilliseconds:F0}ms)...");
        try
        {
            await Task.Run(asyncAction).WaitAsync(timeout).ConfigureAwait(false);
            double elapsedMs = Stopwatch.GetElapsedTime(startTicks).TotalMilliseconds;
            log($"[SESSION_TEARDOWN] Finished step '{stepName}' in {elapsedMs:F1}ms.");
        }
        catch (TimeoutException)
        {
            double elapsedMs = Stopwatch.GetElapsedTime(startTicks).TotalMilliseconds;
            log($"[SESSION_TEARDOWN] Step '{stepName}' timed out after {elapsedMs:F1}ms; continuing launcher shutdown.");
        }
        catch (Exception ex)
        {
            double elapsedMs = Stopwatch.GetElapsedTime(startTicks).TotalMilliseconds;
            log($"[SESSION_TEARDOWN] Step '{stepName}' warning after {elapsedMs:F1}ms: {ex.Message}");
        }
    }

    private void WireDiscordPresenceEvents(DiscordPresenceService presence)
    {
        presence.OnPlayerIdentified += playerName =>
        {
            Log(LogCategory.Executable, $"Identified active player: {playerName}");
            EnsureLiveOnlinePlayerAtLeastOne();
            if (presence.AccountId > 0 && _activeVoiceClient != null)
            {
                _activeVoiceClient.LocalAccountId = (uint)presence.AccountId;
            }
        };

        presence.OnActivityUpdated += (details, state) =>
        {
            string activityLine = string.IsNullOrWhiteSpace(details) ? state : $"{state} | {details}";
            if (!string.IsNullOrWhiteSpace(activityLine))
            {
                Log(LogCategory.GameClient, $"[Discord RPC] {activityLine}");
            }

            string previewText;
            if (string.IsNullOrWhiteSpace(details) && string.IsNullOrWhiteSpace(state))
            {
                previewText = string.Empty;
            }
            else
            {
                string prefix = DiscordStreamerPrivacyMode ? "DRP (Privacy):" : "DRP:";
                string summary = string.IsNullOrWhiteSpace(state) ? details : $"{details} • {state}";
                previewText = $"{prefix} {summary}";
            }

            RunOnUiThread(() => DiscordPresencePreviewText = previewText);
        };
    }

    public static Task RunBoundedTeardownStepAsync(
        string stepName,
        Action syncAction,
        TimeSpan timeout,
        Action<string> log)
    {
        return RunBoundedTeardownStepAsync(
            stepName,
            () =>
            {
                syncAction();
                return Task.CompletedTask;
            },
            timeout,
            log);
    }

    /// <summary>
    /// Fail-safe, bounded asynchronous teardown of active launcher session services after game exit.
    /// Runs every step off the UI SynchronizationContext with explicit timeouts so SignalR, WASAPI,
    /// Named Pipe, or P2P teardown can never deadlock launcher exit or crash triage.
    /// </summary>
    public async Task DisposeActiveGameSessionAsync(string reason)
    {
        long teardownStartTicks = Stopwatch.GetTimestamp();
        Action<string> stepLog = msg => Log(LogCategory.Executable, msg);
        Log(LogCategory.Executable, $"[SESSION_TEARDOWN] Starting bounded game session teardown (reason={reason})...");

        CancellationTokenSource? auditCts = Interlocked.Exchange(ref _activePostBootAuditCts, null);
        if (auditCts is not null)
        {
            try
            {
                auditCts.Cancel();
                auditCts.Dispose();
            }
            catch (Exception ex)
            {
                stepLog($"[LAUNCH_RECEIPT] Post-boot audit cancellation notice: {ex.Message}");
            }
        }

        TelemetryRingConsumerService? telemetry = Interlocked.Exchange(ref _activeTelemetryConsumer, null);
        if (telemetry != null)
        {
            await RunBoundedTeardownStepAsync(
                "TelemetryRingConsumerService.Dispose",
                () => telemetry.Dispose(),
                TimeSpan.FromMilliseconds(1000),
                stepLog).ConfigureAwait(false);
        }

        DiscordPresenceService? presence = Interlocked.Exchange(ref _activeDiscordPresence, null);
        if (presence != null)
        {
            await RunBoundedTeardownStepAsync(
                "DiscordPresenceService.DisposeAsync",
                async () => await presence.DisposeAsync().ConfigureAwait(false),
                TimeSpan.FromMilliseconds(1500),
                stepLog).ConfigureAwait(false);
        }

        await RunBoundedTeardownStepAsync(
            "VoiceClientService.DisposeVoiceClientAsync",
            () => DisposeVoiceClientAsync(),
            TimeSpan.FromMilliseconds(1500),
            stepLog).ConfigureAwait(false);

        _activeIpcAuthKey = null;
        RunOnUiThread(() => DiscordPresencePreviewText = string.Empty);

        HotReloadWatcherService? hotReload = Interlocked.Exchange(ref _activeHotReloadService, null);
        if (hotReload != null)
        {
            await RunBoundedTeardownStepAsync(
                "HotReloadWatcherService.DisposeAsync",
                async () => await hotReload.DisposeAsync().ConfigureAwait(false),
                TimeSpan.FromMilliseconds(1500),
                stepLog).ConfigureAwait(false);
        }

        if (_cockpitIpcPersistent && Volatile.Read(ref _backgroundDisposed) == 0 && Volatile.Read(ref _shutdownInitiated) == 0)
        {
            if (_activeIpcService is not null)
            {
                _activeIpcService.AuthKey = null;
                Log(LogCategory.Executable,
                    $"[SESSION_TEARDOWN] Cleared session AuthKey while keeping persistent Cockpit IPC pipe '{_activeIpcService.PipeName}' active.");
            }
        }
        else
        {
            WillowmakerIpcService? ipc = Interlocked.Exchange(ref _activeIpcService, null);
            _boundStreamEventsService = null;
            if (ipc != null)
            {
                await RunBoundedTeardownStepAsync(
                    "WillowmakerIpcService.DisposeAsync",
                    async () => await ipc.DisposeAsync().ConfigureAwait(false),
                    TimeSpan.FromMilliseconds(1500),
                    stepLog).ConfigureAwait(false);
            }
        }

        await RunBoundedTeardownStepAsync(
            "P2pHostServerService.StopActiveHostServer",
            () => P2pHostServerService.StopActiveHostServer(),
            TimeSpan.FromMilliseconds(1500),
            stepLog).ConfigureAwait(false);

        await RunBoundedTeardownStepAsync(
            "ClientNetcodeRelaySessionManager.StopRelay",
            () => _netcodeRelaySessionManager.StopRelay(),
            TimeSpan.FromMilliseconds(1000),
            stepLog).ConfigureAwait(false);

        await RunBoundedTeardownStepAsync(
            "P2pLoopbackAuthService.Stop",
            () => P2pLoopbackAuthService.Stop(),
            TimeSpan.FromMilliseconds(1000),
            stepLog).ConfigureAwait(false);

        double totalElapsedMs = Stopwatch.GetElapsedTime(teardownStartTicks).TotalMilliseconds;
        Log(LogCategory.Executable, $"[SESSION_TEARDOWN] Completed bounded game session teardown (reason={reason}, elapsed={totalElapsedMs:F1}ms).");
    }

    private void EmitLaunchReceiptAndSchedulePostBootAudit(
        string clientDir,
        string clientExecutablePath,
        int gamePid,
        string[] arguments,
        System.Collections.Specialized.StringDictionary childEnvironment,
        bool isSafeMode)
    {
        try
        {
            string? vkLayersEnv = childEnvironment.ContainsKey("VK_INSTANCE_LAYERS")
                ? childEnvironment["VK_INSTANCE_LAYERS"]
                : null;
            bool expectVkLayer = !string.IsNullOrWhiteSpace(vkLayersEnv) &&
                                 vkLayersEnv.Contains("VK_LAYER_HON_replay_capture", StringComparison.OrdinalIgnoreCase);

            string receiptPath = LaunchReceiptService.WriteLaunchReceipt(
                clientDir: clientDir,
                clientExecutablePath: clientExecutablePath,
                gamePid: gamePid,
                rawArguments: arguments,
                childEnvironment: childEnvironment,
                selectedVulkanHudMode: SelectedVulkanHudMode,
                selectedRayTracingPreset: SelectedRayTracingPreset,
                selectedShadowFidelity: SelectedShadowFidelity,
                selectedPresentationMode: SelectedPresentationMode,
                allowThirdPartyVulkanOverlays: AllowThirdPartyVulkanOverlays,
                expectedVkLayerReplayCaptureLoaded: expectVkLayer,
                isSafeMode: isSafeMode,
                log: msg => Log(LogCategory.Executable, msg));

            CancellationTokenSource nextAuditCts = new();
            CancellationToken token = nextAuditCts.Token;
            CancellationTokenSource? prevAuditCts = Interlocked.Exchange(ref _activePostBootAuditCts, nextAuditCts);
            if (prevAuditCts is not null)
            {
                try
                {
                    prevAuditCts.Cancel();
                    prevAuditCts.Dispose();
                }
                catch (Exception ex)
                {
                    Log(LogCategory.Executable, $"[LAUNCH_RECEIPT] Previous audit CTS cleanup notice: {ex.Message}");
                }
            }

            _ = Task.Run(async () =>
            {
                await LaunchReceiptService.RunPostBootModuleAuditAsync(
                    gamePid: gamePid,
                    receiptPath: receiptPath,
                    expectedVkLayerReplayCaptureLoaded: expectVkLayer,
                    allowThirdPartyVulkanOverlays: AllowThirdPartyVulkanOverlays,
                    delay: TimeSpan.FromSeconds(10),
                    log: msg => Log(LogCategory.Executable, msg),
                    cancellationToken: token).ConfigureAwait(false);
            }, token);
        }
        catch (Exception ex)
        {
            Log(LogCategory.Executable, $"[LAUNCH_RECEIPT] Non-fatal receipt emit warning: {ex.Message}");
        }
    }

    /// <summary>
    ///     Determines whether the native Vulkan swapchain capture/overlay layer (<c>VK_LAYER_HON_replay_capture</c>)
    ///     should be injected into a live game client process.
    ///     Standard gameplay modes ("Off (Clean Screen)", "Minimal FPS", "FPS Only") use DXVK's built-in HUD with
    ///     an unhooked swapchain so Windows DWM can sustain Hardware Independent Flip at 200+ Hz.
    /// </summary>
    public static bool ShouldInjectVulkanCaptureLayer(string? selectedVulkanHudMode, bool captureEnabled = false)
    {
        if (captureEnabled)
            return true;

        if (string.IsNullOrWhiteSpace(selectedVulkanHudMode))
            return false;

        return selectedVulkanHudMode.Contains("Pro Diagnostics", StringComparison.OrdinalIgnoreCase) ||
               selectedVulkanHudMode.Contains("Modern HUD", StringComparison.OrdinalIgnoreCase) ||
               selectedVulkanHudMode.Contains("DevTools", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    ///     Configures per-user DXVK, OpenAL Soft, IPC authentication, and Vulkan swapchain layer
    ///     environment variables exclusively on the child game <see cref="ProcessStartInfo" />
    ///     without polluting the Avalonia launcher's own process environment.
    /// </summary>
    public void ConfigureChildClientEnvironment(
        ProcessStartInfo startInfo,
        string clientDir,
        string? ipcAuthKey = null,
        bool? isWindowsOverride = null,
        bool isSafeMode = false)
    {
        ArgumentNullException.ThrowIfNull(startInfo);
        ArgumentException.ThrowIfNullOrWhiteSpace(clientDir);

        string userdataKongorDir = Path.Combine(clientDir, "userdataKONGOR");
        string dxvkCacheDir = Path.Combine(userdataKongorDir, "dxvk_cache");
        startInfo.EnvironmentVariables["DXVK_CONFIG_FILE"] = Path.Combine(userdataKongorDir, "dxvk.conf");
        startInfo.EnvironmentVariables["DXVK_SHADER_CACHE"] = "1";
        startInfo.EnvironmentVariables["DXVK_SHADER_CACHE_PATH"] = dxvkCacheDir;
        startInfo.EnvironmentVariables["DXVK_STATE_CACHE_PATH"] = dxvkCacheDir;
        startInfo.EnvironmentVariables["ALSOFT_CONF"] = Path.Combine(userdataKongorDir, "alsoft.ini");

        if (!string.IsNullOrWhiteSpace(ipcAuthKey))
        {
            startInfo.EnvironmentVariables["KONGOR_IPC_AUTH_KEY"] = ipcAuthKey;
        }
        else
        {
            startInfo.EnvironmentVariables.Remove("KONGOR_IPC_AUTH_KEY");
        }

        if (isSafeMode)
        {
            startInfo.EnvironmentVariables["KONGOR_SAFE_MODE"] = "1";
            startInfo.EnvironmentVariables["VK_LOADER_LAYERS_DISABLE"] =
                VulkanImplicitLayerShield.BuildLoaderLayersDisableMask(
                    injectKongorReplayCaptureLayer: false,
                    allowThirdPartyVulkanOverlays: false)
                ?? "~implicit~,VK_LAYER_HON_replay_capture";
            VulkanImplicitLayerShieldPlan safeShieldPlan = VulkanImplicitLayerShield.DiscoverAndBuildPlan(
                allowThirdPartyVulkanOverlays: false,
                log: msg => Log(LogCategory.Executable, msg));
            VulkanImplicitLayerShield.ApplyToEnvironment(startInfo.EnvironmentVariables, safeShieldPlan);
            Log(LogCategory.Executable,
                $"[WILLOWMAKER] Safe Mode active on child process (KONGOR_SAFE_MODE=1). {VulkanImplicitLayerShield.FormatDiagnosticSummary(safeShieldPlan)}");
        }

        if (SelectedMonitor is { } crossMon && !string.IsNullOrWhiteSpace(crossMon.NormalizedDeviceToken))
        {
            startInfo.EnvironmentVariables["KONGOR_TARGET_MONITOR"] = crossMon.NormalizedDeviceToken;
        }
        startInfo.EnvironmentVariables["KONGOR_MANUAL_FPS_OVERRIDE"] =
            (ManualRefreshRateOverrideEnabled && int.TryParse(ManualMaxFpsText, out int crossEnvM) && crossEnvM >= 0
                ? (crossEnvM == 0 ? 1000 : crossEnvM)
                : 0).ToString();
        string detectedMonitorsEnv = HardwareDetectionService.FormatKongorDetectedMonitorsCvar(AvailableMonitors);
        if (!string.IsNullOrWhiteSpace(detectedMonitorsEnv))
        {
            startInfo.EnvironmentVariables["KONGOR_DETECTED_MONITORS"] = detectedMonitorsEnv;
        }

        string linuxGuardPath = Path.Combine(clientDir, "compatibility", "libkongor_native_guard.so");
        if (!File.Exists(linuxGuardPath))
        {
            linuxGuardPath = Path.Combine(clientDir, "compatibility", "libk2_native_guard.so");
        }
        if (OperatingSystem.IsLinux() && File.Exists(linuxGuardPath))
        {
            string? existingPreload = startInfo.EnvironmentVariables["LD_PRELOAD"] ?? Environment.GetEnvironmentVariable("LD_PRELOAD");
            if (string.IsNullOrWhiteSpace(existingPreload))
            {
                startInfo.EnvironmentVariables["LD_PRELOAD"] = linuxGuardPath;
            }
            else if (!existingPreload.Contains(linuxGuardPath, StringComparison.Ordinal))
            {
                startInfo.EnvironmentVariables["LD_PRELOAD"] = $"{linuxGuardPath}:{existingPreload}";
            }
        }

        string macGuardPath = Path.Combine(clientDir, "compatibility", "libkongor_native_guard.dylib");
        if (OperatingSystem.IsMacOS() && File.Exists(macGuardPath))
        {
            string? existingDyldInsert = startInfo.EnvironmentVariables["DYLD_INSERT_LIBRARIES"] ?? Environment.GetEnvironmentVariable("DYLD_INSERT_LIBRARIES");
            if (string.IsNullOrWhiteSpace(existingDyldInsert))
            {
                startInfo.EnvironmentVariables["DYLD_INSERT_LIBRARIES"] = macGuardPath;
            }
            else if (!existingDyldInsert.Contains(macGuardPath, StringComparison.Ordinal))
            {
                startInfo.EnvironmentVariables["DYLD_INSERT_LIBRARIES"] = $"{macGuardPath}:{existingDyldInsert}";
            }
        }

        bool isWindows = isWindowsOverride ?? OperatingSystem.IsWindows();
        if (!isWindows)
        {
            return;
        }

        ApplyWindowsHighDpiEnvironment(startInfo, setProcessEnv: null, msg => Log(LogCategory.Executable, msg));
        startInfo.EnvironmentVariables["SHIM_MCCOMPAT"] = "0x800000001";

        string spatialAudioEnv = EnableSpatialAudio ? "1" : "0";
        startInfo.EnvironmentVariables["KONGOR_AUDIO_HRTF"] = spatialAudioEnv;
        startInfo.EnvironmentVariables["KONGOR_AUDIO_WASAPI"] = spatialAudioEnv;
        if (SelectedMonitor is { } targetMon && !string.IsNullOrWhiteSpace(targetMon.NormalizedDeviceToken)) startInfo.EnvironmentVariables["KONGOR_TARGET_MONITOR"] = targetMon.NormalizedDeviceToken;
        startInfo.EnvironmentVariables["KONGOR_MANUAL_FPS_OVERRIDE"] = (ManualRefreshRateOverrideEnabled && int.TryParse(ManualMaxFpsText, out int envM) && envM >= 0 ? (envM == 0 ? 1000 : envM) : 0).ToString();

        bool graphicsCrash = !string.IsNullOrEmpty(_activeTriageResult?.FaultingModule) &&
            (_activeTriageResult.FaultingModule.Contains("vid_vulkan", StringComparison.OrdinalIgnoreCase) ||
             _activeTriageResult.FaultingModule.Contains("vulkan", StringComparison.OrdinalIgnoreCase) ||
             _activeTriageResult.FaultingModule.Contains("d3d9", StringComparison.OrdinalIgnoreCase) ||
             _activeTriageResult.FaultingModule.Contains("dxvk", StringComparison.OrdinalIgnoreCase));

        bool forceNativeD3d9 = _requiresNativeD3D9Fallback ||
                               (isSafeMode && graphicsCrash) ||
                               SelectedSwapchainBackend.Contains("Compatibility", StringComparison.OrdinalIgnoreCase);

        bool allowExperimentalD3d12 = SelectedSwapchainBackend.Contains("D3D9On12", StringComparison.OrdinalIgnoreCase) && IsExperimentalD3D12Enabled();
        if (SelectedSwapchainBackend.Contains("D3D9On12", StringComparison.OrdinalIgnoreCase) && !allowExperimentalD3d12)
        {
            Log(LogCategory.General, "[WILLOWMAKER] Experimental D3D12 swapchain requested without KONGOR_EXPERIMENTAL_D3D12=1; falling back to production DXVK/Vulkan WSI.");
        }

        if (allowExperimentalD3d12 || forceNativeD3d9)
        {
            startInfo.EnvironmentVariables.Remove("VK_INSTANCE_LAYERS");
            startInfo.EnvironmentVariables.Remove("VK_LAYER_PATH");
            startInfo.EnvironmentVariables.Remove("VK_ADD_LAYER_PATH");
            startInfo.EnvironmentVariables.Remove("HON_CAPTURE_ENABLE");
            startInfo.EnvironmentVariables.Remove("HON_MODERN_HUD");
            startInfo.EnvironmentVariables.Remove("VK_LOADER_LAYERS_DISABLE");

            if (forceNativeD3d9)
            {
                startInfo.EnvironmentVariables["HON_FORCE_NATIVE_D3D9"] = "1";
                Log(LogCategory.General,
                    $"[WILLOWMAKER] Configured Native D3D9 Compatibility Backend on child process (HON_FORCE_NATIVE_D3D9=1, AutoFallback={_requiresNativeD3D9Fallback})");
            }
            else
            {
                startInfo.EnvironmentVariables.Remove("HON_FORCE_NATIVE_D3D9");
                Log(LogCategory.General,
                    "[WILLOWMAKER] Configured Direct3D 12 Swapchain Backend on child process (Vulkan Layer interception disabled)");
            }
        }
        else
        {
            startInfo.EnvironmentVariables.Remove("HON_FORCE_NATIVE_D3D9");

            bool injectVulkanLayer = ShouldInjectVulkanCaptureLayer(SelectedVulkanHudMode, captureEnabled: false);
            if (injectVulkanLayer)
            {
                string existingLayerPath = Environment.GetEnvironmentVariable("VK_LAYER_PATH") ?? string.Empty;
                string layerPathStr = string.IsNullOrEmpty(existingLayerPath)
                    ? clientDir
                    : $"{clientDir};{existingLayerPath}";

                startInfo.EnvironmentVariables["VK_LAYER_PATH"] = layerPathStr;
                startInfo.EnvironmentVariables["VK_ADD_LAYER_PATH"] = layerPathStr;
                startInfo.EnvironmentVariables["VK_INSTANCE_LAYERS"] = "VK_LAYER_HON_replay_capture";
                startInfo.EnvironmentVariables["HON_CAPTURE_ENABLE"] = "0";
                startInfo.EnvironmentVariables["HON_CAPTURE_SLOTS"] = "3";
                startInfo.EnvironmentVariables["HON_MODERN_HUD"] = "1";
                startInfo.EnvironmentVariables.Remove("HON_CAPTURE_DISABLE");

                Log(LogCategory.General,
                    $"[WILLOWMAKER] Configured child process Vulkan Swapchain Layer: VK_INSTANCE_LAYERS='VK_LAYER_HON_replay_capture', HON_MODERN_HUD=1 (HudMode='{SelectedVulkanHudMode}'), ThirdPartyOverlays={AllowThirdPartyVulkanOverlays}");
            }
            else
            {
                startInfo.EnvironmentVariables.Remove("VK_INSTANCE_LAYERS");
                startInfo.EnvironmentVariables.Remove("VK_LAYER_PATH");
                startInfo.EnvironmentVariables.Remove("VK_ADD_LAYER_PATH");
                startInfo.EnvironmentVariables.Remove("VK_LOADER_LAYERS_ENABLE");
                startInfo.EnvironmentVariables.Remove("HON_CAPTURE_ENABLE");
                startInfo.EnvironmentVariables.Remove("HON_CAPTURE_SLOTS");
                startInfo.EnvironmentVariables.Remove("HON_MODERN_HUD");
                startInfo.EnvironmentVariables["HON_CAPTURE_DISABLE"] = "1";

                Log(LogCategory.General,
                    $"[WILLOWMAKER] Pure DirectFlip Vulkan path active (disabled VK_LAYER_HON_replay_capture via HON_CAPTURE_DISABLE=1 for HudMode='{SelectedVulkanHudMode}'; DXVK native HUD active, ThirdPartyOverlays={AllowThirdPartyVulkanOverlays})");
            }

            string? overlayDisableMask = VulkanImplicitLayerShield.BuildLoaderLayersDisableMask(
                injectKongorReplayCaptureLayer: injectVulkanLayer,
                allowThirdPartyVulkanOverlays: AllowThirdPartyVulkanOverlays);
            if (!string.IsNullOrEmpty(overlayDisableMask))
            {
                startInfo.EnvironmentVariables["VK_LOADER_LAYERS_DISABLE"] = overlayDisableMask;
            }
            else
            {
                startInfo.EnvironmentVariables.Remove("VK_LOADER_LAYERS_DISABLE");
            }

            VulkanImplicitLayerShieldPlan shieldPlan = VulkanImplicitLayerShield.DiscoverAndBuildPlan(
                allowThirdPartyVulkanOverlays: AllowThirdPartyVulkanOverlays,
                log: msg => Log(LogCategory.General, msg));
            VulkanImplicitLayerShield.ApplyToEnvironment(startInfo.EnvironmentVariables, shieldPlan);
            Log(LogCategory.General,
                $"[WILLOWMAKER][VULKAN_SHIELD] {VulkanImplicitLayerShield.FormatDiagnosticSummary(shieldPlan)}");
        }

        string canonicalSettingsDir = Path.Combine(clientDir, "userdataKONGOR");
        LauncherSettingsModel launcherSettings = File.Exists(LauncherSettingsService.GetSettingsFilePath(canonicalSettingsDir))
            ? LauncherSettingsService.Load(canonicalSettingsDir)
            : LauncherSettingsService.Load(clientDir);
        bool injectRcasLayer = ShouldInjectVulkanCaptureLayer(SelectedVulkanHudMode, captureEnabled: false);
        if (injectRcasLayer && launcherSettings.EnableFidelityFxRcas)
        {
            string sharpness = launcherSettings.FidelityFxRcasSharpness.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);
            startInfo.EnvironmentVariables["HON_FSR_ENABLE"] = "1";
            startInfo.EnvironmentVariables["HON_FSR_SHARPNESS"] = sharpness;
        }
        else
        {
            startInfo.EnvironmentVariables["HON_FSR_ENABLE"] = "0";
        }
    }
}
