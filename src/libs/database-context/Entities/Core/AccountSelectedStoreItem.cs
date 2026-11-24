// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

namespace MERRICK.DatabaseContext.Entities.Core;

/// <summary>
///     Represents an active/equipped store item code selected by an account (e.g. custom icon, chat color, taunt).
/// </summary>
[Table("AccountSelectedStoreItems", Schema = "core")]
[Index(nameof(AccountID))]
[Index(nameof(AccountID), nameof(ItemCode), IsUnique = true)]
public class AccountSelectedStoreItem
{
    [Key]
    public int ID { get; set; }

    [NotMapped]
    public int Id
    {
        get => ID;
        set => ID = value;
    }

    public int AccountID { get; set; }

    [ForeignKey(nameof(AccountID))]
    [InverseProperty(nameof(Core.Account.SelectedStoreItems))]
    public Account Account { get; set; } = null!;

    [MaxLength(100)]
    public required string ItemCode { get; set; }

    public static implicit operator string(AccountSelectedStoreItem? item) => item?.ItemCode ?? string.Empty;
    public static implicit operator AccountSelectedStoreItem(string itemCode) => new() { ItemCode = itemCode };

    public override string ToString() => ItemCode;

    public override bool Equals(object? obj)
    {
        if (obj is AccountSelectedStoreItem other)
            return string.Equals(ItemCode, other.ItemCode, StringComparison.OrdinalIgnoreCase);
        if (obj is string str)
            return string.Equals(ItemCode, str, StringComparison.OrdinalIgnoreCase);
        return false;
    }

    public override int GetHashCode() => StringComparer.OrdinalIgnoreCase.GetHashCode(ItemCode);
}
