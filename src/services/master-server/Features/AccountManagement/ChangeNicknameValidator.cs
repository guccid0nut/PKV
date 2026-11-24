// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using FluentValidation;
using NEXUS.Contracts.CQRS.AccountManagement;

namespace KONGOR.MasterServer.Features.AccountManagement;

public sealed class ChangeNicknameValidator : AbstractValidator<ChangeNicknameCommand>
{
    public ChangeNicknameValidator()
    {
        RuleFor(x => x.AccountId)
            .GreaterThan(0)
            .WithMessage("AccountId must be greater than zero.");

        RuleFor(x => x.NewNickname)
            .NotEmpty()
            .WithMessage("New nickname must not be empty.")
            .Length(3, 15)
            .WithMessage("New nickname must be between 3 and 15 characters.");

        RuleFor(x => x.Password)
            .NotEmpty()
            .WithMessage("Password must not be empty.");
    }
}
