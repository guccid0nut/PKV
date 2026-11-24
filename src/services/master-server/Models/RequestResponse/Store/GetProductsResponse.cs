// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2022-2026 [K]ONGOR and Contributors
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV) -- Upstream lineage: https://github.com/Project-KONGOR-Open-Source

using System.Text.Json;
using KONGOR.MasterServer.Serialization;
using KONGOR.MasterServer.Services.Store;

namespace KONGOR.MasterServer.Models.RequestResponse.Store;

/// <summary>
///     Response model for the "get_products" endpoint.
///     Returns all enabled store products grouped by their category name, keyed by product ID.
///     The client uses this data to populate the in-game store and to determine purchasable items.
/// </summary>
public partial class GetProductsResponse
{
    /// <summary>
    ///     Creates a <see cref="GetProductsResponse" /> from all enabled store items in the store configuration.
    /// </summary>
    public GetProductsResponse(StoreItemsConfiguration storeItemsConfiguration)
    {
        Dictionary<string, Dictionary<int, GetProductsResponseEntry>> products = [];

        AddProducts(products, storeItemsConfiguration, "Alt Avatar", StoreItemType.AlternativeAvatar);
        AddProducts(products, storeItemsConfiguration, "Account Icon", StoreItemType.AccountIcon);
        AddProducts(products, storeItemsConfiguration, "Chat Color", StoreItemType.ChatNameColour);
        AddProducts(products, storeItemsConfiguration, "Chat Symbol", StoreItemType.ChatSymbol);
        AddProducts(products, storeItemsConfiguration, "Taunt", StoreItemType.Taunt);
        AddProducts(products, storeItemsConfiguration, "Misc", StoreItemType.Miscellaneous);
        AddProducts(products, storeItemsConfiguration, "Alt Announcement", StoreItemType.AnnouncerVoice);
        AddProducts(products, storeItemsConfiguration, "Couriers", StoreItemType.Courier);
        AddProducts(products, storeItemsConfiguration, "Hero", StoreItemType.Hero);
        AddProducts(products, storeItemsConfiguration, "Ward", StoreItemType.Ward);
        AddProducts(products, storeItemsConfiguration, "EAP", StoreItemType.EarlyAccessProduct);
        AddProducts(products, storeItemsConfiguration, "Mastery", StoreItemType.Mastery);
        AddProducts(products, storeItemsConfiguration, "Creep", StoreItemType.Creep);
        AddProducts(products, storeItemsConfiguration, "Taunt Badge", StoreItemType.TauntBadge);
        AddProducts(products, storeItemsConfiguration, "Teleport Effect", StoreItemType.TeleportEffect);
        AddProducts(products, storeItemsConfiguration, "Selection Circle", StoreItemType.SelectionCircle);

        Products = products;

        string serialised = JsonSerializer.Serialize(products, MasterServerJsonContext.Default.DictionaryStringDictionaryInt32GetProductsResponseEntry);
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(serialised));

        // Compute A Stable CRC32 Of The Serialised Products For Client-Side Cache Validation
        CRC = BitConverter.ToInt32(hash, 0);
    }

    /// <summary>
    ///     Store products grouped by category name (e.g. "Alt Avatar", "Taunt", "Hero").
    ///     Each category maps product IDs to their details.
    /// </summary>
    [PHPProperty("products")]
    public Dictionary<string, Dictionary<int, GetProductsResponseEntry>> Products { get; init; }

    /// <summary>
    ///     CRC32 checksum of the products data, used by the client for cache validation.
    /// </summary>
    [PHPProperty("crc")]
    public int CRC { get; init; }

    private static void AddProducts(Dictionary<string, Dictionary<int, GetProductsResponseEntry>> products,
        StoreItemsConfiguration storeItemsConfiguration, string categoryName, StoreItemType type)
    {
        Dictionary<int, GetProductsResponseEntry> entries = [];

        foreach (StoreItem item in storeItemsConfiguration.GetEnabledItemsByType(type))
            entries[item.ID] = new GetProductsResponseEntry(item);

        if (type == StoreItemType.Hero)
        {
            bool isSandbox = UpgradesResolutionService.UnlockAllItems;
            entries.Clear();
            List<string> allHeroIds = ASPIRE.Common.Constants.Heroes.AllHeroIdentifiers();
            for (int i = 0; i < allHeroIds.Count; i++)
            {
                string heroIdentifier = allHeroIds[i];
                int heroId = i + 1;
                string displayName = ASPIRE.Common.Constants.Heroes.ResolveHeroDisplayName(heroIdentifier) ?? heroIdentifier;

                entries[heroId] = new GetProductsResponseEntry
                {
                    ProductCode = $"{heroIdentifier}.Hero",
                    ProductName = displayName,
                    GoldCost = isSandbox ? 0 : 250,
                    SilverCost = isSandbox ? 0 : 500,
                    Purchasable = true,
                    IsPremium = false,
                    IsDynamic = 0,
                    LocalPath = null
                };
            }
        }

        products[categoryName] = entries;
    }
}

/// <summary>
///     A single product entry within the "get_products" response.
/// </summary>
public partial class GetProductsResponseEntry
{
    public GetProductsResponseEntry()
    {
    }

    public GetProductsResponseEntry(StoreItem storeItem)
    {
        bool isSandbox = UpgradesResolutionService.UnlockAllItems;
        ProductCode = storeItem.Code;
        ProductName = storeItem.Name;
        GoldCost = isSandbox ? 0 : storeItem.GoldCost;
        Purchasable = storeItem.Purchasable;
        IsPremium = storeItem.IsPremium;
        SilverCost = isSandbox ? 0 : storeItem.SilverCost;
        IsDynamic = storeItem.IsDynamic ? 1 : 0;

        if (storeItem.IsDynamic) LocalPath = storeItem.Resource;
    }

    /// <summary>
    ///     The unprefixed product code (e.g. "Hero_Pyromancer.Female").
    /// </summary>
    [PHPProperty("name")]
    public string ProductCode { get; init; } = string.Empty;

    /// <summary>
    ///     The human-readable product name (e.g. "Female Pyromancer").
    /// </summary>
    [PHPProperty("cname")]
    public string ProductName { get; init; } = string.Empty;

    /// <summary>
    ///     The gold coin cost.
    /// </summary>
    [PHPProperty("cost")]
    public int GoldCost { get; init; }

    /// <summary>
    ///     Whether the product can be purchased.
    /// </summary>
    [PHPProperty("purchasable")]
    public bool Purchasable { get; init; }

    /// <summary>
    ///     Whether the product is premium (silver-coin-only).
    /// </summary>
    [PHPProperty("premium")]
    public bool IsPremium { get; init; }

    /// <summary>
    ///     The silver coin cost.
    /// </summary>
    [PHPProperty("premium_mmp_cost")]
    public int SilverCost { get; init; }

    /// <summary>
    ///     Whether the product has dynamic (downloadable) content.
    /// </summary>
    [PHPProperty("dynamic")]
    public int IsDynamic { get; init; }

    /// <summary>
    ///     The local resource path for dynamic content. Only set when <see cref="IsDynamic" /> is 1.
    /// </summary>
    [PHPProperty("local_path")]
    public string? LocalPath { get; init; }
}
