// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using MediatR;
using MERRICK.DatabaseContext;
using MERRICK.DatabaseContext.Entities.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NEXUS.Contracts.CQRS.Admin;
using NEXUS.Contracts.CQRS.Admin.Users;

namespace ZORGATH.WebPortal.API.Features.Admin.Users;

public sealed class AdjustUserBalanceHandler(
    MerrickContext db,
    ILogger<AdjustUserBalanceHandler> logger,
    TimeProvider? timeProvider = null) : IRequestHandler<AdjustUserBalanceCommand, AdminActionResultDto>
{
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    public async Task<AdminActionResultDto> Handle(AdjustUserBalanceCommand request, CancellationToken cancellationToken)
    {
        logger.LogInformation(
            "[{Timestamp}] [AdjustUserBalanceHandler] Adjusting balance for User {UserId}: GoldDelta={GoldDelta}, SilverDelta={SilverDelta}, TicketsDelta={TicketsDelta}, Reason={Reason}",
            _timeProvider.GetUtcNow(),
            request.UserId,
            request.GoldDelta,
            request.SilverDelta,
            request.TicketsDelta,
            request.Reason);

        User? user = await db.Users
            .Include(u => u.Accounts)
            .FirstOrDefaultAsync(u => u.ID == request.UserId, cancellationToken);

        if (user == null)
        {
            logger.LogWarning("[{Timestamp}] [AdjustUserBalanceHandler] User #{UserId} not found.", _timeProvider.GetUtcNow(), request.UserId);
            return new AdminActionResultDto(
                Success: false,
                Message: $"User #{request.UserId} not found",
                AffectedId: request.UserId);
        }

        int oldGold = user.GoldCoins;
        int oldSilver = user.SilverCoins;
        int oldPlinko = user.PlinkoTickets;

        user.GoldCoins = Math.Max(0, user.GoldCoins + request.GoldDelta);
        user.SilverCoins = Math.Max(0, user.SilverCoins + request.SilverDelta);
        user.PlinkoTickets = Math.Max(0, user.PlinkoTickets + request.TicketsDelta);

        Account? mainAccount = user.Accounts.FirstOrDefault(a => a.IsMain) ?? user.Accounts.FirstOrDefault();
        db.EconomyTransactions.Add(new EconomyTransaction
        {
            UserID = user.ID,
            AccountID = mainAccount?.ID,
            GoldCoinsDelta = user.GoldCoins - oldGold,
            SilverCoinsDelta = user.SilverCoins - oldSilver,
            PlinkoTicketsDelta = user.PlinkoTickets - oldPlinko,
            GoldCoinsBalanceAfter = user.GoldCoins,
            SilverCoinsBalanceAfter = user.SilverCoins,
            PlinkoTicketsBalanceAfter = user.PlinkoTickets,
            TransactionType = "AdminAdjustBalance",
            Description = request.Reason,
            Timestamp = _timeProvider.GetUtcNow()
        });

        await db.SaveChangesAsync(cancellationToken);

        logger.LogInformation(
            "[{Timestamp}] [AdjustUserBalanceHandler] Balance updated for User {UserId}: Gold={Gold} (delta {GoldDelta}), Silver={Silver} (delta {SilverDelta}), Tickets={Tickets} (delta {TicketsDelta})",
            _timeProvider.GetUtcNow(),
            user.ID,
            user.GoldCoins,
            user.GoldCoins - oldGold,
            user.SilverCoins,
            user.SilverCoins - oldSilver,
            user.PlinkoTickets,
            user.PlinkoTickets - oldPlinko);

        return new AdminActionResultDto(
            Success: true,
            Message: $"User #{user.ID} balances adjusted: Gold={user.GoldCoins} (delta {user.GoldCoins - oldGold}), Silver={user.SilverCoins} (delta {user.SilverCoins - oldSilver}), Tickets={user.PlinkoTickets} (delta {user.PlinkoTickets - oldPlinko}).",
            AffectedId: user.ID);
    }
}
