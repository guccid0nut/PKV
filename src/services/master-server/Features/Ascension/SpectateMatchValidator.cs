// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using FluentValidation;
using NEXUS.Contracts.CQRS.Ascension;

namespace KONGOR.MasterServer.Features.Ascension;

public sealed class SpectateMatchValidator : AbstractValidator<SpectateMatchCommand>
{
    public SpectateMatchValidator()
    {
        RuleFor(x => x.MatchId)
            .GreaterThan(0)
            .WithMessage("MatchId must be greater than zero.");
    }
}
