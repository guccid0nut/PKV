// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

namespace NEXUS.Contracts.Replay;

/// <summary>
///     Comprehensive parsed match replay timeline and player statistics DTO.
/// </summary>
public sealed class MatchReplayTimelineDto
{
    /// <summary>
    ///     Unique match identifier.
    /// </summary>
    public required int MatchID { get; set; }

    /// <summary>
    ///     Map name on which the match was played (e.g. "caldavar").
    /// </summary>
    public required string Map { get; set; }

    /// <summary>
    ///     Game mode string (e.g. "Ranked (All Pick)").
    /// </summary>
    public required string GameMode { get; set; }

    /// <summary>
    ///     Total duration of the match in seconds.
    /// </summary>
    public required int TotalDurationSeconds { get; set; }

    /// <summary>
    ///     Winning team index (1 = Legion, 2 = Hellbourne).
    /// </summary>
    public required int WinnerTeam { get; set; }

    /// <summary>
    ///     Total score achieved by Legion.
    /// </summary>
    public int ScoreLegion { get; set; }

    /// <summary>
    ///     Total score achieved by Hellbourne.
    /// </summary>
    public int ScoreHellbourne { get; set; }

    /// <summary>
    ///     Underlying telemetry source ("server_log", "honreplay", or "db_reconstruction").
    /// </summary>
    public string TelemetrySource { get; set; } = "db_reconstruction";

    /// <summary>
    ///     Human-readable telemetry fidelity badge label ("Verified Server Telemetry" vs "Tactical Reconstruction").
    /// </summary>
    public string TelemetryLabel { get; set; } = "Tactical Reconstruction";

    /// <summary>
    ///     Player summaries and final match statistics.
    /// </summary>
    public List<ReplayPlayerSummary> Players { get; set; } = [];

    /// <summary>
    ///     Key event pins on the match timeline (kills, towers, objectives).
    /// </summary>
    public List<MatchTimelineEventPin> EventPins { get; set; } = [];

    /// <summary>
    ///     Periodic snapshot keyframes throughout the replay duration.
    /// </summary>
    public List<MatchTimelineKeyframe> Keyframes { get; set; } = [];
}
