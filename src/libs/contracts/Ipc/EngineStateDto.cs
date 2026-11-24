// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using MemoryPack;

namespace NEXUS.Contracts.Ipc;

/// <summary>
///     Structured engine telemetry snapshot exchanged over IPC.
/// </summary>
[MemoryPackable]
public sealed partial class EngineStateDto
{
    public double CurrentFps { get; set; }
    public long MemoryWorkingSetBytes { get; set; }
    public int ActiveMatchId { get; set; }
    public string CurrentMap { get; set; } = string.Empty;
    public string PlayerName { get; set; } = string.Empty;
    public int ConnectedPlayers { get; set; }
    public long UptimeSeconds { get; set; }
    public int GamePhase { get; set; }
    public string GamePhaseName { get; set; } = "Login (0-1)";
    public bool IsSpectator { get; set; }
    public int ActiveSpectatorCount { get; set; }
    public int MaxSpectators { get; set; }
    public string? SpectatorTarget { get; set; }
    public int CanonicalGamePhase { get; set; }
    public bool IsAuthenticated { get; set; }
    public bool IsActiveMatch { get; set; }
}
