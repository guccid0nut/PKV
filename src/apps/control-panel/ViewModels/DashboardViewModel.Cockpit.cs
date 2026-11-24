// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NEXUS.ControlPanel.GUI.Services;
using NEXUS.ControlPanel.GUI.Services.Models;

namespace NEXUS.ControlPanel.GUI.ViewModels;

public sealed partial class DashboardViewModel
{
    private ICockpitAppBridgeService? _cockpitBridge;

    private ICockpitAppBridgeService CockpitBridge =>
        _cockpitBridge ??= new CockpitAppBridgeService(_orchestrator);

    public ObservableCollection<CockpitPipeBadgeItem> ConnectedIpcPipes { get; } = [];
    public bool HasConnectedIpcPipes => ConnectedIpcPipes.Count > 0;

    [ObservableProperty] private bool _isWillowmakerConnected;
    [ObservableProperty] private string _willowmakerStatusMessage = "Checking IPC...";
    [ObservableProperty] private string _willowmakerStatusColor = "#94A3B8";
    [ObservableProperty] private string _cockpitActionFeedback = string.Empty;
    [ObservableProperty] private bool _hasCockpitActionFeedback;
    [ObservableProperty] private string _cockpitActionFeedbackColor = "#38BDF8";

    /// <summary>
    /// Resolves Control Panel's active Master Server + CDN endpoint pair for WILLOWMAKER synchronisation:
    /// <c>localhost:55555</c> + <c>localhost:55555/cdn</c> when the local cluster is active (or in Local mode),
    /// or the configured WAN / LAN endpoint when explicitly selected.
    /// </summary>
    public (string MasterServerEndpoint, string CdnEndpoint) ResolveActiveCockpitEndpointPair()
    {
        if (IsWanMode && !string.IsNullOrWhiteSpace(WanHost))
        {
            string normalizedWan = NormalizeHostWithDefaultPort(WanHost, 55555);
            return (normalizedWan, $"{normalizedWan}/cdn");
        }

        if (ClusterState != ServerState.Running &&
            IsLanMode &&
            !string.IsNullOrWhiteSpace(SelectedLanIp) &&
            !string.Equals(SelectedLanIp.Trim(), "127.0.0.1", StringComparison.Ordinal))
        {
            string normalizedLan = NormalizeHostWithDefaultPort(SelectedLanIp, 55555);
            return (normalizedLan, $"{normalizedLan}/cdn");
        }

        return ("localhost:55555", "localhost:55555/cdn");
    }

    [RelayCommand]
    public async Task OpenWillowmakerAsync()
    {
        (string masterEndpoint, string cdnEndpoint) = ResolveActiveCockpitEndpointPair();
        string? bundleRoot = _bundledLayout?.Root;
        string? discordClientId = null;
        if (_secretsManager is not null)
        {
            try
            {
                NexusSecrets secrets = await _secretsManager.LoadSecretsAsync().ConfigureAwait(false);
                discordClientId = secrets.DiscordClientId;
            }
            catch
            {
                // Fall back to default Project KONGOR Discord Application ID if vault read fails
            }
        }

        RunOnUiThread(() =>
        {
            CockpitActionFeedback = $"Connecting to WILLOWMAKER ({masterEndpoint})...";
            CockpitActionFeedbackColor = "#38BDF8";
            HasCockpitActionFeedback = true;
        });

        CockpitLaunchOrSyncResult result = await CockpitBridge
            .OpenOrSyncWillowmakerAsync(masterEndpoint, cdnEndpoint, rootOverride: bundleRoot, discordClientId: discordClientId)
            .ConfigureAwait(false);

        await RefreshWillowmakerCockpitHealthAsync().ConfigureAwait(false);

        RunOnUiThread(() =>
        {
            CockpitActionFeedback = result.StatusMessage;
            CockpitActionFeedbackColor = result.Succeeded ? "#10B981" : "#EF4444";
            HasCockpitActionFeedback = !string.IsNullOrWhiteSpace(result.StatusMessage);
            ClusterStatusText = result.StatusMessage;
            ClusterStatusColor = result.Succeeded ? "#10B981" : "#EF4444";
        });
    }

    [RelayCommand]
    public async Task OpenCompelGuiAsync()
    {
        if (_onOpenServerChatPopOut is not null)
        {
            RunOnUiThread(() =>
            {
                _onOpenServerChatPopOut.Invoke();
                CockpitActionFeedback = "Opened Game Server Chat Manager pop-out window.";
                CockpitActionFeedbackColor = "#10B981";
                HasCockpitActionFeedback = true;
            });
            return;
        }

        string? bundleRoot = _bundledLayout?.Root;
        CompelGuiLaunchResult result = await CockpitBridge
            .OpenCompelGuiAsync(rootOverride: bundleRoot)
            .ConfigureAwait(false);

        RunOnUiThread(() =>
        {
            CockpitActionFeedback = result.StatusMessage;
            CockpitActionFeedbackColor = result.Succeeded ? "#10B981" : "#EF4444";
            HasCockpitActionFeedback = !string.IsNullOrWhiteSpace(result.StatusMessage);
            ClusterStatusText = result.StatusMessage;
            ClusterStatusColor = result.Succeeded ? "#10B981" : "#EF4444";
        });
    }

    public async Task RefreshWillowmakerCockpitHealthAsync()
    {
        IReadOnlyList<CockpitServerPipeHint>? serverHints = _getServerPipeHints?.Invoke();
        IReadOnlyList<CockpitPipeBadgeItem> connectedPipes = await CockpitBridge
            .ProbeConnectedCockpitPipesAsync(
                knownServerInstances: serverHints,
                passiveNativeClientProbe: true)
            .ConfigureAwait(false);

        CockpitPipeBadgeItem? wmBadge = connectedPipes.FirstOrDefault(p =>
            string.Equals(p.Key, "willowmaker-ipc", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(p.Key, "mcp-ipc-bridge", StringComparison.OrdinalIgnoreCase));

        RunOnUiThread(() =>
        {
            IsWillowmakerConnected = wmBadge is { IsConnected: true };
            WillowmakerStatusMessage = wmBadge is { IsConnected: true }
                ? wmBadge.StatusMessage
                : "Standby";
            WillowmakerStatusColor = wmBadge is { IsConnected: true }
                ? wmBadge.StatusColor
                : "#94A3B8";

            SyncConnectedPipeBadges(connectedPipes);
        });
    }

    private void SyncConnectedPipeBadges(IReadOnlyList<CockpitPipeBadgeItem> latest)
    {
        bool identical = ConnectedIpcPipes.Count == latest.Count;
        if (identical)
        {
            for (int i = 0; i < latest.Count; i++)
            {
                if (!Equals(ConnectedIpcPipes[i], latest[i]))
                {
                    identical = false;
                    break;
                }
            }
        }

        if (identical)
        {
            return;
        }

        ConnectedIpcPipes.Clear();
        foreach (CockpitPipeBadgeItem item in latest)
        {
            if (item.IsConnected)
            {
                ConnectedIpcPipes.Add(item);
            }
        }
        OnPropertyChanged(nameof(HasConnectedIpcPipes));
    }

    private static string NormalizeHostWithDefaultPort(string rawHost, int defaultPort)
    {
        string cleaned = (rawHost ?? string.Empty).Trim();
        if (cleaned.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            cleaned = cleaned[8..];
        }
        else if (cleaned.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
        {
            cleaned = cleaned[7..];
        }

        int slashIdx = cleaned.IndexOf('/');
        if (slashIdx >= 0)
        {
            cleaned = cleaned[..slashIdx];
        }

        if (string.IsNullOrWhiteSpace(cleaned))
        {
            return $"localhost:{defaultPort}";
        }

        // IPv6 literal with brackets: [::1]:55555 or [::1]
        if (cleaned.StartsWith('['))
        {
            int closeBracket = cleaned.IndexOf(']');
            if (closeBracket > 0)
            {
                bool hasPortAfterBracket = closeBracket + 1 < cleaned.Length && cleaned[closeBracket + 1] == ':';
                return hasPortAfterBracket ? cleaned : $"{cleaned}:{defaultPort}";
            }
        }

        int firstColon = cleaned.IndexOf(':');
        int lastColon = cleaned.LastIndexOf(':');
        if (firstColon > 0 && firstColon == lastColon)
        {
            return cleaned;
        }

        return $"{cleaned}:{defaultPort}";
    }
}
