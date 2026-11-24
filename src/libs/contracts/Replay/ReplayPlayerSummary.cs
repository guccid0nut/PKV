// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

namespace NEXUS.Contracts.Replay;

/// <summary>
///     Final match statistics and summary for an individual player in a replay.
/// </summary>
public sealed class ReplayPlayerSummary
{
    /// <summary>
    ///     Account identifier of the player.
    /// </summary>
    public required int AccountID { get; set; }

    /// <summary>
    ///     In-game account nickname.
    /// </summary>
    public required string AccountName { get; set; }

    /// <summary>
    ///     Technical hero entity identifier (e.g. "Hero_Accursed").
    /// </summary>
    public required string HeroIdentifier { get; set; }

    /// <summary>
    ///     User-friendly hero display name (e.g. "Accursed").
    /// </summary>
    public required string HeroDisplayName { get; set; }

    /// <summary>
    ///     Team index (1 = Legion, 2 = Hellbourne).
    /// </summary>
    public required int Team { get; set; }

    /// <summary>
    ///     Final hero level achieved at match end.
    /// </summary>
    public int FinalLevel { get; set; }

    /// <summary>
    ///     Final kill count.
    /// </summary>
    public int FinalKills { get; set; }

    /// <summary>
    ///     Final death count.
    /// </summary>
    public int FinalDeaths { get; set; }

    /// <summary>
    ///     Final assist count.
    /// </summary>
    public int FinalAssists { get; set; }

    /// <summary>
    ///     Final gold per minute (GPM).
    /// </summary>
    public int FinalGpm { get; set; }

    /// <summary>
    ///     Final experience per minute (XPM).
    /// </summary>
    public int FinalXpm { get; set; }

    /// <summary>
    ///     Final accumulated gold.
    /// </summary>
    public int FinalGold { get; set; }

    /// <summary>
    ///     List of final inventory item identifiers.
    /// </summary>
    public List<string> FinalItems { get; set; } = [];

    /// <summary>
    ///     List of abilities leveled and equipped by the player.
    /// </summary>
    public List<ReplayPlayerAbilityDto> Abilities { get; set; } = [];
}
