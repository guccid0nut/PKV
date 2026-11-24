// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using MediatR;
using Microsoft.AspNetCore.Mvc;
using NEXUS.Contracts.CQRS.Email;
using ZORGATH.WebPortal.API.Helpers;

namespace ZORGATH.WebPortal.API.Features.Email;

public sealed class RegisterEmailAddressHandler(
    MerrickContext databaseContext,
    IEmailService emailService,
    IWebHostEnvironment hostEnvironment,
    ILogger<RegisterEmailAddressHandler> logger)
    : IRequestHandler<RegisterEmailAddressCommand, RegisterEmailAddressResult>
{
    public async Task<RegisterEmailAddressResult> Handle(
        RegisterEmailAddressCommand request,
        CancellationToken cancellationToken)
    {
        logger.LogInformation("Processing email registration request for {EmailAddress}", request.EmailAddress);

        if (!string.Equals(request.EmailAddress, request.ConfirmEmailAddress, StringComparison.OrdinalIgnoreCase))
        {
            logger.LogWarning("Email mismatch: Confirm '{Confirm}' does not match '{Email}'",
                request.ConfirmEmailAddress, request.EmailAddress);
            return RegisterEmailAddressResult.BadRequest(
                $@"Email Address ""{request.ConfirmEmailAddress}"" Does Not Match ""{request.EmailAddress}""");
        }

        Token? token = await databaseContext.Tokens.SingleOrDefaultAsync(t =>
            t.EmailAddress == request.EmailAddress &&
            t.Purpose == TokenPurpose.EmailAddressVerification, cancellationToken);

        if (token is null)
        {
            IActionResult result = EmailAddressHelpers.SanitizeEmailAddress(request.EmailAddress, hostEnvironment);

            if (result is BadRequestObjectResult badRequest)
            {
                logger.LogWarning("Email sanitization rejected {EmailAddress}: {Reason}",
                    request.EmailAddress, badRequest.Value);
                return RegisterEmailAddressResult.BadRequest(badRequest.Value?.ToString() ?? "Email Address Is Not Valid");
            }

            if (result is not ContentResult contentResult || contentResult.Content is null)
            {
                logger.LogError("[BUG] Sanitized Email Address \"{EmailAddress}\" Is NULL or Unprocessable", request.EmailAddress);
                return RegisterEmailAddressResult.Unprocessable($@"Unable To Process Email Address ""{request.EmailAddress}""");
            }

            string sanitizedEmailAddress = contentResult.Content;

            token = new Token
            {
                Purpose = TokenPurpose.EmailAddressVerification,
                EmailAddress = request.EmailAddress,
                Value = Guid.CreateVersion7(),
                Data = sanitizedEmailAddress
            };

            await databaseContext.Tokens.AddAsync(token, cancellationToken);
            await databaseContext.SaveChangesAsync(cancellationToken);

            bool sent = await emailService.SendEmailAddressRegistrationLink(request.EmailAddress,
                token.Value.ToString(), cancellationToken);

            if (!sent)
            {
                logger.LogError("Failed to deliver verification email to {EmailAddress}. Revoking token.", request.EmailAddress);
                databaseContext.Tokens.Remove(token);
                await databaseContext.SaveChangesAsync(cancellationToken);

                return RegisterEmailAddressResult.ServiceUnavailable("Failed To Send Email Address Verification Email");
            }
        }
        else
        {
            logger.LogInformation("Existing token found for {EmailAddress}, Consumed: {Consumed}",
                request.EmailAddress, token.TimestampConsumed.HasValue);

            return token.TimestampConsumed is null
                ? RegisterEmailAddressResult.BadRequest(
                    $@"A Registration Request For Email Address ""{request.EmailAddress}"" Has Already Been Made (Check Your Email Inbox For A Registration Link)")
                : RegisterEmailAddressResult.BadRequest($@"Email Address ""{request.EmailAddress}"" Is Already Registered");
        }

        logger.LogInformation("Verification token successfully generated and dispatched for {EmailAddress}", request.EmailAddress);
        return RegisterEmailAddressResult.Succeeded(
            $@"Email Address Registration Token Was Successfully Created, And An Email Was Sent To Address ""{request.EmailAddress}""");
    }
}
