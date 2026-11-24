// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

namespace KONGOR.MasterServer.Models.RequestResponse.Plinko;

public partial class PlinkoIndexResponseDTO
{
    [PHPProperty("status_code")] public int StatusCode { get; init; } = 1;

    [PHPProperty("tiers")] public int[]? Tiers { get; init; }

    [PHPProperty("ticket_cost")] public int TicketCost { get; init; }

    [PHPProperty("gold_cost")] public int GoldCost { get; init; }

    [PHPProperty("user_gold")] public int UserGold { get; init; }

    [PHPProperty("silver")] public int Silver { get; init; }

    [PHPProperty("user_tickets")] public int UserTickets { get; init; }

    [PHPProperty("amount_of_products")] public string AmountOfProducts { get; init; } = string.Empty;

    [PHPProperty("last_update_time")] public string LastUpdateTime { get; init; } = string.Empty;
}

public partial class PlinkoDropResponseDTO
{
    [PHPProperty("status_code")] public int StatusCode { get; init; } = 1;

    [PHPProperty("random_tier")] public int RandomTier { get; init; }

    [PHPProperty("product_id")] public int ProductID { get; init; }

    [PHPProperty("product_name")] public string ProductName { get; init; } = string.Empty;

    [PHPProperty("product_path")] public string ProductPath { get; init; } = string.Empty;

    [PHPProperty("product_type")] public string ProductType { get; init; } = string.Empty;

    [PHPProperty("ticket_amount")] public int TicketAmount { get; init; }

    [PHPProperty("user_tickets")] public int UserTickets { get; init; }

    [PHPProperty("user_gold")] public int UserGold { get; init; }

    [PHPProperty("products_exhausted")] public int ProductsExhausted { get; init; }
}

public partial class PlinkoViewChestResponseDTO
{
    [PHPProperty("status_code")] public int StatusCode { get; init; } = 1;

    [PHPProperty("tier_id")] public int TierID { get; init; }

    [PHPProperty("target_index")] public int TargetIndex { get; init; }

    [PHPProperty("first_item_index")] public int FirstItemIndex { get; init; }

    [PHPProperty("items_amount")] public int ItemsAmount { get; init; }

    [PHPProperty("product_names")] public string ProductNames { get; init; } = string.Empty;

    [PHPProperty("product_types")] public string ProductTypes { get; init; } = string.Empty;

    [PHPProperty("product_paths")] public string ProductPaths { get; init; } = string.Empty;

    [PHPProperty("product_ids")] public string ProductIDs { get; init; } = string.Empty;
}

public partial class TicketExchangeItemDTO
{
    [PHPProperty("id")] public int ID { get; init; }

    [PHPProperty("cost")] public int Cost { get; init; }

    [PHPProperty("product_id")] public int ProductID { get; init; }

    [PHPProperty("name")] public string Name { get; init; } = string.Empty;

    [PHPProperty("type")] public string Type { get; init; } = string.Empty;

    [PHPProperty("local_path")] public string LocalPath { get; init; } = string.Empty;
}

public partial class TicketExchangeListResponseDTO
{
    [PHPProperty("status_code")] public int StatusCode { get; init; } = 51;

    [PHPProperty("items")] public Dictionary<int, TicketExchangeItemDTO>? Items { get; init; }

    [PHPProperty("user_tickets")] public int UserTickets { get; init; }
}

public partial class TicketExchangePurchaseResponseDTO
{
    [PHPProperty("status_code")] public int StatusCode { get; init; }

    [PHPProperty("tickets_remaining")] public int TicketsRemaining { get; init; }

    [PHPProperty("grabBag")] public string GrabBag { get; init; } = "0";

    [PHPProperty("grabBagTheme")] public string GrabBagTheme { get; init; } = string.Empty;

    [PHPProperty("grabBagIDs")] public string GrabBagIDs { get; init; } = string.Empty;

    [PHPProperty("grabBagTypes")] public string GrabBagTypes { get; init; } = string.Empty;

    [PHPProperty("grabBagLocalPaths")] public string GrabBagLocalPaths { get; init; } = string.Empty;

    [PHPProperty("grabBagProductNames")] public string GrabBagProductNames { get; init; } = string.Empty;
}
