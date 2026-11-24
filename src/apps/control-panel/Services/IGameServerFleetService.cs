// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using NEXUS.ControlPanel.GUI.Services.Models;

namespace NEXUS.ControlPanel.GUI.Services;

public interface IGameServerFleetService
{
    Task<List<GameServerHostEntry>> GetDiscoveredHostsAsync(CancellationToken cancellationToken = default);

    Task<(bool Success, long LatencyMs, CompelPingResponseDto? Response)> PingHostAsync(string hostAddress, int port,
        CancellationToken cancellationToken = default);

    Task<(bool Success, CompelStatusResponseDto? Status, string? ErrorMessage)> GetHostStatusAsync(string hostAddress,
        int port, string? authToken = null, CancellationToken cancellationToken = default);

    Task<(bool Success, string Message)> StartInstancesAsync(string hostAddress, int port, string? authToken = null,
        CancellationToken cancellationToken = default);

    Task<(bool Success, string Message)> StopInstancesAsync(string hostAddress, int port, string? authToken = null,
        CancellationToken cancellationToken = default);

    Task<(bool Success, string Message)> RestartInstancesAsync(string hostAddress, int port, string? authToken = null,
        CancellationToken cancellationToken = default);

    Task<(bool Success, string Message)> SyncDistributionAsync(string hostAddress, int port, string? authToken = null,
        CancellationToken cancellationToken = default);

    Task<(bool Success, string Message)> StartWorkerAsync(string hostAddress, int port, int targetServerPort,
        string? authToken = null, CancellationToken cancellationToken = default);

    Task<(bool Success, string Message)> StopWorkerAsync(string hostAddress, int port, int targetServerPort,
        string? authToken = null, CancellationToken cancellationToken = default);

    Task<(bool Success, string Message)> RestartWorkerAsync(string hostAddress, int port, int targetServerPort,
        string? authToken = null, CancellationToken cancellationToken = default);

    Task<(bool Success, List<CompelDedicatedServerLogLineDto> Logs, string? ErrorMessage)> GetServerLogsAsync(
        string hostAddress, int port, int targetServerPort, int tail = 100, string? authToken = null,
        CancellationToken cancellationToken = default);

    IAsyncEnumerable<CompelDedicatedServerLogLineDto> StreamServerLogsAsync(
        string hostAddress, int port, int targetServerPort, int tail = 50, string? authToken = null,
        CancellationToken cancellationToken = default);

    Task<(bool Success, string Channel, string Message)> SendWorkerCommandAsync(string hostAddress, int port,
        int targetServerPort, string command, string? authToken = null, CancellationToken cancellationToken = default);

    bool IsEndpointKnownOffline(string hostAddress, int port) => false;
}
