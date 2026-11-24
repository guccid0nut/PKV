// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace MERRICK.DatabaseContext.Entities.Statistics;

/// <summary>
///     Normalized 1NF relational entity representing an individual item transaction
///     (buy, sell, or use) during a match. Mapped to <c>stat.MatchItemEvents</c>.
/// </summary>
[Table("MatchItemEvents", Schema = "stat")]
[Index(nameof(ItemIdentifier))]
[Index(nameof(MatchID), nameof(AccountID), nameof(GameTimeSeconds))]
[Index(nameof(EventType))]
public class MatchItemEvent
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
    ///     The timestamp in seconds from game start when the transaction occurred.
    /// </summary>
    public int GameTimeSeconds { get; set; }

    /// <summary>
    ///     Event type: 1 = Purchased, 2 = Sold, 3 = Used / Consumed.
    /// </summary>
    public byte EventType { get; set; }

    /// <summary>
    ///     Normalized item identifier (e.g. "Item_PortalKey", "Item_PostHaste").
    /// </summary>
    [MaxLength(100)]
    public string ItemIdentifier { get; set; } = string.Empty;

    [NotMapped]
    public string Action
    {
        get => EventType switch
        {
            1 => "Purchased",
            2 => "Sold",
            3 => "Used",
            _ => EventType.ToString()
        };
        set
        {
            EventType = value switch
            {
                "Purchased" => 1,
                "Sold" => 2,
                "Used" => 3,
                _ => 0
            };
        }
    }
}
