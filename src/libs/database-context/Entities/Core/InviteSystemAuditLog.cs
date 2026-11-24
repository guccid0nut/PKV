// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

namespace MERRICK.DatabaseContext.Entities.Core;

[Table("InviteSystemAuditLogs", Schema = "core")]
[Index(nameof(StaffAccountID))]
[Index(nameof(Timestamp))]
public class InviteSystemAuditLog
{
    [Key] public int ID { get; set; }

    public int StaffAccountID { get; set; }

    [MaxLength(64)] public required string StaffAccountName { get; set; }

    public bool InviteOnlyEnabled { get; set; }

    [MaxLength(512)] public string? Notes { get; set; }

    public DateTimeOffset Timestamp { get; set; } = TimeProvider.System.GetUtcNow();

    [MaxLength(45)] public string? IpAddress { get; set; }
}
