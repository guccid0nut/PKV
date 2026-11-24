// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kongor.Core.Network;
using WILLOWMAKER.Core.Services;
using WILLOWMAKER.Services;
using WILLOWMAKER.Services.P2P;

namespace WILLOWMAKER.Core.ViewModels;

public partial class DirectPlayViewModel : ObservableObject
{
    private readonly Func<IEnumerable<string>, Task> _launcherCallback;
    private readonly ClientNetcodeRelaySessionManager? _relaySessionManager;
    private readonly Action<string>? _logSink;
    private long _publicIpEpoch;

    public ClientNetcodeRelaySessionManager? RelaySessionManager => _relaySessionManager;

    /// <summary>
    ///     Injectable UPnP IGD port-mapping delegate (defaults to <see cref="UpnpIgdPortMapper.TryMapUdpPortAsync" />)
    ///     so unit and headless GUI tests can deterministically exercise router mapping without LAN multicast.
    /// </summary>
    public Func<int, string, CancellationToken, Task<UpnpPortMappingResult>>? UpnpPortMapperOverride { get; set; }

    /// <summary>
    ///     Injectable Windows Firewall UDP coverage auditor delegate (defaults to
    ///     <see cref="WindowsFirewallUdpCoverageAuditor.RunAuditAsync" />) for deterministic testing.
    /// </summary>
    public Func<int, int, string?, CancellationToken, Task<FirewallUdpAuditReport?>>? FirewallAuditorOverride { get; set; }

    /// <summary>
    ///     Injectable RFC 5389/5780 STUN NAT behavior classifier delegate (defaults to
    ///     <see cref="P2pConnectionService.ProbeNatBehaviorAsync" />) so unit and offline tests can
    ///     deterministically simulate OpenInternet, ConeNat, SymmetricOrCgnat, and UdpBlocked networks.
    /// </summary>
    public Func<int, CancellationToken, Task<StunProbeResult>>? StunNatClassifierOverride { get; set; }

    /// <summary>
    ///     Optional override for community relay endpoints (COMPEL UDP proxy, coturn, rathole).
    /// </summary>
    public DirectPlayRelayOptions? RelayOptionsOverride { get; set; }

    public DirectPlayTransportPlan? LastTransportPlan { get; private set; }

    private void EmitLog(string message)
    {
        Trace.WriteLine(message);
        _logSink?.Invoke(message);
    }

    [ObservableProperty]
    private string _playerNickname;

    [ObservableProperty]
    private string _hostPort = P2pConnectionService.DefaultPort.ToString();

    [ObservableProperty]
    private string _detectedPublicIp = "Resolving...";

    [ObservableProperty]
    private string _detectedLocalIp = P2pConnectionService.GetLocalIpAddress();

    [ObservableProperty]
    private string _joinAddress = string.Empty;

    [ObservableProperty]
    private string _statusMessage = "Ready. Configure match in-game as Host or Connect to a friend.";

    [ObservableProperty]
    private string _statusColor = "#94A3B8";

    [ObservableProperty]
    private bool _isLoadingIp;

    [ObservableProperty]
    private bool _isHostMode = true;

    [ObservableProperty]
    private bool _bypassMasterServer = true;

    [ObservableProperty]
    private string _buildStamp = string.Empty;

    [ObservableProperty]
    private string _upnpStatusText = "UPnP Auto-Port Forward: Not probed yet (click 'Map UPnP & Audit Firewall' or Launch Host)";

    [ObservableProperty]
    private string _upnpStatusColor = "#94A3B8";

    [ObservableProperty]
    private bool _isUpnpMapped;

    [ObservableProperty]
    private string _firewallAuditStatusText = "Windows Firewall UDP Audit: Pending check";

    [ObservableProperty]
    private string _firewallAuditStatusColor = "#94A3B8";

    [ObservableProperty]
    private string _firewallRemediationCommand = string.Empty;

    [ObservableProperty]
    private bool _hasFirewallWarning;

    [ObservableProperty]
    private bool _isRunningNetworkDiagnostics;

    [ObservableProperty]
    private NatBehaviorClassification _natClassification = NatBehaviorClassification.Unknown;

    [ObservableProperty]
    private string _natStatusText = "STUN NAT Classification: Pending check";

    [ObservableProperty]
    private string _natStatusColor = "#94A3B8";

    [ObservableProperty]
    private DirectPlayTransportStrategy _recommendedTransportStrategy = DirectPlayTransportStrategy.DirectUpnp;

    [ObservableProperty]
    private string _relayStatusText = "P2P Relay Readiness: Pending NAT check";

    [ObservableProperty]
    private string _relayStatusColor = "#94A3B8";

    [ObservableProperty]
    private bool _isRelayRequired;

    [ObservableProperty]
    private string _relayConfigSnippet = string.Empty;

    partial void OnBuildStampChanged(string value)
    {
        if (!string.IsNullOrWhiteSpace(value) && StatusMessage.StartsWith("Ready.", StringComparison.Ordinal))
        {
            StatusMessage = $"Ready (Build {value}). Configure match in-game as Host or Connect to a friend.";
        }
    }

    public string InviteCode =>
        string.IsNullOrWhiteSpace(DetectedPublicIp) || DetectedPublicIp.Contains("Resolving") || DetectedPublicIp.Contains("Unavailable")
            ? $"{DetectedLocalIp}:{HostPort}"
            : $"{DetectedPublicIp}:{HostPort}";

    public DirectPlayViewModel(
        Func<IEnumerable<string>, Task> launcherCallback,
        ClientNetcodeRelaySessionManager? relaySessionManager = null,
        Action<string>? logSink = null)
    {
        _launcherCallback = launcherCallback;
        _relaySessionManager = relaySessionManager;
        _logSink = logSink;
        _playerNickname = P2pConnectionService.ResolveDefaultNickname();
        int initialPort = P2pConnectionService.FindAvailableUdpPort(P2pConnectionService.DefaultPort);
        _hostPort = initialPort.ToString();

        _ = RefreshIpAsync();
    }

    partial void OnPlayerNicknameChanged(string value)
    {
        if (!string.IsNullOrWhiteSpace(value) &&
            !value.Equals("UnnamedNewbie", StringComparison.OrdinalIgnoreCase) &&
            !value.Equals(Environment.UserName, StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                LauncherSettingsModel settings = LauncherSettingsService.Load();
                settings.LastPlayerNickname = value.Trim();
                LauncherSettingsService.Save(settings);
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"[DirectPlay] Settings save notice: {ex.Message}");
            }
        }
    }

    [RelayCommand]
    public void SelectHostMode() => IsHostMode = true;

    [RelayCommand]
    public void SelectJoinMode() => IsHostMode = false;

    [RelayCommand]
    public async Task RefreshIpAsync()
    {
        long startEpoch = Interlocked.Read(ref _publicIpEpoch);
        IsLoadingIp = true;
        StatusMessage = "Detecting public and local network IP addresses...";
        StatusColor = "#38BDF8";

        try
        {
            int requestedPort = int.TryParse(HostPort, out int parsedPort) && parsedPort > 0 && parsedPort <= 65535
                ? parsedPort
                : P2pConnectionService.DefaultPort;
            if (P2pHostServerService.ActiveHostPort != requestedPort)
            {
                int freePort = P2pConnectionService.FindAvailableUdpPort(requestedPort);
                if (Interlocked.Read(ref _publicIpEpoch) == startEpoch)
                {
                    HostPort = freePort.ToString();
                }
            }

            if (Interlocked.Read(ref _publicIpEpoch) == startEpoch)
            {
                DetectedLocalIp = P2pConnectionService.GetLocalIpAddress();
            }

            string? publicIp = await P2pConnectionService.GetPublicIpAddressAsync();
            if (Interlocked.Read(ref _publicIpEpoch) != startEpoch)
            {
                EmitLog($"[DirectPlay] Discarding stale background RefreshIpAsync result '{publicIp ?? "null"}' (epoch advanced by authoritative diagnostics).");
                return;
            }

            DetectedPublicIp = publicIp ?? "Unavailable (Use LAN / VPN IP)";
            OnPropertyChanged(nameof(InviteCode));

            StatusMessage = "Network detected successfully. Share your invite code with friends!";
            StatusColor = "#34D399";
        }
        catch (Exception ex)
        {
            if (Interlocked.Read(ref _publicIpEpoch) != startEpoch)
            {
                EmitLog($"[DirectPlay] Ignoring stale background RefreshIpAsync exception after authoritative diagnostics: {ex.Message}");
                return;
            }

            DetectedPublicIp = "Unavailable";
            StatusMessage = $"IP resolution note: {ex.Message}. Local LAN IP is ready.";
            StatusColor = "#FBBF24";
        }
        finally
        {
            IsLoadingIp = false;
        }
    }

    [RelayCommand]
    public async Task CopyInviteCodeAsync()
    {
        string code = InviteCode;
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop &&
            desktop.MainWindow?.Clipboard is { } clipboard)
        {
            await clipboard.SetTextAsync(code);
            StatusMessage = $"✓ Copied connection code '{code}' to clipboard!";
            StatusColor = "#34D399";
        }
        else
        {
            StatusMessage = $"Connection code: {code}";
        }
    }

    [RelayCommand]
    public async Task CopyLocalInviteCodeAsync()
    {
        string code = $"{DetectedLocalIp}:{HostPort}";
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop &&
            desktop.MainWindow?.Clipboard is { } clipboard)
        {
            await clipboard.SetTextAsync(code);
            StatusMessage = $"✓ Copied LAN/VPN connection code '{code}' to clipboard!";
            StatusColor = "#34D399";
        }
        else
        {
            StatusMessage = $"LAN Connection code: {code}";
        }
    }

    [RelayCommand]
    public async Task RunNetworkDiagnosticsAsync()
    {
        if (IsRunningNetworkDiagnostics)
        {
            return;
        }

        IsRunningNetworkDiagnostics = true;
        try
        {
            int activePort = int.TryParse(HostPort, out int parsed) && parsed is >= 1 and <= 65535
                ? parsed
                : P2pConnectionService.DefaultPort;
            string localIp = string.IsNullOrWhiteSpace(DetectedLocalIp)
                ? P2pConnectionService.GetLocalIpAddress()
                : DetectedLocalIp;

            await RunUpnpAndFirewallAuditAsync(activePort, localIp, CancellationToken.None);
        }
        finally
        {
            IsRunningNetworkDiagnostics = false;
        }
    }

    [RelayCommand]
    public async Task CopyFirewallFixCommandAsync()
    {
        if (string.IsNullOrWhiteSpace(FirewallRemediationCommand))
        {
            return;
        }

        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop &&
            desktop.MainWindow?.Clipboard is { } clipboard)
        {
            await clipboard.SetTextAsync(FirewallRemediationCommand);
            StatusMessage = "✓ Copied elevated PowerShell Windows Firewall rule command to clipboard!";
            StatusColor = "#34D399";
        }
        else
        {
            StatusMessage = FirewallRemediationCommand;
        }
    }

    [RelayCommand]
    public async Task CopyRelayConfigAsync()
    {
        if (string.IsNullOrWhiteSpace(RelayConfigSnippet))
        {
            return;
        }

        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop &&
            desktop.MainWindow?.Clipboard is { } clipboard)
        {
            await clipboard.SetTextAsync(RelayConfigSnippet);
            StatusMessage = "✓ Copied P2P relay configuration snippet to clipboard!";
            StatusColor = "#34D399";
        }
        else
        {
            StatusMessage = RelayConfigSnippet;
        }
    }

    public async Task RunUpnpAndFirewallAuditAsync(int activePort, string localIp, CancellationToken cancellationToken = default)
    {
        // Advance epoch immediately so any in-flight constructor RefreshIpAsync HTTP probe
        // cannot clobber authoritative UPnP / STUN diagnostics or InviteCode.
        Interlocked.Increment(ref _publicIpEpoch);

        // 1. Windows Firewall UDP Coverage Audit (portRangeOffset relative to BaseGameServerPort 11235)
        int portRangeOffset = activePort - 11235;
        try
        {
            FirewallUdpAuditReport? report = FirewallAuditorOverride is not null
                ? await FirewallAuditorOverride(portRangeOffset, 1, localIp, cancellationToken)
                : await WindowsFirewallUdpCoverageAuditor.RunAuditAsync(portRangeOffset, 1, localIp, cancellationToken);

            if (report is null)
            {
                HasFirewallWarning = false;
                FirewallRemediationCommand = string.Empty;
                FirewallAuditStatusText = OperatingSystem.IsWindows()
                    ? "Windows Firewall UDP Audit: netsh check unavailable (verify UDP rule manually if LAN/WAN players cannot join)."
                    : $"Firewall UDP Check: Non-Windows host ({activePort}/UDP + {activePort + P2pConnectionService.VoicePortOffset}/UDP required).";
                FirewallAuditStatusColor = "#94A3B8";
            }
            else if (report.IsFullyCovered)
            {
                HasFirewallWarning = false;
                FirewallRemediationCommand = string.Empty;
                FirewallAuditStatusText = $"✓ Windows Firewall ({report.ActiveProfile} profile) allows inbound UDP for DirectPlay ({activePort}/UDP).";
                FirewallAuditStatusColor = "#34D399";
                EmitLog($"[DirectPlay][Firewall] Inbound UDP covered on active profile '{report.ActiveProfile}' for port {activePort}.");
            }
            else
            {
                HasFirewallWarning = true;
                FirewallRemediationCommand = report.RemediationPowerShellCommand ?? string.Empty;
                FirewallAuditStatusText = report.WarningBanner
                    ?? $"⚠ Windows Firewall ({report.ActiveProfile}) blocks UDP {string.Join(", ", report.UncoveredUdpPorts)}.";
                FirewallAuditStatusColor = "#FBBF24";
                EmitLog($"[DirectPlay][Firewall] {FirewallAuditStatusText}");
            }
        }
        catch (Exception ex)
        {
            HasFirewallWarning = false;
            FirewallAuditStatusText = $"Windows Firewall audit notice: {ex.Message}";
            FirewallAuditStatusColor = "#FBBF24";
            EmitLog($"[DirectPlay][Firewall] Audit exception: {ex.Message}");
        }

        // 2. UPnP IGD Automatic UDP Port Mapping
        UpnpPortMappingResult? upnpResult = null;
        try
        {
            UpnpPortMappingResult upnp = UpnpPortMapperOverride is not null
                ? await UpnpPortMapperOverride(activePort, localIp, cancellationToken)
                : await UpnpIgdPortMapper.TryMapUdpPortAsync(
                    activePort,
                    localIp,
                    description: "Project KONGOR DirectPlay",
                    logSink: EmitLog,
                    cancellationToken: cancellationToken);

            upnpResult = upnp;
            IsUpnpMapped = upnp.Succeeded;
            UpnpStatusText = upnp.Succeeded
                ? $"✓ {upnp.DiagnosticMessage}"
                : $"⚠ {upnp.DiagnosticMessage}";
            UpnpStatusColor = upnp.Succeeded ? "#34D399" : "#FBBF24";

            if (upnp.Succeeded && !string.IsNullOrWhiteSpace(upnp.ExternalIpAddress))
            {
                DetectedPublicIp = upnp.ExternalIpAddress;
                OnPropertyChanged(nameof(InviteCode));
            }
        }
        catch (Exception ex)
        {
            IsUpnpMapped = false;
            UpnpStatusText = $"⚠ UPnP auto-mapping skipped: {ex.Message}";
            UpnpStatusColor = "#FBBF24";
            EmitLog($"[DirectPlay][UPnP] Exception: {ex.Message}");
        }

        // 3. RFC 5389/5780 2-Endpoint STUN NAT Classification
        StunProbeResult? stunResult = null;
        try
        {
            if (StunNatClassifierOverride is not null)
            {
                stunResult = await StunNatClassifierOverride(activePort, cancellationToken);
                EmitLog($"[P2P-NAT] Override STUN classification={stunResult.Classification}, External={stunResult.PrimaryMappedEndPoint?.ToString() ?? "none"}, Detail={stunResult.DiagnosticMessage}");
            }
            else if (UpnpPortMapperOverride is not null)
            {
                stunResult = new StunProbeResult(
                    NatBehaviorClassification.Unknown,
                    LocalEndPoint: null,
                    PrimaryMappedEndPoint: null,
                    SecondaryMappedEndPoint: null,
                    UsedXorMappedAddress: false,
                    IsCgnatAddress: false,
                    PrimaryRttMs: 0,
                    SecondaryRttMs: 0,
                    RejectedSpoofedPackets: 0,
                    DiagnosticMessage: "STUN probe deferred (UPnP override active without StunNatClassifierOverride).");
                EmitLog($"[P2P-NAT] {stunResult.DiagnosticMessage}");
            }
            else
            {
                stunResult = await P2pConnectionService.ProbeNatBehaviorAsync(
                    localPort: 0,
                    timeout: TimeSpan.FromMilliseconds(1500),
                    logSink: EmitLog,
                    cancellationToken: cancellationToken);
            }

            NatClassification = stunResult.Classification;
            string natLabel = P2pConnectionService.FormatNatClassificationLabel(stunResult.Classification);
            NatStatusText = stunResult.Classification switch
            {
                NatBehaviorClassification.OpenInternet or NatBehaviorClassification.ConeNat => $"✓ NAT: {natLabel}",
                NatBehaviorClassification.SymmetricOrCgnat or NatBehaviorClassification.UdpBlocked => $"⚠ NAT: {natLabel}",
                _ => $"NAT: {natLabel}"
            };
            NatStatusColor = stunResult.Classification switch
            {
                NatBehaviorClassification.OpenInternet or NatBehaviorClassification.ConeNat => "#34D399",
                NatBehaviorClassification.SymmetricOrCgnat or NatBehaviorClassification.UdpBlocked => "#FBBF24",
                _ => "#94A3B8"
            };

            if (upnpResult?.Succeeded != true &&
                !stunResult.IsCgnatAddress &&
                !string.IsNullOrWhiteSpace(stunResult.ExternalIpAddress) &&
                (string.IsNullOrWhiteSpace(DetectedPublicIp) ||
                 DetectedPublicIp.Contains("Resolving", StringComparison.OrdinalIgnoreCase) ||
                 DetectedPublicIp.Contains("Unavailable", StringComparison.OrdinalIgnoreCase)))
            {
                DetectedPublicIp = stunResult.ExternalIpAddress;
                OnPropertyChanged(nameof(InviteCode));
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            NatClassification = NatBehaviorClassification.Unknown;
            NatStatusText = $"⚠ STUN NAT probe skipped: {ex.Message}";
            NatStatusColor = "#FBBF24";
            EmitLog($"[P2P-NAT] Exception during STUN probe: {ex.Message}");
        }

        // 4. DirectPlay Transport & CGNAT Relay Planning (COMPEL Proxy / coturn TURN / rathole UDP Tunnel)
        try
        {
            DirectPlayTransportPlan plan = P2pConnectionService.PlanDirectPlayTransport(
                activePort,
                localIp,
                upnpResult,
                stunResult,
                RelayOptionsOverride,
                EmitLog);

            LastTransportPlan = plan;
            NatClassification = plan.NatClassification;
            RecommendedTransportStrategy = plan.Strategy;
            IsRelayRequired = plan.RequiresRelay;

            RelayConfigSnippet = plan.Strategy switch
            {
                DirectPlayTransportStrategy.CompelUdpProxyRelay => plan.CompelRelayEndPoint ?? string.Empty,
                DirectPlayTransportStrategy.CoturnTurnRelay => plan.TurnAllocationProfile ?? plan.TurnServerUri ?? string.Empty,
                DirectPlayTransportStrategy.RatholeUdpTunnel => plan.RatholeClientToml ?? string.Empty,
                _ => plan.RatholeClientToml ?? string.Empty
            };

            RelayStatusText = plan.RequiresRelay
                ? $"⚠ Relay Required ({plan.Strategy}): {plan.DiagnosticSummary}"
                : $"✓ Direct/P2P Ready ({plan.Strategy}): {plan.DiagnosticSummary}";
            RelayStatusColor = plan.RequiresRelay ? "#FBBF24" : "#34D399";

            EmitLog($"[P2P-RELAY] Plan applied: Strategy={plan.Strategy}, NAT={plan.NatClassification}, RequiresRelay={plan.RequiresRelay}, RecommendedInvite={plan.RecommendedInviteAddress}");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            IsRelayRequired = false;
            RelayStatusText = $"⚠ P2P relay planning notice: {ex.Message}";
            RelayStatusColor = "#FBBF24";
            EmitLog($"[P2P-RELAY] Exception during transport planning: {ex.Message}");
        }
    }

    [RelayCommand]
    public async Task LaunchHostGameAsync()
    {
        string rawNick = string.IsNullOrWhiteSpace(PlayerNickname) ? "Player" : PlayerNickname.Trim();
        string nick = P2pCookieCodec.SanitizeNickname(rawNick);
        string gameDir = ResolveGameDirectory();

        P2pHostServerService.StopActiveHostServer();
        int requestedPort = int.TryParse(HostPort, out int parsedHostPort) && parsedHostPort > 0 && parsedHostPort <= 65535
            ? parsedHostPort
            : P2pConnectionService.DefaultPort;
        int activePort = P2pConnectionService.FindAvailableUdpPort(requestedPort);
        HostPort = activePort.ToString();
        OnPropertyChanged(nameof(InviteCode));

        // Attempt UPnP auto port-forward + Windows Firewall UDP reachability audit before launching host
        await RunUpnpAndFirewallAuditAsync(activePort, DetectedLocalIp, CancellationToken.None);

        StatusMessage = $"Starting P2P Host server on UDP {activePort} and launching HoN as '{nick}'...";
        StatusColor = "#38BDF8";

        try
        {
            LauncherSettingsModel settings = LauncherSettingsService.Load();
            settings.LastPlayerNickname = nick;
            LauncherSettingsService.Save(settings);
            P2pConnectionService.EnsureClientDefaultNameConfigured(gameDir, nick);
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[DirectPlay] Host launch settings notice: {ex.Message}");
        }

        EmitLog($"[DirectPlay] Preparing P2P Host launch (OS={System.Runtime.InteropServices.RuntimeInformation.OSDescription}, GameDir='{gameDir}', RequestedPort={requestedPort} -> BoundCandidate={activePort}, BypassMaster={BypassMasterServer}).");

        string loopbackAuth = BypassMasterServer ? P2pLoopbackAuthService.EnsureStarted() : string.Empty;
        P2pHostLaunchResult companionResult = await P2pHostServerService.StartOrRestartHostServerAsync(
            gameDir,
            activePort,
            nick,
            logSink: _logSink);

        if (companionResult.Succeeded && !string.IsNullOrWhiteSpace(companionResult.HostConnectAddress))
        {
            EmitLog($"[DirectPlay] P2P Host ready on {InviteCode} (ConnectAddr={companionResult.HostConnectAddress}, PID={companionResult.ProcessId}).");
            StatusMessage = $"P2P Host active on {InviteCode}! Configure match in-game and share your Invite Code with friends.";
            StatusColor = "#34D399";
        }
        else
        {
            EmitLog($"[DirectPlay] {companionResult.DiagnosticMessage}");
            StatusMessage = $"Launching P2P Host as '{nick}' on UDP {activePort} ({companionResult.DiagnosticMessage})";
            StatusColor = "#FBBF24";
        }

        if (BypassMasterServer && string.IsNullOrWhiteSpace(loopbackAuth))
        {
            loopbackAuth = P2pLoopbackAuthService.EnsureStarted();
        }

        List<string> args = BuildHostClientArguments(
            nick,
            activePort,
            BypassMasterServer,
            companionResult.Succeeded ? companionResult.HostConnectAddress : string.Empty,
            loopbackAuth);

        await _launcherCallback(args);
    }

    [RelayCommand]
    public async Task ConnectToHostAsync()
    {
        if (string.IsNullOrWhiteSpace(JoinAddress))
        {
            StatusMessage = "Please enter or paste the Host IP:Port address!";
            StatusColor = "#F87171";
            return;
        }

        int port = int.TryParse(HostPort, out int p) && p >= 1 && p <= 65535
            ? p
            : P2pConnectionService.DefaultPort;

        string normalized;
        try
        {
            normalized = P2pConnectionService.NormalizeConnectAddress(JoinAddress, port);
        }
        catch (ArgumentException ex)
        {
            Trace.WriteLine($"[SECURITY-ALERT][P2P-INJECTION] Blocked invalid JoinAddress in DirectPlayViewModel: {ex.Message}");
            StatusMessage = "Invalid Host IP:Port address. Enter IPv4:port, [IPv6]:port, or hostname:port.";
            StatusColor = "#F87171";
            return;
        }

        string rawNick = string.IsNullOrWhiteSpace(PlayerNickname) ? "Player" : PlayerNickname.Trim();
        string nick = P2pCookieCodec.SanitizeNickname(rawNick);

        StatusMessage = $"Connecting to {normalized} as '{nick}'...";
        StatusColor = "#38BDF8";

        string guestGameDir = ResolveGameDirectory();
        EmitLog($"[DirectPlay] Connecting as guest '{nick}' to '{normalized}' (OS={System.Runtime.InteropServices.RuntimeInformation.OSDescription}, GameDir='{guestGameDir}', BypassMaster={BypassMasterServer}).");

        try
        {
            LauncherSettingsModel settings = LauncherSettingsService.Load();
            settings.LastPlayerNickname = nick;
            LauncherSettingsService.Save(settings);
            P2pConnectionService.EnsureClientDefaultNameConfigured(guestGameDir, nick);
        }
        catch (Exception ex)
        {
            EmitLog($"[DirectPlay] Guest launch settings notice: {ex.Message}");
        }

        string connectAddress = normalized;
        if (_relaySessionManager != null && _relaySessionManager.EnableClientZstdRelay)
        {
            try
            {
                if (!IPEndPoint.TryParse(normalized, out IPEndPoint? remoteEp))
                {
                    int lastColon = normalized.LastIndexOf(':');
                    if (lastColon > 0 &&
                        int.TryParse(normalized.AsSpan(lastColon + 1), out int resolvedPort) &&
                        resolvedPort >= 1 &&
                        resolvedPort <= 65535)
                    {
                        string hostPart = normalized[..lastColon].Trim('[', ']');
                        using System.Threading.CancellationTokenSource dnsCts = new(TimeSpan.FromMilliseconds(1500));
                        IPAddress[] addresses = await Dns.GetHostAddressesAsync(hostPart, dnsCts.Token).ConfigureAwait(false);
                        IPAddress? preferredIp = null;
                        for (int i = 0; i < addresses.Length; i++)
                        {
                            if (addresses[i].AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                            {
                                preferredIp = addresses[i];
                                break;
                            }
                        }

                        preferredIp ??= addresses.Length > 0 ? addresses[0] : null;
                        if (preferredIp != null)
                        {
                            remoteEp = new IPEndPoint(preferredIp, resolvedPort);
                            Trace.WriteLine($"[DirectPlay] Resolved hostname '{hostPart}:{resolvedPort}' -> {remoteEp} for ClientNetcodeRelay.");
                        }
                    }
                }

                if (remoteEp != null)
                {
                    IPEndPoint effectiveEp = _relaySessionManager.ResolveEffectiveConnectEndPoint(remoteEp);
                    connectAddress = effectiveEp.ToString();
                    Trace.WriteLine($"[DirectPlay] Routed connection through ClientNetcodeRelay: {effectiveEp} -> {remoteEp}");
                }
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"[DirectPlay] ClientNetcodeRelay session notice: {ex.Message}");
            }
        }

        string loopbackAuth = BypassMasterServer ? P2pLoopbackAuthService.EnsureStarted() : string.Empty;
        List<string> args = BuildGuestClientArguments(
            nick,
            connectAddress,
            BypassMasterServer,
            loopbackAuth);

        await _launcherCallback(args);
    }

    public static List<string> BuildHostClientArguments(
        string sanitizedNick,
        int activePort,
        bool bypassMasterServer,
        string hostConnectAddress,
        string loopbackAuthEndpoint)
    {
        if (activePort < 1 || activePort > 65535)
        {
            throw new ArgumentOutOfRangeException(nameof(activePort), activePort, "[SECURITY-ALERT][P2P-INJECTION] Host P2P port must be between 1 and 65535.");
        }

        string safeNick = P2pCookieCodec.SanitizeNickname(string.IsNullOrWhiteSpace(sanitizedNick) ? "Player" : sanitizedNick);
        if (string.IsNullOrWhiteSpace(safeNick))
        {
            safeNick = "Player";
        }

        string standaloneBool = bypassMasterServer ? "true" : "false";
        List<string> execCommands =
        [
            $"Set p2p_nickname {safeNick}",
            $"Set p2p_port {activePort}",
            "Set p2p_mode true",
            "Set p2p_auto_host true",
            $"Set p2p_standalone {standaloneBool}",
            "Set net_enableWatermark false"
        ];

        List<string> args =
        [
            $"+Set p2p_nickname \"{safeNick}\"",
            $"+Set p2p_port {activePort}",
            "+Set p2p_mode true",
            "+Set p2p_auto_host true",
            $"+Set p2p_standalone {standaloneBool}",
            "+Set net_enableWatermark false"
        ];

        if (!string.IsNullOrWhiteSpace(hostConnectAddress))
        {
            string validatedHostAddress = P2pConnectionService.ValidateConnectAddressOrThrow(hostConnectAddress, activePort);
            execCommands.Add($"Set p2p_host_addr {validatedHostAddress}");
            args.Add($"+Set p2p_host_addr \"{validatedHostAddress}\"");
        }

        if (bypassMasterServer && !string.IsNullOrWhiteSpace(loopbackAuthEndpoint))
        {
            string validatedLoopback = P2pConnectionService.ValidateConnectAddressOrThrow(loopbackAuthEndpoint);
            execCommands.Add($"Set login_name {safeNick}");
            execCommands.Add("Set login_password b83a886a5c437ccd9ac15473fd6f1788");
            execCommands.Add("Set login_useSRP false");
            execCommands.Add("Set login_autoLogin false");
            execCommands.Add("Set login_rememberPassword false");

            args.Add($"+Set login_name \"{safeNick}\"");
            args.Add("+Set login_password \"b83a886a5c437ccd9ac15473fd6f1788\"");
            args.Add("+Set login_useSRP false");
            args.Add("+Set login_autoLogin false");
            args.Add("+Set login_rememberPassword false");
            args.Add($"-masterserver {validatedLoopback}");
            args.Add($"-webserver {validatedLoopback}");
            args.Add($"-messageserver {validatedLoopback}");
        }

        string hostExecScript = string.Join("; ", execCommands) + ";";
        args.Add($"-execute \"{hostExecScript}\"");
        return args;
    }

    public static List<string> BuildGuestClientArguments(
        string sanitizedNick,
        string normalizedAddress,
        bool bypassMasterServer,
        string loopbackAuthEndpoint)
    {
        string validatedAddress = P2pConnectionService.ValidateConnectAddressOrThrow(normalizedAddress);
        string safeNick = P2pCookieCodec.SanitizeNickname(string.IsNullOrWhiteSpace(sanitizedNick) ? "Player" : sanitizedNick);
        if (string.IsNullOrWhiteSpace(safeNick))
        {
            safeNick = "Player";
        }

        string standaloneBool = bypassMasterServer ? "true" : "false";
        List<string> execCommands =
        [
            $"Set p2p_nickname {safeNick}",
            "Set p2p_mode true",
            "Set p2p_auto_connect true",
            $"Set p2p_connect_addr {validatedAddress}",
            $"Set p2p_last_ip {validatedAddress}",
            $"Set p2p_standalone {standaloneBool}",
            "Set net_enableWatermark false"
        ];

        List<string> args =
        [
            $"+Set p2p_nickname \"{safeNick}\"",
            "+Set p2p_mode true",
            "+Set p2p_auto_connect true",
            $"+Set p2p_connect_addr \"{validatedAddress}\"",
            $"+Set p2p_last_ip \"{validatedAddress}\"",
            $"+Set p2p_standalone {standaloneBool}",
            "+Set net_enableWatermark false"
        ];

        if (bypassMasterServer && !string.IsNullOrWhiteSpace(loopbackAuthEndpoint))
        {
            string validatedLoopback = P2pConnectionService.ValidateConnectAddressOrThrow(loopbackAuthEndpoint);
            execCommands.Add($"Set login_name {safeNick}");
            execCommands.Add("Set login_password b83a886a5c437ccd9ac15473fd6f1788");
            execCommands.Add("Set login_useSRP false");
            execCommands.Add("Set login_autoLogin false");
            execCommands.Add("Set login_rememberPassword false");

            args.Add($"+Set login_name \"{safeNick}\"");
            args.Add("+Set login_password \"b83a886a5c437ccd9ac15473fd6f1788\"");
            args.Add("+Set login_useSRP false");
            args.Add("+Set login_autoLogin false");
            args.Add("+Set login_rememberPassword false");
            args.Add($"-masterserver {validatedLoopback}");
            args.Add($"-webserver {validatedLoopback}");
            args.Add($"-messageserver {validatedLoopback}");
        }

        string guestExecScript = string.Join("; ", execCommands) + ";";
        args.Add($"-execute \"{guestExecScript}\"");
        return args;
    }

    private static string ResolveGameDirectory()
    {
        return LocationGuard.ResolveGameClientDirectory(Environment.CurrentDirectory)
            ?? LocationGuard.ResolveGameClientDirectory(AppContext.BaseDirectory)
            ?? LocationGuard.ResolveGameClientDirectory()
            ?? Environment.CurrentDirectory;
    }
}
