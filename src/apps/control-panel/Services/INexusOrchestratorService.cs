// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using NEXUS.ControlPanel.GUI.Services.Models;

namespace NEXUS.ControlPanel.GUI.Services;

public delegate Task<(int ExitCode, string Output)> DockerStackScriptRunner(
    string action,
    IEnumerable<string>? extraArgs,
    string repoRoot,
    Action<string, bool>? onLine,
    CancellationToken cancellationToken);

public interface INexusOrchestratorService : IDisposable
{
    ServerState CurrentClusterState { get; }
    IReadOnlyList<LogMessage> LogHistory { get; }
    string AspireDashboardUrl { get; }

    event Action<ServerState>? OnClusterStateChanged;
    event Action<string, ServerState>? OnServiceStateChanged;
    event Action<LogMessage>? OnLogReceived;

    ServerState GetServiceState(string serviceKey);

    Task StartClusterAsync(bool withGameServers = false, string? customWorkingDir = null,
        CancellationToken cancellationToken = default);

    Task StopClusterAsync(CancellationToken cancellationToken = default);

    Task RestartClusterAsync(bool withGameServers = false, string? customWorkingDir = null,
        CancellationToken cancellationToken = default);

    Task StartServiceAsync(string serviceKey, string? customWorkingDir = null,
        CancellationToken cancellationToken = default);

    Task StopServiceAsync(string serviceKey, CancellationToken cancellationToken = default);

    Task RestartServiceAsync(string serviceKey, string? customWorkingDir = null,
        CancellationToken cancellationToken = default);

    Task StartAllAsync(bool includeGameServers = false, CancellationToken cancellationToken = default);
    Task StopAllAsync(CancellationToken cancellationToken = default);

    Task LaunchGameClientAsync(string? customClientPath = null, CancellationToken cancellationToken = default);
    Task LaunchGameServerAsync(string? customServerPath = null, CancellationToken cancellationToken = default);

    Task LaunchQueueArcadeServerAsync(string? customServerPath = null, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    Task StopQueueArcadeServersAsync(CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    Task<(bool Enabled, bool LiveRedisSynced, string StatusSummary)> GetQueueArcadeStateAsync(
        CancellationToken cancellationToken = default) =>
        Task.FromResult((true, false, "Enabled (Default)"));

    Task<(bool Enabled, bool LiveRedisSynced, string StatusMessage)> SetQueueArcadeEnabledAsync(
        bool enabled,
        CancellationToken cancellationToken = default) =>
        Task.FromResult((enabled, false, enabled ? "Queue Arcade enabled." : "Queue Arcade paused."));

    Task<RuntimeExitInspectionResult> InspectRuntimeForExitAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(RuntimeExitInspectionResult.Idle);

    void DetachForBackgroundExit() { }

    void AppendLog(LogMessage log);
    void ClearLogs();
}
