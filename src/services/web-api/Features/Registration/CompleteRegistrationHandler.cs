// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using ASPIRE.Common.Security;
using FluentValidation.Results;
using MediatR;
using MERRICK.DatabaseContext.Entities.Core;
using MERRICK.DatabaseContext.Mappings;
using MERRICK.DatabaseContext.Queries;
using MERRICK.DatabaseContext.Services;
using Microsoft.AspNetCore.Identity;
using NEXUS.Contracts.CQRS.Registration;
using ZORGATH.WebPortal.API.Handlers;
using ZORGATH.WebPortal.API.Validators;

namespace ZORGATH.WebPortal.API.Features.Registration;

public sealed class CompleteRegistrationHandler(
    MerrickContext databaseContext,
    IEmailService emailService,
    PasswordValidator passwordValidator,
    ILogger<CompleteRegistrationHandler> logger,
    TimeProvider? timeProvider = null,
    Argon2idHasher? argon2Hasher = null)
    : IRequestHandler<CompleteRegistrationCommand, RegistrationResult>
{
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
    private readonly Argon2idHasher _argon2Hasher = argon2Hasher ?? new();

    public async Task<RegistrationResult> Handle(
        CompleteRegistrationCommand request,
        CancellationToken cancellationToken)
    {
        logger.LogInformation("Processing complete registration for account {Name}", request.Name);

        ValidationResult validationResult = await passwordValidator.ValidateAsync(request.Password, cancellationToken);
        if (!validationResult.IsValid)
        {
            string err = string.Join(", ", validationResult.Errors.Select(e => e.ErrorMessage));
            logger.LogWarning("Password complexity failed for {Name}: {Error}", request.Name, err);
            return RegistrationResult.BadRequest(err);
        }

        if (!PerimeterSecuritySanitizer.IsValidNickname(request.Name))
        {
            logger.LogWarning("Invalid nickname format for {Name}", request.Name);
            return RegistrationResult.BadRequest("Account nickname must be between 3 and 15 alphanumeric characters or underscores.");
        }

        if (!Guid.TryParse(request.Token, out Guid parsedTokenGuid))
        {
            logger.LogWarning("Malformed registration token guid: {Token}", request.Token);
            return RegistrationResult.NotFound($@"Registration Token ""{request.Token}"" Was Not Found");
        }

        Token? token = await databaseContext.Tokens.SingleOrDefaultAsync(t =>
            t.Value == parsedTokenGuid &&
            t.Purpose == TokenPurpose.EmailAddressVerification, cancellationToken);

        if (token is null)
        {
            logger.LogWarning("Token not found: {Token}", request.Token);
            return RegistrationResult.NotFound($@"Registration Token ""{request.Token}"" Was Not Found");
        }

        if (token.TimestampConsumed is not null)
        {
            logger.LogWarning("Token already consumed: {Token}", request.Token);
            return RegistrationResult.Conflict($@"Registration Token ""{request.Token}"" Has Already Been Consumed");
        }

        string sanitizedEmailAddress = token.Data;

        if (await CompiledQueries.UserExistsByEmailQuery(databaseContext, sanitizedEmailAddress))
        {
            logger.LogWarning("Registration conflict: User with email {Email} already exists", sanitizedEmailAddress);
            return RegistrationResult.Conflict($@"User With Email ""{sanitizedEmailAddress}"" Already Exists");
        }

        if (await CompiledQueries.AccountExistsByNameQuery(databaseContext, request.Name))
        {
            logger.LogWarning("Registration conflict: Account with name {Name} already exists", request.Name);
            return RegistrationResult.Conflict($@"Account With Name ""{request.Name}"" Already Exists");
        }

        Role? role = await databaseContext.Roles.SingleOrDefaultAsync(r =>
            r.Name == UserRoles.User, cancellationToken);

        if (role is null)
        {
            logger.LogError("Required system role '{Role}' was not found in database", UserRoles.User);
            return RegistrationResult.NotFound($@"User Role ""{UserRoles.User}"" Was Not Found");
        }

        string salt = SRPRegistrationHandlers.GenerateSRPPasswordSalt();

        User user = new()
        {
            EmailAddress = sanitizedEmailAddress,
            Role = role,
            SRPPasswordSalt = salt,
            SRPPasswordHash = SRPRegistrationHandlers.ComputeSRPPasswordHash(request.Password, salt)
        };

        user.PBKDF2PasswordHash = new PasswordHasher<User>().HashPassword(user, request.Password);
        user.Argon2idPasswordHash = await _argon2Hasher.HashPasswordAsync(request.Password, cancellationToken);

        await databaseContext.Users.AddAsync(user, cancellationToken);

        Account account = new() { Name = request.Name, User = user, IsMain = true };
        user.Accounts.Add(account);

        token.TimestampConsumed = _timeProvider.GetUtcNow();

        await databaseContext.SaveChangesAsync(cancellationToken);

        await emailService.SendEmailAddressRegistrationConfirmation(user.EmailAddress, account.Name, cancellationToken);

        logger.LogInformation("Account {Name} (User #{UserId}) successfully created via verification token", account.Name, user.ID);
        return RegistrationResult.Created(user.ID, user.ToBasicDTO(false));
    }
}
