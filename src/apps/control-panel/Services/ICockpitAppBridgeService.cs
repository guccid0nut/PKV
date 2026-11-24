// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace NEXUS.ControlPanel.GUI.Services;

public sealed record CockpitBinaryResolution(
    bool Found,
    string? ExecutablePath,
    string? WorkingDirectory,
    bool LaunchViaDotnetHost,
    string LayoutKind,
    string DiagnosticMessage);

public sealed record WillowmakerCockpitStatus(
    bool IsConnected,
    int ProcessId,
    string MasterServerEndpoint,
    string CdnEndpoint,
    bool IsGameRunning,
    double LatencyMs,
    string PipePath,
    string StatusMessage,
    string StatusColor,
    string HostKind = "Willowmaker");

public sealed record CockpitLaunchOrSyncResult(
    bool Succeeded,
    bool WasAlreadyRunning,
    bool SyncedViaIpc,
    bool LaunchedNewProcess,
    int ProcessId,
    string AppliedMasterServer,
    string AppliedCdnEndpoint,
    string? ExecutablePath,
    string StatusMessage);

public sealed record CompelGuiLaunchResult(
    bool Succeeded,
    int ProcessId,
    string? ExecutablePath,
    string LayoutKind,
    string StatusMessage);

public sealed record IpcPipeHealthStatus(
    string Name,
    bool IsConnected,
    string PipePath,
    double LatencyMs,
    string StatusMessage,
    string StatusColor);

public sealed record CockpitServerPipeHint(
    string InstanceTag,
    string ServerName,
    int GamePort,
    int WorkerPort,
    bool IsRunning,
    bool IsContainer,
    bool? ReportedIpcConnected,
    string? ReportedIpcPath,
    string? ReportedIpcTransport);

public sealed record CockpitPipeBadgeItem(
    string Key,
    string Label,
    string LabelColor,
    string StatusMessage,
    string StatusColor,
    string PipePath,
    string ToolTipText,
    bool IsConnected,
    double LatencyMs = 0);

public sealed record CoreClusterReadinessStatus(
    bool IsMasterHealthOk,
    int MasterStatusCode,
    double MasterLatencyMs,
    string MasterStatusMessage,
    string MasterStatusColor,
    bool IsChatTcpReady,
    double ChatLatencyMs,
    string ChatStatusMessage,
    string ChatStatusColor);

public sealed record ExportDryRunStageItem(
    string StageName,
    bool Passed,
    string Detail,
    string BadgeText,
    string BadgeColor);

public sealed record ExportDryRunVerificationResult(
    bool Succeeded,
    int ExitCode,
    string SelectedStagingRoot,
    double SelectedDriveFreeGb,
    string SummaryMessage,
    IReadOnlyList<ExportDryRunStageItem> Stages,
    string RawOutput);

/// <summary>
/// Unified App Cockpit bridge linking <c>NEXUS.ControlPanel</c> to <c>WILLOWMAKER</c> (Named Pipe IPC
/// + cross-layout launch), <c>COMPEL.GUI</c> (1-click fleet manager launch), <c>TAO</c> / native K2
/// client IPC diagnostics, core cluster readiness probes, and pre-export dry-run verification.
/// </summary>
public interface ICockpitAppBridgeService
{
    CockpitBinaryResolution ResolveWillowmakerBinary(string? rootOverride = null, string? targetPlatformOverride = null);

    CockpitBinaryResolution ResolveCompelGuiBinary(string? rootOverride = null, string? targetPlatformOverride = null);

    Task<WillowmakerCockpitStatus> ProbeWillowmakerIpcAsync(
        string? pipeNameOverride = null,
        int timeoutMs = 250,
        CancellationToken cancellationToken = default);

    Task<CockpitLaunchOrSyncResult> OpenOrSyncWillowmakerAsync(
        string masterServerEndpoint,
        string cdnEndpoint,
        string? rootOverride = null,
        string? pipeNameOverride = null,
        string? discordClientId = null,
        CancellationToken cancellationToken = default);

    Task<CompelGuiLaunchResult> OpenCompelGuiAsync(
        string? rootOverride = null,
        CancellationToken cancellationToken = default);

    Task<IpcPipeHealthStatus> ProbeNativeClientPipeAsync(
        string? pipeNameOverride = null,
        int timeoutMs = 200,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<CockpitPipeBadgeItem>> ProbeConnectedCockpitPipesAsync(
        IReadOnlyList<CockpitServerPipeHint>? knownServerInstances = null,
        bool passiveNativeClientProbe = true,
        string? willowmakerPipeOverride = null,
        string? nativeClientPipeOverride = null,
        IReadOnlyList<string>? discoveredPipeNamesOverride = null,
        CancellationToken cancellationToken = default);

    Task<CoreClusterReadinessStatus> ProbeCoreClusterEndpointsAsync(
        int masterPort = 55555,
        int chatPort = 11031,
        HttpMessageHandler? httpHandlerOverride = null,
        CancellationToken cancellationToken = default);

    (string StagingRoot, double FreeGb) SelectExportStagingRoot(
        string repoRoot,
        IReadOnlyList<(string DriveRoot, long FreeBytes)>? drivesOverride = null);

    Task<ExportDryRunVerificationResult> VerifyExportReadinessDryRunAsync(
        string? repoRootOverride = null,
        CancellationToken cancellationToken = default);
}
