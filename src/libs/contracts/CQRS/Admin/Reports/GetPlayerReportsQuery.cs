// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using MediatR;
using NEXUS.Contracts.Clients;

namespace NEXUS.Contracts.CQRS.Admin.Reports;

/// <summary>
///     Queries player reports with filtering and pagination. Intentionally un-cached for real-time moderator queue consistency.
/// </summary>
public sealed record GetPlayerReportsQuery(
    string? Status = null,
    string? Reason = null,
    string? Search = null,
    int? MatchId = null,
    int? ReportId = null,
    int Page = 1,
    int PageSize = 25) : IRequest<IReadOnlyList<PlayerReportDto>>;
