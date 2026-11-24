// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ============================================================================
// File: DuckDbAnalyticsApiController.cs
// System: ZORGATH Web Portal API - DuckDB Analytics Lakehouse Controller
// Policy: Strict Explicit Typing (NO 'var')
// ============================================================================

using MERRICK.DatabaseContext.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ZORGATH.WebPortal.API.Controllers;

/// <summary>
/// REST API controller providing high-performance analytical queries and combat breakdowns powered by the DuckDB Lakehouse.
/// </summary>
[ApiController]
[Route("api/lakehouse")]
[Produces("application/json")]
[EnableRateLimiting(RateLimiterPolicies.Relaxed)]
public sealed class DuckDbAnalyticsApiController(
    IDuckDbAnalyticsService analyticsService,
    ILogger<DuckDbAnalyticsApiController>? logger = null) : ControllerBase
{
    private readonly IDuckDbAnalyticsService _analyticsService = analyticsService;
    private readonly ILogger<DuckDbAnalyticsApiController> _logger = logger ?? NullLogger<DuckDbAnalyticsApiController>.Instance;

    /// <summary>
    /// Retrieves the full 10-player combat performance breakdown for a given match from the DuckDB Lakehouse.
    /// </summary>
    /// <param name="matchId">The unique 64-bit integer identifier of the match.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A list of player combat breakdowns for the match.</returns>
    /// <response code="200">Match combat breakdown retrieved successfully.</response>
    /// <response code="400">Match ID is invalid.</response>
    [HttpGet("matches/{matchId:long}/breakdown")]
    [ProducesResponseType(typeof(IReadOnlyList<MatchCombatPlayerBreakdownDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(string), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> GetMatchBreakdown(long matchId, CancellationToken ct = default)
    {
        _logger.LogInformation("[DuckDbAnalyticsApiController.GetMatchBreakdown] Requested breakdown for Match #{MatchId}", matchId);
        if (matchId <= 0)
        {
            return BadRequest("Invalid match ID.");
        }

        IReadOnlyList<MatchCombatPlayerBreakdownDto> results =
            await _analyticsService.GetMatchCombatBreakdownAsync(matchId, ct);

        return Ok(results);
    }

    /// <summary>
    /// Retrieves the career or match-specific combat summary for a player from the DuckDB Lakehouse.
    /// </summary>
    /// <param name="accountId">The unique integer identifier of the player account.</param>
    /// <param name="matchId">Optional match ID filter to retrieve stats for a specific match.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Combat summary DTO for the player.</returns>
    /// <response code="200">Player combat summary retrieved successfully.</response>
    /// <response code="400">Account ID is invalid.</response>
    /// <response code="404">No lakehouse records found for the account.</response>
    [HttpGet("players/{accountId:int}/combat-summary")]
    [ProducesResponseType(typeof(PlayerCombatSummaryDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(string), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(string), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetPlayerCombatSummary(
        int accountId,
        [FromQuery] long? matchId = null,
        CancellationToken ct = default)
    {
        _logger.LogInformation(
            "[DuckDbAnalyticsApiController.GetPlayerCombatSummary] Requested summary for Account #{AccountId} (Match: {MatchId})",
            accountId, matchId.HasValue ? matchId.Value.ToString() : "ALL");

        if (accountId <= 0)
        {
            return BadRequest("Invalid account ID.");
        }

        PlayerCombatSummaryDto? summary =
            await _analyticsService.GetPlayerCombatSummaryAsync(accountId, matchId, ct);

        if (summary == null)
        {
            return NotFound($"No lakehouse combat records found for Account #{accountId}.");
        }

        return Ok(summary);
    }

    /// <summary>
    /// Retrieves head-to-head combat and matchup statistics between two heroes.
    /// </summary>
    /// <param name="heroId">Primary hero identifier.</param>
    /// <param name="enemyHeroId">Opponent hero identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Matchup win rate and combat statistics between the two heroes.</returns>
    /// <response code="200">Hero matchup statistics retrieved successfully.</response>
    [HttpGet("heroes/{heroId:int}/matchup/{enemyHeroId:int}")]
    [ProducesResponseType(typeof(HeroMatchupWinrateDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetHeroMatchup(short heroId, short enemyHeroId, CancellationToken ct = default)
    {
        _logger.LogInformation(
            "[DuckDbAnalyticsApiController.GetHeroMatchup] Requested matchup: Hero {HeroId} vs Enemy {EnemyHeroId}",
            heroId, enemyHeroId);

        HeroMatchupWinrateDto? matchup =
            await _analyticsService.GetHeroMatchupAsync(heroId, enemyHeroId, ct);

        return Ok(matchup);
    }

    /// <summary>
    /// Retrieves spatial heatmap coordinate clusters for kills, deaths, and wards for 2D minimap canvas rendering.
    /// </summary>
    /// <param name="matchId">The unique 64-bit integer identifier of the match.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A collection of combat heatmap coordinates.</returns>
    /// <response code="200">Combat heatmap data points retrieved successfully.</response>
    /// <response code="400">Match ID is invalid.</response>
    [HttpGet("matches/{matchId:long}/heatmap")]
    [HttpGet("/api/analytics/match/{matchId:long}/heatmap")]
    [ProducesResponseType(typeof(IReadOnlyList<CombatHeatmapPointDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(string), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> GetMatchHeatmap(long matchId, CancellationToken ct = default)
    {
        _logger.LogInformation("[DuckDbAnalyticsApiController.GetMatchHeatmap] Requested heatmap for Match #{MatchId}", matchId);
        if (matchId <= 0)
        {
            return BadRequest("Invalid match ID.");
        }

        IReadOnlyList<CombatHeatmapPointDto> points =
            await _analyticsService.GetCombatHeatmapAsync(matchId, ct);

        return Ok(points);
    }

    /// <summary>
    /// Retrieves the full hero matchup performance matrix against all opponent heroes.
    /// </summary>
    /// <param name="heroId">Primary hero identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Matrix list of win rates and kill ratios against all opponent heroes.</returns>
    /// <response code="200">Hero matchup matrix retrieved successfully.</response>
    /// <response code="400">Hero ID is invalid.</response>
    [HttpGet("heroes/{heroId:int}/matchups")]
    [HttpGet("/api/analytics/heroes/{heroId:int}/matchups")]
    [ProducesResponseType(typeof(IReadOnlyList<HeroMatchupMatrixEntryDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(string), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> GetHeroMatchupMatrix(short heroId, CancellationToken ct = default)
    {
        _logger.LogInformation("[DuckDbAnalyticsApiController.GetHeroMatchupMatrix] Requested matchup matrix for Hero #{HeroId}", heroId);
        if (heroId <= 0)
        {
            return BadRequest("Invalid hero ID.");
        }

        IReadOnlyList<HeroMatchupMatrixEntryDto> matrix =
            await _analyticsService.GetHeroMatchupMatrixAsync(heroId, ct);

        return Ok(matrix);
    }
}
