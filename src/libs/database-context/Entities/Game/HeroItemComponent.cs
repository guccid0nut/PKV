// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace MERRICK.DatabaseContext.Entities.Game;

/// <summary>
///     Normalized 1NF relational entity representing a component item required to craft
///     a parent recipe item. Mapped to <c>misc.HeroItemComponents</c>.
/// </summary>
[Table("HeroItemComponents", Schema = "misc")]
[Index(nameof(ParentItemID))]
[Index(nameof(ComponentItemCode))]
[Index(nameof(ParentItemID), nameof(ComponentItemCode), IsUnique = true)]
public class HeroItemComponent
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

    public int ParentItemID { get; set; }

    [ForeignKey(nameof(ParentItemID))]
    [System.Text.Json.Serialization.JsonIgnore]
    public HeroItem ParentItem { get; set; } = null!;

    /// <summary>
    ///     Item code of the required component (e.g. "minortotem", "recipe", "marchers").
    /// </summary>
    [MaxLength(100)]
    public string ComponentItemCode { get; set; } = string.Empty;

    /// <summary>
    ///     Required quantity of this component (defaults to 1).
    /// </summary>
    public int Quantity { get; set; } = 1;
}
