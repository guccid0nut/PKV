// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NEXUS.ControlPanel.GUI.Services;
using NEXUS.ControlPanel.GUI.Services.Models;
using NEXUS.ControlPanel.GUI.ViewModels;

namespace COMPEL.GUI.ViewModels;

public sealed partial class CompelChatViewModel : ObservableObject, IDisposable
{
    private readonly IGameServerFleetService _fleetService;
    private readonly ISecretsManagerService? _secretsManager;
    private readonly DispatcherTimer _pollTimer;

    [ObservableProperty] private string _hostAddress = "127.0.0.1";
    [ObservableProperty] private int _controlPort = 11234;
    [ObservableProperty] private string _authToken = string.Empty;

    [ObservableProperty] private bool _isConnected;
    [ObservableProperty] private bool _isConnecting;
    [ObservableProperty] private string _connectionStatusText = "Ready to connect";
    [ObservableProperty] private string _connectionStatusDotColor = "#64748B";

    [ObservableProperty] private ObservableCollection<GameServerInstanceDetailViewModel> _allInstances = [];
    [ObservableProperty] private ObservableCollection<GameServerInstanceDetailViewModel> _filteredInstances = [];
    [ObservableProperty] private GameServerInstanceDetailViewModel? _selectedInstance;

    [ObservableProperty] private string _chatSearchQuery = string.Empty;
    [ObservableProperty] private string _selectedStatusFilter = "ALL";
    [ObservableProperty] private string _commandInput = string.Empty;
    [ObservableProperty] private bool _isSendingCommand;
    [ObservableProperty] private string _lastCommandStatus = string.Empty;

    public CompelChatViewModel(IGameServerFleetService fleetService, ISecretsManagerService? secretsManager = null)
    {
        _fleetService = fleetService;
        _secretsManager = secretsManager;

        _pollTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1500) };
        _pollTimer.Tick += async (_, _) => await PollActiveInstanceAndHeartbeatAsync();
        _pollTimer.Start();

        _ = InitializeAndConnectAsync();
    }

    private async Task InitializeAndConnectAsync()
    {
        if (_secretsManager != null && string.IsNullOrWhiteSpace(AuthToken))
        {
            try
            {
                NexusSecrets secrets = await _secretsManager.LoadSecretsAsync();
                if (!string.IsNullOrWhiteSpace(secrets.CompelAuthenticationToken) && secrets.CompelAuthenticationToken != "...")
                {
                    AuthToken = secrets.CompelAuthenticationToken.Trim();
                }
            }
            catch { }
        }

        await ConnectAsync();
    }

    public void Dispose()
    {
        _pollTimer.Stop();
        _activeStreamCts?.Cancel();
        _activeStreamCts?.Dispose();
    }

    [RelayCommand]
    public async Task ConnectAsync()
    {
        if (IsConnecting) return;
        IsConnecting = true;
        ConnectionStatusText = $"Connecting to {HostAddress}:{ControlPort}...";
        ConnectionStatusDotColor = "#F59E0B";

        try
        {
            var (pingSuccess, latency, _) = await _fleetService.PingHostAsync(HostAddress, ControlPort);
            if (pingSuccess)
            {
                IsConnected = true;
                ConnectionStatusText = $"Connected to COMPEL at {HostAddress}:{ControlPort} ({latency} ms)";
                ConnectionStatusDotColor = "#10B981";
                await RefreshInstancesAsync();
            }
            else
            {
                IsConnected = false;
                ConnectionStatusText = $"COMPEL offline at {HostAddress}:{ControlPort}";
                ConnectionStatusDotColor = "#EF4444";
            }
        }
        catch (Exception ex)
        {
            IsConnected = false;
            ConnectionStatusText = $"Connection failed: {ex.Message}";
            ConnectionStatusDotColor = "#EF4444";
        }
        finally
        {
            IsConnecting = false;
        }
    }

    [RelayCommand]
    public async Task RefreshInstancesAsync()
    {
        try
        {
            var (statusSuccess, status, err) = await _fleetService.GetHostStatusAsync(HostAddress, ControlPort, AuthToken);
            if (!statusSuccess || status == null)
            {
                ConnectionStatusText = $"Status query failed: {err ?? "Unknown error"}";
                return;
            }

            List<GameServerInstanceDetailViewModel> updatedVms = [];
            if (status.Ports != null && status.Instances > 0)
            {
                string prefix = string.IsNullOrWhiteSpace(status.ServerNamePrefix) ? "DONUT ARENA" : status.ServerNamePrefix;
                string location = string.IsNullOrWhiteSpace(status.Location) ? "USE" : status.Location;

                for (int i = 0; i < status.Instances; i++)
                {
                    int gPort = status.UseProxy ? status.Ports.PublicGameStart + i : status.Ports.GameStart + i;
                    int vPort = status.UseProxy ? status.Ports.PublicVoiceStart + i : status.Ports.VoiceStart + i;

                    var existing = AllInstances.FirstOrDefault(x => x.GamePort == gPort);
                    var item = existing ?? new GameServerInstanceDetailViewModel();

                    item.HostAddress = HostAddress;
                    item.ControlPort = ControlPort;
                    item.AuthToken = AuthToken;
                    item.InstanceNumber = i + 1;
                    item.ServerName = $"{prefix}-{location}-{i + 1}";
                    item.GamePort = gPort;
                    item.VoicePort = vPort;
                    item.ProtocolMode = status.UseProxy ? "Proxied UDP" : "Direct UDP";
                    item.Endpoint = $"{HostAddress}:{gPort}";
                    item.VoiceEndpoint = $"{HostAddress}:{vPort}";

                    string state = status.ManagerRunning ? "Available" : "Stopped";
                    item.UpdateStatus(state, status.ManagerRunning);

                    updatedVms.Add(item);
                }
            }

            AllInstances = [.. updatedVms];
            ApplyFilters();

            if (SelectedInstance == null || !AllInstances.Any(i => i.GamePort == SelectedInstance.GamePort))
            {
                SelectedInstance = AllInstances.FirstOrDefault(i => i.IsRunning) ?? AllInstances.FirstOrDefault();
            }

            if (SelectedInstance != null)
            {
                if (SelectedInstance.ChatMessages.Count == 0)
                {
                    SelectedInstance.AddSystemMessage($"Connected to {SelectedInstance.ServerName} (UDP {SelectedInstance.GamePort}). Ready for commands.");
                }
                StartOrSwitchLogStream(SelectedInstance);
                _ = PollActiveInstanceLogsAsync();
            }
        }
        catch (Exception ex)
        {
            ConnectionStatusText = $"Refresh instances error: {ex.Message}";
        }
    }

    partial void OnChatSearchQueryChanged(string value) => ApplyFilters();
    partial void OnSelectedStatusFilterChanged(string value) => ApplyFilters();

    private void ApplyFilters()
    {
        IEnumerable<GameServerInstanceDetailViewModel> filtered = AllInstances;

        if (!string.IsNullOrWhiteSpace(ChatSearchQuery))
        {
            filtered = filtered.Where(i =>
                i.ServerName.Contains(ChatSearchQuery, StringComparison.OrdinalIgnoreCase) ||
                i.GamePort.ToString().Contains(ChatSearchQuery, StringComparison.OrdinalIgnoreCase) ||
                i.Endpoint.Contains(ChatSearchQuery, StringComparison.OrdinalIgnoreCase));
        }

        if (!string.IsNullOrWhiteSpace(SelectedStatusFilter) && SelectedStatusFilter != "ALL")
        {
            filtered = SelectedStatusFilter.ToUpperInvariant() switch
            {
                "AVAILABLE" => filtered.Where(i => i.IsRunning && i.ServerStatus.Equals("Available", StringComparison.OrdinalIgnoreCase)),
                "IN MATCH" or "INMATCH" => filtered.Where(i => i.IsRunning && i.ServerStatus.Equals("InMatch", StringComparison.OrdinalIgnoreCase)),
                "STOPPED" => filtered.Where(i => !i.IsRunning || i.ServerStatus.Equals("Stopped", StringComparison.OrdinalIgnoreCase)),
                _ => filtered
            };
        }

        FilteredInstances = [.. filtered];
    }

    [RelayCommand]
    public void SelectInstance(GameServerInstanceDetailViewModel? instance)
    {
        if (instance == null) return;
        SelectedInstance = instance;
        if (instance.ChatMessages.Count == 0)
        {
            instance.AddSystemMessage($"Connected to {instance.ServerName} (UDP {instance.GamePort}). Ready for commands.");
        }
        StartOrSwitchLogStream(instance);
        _ = PollActiveInstanceLogsAsync();
    }

    [RelayCommand]
    public async Task StartNewServerInstanceAsync()
    {
        var target = AllInstances.FirstOrDefault(i => !i.IsRunning) ?? AllInstances.FirstOrDefault();
        if (target == null) return;

        target.UpdateStatus("Starting", isRunning: true);
        target.AddSystemMessage($"Spinning up dedicated game server on UDP {target.GamePort}...");
        SelectedInstance = target;

        var (success, msg) = await _fleetService.StartWorkerAsync(HostAddress, ControlPort, target.GamePort, AuthToken);
        if (success)
        {
            target.AddSystemMessage($"Server start signal dispatched successfully ({msg}).");
        }
        else
        {
            target.AddSystemMessage($"Server start failed: {msg}");
        }

        await RefreshInstancesAsync();
    }

    [RelayCommand]
    public async Task StartSelectedInstanceAsync()
    {
        if (SelectedInstance == null) return;
        var target = SelectedInstance;

        target.UpdateStatus("Starting", isRunning: true);
        target.AddSystemMessage($"Starting dedicated game server on UDP {target.GamePort}...");

        var (success, msg) = await _fleetService.StartWorkerAsync(HostAddress, ControlPort, target.GamePort, AuthToken);
        if (success)
        {
            target.AddSystemMessage($"Server start signal dispatched successfully ({msg}).");
        }
        else
        {
            target.AddSystemMessage($"Server start failed: {msg}");
        }

        await RefreshInstancesAsync();
    }

    [RelayCommand]
    public async Task StopInstanceAsync(GameServerInstanceDetailViewModel? instance)
    {
        var target = instance ?? SelectedInstance;
        if (target == null) return;

        target.UpdateStatus("Stopped", isRunning: false);
        target.AddSystemMessage($"Stopping dedicated game server on UDP {target.GamePort}...");

        var (success, msg) = await _fleetService.StopWorkerAsync(HostAddress, ControlPort, target.GamePort, AuthToken);
        if (success)
        {
            target.AddSystemMessage($"Server on Port {target.GamePort} stopped cleanly.");
        }
        else
        {
            target.AddSystemMessage($"Stop request issue: {msg}");
        }

        await RefreshInstancesAsync();
    }

    [RelayCommand]
    public async Task RestartSelectedInstanceAsync()
    {
        if (SelectedInstance == null) return;
        var target = SelectedInstance;

        target.UpdateStatus("Restarting", isRunning: true);
        target.AddSystemMessage($"Restarting dedicated game server on UDP {target.GamePort}...");

        var (success, msg) = await _fleetService.RestartWorkerAsync(HostAddress, ControlPort, target.GamePort, AuthToken);
        if (success)
        {
            target.AddSystemMessage($"Server restart signal dispatched ({msg}).");
        }
        else
        {
            target.AddSystemMessage($"Server restart failed: {msg}");
        }

        await RefreshInstancesAsync();
    }

    [RelayCommand]
    public async Task SendCommandAsync()
    {
        if (SelectedInstance == null || string.IsNullOrWhiteSpace(CommandInput)) return;

        string cmd = CommandInput.Trim();
        CommandInput = string.Empty;
        await DispatchCommandToInstanceAsync(SelectedInstance, cmd);
    }

    [RelayCommand]
    public async Task SendQuickCommandAsync(string? command)
    {
        if (SelectedInstance == null || string.IsNullOrWhiteSpace(command)) return;

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

        await DispatchCommandToInstanceAsync(SelectedInstance, trimmed);
    }

    private async Task DispatchCommandToInstanceAsync(GameServerInstanceDetailViewModel target, string command)
    {
        if (IsSendingCommand) return;
        IsSendingCommand = true;

        target.AddOperatorMessage(command, "dispatching...");
        LastCommandStatus = $"Sending '{command}' to Port {target.GamePort}...";

        try
        {
            var (success, channel, message) = await _fleetService.SendWorkerCommandAsync(
                HostAddress,
                ControlPort,
                target.GamePort,
                command,
                AuthToken);

            LastCommandStatus = success
                ? $"✓ Sent to Port {target.GamePort} via {channel}"
                : $"✗ Failed: {message}";
        }
        catch (Exception ex)
        {
            LastCommandStatus = $"✗ Error: {ex.Message}";
        }
        finally
        {
            IsSendingCommand = false;
        }

        await PollActiveInstanceLogsAsync();
    }

    [RelayCommand]
    public void ClearChat()
    {
        SelectedInstance?.ClearChat();
    }

    private CancellationTokenSource? _activeStreamCts;
    private Task? _activeStreamTask;
    private int _streamedPort;
    private volatile bool _isSseActive;

    private void StartOrSwitchLogStream(GameServerInstanceDetailViewModel? target)
    {
        if (target == null || !IsConnected)
        {
            _activeStreamCts?.Cancel();
            _activeStreamCts?.Dispose();
            _activeStreamCts = null;
            _activeStreamTask = null;
            _streamedPort = 0;
            _isSseActive = false;
            return;
        }

        if (_streamedPort == target.GamePort && _isSseActive && _activeStreamTask is { IsCompleted: false })
        {
            return;
        }

        _activeStreamCts?.Cancel();
        _activeStreamCts?.Dispose();

        CancellationTokenSource cts = new();
        _activeStreamCts = cts;
        _streamedPort = target.GamePort;
        _activeStreamTask = Task.Run(() => StreamLogsLoopAsync(target, cts.Token));
    }

    private async Task StreamLogsLoopAsync(GameServerInstanceDetailViewModel target, CancellationToken cancellationToken)
    {
        try
        {
            await foreach (CompelDedicatedServerLogLineDto log in _fleetService.StreamServerLogsAsync(
                HostAddress,
                ControlPort,
                target.GamePort,
                tail: 50,
                AuthToken,
                cancellationToken).ConfigureAwait(false))
            {
                _isSseActive = true;
                Dispatcher.UIThread.Post(() =>
                {
                    target.IngestLogs([log]);
                });
            }

            _isSseActive = false;
        }
        catch (OperationCanceledException)
        {
            // Expected on stream switch or dispose
        }
        catch (Exception)
        {
            _isSseActive = false;
        }
    }

    private async Task PollActiveInstanceAndHeartbeatAsync()
    {
        if (!IsConnected) return;

        if (SelectedInstance != null)
        {
            if (_activeStreamTask == null || _activeStreamTask.IsCompleted || !_isSseActive)
            {
                StartOrSwitchLogStream(SelectedInstance);
            }
        }

        await PollActiveInstanceLogsAsync();
    }

    private async Task PollActiveInstanceLogsAsync()
    {
        if (SelectedInstance == null) return;

        // If SSE stream is active and pushing lines reactively, skip redundant HTTP polling
        if (_isSseActive) return;

        var target = SelectedInstance;
        try
        {
            var (success, logs, _) = await _fleetService.GetServerLogsAsync(
                HostAddress,
                ControlPort,
                target.GamePort,
                tail: 100,
                AuthToken);

            if (success && logs.Count > 0)
            {
                target.IngestLogs(logs);
            }
        }
        catch { }
    }
}
