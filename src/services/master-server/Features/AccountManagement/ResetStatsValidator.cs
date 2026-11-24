// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using FluentValidation;
using NEXUS.Contracts.CQRS.AccountManagement;

namespace KONGOR.MasterServer.Features.AccountManagement;

public sealed class ResetStatsValidator : AbstractValidator<ResetStatsCommand>
{
    public ResetStatsValidator()
    {
        RuleFor(x => x.AccountId)
            .GreaterThan(0)
            .WithMessage("AccountId must be greater than zero.");
    }
}
