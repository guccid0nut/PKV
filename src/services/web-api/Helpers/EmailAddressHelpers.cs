// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2022-2026 [K]ONGOR and Contributors
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV) -- Upstream lineage: https://github.com/Project-KONGOR-Open-Source

namespace ZORGATH.WebPortal.API.Helpers;

public static partial class EmailAddressHelpers
{
    [GeneratedRegex(@"^(?<local>[a-zA-Z0-9_\-.]+)@(?<domain>[a-zA-Z]+)\.(?<tld>[a-zA-Z]{1,3}|co.uk)$")]
    private static partial Regex EmailRegex();

    public static IActionResult SanitizeEmailAddress(string email, IWebHostEnvironment hostEnvironment)
    {
        if (!hostEnvironment.IsDevelopment())
        {
            if (email.Split('@').First().Contains('+'))
                return new BadRequestObjectResult(@"Alias Creating Character ""+"" Is Not Allowed");

            string[] allowedEmailProviders =
            [
                "outlook", "hotmail", "live", "msn", // Microsoft Outlook
                "protonmail", "proton", // Proton Mail
                "gmail", "googlemail", // Google Mail
                "yahoo", "rocketmail", "ymail", // Yahoo Mail
                "aol", "yandex", "gmx", "mail", // AOL Mail, Yandex Mail, GMX Mail, mail.com
                "icloud", "me", "mac" // iCloud Mail
            ];

            Regex pattern = EmailRegex();

            Match match = pattern.Match(email);

            if (!match.Success) return new BadRequestObjectResult($@"Email Address ""{email}"" Is Not Valid");

            string local = match.Groups["local"].Value;
            string domain = match.Groups["domain"].Value;
            string tld = match.Groups["tld"].Value;

            if (!allowedEmailProviders.Contains(domain, StringComparer.OrdinalIgnoreCase))
                return new BadRequestObjectResult($@"Email Address Provider ""{domain}"" Is Not Allowed");

            // These Email Providers Ignore Period Characters
            // Users Can Create Aliases With The Same Email Address By Simply Adding Some Period Characters To The Local Part
            if (domain is "protonmail" or "proton" or "gmail" or "googlemail") local = local.Replace(".", string.Empty);

            email = $"{local}@{domain}.{tld}";
        }

        return new ContentResult { Content = email };
    }
}
