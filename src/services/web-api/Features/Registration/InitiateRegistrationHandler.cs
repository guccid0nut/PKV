// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using MediatR;
using MERRICK.DatabaseContext.Entities.Core;
using MERRICK.DatabaseContext.Queries;
using NEXUS.Contracts.CQRS.Registration;

namespace ZORGATH.WebPortal.API.Features.Registration;

public sealed class InitiateRegistrationHandler(
    MerrickContext databaseContext,
    IEmailService emailService,
    ILogger<InitiateRegistrationHandler> logger,
    TimeProvider? timeProvider = null)
    : IRequestHandler<InitiateRegistrationCommand, InitiateRegistrationResult>
{
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    public async Task<InitiateRegistrationResult> Handle(
        InitiateRegistrationCommand request,
        CancellationToken cancellationToken)
    {
        logger.LogInformation("Initiating registration for {Email}", request.EmailAddress);

        if (await CompiledQueries.UserExistsByEmailQuery(databaseContext, request.EmailAddress))
        {
            logger.LogWarning("Registration conflict: User with email '{Email}' already exists", request.EmailAddress);
            return InitiateRegistrationResult.Conflict($@"User With Email ""{request.EmailAddress}"" Already Exists");
        }

        Token token = new()
        {
            Purpose = TokenPurpose.EmailAddressVerification,
            Data = request.EmailAddress,
            EmailAddress = request.EmailAddress,
            Value = Guid.NewGuid(),
            TimestampCreated = _timeProvider.GetUtcNow()
        };

        await databaseContext.Tokens.AddAsync(token, cancellationToken);
        await databaseContext.SaveChangesAsync(cancellationToken);

        bool emailSent = await emailService.SendEmailAddressRegistrationLink(
            request.EmailAddress,
            token.Value.ToString(),
            cancellationToken);

        if (!emailSent)
        {
            logger.LogError("Failed to deliver verification email to {Email}", request.EmailAddress);
            return InitiateRegistrationResult.BadRequest("Failed to send verification email.");
        }

        logger.LogInformation("Verification email successfully dispatched to {Email}", request.EmailAddress);
        return InitiateRegistrationResult.Succeeded("Verification email sent.");
    }
}
