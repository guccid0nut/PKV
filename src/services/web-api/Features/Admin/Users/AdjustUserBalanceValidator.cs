// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using FluentValidation;
using NEXUS.Contracts.CQRS.Admin.Users;

namespace ZORGATH.WebPortal.API.Features.Admin.Users;

public sealed class AdjustUserBalanceValidator : AbstractValidator<AdjustUserBalanceCommand>
{
    public AdjustUserBalanceValidator()
    {
        RuleFor(c => c.UserId)
            .GreaterThan(0)
            .WithMessage("UserId must be greater than 0.");

        RuleFor(c => c.Reason)
            .MaximumLength(500)
            .WithMessage("Reason must not exceed 500 characters.");
    }
}
