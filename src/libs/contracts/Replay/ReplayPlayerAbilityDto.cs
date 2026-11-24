// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

namespace NEXUS.Contracts.Replay;

/// <summary>
///     Ability snapshot metadata for an individual hero ability slot.
/// </summary>
public sealed class ReplayPlayerAbilityDto
{
    /// <summary>
    ///     Ability hotkey slot designation ("Q", "W", "E", "R", "D", "F").
    /// </summary>
    public required string Slot { get; set; }

    /// <summary>
    ///     Ability internal name.
    /// </summary>
    public required string Name { get; set; }

    /// <summary>
    ///     Relative path to the ability icon asset.
    /// </summary>
    public required string IconPath { get; set; }

    /// <summary>
    ///     Current ability skill rank level (1 to 4).
    /// </summary>
    public int Level { get; set; } = 4;

    /// <summary>
    ///     Total base cooldown duration in seconds or milliseconds.
    /// </summary>
    public int CooldownTotal { get; set; }

    /// <summary>
    ///     Remaining cooldown duration before ability is castable.
    /// </summary>
    public int CooldownRemaining { get; set; }

    /// <summary>
    ///     Indicates whether the ability is currently off cooldown and ready to cast.
    /// </summary>
    public bool IsReady => CooldownRemaining <= 0;
}
