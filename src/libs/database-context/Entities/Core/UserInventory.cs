// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

namespace MERRICK.DatabaseContext.Entities.Core;

[Table("UserInventory", Schema = "core")]
[Index(nameof(UserID))]
[Index(nameof(AccountID))]
[Index(nameof(ItemCode))]
[Index(nameof(UserID), nameof(ItemCode), IsUnique = true)]
[Index(nameof(StoreItemId))]
[Index(nameof(UserID), nameof(StoreItemId))]
public class UserInventory
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

    public int UserID { get; set; }

    [ForeignKey(nameof(UserID))] public User? User { get; set; }

    public int? AccountID { get; set; }

    [ForeignKey(nameof(AccountID))] public Account? Account { get; set; }

    [MaxLength(255)] public required string ItemCode { get; set; }

    [NotMapped]
    public string ProductCode
    {
        get => ItemCode;
        set => ItemCode = value;
    }

    [NotMapped]
    public int UserId
    {
        get => UserID;
        set => UserID = value;
    }

    [NotMapped]
    public int? AccountId
    {
        get => AccountID;
        set => AccountID = value;
    }

    public int? StoreItemId { get; set; }

    [NotMapped]
    public int? StoreItemID
    {
        get => StoreItemId;
        set => StoreItemId = value;
    }

    [ForeignKey(nameof(StoreItemId))] public StoreItem? StoreItem { get; set; }

    public int Quantity { get; set; } = 1;

    public bool IsPermanent { get; set; } = true;

    public DateTimeOffset TimestampAcquired { get; set; } = TimeProvider.System.GetUtcNow();

    [NotMapped]
    public DateTimeOffset GrantedAtUtc
    {
        get => TimestampAcquired;
        set => TimestampAcquired = value;
    }

    public DateTimeOffset? TimestampExpires { get; set; }

    [MaxLength(64)] public string Source { get; set; } = "Store";

    [NotMapped]
    public string GrantSource
    {
        get => Source;
        set => Source = value;
    }
}
