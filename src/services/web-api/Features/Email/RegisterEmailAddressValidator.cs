// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using FluentValidation;
using NEXUS.Contracts.CQRS.Email;

namespace ZORGATH.WebPortal.API.Features.Email;

public sealed class RegisterEmailAddressValidator : AbstractValidator<RegisterEmailAddressCommand>
{
    public RegisterEmailAddressValidator()
    {
        RuleFor(x => x.EmailAddress)
            .NotEmpty().WithMessage("Email address is required.")
            .EmailAddress().WithMessage("A valid email address is required.");

        RuleFor(x => x.ConfirmEmailAddress)
            .NotEmpty().WithMessage("Confirmation email address is required.")
            .Equal(x => x.EmailAddress)
            .WithMessage(x => $@"Email Address ""{x.ConfirmEmailAddress}"" Does Not Match ""{x.EmailAddress}""");
    }
}
