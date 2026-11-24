// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using MediatR;
using MERRICK.DatabaseContext;
using MERRICK.DatabaseContext.Entities.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NEXUS.Contracts.CQRS.Admin;
using NEXUS.Contracts.CQRS.Admin.Moderation;
using NEXUS.Contracts.Events;

namespace ZORGATH.WebPortal.API.Features.Admin.Moderation;

public sealed class UnmuteAccountHandler(
    MerrickContext db,
    ILogger<UnmuteAccountHandler> logger,
    IEventBus? eventBus = null) : IRequestHandler<UnmuteAccountCommand, AdminActionResultDto>
{
    public async Task<AdminActionResultDto> Handle(UnmuteAccountCommand request, CancellationToken cancellationToken)
    {
        logger.LogInformation("[UnmuteAccountHandler] Unmuting Account {AccountId}", request.AccountId);

        Account? account = await db.Accounts.FirstOrDefaultAsync(a => a.ID == request.AccountId, cancellationToken);
        if (account == null)
        {
            return new AdminActionResultDto(
                Success: false,
                Message: $"Account {request.AccountId} not found.",
                AffectedId: request.AccountId);
        }

        account.MuteExpiration = null;
        account.MuteReason = null;
        await db.SaveChangesAsync(cancellationToken);

        if (eventBus != null)
        {
            AccountUnmutedEvent domainEvent = new(account.ID, account.Name);
            await eventBus.PublishAsync(domainEvent, cancellationToken);
        }

        return new AdminActionResultDto(
            Success: true,
            Message: $"Account {account.Name} (ID: {account.ID}) successfully unmuted.",
            AffectedId: account.ID);
    }
}
