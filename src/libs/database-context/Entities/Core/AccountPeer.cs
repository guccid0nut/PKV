// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2022-2026 [K]ONGOR and Contributors
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV) -- Upstream lineage: https://github.com/Project-KONGOR-Open-Source

using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;
using MERRICK.DatabaseContext.Enumerations;

namespace MERRICK.DatabaseContext.Entities.Core;

[Table("AccountPeers", Schema = "core")]
[Index(nameof(AccountID), nameof(PeerAccountID), nameof(Type), IsUnique = true, Name = "IX_AccountPeers_AccountID_PeerAccountID_Type")]
[Index(nameof(PeerAccountID), Name = "IX_AccountPeers_PeerAccountID")]
[Index(nameof(TimestampExpires), Name = "IX_AccountPeers_TimestampExpires")]
public class AccountPeer
{
    [Key]
    public int ID { get; set; }

    public int AccountID { get; set; }

    [ForeignKey(nameof(AccountID))]
    public Account? Account { get; set; }

    public int PeerAccountID { get; set; }

    [ForeignKey(nameof(PeerAccountID))]
    public Account? PeerAccount { get; set; }

    public PeerType Type { get; set; } // Banned = 0, Ignored = 1

    [MaxLength(100)]
    public string? Reason { get; set; }

    public DateTimeOffset TimestampCreated { get; set; } = TimeProvider.System.GetUtcNow();

    public DateTimeOffset? TimestampExpires { get; set; }
}
