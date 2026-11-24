// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using NEXUS.Contracts.ControlPlane;

namespace NEXUS.Contracts.Services;

/// <summary>
///     Client contract for executing management probes and lifecycle actions against COMPEL game server control planes.
/// </summary>
public interface IControlPlaneClient
{
    /// <summary>
    ///     Probes a host control plane for connectivity and latency.
    /// </summary>
    Task<CompelPingResponseDto?> PingAsync(string hostAddress, int port, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Retrieves current configuration, port allocation, and instance status from a host control plane.
    /// </summary>
    Task<CompelStatusResponseDto?> GetStatusAsync(string hostAddress, int port, string? authToken = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    ///     Executes an arbitrary lifecycle action against a host control plane.
    /// </summary>
    Task<CompelActionResponseDto> ExecuteActionAsync(string hostAddress, int port, string action,
        string? authToken = null, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Starts all configured match server instances on the target host.
    /// </summary>
    Task<CompelActionResponseDto> StartInstancesAsync(string hostAddress, int port, string? authToken = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    ///     Stops all running match server instances on the target host.
    /// </summary>
    Task<CompelActionResponseDto> StopInstancesAsync(string hostAddress, int port, string? authToken = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    ///     Restarts all match server instances on the target host.
    /// </summary>
    Task<CompelActionResponseDto> RestartInstancesAsync(string hostAddress, int port, string? authToken = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    ///     Triggers immediate distribution synchronisation against the CDN bucket.
    /// </summary>
    Task<CompelActionResponseDto> SyncDistributionAsync(string hostAddress, int port, string? authToken = null,
        CancellationToken cancellationToken = default);
}
