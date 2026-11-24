// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

namespace DAWNBRINGER.WebPortal.UI.Models;

public class StaffReportsViewModel
{
    public List<PlayerReportDto> Reports { get; set; } = [];

    public int TotalReports { get; set; }

    public int PendingReports { get; set; }

    public int InReviewReports { get; set; }

    public int ResolvedReports { get; set; }

    public int DismissedReports { get; set; }

    public int AppealedReports { get; set; }

    public string? StatusFilter { get; set; }

    public string? SearchQuery { get; set; }

    public string? ReasonFilter { get; set; }

    public int? MatchIdFilter { get; set; }

    public int? ReportIdFilter { get; set; }

    public List<string> AvailableReasons { get; set; } = [];

    public int PageNumber { get; set; } = 1;

    public int PageSize { get; set; } = 25;

    public int TotalCount { get; set; }

    public int TotalPages => (int)Math.Ceiling((double)TotalCount / Math.Max(1, PageSize));
}
