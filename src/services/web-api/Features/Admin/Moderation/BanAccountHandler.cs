// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using MediatR;
using MERRICK.DatabaseContext;
using MERRICK.DatabaseContext.Entities.Core;
using NEXUS.Contracts.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NEXUS.Contracts.CQRS.Admin;
using NEXUS.Contracts.CQRS.Admin.Moderation;
using NEXUS.Contracts.Events;

namespace ZORGATH.WebPortal.API.Features.Admin.Moderation;

public sealed class BanAccountHandler(
    MerrickContext db,
    IAccountRepository accountRepo,
    ILogger<BanAccountHandler> logger,
    IEventBus? eventBus = null,
    TimeProvider? timeProvider = null) : IRequestHandler<BanAccountCommand, AdminActionResultDto>
{
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    public async Task<AdminActionResultDto> Handle(BanAccountCommand request, CancellationToken cancellationToken)
    {
        logger.LogInformation(
            "[BanAccountHandler] Executing ban on Account {AccountId} by Staff {StaffId}: {Reason} (Expires: {ExpiresAt})",
            request.AccountId,
            request.BannedBy,
            request.Reason,
            request.ExpiresAt);

        await accountRepo.BanAccountAsync(request.AccountId, request.ExpiresAt, request.Reason, request.BannedBy, cancellationToken);

        Account? account = await db.Accounts.AsNoTracking()
            .FirstOrDefaultAsync(a => a.ID == request.AccountId, cancellationToken);

        string accountName = account?.Name ?? $"Account_{request.AccountId}";

        if (eventBus != null)
        {
            AccountBannedEvent domainEvent = new(
                Guid.NewGuid(),
                _timeProvider.GetUtcNow(),
                request.AccountId,
                accountName,
                request.ExpiresAt,
                request.Reason,
                request.BannedBy
            );

            await eventBus.PublishAsync(domainEvent, cancellationToken);
        }

        return new AdminActionResultDto(
            Success: true,
            Message: $"Account {accountName} (ID: {request.AccountId}) successfully banned.",
            AffectedId: request.AccountId,
            Details: request.Reason);
    }
}
