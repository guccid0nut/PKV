// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using FluentValidation;
using NEXUS.Contracts.CQRS.Registration;

namespace ZORGATH.WebPortal.API.Features.Registration;

public sealed class RegisterDirectUserValidator : AbstractValidator<RegisterDirectUserCommand>
{
    public RegisterDirectUserValidator()
    {
        RuleFor(x => x.Name)
            .NotEmpty().WithMessage("Account nickname is required.")
            .Length(3, 15).WithMessage("Account nickname must be between 3 and 15 alphanumeric characters or underscores.");

        RuleFor(x => x.Password)
            .NotEmpty().WithMessage("Password is required.");

        RuleFor(x => x)
            .Must(x => !string.IsNullOrWhiteSpace(x.Email) || !string.IsNullOrWhiteSpace(x.DiscordId))
            .WithMessage("Either Email or DiscordId must be provided for direct registration.");
    }
}
