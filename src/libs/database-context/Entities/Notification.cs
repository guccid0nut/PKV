// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

namespace MERRICK.DatabaseContext.Entities;

[Table("Notifications", Schema = "misc")]
[Index(nameof(AccountId), IsUnique = false)]
public class Notification
{
    [Key] public int NotificationId { get; set; }

    [NotMapped]
    public int ID
    {
        get => NotificationId;
        set => NotificationId = value;
    }

    [MaxLength(2048)] public string Content { get; set; } = string.Empty;

    public int AccountId { get; set; }

    [NotMapped]
    public int AccountID
    {
        get => AccountId;
        set => AccountId = value;
    }

    [ForeignKey(nameof(AccountId))] public Account? Account { get; set; }

    public DateTime TimestampCreated { get; set; } = TimeProvider.System.GetUtcNow().UtcDateTime;
}
