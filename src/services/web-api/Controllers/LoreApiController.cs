// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using MediatR;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Logging.Abstractions;
using NEXUS.Contracts.Clients;
using NEXUS.Contracts.CQRS.Lore;
using ZORGATH.WebPortal.API.Constants;
using ZORGATH.WebPortal.API.Features.Lore;
using ZORGATH.WebPortal.API.Infrastructure.Attributes;

namespace ZORGATH.WebPortal.API.Controllers;

/// <summary>
/// REST API controller providing Newerth lore story arcs, digital comic issues, page readers, community star ratings, and publishing operations.
/// </summary>
[ApiController]
[Route("api/lore")]
[Produces("application/json")]
[EnableRateLimiting(RateLimiterPolicies.Relaxed)]
public class LoreApiController(
    IMediator mediator,
    ILogger<LoreApiController>? logger = null) : ControllerBase
{
    private readonly IMediator _mediator = mediator ?? throw new ArgumentNullException(nameof(mediator));
    private readonly ILogger<LoreApiController> _logger = logger ?? NullLogger<LoreApiController>.Instance;

    private bool HasTrustedInternalServiceKey() =>
        HttpContext != null && RateLimiterPolicies.IsValidInternalServiceKey(HttpContext);

    /// <summary>
    /// Retrieves all story arcs with their nested comic issue summaries.
    /// </summary>
    /// <param name="includeUnpublished">Whether to include unpublished draft arcs (staff mode).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>List of lore arc DTOs.</returns>
    /// <response code="200">Story arcs retrieved successfully.</response>
    [HttpGet("arcs")]
    [ProducesResponseType(typeof(List<LoreArcDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetArcs([FromQuery] bool includeUnpublished = false, CancellationToken ct = default)
    {
        bool effectiveIncludeUnpublished = includeUnpublished && HasTrustedInternalServiceKey();
        if (includeUnpublished && !effectiveIncludeUnpublished)
        {
            _logger.LogWarning(
                "[SECURITY-ALERT] [LoreApiController] Rejected spoofed includeUnpublished=true on GetArcs without valid X-Internal-Service-Key (IP={RemoteIp})",
                HttpContext?.Connection?.RemoteIpAddress);
        }

        var result = await _mediator.Send(new GetArcsQuery(effectiveIncludeUnpublished), ct);
        return Ok(result);
    }

    /// <summary>
    /// Retrieves a single story arc by its unique URL slug.
    /// </summary>
    /// <param name="slug">The URL-friendly slug identifier of the story arc.</param>
    /// <param name="includeUnpublished">Whether to include unpublished arcs.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Story arc details DTO if found; otherwise, 404 NotFound.</returns>
    /// <response code="200">Story arc retrieved successfully.</response>
    /// <response code="404">Story arc was not found.</response>
    [HttpGet("arcs/{slug}")]
    [ProducesResponseType(typeof(LoreArcDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetArcBySlug(string slug, [FromQuery] bool includeUnpublished = false, CancellationToken ct = default)
    {
        bool effectiveIncludeUnpublished = includeUnpublished && HasTrustedInternalServiceKey();
        if (includeUnpublished && !effectiveIncludeUnpublished)
        {
            _logger.LogWarning(
                "[SECURITY-ALERT] [LoreApiController] Rejected spoofed includeUnpublished=true on GetArcBySlug('{Slug}') without valid X-Internal-Service-Key (IP={RemoteIp})",
                slug,
                HttpContext?.Connection?.RemoteIpAddress);
        }

        var result = await _mediator.Send(new GetArcBySlugQuery(slug, effectiveIncludeUnpublished), ct);
        return result != null ? Ok(result) : NotFound();
    }

    /// <summary>
    /// Retrieves summary listings of comic issues, optionally filtered by story arc.
    /// </summary>
    /// <param name="arcId">Optional story arc ID filter.</param>
    /// <param name="includeUnpublished">Whether to include unpublished issues.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>List of comic summary DTOs.</returns>
    /// <response code="200">Comic summaries retrieved successfully.</response>
    [HttpGet("comics")]
    [ProducesResponseType(typeof(List<LoreComicSummaryDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetComics([FromQuery] int? arcId = null, [FromQuery] bool includeUnpublished = false, CancellationToken ct = default)
    {
        bool effectiveIncludeUnpublished = includeUnpublished && HasTrustedInternalServiceKey();
        if (includeUnpublished && !effectiveIncludeUnpublished)
        {
            _logger.LogWarning(
                "[SECURITY-ALERT] [LoreApiController] Rejected spoofed includeUnpublished=true on GetComics without valid X-Internal-Service-Key (IP={RemoteIp})",
                HttpContext?.Connection?.RemoteIpAddress);
        }

        var result = await _mediator.Send(new GetComicsQuery(arcId, effectiveIncludeUnpublished), ct);
        return Ok(result);
    }

    /// <summary>
    /// Retrieves detailed comic issue data including full page image URLs, credits, hotspots, and user rating.
    /// </summary>
    /// <param name="idOrSlug">Numeric comic ID or unique URL slug.</param>
    /// <param name="currentAccountId">Optional requesting account ID to include existing user score.</param>
    /// <param name="isStaff">Staff flag to access unpublished content and internal rating metrics.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Comic detail DTO if found; otherwise, 404 NotFound.</returns>
    /// <response code="200">Comic details retrieved successfully.</response>
    /// <response code="404">Comic was not found.</response>
    [HttpGet("comics/{idOrSlug}")]
    [ProducesResponseType(typeof(LoreComicDetailDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetComic(string idOrSlug, [FromQuery] int? currentAccountId = null, [FromQuery] bool isStaff = false, CancellationToken ct = default)
    {
        bool effectiveIsStaff = isStaff && HasTrustedInternalServiceKey();
        if (isStaff && !effectiveIsStaff)
        {
            _logger.LogWarning(
                "[SECURITY-ALERT] [LoreApiController] Rejected spoofed isStaff=true on GetComic('{IdOrSlug}') without valid X-Internal-Service-Key (IP={RemoteIp})",
                idOrSlug,
                HttpContext?.Connection?.RemoteIpAddress);
        }

        var result = await _mediator.Send(new GetComicDetailQuery(idOrSlug, currentAccountId, effectiveIsStaff), ct);
        return result != null ? Ok(result) : NotFound();
    }

    /// <summary>
    /// Submits or updates a 1-5 star user rating for a comic issue.
    /// </summary>
    /// <param name="id">The unique integer identifier of the comic.</param>
    /// <param name="request">Rating request containing account ID and score (1 to 5).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Success response with recorded score.</returns>
    /// <response code="200">Rating recorded successfully.</response>
    /// <response code="400">Score was out of range (1-5) or match count requirement was not met.</response>
    /// <response code="401">Valid account required to submit a rating.</response>
    /// <response code="404">Comic was not found.</response>
    [HttpPost("comics/{id:int}/rate")]
    [RequireInternalServiceKey]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(string), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(string), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(string), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> RateComic(int id, [FromBody] RateComicRequestDto request, CancellationToken ct = default)
    {
        var result = await _mediator.Send(new RateComicCommand(id, request.AccountID, request.Score), ct);
        return result.StatusCode switch
        {
            200 => Ok(new { Success = true, request.Score }),
            400 => BadRequest(result.Message),
            401 => Unauthorized(result.Message),
            404 => NotFound(result.Message),
            _ => StatusCode(result.StatusCode, result.Message)
        };
    }

    /// <summary>
    /// Retrieves a specific player's previously submitted star rating for a comic issue.
    /// </summary>
    /// <param name="id">The unique integer identifier of the comic.</param>
    /// <param name="accountId">Account ID to lookup.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Object containing the user's score if rated; otherwise null.</returns>
    /// <response code="200">User rating score retrieved successfully.</response>
    [HttpGet("comics/{id:int}/my-rating")]
    [RequireInternalServiceKey]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> GetUserRating(int id, [FromQuery] int accountId, CancellationToken ct = default)
    {
        var score = await _mediator.Send(new GetUserRatingQuery(id, accountId), ct);
        return Ok(new { Score = score });
    }

    /// <summary>
    /// Retrieves staff rating metrics and score distribution breakdown for a comic issue.
    /// </summary>
    /// <param name="id">The unique integer identifier of the comic.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Rating metrics DTO with average and distribution.</returns>
    /// <response code="200">Staff rating metrics retrieved successfully.</response>
    [HttpGet("comics/{id:int}/staff-metrics")]
    [RequireInternalServiceKey]
    [ProducesResponseType(typeof(LoreRatingMetricDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetStaffMetrics(int id, CancellationToken ct = default)
    {
        var metrics = await _mediator.Send(new GetStaffMetricsQuery(id), ct);
        return Ok(metrics);
    }

    /// <summary>
    /// Creates or updates a comic issue along with its pages and credit attributions.
    /// </summary>
    /// <param name="request">Comic data payload including pages and credits.</param>
    /// <param name="isStaff">Staff authorization flag.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Saved comic summary DTO.</returns>
    /// <response code="200">Comic saved successfully.</response>
    /// <response code="403">Forbidden if the requester does not have staff permissions.</response>
    [HttpPost("comics")]
    [RequireInternalServiceKey]
    [ProducesResponseType(typeof(LoreComicSummaryDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> SaveComic([FromBody] SaveComicRequestDto request, [FromQuery] bool isStaff = false, CancellationToken ct = default)
    {
        if (HttpContext != null && !HasTrustedInternalServiceKey())
        {
            _logger.LogWarning("[SECURITY-ALERT] [LoreApiController] Blocked SaveComic without valid X-Internal-Service-Key (IP={RemoteIp})", HttpContext.Connection.RemoteIpAddress);
            return Unauthorized("Invalid or missing inter-service authentication key.");
        }

        if (!isStaff)
        {
            _logger.LogWarning("[SECURITY-ALERT] [LoreApiController] Rejected SaveComic with isStaff=false");
            return Forbid();
        }

        var result = await _mediator.Send(new SaveComicCommand(request, isStaff), ct);
        return result != null ? Ok(result) : Forbid();
    }

    /// <summary>
    /// Deletes a comic issue and cascades deletion to pages, credits, and ratings.
    /// </summary>
    /// <param name="id">The unique integer identifier of the comic to delete.</param>
    /// <param name="isStaff">Staff authorization flag.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Success response.</returns>
    /// <response code="200">Comic deleted successfully.</response>
    /// <response code="403">Forbidden if the requester does not have staff permissions.</response>
    /// <response code="404">Comic was not found.</response>
    [HttpDelete("comics/{id:int}")]
    [RequireInternalServiceKey]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> DeleteComic(int id, [FromQuery] bool isStaff = false, CancellationToken ct = default)
    {
        if (HttpContext != null && !HasTrustedInternalServiceKey())
        {
            _logger.LogWarning("[SECURITY-ALERT] [LoreApiController] Blocked DeleteComic({ComicId}) without valid X-Internal-Service-Key (IP={RemoteIp})", id, HttpContext.Connection.RemoteIpAddress);
            return Unauthorized("Invalid or missing inter-service authentication key.");
        }

        if (!isStaff)
        {
            _logger.LogWarning("[SECURITY-ALERT] [LoreApiController] Rejected DeleteComic({ComicId}) with isStaff=false", id);
            return Forbid();
        }

        var result = await _mediator.Send(new DeleteComicCommand(id, isStaff), ct);
        return result.StatusCode switch
        {
            200 => Ok(new { Success = true }),
            403 => Forbid(),
            404 => NotFound(),
            _ => StatusCode(result.StatusCode, result.Message)
        };
    }

    /// <summary>
    /// Creates or updates a lore story arc.
    /// </summary>
    /// <param name="request">Story arc metadata payload.</param>
    /// <param name="isStaff">Staff authorization flag.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Saved story arc DTO.</returns>
    /// <response code="200">Story arc saved successfully.</response>
    /// <response code="403">Forbidden if the requester does not have staff permissions.</response>
    [HttpPost("arcs")]
    [RequireInternalServiceKey]
    [ProducesResponseType(typeof(LoreArcDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> SaveArc([FromBody] SaveArcRequestDto request, [FromQuery] bool isStaff = false, CancellationToken ct = default)
    {
        if (HttpContext != null && !HasTrustedInternalServiceKey())
        {
            _logger.LogWarning("[SECURITY-ALERT] [LoreApiController] Blocked SaveArc without valid X-Internal-Service-Key (IP={RemoteIp})", HttpContext.Connection.RemoteIpAddress);
            return Unauthorized("Invalid or missing inter-service authentication key.");
        }

        if (!isStaff)
        {
            _logger.LogWarning("[SECURITY-ALERT] [LoreApiController] Rejected SaveArc with isStaff=false");
            return Forbid();
        }

        var result = await _mediator.Send(new SaveArcCommand(request, isStaff), ct);
        return result != null ? Ok(result) : Forbid();
    }

    /// <summary>
    /// Deletes a story arc by its unique identifier.
    /// </summary>
    /// <param name="id">The unique integer identifier of the story arc to delete.</param>
    /// <param name="isStaff">Staff authorization flag.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Success response.</returns>
    /// <response code="200">Story arc deleted successfully.</response>
    /// <response code="403">Forbidden if the requester does not have staff permissions.</response>
    /// <response code="404">Story arc was not found.</response>
    [HttpDelete("arcs/{id:int}")]
    [RequireInternalServiceKey]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> DeleteArc(int id, [FromQuery] bool isStaff = false, CancellationToken ct = default)
    {
        if (HttpContext != null && !HasTrustedInternalServiceKey())
        {
            _logger.LogWarning("[SECURITY-ALERT] [LoreApiController] Blocked DeleteArc({ArcId}) without valid X-Internal-Service-Key (IP={RemoteIp})", id, HttpContext.Connection.RemoteIpAddress);
            return Unauthorized("Invalid or missing inter-service authentication key.");
        }

        if (!isStaff)
        {
            _logger.LogWarning("[SECURITY-ALERT] [LoreApiController] Rejected DeleteArc({ArcId}) with isStaff=false", id);
            return Forbid();
        }

        var result = await _mediator.Send(new DeleteArcCommand(id, isStaff), ct);
        return result.StatusCode switch
        {
            200 => Ok(new { Success = true }),
            403 => Forbid(),
            404 => NotFound(),
            _ => StatusCode(result.StatusCode, result.Message)
        };
    }
}
