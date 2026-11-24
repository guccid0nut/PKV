// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2022-2026 [K]ONGOR and Contributors
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV) -- Upstream lineage: https://github.com/Project-KONGOR-Open-Source

using MERRICK.DatabaseContext.Mappings;
using Microsoft.AspNetCore.OutputCaching;
using NEXUS.Contracts.Data;

namespace ZORGATH.WebPortal.API.Controllers;

/// <summary>
/// Web portal user identity controller managing account registration, SRP credential authentication, and user profile retrieval.
/// </summary>
[ApiController]
[Route("[controller]")]
[EnableRateLimiting(RateLimiterPolicies.Strict)]
public partial class UserController(
    IAccountRepository accountRepository,
    ILogger<UserController> logger,
    IUserService userService,
    IAuthenticationService authenticationService) : ControllerBase
{
    [LoggerMessage(Level = LogLevel.Error, Message = "[BUG] Unknown User Role \"{RoleName}\"")]
    private partial void LogUnknownUserRole(string roleName);

    /// <summary>
    /// Registers a new user identity along with their primary in-game account.
    /// </summary>
    /// <param name="payload">Registration payload containing credentials, email verification token, and initial nickname.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The newly registered user details.</returns>
    /// <response code="201">User and main account successfully created.</response>
    /// <response code="400">Validation errors or malformed payload.</response>
    /// <response code="404">Email verification token not found.</response>
    /// <response code="409">Account name or email address is already registered.</response>
    [HttpPost("Register", Name = "Register User And Main Account")]
    [Consumes("application/json")]
    [AllowAnonymous]
    [ProducesResponseType(typeof(GetBasicUserDTO), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(string), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(IEnumerable<string>), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(string), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(string), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> RegisterUserAndMainAccount([FromBody] RegisterUserAndMainAccountDTO payload,
        CancellationToken cancellationToken = default)
    {
        ServiceResult<User> result = await userService.RegisterUserAsync(payload, cancellationToken);

        if (result.IsSuccess)
        {
            User user = result.Data!;
            return CreatedAtAction(nameof(GetUser), new { id = user.ID }, user.ToBasicDTO(false));
        }

        return result.ErrorType switch
        {
            ServiceErrorType.BadRequest => result.ValidationErrors != null
                ? BadRequest(result.ValidationErrors)
                : BadRequest(result.ErrorMessage),
            ServiceErrorType.NotFound => NotFound(result.ErrorMessage),
            ServiceErrorType.Conflict => Conflict(result.ErrorMessage),
            _ => StatusCode(StatusCodes.Status500InternalServerError, result.ErrorMessage)
        };
    }

    /// <summary>
    /// Authenticates a user using SRP-6a credentials and issues JWT access and refresh tokens.
    /// </summary>
    /// <param name="payload">Login credentials payload including email or username and SRP proof.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Authentication result containing JWT token and user summary.</returns>
    /// <response code="200">User authenticated successfully.</response>
    /// <response code="401">Invalid credentials or failed password verification.</response>
    /// <response code="404">User not found.</response>
    /// <response code="422">Account is banned or suspended.</response>
    [HttpPost("LogIn", Name = "Log In User")]
    [Consumes("application/json")]
    [AllowAnonymous]
    [ProducesResponseType(typeof(GetAuthenticationTokenDTO), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(string), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(string), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(string), StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> LogInUser([FromBody] LogInUserDTO payload,
        CancellationToken cancellationToken = default)
    {
        ServiceResult<GetAuthenticationTokenDTO> result =
            await authenticationService.AuthenticateUserAsync(payload, cancellationToken);

        if (result.IsSuccess) return Ok(result.Data);

        return result.ErrorType switch
        {
            ServiceErrorType.NotFound => NotFound(result.ErrorMessage),
            ServiceErrorType.Unauthorized => Unauthorized(result.ErrorMessage),
            ServiceErrorType.UnprocessableEntity => UnprocessableEntity(result.ErrorMessage),
            _ => StatusCode(StatusCodes.Status500InternalServerError, result.ErrorMessage)
        };
    }

    /// <summary>
    /// Retrieves a user's basic profile details by ID with role-based email obfuscation.
    /// </summary>
    /// <param name="id">Unique identifier of the user.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Basic user profile information.</returns>
    /// <response code="200">User found and profile returned.</response>
    /// <response code="400">Unknown or invalid user role.</response>
    /// <response code="401">Unauthorized request.</response>
    /// <response code="403">Forbidden access to requested user resource.</response>
    /// <response code="404">User with specified ID not found.</response>
    [HttpGet("{id:int}", Name = "Get User")]
    [Authorize(UserRoles.AllRoles)]
    [OutputCache(PolicyName = OutputCachePolicies.CacheForFiveMinutes, VaryByHeaderNames = ["Authorization"])]
    [ProducesResponseType(typeof(GetBasicUserDTO), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(string), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(string), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(string), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(string), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetUser(int id, CancellationToken cancellationToken = default)
    {
        User? user = await accountRepository.GetUserByIdAsync(id, cancellationToken);

        if (user is null) return NotFound($@"User With ID ""{id}"" Was Not Found");

        string role = User.Claims.GetUserRole();

        if (string.Equals(role, UserRoles.Administrator, StringComparison.OrdinalIgnoreCase))
            return Ok(user.ToBasicDTO());

        if (string.Equals(role, UserRoles.User, StringComparison.OrdinalIgnoreCase))
        {
            string obfuscatedEmail = new([.. user.EmailAddress.Select(c => char.IsLetterOrDigit(c) ? '*' : c)]);
            GetBasicUserDTO dto = user.ToBasicDTO();
            return Ok(dto with { EmailAddress = obfuscatedEmail });
        }

        LogUnknownUserRole(role);

        return BadRequest($@"Unknown User Role ""{role}""");
    }
}
