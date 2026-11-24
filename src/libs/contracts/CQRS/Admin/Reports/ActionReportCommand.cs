// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using MediatR;

namespace NEXUS.Contracts.CQRS.Admin.Reports;

/// <summary>
///     Actions a player report with sanction application and concurrency protection via <see cref="ILockableRequest"/>.
/// </summary>
public sealed record ActionReportCommand(
    int ReportId,
    string ActionType,
    int? DurationHours,
    string? Reason,
    string? StaffNotes,
    int StaffAccountId) : IRequest<AdminActionResultDto>, ILockableRequest
{
    public string LockKey => $"lock:report:{ReportId}";
    public TimeSpan LockExpiry => TimeSpan.FromSeconds(15);
    public TimeSpan LockTimeout => TimeSpan.FromSeconds(5);
}
