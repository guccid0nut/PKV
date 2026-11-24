// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccid0nut/PKV)

using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace MERRICK.DatabaseContext.Entities.Quests;

[Table("AccountWarEffortBagClaims", Schema = "data")]
public class AccountWarEffortBagClaim
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public long Id { get; set; }

    public int AccountId { get; set; }

    public int CycleId { get; set; }

    public int BagIndex { get; set; }

    public int BagId { get; set; }

    public DateTime ClaimedAtUtc { get; set; } = TimeProvider.System.GetUtcNow().UtcDateTime;

    [MaxLength(32)]
    public string RewardType { get; set; } = "MMPOINTS";

    public int RewardAmount { get; set; }

    public int? GrantedProductId { get; set; }

    public bool WasAltFallback { get; set; }

    public bool AutoClaimedAtRollover { get; set; }

    [MaxLength(128)]
    public string IdempotencyKey { get; set; } = string.Empty;
}
