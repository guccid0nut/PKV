// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ============================================================================
// File: ZombieSocketDetector.cs
// System: COMPEL Server Supervisor [SR-08]
// Subsystem: Zombie Socket Eviction & Active Port Reclamation
// ============================================================================

using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace COMPEL.Services.Supervision;

/// <summary>
///     Detects and evicts lingering or zombie UDP sockets (e.g. following an abnormal crash of hon_x64.exe)
///     to guarantee that dedicated game ports and voice ports are released and immediately bindable
///     before launching a new worker process.
///     Backed by <see cref="SocketReclamationService"/> and native kernel socket table queries.
/// </summary>
public static class ZombieSocketDetector
{
    /// <summary>
    ///     Checks synchronously if the specified UDP port is currently free and available for binding.
    /// </summary>
    public static bool IsPortAvailable(int port)
    {
        return SocketReclamationService.IsPortAvailable(port);
    }

    /// <summary>
    ///     Ensures the specified port is available, actively querying the kernel socket table
    ///     and evicting lingering zombie processes if occupied.
    /// </summary>
    public static async Task<bool> EnsurePortAvailableAsync(
        int port,
        int maxWaitMs = 250,
        ILogger? logger = null,
        CancellationToken cancellationToken = default)
    {
        PortReclamationResult result = await SocketReclamationService.ReclaimPortAsync(
            port,
            expectedOrphanPid: 0,
            maxWaitMs: maxWaitMs,
            logger: logger,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        return result.Success;
    }

    /// <summary>
    ///     Verifies and evicts zombie sockets across both game and voice UDP ports concurrently.
    /// </summary>
    public static async Task<bool> EvictZombieSocketsAsync(
        int gamePort,
        int voicePort,
        int maxWaitMs = 300,
        ILogger? logger = null,
        CancellationToken cancellationToken = default)
    {
        return await SocketReclamationService.EvictZombiePortsAsync(
            gamePort,
            voicePort,
            expectedGamePid: 0,
            maxWaitMs: maxWaitMs,
            logger: logger,
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }
}
