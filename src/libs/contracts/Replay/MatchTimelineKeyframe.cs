// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

namespace NEXUS.Contracts.Replay;

/// <summary>
///     Point-in-time snapshot of the game state across all players and structures at a specific elapsed second.
/// </summary>
public sealed class MatchTimelineKeyframe
{
    /// <summary>
    ///     Elapsed game time in whole seconds.
    /// </summary>
    public required int Second { get; set; }

    /// <summary>
    ///     Formatted elapsed time string (e.g. "15:42").
    /// </summary>
    public required string ElapsedFormatted { get; set; }

    /// <summary>
    ///     Player state snapshots at this keyframe.
    /// </summary>
    public List<ReplayPlayerSnapshot> Players { get; set; } = [];

    /// <summary>
    ///     Building and structure alive states keyed by structure identifier.
    /// </summary>
    public Dictionary<string, bool> Structures { get; set; } = [];
}
