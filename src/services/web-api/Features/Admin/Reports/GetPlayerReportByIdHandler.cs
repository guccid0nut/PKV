// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using MediatR;
using Microsoft.EntityFrameworkCore;
using MERRICK.DatabaseContext.Entities.Game;
using MERRICK.DatabaseContext.Extensions;
using MERRICK.DatabaseContext.Persistence;
using NEXUS.Contracts.Clients;
using NEXUS.Contracts.CQRS.Admin.Reports;

namespace ZORGATH.WebPortal.API.Features.Admin.Reports;

/// <summary>
///     Handler for retrieving a single player report by its unique ID.
/// </summary>
public sealed class GetPlayerReportByIdHandler(MerrickContext db)
    : IRequestHandler<GetPlayerReportByIdQuery, PlayerReportDto?>
{
    /// <inheritdoc />
    public async Task<PlayerReportDto?> Handle(GetPlayerReportByIdQuery request, CancellationToken cancellationToken)
    {
        PlayerReport? report = await db.PlayerReports
            .AsNoTracking()
            .FirstOrDefaultAsync(r => r.ID == request.ReportId, cancellationToken);

        return report?.ToDto();
    }
}
