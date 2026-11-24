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

public sealed class DismissReportHandler(
    MerrickContext db,
    ILogger<DismissReportHandler> logger,
    IEventBus? eventBus = null,
    TimeProvider? timeProvider = null) : IRequestHandler<DismissReportCommand, AdminActionResultDto>
{
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    public async Task<AdminActionResultDto> Handle(DismissReportCommand request, CancellationToken cancellationToken)
    {
        logger.LogInformation(
            "[DismissReportHandler] Dismissing Report {ReportId} by Staff {StaffAccountId}",
            request.ReportId,
            request.StaffAccountId);

        PlayerReport? report = await db.PlayerReports.FirstOrDefaultAsync(r => r.ID == request.ReportId, cancellationToken);
        if (report == null)
        {
            return new AdminActionResultDto(
                Success: false,
                Message: $"Report {request.ReportId} not found.",
                AffectedId: request.ReportId);
        }

        report.Status = "APPEALED";
        report.StaffNotes = request.Comment;
        report.ReviewedByStaffName = $"Staff_{request.StaffAccountId}";
        report.TimestampReviewed = _timeProvider.GetUtcNow();

        // Clear sanctions on reported account and linked accounts
        Account? target = await db.Accounts.Include(a => a.User).ThenInclude(u => u.Accounts)
            .FirstOrDefaultAsync(a => a.Name == report.ReportedAccountName || a.ID == report.ReportedAccountID, cancellationToken);

        if (target != null)
        {
            List<Account> allAccounts = target.User?.Accounts.ToList() ?? [target];
            foreach (Account acc in allAccounts)
            {
                acc.BanExpiration = null;
                acc.BanReason = null;
                acc.SuspensionExpiration = null;
                acc.SuspensionReason = null;
                acc.MuteExpiration = null;
                acc.MuteReason = null;
                if (acc.Type == AccountType.Disabled) acc.Type = AccountType.Legacy;

                if (eventBus != null)
                {
                    AccountUnmutedEvent unmutedEvent = new(acc.ID, acc.Name);
                    await eventBus.PublishAsync(unmutedEvent, cancellationToken);
                }
            }
        }

        await db.SaveChangesAsync(cancellationToken);

        return new AdminActionResultDto(
            Success: true,
            Message: $"Report {request.ReportId} appealed and dismissed.",
            AffectedId: request.ReportId);
    }
}
