// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using MediatR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NEXUS.Contracts.Clients;
using NEXUS.Contracts.Constants;
using NEXUS.Contracts.CQRS.Admin;
using NEXUS.Contracts.CQRS.Admin.Accounts;
using NEXUS.Contracts.CQRS.Admin.Applications;
using NEXUS.Contracts.CQRS.Admin.Inventory;
using NEXUS.Contracts.CQRS.Admin.Moderation;
using NEXUS.Contracts.CQRS.Admin.Reports;
using NEXUS.Contracts.Data;
using NEXUS.Contracts.Events;
using ZORGATH.WebPortal.API.Features.Admin.Accounts;
using ZORGATH.WebPortal.API.Features.Admin.Applications;
using ZORGATH.WebPortal.API.Features.Admin.Inventory;
using ZORGATH.WebPortal.API.Features.Admin.Moderation;
using ZORGATH.WebPortal.API.Features.Admin.Reports;
using ZORGATH.WebPortal.API.Infrastructure.Attributes;

namespace ZORGATH.WebPortal.API.Controllers;

/// <summary>
/// Administrative API controller handling player moderation, reports, sanctions, host applications, streamer applications, and player inventory management.
/// </summary>
[ApiController]
[Route("api/admin")]
[Produces("application/json")]
[EnableRateLimiting(RateLimiterPolicies.Relaxed)]
public class AdminApiController : ControllerBase
{
    private readonly IMediator _mediator;

    /// <summary>
    /// Initializes a new instance of <see cref="AdminApiController"/> using MediatR dispatching.
    /// </summary>
    /// <param name="mediator">The MediatR mediator instance.</param>
    [ActivatorUtilitiesConstructor]
    public AdminApiController(IMediator mediator)
    {
        _mediator = mediator;
    }


    /// <summary>
    /// Retrieves a paginated list of player reports filtered by status, reason, search query, match ID, or report ID.
    /// </summary>
    /// <param name="status">Optional filter for report status (e.g., "PENDING", "RESOLVED", "DISMISSED").</param>
    /// <param name="reason">Optional filter for report reason category.</param>
    /// <param name="search">Optional text search across reported account name, reporter account name, description, or report ID.</param>
    /// <param name="matchId">Optional filter for reports originating from a specific match ID.</param>
    /// <param name="reportId">Optional filter for a specific player report ID.</param>
    /// <param name="page">The 1-based page index (defaults to 1).</param>
    /// <param name="pageSize">The number of reports to return per page (defaults to 25).</param>
    /// <param name="ct">A token to monitor for cancellation requests.</param>
    /// <returns>An <see cref="IActionResult"/> containing a list of player reports.</returns>
    /// <response code="200">The player reports were successfully retrieved.</response>
    [HttpGet("reports")]
    [RequireInternalServiceKey]
    [ProducesResponseType(typeof(List<PlayerReportDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetPlayerReports(
        [FromQuery] string? status,
        [FromQuery] string? reason,
        [FromQuery] string? search,
        [FromQuery] int? matchId,
        [FromQuery] int? reportId,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 25,
        CancellationToken ct = default)
    {
        var pagedReports = await _mediator.Send(
            new GetPlayerReportsQuery(status, reason, search, matchId, reportId, page, pageSize), ct);
        return Ok(pagedReports);
    }

    /// <summary>
    /// Retrieves the total count of player reports matching the specified filter criteria.
    /// </summary>
    /// <param name="status">Optional filter for report status.</param>
    /// <param name="reason">Optional filter for report reason category.</param>
    /// <param name="search">Optional text search across account names, description, or report ID.</param>
    /// <param name="matchId">Optional filter for reports originating from a specific match ID.</param>
    /// <param name="reportId">Optional filter for a specific player report ID.</param>
    /// <param name="ct">A token to monitor for cancellation requests.</param>
    /// <returns>An <see cref="IActionResult"/> containing the count of matching player reports.</returns>
    /// <response code="200">The count of matching player reports was successfully calculated.</response>
    [HttpGet("reports/count")]
    [RequireInternalServiceKey]
    [ProducesResponseType(typeof(int), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetPlayerReportsCount(
        [FromQuery] string? status,
        [FromQuery] string? reason,
        [FromQuery] string? search,
        [FromQuery] int? matchId,
        [FromQuery] int? reportId,
        CancellationToken ct = default)
    {
        var totalCount = await _mediator.Send(
            new GetPlayerReportsCountQuery(status, reason, search, matchId, reportId), ct);
        return Ok(totalCount);
    }

    /// <summary>
    /// Retrieves a single player report by its unique report ID.
    /// </summary>
    /// <param name="id">The unique numeric identifier of the player report.</param>
    /// <param name="ct">A token to monitor for cancellation requests.</param>
    /// <returns>An <see cref="IActionResult"/> containing the player report if found.</returns>
    /// <response code="200">The player report was found and returned.</response>
    /// <response code="404">No player report exists with the specified ID.</response>
    [HttpGet("reports/{id:int}")]
    [RequireInternalServiceKey]
    [ProducesResponseType(typeof(PlayerReportDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetPlayerReportById(int id, CancellationToken ct = default)
    {
        PlayerReportDto? report = await _mediator.Send(new GetPlayerReportByIdQuery(id), ct);
        return report != null ? Ok(report) : NotFound();
    }

    /// <summary>
    /// Dismisses a player report, changes its status to APPEALED, and lifts existing sanctions on the reported account.
    /// </summary>
    /// <param name="id">The unique numeric identifier of the report to dismiss.</param>
    /// <param name="req">The dismissal request payload containing the staff account ID and comments.</param>
    /// <param name="ct">A token to monitor for cancellation requests.</param>
    /// <returns>An <see cref="IActionResult"/> indicating the operation success.</returns>
    /// <response code="200">The player report was dismissed and sanctions cleared successfully.</response>
    /// <response code="404">No player report exists with the specified ID.</response>
    [HttpPost("reports/{id:int}/dismiss")]
    [RequireInternalServiceKey]
    [ProducesResponseType(typeof(bool), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(string), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> DismissReport(int id, [FromBody] DismissReportRequestDto req,
        CancellationToken ct = default)
    {
        var result = await _mediator.Send(new DismissReportCommand(id, req.StaffAccountId, req.Comment), ct);
        return result.Success ? Ok(true) : NotFound(result.Message);
    }

    /// <summary>
    /// Applies administrative disciplinary action (ban, mute, suspend, or dismiss) to resolve a player report.
    /// </summary>
    /// <param name="req">The action request payload containing action type, duration, reasons, and staff notes.</param>
    /// <param name="ct">A token to monitor for cancellation requests.</param>
    /// <returns>An <see cref="IActionResult"/> indicating the operation success.</returns>
    /// <response code="200">The administrative action was successfully applied to the report and account.</response>
    /// <response code="404">No player report exists with the specified ID.</response>
    [HttpPost("reports/action")]
    [RequireInternalServiceKey]
    [ProducesResponseType(typeof(bool), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(string), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> ActionReport([FromBody] ActionReportRequestDto req, CancellationToken ct = default)
    {
        var result = await _mediator.Send(new ActionReportCommand(
            req.ReportId,
            req.ActionType,
            req.DurationHours,
            req.Reason,
            req.StaffNotes,
            req.StaffAccountId), ct);
        return result.Success ? Ok(true) : NotFound(result.Message);
    }

    /// <summary>
    /// Directly applies a disciplinary sanction (ban, mute, or suspend) to an account without requiring a pre-existing report.
    /// </summary>
    /// <param name="req">The direct punishment request payload containing target account name, punishment type, and duration.</param>
    /// <param name="ct">A token to monitor for cancellation requests.</param>
    /// <returns>An <see cref="IActionResult"/> indicating the operation success.</returns>
    /// <response code="200">The direct disciplinary sanction was successfully applied.</response>
    /// <response code="404">Target account was not found.</response>
    [HttpPost("accounts/direct-punish")]
    [RequireInternalServiceKey]
    [ProducesResponseType(typeof(bool), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(string), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> DirectPunishAccount([FromBody] DirectPunishRequestDto req,
        CancellationToken ct = default)
    {
        var result = await _mediator.Send(new DirectPunishAccountCommand(
            req.TargetAccountName,
            req.PunishmentType,
            req.DurationHours,
            req.Reason,
            req.Notes,
            req.StaffAccountId), ct);
        return result.Success ? Ok(true) : NotFound(result.Message);
    }

    /// <summary>
    /// Issues an account ban with an optional expiration timestamp and reason.
    /// </summary>
    /// <param name="id">The unique numeric identifier of the account to ban.</param>
    /// <param name="req">The ban request payload containing expiration date, reason, and staff ID.</param>
    /// <param name="ct">A token to monitor for cancellation requests.</param>
    /// <returns>An <see cref="IActionResult"/> indicating the operation success.</returns>
    /// <response code="200">The account was banned successfully.</response>
    /// <response code="400">Failed to ban account or invalid request arguments.</response>
    [HttpPost("accounts/{id:int}/ban")]
    [RequireInternalServiceKey]
    [ProducesResponseType(typeof(bool), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(string), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> BanAccount(int id, [FromBody] BanAccountRequestDto req,
        CancellationToken ct = default)
    {
        var result = await _mediator.Send(new BanAccountCommand(id, req.ExpiresAt, req.Reason, req.BannedBy), ct);
        return result.Success ? Ok(true) : BadRequest(result.Message);
    }

    /// <summary>
    /// Lifts an existing ban sanction from an account.
    /// </summary>
    /// <param name="id">The unique numeric identifier of the account to unban.</param>
    /// <param name="ct">A token to monitor for cancellation requests.</param>
    /// <returns>An <see cref="IActionResult"/> indicating the operation success.</returns>
    /// <response code="200">The account ban was lifted successfully.</response>
    /// <response code="400">Failed to unban account or account is not banned.</response>
    [HttpPost("accounts/{id:int}/unban")]
    [RequireInternalServiceKey]
    [ProducesResponseType(typeof(bool), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(string), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> UnbanAccount(int id, CancellationToken ct = default)
    {
        var result = await _mediator.Send(new UnbanAccountCommand(id), ct);
        return result.Success ? Ok(true) : BadRequest(result.Message);
    }

    /// <summary>
    /// Issues a mute sanction on an account preventing in-game chat communication.
    /// </summary>
    /// <param name="id">The unique numeric identifier of the account to mute.</param>
    /// <param name="req">The mute request payload containing expiration timestamp, reason, and staff ID.</param>
    /// <param name="ct">A token to monitor for cancellation requests.</param>
    /// <returns>An <see cref="IActionResult"/> indicating the operation success.</returns>
    /// <response code="200">The account was muted successfully.</response>
    /// <response code="404">No account exists with the specified ID.</response>
    [HttpPost("accounts/{id:int}/mute")]
    [RequireInternalServiceKey]
    [ProducesResponseType(typeof(bool), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(string), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> MuteAccount(int id, [FromBody] MuteAccountRequestDto req,
        CancellationToken ct = default)
    {
        var result = await _mediator.Send(new MuteAccountCommand(id, req.ExpiresAt, req.Reason ?? "Direct Staff Sanction", req.MutedBy), ct);
        return result.Success ? Ok(true) : NotFound(result.Message);
    }

    /// <summary>
    /// Lifts an existing mute sanction from an account.
    /// </summary>
    /// <param name="id">The unique numeric identifier of the account to unmute.</param>
    /// <param name="ct">A token to monitor for cancellation requests.</param>
    /// <returns>An <see cref="IActionResult"/> indicating the operation success.</returns>
    /// <response code="200">The account was unmuted successfully.</response>
    /// <response code="404">No account exists with the specified ID.</response>
    [HttpPost("accounts/{id:int}/unmute")]
    [RequireInternalServiceKey]
    [ProducesResponseType(typeof(bool), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(string), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> UnmuteAccount(int id, CancellationToken ct = default)
    {
        var result = await _mediator.Send(new UnmuteAccountCommand(id), ct);
        return result.Success ? Ok(true) : NotFound(result.Message);
    }

    /// <summary>
    /// Retrieves all dedicated server host applications.
    /// </summary>
    /// <param name="ct">A token to monitor for cancellation requests.</param>
    /// <returns>An <see cref="IActionResult"/> containing a list of host application DTOs.</returns>
    /// <response code="200">The list of host applications was retrieved successfully.</response>
    [HttpGet("host-applications")]
    [RequireInternalServiceKey]
    [ProducesResponseType(typeof(List<HostApplicationDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetHostApplications(CancellationToken ct = default)
    {
        List<HostApplicationDto> apps = await _mediator.Send(new GetHostApplicationsQuery(), ct);
        return Ok(apps);
    }

    /// <summary>
    /// Approves a dedicated server host application and creates or configures the dedicated host account.
    /// </summary>
    /// <param name="id">The unique numeric identifier of the host application to approve.</param>
    /// <param name="ct">A token to monitor for cancellation requests.</param>
    /// <returns>An <see cref="IActionResult"/> indicating the operation success.</returns>
    /// <response code="200">The host application was approved and host account provisioned.</response>
    /// <response code="404">No host application exists with the specified ID.</response>
    [HttpPost("host-applications/{id:int}/approve")]
    [RequireInternalServiceKey]
    [ProducesResponseType(typeof(bool), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(string), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> ApproveHostApplication(int id, CancellationToken ct = default)
    {
        var result = await _mediator.Send(new ApproveHostApplicationCommand(id), ct);
        return result.Success ? Ok(true) : NotFound(result.Message);
    }

    /// <summary>
    /// Rejects a dedicated server host application with specified staff notes or reason.
    /// </summary>
    /// <param name="id">The unique numeric identifier of the host application to reject.</param>
    /// <param name="req">The rejection request payload containing staff reason.</param>
    /// <param name="ct">A token to monitor for cancellation requests.</param>
    /// <returns>An <see cref="IActionResult"/> indicating the operation success.</returns>
    /// <response code="200">The host application was rejected successfully.</response>
    /// <response code="404">No host application exists with the specified ID.</response>
    [HttpPost("host-applications/{id:int}/reject")]
    [RequireInternalServiceKey]
    [ProducesResponseType(typeof(bool), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(string), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> RejectHostApplication(int id, [FromBody] RejectApplicationRequestDto req,
        CancellationToken ct = default)
    {
        var result = await _mediator.Send(new RejectHostApplicationCommand(id, req.Reason), ct);
        return result.Success ? Ok(true) : NotFound(result.Message);
    }

    /// <summary>
    /// Revokes an approved dedicated server host application and disables the associated host account.
    /// </summary>
    /// <param name="id">The unique numeric identifier of the host application to revoke.</param>
    /// <param name="req">Optional rejection request payload containing revocation reason.</param>
    /// <param name="ct">A token to monitor for cancellation requests.</param>
    /// <returns>An <see cref="IActionResult"/> indicating the operation success.</returns>
    /// <response code="200">The host application was revoked successfully.</response>
    /// <response code="404">No host application exists with the specified ID.</response>
    [HttpPost("host-applications/{id:int}/revoke")]
    [RequireInternalServiceKey]
    [ProducesResponseType(typeof(bool), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(string), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> RevokeHostApplication(int id, [FromBody] RejectApplicationRequestDto? req = null,
        CancellationToken ct = default)
    {
        var result = await _mediator.Send(new RevokeHostApplicationCommand(id, req?.Reason), ct);
        return result.Success ? Ok(true) : NotFound(result.Message);
    }

    /// <summary>
    /// Retrieves all streamer program applications.
    /// </summary>
    /// <param name="ct">A token to monitor for cancellation requests.</param>
    /// <returns>An <see cref="IActionResult"/> containing a list of streamer application DTOs.</returns>
    /// <response code="200">The streamer applications were retrieved successfully.</response>
    [HttpGet("streamer-applications")]
    [RequireInternalServiceKey]
    [ProducesResponseType(typeof(List<StreamerApplicationDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetStreamerApplications(CancellationToken ct = default)
    {
        List<StreamerApplicationDto> apps = await _mediator.Send(new GetStreamerApplicationsQuery(), ct);
        return Ok(apps);
    }

    /// <summary>
    /// Approves a streamer program application and upgrades the applicant's main account to Streamer status.
    /// </summary>
    /// <param name="id">The unique numeric identifier of the streamer application to approve.</param>
    /// <param name="ct">A token to monitor for cancellation requests.</param>
    /// <returns>An <see cref="IActionResult"/> indicating the operation success.</returns>
    /// <response code="200">The streamer application was approved successfully.</response>
    /// <response code="404">No streamer application exists with the specified ID.</response>
    [HttpPost("streamer-applications/{id:int}/approve")]
    [RequireInternalServiceKey]
    [ProducesResponseType(typeof(bool), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(string), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> ApproveStreamerApplication(int id, CancellationToken ct = default)
    {
        var result = await _mediator.Send(new ApproveStreamerApplicationCommand(id), ct);
        return result.Success ? Ok(true) : NotFound(result.Message);
    }

    /// <summary>
    /// Rejects a streamer program application with specified staff notes or reason.
    /// </summary>
    /// <param name="id">The unique numeric identifier of the streamer application to reject.</param>
    /// <param name="req">The rejection request payload containing staff reason.</param>
    /// <param name="ct">A token to monitor for cancellation requests.</param>
    /// <returns>An <see cref="IActionResult"/> indicating the operation success.</returns>
    /// <response code="200">The streamer application was rejected successfully.</response>
    /// <response code="404">No streamer application exists with the specified ID.</response>
    [HttpPost("streamer-applications/{id:int}/reject")]
    [RequireInternalServiceKey]
    [ProducesResponseType(typeof(bool), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(string), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> RejectStreamerApplication(int id, [FromBody] RejectApplicationRequestDto req,
        CancellationToken ct = default)
    {
        var result = await _mediator.Send(new RejectStreamerApplicationCommand(id, req.Reason), ct);
        return result.Success ? Ok(true) : NotFound(result.Message);
    }

    /// <summary>
    /// Revokes an approved streamer program application and reverts the main account to Normal status.
    /// </summary>
    /// <param name="id">The unique numeric identifier of the streamer application to revoke.</param>
    /// <param name="ct">A token to monitor for cancellation requests.</param>
    /// <returns>An <see cref="IActionResult"/> indicating the operation success.</returns>
    /// <response code="200">The streamer application was revoked successfully.</response>
    /// <response code="404">No streamer application exists with the specified ID.</response>
    [HttpPost("streamer-applications/{id:int}/revoke")]
    [RequireInternalServiceKey]
    [ProducesResponseType(typeof(bool), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(string), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> RevokeStreamerApplication(int id, CancellationToken ct = default)
    {
        var result = await _mediator.Send(new RevokeStreamerApplicationCommand(id), ct);
        return result.Success ? Ok(true) : NotFound(result.Message);
    }

    /// <summary>
    /// Retrieves a list of recently created accounts up to the specified limit.
    /// </summary>
    /// <param name="limit">The maximum number of recent accounts to return (defaults to 20).</param>
    /// <param name="ct">A token to monitor for cancellation requests.</param>
    /// <returns>An <see cref="IActionResult"/> containing a list of account DTOs.</returns>
    /// <response code="200">The recent accounts were retrieved successfully.</response>
    [HttpGet("accounts/recent")]
    [RequireInternalServiceKey]
    [ProducesResponseType(typeof(List<AccountDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetRecentAccounts([FromQuery] int limit = 20, CancellationToken ct = default)
    {
        List<AccountDto> accounts = await _mediator.Send(new GetRecentAccountsQuery(limit), ct);
        return Ok(accounts);
    }

    /// <summary>
    /// Retrieves comprehensive account details including user information and statistics by account name.
    /// </summary>
    /// <param name="name">The account name to look up.</param>
    /// <param name="ct">A token to monitor for cancellation requests.</param>
    /// <returns>An <see cref="IActionResult"/> containing the account DTO if found.</returns>
    /// <response code="200">The account was found and returned.</response>
    /// <response code="404">No account exists with the specified name.</response>
    [HttpGet("accounts/detail/{name}")]
    [RequireInternalServiceKey]
    [ProducesResponseType(typeof(AccountDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetAccountDetail(string name, CancellationToken ct = default)
    {
        AccountDto? account = await _mediator.Send(new GetAccountDetailQuery(name), ct);
        return account != null ? Ok(account) : NotFound();
    }

    /// <summary>
    /// Grants an item to a user's inventory by product code.
    /// </summary>
    /// <param name="req">The request payload containing target account name and product code.</param>
    /// <param name="ct">A token to monitor for cancellation requests.</param>
    /// <returns>An <see cref="IActionResult"/> indicating the operation success.</returns>
    /// <response code="200">The item was added to the account inventory successfully.</response>
    /// <response code="404">No account exists with the specified name.</response>
    [HttpPost("inventory/add")]
    [RequireInternalServiceKey]
    [ProducesResponseType(typeof(bool), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(string), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> AddInventoryItem([FromBody] ModifyInventoryRequestDto req,
        CancellationToken ct = default)
    {
        var result = await _mediator.Send(new AddInventoryItemCommand(req.AccountName, req.ProductCode), ct);
        return result.Success ? Ok(true) : NotFound(result.Message);
    }

    /// <summary>
    /// Removes an item from a user's inventory by product code.
    /// </summary>
    /// <param name="req">The request payload containing target account name and product code.</param>
    /// <param name="ct">A token to monitor for cancellation requests.</param>
    /// <returns>An <see cref="IActionResult"/> indicating the operation success.</returns>
    /// <response code="200">The item was removed from the account inventory successfully.</response>
    /// <response code="404">No account exists with the specified name.</response>
    [HttpPost("inventory/remove")]
    [RequireInternalServiceKey]
    [ProducesResponseType(typeof(bool), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(string), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> RemoveInventoryItem([FromBody] ModifyInventoryRequestDto req,
        CancellationToken ct = default)
    {
        var result = await _mediator.Send(new RemoveInventoryItemCommand(req.AccountName, req.ProductCode), ct);
        return result.Success ? Ok(true) : NotFound(result.Message);
    }

    /// <summary>
    /// Resets a user's inventory and restores default starter items.
    /// </summary>
    /// <param name="req">The request payload containing target account name.</param>
    /// <param name="ct">A token to monitor for cancellation requests.</param>
    /// <returns>An <see cref="IActionResult"/> indicating the operation success.</returns>
    /// <response code="200">The inventory was reset successfully.</response>
    /// <response code="404">No account exists with the specified name.</response>
    [HttpPost("inventory/reset")]
    [RequireInternalServiceKey]
    [ProducesResponseType(typeof(bool), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(string), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> ResetInventory([FromBody] ResetInventoryRequestDto req,
        CancellationToken ct = default)
    {
        var result = await _mediator.Send(new ResetInventoryCommand(req.AccountName), ct);
        return result.Success ? Ok(true) : NotFound(result.Message);
    }
}

/// <summary>
/// Request payload for dismissing a player report.
/// </summary>
/// <param name="StaffAccountId">The staff member's account ID performing the dismissal.</param>
/// <param name="Comment">Optional staff comment explaining the dismissal.</param>
public record DismissReportRequestDto(int StaffAccountId, string? Comment);

/// <summary>
/// Request payload for rejecting a host or streamer application.
/// </summary>
/// <param name="Reason">Optional explanation for the application rejection.</param>
public record RejectApplicationRequestDto(string? Reason);

/// <summary>
/// Request payload for banning a player account.
/// </summary>
/// <param name="ExpiresAt">Optional expiration timestamp for temporary bans, or null for permanent bans.</param>
/// <param name="Reason">The reason for issuing the account ban.</param>
/// <param name="BannedBy">Optional staff account ID who initiated the ban.</param>
public record BanAccountRequestDto(DateTimeOffset? ExpiresAt, string Reason, int? BannedBy);

/// <summary>
/// Request payload for muting a player account.
/// </summary>
/// <param name="ExpiresAt">Optional expiration timestamp for the mute sanction.</param>
/// <param name="Reason">Optional reason for issuing the mute sanction.</param>
/// <param name="MutedBy">Optional staff account ID who initiated the mute sanction.</param>
public record MuteAccountRequestDto(DateTimeOffset? ExpiresAt, string? Reason, int? MutedBy);

/// <summary>
/// Request payload for adding or removing an item from a user's inventory.
/// </summary>
/// <param name="AccountName">The player account name.</param>
/// <param name="ProductCode">The product/item code to modify.</param>
public record ModifyInventoryRequestDto(string AccountName, string ProductCode);

/// <summary>
/// Request payload for resetting a player account inventory to default starter items.
/// </summary>
/// <param name="AccountName">The player account name whose inventory will be reset.</param>
public record ResetInventoryRequestDto(string AccountName);
