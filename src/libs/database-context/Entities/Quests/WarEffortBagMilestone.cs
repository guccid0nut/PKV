// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccid0nut/PKV)

using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace MERRICK.DatabaseContext.Entities.Quests;

[Table("WarEffortBagMilestones", Schema = "data")]
public class WarEffortBagMilestone
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public int Id { get; set; }

    public int CycleId { get; set; }

    /// <summary>
    ///     1-based position along the 14-bag War Effort scroll map (1..14).
    /// </summary>
    public int BagIndex { get; set; }

    /// <summary>
    ///     Globally unique bag identifier sent over the K2 PHP protocol (cycleNumber * 100 + bagIndex).
    /// </summary>
    public int BagId { get; set; }

    public int RequiredPoints { get; set; }

    /// <summary>
    ///     MMPOINTS (Silver), POINTS (Gold), PLINKO_TICKETS, or PRODUCT_ID.
    /// </summary>
    [MaxLength(32)]
    public string RewardType { get; set; } = "MMPOINTS";

    public int RewardAmount { get; set; }

    public int? ProductId { get; set; }

    public int AltSilverFallback { get; set; } = 250;

    [MaxLength(128)]
    public string Title { get; set; } = string.Empty;
}
