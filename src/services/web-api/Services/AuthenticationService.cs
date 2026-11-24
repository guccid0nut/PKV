// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Globalization;
using MERRICK.DatabaseContext.Services;
using NEXUS.Contracts.Data;

namespace ZORGATH.WebPortal.API.Services;

public partial class AuthenticationService(
    IAccountRepository accountRepository,
    IOptions<OperationalConfiguration> configuration,
    ILogger<AuthenticationService> logger,
    TimeProvider? timeProvider = null,
    Argon2idHasher? argon2Hasher = null) : IAuthenticationService
{
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
    private readonly Argon2idHasher _argon2Hasher = argon2Hasher ?? new();

    public async Task<ServiceResult<GetAuthenticationTokenDTO>> AuthenticateUserAsync(LogInUserDTO payload,
        CancellationToken cancellationToken = default)
    {
        Account? account = await accountRepository.GetByNicknameAsync(payload.Name, cancellationToken);

        if (account is null)
        {
            LogAccountNotFound(payload.Name);
            return ServiceResult.Failure<GetAuthenticationTokenDTO>($@"Account ""{payload.Name}"" Was Not Found",
                ServiceErrorType.NotFound);
        }

        User user = account.User;

        bool authenticated;
        if (!string.IsNullOrEmpty(user.Argon2idPasswordHash))
        {
            authenticated = await _argon2Hasher.VerifyPasswordAsync(payload.Password, user.Argon2idPasswordHash, cancellationToken);
        }
        else
        {
            PasswordVerificationResult result =
                new PasswordHasher<User>().VerifyHashedPassword(user, user.PBKDF2PasswordHash, payload.Password);
            authenticated = result is PasswordVerificationResult.Success;

            if (authenticated)
            {
                // Shadow upgrade: generate Argon2id hash and persist atomically
                string argon2Hash = await _argon2Hasher.HashPasswordAsync(payload.Password, cancellationToken);
                await accountRepository.UpgradeUserArgon2idHashAsync(user.ID, argon2Hash, cancellationToken);
                LogArgon2idUpgrade(user.ID, account.Name);
            }
        }

        if (!authenticated)
        {
            LogInvalidPassword(payload.Name);
            return ServiceResult.Failure<GetAuthenticationTokenDTO>("Invalid User Name And/Or Password",
                ServiceErrorType.Unauthorized);
        }

        if (!new[] { UserRoles.Administrator, UserRoles.User }.Contains(user.Role.Name))
        {
            LogUnknownRole(user.Role.Name, account.Name);
            return ServiceResult.Failure<GetAuthenticationTokenDTO>($@"Unknown User Role ""{user.Role.Name}""",
                ServiceErrorType.UnprocessableEntity);
        }

        IEnumerable<Claim> userRoleClaims = user.Role.Name is UserRoles.Administrator
            ? UserRoleClaims.Administrator
            : UserRoleClaims.User;

        IEnumerable<Claim> openIDClaims =
        [
            new(JwtRegisteredClaimNames.Sub, account.Name, ClaimValueTypes.String),
            new(JwtRegisteredClaimNames.Iat,
                _timeProvider.GetUtcNow().ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture),
                ClaimValueTypes.Integer64),
            new(JwtRegisteredClaimNames.AuthTime,
                _timeProvider.GetUtcNow().ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture),
                ClaimValueTypes.Integer64),
            new(JwtRegisteredClaimNames.Nonce, Guid.CreateVersion7().ToString(), ClaimValueTypes.String),
            new(JwtRegisteredClaimNames.Jti, Guid.CreateVersion7().ToString(), ClaimValueTypes.String),
            new(JwtRegisteredClaimNames.Email, user.EmailAddress, ClaimValueTypes.Email)
        ];

        IEnumerable<Claim> customClaims =
        [
            new(Claims.UserID, user.ID.ToString(CultureInfo.InvariantCulture), ClaimValueTypes.String),
            new(Claims.AccountID, account.ID.ToString(CultureInfo.InvariantCulture), ClaimValueTypes.String),
            new(Claims.AccountIsMain, account.IsMain.ToString(), ClaimValueTypes.Boolean),
            new(Claims.ClanName, account.Clan?.Name ?? string.Empty, ClaimValueTypes.String),
            new(Claims.ClanTag, account.Clan?.Tag ?? string.Empty, ClaimValueTypes.String)
        ];

        IEnumerable<Claim> allTokenClaims = Enumerable.Empty<Claim>().Union(userRoleClaims).Union(openIDClaims)
            .Union(customClaims).OrderBy(claim => claim.Type);

        OperationalConfiguration config = configuration.Value;

        JwtSecurityToken token = new
        (
            config.JWT.Issuer,
            config.JWT.Audience,
            allTokenClaims,
            expires: _timeProvider.GetUtcNow().AddHours(config.JWT.DurationInHours).DateTime,
            signingCredentials: new SigningCredentials(
                new SymmetricSecurityKey(Encoding.UTF8.GetBytes(config.JWT.SigningKey)),
                SecurityAlgorithms.HmacSha256)
        );

        string jwt = new JwtSecurityTokenHandler().WriteToken(token);

        LogLoginSuccess(account.Name, user.ID);

        return ServiceResult.Success(new GetAuthenticationTokenDTO(user.ID, "JWT", jwt));
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "[Auth] Account not found: {AccountName}")]
    private partial void LogAccountNotFound(string accountName);

    [LoggerMessage(Level = LogLevel.Warning, Message = "[Auth] Invalid password for account: {AccountName}")]
    private partial void LogInvalidPassword(string accountName);

    [LoggerMessage(Level = LogLevel.Error, Message = "[Auth] Unknown/Invalid Role '{Role}' for account: {AccountName}")]
    private partial void LogUnknownRole(string role, string accountName);

    [LoggerMessage(Level = LogLevel.Information, Message = "[Auth] Login success: {AccountName} (ID: {Id})")]
    private partial void LogLoginSuccess(string accountName, int id);

    [LoggerMessage(Level = LogLevel.Information, Message = "[Auth] User #{UserId} ('{AccountName}') successfully shadow-upgraded to Argon2id")]
    private partial void LogArgon2idUpgrade(int userId, string accountName);
}
