// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using ASPIRE.Common.DTOs;

namespace DAWNBRINGER.WebPortal.UI.Models;

public class StaffInvitesViewModel
{
    public InviteSystemStatusDto Status { get; set; } = new(false, 0, 0, []);
    public InviteTreeNodeDto? LineageTree { get; set; }
    public string? SearchQuery { get; set; }
    public string? SuccessMessage { get; set; }
    public string? ErrorMessage { get; set; }
}
