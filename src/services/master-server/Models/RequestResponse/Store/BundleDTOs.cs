// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Text.Json.Serialization;

namespace KONGOR.MasterServer.Models.RequestResponse.Store;

public class BundleContentResponse
{
    [JsonPropertyName("info")] public required BundleInfoDTO Info { get; set; }

    [JsonPropertyName("list")] public List<BundleItemDTO> List { get; set; } = [];
}

public class BundleInfoDTO
{
    [JsonPropertyName("bundle_id")] public int BundleID { get; set; }

    [JsonPropertyName("grab_bag")] public bool GrabBag { get; set; }

    [JsonPropertyName("prize_quantity")] public int PrizeQuantity { get; set; }

    [JsonPropertyName("max_purchasable")] public int MaxPurchasable { get; set; }

    [JsonPropertyName("gold_coins")] public int GoldCoins { get; set; }

    [JsonPropertyName("silver_coins")] public int SilverCoins { get; set; }

    [JsonPropertyName("is_owned")] public bool IsOwned { get; set; }

    [JsonPropertyName("ui_theme")] public string UiTheme { get; set; } = "default";

    [JsonPropertyName("model")] public string Model { get; set; } = string.Empty;
}

public class BundleItemDTO
{
    [JsonPropertyName("product_id")] public int ProductID { get; set; }

    [JsonPropertyName("item_code")] public string ItemCode { get; set; } = string.Empty;

    [JsonPropertyName("gold_coins")] public int GoldCoins { get; set; }

    [JsonPropertyName("silver_coins")] public int SilverCoins { get; set; }

    [JsonPropertyName("local_content")] public string LocalContent { get; set; } = string.Empty;

    [JsonPropertyName("is_owned")] public bool IsOwned { get; set; }
}
