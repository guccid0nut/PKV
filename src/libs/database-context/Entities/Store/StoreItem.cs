// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

namespace MERRICK.DatabaseContext.Entities.Store;

[Table("StoreItems", Schema = "data")]
[Index(nameof(Code), IsUnique = true)]
[Index(nameof(UpgradeId))]
public class StoreItem
{
    [Key] public int Id { get; set; }

    [MaxLength(255)] public required string Name { get; set; }

    [MaxLength(255)] [Required] public required string Code { get; set; }

    public int UpgradeId { get; set; }

    public int UpgradeType { get; set; }

    public required string LocalContent { get; set; }

    public bool Purchasable { get; set; }

    public int Price { get; set; }

    public int PriceInMMP { get; set; }

    public bool Premium { get; set; }

    public bool Enabled { get; set; }

    public bool IsBundle { get; set; }

    public string? ProductsRequired { get; set; }

    [InverseProperty(nameof(UserInventory.StoreItem))]
    public List<UserInventory> UserInventories { get; set; } = [];
}
