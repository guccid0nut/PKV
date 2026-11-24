// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Collections.Generic;
using System.Threading.Tasks;
using DAWNBRINGER.WebPortal.UI.Models.DeveloperIssues;

namespace DAWNBRINGER.WebPortal.UI.Hubs;

/// <summary>
/// Strongly-typed SignalR client interface for real-time developer issues and self-healing telemetry.
/// </summary>
public interface IDeveloperIssuesClient
{
    Task OnIssueIngested(DeveloperIssueItemDto issue);
    Task OnIssueUpdated(DeveloperIssueItemDto issue);
    Task OnKpisUpdated(DeveloperIssuesKpiDto kpis);
    Task OnReflexResult(ReflexTriggerResultDto result);
    Task OnActivityRecorded(DeveloperIssueActivityDto activity);
    Task OnInitialState(DeveloperIssuesDashboardViewModel state);
}
