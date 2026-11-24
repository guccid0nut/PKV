// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using FluentValidation;
using NEXUS.Contracts.CQRS.Store;

namespace KONGOR.MasterServer.Features.Store;

/// <summary>
///     Validator for <see cref="GetVaultItemsQuery"/>.
/// </summary>
public sealed class GetVaultItemsValidator : AbstractValidator<GetVaultItemsQuery>
{
    public GetVaultItemsValidator()
    {
        RuleFor(q => q.AccountId)
            .GreaterThan(0)
            .WithMessage("AccountId must be greater than zero.");
    }
}
