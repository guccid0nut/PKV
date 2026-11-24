// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Input;
using NEXUS.ControlPanel.GUI.Services;
using NEXUS.ControlPanel.GUI.Services.Models;

namespace NEXUS.ControlPanel.GUI.ViewModels;

public sealed partial class GameServersViewModel
{
    private ICockpitAppBridgeService? _cockpitBridge;
    private Action? _onRequestPopOutChat;
    private Action? _onRequestDockChat;

    private ICockpitAppBridgeService CockpitBridge =>
        _cockpitBridge ??= new CockpitAppBridgeService(_orchestrator);

    public void ConfigureChatPopOutHost(Action? onRequestPopOutChat, Action? onRequestDockChat)
    {
        _onRequestPopOutChat = onRequestPopOutChat;
        _onRequestDockChat = onRequestDockChat;
    }

    [RelayCommand]
    public void PopOutChat()
    {
        bool wasAlreadyOpen = IsChatPoppedOut;
        IsChatPoppedOut = true;
        RebuildChatInstancesList();
        StartOrSwitchChatLogStream(SelectedChatInstance);
        _ = PollSelectedChatInstanceLogsAsync();
        _ = RefreshFleetQuietlyAsync(forceDiscovery: true);

        string msg = wasAlreadyOpen
            ? $"Focused popped-out Game Server Chat Manager ({AllChatInstances.Count} channel(s))."
            : $"Popped out Game Server Chat Manager ({AllChatInstances.Count} channel(s) live).";
        FleetStatusMessage = msg;
        EmitPopOutTelemetry(msg);

        _onRequestPopOutChat?.Invoke();
    }

    [RelayCommand]
    public void DockChat()
    {
        if (!IsChatPoppedOut)
        {
            return;
        }

        IsChatPoppedOut = false;
        IsChatModeActive = true;
        StartOrSwitchChatLogStream(SelectedChatInstance);
        FleetStatusMessage = "Docked Game Server Chat Manager back into NEXUS Control Panel.";
        EmitPopOutTelemetry("Docked Game Server Chat Manager back into NEXUS Control Panel.");

        _onRequestDockChat?.Invoke();
    }

    public void NotifyPopOutWindowClosed()
    {
        if (!IsChatPoppedOut)
        {
            return;
        }

        IsChatPoppedOut = false;
        EmitPopOutTelemetry($"Pop-out Game Server Chat window closed (IsChatModeActive={IsChatModeActive}).");

        if (!IsChatModeActive)
        {
            StartOrSwitchChatLogStream(null);
        }
        else
        {
            StartOrSwitchChatLogStream(SelectedChatInstance);
        }
    }

    /// <summary>
    /// Backward-compatible command entry point (previously spawned external COMPEL.GUI.exe);
    /// now pops out or focuses NEXUS's live multi-server Game Server Chat Manager.
    /// </summary>
    [RelayCommand]
    public Task OpenCompelGuiAsync()
    {
        PopOutChat();
        return Task.CompletedTask;
    }

    public IReadOnlyList<CockpitServerPipeHint> GetServerPipeHints()
    {
        List<CockpitServerPipeHint> snapshot = [];
        foreach (GameServerInstanceDetailViewModel inst in AllChatInstances.ToArray())
        {
            snapshot.Add(new CockpitServerPipeHint(
                inst.InstanceTag,
                inst.CleanServerName,
                inst.GamePort,
                inst.WorkerPort,
                inst.IsRunning,
                inst.IsContainer,
                inst.ReportedIpcConnected,
                inst.ReportedIpcPath,
                inst.ReportedIpcTransport));
        }
        return snapshot;
    }

    private void EmitPopOutTelemetry(string message)
    {
        Console.WriteLine($"[ControlPanel] [POPOUT-CHAT] {message}");
        _orchestrator?.AppendLog(new LogMessage
        {
            ServiceName = "SERVER CHAT",
            Message = $"[POPOUT-CHAT] {message}",
            Level = "INFO",
            IsError = false,
            Timestamp = TimeProvider.System.GetUtcNow()
        });
    }
}
