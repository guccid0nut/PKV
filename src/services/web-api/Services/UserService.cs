// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Diagnostics.CodeAnalysis;
using MERRICK.DatabaseContext.Queries;
using MERRICK.DatabaseContext.Services;

#pragma warning disable CA1304, CA1305, CA1307, CA1311
// ReSharper disable SpecifyACultureInStringConversionExplicitly

namespace ZORGATH.WebPortal.API.Services;

[SuppressMessage("ReSharper", "SpecifyACultureInStringConversionExplicitly",
    Justification = "EF Core LINQ expression tree requiring SQL Server translation")]
public partial class UserService(
    MerrickContext databaseContext,
    IEmailService emailService,
    PasswordValidator passwordValidator,
    ILogger<UserService> logger,
    TimeProvider? timeProvider = null,
    Argon2idHasher? argon2Hasher = null) : IUserService
{
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
    private readonly Argon2idHasher _argon2Hasher = argon2Hasher ?? new();

    public async Task<ServiceResult<User>> RegisterUserAsync(RegisterUserAndMainAccountDTO payload,
        CancellationToken cancellationToken = default)
    {
        if (!string.Equals(payload.Password, payload.ConfirmPassword, StringComparison.Ordinal))
        {
            LogPasswordMismatch();
            return ServiceResult.Failure<User>(
                "Password and confirmation password do not match.",
                ServiceErrorType.BadRequest);
        }

        ValidationResult result = await passwordValidator.ValidateAsync(payload.Password, cancellationToken);

        if (!result.IsValid)
        {
            string errors = string.Join(", ", result.Errors.Select(e => e.ErrorMessage));
            LogPasswordValidationFailed(errors);
            return ServiceResult.ValidationFailure<User>(result.Errors.Select(error => error.ErrorMessage));
        }

        if (!Guid.TryParse(payload.Token, out Guid parsedTokenGuid))
        {
            LogTokenNotFound(SecurityMasking.MaskToken(payload.Token));
            return ServiceResult.Failure<User>(
                $@"Email Registration Token ""{SecurityMasking.MaskToken(payload.Token)}"" Was Not Found",
                ServiceErrorType.NotFound);
        }

        Token? token = await databaseContext.Tokens.SingleOrDefaultAsync(token =>
            token.Value == parsedTokenGuid &&
            token.Purpose == TokenPurpose.EmailAddressVerification, cancellationToken);

        if (token is null)
        {
            LogTokenNotFound(SecurityMasking.MaskToken(payload.Token));
            return ServiceResult.Failure<User>(
                $@"Email Registration Token ""{SecurityMasking.MaskToken(payload.Token)}"" Was Not Found",
                ServiceErrorType.NotFound);
        }

        if (token.TimestampConsumed is not null)
        {
            LogTokenConsumed(SecurityMasking.MaskToken(payload.Token));
            return ServiceResult.Failure<User>(
                $@"Email Registration Token ""{SecurityMasking.MaskToken(payload.Token)}"" Has Already Been Consumed",
                ServiceErrorType.Conflict);
        }

        string sanitizedEmailAddress = token.Data;

        if (await CompiledQueries.UserExistsByEmailQuery(databaseContext, sanitizedEmailAddress))
        {
            LogEmailCollision(sanitizedEmailAddress);
            return ServiceResult.Failure<User>($@"User With Email ""{token.EmailAddress}"" Already Exists",
                ServiceErrorType.Conflict);
        }

        if (await CompiledQueries.AccountExistsByNameQuery(databaseContext, payload.Name))
        {
            LogAccountCollision(payload.Name);
            return ServiceResult.Failure<User>($@"Account With Name ""{payload.Name}"" Already Exists",
                ServiceErrorType.Conflict);
        }

        Role? role = await databaseContext.Roles.SingleOrDefaultAsync(role =>
            role.Name == UserRoles.User, cancellationToken);

        if (role is null)
        {
            LogRoleNotFound(UserRoles.User);
            return ServiceResult.Failure<User>($@"User Role ""{UserRoles.User}"" Was Not Found",
                ServiceErrorType.NotFound);
        }

        string salt = SRPRegistrationHandlers.GenerateSRPPasswordSalt();

        User user = new()
        {
            EmailAddress = sanitizedEmailAddress,
            Role = role,
            SRPPasswordSalt = salt,
            SRPPasswordHash = SRPRegistrationHandlers.ComputeSRPPasswordHash(payload.Password, salt)
        };

        user.PBKDF2PasswordHash = new PasswordHasher<User>().HashPassword(user, payload.Password);
        user.Argon2idPasswordHash = await _argon2Hasher.HashPasswordAsync(payload.Password, cancellationToken);

        await databaseContext.Users.AddAsync(user, cancellationToken);

        Account account = new() { Name = payload.Name, User = user, IsMain = true };

        user.Accounts.Add(account);

        token.TimestampConsumed = _timeProvider.GetUtcNow();

        await databaseContext.SaveChangesAsync(cancellationToken);

        LogRegistrationSuccess(user.ID, account.ID);

        await emailService.SendEmailAddressRegistrationConfirmation(user.EmailAddress, account.Name, cancellationToken);

        return ServiceResult.Success(user);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "[Register] Password confirmation mismatch.")]
    private partial void LogPasswordMismatch();

    [LoggerMessage(Level = LogLevel.Warning, Message = "[Register] Password validation failed: {Errors}")]
    private partial void LogPasswordValidationFailed(string errors);

    [LoggerMessage(Level = LogLevel.Warning, Message = "[Register] Token not found: {TokenPrefix}")]
    private partial void LogTokenNotFound(string tokenPrefix);

    [LoggerMessage(Level = LogLevel.Warning, Message = "[Register] Token already consumed: {TokenPrefix}")]
    private partial void LogTokenConsumed(string tokenPrefix);

    [LoggerMessage(Level = LogLevel.Warning, Message = "[Register] User email collision: {Email}")]
    private partial void LogEmailCollision(string email);

    [LoggerMessage(Level = LogLevel.Warning, Message = "[Register] Account name collision: {Name}")]
    private partial void LogAccountCollision(string name);

    [LoggerMessage(Level = LogLevel.Error, Message = "[Register] User Role '{Role}' not found in database.")]
    private partial void LogRoleNotFound(string role);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "[Register] Success. UserID: {UserID}, AccountID: {AccountID}")]
    private partial void LogRegistrationSuccess(int userId, int accountId);
}
