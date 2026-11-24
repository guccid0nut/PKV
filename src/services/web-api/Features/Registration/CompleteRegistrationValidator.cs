// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using FluentValidation;
using NEXUS.Contracts.CQRS.Registration;

namespace ZORGATH.WebPortal.API.Features.Registration;

public sealed class CompleteRegistrationValidator : AbstractValidator<CompleteRegistrationCommand>
{
    public CompleteRegistrationValidator()
    {
        RuleFor(x => x.Token)
            .NotEmpty().WithMessage("Registration token is required.")
            .Must(t => Guid.TryParse(t, out _)).WithMessage(t => $@"Registration Token ""{t.Token}"" Was Not Found");

        RuleFor(x => x.Name)
            .NotEmpty().WithMessage("Account nickname is required.")
            .Length(3, 15).WithMessage("Account nickname must be between 3 and 15 alphanumeric characters or underscores.");

        RuleFor(x => x.Password)
            .NotEmpty().WithMessage("Password is required.");
    }
}
