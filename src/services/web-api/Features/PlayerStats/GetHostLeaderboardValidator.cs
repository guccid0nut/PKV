// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using FluentValidation;
using NEXUS.Contracts.CQRS.PlayerStats;

namespace ZORGATH.WebPortal.API.Features.PlayerStats;

/// <summary>
///     Validator for <see cref="GetHostLeaderboardQuery"/>.
/// </summary>
public sealed class GetHostLeaderboardValidator : AbstractValidator<GetHostLeaderboardQuery>
{
    public GetHostLeaderboardValidator()
    {
        // No parameters to validate; query requests the top host list.
    }
}
