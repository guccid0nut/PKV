// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

namespace DAWNBRINGER.WebPortal.UI.Models;

public class StaffHostsViewModel
{
    public List<StaffHostApplicationDto> PendingApplications { get; set; } = [];
    public List<StaffHostApplicationDto> ApprovedHosts { get; set; } = [];
    public List<StaffHostApplicationDto> RejectedApplications { get; set; } = [];
    public List<StaffHostApplicationDto> RevokedHosts { get; set; } = [];

    public int TotalApplications => PendingApplications.Count + ApprovedHosts.Count + RejectedApplications.Count +
                                    RevokedHosts.Count;

    public int ActiveHostCount => ApprovedHosts.Count;
    public int TotalHostedMatches { get; set; }

    public string? StatusFilter { get; set; }
    public string? SearchQuery { get; set; }
}
