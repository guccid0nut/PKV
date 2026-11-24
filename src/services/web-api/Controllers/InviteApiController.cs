// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using ASPIRE.Common.DTOs;
using MediatR;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.Extensions.Logging.Abstractions;
using NEXUS.Contracts.CQRS.Invites;
using NEXUS.Contracts.Data;
using ZORGATH.WebPortal.API.Infrastructure.Attributes;

namespace ZORGATH.WebPortal.API.Controllers;

/// <summary>
/// Request payload for generating bootstrap admin invite codes in standalone mode.
/// </summary>
public record BootstrapInvitesRequestDto(int? Count = null);

/// <summary>
/// REST API controller managing player invite codes, generation quotas, lineage hierarchy trees, and invite-only beta gating mode.
/// </summary>
[ApiController]
[Route("api/invites")]
[Produces("application/json")]
[EnableRateLimiting(RateLimiterPolicies.Relaxed)]
public class InviteApiController : ControllerBase
{
    private readonly IMediator _mediator;
    private readonly ILogger<InviteApiController> _logger;

    public InviteApiController(
        IMediator mediator,
        ILogger<InviteApiController>? logger = null)
    {
        _mediator = mediator;
        _logger = logger ?? NullLogger<InviteApiController>.Instance;
    }


    /// <summary>
    /// Generates a new unique registration invite code for a player if their match quota permits.
    /// </summary>
    /// <param name="userId">The unique identifier of the user generating the invite.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Result DTO containing the generated invite code or quota restriction details.</returns>
    /// <response code="200">Invite code generated or quota status returned.</response>
    /// <response code="400">Failed to generate invite code due to database error.</response>
    [HttpPost("generate")]
    [RequireInternalServiceKey]
    [ProducesResponseType(typeof(GenerateInviteResult), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(GenerateInviteResult), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> GenerateInviteCode([FromQuery] int userId, CancellationToken ct = default)
    {
        string? ip = HttpContext.Connection.RemoteIpAddress?.ToString();
        var result = await _mediator.Send(new GenerateInviteCommand(userId, ip), ct);

        if (!result.Success && result.Message == "Failed to generate invite code.")
        {
            return BadRequest(result);
        }

        return Ok(result);
    }

    /// <summary>
    /// Retrieves the invite generation quota and eligibility status for a user.
    /// </summary>
    /// <param name="userId">The unique identifier of the user.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Invite quota status detailing capacity and matches played.</returns>
    /// <response code="200">User invite quota status retrieved successfully.</response>
    [HttpGet("quota/{userId:int}")]
    [RequireInternalServiceKey]
    [ProducesResponseType(typeof(GenerateInviteResult), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetUserQuota(int userId, CancellationToken ct = default)
    {
        var result = await _mediator.Send(new GetUserQuotaQuery(userId), ct);
        return Ok(result);
    }

    /// <summary>
    /// Retrieves all invite codes created by a specific user along with their redemption status.
    /// </summary>
    /// <param name="userId">The unique identifier of the user.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>List of invite code DTOs created by the user.</returns>
    /// <response code="200">User invites retrieved successfully.</response>
    [HttpGet("my-invites/{userId:int}")]
    [RequireInternalServiceKey]
    [ProducesResponseType(typeof(List<InviteSummaryDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetMyInvites(int userId, CancellationToken ct = default)
    {
        var result = await _mediator.Send(new GetMyInvitesQuery(userId), ct);
        return Ok(result);
    }

    /// <summary>
    /// Validates an invite code during registration, verifying active status and returning the inviter's identity.
    /// </summary>
    /// <param name="code">The alphanumeric invite code to validate.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Validation result DTO indicating validity and inviter nickname.</returns>
    /// <response code="200">Validation result returned successfully.</response>
    [HttpGet("validate")]
    [AllowAnonymous]
    [ProducesResponseType(typeof(ValidateInviteResult), StatusCodes.Status200OK)]
    public async Task<IActionResult> ValidateInvite([FromQuery] string code, CancellationToken ct = default)
    {
        var result = await _mediator.Send(new ValidateInviteQuery(code), ct);
        return Ok(result);
    }

    /// <summary>
    /// Checks whether the server is currently operating in invite-only registration mode.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>An object indicating whether invite-only mode is active.</returns>
    /// <response code="200">Invite-only mode status returned.</response>
    [HttpGet("mode")]
    [AllowAnonymous]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> GetInviteOnlyMode(CancellationToken ct = default)
    {
        bool isEnabled = await _mediator.Send(new GetInviteOnlyModeQuery(), ct);
        return Ok(new { isInviteOnlyEnabled = isEnabled });
    }

    /// <summary>
    /// Retrieves administrative metrics and audit history for the invite gating system.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Invite system status metrics and audit log entries.</returns>
    /// <response code="200">Administrative invite system status retrieved successfully.</response>
    [HttpGet("/api/admin/invites/status")]
    [RequireInternalServiceKey]
    [ProducesResponseType(typeof(InviteSystemStatusResult), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetInviteSystemStatus(CancellationToken ct = default)
    {
        var result = await _mediator.Send(new GetInviteSystemStatusQuery(), ct);
        return Ok(result);
    }

    /// <summary>
    /// Toggles the platform between open registration and invite-only mode, logging an administrative audit entry.
    /// </summary>
    /// <param name="req">Toggle request containing enabled status and reason notes.</param>
    /// <param name="staffAccountId">Account ID of the staff member toggling the mode.</param>
    /// <param name="staffAccountName">Account name of the staff member toggling the mode.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Status indicating successful toggle update.</returns>
    /// <response code="200">Invite-only mode toggled successfully.</response>
    [HttpPost("/api/admin/invites/toggle")]
    [RequireInternalServiceKey]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> ToggleInviteOnlyMode(
        [FromBody] InviteModeToggleRequestDto req,
        [FromQuery] int staffAccountId,
        [FromQuery] string staffAccountName,
        CancellationToken ct = default)
    {
        string? ip = HttpContext.Connection.RemoteIpAddress?.ToString();
        var result = await _mediator.Send(
            new ToggleInviteOnlyModeCommand(staffAccountId, staffAccountName, req.Enabled, req.Notes, ip),
            ct);

        return Ok(new { success = result.Success, enabled = result.Enabled });
    }

    /// <summary>
    /// Retrieves a 4-generation invite tree graph rooted at the specified user ID for moderation analysis.
    /// </summary>
    /// <param name="userId">Root user ID to inspect.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Root invite tree node containing hierarchical invite descendants.</returns>
    /// <response code="200">Invite lineage tree retrieved successfully.</response>
    /// <response code="404">Root user not found in the invite graph.</response>
    [HttpGet("/api/admin/invites/tree/{userId:int}")]
    [RequireInternalServiceKey]
    [ProducesResponseType(typeof(InviteTreeNodeResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetInviteLineageTree(int userId, CancellationToken ct = default)
    {
        var treeNode = await _mediator.Send(new GetInviteLineageTreeQuery(userId), ct);
        if (treeNode == null) return NotFound("User not found");

        return Ok(treeNode);
    }

    /// <summary>
    /// Revokes all unused invite codes and optionally bans all accounts downstream of a compromised user in the invite tree.
    /// </summary>
    /// <param name="req">Prune request containing root user ID, revocation reason, and ban flag.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Summary of affected users, revoked codes, and banned accounts.</returns>
    /// <response code="200">Invite subtree pruned successfully.</response>
    [HttpPost("/api/admin/invites/prune-branch")]
    [RequireInternalServiceKey]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> PruneInviteSubtree([FromBody] PruneSubtreeRequestDto req, CancellationToken ct = default)
    {
        _logger.LogWarning("[InviteApiController] Pruning invite subtree for User #{RootUserId}. Reason: {Reason}, BanAccounts: {BanAccounts}",
            req.RootUserId, req.Reason, req.BanAccounts);

        var result = await _mediator.Send(
            new PruneInviteSubtreeCommand(req.RootUserId, req.Reason, req.BanAccounts),
            ct);

        return Ok(new
        {
            success = result.Success,
            affectedUsers = result.AffectedUsers,
            revokedCodes = result.RevokedCodes,
            bannedAccounts = result.BannedAccounts
        });
    }

    /// <summary>
    /// Generates a batch of single-use invite codes on behalf of the standalone bootstrap admin user.
    /// </summary>
    /// <param name="count">Optional count parameter from query.</param>
    /// <param name="body">Optional JSON request body containing count.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Count and list of generated invite codes.</returns>
    [HttpPost("/api/admin/invites/bootstrap")]
    [RequireInternalServiceKey]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> BootstrapInvites(
        [FromQuery] int count = 1,
        [FromBody(EmptyBodyBehavior = EmptyBodyBehavior.Allow)] BootstrapInvitesRequestDto? body = null,
        CancellationToken ct = default)
    {
        int resolvedCount = Math.Clamp(body?.Count ?? count, 1, 100);
        string? remoteIp = HttpContext.Connection.RemoteIpAddress?.ToString();

        BootstrapInvitesResult result = await _mediator.Send(new BootstrapInvitesCommand(resolvedCount, remoteIp), ct);
        if (!result.AdminFound)
        {
            return NotFound(new { error = "Standalone bootstrap admin user 'admin@kongor.local' not found. Start the standalone cluster first." });
        }

        _logger.LogInformation("[Bootstrap] Generated {Count} admin invite codes via /api/admin/invites/bootstrap", result.Count);
        return Ok(new { count = result.Count, codes = result.Codes });
    }
}
