// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

namespace NEXUS.Contracts.Replay;

/// <summary>
///     High-value combat or objective event pin positioned along a match timeline.
/// </summary>
public sealed class MatchTimelineEventPin
{
    /// <summary>
    ///     Game time in seconds at which the event occurred.
    /// </summary>
    public required int MatchTimeSeconds { get; set; }

    /// <summary>
    ///     Event category classification ("KILL", "TOWER", "BOSS", "MULTIKILL").
    /// </summary>
    public required string EventType { get; set; }

    /// <summary>
    ///     Text description of the event.
    /// </summary>
    public required string Description { get; set; }

    /// <summary>
    ///     Attacking / initiating hero identifier.
    /// </summary>
    public string AttackerHero { get; set; } = string.Empty;

    /// <summary>
    ///     Victim / target hero identifier.
    /// </summary>
    public string VictimHero { get; set; } = string.Empty;

    /// <summary>
    ///     Map coordinate X where event took place.
    /// </summary>
    public float PositionX { get; set; }

    /// <summary>
    ///     Map coordinate Y where event took place.
    /// </summary>
    public float PositionY { get; set; }

    /// <summary>
    ///     Team index associated with the event (1 = Legion, 2 = Hellbourne, 0 = Neutral).
    /// </summary>
    public int Team { get; set; }

    /// <summary>
    ///     Optional ability or item identifier involved in the event.
    /// </summary>
    public string AbilityOrItem { get; set; } = string.Empty;
}
