// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using FluentValidation;
using NEXUS.Contracts.CQRS.Store;

namespace KONGOR.MasterServer.Features.Store;

public sealed class PurchaseLobbyProductValidator : AbstractValidator<PurchaseLobbyProductCommand>
{
    public PurchaseLobbyProductValidator()
    {
        RuleFor(c => c.AccountId)
            .GreaterThan(0)
            .WithMessage("AccountId must be greater than 0.");

        RuleFor(c => c.HeroName)
            .NotEmpty()
            .WithMessage("HeroName is required.");

        RuleFor(c => c.AvatarCode)
            .NotEmpty()
            .WithMessage("AvatarCode is required.");

        RuleFor(c => c.Currency)
            .NotEmpty()
            .WithMessage("Currency is required.");
    }
}
