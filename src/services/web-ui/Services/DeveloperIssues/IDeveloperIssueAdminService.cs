// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using DAWNBRINGER.WebPortal.UI.Models.DeveloperIssues;

namespace DAWNBRINGER.WebPortal.UI.Services.DeveloperIssues;

/// <summary>
/// Service interface for querying and administering developer issues, telemetry KPIs,
/// and executing automated self-healing reflexes.
/// </summary>
public interface IDeveloperIssueAdminService
{
    /// <summary>
    /// Gets the complete dashboard view model with filtered issues, KPIs, and recent activity.
    /// </summary>
    Task<DeveloperIssuesDashboardViewModel> GetDashboardViewModelAsync(
        string? subsystem = null,
        string? severity = null,
        string? status = null,
        string? search = null,
        CancellationToken ct = default);

    /// <summary>
    /// Computes and returns the high-level KPI metrics across all tracked issues.
    /// </summary>
    Task<DeveloperIssuesKpiDto> GetKpisAsync(CancellationToken ct = default);

    /// <summary>
    /// Gets filtered issues matching the specified criteria.
    /// </summary>
    Task<IReadOnlyList<DeveloperIssueItemDto>> GetIssuesAsync(
        string? subsystem = null,
        string? severity = null,
        string? status = null,
        string? search = null,
        int limit = 100,
        CancellationToken ct = default);

    /// <summary>
    /// Retrieves a single issue by its unique deduplication fingerprint.
    /// </summary>
    Task<DeveloperIssueItemDto?> GetIssueByFingerprintAsync(string fingerprint, CancellationToken ct = default);

    /// <summary>
    /// Executes a deterministic reflex self-healing recipe against the targeted issue fingerprint.
    /// </summary>
    Task<ReflexTriggerResultDto> TriggerReflexHealAsync(string fingerprint, CancellationToken ct = default);

    /// <summary>
    /// Manually marks an issue as resolved with developer resolution notes.
    /// </summary>
    Task<bool> ResolveIssueManuallyAsync(string fingerprint, string notes, CancellationToken ct = default);

    /// <summary>
    /// Ingests a new issue or increments occurrence of an existing one, broadcasting updates over SignalR.
    /// </summary>
    Task IngestOrUpdateIssueAsync(DeveloperIssueItemDto issue, CancellationToken ct = default);
}
