// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kongor.Cli.Mcp.Services;
using Kongor.Core.Configuration;
using NEXUS.ControlPanel.GUI.Services;
using NEXUS.ControlPanel.GUI.Services.Models;

namespace NEXUS.ControlPanel.GUI.ViewModels;

public sealed partial class DashboardViewModel : ViewModelBase, IDisposable
{
    private readonly IDatabaseHealthService _databaseHealth;
    private readonly IDockerHealthService _dockerHealth;

    private readonly CancellationTokenSource _healthCts = new();
    private readonly INexusOrchestratorService _orchestrator;
    private readonly ISecretsManagerService? _secretsManager;
    private readonly Action? _onNavigateToGameServers;
    private readonly Action? _onNavigateToHostedServers;
    private readonly Action? _onLaunchDockerServer;
    private readonly Action? _onLaunchK3sAgonesServer;
    private readonly Action? _onLaunchDockerQueueArcade;
    private readonly Action? _onLaunchK3sQueueArcade;
    private readonly Action? _onOpenServerChatPopOut;
    private readonly Func<IReadOnlyList<CockpitServerPipeHint>>? _getServerPipeHints;
    private readonly Func<Task>? _onSecretsRotatedExternal;
    private readonly DockerStackScriptRunner _stackRunner;
    private readonly Lock _stackScriptLock = new();
    private CancellationTokenSource? _activeStackScriptCts;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanRestartCluster))]
    private bool _isDockerStackBusy;

    [ObservableProperty] private string _dockerStackButtonLabel = "🐳 Docker Stack";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStaleImageWarning))]
    private string _staleImageWarningText = string.Empty;

    public bool HasStaleImageWarning => !string.IsNullOrWhiteSpace(StaleImageWarningText);
    private readonly BundledLayout? _bundledLayout;

    public bool IsBundledLayout => _bundledLayout is not null;

    public string ClusterSubtitleText => IsBundledLayout
        ? "Standalone Self-Hosting Stack — Local PostgreSQL 17, Garnet Cache, Master/Chat/Portal & COMPEL Fleet."
        : "Orchestrated by .NET Aspire with Supabase Cloud DB, Redis Cache, and COMPEL Game Server support.";

    public string WebPortalButtonLabel => IsBundledLayout ? "🌐 Web Portal (5266)" : "🌐 Web Portal (5557)";
    public string WebPortalApiButtonLabel => IsBundledLayout ? "🔑 Web Portal API Swagger (5200)" : "🔑 Web Portal API Swagger (5201)";

    // Hosting Mode Options & State
    public ObservableCollection<string> HostingModeOptions { get; } = ["Local only", "LAN / Tailscale", "Public Internet"];
    public ObservableCollection<string> AvailableLanIps { get; } = [];

    [ObservableProperty] private string _selectedHostingMode = "Local only";
    [ObservableProperty] private string _selectedLanIp = string.Empty;
    [ObservableProperty] private string _wanHost = string.Empty;
    [ObservableProperty] private bool _wanWarningAcknowledged;
    [ObservableProperty] private bool _isWanWarningDialogOpen;
    [ObservableProperty] private string _hostingModeRestartHint = string.Empty;
    [ObservableProperty] private string _copyFeedbackMessage = string.Empty;
    [ObservableProperty] private string _friendJoinInstructionsPreview = string.Empty;

    public bool IsLanMode => string.Equals(SelectedHostingMode, "LAN / Tailscale", StringComparison.Ordinal);
    public bool IsWanMode => string.Equals(SelectedHostingMode, "Public Internet", StringComparison.Ordinal);
    public bool ShowLanSelector => IsLanMode || IsWanMode;
    public bool HasHostingModeRestartHint => !string.IsNullOrWhiteSpace(HostingModeRestartHint);
    public bool HasCopyFeedbackMessage => !string.IsNullOrWhiteSpace(CopyFeedbackMessage);

    public Task InitialHydrationTask { get; }

    private readonly SemaphoreSlim _persistHostingLock = new(1, 1);
    private string _previousHostingMode = "Local only";
    private bool _suppressHostingSave;
    private bool _hostingSettingsHydrated;

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(CanRestartCluster))] private ServerState _clusterState = ServerState.Stopped;

    [ObservableProperty] private string _clusterStatusColor = "#EF4444"; // Red

    [ObservableProperty] private string _clusterStatusText = "STOPPED";

    [ObservableProperty] private string _databaseLabel = "⚡ Supabase:";

    [ObservableProperty] private string _databaseStatusColor = "#64748B"; // Gray

    [ObservableProperty] private string _databaseStatusMessage = "Checking Database...";

    [ObservableProperty] private string _discordDevPortalUrl = string.Empty;

    [ObservableProperty] private string _dockerStatusColor = "#64748B"; // Gray

    [ObservableProperty] private string _dockerStatusMessage = "Checking Docker...";

    [ObservableProperty] private string _grafanaDashboardUrl = string.Empty;

    [ObservableProperty] private bool _hasDiscordConfigured;

    [ObservableProperty] private bool _hasGrafanaConfigured;

    [ObservableProperty] private bool _hasPyroscopeConfigured;

    [ObservableProperty] private bool _hasSupabaseConfigured;

    [ObservableProperty] private bool _hasTwitchConfigured;

    [ObservableProperty] private bool _isDatabaseOnline;

    [ObservableProperty] private bool _isDockerOnline;

    [ObservableProperty] private bool _isPyroscopeOnline;

    [ObservableProperty] private bool _isRedisOnline;

    [ObservableProperty] private string _pyroscopeDashboardUrl = string.Empty;

    [ObservableProperty] private string _pyroscopeStatusColor = "#64748B"; // Gray

    [ObservableProperty] private string _pyroscopeStatusMessage = "Checking Pyroscope...";

    [ObservableProperty] private string _redisStatusColor = "#64748B"; // Gray

    [ObservableProperty] private string _redisStatusMessage = "Checking Redis...";

    [ObservableProperty] private string _supabaseDashboardUrl = string.Empty;

    [ObservableProperty] private string _twitchDevConsoleUrl = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(QueueArcadeToggleMenuHeader))]
    [NotifyPropertyChangedFor(nameof(QueueArcadeButtonLabel))]
    private bool _isQueueArcadeEnabled = true;

    [ObservableProperty] private string _queueArcadeStatusSummary = "Enabled";

    public string QueueArcadeToggleMenuHeader => IsQueueArcadeEnabled
        ? "✅ Queue Arcade: Enabled (Click to Pause)"
        : "⏸️ Queue Arcade: Paused (Click to Enable)";

    public string QueueArcadeButtonLabel => IsQueueArcadeEnabled
        ? "🕹️ Queue Arcade"
        : "🕹️ Queue Arcade (Paused)";

    public DashboardViewModel(
        INexusOrchestratorService orchestrator,
        IDockerHealthService dockerHealth,
        IDatabaseHealthService? databaseHealth = null,
        ISecretsManagerService? secretsManager = null,
        Action? onNavigateToGameServers = null,
        Action? onNavigateToHostedServers = null,
        Action? onLaunchDockerServer = null,
        Action? onLaunchK3sAgonesServer = null,
        string? bundleProbeDirectory = null,
        Action? onLaunchDockerQueueArcade = null,
        Action? onLaunchK3sQueueArcade = null,
        ICockpitAppBridgeService? cockpitBridge = null,
        Func<Task>? onSecretsRotatedExternal = null,
        DockerStackScriptRunner? stackRunner = null,
        Action? onOpenServerChatPopOut = null,
        Func<IReadOnlyList<CockpitServerPipeHint>>? getServerPipeHints = null)
    {
        _orchestrator = orchestrator;
        _cockpitBridge = cockpitBridge ?? new CockpitAppBridgeService(orchestrator);
        _dockerHealth = dockerHealth;
        _databaseHealth = databaseHealth ?? new DatabaseHealthService();
        _secretsManager = secretsManager;
        _onNavigateToGameServers = onNavigateToGameServers;
        _onNavigateToHostedServers = onNavigateToHostedServers;
        _onLaunchDockerServer = onLaunchDockerServer;
        _onLaunchK3sAgonesServer = onLaunchK3sAgonesServer;
        _onLaunchDockerQueueArcade = onLaunchDockerQueueArcade;
        _onLaunchK3sQueueArcade = onLaunchK3sQueueArcade;
        _onOpenServerChatPopOut = onOpenServerChatPopOut;
        _getServerPipeHints = getServerPipeHints;
        _onSecretsRotatedExternal = onSecretsRotatedExternal;
        _stackRunner = stackRunner ?? DockerStackProbe.RunStackScriptAsync;

        BundledLayoutResolver.TryResolve(out _bundledLayout, bundleProbeDirectory);
        Console.Error.WriteLine($"[DashboardViewModel] BundledLayout resolved: {(_bundledLayout is not null ? _bundledLayout.Root : "false (Aspire Dev Mode)")}");

        _orchestrator.OnClusterStateChanged += UpdateClusterState;
        _orchestrator.OnServiceStateChanged += UpdateServiceState;

        _suppressHostingSave = true;
        try
        {
            RefreshNetworkInterfaces();
        }
        finally
        {
            _suppressHostingSave = false;
        }
        InitializeMicroservices();
        FriendJoinInstructionsPreview = BuildFriendJoinInstructions();
        UpdateClusterState(_orchestrator.CurrentClusterState);

        InitialHydrationTask = RefreshSecretsLinksAsync();
        StartPeriodicHealthCheck();
    }

    public ObservableCollection<ServiceItemViewModel> Microservices { get; } = [];

    public bool CanStartCluster => ClusterState is ServerState.Stopped or ServerState.Faulted;
    public bool CanStopCluster => ClusterState is ServerState.Running or ServerState.Starting;
    public bool CanRestartCluster => !IsDockerStackBusy && CanStopCluster;

    public void Dispose()
    {
        _orchestrator.OnClusterStateChanged -= UpdateClusterState;
        _orchestrator.OnServiceStateChanged -= UpdateServiceState;
        CancelActiveStackScript();
        _healthCts.Cancel();
        _healthCts.Dispose();
    }

    private void InitializeMicroservices()
    {
        Microservices.Add(new ServiceItemViewModel(
            _orchestrator,
            "master-server",
            "👑 KONGOR Master Server",
            "👑",
            IsBundledLayout ? "HTTP: 55555" : "HTTP: 55555 / 80",
            "SRP Auth, Store, Vault, Stats, Replays",
            "#F59E0B"));

        Microservices.Add(new ServiceItemViewModel(
            _orchestrator,
            "chat-server",
            "💬 TRANSMUTANSTEIN Chat",
            "💬",
            IsBundledLayout ? "TCP: 11031 / 11032 / HTTP: 5554" : "TCP: 11031 / 11032 / 11033",
            "Chat Channels, Matchmaking, COMPEL Hub",
            "#A855F7"));

        Microservices.Add(new ServiceItemViewModel(
            _orchestrator,
            "web-portal-api",
            "🛡️ ZORGATH Web Portal API",
            "🛡️",
            IsBundledLayout ? "HTTP: 127.0.0.1:5200 (Loopback)" : "HTTPS: 5201 / HTTP 5200",
            "JWT Authentication & User Accounts",
            "#3B82F6"));

        Microservices.Add(new ServiceItemViewModel(
            _orchestrator,
            "web-portal-ui",
            "🌐 DAWNBRINGER Web Portal",
            "🌐",
            IsBundledLayout ? "HTTP: 5266" : "HTTPS: 5557 / HTTP 5266",
            "Razor UI, Discord Login, 3D Hero Viewer",
            "#10B981"));
    }

    [RelayCommand]
    public async Task StartClusterAsync()
    {
        try
        {
            await _orchestrator.StartClusterAsync();
        }
        catch (Exception ex)
        {
            ClusterStatusText = $"ERROR: {ex.Message}";
            ClusterStatusColor = "#EF4444";
        }
    }

    [RelayCommand]
    public async Task LaunchLocalGameServersAsync()
    {
        try
        {
            if (ClusterState != ServerState.Running)
            {
                ClusterStatusText = "Starting Aspire cluster before launching local game servers...";
                ClusterStatusColor = "#F59E0B";
                await _orchestrator.StartClusterAsync(withGameServers: true);
            }
            else
            {
                ClusterStatusText = "Launching local game server fleet in background...";
                ClusterStatusColor = "#F59E0B";
                await _orchestrator.LaunchGameServerAsync();
            }

            ClusterStatusText = "Local game server fleet launched in background!";
            ClusterStatusColor = "#10B981";

            _onNavigateToGameServers?.Invoke();
        }
        catch (Exception ex)
        {
            ClusterStatusText = $"ERROR: {ex.Message}";
            ClusterStatusColor = "#EF4444";
        }
    }

    [RelayCommand]
    public void LaunchHostedGameServers()
    {
        ClusterStatusText = "Navigating to Cloud-Native Container Hub...";
        ClusterStatusColor = "#38BDF8";
        _onNavigateToHostedServers?.Invoke();
    }

    [RelayCommand]
    public void LaunchDockerGameServers()
    {
        ClusterStatusText = "Opening Docker Container Game Server Manager...";
        ClusterStatusColor = "#38BDF8";
        if (_onLaunchDockerServer != null)
        {
            _onLaunchDockerServer.Invoke();
        }
        else
        {
            _onNavigateToHostedServers?.Invoke();
        }
    }

    [RelayCommand]
    public void LaunchK3sAgonesGameServers()
    {
        ClusterStatusText = "Opening K3s + Agones Fleet Manager...";
        ClusterStatusColor = "#818CF8";
        if (_onLaunchK3sAgonesServer != null)
        {
            _onLaunchK3sAgonesServer.Invoke();
        }
        else
        {
            _onNavigateToHostedServers?.Invoke();
        }
    }

    [RelayCommand]
    public Task LaunchGameServersAsync() => LaunchLocalGameServersAsync();

    [RelayCommand]
    public async Task LaunchLocalQueueArcadeAsync()
    {
        try
        {
            if (ClusterState != ServerState.Running)
            {
                ClusterStatusText = "Starting cluster before launching Queue Arcade servers...";
                ClusterStatusColor = "#F59E0B";
                await _orchestrator.StartClusterAsync(withGameServers: false);
            }

            ClusterStatusText = "Launching Queue Arcade fleet in background (Port 11484, #Q1 warm + #Q2 overflow)...";
            ClusterStatusColor = "#F59E0B";
            await _orchestrator.LaunchQueueArcadeServerAsync();

            ClusterStatusText = "Queue Arcade fleet launched in background (Port 11484)!";
            ClusterStatusColor = "#10B981";
            _onNavigateToGameServers?.Invoke();
        }
        catch (Exception ex)
        {
            ClusterStatusText = $"ERROR: {ex.Message}";
            ClusterStatusColor = "#EF4444";
        }
    }

    [RelayCommand]
    public void LaunchDockerQueueArcade()
    {
        ClusterStatusText = "Opening Docker Queue Arcade Container Manager...";
        ClusterStatusColor = "#38BDF8";
        if (_onLaunchDockerQueueArcade != null)
        {
            _onLaunchDockerQueueArcade.Invoke();
        }
        else
        {
            _onNavigateToHostedServers?.Invoke();
        }
    }

    [RelayCommand]
    public void LaunchK3sAgonesQueueArcade()
    {
        ClusterStatusText = "Opening K3s + Agones Queue Arcade Fleet Manager...";
        ClusterStatusColor = "#818CF8";
        if (_onLaunchK3sQueueArcade != null)
        {
            _onLaunchK3sQueueArcade.Invoke();
        }
        else
        {
            _onNavigateToHostedServers?.Invoke();
        }
    }

    [RelayCommand]
    public async Task ToggleQueueArcadeEnabledAsync()
    {
        try
        {
            bool targetEnabled = !IsQueueArcadeEnabled;
            (bool enabled, bool liveSynced, string message) =
                await _orchestrator.SetQueueArcadeEnabledAsync(targetEnabled);

            IsQueueArcadeEnabled = enabled;
            QueueArcadeStatusSummary = liveSynced
                ? (enabled ? "Enabled (Live)" : "Paused (Live)")
                : (enabled ? "Enabled (Vault)" : "Paused (Vault)");
            ClusterStatusText = message;
            ClusterStatusColor = !liveSynced ? "#F59E0B" : (enabled ? "#10B981" : "#F59E0B");
        }
        catch (Exception ex)
        {
            ClusterStatusText = $"ERROR toggling Queue Arcade: {ex.Message}";
            ClusterStatusColor = "#EF4444";
        }
    }

    [RelayCommand]
    public async Task StopQueueArcadeServersAsync()
    {
        try
        {
            ClusterStatusText = "Stopping Queue Arcade servers...";
            ClusterStatusColor = "#F59E0B";
            await _orchestrator.StopQueueArcadeServersAsync();
            ClusterStatusText = "Queue Arcade servers stopped (Ranked game servers untouched).";
            ClusterStatusColor = "#10B981";
        }
        catch (Exception ex)
        {
            ClusterStatusText = $"ERROR stopping Queue Arcade: {ex.Message}";
            ClusterStatusColor = "#EF4444";
        }
    }

    [RelayCommand]
    public async Task LaunchGameClientAsync()
    {
        try
        {
            ClusterStatusText = "Launching game client...";
            ClusterStatusColor = "#F59E0B";
            await _orchestrator.LaunchGameClientAsync();
            ClusterStatusText = "Game client launched!";
            ClusterStatusColor = "#10B981";
        }
        catch (Exception ex)
        {
            ClusterStatusText = $"ERROR: {ex.Message}";
            ClusterStatusColor = "#EF4444";
        }
    }

    [RelayCommand]
    public async Task StopClusterAsync()
    {
        await _orchestrator.StopClusterAsync();
    }

    public static event Action? OnRequestAppExit;

    [RelayCommand]
    public async Task StopAllServicesAsync()
    {
        CancelActiveStackScript();
        if (OnRequestAppExit != null)
            OnRequestAppExit.Invoke();
        else
            await _orchestrator.StopAllAsync();
    }

    [RelayCommand]
    public async Task RestartClusterAsync()
    {
        if (IsDockerStackBusy || !CanStopCluster)
        {
            return;
        }

        IsDockerStackBusy = true;
        DockerStackButtonLabel = "🐳 Docker Stack (Restarting...)";
        ClusterStatusText = "🔄 Restarting Cluster / Docker Stack...";
        ClusterStatusColor = "#F59E0B";

        try
        {
            await _orchestrator.RestartClusterAsync();
            if (_orchestrator.CurrentClusterState == ServerState.Faulted)
            {
                string? lastErr = _orchestrator.LogHistory
                    .Reverse()
                    .FirstOrDefault(m => m.IsError)?
                    .Message;
                ClusterStatusText = !string.IsNullOrWhiteSpace(lastErr)
                    ? $"Restart failed: {lastErr}"
                    : "Cluster / Docker Stack restart failed — inspect Live Logs.";
                ClusterStatusColor = "#EF4444";
            }
            else if (_orchestrator.CurrentClusterState == ServerState.Running)
            {
                ClusterStatusText = "CLUSTER / DOCKER STACK RESTARTED & ONLINE";
                ClusterStatusColor = "#10B981";
            }
        }
        catch (Exception ex)
        {
            _orchestrator.AppendLog(new LogMessage
            {
                ServiceName = "DOCKER STACK",
                Message = $"Cluster / Docker Stack restart threw exception: {ex.Message}",
                Level = "ERROR",
                IsError = true,
                Timestamp = TimeProvider.System.GetUtcNow()
            });
            ClusterStatusText = $"ERROR (restart): {ex.Message}";
            ClusterStatusColor = "#EF4444";
        }
        finally
        {
            IsDockerStackBusy = false;
            DockerStackButtonLabel = "🐳 Docker Stack";
            await RefreshInfrastructureHealthAsync();
        }
    }

    private static string? TryExtractDockerStackProgressSummary(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return null;
        }

        const string marker = "[DOCKER-STACK]";
        int markerIdx = line.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (markerIdx < 0)
        {
            return null;
        }

        string tail = line[(markerIdx + marker.Length)..].Trim();
        if (tail.StartsWith('['))
        {
            int closeBracket = tail.IndexOf(']');
            if (closeBracket > 0 && closeBracket + 1 < tail.Length)
            {
                tail = tail[(closeBracket + 1)..].Trim();
            }
        }

        if (tail.StartsWith("Publishing ", StringComparison.OrdinalIgnoreCase))
        {
            int parenIdx = tail.IndexOf('(');
            string label = parenIdx > 0 ? tail[..parenIdx].Trim() : tail;
            return $"{label}...";
        }

        if (tail.StartsWith("Building image for service ", StringComparison.OrdinalIgnoreCase) ||
            tail.StartsWith("Starting Docker Compose stack", StringComparison.OrdinalIgnoreCase) ||
            tail.StartsWith("Waiting for ", StringComparison.OrdinalIgnoreCase) ||
            tail.StartsWith("Seeding shared Dedicated Server", StringComparison.OrdinalIgnoreCase) ||
            tail.StartsWith("[STAGE ", StringComparison.OrdinalIgnoreCase))
        {
            return tail.Length > 78 ? $"{tail[..75]}..." : tail;
        }

        return null;
    }

    [RelayCommand]
    public Task LaunchDockerStackBalancedAsync() =>
        RunDockerStackActionAsync("up", ["-Performance", "Balanced", "-SkipAutoRebuild", "-StopHostConflict"], "🐳 Launching Docker Stack (Balanced)...");

    [RelayCommand]
    public Task LaunchDockerStackEcoAsync() =>
        RunDockerStackActionAsync("up", ["-Performance", "Eco", "-SkipAutoRebuild", "-StopHostConflict"], "🌱 Launching Docker Stack (Eco)...");

    [RelayCommand]
    public Task LaunchDockerStackHighAsync() =>
        RunDockerStackActionAsync("up", ["-Performance", "High", "-SkipAutoRebuild", "-StopHostConflict"], "🔥 Launching Docker Stack (High)...");

    [RelayCommand]
    public Task LaunchDockerStackWithClientAsync() =>
        RunDockerStackActionAsync("up", ["-Performance", "Balanced", "-SkipAutoRebuild", "-LaunchClient", "-StopHostConflict"], "🎮 Launching Docker Stack (Balanced) + Game Client...");

    [RelayCommand]
    public Task RestartDockerStackAsync() =>
        RunDockerStackActionAsync("restart", null, "🔄 Restarting Docker Stack (Preserving Redis)...");

    [RelayCommand]
    public Task SyncDockerStackAsync()
    {
        string activePreset = DockerStackProbe.TryReadActivePerformancePreset(SecretsManagerService.FindSolutionRoot()) ?? "Balanced";
        return RunDockerStackActionAsync(
            "sync",
            ["-Performance", activePreset],
            $"🔨 Rebuilding & Syncing Docker Stack ({activePreset})...");
    }

    [RelayCommand]
    public Task CleanupLegacyDockerAsync() =>
        RunDockerStackActionAsync("cleanup", null, "🧹 Cleaning Up Legacy Docker Containers & Images...");

    [RelayCommand]
    public Task CheckDockerStackPreflightAsync() =>
        RunDockerStackActionAsync("check", null, "🩺 Running Docker Stack Pre-Flight Check...");

    [RelayCommand]
    public Task InitDockerStackSecretsAsync() =>
        RunDockerStackActionAsync("init-secrets", null, "🔑 Initializing / Rotating Dev Secrets...", invokeSecretsReload: true);

    [RelayCommand]
    public Task StopDockerStackAsync() =>
        RunDockerStackActionAsync("down", null, "⏹️ Stopping Docker Stack...");

    private void CancelActiveStackScript()
    {
        lock (_stackScriptLock)
        {
            try
            {
                _activeStackScriptCts?.Cancel();
            }
            catch (ObjectDisposedException ex)
            {
                Debug.WriteLine($"[DashboardViewModel] CancelActiveStackScript notice: {ex.Message}");
            }
        }
    }

    private async Task RunDockerStackActionAsync(
        string action,
        string[]? extraArgs,
        string busyBanner,
        bool invokeSecretsReload = false)
    {
        if (IsDockerStackBusy)
        {
            return;
        }

        IsDockerStackBusy = true;
        DockerStackButtonLabel = "🐳 Docker Stack (Busy)";
        ClusterStatusText = busyBanner;
        ClusterStatusColor = "#F59E0B";

        if (action is "sync" or "down")
        {
            StaleImageWarningText = string.Empty;
        }

        _orchestrator.AppendLog(new LogMessage
        {
            ServiceName = "DOCKER STACK",
            Message = $"{busyBanner} [action={action}]",
            Level = "INFO",
            IsError = false,
            Timestamp = TimeProvider.System.GetUtcNow()
        });

        string? lastErrorLine = null;
        try
        {
            using CancellationTokenSource linkedCts = CancellationTokenSource.CreateLinkedTokenSource(_healthCts.Token);
            lock (_stackScriptLock)
            {
                _activeStackScriptCts = linkedCts;
            }

            int exitCode;
            try
            {
                string repoRoot = SecretsManagerService.FindSolutionRoot();
                (exitCode, _) = await _stackRunner(
                    action,
                    extraArgs,
                    repoRoot,
                    (line, isErr) =>
                    {
                        if (line.Contains("[WARN] Cached Docker images", StringComparison.OrdinalIgnoreCase))
                        {
                            RunOnUiThread(() =>
                                StaleImageWarningText = "⚠️ C#/source changed since last Docker image build — run 'Rebuild & Sync Stack' in 🐳 Docker Stack to apply code changes.");
                        }

                        string? stepSummary = TryExtractDockerStackProgressSummary(line);
                        if (!string.IsNullOrWhiteSpace(stepSummary))
                        {
                            RunOnUiThread(() => ClusterStatusText = $"{busyBanner.TrimEnd('.')} — {stepSummary}");
                        }

                        if (isErr || line.Contains("[ERROR]", StringComparison.OrdinalIgnoreCase))
                        {
                            lastErrorLine = line;
                        }

                        _orchestrator.AppendLog(new LogMessage
                        {
                            ServiceName = "DOCKER STACK",
                            Message = line,
                            Level = isErr ? "WARN" : "INFO",
                            IsError = isErr,
                            Timestamp = TimeProvider.System.GetUtcNow()
                        });
                    },
                    linkedCts.Token);
            }
            finally
            {
                lock (_stackScriptLock)
                {
                    if (ReferenceEquals(_activeStackScriptCts, linkedCts))
                    {
                        _activeStackScriptCts = null;
                    }
                }
            }

            if (exitCode == 0)
            {
                if (invokeSecretsReload && _onSecretsRotatedExternal != null)
                {
                    await _onSecretsRotatedExternal();
                }

                ClusterStatusText = action switch
                {
                    "up" => "DOCKER STACK ONLINE & RUNNING",
                    "restart" => "DOCKER STACK RESTARTED & READY",
                    "sync" => "DOCKER STACK REBUILT & SYNCED",
                    "cleanup" => "LEGACY DOCKER ARTIFACTS CLEANED UP",
                    "down" => "DOCKER STACK STOPPED",
                    "check" => "DOCKER STACK PRE-FLIGHT PASSED",
                    "init-secrets" => "DEV SECRETS INITIALIZED & SYNCED",
                    _ => $"DOCKER STACK ({action.ToUpperInvariant()}) COMPLETED"
                };
                ClusterStatusColor = action == "down" ? "#64748B" : "#10B981";
            }
            else
            {
                ClusterStatusText = exitCode == 75
                    ? "Docker Stack busy — wait for the active Docker operation to finish."
                    : !string.IsNullOrWhiteSpace(lastErrorLine)
                        ? $"Docker Stack '{action}' failed (exit {exitCode}): {lastErrorLine}"
                        : $"Docker Stack '{action}' failed (exit {exitCode}) — inspect Live Logs.";
                ClusterStatusColor = "#EF4444";
            }
        }
        catch (Exception ex)
        {
            _orchestrator.AppendLog(new LogMessage
            {
                ServiceName = "DOCKER STACK",
                Message = $"Docker Stack '{action}' threw exception: {ex.Message}",
                Level = "ERROR",
                IsError = true,
                Timestamp = TimeProvider.System.GetUtcNow()
            });
            ClusterStatusText = $"ERROR ({action}): {ex.Message}";
            ClusterStatusColor = "#EF4444";
        }
        finally
        {
            IsDockerStackBusy = false;
            DockerStackButtonLabel = "🐳 Docker Stack";
            await RefreshInfrastructureHealthAsync();
        }
    }

    [RelayCommand]
    public async Task StartPyroscopeAsync()
    {
        await _orchestrator.StartServiceAsync("pyroscope");
        await RefreshInfrastructureHealthAsync();
    }

    [RelayCommand]
    public async Task RefreshInfrastructureHealthAsync()
    {
        Task<DockerStatus> dockerTask = _dockerHealth.CheckDockerStatusAsync();
        Task<DatabaseStatus> sqlTask = _databaseHealth.CheckDatabaseHealthAsync(_secretsManager);
        Task<DatabaseStatus> redisTask = _databaseHealth.CheckRedisHealthAsync();
        Task<DatabaseStatus> pyroscopeTask = _databaseHealth.CheckPyroscopeHealthAsync();
        Task<(bool Enabled, bool LiveRedisSynced, string StatusSummary)> arcadeStateTask =
            _orchestrator.GetQueueArcadeStateAsync();
        Task cockpitPipesTask = RefreshWillowmakerCockpitHealthAsync();

        await Task.WhenAll(dockerTask, sqlTask, redisTask, pyroscopeTask, arcadeStateTask, cockpitPipesTask);

        DockerStatus docker = await dockerTask;
        DockerComposeStackSnapshot dockerStack = docker.IsRunning
            ? await DockerStackProbe.ProbeAsync(
                SecretsManagerService.FindSolutionRoot(),
                includeFleetHttpStatus: false,
                cancellationToken: _healthCts.Token)
            : DockerComposeStackSnapshot.Inactive();
        DatabaseStatus sql = await sqlTask;
        DatabaseStatus redis = await redisTask;
        DatabaseStatus pyroscope = await pyroscopeTask;
        (bool arcadeEnabled, _, string arcadeSummary) = await arcadeStateTask;

        RunOnUiThread(ApplyUpdates);

        await RefreshSecretsLinksAsync();
        return;

        void ApplyUpdates()
        {
            IsQueueArcadeEnabled = arcadeEnabled;
            QueueArcadeStatusSummary = arcadeSummary ?? (arcadeEnabled ? "Enabled" : "Paused");
            IsDockerOnline = docker.IsRunning;
            DockerStatusMessage = dockerStack.IsStackActive
                ? $"{docker.StatusMessage} [kongor-stack: {dockerStack.RunningContainerCount}/{dockerStack.TotalContainerCount} running]"
                : docker.StatusMessage;
            DockerStatusColor = docker.IsRunning ? "#10B981" : "#EF4444";

            IsDatabaseOnline = sql?.IsConnected ?? false;
            DatabaseStatusMessage = sql?.StatusMessage ?? "Database is offline.";
            DatabaseStatusColor = sql?.StatusColor ?? "#EF4444";
            DatabaseLabel = (sql?.DatabaseType ?? string.Empty).Contains("Supabase", StringComparison.OrdinalIgnoreCase)
                ? "⚡ Supabase:"
                : (sql?.DatabaseType ?? string.Empty).Contains("PostgreSQL", StringComparison.OrdinalIgnoreCase)
                    ? "🗄️ PostgreSQL:"
                    : "🗄️ SQL Server:";

            IsRedisOnline = redis?.IsConnected ?? false;
            RedisStatusMessage = redis?.StatusMessage ?? "Redis Cache is offline.";
            RedisStatusColor = redis?.StatusColor ?? "#EF4444";

            IsPyroscopeOnline = pyroscope?.IsConnected ?? false;
            PyroscopeStatusMessage = pyroscope?.StatusMessage ?? "Pyroscope is offline.";
            PyroscopeStatusColor = pyroscope?.StatusColor ?? "#EF4444";
        }
    }

    public async Task RefreshSecretsLinksAsync()
    {
        if (_secretsManager == null) return;

        try
        {
            NexusSecrets secrets = await _secretsManager.LoadSecretsAsync();

            bool hasSupabase = !string.IsNullOrWhiteSpace(secrets.SupabaseUrl);
            string supabaseUrl = string.Empty;
            if (hasSupabase)
            {
                string rawUrl = secrets.SupabaseUrl.Trim();
                Match match = Regex.Match(rawUrl, @"https?://([a-zA-Z0-9\-]+)\.supabase\.co", RegexOptions.IgnoreCase);
                if (match.Success)
                {
                    string projectRef = match.Groups[1].Value;
                    supabaseUrl = $"https://supabase.com/dashboard/project/{projectRef}";
                }
                else
                {
                    supabaseUrl = rawUrl;
                }
            }

            bool hasGrafana = !string.IsNullOrWhiteSpace(secrets.GrafanaOtlpEndpoint) ||
                              !string.IsNullOrWhiteSpace(secrets.GrafanaOtlpHeaders);
            string grafanaUrl = string.Empty;
            if (hasGrafana)
            {
                if (!string.IsNullOrWhiteSpace(secrets.GrafanaOtlpEndpoint))
                {
                    string endpoint = secrets.GrafanaOtlpEndpoint.Trim();
                    if (Uri.TryCreate(endpoint, UriKind.Absolute, out Uri? uri))
                    {
                        string host = uri.Host;
                        // If it's a specific custom grafana instance (not an ingestion gateway)
                        if (!host.StartsWith("otlp-gateway-", StringComparison.OrdinalIgnoreCase) &&
                            !host.StartsWith("otlp-", StringComparison.OrdinalIgnoreCase) &&
                            host.EndsWith(".grafana.net", StringComparison.OrdinalIgnoreCase))
                            grafanaUrl = $"{uri.Scheme}://{host}";
                        else
                            grafanaUrl = "https://grafana.com";
                    }
                    else
                    {
                        grafanaUrl = "https://grafana.com";
                    }
                }
                else
                {
                    grafanaUrl = "https://grafana.com";
                }
            }

            bool hasPyroscope = !string.IsNullOrWhiteSpace(secrets.PyroscopeServerAddress) ||
                                !string.IsNullOrWhiteSpace(secrets.GrafanaOtlpEndpoint);
            string pyroscopeUrl = string.Empty;
            if (hasPyroscope)
            {
                if (!string.IsNullOrWhiteSpace(secrets.PyroscopeServerAddress))
                {
                    string pyroAddress = secrets.PyroscopeServerAddress.Trim();
                    if (Uri.TryCreate(pyroAddress, UriKind.Absolute, out Uri? uri))
                    {
                        string host = uri.Host;
                        // If it's a local or dedicated Pyroscope server (e.g. localhost:4040)
                        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
                            host.Equals("127.0.0.1", StringComparison.OrdinalIgnoreCase) ||
                            (!host.StartsWith("profiles-", StringComparison.OrdinalIgnoreCase) &&
                             !host.EndsWith(".grafana.net", StringComparison.OrdinalIgnoreCase)))
                            pyroscopeUrl = pyroAddress;
                        else
                            // In Grafana Cloud, profiles are viewed in Grafana Cloud Explore UI
                            pyroscopeUrl = grafanaUrl.Length > 0 ? grafanaUrl : "https://grafana.com";
                    }
                    else
                    {
                        pyroscopeUrl = pyroAddress;
                    }
                }
                else
                {
                    pyroscopeUrl = "http://localhost:4040";
                }
            }

            bool hasDiscord = !string.IsNullOrWhiteSpace(secrets.DiscordClientId);
            string discordUrl = hasDiscord
                ? $"https://discord.com/developers/applications/{secrets.DiscordClientId.Trim()}"
                : string.Empty;

            bool hasTwitch = !string.IsNullOrWhiteSpace(secrets.TwitchClientId);
            string twitchUrl = hasTwitch
                ? $"https://dev.twitch.tv/console/apps/{secrets.TwitchClientId.Trim()}"
                : string.Empty;

            RunOnUiThread(() =>
            {
                ApplySecretsState();

                // Hydrate standalone hosting mode & network settings once without triggering save or popup
                if (!_hostingSettingsHydrated)
                {
                    _hostingSettingsHydrated = true;
                    _suppressHostingSave = true;
                    try
                    {
                        WanWarningAcknowledged = secrets.WanWarningAcknowledged;
                        SelectedHostingMode = MapHostingModeToDisplay(secrets.HostingMode);
                        _previousHostingMode = SelectedHostingMode;
                        WanHost = secrets.WanHost ?? string.Empty;

                        if (!string.IsNullOrWhiteSpace(secrets.SelectedLanIp))
                        {
                            SelectedLanIp = secrets.SelectedLanIp.Trim();
                            if (!AvailableLanIps.Contains(SelectedLanIp))
                            {
                                AvailableLanIps.Insert(0, SelectedLanIp);
                            }
                        }
                        else if (string.IsNullOrWhiteSpace(SelectedLanIp) && AvailableLanIps.Count > 0)
                        {
                            SelectedLanIp = AvailableLanIps[0];
                        }

                        FriendJoinInstructionsPreview = BuildFriendJoinInstructions();
                    }
                    finally
                    {
                        _suppressHostingSave = false;
                    }
                }
            });

            void ApplySecretsState()
            {
                HasSupabaseConfigured = hasSupabase;
                SupabaseDashboardUrl = supabaseUrl;

                HasGrafanaConfigured = hasGrafana;
                GrafanaDashboardUrl = grafanaUrl;

                HasPyroscopeConfigured = hasPyroscope;
                PyroscopeDashboardUrl = pyroscopeUrl;

                HasDiscordConfigured = hasDiscord;
                DiscordDevPortalUrl = discordUrl;

                HasTwitchConfigured = hasTwitch;
                TwitchDevConsoleUrl = twitchUrl;
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[DashboardViewModel] Failed to refresh secrets links: {ex.Message}");
        }
    }

    private void StartPeriodicHealthCheck()
    {
        _ = Task.Run(async () =>
        {
            // Initial probe immediately
            try
            {
                await RefreshInfrastructureHealthAsync();
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[DashboardViewModel] Initial health check exception: {ex.Message}");
            }

            using PeriodicTimer timer = new(TimeSpan.FromSeconds(8));
            while (!_healthCts.IsCancellationRequested)
                try
                {
                    await timer.WaitForNextTickAsync(_healthCts.Token);
                    await RefreshInfrastructureHealthAsync();
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[DashboardViewModel] Periodic health check exception: {ex.Message}");
                }
        });
    }

    [RelayCommand]
    public async Task RefreshDockerStatusAsync()
    {
        await RefreshInfrastructureHealthAsync();
    }

    [RelayCommand]
    public void OpenWebPortal()
    {
        OpenBrowserUrl(IsBundledLayout ? "http://127.0.0.1:5266" : "https://localhost:5557");
    }

    [RelayCommand]
    public void OpenAspireDashboard()
    {
        OpenBrowserUrl(_orchestrator.AspireDashboardUrl);
    }

    [RelayCommand]
    public async Task OpenMasterServerAsync()
    {
        string address = "http://localhost:55555";
        if (_secretsManager != null)
            try
            {
                NexusSecrets secrets = await _secretsManager.LoadSecretsAsync();
                if (!string.IsNullOrWhiteSpace(secrets.MasterServerAddress))
                    address = secrets.MasterServerAddress.Trim();
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[DashboardViewModel] OpenMasterServerAsync secrets load exception: {ex.Message}");
            }

        if (!address.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !address.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            address = $"http://{address}";

        string swaggerUrl = $"{address.TrimEnd('/')}/swagger";
        OpenBrowserUrl(swaggerUrl);
    }

    [RelayCommand]
    public void OpenWebPortalApi()
    {
        OpenBrowserUrl(IsBundledLayout ? "http://127.0.0.1:5200/swagger" : "https://localhost:5201/swagger");
    }

    [RelayCommand]
    public void OpenSupabaseDashboard()
    {
        if (!string.IsNullOrWhiteSpace(SupabaseDashboardUrl))
            OpenBrowserUrl(SupabaseDashboardUrl);
        else
            OpenBrowserUrl("https://supabase.com/dashboard");
    }

    [RelayCommand]
    public void OpenGrafanaDashboard()
    {
        if (!string.IsNullOrWhiteSpace(GrafanaDashboardUrl))
            OpenBrowserUrl(GrafanaDashboardUrl);
        else
            OpenBrowserUrl("https://grafana.com/orgs");
    }

    [RelayCommand]
    public void OpenPyroscopeDashboard()
    {
        if (!string.IsNullOrWhiteSpace(PyroscopeDashboardUrl))
            OpenBrowserUrl(PyroscopeDashboardUrl);
        else
            OpenBrowserUrl("http://localhost:4040");
    }

    [RelayCommand]
    public void OpenDiscordDevPortal()
    {
        if (!string.IsNullOrWhiteSpace(DiscordDevPortalUrl))
            OpenBrowserUrl(DiscordDevPortalUrl);
        else
            OpenBrowserUrl("https://discord.com/developers/applications");
    }

    [RelayCommand]
    public void OpenTwitchDevConsole()
    {
        if (!string.IsNullOrWhiteSpace(TwitchDevConsoleUrl))
            OpenBrowserUrl(TwitchDevConsoleUrl);
        else
            OpenBrowserUrl("https://dev.twitch.tv/console");
    }

    private void UpdateClusterState(ServerState state)
    {
        void Apply()
        {
            ClusterState = state;
            if (state is ServerState.Stopped or ServerState.Starting)
            {
                HostingModeRestartHint = string.Empty;
            }
            switch (state)
            {
                case ServerState.Running:
                    ClusterStatusText = "ONLINE & RUNNING";
                    ClusterStatusColor = "#10B981";
                    _ = RefreshInfrastructureHealthAsync();
                    break;
                case ServerState.Starting:
                    ClusterStatusText = "STARTING SERVICES...";
                    ClusterStatusColor = "#F59E0B";
                    break;
                case ServerState.Stopping:
                    ClusterStatusText = "STOPPING...";
                    ClusterStatusColor = "#F59E0B";
                    break;
                case ServerState.Faulted:
                    ClusterStatusText = "FAULTED / CRASHED";
                    ClusterStatusColor = "#EF4444";
                    break;
                case ServerState.Stopped:
                default:
                    ClusterStatusText = "STOPPED";
                    ClusterStatusColor = "#64748B";
                    break;
            }

            OnPropertyChanged(nameof(CanStartCluster));
            OnPropertyChanged(nameof(CanStopCluster));
        }

        RunOnUiThread(Apply);
    }

    private static void RunOnUiThread(Action action)
    {
        if (Avalonia.Application.Current is null)
        {
            action();
            return;
        }

        try
        {
            if (Dispatcher.UIThread.CheckAccess())
                action();
            else
                Dispatcher.UIThread.Post(action);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[DashboardViewModel] Dispatcher dispatch fallback: {ex.Message}");
            action();
        }
    }

    private void UpdateServiceState(string serviceKey, ServerState state)
    {
        void Apply()
        {
            foreach (ServiceItemViewModel service in Microservices)
                if (service.Key == serviceKey)
                    service.UpdateState(state);
        }

        try
        {
            if (Dispatcher.UIThread.CheckAccess())
                Apply();
            else
                Dispatcher.UIThread.Post(Apply);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[DashboardViewModel] Dispatcher dispatch exception in UpdateServiceState: {ex.Message}");
            Apply();
        }
    }

    partial void OnSelectedHostingModeChanged(string value)
    {
        OnPropertyChanged(nameof(IsLanMode));
        OnPropertyChanged(nameof(IsWanMode));
        OnPropertyChanged(nameof(ShowLanSelector));
        FriendJoinInstructionsPreview = BuildFriendJoinInstructions();

        if (_suppressHostingSave)
        {
            _previousHostingMode = value;
            return;
        }

        if (string.Equals(value, "Public Internet", StringComparison.Ordinal) && !WanWarningAcknowledged)
        {
            IsWanWarningDialogOpen = true;
            return;
        }

        _previousHostingMode = value;
        _ = PersistHostingSettingsAsync();
    }

    partial void OnSelectedLanIpChanged(string value)
    {
        FriendJoinInstructionsPreview = BuildFriendJoinInstructions();
        if (!_suppressHostingSave)
        {
            _ = PersistHostingSettingsAsync();
        }
    }

    partial void OnWanHostChanged(string value)
    {
        FriendJoinInstructionsPreview = BuildFriendJoinInstructions();
        if (!_suppressHostingSave)
        {
            _ = PersistHostingSettingsAsync();
        }
    }

    partial void OnHostingModeRestartHintChanged(string value)
    {
        OnPropertyChanged(nameof(HasHostingModeRestartHint));
    }

    partial void OnCopyFeedbackMessageChanged(string value)
    {
        OnPropertyChanged(nameof(HasCopyFeedbackMessage));
    }

    private static string MapHostingModeToDisplay(string? canonical) => canonical?.Trim() switch
    {
        "Lan" => "LAN / Tailscale",
        "Wan" or "PublicInternet" => "Public Internet",
        _ => "Local only"
    };

    private static string MapHostingModeToCanonical(string? display) => display?.Trim() switch
    {
        "LAN / Tailscale" => "Lan",
        "Public Internet" => "Wan",
        _ => "Local"
    };

    [RelayCommand]
    public async Task ConfirmWanWarningAsync()
    {
        WanWarningAcknowledged = true;
        IsWanWarningDialogOpen = false;
        _previousHostingMode = "Public Internet";
        Console.Error.WriteLine("[NexusStandalone] User acknowledged WAN plaintext-HTTP warning; persisting HostingMode=Wan");
        await PersistHostingSettingsAsync();
    }

    [RelayCommand]
    public void CancelWanWarning()
    {
        IsWanWarningDialogOpen = false;
        _suppressHostingSave = true;
        SelectedHostingMode = _previousHostingMode;
        _suppressHostingSave = false;
        FriendJoinInstructionsPreview = BuildFriendJoinInstructions();
        Console.Error.WriteLine("[NexusStandalone] User cancelled WAN warning; reverted hosting mode");
    }

    public async Task PersistHostingSettingsAsync()
    {
        if (_secretsManager is null) return;

        await _persistHostingLock.WaitAsync();
        try
        {
            NexusSecrets secrets = await _secretsManager.LoadSecretsAsync();
            secrets.HostingMode = MapHostingModeToCanonical(SelectedHostingMode);
            secrets.SelectedLanIp = SelectedLanIp ?? string.Empty;
            secrets.WanHost = WanHost ?? string.Empty;
            secrets.WanWarningAcknowledged = WanWarningAcknowledged;

            await _secretsManager.SaveSecretsAsync(secrets);

            if (ClusterState == ServerState.Running)
            {
                HostingModeRestartHint = "Restart cluster to apply updated network bind/advertising settings.";
            }
            else
            {
                HostingModeRestartHint = string.Empty;
            }

            Console.Error.WriteLine($"[NexusStandalone] Persisted hosting settings: HostingMode={secrets.HostingMode}, SelectedLanIp={secrets.SelectedLanIp}, WanHost={secrets.WanHost}, WanWarningAcknowledged={secrets.WanWarningAcknowledged}");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[NexusStandalone] Error persisting hosting settings: {ex.Message}");
        }
        finally
        {
            _persistHostingLock.Release();
        }
    }

    [RelayCommand]
    public void RefreshNetworkInterfaces()
    {
        try
        {
            List<string> ips = [];
            foreach (NetworkInterface ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up || ni.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                    continue;

                IPInterfaceProperties ipProps = ni.GetIPProperties();
                foreach (UnicastIPAddressInformation addr in ipProps.UnicastAddresses)
                {
                    if (addr.Address.AddressFamily != AddressFamily.InterNetwork)
                        continue;

                    string ip = addr.Address.ToString();
                    if (ip.StartsWith("169.254.", StringComparison.Ordinal))
                        continue;

                    if (!ips.Contains(ip))
                        ips.Add(ip);
                }
            }

            AvailableLanIps.Clear();
            if (ips.Count == 0)
            {
                AvailableLanIps.Add("127.0.0.1");
            }
            else
            {
                foreach (string ip in ips)
                {
                    AvailableLanIps.Add(ip);
                }
            }

            if (string.IsNullOrWhiteSpace(SelectedLanIp) || !AvailableLanIps.Contains(SelectedLanIp))
            {
                SelectedLanIp = AvailableLanIps[0];
            }

            Console.Error.WriteLine($"[DashboardViewModel] Refreshed network interfaces: {string.Join(", ", AvailableLanIps)}; selected: {SelectedLanIp}");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[DashboardViewModel] Error enumerating network interfaces: {ex.Message}");
            if (AvailableLanIps.Count == 0)
                AvailableLanIps.Add("127.0.0.1");
            if (string.IsNullOrWhiteSpace(SelectedLanIp))
                SelectedLanIp = "127.0.0.1";
        }
    }

    private string GetBuildId()
    {
        if (_bundledLayout is not null)
        {
            string manifestPath = Path.Combine(_bundledLayout.Root, "build_manifest.json");
            if (File.Exists(manifestPath))
            {
                try
                {
                    string json = File.ReadAllText(manifestPath);
                    using JsonDocument doc = JsonDocument.Parse(json);
                    if (doc.RootElement.TryGetProperty("buildId", out JsonElement elem) && elem.ValueKind == JsonValueKind.String)
                    {
                        string? id = elem.GetString();
                        if (!string.IsNullOrWhiteSpace(id))
                            return id.Trim();
                    }
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[DashboardViewModel] Error reading build_manifest.json: {ex.Message}");
                }
            }
        }

        return "standalone";
    }

    public string BuildFriendJoinInstructions()
    {
        string buildId = GetBuildId();
        string host;
        if (string.Equals(SelectedHostingMode, "LAN / Tailscale", StringComparison.Ordinal))
        {
            host = !string.IsNullOrWhiteSpace(SelectedLanIp) ? SelectedLanIp.Trim() : "127.0.0.1";
        }
        else if (string.Equals(SelectedHostingMode, "Public Internet", StringComparison.Ordinal))
        {
            host = !string.IsNullOrWhiteSpace(WanHost)
                ? WanHost.Trim()
                : (!string.IsNullOrWhiteSpace(SelectedLanIp) ? SelectedLanIp.Trim() : "<your-public-ip-or-dns>");
        }
        else
        {
            host = "127.0.0.1";
        }

        StringBuilder sb = new();
        sb.AppendLine($"Project Kongor — Friend Join Instructions (Build: {buildId})");
        sb.AppendLine($"1. Register Account: http://{host}:5266/Account/Register (ask the host for a single-use invite code from userdata/secrets/invite_codes.txt or 'kongor invite create')");
        sb.AppendLine($"2. Launcher (WILLOWMAKER) Master Server: {host}:55555");
        if (IsWanMode)
        {
            sb.AppendLine("3. Host Router Port Forwarding: TCP 55555, 11031, 5266; UDP 11234 (ping), 11235-11236 (game), 11735-11736 (voice)");
        }

        return sb.ToString().TrimEnd();
    }

    [RelayCommand]
    public async Task CopyFriendJoinInstructionsAsync()
    {
        string text = BuildFriendJoinInstructions();
        FriendJoinInstructionsPreview = text;

        try
        {
            if (Avalonia.Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime { MainWindow.Clipboard: { } clipboard })
            {
                await clipboard.SetTextAsync(text);
                CopyFeedbackMessage = "Copied Friend Join Instructions to clipboard!";
            }
            else
            {
                CopyFeedbackMessage = "Copied Friend Join Instructions to clipboard!";
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[DashboardViewModel] Clipboard error: {ex.Message}");
            CopyFeedbackMessage = "Copied Friend Join Instructions to clipboard!";
        }

        Console.Error.WriteLine($"[NexusStandalone] Generated Friend Join Instructions for mode={SelectedHostingMode}");
    }

    public static void OpenBrowserUrl(string url)
    {
        try
        {
            ProcessStartInfo psi = new() { FileName = url, UseShellExecute = true };
            Process.Start(psi);
        }
        catch (Exception)
        {
            try
            {
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                    Process.Start(new ProcessStartInfo("cmd", $"/c start {url.Replace("&", "^&")}")
                    {
                        CreateNoWindow = true
                    });
                else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                    Process.Start("xdg-open", url);
                else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX)) Process.Start("open", url);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[DashboardViewModel] OpenBrowserUrl shell launch exception: {ex.Message}");
            }
        }
    }
}
