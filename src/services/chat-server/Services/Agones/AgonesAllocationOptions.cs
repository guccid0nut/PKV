// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ============================================================================
// File: AgonesAllocationOptions.cs
// System: TRANSMUTANSTEIN - Chat Server & Matchmaking
// Subsystem: Cloud-Native Fleet Orchestration (Agones Integration)
// ============================================================================

namespace TRANSMUTANSTEIN.ChatServer.Services.Agones;

/// <summary>
///     Configuration options for Agones cloud-native game server allocation.
/// </summary>
public sealed class AgonesAllocationOptions
{
    public const string SectionName = "AgonesAllocation";

    /// <summary>
    ///     Gets or sets whether cloud-native Agones GameServer allocation is enabled.
    /// </summary>
    public bool Enabled { get; set; } = false;

    /// <summary>
    ///     Gets or sets the Agones Allocator endpoint URL (REST API).
    /// </summary>
    public string Endpoint { get; set; } = "http://localhost:5000";

    /// <summary>
    ///     Gets or sets the Kubernetes namespace containing the dedicated game server fleet.
    /// </summary>
    public string Namespace { get; set; } = "default";

    /// <summary>
    ///     Gets or sets the target Agones Fleet name to allocate pods from.
    /// </summary>
    public string FleetName { get; set; } = "kongor-dedicated-fleet";

    /// <summary>
    ///     Gets or sets the allocation timeout in seconds.
    /// </summary>
    public int TimeoutSeconds { get; set; } = 5;

    /// <summary>
    ///     Gets or sets whether to fallback to allocating any available server if the preferred region is unavailable.
    /// </summary>
    public bool FallbackToAnyRegion { get; set; } = true;
}
