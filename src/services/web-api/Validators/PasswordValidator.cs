// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2022-2026 [K]ONGOR and Contributors
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV) -- Upstream lineage: https://github.com/Project-KONGOR-Open-Source

namespace ZORGATH.WebPortal.API.Validators;

public class PasswordValidator : AbstractValidator<string>
{
    public PasswordValidator(IOptions<IdentityOptions> identityOptions)
    {
        PasswordOptions options = identityOptions.Value.Password;

        RuleFor(password => password).NotEmpty().WithMessage("Password Must Not Be Empty");

        if (options.RequiredLength > 0)
            RuleFor(password => password).MinimumLength(options.RequiredLength)
                .WithMessage($"Password Must Be At Least {options.RequiredLength} Characters Long");

        if (options.RequireDigit)
            RuleFor(password => password).Must(password => password.Any(char.IsDigit))
                .WithMessage("Password Must Contain At Least One Digit");

        if (options.RequireLowercase)
            RuleFor(password => password).Must(password => password.Any(char.IsLower))
                .WithMessage("Password Must Contain At Least One Lowercase Letter");

        if (options.RequireUppercase)
            RuleFor(password => password).Must(password => password.Any(char.IsUpper))
                .WithMessage("Password Must Contain At Least One Uppercase Letter");

        if (options.RequireNonAlphanumeric)
            RuleFor(password => password).Must(password => password.Any(character => !char.IsLetterOrDigit(character)))
                .WithMessage("Password Must Contain At Least One Non-Alphanumeric Character");

        if (options.RequiredUniqueChars > 1)
            RuleFor(password => password).Must(password => password.Distinct().Count() >= options.RequiredUniqueChars)
                .WithMessage($"Password Must Contain At Least {options.RequiredUniqueChars} Unique Characters");
    }
}
