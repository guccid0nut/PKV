// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

namespace DAWNBRINGER.WebPortal.UI.Models;

public sealed class AnnouncementItemDto
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Title { get; set; } = string.Empty;
    public string Category { get; set; } = "PATCH NOTES";
    public string BadgeColor { get; set; } = "#00e5ff";
    public string Date { get; set; } = TimeProvider.System.GetUtcNow().UtcDateTime.ToString("MMM dd, yyyy");
    public string Description { get; set; } = string.Empty;
    public string ImageUrl { get; set; } = "/ui/fe2/mainmenu/bg3_new.tga";
    public string ActionType { get; set; } = "url";
    public string ActionTarget { get; set; } = "https://discord.gg/nexus";
    public string? VideoUrl { get; set; }
    public bool IsActive { get; set; } = true;
    public int Priority { get; set; } = 100;
    public DateTime CreatedAt { get; set; } = TimeProvider.System.GetUtcNow().UtcDateTime;
    public DateTime? UpdatedAt { get; set; }
}

public sealed class StaffAnnouncementsViewModel
{
    public IReadOnlyList<AnnouncementItemDto> Announcements { get; set; } = [];
    public string? SuccessMessage { get; set; }
    public string? ErrorMessage { get; set; }
}
