// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace MERRICK.DatabaseContext.Entities.Statistics;

/// <summary>
///     Normalized 1NF relational entity representing an individual ability level-up
///     or skill action during a match. Mapped to <c>stat.MatchAbilityEvents</c>.
/// </summary>
[Table("MatchAbilityEvents", Schema = "stat")]
[Index(nameof(AbilityIdentifier))]
[Index(nameof(HeroIdentifier))]
[Index(nameof(MatchID), nameof(AccountID), nameof(GameTimeSeconds))]
public class MatchAbilityEvent
{
    [Key]
    public int ID { get; set; }

    [NotMapped]
    [System.Text.Json.Serialization.JsonIgnore]
    public int Id
    {
        get => ID;
        set => ID = value;
    }

    public int PlayerStatisticID { get; set; }

    [ForeignKey(nameof(PlayerStatisticID))]
    [System.Text.Json.Serialization.JsonIgnore]
    public PlayerStatistics PlayerStatistic { get; set; } = null!;

    public int MatchID { get; set; }

    public int AccountID { get; set; }

    /// <summary>
    ///     Hero internal identifier (e.g. "Hero_Valkyrie", "Hero_Pyromancer").
    /// </summary>
    [MaxLength(100)]
    public string HeroIdentifier { get; set; } = string.Empty;

    /// <summary>
    ///     Normalized ability identifier (e.g. "Ability_Valkyrie1", "Ability_AttributeBoost").
    /// </summary>
    [MaxLength(100)]
    public string AbilityIdentifier { get; set; } = string.Empty;

    /// <summary>
    ///     Ability slot index (0 to 3 for primary spells, 4 for attribute boost / sub-abilities).
    /// </summary>
    public byte SlotIndex { get; set; }

    /// <summary>
    ///     Skill rank level (1 to 4).
    /// </summary>
    public byte Level { get; set; } = 1;

    /// <summary>
    ///     The timestamp in seconds from game start when the skill was leveled or cast.
    /// </summary>
    public int GameTimeSeconds { get; set; }

    [NotMapped]
    public string HeroName
    {
        get => HeroIdentifier;
        set => HeroIdentifier = value;
    }

    [NotMapped]
    public string AbilityName
    {
        get => AbilityIdentifier;
        set => AbilityIdentifier = value;
    }

    [NotMapped]
    public int GameTime
    {
        get => GameTimeSeconds;
        set => GameTimeSeconds = value;
    }
}
