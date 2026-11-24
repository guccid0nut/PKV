// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using FluentValidation;
using NEXUS.Contracts.CQRS.PlayerStats;

namespace ZORGATH.WebPortal.API.Features.PlayerStats;

/// <summary>
///     Validation rules for <see cref="GetPlayerProfileQuery"/>.
/// </summary>
public sealed class GetPlayerProfileValidator : AbstractValidator<GetPlayerProfileQuery>
{
    public GetPlayerProfileValidator()
    {
        RuleFor(q => q.InGameName)
            .NotEmpty().WithMessage("In-game name (IGN) is required.")
            .MaximumLength(50).WithMessage("In-game name (IGN) cannot exceed 50 characters.");
    }
}
