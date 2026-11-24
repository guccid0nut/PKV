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
using NEXUS.Contracts.Domain;
using NEXUS.Contracts.Events;

namespace ZORGATH.WebPortal.API.Features.Admin.Users;

public sealed class SetAccountRoleHandler(
    MerrickContext db,
    ILogger<SetAccountRoleHandler> logger,
    IEventBus? eventBus = null,
    TimeProvider? timeProvider = null) : IRequestHandler<SetAccountRoleCommand, AdminActionResultDto>
{
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    public async Task<AdminActionResultDto> Handle(SetAccountRoleCommand request, CancellationToken cancellationToken)
    {
        logger.LogInformation(
            "[{Timestamp}] [SetAccountRoleHandler] Setting account #{AccountId} type to {AccountType}",
            _timeProvider.GetUtcNow(),
            request.AccountId,
            (AccountType)request.AccountType);

        Account? account = await db.Accounts.FirstOrDefaultAsync(a => a.ID == request.AccountId, cancellationToken);
        if (account == null)
        {
            logger.LogWarning(
                "[{Timestamp}] [SetAccountRoleHandler] Account #{AccountId} not found.",
                _timeProvider.GetUtcNow(),
                request.AccountId);
            return new AdminActionResultDto(
                Success: false,
                Message: $"Account #{request.AccountId} not found",
                AffectedId: request.AccountId);
        }

        account.Type = (AccountType)request.AccountType;
        await db.SaveChangesAsync(cancellationToken);

        if (eventBus != null && account.Type == AccountType.Disabled)
        {
            AccountBannedEvent domainEvent = new(
                Guid.NewGuid(),
                _timeProvider.GetUtcNow(),
                account.ID,
                account.Name,
                _timeProvider.GetUtcNow().AddYears(100),
                request.Reason ?? "Account disabled by administrator"
            );

            await eventBus.PublishAsync(domainEvent, cancellationToken);
        }

        logger.LogInformation(
            "[{Timestamp}] [SetAccountRoleHandler] Successfully updated account #{AccountId} ({AccountName}) type to {AccountType}",
            _timeProvider.GetUtcNow(),
            account.ID,
            account.Name,
            account.Type);

        return new AdminActionResultDto(
            Success: true,
            Message: $"Account #{account.ID} ({account.Name}) type set to {account.Type}.",
            AffectedId: account.ID);
    }
}
