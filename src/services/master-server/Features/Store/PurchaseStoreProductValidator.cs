// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using FluentValidation;
using NEXUS.Contracts.CQRS.Store;

namespace KONGOR.MasterServer.Features.Store;

/// <summary>
///     Validation rules for <see cref="PurchaseStoreProductCommand"/>.
/// </summary>
public sealed class PurchaseStoreProductValidator : AbstractValidator<PurchaseStoreProductCommand>
{
    public PurchaseStoreProductValidator()
    {
        RuleFor(c => c.AccountId)
            .GreaterThan(0).WithMessage("AccountId must be greater than zero.");

        RuleFor(c => c.ProductId)
            .GreaterThan(0).WithMessage("ProductId must be greater than zero.");

        RuleFor(c => c.Currency)
            .NotEmpty().WithMessage("Currency is required.");

        RuleFor(c => c.CategoryId)
            .GreaterThan(0).WithMessage("CategoryId must be greater than zero.");

        RuleFor(c => c.CurrentPage)
            .GreaterThanOrEqualTo(1).WithMessage("CurrentPage must be at least 1.");
    }
}
