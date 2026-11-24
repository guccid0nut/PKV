// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace MERRICK.DatabaseContext.Entities.Statistics;

/// <summary>
///     Normalized 1NF relational entity representing a discrete inventory slot
///     held by a player at the end of a match. Mapped to <c>stat.MatchPlayerInventories</c>.
/// </summary>
[Table("MatchPlayerInventories", Schema = "stat")]
[Index(nameof(ItemIdentifier))]
[Index(nameof(MatchID), nameof(AccountID))]
[Index(nameof(PlayerStatisticID), nameof(SlotIndex), IsUnique = true)]
public class MatchPlayerInventory
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
    ///     Slot index: 0 through 5 for main inventory, 6 through 11 for stash.
    /// </summary>
    public int SlotIndex { get; set; }

    /// <summary>
    ///     Normalized item identifier (e.g., "Item_PortalKey", "Item_PostHaste").
    /// </summary>
    [MaxLength(100)]
    public string ItemIdentifier { get; set; } = string.Empty;

    /// <summary>
    ///     Remaining charges on stackable or charged items (wards, dust, potions).
    /// </summary>
    public int Charges { get; set; }
}
