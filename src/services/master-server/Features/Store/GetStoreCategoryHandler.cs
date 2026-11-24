// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

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
///     MediatR query handler for <see cref="GetStoreCategoryQuery"/>.
///     Retrieves the account context and populates store item listings for the requested category and page.
/// </summary>
public sealed class GetStoreCategoryHandler(
    MerrickContext db,
    ILogger<GetStoreCategoryHandler> logger)
    : IRequestHandler<GetStoreCategoryQuery, StoreCategoryResultDto>
{
    public async Task<StoreCategoryResultDto> Handle(
        GetStoreCategoryQuery request,
        CancellationToken cancellationToken)
    {
        logger.LogInformation(
            "[GetStoreCategoryHandler] Query: AccountId={AccountId}, CategoryId={CategoryId}, Page={Page}",
            request.AccountId, request.CategoryId, request.Page);

        Account? account = await db.Accounts
            .Include(a => a.User).ThenInclude(u => u!.InventoryItems)
            .Include(a => a.SelectedStoreItems)
            .AsNoTracking()
            .AsSplitQuery()
            .FirstOrDefaultAsync(a => a.ID == request.AccountId, cancellationToken);

        if (account is null)
        {
            logger.LogWarning("[GetStoreCategoryHandler] Account ID {AccountId} not found in database", request.AccountId);
            return new StoreCategoryResultDto(
                request.CategoryId,
                request.Page,
                request.HostTime,
                (int)StoreResponseCode.BASIC_ITEM_LIST_RESPONSE,
                StoreController.CreateErrorResponse((int)StoreErrorCode.STORE_ACCOUNT_LOAD_ERROR));
        }

        account.User ??= new User
        {
            ID = account.ID,
            EmailAddress = $"{account.Name.ToLowerInvariant()}@kongor.net",
            SRPPasswordSalt = "salt",
            SRPPasswordHash = "hash",
            InventoryItems = [],
            SilverCoins = 0,
            GoldCoins = 0
        };

        Dictionary<string, object> response = [];
        StoreController.PopulateItemListing(account.User, account, response, request.CategoryId, request.Page);

        response["popupCode"] = -1;
        response["responseCode"] = (int)StoreResponseCode.BASIC_ITEM_LIST_RESPONSE;
        response["requestHostTime"] = request.HostTime;

        if (!response.ContainsKey("errorCode")) response["errorCode"] = 0;

        return new StoreCategoryResultDto(
            request.CategoryId,
            request.Page,
            request.HostTime,
            (int)StoreResponseCode.BASIC_ITEM_LIST_RESPONSE,
            response);
    }
}
