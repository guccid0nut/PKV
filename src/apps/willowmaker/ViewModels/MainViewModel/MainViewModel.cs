// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2022-2026 [K]ONGOR and Contributors
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV) -- Upstream lineage: https://github.com/Project-KONGOR-Open-Source

namespace WILLOWMAKER.Core.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private readonly SynchronizationContext? _uiSyncContext = SynchronizationContext.Current;
    private readonly LiveTelemetryService? _liveTelemetryService;
    private readonly ClientNetcodeRelaySessionManager _netcodeRelaySessionManager = new();

    public ClientNetcodeRelaySessionManager NetcodeRelaySessionManager => _netcodeRelaySessionManager;

    public ConnectivityProbe MasterServerProbe { get; }
    public ConnectivityProbe CDNProbe { get; }
    public bool IsKongorNetEnabled => EndpointPairRegistry.IsKongorNetPkvEnabled;

    private void RunOnUiThread(Action action, SynchronizationContext? preferredContext = null)
    {
        SynchronizationContext? targetCtx = preferredContext ?? _uiSyncContext;
        if (targetCtx is not null && SynchronizationContext.Current != targetCtx)
        {
            targetCtx.Post(_ => action(), null);
            return;
        }

        if (Avalonia.Application.Current is not null && !Avalonia.Threading.Dispatcher.UIThread.CheckAccess())
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(action);
            return;
        }

        action();
    }

    public MainViewModel()
    {
        MasterServerProbe = new ConnectivityProbe("Master Server", msg => Log(LogCategory.General, msg));
        CDNProbe = new ConnectivityProbe(
            "CDN",
            msg => Log(LogCategory.Synchronise, msg),
            handlerFactory: () => CdnHttpHandlerOverride,
            fallbackUrlResolver: failedUrl =>
                failedUrl.Contains("cdn.newbs.io", StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrWhiteSpace(SecondaryEmergencyCdnBaseUrl)
                    ? AddressValidation.NormaliseCDNURL(SecondaryEmergencyCdnBaseUrl)
                    : null,
            disposeCustomHandler: false);
        CDNProbe.ProbeCompleted += (probe, succeeded) =>
        {
            RunOnUiThread(() =>
            {
                if (LaunchIsInProgress)
                    return;

                if (succeeded)
                {
                    _ = InspectCdnUpdateAsync(silent: true);
                }
                else
                {
                    ResetCdnUpdateStateToOffline();
                }
            });
        };

        ModManager = new ModManagerViewModel(Log, () =>
        {
            IsLiveLogsOpen = true;
            LiveLogs.SelectedCategoryFilter = "MODS";
        });
        string? relayEnv = Environment.GetEnvironmentVariable("KONGOR_ENABLE_CLIENT_ZSTD_RELAY");
        if (string.Equals(relayEnv, "1", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(relayEnv, "true", StringComparison.OrdinalIgnoreCase))
        {
            _netcodeRelaySessionManager.EnableClientZstdRelay = true;
        }
        DirectPlay = new DirectPlayViewModel(
            args => LaunchGameClientCore(true, args),
            _netcodeRelaySessionManager,
            msg => Log(LogCategory.Executable, msg));
        LoadLauncherSettings();
        RefreshVoiceDevices();

        // Dynamically extract Kongor character model DDS asset from client archives on boot (0 repo files)
        string clientRoot = LocationGuard.ResolveGameClientDirectory() ?? Directory.GetCurrentDirectory();

        CheckColdBootCrashSentinel(clientRoot);
        BuildManifestDto? buildManifest = PlaytestPolicyService.TryReadBuildManifest(
            clientRoot,
            msg => Log(LogCategory.Version, msg));
        if (buildManifest is not null && !string.IsNullOrWhiteSpace(buildManifest.BuildId))
        {
            VersionDisplay = $"{VersionChecker.CurrentVersionDisplay} • {buildManifest.BuildId}";
            DirectPlay.BuildStamp = buildManifest.BuildId;
        }
        string? cacheFilePath = KongorModelExtractor.ExtractKongorModelToCacheFile(clientRoot);
        if (cacheFilePath != null && File.Exists(cacheFilePath))
            try
            {
                KongorTauntBitmap = new Bitmap(cacheFilePath);
                Log(LogCategory.General,
                    $"[KONGOR MODEL] Extracted client character model asset directly into '{cacheFilePath}'.");
            }
            catch (Exception ex)
            {
                Log(LogCategory.General, $"[KONGOR MODEL ERROR] Failed loading bitmap from cache: {ex.Message}");
            }

        string configuredAddress = BuildMasterServerAddress();
        Log(LogCategory.Parameters,
            $"-masterserver {configuredAddress} -webserver {configuredAddress} -messageserver {configuredAddress}");

        // Initialize Live Telemetry Service
        var uiSyncCtx = System.Threading.SynchronizationContext.Current;
        _liveTelemetryService = new LiveTelemetryService(msg => Log(LogCategory.General, msg));
        _liveTelemetryService.OnTelemetryUpdated += data =>
        {
            void ApplyTelemetrySnapshot()
            {
                if (IsOfflineModeSelection(TryGetMasterServerComboText() ?? LoadedMasterServerAddress))
                    return;

                LiveOnlinePlayers = data.OnlinePlayers;
                LiveMatchesCount = data.MatchesInProgress;
                switch (data.ServerStatus)
                {
                    case MasterServerStatus.Active:
                        ServerStatusText = "Active";
                        ServerStatusColor = "#22c55e"; // Vivid green
                        break;
                    case MasterServerStatus.Connecting:
                        ServerStatusText = "Connecting...";
                        ServerStatusColor = "#fbbf24"; // Amber/Yellow
                        break;
                    case MasterServerStatus.Offline:
                    default:
                        ServerStatusText = "Offline";
                        ServerStatusColor = "#ef4444"; // Vivid red
                        break;
                }

                if (data.TopWinratePlayers.Count > 0)
                {
                    HasTopWinrateTicker = true;
                    StringBuilder sb = new();
                    sb.Append("🔥 Top Winrate: ");
                    for (int i = 0; i < Math.Min(data.TopWinratePlayers.Count, 5); i++)
                    {
                        LeaderboardPlayerDto p = data.TopWinratePlayers[i];
                        sb.Append($"#{p.Rank} {p.AccountName} ({p.WinRate:F1}%) ");
                        if (i < Math.Min(data.TopWinratePlayers.Count, 5) - 1) sb.Append("• ");
                    }

                    LeaderboardTickerText = sb.ToString();
                }
                else
                {
                    HasTopWinrateTicker = false;
                    LeaderboardTickerText = string.Empty;
                }
            }

            if (uiSyncCtx != null && System.Threading.SynchronizationContext.Current != uiSyncCtx)
            {
                uiSyncCtx.Post(_ => ApplyTelemetrySnapshot(), null);
            }
            else if (Avalonia.Application.Current != null && !Avalonia.Threading.Dispatcher.UIThread.CheckAccess())
            {
                Avalonia.Threading.Dispatcher.UIThread.Post(ApplyTelemetrySnapshot);
            }
            else
            {
                ApplyTelemetrySnapshot();
            }
        };
        RefreshTelemetryPollingForSelection();
    }

    [ObservableProperty]
    public partial string? GitHubLink { get; set; } = "https://github.com/Project-KONGOR-Open-Source";

    [ObservableProperty] public partial string? WebPortalLink { get; set; } = "https://kongor.net";

    [ObservableProperty] public partial string? RedditLink { get; set; } = "https://www.reddit.com/r/newerth";

    [ObservableProperty] public partial string? DiscordLink { get; set; } = "https://discord.com/invite/N6pKzGDqUH";

    [ObservableProperty]
    public partial string? ElementLink { get; set; } = "https://app.element.io/#/room/#newerth:matrix.org";

    [ObservableProperty] public partial string VersionDisplay { get; set; } = VersionChecker.CurrentVersionDisplay;

    [ObservableProperty] public partial bool PureCompatibilityMode { get; set; } = false;
    [ObservableProperty] public partial bool AutoRestartMidMatchCrash { get; set; } = false;
    [ObservableProperty] public partial bool DiscordStreamerPrivacyMode { get; set; } = false;
    [ObservableProperty] public partial string DiscordPresencePreviewText { get; set; } = string.Empty;

    [ObservableProperty] public partial LiveLogsViewModel LiveLogs { get; set; } = new();

    [ObservableProperty] public partial bool IsLiveLogsOpen { get; set; } = false;

    [ObservableProperty] public partial ReplayConverterViewModel ReplayConverter { get; set; } = new();

    [ObservableProperty] public partial ModManagerViewModel ModManager { get; set; } = new();

    [ObservableProperty] public partial DirectPlayViewModel DirectPlay { get; set; }

    [ObservableProperty] public partial bool IsDirectPlayOpen { get; set; } = false;

    [ObservableProperty] public partial bool IsModManagerOpen { get; set; } = false;

    [ObservableProperty] public partial bool IsAdvancedOptionsOpen { get; set; } = false;

    public string AdvancedOptionsButtonText => IsAdvancedOptionsOpen ? "Hide Advanced Settings ▲" : "Advanced Engine, Audio & CDN Settings ▼";

    partial void OnIsAdvancedOptionsOpenChanged(bool value)
    {
        OnPropertyChanged(nameof(AdvancedOptionsButtonText));
    }

    [ObservableProperty] public partial Bitmap? KongorTauntBitmap { get; set; } = null;

    [ObservableProperty] public partial string VulkanStatusText { get; set; } = "Vulkan 1.3";

    [ObservableProperty] public partial string VulkanStatusColor { get; set; } = "#10b981";

    [ObservableProperty] public partial bool VulkanUpgradeRequired { get; set; } = false;

    /// <summary>
    ///     Callback wired by <see cref="ClientLauncher" /> to restore and focus the WILLOWMAKER desktop window
    ///     when requested over local Cockpit IPC by NEXUS.ControlPanel.
    /// </summary>
    public Func<bool>? RequestBringToFront { get; set; }

    /// <summary>
    ///     Invoked by <see cref="ClientLauncher" /> from the MainWindow's <see cref="Window.Opened" /> event once the window
    ///     is realised and able to host a modal dialog.
    /// </summary>
    internal void OnMainWindowOpened()
    {
        _ = OnMainWindowOpenedAsync();
    }

    private async Task OnMainWindowOpenedAsync()
    {
        await EnsureCockpitIpcServerAsync();

        if (await CheckLocationGuard() is false)
            return;

        RefreshGraphicsDriverStatus();

        UpdateStatus = UpdateStatus.ApplicationUpToDate;
        Log(LogCategory.Version, "Manual update mode active. Click 'Check for Updates' to query the CDN for updates.");
    }

    [RelayCommand]
    private void GoToURL(string url)
    {
        OpenExternalUrl(url);
    }

    public void OpenExternalUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return;
        }

        string trimmed = url.Trim();
        if (!trimmed.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !trimmed.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            Log(LogCategory.General, $"[URL_OPENER] Ignored non-HTTP(S) URL '{trimmed}'.");
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo { FileName = trimmed, UseShellExecute = true });
            Log(LogCategory.General, $"[URL_OPENER] Opened external URL '{trimmed}'.");
            return;
        }
        catch (Exception shellEx)
        {
            Log(LogCategory.General, $"[URL_OPENER] Shell URL open notice ('{trimmed}'): {shellEx.Message}. Trying POSIX fallback...");
        }

        if (OperatingSystem.IsLinux())
        {
            (string FileName, string[] Args)[] openers =
            [
                ("xdg-open", [trimmed]),
                ("gio", ["open", trimmed])
            ];

            foreach ((string fileName, string[] args) in openers)
            {
                try
                {
                    ProcessStartInfo psi = new()
                    {
                        FileName = fileName,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    };
                    foreach (string arg in args)
                    {
                        psi.ArgumentList.Add(arg);
                    }

                    Process.Start(psi);
                    Log(LogCategory.General, $"[URL_OPENER] Opened '{trimmed}' via '{fileName}'.");
                    return;
                }
                catch (Exception fallbackEx)
                {
                    Log(LogCategory.General, $"[URL_OPENER] Fallback '{fileName}' unavailable: {fallbackEx.Message}");
                }
            }
        }
        else if (OperatingSystem.IsMacOS())
        {
            try
            {
                ProcessStartInfo psi = new()
                {
                    FileName = "open",
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                psi.ArgumentList.Add(trimmed);
                Process.Start(psi);
                Log(LogCategory.General, $"[URL_OPENER] Opened '{trimmed}' via macOS 'open'.");
                return;
            }
            catch (Exception macEx)
            {
                Log(LogCategory.General, $"[URL_OPENER] macOS 'open' fallback failed: {macEx.Message}");
            }
        }
    }

    [RelayCommand]
    public void ToggleLiveLogs()
    {
        IsLiveLogsOpen = !IsLiveLogsOpen;
    }

    [RelayCommand]
    public void CloseLiveLogs()
    {
        IsLiveLogsOpen = false;
    }

    [RelayCommand]
    public void ToggleDirectPlay()
    {
        IsDirectPlayOpen = !IsDirectPlayOpen;
        if (IsDirectPlayOpen) _ = DirectPlay.RefreshIpAsync();
    }

    [RelayCommand]
    public void CloseDirectPlay()
    {
        IsDirectPlayOpen = false;
    }

    [RelayCommand]
    public void ToggleModManager()
    {
        IsModManagerOpen = !IsModManagerOpen;
        Log(LogCategory.General, $"[ModManager] Overlay toggled -> {(IsModManagerOpen ? "Open" : "Closed")}.");
    }

    [RelayCommand]
    public void CloseModManager()
    {
        IsModManagerOpen = false;
    }

    [RelayCommand]
    public void ToggleAdvancedOptions()
    {
        IsAdvancedOptionsOpen = !IsAdvancedOptionsOpen;
        Log(LogCategory.General, $"[MainView] Advanced settings drawer -> {(IsAdvancedOptionsOpen ? "Expanded" : "Collapsed")}.");
    }

    [RelayCommand]
    public void ClickKongorModel()
    {
        string clientRoot = LocationGuard.ResolveGameClientDirectory() ?? Directory.GetCurrentDirectory();

        KongorAudioService.PlayKongorTauntSound(clientRoot);
        Log(LogCategory.General, "[KONGOR AUDIO] Played client Kongor sound effect on model click.");
    }

    public void RefreshGraphicsDriverStatus(string? clientDirectory = null)
    {
        try
        {
            string? clientDir = clientDirectory ?? LocationGuard.ResolveGameClientDirectory() ??
                                Path.Combine(AppContext.BaseDirectory, "games", "client");
            if (!string.IsNullOrEmpty(clientDir) && Directory.Exists(clientDir))
            {
                ClientGraphicsDriverReport report = VulkanDriverUpgradeService.InspectClient(clientDir);
                VulkanUpgradeRequired = report.RequiresUpgrade;
                VulkanStatusText = report.StatusSummary;
                VulkanStatusColor = report.RequiresUpgrade ? "#f59e0b" : "#10b981";
            }
        }
        catch (Exception ex)
        {
            Log(LogCategory.General, $"[VULKAN STATUS] Failed to inspect driver status: {ex.Message}");
        }
    }

    [RelayCommand]
    public async Task UpgradeToVulkan()
    {
        string? clientDir = LocationGuard.ResolveGameClientDirectory() ??
                            Path.Combine(AppContext.BaseDirectory, "games", "client");
        if (string.IsNullOrEmpty(clientDir) || !Directory.Exists(clientDir))
        {
            Log(LogCategory.General, "[VULKAN] Cannot upgrade: game client directory was not found.");
            return;
        }

        Log(LogCategory.General, "[VULKAN] Starting manual Vulkan 1.3 / DXVK driver modernization...");
        RayTracingPreset preset = RayTracingPreset.Competitive;
        if (EnableRayTracing)
            preset = SelectedRayTracingPreset.StartsWith("Ultra", StringComparison.OrdinalIgnoreCase)
                ? RayTracingPreset.Ultra
                : RayTracingPreset.Balanced;

        VulkanUpgradeResult result = await VulkanDriverUpgradeService.UpgradeToVulkanAsync(
            clientDir, preset, msg => Log(LogCategory.General, msg));
        RefreshGraphicsDriverStatus(clientDir);
    }

    private int _backgroundDisposed;
    private int _shutdownInitiated;

    /// <summary>
    ///     Optional test override for clean launcher process exit so unit tests can assert shutdown
    ///     behavior without terminating the xUnit host process.
    /// </summary>
    public Action<int>? ExitProcessOverride { get; set; }

    public void DisposeApplicationBackgroundServices()
    {
        if (Interlocked.Exchange(ref _backgroundDisposed, 1) != 0)
        {
            return;
        }

        try
        {
            MasterServerProbe.CancelPending();
        }
        catch (Exception ex)
        {
            Log(LogCategory.General, $"[SHUTDOWN] MasterServerProbe cancel notice: {ex.Message}");
        }

        try
        {
            CDNProbe.CancelPending();
        }
        catch (Exception ex)
        {
            Log(LogCategory.General, $"[SHUTDOWN] CDNProbe cancel notice: {ex.Message}");
        }

        try
        {
            _liveTelemetryService?.Dispose();
            _netcodeRelaySessionManager.Dispose();
        }
        catch (Exception ex)
        {
            Log(LogCategory.General, $"[SHUTDOWN] LiveTelemetryService dispose notice: {ex.Message}");
        }

        try
        {
            if (IsContinuousAudioPlaying)
            {
                IsContinuousAudioPlaying = false;
                AudioModernizationService audioService = new(Microsoft.Extensions.Logging.Abstractions.NullLogger<AudioModernizationService>.Instance);
                audioService.StopAudition();
            }
        }
        catch (Exception ex)
        {
            Log(LogCategory.General, $"[SHUTDOWN] Spatial audio audition stop notice: {ex.Message}");
        }

        CancellationTokenSource? postBootAuditCts = Interlocked.Exchange(ref _activePostBootAuditCts, null);
        if (postBootAuditCts is not null)
        {
            try
            {
                postBootAuditCts.Cancel();
                postBootAuditCts.Dispose();
            }
            catch (Exception ex)
            {
                Log(LogCategory.General, $"[SHUTDOWN] Post-boot audit CTS dispose notice: {ex.Message}");
            }
        }

        DisposeVoiceClient();
        StopCockpitIpcOnApplicationShutdown();
    }

    public void ShutdownLauncherCleanly(int exitCode)
    {
        if (Interlocked.Exchange(ref _shutdownInitiated, 1) != 0)
        {
            return;
        }

        Log(LogCategory.Executable, $"[SHUTDOWN] Initiating clean WILLOWMAKER shutdown (exitCode={exitCode})...");

        try
        {
            SaveLauncherSettings();
        }
        catch (Exception ex)
        {
            Log(LogCategory.Executable, $"[SHUTDOWN] SaveLauncherSettings notice: {ex.Message}");
        }

        DisposeApplicationBackgroundServices();
        Log(LogCategory.Executable, $"[SHUTDOWN] Background services stopped; flushing WILLOWMAKER.log and exiting (exitCode={exitCode}).");

        try
        {
            Logger.Dispose();
        }
        catch
        {
            // Ignore logger flush exceptions during final process termination
        }

        if (ExitProcessOverride is not null)
        {
            ExitProcessOverride(exitCode);
            return;
        }

        // Arm fail-safe background exit watchdog so window teardown can never hang the process
        _ = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromMilliseconds(1200)).ConfigureAwait(false);
            Environment.Exit(exitCode);
        });

        try
        {
            if (Avalonia.Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop)
            {
                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    try
                    {
                        desktop.MainWindow?.Hide();
                        desktop.Shutdown(exitCode);
                    }
                    catch
                    {
                        Environment.Exit(exitCode);
                    }
                });
            }
        }
        catch
        {
            // Fall through to immediate Environment.Exit below
        }

        Environment.Exit(exitCode);
    }
}
