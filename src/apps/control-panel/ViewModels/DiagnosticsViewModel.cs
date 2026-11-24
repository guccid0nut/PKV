// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NEXUS.ControlPanel.GUI.Services;
using NEXUS.ControlPanel.GUI.Services.Models;

namespace NEXUS.ControlPanel.GUI.ViewModels;

public sealed partial class DiagnosticsViewModel : ViewModelBase
{
    private readonly IDatabaseHealthService? _databaseHealth;
    private readonly IDockerHealthService _dockerHealth;
    private readonly IPortScannerService _portScanner;
    private readonly ICockpitAppBridgeService _cockpitBridge;
    private readonly Func<IReadOnlyList<CockpitServerPipeHint>>? _getServerPipeHints;

    [ObservableProperty] private string _dockerStatusMessage = "Checking...";

    [ObservableProperty] private string _dotnetInfo = string.Empty;

    [ObservableProperty] private bool _isDockerHealthy;

    [ObservableProperty] private bool _isPyroscopeHealthy;

    [ObservableProperty] private bool _isScanning;

    [ObservableProperty] private string _osInfo = string.Empty;

    [ObservableProperty] private ObservableCollection<PortStatus> _portStatuses = [];

    [ObservableProperty] private string _pyroscopeStatusColor = "#94A3B8";

    [ObservableProperty] private string _pyroscopeStatusMessage = "Checking Pyroscope...";

    [ObservableProperty] private string _pyroscopeUrl = "http://localhost:4040";

    [ObservableProperty] private bool _isDotnetMonitorHealthy;

    [ObservableProperty] private string _dotnetMonitorStatusColor = "#94A3B8";

    [ObservableProperty] private string _dotnetMonitorStatusMessage = "Checking dotnet-monitor...";

    [ObservableProperty] private string _dotnetMonitorUrl = "http://localhost:52323";

    // --- Live TAO / Native Client IPC, WILLOWMAKER IPC, Server Pipes & Core Cluster Readiness ---
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowNativeClientPipeCard))]
    [NotifyPropertyChangedFor(nameof(ShowWillowmakerPipeCard))]
    [NotifyPropertyChangedFor(nameof(ShowServerPipesCard))]
    [NotifyPropertyChangedFor(nameof(HasNoVisibleIpcPipesMessage))]
    private bool _showDisconnectedPipes;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowNativeClientPipeCard))]
    [NotifyPropertyChangedFor(nameof(HasNoVisibleIpcPipesMessage))]
    private bool _isNativeClientPipeConnected;

    [ObservableProperty] private string _nativeClientPipeStatusMessage = "Checking Game Client Native Pipe...";

    [ObservableProperty] private string _nativeClientPipeStatusColor = "#94A3B8";

    [ObservableProperty] private string _nativeClientPipePath = OperatingSystem.IsWindows()
        ? @"\\.\pipe\kongor-native-client"
        : "/tmp/kongor-native-client.sock";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowWillowmakerPipeCard))]
    [NotifyPropertyChangedFor(nameof(HasNoVisibleIpcPipesMessage))]
    private bool _isWillowmakerPipeConnected;

    [ObservableProperty] private string _willowmakerPipeLabel = "🎯 WILLOWMAKER Launcher IPC";

    [ObservableProperty] private string _willowmakerPipeStatusMessage = "Checking WILLOWMAKER IPC Pipe...";

    [ObservableProperty] private string _willowmakerPipeStatusColor = "#94A3B8";

    [ObservableProperty] private string _willowmakerPipePath = CockpitAppBridgeService.FormatPipeDisplayPath(
        CockpitAppBridgeService.DefaultWillowmakerPipeName);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowServerPipesCard))]
    [NotifyPropertyChangedFor(nameof(HasNoVisibleIpcPipesMessage))]
    private bool _isServerPipesConnected;

    [ObservableProperty] private string _serverPipesStatusMessage = "Standby — No dedicated server pipes active";

    [ObservableProperty] private string _serverPipesStatusColor = "#94A3B8";

    [ObservableProperty] private string _serverPipesToolTip = "Connected dedicated game server IPC pipes";

    public bool ShowNativeClientPipeCard => IsNativeClientPipeConnected || ShowDisconnectedPipes;
    public bool ShowWillowmakerPipeCard => IsWillowmakerPipeConnected || ShowDisconnectedPipes;
    public bool ShowServerPipesCard => IsServerPipesConnected || ShowDisconnectedPipes;
    public bool HasNoVisibleIpcPipesMessage => !ShowNativeClientPipeCard && !ShowWillowmakerPipeCard && !ShowServerPipesCard;

    [ObservableProperty] private bool _isMasterServerHealthOk;

    [ObservableProperty] private string _masterServerHealthMessage = "Checking MasterServer /health (:55555)...";

    [ObservableProperty] private string _masterServerHealthColor = "#94A3B8";

    [ObservableProperty] private bool _isChatServerTcpReady;

    [ObservableProperty] private string _chatServerTcpMessage = "Checking ChatServer TCP (:11031)...";

    [ObservableProperty] private string _chatServerTcpColor = "#94A3B8";

    // --- Pre-Export Playtest Bundle Readiness (Zero-Write Dry-Run) ---
    [ObservableProperty] private bool _isRunningExportDryRun;

    [ObservableProperty] private bool _hasExportDryRunResults;

    [ObservableProperty] private string _exportDryRunSummary =
        "Run a zero-write pre-export verification across Windows, Linux, and macOS bundle stages (auto-selects a staging drive with >= 20 GB free).";

    [ObservableProperty] private string _exportDryRunSummaryColor = "#94A3B8";

    [ObservableProperty] private string _exportDryRunStagingInfo = string.Empty;

    [ObservableProperty] private ObservableCollection<ExportDryRunStageItem> _exportDryRunStages = [];

    public DiagnosticsViewModel(
        IPortScannerService portScanner,
        IDockerHealthService dockerHealth,
        IDatabaseHealthService? databaseHealth = null,
        ICockpitAppBridgeService? cockpitBridge = null,
        Func<IReadOnlyList<CockpitServerPipeHint>>? getServerPipeHints = null)
    {
        _portScanner = portScanner;
        _dockerHealth = dockerHealth;
        _databaseHealth = databaseHealth;
        _cockpitBridge = cockpitBridge ?? new CockpitAppBridgeService();
        _getServerPipeHints = getServerPipeHints;

        OsInfo = $"{RuntimeInformation.OSDescription} ({RuntimeInformation.OSArchitecture})";
        DotnetInfo = $".NET {Environment.Version}";

        _ = RunDiagnosticsAsync();
    }

    [RelayCommand]
    public async Task RunDiagnosticsAsync()
    {
        RunOnUiThread(() => IsScanning = true);

        try
        {
            Task<DockerStatus> dockerTask = _dockerHealth.CheckDockerStatusAsync();
            Task<List<PortStatus>> portsTask = _portScanner.ScanRequiredPortsAsync();
            Task cockpitTask = RefreshCockpitHealthAsync();

            DatabaseStatus? pStatus = null;
            DatabaseStatus? mStatus = null;
            if (_databaseHealth != null)
            {
                Task<DatabaseStatus> pyroTask = _databaseHealth.CheckPyroscopeHealthAsync();
                Task<DatabaseStatus> monTask = _databaseHealth.CheckDotnetMonitorHealthAsync();
                await Task.WhenAll(dockerTask, portsTask, cockpitTask, pyroTask, monTask);
                pStatus = await pyroTask;
                mStatus = await monTask;
            }
            else
            {
                await Task.WhenAll(dockerTask, portsTask, cockpitTask);
            }

            DockerStatus dStatus = await dockerTask;
            List<PortStatus> ports = await portsTask;

            RunOnUiThread(() =>
            {
                IsDockerHealthy = dStatus.IsRunning;
                DockerStatusMessage = dStatus.StatusMessage;

                if (pStatus != null)
                {
                    IsPyroscopeHealthy = pStatus.IsConnected;
                    PyroscopeStatusMessage = pStatus.StatusMessage;
                    PyroscopeStatusColor = pStatus.StatusColor;
                }

                if (mStatus != null)
                {
                    IsDotnetMonitorHealthy = mStatus.IsConnected;
                    DotnetMonitorStatusMessage = mStatus.StatusMessage;
                    DotnetMonitorStatusColor = mStatus.StatusColor;
                }

                PortStatuses.Clear();
                foreach (PortStatus p in ports) PortStatuses.Add(p);
            });
        }
        finally
        {
            RunOnUiThread(() => IsScanning = false);
        }
    }

    [RelayCommand]
    public async Task RefreshCockpitHealthAsync()
    {
        IReadOnlyList<CockpitServerPipeHint>? serverHints = _getServerPipeHints?.Invoke();
        Task<IpcPipeHealthStatus> nativeTask = _cockpitBridge.ProbeNativeClientPipeAsync();
        Task<WillowmakerCockpitStatus> willowTask = _cockpitBridge.ProbeWillowmakerIpcAsync();
        Task<CoreClusterReadinessStatus> clusterTask = _cockpitBridge.ProbeCoreClusterEndpointsAsync();
        Task<IReadOnlyList<CockpitPipeBadgeItem>> allPipesTask = _cockpitBridge.ProbeConnectedCockpitPipesAsync(
            knownServerInstances: serverHints,
            passiveNativeClientProbe: true);

        await Task.WhenAll(nativeTask, willowTask, clusterTask, allPipesTask);

        IpcPipeHealthStatus nativeStatus = await nativeTask;
        WillowmakerCockpitStatus willowStatus = await willowTask;
        CoreClusterReadinessStatus clusterStatus = await clusterTask;
        IReadOnlyList<CockpitPipeBadgeItem> connectedPipes = await allPipesTask;
        CockpitPipeBadgeItem? serverPipeBadge = connectedPipes.FirstOrDefault(p =>
            string.Equals(p.Key, "server-pipes", StringComparison.OrdinalIgnoreCase));

        RunOnUiThread(() =>
        {
            IsNativeClientPipeConnected = nativeStatus.IsConnected;
            NativeClientPipePath = nativeStatus.PipePath;
            NativeClientPipeStatusMessage = nativeStatus.StatusMessage;
            NativeClientPipeStatusColor = nativeStatus.StatusColor;

            bool isMcpBridge = willowStatus.IsConnected &&
                string.Equals(willowStatus.HostKind, "McpBridge", StringComparison.OrdinalIgnoreCase);
            WillowmakerPipeLabel = isMcpBridge ? "🤖 MCP IPC Bridge (willowmaker-ipc)" : "🎯 WILLOWMAKER Launcher IPC";
            IsWillowmakerPipeConnected = willowStatus.IsConnected;
            WillowmakerPipePath = willowStatus.PipePath;
            WillowmakerPipeStatusMessage = willowStatus.StatusMessage;
            WillowmakerPipeStatusColor = willowStatus.StatusColor;

            IsServerPipesConnected = serverPipeBadge is { IsConnected: true };
            ServerPipesStatusMessage = serverPipeBadge is { IsConnected: true }
                ? serverPipeBadge.StatusMessage
                : "Standby — No dedicated server pipes active";
            ServerPipesStatusColor = serverPipeBadge is { IsConnected: true }
                ? serverPipeBadge.StatusColor
                : "#94A3B8";
            ServerPipesToolTip = serverPipeBadge is { IsConnected: true }
                ? serverPipeBadge.ToolTipText
                : "Connected dedicated game server IPC pipes";

            IsMasterServerHealthOk = clusterStatus.IsMasterHealthOk;
            MasterServerHealthMessage = clusterStatus.MasterStatusMessage;
            MasterServerHealthColor = clusterStatus.MasterStatusColor;

            IsChatServerTcpReady = clusterStatus.IsChatTcpReady;
            ChatServerTcpMessage = clusterStatus.ChatStatusMessage;
            ChatServerTcpColor = clusterStatus.ChatStatusColor;
        });
    }

    [RelayCommand]
    public async Task VerifyExportReadinessAsync()
    {
        if (IsRunningExportDryRun)
        {
            return;
        }

        RunOnUiThread(() =>
        {
            IsRunningExportDryRun = true;
            ExportDryRunSummary = "Running scripts/release/export_playtest_bundle.ps1 -Platform all -DryRun -AllowDirtyWorktree...";
            ExportDryRunSummaryColor = "#38BDF8";
        });

        try
        {
            ExportDryRunVerificationResult result = await _cockpitBridge.VerifyExportReadinessDryRunAsync();
            RunOnUiThread(() =>
            {
                ExportDryRunStages.Clear();
                foreach (ExportDryRunStageItem stage in result.Stages)
                {
                    ExportDryRunStages.Add(stage);
                }

                HasExportDryRunResults = ExportDryRunStages.Count > 0;
                ExportDryRunSummary = result.SummaryMessage;
                ExportDryRunSummaryColor = result.Succeeded ? "#10B981" : "#EF4444";
                ExportDryRunStagingInfo = !string.IsNullOrWhiteSpace(result.SelectedStagingRoot)
                    ? $"Selected Staging Root: {result.SelectedStagingRoot} ({result.SelectedDriveFreeGb:F1} GB available)"
                    : string.Empty;
            });
        }
        finally
        {
            RunOnUiThread(() => IsRunningExportDryRun = false);
        }
    }

    [RelayCommand]
    public async Task StartPyroscopeAsync()
    {
        try
        {
            await _dockerHealth.StartContainerAsync("kongor-pyroscope");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[DiagnosticsViewModel] StartPyroscope notice: {ex.Message}");
        }
        await RunDiagnosticsAsync();
    }

    [RelayCommand]
    public void OpenPyroscope()
    {
        OpenBrowserUrl(PyroscopeUrl);
    }

    [RelayCommand]
    public void OpenDotnetMonitor()
    {
        OpenBrowserUrl($"{DotnetMonitorUrl}/processes");
    }

    [RelayCommand]
    public void OpenDotnetMonitorMetrics()
    {
        OpenBrowserUrl($"{DotnetMonitorUrl}/metrics");
    }

    [RelayCommand]
    public void OpenServiceFlamegraph(string serviceName)
    {
        string queryUrl = string.IsNullOrWhiteSpace(serviceName)
            ? PyroscopeUrl
            : $"{PyroscopeUrl.TrimEnd('/')}/?query={Uri.EscapeDataString(serviceName + ".cpu")}";
        OpenBrowserUrl(queryUrl);
    }

    [RelayCommand]
    public void OpenPyroscopeDiff()
    {
        string diffUrl = $"{PyroscopeUrl.TrimEnd('/')}/comparison";
        OpenBrowserUrl(diffUrl);
    }

    private static void OpenBrowserUrl(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            // Ignore browser opening failures
            Console.Error.WriteLine($"[DiagnosticsViewModel] Browser launch notice for '{url}': {ex.Message}");
        }
    }

    private static void RunOnUiThread(Action action)
    {
        if (Avalonia.Application.Current is null || Dispatcher.UIThread.CheckAccess())
        {
            action();
        }
        else
        {
            Dispatcher.UIThread.Post(action);
        }
    }
}
