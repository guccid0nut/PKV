// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using MediatR;
using Microsoft.Extensions.Logging.Abstractions;
using NEXUS.Contracts.Clients;
using NEXUS.Contracts.CQRS.Guides;
using ZORGATH.WebPortal.API.Features.Guides;
using ZORGATH.WebPortal.API.Infrastructure.Attributes;

namespace ZORGATH.WebPortal.API.Controllers;

/// <summary>
/// REST API controller providing hero guide creation, search, community voting, and account guide preference management.
/// </summary>
[ApiController]
[Route("api/guides")]
[Produces("application/json")]
[EnableRateLimiting(RateLimiterPolicies.Relaxed)]
public class GuideApiController : ControllerBase
{
    public const int MaxGuidePageSize = 100;

    private readonly IMediator _mediator;
    private readonly ILogger<GuideApiController> _logger;

    public GuideApiController(IMediator mediator, ILogger<GuideApiController>? logger = null)
    {
        _mediator = mediator;
        _logger = logger ?? NullLogger<GuideApiController>.Instance;
    }

    private bool HasTrustedInternalServiceKey() =>
        HttpContext != null && RateLimiterPolicies.IsValidInternalServiceKey(HttpContext);

    /// <summary>
    /// Searches and retrieves paginated hero guides with optional hero, map, keyword, and sorting filters.
    /// </summary>
    /// <param name="hero">Optional hero name or hero identifier filter.</param>
    /// <param name="map">Target map filter ("caldavar", "midwars", or "all").</param>
    /// <param name="q">Optional keyword search query.</param>
    /// <param name="sort">Sorting mode ("featured", "rating", "newest", "official", "favorites", "defaults", "myguides").</param>
    /// <param name="page">Page index (1-based).</param>
    /// <param name="pageSize">Number of guides per page.</param>
    /// <param name="currentAccountName">Optional requesting account name to resolve favorites and personal guides.</param>
    /// <param name="isStaff">Flag indicating whether the requesting user has staff privileges.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Paginated guide search results.</returns>
    /// <response code="200">Guide search results retrieved successfully.</response>
    [HttpGet]
    [ProducesResponseType(typeof(GuideSearchResultDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetGuides(
        [FromQuery] string? hero = null,
        [FromQuery] string? map = null,
        [FromQuery] string? q = null,
        [FromQuery] string? sort = "featured",
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 18,
        [FromQuery] string? currentAccountName = null,
        [FromQuery] bool isStaff = false,
        CancellationToken ct = default)
    {
        int clampedPage = Math.Max(1, page);
        int clampedPageSize = Math.Clamp(pageSize, 1, MaxGuidePageSize);
        bool effectiveIsStaff = isStaff && HasTrustedInternalServiceKey();
        if (isStaff && !effectiveIsStaff)
        {
            _logger.LogWarning(
                "[SECURITY-ALERT] [GuideApiController] Rejected spoofed isStaff=true on GetGuides without valid X-Internal-Service-Key (IP={RemoteIp})",
                HttpContext?.Connection?.RemoteIpAddress);
        }

        var result = await _mediator.Send(
            new SearchGuidesQuery(hero, map, q, sort, clampedPage, clampedPageSize, currentAccountName, effectiveIsStaff), ct);
        return Ok(result);
    }

    /// <summary>
    /// Retrieves complete hero guide details by guide ID, including author, items, and skill build queues.
    /// </summary>
    /// <param name="id">The unique integer identifier of the hero guide.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Hero guide DTO if found; otherwise, 404 NotFound.</returns>
    /// <response code="200">Hero guide details retrieved successfully.</response>
    /// <response code="404">Hero guide was not found.</response>
    [HttpGet("{id:int}")]
    [ProducesResponseType(typeof(HeroGuideDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetGuideById(int id, CancellationToken ct = default)
    {
        var guide = await _mediator.Send(new GetGuideByIdQuery(id), ct);
        return guide != null ? Ok(guide) : NotFound();
    }

    /// <summary>
    /// Retrieves official and featured guides for a specific hero identifier.
    /// </summary>
    /// <param name="heroIdentifier">Hero identifier string (e.g., "Hero_Pyromancer").</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>List of featured hero guides.</returns>
    /// <response code="200">Featured hero guides retrieved successfully.</response>
    [HttpGet("hero/{heroIdentifier}")]
    [ProducesResponseType(typeof(List<HeroGuideDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetGuidesForHero(string heroIdentifier, CancellationToken ct = default)
    {
        var guides = await _mediator.Send(new GetGuidesForHeroQuery(heroIdentifier), ct);
        return Ok(guides);
    }

    /// <summary>
    /// Creates a new hero guide or updates an existing guide if authored by the requesting account.
    /// </summary>
    /// <param name="req">Save guide request payload containing guide contents, author name, and permissions.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Save guide operation result DTO.</returns>
    /// <response code="200">Guide saved successfully.</response>
    /// <response code="401">Author account not found or unauthorized.</response>
    /// <response code="403">Forbidden from updating another player's guide without staff permissions.</response>
    [HttpPost("save")]
    [RequireInternalServiceKey]
    [ProducesResponseType(typeof(SaveGuideResultDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(SaveGuideResultDto), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> SaveGuide([FromBody] SaveGuideApiRequestDto req, CancellationToken ct = default)
    {
        if (req.IsStaff && HttpContext != null && !HasTrustedInternalServiceKey())
        {
            _logger.LogWarning("[SECURITY-ALERT] [GuideApiController] Rejected spoofed req.IsStaff on SaveGuide without valid X-Internal-Service-Key");
            return Forbid();
        }

        var result = await _mediator.Send(new SaveGuideCommand(req), ct);
        return result.Status switch
        {
            GuideSaveStatus.Unauthorized => Unauthorized(result.Dto),
            GuideSaveStatus.Forbidden => Forbid(),
            _ => Ok(result.Dto)
        };
    }

    /// <summary>
    /// Deletes a community hero guide by ID if owned by the requesting account.
    /// </summary>
    /// <param name="id">The unique integer identifier of the guide to delete.</param>
    /// <param name="req">Delete request payload specifying the account name.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>True if deleted successfully.</returns>
    /// <response code="200">Hero guide deleted successfully.</response>
    /// <response code="400">Official featured guides cannot be deleted.</response>
    /// <response code="403">Forbidden from deleting guides authored by other players.</response>
    /// <response code="404">Guide was not found.</response>
    [HttpPost("{id:int}/delete")]
    [RequireInternalServiceKey]
    [ProducesResponseType(typeof(bool), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(string), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(string), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> DeleteGuide(int id, [FromBody] DeleteGuideApiRequestDto req,
        CancellationToken ct = default)
    {
        var result = await _mediator.Send(new DeleteGuideCommand(id, req.AccountName), ct);
        return result.Status switch
        {
            GuideDeleteStatus.NotFound => NotFound(result.ErrorMessage),
            GuideDeleteStatus.BadRequest => BadRequest(result.ErrorMessage),
            GuideDeleteStatus.Forbidden => Forbid(),
            _ => Ok(true)
        };
    }

    /// <summary>
    /// Records an upvote or downvote for a hero guide and recalculates its 5-star rating.
    /// </summary>
    /// <param name="id">The unique integer identifier of the guide.</param>
    /// <param name="isUpvote">True for upvote; false for downvote.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Updated vote tallies and computed rating.</returns>
    /// <response code="200">Vote recorded and rating updated.</response>
    /// <response code="404">Guide was not found.</response>
    [HttpPost("{id:int}/vote")]
    [RequireInternalServiceKey]
    [ProducesResponseType(typeof(VoteGuideResultDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(string), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> VoteGuide(int id, [FromQuery] bool isUpvote, CancellationToken ct = default)
    {
        var result = await _mediator.Send(new VoteGuideCommand(id, isUpvote), ct);
        return result != null ? Ok(result) : NotFound("Guide not found");
    }

    /// <summary>
    /// Toggles the public visibility status of a hero guide.
    /// </summary>
    /// <param name="id">The unique integer identifier of the guide.</param>
    /// <param name="req">Request payload specifying account name and staff flag.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Updated privacy status DTO.</returns>
    /// <response code="200">Guide privacy status toggled successfully.</response>
    /// <response code="403">Forbidden from toggling privacy on another author's guide.</response>
    /// <response code="404">Guide was not found.</response>
    [HttpPost("{id:int}/toggle-privacy")]
    [RequireInternalServiceKey]
    [ProducesResponseType(typeof(TogglePrivacyResultDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(string), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> TogglePrivacy(int id, [FromBody] TogglePrivacyApiRequestDto req,
        CancellationToken ct = default)
    {
        if (req.IsStaff && HttpContext != null && !HasTrustedInternalServiceKey())
        {
            _logger.LogWarning("[SECURITY-ALERT] [GuideApiController] Rejected spoofed req.IsStaff on TogglePrivacy({GuideId}) without valid X-Internal-Service-Key", id);
            return Forbid();
        }

        var result = await _mediator.Send(new ToggleGuidePrivacyCommand(id, req.AccountName, req.IsStaff), ct);
        return result.Status switch
        {
            GuideToggleStatus.NotFound => NotFound(result.ErrorMessage),
            GuideToggleStatus.Forbidden => Forbid(),
            _ => Ok(result.Dto)
        };
    }

    /// <summary>
    /// Toggles the official/featured badge on a hero guide.
    /// </summary>
    /// <param name="id">The unique integer identifier of the guide.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Updated official featured status DTO.</returns>
    /// <response code="200">Official status toggled successfully.</response>
    /// <response code="404">Guide was not found.</response>
    [HttpPost("{id:int}/toggle-official")]
    [RequireInternalServiceKey]
    [ProducesResponseType(typeof(ToggleOfficialResultDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(string), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> ToggleOfficial(int id, CancellationToken ct = default)
    {
        var result = await _mediator.Send(new ToggleGuideOfficialCommand(id), ct);
        return result != null ? Ok(result) : NotFound("Guide not found");
    }

    /// <summary>
    /// Sets or unsets a hero guide as the account's active default in-game guide for that hero.
    /// </summary>
    /// <param name="id">The unique integer identifier of the guide.</param>
    /// <param name="req">Request payload containing the account name.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Default guide update result DTO.</returns>
    /// <response code="200">Default guide preference updated successfully.</response>
    /// <response code="404">Account or guide was not found.</response>
    [HttpPost("{id:int}/toggle-default")]
    [RequireInternalServiceKey]
    [ProducesResponseType(typeof(ToggleDefaultGuideResultDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(string), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> ToggleDefault(int id, [FromBody] ToggleDefaultApiRequestDto req,
        CancellationToken ct = default)
    {
        var result = await _mediator.Send(new ToggleDefaultGuideCommand(id, req.AccountName), ct);
        return result.Status switch
        {
            GuidePreferenceStatus.AccountNotFound => NotFound("Account not found"),
            GuidePreferenceStatus.GuideNotFound => NotFound("Guide not found"),
            _ => Ok(result.Dto)
        };
    }

    /// <summary>
    /// Toggles a hero guide into or out of the account's favorites list.
    /// </summary>
    /// <param name="id">The unique integer identifier of the guide.</param>
    /// <param name="req">Request payload containing the account name.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Favorite guide update result DTO.</returns>
    /// <response code="200">Favorite guide preference toggled successfully.</response>
    /// <response code="404">Account or guide was not found.</response>
    [HttpPost("{id:int}/toggle-favorite")]
    [RequireInternalServiceKey]
    [ProducesResponseType(typeof(ToggleFavoriteGuideResultDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(string), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> ToggleFavorite(int id, [FromBody] ToggleFavoriteApiRequestDto req,
        CancellationToken ct = default)
    {
        var result = await _mediator.Send(new ToggleFavoriteGuideCommand(id, req.AccountName), ct);
        return result.Status switch
        {
            GuidePreferenceStatus.AccountNotFound => NotFound("Account not found"),
            GuidePreferenceStatus.GuideNotFound => NotFound("Guide not found"),
            _ => Ok(result.Dto)
        };
    }

    /// <summary>
    /// Retrieves community difficulty rating votes for a specific hero.
    /// </summary>
    /// <param name="heroIdentifier">Hero code identifier (e.g., "Hero_Pyromancer").</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>List of difficulty vote DTOs for the hero.</returns>
    /// <response code="200">Hero difficulty votes retrieved successfully.</response>
    [HttpGet("hero/{heroIdentifier}/difficulty-votes")]
    [ProducesResponseType(typeof(List<HeroDifficultyVoteDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetDifficultyVotes(string heroIdentifier, CancellationToken ct = default)
    {
        var votes = await _mediator.Send(new GetHeroDifficultyVotesQuery(heroIdentifier), ct);
        return Ok(votes);
    }

    /// <summary>
    /// Records a player's community difficulty rating vote (1-5) for a hero and computes the new average.
    /// </summary>
    /// <param name="heroIdentifier">Hero code identifier (e.g., "Hero_Pyromancer").</param>
    /// <param name="req">Request payload specifying voter in-game nickname and difficulty score.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Difficulty vote result DTO with new average and total vote count.</returns>
    /// <response code="200">Difficulty vote recorded successfully.</response>
    /// <response code="400">Account not found.</response>
    [HttpPost("hero/{heroIdentifier}/vote-difficulty")]
    [RequireInternalServiceKey]
    [ProducesResponseType(typeof(HeroDifficultyVoteResultDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(HeroDifficultyVoteResultDto), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> VoteHeroDifficulty(string heroIdentifier,
        [FromBody] VoteDifficultyApiRequestDto req, CancellationToken ct = default)
    {
        var result = await _mediator.Send(new VoteHeroDifficultyCommand(heroIdentifier, req.Ign, req.Difficulty), ct);
        return result.Success ? Ok(result) : BadRequest(result);
    }
}
