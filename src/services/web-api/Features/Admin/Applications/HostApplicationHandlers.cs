// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using MediatR;
using Microsoft.EntityFrameworkCore;
using MERRICK.DatabaseContext.Entities.Core;
using MERRICK.DatabaseContext.Entities.Game;
using MERRICK.DatabaseContext.Extensions;
using MERRICK.DatabaseContext.Persistence;
using NEXUS.Contracts.Clients;
using NEXUS.Contracts.CQRS.Admin;
using NEXUS.Contracts.CQRS.Admin.Applications;
using NEXUS.Contracts.Data;
using NEXUS.Contracts.Domain;
using UserRoles = MERRICK.DatabaseContext.Constants.UserRoles;

namespace ZORGATH.WebPortal.API.Features.Admin.Applications;

/// <summary>
///     Handler for retrieving all host applications.
/// </summary>
public sealed class GetHostApplicationsHandler(MerrickContext db)
    : IRequestHandler<GetHostApplicationsQuery, List<HostApplicationDto>>
{
    /// <inheritdoc />
    public async Task<List<HostApplicationDto>> Handle(GetHostApplicationsQuery request, CancellationToken cancellationToken)
    {
        List<HostApplication> apps = await db.HostApplications
            .Include(h => h.User)
            .Include(h => h.MainAccount)
            .Include(h => h.HostAccount)
            .OrderByDescending(h => h.TimestampApplied)
            .ToListAsync(cancellationToken);

        return apps.Select(h => h.ToDto()).ToList();
    }
}

/// <summary>
///     Handler for approving a dedicated server host application.
/// </summary>
public sealed class ApproveHostApplicationHandler(MerrickContext db, TimeProvider? timeProvider = null)
    : IRequestHandler<ApproveHostApplicationCommand, AdminActionResultDto>
{
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    /// <inheritdoc />
    public async Task<AdminActionResultDto> Handle(ApproveHostApplicationCommand request, CancellationToken cancellationToken)
    {
        HostApplication? application = await db.HostApplications
            .Include(h => h.User)
            .Include(h => h.MainAccount)
            .FirstOrDefaultAsync(h => h.ID == request.ApplicationId, cancellationToken);

        if (application == null)
            return new AdminActionResultDto(false, "Application not found", 0);

        application.Status = HostApplicationStatus.Approved;
        application.TimestampReviewed = _timeProvider.GetUtcNow();

        string hostEmail = $"host_{application.HostUsername.ToLowerInvariant()}@projectkongor.local";
        User? hostUser = await db.Users.FirstOrDefaultAsync(u => u.EmailAddress == hostEmail, cancellationToken);

        if (hostUser == null)
        {
            Role? userRole = await db.Roles.FirstOrDefaultAsync(r => r.Name == UserRoles.User, cancellationToken);
            if (userRole == null)
            {
                userRole = new Role { Name = UserRoles.User };
                db.Roles.Add(userRole);
                await db.SaveChangesAsync(cancellationToken);
            }

            hostUser = new User
            {
                EmailAddress = hostEmail,
                SRPPasswordSalt = string.Empty,
                SRPPasswordHash = string.Empty,
                PBKDF2PasswordHash = string.Empty,
                Role = userRole,
                GoldCoins = 0,
                SilverCoins = 0,
                PlinkoTickets = 0,
                TimestampCreated = _timeProvider.GetUtcNow(),
                TimestampLastActive = _timeProvider.GetUtcNow()
            };
            db.Users.Add(hostUser);
            await db.SaveChangesAsync(cancellationToken);
        }

        Account? hostAccount = await db.Accounts.FirstOrDefaultAsync(a => a.Name == application.HostUsername, cancellationToken);
        if (hostAccount == null)
        {
            hostAccount = new Account
            {
                Name = application.HostUsername,
                User = hostUser,
                OwnerAccountID = application.MainAccountID,
                Type = AccountType.ServerHost,
                IsMain = false,
                TimestampCreated = _timeProvider.GetUtcNow(),
                TimestampLastActive = _timeProvider.GetUtcNow()
            };
            db.Accounts.Add(hostAccount);
            await db.SaveChangesAsync(cancellationToken);
        }
        else
        {
            hostAccount.Type = AccountType.ServerHost;
            hostAccount.OwnerAccountID = application.MainAccountID;
        }

        application.HostAccountID = hostAccount.ID;
        application.HostAccount = hostAccount;

        await db.SaveChangesAsync(cancellationToken);
        return new AdminActionResultDto(true, "Host application approved successfully", application.ID);
    }
}

/// <summary>
///     Handler for rejecting a dedicated server host application.
/// </summary>
public sealed class RejectHostApplicationHandler(MerrickContext db, TimeProvider? timeProvider = null)
    : IRequestHandler<RejectHostApplicationCommand, AdminActionResultDto>
{
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    /// <inheritdoc />
    public async Task<AdminActionResultDto> Handle(RejectHostApplicationCommand request, CancellationToken cancellationToken)
    {
        HostApplication? application = await db.HostApplications.FirstOrDefaultAsync(h => h.ID == request.ApplicationId, cancellationToken);
        if (application == null)
            return new AdminActionResultDto(false, "Application not found", 0);

        application.Status = HostApplicationStatus.Rejected;
        application.StaffNotes = request.Reason;
        application.TimestampReviewed = _timeProvider.GetUtcNow();
        await db.SaveChangesAsync(cancellationToken);

        return new AdminActionResultDto(true, "Host application rejected successfully", application.ID);
    }
}

/// <summary>
///     Handler for revoking an approved dedicated server host application.
/// </summary>
public sealed class RevokeHostApplicationHandler(MerrickContext db, TimeProvider? timeProvider = null)
    : IRequestHandler<RevokeHostApplicationCommand, AdminActionResultDto>
{
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    /// <inheritdoc />
    public async Task<AdminActionResultDto> Handle(RevokeHostApplicationCommand request, CancellationToken cancellationToken)
    {
        HostApplication? application = await db.HostApplications
            .Include(h => h.HostAccount)
            .FirstOrDefaultAsync(h => h.ID == request.ApplicationId, cancellationToken);

        if (application == null)
            return new AdminActionResultDto(false, "Application not found", 0);

        application.Status = HostApplicationStatus.Revoked;
        application.StaffNotes = request.Reason;
        application.TimestampReviewed = _timeProvider.GetUtcNow();
        if (application.HostAccount != null)
        {
            application.HostAccount.Type = AccountType.Disabled;
        }

        await db.SaveChangesAsync(cancellationToken);
        return new AdminActionResultDto(true, "Host application revoked successfully", application.ID);
    }
}
