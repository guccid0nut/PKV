// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using MediatR;
using Microsoft.EntityFrameworkCore;
using MERRICK.DatabaseContext.Entities.Game;
using MERRICK.DatabaseContext.Extensions;
using MERRICK.DatabaseContext.Persistence;
using NEXUS.Contracts.Clients;
using NEXUS.Contracts.CQRS.Admin;
using NEXUS.Contracts.CQRS.Admin.Applications;
using NEXUS.Contracts.Data;
using NEXUS.Contracts.Domain;

namespace ZORGATH.WebPortal.API.Features.Admin.Applications;

/// <summary>
///     Handler for retrieving all streamer program applications.
/// </summary>
public sealed class GetStreamerApplicationsHandler(MerrickContext db)
    : IRequestHandler<GetStreamerApplicationsQuery, List<StreamerApplicationDto>>
{
    /// <inheritdoc />
    public async Task<List<StreamerApplicationDto>> Handle(GetStreamerApplicationsQuery request, CancellationToken cancellationToken)
    {
        List<StreamerApplication> apps = await db.StreamerApplications
            .Include(s => s.User)
            .Include(s => s.MainAccount)
            .OrderByDescending(s => s.TimestampApplied)
            .ToListAsync(cancellationToken);

        return apps.Select(s => s.ToDto()).ToList();
    }
}

/// <summary>
///     Handler for approving a streamer program application.
/// </summary>
public sealed class ApproveStreamerApplicationHandler(MerrickContext db, TimeProvider? timeProvider = null)
    : IRequestHandler<ApproveStreamerApplicationCommand, AdminActionResultDto>
{
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    /// <inheritdoc />
    public async Task<AdminActionResultDto> Handle(ApproveStreamerApplicationCommand request, CancellationToken cancellationToken)
    {
        StreamerApplication? application = await db.StreamerApplications
            .Include(s => s.User)
            .Include(s => s.MainAccount)
            .FirstOrDefaultAsync(s => s.ID == request.ApplicationId, cancellationToken);

        if (application == null)
            return new AdminActionResultDto(false, "Application not found", 0);

        application.Status = StreamerApplicationStatus.Approved;
        application.TimestampReviewed = _timeProvider.GetUtcNow();

        if (application.MainAccount != null)
        {
            application.MainAccount.Type = AccountType.Streamer;
        }

        await db.SaveChangesAsync(cancellationToken);
        return new AdminActionResultDto(true, "Streamer application approved successfully", application.ID);
    }
}

/// <summary>
///     Handler for rejecting a streamer program application.
/// </summary>
public sealed class RejectStreamerApplicationHandler(MerrickContext db, TimeProvider? timeProvider = null)
    : IRequestHandler<RejectStreamerApplicationCommand, AdminActionResultDto>
{
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    /// <inheritdoc />
    public async Task<AdminActionResultDto> Handle(RejectStreamerApplicationCommand request, CancellationToken cancellationToken)
    {
        StreamerApplication? application =
            await db.StreamerApplications.FirstOrDefaultAsync(s => s.ID == request.ApplicationId, cancellationToken);

        if (application == null)
            return new AdminActionResultDto(false, "Application not found", 0);

        application.Status = StreamerApplicationStatus.Rejected;
        application.StaffNotes = request.Reason;
        application.TimestampReviewed = _timeProvider.GetUtcNow();
        await db.SaveChangesAsync(cancellationToken);

        return new AdminActionResultDto(true, "Streamer application rejected successfully", application.ID);
    }
}

/// <summary>
///     Handler for revoking an approved streamer program application.
/// </summary>
public sealed class RevokeStreamerApplicationHandler(MerrickContext db, TimeProvider? timeProvider = null)
    : IRequestHandler<RevokeStreamerApplicationCommand, AdminActionResultDto>
{
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    /// <inheritdoc />
    public async Task<AdminActionResultDto> Handle(RevokeStreamerApplicationCommand request, CancellationToken cancellationToken)
    {
        StreamerApplication? application = await db.StreamerApplications
            .Include(s => s.MainAccount)
            .FirstOrDefaultAsync(s => s.ID == request.ApplicationId, cancellationToken);

        if (application == null)
            return new AdminActionResultDto(false, "Application not found", 0);

        application.Status = StreamerApplicationStatus.Revoked;
        application.TimestampReviewed = _timeProvider.GetUtcNow();

        if (application.MainAccount != null && application.MainAccount.Type == AccountType.Streamer)
        {
            application.MainAccount.Type = AccountType.Normal;
        }

        await db.SaveChangesAsync(cancellationToken);
        return new AdminActionResultDto(true, "Streamer application revoked successfully", application.ID);
    }
}
