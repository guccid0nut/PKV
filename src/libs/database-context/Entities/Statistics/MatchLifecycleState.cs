// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

namespace MERRICK.DatabaseContext.Entities.Statistics;

/// <summary>
///     Represents the authoritative lifecycle state of a match from allocation to termination.
/// </summary>
public enum MatchLifecycleState
{
    /// <summary>
    ///     Match ID has been minted but server has not entered the lobby yet.
    /// </summary>
    Allocated = 0,

    /// <summary>
    ///     Game server is in the pre-game lobby waiting for players or picking heroes.
    /// </summary>
    Lobby = 1,

    /// <summary>
    ///     Server and clients are transitioning and loading the map.
    /// </summary>
    Loading = 2,

    /// <summary>
    ///     Match is actively running in-game (first creep wave / active combat).
    /// </summary>
    Active = 3,

    /// <summary>
    ///     Match reached normal game end (base destroyed / concession) and submitted post-match statistics.
    /// </summary>
    Completed = 4,

    /// <summary>
    ///     Match was cancelled prior to gameplay start (e.g. host closed lobby, players left during pick phase).
    /// </summary>
    Cancelled = 5,

    /// <summary>
    ///     Match was terminated prematurely during active gameplay (e.g. all players disconnected, server supervisor recycled).
    /// </summary>
    Abandoned = 6,

    /// <summary>
    ///     Match aborted due to fatal engine exception or unrecoverable error.
    /// </summary>
    Error = 7
}
