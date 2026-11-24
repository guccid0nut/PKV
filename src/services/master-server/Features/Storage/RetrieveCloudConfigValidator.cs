// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using FluentValidation;
using NEXUS.Contracts.CQRS.Storage;

namespace KONGOR.MasterServer.Features.Storage;

public sealed class RetrieveCloudConfigValidator : AbstractValidator<RetrieveCloudConfigQuery>
{
    public RetrieveCloudConfigValidator()
    {
        RuleFor(x => x.AccountId)
            .GreaterThan(0)
            .WithMessage("AccountId must be greater than zero.");
    }
}
