// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using FluentValidation;
using NEXUS.Contracts.CQRS.PlayerStats;

namespace ZORGATH.WebPortal.API.Features.PlayerStats;

/// <summary>
///     Validation rules for <see cref="GetLeaderboardQuery"/>.
/// </summary>
public sealed class GetLeaderboardValidator : AbstractValidator<GetLeaderboardQuery>
{
    public GetLeaderboardValidator()
    {
        RuleFor(q => q.Page)
            .GreaterThanOrEqualTo(1).WithMessage("Page number must be at least 1.");

        RuleFor(q => q.PageSize)
            .InclusiveBetween(1, 100).WithMessage("Page size must be between 1 and 100.");

        RuleFor(q => q.Search)
            .MaximumLength(100).WithMessage("Search query cannot exceed 100 characters.")
            .When(q => !string.IsNullOrEmpty(q.Search));

        RuleFor(q => q.Sort)
            .NotEmpty().WithMessage("Sort field cannot be empty.")
            .MaximumLength(50).WithMessage("Sort field cannot exceed 50 characters.");
    }
}
