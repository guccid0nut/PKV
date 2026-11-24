// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using FluentValidation;
using NEXUS.Contracts.CQRS.Store;

namespace KONGOR.MasterServer.Features.Store;

/// <summary>
///     Validator for <see cref="RedeemStoreCodeCommand"/>.
/// </summary>
public sealed class RedeemStoreCodeValidator : AbstractValidator<RedeemStoreCodeCommand>
{
    public RedeemStoreCodeValidator()
    {
        RuleFor(c => c.AccountId)
            .GreaterThan(0)
            .WithMessage("AccountId must be greater than zero.");

        RuleFor(c => c.Code)
            .NotEmpty()
            .WithMessage("Redemption code must not be empty.")
            .MinimumLength(2)
            .WithMessage("Redemption code must be at least 2 characters.");
    }
}
