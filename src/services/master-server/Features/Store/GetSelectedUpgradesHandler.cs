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
///     MediatR query handler for <see cref="GetSelectedUpgradesQuery"/>.
///     Retrieves product IDs of selected upgrades for the specified account.
/// </summary>
public sealed class GetSelectedUpgradesHandler(
    MerrickContext db,
    ILogger<GetSelectedUpgradesHandler> logger)
    : IRequestHandler<GetSelectedUpgradesQuery, SelectedUpgradesResultDto>
{
    public async Task<SelectedUpgradesResultDto> Handle(
        GetSelectedUpgradesQuery request,
        CancellationToken cancellationToken)
    {
        logger.LogDebug("[GetSelectedUpgradesHandler] Handling selected upgrades query for AccountId={AccountId}", request.AccountId);

        Account? account = await db.Accounts
            .AsNoTracking()
            .Include(a => a.User)
            .Include(a => a.SelectedStoreItems)
            .FirstOrDefaultAsync(a => a.ID == request.AccountId, cancellationToken);

        Dictionary<string, object> response = [];

        if (account is not null)
        {
            StoreVaultHelpers.PopulateSelectedUpgrades(account, response, db, logger);
        }
        else
        {
            response["selectedUpgrades"] = string.Empty;
        }

        response["responseCode"] = (int)StoreResponseCode.UPDATE_SELECTED_RESPONSE;
        response["popupCode"] = -1;
        response["requestHostTime"] = request.HostTime;

        return new SelectedUpgradesResultDto(
            request.AccountId,
            request.HostTime,
            (int)StoreResponseCode.UPDATE_SELECTED_RESPONSE,
            response);
    }
}
