// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

namespace MERRICK.DatabaseContext.Entities.Game;

/// <summary>
///     Represents an ability skill point allocation at a specific hero level sequence (1 to 25).
///     Normalized 1NF relational entity mapped to <c>misc.HeroGuideAbilities</c>.
/// </summary>
[Table("HeroGuideAbilities", Schema = "misc")]
[Index(nameof(GuideID))]
[Index(nameof(GuideID), nameof(LevelSequence), IsUnique = true)]
public class HeroGuideAbility
{
    [Key]
    public int ID { get; set; }

    [NotMapped]
    public int Id
    {
        get => ID;
        set => ID = value;
    }

    public int GuideID { get; set; }

    [NotMapped]
    public int HeroGuideID
    {
        get => GuideID;
        set => GuideID = value;
    }

    [ForeignKey(nameof(GuideID))]
    [InverseProperty(nameof(HeroGuide.Abilities))]
    public HeroGuide Guide { get; set; } = null!;

    [NotMapped]
    public HeroGuide HeroGuide
    {
        get => Guide;
        set => Guide = value!;
    }

    /// <summary>
    ///     Hero level sequence from 1 to 25.
    /// </summary>
    public int LevelSequence { get; set; }

    /// <summary>
    ///     Ability slot index: 1 = Q, 2 = W, 3 = E, 4 = R (Ultimate), 5 = Stats / Attribute Boost.
    /// </summary>
    public int AbilitySlot { get; set; }

    /// <summary>
    ///     The normalized ability code name (e.g. "Ability_Accursed1", "Ability_AttributeBoost").
    /// </summary>
    [MaxLength(100)]
    public string AbilityCode { get; set; } = string.Empty;

    public override string ToString() => $"[Level {LevelSequence}] Slot {AbilitySlot} ({AbilityCode}) (Guide #{GuideID})";

    public override bool Equals(object? obj)
    {
        if (obj is HeroGuideAbility other)
        {
            return GuideID == other.GuideID &&
                   LevelSequence == other.LevelSequence &&
                   AbilitySlot == other.AbilitySlot &&
                   string.Equals(AbilityCode, other.AbilityCode, StringComparison.OrdinalIgnoreCase);
        }
        return false;
    }

    public override int GetHashCode() =>
        HashCode.Combine(GuideID, LevelSequence, AbilitySlot, StringComparer.OrdinalIgnoreCase.GetHashCode(AbilityCode));
}
