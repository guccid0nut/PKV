// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using NEXUS.Contracts.Clients;

namespace NEXUS.ControlPanel.GUI.Services;

/// <summary>
/// Control Panel moderation service wiring operator triage in <c>NEXUS.ControlPanel.GUI</c>
/// to <c>PlayerReports</c> and account sanctions (Mute / Ban / Dismiss) via WebPortal API or Direct DB.
/// </summary>
public interface IModerationQueueService
{
    Task<List<PlayerReportDto>> GetReportsAsync(
        string? status = "PENDING",
        string? search = null,
        int page = 1,
        int pageSize = 50,
        CancellationToken cancellationToken = default);

    Task<bool> ActionReportAsync(
        int reportId,
        string actionType,
        int? durationHours,
        string reason,
        string? staffNotes = null,
        int staffAccountId = 1,
        CancellationToken cancellationToken = default);

    Task<bool> DismissReportAsync(
        int reportId,
        int staffAccountId = 1,
        string? comment = null,
        CancellationToken cancellationToken = default);
}
