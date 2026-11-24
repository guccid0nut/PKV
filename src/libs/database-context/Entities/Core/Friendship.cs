// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using MERRICK.DatabaseContext.Enumerations;

namespace MERRICK.DatabaseContext.Entities.Core;

[Table("Friendships", Schema = "core")]
[Index(nameof(AccountID))]
[Index(nameof(FriendAccountID))]
[Index(nameof(AccountID), nameof(FriendAccountID), IsUnique = true)]
[Index(nameof(Status))]
public class Friendship
{
    [Key]
    [Column("Id")]
    public int ID { get; set; }

    [NotMapped]
    public int Id
    {
        get => ID;
        set => ID = value;
    }

    public int AccountID { get; set; }

    [ForeignKey(nameof(AccountID))] public Account? Account { get; set; }

    public int FriendAccountID { get; set; }

    [ForeignKey(nameof(FriendAccountID))] public Account? FriendAccount { get; set; }

    [MaxLength(32)] public string FriendGroup { get; set; } = "General";

    public FriendshipStatus Status { get; set; } = FriendshipStatus.Accepted;

    public DateTimeOffset TimestampCreated { get; set; } = TimeProvider.System.GetUtcNow();

    public DateTimeOffset? TimestampUpdated { get; set; }
}
