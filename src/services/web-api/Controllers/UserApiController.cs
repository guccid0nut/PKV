// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using MERRICK.DatabaseContext.Constants;

using MERRICK.DatabaseContext.Services;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging.Abstractions;
using NEXUS.Contracts.Clients;
using NEXUS.Contracts.Data;
using ZORGATH.WebPortal.API.Handlers;
using ZORGATH.WebPortal.API.Infrastructure.Attributes;

namespace ZORGATH.WebPortal.API.Controllers;

/// <summary>
/// Core internal and public user account management API, handling identity lookups, authentication tokens, credentials, and telemetry.
/// </summary>
[ApiController]
[Route("api/users")]
[Produces("application/json")]
[EnableRateLimiting(RateLimiterPolicies.Relaxed)]
[RequireInternalServiceKey]
public class UserApiController(
    IAccountRepository accountRepo,
    IInventoryRepository inventoryRepo,
    ISessionRepository sessionRepo,
    ILogger<UserApiController>? logger = null,
    TimeProvider? timeProvider = null,
    Argon2idHasher? argon2Hasher = null) : ControllerBase
{
    private readonly ILogger<UserApiController> _logger = logger ?? NullLogger<UserApiController>.Instance;
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
    private readonly Argon2idHasher _argon2Hasher = argon2Hasher ?? new();

    /// <summary>
    /// Retrieves a complete user profile and account details by primary user ID.
    /// </summary>
    /// <param name="id">The unique numeric user ID.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The detailed user profile DTO if found.</returns>
    /// <response code="200">User found and returned.</response>
    /// <response code="404">User not found with specified ID.</response>
    [HttpGet("{id:int}")]
    [RequireInternalServiceKey]
    [ProducesResponseType(typeof(UserDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetUserById(int id, CancellationToken ct = default)
    {
        _logger.LogDebug("[UserApiController] GetUserById #{UserId}", id);
        User? user = await accountRepo.GetUserByIdAsync(id, ct);
        return user != null ? Ok(user.ToDto()) : NotFound();
    }

    /// <summary>
    /// Retrieves a user profile by registered email address.
    /// </summary>
    /// <param name="email">Email address query.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>User profile DTO.</returns>
    /// <response code="200">User found and returned.</response>
    /// <response code="400">Email query parameter is empty.</response>
    /// <response code="404">User not found.</response>
    [HttpGet("by-email")]
    [RequireInternalServiceKey]
    [ProducesResponseType(typeof(UserDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetUserByEmail([FromQuery] string email, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(email)) return BadRequest("Email is required");
        _logger.LogDebug("[UserApiController] GetUserByEmail '{Email}'", email);
        User? user = await accountRepo.GetUserByEmailAsync(email, ct);
        return user != null ? Ok(user.ToDto()) : NotFound();
    }

    /// <summary>
    /// Retrieves a user profile by linked Discord snowflake ID.
    /// </summary>
    /// <param name="discordId">Discord snowflake ID.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>User profile DTO.</returns>
    /// <response code="200">User found and returned.</response>
    /// <response code="400">Discord ID is empty.</response>
    /// <response code="404">User not found.</response>
    [HttpGet("by-discord/{discordId}")]
    [RequireInternalServiceKey]
    [ProducesResponseType(typeof(UserDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetUserByDiscordId(string discordId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(discordId)) return BadRequest("Discord ID is required");
        _logger.LogDebug("[UserApiController] GetUserByDiscordId '{DiscordId}'", discordId);
        User? user = await accountRepo.GetUserByDiscordIdAsync(discordId, ct);
        return user != null ? Ok(user.ToDto()) : NotFound();
    }

    /// <summary>
    /// Retrieves a user profile by in-game account nickname.
    /// </summary>
    /// <param name="accountName">In-game account nickname.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>User profile DTO.</returns>
    /// <response code="200">User found and returned.</response>
    /// <response code="400">Account name is empty.</response>
    /// <response code="404">User not found.</response>
    [HttpGet("by-account-name/{accountName}")]
    [HttpGet("by-account/{accountName}")]
    [RequireInternalServiceKey]
    [ProducesResponseType(typeof(UserDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetUserByAccountName(string accountName, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(accountName)) return BadRequest("Account name is required");
        _logger.LogDebug("[UserApiController] GetUserByAccountName '{AccountName}'", accountName);
        User? user = await accountRepo.GetUserByAccountNameAsync(accountName, ct);
        return user != null ? Ok(user.ToDto()) : NotFound();
    }

    /// <summary>
    /// Retrieves specific sub-account details by in-game nickname.
    /// </summary>
    /// <param name="name">In-game nickname.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Account DTO.</returns>
    /// <response code="200">Account found and returned.</response>
    /// <response code="400">Account name is empty.</response>
    /// <response code="404">Account not found.</response>
    [HttpGet("accounts/by-name/{name}")]
    [RequireInternalServiceKey]
    [ProducesResponseType(typeof(AccountDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetAccountByName(string name, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(name)) return BadRequest("Account name is required");
        _logger.LogDebug("[UserApiController] GetAccountByName '{Name}'", name);
        Account? account = await accountRepo.GetByNicknameAsync(name, ct);
        return account != null ? Ok(account.ToDto()) : NotFound();
    }

    /// <summary>
    /// Retrieves specific sub-account details by internal numeric account ID.
    /// </summary>
    /// <param name="id">Account numeric ID.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Account DTO.</returns>
    /// <response code="200">Account found and returned.</response>
    /// <response code="404">Account not found.</response>
    [HttpGet("accounts/{id:int}")]
    [HttpGet("accounts/by-id/{id:int}")]
    [RequireInternalServiceKey]
    [ProducesResponseType(typeof(AccountDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetAccountById(int id, CancellationToken ct = default)
    {
        _logger.LogDebug("[UserApiController] GetAccountById #{AccountId}", id);
        Account? account = await accountRepo.GetByIdAsync(id, ct);
        return account != null ? Ok(account.ToDto()) : NotFound();
    }

    /// <summary>
    /// Public non-sensitive profile lookup for the Codex and Leaderboards, completely sanitized of PII.
    /// </summary>
    /// <param name="accountName">In-game account nickname.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Publicly viewable profile metadata.</returns>
    /// <response code="200">Profile found and returned.</response>
    /// <response code="400">Account name parameter is empty.</response>
    /// <response code="404">Account not found.</response>
    [HttpGet("public/{accountName}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetPublicProfile(string accountName, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(accountName)) return BadRequest("Account name is required");
        _logger.LogDebug("[UserApiController] GetPublicProfile '{AccountName}'", accountName);
        Account? account = await accountRepo.GetByNicknameAsync(accountName, ct);
        if (account == null) return NotFound();

        return Ok(new
        {
            account.ID,
            account.Name,
            account.Clan?.Tag,
            ClanName = account.Clan?.Name,
            account.ClanTier,
            account.AscensionLevel,
            RoleName = account.User?.Role?.Name ?? "Player",
            account.TimestampCreated
        });
    }

    /// <summary>
    /// Checks whether an in-game account nickname is already registered.
    /// </summary>
    /// <param name="name">Account nickname via route.</param>
    /// <param name="queryName">Account nickname via query parameter fallback.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>True if the nickname exists; false otherwise.</returns>
    /// <response code="200">Lookup completed successfully.</response>
    /// <response code="400">Account name was not supplied.</response>
    [HttpGet("accounts/exists/{name}")]
    [HttpGet("accounts/exists")]
    [ProducesResponseType(typeof(bool), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> ExistsAccountName([FromRoute] string? name, [FromQuery(Name = "name")] string? queryName, CancellationToken ct = default)
    {
        string? targetName = !string.IsNullOrWhiteSpace(name) ? name : queryName;
        if (string.IsNullOrWhiteSpace(targetName)) return BadRequest("Account name is required");
        bool exists = await accountRepo.ExistsByNicknameAsync(targetName, ct);
        return Ok(exists);
    }

    /// <summary>
    /// Checks whether an email address is already associated with an existing user.
    /// </summary>
    /// <param name="email">Email address to query.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>True if the email exists; false otherwise.</returns>
    /// <response code="200">Lookup completed successfully.</response>
    [HttpGet("exists-email")]
    [ProducesResponseType(typeof(bool), StatusCodes.Status200OK)]
    public async Task<IActionResult> ExistsEmail([FromQuery] string email, CancellationToken ct = default)
    {
        bool exists = await accountRepo.ExistsByEmailAsync(email, ct);
        return Ok(exists);
    }

    /// <summary>
    /// Authenticates or registers a user via Discord OAuth credentials.
    /// </summary>
    /// <param name="payload">Discord auth payload with snowflake ID, username, and avatar.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The authenticated user profile DTO.</returns>
    /// <response code="200">Discord user authenticated or provisioned successfully.</response>
    /// <response code="400">Discord ID is missing.</response>
    [HttpPost("discord-auth")]
    [ProducesResponseType(typeof(UserDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> RegisterOrLoginDiscord([FromBody] DiscordAuthRequestDto payload,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(payload.DiscordId)) return BadRequest("Discord ID is required");
        _logger.LogInformation("[UserApiController] RegisterOrLoginDiscord DiscordId '{DiscordId}'", payload.DiscordId);
        User user = await accountRepo.RegisterOrLoginDiscordUserAsync(payload, ct);
        return Ok(user.ToDto());
    }

    /// <summary>
    /// Registers a new user account with initial in-game nickname and multi-protocol password hashes.
    /// </summary>
    /// <param name="payload">User registration payload.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The newly created user profile.</returns>
    /// <response code="201">User registered successfully.</response>
    /// <response code="400">Missing required email or nickname.</response>
    /// <response code="409">Email or nickname already exists.</response>
    [HttpPost("register")]
    [ProducesResponseType(typeof(UserDto), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> RegisterUser([FromBody] RegisterUserRequestDto payload, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(payload.EmailAddress)) return BadRequest("Email is required");
        if (string.IsNullOrWhiteSpace(payload.InGameNickname)) return BadRequest("In-game nickname is required");

        if (await accountRepo.ExistsByEmailAsync(payload.EmailAddress, ct))
            return Conflict("Email already exists");
        if (await accountRepo.ExistsByNicknameAsync(payload.InGameNickname, ct))
            return Conflict("In-game nickname already exists");

        Role? role = await accountRepo.GetRoleByNameAsync(UserRoles.User, ct);
        role ??= new Role { Name = UserRoles.User };

        string salt = SRPRegistrationHandlers.GenerateSRPPasswordSalt();
        string srpHash = SRPRegistrationHandlers.ComputeSRPPasswordHash(payload.PlainTextPassword, salt);

        User user = new()
        {
            EmailAddress = payload.EmailAddress,
            Role = role,
            SRPPasswordSalt = salt,
            SRPPasswordHash = srpHash,
            DiscordId = payload.DiscordId,
            DiscordUsername = payload.DiscordUsername,
            DiscordAvatar = payload.AvatarHash
        };
        user.PBKDF2PasswordHash = new PasswordHasher<User>().HashPassword(user, payload.PlainTextPassword);
        user.Argon2idPasswordHash = await _argon2Hasher.HashPasswordAsync(payload.PlainTextPassword, ct);
        await accountRepo.CreateUserAsync(user, ct);

        Account account = new()
        {
            Name = payload.InGameNickname,
            User = user,
            UserID = user.ID,
            Type = AccountType.Normal,
            IsMain = true
        };
        await accountRepo.CreateAsync(account, ct);

        User? createdUser = await accountRepo.GetUserByIdAsync(user.ID, ct);
        return CreatedAtAction(nameof(GetUserById), new { id = user.ID }, (createdUser ?? user).ToDto());
    }

    /// <summary>
    /// Authenticates a player via account name or email, performing shadow-upgrade from PBKDF2 to Argon2id upon successful verification.
    /// </summary>
    /// <param name="payload">Credentials including login handle and plain password.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Issued session authentication token and expiration timestamp.</returns>
    /// <response code="200">Authenticated successfully.</response>
    /// <response code="400">Missing login field.</response>
    /// <response code="401">Invalid credentials provided.</response>
    /// <response code="404">User not found.</response>
    [HttpPost("login")]
    [EnableRateLimiting(RateLimiterPolicies.Strict)]
    [ProducesResponseType(typeof(AuthenticationTokenResponseDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> LogInUser([FromBody] LogInUserRequestDto payload, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(payload.Login)) return BadRequest("Login is required");
        User? user = await accountRepo.GetUserByAccountNameAsync(payload.Login, ct)
                     ?? await accountRepo.GetUserByEmailAsync(payload.Login, ct);
        if (user == null) return NotFound("User not found");

        bool authenticated = false;
        if (!string.IsNullOrEmpty(user.Argon2idPasswordHash))
        {
            authenticated = await _argon2Hasher.VerifyPasswordAsync(payload.Password, user.Argon2idPasswordHash, ct);
        }
        else if (!string.IsNullOrEmpty(user.PBKDF2PasswordHash))
        {
            PasswordVerificationResult verifyResult = new PasswordHasher<User>().VerifyHashedPassword(user, user.PBKDF2PasswordHash, payload.Password);
            authenticated = verifyResult != PasswordVerificationResult.Failed;

            if (authenticated)
            {
                string argon2Hash = await _argon2Hasher.HashPasswordAsync(payload.Password, ct);
                await accountRepo.UpgradeUserArgon2idHashAsync(user.ID, argon2Hash, ct);
                _logger.LogInformation("[UserApiController] User #{UserId} shadow-upgraded to Argon2id", user.ID);
            }
        }

        if (!authenticated &&
            string.IsNullOrEmpty(user.Argon2idPasswordHash) &&
            string.IsNullOrEmpty(user.PBKDF2PasswordHash) &&
            !string.IsNullOrEmpty(user.SRPPasswordSalt) &&
            !string.IsNullOrEmpty(user.SRPPasswordHash))
        {
            string computedSrp = SRPRegistrationHandlers.ComputeSRPPasswordHash(
                payload.Password,
                user.SRPPasswordSalt,
                passwordIsHashed: false);
            authenticated = System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
                System.Text.Encoding.UTF8.GetBytes(computedSrp),
                System.Text.Encoding.UTF8.GetBytes(user.SRPPasswordHash));
        }

        if (!authenticated)
        {
            return Unauthorized("Invalid credentials");
        }

        Token token = await sessionRepo.IssueTokenAsync(user.ID, TokenPurpose.Session, TimeSpan.FromDays(7), ct);
        return Ok(new AuthenticationTokenResponseDto(token.Value.ToString(), token.Expires ?? _timeProvider.GetUtcNow().AddDays(7)));
    }

    /// <summary>
    /// Configures the engine SRP and PBKDF2 authentication credentials for in-game client login.
    /// </summary>
    /// <param name="userId">Numeric user ID.</param>
    /// <param name="req">SRP salt and computed SRP/PBKDF2 hashes.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>True if password was updated.</returns>
    /// <response code="200">Credentials updated successfully.</response>
    /// <response code="404">User not found.</response>
    [HttpPost("{userId:int}/set-ingame-password")]
    [HttpPost("{userId:int}/in-game-password")]
    [ProducesResponseType(typeof(bool), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> SetInGamePassword(int userId, [FromBody] SetInGamePasswordRequestDto req,
        CancellationToken ct = default)
    {
        _logger.LogInformation("[UserApiController] SetInGamePassword for User #{UserId}", userId);
        bool success = await accountRepo.SetInGamePasswordAsync(userId, req.Salt, req.SrpHash, req.Pbkdf2Hash, argon2idHash: null, ct);
        if (!success) return NotFound("User not found");
        return Ok(true);
    }

    /// <summary>
    /// Links a verified Twitch account to the user profile for stream rewards and presence.
    /// </summary>
    /// <param name="userId">Numeric user ID.</param>
    /// <param name="req">Twitch ID and username.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>True if link succeeded.</returns>
    /// <response code="200">Twitch profile linked.</response>
    /// <response code="404">User not found.</response>
    [HttpPost("{userId:int}/link-twitch")]
    [ProducesResponseType(typeof(bool), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> LinkTwitch(int userId, [FromBody] LinkTwitchRequestDto req,
        CancellationToken ct = default)
    {
        _logger.LogInformation("[UserApiController] LinkTwitch for User #{UserId} ('{TwitchUsername}')", userId,
            req.TwitchUsername);
        bool success = await accountRepo.LinkTwitchAsync(userId, req.TwitchId, req.TwitchUsername, ct);
        if (!success) return NotFound("User not found");
        return Ok(true);
    }

    /// <summary>
    /// Unlinks any associated Twitch account from the user profile.
    /// </summary>
    /// <param name="userId">Numeric user ID.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>True if unlinked.</returns>
    /// <response code="200">Twitch profile unlinked.</response>
    /// <response code="404">User not found.</response>
    [HttpPost("{userId:int}/unlink-twitch")]
    [ProducesResponseType(typeof(bool), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> UnlinkTwitch(int userId, CancellationToken ct = default)
    {
        _logger.LogInformation("[UserApiController] UnlinkTwitch for User #{UserId}", userId);
        bool success = await accountRepo.UnlinkTwitchAsync(userId, ct);
        if (!success) return NotFound("User not found");
        return Ok(true);
    }

    /// <summary>
    /// Submits an application to host dedicated game server instances.
    /// </summary>
    /// <param name="req">Host specifications, instance count, location, and prefix.</param>
    /// <param name="accountName">Optional verified applicant account nickname forwarded by the BFF.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>True if application was recorded.</returns>
    /// <response code="200">Host application submitted.</response>
    /// <response code="400">Application already pending or account not found.</response>
    [HttpPost("apply-host")]
    [ProducesResponseType(typeof(bool), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> ApplyHost([FromBody] ApplyHostRequestDto req, [FromQuery] string? accountName = null, CancellationToken ct = default)
    {
        string? candidateAccountName = !string.IsNullOrWhiteSpace(accountName)
            ? accountName.Trim()
            : HttpContext?.Request.Headers["X-Authenticated-Account"].FirstOrDefault()?.Trim();
        if (string.IsNullOrWhiteSpace(candidateAccountName))
        {
            candidateAccountName = req.ServerNamePrefix?.Trim();
        }

        Account? verifiedAccount = !string.IsNullOrWhiteSpace(candidateAccountName)
            ? await accountRepo.GetByNicknameAsync(candidateAccountName, ct)
            : null;
        if (verifiedAccount == null)
        {
            _logger.LogWarning(
                "[SECURITY-ALERT] [UserApiController] Rejected ApplyHost with unverified applicant '{CandidateAccount}' and ServerNamePrefix '{ServerNamePrefix}' from {RemoteIp} — blocked Account #1 fallback",
                candidateAccountName,
                req.ServerNamePrefix,
                HttpContext?.Connection.RemoteIpAddress?.ToString() ?? "unknown");
            return BadRequest("Host application cannot be submitted (verified applicant account not found)");
        }

        _logger.LogInformation("[UserApiController] ApplyHost for verified account '{AccountName}' with ServerNamePrefix '{ServerNamePrefix}'",
            verifiedAccount.Name,
            req.ServerNamePrefix);
        HostApplication? application = await accountRepo.CreateHostApplicationForAccountAsync(verifiedAccount.Name, req.ServerNamePrefix, req.Location,
            req.Instances, req.ApplicationNotes, ct);
        if (application == null)
            return BadRequest("Host application cannot be submitted (already pending or account not found)");

        return Ok(true);
    }

    /// <summary>
    /// Submits an application for verified streamer partner status.
    /// </summary>
    /// <param name="req">Channel URL, Twitch metadata, and notes.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>True if application was recorded.</returns>
    /// <response code="200">Streamer application submitted.</response>
    /// <response code="400">Application already pending or user not found.</response>
    [HttpPost("apply-streamer")]
    [ProducesResponseType(typeof(bool), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> ApplyStreamer([FromBody] ApplyStreamerClientDto req,
        CancellationToken ct = default)
    {
        _logger.LogInformation("[UserApiController] ApplyStreamer for User #{UserId}", req.UserId);
        string twitchUser = !string.IsNullOrEmpty(req.TwitchUsername)
            ? req.TwitchUsername
            : !string.IsNullOrEmpty(req.TwitchChannelUrl)
                ? req.TwitchChannelUrl.TrimEnd('/').Split('/').LastOrDefault() ?? "Unknown"
                : "Unknown";

        StreamerApplication? app = await accountRepo.CreateOrUpdateStreamerApplicationAsync(req.UserId, twitchUser,
            req.TwitchId, req.TwitchChannelUrl, req.ApplicationNotes, ct);
        if (app == null) return BadRequest("Streamer application is already pending or user not found");
        return Ok(true);
    }

    /// <summary>
    /// Retrieves the current user's streamer partner application status.
    /// </summary>
    /// <param name="userId">Numeric user ID.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Streamer application details DTO.</returns>
    /// <response code="200">Application found.</response>
    /// <response code="404">No application found.</response>
    [HttpGet("{userId:int}/streamer-app")]
    [ProducesResponseType(typeof(StreamerApplicationDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetStreamerApplication(int userId, CancellationToken ct = default)
    {
        _logger.LogDebug("[UserApiController] GetStreamerApplication for User #{UserId}", userId);
        StreamerApplication? app = await accountRepo.GetStreamerApplicationByUserIdAsync(userId, ct);
        return app != null ? Ok(app.ToDto()) : NotFound();
    }

    /// <summary>
    /// Retrieves the current user's dedicated server host application status.
    /// </summary>
    /// <param name="userId">Numeric user ID.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Host application details DTO.</returns>
    /// <response code="200">Application found.</response>
    /// <response code="404">No application found.</response>
    [HttpGet("{userId:int}/host-app")]
    [ProducesResponseType(typeof(HostApplicationDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetHostApplication(int userId, CancellationToken ct = default)
    {
        _logger.LogDebug("[UserApiController] GetHostApplication for User #{UserId}", userId);
        HostApplication? app = await accountRepo.GetHostApplicationByUserIdAsync(userId, ct);
        return app != null ? Ok(app.ToDto()) : NotFound();
    }

    /// <summary>
    /// Lists all web portal cosmetic items, badges, and titles unlocked by the user.
    /// </summary>
    /// <param name="userId">Numeric user ID.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Collection of unlocked web rewards.</returns>
    /// <response code="200">Rewards collection returned.</response>
    [HttpGet("{userId:int}/web-rewards")]
    [ProducesResponseType(typeof(List<UserWebRewardDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetUserWebRewards(int userId, CancellationToken ct = default)
    {
        _logger.LogDebug("[UserApiController] GetUserWebRewards for User #{UserId}", userId);
        IReadOnlyList<UserWebReward> rewards = await inventoryRepo.GetUserWebRewardsAsync(userId, ct);
        return Ok(rewards.Select(r => r.ToDto()).ToList());
    }

    /// <summary>
    /// Equips an unlocked web title, badge, or cosmetic border on the user's web profile.
    /// </summary>
    /// <param name="userId">Numeric user ID.</param>
    /// <param name="req">Reward type and item code.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>True if equipped successfully.</returns>
    /// <response code="200">Reward equipped.</response>
    [HttpPost("{userId:int}/equip-web-reward")]
    [ProducesResponseType(typeof(bool), StatusCodes.Status200OK)]
    public async Task<IActionResult> EquipWebReward(int userId, [FromBody] EquipWebRewardRequestDto req,
        CancellationToken ct = default)
    {
        _logger.LogInformation(
            "[UserApiController] EquipWebReward for User #{UserId}, Type '{RewardType}', Code '{RewardCode}'", userId,
            req.RewardType, req.RewardCode);
        bool success = await inventoryRepo.EquipWebRewardAsync(userId, req.RewardType, req.RewardCode, ct);
        return Ok(success);
    }

    /// <summary>
    /// Retrieves real-time telemetry, tick pacing, and match counts for a host operator's fleet.
    /// </summary>
    /// <param name="accountName">Operator account name.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Host telemetry metrics summary.</returns>
    /// <response code="200">Telemetry returned.</response>
    /// <response code="404">Account not found.</response>
    [HttpGet("host-telemetry/{accountName}")]
    [HttpGet("accounts/{accountName}/host-telemetry")]
    [ProducesResponseType(typeof(HostTelemetryResultDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetHostTelemetry(string accountName, CancellationToken ct = default)
    {
        _logger.LogDebug("[UserApiController] GetHostTelemetry for Account '{AccountName}'", accountName);
        HostTelemetryResultDto? dto = await accountRepo.GetHostTelemetryAsync(accountName, ct);
        if (dto == null) return NotFound("Account not found");
        return Ok(dto);
    }

    /// <summary>
    /// Validates a bearer or session token against the active session store.
    /// </summary>
    /// <param name="req">Token string and expected purpose.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Validated token details if valid.</returns>
    /// <response code="200">Token is valid.</response>
    /// <response code="404">Token invalid, expired, or purpose mismatch.</response>
    [HttpPost("tokens/validate")]
    [ProducesResponseType(typeof(TokenDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> ValidateToken([FromBody] ValidateTokenRequestDto req,
        CancellationToken ct = default)
    {
        _logger.LogDebug("[UserApiController] ValidateToken Purpose {Purpose}", req.Purpose);
        Token? token = await sessionRepo.ValidateTokenAsync(req.TokenString, (TokenPurpose)req.Purpose, ct);
        return token != null ? Ok(token.ToDto()) : NotFound();
    }

    /// <summary>
    /// Issues a new scoped token for a user with defined TTL.
    /// </summary>
    /// <param name="req">Target user ID, purpose, and TTL seconds.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Newly issued token details.</returns>
    /// <response code="200">Token issued successfully.</response>
    [HttpPost("tokens/issue")]
    [ProducesResponseType(typeof(TokenDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> IssueToken([FromBody] IssueTokenRequestDto req, CancellationToken ct = default)
    {
        _logger.LogInformation("[UserApiController] IssueToken for User #{UserId}, Purpose {Purpose}", req.UserId,
            req.Purpose);
        Token token = await sessionRepo.IssueTokenAsync(req.UserId, (TokenPurpose)req.Purpose,
            TimeSpan.FromSeconds(req.TtlSeconds), ct);
        return Ok(token.ToDto());
    }

    /// <summary>
    /// Explicitly invalidates and revokes a token from the session store.
    /// </summary>
    /// <param name="req">Token string to revoke.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>True if revoked.</returns>
    /// <response code="200">Token revoked.</response>
    [HttpPost("tokens/invalidate")]
    [ProducesResponseType(typeof(bool), StatusCodes.Status200OK)]
    public async Task<IActionResult> InvalidateToken([FromBody] InvalidateTokenRequestDto req,
        CancellationToken ct = default)
    {
        _logger.LogInformation("[UserApiController] InvalidateToken");
        await sessionRepo.InvalidateTokenAsync(req.TokenString, ct);
        return Ok(true);
    }
}

public record SetInGamePasswordRequestDto(string Salt, string SrpHash, string? Pbkdf2Hash = null);

public record LinkTwitchRequestDto(string TwitchId, string TwitchUsername);

public record EquipWebRewardRequestDto(string RewardType, string RewardCode);

public record ValidateTokenRequestDto(string TokenString, int Purpose);

public record IssueTokenRequestDto(int UserId, int Purpose, int TtlSeconds);

public record InvalidateTokenRequestDto(string TokenString);
