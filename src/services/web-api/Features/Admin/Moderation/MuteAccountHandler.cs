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

public sealed class MuteAccountHandler(
    MerrickContext db,
    ILogger<MuteAccountHandler> logger,
    IEventBus? eventBus = null,
    TimeProvider? timeProvider = null) : IRequestHandler<MuteAccountCommand, AdminActionResultDto>
{
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    public async Task<AdminActionResultDto> Handle(MuteAccountCommand request, CancellationToken cancellationToken)
    {
        logger.LogInformation(
            "[MuteAccountHandler] Executing mute on Account {AccountId} by Staff {StaffId}: {Reason} (Expires: {ExpiresAt})",
            request.AccountId,
            request.MutedBy,
            request.Reason,
            request.ExpiresAt);

        Account? account = await db.Accounts.FirstOrDefaultAsync(a => a.ID == request.AccountId, cancellationToken);
        if (account == null)
        {
            return new AdminActionResultDto(
                Success: false,
                Message: $"Account {request.AccountId} not found.",
                AffectedId: request.AccountId);
        }

        account.MuteExpiration = request.ExpiresAt ?? _timeProvider.GetUtcNow().AddDays(7);
        account.MuteReason = request.Reason ?? "Direct Staff Sanction";
        await db.SaveChangesAsync(cancellationToken);

        if (eventBus != null)
        {
            AccountMutedEvent domainEvent = new(
                account.ID,
                account.Name,
                account.MuteExpiration,
                account.MuteReason,
                request.MutedBy
            );
            await eventBus.PublishAsync(domainEvent, cancellationToken);
        }

        return new AdminActionResultDto(
            Success: true,
            Message: $"Account {account.Name} (ID: {account.ID}) successfully muted.",
            AffectedId: account.ID,
            Details: account.MuteReason);
    }
}
