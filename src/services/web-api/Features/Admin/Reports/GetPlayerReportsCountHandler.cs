// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using MediatR;
using MERRICK.DatabaseContext;
using MERRICK.DatabaseContext.Entities.Game;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NEXUS.Contracts.CQRS.Admin.Reports;

namespace ZORGATH.WebPortal.API.Features.Admin.Reports;

public sealed class GetPlayerReportsCountHandler(
    MerrickContext db,
    ILogger<GetPlayerReportsCountHandler> logger) : IRequestHandler<GetPlayerReportsCountQuery, int>
{
    public async Task<int> Handle(GetPlayerReportsCountQuery request, CancellationToken cancellationToken)
    {
        logger.LogInformation(
            "[GetPlayerReportsCountHandler] Querying reports count (Status={Status}, Reason={Reason}, Search={Search})",
            request.Status,
            request.Reason,
            request.Search);

        IQueryable<PlayerReport> query = db.PlayerReports.AsNoTracking();

        if (request.ReportId.HasValue && request.ReportId.Value > 0)
        {
            query = query.Where(r => r.ID == request.ReportId.Value);
        }
        else
        {
            if (request.MatchId.HasValue && request.MatchId.Value > 0)
                query = query.Where(r => r.MatchID == request.MatchId.Value);

            if (!string.IsNullOrWhiteSpace(request.Status) && request.Status != "all")
                query = query.Where(r => r.Status == request.Status);

            if (!string.IsNullOrWhiteSpace(request.Reason) && request.Reason != "all")
                query = query.Where(r => r.Reason == request.Reason);

            if (!string.IsNullOrWhiteSpace(request.Search))
            {
                string s = request.Search.Trim();
                int? parsedId = int.TryParse(s.TrimStart('#'), out int pid) ? pid : null;
                query = query.Where(r =>
                    (parsedId.HasValue && r.ID == parsedId.Value) ||
                    r.ReportedAccountName.Contains(s) ||
                    r.ReporterAccountName.Contains(s) ||
                    (r.Description != null && r.Description.Contains(s)));
            }
        }

        return await query.CountAsync(cancellationToken);
    }
}
