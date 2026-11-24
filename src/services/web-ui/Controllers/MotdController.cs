// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using DAWNBRINGER.WebPortal.UI.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DAWNBRINGER.WebPortal.UI.Controllers;

[ApiController]
[AllowAnonymous]
public class MotdController(IAnnouncementService announcementService, ILogger<MotdController> logger) : ControllerBase
{
    private readonly IAnnouncementService _announcementService = announcementService;
    private readonly ILogger<MotdController> _logger = logger;

    [HttpGet("api/motd/feed")]
    [HttpGet("motd/feed.json")]
    public IActionResult GetFeed()
    {
        IReadOnlyList<AnnouncementItemDto> feed = _announcementService.GetActiveFeed();
        _logger.LogInformation("[MotdController] Serving active MOTD feed with {Count} items", feed.Count);
        return Ok(feed);
    }

    [HttpGet("api/motd/banner")]
    public IActionResult GetTopBanner()
    {
        AnnouncementItemDto? banner = _announcementService.GetActiveFeed().FirstOrDefault();
        if (banner == null) return NotFound(new { message = "No active banner available." });
        return Ok(banner);
    }
}
