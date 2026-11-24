// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using MediatR;
using MERRICK.DatabaseContext;
using MERRICK.DatabaseContext.Entities.Core;
using MERRICK.DatabaseContext.Entities.Game;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NEXUS.Contracts.CQRS.Admin;
using NEXUS.Contracts.CQRS.Admin.Reports;
using NEXUS.Contracts.Events;

namespace ZORGATH.WebPortal.API.Features.Admin.Reports;

public sealed class ActionReportHandler(
    MerrickContext db,
    ILogger<ActionReportHandler> logger,
    IEventBus? eventBus = null,
    TimeProvider? timeProvider = null) : IRequestHandler<ActionReportCommand, AdminActionResultDto>
{
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    public async Task<AdminActionResultDto> Handle(ActionReportCommand request, CancellationToken cancellationToken)
    {
        logger.LogInformation(
            "[ActionReportHandler] Actioning Report {ReportId} with ActionType '{ActionType}' by Staff {StaffAccountId}",
            request.ReportId,
            request.ActionType,
            request.StaffAccountId);

        PlayerReport? report = await db.PlayerReports.FirstOrDefaultAsync(r => r.ID == request.ReportId, cancellationToken);
        if (report == null)
        {
            return new AdminActionResultDto(
                Success: false,
                Message: $"Report {request.ReportId} not found.",
                AffectedId: request.ReportId);
        }

        Account? target = await db.Accounts.Include(a => a.User).ThenInclude(u => u.Accounts)
            .FirstOrDefaultAsync(a => a.Name == report.ReportedAccountName || a.ID == report.ReportedAccountID, cancellationToken);

        DateTimeOffset? expiration = request.DurationHours.HasValue && request.DurationHours.Value > 0
            ? _timeProvider.GetUtcNow().AddHours(request.DurationHours.Value)
            : null;

        if (target != null)
        {
            List<Account> allAccounts = target.User?.Accounts.ToList() ?? [target];
            string actionReason = request.StaffNotes ?? request.Reason ?? report.Reason;
            foreach (Account acc in allAccounts)
            {
                switch (request.ActionType.ToLowerInvariant())
                {
                    case "ban":
                        acc.BanExpiration = expiration ?? _timeProvider.GetUtcNow().AddYears(100);
                        acc.BanReason = actionReason;
                        acc.Type = AccountType.Disabled;
                        break;
                    case "mute":
                        if (acc.Type == AccountType.Disabled || acc.IsBanned)
                        {
                            acc.Type = AccountType.Normal;
                            acc.BanExpiration = null;
                            acc.BanReason = null;
                        }

                        acc.MuteExpiration = expiration ?? _timeProvider.GetUtcNow().AddDays(7);
                        acc.MuteReason = actionReason;
                        break;
                    case "suspend":
                        if (acc.Type == AccountType.Disabled || acc.IsBanned)
                        {
                            acc.Type = AccountType.Normal;
                            acc.BanExpiration = null;
                            acc.BanReason = null;
                        }

                        acc.SuspensionExpiration = expiration ?? _timeProvider.GetUtcNow().AddDays(3);
                        acc.SuspensionReason = actionReason;
                        break;
                }
            }
        }

        if (request.ActionType.Equals("dismiss", StringComparison.OrdinalIgnoreCase))
            report.Status = "DISMISSED";
        else
            report.Status = "RESOLVED";

        report.PunishmentType = request.ActionType.ToUpperInvariant();
        report.PunishmentDurationHours = request.DurationHours;
        report.PunishmentExpiration = expiration;
        report.StaffNotes = request.StaffNotes;
        report.ReviewedByStaffName = $"Staff_{request.StaffAccountId}";
        report.TimestampReviewed = _timeProvider.GetUtcNow();
        await db.SaveChangesAsync(cancellationToken);

        if (request.ActionType.Equals("mute", StringComparison.OrdinalIgnoreCase) && eventBus != null && target != null)
        {
            List<Account> allAccounts = target.User?.Accounts.ToList() ?? [target];
            foreach (Account acc in allAccounts)
            {
                AccountMutedEvent mutedEvent = new(
                    acc.ID,
                    acc.Name,
                    acc.MuteExpiration,
                    acc.MuteReason,
                    request.StaffAccountId);
                await eventBus.PublishAsync(mutedEvent, cancellationToken);
            }
        }

        return new AdminActionResultDto(
            Success: true,
            Message: $"Report {request.ReportId} actioned with {request.ActionType.ToUpperInvariant()}.",
            AffectedId: request.ReportId,
            Details: request.StaffNotes);
    }
}
