// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using FluentValidation;
using NEXUS.Contracts.CQRS.Store;

namespace KONGOR.MasterServer.Features.Store;

/// <summary>
///     Validation rules for <see cref="GetStoreCategoryQuery"/>.
/// </summary>
public sealed class GetStoreCategoryValidator : AbstractValidator<GetStoreCategoryQuery>
{
    public GetStoreCategoryValidator()
    {
        RuleFor(q => q.AccountId)
            .GreaterThan(0).WithMessage("AccountId must be greater than zero.");

        RuleFor(q => q.Page)
            .GreaterThanOrEqualTo(1).WithMessage("Page must be at least 1.");

        RuleFor(q => q.CategoryId)
            .GreaterThan(0).WithMessage("CategoryId must be greater than zero.");
    }
}
