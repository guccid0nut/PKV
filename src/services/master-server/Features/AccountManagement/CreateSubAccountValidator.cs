// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using FluentValidation;
using NEXUS.Contracts.CQRS.AccountManagement;

namespace KONGOR.MasterServer.Features.AccountManagement;

public sealed class CreateSubAccountValidator : AbstractValidator<CreateSubAccountCommand>
{
    public CreateSubAccountValidator()
    {
        RuleFor(x => x.MainAccountId)
            .GreaterThan(0)
            .WithMessage("MainAccountId must be greater than zero.");

        RuleFor(x => x.SubNickname)
            .NotEmpty()
            .WithMessage("SubNickname is required.");

        RuleFor(x => x.Password)
            .NotEmpty()
            .WithMessage("Password is required.");
    }
}
