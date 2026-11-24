// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using MediatR;

namespace NEXUS.Contracts.CQRS.Admin.Reports;

/// <summary>
///     Dismisses / appeals a player report with concurrency protection via <see cref="ILockableRequest"/>.
/// </summary>
public sealed record DismissReportCommand(
    int ReportId,
    int StaffAccountId,
    string? Comment) : IRequest<AdminActionResultDto>, ILockableRequest
{
    public string LockKey => $"lock:report:{ReportId}";
    public TimeSpan LockExpiry => TimeSpan.FromSeconds(15);
    public TimeSpan LockTimeout => TimeSpan.FromSeconds(5);
}
