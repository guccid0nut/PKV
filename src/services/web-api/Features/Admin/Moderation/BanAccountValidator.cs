// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using FluentValidation;
using NEXUS.Contracts.CQRS.Admin.Moderation;

namespace ZORGATH.WebPortal.API.Features.Admin.Moderation;

public sealed class BanAccountValidator : AbstractValidator<BanAccountCommand>
{
    public BanAccountValidator()
    {
        RuleFor(c => c.AccountId)
            .GreaterThan(0)
            .WithMessage("AccountId must be greater than 0.");

        RuleFor(c => c.Reason)
            .NotEmpty()
            .MaximumLength(500)
            .WithMessage("Ban reason is required and must not exceed 500 characters.");
    }
}
