// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Collections.Frozen;
using NEXUS.Contracts.Services;

namespace KONGOR.MasterServer.Services.Store;

public class StoreCatalogService : IStoreCatalogService
{
    public static readonly IStoreCatalogService Instance = new StoreCatalogService();

    private readonly FrozenDictionary<string, CatalogItemDTO> _byCode;
    private readonly FrozenDictionary<int, CatalogItemDTO> _byId;
    private readonly List<DailySpecialDTO> _dailySpecials;
    private readonly List<MasteryRewardTierDTO> _masteryTiers;

    public StoreCatalogService()
    {
        Dictionary<string, CatalogItemDTO> codeMap = new(StringComparer.OrdinalIgnoreCase);
        Dictionary<int, CatalogItemDTO> idMap = [];

        // 1. Seed static catalog products
        foreach (Product product in StaticCatalog.Products)
        {
            CatalogItemDTO dto = new(
                0,
                product.Name,
                product.Description,
                product.ProductCode,
                product.Type,
                product.CostGold,
                product.CostSilver,
                product.IconPath
            );

            codeMap[product.ProductCode] = dto;
        }

        // 2. Seed StoreItemsConfiguration
        foreach (StoreItem item in JSONConfiguration.StoreItemsConfiguration.StoreItems)
        {
            CatalogItemDTO dto = new(
                item.ID,
                item.Name,
                item.Name,
                item.Code,
                item.Type.ToString(),
                item.GoldCost,
                item.SilverCost,
                string.Empty
            );

            codeMap[item.Code] = dto;
            if (!string.IsNullOrEmpty(item.PrefixedCode)) codeMap[item.PrefixedCode] = dto;

            if (item.ID > 0) idMap[item.ID] = dto;
        }

        _byCode = codeMap.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);
        _byId = idMap.ToFrozenDictionary();

        _dailySpecials =
        [
            .. StaticCatalog.DailySpecials.Select(ds => new DailySpecialDTO(
                ds.PanelIndex,
                ds.ProductCode,
                ds.DiscountOff,
                ds.DiscountSilver,
                ds.TagTitle,
                ds.TagDateLimit
            ))
        ];

        MasteryRewardsConfiguration config = JSONConfiguration.MasteryRewardsConfiguration;
        _masteryTiers =
        [
            .. config.MasteryRewards.Select(r => new MasteryRewardTierDTO(
                r.RequiredLevel,
                r.ProductIdentifier,
                r.ProductCode ?? string.Empty,
                r.ProductName ?? string.Empty,
                r.ProductLocalResource ?? string.Empty,
                r.ProductQuantity,
                r.GoldCoins,
                r.SilverCoins,
                r.PlinkoTickets
            ))
        ];
    }

    public bool IsValidProductCode(string productCode)
    {
        return !string.IsNullOrEmpty(productCode) && _byCode.ContainsKey(productCode);
    }

    public int GetProductId(string productCode)
    {
        if (string.IsNullOrEmpty(productCode)) return 0;

        return _byCode.TryGetValue(productCode, out CatalogItemDTO? item) ? item.ID : 0;
    }

    public CatalogItemDTO? GetProductByCode(string productCode)
    {
        if (string.IsNullOrEmpty(productCode)) return null;

        _byCode.TryGetValue(productCode, out CatalogItemDTO? item);
        return item;
    }

    public CatalogItemDTO? GetProductById(int productId)
    {
        _byId.TryGetValue(productId, out CatalogItemDTO? item);
        return item;
    }

    public IReadOnlyList<DailySpecialDTO> GetDailySpecials()
    {
        return _dailySpecials;
    }

    public IReadOnlyList<MasteryRewardTierDTO> GetMasteryRewardTiers()
    {
        return _masteryTiers;
    }
}
