// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

namespace NEXUS.Contracts.Replay;

/// <summary>
///     Snapshot of a single player's positional, resource, and status data at a specific timeline keyframe.
/// </summary>
public sealed class ReplayPlayerSnapshot
{
    /// <summary>
    ///     Account identifier of the player.
    /// </summary>
    public required int AccountID { get; set; }

    /// <summary>
    ///     Player account name.
    /// </summary>
    public required string AccountName { get; set; }

    /// <summary>
    ///     Technical hero identifier (e.g. "Hero_Devourer").
    /// </summary>
    public required string HeroIdentifier { get; set; }

    /// <summary>
    ///     Hero display name.
    /// </summary>
    public required string HeroDisplayName { get; set; }

    /// <summary>
    ///     Team index (1 = Legion, 2 = Hellbourne).
    /// </summary>
    public required int Team { get; set; }

    /// <summary>
    ///     Current hero level.
    /// </summary>
    public int Level { get; set; }

    /// <summary>
    ///     Current kill count.
    /// </summary>
    public int Kills { get; set; }

    /// <summary>
    ///     Current death count.
    /// </summary>
    public int Deaths { get; set; }

    /// <summary>
    ///     Current assist count.
    /// </summary>
    public int Assists { get; set; }

    /// <summary>
    ///     Current gold per minute.
    /// </summary>
    public int Gpm { get; set; }

    /// <summary>
    ///     Current experience per minute.
    /// </summary>
    public int Xpm { get; set; }

    /// <summary>
    ///     Current unspent gold balance.
    /// </summary>
    public int Gold { get; set; }

    /// <summary>
    ///     Health percentage (0.0 to 100.0).
    /// </summary>
    public float HealthPercent { get; set; }

    /// <summary>
    ///     Mana percentage (0.0 to 100.0).
    /// </summary>
    public float ManaPercent { get; set; }

    /// <summary>
    ///     Normalized map coordinate X (0.0 to 1.0).
    /// </summary>
    public float PositionX { get; set; }

    /// <summary>
    ///     Normalized map coordinate Y (0.0 to 1.0).
    /// </summary>
    public float PositionY { get; set; }

    /// <summary>
    ///     Unit heading orientation angle in degrees.
    /// </summary>
    public float HeadingAngle { get; set; }

    /// <summary>
    ///     Whether the player unit is currently alive.
    /// </summary>
    public bool IsAlive { get; set; }

    /// <summary>
    ///     Current item identifiers in inventory.
    /// </summary>
    public List<string> Items { get; set; } = [];

    /// <summary>
    ///     Current abilities and cooldown states.
    /// </summary>
    public List<ReplayPlayerAbilityDto> Abilities { get; set; } = [];

    /// <summary>
    ///     Most recently cast ability identifier, if any.
    /// </summary>
    public string? LastCastAbility { get; set; }
}
