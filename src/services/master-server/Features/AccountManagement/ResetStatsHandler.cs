// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using MediatR;
using Microsoft.Extensions.Logging;
using NEXUS.Contracts.CQRS.AccountManagement;
using NEXUS.Contracts.Data;
using MERRICK.DatabaseContext.Entities.Core;

namespace KONGOR.MasterServer.Features.AccountManagement;

/// <summary>
///     MediatR command handler for resetting player statistics (<see cref="ResetStatsCommand"/>).
///     Revokes stat reset consumable items from the player inventory and returns the golden status payload.
/// </summary>
public sealed class ResetStatsHandler(
    IAccountRepository accountRepository,
    IInventoryRepository inventoryRepository,
    ILogger<ResetStatsHandler> logger,
    TimeProvider? timeProvider = null)
    : IRequestHandler<ResetStatsCommand, ResetStatsResultDto>
{
    private static readonly string[] StatResetCodes = ["m.Stat Reset", "Stat Reset", "ac.resetstats", "other.163", "163"];
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    public async Task<ResetStatsResultDto> Handle(
        ResetStatsCommand request,
        CancellationToken cancellationToken)
    {
        logger.LogInformation(
            "[{Timestamp}] [ResetStatsHandler] Stat reset requested for Account ID: {AccountId}",
            _timeProvider.GetUtcNow(),
            request.AccountId);

        MERRICK.DatabaseContext.Entities.Core.Account? account = await accountRepository.GetByIdAsync(request.AccountId, cancellationToken);
        if (account is null)
        {
            logger.LogWarning(
                "[{Timestamp}] [ResetStatsHandler] Account not found for Account ID: {AccountId}",
                _timeProvider.GetUtcNow(),
                request.AccountId);

            Dictionary<string, object> unauthResponse = new()
            {
                ["0"] = false,
                ["success"] = 0,
                ["errors"] = "Session cookie invalid.",
                ["error_code"] = 1,
                ["error_msg"] = "Session cookie invalid."
            };

            return new ResetStatsResultDto(false, 1, "Session cookie invalid.", unauthResponse);
        }

        foreach (string code in StatResetCodes)
        {
            await inventoryRepository.RevokeItemAsync(account.ID, code, cancellationToken);
        }

        if (account.User?.InventoryItems is not null)
        {
            account.User.InventoryItems.RemoveAll(inv =>
                StatResetCodes.Any(c => c.Equals(inv.ItemCode, StringComparison.OrdinalIgnoreCase)));
        }

        logger.LogInformation(
            "[{Timestamp}] [ResetStatsHandler] Stat reset items revoked and processed for account '{AccountName}' (ID: {AccountId})",
            _timeProvider.GetUtcNow(),
            account.Name,
            account.ID);

        Dictionary<string, object> response = new()
        {
            ["0"] = true,
            ["success"] = 1,
            ["status"] = "OK",
            ["account_id"] = account.ID.ToString(),
            ["nickname"] = account.Name,
            ["errors"] = string.Empty,
            ["error_code"] = 0,
            ["error_msg"] = string.Empty
        };

        return new ResetStatsResultDto(true, 0, string.Empty, response);
    }
}
