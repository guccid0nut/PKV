// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccid0nut/PKV)

using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace MERRICK.DatabaseContext.Entities.Quests;

[Table("WarEffortCycles", Schema = "data")]
public class WarEffortCycle
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public int Id { get; set; }

    public int CycleNumber { get; set; }

    [MaxLength(128)]
    public string Name { get; set; } = string.Empty;

    public DateTime StartsAtUtc { get; set; }

    public DateTime EndsAtUtc { get; set; }

    public bool IsActive { get; set; }

    public DateTime CreatedAtUtc { get; set; } = TimeProvider.System.GetUtcNow().UtcDateTime;
}
