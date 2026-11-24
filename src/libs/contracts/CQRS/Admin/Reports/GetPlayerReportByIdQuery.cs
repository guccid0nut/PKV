// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using MediatR;
using NEXUS.Contracts.Clients;

namespace NEXUS.Contracts.CQRS.Admin.Reports;

/// <summary>
///     Query to retrieve a single player report by its unique report ID.
/// </summary>
/// <param name="ReportId">The unique numeric report ID.</param>
public sealed record GetPlayerReportByIdQuery(int ReportId) : IRequest<PlayerReportDto?>;
