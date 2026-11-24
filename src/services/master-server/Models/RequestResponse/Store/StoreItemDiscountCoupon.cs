// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2022-2026 [K]ONGOR and Contributors
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV) -- Upstream lineage: https://github.com/Project-KONGOR-Open-Source

using System.Globalization;

namespace KONGOR.MasterServer.Models.RequestResponse.Store;

public partial class StoreItemDiscountCoupon
{
    [PHPProperty("product_id")] public required int Id { get; set; }

    [PhpIgnore] public required string Name { get; set; }

    [PhpIgnore] public string Code => $"cp.{Name}";

    [PhpIgnore] public required string Hero { get; set; }

    [PHPProperty("coupon_id")]
    // The integer value of the "discount" property part of the form data which gets sent when making a purchase in the in-game store.
    // The "discount" property will be set to this value after choosing to use a discount coupon.
    public int DiscountId => Id;

    [PHPProperty("coupon_products")] public required string ApplicableProducts { get; set; }

    [PhpIgnore] public required IEnumerable<string> ApplicableProductsList { get; set; }

    [PHPProperty("discount")] public double DiscountGold => 0.75;

    [PHPProperty("mmp_discount")] public double DiscountSilver => 0.75;

    [PHPProperty("end_time")]
    public string DiscountExpirationDate { get; set; } =
        TimeProvider.System.GetUtcNow().AddYears(1000).ToString("dd MMMM yyyy", CultureInfo.InvariantCulture);
}
