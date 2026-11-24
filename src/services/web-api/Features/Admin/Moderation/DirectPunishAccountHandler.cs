// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using MediatR;
using Microsoft.EntityFrameworkCore;
using MERRICK.DatabaseContext.Entities.Core;
using MERRICK.DatabaseContext.Entities.Game;
using MERRICK.DatabaseContext.Persistence;
using NEXUS.Contracts.CQRS.Admin;
using NEXUS.Contracts.CQRS.Admin.Moderation;
using NEXUS.Contracts.Domain;
using NEXUS.Contracts.Events;

namespace ZORGATH.WebPortal.API.Features.Admin.Moderation;

/// <summary>
///     Handler for applying direct administrative disciplinary actions (ban, mute, or suspend) to an account.
/// </summary>
public sealed class DirectPunishAccountHandler(
    MerrickContext db,
    IEventBus? eventBus = null,
    TimeProvider? timeProvider = null) : IRequestHandler<DirectPunishAccountCommand, AdminActionResultDto>
{
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    /// <inheritdoc />
    public async Task<AdminActionResultDto> Handle(DirectPunishAccountCommand request, CancellationToken cancellationToken)
    {
        Account? target = await db.Accounts
            .Include(a => a.User)
            .ThenInclude(u => u.Accounts)
            .FirstOrDefaultAsync(a => a.Name == request.TargetAccountName, cancellationToken);

        if (target == null)
            return new AdminActionResultDto(false, "Target account not found", 0);

        DateTimeOffset? expiration = request.DurationHours.HasValue && request.DurationHours.Value > 0
            ? _timeProvider.GetUtcNow().AddHours(request.DurationHours.Value)
            : null;

        string punishReason = request.Notes ?? request.Reason ?? "Direct Staff Sanction";
        List<Account> allAccounts = target.User?.Accounts.ToList() ?? [target];

        foreach (Account acc in allAccounts)
        {
            switch (request.PunishmentType.ToLowerInvariant())
            {
                case "ban":
                    acc.BanExpiration = expiration ?? _timeProvider.GetUtcNow().AddYears(100);
                    acc.BanReason = punishReason;
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
                    acc.MuteReason = punishReason;
                    break;
                case "suspend":
                    if (acc.Type == AccountType.Disabled || acc.IsBanned)
                    {
                        acc.Type = AccountType.Normal;
                        acc.BanExpiration = null;
                        acc.BanReason = null;
                    }

                    acc.SuspensionExpiration = expiration ?? _timeProvider.GetUtcNow().AddDays(3);
                    acc.SuspensionReason = punishReason;
                    break;
            }
        }

        // Also create a resolved PlayerReport record for audit history
        PlayerReport directReport = new()
        {
            ReporterAccountName = $"Staff_{request.StaffAccountId}",
            ReportedAccountID = target.ID,
            ReportedAccountName = target.Name,
            Reason = request.Reason ?? "Direct Staff Sanction",
            Description = request.Notes ?? "Direct sanction by staff",
            Status = "RESOLVED",
            PunishmentType = request.PunishmentType.ToUpperInvariant(),
            PunishmentDurationHours = request.DurationHours,
            PunishmentExpiration = expiration,
            StaffNotes = request.Notes,
            ReviewedByStaffName = $"Staff_{request.StaffAccountId}",
            TimestampCreated = _timeProvider.GetUtcNow(),
            TimestampReviewed = _timeProvider.GetUtcNow()
        };
        db.PlayerReports.Add(directReport);

        await db.SaveChangesAsync(cancellationToken);

        if (request.PunishmentType.Equals("mute", StringComparison.OrdinalIgnoreCase) && eventBus != null)
        {
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

        return new AdminActionResultDto(true, "Sanction applied successfully", target.ID);
    }
}
