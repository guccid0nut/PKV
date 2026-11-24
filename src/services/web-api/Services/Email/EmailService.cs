// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2022-2026 [K]ONGOR and Contributors
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV) -- Upstream lineage: https://github.com/Project-KONGOR-Open-Source

using SendGrid;
using SendGrid.Helpers.Mail;

namespace ZORGATH.WebPortal.API.Services.Email;

public partial class EmailService(
    IOptions<OperationalConfiguration> configuration,
    ILogger<EmailService> logger,
    IWebHostEnvironment hostEnvironment,
    ISendGridClient sendGridClient) : IEmailService
{
    private string BaseURL =>
        string.IsNullOrWhiteSpace(configuration.Value.UIBaseUrl)
            ? hostEnvironment.IsDevelopment() ? "https://localhost:5557" : "https://portal.ui.kongor.net"
            : configuration.Value.UIBaseUrl;

    public async Task<bool> SendEmailAddressRegistrationLink(string emailAddress, string token,
        CancellationToken cancellationToken = default)
    {
        string link = BaseURL + "/register/" + token;

        const string subject = "Verify Email Address";

        string body = "You need to verify your email address before you can create your Heroes Of Newerth account."
                      + Environment.NewLine + "Please follow the link below to continue:"
                      + Environment.NewLine + Environment.NewLine + link
                      + Environment.NewLine + Environment.NewLine + "Regards,"
                      + Environment.NewLine + "The Project KONGOR Team";

        return await SendEmail(emailAddress, subject, body, cancellationToken);
    }

    public async Task<bool> SendEmailAddressRegistrationConfirmation(string emailAddress, string accountName,
        CancellationToken cancellationToken = default)
    {
        const string subject = "Email Address Verified";

        string body = $"Hi {accountName},"
                      + Environment.NewLine + Environment.NewLine +
                      "Congratulations on verifying the email address linked to your Heroes Of Newerth account."
                      + " " +
                      "Please remember to be respectful to your fellow Newerthians, and to maintain your account in good standing."
                      + " " +
                      "Suspensions carry over across accounts so, if you receive a suspension, you will not be able to log back into the game by creating a new account."
                      + Environment.NewLine + Environment.NewLine + "Regards,"
                      + Environment.NewLine + "The Project KONGOR Team";

        return await SendEmail(emailAddress, subject, body, cancellationToken);
    }

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "SendGrid API Key Not Configured. Falling Back To Console logging for development.")]
    private partial void LogSendGridApiKeyMissing();

    [LoggerMessage(Level = LogLevel.Error, Message = "Exception Occurred While Sending Email to {ToEmail}")]
    private partial void LogEmailSendException(Exception ex, string toEmail);

    private async Task<bool> SendEmail(string toEmail, string subject, string plainTextContent,
        CancellationToken cancellationToken = default)
    {
        try
        {
            OperationalConfiguration config = configuration.Value;
            if (string.IsNullOrWhiteSpace(config.Email.ApiKey) ||
                config.Email.ApiKey == "SENDGRID_API_KEY_PLACEHOLDER" ||
                config.Email.ApiKey.StartsWith("SG.development_placeholder", StringComparison.OrdinalIgnoreCase))
            {
                LogSendGridApiKeyMissing();
                return true;
            }

            EmailAddress from = new(config.Email.FromEmail, config.Email.FromName);
            EmailAddress to = new(toEmail);
            SendGridMessage msg = MailHelper.CreateSingleEmail(from, to, subject, plainTextContent, null);

            Response response = await sendGridClient.SendEmailAsync(msg, cancellationToken);

            return response.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            if (logger.IsEnabled(LogLevel.Error)) LogEmailSendException(ex, toEmail);

            return false;
        }
    }
}
