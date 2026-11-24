// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using MediatR;
using Microsoft.Extensions.Logging.Abstractions;
using NEXUS.Contracts.Admin;
using NEXUS.Contracts.CQRS.Admin;
using NEXUS.Contracts.CQRS.Admin.Users;
using NEXUS.Contracts.Events;
using ZORGATH.WebPortal.API.Features.Admin.Users;
using ZORGATH.WebPortal.API.Infrastructure.Attributes;

namespace ZORGATH.WebPortal.API.Controllers;

/// <summary>
/// Administrative controller for user account management, role assignment, credential administration, and player economy adjustments.
/// </summary>
[ApiController]
[Route("api/admin/users")]
[Produces("application/json")]
[EnableRateLimiting(RateLimiterPolicies.Relaxed)]
public class AdminUserController : ControllerBase
{
    private readonly IMediator _mediator;
    private readonly ILogger<AdminUserController> _logger;

    public AdminUserController(
        IMediator mediator,
        ILogger<AdminUserController>? logger = null)
    {
        _mediator = mediator;
        _logger = logger ?? NullLogger<AdminUserController>.Instance;
    }


    /// <summary>
    /// Retrieves aggregate summary statistics for users, accounts, staff, and server hosts.
    /// </summary>
    /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
    /// <returns>An <see cref="IActionResult"/> containing the user summary statistics.</returns>
    /// <response code="200">The summary statistics were successfully retrieved.</response>
    /// <response code="500">A database connectivity or server error occurred while calculating statistics.</response>
    [HttpGet("stats")]
    [RequireInternalServiceKey]
    [ProducesResponseType(typeof(UserSummaryStatsDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(UserSummaryStatsDto), StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> GetSummaryStats(CancellationToken cancellationToken = default)
    {
        try
        {
            var result = await _mediator.Send(new GetAdminUserSummaryQuery(), cancellationToken);
            return Ok(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting admin user summary stats");
            return StatusCode(StatusCodes.Status500InternalServerError, new UserSummaryStatsDto
            {
                TotalUsers = 0,
                TotalAccounts = 0,
                TotalStaff = 0,
                TotalHosts = 0,
                IsDatabaseConnected = false,
                StatusMessage = $"Cannot reach database: {ex.Message}"
            });
        }
    }

    /// <summary>
    /// Searches for users matching a search query string and optional role filter.
    /// </summary>
    /// <param name="query">Optional search string matching email address, Discord username/ID, or account name.</param>
    /// <param name="roleFilter">Optional role filter ("STAFF", "HOST", or "STANDARD").</param>
    /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
    /// <returns>An <see cref="IActionResult"/> containing the list of matching admin user details.</returns>
    /// <response code="200">The matching users were successfully retrieved.</response>
    /// <response code="500">An unexpected server error occurred while searching for users.</response>
    [HttpGet]
    [RequireInternalServiceKey]
    [ProducesResponseType(typeof(List<AdminUserDetailDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(string), StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> SearchUsers([FromQuery] string? query, [FromQuery] string? roleFilter,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var result = await _mediator.Send(new SearchAdminUsersQuery(query, roleFilter), cancellationToken);
            return Ok(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error searching users in AdminUserController");
            return StatusCode(StatusCodes.Status500InternalServerError, ex.Message);
        }
    }

    /// <summary>
    /// Retrieves comprehensive user profile details and associated game accounts by user ID.
    /// </summary>
    /// <param name="id">The unique numeric identifier of the user.</param>
    /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
    /// <returns>An <see cref="IActionResult"/> containing the admin user detail DTO if found.</returns>
    /// <response code="200">The user was found and details returned successfully.</response>
    /// <response code="404">No user exists with the specified ID.</response>
    /// <response code="500">An unexpected server error occurred while retrieving user details.</response>
    [HttpGet("{id:int}")]
    [RequireInternalServiceKey]
    [ProducesResponseType(typeof(AdminUserDetailDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(string), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(string), StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> GetUserById(int id, CancellationToken cancellationToken = default)
    {
        try
        {
            var user = await _mediator.Send(new GetAdminUserByIdQuery(id), cancellationToken);
            if (user == null) return NotFound($"User #{id} not found");

            return Ok(user);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting user details by id in AdminUserController");
            return StatusCode(StatusCodes.Status500InternalServerError, ex.Message);
        }
    }

    /// <summary>
    /// Assigns a global system role (Administrator, User, etc.) to a user.
    /// </summary>
    /// <param name="id">The unique numeric identifier of the user.</param>
    /// <param name="request">The request payload containing the target role name.</param>
    /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
    /// <returns>An <see cref="IActionResult"/> indicating the operation success.</returns>
    /// <response code="200">The role was assigned successfully.</response>
    /// <response code="404">No user exists with the specified ID.</response>
    [HttpPost("{id:int}/role")]
    [RequireInternalServiceKey]
    [ProducesResponseType(typeof(bool), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(string), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> SetUserRole(int id, [FromBody] SetUserRoleRequest request,
        CancellationToken cancellationToken = default)
    {
        var result = await _mediator.Send(new SetUserRoleCommand(id, request.RoleName), cancellationToken);
        return result.Success ? Ok(true) : NotFound(result.Message);
    }

    /// <summary>
    /// Sets the account type and privileges for a specific game account.
    /// </summary>
    /// <param name="id">The unique numeric identifier of the account.</param>
    /// <param name="request">The request payload containing the target account type integer.</param>
    /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
    /// <returns>An <see cref="IActionResult"/> indicating the operation success.</returns>
    /// <response code="200">The account type was updated successfully.</response>
    /// <response code="404">No account exists with the specified ID.</response>
    [HttpPost("/api/admin/accounts/{id:int}/role")]
    [RequireInternalServiceKey]
    [ProducesResponseType(typeof(bool), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(string), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> SetAccountRole(int id, [FromBody] SetAccountRoleRequest request,
        CancellationToken cancellationToken = default)
    {
        var result = await _mediator.Send(new SetAccountRoleCommand(id, request.Type), cancellationToken);
        return result.Success ? Ok(true) : NotFound(result.Message);
    }

    /// <summary>
    /// Resets the SRP password credentials for a dedicated server host account.
    /// </summary>
    /// <param name="id">The unique numeric identifier of the host account.</param>
    /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
    /// <returns>An <see cref="IActionResult"/> containing the newly generated raw host password.</returns>
    /// <response code="200">The host password was reset successfully.</response>
    /// <response code="404">No account exists with the specified ID.</response>
    [HttpPost("/api/admin/accounts/{id:int}/reset-password")]
    [RequireInternalServiceKey]
    [ProducesResponseType(typeof(ResetCredentialResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(string), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> ResetHostPassword(int id, CancellationToken cancellationToken = default)
    {
        var result = await _mediator.Send(new ResetHostPasswordCommand(id), cancellationToken);
        return result != null ? Ok(result) : NotFound($"Account #{id} not found");
    }

    /// <summary>
    /// Resets the authentication token for a dedicated server host account.
    /// </summary>
    /// <param name="id">The unique numeric identifier of the host account.</param>
    /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
    /// <returns>An <see cref="IActionResult"/> containing the newly generated host authentication token.</returns>
    /// <response code="200">The host authentication token was reset successfully.</response>
    /// <response code="404">No account exists with the specified ID.</response>
    [HttpPost("/api/admin/accounts/{id:int}/reset-token")]
    [RequireInternalServiceKey]
    [ProducesResponseType(typeof(ResetCredentialResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(string), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> ResetHostAuthToken(int id, CancellationToken cancellationToken = default)
    {
        var result = await _mediator.Send(new ResetHostAuthTokenCommand(id), cancellationToken);
        return result != null ? Ok(result) : NotFound($"Account #{id} not found");
    }

    /// <summary>
    /// Sets absolute currency balances (Gold Coins, Silver Coins, and Plinko Tickets) for a user and records an audit transaction.
    /// </summary>
    /// <param name="id">The unique numeric identifier of the user.</param>
    /// <param name="request">The request payload specifying new balance amounts.</param>
    /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
    /// <returns>An <see cref="IActionResult"/> indicating the operation success.</returns>
    /// <response code="200">The user's economy balances were updated successfully.</response>
    /// <response code="404">No user exists with the specified ID.</response>
    [HttpPost("{id:int}/economy")]
    [RequireInternalServiceKey]
    [ProducesResponseType(typeof(bool), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(string), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> UpdateUserEconomy(int id, [FromBody] UpdateUserEconomyRequest request,
        CancellationToken cancellationToken = default)
    {
        var result = await _mediator.Send(
            new UpdateUserEconomyCommand(id, request.GoldCoins, request.SilverCoins, request.PlinkoTickets),
            cancellationToken);
        return result.Success ? Ok(true) : NotFound(result.Message);
    }

    /// <summary>
    /// Adjusts user currency balances by applying relative deltas and records an audit transaction.
    /// </summary>
    /// <param name="id">The unique numeric identifier of the user.</param>
    /// <param name="request">The request payload specifying delta amounts to add or subtract.</param>
    /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
    /// <returns>An <see cref="IActionResult"/> indicating the operation success.</returns>
    /// <response code="200">The user's balances were adjusted successfully.</response>
    /// <response code="404">No user exists with the specified ID.</response>
    [HttpPost("{id:int}/adjust-balance")]
    [RequireInternalServiceKey]
    [ProducesResponseType(typeof(bool), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(string), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> AdjustUserBalance(int id, [FromBody] AdjustUserBalanceRequest request,
        CancellationToken cancellationToken = default)
    {
        var result = await _mediator.Send(
            new AdjustUserBalanceCommand(id, request.GoldDelta, request.SilverDelta, request.TicketsDelta),
            cancellationToken);
        return result.Success ? Ok(true) : NotFound(result.Message);
    }

    /// <summary>
    /// Promotes a user to the Administrator role and marks their main account as Staff.
    /// </summary>
    /// <param name="id">The unique numeric identifier of the user.</param>
    /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
    /// <returns>An <see cref="IActionResult"/> indicating the operation success.</returns>
    /// <response code="200">The user was promoted to staff successfully.</response>
    /// <response code="404">No user exists with the specified ID.</response>
    [HttpPost("{id:int}/promote-staff")]
    [RequireInternalServiceKey]
    [ProducesResponseType(typeof(bool), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(string), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> PromoteUserToStaff(int id, CancellationToken cancellationToken = default)
    {
        var result = await _mediator.Send(new PromoteUserToStaffCommand(id), cancellationToken);
        return result.Success ? Ok(true) : NotFound(result.Message);
    }

    /// <summary>
    /// Promotes a user's main account to a ServerHost account type.
    /// </summary>
    /// <param name="id">The unique numeric identifier of the user.</param>
    /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
    /// <returns>An <see cref="IActionResult"/> indicating the operation success.</returns>
    /// <response code="200">The user was promoted to server host successfully.</response>
    /// <response code="404">No user exists with the specified ID.</response>
    [HttpPost("{id:int}/promote-host")]
    [RequireInternalServiceKey]
    [ProducesResponseType(typeof(bool), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(string), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> PromoteUserToHost(int id, CancellationToken cancellationToken = default)
    {
        var result = await _mediator.Send(new PromoteUserToHostCommand(id), cancellationToken);
        return result.Success ? Ok(true) : NotFound(result.Message);
    }

    /// <summary>
    /// Reverts a user to the standard User role and resets all associated accounts to Normal account type.
    /// </summary>
    /// <param name="id">The unique numeric identifier of the user.</param>
    /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
    /// <returns>An <see cref="IActionResult"/> indicating the operation success.</returns>
    /// <response code="200">The user was reverted to standard status successfully.</response>
    /// <response code="404">No user exists with the specified ID.</response>
    [HttpPost("{id:int}/revert-standard")]
    [RequireInternalServiceKey]
    [ProducesResponseType(typeof(bool), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(string), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> RevertUserToStandard(int id, CancellationToken cancellationToken = default)
    {
        var result = await _mediator.Send(new RevertUserToStandardCommand(id), cancellationToken);
        return result.Success ? Ok(true) : NotFound(result.Message);
    }
}
