// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

namespace MERRICK.DatabaseContext.Entities.Statistics;

/// <summary>
///     The accumulated mastery experience for a single hero associated with a player's mastery profile.
///     Promoted to a first-class 1NF relational entity mapped to the <c>stat.HeroMasteryExperiences</c> table.
/// </summary>
[Table("HeroMasteryExperiences", Schema = "stat")]
[Index(nameof(MasteryID))]
[Index(nameof(HeroIdentifier))]
[Index(nameof(MasteryID), nameof(HeroIdentifier), IsUnique = true)]
public class HeroMasteryExperience
{
    [Key]
    public int ID { get; set; }

    [NotMapped]
    public int Id
    {
        get => ID;
        set => ID = value;
    }

    public int MasteryID { get; set; }

    [ForeignKey(nameof(MasteryID))]
    [InverseProperty(nameof(Statistics.Mastery.HeroExperiences))]
    public Mastery Mastery { get; set; } = null!;

    /// <summary>
    ///     The hero identifier, in the format "Hero_{Name}" (for example "Hero_Accursed").
    /// </summary>
    [MaxLength(100)]
    public required string HeroIdentifier { get; set; }

    /// <summary>
    ///     The accumulated mastery experience for this hero.
    /// </summary>
    public int Experience { get; set; }

    /// <summary>
    ///     The calculated mastery level derived from <see cref="Experience"/>.
    /// </summary>
    [NotMapped]
    public int Level => Statistics.Mastery.GetLevelFromExperience(Experience);

    public override string ToString() => $"{HeroIdentifier}: {Experience} XP (Lv. {Level})";

    public override bool Equals(object? obj)
    {
        if (obj is HeroMasteryExperience other)
            return MasteryID == other.MasteryID &&
                   string.Equals(HeroIdentifier, other.HeroIdentifier, StringComparison.OrdinalIgnoreCase);
        return false;
    }

    public override int GetHashCode() =>
        HashCode.Combine(MasteryID, StringComparer.OrdinalIgnoreCase.GetHashCode(HeroIdentifier));
}
