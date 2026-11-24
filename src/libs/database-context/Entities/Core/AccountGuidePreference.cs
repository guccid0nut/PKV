// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2022-2026 [K]ONGOR and Contributors
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV) -- Upstream lineage: https://github.com/Project-KONGOR-Open-Source

using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using MERRICK.DatabaseContext.Entities.Game;
using Microsoft.EntityFrameworkCore;

namespace MERRICK.DatabaseContext.Entities.Core;

/// <summary>
///     Represents an account's guide preference (favorite or default guide) with foreign key cascade integrity.
///     Normalized relational entity mapped to <c>core.AccountGuidePreferences</c>.
/// </summary>
[Table("AccountGuidePreferences", Schema = "core")]
[Index(nameof(AccountID))]
[Index(nameof(GuideID))]
[Index(nameof(AccountID), nameof(GuideID), IsUnique = true)]
public class AccountGuidePreference
{
    [Key]
    public int ID { get; set; }

    public int AccountID { get; set; }

    [ForeignKey(nameof(AccountID))]
    [InverseProperty(nameof(Account.GuidePreferences))]
    public Account Account { get; set; } = null!;

    public int GuideID { get; set; }

    [ForeignKey(nameof(GuideID))]
    public HeroGuide Guide { get; set; } = null!;

    public bool IsFavorite { get; set; }

    public bool IsDefault { get; set; }

    public DateTimeOffset TimestampUpdated { get; set; } = TimeProvider.System.GetUtcNow();

    public override string ToString() => $"[Account #{AccountID} Guide #{GuideID}] Fav: {IsFavorite}, Def: {IsDefault}";
}
