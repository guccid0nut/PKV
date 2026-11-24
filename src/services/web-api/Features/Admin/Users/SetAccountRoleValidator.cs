// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using FluentValidation;
using NEXUS.Contracts.CQRS.Admin.Users;
using NEXUS.Contracts.Domain;

namespace ZORGATH.WebPortal.API.Features.Admin.Users;

public sealed class SetAccountRoleValidator : AbstractValidator<SetAccountRoleCommand>
{
    public SetAccountRoleValidator()
    {
        RuleFor(c => c.AccountId)
            .GreaterThan(0)
            .WithMessage("AccountId must be greater than 0.");

        RuleFor(c => c.AccountType)
            .Must(t => Enum.IsDefined(typeof(AccountType), (AccountType)t))
            .WithMessage("Invalid account type.");
    }
}
