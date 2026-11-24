// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NEXUS.ControlPanel.GUI.Services;
using NEXUS.ControlPanel.GUI.Services.Models;

namespace NEXUS.ControlPanel.GUI.ViewModels;

public sealed partial class GameServersViewModel : ViewModelBase, IDisposable
{
    private readonly List<GameServerHostItemViewModel> _allHosts = [];
    private readonly IGameServerFleetService _fleetService;
    private readonly INexusOrchestratorService? _orchestrator;
    private readonly ISecretsManagerService? _secretsManager;
    private readonly IDockerHealthService? _dockerHealth;
    private readonly Action? _onNavigateToSecretsConfig;
    private readonly DispatcherTimer _chatPollTimer;
    private readonly DispatcherTimer _fleetPollTimer;
    private DateTimeOffset _lastDebouncedRefresh = DateTimeOffset.MinValue;
    private int _consecutiveOfflinePolls;
    private DateTimeOffset _lastFullDiscoveryTime = DateTimeOffset.MinValue;
    private readonly TimeSpan _fullDiscoveryInterval = TimeSpan.FromSeconds(60);

    [ObservableProperty] private long _averagePingMs;
    [ObservableProperty] private string _fleetStatusMessage = "Ready";

    [ObservableProperty] private ObservableCollection<GameServerHostItemViewModel> _hosts = [];
    [ObservableProperty] private bool _isRefreshing;
    [ObservableProperty] private string _newHostAddress = string.Empty;
    [ObservableProperty] private string _newHostLocation = "USE";
    [ObservableProperty] private string _newHostName = string.Empty;
    [ObservableProperty] private int _newHostPort = 11234;
    [ObservableProperty] private int _onlineHosts;
    [ObservableProperty] private string _searchQuery = string.Empty;
    [ObservableProperty] private string _selectedLocationFilter = "^yALL";
    [ObservableProperty] private bool _showAddHostDialog;
    [ObservableProperty] private int _totalActiveInstances;
    [ObservableProperty] private int _totalHosts;

    // --- Cloud-Native Container Launcher State ---
    [ObservableProperty] private bool _showContainerLauncherDialog;
    [ObservableProperty] private string _selectedContainerPlatform = "Docker";
    [ObservableProperty] private string _compelPriorityMode = "Background";
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ContainerLauncherModalTitle))]
    private bool _isQueueArcadeContainerMode;
    [ObservableProperty] private bool _revealContainerSecretsInCommand;

    public string ContainerLauncherModalTitle => IsQueueArcadeContainerMode
        ? "🕹️ Queue Arcade Container Launcher"
        : "🐳 Dedicated Game Server Launcher";
    [ObservableProperty] private string _generatedContainerCommand = string.Empty;
    [ObservableProperty] private bool _isLaunchingContainer;
    [ObservableProperty] private string _containerLaunchFeedback = string.Empty;
    [ObservableProperty] private bool _isDockerImageBuilt = true;

    public ObservableCollection<string> ContainerPlatforms { get; } = ["Docker", "Minikube", "Kubernetes", "K3s (Agones)"];
    public ObservableCollection<string> CompelPriorityModes { get; } = ["Background", "HighPriority", "Adaptive"];
    public ObservableCollection<string> Locations { get; } = ["^yALL", "^cUSE", "^gUSW", "^pEU", "^rAU", "^oSEA", "^mBR", "^wRU"];
    public ObservableCollection<string> ColoredLocations => Locations;

    [ObservableProperty] private string _customServerNamePrefix = "^pDONUT ^oARENA";
    [ObservableProperty] private string _serverNameApplyFeedback = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FleetTabBackground))]
    [NotifyPropertyChangedFor(nameof(FleetTabBorderBrush))]
    [NotifyPropertyChangedFor(nameof(FleetTabForeground))]
    [NotifyPropertyChangedFor(nameof(ChatTabBackground))]
    [NotifyPropertyChangedFor(nameof(ChatTabBorderBrush))]
    [NotifyPropertyChangedFor(nameof(ChatTabForeground))]
    [NotifyPropertyChangedFor(nameof(IsChatSurfaceActive))]
    [NotifyPropertyChangedFor(nameof(IsDockedChatVisible))]
    [NotifyPropertyChangedFor(nameof(IsPopOutPlaceholderVisible))]
    private bool _isChatModeActive;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsChatSurfaceActive))]
    [NotifyPropertyChangedFor(nameof(IsDockedChatVisible))]
    [NotifyPropertyChangedFor(nameof(IsPopOutPlaceholderVisible))]
    [NotifyPropertyChangedFor(nameof(PopOutChatButtonLabel))]
    private bool _isChatPoppedOut;

    public bool IsChatSurfaceActive => IsChatModeActive || IsChatPoppedOut;
    public bool IsDockedChatVisible => IsChatModeActive && !IsChatPoppedOut;
    public bool IsPopOutPlaceholderVisible => IsChatModeActive && IsChatPoppedOut;
    public string PopOutChatButtonLabel => IsChatPoppedOut ? "Focus Chat Window" : "Pop Out Chat";

    public string FleetTabBackground => !IsChatModeActive ? "#00F0FF" : "Transparent";
    public string FleetTabBorderBrush => !IsChatModeActive ? "#00F0FF" : "Transparent";
    public string FleetTabForeground => !IsChatModeActive ? "#0B0F17" : "#94A3B8";

    public string ChatTabBackground => IsChatModeActive ? "#00F0FF" : "Transparent";
    public string ChatTabBorderBrush => IsChatModeActive ? "#00F0FF" : "Transparent";
    public string ChatTabForeground => IsChatModeActive ? "#0B0F17" : "#94A3B8";

    [ObservableProperty] private bool _hasActiveChatInstances;
    [ObservableProperty] private GameServerInstanceDetailViewModel? _selectedChatInstance;
    [ObservableProperty] private ObservableCollection<GameServerInstanceDetailViewModel> _allChatInstances = [];
    [ObservableProperty] private ObservableCollection<GameServerInstanceDetailViewModel> _filteredChatInstances = [];
    [ObservableProperty] private string _chatSearchQuery = string.Empty;
    [ObservableProperty] private string _selectedChatStatusFilter = "ALL";
    [ObservableProperty] private string _commandInput = string.Empty;
    [ObservableProperty] private bool _isSendingCommand;
    [ObservableProperty] private string _lastCommandStatus = string.Empty;
    [ObservableProperty] private bool _autoScroll = true;
    [ObservableProperty] private bool _hasIdleCommandTargetWarning;
    [ObservableProperty] private string _idleCommandTargetWarningText = string.Empty;
    [ObservableProperty] private string _switchAndSendButtonLabel = string.Empty;
    private GameServerInstanceDetailViewModel? _pendingActiveSwitchTarget;
    private string _pendingActiveSwitchCommand = string.Empty;

    public GameServersViewModel(
        IGameServerFleetService fleetService,
        INexusOrchestratorService? orchestrator = null,
        ISecretsManagerService? secretsManager = null,
        IDockerHealthService? dockerHealth = null,
        Action? onNavigateToSecretsConfig = null,
        ICockpitAppBridgeService? cockpitBridge = null)
    {
        _fleetService = fleetService;
        _orchestrator = orchestrator;
        _secretsManager = secretsManager;
        _dockerHealth = dockerHealth;
        _onNavigateToSecretsConfig = onNavigateToSecretsConfig;
        _cockpitBridge = cockpitBridge ?? new CockpitAppBridgeService(orchestrator);

        _chatPollTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(2000) };
        _chatPollTimer.Tick += async (_, _) =>
        {
            if (IsChatSurfaceActive && SelectedChatInstance != null)
            {
                await PollSelectedChatInstanceLogsAsync();
            }
        };
        _chatPollTimer.Start();

        _fleetPollTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _fleetPollTimer.Tick += async (_, _) => await AutoPollFleetAsync();
        _fleetPollTimer.Start();

        if (_orchestrator != null)
        {
            _orchestrator.OnLogReceived += HandleOrchestratorLog;
            _orchestrator.OnServiceStateChanged += HandleServiceStateChanged;
            _orchestrator.OnClusterStateChanged += HandleClusterStateChanged;
        }

        _ = InitializeContainerLauncherSecretsAsync();
        _ = LoadAndRefreshFleetAsync();
    }

    private async Task InitializeContainerLauncherSecretsAsync()
    {
        try
        {
            if (_secretsManager != null)
            {
                _cachedSecrets = await _secretsManager.LoadSecretsAsync();
                if (!string.IsNullOrWhiteSpace(_cachedSecrets.CompelPriorityMode))
                {
                    CompelPriorityMode = _cachedSecrets.CompelPriorityMode;
                }
                if (!string.IsNullOrWhiteSpace(_cachedSecrets.ContainerRuntimeTarget))
                {
                    SelectedContainerPlatform = _cachedSecrets.ContainerRuntimeTarget;
                }
                if (!string.IsNullOrWhiteSpace(_cachedSecrets.CompelServerNamePrefix))
                {
                    CustomServerNamePrefix = _cachedSecrets.CompelServerNamePrefix;
                }
            }
            UpdateGeneratedContainerCommand();
        }
        catch { }
    }

    public void Dispose()
    {
        _chatPollTimer.Stop();
        _fleetPollTimer.Stop();
        try
        {
            _activeChatStreamCts?.Cancel();
            _activeChatStreamCts?.Dispose();
        }
        catch (ObjectDisposedException ex)
        {
            System.Diagnostics.Debug.WriteLine($"[GameServersViewModel] Dispose CTS already disposed: {ex.Message}");
        }
        if (_orchestrator != null)
        {
            _orchestrator.OnLogReceived -= HandleOrchestratorLog;
            _orchestrator.OnServiceStateChanged -= HandleServiceStateChanged;
            _orchestrator.OnClusterStateChanged -= HandleClusterStateChanged;
        }
    }

    [RelayCommand]
    public async Task LoadAndRefreshFleetAsync()
    {
        if (IsRefreshing) return;
        IsRefreshing = true;
        ResetPollBackoff();
        FleetStatusMessage = "Discovering game server hosts...";

        try
        {
            _lastFullDiscoveryTime = TimeProvider.System.GetUtcNow();
            List<GameServerHostEntry> entries = await _fleetService.GetDiscoveredHostsAsync();

            _allHosts.Clear();
            _allHosts.AddRange(entries.Select(entry => new GameServerHostItemViewModel(entry, _fleetService)));

            ApplyFilters();

            FleetStatusMessage = "Pinging fleet nodes...";
            List<Task> pingTasks = [.. _allHosts.Select(h => h.RefreshAsync())];
            await Task.WhenAll(pingTasks);

            UpdateSummaryMetrics();
            RebuildChatInstancesList();
            FleetStatusMessage = $"Updated {_allHosts.Count} game server hosts.";
            if (Avalonia.Threading.Dispatcher.UIThread.CheckAccess())
            {
                _fleetPollTimer.Interval = ComputeNextPollInterval();
            }
        }
        catch (Exception ex)
        {
            FleetStatusMessage = $"Fleet refresh failed: {ex.Message}";
        }
        finally
        {
            IsRefreshing = false;
        }
    }

    private void UpdateSummaryMetrics()
    {
        TotalHosts = _allHosts.Count;
        OnlineHosts = _allHosts.Count(h => h.PingMs >= 0);
        int liveInstances = _allHosts.Sum(h => h.InstancesList.Count(i => i.IsRunning));
        TotalActiveInstances = liveInstances > 0 || _allHosts.Any(h => h.InstancesList.Count > 0)
            ? liveInstances
            : _allHosts.Where(h => h.IsManagerRunning).Sum(h => h.Instances);

        List<long> validPings = [.. _allHosts.Where(h => h.PingMs >= 0).Select(h => h.PingMs)];
        AveragePingMs = validPings.Count > 0 ? (long)validPings.Average() : 0;
    }

    private static string StripCarets(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;
        return System.Text.RegularExpressions.Regex.Replace(text, @"\^\d{3}|\^[a-zA-Z*]", string.Empty);
    }

    private void RebuildChatInstancesList()
    {
        List<GameServerInstanceDetailViewModel> instances = [];
        foreach (var host in _allHosts)
        {
            // Only skip hosts that are completely unreachable and have no known instances
            if (host.PingMs < 0 && !host.IsManagerRunning && host.InstancesList.Count == 0) continue;

            foreach (var inst in host.InstancesList)
            {
                if (inst.IsTabClosed) continue;
                instances.Add(inst);
            }
        }

        var prevSelected = SelectedChatInstance;
        SyncCollection(AllChatInstances, instances, i => (i.HostAddress, i.ControlPort, i.GamePort));
        ApplyChatFilters();

        if (AllChatInstances.Count == 0)
        {
            SelectedChatInstance = null;
            HasActiveChatInstances = false;
            return;
        }

        if (prevSelected != null && !prevSelected.IsTabClosed)
        {
            SelectedChatInstance = AllChatInstances.FirstOrDefault(i => i.GamePort == prevSelected.GamePort && i.ControlPort == prevSelected.ControlPort && i.HostAddress == prevSelected.HostAddress)
                                   ?? AllChatInstances.FirstOrDefault();
        }
        else
        {
            SelectedChatInstance = AllChatInstances.FirstOrDefault(i => i.IsRunning) ?? AllChatInstances.FirstOrDefault();
        }

        HasActiveChatInstances = SelectedChatInstance != null && FilteredChatInstances.Count > 0;

        if (SelectedChatInstance != null && SelectedChatInstance.ChatMessages.Count == 0 && SelectedChatInstance.IsRunning)
        {
            SelectedChatInstance.AddSystemMessage($"Connected to {SelectedChatInstance.ServerName} (UDP {SelectedChatInstance.GamePort} • {SelectedChatInstance.HostingBadgeText}). Ready for commands.");
            _ = PollSelectedChatInstanceLogsAsync();
        }
    }

    partial void OnSelectedChatInstanceChanged(GameServerInstanceDetailViewModel? oldValue, GameServerInstanceDetailViewModel? newValue)
    {
        if (oldValue != null) oldValue.IsSelected = false;
        if (newValue != null) newValue.IsSelected = true;
        HasActiveChatInstances = newValue != null && FilteredChatInstances.Count > 0;
    }

    partial void OnSearchQueryChanged(string value)
    {
        _ = value;
        ApplyFilters();
    }

    partial void OnSelectedLocationFilterChanged(string value)
    {
        _ = value;
        ApplyFilters();
    }

    private void ApplyFilters()
    {
        IEnumerable<GameServerHostItemViewModel> filtered = _allHosts;

        if (!string.IsNullOrWhiteSpace(SearchQuery))
            filtered = filtered.Where(h =>
                h.Name.Contains(SearchQuery, StringComparison.OrdinalIgnoreCase) ||
                h.HostAddress.Contains(SearchQuery, StringComparison.OrdinalIgnoreCase) ||
                h.Location.Contains(SearchQuery, StringComparison.OrdinalIgnoreCase));

        string cleanLocFilter = StripCarets(SelectedLocationFilter).Trim().ToUpperInvariant();
        if (!string.IsNullOrWhiteSpace(cleanLocFilter) && cleanLocFilter != "ALL")
        {
            filtered = filtered.Where(h =>
            {
                string hostLoc = StripCarets(h.Location).Trim().ToUpperInvariant();
                return hostLoc.Equals(cleanLocFilter, StringComparison.OrdinalIgnoreCase) ||
                       (cleanLocFilter == "USE" && hostLoc.Equals("NEWERTH", StringComparison.OrdinalIgnoreCase));
            });
        }

        Hosts = [.. filtered];
    }

    // --- IM Chat-Style View Filtering & Selection ---
    partial void OnAllChatInstancesChanged(ObservableCollection<GameServerInstanceDetailViewModel> value)
    {
        _ = value;
        ApplyChatFilters();
    }

    partial void OnChatSearchQueryChanged(string value)
    {
        _ = value;
        ApplyChatFilters();
    }

    partial void OnSelectedChatStatusFilterChanged(string value)
    {
        _ = value;
        ApplyChatFilters();
    }

    private void ApplyChatFilters()
    {
        IEnumerable<GameServerInstanceDetailViewModel> filtered = AllChatInstances.Where(i => !i.IsTabClosed);

        if (!string.IsNullOrWhiteSpace(ChatSearchQuery))
        {
            filtered = filtered.Where(i =>
                i.ServerName.Contains(ChatSearchQuery, StringComparison.OrdinalIgnoreCase) ||
                i.GamePort.ToString().Contains(ChatSearchQuery, StringComparison.OrdinalIgnoreCase) ||
                i.Endpoint.Contains(ChatSearchQuery, StringComparison.OrdinalIgnoreCase));
        }

        if (!string.IsNullOrWhiteSpace(SelectedChatStatusFilter) && SelectedChatStatusFilter != "ALL")
        {
            filtered = SelectedChatStatusFilter.ToUpperInvariant() switch
            {
                "AVAILABLE" => filtered.Where(i => i.IsRunning && i.ServerStatus.Equals("Available", StringComparison.OrdinalIgnoreCase)),
                "IN MATCH" or "INMATCH" => filtered.Where(i => i.IsRunning && i.ServerStatus.Equals("InMatch", StringComparison.OrdinalIgnoreCase)),
                "STOPPED" => filtered.Where(i => (!i.IsRunning || i.ServerStatus.Equals("Stopped", StringComparison.OrdinalIgnoreCase)) && !i.ServerStatus.Equals("Crashed", StringComparison.OrdinalIgnoreCase)),
                "CRASHED" => filtered.Where(i => i.ServerStatus.Equals("Crashed", StringComparison.OrdinalIgnoreCase) || i.HasErrors),
                _ => filtered
            };
        }

        SyncCollection(FilteredChatInstances, [.. filtered], i => (i.HostAddress, i.ControlPort, i.GamePort));
        HasActiveChatInstances = FilteredChatInstances.Count > 0 && SelectedChatInstance != null;
    }

    [RelayCommand]
    public void SwitchToChatMode()
    {
        IsChatModeActive = true;
        RebuildChatInstancesList();
        StartOrSwitchChatLogStream(SelectedChatInstance);
        _ = PollSelectedChatInstanceLogsAsync();
    }

    [RelayCommand]
    public void SwitchToFleetMode()
    {
        IsChatModeActive = false;
        if (!IsChatPoppedOut)
        {
            StartOrSwitchChatLogStream(null);
        }
    }

    [RelayCommand]
    public void OpenChatForInstance(GameServerInstanceDetailViewModel instance)
    {
        if (instance == null) return;
        SelectedChatInstance = instance;
        if (instance.ChatMessages.Count == 0)
        {
            instance.AddSystemMessage($"Connected to {instance.ServerName} (UDP {instance.GamePort}). Ready for commands.");
        }

        if (IsChatPoppedOut)
        {
            _onRequestPopOutChat?.Invoke();
        }
        else
        {
            IsChatModeActive = true;
        }

        StartOrSwitchChatLogStream(instance);
        _ = PollSelectedChatInstanceLogsAsync();
    }

    [RelayCommand]
    public void SelectChatInstance(GameServerInstanceDetailViewModel instance)
    {
        if (instance == null) return;
        DismissIdleTargetWarning();
        SelectedChatInstance = instance;
        if (instance.ChatMessages.Count == 0)
        {
            instance.AddSystemMessage($"Connected to {instance.ServerName} (UDP {instance.GamePort}). Ready for commands.");
        }
        StartOrSwitchChatLogStream(instance);
        _ = PollSelectedChatInstanceLogsAsync();
    }

    [RelayCommand]
    public async Task StartNewServerInstanceAsync()
    {
        var target = AllChatInstances.FirstOrDefault(i => !i.IsRunning && !i.IsTabClosed)
                     ?? AllChatInstances.FirstOrDefault(i => !i.IsRunning);

        if (target != null)
        {
            target.DesiredRunning = true;
            target.IsPermanentlyStopped = false;
            target.IsTabClosed = false;
            target.UpdateStatus("Starting", isRunning: true);
            target.AddSystemMessage($"Spinning up dedicated game server on UDP {target.GamePort}...");
            SelectedChatInstance = target;

            int targetPort = target.WorkerPort > 0 ? target.WorkerPort : target.GamePort;
            var (success, msg) = await _fleetService.StartWorkerAsync(target.HostAddress, target.ControlPort, targetPort, target.AuthToken);
            if (success)
            {
                target.AddSystemMessage($"Server start signal dispatched successfully ({msg}).");
            }
            else
            {
                target.AddSystemMessage($"Server start failed: {msg}");
            }

            await RefreshFleetQuietlyAsync();
            return;
        }

        var host = _allHosts.FirstOrDefault(h => h.PingMs >= 0) ?? _allHosts.FirstOrDefault();
        if (host != null)
        {
            await host.StartAsync();
            await RefreshFleetQuietlyAsync();
        }
    }

    [RelayCommand]
    public async Task StartSelectedInstanceAsync()
    {
        if (SelectedChatInstance == null) return;
        var target = SelectedChatInstance;

        target.DesiredRunning = true;
        target.IsPermanentlyStopped = false;
        target.IsTabClosed = false;
        target.UpdateStatus("Starting", isRunning: true);
        target.AddSystemMessage($"Starting dedicated game server on UDP {target.GamePort}...");

        int targetPort = target.WorkerPort > 0 ? target.WorkerPort : target.GamePort;
        var (success, msg) = await _fleetService.StartWorkerAsync(target.HostAddress, target.ControlPort, targetPort, target.AuthToken);
        if (success)
        {
            target.AddSystemMessage($"Server start signal dispatched successfully ({msg}).");
        }
        else
        {
            target.AddSystemMessage($"Server start failed: {msg}");
        }

        await RefreshFleetQuietlyAsync();
    }

    [RelayCommand]
    public async Task StopInstanceAsync(GameServerInstanceDetailViewModel? instance)
    {
        var target = instance ?? SelectedChatInstance;
        if (target == null) return;

        target.DesiredRunning = false;
        target.IsPermanentlyStopped = true;
        target.UpdateStatus("Stopped", isRunning: false);
        target.AddSystemMessage($"⏹ Stopping dedicated game server on UDP {target.GamePort} (Auto-relaunch disabled)...");

        int targetPort = target.WorkerPort > 0 ? target.WorkerPort : target.GamePort;
        var (success, msg) = await _fleetService.StopWorkerAsync(target.HostAddress, target.ControlPort, targetPort, target.AuthToken);
        if (success)
        {
            target.AddSystemMessage($"Server on Port {target.GamePort} stopped cleanly.");
        }
        else
        {
            target.AddSystemMessage($"Stop request encountered an issue: {msg}");
        }

        if (target.IsLocal && target.ProcessId.HasValue && target.ProcessId > 0)
        {
            try
            {
                var proc = Process.GetProcessById(target.ProcessId.Value);
                if (!proc.HasExited)
                {
                    proc.Kill();
                    target.AddSystemMessage($"Local process PID {target.ProcessId.Value} terminated.");
                }
            }
            catch { }
        }

        await RefreshFleetQuietlyAsync();
    }

    [RelayCommand]
    public async Task KillInstanceAsync(GameServerInstanceDetailViewModel? instance)
    {
        var target = instance ?? SelectedChatInstance;
        if (target == null) return;

        target.DesiredRunning = false;
        target.IsPermanentlyStopped = true;
        target.UpdateStatus("Stopped", isRunning: false);
        target.AddSystemMessage($"💀 Force terminating dedicated game server on UDP {target.GamePort}...");

        int targetPort = target.WorkerPort > 0 ? target.WorkerPort : target.GamePort;
        _ = _fleetService.SendWorkerCommandAsync(target.HostAddress, target.ControlPort, targetPort, "quit", target.AuthToken);
        var (success, msg) = await _fleetService.StopWorkerAsync(target.HostAddress, target.ControlPort, targetPort, target.AuthToken);

        if (target.IsLocal && target.ProcessId.HasValue && target.ProcessId > 0)
        {
            try
            {
                var proc = Process.GetProcessById(target.ProcessId.Value);
                if (!proc.HasExited)
                {
                    proc.Kill();
                }
            }
            catch { }
        }

        target.AddSystemMessage($"💀 Server on Port {target.GamePort} terminated.");
        await RefreshFleetQuietlyAsync();
    }

    [RelayCommand]
    public void CloseChatTab(GameServerInstanceDetailViewModel? instance)
    {
        var target = instance ?? SelectedChatInstance;
        if (target == null) return;

        target.IsTabClosed = true;
        ApplyChatFilters();

        if (SelectedChatInstance == target)
        {
            SelectedChatInstance = FilteredChatInstances.FirstOrDefault();
        }
        HasActiveChatInstances = SelectedChatInstance != null && FilteredChatInstances.Count > 0;
    }

    [RelayCommand]
    public async Task CleanZombieWorkersAsync()
    {
        FleetStatusMessage = "Purging zombie dedicated game server processes...";
        int killedCount = 0;
        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                var procs = Process.GetProcessesByName("hon_x64");
                foreach (var p in procs)
                {
                    try
                    {
                        // Protect active player game client (interactive window titled 'Heroes of Newerth' or \games\client\ binary)
                        if (NexusOrchestratorService.IsInteractiveGameClientProcess(p))
                        {
                            continue;
                        }

                        p.Kill();
                        killedCount++;
                    }
                    catch { }
                }
            }
        }
        catch { }

        FleetStatusMessage = killedCount > 0
            ? $"✓ Cleaned up {killedCount} zombie server processes."
            : "✓ No zombie server processes found.";

        await LoadAndRefreshFleetAsync();
    }

    [RelayCommand]
    public async Task ApplyServerNamePrefixAsync()
    {
        if (string.IsNullOrWhiteSpace(CustomServerNamePrefix)) return;

        ServerNameApplyFeedback = "Saving server name...";
        try
        {
            if (_secretsManager != null)
            {
                _cachedSecrets = await _secretsManager.LoadSecretsAsync();
                _cachedSecrets.CompelServerNamePrefix = CustomServerNamePrefix.Trim();
                await _secretsManager.SaveSecretsAsync(_cachedSecrets);
            }

            UpdateGeneratedContainerCommand();

            int updatedCount = 0;
            foreach (var inst in AllChatInstances.Where(i => i.IsRunning))
            {
                try
                {
                    int targetPort = inst.WorkerPort > 0 ? inst.WorkerPort : inst.GamePort;
                    string dynamicName = $"{CustomServerNamePrefix.Trim()} {inst.InstanceTag}";
                    await _fleetService.SendWorkerCommandAsync(
                        inst.HostAddress,
                        inst.ControlPort,
                        targetPort,
                        $"Set svr_name \"{dynamicName}\"",
                        inst.AuthToken);
                    inst.ServerName = dynamicName;
                    updatedCount++;
                }
                catch { }
            }

            ServerNameApplyFeedback = updatedCount > 0
                ? $"✓ Saved & applied to {updatedCount} active server(s)!"
                : "✓ Saved prefix! New servers will launch with this name.";
        }
        catch (Exception ex)
        {
            ServerNameApplyFeedback = $"⚠️ Failed to save: {ex.Message}";
        }
    }

    [RelayCommand]
    public async Task RestartSelectedInstanceAsync()
    {
        if (SelectedChatInstance == null) return;
        var target = SelectedChatInstance;

        target.UpdateStatus("Restarting", isRunning: true);
        target.AddSystemMessage($"Restarting dedicated game server on UDP {target.GamePort}...");

        int targetPort = target.WorkerPort > 0 ? target.WorkerPort : target.GamePort;
        var (success, msg) = await _fleetService.RestartWorkerAsync(target.HostAddress, target.ControlPort, targetPort, target.AuthToken);
        if (success)
        {
            target.AddSystemMessage($"Server restart signal dispatched ({msg}).");
        }
        else
        {
            target.AddSystemMessage($"Server restart failed: {msg}");
        }

        await RefreshFleetQuietlyAsync();
    }

    [RelayCommand]
    public async Task SendCommandAsync()
    {
        if (SelectedChatInstance == null || string.IsNullOrWhiteSpace(CommandInput)) return;

        string input = CommandInput.Trim();
        CommandInput = string.Empty;
        var (kind, cmdToSend, displayText) = ClassifySmartHybridInput(input);
        if (string.IsNullOrWhiteSpace(cmdToSend)) return;
        await DispatchCommandToInstanceAsync(SelectedChatInstance, cmdToSend, displayText);
    }

    [RelayCommand]
    public async Task SendQuickCommandAsync(string? command)
    {
        if (SelectedChatInstance == null || string.IsNullOrWhiteSpace(command)) return;

        string trimmed = command.Trim();
        if (trimmed.Equals("announce", StringComparison.OrdinalIgnoreCase) || command.EndsWith(' '))
        {
            CommandInput = trimmed + " ";
            if (trimmed.StartsWith("givegold", StringComparison.OrdinalIgnoreCase) || trimmed.StartsWith("gold", StringComparison.OrdinalIgnoreCase))
            {
                LastCommandStatus = "Type gold amount or target and press Enter (e.g. 55555, all 20000, 0 10000).";
            }
            else
            {
                LastCommandStatus = "Type your announcement message and press Enter or Send to broadcast.";
            }
            return;
        }

        var (kind, cmdToSend, displayText) = ClassifySmartHybridInput(trimmed);
        await DispatchCommandToInstanceAsync(SelectedChatInstance, cmdToSend, displayText);
    }

    private async Task DispatchCommandToInstanceAsync(GameServerInstanceDetailViewModel target, string command, string? displayText = null)
    {
        if (IsSendingCommand) return;
        IsSendingCommand = true;

        string textToShow = displayText ?? command;
        target.AddOperatorMessage(textToShow, "dispatching...");
        LastCommandStatus = $"Sending '{textToShow}' to Port {target.GamePort}...";

        try
        {
            int targetPort = target.WorkerPort > 0 ? target.WorkerPort : target.GamePort;
            var (success, channel, message) = await _fleetService.SendWorkerCommandAsync(
                target.HostAddress,
                target.ControlPort,
                targetPort,
                command,
                target.AuthToken);

            LastCommandStatus = success
                ? $"✓ Delivered to Port {target.GamePort} ({channel})"
                : $"✗ Failed: {message}";

            if (success && IsMatchScopedCommand(command))
            {
                if (string.Equals(target.ServerStatus, "Available", StringComparison.OrdinalIgnoreCase))
                {
                    GameServerInstanceDetailViewModel? activeInstance = AllChatInstances.FirstOrDefault(i =>
                        i != target &&
                        i.IsRunning &&
                        IsActiveMatchStatus(i.ServerStatus));

                    if (activeInstance != null)
                    {
                        _pendingActiveSwitchTarget = activeInstance;
                        _pendingActiveSwitchCommand = command;
                        IdleCommandTargetWarningText =
                            $"⚠️ Port {target.GamePort} is currently Idle (Available). Active match detected on Port {activeInstance.GamePort} ({activeInstance.CleanServerName}).";
                        SwitchAndSendButtonLabel = $"Switch & Send to {activeInstance.GamePort}";
                        HasIdleCommandTargetWarning = true;
                    }
                    else
                    {
                        DismissIdleTargetWarning();
                    }
                }
                else
                {
                    DismissIdleTargetWarning();
                }
            }
        }
        catch (Exception ex)
        {
            LastCommandStatus = $"✗ Error: {ex.Message}";
        }
        finally
        {
            IsSendingCommand = false;
        }

        await PollSelectedChatInstanceLogsAsync();
    }

    private static bool IsMatchScopedCommand(string command)
    {
        string trimmed = command.Trim();
        return trimmed.StartsWith("ServerChat", StringComparison.OrdinalIgnoreCase) ||
               trimmed.StartsWith("AddFakePlayer", StringComparison.OrdinalIgnoreCase) ||
               trimmed.StartsWith("GiveGold", StringComparison.OrdinalIgnoreCase) ||
               trimmed.StartsWith("gold ", StringComparison.OrdinalIgnoreCase) ||
               trimmed.Equals("gold", StringComparison.OrdinalIgnoreCase) ||
               trimmed.StartsWith("ListPlayers", StringComparison.OrdinalIgnoreCase) ||
               trimmed.StartsWith("announce", StringComparison.OrdinalIgnoreCase) ||
               trimmed.StartsWith("say ", StringComparison.OrdinalIgnoreCase) ||
               trimmed.StartsWith("broadcast ", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsActiveMatchStatus(string? status)
    {
        if (string.IsNullOrWhiteSpace(status)) return false;
        return status.Equals("InMatch", StringComparison.OrdinalIgnoreCase) ||
               status.Equals("InGame", StringComparison.OrdinalIgnoreCase) ||
               status.Equals("Match", StringComparison.OrdinalIgnoreCase) ||
               status.Equals("Occupied", StringComparison.OrdinalIgnoreCase);
    }

    [RelayCommand]
    public async Task SwitchAndSendToActiveServerAsync()
    {
        GameServerInstanceDetailViewModel? activeTarget = _pendingActiveSwitchTarget;
        string pendingCommand = _pendingActiveSwitchCommand;
        DismissIdleTargetWarning();

        if (activeTarget == null || string.IsNullOrWhiteSpace(pendingCommand)) return;

        SelectChatInstance(activeTarget);
        await DispatchCommandToInstanceAsync(activeTarget, pendingCommand);
    }

    [RelayCommand]
    public void DismissIdleTargetWarning()
    {
        HasIdleCommandTargetWarning = false;
        IdleCommandTargetWarningText = string.Empty;
        SwitchAndSendButtonLabel = string.Empty;
        _pendingActiveSwitchTarget = null;
        _pendingActiveSwitchCommand = string.Empty;
    }

    [RelayCommand]
    public void ClearSelectedChat()
    {
        SelectedChatInstance?.ClearChat();
    }

    [RelayCommand]
    public async Task CopySelectedServerLogsAsync()
    {
        if (SelectedChatInstance == null)
        {
            LastCommandStatus = "No server selected to copy logs.";
            return;
        }

        string text = SelectedChatInstance.GetLogsAsPlainText();
        if (string.IsNullOrWhiteSpace(text))
        {
            LastCommandStatus = "No logs recorded for this server yet.";
            return;
        }

        try
        {
            if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime
                {
                    MainWindow.Clipboard: { } clipboard
                })
            {
                await clipboard.SetTextAsync(text);
                int lineCount = text.Split('\n').Length;
                LastCommandStatus = $"✓ Copied {lineCount} log lines from {SelectedChatInstance.ServerName} to clipboard!";
                SelectedChatInstance.AddSystemMessage($"📋 Copied {lineCount} log lines to clipboard.");
            }
            else
            {
                LastCommandStatus = "Clipboard not available in current environment.";
            }
        }
        catch (Exception ex)
        {
            LastCommandStatus = $"Failed to copy logs: {ex.Message}";
        }
    }

    private CancellationTokenSource? _activeChatStreamCts;
    private Task? _activeChatStreamTask;
    private int _streamedChatPort;
    private int _streamedControlPort;
    private volatile bool _isChatSseActive;

    private void StartOrSwitchChatLogStream(GameServerInstanceDetailViewModel? target)
    {
        if (target == null ||
            !IsChatSurfaceActive ||
            !target.IsRunning ||
            _fleetService.IsEndpointKnownOffline(target.HostAddress, target.ControlPort))
        {
            try
            {
                _activeChatStreamCts?.Cancel();
            }
            catch (ObjectDisposedException ex)
            {
                System.Diagnostics.Debug.WriteLine($"[GameServersViewModel] Stop chat stream CTS already disposed: {ex.Message}");
            }
            _activeChatStreamCts = null;
            _activeChatStreamTask = null;
            _streamedChatPort = 0;
            _streamedControlPort = 0;
            _isChatSseActive = false;
            return;
        }

        int targetPort = target.WorkerPort > 0 ? target.WorkerPort : target.GamePort;
        if (_streamedChatPort == targetPort && _streamedControlPort == target.ControlPort && _isChatSseActive && _activeChatStreamTask is { IsCompleted: false })
        {
            return;
        }

        try
        {
            _activeChatStreamCts?.Cancel();
        }
        catch (ObjectDisposedException ex)
        {
            System.Diagnostics.Debug.WriteLine($"[GameServersViewModel] Switch chat stream CTS already disposed: {ex.Message}");
        }

        CancellationTokenSource cts = new();
        _activeChatStreamCts = cts;
        _streamedChatPort = targetPort;
        _streamedControlPort = target.ControlPort;
        CancellationToken token = cts.Token;
        _activeChatStreamTask = Task.Run(() => StreamChatLogsLoopAsync(target, targetPort, token));
    }

    private async Task StreamChatLogsLoopAsync(GameServerInstanceDetailViewModel target, int targetPort, CancellationToken cancellationToken)
    {
        try
        {
            await foreach (CompelDedicatedServerLogLineDto log in _fleetService.StreamServerLogsAsync(
                target.HostAddress,
                target.ControlPort,
                targetPort,
                tail: 50,
                target.AuthToken,
                cancellationToken).ConfigureAwait(false))
            {
                _isChatSseActive = true;
                Dispatcher.UIThread.Post(() =>
                {
                    target.IngestLogs([log]);
                });
            }

            _isChatSseActive = false;
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
        {
            // Normal on cancellation or instance switch
        }
        catch (Exception)
        {
            _isChatSseActive = false;
        }
    }

    private async Task PollSelectedChatInstanceLogsAsync()
    {
        if (!IsChatSurfaceActive || SelectedChatInstance == null) return;

        if (!SelectedChatInstance.IsRunning ||
            _fleetService.IsEndpointKnownOffline(SelectedChatInstance.HostAddress, SelectedChatInstance.ControlPort))
        {
            StartOrSwitchChatLogStream(null);
            return;
        }

        if (_activeChatStreamTask == null || _activeChatStreamTask.IsCompleted || !_isChatSseActive)
        {
            StartOrSwitchChatLogStream(SelectedChatInstance);
        }

        // If SSE stream is active and pushing lines reactively, skip redundant HTTP polling
        if (_isChatSseActive) return;

        var target = SelectedChatInstance;
        try
        {
            int targetPort = target.WorkerPort > 0 ? target.WorkerPort : target.GamePort;
            var (success, logs, _) = await _fleetService.GetServerLogsAsync(
                target.HostAddress,
                target.ControlPort,
                targetPort,
                tail: 100,
                target.AuthToken);

            if (success && logs.Count > 0)
            {
                target.IngestLogs(logs);
            }
        }
        catch { }
    }

    private void HandleOrchestratorLog(LogMessage log)
    {
        if (log.ServiceName.Contains("COMPEL", StringComparison.OrdinalIgnoreCase) ||
            log.ServiceName.Contains("GAME SERVER", StringComparison.OrdinalIgnoreCase) ||
            log.Message.Contains("FleetSupervisor", StringComparison.OrdinalIgnoreCase) ||
            log.Message.Contains("ActiveWorkerCount", StringComparison.OrdinalIgnoreCase) ||
            log.Message.Contains("hon_x64", StringComparison.OrdinalIgnoreCase))
        {
            ResetPollBackoff();
            DebounceFleetRefresh();
        }
    }

    private void HandleServiceStateChanged(string serviceName, ServerState state)
    {
        if (serviceName.Contains("COMPEL", StringComparison.OrdinalIgnoreCase) ||
            serviceName.Contains("GAME SERVER", StringComparison.OrdinalIgnoreCase))
        {
            ResetPollBackoff();
            DebounceFleetRefresh();
        }
    }

    private void HandleClusterStateChanged(ServerState state)
    {
        if (state is ServerState.Starting or ServerState.Running)
        {
            ResetPollBackoff();
            DebounceFleetRefresh();
        }
    }

    public void ResetPollBackoff()
    {
        _consecutiveOfflinePolls = 0;
        if (Dispatcher.UIThread.CheckAccess())
        {
            _fleetPollTimer.Interval = TimeSpan.FromSeconds(3);
        }
        else
        {
            Dispatcher.UIThread.Post(() => _fleetPollTimer.Interval = TimeSpan.FromSeconds(3));
        }
    }

    private TimeSpan ComputeNextPollInterval()
    {
        // 1. High-frequency / active state: starting, restarting, or active servers
        bool hasActiveActivity = AllChatInstances.Any(i => i.ServerStatus is "Starting" or "Restarting");
        if (hasActiveActivity)
        {
            _consecutiveOfflinePolls = 0;
            return TimeSpan.FromSeconds(3);
        }

        // 2. Normal online state: any host is online and responsive, or cluster is running
        bool anyHostOnline = _allHosts.Any(h => h.PingMs >= 0);
        bool clusterRunning = _orchestrator?.CurrentClusterState == ServerState.Running;
        if (anyHostOnline || clusterRunning)
        {
            _consecutiveOfflinePolls = 0;
            return TimeSpan.FromSeconds(5);
        }

        // 3. Idle / all nodes offline: progressive exponential backoff
        // Poll intervals: 5s -> 10s -> 20s -> 30s -> 60s max
        _consecutiveOfflinePolls++;
        int backoffSeconds = _consecutiveOfflinePolls switch
        {
            1 => 5,
            2 => 10,
            3 => 20,
            4 => 30,
            _ => 60
        };
        return TimeSpan.FromSeconds(backoffSeconds);
    }

    private void DebounceFleetRefresh()
    {
        ResetPollBackoff();
        DateTimeOffset now = TimeProvider.System.GetUtcNow();
        if ((now - _lastDebouncedRefresh).TotalMilliseconds < 1200) return;
        _lastDebouncedRefresh = now;
        _ = Dispatcher.UIThread.InvokeAsync(() => RefreshFleetQuietlyAsync(forceDiscovery: true));
    }

    public TimeSpan CurrentPollInterval => _fleetPollTimer.Interval;

    public async Task AutoPollFleetAsync()
    {
        await RefreshFleetQuietlyAsync(forceDiscovery: false);

        if (Dispatcher.UIThread.CheckAccess())
        {
            _fleetPollTimer.Interval = ComputeNextPollInterval();
        }

        if (IsChatSurfaceActive && SelectedChatInstance != null)
        {
            await PollSelectedChatInstanceLogsAsync();
        }
    }

    public void OnNavigatedTo()
    {
        ResetPollBackoff();
        _ = RefreshFleetQuietlyAsync(forceDiscovery: true);
    }

    public async Task RefreshFleetQuietlyAsync(bool forceDiscovery = false)
    {
        if (IsRefreshing) return;
        try
        {
            DateTimeOffset now = TimeProvider.System.GetUtcNow();
            bool shouldRunDiscovery = forceDiscovery ||
                                     _allHosts.Count == 0 ||
                                     (now - _lastFullDiscoveryTime) >= _fullDiscoveryInterval;

            if (shouldRunDiscovery)
            {
                _lastFullDiscoveryTime = now;
                List<GameServerHostEntry> entries = await _fleetService.GetDiscoveredHostsAsync();
                bool addedNewHost = false;
                foreach (var entry in entries)
                {
                    if (!_allHosts.Any(h => h.HostAddress == entry.HostAddress && h.Port == entry.Port))
                    {
                        _allHosts.Add(new GameServerHostItemViewModel(entry, _fleetService));
                        addedNewHost = true;
                    }
                }

                if (addedNewHost)
                {
                    ApplyFilters();
                }
            }

            List<Task> pingTasks = [.. _allHosts.Select(h => h.RefreshAsync())];
            await Task.WhenAll(pingTasks);
            UpdateSummaryMetrics();
            RebuildChatInstancesList();
        }
        catch { }
    }

    // --- Fleet Broadcast Commands ---
    [RelayCommand]
    public async Task StartAllAsync()
    {
        ResetPollBackoff();
        FleetStatusMessage = "Broadcasting START to all online nodes...";
        List<Task> tasks = [.. _allHosts.Where(h => h.PingMs >= 0).Select(h => h.StartAsync())];
        await Task.WhenAll(tasks);
        UpdateSummaryMetrics();
        RebuildChatInstancesList();
        FleetStatusMessage = "Broadcasted START to all nodes.";
    }

    [RelayCommand]
    public async Task StopAllAsync()
    {
        ResetPollBackoff();
        FleetStatusMessage = "Broadcasting STOP to all online nodes...";
        List<Task> tasks = [.. _allHosts.Where(h => h.PingMs >= 0).Select(h => h.StopAsync())];
        await Task.WhenAll(tasks);
        UpdateSummaryMetrics();
        RebuildChatInstancesList();
        FleetStatusMessage = "Broadcasted STOP to all nodes.";
    }

    [RelayCommand]
    public async Task RestartAllAsync()
    {
        ResetPollBackoff();
        FleetStatusMessage = "Broadcasting RESTART to all online nodes...";
        List<Task> tasks = [.. _allHosts.Where(h => h.PingMs >= 0).Select(h => h.RestartAsync())];
        await Task.WhenAll(tasks);
        UpdateSummaryMetrics();
        RebuildChatInstancesList();
        FleetStatusMessage = "Broadcasted RESTART to all nodes.";
    }

    [RelayCommand]
    public async Task SyncAllAsync()
    {
        ResetPollBackoff();
        FleetStatusMessage = "Broadcasting CDN SYNC to all nodes...";
        List<Task> tasks = [.. _allHosts.Where(h => h.PingMs >= 0).Select(h => h.SyncAsync())];
        await Task.WhenAll(tasks);
        UpdateSummaryMetrics();
        FleetStatusMessage = "Broadcasted CDN SYNC to all nodes.";
    }

    [RelayCommand]
    public void ToggleAddHostDialog()
    {
        ShowAddHostDialog = !ShowAddHostDialog;
    }

    [RelayCommand]
    public async Task SaveNewHostAsync()
    {
        if (string.IsNullOrWhiteSpace(NewHostAddress)) return;

        GameServerHostEntry entry = new()
        {
            Name = string.IsNullOrWhiteSpace(NewHostName) ? NewHostAddress : NewHostName,
            HostAddress = NewHostAddress.Trim(),
            Port = NewHostPort > 0 ? NewHostPort : 11234,
            Location = string.IsNullOrWhiteSpace(NewHostLocation) ? "USE" : NewHostLocation.ToUpperInvariant(),
            IsLocal = false
        };

        GameServerHostItemViewModel vm = new(entry, _fleetService);
        _allHosts.Add(vm);
        ApplyFilters();

        ShowAddHostDialog = false;
        NewHostName = string.Empty;
        NewHostAddress = string.Empty;

        await vm.RefreshAsync();
        UpdateSummaryMetrics();
        RebuildChatInstancesList();
    }

    private NexusSecrets _cachedSecrets = new();

    // --- Cloud-Native Container Launcher Methods & Commands ---
    partial void OnRevealContainerSecretsInCommandChanged(bool value)
    {
        UpdateGeneratedContainerCommand();
    }

    partial void OnSelectedContainerPlatformChanged(string value)
    {
        UpdateGeneratedContainerCommand();
    }

    partial void OnCompelPriorityModeChanged(string value)
    {
        UpdateGeneratedContainerCommand();
    }

    partial void OnCustomServerNamePrefixChanged(string value)
    {
        UpdateGeneratedContainerCommand();
    }

    partial void OnIsQueueArcadeContainerModeChanged(bool value)
    {
        _ = value;
        UpdateGeneratedContainerCommand();
    }

    public void UpdateGeneratedContainerCommand()
    {
        NexusSecrets secrets = _cachedSecrets ?? new NexusSecrets();
        secrets.CompelPriorityMode = CompelPriorityMode;
        if (!string.IsNullOrWhiteSpace(CustomServerNamePrefix))
        {
            secrets.CompelServerNamePrefix = CustomServerNamePrefix.Trim();
        }

        ContainerPlatform platform = SelectedContainerPlatform switch
        {
            "Minikube" => ContainerPlatform.Minikube,
            "Kubernetes" => ContainerPlatform.Kubernetes,
            "K3s (Agones)" => ContainerPlatform.K3sAgones,
            _ => ContainerPlatform.Docker
        };

        GeneratedContainerCommand = ContainerLaunchCommandBuilder.BuildCommand(
            secrets,
            platform,
            maskSecrets: !RevealContainerSecretsInCommand,
            isWindowsHost: RuntimeInformation.IsOSPlatform(OSPlatform.Windows),
            queueArcadeMode: IsQueueArcadeContainerMode);
    }

    [RelayCommand]
    public async Task ToggleContainerLauncherAsync()
    {
        ShowContainerLauncherDialog = !ShowContainerLauncherDialog;
        if (ShowContainerLauncherDialog)
        {
            await OpenContainerLauncherAsync();
        }
    }

    public async Task OpenContainerLauncherAsync()
    {
        ShowContainerLauncherDialog = true;
        if (_secretsManager != null)
        {
            _cachedSecrets = await _secretsManager.LoadSecretsAsync();
            if (!string.IsNullOrWhiteSpace(_cachedSecrets.CompelPriorityMode))
            {
                CompelPriorityMode = _cachedSecrets.CompelPriorityMode;
            }
            if (!string.IsNullOrWhiteSpace(_cachedSecrets.ContainerRuntimeTarget))
            {
                SelectedContainerPlatform = _cachedSecrets.ContainerRuntimeTarget;
            }
        }

        UpdateGeneratedContainerCommand();

        if (_dockerHealth != null)
        {
            IsDockerImageBuilt = await _dockerHealth.IsImageBuiltAsync();
            ContainerLaunchFeedback = IsDockerImageBuilt
                ? "Ready to launch."
                : "⚠️ Docker image not built yet. Click 'Build Image First'.";
        }
        else
        {
            ContainerLaunchFeedback = "Ready.";
        }
    }

    [RelayCommand]
    public void NavigateToSecretsConfig()
    {
        ShowContainerLauncherDialog = false;
        _onNavigateToSecretsConfig?.Invoke();
    }

    [RelayCommand]
    public async Task CopyContainerCommandAsync()
    {
        try
        {
            NexusSecrets secrets = _cachedSecrets ?? new NexusSecrets();
            secrets.CompelPriorityMode = CompelPriorityMode;

            ContainerPlatform platform = SelectedContainerPlatform switch
            {
                "Minikube" => ContainerPlatform.Minikube,
                "Kubernetes" => ContainerPlatform.Kubernetes,
                "K3s (Agones)" => ContainerPlatform.K3sAgones,
                _ => ContainerPlatform.Docker
            };

            string fullCommand = ContainerLaunchCommandBuilder.BuildCommand(
                secrets,
                platform,
                maskSecrets: false,
                isWindowsHost: RuntimeInformation.IsOSPlatform(OSPlatform.Windows),
                queueArcadeMode: IsQueueArcadeContainerMode);

            if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime
                {
                    MainWindow.Clipboard: { } clipboard
                })
            {
                await clipboard.SetTextAsync(fullCommand);
                ContainerLaunchFeedback = $"✓ Copied {SelectedContainerPlatform} command to clipboard!";
            }
            else
            {
                ContainerLaunchFeedback = "Clipboard not available.";
            }
        }
        catch (Exception ex)
        {
            ContainerLaunchFeedback = $"Failed to copy command: {ex.Message}";
        }
    }

    [RelayCommand]
    public async Task LaunchContainerInDockerAsync()
    {
        if (_dockerHealth == null || IsLaunchingContainer) return;
        IsLaunchingContainer = true;
        ContainerLaunchFeedback = "Launching dedicated server container in Docker...";

        try
        {
            bool imageBuilt = await _dockerHealth.IsImageBuiltAsync();
            if (!imageBuilt)
            {
                IsDockerImageBuilt = false;
                ContainerLaunchFeedback = "⚠️ Docker image 'projectkongor/compel-dedicated:latest' not built yet. Click 'Build Image First'.";
                return;
            }

            IsDockerImageBuilt = true;

            if (_secretsManager != null)
            {
                _cachedSecrets = await _secretsManager.LoadSecretsAsync();
            }
            NexusSecrets secrets = _cachedSecrets ?? new NexusSecrets();

            string repoRoot = DockerHealthService.ResolveRepositoryRoot();
            string gamesServerDir = Path.Combine(repoRoot, "games", "server");
            string serverDir = !string.IsNullOrWhiteSpace(secrets.CompelPath) && Directory.Exists(secrets.CompelPath)
                ? secrets.CompelPath
                : (Directory.Exists(gamesServerDir) ? gamesServerDir : Path.Combine(repoRoot, "server"));
            string artefactsDir = Path.Combine(repoRoot, "artefacts");
            try { Directory.CreateDirectory(artefactsDir); } catch { }

            string serverPathNorm = Path.GetFullPath(serverDir).Replace('\\', '/');
            string artefactsPathNorm = Path.GetFullPath(artefactsDir).Replace('\\', '/');
            string serverVol = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
                ? $"\"{serverPathNorm}:/app/server:ro\""
                : $"{serverPathNorm}:/app/server:ro";
            string artefactsVol = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
                ? $"\"{artefactsPathNorm}:/app/artefacts:rw\""
                : $"{artefactsPathNorm}:/app/artefacts:rw";

            int instances = IsQueueArcadeContainerMode
                ? (secrets.QueueArcadeInstances > 0 ? secrets.QueueArcadeInstances : 2)
                : (secrets.CompelInstances > 0 ? secrets.CompelInstances : 2);

            // Auto-detect missing or stale Docker image (e.g. when linux_native_guard.cpp or entrypoint.sh is newer than image)
            bool isStaleOrMissing = !IsDockerImageBuilt || await _dockerHealth.IsDedicatedImageStaleAsync(repoRoot);
            if (isStaleOrMissing)
            {
                string dockerfilePath = Path.Combine(repoRoot, "infra", "docker", "Dockerfile.dedicated");
                if (File.Exists(dockerfilePath))
                {
                    ContainerLaunchFeedback = "⏳ Dedicated server source updated — rebuilding Docker image (1-2 min)...";
                    (bool buildOk, string buildOut) = await _dockerHealth.BuildImageAsync(dockerfilePath, repoRoot);
                    if (!buildOk)
                    {
                        ContainerLaunchFeedback = $"⚠️ Image rebuild failed: {buildOut}";
                        return;
                    }
                    IsDockerImageBuilt = true;
                }
            }

            // Docker Tier D binds to port offset 100-199 (11334); Queue Arcade Tier Q binds to port offset 250-299 (11484).
            Docker.DotNet.Models.CreateContainerParameters createParams = ContainerLaunchCommandBuilder.BuildCreateContainerParameters(
                secrets,
                CompelPriorityMode,
                instances,
                serverPathNorm,
                artefactsPathNorm,
                RuntimeInformation.IsOSPlatform(OSPlatform.Windows),
                queueArcadeMode: IsQueueArcadeContainerMode);

            (bool success, string output) = await _dockerHealth.LaunchContainerAsync(createParams);
            ContainerLaunchFeedback = success ? $"✓ {output}" : $"⚠️ {output}";

            if (success)
            {
                int targetControlPort = IsQueueArcadeContainerMode
                    ? 11484
                    : (secrets.CompelControlPlanePort is >= 11300 and < 11400
                        ? secrets.CompelControlPlanePort
                        : 11334);

                if (_fleetService != null)
                {
                    ContainerLaunchFeedback = $"⏳ {output} — waiting for COMPEL control plane on 127.0.0.1:{targetControlPort}...";
                    bool controlPlaneReady = false;
                    for (int attempt = 1; attempt <= 25; attempt++)
                    {
                        await Task.Delay(1200);
                        var pingResult = await _fleetService.PingHostAsync("127.0.0.1", targetControlPort);
                        if (pingResult.Success)
                        {
                            controlPlaneReady = true;
                            break;
                        }
                    }

                    await LoadAndRefreshFleetAsync();
                    ContainerLaunchFeedback = controlPlaneReady
                        ? $"✓ {output} — COMPEL online on 127.0.0.1:{targetControlPort}!"
                        : $"✓ {output} — container initializing on 127.0.0.1:{targetControlPort}.";
                }
                else
                {
                    await Task.Delay(2500);
                    await LoadAndRefreshFleetAsync();
                }
            }
        }
        catch (Exception ex)
        {
            ContainerLaunchFeedback = $"Error launching container: {ex.Message}";
        }
        finally
        {
            IsLaunchingContainer = false;
        }
    }

    [RelayCommand]
    public async Task StopDockerContainerAsync()
    {
        if (_dockerHealth == null) return;
        string targetContainer = IsQueueArcadeContainerMode
            ? ContainerLaunchCommandBuilder.QueueArcadeContainerName
            : ContainerLaunchCommandBuilder.RankedContainerName;
        ContainerLaunchFeedback = $"Stopping container '{targetContainer}'...";
        (bool success, string output) = await _dockerHealth.StopContainerAsync(targetContainer);
        ContainerLaunchFeedback = success ? $"✓ {output}" : $"⚠️ {output}";
        if (success)
        {
            await RefreshFleetQuietlyAsync();
        }
    }

    [RelayCommand]
    public async Task BuildDockerImageAsync()
    {
        if (_dockerHealth == null || IsLaunchingContainer) return;
        IsLaunchingContainer = true;
        ContainerLaunchFeedback = "Building dedicated server Docker image (this may take 1-2 minutes)...";

        try
        {
            string repoRoot = DockerHealthService.ResolveRepositoryRoot();
            string dockerfilePath = Path.Combine(repoRoot, "infra", "docker", "Dockerfile.dedicated");
            string contextPath = repoRoot;
            var (success, output) = await _dockerHealth.BuildImageAsync(dockerfilePath, contextPath);
            if (success)
            {
                IsDockerImageBuilt = true;
                ContainerLaunchFeedback = "✓ Image built successfully! You can now launch your container.";
            }
            else
            {
                ContainerLaunchFeedback = $"⚠️ {output}";
            }
        }
        catch (Exception ex)
        {
            ContainerLaunchFeedback = $"Error building image: {ex.Message}";
        }
        finally
        {
            IsLaunchingContainer = false;
        }
    }

    [RelayCommand]
    public async Task LaunchLocalServerFromChatAsync()
    {
        // Local Tier L (port 11234 / offset 0) and Docker Tier D (port 11334 / offset 100) co-exist concurrently.
        if (_orchestrator != null)
        {
            await _orchestrator.LaunchGameServerAsync();
        }

        // Fast readiness probe: poll for up to 3.5 seconds until COMPEL control plane is online
        for (int i = 0; i < 14; i++)
        {
            await Task.Delay(250);
            var ping = await _fleetService.PingHostAsync("127.0.0.1", 11234);
            if (ping.Success)
            {
                await Task.Delay(200);
                break;
            }
        }

        await LoadAndRefreshFleetAsync();
    }

    [RelayCommand]
    public async Task OpenContainerHubFromChatAsync()
    {
        IsChatModeActive = false;
        await OpenContainerLauncherAsync();
    }

    [RelayCommand]
    public async Task DeployK3sAgonesAsync()
    {
        if (IsLaunchingContainer) return;
        IsLaunchingContainer = true;
        ContainerLaunchFeedback = "Deploying dedicated game server fleet to K3s + Agones...";

        try
        {
            if (_secretsManager != null)
            {
                _cachedSecrets = await _secretsManager.LoadSecretsAsync();
            }
            NexusSecrets secrets = _cachedSecrets ?? new NexusSecrets();
            string repoRoot = DockerHealthService.ResolveRepositoryRoot();
            string fleetYamlPath = Path.Combine(repoRoot, "infra", "k8s", "agones", "fleet.yaml");
            string kubectlCli = ResolveKubectlCliPath();

            // 1. Provision isolated K3s host credentials secret in k3s cluster
            string user = IsQueueArcadeContainerMode
                ? ContainerLaunchCommandBuilder.ResolveQueueArcadeHostUsername(secrets.HostAccountUserName, "guccihost_arc")
                : ContainerLaunchCommandBuilder.ResolveFleetHostUsername(
                    secrets.HostAccountUserName,
                    ContainerPlatform.K3sAgones,
                    "guccihost_k3s");
            string secretName = IsQueueArcadeContainerMode
                ? "kongor-arcade-host-credentials"
                : "kongor-host-credentials";
            string pass = !string.IsNullOrWhiteSpace(secrets.HostAccountPassword) ? secrets.HostAccountPassword : "DevPassword123";
            string tok = !string.IsNullOrWhiteSpace(secrets.CompelAuthenticationToken) ? secrets.CompelAuthenticationToken : "compel_token";

            try
            {
                using Process secretProc = new();
                secretProc.StartInfo = new ProcessStartInfo
                {
                    FileName = kubectlCli,
                    Arguments = $"create secret generic {secretName} --from-literal=username=\"{user}\" --from-literal=password=\"{pass}\" --from-literal=token=\"{tok}\" --dry-run=client -o yaml",
                    WorkingDirectory = repoRoot,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                secretProc.Start();
                string secretYaml = await secretProc.StandardOutput.ReadToEndAsync();
                await secretProc.WaitForExitAsync();

                if (!string.IsNullOrWhiteSpace(secretYaml))
                {
                    using Process applySecretProc = new();
                    applySecretProc.StartInfo = new ProcessStartInfo
                    {
                        FileName = kubectlCli,
                        Arguments = "apply -f -",
                        WorkingDirectory = repoRoot,
                        RedirectStandardInput = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    };
                    applySecretProc.Start();
                    await applySecretProc.StandardInput.WriteAsync(secretYaml);
                    applySecretProc.StandardInput.Close();
                    await applySecretProc.WaitForExitAsync();
                }
            }
            catch
            {
                // Secret application best-effort
            }

            // 2. Apply Agones GameServer Fleet manifest
            using Process applyProc = new();
            applyProc.StartInfo = new ProcessStartInfo
            {
                FileName = kubectlCli,
                Arguments = $"apply -f \"{fleetYamlPath}\"",
                WorkingDirectory = repoRoot,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            applyProc.Start();
            string stdOut = await applyProc.StandardOutput.ReadToEndAsync();
            string stdErr = await applyProc.StandardError.ReadToEndAsync();
            await applyProc.WaitForExitAsync();

            if (applyProc.ExitCode != 0)
            {
                ContainerLaunchFeedback = $"⚠️ kubectl apply failed: {stdErr.Trim()}";
                return;
            }

            // 3. Recycle Agones fleet pods so updated credentials/image/overrides take effect cleanly
            using (Process recycleProc = new())
            {
                recycleProc.StartInfo = new ProcessStartInfo
                {
                    FileName = kubectlCli,
                    Arguments = "delete pod -l agones.dev/role=gameserver --ignore-not-found=true",
                    WorkingDirectory = repoRoot,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                recycleProc.Start();
                await recycleProc.WaitForExitAsync();
            }

            ContainerLaunchFeedback = "✓ K3s + Agones GameServer fleet deployed successfully!";
            await LoadAndRefreshFleetAsync();
        }
        catch (Exception ex)
        {
            ContainerLaunchFeedback = $"Error deploying to K3s: {ex.Message}";
        }
        finally
        {
            IsLaunchingContainer = false;
        }
    }

    private static string ResolveKubectlCliPath()
    {
        string dockerBin = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "Docker", "Docker", "resources", "bin", "kubectl.exe");
        if (File.Exists(dockerBin)) return dockerBin;
        return "kubectl";
    }

    [RelayCommand]
    public async Task StartFirstAvailableHostFromChatAsync()
    {
        var host = _allHosts.FirstOrDefault(h => h.PingMs >= 0) ?? _allHosts.FirstOrDefault();
        if (host != null)
        {
            await host.StartAsync();
            await LoadAndRefreshFleetAsync();
        }
    }

    public static void SyncCollection<T, TKey>(ObservableCollection<T> target, IList<T> desired, Func<T, TKey> keySelector)
    {
        var desiredKeys = new HashSet<TKey>(desired.Select(keySelector));
        for (int k = target.Count - 1; k >= 0; k--)
        {
            if (!desiredKeys.Contains(keySelector(target[k])))
            {
                target.RemoveAt(k);
            }
        }

        for (int k = 0; k < desired.Count; k++)
        {
            var desiredItem = desired[k];
            var desiredKey = keySelector(desiredItem);
            int existingIdx = -1;
            for (int j = k; j < target.Count; j++)
            {
                if (EqualityComparer<TKey>.Default.Equals(keySelector(target[j]), desiredKey))
                {
                    existingIdx = j;
                    break;
                }
            }

            if (existingIdx == k)
            {
                continue;
            }

            if (existingIdx > k)
            {
                target.Move(existingIdx, k);
            }
            else
            {
                target.Insert(k, desiredItem);
            }
        }
    }

    public static (SmartInputKind Kind, string CommandToSend, string DisplayText) ClassifySmartHybridInput(string rawInput)
    {
        if (string.IsNullOrWhiteSpace(rawInput))
        {
            return (SmartInputKind.ConsoleCommand, string.Empty, string.Empty);
        }

        string trimmed = rawInput.Trim();

        // 1. Explicit escape prefixes: / or > always force raw console command
        if (trimmed.StartsWith('/') || trimmed.StartsWith('>'))
        {
            string clean = trimmed[1..].Trim();
            return (SmartInputKind.ConsoleCommand, clean, trimmed);
        }

        // 2. Extract first token and strip optional '=' assignment
        int firstSpace = trimmed.IndexOf(' ');
        string firstToken = firstSpace > 0 ? trimmed[..firstSpace] : trimmed;
        int eqIdx = firstToken.IndexOf('=');
        if (eqIdx > 0)
        {
            firstToken = firstToken[..eqIdx];
        }

        // 3. Known cvar prefixes
        if (firstToken.StartsWith("svr_", StringComparison.OrdinalIgnoreCase) ||
            firstToken.StartsWith("host_", StringComparison.OrdinalIgnoreCase) ||
            firstToken.StartsWith("g_", StringComparison.OrdinalIgnoreCase) ||
            firstToken.StartsWith("net_", StringComparison.OrdinalIgnoreCase) ||
            firstToken.StartsWith("sys_", StringComparison.OrdinalIgnoreCase) ||
            firstToken.StartsWith("vid_", StringComparison.OrdinalIgnoreCase) ||
            firstToken.StartsWith("fs_", StringComparison.OrdinalIgnoreCase) ||
            firstToken.StartsWith("sound_", StringComparison.OrdinalIgnoreCase) ||
            firstToken.StartsWith("ui_", StringComparison.OrdinalIgnoreCase) ||
            firstToken.StartsWith("cl_", StringComparison.OrdinalIgnoreCase) ||
            firstToken.StartsWith("con_", StringComparison.OrdinalIgnoreCase) ||
            firstToken.StartsWith("cg_", StringComparison.OrdinalIgnoreCase) ||
            firstToken.StartsWith("cam_", StringComparison.OrdinalIgnoreCase) ||
            firstToken.StartsWith("res_", StringComparison.OrdinalIgnoreCase) ||
            firstToken.StartsWith("mem_", StringComparison.OrdinalIgnoreCase))
        {
            return (SmartInputKind.ConsoleCommand, trimmed, trimmed);
        }

        // 4. Known console commands
        if (s_knownConsoleCommands.Contains(firstToken))
        {
            return (SmartInputKind.ConsoleCommand, trimmed, trimmed);
        }

        // 5. Default: Plain conversational text routed to players via ServerChat "<msg>"
        string escaped = trimmed.Replace("\"", "\\\"");
        string serverChatCmd = $"ServerChat \"{escaped}\"";
        return (SmartInputKind.ServerChat, serverChatCmd, trimmed);
    }

    private static readonly HashSet<string> s_knownConsoleCommands = new(StringComparer.OrdinalIgnoreCase)
    {
        "serverchat",
        "serverstatus",
        "addfakeplayer",
        "removefakeplayers",
        "givegold",
        "gold",
        "listplayers",
        "announce",
        "say",
        "broadcast",
        "kick",
        "ban",
        "status",
        "quit",
        "exit",
        "stop",
        "restart",
        "start",
        "pause",
        "unpause",
        "uptime",
        "info",
        "version",
        "connect",
        "disconnect",
        "echo",
        "clear",
        "help",
        "list",
        "cmd",
        "cvar",
        "exec",
        "set",
        "get",
        "ping",
        "msg",
        "tell",
        "whisper",
        "rematch",
        "resetgame",
        "startgame",
        "endgame",
        "setstatus",
        "spawnbot",
        "killbot",
        "kill",
        "dump",
        "profile",
        "reconnect"
    };
}

public enum SmartInputKind
{
    ConsoleCommand,
    ServerChat
}
