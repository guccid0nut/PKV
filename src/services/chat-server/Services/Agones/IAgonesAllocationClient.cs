// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ============================================================================
// File: IAgonesAllocationClient.cs
// System: TRANSMUTANSTEIN - Chat Server & Matchmaking
// Subsystem: Cloud-Native Fleet Orchestration (Agones Integration)
// ============================================================================

using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace TRANSMUTANSTEIN.ChatServer.Services.Agones;

/// <summary>
///     Client interface for allocating dedicated game servers via Agones GameServerAllocation API.
/// </summary>
public interface IAgonesAllocationClient
{
    /// <summary>
    ///     Gets whether Agones allocation is enabled and configured.
    /// </summary>
    bool IsEnabled { get; }

    /// <summary>
    ///     Requests allocation of a game server from the Agones fleet matching the desired regions.
    /// </summary>
    /// <param name="desiredRegions">Target regions requested by the matchmaking group.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The allocation result including connection address, ports, and pod identity.</returns>
    Task<GameServerAllocationResult> AllocateGameServerAsync(
        IReadOnlyList<string> desiredRegions,
        CancellationToken cancellationToken = default);
}
