// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System;
using System.Collections.Generic;

namespace DAWNBRINGER.WebPortal.UI.Models.DeveloperIssues;

/// <summary>
/// Data transfer object for an individual developer issue in the dashboard.
/// </summary>
public sealed record DeveloperIssueItemDto
{
    public required string Id { get; init; }
    public required string Fingerprint { get; init; }
    public required string Subsystem { get; init; }
    public required string Severity { get; init; }
    public required string Status { get; init; }
    public required string Title { get; init; }
    public required string RawMessage { get; init; }
    public required string NormalizedSignature { get; init; }
    public string? StackTrace { get; init; }
    public string? FaultingModule { get; init; }
    public string? FaultingAddress { get; init; }
    public int OccurrenceCount { get; init; } = 1;
    public DateTimeOffset FirstSeenUtc { get; init; }
    public DateTimeOffset LastSeenUtc { get; init; }
    public string? ResolutionRecipeApplied { get; init; }
    public string? ResolutionReceipt { get; init; }
    public string? AssociatedIssueTrackerId { get; init; }
}

/// <summary>
/// Top-level KPI metrics for the developer issues telemetry dashboard.
/// </summary>
public sealed record DeveloperIssuesKpiDto
{
    public int TotalCaught { get; init; }
    public int AutoResolvedCount { get; init; }
    public int ActionRequiredCount { get; init; }
    public int RegressionCount { get; init; }
    public int OpenCount { get; init; }
    public string SystemHealth { get; init; } = "HEALTHY";
    public double AutoResolvedPercent => TotalCaught > 0 ? Math.Round((double)AutoResolvedCount / TotalCaught * 100.0, 1) : 100.0;
}

/// <summary>
/// Chronological activity item in the live receipt stream.
/// </summary>
public sealed record DeveloperIssueActivityDto
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N")[..8];
    public required string Fingerprint { get; init; }
    public required string Action { get; init; }
    public required string Recipe { get; init; }
    public required string Receipt { get; init; }
    public DateTimeOffset Timestamp { get; init; } = TimeProvider.System.GetUtcNow();
}

/// <summary>
/// Complete view model powering the /admin/issues and /System/Issues dashboard page.
/// </summary>
public sealed class DeveloperIssuesDashboardViewModel
{
    public DeveloperIssuesKpiDto Kpis { get; set; } = new();
    public List<DeveloperIssueItemDto> Issues { get; set; } = [];
    public List<DeveloperIssueActivityDto> RecentActivities { get; set; } = [];
    public string? SelectedSubsystem { get; set; }
    public string? SelectedSeverity { get; set; }
    public string? SelectedStatus { get; set; }
    public string? SearchQuery { get; set; }
    public bool IsDatabaseConnected { get; set; }
    public DateTimeOffset LastRefreshedUtc { get; set; } = TimeProvider.System.GetUtcNow();
}

/// <summary>
/// Result of an automated or manual reflex healing trigger.
/// </summary>
public sealed record ReflexTriggerResultDto
{
    public bool Success { get; init; }
    public bool Healed { get; init; }
    public required string Fingerprint { get; init; }
    public required string RecipeName { get; init; }
    public required string Receipt { get; init; }
    public required string NewStatus { get; init; }
    public string? SuggestedDownstreamTool { get; init; }
}
