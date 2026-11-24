// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NEXUS.ControlPanel.GUI.Services;
using NEXUS.ControlPanel.GUI.Services.Models;

namespace NEXUS.ControlPanel.GUI.ViewModels;

public sealed partial class ServiceItemViewModel : ViewModelBase
{
    private readonly INexusOrchestratorService _orchestrator;

    [ObservableProperty] private ServerState _state = ServerState.Stopped;

    [ObservableProperty] private string _statusColor = "#EF4444"; // Red

    [ObservableProperty] private string _statusText = "STOPPED";

    public ServiceItemViewModel(
        INexusOrchestratorService orchestrator,
        string key,
        string title,
        string icon,
        string portInfo,
        string description,
        string accentColor)
    {
        _orchestrator = orchestrator;
        Key = key;
        Title = title;
        Icon = icon;
        PortInfo = portInfo;
        Description = description;
        AccentColor = accentColor;

        UpdateState(_orchestrator.GetServiceState(Key));
    }

    public string Key { get; }
    public string Title { get; }
    public string Icon { get; }
    public string PortInfo { get; }
    public string Description { get; }
    public string AccentColor { get; }

    public bool CanStart => State is ServerState.Stopped or ServerState.Faulted;

    public bool CanStop => State is ServerState.Running or ServerState.Starting;

    public bool CanRestart => State == ServerState.Running;

    [RelayCommand]
    public async Task StartAsync()
    {
        await _orchestrator.StartServiceAsync(Key);
    }

    [RelayCommand]
    public async Task StopAsync()
    {
        await _orchestrator.StopServiceAsync(Key);
    }

    [RelayCommand]
    public async Task RestartAsync()
    {
        await _orchestrator.RestartServiceAsync(Key);
    }

    public void UpdateState(ServerState newState)
    {
        State = newState;
        switch (newState)
        {
            case ServerState.Running:
                StatusText = "RUNNING";
                StatusColor = "#10B981"; // Emerald Green
                break;
            case ServerState.Starting:
                StatusText = "STARTING...";
                StatusColor = "#F59E0B"; // Amber
                break;
            case ServerState.Stopping:
                StatusText = "STOPPING...";
                StatusColor = "#F59E0B"; // Amber
                break;
            case ServerState.Faulted:
                StatusText = "FAULTED";
                StatusColor = "#EF4444"; // Red
                break;
            case ServerState.Stopped:
            default:
                StatusText = "STOPPED";
                StatusColor = "#64748B"; // Slate Gray
                break;
        }

        OnPropertyChanged(nameof(CanStart));
        OnPropertyChanged(nameof(CanStop));
        OnPropertyChanged(nameof(CanRestart));
    }
}
