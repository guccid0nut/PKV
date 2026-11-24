// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

namespace MERRICK.DatabaseContext.Entities.Core;

[Table("AccountSessions", Schema = "core")]
[Index(nameof(AccountID))]
[Index(nameof(UserID))]
[Index(nameof(IPAddress))]
[Index(nameof(HWID))]
[Index(nameof(TimestampLogin))]
public class AccountSession
{
    [Key] public int ID { get; set; }

    [NotMapped]
    public int Id
    {
        get => ID;
        set => ID = value;
    }

    public int AccountID { get; set; }

    [ForeignKey(nameof(AccountID))] public Account? Account { get; set; }

    public int? UserID { get; set; }

    [ForeignKey(nameof(UserID))] public User? User { get; set; }

    [MaxLength(45)] public required string IPAddress { get; set; }

    [MaxLength(64)] public string? MACAddress { get; set; }

    [MaxLength(128)] public string? HWID { get; set; }

    [NotMapped]
    public string? SystemInformationHash
    {
        get => HWID;
        set => HWID = value;
    }

    public string? SystemInformation { get; set; }

    [NotMapped]
    public string? SystemInformationRaw
    {
        get => SystemInformation;
        set => SystemInformation = value;
    }

    [MaxLength(128)] public string? OperatingSystem { get; set; }

    [NotMapped]
    public string? OSType
    {
        get => OperatingSystem;
        set => OperatingSystem = value;
    }

    [MaxLength(256)] public string? Processor { get; set; }

    [MaxLength(256)] public string? VideoCard { get; set; }

    [MaxLength(64)] public string? RAM { get; set; }

    [MaxLength(128)] public string? ClientVersion { get; set; }

    [MaxLength(256)] public string? UserAgent { get; set; }

    [MaxLength(128)] public string? SessionCookie { get; set; }

    public DateTimeOffset TimestampLogin { get; set; } = TimeProvider.System.GetUtcNow();

    public DateTimeOffset? TimestampLastSeen { get; set; } = TimeProvider.System.GetUtcNow();

    public bool IsActive { get; set; } = true;
}
