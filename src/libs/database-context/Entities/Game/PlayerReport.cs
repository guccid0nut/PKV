// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

namespace MERRICK.DatabaseContext.Entities.Game;

[Table("PlayerReports", Schema = "misc")]
[Index(nameof(ReporterAccountID))]
[Index(nameof(ReportedAccountID))]
[Index(nameof(ReportedAccountName))]
[Index(nameof(ReporterAccountName))]
[Index(nameof(MatchID))]
[Index(nameof(Status))]
[Index(nameof(TimestampCreated))]
public class PlayerReport
{
    [Key]
    [Column("ID")]
    public int ID { get; set; }

    public int? ReporterAccountID { get; set; }

    [ForeignKey(nameof(ReporterAccountID))]
    public Account? ReporterAccount { get; set; }

    [MaxLength(50)] public required string ReporterAccountName { get; set; }

    public int? ReportedAccountID { get; set; }

    [ForeignKey(nameof(ReportedAccountID))]
    public Account? ReportedAccount { get; set; }

    [MaxLength(50)] public required string ReportedAccountName { get; set; }

    public int? MatchID { get; set; }

    [ForeignKey(nameof(MatchID))] public MatchStatistics? Match { get; set; }

    [MaxLength(100)] public required string Reason { get; set; }

    [MaxLength(1000)] public string? Description { get; set; }

    [MaxLength(50)] public string Status { get; set; } = "PENDING"; // PENDING, IN_REVIEW, RESOLVED, DISMISSED

    public DateTimeOffset TimestampCreated { get; set; } = TimeProvider.System.GetUtcNow();

    public DateTimeOffset? TimestampReviewed { get; set; }

    [MaxLength(50)] public string? ReviewedByStaffName { get; set; }

    [MaxLength(50)] public string? PunishmentType { get; set; } // NONE, MUTE, SUSPEND, BAN

    public int? PunishmentDurationHours { get; set; }

    public DateTimeOffset? PunishmentExpiration { get; set; }

    [MaxLength(1000)] public string? StaffNotes { get; set; }
}
