// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

namespace NEXUS.Contracts.Replay;

/// <summary>
///     Detailed result of resolving a player camera target query.
/// </summary>
public sealed class PlayerResolutionResult
{
    /// <summary>
    ///     Resolved client index (0..9).
    /// </summary>
    public int ClientIndex { get; set; }

    /// <summary>
    ///     Player metadata instance if resolved.
    /// </summary>
    public ReplayPlayerMetadata? Player { get; set; }

    /// <summary>
    ///     Confidence tier of the resolution match.
    /// </summary>
    public PlayerMatchConfidence Confidence { get; set; }

    /// <summary>
    ///     Detailed rationale explaining how the player was resolved.
    /// </summary>
    public string MatchReason { get; set; } = string.Empty;

    /// <summary>
    ///     Original query string provided by caller.
    /// </summary>
    public string Query { get; set; } = string.Empty;

    /// <summary>
    ///     Formats the resolution result into a readable log string.
    /// </summary>
    public override string ToString()
    {
        string playerName = Player != null ? $"{Player.Name} ({Player.CleanHeroName})" : "Unassigned";
        return $"Resolved[Slot={ClientIndex}, Player={playerName}, Confidence={Confidence}, Reason='{MatchReason}']";
    }
}
