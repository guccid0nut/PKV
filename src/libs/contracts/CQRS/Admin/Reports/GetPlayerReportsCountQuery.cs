// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using MediatR;

namespace NEXUS.Contracts.CQRS.Admin.Reports;

/// <summary>
///     Queries total count of player reports matching the specified filter criteria.
/// </summary>
public sealed record GetPlayerReportsCountQuery(
    string? Status = null,
    string? Reason = null,
    string? Search = null,
    int? MatchId = null,
    int? ReportId = null) : IRequest<int>;
