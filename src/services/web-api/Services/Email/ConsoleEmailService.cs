// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using Microsoft.Extensions.Options;

namespace ZORGATH.WebPortal.API.Services.Email;

/// <summary>
/// Development and local fallback email service that formats and logs registration tokens, verification links,
/// and confirmation notices directly to structured logs and stdout, eliminating external SendGrid credentials.
/// </summary>
public sealed class ConsoleEmailService(
    IOptions<OperationalConfiguration> configuration,
    ILogger<ConsoleEmailService> logger,
    IWebHostEnvironment hostEnvironment,
    TimeProvider? timeProvider = null) : IEmailService
{
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    private string BaseUrl =>
        string.IsNullOrWhiteSpace(configuration.Value.UIBaseUrl)
            ? hostEnvironment.IsDevelopment() ? "https://localhost:5557" : "https://portal.ui.kongor.net"
            : configuration.Value.UIBaseUrl;

    public Task<bool> SendEmailAddressRegistrationLink(string emailAddress, string token,
        CancellationToken cancellationToken = default)
    {
        string link = $"{BaseUrl}/register/{token}";

        logger.LogInformation(
            """

            ================================================================================
            [CONSOLE EMAIL SERVICE] Registration Verification Link Dispatched
            To:                 {EmailAddress}
            Token:              {Token}
            Verification Link:  {VerificationLink}
            Generated At (UTC): {Timestamp}
            ================================================================================
            """,
            emailAddress, token, link, _timeProvider.GetUtcNow());

        return Task.FromResult(true);
    }

    public Task<bool> SendEmailAddressRegistrationConfirmation(string emailAddress, string accountName,
        CancellationToken cancellationToken = default)
    {
        logger.LogInformation(
            """

            ================================================================================
            [CONSOLE EMAIL SERVICE] Registration Confirmation Dispatched
            To:                 {EmailAddress}
            Account Name:       {AccountName}
            Status:             Email Verified & Account Welcomed to Project KONGOR
            Confirmed At (UTC): {Timestamp}
            ================================================================================
            """,
            emailAddress, accountName, _timeProvider.GetUtcNow());

        return Task.FromResult(true);
    }
}
