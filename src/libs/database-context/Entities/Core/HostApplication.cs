// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

namespace MERRICK.DatabaseContext.Entities.Core;

[Index(nameof(HostUsername), IsUnique = true)]
[Index(nameof(Status))]
public class HostApplication
{
    [Key] public int ID { get; set; }

    public int UserID { get; set; }

    public User User { get; set; } = null!;

    public int MainAccountID { get; set; }

    public Account MainAccount { get; set; } = null!;

    public int? HostAccountID { get; set; }

    public Account? HostAccount { get; set; }

    [MaxLength(15)] public string HostUsername { get; set; } = string.Empty;

    [MaxLength(512)] public string? EncryptedHostPassword { get; set; }
    [MaxLength(512)] public string? EncryptedAuthToken { get; set; }

    public HostApplicationStatus Status { get; set; } = HostApplicationStatus.Pending;

    [MaxLength(16)] public string Location { get; set; } = "USE";

    [NotMapped]
    public string? ServerLocation
    {
        get => Location;
        set => Location = value ?? "USE";
    }

    public int Instances { get; set; } = 2;

    [MaxLength(64)] public string ServerNamePrefix { get; set; } = "Community Server";

    [MaxLength(500)] public string? ApplicationNotes { get; set; }

    [MaxLength(500)] public string? StaffNotes { get; set; }

    [MaxLength(64)] public string? ReviewedByStaffName { get; set; }

    public DateTimeOffset TimestampApplied { get; set; } = TimeProvider.System.GetUtcNow();

    public DateTimeOffset? TimestampReviewed { get; set; }

    public DateTimeOffset? TimestampLastHostActivity { get; set; }
}
