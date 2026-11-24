// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

namespace NEXUS.Contracts.Services;

/// <summary>
///     Store catalog product item representation.
/// </summary>
public record CatalogItemDTO(
    int ID,
    string Name,
    string Description,
    string ProductCode,
    string Type,
    int CostGold,
    int CostSilver,
    string IconPath
);

/// <summary>
///     Daily special discounted product offering.
/// </summary>
public record DailySpecialDTO(
    int PanelIndex,
    string ProductCode,
    int DiscountOff,
    int DiscountSilver,
    string TagTitle,
    string TagDateLimit
);

/// <summary>
///     Progression and mastery level reward tier definition.
/// </summary>
public record MasteryRewardTierDTO(
    int RequiredLevel,
    int ProductIdentifier,
    string ProductCode,
    string ProductName,
    string ProductLocalResource,
    int ProductQuantity,
    int GoldCoins,
    int SilverCoins,
    int PlinkoTickets
);

/// <summary>
///     Service abstraction for querying product items, daily specials, and mastery reward tiers.
/// </summary>
public interface IStoreCatalogService
{
    bool IsValidProductCode(string productCode);

    int GetProductId(string productCode);

    CatalogItemDTO? GetProductByCode(string productCode);

    CatalogItemDTO? GetProductById(int productId);

    IReadOnlyList<DailySpecialDTO> GetDailySpecials();

    IReadOnlyList<MasteryRewardTierDTO> GetMasteryRewardTiers();
}
