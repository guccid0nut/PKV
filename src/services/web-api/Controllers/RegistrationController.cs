// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2022-2026 [K]ONGOR and Contributors
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV) -- Upstream lineage: https://github.com/Project-KONGOR-Open-Source

using ASPIRE.Common.DTOs;
using MediatR;
using MERRICK.DatabaseContext.Repositories;
using MERRICK.DatabaseContext.Services;
using Microsoft.Extensions.Logging.Abstractions;
using NEXUS.Contracts.CQRS.Registration;
using ZORGATH.WebPortal.API.Extensions;
using ZORGATH.WebPortal.API.Features.Registration;
using ZORGATH.WebPortal.API.Infrastructure.Attributes;
using ZORGATH.WebPortal.API.Validators;

namespace ZORGATH.WebPortal.API.Controllers;

/// <summary>
/// Handles player account registration workflows, email verification tokens, and direct API onboarding.
/// </summary>
[ApiController]
[Route("[controller]")]
[Consumes("application/json")]
[EnableRateLimiting(RateLimiterPolicies.Strict)]
public class RegistrationController : ControllerBase
{
    private readonly IMediator _mediator;
    private readonly ILogger<RegistrationController> _logger;

    public RegistrationController(
        IMediator mediator,
        ILogger<RegistrationController>? logger = null)
    {
        _mediator = mediator;
        _logger = logger ?? NullLogger<RegistrationController>.Instance;
    }


    /// <summary>
    /// Initiates a new account registration workflow by validating the email address and dispatching an activation token.
    /// </summary>
    /// <param name="payload">Registration initiation payload containing the player's email address.</param>
    /// <param name="cancellationToken">Cancellation token for async database operations.</param>
    /// <returns>A confirmation message indicating the verification email was dispatched.</returns>
    /// <response code="200">Verification email sent successfully.</response>
    /// <response code="400">Failed to send verification email due to an email delivery failure.</response>
    /// <response code="409">A user account with the specified email address already exists.</response>
    [HttpPost("InitiateRegistration", Name = "Initiate Registration")]
    [AllowAnonymous]
    [ProducesResponseType(typeof(string), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(string), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(string), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> InitiateRegistration([FromBody] RegisterEmailAddressDTO payload,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("InitiateRegistration called for {Email}", payload.EmailAddress);

        InitiateRegistrationResult result = await _mediator.Send(
            new InitiateRegistrationCommand(payload.EmailAddress),
            cancellationToken);

        return result.ToActionResult(this);
    }

    /// <summary>
    /// Completes the token-based account registration by validating password complexity, verifying the activation token, and creating credentials.
    /// </summary>
    /// <param name="payload">Payload containing registration token, nickname, and password.</param>
    /// <param name="cancellationToken">Cancellation token for async database operations.</param>
    /// <returns>Basic user profile details for the newly provisioned player account.</returns>
    /// <response code="201">Account provisioned successfully.</response>
    /// <response code="400">Password complexity failed or account name is invalid.</response>
    /// <response code="404">Activation token was not found or has expired.</response>
    /// <response code="409">Token has already been consumed, or the email/nickname is already taken.</response>
    [HttpPost("CompleteRegistration", Name = "Complete Registration")]
    [AllowAnonymous]
    [ProducesResponseType(typeof(GetBasicUserDTO), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(string), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(string), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(string), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> CompleteRegistration([FromBody] RegisterUserWithTokenDTO payload,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("CompleteRegistration called for {Name}", payload.Name);

        RegistrationResult result = await _mediator.Send(
            new CompleteRegistrationCommand(payload.Token, payload.Name, payload.Password),
            cancellationToken);

        return result.ToActionResult(this);
    }

    /// <summary>
    /// Directly registers a user account with optional Discord OAuth credentials and invite code verification.
    /// </summary>
    /// <param name="payload">Direct user registration payload including credentials, profile info, and optional invite code.</param>
    /// <param name="cancellationToken">Cancellation token for async database operations.</param>
    /// <returns>Basic user profile details for the newly registered account.</returns>
    /// <response code="201">Direct account registration succeeded.</response>
    /// <response code="400">Invalid parameters, failed password validation, or missing required invite code.</response>
    /// <response code="404">Required default user role not found in system.</response>
    /// <response code="409">Account name or email address is already registered.</response>
    [HttpPost("Register", Name = "Register Direct User")]
    [RequireInternalServiceKey]
    [AllowAnonymous]
    [ProducesResponseType(typeof(GetBasicUserDTO), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(string), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(string), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(string), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Register([FromBody] RegisterDirectUserDTO payload,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Register direct called for account {Name}, DiscordId: {DiscordId}",
            payload.Name, payload.DiscordId);

        string? remoteIp = HttpContext?.Connection.RemoteIpAddress?.ToString();

        RegistrationResult result = await _mediator.Send(
            new RegisterDirectUserCommand(
                payload.Email,
                payload.Name,
                payload.Password,
                payload.DiscordId,
                payload.DiscordUsername,
                payload.DiscordAvatar,
                payload.InviteCode,
                remoteIp),
            cancellationToken);

        return result.ToActionResult(this);
    }
}
