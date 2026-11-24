// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccid0nut/PKV)

using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace MERRICK.DatabaseContext.Entities.Quests;

[Table("AccountQuestSlots", Schema = "data")]
public class AccountQuestSlot
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public long Id { get; set; }

    public int AccountId { get; set; }

    public int CycleId { get; set; }

    /// <summary>
    ///     0 or 1 (the two active daily quest slots).
    /// </summary>
    public int SlotIndex { get; set; }

    public int QuestDefinitionId { get; set; }

    public int Progress { get; set; }

    public int Goal { get; set; } = 1;

    public DateTime AssignedAtUtc { get; set; } = TimeProvider.System.GetUtcNow().UtcDateTime;

    public int? LastMatchId { get; set; }

    public DateTime? CompletedAtUtc { get; set; }
}
