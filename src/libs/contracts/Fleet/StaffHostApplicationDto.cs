// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

namespace NEXUS.Contracts.Fleet;

/// <summary>
///     Detailed staff review model for evaluating community game server host applications.
/// </summary>
public sealed class StaffHostApplicationDto
{
    public int Id { get; set; }
    public int UserId { get; set; }
    public required string MainAccountName { get; set; }
    public required string HostUsername { get; set; }
    public required string Status { get; set; }
    public string Location { get; set; } = "USE";
    public int Instances { get; set; } = 2;
    public string ServerNamePrefix { get; set; } = "Community Server";
    public string? ApplicationNotes { get; set; }
    public string? StaffNotes { get; set; }
    public string? ReviewedByStaffName { get; set; }
    public DateTimeOffset TimestampApplied { get; set; }
    public DateTimeOffset? TimestampReviewed { get; set; }
    public int TotalMatchesPlayed { get; set; }
    public int DisciplinaryReportCount { get; set; }
    public bool HasHostAccount { get; set; }
}
