// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NEXUS.ControlPanel.GUI.Services;
using NEXUS.ControlPanel.GUI.Services.Models;
using NEXUS.Contracts.ControlPlane;

namespace NEXUS.ControlPanel.GUI.ViewModels;

public sealed partial class GameServerHostItemViewModel : ObservableObject
{
    private readonly IGameServerFleetService _fleetService;
    [ObservableProperty] private string _authToken = string.Empty;
    [ObservableProperty] private string _controlPlaneUrl = string.Empty;
    [ObservableProperty] private string _distributionVersion = "—";
    [ObservableProperty] private string _expandButtonText = "▼ View Instances";
    [ObservableProperty] private string _hostAddress = "127.0.0.1";

    [ObservableProperty] private string _id = string.Empty;
    [ObservableProperty] private int _instances;
    [ObservableProperty] private ObservableCollection<GameServerInstanceDetailViewModel> _instancesList = [];
    [ObservableProperty] private bool _isBusy;

    [ObservableProperty] private bool _isExpanded;
    [ObservableProperty] private bool _isLocal;
    [ObservableProperty] private bool _isManagerRunning;
    [ObservableProperty] private bool _isProxyRunning;
    [ObservableProperty] private string _lastActionResult = string.Empty;
    [ObservableProperty] private string _location = "USE";
    [ObservableProperty] private string _name = string.Empty;
    [ObservableProperty] private long _pingMs = -1;
    [ObservableProperty] private int _pingResponderPort;
    [ObservableProperty] private string _pingResponderStatus = "Inactive";
    [ObservableProperty] private int _port = 8080;
    [ObservableProperty] private string _portRangeText = "—";
    [ObservableProperty] private string _proxyStatus = "Direct / Disabled";
    [ObservableProperty] private string _serverStateColor = "#64748B";
    [ObservableProperty] private string _serverStateText = "Offline";
    [ObservableProperty] private string _statusBadgeBackground = "#1E293B";
    [ObservableProperty] private string _statusColor = "#64748B"; // slate-500

    [ObservableProperty] private string _hostingBadgeText = "💻 LOCAL";
    [ObservableProperty] private string _hostingBadgeColor = "#10B981";
    [ObservableProperty] private string _hostingBadgeBackground = "#064E3B";
    [ObservableProperty] private string _hostingEnvironment = "Local Windows Process";

    [ObservableProperty] private string _statusText = "Offline";
    [ObservableProperty] private string _syncState = "—";
    [ObservableProperty] private string _uptimeText = "—";

    private bool _wasExplicitlyStoppedByOperator;

    public GameServerHostItemViewModel(GameServerHostEntry entry, IGameServerFleetService fleetService)
    {
        _fleetService = fleetService;
        Id = entry.Id;
        Name = entry.Name;
        HostAddress = entry.HostAddress;
        Port = entry.Port;
        Location = entry.Location;
        AuthToken = entry.AuthToken;
        IsLocal = entry.IsLocal;
        ControlPlaneUrl = $"http://{HostAddress}:{Port}/status";

        bool isK8s = Port == 11434 || Port == 11254 || (Name?.Contains("k3s", StringComparison.OrdinalIgnoreCase) == true) || (Name?.Contains("k8s", StringComparison.OrdinalIgnoreCase) == true);
        bool isDocker = !isK8s && (Port == 11334 || Port == 11244 || (Name?.Contains("dock", StringComparison.OrdinalIgnoreCase) == true));

        bool isLocalAddress = HostAddress.Equals("127.0.0.1", StringComparison.OrdinalIgnoreCase)
            || HostAddress.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            || HostAddress.Equals("::1", StringComparison.OrdinalIgnoreCase)
            || HostAddress.Equals("0.0.0.0", StringComparison.OrdinalIgnoreCase);

        if (!isLocalAddress)
        {
            HostingEnvironment = "Remote Cloud Node";
            HostingBadgeText = "☁️ HOSTED";
            HostingBadgeColor = "#A855F7";
            HostingBadgeBackground = "#3B0764";
        }
        else if (isK8s)
        {
            HostingEnvironment = "Kubernetes / K3s";
            HostingBadgeText = "☸️ K3S";
            HostingBadgeColor = "#818CF8";
            HostingBadgeBackground = "#1E1B4B";
            if (string.IsNullOrWhiteSpace(Name) || Name.StartsWith("Local", StringComparison.OrdinalIgnoreCase) || Name == "HOST")
            {
                Name = "☸️ K3s Cluster (COMPEL)";
            }
        }
        else if (isDocker)
        {
            HostingEnvironment = "Docker Container";
            HostingBadgeText = "🐳 DOCKER";
            HostingBadgeColor = "#38BDF8";
            HostingBadgeBackground = "#0C4A6E";
            if (string.IsNullOrWhiteSpace(Name) || Name.StartsWith("Local", StringComparison.OrdinalIgnoreCase) || Name == "HOST")
            {
                Name = "🐳 Docker Container (COMPEL)";
            }
        }
        else
        {
            HostingEnvironment = "Local Windows Process";
            HostingBadgeText = "💻 LOCAL";
            HostingBadgeColor = "#10B981";
            HostingBadgeBackground = "#064E3B";
            if (string.IsNullOrWhiteSpace(Name) || Name.StartsWith("Local", StringComparison.OrdinalIgnoreCase) || Name == "HOST")
            {
                Name = "💻 Local Windows Server (COMPEL)";
            }
        }
    }

    public string PingDisplay => PingMs >= 0 ? $"{PingMs} ms" : "Timeout";

    public string PingColor => PingMs switch
    {
        < 0 => "#EF4444", // red (timeout)
        <= 60 => "#FFD700", // gold (< 60 ms)
        <= 119 => "#FFFFFF", // white (61 - 119 ms)
        _ => "#EF4444" // red (120+ ms)
    };

    [RelayCommand]
    public void ToggleExpand()
    {
        IsExpanded = !IsExpanded;
        UpdateExpandButtonText();
    }

    private void UpdateExpandButtonText()
    {
        int count = InstancesList.Count > 0 ? InstancesList.Count : Instances;
        ExpandButtonText = IsExpanded
            ? count > 0 ? $"▲ Hide Instances ({count})" : "▲ Hide Instances"
            : count > 0
                ? $"▼ View Instances ({count})"
                : "▼ View Instances";
    }

    [RelayCommand]
    public async Task RefreshAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        try
        {
            await RefreshInternalAsync();
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task RefreshInternalAsync()
    {
        (bool pingSuccess, long latency, CompelPingResponseDto? pingResp) =
            await _fleetService.PingHostAsync(HostAddress, Port);
        PingMs = pingSuccess ? latency : -1;
        OnPropertyChanged(nameof(PingDisplay));
        OnPropertyChanged(nameof(PingColor));

        if (!pingSuccess)
        {
            StatusText = "Unreachable";
            StatusColor = "#EF4444";
            StatusBadgeBackground = "#450A0A";
            ServerStateText = "Unreachable";
            ServerStateColor = "#EF4444";
            IsManagerRunning = false;
            IsProxyRunning = false;
            foreach (var inst in InstancesList)
            {
                inst.UpdateStatus("Offline", isRunning: false);
                inst.UpdateIpcPipeState(false, null, null);
            }
            UpdateExpandButtonText();
            return;
        }

        (bool statusSuccess, CompelStatusResponseDto? status, string? err) =
            await _fleetService.GetHostStatusAsync(HostAddress, Port, AuthToken);
        if (statusSuccess && status is not null)
        {
            Instances = status.Instances;
            IsManagerRunning = status.ManagerRunning;
            IsProxyRunning = status.ProxyRunning;
            DistributionVersion = string.IsNullOrWhiteSpace(status.DistributionVersion)
                ? "4.10.1"
                : status.DistributionVersion;
            SyncState = string.IsNullOrWhiteSpace(status.SynchronisationState) ? "Ready" : status.SynchronisationState;
            ControlPlaneUrl = $"http://{HostAddress}:{Port}/status";
            PingResponderPort = status.Ports?.PingPort ?? 0;
            PingResponderStatus = status.PingResponderBound == true
                ? PingResponderPort > 0 ? $"Bound ({PingResponderPort})" : "Bound"
                : "Unbound";
            ProxyStatus = status.UseProxy
                ? status.ProxyRunning ? $"Active ({status.ProxyFailedForwarderCount} errors)" : "Proxy Offline"
                : "Direct (Disabled)";

            if (status.Ports != null)
                PortRangeText = status.UseProxy
                    ? $"{status.Ports.PublicGameStart}–{status.Ports.PublicGameEnd} (Proxy)"
                    : $"{status.Ports.GameStart}–{status.Ports.GameEnd} (Direct)";

            TimeSpan uptime = TimeSpan.FromSeconds(status.UptimeSeconds);
            UptimeText = uptime.TotalHours >= 1
                ? $"{(int)uptime.TotalHours}h {uptime.Minutes}m"
                : $"{uptime.Minutes}m {uptime.Seconds}s";

            if (status.ManagerRunning)
            {
                _wasExplicitlyStoppedByOperator = false;
                int runningWorkerCount = status.Workers?.Count(w => w.Running) ?? (status.ActiveWorkers > 0 ? status.ActiveWorkers : Instances);
                bool hasCrashes = status.Workers?.Any(w => w.State.Equals("Crashed", StringComparison.OrdinalIgnoreCase) || (w.CrashCount > 0 && !w.Running)) == true;

                if (hasCrashes || runningWorkerCount < Instances)
                {
                    StatusText = hasCrashes ? "Degraded (Crashed)" : "Degraded";
                    StatusColor = "#F59E0B"; // amber
                    StatusBadgeBackground = "#451A03";
                    ServerStateText = $"Degraded ({runningWorkerCount}/{Instances} Slaves Active)";
                    ServerStateColor = "#F59E0B";
                }
                else
                {
                    StatusText = "Running";
                    StatusColor = "#10B981"; // green
                    StatusBadgeBackground = "#064E3B";
                    ServerStateText = $"Running ({Instances} Slaves Active)";
                    ServerStateColor = "#10B981"; // green
                }
            }
            else
            {
                string stoppedText = _wasExplicitlyStoppedByOperator ? "Stopped by Operator" : "Stopped (Standby)";
                string stoppedColor = _wasExplicitlyStoppedByOperator ? "#F43F5E" : "#F59E0B";
                string stoppedBg = _wasExplicitlyStoppedByOperator ? "#4C0519" : "#451A03";

                StatusText = stoppedText;
                ServerStateText = stoppedText;
                StatusColor = stoppedColor;
                ServerStateColor = stoppedColor;
                StatusBadgeBackground = stoppedBg;
            }

            // Detect whether this host is running inside Docker/Linux container, local Windows process, or remote cloud node
            bool isContainer = (!string.IsNullOrWhiteSpace(status.OperatingSystem) && status.OperatingSystem.Contains("Linux", StringComparison.OrdinalIgnoreCase))
                || (!string.IsNullOrWhiteSpace(status.DistributionVersion) && status.DistributionVersion.Contains("linux", StringComparison.OrdinalIgnoreCase));

            bool isLocalAddress = HostAddress.Equals("127.0.0.1", StringComparison.OrdinalIgnoreCase)
                || HostAddress.Equals("localhost", StringComparison.OrdinalIgnoreCase)
                || HostAddress.Equals("::1", StringComparison.OrdinalIgnoreCase)
                || HostAddress.Equals("0.0.0.0", StringComparison.OrdinalIgnoreCase);

            bool isK8s = Port == 11434 || Port == 11254 || (Name?.Contains("k3s", StringComparison.OrdinalIgnoreCase) == true) || (Name?.Contains("k8s", StringComparison.OrdinalIgnoreCase) == true);
            bool isDocker = !isK8s && (isContainer || Port == 11334 || Port == 11244 || (Name?.Contains("dock", StringComparison.OrdinalIgnoreCase) == true));

            if (!isLocalAddress)
            {
                HostingEnvironment = "Remote Cloud Node";
                HostingBadgeText = "☁️ HOSTED";
                HostingBadgeColor = "#A855F7";
                HostingBadgeBackground = "#3B0764";
            }
            else if (isK8s)
            {
                HostingEnvironment = "Kubernetes / K3s";
                HostingBadgeText = "☸️ K3S";
                HostingBadgeColor = "#818CF8";
                HostingBadgeBackground = "#1E1B4B";
                if (string.IsNullOrWhiteSpace(Name) || Name.StartsWith("Local", StringComparison.OrdinalIgnoreCase) || Name == "HOST")
                {
                    Name = "☸️ K3s Cluster (COMPEL)";
                }
            }
            else if (isDocker)
            {
                HostingEnvironment = "Docker Container";
                HostingBadgeText = "🐳 DOCKER";
                HostingBadgeColor = "#38BDF8";
                HostingBadgeBackground = "#0C4A6E";
                if (string.IsNullOrWhiteSpace(Name) || Name.StartsWith("Local", StringComparison.OrdinalIgnoreCase) || Name == "HOST")
                {
                    Name = "🐳 Docker Container (COMPEL)";
                }
            }
            else
            {
                HostingEnvironment = "Local Windows Process";
                HostingBadgeText = "💻 LOCAL";
                HostingBadgeColor = "#10B981";
                HostingBadgeBackground = "#064E3B";
                if (string.IsNullOrWhiteSpace(Name) || Name.StartsWith("Local", StringComparison.OrdinalIgnoreCase) || Name == "HOST")
                {
                    Name = "💻 Local Windows Server (COMPEL)";
                }
            }

            // Populate per-instance slave breakdowns instantaneously from ports & instances
            List<GameServerInstanceDetailViewModel> details = [];
            if (status.Ports != null && status.Instances > 0)
            {
                string prefix = !string.IsNullOrWhiteSpace(status.ServerNamePrefix) ? status.ServerNamePrefix! : (Name ?? "KONGOR ARENA");
                string tierLetter = isK8s ? "K" : (isDocker ? "D" : "L");
                for (int i = 0; i < status.Instances; i++)
                {
                    int rawPort = status.Ports.GameStart + i;
                    int gPort = status.UseProxy ? status.Ports.PublicGameStart + i : status.Ports.GameStart + i;
                    int vPort = status.UseProxy ? status.Ports.PublicVoiceStart + i : status.Ports.VoiceStart + i;

                    GameServerInstanceDetailViewModel? existing = InstancesList.FirstOrDefault(x => x.GamePort == gPort);
                    GameServerInstanceDetailViewModel item = existing ?? new GameServerInstanceDetailViewModel();

                    item.HostAddress = HostAddress;
                    item.ControlPort = Port;
                    item.AuthToken = AuthToken;
                    item.TierCode = tierLetter;
                    item.InstanceTag = $"#{tierLetter}{i + 1}";
                    item.InstanceNumber = i + 1;
                    item.GamePort = gPort;
                    item.WorkerPort = rawPort;
                    item.VoicePort = vPort;
                    item.ProtocolMode = status.UseProxy ? "Proxied UDP" : "Direct UDP";
                    item.Endpoint = $"{HostAddress}:{gPort}";
                    item.VoiceEndpoint = $"{HostAddress}:{vPort}";

                    item.HostingEnvironment = HostingEnvironment;
                    item.HostingBadgeText = HostingBadgeText;
                    item.HostingBadgeColor = HostingBadgeColor;
                    item.HostingBadgeBackground = HostingBadgeBackground;
                    item.IsContainer = isContainer || isDocker || isK8s;
                    item.IsLocal = isLocalAddress && !isContainer && !isDocker && !isK8s;

                    CompelWorkerStatusDto? worker = status.Workers?.FirstOrDefault(w =>
                        w.Port == rawPort ||
                        w.PublicGamePort == gPort ||
                        w.InstanceIndex == (i + 1));

                    if (!string.IsNullOrWhiteSpace(worker?.ServerName))
                    {
                        item.ServerName = worker.ServerName;
                    }
                    else if (!string.IsNullOrWhiteSpace(prefix))
                    {
                        item.ServerName = !string.IsNullOrWhiteSpace(Location) && Location != "^yALL"
                            ? $"{prefix}-{Location}-{tierLetter}{i + 1}"
                            : $"{prefix} #{tierLetter}{i + 1}";
                    }
                    else
                    {
                        item.ServerName = $"{Location} #{tierLetter}{i + 1}";
                    }

                    string state = worker != null ? worker.State : (status.ManagerRunning ? "Available" : "Stopped");
                    bool isRunning = worker != null ? worker.Running : status.ManagerRunning;
                    item.ProcessId = worker?.ProcessId;
                    item.DesiredRunning = isRunning;
                    item.UpdateStatus(state, isRunning);
                    item.UpdateIpcPipeState(worker?.IpcConnected, worker?.IpcPath, worker?.IpcTransport);

                    details.Add(item);
                }
            }

            SyncInstancesList(details);
            UpdateExpandButtonText();
        }
        else
        {
            StatusText = "Auth Required";
            StatusColor = "#A855F7"; // purple
            StatusBadgeBackground = "#3B0764";
            ServerStateText = "Auth Required";
            ServerStateColor = "#A855F7";
            InstancesList.Clear();
            UpdateExpandButtonText();
        }
    }

    private void SyncInstancesList(IList<GameServerInstanceDetailViewModel> details)
    {
        var desiredPorts = new HashSet<int>(details.Select(d => d.GamePort));
        for (int k = InstancesList.Count - 1; k >= 0; k--)
        {
            if (!desiredPorts.Contains(InstancesList[k].GamePort))
            {
                InstancesList.RemoveAt(k);
            }
        }

        for (int k = 0; k < details.Count; k++)
        {
            var detailItem = details[k];
            int existingIdx = -1;
            for (int j = k; j < InstancesList.Count; j++)
            {
                if (InstancesList[j].GamePort == detailItem.GamePort)
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
                InstancesList.Move(existingIdx, k);
            }
            else
            {
                InstancesList.Insert(k, detailItem);
            }
        }
    }

    [RelayCommand]
    public async Task StartAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        LastActionResult = "Starting instances...";
        ServerStateText = "Starting instances...";
        ServerStateColor = "#38BDF8";

        try
        {
            _wasExplicitlyStoppedByOperator = false;
            (bool success, string msg) = await _fleetService.StartInstancesAsync(HostAddress, Port, AuthToken);
            LastActionResult = success ? $"✓ {msg}" : $"✗ {msg}";
            await RefreshInternalAsync();
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    public async Task StopAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        LastActionResult = "Stopping instances...";
        ServerStateText = "Stopping instances...";
        ServerStateColor = "#F43F5E";

        try
        {
            _wasExplicitlyStoppedByOperator = true;
            (bool success, string msg) = await _fleetService.StopInstancesAsync(HostAddress, Port, AuthToken);
            LastActionResult = success ? $"✓ {msg}" : $"✗ {msg}";
            await RefreshInternalAsync();
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    public async Task RestartAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        LastActionResult = "Restarting instances...";
        ServerStateText = "Restarting instances...";
        ServerStateColor = "#A855F7";

        try
        {
            _wasExplicitlyStoppedByOperator = false;
            (bool success, string msg) = await _fleetService.RestartInstancesAsync(HostAddress, Port, AuthToken);
            LastActionResult = success ? $"✓ {msg}" : $"✗ {msg}";
            await RefreshInternalAsync();
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    public async Task SyncAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        LastActionResult = "Triggering CDN sync...";

        try
        {
            (bool success, string msg) = await _fleetService.SyncDistributionAsync(HostAddress, Port, AuthToken);
            LastActionResult = success ? $"✓ {msg}" : $"✗ {msg}";
            await RefreshInternalAsync();
        }
        finally
        {
            IsBusy = false;
        }
    }
}
