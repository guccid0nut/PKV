// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2022-2026 [K]ONGOR and Contributors
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV) -- Upstream lineage: https://github.com/Project-KONGOR-Open-Source

namespace ZORGATH.WebPortal.API.Services.Email;

public interface IEmailService
{
    /// <summary>
    /// Dispatches an email address verification link containing the registration token.
    /// In production, sent via SendGrid (EmailService); in development or when unconfigured, logged to console (ConsoleEmailService).
    /// </summary>
    Task<bool> SendEmailAddressRegistrationLink(string emailAddress, string token,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Dispatches an account registration confirmation notification.
    /// In production, sent via SendGrid (EmailService); in development or when unconfigured, logged to console (ConsoleEmailService).
    /// </summary>
    Task<bool> SendEmailAddressRegistrationConfirmation(string emailAddress, string accountName,
        CancellationToken cancellationToken = default);
}

