// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ============================================================================
// File: LearnatoriumApiController.cs
// System: ZORGATH Web Portal API - Learnatorium Playback & Compendium Controller
// Policy: Strict Explicit Typing (NO 'var')
// ============================================================================

using NEXUS.Contracts.Learnatorium;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ZORGATH.WebPortal.API.Services.Learnatorium;

namespace ZORGATH.WebPortal.API.Controllers;

/// <summary>
/// REST API controller providing Learnatorium hero guides, 2D replay position playback keyframes, and combat event logs.
/// </summary>
[ApiController]
[Route("api/learnatorium")]
[Produces("application/json")]
[EnableRateLimiting(RateLimiterPolicies.Relaxed)]
public sealed class LearnatoriumApiController(
    ILearnatoriumService learnatoriumService,
    ILogger<LearnatoriumApiController>? logger = null) : ControllerBase
{
    private readonly ILearnatoriumService _learnatoriumService = learnatoriumService;
    private readonly ILogger<LearnatoriumApiController> _logger = logger ?? NullLogger<LearnatoriumApiController>.Instance;

    /// <summary>
    /// Retrieves dynamic skill progression and item build guide for a specific hero.
    /// </summary>
    /// <param name="heroIdentifier">Hero code identifier (e.g., "Hero_Pyromancer") or name.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Hero guide DTO containing abilities, item recommendations, and leveling order.</returns>
    /// <response code="200">Hero guide retrieved successfully.</response>
    /// <response code="400">Hero identifier is empty or invalid.</response>
    /// <response code="404">Hero guide was not found for the specified hero.</response>
    [HttpGet("heroes/{heroIdentifier}/guide")]
    [ProducesResponseType(typeof(LearnatoriumHeroGuideDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(string), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(string), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetHeroGuide(string heroIdentifier, CancellationToken ct = default)
    {
        _logger.LogInformation("[LearnatoriumApiController.GetHeroGuide] Guide requested for {Hero}", heroIdentifier);
        if (string.IsNullOrWhiteSpace(heroIdentifier))
        {
            return BadRequest("Hero identifier must be specified.");
        }

        LearnatoriumHeroGuideDto? guide = await _learnatoriumService.GetHeroGuideAsync(heroIdentifier, ct);
        if (guide == null)
        {
            return NotFound($"No guide found for hero '{heroIdentifier}'.");
        }

        return Ok(guide);
    }

    /// <summary>
    /// Retrieves complete 2D minimap coordinate playback keyframes for the browser replay scrubber.
    /// </summary>
    /// <param name="matchId">The unique integer identifier of the match.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Replay playback DTO containing timestamped coordinate keyframes and hero positions.</returns>
    /// <response code="200">Replay playback keyframes retrieved successfully.</response>
    /// <response code="400">Match ID is invalid.</response>
    /// <response code="404">Replay playback data not found for the given match ID.</response>
    [HttpGet("matches/{matchId:int}/playback")]
    [ProducesResponseType(typeof(LearnatoriumReplayPlaybackDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(string), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(string), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetReplayPlayback(int matchId, CancellationToken ct = default)
    {
        _logger.LogInformation("[LearnatoriumApiController.GetReplayPlayback] Replay playback requested for Match #{MatchId}", matchId);
        if (matchId <= 0)
        {
            return BadRequest("Invalid match ID.");
        }

        LearnatoriumReplayPlaybackDto? playback = await _learnatoriumService.GetReplayPlaybackAsync(matchId, ct);
        if (playback == null)
        {
            return NotFound($"No replay playback data found for Match #{matchId}.");
        }

        return Ok(playback);
    }

    /// <summary>
    /// Retrieves chronological combat events and highlights for a match.
    /// </summary>
    /// <param name="matchId">The unique integer identifier of the match.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>List of chronological combat highlights including kills, deaths, and objective captures.</returns>
    /// <response code="200">Combat highlights log retrieved successfully.</response>
    /// <response code="400">Match ID is invalid.</response>
    [HttpGet("matches/{matchId:int}/combat-log")]
    [ProducesResponseType(typeof(IReadOnlyList<PlaybackCombatHighlightDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(string), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> GetCombatLog(int matchId, CancellationToken ct = default)
    {
        _logger.LogInformation("[LearnatoriumApiController.GetCombatLog] Combat log requested for Match #{MatchId}", matchId);
        if (matchId <= 0)
        {
            return BadRequest("Invalid match ID.");
        }

        IReadOnlyList<PlaybackCombatHighlightDto> highlights = await _learnatoriumService.GetCombatLogAsync(matchId, ct);
        return Ok(highlights);
    }
}
