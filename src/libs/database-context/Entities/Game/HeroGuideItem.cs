// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

namespace MERRICK.DatabaseContext.Entities.Game;

/// <summary>
///     Identifies the category slot in a hero guide's recommended item build.
/// </summary>
public enum HeroGuideItemSlot
{
    Starting = 0,
    EarlyGame = 1,
    Core = 2,
    Luxury = 3,
    Situational = 4
}

/// <summary>
///     Backwards-compatible alias / duplicate for <see cref="HeroGuideItemSlot"/>.
/// </summary>
public enum GuideSlotType
{
    Starting = 0,
    EarlyGame = 1,
    Core = 2,
    Luxury = 3,
    Situational = 4
}

/// <summary>
///     Represents an individual item recommendation within a hero guide.
///     Normalized 1NF relational entity mapped to <c>misc.HeroGuideItems</c>.
/// </summary>
[Table("HeroGuideItems", Schema = "misc")]
[Index(nameof(GuideID))]
[Index(nameof(GuideID), nameof(SlotType), nameof(OrderIndex), IsUnique = true)]
public class HeroGuideItem
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
    [InverseProperty(nameof(HeroGuide.Items))]
    public HeroGuide Guide { get; set; } = null!;

    [NotMapped]
    public HeroGuide HeroGuide
    {
        get => Guide;
        set => Guide = value!;
    }

    public GuideSlotType SlotType { get; set; }

    [NotMapped]
    public HeroGuideItemSlot ItemSlot
    {
        get => (HeroGuideItemSlot)SlotType;
        set => SlotType = (GuideSlotType)value;
    }

    [NotMapped]
    public GuideSlotType GuideSlotType
    {
        get => SlotType;
        set => SlotType = value;
    }

    public int OrderIndex { get; set; }

    [MaxLength(100)]
    public string ItemCode { get; set; } = string.Empty;

    public override string ToString() => $"[{SlotType} #{OrderIndex}] {ItemCode} (Guide #{GuideID})";

    public override bool Equals(object? obj)
    {
        if (obj is HeroGuideItem other)
        {
            return GuideID == other.GuideID &&
                   SlotType == other.SlotType &&
                   OrderIndex == other.OrderIndex &&
                   string.Equals(ItemCode, other.ItemCode, StringComparison.OrdinalIgnoreCase);
        }
        return false;
    }

    public override int GetHashCode() =>
        HashCode.Combine(GuideID, SlotType, OrderIndex, StringComparer.OrdinalIgnoreCase.GetHashCode(ItemCode));
}
