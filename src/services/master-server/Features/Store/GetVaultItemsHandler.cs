// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2022-2026 [K]ONGOR and Contributors
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV) -- Upstream lineage: https://github.com/Project-KONGOR-Open-Source

using MediatR;
using MERRICK.DatabaseContext;
using MERRICK.DatabaseContext.Entities.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NEXUS.Contracts.CQRS.Store;
using NEXUS.Contracts.Store;
using KONGOR.MasterServer.Controllers.Store;

namespace KONGOR.MasterServer.Features.Store;

/// <summary>
///     MediatR query handler for <see cref="GetVaultItemsQuery"/>.
///     Retrieves categorized user-owned vault items and selected upgrades.
/// </summary>
public sealed class GetVaultItemsHandler(
    MerrickContext db,
    ILogger<GetVaultItemsHandler> logger)
    : IRequestHandler<GetVaultItemsQuery, VaultItemsResultDto>
{
    private static readonly StoreCategory[] VaultCategories =
    [
        StoreCategory.AccountIcons,
        StoreCategory.NameColors,
        StoreCategory.ChatSymbols,
        StoreCategory.Taunt,
        StoreCategory.TauntBadges,
        StoreCategory.Announcers,
        StoreCategory.Couriers,
        StoreCategory.Wards,
        StoreCategory.TeleportationEffects,
        StoreCategory.EarlyAccessHeroes,
        StoreCategory.Creeps,
        StoreCategory.SelectionCircles,
        StoreCategory.Enhancements,
        StoreCategory.Miscellaneous
    ];

    public async Task<VaultItemsResultDto> Handle(
        GetVaultItemsQuery request,
        CancellationToken cancellationToken)
    {
        logger.LogDebug("[GetVaultItemsHandler] Handling vault items query for AccountId={AccountId}", request.AccountId);

        Account? account = await db.Accounts
            .AsNoTracking()
            .Include(a => a.User).ThenInclude(u => u!.InventoryItems)
            .Include(a => a.SelectedStoreItems)
            .FirstOrDefaultAsync(a => a.ID == request.AccountId, cancellationToken);

        if (account is null)
        {
            logger.LogWarning("[GetVaultItemsHandler] Account #{AccountId} not found for vault view.", request.AccountId);
            return new VaultItemsResultDto(
                request.AccountId,
                request.HostTime,
                (int)StoreResponseCode.VAULT_ITEM_LIST_RESPONSE,
                new Dictionary<string, object>
                {
                    ["responseCode"] = (int)StoreResponseCode.VAULT_ITEM_LIST_RESPONSE,
                    ["requestHostTime"] = request.HostTime,
                    ["totalPoints"] = 0,
                    ["totalMMPoints"] = 0
                });
        }

        Dictionary<string, object> response = [];
        Dictionary<StoreItemType, List<string>> categorizedVaultEntries =
            StoreVaultHelpers.CategorizeUserOwnedVaultData(account, db, logger);

        foreach (StoreCategory category in VaultCategories)
        {
            StoreItemType itemType = StoreController.MapStoreCategoryToItemType((int)category);
            List<string> entries = categorizedVaultEntries.GetValueOrDefault(itemType) ?? [];
            response["vaultCategory" + (int)category] = string.Join("|", entries);
        }

        response["requestHostTime"] = request.HostTime;
        response["responseCode"] = (int)StoreResponseCode.VAULT_ITEM_LIST_RESPONSE;
        response["totalPoints"] = account.User?.GoldCoins ?? 0;
        response["totalMMPoints"] = account.User?.SilverCoins ?? 0;

        StoreVaultHelpers.PopulateSelectedUpgrades(account, response, db, logger);

        return new VaultItemsResultDto(
            request.AccountId,
            request.HostTime,
            (int)StoreResponseCode.VAULT_ITEM_LIST_RESPONSE,
            response);
    }
}
