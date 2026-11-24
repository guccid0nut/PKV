// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccid0nut/PKV)

using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace MERRICK.DatabaseContext.Entities.Quests;

[Table("AccountWarEffortProgress", Schema = "data")]
public class AccountWarEffortProgress
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public long Id { get; set; }

    public int AccountId { get; set; }

    public int CycleId { get; set; }

    public int TotalBagPoints { get; set; }

    public int QuestsCompletedCount { get; set; }

    public int QuestsResetCount { get; set; }

    public int RerollsAvailable { get; set; } = 1;

    [MaxLength(16)]
    public string LastDailyRefreshUtcDate { get; set; } = string.Empty;

    public DateTime LastUpdatedUtc { get; set; } = TimeProvider.System.GetUtcNow().UtcDateTime;
}
