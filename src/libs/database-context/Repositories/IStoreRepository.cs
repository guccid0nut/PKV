// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using MERRICK.DatabaseContext.Entities.Core;
using MERRICK.DatabaseContext.Entities.Store;

namespace NEXUS.Contracts.Data;

/// <summary>
/// Encapsulates data access for store item catalog lookups, product resolution,
/// item ownership verification, and store account metadata.
/// </summary>
public interface IStoreRepository
{
    /// <summary>
    /// Retrieves a store item by its unique database primary key ID.
    /// </summary>
    Task<StoreItem?> GetProductByIdAsync(int productId, CancellationToken ct = default);

    /// <summary>
    /// Retrieves a store item by its product code or name (case-insensitive).
    /// </summary>
    Task<StoreItem?> GetProductByCodeAsync(string code, CancellationToken ct = default);

    /// <summary>
    /// Retrieves the entire catalog of store items.
    /// </summary>
    Task<IReadOnlyList<StoreItem>> GetStoreCatalogAsync(CancellationToken ct = default);

    /// <summary>
    /// Retrieves all enabled store items in the catalog.
    /// </summary>
    Task<IReadOnlyList<StoreItem>> GetEnabledProductsAsync(CancellationToken ct = default);

    /// <summary>
    /// Retrieves all purchasable store items in the catalog.
    /// </summary>
    Task<IReadOnlyList<StoreItem>> GetPurchasableProductsAsync(CancellationToken ct = default);

    /// <summary>
    /// Retrieves all store items matching a specific upgrade type.
    /// </summary>
    Task<IReadOnlyList<StoreItem>> GetProductsByTypeAsync(int upgradeType, CancellationToken ct = default);

    /// <summary>
    /// Retrieves all owned item codes for an account by account nickname/name from core.UserInventory.
    /// </summary>
    Task<IReadOnlyList<string>> GetOwnedItemCodesAsync(string accountName, CancellationToken ct = default);

    /// <summary>
    /// Retrieves all owned item codes for an account by account ID from core.UserInventory.
    /// </summary>
    Task<IReadOnlyList<string>> GetOwnedItemCodesByAccountIdAsync(int accountId, CancellationToken ct = default);

    /// <summary>
    /// Loads an Account with its associated User and owned items by account name,
    /// optimized for store handlers (DailySpecial, BundleContents).
    /// </summary>
    Task<Account?> GetAccountWithUserForStoreAsync(string accountName, CancellationToken ct = default);

    /// <summary>
    /// Loads an Account with its associated User and owned items by account ID,
    /// optimized for store handlers.
    /// </summary>
    Task<Account?> GetAccountWithUserForStoreByIdAsync(int accountId, CancellationToken ct = default);

    /// <summary>
    /// Checks whether an account owns a specific item code.
    /// </summary>
    Task<bool> HasItemAsync(int accountId, string itemCode, CancellationToken ct = default);

    /// <summary>
    /// Recursively resolves the recipe components tree for a given item code from misc.HeroItems and misc.HeroItemComponents.
    /// </summary>
    Task<ItemRecipeNodeDTO?> ResolveRecipeTreeAsync(string itemCode, CancellationToken ct = default);
}
