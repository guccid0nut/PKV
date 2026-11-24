// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using ASPIRE.Common.Serialization.PHP;
using NEXUS.Contracts.Clients;

namespace NEXUS.Contracts.Data;

public interface IInventoryRepository
{
    Task<IReadOnlyList<OwnedStoreItemDTO>> GetOwnedItemsAsync(int accountId, CancellationToken ct = default);
    Task<bool> HasItemAsync(int accountId, string itemCode, CancellationToken ct = default);
    Task GrantItemAsync(int accountId, string itemCode, int quantity = 1, CancellationToken ct = default);
    Task<bool> RevokeItemAsync(int accountId, string itemCode, CancellationToken ct = default);

    Task<bool> DeductCurrenciesAsync(int accountId, int goldDelta, int silverDelta, int plinkoDelta,
        CancellationToken ct = default);

    Task AddCurrenciesAsync(int accountId, int goldDelta, int silverDelta, int plinkoDelta,
        CancellationToken ct = default);

    Task<IReadOnlyList<string>> GetOwnedItemCodesAsync(int accountId, CancellationToken ct = default);
    Task<IReadOnlyList<string>> GetSelectedStoreItemsAsync(int accountId, CancellationToken ct = default);
    Task SetSelectedStoreItemsAsync(int accountId, IEnumerable<string> items, CancellationToken ct = default);
    Task<StoreItem?> GetStoreItemByCodeAsync(string itemCode, CancellationToken ct = default);
    Task<IReadOnlyList<StoreItem>> GetStoreCatalogAsync(CancellationToken ct = default);

    Task<IReadOnlyList<UserWebReward>> GetUserWebRewardsAsync(int userId, CancellationToken ct = default);
    Task<bool> EquipWebRewardAsync(int userId, string rewardType, string rewardCode, CancellationToken ct = default);

    Task<PurchaseItemResultDto> PurchaseStoreItemAsync(int userId, int itemId, string currency, string? idempotencyKey,
        CancellationToken ct = default) => PurchaseStoreItemAsync(userId, itemId, currency, ct);

    Task<PurchaseItemResultDto> PurchaseStoreItemAsync(int userId, int itemId, string currency,
        CancellationToken ct = default);

    Task<EquipItemResultDto> EquipStoreItemAsync(string accountName, int itemId, string? code,
        CancellationToken ct = default);

    Task<PlinkoStatusResponseDto> GetPlinkoStatusAsync(int userId, CancellationToken ct = default);

    Task<PlinkoDropResultDto> PlayPlinkoDropAsync(int userId, string? currency, bool useDaily,
        CancellationToken ct = default);

    Task<RedeemCodeResultDto> RedeemPromoCodeAsync(int userId, string code, CancellationToken ct = default);
    Task<bool> ValidatePromoCodeAsync(string code, CancellationToken ct = default);
}
