// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

namespace ASPIRE.Common.DTOs;

public class ApplyStreamerRequestDto
{
    public string TwitchUsername { get; set; } = string.Empty;
    public string? TwitchId { get; set; }
    public string? TwitchChannelUrl { get; set; }
    public string? ApplicationNotes { get; set; }
}

public class LinkTwitchRequestDto
{
    public string TwitchUsername { get; set; } = string.Empty;
    public string? TwitchId { get; set; }
}

public class StaffStreamerApplicationDto
{
    public int Id { get; set; }
    public int UserId { get; set; }
    public required string MainAccountName { get; set; }
    public required string TwitchUsername { get; set; }
    public string? TwitchId { get; set; }
    public string? TwitchChannelUrl { get; set; }
    public required string Status { get; set; }
    public string? ApplicationNotes { get; set; }
    public string? StaffNotes { get; set; }
    public string? ReviewedByStaffName { get; set; }
    public DateTimeOffset TimestampApplied { get; set; }
    public DateTimeOffset? TimestampReviewed { get; set; }
    public int TotalMatchesPlayed { get; set; }
    public int DisciplinaryReportCount { get; set; }
    public bool IsStreamerAccount { get; set; }
}

public class StaffStreamersViewModel
{
    public List<StaffStreamerApplicationDto> PendingApplications { get; set; } = [];
    public List<StaffStreamerApplicationDto> ApprovedStreamers { get; set; } = [];
    public List<StaffStreamerApplicationDto> RejectedApplications { get; set; } = [];
    public List<StaffStreamerApplicationDto> RevokedStreamers { get; set; } = [];

    public int TotalApplications => PendingApplications.Count + ApprovedStreamers.Count + RejectedApplications.Count +
                                    RevokedStreamers.Count;

    public int ActiveStreamerCount => ApprovedStreamers.Count;
}
