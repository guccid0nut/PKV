// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2022-2026 [K]ONGOR and Contributors
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV) -- Upstream lineage: https://github.com/Project-KONGOR-Open-Source

using MediatR;
using Microsoft.Extensions.Logging.Abstractions;
using NEXUS.Contracts.CQRS.Email;
using ZORGATH.WebPortal.API.Extensions;

namespace ZORGATH.WebPortal.API.Controllers;

/// <summary>
/// Web portal email management controller handling email address registration verification tokens and dispatching confirmation emails.
/// </summary>
[ApiController]
[Route("[controller]")]
[Consumes("application/json")]
[EnableRateLimiting(RateLimiterPolicies.Strict)]
public class EmailAddressController : ControllerBase
{
    private readonly IMediator _mediator;
    private readonly ILogger<EmailAddressController> _logger;

    public EmailAddressController(
        IMediator mediator,
        ILogger<EmailAddressController>? logger = null)
    {
        _mediator = mediator;
        _logger = logger ?? NullLogger<EmailAddressController>.Instance;
    }


    /// <summary>
    /// Registers an email address by validating format and issuing an email verification link token.
    /// </summary>
    /// <param name="payload">Payload containing the target email address and confirmation email address.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A status message indicating whether the registration token was generated and dispatched.</returns>
    /// <response code="200">Email verification token successfully created and confirmation email dispatched.</response>
    /// <response code="400">Email addresses do not match, or address is already registered/pending confirmation.</response>
    /// <response code="422">Email address failed sanitization or could not be processed.</response>
    /// <response code="503">Failed to deliver verification email through the email service provider.</response>
    [HttpPost("Register", Name = "Register Email Address")]
    [AllowAnonymous]
    [ProducesResponseType(typeof(string), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(string), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(string), StatusCodes.Status422UnprocessableEntity)]
    [ProducesResponseType(typeof(string), StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> RegisterEmailAddress(RegisterEmailAddressDTO payload,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("EmailAddressController: Disagreeing/Verifying payload for {EmailAddress}", payload.EmailAddress);

        RegisterEmailAddressResult result = await _mediator.Send(
            new RegisterEmailAddressCommand(payload.EmailAddress, payload.ConfirmEmailAddress),
            cancellationToken);

        return result.ToActionResult(this);
    }
}
