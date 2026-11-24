// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ============================================================================
// File: LearnatoriumController.cs
// System: DAWNBRINGER Web Portal UI - Learnatorium Replay Playback & Compendium Controller
// Policy: Strict Explicit Typing (NO 'var')
// ============================================================================

using DAWNBRINGER.WebPortal.UI.Services.ApiClient;
using Microsoft.AspNetCore.Mvc;
using NEXUS.Contracts.Learnatorium;

namespace DAWNBRINGER.WebPortal.UI.Controllers;

public sealed class LearnatoriumController(
    ILearnatoriumApiClient learnatoriumClient,
    ILogger<LearnatoriumController> logger) : Controller
{
    private readonly ILearnatoriumApiClient _learnatoriumClient = learnatoriumClient;
    private readonly ILogger<LearnatoriumController> _logger = logger;

    [HttpGet("learnatorium")]
    public IActionResult Index()
    {
        // Redirect to featured canonical match replay
        return RedirectToAction(nameof(Playback), new { matchId = 15984210 });
    }

    [HttpGet("learnatorium/playback/{matchId:int}")]
    public async Task<IActionResult> Playback(int matchId, CancellationToken ct = default)
    {
        _logger.LogInformation("[LearnatoriumController] Rendering 2D minimap playback for Match #{MatchId}", matchId);
        if (matchId <= 0)
        {
            matchId = 15984210;
        }

        LearnatoriumReplayPlaybackDto? playback = await _learnatoriumClient.GetReplayPlaybackAsync(matchId, ct);
        if (playback == null)
        {
            return NotFound("Playback data unavailable for this match.");
        }

        return View("~/Views/Home/LearnatoriumPlayback.cshtml", playback);
    }

    [HttpGet("api/learnatorium/playback/{matchId:int}")]
    public async Task<IActionResult> GetPlaybackJson(int matchId, CancellationToken ct = default)
    {
        if (matchId <= 0) matchId = 15984210;
        LearnatoriumReplayPlaybackDto? playback = await _learnatoriumClient.GetReplayPlaybackAsync(matchId, ct);
        return playback != null ? Json(playback) : NotFound();
    }

    [HttpGet("api/learnatorium/guide/{heroIdentifier}")]
    public async Task<IActionResult> GetHeroGuideJson(string heroIdentifier, CancellationToken ct = default)
    {
        LearnatoriumHeroGuideDto? guide = await _learnatoriumClient.GetHeroGuideAsync(heroIdentifier, ct);
        return guide != null ? Json(guide) : NotFound();
    }
}
